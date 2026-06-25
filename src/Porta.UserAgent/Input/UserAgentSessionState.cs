namespace Porta.UserAgent.Input;

internal sealed class UserAgentSessionState
{
    private readonly HashSet<int> _firedFocusMilestones = new(); // 15, 30, 45, 60, 90
    private bool _firstFocusMessageSent;
    private bool _longSessionToastSent;
    private bool _sessionWrapToastSent;
    private DateTimeOffset _lastToastSentUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _sessionDate;
    private double _totalFocusSecondsToday;

    // Recovery-nudge caps (Launch 2.2): the "you were close" nudge is high-value but a nag if
    // overused, so it is limited beyond the global 8-minute toast gate — at most a few per day
    // and never twice within an hour.
    private int _recoveryNudgesToday;
    private DateTimeOffset _lastRecoveryNudgeUtc = DateTimeOffset.MinValue;
    private const int MaxRecoveryNudgesPerDay = 3;
    private const double RecoveryNudgeMinIntervalMinutes = 60;

    // Tracks last time any activity was observed (updated by UserAgentHost each tick)
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.MinValue;

    // Accumulated focus seconds for today (updated each tick when in active focus)
    public double TotalFocusSecondsToday => _totalFocusSecondsToday;

    public void RecordActivity(DateTimeOffset nowUtc, double focusSecondsThisTick)
    {
        LastActivityUtc = nowUtc;
        _totalFocusSecondsToday += focusSecondsThisTick;
    }

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

    // Recovery nudge may fire only if under the daily cap AND at least an hour since the last one.
    public bool CanShowRecoveryNudge(DateTimeOffset nowUtc) =>
        _recoveryNudgesToday < MaxRecoveryNudgesPerDay
        && (nowUtc - _lastRecoveryNudgeUtc).TotalMinutes >= RecoveryNudgeMinIntervalMinutes;

    public void RecordRecoveryNudgeShown(DateTimeOffset nowUtc)
    {
        _recoveryNudgesToday++;
        _lastRecoveryNudgeUtc = nowUtc;
    }

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
            _recoveryNudgesToday = 0;
            _lastRecoveryNudgeUtc = DateTimeOffset.MinValue;
            _totalFocusSecondsToday = 0;
            LastActivityUtc = DateTimeOffset.MinValue;
            _sessionDate = nowUtc;
        }
    }
}
