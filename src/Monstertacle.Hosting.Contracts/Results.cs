namespace Monstertacle.Hosting.Contracts;

public static class ResultContract
{
    public const int Version = 1;
    public const int MaxArtifactsPerJob = 4;
    public const int MaxTotalArtifactBytes = 64 * 1024;

    public static bool IsArtifactName(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

public sealed record ArtifactUpload(string Name, int Bytes, string Sha256, byte[] Content);

public sealed record JobCompletion(int Version, Guid SessionId, Guid JobId, JobObservation Evidence, ArtifactUpload[] Artifacts);

public sealed record ArtifactDescriptor(string Name, int Bytes, string Sha256);

public sealed record JobResultManifest(int Version, Guid SessionId, Guid JobId, JobObservation Evidence,
    IReadOnlyList<ArtifactDescriptor> Artifacts);

public sealed record VerifiedJobResult(string Sha256, JobResultManifest Manifest);

public sealed record JobResultSummary(JobStatus Status, VerifiedJobResult? Result);
public sealed record SessionResultManifest(int Version, Guid SessionId, string Project,
    IReadOnlyList<JobResultSummary> Jobs, bool Complete);
public sealed record VerifiedSessionResult(string Sha256, SessionResultManifest Manifest);
public sealed record ArtifactContent(ArtifactDescriptor Artifact, byte[] Content);
public sealed record AcceptResult(Guid MutationId, string Sha256);
public sealed record ResultAcceptance(Guid SessionId, Guid MutationId, string Sha256, DateTimeOffset AcceptedUtc);
