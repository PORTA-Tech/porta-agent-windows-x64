namespace Porta.Agent.Core.Analysis;

/// <summary>
/// Workstream 1 — the personal pressure baseline. p80 of the user's OWN pressure readings
/// (active buckets, last 7 days). "Elevated" means elevated relative to this user's normal,
/// never an absolute constant: post-1a a normally-working person sits above the old fixed
/// 0.50 most of the day, which is exactly why breaks felt like a clock nag.
/// </summary>
internal readonly record struct PersonalPressureBaselineResult(
    double P80,
    int SampleCount,
    int DaysOfData)
{
    /// <summary>
    /// The baseline only drives the gate once there is enough of the user's own data
    /// (locked decision: ≥ 3 days). Below that the absolute-constant cold-start path runs.
    /// </summary>
    public bool Ready =>
        DaysOfData >= PortaExecutionConstants.BreakPolicy.PersonalBaselineMinDays
        && SampleCount >= PortaExecutionConstants.BreakPolicy.PersonalBaselineMinSamples
        && double.IsFinite(P80)
        && P80 > 0d;
}

/// <summary>
/// What the break policy needs to personalize one decision. Built by the store from
/// recorded data; consumed by <see cref="BreakSuggestionPolicy"/> as a pure input.
/// </summary>
internal readonly record struct BreakPersonalizationSnapshot(
    // The user's own p80 when the baseline is ready; null → cold start (absolute constant).
    double? PersonalPressureThreshold,
    // Mean pressure over the last 10 min dropped >5% below the 10 min before it — the user
    // is settling in; a short break would interrupt, not relieve.
    bool PressureDeclining,
    // Diagnostics: "personal_p80" | "cold_start_absolute" — recorded into the decision snapshot.
    string NeedGateMode)
{
    public static BreakPersonalizationSnapshot ColdStart { get; } = new(
        PersonalPressureThreshold: null,
        PressureDeclining: false,
        NeedGateMode: "cold_start_absolute");
}

internal static class PersonalPressureBaseline
{
    /// <summary>Nearest-rank p80 over the readings. Pure; caller supplies the sample.</summary>
    public static PersonalPressureBaselineResult Compute(IReadOnlyList<double> readings, int daysOfData)
    {
        if (readings is null || readings.Count == 0)
        {
            return new PersonalPressureBaselineResult(0d, 0, Math.Max(0, daysOfData));
        }

        var sorted = new List<double>(readings.Count);
        foreach (double value in readings)
        {
            if (double.IsFinite(value))
            {
                sorted.Add(Math.Clamp(value, 0d, 1d));
            }
        }

        if (sorted.Count == 0)
        {
            return new PersonalPressureBaselineResult(0d, 0, Math.Max(0, daysOfData));
        }

        sorted.Sort();
        int percentile = PortaExecutionConstants.BreakPolicy.PersonalBaselinePercentile;
        int rank = (int)Math.Ceiling(percentile / 100d * sorted.Count);
        double p80 = sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
        return new PersonalPressureBaselineResult(p80, sorted.Count, Math.Max(0, daysOfData));
    }
}

/// <summary>
/// Workstream 1 — acceptance-aware backoff. Every dismissed/skipped break doubles the
/// cooldown (30m → 1h → 2h → cap 4h); an accepted break, a self-taken long rest, or a new
/// morning resets it. A user who keeps refusing breaks gets asked less and less — the
/// system respects the answer instead of repeating the question.
/// </summary>
internal static class BreakBackoffPolicy
{
    public static int EffectiveCooldownSeconds(int dismissStreak, int baseCooldownSeconds)
    {
        int streak = Math.Clamp(dismissStreak, 0, 16);
        long cooldown = Math.Max(1, baseCooldownSeconds);
        for (int i = 0; i < streak && cooldown < PortaExecutionConstants.BreakPolicy.BackoffCooldownCapSeconds; i++)
        {
            cooldown *= 2;
        }

        return (int)Math.Min(cooldown, PortaExecutionConstants.BreakPolicy.BackoffCooldownCapSeconds);
    }

    /// <summary>
    /// The "backoff day" used for the morning reset: days roll over at 5:00 local, so
    /// 23:50 and 00:30 belong to the same working day, and the first bucket after 5:00
    /// starts a fresh one.
    /// </summary>
    public static string ResolveBackoffDay(DateTimeOffset localTime)
        => localTime.AddHours(-5).ToString("yyyy-MM-dd");
}
