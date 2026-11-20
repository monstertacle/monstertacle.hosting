using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Monstertacle.Hosting.Contracts;
using Access = Monstertacle.Hosting.Security.BrokerPolicy.Access;

namespace Monstertacle.Hosting.ControlPlane;

internal sealed partial class SessionStore
{
    private const string Projection = """
        SELECT s.id, s.project, s.admitted_utc, s.payload_digest, j.id, j.lane, j.state, j.cancel_requested,
               j.execution_certainty, j.cancel_unsupported
        FROM sessions s JOIN jobs j ON j.session_id = s.id
        """;

    private readonly string connectionString;
    private readonly TimeProvider time;

    public SessionStore(IConfiguration configuration, TimeProvider time)
    {
        this.time = time;
        var path = configuration["Broker:DatabaseFile"];
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Broker:DatabaseFile must name a durable SQLite file.");
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();

        using var connection = Open();
        using var journal = Command(connection, null, "PRAGMA journal_mode = WAL;");
        if (!string.Equals(journal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The broker database requires SQLite WAL mode.");
        }

        using var transaction = connection.BeginTransaction();
        using var version = Command(connection, transaction, "PRAGMA user_version;");
        var schemaVersion = (long)version.ExecuteScalar()!;
        switch (schemaVersion)
        {
            case 4L:
            case 3L:
            case 2L:
            case 1L:
                break;
            case 0L:
                using (var schema = Command(connection, transaction, """
                    CREATE TABLE sessions (
                        id TEXT PRIMARY KEY,
                        principal TEXT NOT NULL,
                        issuer TEXT NOT NULL,
                        subject TEXT NOT NULL,
                        project TEXT NOT NULL,
                        mutation_id TEXT NOT NULL,
                        payload_digest TEXT NOT NULL,
                        task TEXT NOT NULL,
                        admitted_utc TEXT NOT NULL,
                        UNIQUE (principal, project, mutation_id)
                    ) STRICT;
                    CREATE INDEX sessions_owner ON sessions (principal, project, id);
                    CREATE TABLE jobs (
                        id TEXT PRIMARY KEY,
                        session_id TEXT NOT NULL REFERENCES sessions(id),
                        ordinal INTEGER NOT NULL,
                        lane TEXT NOT NULL,
                        state TEXT NOT NULL,
                        UNIQUE (session_id, ordinal)
                    ) STRICT;
                    PRAGMA user_version = 1;
                    """))
                {
                    schema.ExecuteNonQuery();
                }

                break;
            default:
                throw new InvalidOperationException("Unsupported broker database schema.");
        }

        if (schemaVersion < 2L)
        {
            using var upgrade = Command(connection, transaction, """
                ALTER TABLE jobs ADD COLUMN worker_id TEXT;
                ALTER TABLE jobs ADD COLUMN claim_mutation TEXT;
                ALTER TABLE jobs ADD COLUMN fence TEXT;
                ALTER TABLE jobs ADD COLUMN cancel_requested INTEGER NOT NULL DEFAULT 0;
                CREATE UNIQUE INDEX claims_by_worker ON jobs (worker_id, claim_mutation);
                CREATE INDEX jobs_by_lane ON jobs (lane, state, session_id);
                CREATE TABLE cancellations (
                    session_id TEXT NOT NULL REFERENCES sessions(id),
                    principal TEXT NOT NULL,
                    project TEXT NOT NULL,
                    mutation_id TEXT NOT NULL,
                    PRIMARY KEY (principal, project, mutation_id)
                ) STRICT;
                PRAGMA user_version = 2;
                """);
            upgrade.ExecuteNonQuery();
        }

        if (schemaVersion < 3L)
        {
            using var upgrade = Command(connection, transaction, """
                ALTER TABLE jobs ADD COLUMN execution_certainty TEXT;
                ALTER TABLE jobs ADD COLUMN cancel_unsupported INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 3;
                """);
            upgrade.ExecuteNonQuery();
        }

        if (schemaVersion < 4L)
        {
            using var upgrade = Command(connection, transaction, """
                CREATE TABLE job_results (
                    job_id TEXT PRIMARY KEY REFERENCES jobs(id),
                    digest TEXT NOT NULL,
                    manifest TEXT NOT NULL
                ) STRICT;
                CREATE TABLE job_artifacts (
                    job_id TEXT NOT NULL REFERENCES job_results(job_id),
                    name TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    digest TEXT NOT NULL,
                    content BLOB NOT NULL,
                    PRIMARY KEY (job_id, name)
                ) STRICT;
                PRAGMA user_version = 4;
                """);
            upgrade.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Admission Start(Access access, StartSession request)
    {
        if (!access.HasGrant(request.Project, "start") || !access.HasGrant(request.Project, "read"))
        {
            return new(AdmissionOutcome.Denied);
        }

        var digest = request.GetPayloadDigest();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = FindMutation(connection, transaction, access, request.Project, request.MutationId);
        if (existing is not null)
        {
            var lanes = existing.Jobs.Select(job => job.Lane).ToArray();
            if (!access.Allows(request.Project, lanes, "start") || !access.Allows(request.Project, lanes, "read"))
            {
                return new(AdmissionOutcome.Denied);
            }

            return string.Equals(existing.Digest, digest, StringComparison.Ordinal)
                ? new(AdmissionOutcome.Replayed, ProjectStatus(existing))
                : new(AdmissionOutcome.Conflict);
        }

        var plan = access.Plan(request.Project);
        if (plan is null || !access.Allows(request.Project, plan, "start") || !access.Allows(request.Project, plan, "read"))
        {
            return new(AdmissionOutcome.Denied);
        }

        using (var queue = Command(connection, transaction, """
            SELECT COUNT(*) FROM sessions AS s
            WHERE s.project = $project AND EXISTS (
                SELECT 1 FROM jobs j WHERE j.session_id = s.id
                AND (j.state IN ('queued', 'claimed', 'dispatch-authorized', 'running') OR j.execution_certainty = 'unknown')
            );
            """, ("$project", request.Project)))
        {
            if ((long)queue.ExecuteScalar()! >= 256)
            {
                return new(AdmissionOutcome.Capacity);
            }
        }

        var id = Guid.NewGuid();
        var admitted = time.GetUtcNow();
        var jobs = plan.Select(lane => new JobStatus(Guid.NewGuid(), lane, "queued")).ToArray();
        using (var insert = Command(connection, transaction, """
            INSERT INTO sessions (id, principal, issuer, subject, project, mutation_id, payload_digest, task, admitted_utc)
            VALUES ($id, $principal, $issuer, $subject, $project, $mutation, $digest, $task, $admitted);
            """, ("$id", id.ToString("D")), ("$principal", access.Principal), ("$issuer", access.Identity.Issuer!),
            ("$subject", access.Identity.Subject!), ("$project", request.Project),
            ("$mutation", request.MutationId.ToString("D")), ("$digest", digest), ("$task", request.Task),
            ("$admitted", admitted.ToString("O", CultureInfo.InvariantCulture))))
        {
            insert.ExecuteNonQuery();
        }

        for (var ordinal = 0; ordinal < jobs.Length; ordinal++)
        {
            var job = jobs[ordinal];
            using var insert = Command(connection, transaction, """
                INSERT INTO jobs (id, session_id, ordinal, lane, state) VALUES ($job, $session, $ordinal, $lane, $state);
                """, ("$job", job.JobId.ToString("D")), ("$session", id.ToString("D")), ("$ordinal", ordinal),
                ("$lane", job.Lane), ("$state", job.State));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new(AdmissionOutcome.Created, ProjectStatus(new(id, request.Project, admitted, digest, [.. jobs])));
    }

    public SessionStatus? Status(Access access, Guid id)
    {
        using var connection = Open();
        var data = ReadRows(connection, null, Projection + " WHERE s.principal = $principal AND s.id = $id ORDER BY j.ordinal;",
            ("$principal", access.Principal), ("$id", id.ToString("D"))).SingleOrDefault();
        return CanRead(access, data) ? ProjectStatus(data!) : null;
    }

    public SessionStatus? Lookup(Access access, string project, Guid mutation)
    {
        if (!access.HasGrant(project, "read"))
        {
            return null;
        }

        using var connection = Open();
        var data = FindMutation(connection, null, access, project, mutation);
        return CanRead(access, data) ? ProjectStatus(data!) : null;
    }

    public SessionPage List(Access access, string project, Guid? after, int limit)
    {
        using var connection = Open();
        var entries = ReadRows(connection, null, """
            WITH page AS (
                SELECT candidate.id FROM sessions candidate
                WHERE candidate.principal = $principal AND candidate.project = $project AND candidate.id > $after
                  AND NOT EXISTS (
                    SELECT 1 FROM jobs child WHERE child.session_id = candidate.id
                      AND child.lane NOT IN (SELECT value FROM json_each($lanes))
                  )
                ORDER BY candidate.id LIMIT $limit
            )
            """ + "\n" + Projection + " JOIN page ON page.id = s.id ORDER BY s.id, j.ordinal;",
            ("$principal", access.Principal), ("$project", project), ("$after", after?.ToString("D") ?? ""),
            ("$lanes", JsonSerializer.Serialize(access.GrantedLanes(project, "read"))),
            ("$limit", limit + 1));

        return new(entries.Take(limit).Select(ProjectStatus).ToArray(),
            entries.Count > limit ? entries[limit - 1].Id : null);
    }

    private static Entry? FindMutation(SqliteConnection connection, SqliteTransaction? transaction, Access access,
        string project, Guid mutation) =>
        ReadRows(connection, transaction, Projection + """
             WHERE s.principal = $principal AND s.project = $project AND s.mutation_id = $mutation
             ORDER BY j.ordinal;
            """, ("$principal", access.Principal), ("$project", project), ("$mutation", mutation.ToString("D")))
            .SingleOrDefault();

    private static bool CanRead(Access access, Entry? entry) =>
        entry is not null && access.Allows(entry.Project, entry.Jobs.Select(job => job.Lane).ToArray(), "read");

    private SessionStatus ProjectStatus(Entry entry) =>
        new(entry.Id, entry.Project, entry.Admitted, time.GetUtcNow(), entry.Jobs);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string text,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static List<Entry> ReadRows(SqliteConnection connection, SqliteTransaction? transaction, string query,
        params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, query, parameters);
        using var reader = command.ExecuteReader();
        var entries = new List<Entry>();
        Entry? current = null;
        while (reader.Read())
        {
            var id = Guid.ParseExact(reader.GetString(0), "D");
            if (current?.Id != id)
            {
                current = new(id, reader.GetString(1),
                    DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture),
                    reader.GetString(3), []);
                entries.Add(current);
            }

            current.Jobs.Add(new(Guid.ParseExact(reader.GetString(4), "D"), reader.GetString(5), reader.GetString(6),
                reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9)));
        }

        return entries;
    }

    private sealed record Entry(Guid Id, string Project, DateTimeOffset Admitted, string Digest, List<JobStatus> Jobs);
    public sealed record Admission(AdmissionOutcome Outcome, SessionStatus? Session = null);
    public enum AdmissionOutcome { Created, Replayed, Conflict, Denied, Capacity }
}
