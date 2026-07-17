using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

internal sealed record DailyDigestState(
    int SchemaVersion,
    string? LastSentDate);   // "yyyy-MM-dd" (UTC day), or null if never sent

/// <summary>
/// B1 — persists the day the daily digest was last sent, so the "already sent today" guard
/// survives a service restart.
///
/// Root cause this fixes: <see cref="DailyDigestReporter"/> held that date in a plain in-memory
/// field. Any restart after 19:30 UTC (auto-update, crash, reboot) reset it to null, the guard
/// passed, and the agent POSTed the digest again — a second email. It is also why a machine that
/// was off at 19:30 mails the digest the moment it boots.
///
/// The server-side dedup (wasDigestSentForAccount on /api/agent/daily-digest) is the authority and
/// covers every sender; this store simply stops the agent making the pointless duplicate request.
/// Mirrors ControlPlaneReporterStateStore: schema-versioned JSON in ProgramData, atomic replace,
/// and every failure path fails toward "not sent" — never toward silently skipping a real digest.
/// </summary>
internal sealed class DailyDigestStateStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "daily-digest-state.v1.json";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _path;

    public DailyDigestStateStore()
        : this(pathOverride: null)
    {
    }

    internal DailyDigestStateStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    /// <summary>The last UTC day a digest was sent, or null if unknown/never. Never throws.</summary>
    public DateOnly? LoadLastSentDate()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            DailyDigestState? state = JsonSerializer.Deserialize<DailyDigestState>(File.ReadAllText(_path));
            if (state is null || state.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(state.LastSentDate))
            {
                return null;
            }

            return DateOnly.TryParseExact(
                state.LastSentDate.Trim(),
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly parsed)
                ? parsed
                : null;
        }
        catch
        {
            // Fail toward "not sent": at worst the server dedup suppresses a duplicate email.
            return null;
        }
    }

    /// <summary>Records the day a digest was sent. Best-effort — a failed write must never break the agent.</summary>
    public void SaveLastSentDate(DateOnly date)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);

            var state = new DailyDigestState(
                SchemaVersion: CurrentSchemaVersion,
                LastSentDate: date.ToString(DateFormat, CultureInfo.InvariantCulture));

            string tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string ResolvePath(string? pathOverride)
    {
        if (!string.IsNullOrWhiteSpace(pathOverride))
        {
            return Path.GetFullPath(pathOverride);
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", FileName);
    }
}
