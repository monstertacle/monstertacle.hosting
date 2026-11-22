using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Monstertacle.Hosting.Security;

internal sealed class UserTokenSettings
{
    internal const string WorkloadScheme = "WorkloadBearer";
    private const string SelectorScheme = "HostingBearer";
    private const string GitHubIssuer = "https://token.actions.githubusercontent.com";
    private readonly string? workloadScope;
    public string Issuer { get; }
    public string Audience { get; }
    public string Scope { get; }
    public string? WorkloadIssuer { get; }

    public UserTokenSettings(IConfiguration settings)
    {
        Issuer = RequireHttps(settings, "Issuer");
        Audience = RequireHttps(settings, "Audience");
        Scope = RequireScope(settings);

        var workload = settings.GetSection("Workload");
        if (workload.Exists())
        {
            WorkloadIssuer = RequireHttps(workload, "Issuer");
            workloadScope = RequireScope(workload);
            if (WorkloadIssuer.TrimEnd('/') == Issuer.TrimEnd('/') || WorkloadIssuer.TrimEnd('/') == GitHubIssuer)
            {
                throw new InvalidOperationException("The workload token issuer must be separate from the human and GitHub issuers.");
            }
        }
    }

    public void Configure(IServiceCollection services, bool saveToken = false)
    {
        services.TryAddSingleton(TimeProvider.System);
        var authentication = services.AddAuthentication(WorkloadIssuer is null
            ? JwtBearerDefaults.AuthenticationScheme : SelectorScheme);
        AddBearer(authentication, JwtBearerDefaults.AuthenticationScheme, Issuer, Scope, saveToken, workload: false);
        if (WorkloadIssuer is { } workloadIssuer)
        {
            AddBearer(authentication, WorkloadScheme, workloadIssuer, workloadScope!, saveToken, workload: true);
            authentication.AddPolicyScheme(SelectorScheme, null, options =>
            {
                options.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme;
                options.ForwardDefaultSelector = context =>
                {
                    const string prefix = "Bearer ";
                    var header = context.Request.Headers.Authorization.ToString();
                    var reader = new JsonWebTokenHandler();
                    if (header.Length <= 16 * 1024 && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var token = header[prefix.Length..].Trim();
                        if (reader.CanReadToken(token))
                        {
                            try
                            {
                                // Only selects a validator. The selected handler verifies every claim.
                                if (reader.ReadJsonWebToken(token).Issuer == workloadIssuer)
                                {
                                    return WorkloadScheme;
                                }
                            }
                            catch (ArgumentException)
                            {
                                // The default handler rejects malformed credentials.
                            }
                        }
                    }

                    return JwtBearerDefaults.AuthenticationScheme;
                };
            });
        }
        services.AddAuthorization();
    }

    private void AddBearer(AuthenticationBuilder authentication, string scheme, string issuer, string scope,
        bool saveToken, bool workload)
    {
        authentication.AddJwtBearer(scheme, options =>
        {
            options.Authority = issuer;
            options.RequireHttpsMetadata = true;
            options.BackchannelHttpHandler ??= new HttpClientHandler { AllowAutoRedirect = false };
            options.BackchannelTimeout = TimeSpan.FromSeconds(10);
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = saveToken;

            var validation = options.TokenValidationParameters;
            validation.ValidateIssuer = true;
            validation.ValidIssuer = issuer;
            validation.IssuerValidator = (value, _, _) =>
                string.Equals(value, issuer, StringComparison.Ordinal)
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
            if (workload)
            {
                validation.ValidTypes = ["at+jwt"];
            }

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
                            .Contains(scope, StringComparer.Ordinal) != true)
                    {
                        context.Fail("The token requires a subject and the configured scope.");
                    }
                    else if (workload && !ValidWorkloadLifetime(context.SecurityToken))
                    {
                        context.Fail("The workload token lifetime is invalid.");
                    }

                    return Task.CompletedTask;
                }
            };
        });
        authentication.Services.AddOptions<JwtBearerOptions>(scheme)
            .PostConfigure<TimeProvider>((options, time) =>
                options.ConfigurationManager = new IssuerDiscovery(issuer, options.Backchannel, time));
    }

    private static bool ValidWorkloadLifetime(SecurityToken token) =>
        token is JsonWebToken jwt && jwt.IssuedAt != DateTime.MinValue &&
        jwt.IssuedAt <= DateTime.UtcNow && jwt.ValidTo > jwt.IssuedAt &&
        jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(5);

    private static string RequireScope(IConfiguration settings) =>
        settings["Scope"] is { Length: > 0 } scope && !scope.Any(char.IsWhiteSpace) ? scope :
            throw new InvalidOperationException("Authentication requires one OAuth scope.");

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
