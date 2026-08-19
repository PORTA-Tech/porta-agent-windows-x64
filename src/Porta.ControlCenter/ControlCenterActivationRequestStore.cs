namespace Porta.ControlCenter;

using System.IO;
using System.Text.Json;

internal enum ControlCenterActivationTarget
{
    Default,
    Now,
    History,
    Actions,
    Tracking,
    Settings,
    Health,
    Account,
    // Phase 4.1 (2026-08-18) — the widget menu opens these three by name. They are main pages
    // since phase 3.1; before this the widget had no way to ask for any of them.
    Tasks,
    Projects,
    Reports
}

internal static class ControlCenterActivationRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly TimeSpan ExpirationWindow = TimeSpan.FromMinutes(2);

    public static ControlCenterActivationTarget ParseArgs(string[] args)
    {
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--section=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ParseSection(arg["--section=".Length..]);
        }

        return ControlCenterActivationTarget.Default;
    }

    public static void Save(ControlCenterActivationTarget target)
    {
        if (target == ControlCenterActivationTarget.Default)
        {
            Clear();
            return;
        }

        try
        {
            string path = BuildPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PersistedActivationEnvelope envelope = new(
                Section: target.ToString().ToLowerInvariant(),
                CreatedAtUtc: DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(envelope, JsonOptions));
        }
        catch
        {
        }
    }

    public static ControlCenterActivationTarget Consume()
    {
        string path = BuildPath();
        if (!File.Exists(path))
        {
            return ControlCenterActivationTarget.Default;
        }

        try
        {
            PersistedActivationEnvelope? envelope = JsonSerializer.Deserialize<PersistedActivationEnvelope>(
                File.ReadAllText(path),
                JsonOptions);
            Clear();

            if (envelope == null)
            {
                return ControlCenterActivationTarget.Default;
            }

            if ((DateTimeOffset.UtcNow - envelope.CreatedAtUtc) > ExpirationWindow)
            {
                return ControlCenterActivationTarget.Default;
            }

            return ParseSection(envelope.Section);
        }
        catch
        {
            Clear();
            return ControlCenterActivationTarget.Default;
        }
    }

    public static void Clear()
    {
        try
        {
            string path = BuildPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static ControlCenterActivationTarget ParseSection(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "now" or "today" or "overview" => ControlCenterActivationTarget.Now,
            "history" or "timeline" => ControlCenterActivationTarget.History,
            "actions" or "interventions" => ControlCenterActivationTarget.Actions,
            "tracking" or "inputs" => ControlCenterActivationTarget.Tracking,
            "tasks" => ControlCenterActivationTarget.Tasks,
            "projects" => ControlCenterActivationTarget.Projects,
            "reports" => ControlCenterActivationTarget.Reports,
            "controls" or "settings" => ControlCenterActivationTarget.Settings,
            "health" or "system" or "diagnostics" => ControlCenterActivationTarget.Health,
            "account" or "signin" or "sign-in" => ControlCenterActivationTarget.Account,
            _ => ControlCenterActivationTarget.Default
        };
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "control-center.activation-request.v1.json");
    }

    private sealed record PersistedActivationEnvelope(string Section, DateTimeOffset CreatedAtUtc);
}
