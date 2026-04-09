namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

internal sealed class UserBreakOverlayStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public UserBreakOverlayStateStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
    }

    public void SaveCountdown(UserBreakOverlayCountdownState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PersistedBreakOverlayState envelope = new(
                SchemaVer: 1,
                Phase: "countdown",
                InterventionId: state.InterventionId,
                TipKey: state.TipKey,
                BreakKind: state.BreakKind,
                Title: state.Title,
                Message: state.Message,
                AcceptedAtUtc: state.AcceptedAtUtc,
                EndsAtUtc: state.EndsAtUtc);
            File.WriteAllText(_path, JsonSerializer.Serialize(envelope, JsonOptions));
        }
        catch
        {
        }
    }

    public UserBreakOverlayCountdownState? LoadActiveCountdown(DateTimeOffset nowUtc)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            PersistedBreakOverlayState? envelope = JsonSerializer.Deserialize<PersistedBreakOverlayState>(
                File.ReadAllText(_path),
                JsonOptions);
            if (envelope == null
                || !string.Equals(envelope.Phase, "countdown", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(envelope.InterventionId)
                || string.IsNullOrWhiteSpace(envelope.TipKey)
                || string.IsNullOrWhiteSpace(envelope.BreakKind)
                || envelope.EndsAtUtc <= nowUtc)
            {
                Clear();
                return null;
            }

            return new UserBreakOverlayCountdownState(
                InterventionId: envelope.InterventionId.Trim(),
                TipKey: envelope.TipKey.Trim(),
                BreakKind: envelope.BreakKind.Trim(),
                Title: string.IsNullOrWhiteSpace(envelope.Title) ? "Break in progress" : envelope.Title.Trim(),
                Message: string.IsNullOrWhiteSpace(envelope.Message) ? "Ari is holding the screen for a recovery break." : envelope.Message.Trim(),
                AcceptedAtUtc: envelope.AcceptedAtUtc,
                EndsAtUtc: envelope.EndsAtUtc);
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
        }
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.break-overlay.v1.json");
    }

    private sealed record PersistedBreakOverlayState(
        int SchemaVer,
        string Phase,
        string InterventionId,
        string TipKey,
        string BreakKind,
        string Title,
        string Message,
        DateTimeOffset AcceptedAtUtc,
        DateTimeOffset EndsAtUtc);
}

internal sealed record UserBreakOverlayCountdownState(
    string InterventionId,
    string TipKey,
    string BreakKind,
    string Title,
    string Message,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset EndsAtUtc);
