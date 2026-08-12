using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core.Tests.Harness;

public enum ScenarioDatabaseMode
{
    InMemory,
    TempFile
}

public sealed class DeterministicScenarioDatabase : IDisposable
{
    private bool _disposed;

    private DeterministicScenarioDatabase(SqliteConnection connection, string connectionString, string? filePath)
    {
        Connection = connection;
        ConnectionString = connectionString;
        FilePath = filePath;

        Connection.Open();
        Migrations.Migrate(Connection);
    }

    public SqliteConnection Connection { get; }

    public string ConnectionString { get; }

    public string? FilePath { get; }

    public static DeterministicScenarioDatabase Create(ScenarioDatabaseMode mode)
    {
        return mode switch
        {
            ScenarioDatabaseMode.InMemory => CreateInMemory(),
            ScenarioDatabaseMode.TempFile => CreateTempFile(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported scenario database mode.")
        };
    }

    public static DeterministicScenarioDatabase CreateInMemory()
    {
        string name = $"porta_step19_{Guid.NewGuid():N}";
        string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        return new DeterministicScenarioDatabase(connection, connectionString, filePath: null);
    }

    public static DeterministicScenarioDatabase CreateTempFile(string? baseDirectory = null)
    {
        string directory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Path.GetTempPath(), "porta-step19-tests")
            : baseDirectory;
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"scenario-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={filePath}";
        var connection = new SqliteConnection(connectionString);
        return new DeterministicScenarioDatabase(connection, connectionString, filePath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Dispose();

        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            // Maintenance item (c), 2026-08-12 — the SECOND temp directory. This one disposes its
            // connection properly, but it deleted only the .db and left -wal/-shm behind, and a
            // disposed connection still leaves a POOLED handle on the file. Same fix as the
            // scenario runner: clear the pool, then delete all three.
            try
            {
                SqliteConnection.ClearPool(Connection);
            }
            catch (Exception)
            {
                // Cleanup must never fail a test.
            }

            DeterministicFinalizerScenarioRunner.DeleteDatabaseFiles(FilePath);
        }
    }
}
