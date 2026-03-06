namespace Porta.Agent.Core.Outputs;

public readonly record struct NotificationControllerSnapshot(
    int SchemaVer,
    string ContractVersion,
    DateTimeOffset CapturedAtUtc,
    bool IsValid,
    string Provider,
    string FocusAssistMode,
    bool QuietHoursEnabled,
    bool ToastBannersEnabled,
    string RawStateJson);

public readonly record struct NotificationControllerSnapshotResult(
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    NotificationControllerSnapshot Snapshot);

public readonly record struct NotificationControllerActionResult(
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    bool StateChanged);

public interface INotificationController
{
    NotificationControllerSnapshotResult GetSnapshot();
    NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot);
    NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot);
}

public sealed class NoOpNotificationController : INotificationController
{
    public NotificationControllerSnapshotResult GetSnapshot()
    {
        NotificationControllerSnapshot snapshot = new(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            IsValid: true,
            Provider: "noop",
            FocusAssistMode: "unknown",
            QuietHoursEnabled: false,
            ToastBannersEnabled: true,
            RawStateJson: "{}");
        return new NotificationControllerSnapshotResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            Snapshot: snapshot);
    }

    public NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot)
    {
        _ = snapshot;
        return new NotificationControllerActionResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: false);
    }

    public NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot)
    {
        _ = snapshot;
        return new NotificationControllerActionResult(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: false);
    }
}
