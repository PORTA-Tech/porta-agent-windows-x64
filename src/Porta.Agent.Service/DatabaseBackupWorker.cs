using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Service;

/// <summary>
/// Nightly local snapshot of core-state.db.
///
/// Why this exists: the database held every bucket, episode and distraction a user has ever
/// produced, with no copy anywhere. Losing it does not merely lose history — it destroys the
/// Welford baseline, dropping the user back to "Provisional" until it recalibrates. Corruption,
/// a bad migration, or an accidental delete was unrecoverable.
///
/// Contracts:
///   1. VACUUM INTO, never File.Copy. The DB is WAL-mode and written continuously; a plain copy
///      can capture a torn mid-write state. VACUUM INTO writes a consistent, compacted copy of a
///      live database without pausing the agent.
///   2. Write to .tmp, then rename. An interrupted run must never leave a half-written file that
///      looks like a valid backup.
///   3. Retention can never empty the folder — the newest snapshot survives both the age limit
///      and the size ceiling. A cleanup rule that can delete everything is worse than none.
///   4. The whole tick is wrapped in try/catch. A backup failure must never affect capture, the
///      pipeline, or service start. Same rule as AgentUpdateWorker.
///
/// SCOPE: local only. Nothing here touches the network. A copy on the same disk protects against
/// corruption, bad migrations and accidental deletion — it does NOT survive disk failure, loss of
/// the machine, or an attacker with administrator rights, who can take ownership of any local
/// folder. Off-machine backup is a separate job and needs a consent change first.
/// </summary>
internal sealed class DatabaseBackupWorker : BackgroundService
{
    /// <summary>Wall-clock hour (local) the daily snapshot is taken. Deliberately not 23:30 — that is DailyDigestReporter.</summary>
    internal const int BackupHourLocal = 3;

    internal const int RetentionDays = 7;

    /// <summary>
    /// Safety net for disk use, NOT the primary control — RetentionDays is.
    ///
    /// 8 GB → 5 GB, 2026-08-12. The 8 was derived from a database measured at ~640 MB on
    /// 2026-07-27; by 2026-08-12 it was 923 MB and the backups 855 MB each, so the number was
    /// stale before anyone read it. Worse, 8 GB was LARGER THAN THE FREE SPACE ON THE DRIVE it was
    /// protecting — the ceiling could never engage before the disk filled, which is not a ceiling.
    ///
    /// 5 GB, because once the primitive_buckets prune settles the database near 600 MB, seven
    /// backups is ~4.2 GB. That keeps this above normal operation — it must NOT bind day to day or
    /// it silently prunes below the retention the user asked for — while being low enough to
    /// actually fire on a machine that lives near its limit.
    ///
    /// If this ever needs revisiting, measure a real backup first. An earlier 500 MB value was
    /// guessed rather than measured, and would have capped the folder at a single snapshot.
    /// </summary>
    internal const long MaxFolderBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// How many database-sized copies of headroom must REMAIN after a snapshot is written, when
    /// nothing is configured. 1 → 2 (2026-08-12), so the volume keeps room for two more.
    ///
    /// A FLAT FLOOR WAS PROPOSED HERE AND REJECTED, for the second time. `b11f345` (2026-07-28)
    /// removed exactly such a constant — a flat 2 GB — and its reasoning still holds:
    ///
    ///   "An invented floor fails in the worst direction — it silently stops backing up a machine
    ///    that has ample room."
    ///
    /// A 60 MB database on a laptop with 400 MB free must still be backed up; a fixed 3 GB refuses
    /// that machine forever and says nothing. So the requirement stays DERIVED from the one
    /// quantity here that is measured, and only the multiple moves.
    ///
    /// What this buys on the machine that prompted it: a 923 MB database now needs 2.77 GB free
    /// rather than 1.85 GB — close to the fixed 3 GB that was asked for, without breaking the
    /// small case. C: reached 36 MB free twice in the week before this changed.
    ///
    /// Known and accepted (Yadin, 2026-08-12): a stricter rule stops backups SOONER, which is the
    /// opposite of what you want on a tight disk. It is still the right trade — a skipped backup
    /// is recoverable, a full disk corrupting the live database is not — but it is a trade, not a
    /// fix. The fix is not needing ~900 MB free per backup, which the primitive_buckets prune
    /// mostly buys. Next lever after that is RetentionDays 7 → 5.
    /// </summary>
    internal const int DefaultFreeSpaceMultiple = 2;

    /// <summary>
    /// Override for the free-space rule, in bytes. Set it on a machine that needs an explicit
    /// floor; leave it unset and the requirement is derived from the database being backed up.
    /// See <see cref="HasHeadroom(long, long, long?)"/>.
    /// </summary>
    internal const string MinFreeBytesEnvVar = "PORTA_BACKUP_MIN_FREE_BYTES";

    private const string FilePrefix = "core-state-";
    private const string FileExtension = ".db";
    private const string TempExtension = ".tmp";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// A laptop asleep at 03:00 must still get a backup. Ticking every 15 minutes means it is taken
    /// at the first tick after waking, rather than being skipped for the day.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    private readonly ILogger<DatabaseBackupWorker> _logger;

    public DatabaseBackupWorker(ILogger<DatabaseBackupWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No snapshot at boot: startup is the busiest moment for the DB, and a machine that is
        // restarted repeatedly would otherwise snapshot on every start.
        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            RunTick();
        }
    }

    private void RunTick()
    {
        try
        {
            string dbPath = CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: false);
            if (!File.Exists(dbPath))
            {
                return; // nothing to back up yet
            }

            string backupDirectory = GetBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            ProtectBackupFolder(backupDirectory);

            RunTickCore(dbPath, backupDirectory, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            // Never rethrow: a backup bug must not be able to take down the service.
            _logger.LogWarning(ex, "Database backup tick failed.");
        }
    }

    /// <summary>
    /// The body of one tick, against an explicit database, folder and clock. Split out of
    /// <see cref="RunTick"/> so it can be tested without ProgramData or the live service database.
    /// </summary>
    internal void RunTickCore(string dbPath, string backupDirectory, DateTimeOffset nowLocal)
    {
        CleanUpAbandonedTempFiles(backupDirectory);

        // Retention runs on EVERY tick, ahead of every early return below.
        //
        // It used to run only after a snapshot had been written successfully, which made a full
        // disk unrecoverable on its own: the headroom check would skip the backup, skipping the
        // backup would skip the pruning, and pruning was the only thing that could have freed the
        // space the backup needed. The machine would warn every 15 minutes, forever, and never
        // take another snapshot. Deleting expired copies must not depend on being able to write
        // a new one.
        ApplyRetention(backupDirectory, nowLocal.ToUniversalTime());

        IReadOnlyList<BackupCandidate> existing = ReadCandidates(backupDirectory);
        DateTimeOffset? newestLocal = existing.Count == 0
            ? null
            : existing.Max(candidate => candidate.TimestampUtc).ToLocalTime();

        if (!IsDue(nowLocal, newestLocal, BackupHourLocal))
        {
            return;
        }

        if (!HasEnoughRoomFor(dbPath, backupDirectory))
        {
            return;
        }

        string createdPath = CreateSnapshot(dbPath, backupDirectory, nowLocal);
        _logger.LogInformation("Database backup written: {Path}", createdPath);

        // Again, now that a snapshot has been added: the ceiling is enforced against the folder as
        // it stands, not as it stood before the write.
        ApplyRetention(backupDirectory, nowLocal.ToUniversalTime());
    }

    // ---------------------------------------------------------------- scheduling

    /// <summary>
    /// Due when the local clock has passed <paramref name="backupHourLocal"/> and no snapshot has
    /// been taken on today's local date. Exposed for tests.
    /// </summary>
    internal static bool IsDue(DateTimeOffset nowLocal, DateTimeOffset? newestBackupLocal, int backupHourLocal)
    {
        if (nowLocal.Hour < backupHourLocal)
        {
            return false;
        }

        if (newestBackupLocal is null)
        {
            return true;
        }

        return newestBackupLocal.Value.Date < nowLocal.Date;
    }

    // ---------------------------------------------------------------- snapshot

    /// <summary>Writes one snapshot and returns its path. Exposed for tests.</summary>
    internal static string CreateSnapshot(string dbPath, string backupDirectory, DateTimeOffset nowLocal)
    {
        string stamp = nowLocal.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string finalPath = Path.Combine(backupDirectory, $"{FilePrefix}{stamp}{FileExtension}");
        string tempPath = finalPath + TempExtension;

        // VACUUM INTO refuses to overwrite, so a leftover target must go first.
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // Parameterised: the path is interpolated by SQLite, not string-concatenated into SQL.
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", tempPath);
            command.ExecuteNonQuery();
        }

        // Rename last: until this line there is no file that looks like a finished backup.
        File.Move(tempPath, finalPath, overwrite: true);
        return finalPath;
    }

    /// <summary>
    /// Whether there is room to write a snapshot of <paramref name="dbBytes"/> and still have
    /// somewhere to put the next one. Exposed for tests.
    ///
    /// Deliberately not a fixed constant. The value this replaces was a flat 2 GB floor that had
    /// been invented rather than measured, and an invented floor fails in the worst direction: it
    /// silently stops backing up a machine that has ample room, leaving nothing but a warning in a
    /// log nobody reads.
    ///
    /// So the requirement is derived from the one quantity here that IS measured — the size of the
    /// database about to be copied. After this snapshot is written the volume must still hold
    /// another of the same size, which means tonight's backup is never the thing that fills the
    /// disk and tomorrow's still has somewhere to go. A machine with a 640 MB database needs
    /// 1.3 GB free; a machine with a 60 MB database needs 120 MB, instead of being told it needs
    /// 2 GB it was never going to use.
    ///
    /// <paramref name="configuredMinFreeBytes"/> (from PORTA_BACKUP_MIN_FREE_BYTES) replaces the
    /// derived requirement for anyone who needs an explicit floor.
    /// </summary>
    internal static bool HasHeadroom(long availableFreeBytes, long dbBytes, long? configuredMinFreeBytes)
    {
        // Still derived from the database, never invented — see DefaultFreeSpaceMultiple. Only the
        // multiple changed: room for two more copies rather than one.
        long requiredAfterWriting = configuredMinFreeBytes ?? (dbBytes * DefaultFreeSpaceMultiple);
        return availableFreeBytes - dbBytes >= requiredAfterWriting;
    }

    /// <summary>Reads PORTA_BACKUP_MIN_FREE_BYTES. Junk or absent means "derive it". Exposed for tests.</summary>
    internal static long? ReadConfiguredMinFreeBytes()
    {
        string? raw = Environment.GetEnvironmentVariable(MinFreeBytesEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private bool HasEnoughRoomFor(string dbPath, string backupDirectory)
    {
        long availableBytes;
        long dbBytes;
        try
        {
            string? root = Path.GetPathRoot(backupDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return true; // cannot tell — do not block the backup on a probe failure
            }

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            dbBytes = new FileInfo(dbPath).Length;
        }
        catch
        {
            return true;
        }

        if (HasHeadroom(availableBytes, dbBytes, ReadConfiguredMinFreeBytes()))
        {
            return true;
        }

        // Log the numbers, not just the verdict. "Not enough free disk space" on its own is the
        // kind of line that gets read for months without anyone learning what it wanted.
        _logger.LogWarning(
            "Backup skipped: {AvailableMb} MB free, need room for a {DbMb} MB snapshot plus {RequiredMb} MB after it.",
            availableBytes / (1024 * 1024),
            dbBytes / (1024 * 1024),
            (ReadConfiguredMinFreeBytes() ?? dbBytes) / (1024 * 1024));
        return false;
    }

    // ---------------------------------------------------------------- retention

    private void ApplyRetention(string backupDirectory, DateTimeOffset nowUtc)
    {
        IReadOnlyList<BackupCandidate> candidates = ReadCandidates(backupDirectory);
        foreach (BackupCandidate doomed in SelectForDeletion(candidates, nowUtc, RetentionDays, MaxFolderBytes))
        {
            try
            {
                File.Delete(doomed.Path);
                _logger.LogInformation("Pruned old backup: {Path}", doomed.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete old backup {Path}.", doomed.Path);
            }
        }
    }

    /// <summary>
    /// Age limit first, then the size ceiling oldest-first. The newest snapshot is never selected,
    /// whatever the limits say — losing every copy is worse than exceeding a quota. Exposed for tests.
    /// </summary>
    internal static IReadOnlyList<BackupCandidate> SelectForDeletion(
        IReadOnlyList<BackupCandidate> candidates,
        DateTimeOffset nowUtc,
        int retentionDays,
        long maxFolderBytes)
    {
        if (candidates.Count <= 1)
        {
            return Array.Empty<BackupCandidate>();
        }

        // Newest first: index 0 is the protected one.
        List<BackupCandidate> ordered = candidates.OrderByDescending(c => c.TimestampUtc).ToList();
        var doomed = new List<BackupCandidate>();
        var survivors = new List<BackupCandidate> { ordered[0] };

        foreach (BackupCandidate candidate in ordered.Skip(1))
        {
            if ((nowUtc - candidate.TimestampUtc).TotalDays > retentionDays)
            {
                doomed.Add(candidate);
            }
            else
            {
                survivors.Add(candidate);
            }
        }

        long totalBytes = survivors.Sum(c => c.SizeBytes);
        while (totalBytes > maxFolderBytes && survivors.Count > 1)
        {
            BackupCandidate oldest = survivors[^1];
            survivors.RemoveAt(survivors.Count - 1);
            doomed.Add(oldest);
            totalBytes -= oldest.SizeBytes;
        }

        return doomed;
    }

    /// <summary>A finished snapshot on disk. Exposed for tests.</summary>
    internal sealed record BackupCandidate(string Path, DateTimeOffset TimestampUtc, long SizeBytes);

    private static IReadOnlyList<BackupCandidate> ReadCandidates(string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory))
        {
            return Array.Empty<BackupCandidate>();
        }

        var results = new List<BackupCandidate>();
        foreach (string path in Directory.EnumerateFiles(backupDirectory, $"{FilePrefix}*{FileExtension}"))
        {
            // .tmp files are unfinished and must never be treated as backups, nor counted or pruned
            // as if they were. EnumerateFiles' pattern already excludes them (they end .db.tmp), but
            // be explicit — this is the invariant that keeps a torn file from being restored.
            if (path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTimeOffset? stamp = ParseTimestamp(Path.GetFileName(path));
            if (stamp is null)
            {
                continue; // not ours — leave it alone
            }

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                continue;
            }

            results.Add(new BackupCandidate(path, stamp.Value, size));
        }

        return results;
    }

    /// <summary>Reads the timestamp out of core-state-yyyyMMdd-HHmmss.db. Exposed for tests.</summary>
    internal static DateTimeOffset? ParseTimestamp(string fileName)
    {
        if (!fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string stamp = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileExtension.Length);
        if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return null;
        }

        // Written from local time, so read back as local.
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local)).ToUniversalTime();
    }

    private void CleanUpAbandonedTempFiles(string backupDirectory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(backupDirectory, $"*{TempExtension}"))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear abandoned backup temp files.");
        }
    }

    // ---------------------------------------------------------------- location + permissions

    private static string GetBackupDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PortaAgent",
        "backups");

    /// <summary>
    /// SharedDataAclHealer grants BUILTIN\Users MODIFY on PortaAgent with full inheritance, so a
    /// folder created afterwards inherits it and any standard user could delete every backup.
    /// Give this folder the same treatment the security\ subfolder gets: cut inheritance, pin Users
    /// to read-only. SYSTEM/Administrators keep full control; the service is the only writer.
    ///
    /// This does NOT stop an administrator — nothing on Windows can. It stops the ordinary user
    /// account and anything running as it.
    /// </summary>
    private void ProtectBackupFolder(string backupDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(backupDirectory);
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            bool alreadyProtected = security.AreAccessRulesProtected;
            bool changed = false;
            if (!alreadyProtected)
            {
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
                changed = true;
            }

            var toRemove = new List<FileSystemAccessRule>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference == usersSid
                    && rule.AccessControlType == AccessControlType.Allow
                    && (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                {
                    toRemove.Add(rule);
                }
            }

            foreach (FileSystemAccessRule rule in toRemove)
            {
                security.RemoveAccessRule(rule);
                changed = true;
            }

            if (toRemove.Count > 0 || !alreadyProtected)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    usersSid,
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                changed = true;
            }

            if (changed)
            {
                directory.SetAccessControl(security);
                _logger.LogInformation("Backup folder ACL pinned: Users read-only, inheritance cut.");
            }
        }
        catch (Exception ex)
        {
            // Best-effort, exactly like SharedDataAclHealer: a permission failure must not stop
            // backups from being taken.
            _logger.LogWarning(ex, "Could not pin backup folder permissions.");
        }
    }
}
