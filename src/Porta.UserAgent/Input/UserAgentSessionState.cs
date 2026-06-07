namespace Porta.UserAgent.Input;

internal sealed class UserAgentSessionState
{
    private readonly HashSet<int> _firedFocusMilestones = new(); // 15, 30, 45, 60, 90
    private bool _firstFocusMessageSent;
    private bool _longSessionToastSent;
    private bool _sessionWrapToastSent;
    private DateTimeOffset _lastToastSentUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _sessionDate;

    // Pre-distraction focus duration — set when distraction episode starts
    public double PreDistractionFocusSeconds { get; set; }

    public bool InDistractionEpisode { get; private set; }

    public DateTimeOffset DistractionStartedUtc { get; private set; }

    // Returns true if this milestone has not been fired in this session
    public bool TryFireMilestone(int minutes) => _firedFocusMilestones.Add(minutes);

    // 8-minute suppression between any toasts
    public bool CanShowToast(DateTimeOffset nowUtc) =>
        (nowUtc - _lastToastSentUtc).TotalMinutes >= 8;

    public void RecordToastShown(DateTimeOffset nowUtc) => _lastToastSentUtc = nowUtc;

    public bool FirstFocusMessageSent => _firstFocusMessageSent;

    public void MarkFirstFocusMessageSent() => _firstFocusMessageSent = true;

    public bool LongSessionToastSent => _longSessionToastSent;

    public void MarkLongSessionToastSent() => _longSessionToastSent = true;

    public bool SessionWrapToastSent => _sessionWrapToastSent;

    public void MarkSessionWrapToastSent() => _sessionWrapToastSent = true;

    public void MarkDistractionStarted(double preFocusSeconds, DateTimeOffset nowUtc)
    {
        InDistractionEpisode = true;
        PreDistractionFocusSeconds = preFocusSeconds;
        DistractionStartedUtc = nowUtc;
    }

    public void MarkDistractionEnded() => InDistractionEpisode = false;

    // Reset at start of each new calendar day
    public void ResetIfNewDay(DateTimeOffset nowUtc)
    {
        if (DateOnly.FromDateTime(nowUtc.LocalDateTime) != DateOnly.FromDateTime(_sessionDate.LocalDateTime))
        {
            _firedFocusMilestones.Clear();
            _firstFocusMessageSent = false;
            _longSessionToastSent = false;
            _sessionWrapToastSent = false;
            _lastToastSentUtc = DateTimeOffset.MinValue;
            InDistractionEpisode = false;
            PreDistractionFocusSeconds = 0;
            _sessionDate = nowUtc;
        }
    }
}
