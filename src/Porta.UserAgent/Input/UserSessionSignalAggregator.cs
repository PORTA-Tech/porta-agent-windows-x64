namespace Porta.UserAgent.Input;

internal sealed class UserSessionSignalAggregator
{
    private readonly object _gate = new();
    private int _keyboardKeydownCount;
    private int _keyboardBackspaceCount;
    private int _mouseWheelDelta;
    private double _mouseMoveDx;
    private double _mouseMoveDy;
    private DateTimeOffset? _lastInputEventUtc;

    public void RecordKeyboard(bool isBackspace, DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            _keyboardKeydownCount++;
            if (isBackspace)
            {
                _keyboardBackspaceCount++;
            }

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
            _mouseMoveDx += dx;
            _mouseMoveDy += dy;
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
            _mouseWheelDelta += delta;
            TrackLatestInput(timestampUtc);
        }
    }

    public UserSessionSignalSnapshot SnapshotAndReset()
    {
        lock (_gate)
        {
            var snapshot = new UserSessionSignalSnapshot(
                KeyboardKeydownCount: _keyboardKeydownCount,
                KeyboardBackspaceCount: _keyboardBackspaceCount,
                MouseWheelDelta: _mouseWheelDelta,
                MouseMoveDx: _mouseMoveDx,
                MouseMoveDy: _mouseMoveDy,
                LastInputEventUtc: _lastInputEventUtc);

            _keyboardKeydownCount = 0;
            _keyboardBackspaceCount = 0;
            _mouseWheelDelta = 0;
            _mouseMoveDx = 0d;
            _mouseMoveDy = 0d;

            return snapshot;
        }
    }

    public void Requeue(UserSessionSignalSnapshot snapshot)
    {
        lock (_gate)
        {
            _keyboardKeydownCount += snapshot.KeyboardKeydownCount;
            _keyboardBackspaceCount += snapshot.KeyboardBackspaceCount;
            _mouseWheelDelta += snapshot.MouseWheelDelta;
            _mouseMoveDx += snapshot.MouseMoveDx;
            _mouseMoveDy += snapshot.MouseMoveDy;

            if (snapshot.LastInputEventUtc.HasValue)
            {
                if (!_lastInputEventUtc.HasValue || snapshot.LastInputEventUtc.Value > _lastInputEventUtc.Value)
                {
                    _lastInputEventUtc = snapshot.LastInputEventUtc.Value;
                }
            }
        }
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
