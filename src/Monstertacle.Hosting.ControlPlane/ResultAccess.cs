using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Monstertacle.Hosting.Contracts;
using Access = Monstertacle.Hosting.Security.BrokerPolicy.Access;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed partial class SessionStore
{
    public VerifiedSessionResult? ReadResults(Access access, Guid sessionId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var entry = FindOwnedSession(connection, transaction, access, sessionId);
        return AllowsAll(access, entry, "result") ? BuildAggregate(connection, transaction, entry!) : null;
    }

    public ArtifactContent? ReadArtifact(Access access, Guid sessionId, Guid jobId, string name)
    {
        if (!ResultContract.IsArtifactName(name))
        {
            return null;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var entry = FindOwnedSession(connection, transaction, access, sessionId);
        var job = entry?.Jobs.SingleOrDefault(item => item.JobId == jobId);
        if (entry is null || job is null || !access.Allows(entry.Project, [job.Lane], "read") ||
            !access.Allows(entry.Project, [job.Lane], "artifact"))
        {
            return null;
        }

        var manifest = ReadJobResult(connection, transaction, sessionId, jobId);
        var descriptor = manifest?.Manifest.Artifacts.SingleOrDefault(artifact => artifact.Name == name);
        if (descriptor is null)
        {
            return null;
        }

        return new(descriptor, ReadVerifiedBytes(connection, transaction, jobId, descriptor));
    }

    private static byte[] ReadVerifiedBytes(SqliteConnection connection, SqliteTransaction transaction, Guid jobId,
        ArtifactDescriptor descriptor)
    {
        using var command = Command(connection, transaction, """
            SELECT content FROM job_artifacts
            WHERE job_id = $job AND name = $name AND bytes = $bytes AND digest = $digest
              AND length(content) = $bytes AND length(content) <= $maximum;
            """, ("$job", jobId.ToString("D")), ("$name", descriptor.Name), ("$bytes", descriptor.Bytes),
            ("$digest", descriptor.Sha256), ("$maximum", ResultContract.MaxTotalArtifactBytes));
        if (command.ExecuteScalar() is not byte[] content || Sha256(content) != descriptor.Sha256)
        {
            throw new InvalidDataException("The stored artifact failed integrity verification.");
        }

        return content;
    }

    public (ResultOutcome Outcome, ResultAcceptance? Receipt) Accept(Access access, Guid sessionId, AcceptResult request)
    {
        if (request.MutationId == Guid.Empty || request.Sha256 is not { Length: 64 } ||
            !request.Sha256.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
        {
            return (ResultOutcome.Invalid, null);
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var entry = FindOwnedSession(connection, transaction, access, sessionId);
        if (!AllowsAll(access, entry, "accept-result") || !AllowsAll(access, entry, "result"))
        {
            return (ResultOutcome.Missing, null);
        }

        var project = entry!.Project;
        var key = request.MutationId.ToString("D");
        using var lookup = Command(connection, transaction, """
            SELECT session_id, digest, accepted_utc FROM result_acceptances
            WHERE principal = $principal AND project = $project AND mutation = $mutation;
            """, ("$principal", access.Principal), ("$project", project), ("$mutation", key));
        ResultAcceptance? existing = null;
        using (var row = lookup.ExecuteReader())
        {
            if (row.Read())
            {
                existing = new(Guid.ParseExact(row.GetString(0), "D"), request.MutationId, row.GetString(1),
                    DateTimeOffset.ParseExact(row.GetString(2), "O", CultureInfo.InvariantCulture));
            }
        }

        if (existing is not null && existing.SessionId != sessionId)
        {
            var previous = FindOwnedSession(connection, transaction, access, existing.SessionId);
            return (AllowsAll(access, previous, "accept-result") && AllowsAll(access, previous, "result")
                ? ResultOutcome.Conflict : ResultOutcome.Missing, null);
        }

        var aggregate = BuildAggregate(connection, transaction, entry);
        if (!aggregate.Manifest.Complete || aggregate.Sha256 != request.Sha256 ||
            (existing is not null && existing.Sha256 != request.Sha256))
        {
            return (ResultOutcome.Conflict, null);
        }

        foreach (var child in aggregate.Manifest.Jobs)
        {
            foreach (var artifact in child.Result?.Manifest.Artifacts ?? [])
            {
                _ = ReadVerifiedBytes(connection, transaction, child.Status.JobId, artifact);
            }
        }

        if (existing is not null)
        {
            return (ResultOutcome.Accepted, existing);
        }

        var accepted = time.GetUtcNow();
        using var insert = Command(connection, transaction, """
            INSERT INTO result_acceptances (principal, project, mutation, session_id, digest, accepted_utc)
            VALUES ($principal, $project, $mutation, $session, $digest, $accepted);
            """, ("$principal", access.Principal), ("$project", project), ("$mutation", key),
            ("$session", sessionId.ToString("D")), ("$digest", request.Sha256),
            ("$accepted", accepted.ToString("O", CultureInfo.InvariantCulture)));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return (ResultOutcome.Accepted, new(sessionId, request.MutationId, request.Sha256, accepted));
    }

    private static bool AllowsAll(Access access, Entry? entry, string action) =>
        entry is not null && CanRead(access, entry) &&
        access.Allows(entry.Project, entry.Jobs.Select(job => job.Lane).ToArray(), action);

    private static VerifiedSessionResult BuildAggregate(SqliteConnection connection, SqliteTransaction transaction, Entry entry)
    {
        var children = entry.Jobs.Select(job =>
            new JobResultSummary(job, ReadJobResult(connection, transaction, entry.Id, job.JobId))).ToArray();
        var complete = children.All(child => child.Result is not null ||
            child.Status is { State: "cancelled", ExecutionCertainty: null, CancellationRequested: true });
        var manifest = new SessionResultManifest(ResultContract.Version, entry.Id, entry.Project, children, complete);
        return new(Sha256(JsonSerializer.SerializeToUtf8Bytes(manifest, ResultJson)), manifest);
    }

    private static VerifiedJobResult? ReadJobResult(SqliteConnection connection, SqliteTransaction transaction,
        Guid sessionId, Guid jobId)
    {
        using var command = Command(connection, transaction, """
            SELECT digest, manifest FROM job_results WHERE job_id = $job;
            """, ("$job", jobId.ToString("D")));
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }

        var digest = row.GetString(0);
        var body = row.GetString(1);
        if (Sha256(Encoding.UTF8.GetBytes(body)) != digest ||
            JsonSerializer.Deserialize<JobResultManifest>(body, ResultJson) is not { } manifest ||
            manifest.Version != ResultContract.Version || manifest.SessionId != sessionId || manifest.JobId != jobId)
        {
            throw new InvalidDataException("The stored result manifest failed integrity verification.");
        }

        return new(digest, manifest);
    }
}
