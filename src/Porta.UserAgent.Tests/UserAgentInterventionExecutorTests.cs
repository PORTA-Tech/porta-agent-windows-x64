using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserAgentInterventionExecutorTests
{
    [Fact]
    public void ExecuteOrReplay_BreakSuggestionToast_AcksApplied()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"PORTA","message":"Take a short break."}""");

        LocalApiInterventionAckRequest ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.Equal("applied", ack.Result);
        Assert.Null(ack.ErrorCode);
        Assert.Equal(1, presenter.ShowCalls);
    }

    [Fact]
    public void ExecuteOrReplay_ToastFailure_AcksFailed()
    {
        var presenter = new FakePresenter { ShowResult = false, ErrorCode = "toast_unavailable" };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"PORTA","message":"Take a short break."}""");

        LocalApiInterventionAckRequest ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.Equal("failed", ack.Result);
        Assert.Equal("toast_unavailable", ack.ErrorCode);
        Assert.Equal(1, presenter.ShowCalls);
    }

    [Fact]
    public void ExecuteOrReplay_DeepWorkStart_SuppressesNotifications()
    {
        var presenter = new FakePresenter();
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-on",
            "deep_work_notifications_suppressed",
            "suppress_notifications",
            "{}");

        LocalApiInterventionAckRequest ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.Equal("applied", ack.Result);
        Assert.Equal(1, notificationController.GetSnapshotCalls);
        Assert.Equal(1, notificationController.ApplyCalls);
        Assert.True(snapshotStore.SavedSnapshot.HasValue);
    }

    [Fact]
    public void ExecuteOrReplay_DeepWorkExit_RestoresNotifications()
    {
        var presenter = new FakePresenter();
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore
        {
            StoredSnapshot = ValidSnapshot()
        };
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-off",
            "deep_work_notifications_restored",
            "restore_notifications",
            "{}");

        LocalApiInterventionAckRequest ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.Equal("applied", ack.Result);
        Assert.Equal(1, notificationController.RestoreCalls);
        Assert.True(snapshotStore.Cleared);
    }

    [Fact]
    public void ExecuteOrReplay_DuplicateIntervention_ReplaysCachedAckWithoutSecondExecution()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"PORTA","message":"Take a short break."}""");

        LocalApiInterventionAckRequest first = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");
        LocalApiInterventionAckRequest second = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.Equal("applied", first.Result);
        Assert.Equal(first.AppliedAtUtc, second.AppliedAtUtc);
        Assert.Equal(1, presenter.ShowCalls);
    }

    private static UserAgentInterventionExecutor CreateExecutor(
        FakePresenter presenter,
        FakePresentationGate gate,
        FakeNotificationController notificationController,
        FakeNotificationSnapshotStore snapshotStore)
    {
        return new UserAgentInterventionExecutor(
            presenter,
            gate,
            notificationController,
            snapshotStore);
    }

    private static LocalApiInterventionPendingResponse BuildPending(
        string interventionId,
        string action,
        string commandType,
        string payloadJson)
    {
        return new LocalApiInterventionPendingResponse(
            Id: 1,
            InterventionId: interventionId,
            SessionId: "session-1",
            Action: action,
            CommandType: commandType,
            PayloadJson: payloadJson,
            DecidedAtUtc: DateTimeOffset.Parse("2026-03-08T12:00:00Z"),
            ExecutionState: "decided",
            PolicyId: "policy-v1",
            Domain: "test",
            Confidence: 0.9,
            EscalationLevel: "Suggest",
            WhatChanged: string.Empty);
    }

    private static NotificationControllerSnapshot ValidSnapshot()
    {
        return new NotificationControllerSnapshot(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-03-08T12:00:00Z"),
            IsValid: true,
            Provider: "test",
            FocusAssistMode: "unknown",
            QuietHoursEnabled: false,
            ToastBannersEnabled: true,
            RawStateJson: "{}");
    }

    private sealed class FakePresenter : IUserInterventionPresenter
    {
        public bool ShowResult { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public int ShowCalls { get; private set; }

        public bool TryShow(string title, string message, out string errorCode)
        {
            _ = title;
            _ = message;
            ShowCalls++;
            errorCode = ShowResult ? string.Empty : ErrorCode;
            return ShowResult;
        }
    }

    private sealed class FakePresentationGate : IUserSessionPresentationGate
    {
        public bool Available { get; init; }
        public string ErrorCode { get; init; } = "session_unavailable";

        public bool IsPresentationAvailable(out string errorCode)
        {
            errorCode = Available ? string.Empty : ErrorCode;
            return Available;
        }
    }

    private sealed class FakeNotificationController : INotificationController
    {
        public int GetSnapshotCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public NotificationControllerSnapshotResult GetSnapshot()
        {
            GetSnapshotCalls++;
            return new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: ValidSnapshot());
        }

        public NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            ApplyCalls++;
            return new NotificationControllerActionResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: true);
        }

        public NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            RestoreCalls++;
            return new NotificationControllerActionResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: true);
        }
    }

    private sealed class FakeNotificationSnapshotStore : IUserAgentNotificationSnapshotStore
    {
        public NotificationControllerSnapshot? StoredSnapshot { get; set; }
        public NotificationControllerSnapshot? SavedSnapshot { get; private set; }
        public bool Cleared { get; private set; }

        public void Save(NotificationControllerSnapshot snapshot)
        {
            SavedSnapshot = snapshot;
            StoredSnapshot = snapshot;
        }

        public NotificationControllerSnapshot? Load()
        {
            return StoredSnapshot;
        }

        public void Clear()
        {
            Cleared = true;
            StoredSnapshot = null;
        }
    }
}
