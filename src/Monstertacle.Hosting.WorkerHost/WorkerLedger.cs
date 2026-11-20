using System.Text.Json;
using Microsoft.Data.Sqlite;
using Monstertacle.Hosting.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class WorkerLedger
{
    private readonly string connectionString;

    public WorkerLedger(string stateDirectory)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(stateDirectory, "worker-ledger.db"),
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS assignment (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                mutation TEXT NOT NULL,
                claim TEXT
            ) STRICT;
            INSERT OR IGNORE INTO assignment (singleton, mutation) VALUES (1, $mutation);
            """;
        command.Parameters.AddWithValue("$mutation", Guid.NewGuid().ToString("D"));
        command.ExecuteNonQuery();
    }

    public (Guid Mutation, JobClaim? Claim) Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mutation, claim FROM assignment WHERE singleton = 1;";
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidOperationException("The durable worker assignment is missing.");
        }

        return (Guid.ParseExact(row.GetString(0), "D"),
            row.IsDBNull(1) ? null : JsonSerializer.Deserialize<JobClaim>(row.GetString(1)));
    }

    public void Attach(Guid mutation, JobClaim claim) =>
        Update(mutation, mutation, JsonSerializer.Serialize(claim));

    public void Advance(Guid mutation) =>
        Update(mutation, Guid.NewGuid(), null);

    private void Update(Guid expected, Guid mutation, string? claim)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE assignment SET mutation = $mutation, claim = $claim WHERE mutation = $expected;";
        command.Parameters.AddWithValue("$expected", expected.ToString("D"));
        command.Parameters.AddWithValue("$mutation", mutation.ToString("D"));
        command.Parameters.AddWithValue("$claim", (object?)claim ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The durable worker assignment changed unexpectedly.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
