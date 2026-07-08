namespace Porta.Agent.Common;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 2c — parses the reminder quick-add line ("standup in 20m", "ship build at 16:30",
/// "call kamal tomorrow 9am") into a title + due time. PURE and deliberately minimal:
/// exactly the four grammar forms from the build plan, nothing clever. Anything without a
/// recognizable time falls back to +20 minutes with <see cref="ReminderParseResult.TimeRecognized"/>
/// = false, so the widget can show an honest live preview ("Set for 16:30" / "in 20m") before
/// the user confirms — what was scheduled is never ambiguous.
/// </summary>
public readonly record struct ReminderParseResult(
    string Title,
    DateTimeOffset DueAt,        // same offset/kind as the "now" passed in (local time expected)
    string PreviewLabel,         // "in 20m" | "at 16:30" | "tomorrow 09:00"
    bool TimeRecognized);

public static class ReminderTimeParser
{
    private const int DefaultMinutes = 20;

    private static readonly Regex InMinutes = new(
        @"\bin\s+(\d{1,3})\s*m(?:in(?:ute)?s?)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InHours = new(
        @"\bin\s+(\d{1,2})\s*h(?:ou)?rs?\b|\bin\s+(\d{1,2})\s*h\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AtClock = new(
        @"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tomorrow = new(
        @"\btomorrow(?:\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses <paramref name="input"/> relative to <paramref name="now"/> (pass local time).</summary>
    public static ReminderParseResult Parse(string? input, DateTimeOffset now)
    {
        string text = (input ?? string.Empty).Trim();

        Match m = InMinutes.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int minutes) && minutes > 0)
        {
            return Build(text, m, now.AddMinutes(minutes), $"in {minutes}m", recognized: true);
        }

        m = InHours.Match(text);
        if (m.Success)
        {
            string raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(raw, out int hours) && hours > 0)
            {
                return Build(text, m, now.AddHours(hours), $"in {hours}h", recognized: true);
            }
        }

        m = Tomorrow.Match(text);
        if (m.Success)
        {
            (int hour, int minute, bool valid) = ResolveClock(
                m.Groups[1], m.Groups[2], m.Groups[3], defaultHour: 9);
            if (valid)
            {
                DateTimeOffset due = new DateTimeOffset(
                    now.Year, now.Month, now.Day, hour, minute, 0, now.Offset).AddDays(1);
                return Build(text, m, due, $"tomorrow {hour:00}:{minute:00}", recognized: true);
            }
        }

        m = AtClock.Match(text);
        if (m.Success)
        {
            (int hour, int minute, bool valid) = ResolveClock(
                m.Groups[1], m.Groups[2], m.Groups[3], defaultHour: -1);
            // Bare "at 7" with no minutes and no am/pm is too ambiguous to accept silently —
            // require ":mm" or am/pm; otherwise fall through to the default.
            bool precise = m.Groups[2].Success || m.Groups[3].Success;
            if (valid && precise)
            {
                DateTimeOffset due = new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
                if (due <= now)
                {
                    due = due.AddDays(1); // that time already passed today → tomorrow
                }

                return Build(text, m, due, $"at {hour:00}:{minute:00}", recognized: true);
            }
        }

        // No recognizable time: honest default, clearly previewed.
        return new ReminderParseResult(
            Title: NormalizeTitle(text),
            DueAt: now.AddMinutes(DefaultMinutes),
            PreviewLabel: $"in {DefaultMinutes}m",
            TimeRecognized: false);
    }

    private static (int Hour, int Minute, bool Valid) ResolveClock(
        Group hourGroup, Group minuteGroup, Group amPmGroup, int defaultHour)
    {
        if (!hourGroup.Success)
        {
            return defaultHour >= 0 ? (defaultHour, 0, true) : (0, 0, false);
        }

        if (!int.TryParse(hourGroup.Value, out int hour))
        {
            return (0, 0, false);
        }

        int minute = 0;
        if (minuteGroup.Success && (!int.TryParse(minuteGroup.Value, out minute) || minute > 59))
        {
            return (0, 0, false);
        }

        if (amPmGroup.Success)
        {
            if (hour is < 1 or > 12)
            {
                return (0, 0, false);
            }

            bool pm = amPmGroup.Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            hour = (hour % 12) + (pm ? 12 : 0);
        }
        else if (hour > 23)
        {
            return (0, 0, false);
        }

        return (hour, minute, true);
    }

    private static ReminderParseResult Build(
        string text, Match match, DateTimeOffset due, string previewLabel, bool recognized)
    {
        string title = NormalizeTitle(text.Remove(match.Index, match.Length));
        return new ReminderParseResult(title, due, previewLabel, recognized);
    }

    private static string NormalizeTitle(string raw)
    {
        string collapsed = Regex.Replace(raw, @"\s+", " ").Trim().Trim(',', '-', '·').Trim();
        if (collapsed.Length == 0)
        {
            return "Reminder";
        }

        // Sentence-case the first letter for a tidy pill; leave the rest as typed.
        return char.ToUpper(collapsed[0], CultureInfo.CurrentCulture) + collapsed[1..];
    }
}
