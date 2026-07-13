namespace Porta.Agent.Common;

/// <summary>
/// Workstream 1.4 — every break nudge explains its ACTUAL trigger, built from the
/// decision's real inputs. Generic copy ("time for a break!") teaches users that the
/// nudge is a clock; specific copy teaches them it's a measurement. Pure so the exact
/// strings are unit-testable.
/// </summary>
public static class BreakReasonText
{
    /// <summary>
    /// Builds the user-facing reason line for a break suggestion.
    /// <paramref name="needGateReason"/>: "overload_active" | "pressure_above_personal" |
    /// "overload_likelihood" | "long_focus" (long breaks).
    /// </summary>
    public static string For(
        string needGateReason,
        string breakKind,
        double overloadForMinutes,
        double effectiveFocusHours)
    {
        bool longBreak = string.Equals(breakKind, "long_break", StringComparison.OrdinalIgnoreCase);
        if (longBreak || string.Equals(needGateReason, "long_focus", StringComparison.OrdinalIgnoreCase))
        {
            double hours = Math.Max(0.5, Math.Round(effectiveFocusHours * 2d) / 2d);
            string hoursLabel = hours % 1d == 0d
                ? ((int)hours).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : hours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return $"You've been at it {hoursLabel}h without a real pause.";
        }

        return needGateReason switch
        {
            "overload_active" when overloadForMinutes >= 1d
                => $"Signs of overload for the last {(int)Math.Round(overloadForMinutes)} min — a short reset helps.",
            "overload_active"
                => "Signs of overload right now — a short reset helps.",
            "pressure_above_personal"
                => "Pressure has been above your usual level for a while — worth 5 minutes away.",
            "overload_likelihood"
                => "Signs of rising overload — a short reset helps.",
            // Never empty: an unnamed trigger still gets honest, state-based copy.
            _ => "Sustained strain in your recent activity — worth a short pause."
        };
    }
}
