namespace Porta.Agent.Core.Outputs;

public readonly record struct NotificationControllerSnapshot(
    bool SuppressionEnabled,
    string Provider,
    string RawStateJson);

public interface INotificationController
{
    NotificationControllerSnapshot GetSnapshot();
    void ApplySuppression(NotificationControllerSnapshot snapshot);
    void Restore(NotificationControllerSnapshot snapshot);
}

public sealed class NoOpNotificationController : INotificationController
{
    public NotificationControllerSnapshot GetSnapshot()
    {
        return new NotificationControllerSnapshot(
            SuppressionEnabled: false,
            Provider: "noop",
            RawStateJson: "{}");
    }

    public void ApplySuppression(NotificationControllerSnapshot snapshot)
    {
        _ = snapshot;
    }

    public void Restore(NotificationControllerSnapshot snapshot)
    {
        _ = snapshot;
    }
}
