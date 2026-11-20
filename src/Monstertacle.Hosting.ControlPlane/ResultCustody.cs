using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monstertacle.Hosting.Contracts;
using Monstertacle.Hosting.Security;
using Worker = Monstertacle.Hosting.ControlPlane.WorkerRegistrations.WorkerIdentity;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed partial class SessionStore
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    public ResultSubmission Complete(Worker worker, Guid jobId, Guid fence, JobCompletion completion)
    {
        var result = Verify(completion);
        if (result is null || completion.JobId != jobId)
        {
            return new(ResultOutcome.Invalid);
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = FindClaim(connection, transaction, worker, jobId, fence);
        if (job is null || job.Claim.SessionId != completion.SessionId)
        {
            return new(ResultOutcome.Missing);
        }

        using var existing = Command(connection, transaction,
            "SELECT digest FROM job_results WHERE job_id = $job;", ("$job", jobId.ToString("D")));
        if (existing.ExecuteScalar() is string digest)
        {
            return string.Equals(digest, result.Sha256, StringComparison.Ordinal)
                ? new(ResultOutcome.Accepted, result)
                : new(ResultOutcome.Conflict);
        }

        if (!HasSameEvidence(job, completion.Evidence) && !CanRecordEvidence(job, completion.Evidence))
        {
            return new(ResultOutcome.Conflict);
        }

        UpdateEvidence(connection, transaction, jobId, completion.Evidence);
        using var manifest = Command(connection, transaction, """
            INSERT INTO job_results (job_id, digest, manifest) VALUES ($job, $digest, $manifest);
            """, ("$job", jobId.ToString("D")), ("$digest", result.Sha256),
            ("$manifest", JsonSerializer.Serialize(result.Manifest, ResultJson)));
        manifest.ExecuteNonQuery();

        foreach (var upload in completion.Artifacts)
        {
            using var artifact = Command(connection, transaction, """
                INSERT INTO job_artifacts (job_id, name, bytes, digest, content)
                VALUES ($job, $name, $bytes, $digest, $content);
                """, ("$job", jobId.ToString("D")), ("$name", upload.Name), ("$bytes", upload.Bytes),
                ("$digest", upload.Sha256), ("$content", upload.Content));
            artifact.ExecuteNonQuery();
        }

        transaction.Commit();
        return new(ResultOutcome.Accepted, result);
    }

    private static VerifiedJobResult? Verify(JobCompletion completion)
    {
        if (completion.Version != ResultContract.Version ||
            completion.Evidence is not { } evidence || !evidence.IsValid() ||
            evidence.State == "running" || evidence.Certainty is not ("confirmed-ended" or "not-started") ||
            completion.Artifacts is not { Length: <= ResultContract.MaxArtifactsPerJob } ||
            (evidence.Certainty == "not-started" && completion.Artifacts.Length != 0))
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new List<ArtifactDescriptor>();
        var bytesRemaining = ResultContract.MaxTotalArtifactBytes;
        foreach (var artifact in completion.Artifacts)
        {
            if (artifact is null || !BrokerPolicy.ValidName(artifact.Name) || !names.Add(artifact.Name) ||
                artifact.Content is null || artifact.Bytes != artifact.Content.Length ||
                artifact.Bytes > bytesRemaining || !string.Equals(artifact.Sha256, Sha256(artifact.Content), StringComparison.Ordinal))
            {
                return null;
            }

            bytesRemaining -= artifact.Bytes;
            descriptors.Add(new(artifact.Name, artifact.Bytes, artifact.Sha256));
        }

        var manifest = new JobResultManifest(ResultContract.Version, completion.SessionId, completion.JobId, evidence,
            descriptors.OrderBy(artifact => artifact.Name, StringComparer.Ordinal).ToArray());
        return new(Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ResultJson))), manifest);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public enum ResultOutcome { Accepted, Invalid, Missing, Conflict }
    public sealed record ResultSubmission(ResultOutcome Outcome, VerifiedJobResult? Result = null);
}
