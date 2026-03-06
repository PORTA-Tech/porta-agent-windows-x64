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

        if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // Leave best-effort cleanup for OS if lock is still being released.
            }
        }
    }
}
