using Monstertacle.Hosting.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed class WorkerRegistrations
{
    private readonly Dictionary<string, WorkerIdentity> byCertificate = new(StringComparer.OrdinalIgnoreCase);

    public WorkerRegistrations(IConfiguration configuration, string edgeFingerprint)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in configuration.GetSection("Broker:Workers").GetChildren())
        {
            var fingerprint = entry["CertificateSha256"] ?? "";
            var lane = entry["Lane"];
            if (!BrokerPolicy.ValidName(entry.Key) || !BrokerPolicy.ValidName(lane) ||
                !names.Add(entry.Key) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit) ||
                string.Equals(fingerprint, edgeFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !byCertificate.TryAdd(fingerprint, new WorkerIdentity(entry.Key, lane!)))
            {
                throw new InvalidOperationException("A broker worker registration is invalid or duplicated.");
            }
        }
    }

    public WorkerIdentity? Resolve(X509Certificate2? certificate)
    {
        if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            return null;
        }

        return byCertificate.GetValueOrDefault(certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    internal sealed record WorkerIdentity(string Id, string Lane);
}
