using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Monstertacle.Hosting.ControlPlane;

public sealed class BrokerAuthentication
{
    private readonly string issuer;
    private readonly string audience;
    private readonly string scope;
    private readonly byte[] edgeCertificateHash;

    public BrokerAuthentication(IConfiguration configuration)
    {
        var settings = configuration.GetSection("Broker:Authentication");
        issuer = RequireHttps(settings, "Issuer");
        audience = RequireHttps(settings, "Audience");
        scope = settings["Scope"] ?? "";
        if (scope.Length == 0 || scope.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("The broker requires one OAuth scope.");
        }

        var fingerprint = settings["EdgeCertificateSha256"] ?? "";
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The broker requires an edge certificate SHA-256 fingerprint.");
        }

        edgeCertificateHash = Convert.FromHexString(fingerprint);
    }

    public void Configure(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 128 * 1024;
            options.ConfigureHttpsDefaults(https =>
            {
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => IsEdgeCertificate(certificate);
            });
        });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;

            var validation = options.TokenValidationParameters;
            validation.ValidateIssuer = true;
            validation.ValidIssuer = issuer;
            validation.IssuerValidator = (value, _, _) =>
                string.Equals(value, issuer, StringComparison.Ordinal)
                    ? value
                    : throw new Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException();
            validation.ValidateAudience = true;
            validation.ValidAudience = audience;
            validation.IgnoreTrailingSlashWhenValidatingAudience = false;
            validation.AudienceValidator = (values, _, _) =>
                values?.Take(2).ToArray() is [var value] && string.Equals(value, audience, StringComparison.Ordinal);
            validation.ValidateLifetime = true;
            validation.RequireExpirationTime = true;
            validation.ClockSkew = TimeSpan.Zero;
            validation.RequireSignedTokens = true;
            validation.ValidateIssuerSigningKey = true;

            options.Events = new JwtBearerEvents
            {
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

                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddAuthorization();
    }

    public bool IsEdgeRequest(HttpContext context) =>
        context.Request.IsHttps && IsEdgeCertificate(context.Connection.ClientCertificate);

    private bool IsEdgeCertificate(X509Certificate2? certificate) =>
        certificate is not null &&
        DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
        DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), edgeCertificateHash);

    private static string RequireHttps(IConfiguration settings, string key)
    {
        var value = settings[key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException($"Broker authentication requires an HTTPS {key} URI.");
        }

        return value!;
    }
}
