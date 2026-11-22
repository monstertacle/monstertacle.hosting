using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Monstertacle.Hosting.Security;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed class BrokerAuthentication
{
    private readonly UserTokenSettings tokens;
    private readonly byte[] edgeCertificateHash;
    private readonly WorkerRegistrations workers;
    public string? WorkloadIssuer => tokens.WorkloadIssuer;

    public BrokerAuthentication(IConfiguration configuration)
    {
        var settings = configuration.GetSection("Broker:Authentication");
        tokens = new UserTokenSettings(settings);
        var fingerprint = settings["EdgeCertificateSha256"] ?? "";
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The broker requires an edge certificate SHA-256 fingerprint.");
        }

        edgeCertificateHash = Convert.FromHexString(fingerprint);
        workers = new WorkerRegistrations(configuration, fingerprint);
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
                https.ClientCertificateValidation = (certificate, _, _) =>
                    IsEdgeCertificate(certificate) || workers.Resolve(certificate) is not null;
            });
        });

        tokens.Configure(builder.Services);
    }

    public bool IsEdgeRequest(HttpContext context) =>
        context.Request.IsHttps && IsEdgeCertificate(context.Connection.ClientCertificate);

    public WorkerRegistrations.WorkerIdentity? Worker(HttpContext context) =>
        context.Request.IsHttps ? workers.Resolve(context.Connection.ClientCertificate) : null;

    private bool IsEdgeCertificate(X509Certificate2? certificate) =>
        certificate is not null &&
        DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
        DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), edgeCertificateHash);
}
