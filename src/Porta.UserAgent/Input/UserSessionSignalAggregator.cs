namespace Porta.UserAgent.Input;

internal sealed class UserSessionSignalAggregator
{
    private readonly object _gate = new();
    private readonly List<KeyboardSignalSample> _keyboardSamples = [];
    private readonly List<MouseMoveSignalSample> _mouseMoveSamples = [];
    private readonly List<MouseWheelSignalSample> _mouseWheelSamples = [];
    private readonly List<FocusObservationSample> _focusObservations = [];
    private readonly List<IdleObservationSample> _idleObservations = [];
    private DateTimeOffset? _lastInputEventUtc;

    public void RecordKeyboard(bool isBackspace, DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            _keyboardSamples.Add(new KeyboardSignalSample(ToUtcMilliseconds(timestampUtc), isBackspace));
            TrackLatestInput(timestampUtc);
        }
    }

    public void RecordMouseMove(int dx, int dy, DateTimeOffset timestampUtc)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        lock (_gate)
        {
            _mouseMoveSamples.Add(new MouseMoveSignalSample(ToUtcMilliseconds(timestampUtc), dx, dy));
            TrackLatestInput(timestampUtc);
        }
    }

    public void RecordMouseWheel(int delta, DateTimeOffset timestampUtc)
    {
        if (delta == 0)
        {
            return;
        }

        lock (_gate)
        {
            _mouseWheelSamples.Add(new MouseWheelSignalSample(ToUtcMilliseconds(timestampUtc), delta));
            TrackLatestInput(timestampUtc);
        }
    }

    public void RecordFocusObservation(string? appHash, DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            _focusObservations.Add(new FocusObservationSample(
                ToUtcMilliseconds(timestampUtc),
                string.IsNullOrWhiteSpace(appHash) ? null : appHash.Trim().ToLowerInvariant()));
        }
    }

    public void RecordIdleObservation(double idleSeconds, DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            _idleObservations.Add(new IdleObservationSample(
                ToUtcMilliseconds(timestampUtc),
                NormalizeNonNegative(idleSeconds)));
        }
    }

    public UserSessionSignalSnapshot SnapshotAndReset()
    {
        lock (_gate)
        {
            var snapshot = new UserSessionSignalSnapshot(
                KeyboardSamples: _keyboardSamples.ToArray(),
                MouseMoveSamples: _mouseMoveSamples.ToArray(),
                MouseWheelSamples: _mouseWheelSamples.ToArray(),
                FocusObservations: _focusObservations.ToArray(),
                IdleObservations: _idleObservations.ToArray(),
                LastInputEventUtc: _lastInputEventUtc);

            _keyboardSamples.Clear();
            _mouseMoveSamples.Clear();
            _mouseWheelSamples.Clear();
            _focusObservations.Clear();
            _idleObservations.Clear();

            return snapshot;
        }
    }

    public void Requeue(UserSessionSignalSnapshot snapshot)
    {
        lock (_gate)
        {
            _keyboardSamples.AddRange(snapshot.KeyboardSamples);
            _mouseMoveSamples.AddRange(snapshot.MouseMoveSamples);
            _mouseWheelSamples.AddRange(snapshot.MouseWheelSamples);
            _focusObservations.AddRange(snapshot.FocusObservations);
            _idleObservations.AddRange(snapshot.IdleObservations);

            if (snapshot.LastInputEventUtc.HasValue)
            {
                if (!_lastInputEventUtc.HasValue || snapshot.LastInputEventUtc.Value > _lastInputEventUtc.Value)
                {
                    _lastInputEventUtc = snapshot.LastInputEventUtc.Value;
                }
            }
        }
    }

    private static long ToUtcMilliseconds(DateTimeOffset timestampUtc)
    {
        return timestampUtc.ToUniversalTime().ToUnixTimeMilliseconds();
    }

    private static double NormalizeNonNegative(double value)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }

    private void TrackLatestInput(DateTimeOffset timestampUtc)
    {
        DateTimeOffset normalized = timestampUtc.ToUniversalTime();
        if (!_lastInputEventUtc.HasValue || normalized > _lastInputEventUtc.Value)
        {
            _lastInputEventUtc = normalized;
        }
    }
}
