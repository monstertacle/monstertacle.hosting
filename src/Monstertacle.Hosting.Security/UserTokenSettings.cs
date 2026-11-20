using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Monstertacle.Hosting.Security;

internal sealed class UserTokenSettings
{
    public string Issuer { get; }
    public string Audience { get; }
    public string Scope { get; }

    public UserTokenSettings(IConfiguration settings)
    {
        Issuer = RequireHttps(settings, "Issuer");
        Audience = RequireHttps(settings, "Audience");
        Scope = settings["Scope"] ?? "";
        if (Scope.Length == 0 || Scope.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Authentication requires one OAuth scope.");
        }
    }

    public void Configure(IServiceCollection services, bool saveToken = false)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = Issuer;
            options.RequireHttpsMetadata = true;
            options.BackchannelHttpHandler ??= new HttpClientHandler { AllowAutoRedirect = false };
            options.BackchannelTimeout = TimeSpan.FromSeconds(10);
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = saveToken;

            var validation = options.TokenValidationParameters;
            validation.ValidateIssuer = true;
            validation.ValidIssuer = Issuer;
            validation.IssuerValidator = (value, _, _) =>
                string.Equals(value, Issuer, StringComparison.Ordinal)
                    ? value
                    : throw new SecurityTokenInvalidIssuerException();
            validation.ValidateAudience = true;
            validation.ValidAudience = Audience;
            validation.IgnoreTrailingSlashWhenValidatingAudience = false;
            validation.AudienceValidator = (values, _, _) =>
                values?.Take(2).ToArray() is [var value] && string.Equals(value, Audience, StringComparison.Ordinal);
            validation.ValidateLifetime = true;
            validation.RequireExpirationTime = true;
            validation.ClockSkew = TimeSpan.Zero;
            validation.RequireSignedTokens = true;
            validation.ValidateIssuerSigningKey = true;
            validation.AlgorithmValidator = IssuerDiscovery.AcceptsAlgorithm;

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    context.Fail("The access token could not be verified.");
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var subject = context.Principal?.FindAll("sub").Take(2).ToArray();
                    var scopes = context.Principal?.FindAll("scope").Concat(context.Principal.FindAll("scp"));
                    if (subject is not [{ Value: var name }] || string.IsNullOrWhiteSpace(name) ||
                        scopes?.SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            .Contains(Scope, StringComparer.Ordinal) != true)
                    {
                        context.Fail("The token requires a subject and the configured scope.");
                    }

                    return Task.CompletedTask;
                }
            };
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<TimeProvider>((options, time) =>
                options.ConfigurationManager = new IssuerDiscovery(Issuer, options.Backchannel, time));
        services.AddAuthorization();
    }

    public static string RequireHttps(IConfiguration settings, string key)
    {
        var value = settings[key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || (key == "Issuer" && uri.Query.Length != 0))
        {
            throw new InvalidOperationException($"Authentication requires an HTTPS {key} URI.");
        }

        return value!;
    }
}
