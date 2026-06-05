using System.Text;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;

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
    string? BinaryVersion = null,
    string? StartupSource = null,
    string? LaunchReason = null,
    long? HeartbeatSequence = null,
    int? HeartbeatWriteFailureCount = null,
    bool? DesktopWidgetReady = null,
    string? CaptureSelfTestState = null,
    string? CaptureSelfTestErrorCode = null,
    int? CaptureSelfTestRemediationCount = null,
    string? CaptureFailureCode = null);

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
                BinaryVersion = NormalizeIdentifier(snapshot.BinaryVersion),
                StartupSource = NormalizeIdentifier(snapshot.StartupSource),
                LaunchReason = NormalizeIdentifier(snapshot.LaunchReason),
                HeartbeatSequence = NormalizeHeartbeatSequence(snapshot.HeartbeatSequence),
                HeartbeatWriteFailureCount = NormalizeFailureCount(snapshot.HeartbeatWriteFailureCount),
                DesktopWidgetReady = snapshot.DesktopWidgetReady,
                CaptureSelfTestState = NormalizeSelfTestState(snapshot.CaptureSelfTestState),
                CaptureSelfTestErrorCode = NormalizeIdentifier(snapshot.CaptureSelfTestErrorCode),
                CaptureSelfTestRemediationCount = NormalizeFailureCount(snapshot.CaptureSelfTestRemediationCount),
                CaptureFailureCode = NormalizeIdentifier(snapshot.CaptureFailureCode)
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

        string tempPath = BuildUniqueTempPath();
        UserAgentSessionContextSnapshot normalized = snapshot with
        {
            SchemaVersion = CurrentSchemaVersion,
            CaptureContext = NormalizeCaptureContext(snapshot.CaptureContext),
            UpdatedAtUtc = snapshot.UpdatedAtUtc.ToUniversalTime(),
            AgentInstanceId = NormalizeIdentifier(snapshot.AgentInstanceId),
            BinaryVersion = NormalizeIdentifier(snapshot.BinaryVersion),
            StartupSource = NormalizeIdentifier(snapshot.StartupSource),
            LaunchReason = NormalizeIdentifier(snapshot.LaunchReason),
            HeartbeatSequence = NormalizeHeartbeatSequence(snapshot.HeartbeatSequence),
            HeartbeatWriteFailureCount = NormalizeFailureCount(snapshot.HeartbeatWriteFailureCount),
            DesktopWidgetReady = snapshot.DesktopWidgetReady,
            CaptureSelfTestState = NormalizeSelfTestState(snapshot.CaptureSelfTestState),
            CaptureSelfTestErrorCode = NormalizeIdentifier(snapshot.CaptureSelfTestErrorCode),
            CaptureSelfTestRemediationCount = NormalizeFailureCount(snapshot.CaptureSelfTestRemediationCount),
            CaptureFailureCode = NormalizeIdentifier(snapshot.CaptureFailureCode)
        };

        string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
            }

            PromoteTempFileWithRetry(tempPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
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

    private static long? NormalizeHeartbeatSequence(long? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value;
    }

    private string BuildUniqueTempPath()
    {
        string fileName = Path.GetFileName(_path);
        return Path.Combine(
            Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Session context path must have a parent directory."),
            $"{fileName}.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}.tmp");
    }

    private void PromoteTempFileWithRetry(string tempPath)
    {
        const int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    try
                    {
                        File.Move(tempPath, _path);
                    }
                    catch (IOException) when (File.Exists(_path))
                    {
                        File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                }

                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
