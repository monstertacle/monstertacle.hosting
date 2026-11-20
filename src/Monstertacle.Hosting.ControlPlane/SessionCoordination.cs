using Monstertacle.Hosting.Security;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Monstertacle.Hosting.Contracts;
using Access = Monstertacle.Hosting.Security.BrokerPolicy.Access;
using Worker = Monstertacle.Hosting.ControlPlane.WorkerRegistrations.WorkerIdentity;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed partial class SessionStore
{
    private const string WorkerProjection = """
        SELECT j.id, j.session_id, j.fence, j.lane, j.state, j.cancel_requested,
               s.project, s.task, s.principal, s.issuer, s.subject, j.execution_certainty, j.cancel_unsupported
        FROM jobs j JOIN sessions s ON s.id = j.session_id
        """;

    public JobClaim? Claim(Worker worker, Guid mutation, BrokerPolicy policy)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = ReadWorkerJob(connection, transaction, """
             WHERE j.worker_id = $worker AND j.lane = $lane AND j.claim_mutation = $mutation;
            """, ("$worker", worker.Id), ("$lane", worker.Lane), ("$mutation", mutation.ToString("D")));
        if (existing is not null)
        {
            return existing.Claim;
        }

        var current = policy.Read();
        if (current is null)
        {
            return null;
        }

        var grants = current.People.SelectMany(person => person.Grants
            .Where(grant => grant.Lane == worker.Lane && grant.Actions.Contains("start", StringComparer.Ordinal))
            .SelectMany(grant => person.Identities.Select(identity =>
                new { person = person.Id, project = grant.Project, issuer = identity.Issuer, subject = identity.Subject })));
        using var candidate = Command(connection, transaction, """
            SELECT j.id, j.session_id
            FROM jobs j JOIN sessions s ON s.id = j.session_id
            WHERE j.lane = $lane AND j.state = 'queued' AND j.cancel_requested = 0
              AND EXISTS (
                SELECT 1 FROM json_each($grants) g
                WHERE g.value ->> 'person' = s.principal AND g.value ->> 'project' = s.project
                  AND g.value ->> 'issuer' = s.issuer AND g.value ->> 'subject' = s.subject
              )
            ORDER BY s.admitted_utc, j.id LIMIT 1;
            """, ("$lane", worker.Lane), ("$grants", JsonSerializer.Serialize(grants)));
        Guid jobId;
        Guid sessionId;
        using (var row = candidate.ExecuteReader())
        {
            if (!row.Read())
            {
                return null;
            }

            jobId = Guid.ParseExact(row.GetString(0), "D");
            sessionId = Guid.ParseExact(row.GetString(1), "D");
        }

        var fence = Guid.NewGuid();
        using var claim = Command(connection, transaction, """
            UPDATE jobs SET state = 'claimed', worker_id = $worker, claim_mutation = $mutation, fence = $fence
            WHERE id = $id;
            """, ("$worker", worker.Id), ("$mutation", mutation.ToString("D")), ("$fence", fence.ToString("D")),
            ("$id", jobId.ToString("D")));
        claim.ExecuteNonQuery();
        transaction.Commit();
        return new(jobId, sessionId, fence, worker.Lane, "claimed", false);
    }

    public JobClaim? Observe(Worker worker, Guid jobId, Guid fence)
    {
        using var connection = Open();
        return FindClaim(connection, null, worker, jobId, fence)?.Claim;
    }

    public DispatchDecision AuthorizeDispatch(Worker worker, Guid jobId, Guid fence, BrokerPolicy policy)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = FindClaim(connection, transaction, worker, jobId, fence);
        if (job is null)
        {
            return new(DispatchOutcome.Missing);
        }

        var access = BrokerPolicy.Resolve(policy.Read(), job.Identity);
        if (access is null || access.Principal != job.Principal || !access.Allows(job.Project, [worker.Lane], "start"))
        {
            return new(DispatchOutcome.Denied);
        }

        if (job.Claim.CancellationRequested || job.Claim.State is not ("claimed" or "dispatch-authorized"))
        {
            return new(DispatchOutcome.Unavailable);
        }

        if (job.Claim.State == "claimed")
        {
            using var dispatch = Command(connection, transaction, """
                UPDATE jobs SET state = 'dispatch-authorized' WHERE id = $id;
                """, ("$id", jobId.ToString("D")));
            dispatch.ExecuteNonQuery();
        }

        transaction.Commit();
        return new(DispatchOutcome.Authorized,
            new(job.Claim with { State = "dispatch-authorized" }, job.Project, job.Task));
    }

    public Cancellation Cancel(Access access, Guid sessionId, Guid mutation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = FindOwnedSession(connection, transaction, access, sessionId);
        if (!CanCancel(access, session))
        {
            return new(CancelOutcome.Missing);
        }

        using var receipt = Command(connection, transaction, """
            SELECT session_id FROM cancellations
            WHERE principal = $principal AND project = $project AND mutation_id = $mutation;
            """, ("$principal", access.Principal), ("$project", session!.Project), ("$mutation", mutation.ToString("D")));
        if (receipt.ExecuteScalar() is string target)
        {
            if (target == sessionId.ToString("D"))
            {
                return new(CancelOutcome.Accepted, ProjectStatus(session));
            }

            return CanCancel(access, FindOwnedSession(connection, transaction, access, Guid.ParseExact(target, "D")))
                ? new(CancelOutcome.Conflict)
                : new(CancelOutcome.Missing);
        }

        using var cancel = Command(connection, transaction, """
            INSERT INTO cancellations (session_id, principal, project, mutation_id)
            VALUES ($id, $principal, $project, $mutation);
            UPDATE jobs
            SET state = CASE WHEN state IN ('queued', 'claimed') THEN 'cancelled' ELSE state END,
                cancel_requested = 1
            WHERE session_id = $id AND (state IN ('queued', 'claimed', 'dispatch-authorized', 'running')
                OR execution_certainty = 'unknown');
            """, ("$id", sessionId.ToString("D")), ("$principal", access.Principal), ("$project", session.Project),
            ("$mutation", mutation.ToString("D")));
        cancel.ExecuteNonQuery();
        var updated = FindOwnedSession(connection, transaction, access, sessionId)!;
        transaction.Commit();
        return new(CancelOutcome.Accepted, ProjectStatus(updated));
    }

    private static bool CanCancel(Access access, Entry? entry) =>
        entry is not null && CanRead(access, entry) &&
        access.Allows(entry.Project, entry.Jobs.Select(job => job.Lane).ToArray(), "cancel");

    private static Entry? FindOwnedSession(SqliteConnection connection, SqliteTransaction? transaction, Access access, Guid id) =>
        ReadRows(connection, transaction, Projection + " WHERE s.principal = $principal AND s.id = $id ORDER BY j.ordinal;",
            ("$principal", access.Principal), ("$id", id.ToString("D"))).SingleOrDefault();

    private static WorkerJob? FindClaim(SqliteConnection connection, SqliteTransaction? transaction, Worker worker,
        Guid jobId, Guid fence) =>
        ReadWorkerJob(connection, transaction, """
             WHERE j.worker_id = $worker AND j.lane = $lane AND j.id = $id AND j.fence = $fence;
            """, ("$worker", worker.Id), ("$lane", worker.Lane), ("$id", jobId.ToString("D")), ("$fence", fence.ToString("D")));

    private static WorkerJob? ReadWorkerJob(SqliteConnection connection, SqliteTransaction? transaction, string where,
        params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, WorkerProjection + where, parameters);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }

        var claim = new JobClaim(Guid.ParseExact(row.GetString(0), "D"), Guid.ParseExact(row.GetString(1), "D"),
            Guid.ParseExact(row.GetString(2), "D"), row.GetString(3), row.GetString(4), row.GetBoolean(5));
        return new(claim, row.GetString(6), row.GetString(7), row.GetString(8), new(row.GetString(9), row.GetString(10)),
            row.IsDBNull(11) ? null : row.GetString(11), row.GetBoolean(12));
    }

    public ObservationOutcome RecordObservation(Worker worker, Guid jobId, Guid fence, JobObservation observation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = FindClaim(connection, transaction, worker, jobId, fence);
        if (job is null)
        {
            return ObservationOutcome.Missing;
        }

        if (job.Claim.State == observation.State && job.Certainty == observation.Certainty &&
            job.CancellationUnsupported == observation.CancellationUnsupported)
        {
            return ObservationOutcome.Accepted;
        }

        var refinesUncertainty = job.Certainty == "unknown" && observation.Certainty == "confirmed-ended" &&
            job.Claim.State == observation.State;
        if ((job.Claim.State is not ("dispatch-authorized" or "running") && !refinesUncertainty) ||
            (job.Claim.State == "running" && observation.Certainty == "not-started") ||
            (observation.CancellationUnsupported && !job.Claim.CancellationRequested) ||
            (job.CancellationUnsupported && !observation.CancellationUnsupported))
        {
            return ObservationOutcome.Conflict;
        }

        using var update = Command(connection, transaction, """
            UPDATE jobs SET state = $state, execution_certainty = $certainty, cancel_unsupported = $unsupported
            WHERE id = $id;
            """, ("$state", observation.State), ("$certainty", observation.Certainty),
            ("$unsupported", observation.CancellationUnsupported), ("$id", jobId.ToString("D")));
        update.ExecuteNonQuery();
        transaction.Commit();
        return ObservationOutcome.Accepted;
    }

    private sealed record WorkerJob(JobClaim Claim, string Project, string Task, string Principal,
        BrokerPolicy.Identity Identity, string? Certainty, bool CancellationUnsupported);
    public sealed record DispatchDecision(DispatchOutcome Outcome, JobDispatch? Dispatch = null);
    public enum DispatchOutcome { Authorized, Missing, Denied, Unavailable }
    public sealed record Cancellation(CancelOutcome Outcome, SessionStatus? Session = null);
    public enum CancelOutcome { Accepted, Conflict, Missing }
    public enum ObservationOutcome { Accepted, Conflict, Missing }
}
