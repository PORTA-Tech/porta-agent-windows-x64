namespace Porta.Agent.Common;

/// <summary>
/// Everything the compose UI needs to render one frame of the reminder quick-add:
/// the title, the resolved due time (null when nothing valid is selected — confirm
/// must be blocked), the human preview, and the picker state to highlight.
/// </summary>
public readonly record struct ReminderComposeSnapshot(
    string Title,
    DateTimeOffset? DueAt,
    string PreviewLabel,          // "Rings at 16:42 · in 1h20m" | "Rings tomorrow at 09:00" | "Pick a time"
    bool CanConfirm,
    int SelectedDurationMinutes,  // > 0 when a duration drives the due time (chip highlight)
    int ClockHour,                // >= 0 when an exact time drives the due time
    int ClockMinute);

/// <summary>
/// Pure state machine behind the reminder quick-add picker (Workstream 2). Holds the
/// typed text plus at most one MANUAL selection (a duration from chips/stepper, or an
/// exact clock time). Resolution order: manual selection wins over a time recognized in
/// the typed text; typed text wins over nothing. Typing more words never clears a manual
/// selection (titles are typed after picking a time all the time); typing a NEW recognized
/// time replaces the manual selection — the most recent explicit time wins.
/// No WPF anywhere in this class — the window only renders snapshots and forwards taps.
/// </summary>
public sealed class ReminderComposeState
{
    public const string PickTimeLabel = "Pick a time";
    public const int StepMinutesDelta = 15;
    public const int MinDurationMinutes = 5;
    public static readonly int[] ChipMinutes = { 10, 30, 60, 120 };

    private enum ManualKind
    {
        None,
        Duration,
        Clock
    }

    private string _text = string.Empty;
    private ManualKind _manual = ManualKind.None;
    private int _manualDurationMinutes;
    private int _clockHour;
    private int _clockMinute;
    private bool _lastTypedTimeRecognized;

    /// <summary>Updates the typed text. A newly recognized typed time replaces any manual pick.</summary>
    public void SetText(string? text, DateTimeOffset now)
    {
        _text = text ?? string.Empty;
        ReminderParseResult parsed = ReminderTimeParser.Parse(_text, now);

        // Only a TRANSITION into a recognized time overrides the manual selection. While the
        // recognized token merely persists in the text (user keeps typing the title around
        // it), the manual pick — made after typing — stays authoritative.
        if (parsed.TimeRecognized && !_lastTypedTimeRecognized)
        {
            _manual = ManualKind.None;
        }

        _lastTypedTimeRecognized = parsed.TimeRecognized;
    }

    public void SelectChip(int minutes)
    {
        _manual = ManualKind.Duration;
        _manualDurationMinutes = Math.Max(MinDurationMinutes, minutes);
    }

    /// <summary>
    /// ±15m stepper. Adjusts whatever currently drives the due time: a manual duration
    /// (floor 5m), a manual clock time (shifts it), or a typed time (seeds the equivalent
    /// manual state, then adjusts). With nothing selected, +15 starts a 15m duration and
    /// −15 does nothing.
    /// </summary>
    public void Step(int deltaMinutes, DateTimeOffset now)
    {
        switch (_manual)
        {
            case ManualKind.Duration:
                _manualDurationMinutes = Math.Max(MinDurationMinutes, _manualDurationMinutes + deltaMinutes);
                return;
            case ManualKind.Clock:
                ShiftClock(deltaMinutes);
                return;
        }

        ReminderParseResult parsed = ReminderTimeParser.Parse(_text, now);
        if (parsed.TimeRecognized && parsed.Kind == ReminderTimeKind.Duration)
        {
            _manual = ManualKind.Duration;
            _manualDurationMinutes = Math.Max(MinDurationMinutes, parsed.DurationMinutes + deltaMinutes);
            return;
        }

        if (parsed.TimeRecognized && parsed.Kind == ReminderTimeKind.Clock)
        {
            _manual = ManualKind.Clock;
            _clockHour = parsed.DueAt.Hour;
            _clockMinute = parsed.DueAt.Minute;
            ShiftClock(deltaMinutes);
            return;
        }

        if (deltaMinutes > 0)
        {
            _manual = ManualKind.Duration;
            _manualDurationMinutes = Math.Max(MinDurationMinutes, deltaMinutes);
        }
    }

    /// <summary>Exact-time entry (clock toggle). A past time today resolves to tomorrow.</summary>
    public bool SetExactTime(int hour, int minute)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        _manual = ManualKind.Clock;
        _clockHour = hour;
        _clockMinute = minute;
        return true;
    }

    public void Reset()
    {
        _text = string.Empty;
        _manual = ManualKind.None;
        _manualDurationMinutes = 0;
        _lastTypedTimeRecognized = false;
    }

    public ReminderComposeSnapshot Snapshot(DateTimeOffset now)
    {
        ReminderParseResult parsed = ReminderTimeParser.Parse(_text, now);

        DateTimeOffset? due = null;
        int selectedDuration = 0;
        int clockHour = -1;
        int clockMinute = 0;

        switch (_manual)
        {
            case ManualKind.Duration:
                due = now.AddMinutes(_manualDurationMinutes);
                selectedDuration = _manualDurationMinutes;
                break;
            case ManualKind.Clock:
                due = ResolveClock(_clockHour, _clockMinute, now);
                clockHour = _clockHour;
                clockMinute = _clockMinute;
                break;
            default:
                if (parsed.TimeRecognized)
                {
                    due = parsed.DueAt;
                    if (parsed.Kind == ReminderTimeKind.Duration)
                    {
                        selectedDuration = parsed.DurationMinutes;
                    }
                    else
                    {
                        clockHour = parsed.DueAt.Hour;
                        clockMinute = parsed.DueAt.Minute;
                    }
                }

                break;
        }

        return new ReminderComposeSnapshot(
            Title: parsed.Title,
            DueAt: due,
            PreviewLabel: due is { } d ? BuildPreview(d, now) : PickTimeLabel,
            CanConfirm: due != null,
            SelectedDurationMinutes: selectedDuration,
            ClockHour: clockHour,
            ClockMinute: clockMinute);
    }

    private void ShiftClock(int deltaMinutes)
    {
        int total = (((_clockHour * 60) + _clockMinute + deltaMinutes) % 1440 + 1440) % 1440;
        _clockHour = total / 60;
        _clockMinute = total % 60;
    }

    private static DateTimeOffset ResolveClock(int hour, int minute, DateTimeOffset now)
    {
        var due = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        return due <= now ? due.AddDays(1) : due;
    }

    private static string BuildPreview(DateTimeOffset due, DateTimeOffset now)
    {
        bool tomorrow = due.Date > now.Date;
        int minutesUntil = Math.Max(1, (int)Math.Round((due - now).TotalMinutes));
        return tomorrow
            ? $"Rings tomorrow at {due:HH:mm}"
            : $"Rings at {due:HH:mm} · in {ReminderTimeParser.FormatDuration(minutesUntil)}";
    }
}
