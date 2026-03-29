using System.IO;
using System.Text.Json;
using Porta.Agent.Core.Outputs;

namespace Porta.UserAgent.Interventions;

internal sealed class UserAgentNotificationSnapshotStore : IUserAgentNotificationSnapshotStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _path;

    public UserAgentNotificationSnapshotStore()
        : this(pathOverride: null)
    {
    }

    public UserAgentNotificationSnapshotStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    public void Save(NotificationControllerSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Notification snapshot path must have a parent directory.");
        Directory.CreateDirectory(directory);

        string tempPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(new
        {
            schema_ver = CurrentSchemaVersion,
            snapshot
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    public NotificationControllerSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int schemaVer = root.TryGetProperty("schema_ver", out JsonElement schemaNode) && schemaNode.TryGetInt32(out int parsedSchema)
                ? parsedSchema
                : 0;
            if (schemaVer != CurrentSchemaVersion
                || !root.TryGetProperty("snapshot", out JsonElement snapshotNode))
            {
                return null;
            }

            NotificationControllerSnapshot? snapshot = snapshotNode.Deserialize<NotificationControllerSnapshot>();
            if (!snapshot.HasValue || !snapshot.Value.IsValid)
            {
                return null;
            }

            return snapshot.Value;
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

    private static string ResolvePath(string? pathOverride)
    {
        string? explicitPath = string.IsNullOrWhiteSpace(pathOverride)
            ? Environment.GetEnvironmentVariable("PORTA_USER_AGENT_NOTIFICATION_SNAPSHOT_PATH")
            : pathOverride;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "notification-snapshot.v1.json");
    }
}
