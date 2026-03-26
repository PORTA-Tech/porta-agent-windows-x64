using System.Text;
using System.Text.Json;

namespace Porta.Agent.Common.UserAgent;

public static class UserAgentCaptureContexts
{
    public const string Unknown = "Unknown";
    public const string UserSession = "UserSession";
    public const string ServiceSession = "ServiceSession";
}

public sealed record UserAgentSessionContextSnapshot(
    int SchemaVersion,
    string CaptureContext,
    int ProcessId,
    int SessionId,
    DateTimeOffset UpdatedAtUtc,
    string? AgentInstanceId = null,
    string? StartupSource = null,
    string? LaunchReason = null,
    int? HeartbeatWriteFailureCount = null,
    string? CaptureSelfTestState = null,
    string? CaptureSelfTestErrorCode = null,
    int? CaptureSelfTestRemediationCount = null);

public interface IUserAgentSessionContextStore
{
    UserAgentSessionContextSnapshot? ReadCurrent();

    void WriteCurrent(UserAgentSessionContextSnapshot snapshot);
}

public sealed class UserAgentSessionContextStore : IUserAgentSessionContextStore
{
    public const int CurrentSchemaVersion = 1;
    private const string FileName = "user-agent-session-context.v1.json";
    private readonly string _path;
    public string ContextPath => _path;

    public UserAgentSessionContextStore()
        : this(pathOverride: null)
    {
    }

    public UserAgentSessionContextStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    public UserAgentSessionContextSnapshot? ReadCurrent()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            UserAgentSessionContextSnapshot? snapshot = JsonSerializer.Deserialize<UserAgentSessionContextSnapshot>(json);
            if (snapshot == null || snapshot.SchemaVersion != CurrentSchemaVersion)
            {
                return null;
            }

            return snapshot with
            {
                CaptureContext = NormalizeCaptureContext(snapshot.CaptureContext),
                UpdatedAtUtc = snapshot.UpdatedAtUtc.ToUniversalTime(),
                AgentInstanceId = NormalizeIdentifier(snapshot.AgentInstanceId),
                StartupSource = NormalizeIdentifier(snapshot.StartupSource),
                LaunchReason = NormalizeIdentifier(snapshot.LaunchReason),
                HeartbeatWriteFailureCount = NormalizeFailureCount(snapshot.HeartbeatWriteFailureCount),
                CaptureSelfTestState = NormalizeSelfTestState(snapshot.CaptureSelfTestState),
                CaptureSelfTestErrorCode = NormalizeIdentifier(snapshot.CaptureSelfTestErrorCode),
                CaptureSelfTestRemediationCount = NormalizeFailureCount(snapshot.CaptureSelfTestRemediationCount)
            };
        }
        catch
        {
            return null;
        }
    }

    public void WriteCurrent(UserAgentSessionContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Session context path must have a parent directory.");
        Directory.CreateDirectory(directory);

        string tempPath = _path + ".tmp";
        UserAgentSessionContextSnapshot normalized = snapshot with
        {
            SchemaVersion = CurrentSchemaVersion,
            CaptureContext = NormalizeCaptureContext(snapshot.CaptureContext),
            UpdatedAtUtc = snapshot.UpdatedAtUtc.ToUniversalTime(),
            AgentInstanceId = NormalizeIdentifier(snapshot.AgentInstanceId),
            StartupSource = NormalizeIdentifier(snapshot.StartupSource),
            LaunchReason = NormalizeIdentifier(snapshot.LaunchReason),
            HeartbeatWriteFailureCount = NormalizeFailureCount(snapshot.HeartbeatWriteFailureCount),
            CaptureSelfTestState = NormalizeSelfTestState(snapshot.CaptureSelfTestState),
            CaptureSelfTestErrorCode = NormalizeIdentifier(snapshot.CaptureSelfTestErrorCode),
            CaptureSelfTestRemediationCount = NormalizeFailureCount(snapshot.CaptureSelfTestRemediationCount)
        };

        string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static string ResolvePath(string? pathOverride)
    {
        string? explicitPath = string.IsNullOrWhiteSpace(pathOverride)
            ? System.Environment.GetEnvironmentVariable("PORTA_USER_AGENT_CONTEXT_PATH")
            : pathOverride;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", FileName);
    }

    private static string NormalizeCaptureContext(string? captureContext)
    {
        return captureContext?.Trim() switch
        {
            UserAgentCaptureContexts.UserSession => UserAgentCaptureContexts.UserSession,
            UserAgentCaptureContexts.ServiceSession => UserAgentCaptureContexts.ServiceSession,
            _ => UserAgentCaptureContexts.Unknown
        };
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string? NormalizeSelfTestState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static int? NormalizeFailureCount(int? value)
    {
        if (!value.HasValue || value.Value < 0)
        {
            return null;
        }

        return value.Value;
    }
}
