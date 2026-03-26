using Porta.Agent.Common.UserAgent;

namespace Porta.UserAgent.Tests;

public sealed class UserAgentSessionContextStoreTests
{
    [Fact]
    public void WriteCurrent_AndReadCurrent_RoundTripSnapshot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"porta-user-agent-context-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UserAgentSessionContextStore(path);
            var expected = new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 321,
                SessionId: 7,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "useragent-321",
                StartupSource: "control-center-recovery",
                LaunchReason: "stale_heartbeat",
                HeartbeatWriteFailureCount: 2,
                CaptureSelfTestState: "failed",
                CaptureSelfTestErrorCode: "no_live_input_after_user_activity",
                CaptureSelfTestRemediationCount: 2);

            store.WriteCurrent(expected);
            UserAgentSessionContextSnapshot? actual = store.ReadCurrent();

            Assert.NotNull(actual);
            Assert.Equal(expected.ProcessId, actual!.ProcessId);
            Assert.Equal(expected.SessionId, actual.SessionId);
            Assert.Equal(expected.AgentInstanceId, actual.AgentInstanceId);
            Assert.Equal(expected.StartupSource, actual.StartupSource);
            Assert.Equal(expected.LaunchReason, actual.LaunchReason);
            Assert.Equal(expected.HeartbeatWriteFailureCount, actual.HeartbeatWriteFailureCount);
            Assert.Equal(expected.CaptureSelfTestState, actual.CaptureSelfTestState);
            Assert.Equal(expected.CaptureSelfTestErrorCode, actual.CaptureSelfTestErrorCode);
            Assert.Equal(expected.CaptureSelfTestRemediationCount, actual.CaptureSelfTestRemediationCount);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void WriteCurrent_ReplacesExistingFile_WhileSharedReaderIsOpen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"porta-user-agent-context-open-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UserAgentSessionContextStore(path);
            store.WriteCurrent(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 111,
                SessionId: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "useragent-111"));

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                store.WriteCurrent(new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 222,
                    SessionId: 1,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    AgentInstanceId: "useragent-222"));
            }

            UserAgentSessionContextSnapshot? actual = store.ReadCurrent();
            Assert.NotNull(actual);
            Assert.Equal(222, actual!.ProcessId);
            Assert.Equal("useragent-222", actual.AgentInstanceId);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
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
