using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserAgentInterventionExecutorTests
{
    [Fact]
    public void ExecuteOrReplay_BreakSuggestionPrompt_QueuesAcceptedAckAfterUserResponse()
    {
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("overlay_started", ack!.Result);
        Assert.Equal(1, presenter.PromptCalls);

        presenter.CompletePrompt(UserInterventionPromptResponseKind.Accepted);

        Assert.True(executor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest completedAck));
        Assert.Equal("accepted", completedAck.Result);
        Assert.Null(completedAck.ErrorCode);
    }

    [Fact]
    public void ExecuteOrReplay_ShortBreakSuggestionPrompt_QueuesAcceptedAckAfterUserResponse()
    {
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break-short",
            "stretch_break_suggestion",
            "show_stretch_break_toast",
            """{"title":"Ari","message":"Take a short break now?","tip_key":"stretch_break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("overlay_started", ack!.Result);
        Assert.Equal(1, presenter.PromptCalls);

        presenter.CompletePrompt(UserInterventionPromptResponseKind.Accepted);

        Assert.True(executor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest completedAck));
        Assert.Equal("accepted", completedAck.Result);
        Assert.Null(completedAck.ErrorCode);
    }

    [Fact]
    public void ExecuteOrReplay_BreakSuggestionPrompt_UsesCanonicalOverlayLabelsWhenPayloadOmitsThem()
    {
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break-default-labels",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("overlay_started", ack!.Result);
        Assert.NotNull(presenter.LastPrompt);
        Assert.Equal("Take break", presenter.LastPrompt!.AcceptLabel);
        Assert.Equal("Skip", presenter.LastPrompt.DismissLabel);
        Assert.Equal("Snooze", presenter.LastPrompt.SnoozeLabel);
    }

    [Fact]
    public void ExecuteOrReplay_BreakSuggestionPrompt_UsesStoredPreference()
    {
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        preferenceStore.TrySuppressTip("break_suggestion", DateTimeOffset.UtcNow);
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("suppressed_by_preference", ack.Result);
        Assert.Null(ack.ErrorCode);
        Assert.Equal(0, presenter.PromptCalls);
    }

    [Fact]
    public void ExecuteOrReplay_BreakSuggestionPrompt_PersistsTurnOffTipResponse()
    {
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("overlay_started", ack!.Result);
        presenter.CompletePrompt(UserInterventionPromptResponseKind.SuppressedByPreference);

        Assert.True(executor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest completedAck));
        Assert.Equal("suppressed_by_preference", completedAck.Result);
        Assert.True(preferenceStore.IsTipSuppressed("break_suggestion"));
    }

    [Fact]
    public void ExecuteOrReplay_SnoozedPrompt_PersistsAndSuppressesFuturePrompt()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore, () => nowUtc);
        LocalApiInterventionPendingResponse first = BuildPending(
            "intv-break-1",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? firstAck = executor.ExecuteOrReplay(first, "session-1", "agent-1");
        Assert.NotNull(firstAck);
        Assert.Equal("overlay_started", firstAck!.Result);
        presenter.CompletePrompt(UserInterventionPromptResponseKind.Snoozed);

        Assert.True(executor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest completedAck));
        Assert.Equal("snoozed", completedAck.Result);
        Assert.True(preferenceStore.IsTipSnoozed("break_suggestion", nowUtc.AddMinutes(5)));

        nowUtc = nowUtc.AddMinutes(5);
        LocalApiInterventionPendingResponse second = BuildPending(
            "intv-break-2",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? blockedAck = executor.ExecuteOrReplay(second, "session-1", "agent-1");

        Assert.NotNull(blockedAck);
        Assert.Equal("snoozed", blockedAck.Result);
        Assert.Equal("tip_snoozed_active", blockedAck.ErrorCode);
        Assert.Equal(1, presenter.PromptCalls);
    }

    [Fact]
    public void ExecuteOrReplay_StatusToast_AcksApplied()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-overload",
            "inform_overload_risk",
            "show_status_toast",
            """{"title":"Ari","message":"Overload is rising. Slow down or take a short break."}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("applied", ack.Result);
        Assert.Null(ack.ErrorCode);
        Assert.Equal(1, presenter.ShowCalls);
        Assert.Equal("Ari", presenter.LastTitle);
        Assert.Contains("Overload is rising", presenter.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteOrReplay_PromptUnavailableBecauseWidgetFallback_IsAutoSnoozed()
    {
        var presenter = new FakePresenter { PromptShowResult = false, ErrorCode = "widget_forced_toast_fallback" };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break-fallback",
            "break_suggestion",
            "show_break_toast",
            """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("snoozed", ack.Result);
        Assert.Equal("widget_forced_toast_fallback", ack.ErrorCode);
        Assert.Equal(1, presenter.PromptCalls);
    }

    [Fact]
    public void ExecuteOrReplay_SecondPromptWithinGlobalBudget_IsAutoSnoozed()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore, () => nowUtc);

        Assert.Null(executor.ExecuteOrReplay(
            BuildPending(
                "intv-focus-1",
                "focus_lock_in_suggestion",
                "show_focus_lock_in_toast",
                """{"title":"Ari","message":"Keep your focus?","tip_key":"focus_lock_in_suggestion"}"""),
            "session-1",
            "agent-1"));
        presenter.CompletePrompt(UserInterventionPromptResponseKind.Dismissed);
        Assert.True(executor.TryDequeueCompletedAck(out _));

        nowUtc = nowUtc.AddMinutes(5);
        LocalApiInterventionAckRequest? blockedAck = executor.ExecuteOrReplay(
            BuildPending(
                "intv-break-2",
                "break_suggestion",
                "show_break_toast",
                """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}"""),
            "session-1",
            "agent-1");

        Assert.NotNull(blockedAck);
        Assert.Equal("snoozed", blockedAck.Result);
        Assert.Equal("anti_spam_global_prompt_budget", blockedAck.ErrorCode);
        Assert.Equal(1, presenter.PromptCalls);
    }

    [Fact]
    public void ExecuteOrReplay_WeakerPromptWhileStrongerPromptVisible_IsAutoSnoozed()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        var presenter = new FakePresenter { PromptShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore, () => nowUtc);

        LocalApiInterventionAckRequest? firstAck = executor.ExecuteOrReplay(
            BuildPending(
                "intv-break-1",
                "break_suggestion",
                "show_break_toast",
                """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}"""),
            "session-1",
            "agent-1");
        Assert.NotNull(firstAck);
        Assert.Equal("overlay_started", firstAck!.Result);

        LocalApiInterventionAckRequest? blockedAck = executor.ExecuteOrReplay(
            BuildPending(
                "intv-focus-1",
                "focus_lock_in_suggestion",
                "show_focus_lock_in_toast",
                """{"title":"Ari","message":"Keep your focus?","tip_key":"focus_lock_in_suggestion"}"""),
            "session-1",
            "agent-1");

        Assert.NotNull(blockedAck);
        Assert.Equal("snoozed", blockedAck.Result);
        Assert.Equal("anti_spam_active_prompt", blockedAck.ErrorCode);
        Assert.Equal(1, presenter.PromptCalls);
    }

    [Fact]
    public void ExecuteOrReplay_CriticalWidgetState_AutoSnoozesPromptAndSuppressesToast()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        var presenter = new FakePresenter { PromptShowResult = true, ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore, () => nowUtc);
        executor.ObserveWidgetState(BuildWidgetState(hasCriticalState: true));

        LocalApiInterventionAckRequest? promptAck = executor.ExecuteOrReplay(
            BuildPending(
                "intv-break-1",
                "break_suggestion",
                "show_break_toast",
                """{"title":"Ari","message":"Take a long break now?","tip_key":"break_suggestion"}"""),
            "session-1",
            "agent-1");
        LocalApiInterventionAckRequest? toastAck = executor.ExecuteOrReplay(
            BuildPending(
                "intv-overload-1",
                "inform_overload_risk",
                "show_status_toast",
                """{"title":"Ari","message":"Overload is rising."}"""),
            "session-1",
            "agent-1");

        Assert.NotNull(promptAck);
        Assert.Equal("snoozed", promptAck.Result);
        Assert.Equal("anti_spam_critical_state", promptAck.ErrorCode);
        Assert.NotNull(toastAck);
        Assert.Equal("applied", toastAck.Result);
        Assert.Equal(0, presenter.PromptCalls);
        Assert.Equal(0, presenter.ShowCalls);
    }

    [Fact]
    public void ExecuteOrReplay_WeakerStatusToastAfterStrongerFamilyToast_IsSuppressed()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore, () => nowUtc);

        LocalApiInterventionAckRequest? first = executor.ExecuteOrReplay(
            BuildPending(
                "intv-restrict-1",
                "temporary_app_restriction_enabled",
                "show_status_toast",
                """{"title":"Ari","message":"Stay off distracting apps for a few minutes."}"""),
            "session-1",
            "agent-1");

        nowUtc = nowUtc.AddMinutes(5);
        LocalApiInterventionAckRequest? second = executor.ExecuteOrReplay(
            BuildPending(
                "intv-inform-1",
                "inform_overload_risk",
                "show_status_toast",
                """{"title":"Ari","message":"Overload is rising."}"""),
            "session-1",
            "agent-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("applied", first.Result);
        Assert.Equal("applied", second.Result);
        Assert.Equal(1, presenter.ShowCalls);
    }

    [Fact]
    public void ExecuteOrReplay_DeepWorkStart_SuppressesNotifications()
    {
        var presenter = new FakePresenter();
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-on",
            "deep_work_notifications_suppressed",
            "suppress_notifications",
            "{}");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("verified", ack.Result);
        Assert.Equal(2, notificationController.GetSnapshotCalls);
        Assert.Equal(1, notificationController.ApplyCalls);
        Assert.True(snapshotStore.SavedSnapshot.HasValue);
    }

    [Fact]
    public void ExecuteOrReplay_DeepWorkStart_ShowsSuccessToastWithoutChangingAck()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-on",
            "deep_work_notifications_suppressed",
            "suppress_notifications",
            """{"title":"Ari","message":"Deep work started. Ari muted Windows notification banners to protect your focus."}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("verified", ack.Result);
        Assert.Equal(1, presenter.ShowCalls);
        Assert.Contains("muted Windows notification banners", presenter.LastMessage, StringComparison.Ordinal);
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
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-off",
            "deep_work_notifications_restored",
            "restore_notifications",
            "{}");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("verified", ack.Result);
        Assert.Equal(1, notificationController.GetSnapshotCalls);
        Assert.Equal(1, notificationController.RestoreCalls);
        Assert.True(snapshotStore.Cleared);
    }

    [Fact]
    public void ExecuteOrReplay_DeepWorkExit_ShowsSuccessToastWithoutChangingAck()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore
        {
            StoredSnapshot = ValidSnapshot()
        };
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-off",
            "deep_work_notifications_restored",
            "restore_notifications",
            """{"title":"Ari","message":"Deep work ended. Ari restored Windows notification banners."}""");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("verified", ack.Result);
        Assert.Equal(1, presenter.ShowCalls);
        Assert.Contains("restored Windows notification banners", presenter.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteOrReplay_NotificationVerificationUnavailable_FallsBackToApplied()
    {
        var presenter = new FakePresenter();
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController
        {
            FailVerificationSnapshot = true
        };
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-on",
            "deep_work_notifications_suppressed",
            "suppress_notifications",
            "{}");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("applied", ack.Result);
        Assert.Equal(2, notificationController.GetSnapshotCalls);
    }

    [Fact]
    public void ExecuteOrReplay_NotificationVerificationMismatch_AcksFailed()
    {
        var presenter = new FakePresenter();
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController
        {
            VerificationToastsEnabled = true
        };
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-dw-on",
            "deep_work_notifications_suppressed",
            "suppress_notifications",
            "{}");

        LocalApiInterventionAckRequest? ack = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(ack);
        Assert.Equal("failed", ack.Result);
        Assert.Equal("suppression_verify_failed", ack.ErrorCode);
    }

    [Fact]
    public void ExecuteOrReplay_DuplicateStatusIntervention_ReplaysCachedAckWithoutSecondExecution()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var gate = new FakePresentationGate { Available = true };
        var notificationController = new FakeNotificationController();
        var snapshotStore = new FakeNotificationSnapshotStore();
        var preferenceStore = new InMemoryPreferenceStore();
        var executor = CreateExecutor(presenter, gate, notificationController, snapshotStore, preferenceStore);
        LocalApiInterventionPendingResponse intervention = BuildPending(
            "intv-break",
            "inform_overload_risk",
            "show_status_toast",
            """{"title":"PORTA","message":"Overload is rising."}""");

        LocalApiInterventionAckRequest? first = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");
        LocalApiInterventionAckRequest? second = executor.ExecuteOrReplay(intervention, "session-1", "agent-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("applied", first.Result);
        Assert.Equal(first.AppliedAtUtc, second.AppliedAtUtc);
        Assert.Equal(1, presenter.ShowCalls);
    }

    // ── Workstream B1 — the single gated door for host interrupt toasts ──

    [Fact]
    public void TryShowGatedInterruptToast_Allowed_ShowsThenDedupsThroughTheGate()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var executor = CreateExecutor(
            presenter,
            new FakePresentationGate { Available = true },
            new FakeNotificationController(),
            new FakeNotificationSnapshotStore(),
            new InMemoryPreferenceStore());
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-08T12:00:00Z");

        bool shown = executor.TryShowGatedInterruptToast("ari:recovery", "Ari", "You were close.", now, out string reason);

        Assert.True(shown);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(1, presenter.ShowCalls);
        Assert.Equal("You were close.", presenter.LastMessage);

        // Marked-shown → the immediate duplicate is blocked BY THE GATE and never re-renders.
        bool again = executor.TryShowGatedInterruptToast("ari:recovery", "Ari", "You were close.", now, out string reason2);
        Assert.False(again);
        Assert.Equal("anti_spam_duplicate_semantic_toast", reason2);
        Assert.Equal(1, presenter.ShowCalls);
    }

    [Fact]
    public void TryShowGatedInterruptToast_PresentationUnavailable_NeverReachesRenderer()
    {
        var presenter = new FakePresenter { ShowResult = true };
        var executor = CreateExecutor(
            presenter,
            new FakePresentationGate { Available = false, ErrorCode = "session_unavailable" },
            new FakeNotificationController(),
            new FakeNotificationSnapshotStore(),
            new InMemoryPreferenceStore());

        bool shown = executor.TryShowGatedInterruptToast(
            "task:expiry", "Ari", "Time's up.", DateTimeOffset.Parse("2026-03-08T12:00:00Z"), out string reason);

        Assert.False(shown);
        Assert.Equal("session_unavailable", reason);
        Assert.Equal(0, presenter.ShowCalls);
    }

    private static UserAgentInterventionExecutor CreateExecutor(
        FakePresenter presenter,
        FakePresentationGate gate,
        FakeNotificationController notificationController,
        FakeNotificationSnapshotStore snapshotStore,
        InMemoryPreferenceStore preferenceStore,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        return new UserAgentInterventionExecutor(
            presenter,
            gate,
            notificationController,
            snapshotStore,
            preferenceStore,
            antiSpamGate: new UserInterventionAntiSpamGate(),
            utcNowProvider: utcNowProvider);
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
        public bool PromptShowResult { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public int ShowCalls { get; private set; }
        public int PromptCalls { get; private set; }
        public string LastTitle { get; private set; } = string.Empty;
        public string LastMessage { get; private set; } = string.Empty;
        public UserInterventionPrompt? LastPrompt { get; private set; }
        public Action<UserInterventionPromptResponse>? PromptResponseHandler { get; private set; }

        public bool TryShow(string title, string message, out string errorCode)
        {
            LastTitle = title;
            LastMessage = message;
            ShowCalls++;
            errorCode = ShowResult ? string.Empty : ErrorCode;
            return ShowResult;
        }

        public bool TryPresentPrompt(
            UserInterventionPrompt prompt,
            Action<UserInterventionPromptResponse> onResponse,
            out string errorCode)
        {
            LastPrompt = prompt;
            PromptResponseHandler = onResponse;
            PromptCalls++;
            errorCode = PromptShowResult ? string.Empty : ErrorCode;
            return PromptShowResult;
        }

        public void CompletePrompt(UserInterventionPromptResponseKind kind)
        {
            Assert.NotNull(LastPrompt);
            Assert.NotNull(PromptResponseHandler);
            PromptResponseHandler!(new UserInterventionPromptResponse(
                InterventionId: LastPrompt!.InterventionId,
                TipKey: LastPrompt.TipKey,
                Kind: kind));
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
        public bool FailVerificationSnapshot { get; init; }
        public bool? VerificationToastsEnabled { get; init; }

        public NotificationControllerSnapshotResult GetSnapshot()
        {
            GetSnapshotCalls++;
            if (GetSnapshotCalls >= 2 && FailVerificationSnapshot)
            {
                return new NotificationControllerSnapshotResult(
                    Succeeded: false,
                    ErrorCode: "verification_snapshot_failed",
                    ErrorDetail: "simulated verification failure",
                    Snapshot: ValidSnapshot());
            }

            bool? verificationToastsEnabled = GetSnapshotCalls >= 2
                ? VerificationToastsEnabled
                : null;
            return new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: ValidSnapshot() with
                {
                    ToastBannersEnabled = verificationToastsEnabled ?? (ApplyCalls == 0)
                });
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

    private static UserDesktopWidgetState BuildWidgetState(bool hasCriticalState)
    {
        return new UserDesktopWidgetState(
            Mode: UserDesktopWidgetMode.Monitoring,
            Title: "Ari",
            StatusText: "Monitoring",
            MessageSeverity: UserDesktopWidgetMessageSeverity.Neutral,
            FooterText: "Ready",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "0m"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "0m"),
            TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "0%"),
            CanPause: true,
            CanResume: false,
            CriticalState: hasCriticalState
                ? new UserDesktopWidgetCriticalState(
                    Title: "Core service offline",
                    Message: "Open Health.",
                    PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                    PrimaryLabel: "Open Health",
                    SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                    SecondaryLabel: "Retry")
                : null);
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

    private sealed class InMemoryPreferenceStore : IUserInterventionPreferenceStore
    {
        private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _snoozedUntilUtc = new(StringComparer.Ordinal);

        public bool IsTipSuppressed(string tipKey)
        {
            return _suppressed.Contains(tipKey.Trim().ToLowerInvariant());
        }

        public bool IsTipSnoozed(string tipKey, DateTimeOffset nowUtc)
        {
            return _snoozedUntilUtc.TryGetValue(tipKey.Trim().ToLowerInvariant(), out DateTimeOffset snoozeUntilUtc)
                && snoozeUntilUtc > nowUtc;
        }

        public bool TrySuppressTip(string tipKey, DateTimeOffset updatedAtUtc)
        {
            _ = updatedAtUtc;
            _suppressed.Add(tipKey.Trim().ToLowerInvariant());
            return true;
        }

        public bool TrySnoozeTip(string tipKey, DateTimeOffset snoozeUntilUtc, DateTimeOffset updatedAtUtc)
        {
            _ = updatedAtUtc;
            _snoozedUntilUtc[tipKey.Trim().ToLowerInvariant()] = snoozeUntilUtc;
            return true;
        }
    }
}
