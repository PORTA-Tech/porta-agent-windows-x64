using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests.Harness;

public readonly record struct NotificationControllerCall(
    string Operation,
    DateTimeOffset TimestampUtc,
    bool Succeeded,
    string ErrorCode,
    bool StateChanged,
    NotificationControllerSnapshot? ArgumentSnapshot,
    string ResultFocusAssistMode,
    bool ResultQuietHoursEnabled,
    bool ResultToastBannersEnabled,
    bool ResultSuppressed);

public sealed class FakeNotificationController : INotificationController
{
    private readonly List<NotificationControllerCall> _calls = [];

    public FakeNotificationController(DeterministicTestClock clock)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DeterministicTestClock Clock { get; }

    public int GetSnapshotCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public bool FailNextSnapshot { get; set; }

    public bool FailNextApply { get; set; }

    public bool FailNextRestore { get; set; }

    public string SnapshotFailureCode { get; set; } = "snapshot_capture_failed";

    public string ApplyFailureCode { get; set; } = "suppression_apply_failed";

    public string RestoreFailureCode { get; set; } = "suppression_restore_failed";

    public bool CurrentQuietHoursEnabled { get; private set; }

    public bool CurrentToastBannersEnabled { get; private set; } = true;

    public string CurrentFocusAssistMode { get; private set; } = "off";

    public bool IsSuppressed =>
        CurrentQuietHoursEnabled
        || !CurrentToastBannersEnabled
        || string.Equals(CurrentFocusAssistMode, "priority_only", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CurrentFocusAssistMode, "alarms_only", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CurrentFocusAssistMode, "on", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<NotificationControllerCall> Calls => _calls;

    public NotificationControllerSnapshotResult GetSnapshot()
    {
        GetSnapshotCalls++;

        if (FailNextSnapshot)
        {
            FailNextSnapshot = false;
            NotificationControllerSnapshot failedSnapshot = BuildSnapshot(isValid: false);
            _calls.Add(new NotificationControllerCall(
                Operation: nameof(GetSnapshot),
                TimestampUtc: Clock.UtcNow,
                Succeeded: false,
                ErrorCode: SnapshotFailureCode,
                StateChanged: false,
                ArgumentSnapshot: null,
                ResultFocusAssistMode: CurrentFocusAssistMode,
                ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
                ResultToastBannersEnabled: CurrentToastBannersEnabled,
                ResultSuppressed: IsSuppressed));
            return new NotificationControllerSnapshotResult(
                Succeeded: false,
                ErrorCode: SnapshotFailureCode,
                ErrorDetail: "Injected snapshot failure.",
                Snapshot: failedSnapshot);
        }

        NotificationControllerSnapshot snapshot = BuildSnapshot(isValid: true);
        _calls.Add(new NotificationControllerCall(
            Operation: nameof(GetSnapshot),
            TimestampUtc: Clock.UtcNow,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: false,
            ArgumentSnapshot: null,
            ResultFocusAssistMode: CurrentFocusAssistMode,
            ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
            ResultToastBannersEnabled: CurrentToastBannersEnabled,
            ResultSuppressed: IsSuppressed));
        return new NotificationControllerSnapshotResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            Snapshot: snapshot);
    }

    public NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot)
    {
        ApplyCalls++;

        if (FailNextApply)
        {
            FailNextApply = false;
            _calls.Add(new NotificationControllerCall(
                Operation: nameof(ApplySuppression),
                TimestampUtc: Clock.UtcNow,
                Succeeded: false,
                ErrorCode: ApplyFailureCode,
                StateChanged: false,
                ArgumentSnapshot: snapshot,
                ResultFocusAssistMode: CurrentFocusAssistMode,
                ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
                ResultToastBannersEnabled: CurrentToastBannersEnabled,
                ResultSuppressed: IsSuppressed));
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: ApplyFailureCode,
                ErrorDetail: "Injected apply failure.",
                StateChanged: false);
        }

        _ = snapshot;
        bool previousSuppressed = IsSuppressed;
        CurrentFocusAssistMode = "priority_only";
        CurrentQuietHoursEnabled = true;
        CurrentToastBannersEnabled = false;
        bool changed = !previousSuppressed;
        _calls.Add(new NotificationControllerCall(
            Operation: nameof(ApplySuppression),
            TimestampUtc: Clock.UtcNow,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: changed,
            ArgumentSnapshot: snapshot,
            ResultFocusAssistMode: CurrentFocusAssistMode,
            ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
            ResultToastBannersEnabled: CurrentToastBannersEnabled,
            ResultSuppressed: IsSuppressed));
        return new NotificationControllerActionResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: changed);
    }

    public NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot)
    {
        RestoreCalls++;

        if (FailNextRestore)
        {
            FailNextRestore = false;
            _calls.Add(new NotificationControllerCall(
                Operation: nameof(Restore),
                TimestampUtc: Clock.UtcNow,
                Succeeded: false,
                ErrorCode: RestoreFailureCode,
                StateChanged: false,
                ArgumentSnapshot: snapshot,
                ResultFocusAssistMode: CurrentFocusAssistMode,
                ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
                ResultToastBannersEnabled: CurrentToastBannersEnabled,
                ResultSuppressed: IsSuppressed));
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: RestoreFailureCode,
                ErrorDetail: "Injected restore failure.",
                StateChanged: false);
        }

        if (!snapshot.IsValid)
        {
            _calls.Add(new NotificationControllerCall(
                Operation: nameof(Restore),
                TimestampUtc: Clock.UtcNow,
                Succeeded: false,
                ErrorCode: "snapshot_invalid",
                StateChanged: false,
                ArgumentSnapshot: snapshot,
                ResultFocusAssistMode: CurrentFocusAssistMode,
                ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
                ResultToastBannersEnabled: CurrentToastBannersEnabled,
                ResultSuppressed: IsSuppressed));
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "snapshot_invalid",
                ErrorDetail: "Snapshot is invalid and cannot be restored.",
                StateChanged: false);
        }

        bool previousSuppressed = IsSuppressed;
        CurrentFocusAssistMode = snapshot.FocusAssistMode;
        CurrentQuietHoursEnabled = snapshot.QuietHoursEnabled;
        CurrentToastBannersEnabled = snapshot.ToastBannersEnabled;
        bool changed = previousSuppressed != IsSuppressed;
        _calls.Add(new NotificationControllerCall(
            Operation: nameof(Restore),
            TimestampUtc: Clock.UtcNow,
            Succeeded: true,
            ErrorCode: string.Empty,
            StateChanged: changed,
            ArgumentSnapshot: snapshot,
            ResultFocusAssistMode: CurrentFocusAssistMode,
            ResultQuietHoursEnabled: CurrentQuietHoursEnabled,
            ResultToastBannersEnabled: CurrentToastBannersEnabled,
            ResultSuppressed: IsSuppressed));
        return new NotificationControllerActionResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: changed);
    }

    private NotificationControllerSnapshot BuildSnapshot(bool isValid)
    {
        return new NotificationControllerSnapshot(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: Clock.UtcNow,
            IsValid: isValid,
            Provider: "fake",
            FocusAssistMode: CurrentFocusAssistMode,
            QuietHoursEnabled: CurrentQuietHoursEnabled,
            ToastBannersEnabled: CurrentToastBannersEnabled,
            RawStateJson: "{}");
    }
}
