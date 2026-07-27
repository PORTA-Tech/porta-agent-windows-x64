using Porta.Agent.Service;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// Retention is the part of the backup worker that deletes things, so it is the part that gets
/// tested. The invariant that matters most: no combination of rules may ever remove the last
/// surviving snapshot.
/// </summary>
public sealed class DatabaseBackupWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
    private const long OneMb = 1024L * 1024;

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
}
