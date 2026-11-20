using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Monstertacle.Hosting.Security;

internal sealed class IssuerDiscovery(string issuer, HttpClient backchannel, TimeProvider time)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly SemaphoreSlim refresh = new(1);
    private readonly HttpDocumentRetriever documents = new(backchannel) { RequireHttps = true };
    private readonly string metadataAddress = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
    private OpenIdConnectConfiguration? current;
    private DateTimeOffset retryAfter;
    private DateTimeOffset expires;
    private int refreshRequested;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        await refresh.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (now >= retryAfter && (current is null || now >= expires || Volatile.Read(ref refreshRequested) == 1))
            {
                current = null;
                retryAfter = now.AddSeconds(30);
                Interlocked.Exchange(ref refreshRequested, 0);

                var document = await documents.GetDocumentAsync(metadataAddress, cancellationToken);
                var configuration = OpenIdConnectConfiguration.Create(document);
                if (!string.Equals(configuration.Issuer, issuer, StringComparison.Ordinal) ||
                    !Uri.TryCreate(configuration.JwksUri, UriKind.Absolute, out var jwks) ||
                    jwks.Scheme != Uri.UriSchemeHttps || jwks.UserInfo.Length != 0 || jwks.Fragment.Length != 0)
                {
                    throw new InvalidOperationException("Issuer discovery metadata is invalid.");
                }

                var keyDocument = await documents.GetDocumentAsync(configuration.JwksUri, cancellationToken);
                foreach (var key in JsonWebKeySet.Create(keyDocument).Keys.Where(IsSigningKey))
                {
                    configuration.SigningKeys.Add(key);
                }

                if (configuration.SigningKeys.Count == 0)
                {
                    throw new InvalidOperationException("Issuer discovery has no supported signing keys.");
                }

                current = configuration;
                expires = time.GetUtcNow().AddMinutes(5);
            }

            return current ?? throw new InvalidOperationException("Issuer discovery is temporarily unavailable.");
        }
        finally
        {
            refresh.Release();
        }
    }

    public void RequestRefresh() => Interlocked.Exchange(ref refreshRequested, 1);

    public static bool AcceptsAlgorithm(string algorithm, SecurityKey key, SecurityToken _,
        TokenValidationParameters __) =>
        key is JsonWebKey jwk &&
        (string.IsNullOrEmpty(jwk.Alg) || jwk.Alg == algorithm) &&
        (jwk.Kty, algorithm) switch
        {
            ("RSA", "RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512") => true,
            ("EC", "ES256") => jwk.Crv == "P-256",
            ("EC", "ES384") => jwk.Crv == "P-384",
            ("EC", "ES512") => jwk.Crv == "P-521",
            _ => false
        };

    private static bool IsSigningKey(JsonWebKey key) =>
        (key.Kty is "RSA" or "EC") && (string.IsNullOrEmpty(key.Use) || key.Use == "sig") &&
        (key.KeyOps.Count == 0 || key.KeyOps.Contains("verify", StringComparer.Ordinal));
}
