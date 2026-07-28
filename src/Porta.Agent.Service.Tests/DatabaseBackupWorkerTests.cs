using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Porta.Agent.Service;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// Retention is the part of the backup worker that deletes things, so it is the part that gets
/// tested. The invariant that matters most: no combination of rules may ever remove the last
/// surviving snapshot.
///
/// The tick and snapshot tests below run against real SQLite files in a temp folder rather than
/// mocks: VACUUM INTO either produces a database you can open and read, or it does not, and only
/// the real thing answers that.
/// </summary>
public sealed class DatabaseBackupWorkerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
    private const long OneMb = 1024L * 1024;

    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, so a disposed connection still holds the file
        // open until the pool is cleared. Without this the directory delete fails on Windows.
        SqliteConnection.ClearAllPools();

        foreach (string directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // A leaked temp directory is not worth failing a test run over.
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static DatabaseBackupWorker NewWorker() => new(NullLogger<DatabaseBackupWorker>.Instance);

    private string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "porta-backup-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>
    /// Filenames are written from local wall-clock time and ParseTimestamp reads them back the same
    /// way, so tests have to build their expectations in local time too, or they pass in Dubai and
    /// fail in London.
    /// </summary>
    private static DateTimeOffset LocalAt(int year, int month, int day, int hour, int minute = 0) =>
        new(DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Local));

    /// <summary>A real SQLite database with <paramref name="rows"/> known rows in it.</summary>
    private static string CreateTestDatabase(string directory, int rows = 50)
    {
        string path = Path.Combine(directory, "core-state.db");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE buckets (id INTEGER PRIMARY KEY, label TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO buckets (label) VALUES ($label);";
            SqliteParameter label = insert.CreateParameter();
            label.ParameterName = "$label";
            insert.Parameters.Add(label);

            for (int i = 0; i < rows; i++)
            {
                label.Value = $"row-{i}";
                insert.ExecuteNonQuery();
            }
        }

        return path;
    }

    private static int CountRows(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM buckets;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A file that looks like a finished snapshot. Retention reads only the name and the length, so
    /// this does not need to be a real database — the tests that DO need one say so.
    /// </summary>
    private static string WriteFakeSnapshot(string backupDirectory, DateTimeOffset whenLocal, int sizeBytes = 1024)
    {
        string name = $"core-state-{whenLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db";
        string path = Path.Combine(backupDirectory, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private static string[] SnapshotsIn(string backupDirectory) =>
        Directory.GetFiles(backupDirectory, "core-state-*.db")
            .Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static DatabaseBackupWorker.BackupCandidate Candidate(int daysOld, long sizeBytes = OneMb) =>
        new($@"C:\ProgramData\PortaAgent\backups\core-state-{daysOld:00}.db", Now.AddDays(-daysOld), sizeBytes);

    // ------------------------------------------------------------------ age limit

    // These pass retention explicitly rather than reading the configured constant: the rule is
    // being tested, not the current setting. Changing the setting must not break the logic tests.
    private const int TestRetentionDays = 14;

    [Fact]
    public void SelectForDeletion_DropsSnapshotsOlderThanRetention()
    {
        var candidates = new[] { Candidate(0), Candidate(10), Candidate(20), Candidate(30) };

        var doomed = DatabaseBackupWorker.SelectForDeletion(
            candidates, Now, TestRetentionDays, DatabaseBackupWorker.MaxFolderBytes);

        Assert.Equal(2, doomed.Count);
        Assert.Contains(doomed, c => c.TimestampUtc == Now.AddDays(-20));
        Assert.Contains(doomed, c => c.TimestampUtc == Now.AddDays(-30));
    }

    [Fact]
    public void SelectForDeletion_KeepsSnapshotsInsideRetention()
    {
        var candidates = new[] { Candidate(0), Candidate(5), Candidate(13) };

        var doomed = DatabaseBackupWorker.SelectForDeletion(
            candidates, Now, TestRetentionDays, DatabaseBackupWorker.MaxFolderBytes);

        Assert.Empty(doomed);
    }

    /// <summary>
    /// Guards the configured values against the mistake that was actually made: a size ceiling set
    /// below the size of a single real snapshot silently reduces retention to one file. A measured
    /// backup was ~640 MB, so the ceiling must comfortably clear RetentionDays worth of them.
    /// </summary>
    [Fact]
    public void ConfiguredCeiling_HoldsAFullRetentionWindowOfRealSnapshots()
    {
        const long measuredSnapshotBytes = 640L * 1024 * 1024;

        long needed = measuredSnapshotBytes * DatabaseBackupWorker.RetentionDays;

        Assert.True(
            DatabaseBackupWorker.MaxFolderBytes >= needed,
            $"MaxFolderBytes ({DatabaseBackupWorker.MaxFolderBytes / (1024 * 1024)} MB) cannot hold "
                + $"{DatabaseBackupWorker.RetentionDays} snapshots of ~640 MB ({needed / (1024 * 1024)} MB needed).");
    }

    // ------------------------------------------------------------------ size ceiling

    [Fact]
    public void SelectForDeletion_TrimsOldestFirstUntilUnderTheSizeCeiling()
    {
        // Four 10 MB snapshots, all recent, against a 25 MB ceiling: the two oldest must go.
        var candidates = new[]
        {
            Candidate(0, 10 * OneMb),
            Candidate(1, 10 * OneMb),
            Candidate(2, 10 * OneMb),
            Candidate(3, 10 * OneMb),
        };

        var doomed = DatabaseBackupWorker.SelectForDeletion(candidates, Now, DatabaseBackupWorker.RetentionDays, 25 * OneMb);

        Assert.Equal(2, doomed.Count);
        Assert.Contains(doomed, c => c.TimestampUtc == Now.AddDays(-3));
        Assert.Contains(doomed, c => c.TimestampUtc == Now.AddDays(-2));
    }

    // ------------------------------------------------------------------ the invariant

    [Fact]
    public void SelectForDeletion_NeverDeletesTheNewestEvenWhenItAloneExceedsTheCeiling()
    {
        var candidates = new[] { Candidate(0, 900 * OneMb), Candidate(1, 900 * OneMb) };

        var doomed = DatabaseBackupWorker.SelectForDeletion(candidates, Now, DatabaseBackupWorker.RetentionDays, 100 * OneMb);

        Assert.Single(doomed);
        Assert.Equal(Now.AddDays(-1), doomed[0].TimestampUtc);
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesTheNewestEvenWhenEverythingIsExpired()
    {
        var candidates = new[] { Candidate(400), Candidate(500) };

        var doomed = DatabaseBackupWorker.SelectForDeletion(
            candidates, Now, DatabaseBackupWorker.RetentionDays, DatabaseBackupWorker.MaxFolderBytes);

        Assert.Single(doomed);
        Assert.Equal(Now.AddDays(-500), doomed[0].TimestampUtc);
    }

    [Fact]
    public void SelectForDeletion_IsANoOpForZeroOrOneSnapshot()
    {
        Assert.Empty(DatabaseBackupWorker.SelectForDeletion(
            Array.Empty<DatabaseBackupWorker.BackupCandidate>(), Now, 14, DatabaseBackupWorker.MaxFolderBytes));

        Assert.Empty(DatabaseBackupWorker.SelectForDeletion(
            new[] { Candidate(999) }, Now, 14, DatabaseBackupWorker.MaxFolderBytes));
    }

    // ------------------------------------------------------------------ scheduling

    [Fact]
    public void IsDue_IsFalseBeforeTheBackupHour()
    {
        var nowLocal = new DateTimeOffset(2026, 7, 27, 2, 59, 0, TimeSpan.Zero);
        Assert.False(DatabaseBackupWorker.IsDue(nowLocal, newestBackupLocal: null, backupHourLocal: 3));
    }

    [Fact]
    public void IsDue_IsTrueAfterTheBackupHourWhenNoBackupExists()
    {
        var nowLocal = new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
        Assert.True(DatabaseBackupWorker.IsDue(nowLocal, newestBackupLocal: null, backupHourLocal: 3));
    }

    [Fact]
    public void IsDue_IsFalseWhenTodayIsAlreadyBackedUp()
    {
        var nowLocal = new DateTimeOffset(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);
        var takenToday = new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
        Assert.False(DatabaseBackupWorker.IsDue(nowLocal, takenToday, backupHourLocal: 3));
    }

    // A laptop that is off at 03:00 must not silently skip the day.
    [Fact]
    public void IsDue_CatchesUpLaterInTheDayWhenTheMachineWasAsleep()
    {
        var nowLocal = new DateTimeOffset(2026, 7, 27, 11, 30, 0, TimeSpan.Zero);
        var yesterday = new DateTimeOffset(2026, 7, 26, 3, 0, 0, TimeSpan.Zero);
        Assert.True(DatabaseBackupWorker.IsDue(nowLocal, yesterday, backupHourLocal: 3));
    }

    // ------------------------------------------------------------------ filename parsing

    [Fact]
    public void ParseTimestamp_ReadsAWellFormedBackupName()
    {
        DateTimeOffset? parsed = DatabaseBackupWorker.ParseTimestamp("core-state-20260727-030000.db");

        Assert.NotNull(parsed);
        DateTime local = parsed!.Value.ToLocalTime().DateTime;
        Assert.Equal(new DateTime(2026, 7, 27, 3, 0, 0), local);
    }

    [Theory]
    [InlineData("core-state.db")]                    // the live database itself
    [InlineData("core-state-20260727-030000.db.tmp")] // an unfinished snapshot
    [InlineData("core-state-not-a-date.db")]
    [InlineData("something-else.db")]
    [InlineData("")]
    public void ParseTimestamp_RejectsAnythingThatIsNotAFinishedSnapshot(string fileName)
    {
        Assert.Null(DatabaseBackupWorker.ParseTimestamp(fileName));
    }

    // ------------------------------------------------------------------ free space

    [Fact]
    public void HasHeadroom_RequiresRoomForThisSnapshotAndTheNextOne()
    {
        const long dbBytes = 640 * OneMb;

        // Exactly enough for two: the one being written and its successor.
        Assert.True(DatabaseBackupWorker.HasHeadroom(availableFreeBytes: 1280 * OneMb, dbBytes, configuredMinFreeBytes: null));

        // Room for this one but not the next — refuse, so the disk is never filled to the brim.
        Assert.False(DatabaseBackupWorker.HasHeadroom(availableFreeBytes: 1000 * OneMb, dbBytes, configuredMinFreeBytes: null));
    }

    [Fact]
    public void HasHeadroom_RefusesWhenTheVolumeCannotEvenHoldTheSnapshot()
    {
        Assert.False(DatabaseBackupWorker.HasHeadroom(
            availableFreeBytes: 100 * OneMb, dbBytes: 640 * OneMb, configuredMinFreeBytes: null));
    }

    /// <summary>
    /// The point of deriving the requirement instead of hardcoding one: a small database on a small
    /// disk must still get backed up. The 2 GB constant this replaced refused this exact case.
    /// </summary>
    [Fact]
    public void HasHeadroom_BacksUpASmallDatabaseOnASmallDisk()
    {
        Assert.True(DatabaseBackupWorker.HasHeadroom(
            availableFreeBytes: 400 * OneMb, dbBytes: 60 * OneMb, configuredMinFreeBytes: null));
    }

    [Fact]
    public void HasHeadroom_UsesTheConfiguredFloorInsteadOfTheDerivedOne()
    {
        const long dbBytes = 60 * OneMb;

        // The derived rule would allow this; an explicit 4 GB floor does not.
        Assert.True(DatabaseBackupWorker.HasHeadroom(400 * OneMb, dbBytes, configuredMinFreeBytes: null));
        Assert.False(DatabaseBackupWorker.HasHeadroom(400 * OneMb, dbBytes, configuredMinFreeBytes: 4L * 1024 * OneMb));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("not-a-number", null)]
    [InlineData("-1", null)]
    [InlineData("0", 0L)]
    [InlineData(" 2147483648 ", 2147483648L)]
    public void ReadConfiguredMinFreeBytes_FallsBackToDerivedForAnythingItCannotUse(string? raw, long? expected)
    {
        string? previous = Environment.GetEnvironmentVariable(DatabaseBackupWorker.MinFreeBytesEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DatabaseBackupWorker.MinFreeBytesEnvVar, raw);
            Assert.Equal(expected, DatabaseBackupWorker.ReadConfiguredMinFreeBytes());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseBackupWorker.MinFreeBytesEnvVar, previous);
        }
    }

    // ------------------------------------------------------------------ CreateSnapshot

    [Fact]
    public void CreateSnapshot_WritesADatabaseThatCanBeOpenedAndRead()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory, rows: 50);

        string snapshot = DatabaseBackupWorker.CreateSnapshot(dbPath, backupDirectory, LocalAt(2026, 7, 27, 3));

        Assert.True(File.Exists(snapshot));
        Assert.Equal(50, CountRows(snapshot));
    }

    [Fact]
    public void CreateSnapshot_NamesTheFileSoParseTimestampReadsItBack()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);
        DateTimeOffset taken = LocalAt(2026, 7, 27, 3, 15);

        string snapshot = DatabaseBackupWorker.CreateSnapshot(dbPath, backupDirectory, taken);

        Assert.Equal("core-state-20260727-031500.db", Path.GetFileName(snapshot));
        DateTimeOffset? parsed = DatabaseBackupWorker.ParseTimestamp(Path.GetFileName(snapshot));
        Assert.NotNull(parsed);
        Assert.Equal(taken.ToUniversalTime(), parsed!.Value);
    }

    /// <summary>
    /// Contract 2: write to .tmp, then rename. Nothing that looks like a finished backup may exist
    /// before the copy is complete, and nothing half-written may survive one that is.
    /// </summary>
    [Fact]
    public void CreateSnapshot_LeavesNoTempFileBehind()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        DatabaseBackupWorker.CreateSnapshot(dbPath, backupDirectory, LocalAt(2026, 7, 27, 3));

        Assert.Empty(Directory.GetFiles(backupDirectory, "*.tmp"));
        Assert.Single(SnapshotsIn(backupDirectory));
    }

    /// <summary>VACUUM INTO refuses to write to a file that already exists, so a leftover must go.</summary>
    [Fact]
    public void CreateSnapshot_OverwritesAnAbandonedTempFileFromAnInterruptedRun()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory, rows: 12);
        DateTimeOffset taken = LocalAt(2026, 7, 27, 3);

        // Exactly what a killed process leaves: the target name, partially written.
        File.WriteAllText(Path.Combine(backupDirectory, "core-state-20260727-030000.db.tmp"), "torn");

        string snapshot = DatabaseBackupWorker.CreateSnapshot(dbPath, backupDirectory, taken);

        Assert.Equal(12, CountRows(snapshot));
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.tmp"));
    }

    // ------------------------------------------------------------------ RunTickCore

    [Fact]
    public void RunTickCore_TakesAReadableSnapshotWhenOneIsDue()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory, rows: 33);

        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 14));

        string[] snapshots = SnapshotsIn(backupDirectory);
        Assert.Single(snapshots);
        Assert.Equal(33, CountRows(snapshots[0]));
    }

    [Fact]
    public void RunTickCore_DoesNothingBeforeTheBackupHour()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 1));

        Assert.Empty(SnapshotsIn(backupDirectory));
    }

    [Fact]
    public void RunTickCore_TakesOnlyOneSnapshotPerDayAcrossRepeatedTicks()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);
        DatabaseBackupWorker worker = NewWorker();

        worker.RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 3));
        worker.RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 3, 15));
        worker.RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 21));

        Assert.Single(SnapshotsIn(backupDirectory));
    }

    [Fact]
    public void RunTickCore_ClearsTempFilesLeftByAnInterruptedRun()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);
        File.WriteAllText(Path.Combine(backupDirectory, "core-state-20260101-030000.db.tmp"), "torn");

        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 14));

        Assert.Empty(Directory.GetFiles(backupDirectory, "*.tmp"));
    }

    /// <summary>
    /// Retention used to run only after a snapshot had been written, so a day with no backup due
    /// was also a day with no pruning. Expired copies must go regardless.
    /// </summary>
    [Fact]
    public void RunTickCore_PrunesExpiredSnapshotsEvenWhenNoBackupIsDue()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        string expired = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 6, 20, 3));
        string takenToday = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 7, 27, 3));

        // 14:00 on a day that already has a snapshot: nothing is due.
        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 14));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(takenToday));
        Assert.Single(SnapshotsIn(backupDirectory)); // and no new one was written
    }

    /// <summary>
    /// The bug this replaces, stated as a test: pruning is what frees the space a snapshot needs,
    /// so gating it behind a successful snapshot made a full disk unrecoverable. With no room to
    /// write, the tick must still delete what has expired — otherwise nothing ever changes and the
    /// machine warns every 15 minutes forever.
    /// </summary>
    [Fact]
    public void RunTickCore_PrunesExpiredSnapshotsEvenWithNoRoomToWriteANewOne()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        string expired = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 6, 20, 3));
        string yesterday = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 7, 26, 3));

        string? previous = Environment.GetEnvironmentVariable(DatabaseBackupWorker.MinFreeBytesEnvVar);
        try
        {
            // A floor no volume can satisfy: the headroom check fails and the snapshot is skipped.
            Environment.SetEnvironmentVariable(
                DatabaseBackupWorker.MinFreeBytesEnvVar, long.MaxValue.ToString(CultureInfo.InvariantCulture));

            NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 14));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseBackupWorker.MinFreeBytesEnvVar, previous);
        }

        Assert.False(File.Exists(expired));      // pruned anyway — the point of the fix
        Assert.True(File.Exists(yesterday));     // still inside retention
        Assert.Single(SnapshotsIn(backupDirectory)); // and no snapshot was written
    }

    [Fact]
    public void RunTickCore_PrunesAfterWritingSoTheNewSnapshotCountsTowardTheLimits()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        string expired = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 6, 1, 3));

        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 14));

        Assert.False(File.Exists(expired));
        string[] snapshots = SnapshotsIn(backupDirectory);
        Assert.Single(snapshots);
        Assert.Equal("core-state-20260727-140000.db", Path.GetFileName(snapshots[0]));
    }

    /// <summary>Never leave the folder empty, even when everything in it has expired.</summary>
    [Fact]
    public void RunTickCore_KeepsTheNewestSnapshotWhenEverythingHasExpiredAndNothingIsDue()
    {
        string workingDirectory = NewTempDirectory();
        string backupDirectory = NewTempDirectory();
        string dbPath = CreateTestDatabase(workingDirectory);

        WriteFakeSnapshot(backupDirectory, LocalAt(2026, 1, 10, 3));
        string newest = WriteFakeSnapshot(backupDirectory, LocalAt(2026, 1, 20, 3));

        // Before the backup hour, so the tick prunes but cannot replace what it removes.
        NewWorker().RunTickCore(dbPath, backupDirectory, LocalAt(2026, 7, 27, 1));

        Assert.True(File.Exists(newest));
        Assert.Single(SnapshotsIn(backupDirectory));
    }
}
