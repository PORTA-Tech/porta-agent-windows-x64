using Porta.Agent.Core;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiStatusAcceptanceTests
{
    [Fact]
    public void Status_ReturnsOperationalFlags_AndAuthFailureFields()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.22d, confidence: 0.31d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.77d,
                Confidence: 0.83d,
                SustainedAttentionDurationSec: 1450d,
                IdleFragmentationIndex: 0.22d,
                FocusAppEntropy: 0.15d,
                OverloadActive: true,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 1.0d,
                AvgQuality2m: 0.88d,
                ColdStart: false,
                CurrentQualityScore: 0.91d,
                PartialData: false,
                MissingMajorChannels: []),
            NotificationStatus = new AgentNotificationSuppressionStatus(
                Active: true,
                Ready: true,
                ActionState: "active",
                LastError: null,
                BackoffUntilUtc: null,
                EnableCooldownUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                MinOnUntilUtc: DateTimeOffset.UtcNow.AddMinutes(2),
                RestorePending: false),
            BrightnessStatus = new AgentBrightnessActionStatus(
                Supported: true,
                ActionReady: true,
                Active: true,
                RampDirection: "down",
                PreviousLevel: 60,
                TargetLevel: 55,
                CurrentLevel: 58,
                LastError: null,
                BackoffUntilUtc: null,
                CooldownUntilUtc: DateTimeOffset.UtcNow.AddMinutes(10)),
            RecentEvents = [
                new AgentControlEvent(
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Action: "deep_work_notifications_suppressed",
                    TriggerState: "deep_work",
                    TriggerValue: 1,
                    Confidence: 0.8d,
                    DurationSeconds: null,
                    EscalationLevel: "Nudge",
                    Reversible: true,
                    Reason: "test",
                    Contributors: [],
                    WhyJson: "{\"schema_ver\":1,\"reason\":\"Deep work detected\"}")
            ]
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 9876,
                    SessionId: 1,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    AgentInstanceId: "agent-1",
                    StartupSource: "run-key",
                    LaunchReason: "stale_heartbeat",
                    HeartbeatWriteFailureCount: 3,
                    CaptureSelfTestState: "passed",
                    CaptureSelfTestErrorCode: null,
                    CaptureSelfTestRemediationCount: 2)));
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 120,
            MouseMoveDx: 12d,
            MouseMoveDy: -5d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.4d,
            LastInputEventUtc: lastInputUtc));
        controller.SetLocalApiAuthState(false, "TOKEN_READ_FAILED");

        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: false, localApiAuthError: "TOKEN_READ_FAILED");

        Assert.True(dto.overload_active);
        Assert.True(dto.deep_work_active);
        Assert.True(dto.notifications_suppressed);
        Assert.True(dto.brightness_adjusted);
        Assert.True(dto.brightness_supported);
        Assert.False(dto.LocalApiAuthReady);
        Assert.Equal("TOKEN_READ_FAILED", dto.LocalApiAuthError);
        Assert.False(dto.auth.ready);
        Assert.Equal("TOKEN_READ_FAILED", dto.auth.error);
        Assert.False(string.IsNullOrWhiteSpace(dto.status_generated_at_utc));
        Assert.Equal(dto.last_updated_utc, dto.status_generated_at_utc);
        Assert.Equal("fresh", dto.freshness_state);
        Assert.Equal("token_read_failed", dto.degraded_reason);
        Assert.False(string.IsNullOrWhiteSpace(dto.freshness.generated_at_utc));
        Assert.Null(dto.freshness.captured_at_utc);
        Assert.Equal(dto.status_generated_at_utc, dto.freshness.generated_at_utc);
        Assert.Equal(dto.freshness_state, dto.freshness.freshness_state);
        Assert.Equal(dto.degraded_reason, dto.freshness.degraded_reason);
        Assert.False(dto.db_migration_failed);
        Assert.False(string.IsNullOrWhiteSpace(dto.BackendInstanceId));
        Assert.True(dto.BackendProcessId > 0);
        Assert.Equal("UserSession", dto.capture_context);
        Assert.NotNull(dto.UserAgentHeartbeatUpdatedAtUtc);
        Assert.NotNull(dto.user_agent_heartbeat_utc);
        Assert.NotNull(dto.UserAgentObservedHeartbeatUpdatedAtUtc);
        Assert.NotNull(dto.user_agent_observed_heartbeat_utc);
        Assert.Equal("agent-1", dto.UserAgentInstanceId);
        Assert.Equal(9876, dto.UserAgentProcessId);
        Assert.Equal("session-1", dto.UserAgentSessionId);
        Assert.Equal("UserSession", dto.UserAgentObservedCaptureContext);
        Assert.Equal("UserSession", dto.user_agent_observed_capture_context);
        Assert.Equal("session-1", dto.UserAgentObservedSessionId);
        Assert.Equal("session-1", dto.user_agent_observed_session_id);
        Assert.Equal("run-key", dto.UserAgentStartupSource);
        Assert.Equal("run-key", dto.user_agent_startup_source);
        Assert.Equal("stale_heartbeat", dto.UserAgentLaunchReason);
        Assert.Equal("stale_heartbeat", dto.user_agent_launch_reason);
        Assert.Equal(3, dto.UserAgentHeartbeatWriteFailureCount);
        Assert.Equal(3, dto.user_agent_heartbeat_write_failure_count);
        Assert.Equal("passed", dto.CaptureSelfTestState);
        Assert.Equal("passed", dto.capture_self_test_state);
        Assert.Null(dto.CaptureSelfTestErrorCode);
        Assert.Null(dto.capture_self_test_error_code);
        Assert.Equal(2, dto.CaptureSelfTestRemediationCount);
        Assert.Equal(2, dto.capture_self_test_remediation_count);
        Assert.True(dto.keyboard_signal_active);
        Assert.True(dto.mouse_signal_active);
        Assert.NotNull(dto.LastInputEventUtc);
        Assert.NotNull(dto.last_input_event_utc);
        Assert.NotNull(dto.last_accepted_input_age_seconds);
        Assert.InRange(dto.last_accepted_input_age_seconds!.Value, 0d, 5d);
        Assert.Equal("agent-1", dto.LastInputAgentInstanceId);
        Assert.Equal("session-1", dto.LastInputSessionId);
        Assert.Null(dto.OwnershipWarning);
        Assert.Equal(dto.BackendInstanceId, dto.backend_instance_id);
        Assert.Equal(dto.LastInputAgentInstanceId, dto.last_input_agent_instance_id);
        Assert.Null(dto.CaptureFailureReason);
        Assert.Null(dto.capture_failure_reason);
    }

    [Fact]
    public void Status_AndSnapshot_UseCachedRuntimeState_AndMarkStale_WhenBackgroundRefreshFails()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.34d, confidence: 0.78d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.81d,
                Confidence: 0.78d,
                SustainedAttentionDurationSec: 1800d,
                IdleFragmentationIndex: 0.14d,
                FocusAppEntropy: 0.06d,
                OverloadActive: true,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true}",
                ContributorsJson: "{\"schema_ver\":1,\"top\":[\"focus_load\"]}",
                BaselineReady: true,
                BaselineReadyPct: 1d,
                AvgQuality2m: 0.92d,
                ColdStart: false,
                CurrentQualityScore: 0.95d,
                PartialData: false,
                MissingMajorChannels: []),
            NotificationStatus = new AgentNotificationSuppressionStatus(
                Active: true,
                Ready: true,
                ActionState: "active",
                LastError: null,
                BackoffUntilUtc: nowUtc.AddMinutes(2),
                EnableCooldownUntilUtc: nowUtc.AddMinutes(5),
                MinOnUntilUtc: nowUtc.AddMinutes(1),
                RestorePending: false),
            BrightnessStatus = new AgentBrightnessActionStatus(
                Supported: true,
                ActionReady: true,
                Active: true,
                RampDirection: "down",
                PreviousLevel: 68,
                TargetLevel: 55,
                CurrentLevel: 60,
                LastError: null,
                BackoffUntilUtc: null,
                CooldownUntilUtc: nowUtc.AddMinutes(10)),
            DbMigrationFailed = true
        };

        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));
        store.ThrowOnReadLatestStateDetails = true;
        store.ReadLatestStateDetailsFailureMessage = "database is locked";
        store.ThrowOnReadNotificationSuppressionStatus = true;
        store.ReadNotificationSuppressionStatusFailureMessage = "database is locked";
        store.ThrowOnReadBrightnessActionStatus = true;
        store.ReadBrightnessActionStatusFailureMessage = "database is locked";
        store.ThrowOnReadDbMigrationFailed = true;
        store.ReadDbMigrationFailedFailureMessage = "database is locked";
        controller.RefreshRuntimeStateCache();

        AgentServiceStatusSnapshot status = controller.GetStatus();
        AgentLatestStateDetails? latestState = controller.GetLatestStateDetails();
        var statusDto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);
        var snapshotDto = SnapshotMapper.Map(controller.GetCurrentSnapshot(), latestState, status);

        Assert.NotNull(latestState);
        Assert.True(statusDto.overload_active);
        Assert.True(statusDto.deep_work_active);
        Assert.True(statusDto.notifications_suppressed);
        Assert.True(statusDto.brightness_adjusted);
        Assert.True(statusDto.db_migration_failed);
        Assert.False(string.IsNullOrWhiteSpace(statusDto.status_generated_at_utc));
        Assert.NotNull(statusDto.last_successful_db_refresh_utc);
        Assert.Equal("stale", statusDto.runtime_state_freshness);
        Assert.Equal("stale", statusDto.freshness_state);
        Assert.Equal("db_busy", statusDto.runtime_state_refresh_error);
        Assert.Equal("db_migration_failed", statusDto.degraded_reason);
        Assert.Equal(statusDto.status_generated_at_utc, statusDto.freshness.generated_at_utc);
        Assert.Equal(statusDto.last_successful_db_refresh_utc, statusDto.freshness.last_successful_db_refresh_utc);
        Assert.Equal(statusDto.freshness_state, statusDto.freshness.freshness_state);
        Assert.Equal(statusDto.degraded_reason, statusDto.freshness.degraded_reason);
        Assert.True(statusDto.degraded.runtime_state_stale);
        Assert.False(string.IsNullOrWhiteSpace(snapshotDto.snapshot_captured_at_utc));
        Assert.Equal(snapshotDto.timestamp_utc, snapshotDto.snapshot_captured_at_utc);
        Assert.NotNull(snapshotDto.last_successful_db_refresh_utc);
        Assert.Equal("stale", snapshotDto.runtime_state_freshness);
        Assert.Equal("stale", snapshotDto.freshness_state);
        Assert.Equal("db_busy", snapshotDto.runtime_state_refresh_error);
        Assert.Equal("db_busy", snapshotDto.degraded_reason);
        Assert.Equal(snapshotDto.snapshot_captured_at_utc, snapshotDto.freshness.captured_at_utc);
        Assert.Equal(snapshotDto.last_successful_db_refresh_utc, snapshotDto.freshness.last_successful_db_refresh_utc);
        Assert.Equal(snapshotDto.freshness_state, snapshotDto.freshness.freshness_state);
        Assert.Equal(snapshotDto.degraded_reason, snapshotDto.freshness.degraded_reason);
        Assert.Equal(0.81d, snapshotDto.OverloadLikelihood);
        Assert.Equal(0.78d, snapshotDto.Confidence);
        Assert.True(snapshotDto.OverloadActive);
    }

    [Fact]
    public void RefreshRuntimeStateCache_RecoversFreshness_WhenStoreReadsResume()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.21d, confidence: 0.55d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc.AddMinutes(-5),
                OverloadLikelihood: 0.21d,
                Confidence: 0.55d,
                SustainedAttentionDurationSec: 300d,
                IdleFragmentationIndex: 0.2d,
                FocusAppEntropy: 0.1d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.4d,
                AvgQuality2m: 0.5d,
                ColdStart: true,
                CurrentQualityScore: 0.52d,
                PartialData: true,
                MissingMajorChannels: ["keyboard"]),
            NotificationStatus = new AgentNotificationSuppressionStatus(
                Active: false,
                Ready: true,
                ActionState: "inactive",
                LastError: null,
                BackoffUntilUtc: null,
                EnableCooldownUntilUtc: null,
                MinOnUntilUtc: null,
                RestorePending: false),
            BrightnessStatus = new AgentBrightnessActionStatus(
                Supported: true,
                ActionReady: true,
                Active: false,
                RampDirection: "idle",
                PreviousLevel: -1,
                TargetLevel: -1,
                CurrentLevel: -1,
                LastError: null,
                BackoffUntilUtc: null,
                CooldownUntilUtc: null),
            DbMigrationFailed = false
        };

        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));
        controller.RefreshRuntimeStateCache();
        store.ThrowOnReadLatestStateDetails = true;
        store.ReadLatestStateDetailsFailureMessage = "database is locked";
        store.ThrowOnReadNotificationSuppressionStatus = true;
        store.ReadNotificationSuppressionStatusFailureMessage = "database is locked";
        store.ThrowOnReadBrightnessActionStatus = true;
        store.ReadBrightnessActionStatusFailureMessage = "database is locked";
        store.ThrowOnReadDbMigrationFailed = true;
        store.ReadDbMigrationFailedFailureMessage = "database is locked";
        controller.RefreshRuntimeStateCache();

        store.ThrowOnReadLatestStateDetails = false;
        store.ThrowOnReadNotificationSuppressionStatus = false;
        store.ThrowOnReadBrightnessActionStatus = false;
        store.ThrowOnReadDbMigrationFailed = false;
        store.LatestStateDetails = new AgentLatestStateDetails(
            TimestampUtc: nowUtc,
            OverloadLikelihood: 0.88d,
            Confidence: 0.84d,
            SustainedAttentionDurationSec: 2400d,
            IdleFragmentationIndex: 0.07d,
            FocusAppEntropy: 0.03d,
            OverloadActive: true,
            StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true}",
            ContributorsJson: "{\"schema_ver\":1,\"top\":[\"overload\"]}",
            BaselineReady: true,
            BaselineReadyPct: 1d,
            AvgQuality2m: 0.93d,
            ColdStart: false,
            CurrentQualityScore: 0.94d,
            PartialData: false,
            MissingMajorChannels: []);
        store.NotificationStatus = new AgentNotificationSuppressionStatus(
            Active: true,
            Ready: true,
            ActionState: "active",
            LastError: null,
            BackoffUntilUtc: nowUtc.AddMinutes(3),
            EnableCooldownUntilUtc: nowUtc.AddMinutes(6),
            MinOnUntilUtc: nowUtc.AddMinutes(2),
            RestorePending: false);
        store.BrightnessStatus = new AgentBrightnessActionStatus(
            Supported: true,
            ActionReady: true,
            Active: true,
            RampDirection: "down",
            PreviousLevel: 70,
            TargetLevel: 58,
            CurrentLevel: 60,
            LastError: null,
            BackoffUntilUtc: null,
            CooldownUntilUtc: nowUtc.AddMinutes(12));
        store.DbMigrationFailed = true;
        AgentBrainSnapshot refreshedSnapshot = CreateSnapshot(overload: 0.88d, confidence: 0.84d) with
        {
            TimestampUtc = nowUtc,
            LastAction = "focus_protection_tightened",
            StateFlagsJson = "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true}"
        };
        controller.ApplyBrainSnapshot(refreshedSnapshot, backendConnected: true);
        controller.RefreshRuntimeStateCache();

        AgentServiceStatusSnapshot status = controller.GetStatus();
        AgentLatestStateDetails? latestState = controller.GetLatestStateDetails();
        var statusDto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);
        var snapshotDto = SnapshotMapper.Map(controller.GetCurrentSnapshot(), latestState, status);

        Assert.NotNull(latestState);
        Assert.Equal("focus_protection_tightened", status.LastAction);
        Assert.True(statusDto.overload_active);
        Assert.True(statusDto.deep_work_active);
        Assert.True(statusDto.notifications_suppressed);
        Assert.True(statusDto.brightness_adjusted);
        Assert.True(statusDto.db_migration_failed);
        Assert.Equal("fresh", statusDto.runtime_state_freshness);
        Assert.Null(statusDto.runtime_state_refresh_error);
        Assert.False(statusDto.degraded.runtime_state_stale);
        Assert.Equal("fresh", snapshotDto.runtime_state_freshness);
        Assert.Null(snapshotDto.runtime_state_refresh_error);
        Assert.Equal(0.88d, snapshotDto.OverloadLikelihood);
        Assert.Equal(0.84d, snapshotDto.Confidence);
        Assert.True(snapshotDto.OverloadActive);
    }

    [Fact]
    public void Status_ReturnsDbMigrationFailedFlag_FromDiagnosticsSource()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.11d, confidence: 0.51d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.11d,
                Confidence: 0.51d,
                SustainedAttentionDurationSec: 12d,
                IdleFragmentationIndex: 0.01d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.02d,
                AvgQuality2m: 0.2d,
                ColdStart: true,
                CurrentQualityScore: 0.3d,
                PartialData: true,
                MissingMajorChannels: ["keyboard"]),
            DbMigrationFailed = true
        };

        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));
        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.True(dto.db_migration_failed);
        Assert.True(dto.degraded.degraded);
        Assert.True(dto.degraded.db_migration_failed);
        Assert.Equal("Unknown", dto.capture_context);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.LastInputEventUtc);
        Assert.Equal("CAPTURE_CONTEXT_UNKNOWN", dto.CaptureFailureReason);
        Assert.Equal("CAPTURE_CONTEXT_UNKNOWN", dto.capture_failure_reason);
    }

    [Fact]
    public void Status_UsesFreshUserAgentSessionHeartbeat_WhenNoInputHasArrived()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.09d, confidence: 0.41d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.09d,
                Confidence: 0.41d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0d,
                AvgQuality2m: 0d,
                ColdStart: true,
                CurrentQualityScore: 0d,
                PartialData: true,
                MissingMajorChannels: ["keyboard", "mouse"]),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.ServiceSession,
                ProcessId: 1234,
                SessionId: 0,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        var controller = new AgentCoreController(store, sessionContextStore);
        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("ServiceSession", dto.capture_context);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.LastInputEventUtc);
        Assert.Equal("CAPTURE_CONTEXT_SERVICE_SESSION", dto.CaptureFailureReason);
        Assert.Equal("CAPTURE_CONTEXT_SERVICE_SESSION", dto.capture_failure_reason);
    }

    [Fact]
    public void Status_UsesFreshUserSessionHeartbeat_ButKeepsSignalsInactive_UntilPacketsArrive()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.10d, confidence: 0.39d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.10d,
                Confidence: 0.39d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.05d,
                AvgQuality2m: 0.18d,
                ColdStart: true,
                CurrentQualityScore: 0.24d,
                PartialData: true,
                MissingMajorChannels: ["keyboard", "mouse"]),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 5555,
                SessionId: 4,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "useragent-4"));

        var controller = new AgentCoreController(store, sessionContextStore);
        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.NotNull(dto.UserAgentHeartbeatUpdatedAtUtc);
        Assert.NotNull(dto.user_agent_heartbeat_utc);
        Assert.Equal("useragent-4", dto.UserAgentInstanceId);
        Assert.Equal("session-4", dto.UserAgentSessionId);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.LastInputEventUtc);
        Assert.Null(dto.CaptureFailureReason);
        Assert.Null(dto.OwnershipWarning);
    }

    [Fact]
    public void Status_DoesNotTreatProlongedTypingOnlyActivityAsCaptureFailure()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.10d, confidence: 0.39d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.10d,
                Confidence: 0.39d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.05d,
                AvgQuality2m: 0.18d,
                ColdStart: true,
                CurrentQualityScore: 0.24d,
                PartialData: false,
                MissingMajorChannels: ["mouse"]),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 5555,
                SessionId: 4,
                UpdatedAtUtc: nowUtc,
                AgentInstanceId: "useragent-4"));

        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset lastInputUtc = nowUtc;
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "useragent-4",
            SessionId: "session-4",
            WindowStartUtc: lastInputUtc.AddSeconds(-90),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 12,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.True(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.CaptureFailureReason);
        Assert.Null(dto.capture_failure_reason);
    }

    [Fact]
    public void Status_DoesNotTreatProlongedMouseOnlyActivityAsCaptureFailure()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.10d, confidence: 0.39d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.10d,
                Confidence: 0.39d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.05d,
                AvgQuality2m: 0.18d,
                ColdStart: true,
                CurrentQualityScore: 0.24d,
                PartialData: false,
                MissingMajorChannels: ["keyboard"]),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 5555,
                SessionId: 4,
                UpdatedAtUtc: nowUtc,
                AgentInstanceId: "useragent-4"));

        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset lastInputUtc = nowUtc;
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "useragent-4",
            SessionId: "session-4",
            WindowStartUtc: lastInputUtc.AddSeconds(-90),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 120,
            MouseMoveDx: 18d,
            MouseMoveDy: -6d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.False(dto.keyboard_signal_active);
        Assert.True(dto.mouse_signal_active);
        Assert.Null(dto.CaptureFailureReason);
        Assert.Null(dto.capture_failure_reason);
    }

    [Fact]
    public void Status_ReportsSelfTestFailure_WhenHeartbeatIsFresh()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.12d, confidence: 0.44d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.12d,
                Confidence: 0.44d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.03d,
                AvgQuality2m: 0.10d,
                ColdStart: true,
                CurrentQualityScore: 0.16d,
                PartialData: true,
                MissingMajorChannels: ["keyboard", "mouse"]),
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 7002,
                    SessionId: 4,
                    UpdatedAtUtc: nowUtc,
                    AgentInstanceId: "agent-4",
                    CaptureSelfTestState: "failed",
                    CaptureSelfTestErrorCode: "no_live_input_after_user_activity",
                    CaptureSelfTestRemediationCount: 2)));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.Equal("failed", dto.capture_self_test_state);
        Assert.Equal("USER_AGENT_INPUT_SELF_TEST_FAILED", dto.CaptureFailureReason);
        Assert.Equal("USER_AGENT_INPUT_SELF_TEST_FAILED", dto.capture_failure_reason);
    }

    [Fact]
    public void Status_UsesObservedUserHeartbeat_ToReportExplicitStaleHeartbeatFailure()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.08d, confidence: 0.21d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.08d,
                Confidence: 0.21d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.02d,
                AvgQuality2m: 0.08d,
                ColdStart: true,
                CurrentQualityScore: 0.11d,
                PartialData: true,
                MissingMajorChannels: ["keyboard", "mouse"]),
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 7001,
                    SessionId: 4,
                    UpdatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-45),
                    AgentInstanceId: "agent-4")));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("Unknown", dto.capture_context);
        Assert.Equal("USER_AGENT_HEARTBEAT_STALE", dto.CaptureFailureReason);
        Assert.Equal("USER_AGENT_HEARTBEAT_STALE", dto.capture_failure_reason);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
    }

    [Fact]
    public void Status_ReportsSessionSwitched_WhenFreshHeartbeatOwnerDiffersFromLastAcceptedInput()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.11d, confidence: 0.42d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.11d,
                Confidence: 0.42d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.1d,
                AvgQuality2m: 0.22d,
                ColdStart: true,
                CurrentQualityScore: 0.27d,
                PartialData: true,
                MissingMajorChannels: Array.Empty<string>()),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 5001,
                SessionId: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1),
                AgentInstanceId: "agent-1"));
        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 3,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc));

        sessionContextStore.WriteCurrent(new UserAgentSessionContextSnapshot(
            SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
            CaptureContext: UserAgentCaptureContexts.UserSession,
            ProcessId: 5002,
            SessionId: 9,
            UpdatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            AgentInstanceId: "agent-1"));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.Equal("USER_AGENT_SESSION_MISMATCH", dto.OwnershipWarning);
        Assert.Equal("USER_AGENT_SESSION_SWITCHED", dto.CaptureFailureReason);
        Assert.Equal("session-1", dto.LastInputSessionId);
        Assert.Equal("session-9", dto.UserAgentSessionId);
    }

    [Fact]
    public void Status_ClearsStaleOwnership_AfterReplacementHeartbeatStaysHealthyLongEnough()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.13d, confidence: 0.61d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.13d,
                Confidence: 0.61d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 1d,
                AvgQuality2m: 0.91d,
                ColdStart: false,
                CurrentQualityScore: 0.93d,
                PartialData: false,
                MissingMajorChannels: Array.Empty<string>()),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 5101,
                SessionId: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1),
                AgentInstanceId: "agent-1"));
        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc));

        sessionContextStore.WriteCurrent(new UserAgentSessionContextSnapshot(
            SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
            CaptureContext: UserAgentCaptureContexts.UserSession,
            ProcessId: 5102,
            SessionId: 9,
            UpdatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-25),
            AgentInstanceId: "agent-9"));

        var dto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("UserSession", dto.capture_context);
        Assert.Null(dto.LastInputEventUtc);
        Assert.Null(dto.LastInputAgentInstanceId);
        Assert.Null(dto.LastInputSessionId);
        Assert.Null(dto.OwnershipWarning);
        Assert.Null(dto.CaptureFailureReason);
        Assert.Null(dto.capture_failure_reason);
    }

    [Fact]
    public void Status_RejectsPackets_WhenHeartbeatReportsServiceSession()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.18d, confidence: 0.57d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.18d,
                Confidence: 0.57d,
                SustainedAttentionDurationSec: 60d,
                IdleFragmentationIndex: 0.12d,
                FocusAppEntropy: 0.04d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 0.92d,
                AvgQuality2m: 0.84d,
                ColdStart: false,
                CurrentQualityScore: 0.89d,
                PartialData: false,
                MissingMajorChannels: []),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.ServiceSession,
                ProcessId: 4321,
                SessionId: 0,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 6,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 120,
            MouseMoveDx: 10d,
            MouseMoveDy: 4d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc)));

        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("input_signals_source_invalid_context", ex.Message);
        Assert.Equal("ServiceSession", dto.capture_context);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.LastInputEventUtc);
        Assert.Equal("CAPTURE_CONTEXT_SERVICE_SESSION", dto.CaptureFailureReason);
    }

    [Fact]
    public void Status_DoesNotExposeOwnershipMismatch_WhenRejectedPacketDiffersFromHeartbeatOwner()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.12d, confidence: 0.61d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.12d,
                Confidence: 0.61d,
                SustainedAttentionDurationSec: 120d,
                IdleFragmentationIndex: 0.09d,
                FocusAppEntropy: 0.03d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 0.94d,
                AvgQuality2m: 0.87d,
                ColdStart: false,
                CurrentQualityScore: 0.9d,
                PartialData: false,
                MissingMajorChannels: []),
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 6543,
                    SessionId: 3,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    AgentInstanceId: "heartbeat-agent")));
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "packet-agent",
            SessionId: "session-3",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 3,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 120,
            MouseMoveDx: 8d,
            MouseMoveDy: 2d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: lastInputUtc)));

        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("input_signals_source_agent_mismatch", ex.Message);
        Assert.Equal("heartbeat-agent", dto.UserAgentInstanceId);
        Assert.Null(dto.LastInputAgentInstanceId);
        Assert.Equal("session-3", dto.UserAgentSessionId);
        Assert.Null(dto.LastInputSessionId);
        Assert.Null(dto.OwnershipWarning);
        Assert.Null(dto.ownership_warning);
        Assert.False(dto.keyboard_signal_active);
        Assert.False(dto.mouse_signal_active);
        Assert.Null(dto.LastInputEventUtc);
    }

    [Fact]
    public void Status_ReportsActivePolicyVersion_FromRuntimePolicyProvider()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.14d, confidence: 0.72d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.14d,
                Confidence: 0.72d,
                SustainedAttentionDurationSec: 240d,
                IdleFragmentationIndex: 0.08d,
                FocusAppEntropy: 0.02d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 0.94d,
                AvgQuality2m: 0.88d,
                ColdStart: false,
                CurrentQualityScore: 0.91d,
                PartialData: false,
                MissingMajorChannels: [])
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(null),
            new FakeRuntimePolicyProvider("policy-2026-03-10"));

        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal("policy-2026-03-10", status.PolicyVersion);
        Assert.Equal("policy-2026-03-10", dto.PolicyVersion);
    }

    [Fact]
    public void Status_LastInputEventUtc_AdvancesAcrossAcceptedTypingPackets()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.12d, confidence: 0.64d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.12d,
                Confidence: 0.64d,
                SustainedAttentionDurationSec: 90d,
                IdleFragmentationIndex: 0.04d,
                FocusAppEntropy: 0.02d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 0.92d,
                AvgQuality2m: 0.86d,
                ColdStart: false,
                CurrentQualityScore: 0.88d,
                PartialData: false,
                MissingMajorChannels: [])
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 6001,
                SessionId: 6,
                UpdatedAtUtc: nowUtc,
                AgentInstanceId: "agent-6"));

        var controller = new AgentCoreController(store, sessionContextStore);
        DateTimeOffset firstInputUtc = nowUtc.AddSeconds(-8);
        DateTimeOffset secondInputUtc = nowUtc.AddSeconds(-2);

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-6",
            SessionId: "session-6",
            WindowStartUtc: firstInputUtc.AddSeconds(-5),
            WindowEndUtc: firstInputUtc,
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.1d,
            LastInputEventUtc: firstInputUtc));

        var dtoAfterFirstPacket = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-6",
            SessionId: "session-6",
            WindowStartUtc: secondInputUtc.AddSeconds(-5),
            WindowEndUtc: secondInputUtc,
            KeyboardKeydownCount: 6,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.05d,
            LastInputEventUtc: secondInputUtc));

        var dtoAfterSecondPacket = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);

        Assert.Equal(firstInputUtc.ToUniversalTime(), dtoAfterFirstPacket.LastInputEventUtc);
        Assert.Equal(secondInputUtc.ToUniversalTime(), dtoAfterSecondPacket.LastInputEventUtc);
        Assert.NotEqual(dtoAfterFirstPacket.last_input_event_utc, dtoAfterSecondPacket.last_input_event_utc);
        Assert.True(dtoAfterSecondPacket.LastInputEventUtc > dtoAfterFirstPacket.LastInputEventUtc);
    }

    private static AgentBrainSnapshot CreateSnapshot(double overload, double confidence)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.1d,
            ScrollVelocityIndex: 0.1d,
            FocusSwitchRate: 0.1d,
            SmoothedTypingBurstIntensity: 0.1d,
            SmoothedScrollVelocityIndex: 0.1d,
            SmoothedFocusSwitchRate: 0.1d,
            FocusStrength: 0.5d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.2d,
            CognitiveStability: 0.8d,
            DominantStateValue: 0.5d,
            DominantStateName: "focus",
            Confidence: confidence,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: []);
    }

    private sealed class FakeUserAgentSessionContextStore : IUserAgentSessionContextStore
    {
        private UserAgentSessionContextSnapshot? _snapshot;

        public FakeUserAgentSessionContextStore(UserAgentSessionContextSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public UserAgentSessionContextSnapshot? ReadCurrent()
        {
            return _snapshot;
        }

        public void WriteCurrent(UserAgentSessionContextSnapshot snapshot)
        {
            _snapshot = snapshot;
        }
    }

    private sealed class FakeRuntimePolicyProvider : IRuntimePolicyProvider
    {
        private readonly RuntimePolicyEnvelope _envelope;

        public FakeRuntimePolicyProvider(string version)
        {
            _envelope = new RuntimePolicyEnvelope(
                version,
                new RuntimePolicySettings(
                    new RuntimeOverloadStabilizationSettings(
                        PortaExecutionConstants.OverloadStabilization.ThresholdOn,
                        PortaExecutionConstants.OverloadStabilization.ThresholdOff,
                        PortaExecutionConstants.OverloadStabilization.DwellOnSeconds,
                        PortaExecutionConstants.OverloadStabilization.DwellOffSeconds,
                        PortaExecutionConstants.OverloadStabilization.ConfidenceMin,
                        PortaExecutionConstants.OverloadStabilization.GapAllowSeconds,
                        PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold,
                        PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs),
                    new RuntimeBreakPolicySettings(
                        PortaExecutionConstants.BreakPolicy.MicrobreakWindowSeconds,
                        PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsLowOverload,
                        PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsHighOverload,
                        PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds,
                        PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowOverload,
                        PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighOverload,
                        PortaExecutionConstants.BreakPolicy.CooldownSeconds,
                        PortaExecutionConstants.BreakPolicy.ConfidenceMin,
                        PortaExecutionConstants.BreakPolicy.HighOverloadLikelihood,
                        PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMinSeconds,
                        PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMaxSeconds,
                        PortaExecutionConstants.BreakPolicy.DeliveryBoundaryTypingQuietMax,
                        PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax),
                    new RuntimeDeepWorkDetectionSettings(
                        PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds,
                        PortaExecutionConstants.DeepWorkDetection.ConfidenceMin,
                        PortaExecutionConstants.DeepWorkDetection.FragmentationMax,
                        PortaExecutionConstants.DeepWorkDetection.VolatilityMax,
                        PortaExecutionConstants.DeepWorkDetection.IntensityMin,
                        PortaExecutionConstants.DeepWorkDetection.IntensityMax,
                        PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds,
                        PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds,
                        PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds),
                    new RuntimeNotificationSuppressionSettings(
                        PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds,
                        PortaExecutionConstants.NotificationSuppression.EnableCooldownSeconds,
                        PortaExecutionConstants.NotificationSuppression.MinOnSeconds),
                    new RuntimeBrightnessAdjustmentSettings(
                        PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
                        PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness,
                        PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket,
                        PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds,
                        PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds)));
        }

        public RuntimePolicyEnvelope GetEnvelope() => _envelope;
    }
}
