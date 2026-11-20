namespace Monstertacle.Hosting.Contracts;

public static class ResultContract
{
    public const int Version = 1;
    public const int MaxArtifactsPerJob = 4;
    public const int MaxTotalArtifactBytes = 64 * 1024;
}

public sealed record ArtifactUpload(string Name, int Bytes, string Sha256, byte[] Content);

public sealed record JobCompletion(int Version, Guid SessionId, Guid JobId, JobObservation Evidence, ArtifactUpload[] Artifacts);

public sealed record ArtifactDescriptor(string Name, int Bytes, string Sha256);

public sealed record JobResultManifest(int Version, Guid SessionId, Guid JobId, JobObservation Evidence,
    IReadOnlyList<ArtifactDescriptor> Artifacts);

public sealed record VerifiedJobResult(string Sha256, JobResultManifest Manifest);
