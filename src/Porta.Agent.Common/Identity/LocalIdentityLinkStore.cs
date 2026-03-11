using System.Text.Json;

namespace Porta.Agent.Common.Identity;

public static class LocalIdentityLinkStatuses
{
    public const string Linked = "linked";
    public const string Unlinked = "unlinked";
}

public sealed record LocalIdentityLinkSnapshot(
    int SchemaVersion,
    string DeviceId,
    string LinkStatus,
    string? AccountId,
    string? OrgId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LinkedAtUtc = null);

public interface ILocalIdentityLinkStore
{
    LocalIdentityLinkSnapshot GetCurrent();

    LocalIdentityLinkSnapshot SetLink(string accountId, string orgId);

    LocalIdentityLinkSnapshot ClearLink();
}

public sealed class LocalIdentityLinkStore : ILocalIdentityLinkStore
{
    public const int CurrentSchemaVersion = 1;
    private const string FileName = "identity-link.v1.json";
    private readonly object _gate = new();
    private readonly string _path;

    public LocalIdentityLinkStore()
        : this(pathOverride: null)
    {
    }

    public LocalIdentityLinkStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    public LocalIdentityLinkSnapshot GetCurrent()
    {
        lock (_gate)
        {
            LocalIdentityLinkSnapshot snapshot = LoadCurrentUnsafe();
            SaveCurrentUnsafe(snapshot);
            return snapshot;
        }
    }

    public LocalIdentityLinkSnapshot SetLink(string accountId, string orgId)
    {
        string normalizedAccountId = NormalizeRequiredGuid(accountId, nameof(accountId));
        string normalizedOrgId = NormalizeRequiredGuid(orgId, nameof(orgId));

        lock (_gate)
        {
            LocalIdentityLinkSnapshot current = LoadCurrentUnsafe();
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            bool sameLink =
                string.Equals(current.AccountId, normalizedAccountId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.OrgId, normalizedOrgId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal);

            LocalIdentityLinkSnapshot updated = current with
            {
                SchemaVersion = CurrentSchemaVersion,
                AccountId = normalizedAccountId,
                OrgId = normalizedOrgId,
                LinkStatus = LocalIdentityLinkStatuses.Linked,
                LinkedAtUtc = sameLink ? current.LinkedAtUtc ?? nowUtc : nowUtc,
                UpdatedAtUtc = nowUtc
            };

            SaveCurrentUnsafe(updated);
            return updated;
        }
    }

    public LocalIdentityLinkSnapshot ClearLink()
    {
        lock (_gate)
        {
            LocalIdentityLinkSnapshot current = LoadCurrentUnsafe();
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            LocalIdentityLinkSnapshot updated = current with
            {
                SchemaVersion = CurrentSchemaVersion,
                AccountId = null,
                OrgId = null,
                LinkStatus = LocalIdentityLinkStatuses.Unlinked,
                LinkedAtUtc = null,
                UpdatedAtUtc = nowUtc
            };

            SaveCurrentUnsafe(updated);
            return updated;
        }
    }

    private LocalIdentityLinkSnapshot LoadCurrentUnsafe()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return CreateDefault(DateTimeOffset.UtcNow);
            }

            string json = File.ReadAllText(_path);
            LocalIdentityLinkSnapshot? snapshot = JsonSerializer.Deserialize<LocalIdentityLinkSnapshot>(json);
            if (snapshot is null || snapshot.SchemaVersion != CurrentSchemaVersion)
            {
                return CreateDefault(DateTimeOffset.UtcNow);
            }

            string deviceId = NormalizeOptionalGuid(snapshot.DeviceId) ?? CreateDeviceId();
            string? accountId = NormalizeOptionalGuid(snapshot.AccountId);
            string? orgId = NormalizeOptionalGuid(snapshot.OrgId);
            bool linked = accountId is not null && orgId is not null
                && string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.OrdinalIgnoreCase);

            return new LocalIdentityLinkSnapshot(
                SchemaVersion: CurrentSchemaVersion,
                DeviceId: deviceId,
                LinkStatus: linked ? LocalIdentityLinkStatuses.Linked : LocalIdentityLinkStatuses.Unlinked,
                AccountId: linked ? accountId : null,
                OrgId: linked ? orgId : null,
                UpdatedAtUtc: snapshot.UpdatedAtUtc.ToUniversalTime(),
                LinkedAtUtc: linked ? snapshot.LinkedAtUtc?.ToUniversalTime() : null);
        }
        catch
        {
            return CreateDefault(DateTimeOffset.UtcNow);
        }
    }

    private void SaveCurrentUnsafe(LocalIdentityLinkSnapshot snapshot)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Identity link path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        string tempPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(snapshot with
        {
            SchemaVersion = CurrentSchemaVersion,
            DeviceId = NormalizeOptionalGuid(snapshot.DeviceId) ?? CreateDeviceId(),
            LinkStatus = NormalizeLinkStatus(snapshot.LinkStatus),
            AccountId = NormalizeOptionalGuid(snapshot.AccountId),
            OrgId = NormalizeOptionalGuid(snapshot.OrgId),
            UpdatedAtUtc = snapshot.UpdatedAtUtc.ToUniversalTime(),
            LinkedAtUtc = snapshot.LinkedAtUtc?.ToUniversalTime()
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static LocalIdentityLinkSnapshot CreateDefault(DateTimeOffset nowUtc) =>
        new(
            SchemaVersion: CurrentSchemaVersion,
            DeviceId: CreateDeviceId(),
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: nowUtc.ToUniversalTime(),
            LinkedAtUtc: null);

    private static string ResolvePath(string? pathOverride)
    {
        string? explicitPath = string.IsNullOrWhiteSpace(pathOverride)
            ? System.Environment.GetEnvironmentVariable("PORTA_IDENTITY_LINK_PATH")
            : pathOverride;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", FileName);
    }

    private static string CreateDeviceId() => Guid.NewGuid().ToString("D");

    private static string NormalizeRequiredGuid(string? value, string paramName)
    {
        string? normalized = NormalizeOptionalGuid(value);
        if (normalized is null)
        {
            throw new ArgumentException("invalid_guid", paramName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value.Trim(), out Guid guid)
            ? guid.ToString("D")
            : null;
    }

    private static string NormalizeLinkStatus(string? value) =>
        string.Equals(value?.Trim(), LocalIdentityLinkStatuses.Linked, StringComparison.OrdinalIgnoreCase)
            ? LocalIdentityLinkStatuses.Linked
            : LocalIdentityLinkStatuses.Unlinked;
}
