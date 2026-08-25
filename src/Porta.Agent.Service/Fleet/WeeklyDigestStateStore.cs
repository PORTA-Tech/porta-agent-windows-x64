using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

internal sealed record WeeklyDigestState(
    int SchemaVersion,
    string? LastSentWeekEnd);   // "yyyy-MM-dd" — the Sunday of the last week sent, or null

/// <summary>
/// Persists the week the weekly digest was last sent, so the "already sent" guard survives a
/// restart. Mirrors <see cref="DailyDigestStateStore"/> exactly, including the reason it exists:
/// the daily held its date in a plain in-memory field, and any restart after the send hour reset
/// it, passed the guard, and POSTed a second email.
///
/// <b>Its own file, keyed on its own thing.</b> The daily records a UTC DAY; this records the
/// END of a goal-day week. Sharing a file would mean one cadence could clear the other's guard.
///
/// The server-side dedup on /api/agent/weekly-digest is the authority and covers every sender —
/// a second machine on the same account, or a restart here. This store only stops the agent
/// making the pointless duplicate request. Every failure path fails toward "not sent", never
/// toward silently skipping a real digest.
/// </summary>
internal sealed class WeeklyDigestStateStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "weekly-digest-state.v1.json";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _path;

    public WeeklyDigestStateStore()
        : this(pathOverride: null)
    {
    }

    internal WeeklyDigestStateStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    /// <summary>The end date of the last week sent, or null if unknown/never. Never throws.</summary>
    public DateOnly? LoadLastSentWeekEnd()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            WeeklyDigestState? state = JsonSerializer.Deserialize<WeeklyDigestState>(File.ReadAllText(_path));
            if (state is null
                || state.SchemaVersion != CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(state.LastSentWeekEnd))
            {
                return null;
            }

            return DateOnly.TryParseExact(
                state.LastSentWeekEnd.Trim(),
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

    /// <summary>Records the week that was sent. Best-effort — a failed write must never break the agent.</summary>
    public void SaveLastSentWeekEnd(DateOnly weekEnd)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);

            var state = new WeeklyDigestState(
                SchemaVersion: CurrentSchemaVersion,
                LastSentWeekEnd: weekEnd.ToString(DateFormat, CultureInfo.InvariantCulture));

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
