namespace Porta.UserAgent.Interventions;

internal sealed class CaptureDegradedToastGate
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan HealthyResetWindow = TimeSpan.FromMinutes(1);

    private string? _lastSuppressionKey;
    private DateTimeOffset _lastShownUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _healthySinceUtc;

    public void ObserveHealthyState(DateTimeOffset nowUtc)
    {
        if (_lastSuppressionKey == null)
        {
            return;
        }

        _healthySinceUtc ??= nowUtc;
        if ((nowUtc - _healthySinceUtc.Value) >= HealthyResetWindow)
        {
            _lastSuppressionKey = null;
            _lastShownUtc = DateTimeOffset.MinValue;
        }
    }

    public bool ShouldShow(UserCommunicationToast communication, DateTimeOffset nowUtc)
    {
        _healthySinceUtc = null;

        if (_lastSuppressionKey == null)
        {
            return true;
        }

        if (!string.Equals(_lastSuppressionKey, communication.DeduplicationKey, StringComparison.Ordinal))
        {
            return true;
        }

        return (nowUtc - _lastShownUtc) >= RepeatWindow;
    }

    public void MarkShown(UserCommunicationToast communication, DateTimeOffset nowUtc)
    {
        _lastSuppressionKey = communication.DeduplicationKey;
        _lastShownUtc = nowUtc;
        _healthySinceUtc = null;
    }
}
