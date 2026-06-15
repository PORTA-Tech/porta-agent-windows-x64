namespace Porta.Agent.Common.Analysis;

/// <summary>
/// Canonical, single source of truth for attention-segment kind strings.
///
/// Both the producer (the agent's DerivedSessionMetricsComputer, which writes
/// <c>attention_segment_kind</c> into <c>state_buckets.state_flags_json</c>) and every reader
/// (LocalApi's LocalAttentionSegmentAnalytics, the Control Center history, the desktop widget)
/// must agree on this list. Historically they drifted: the agent added category/Phase-3
/// distraction kinds (social media, passive consumption, comm monitoring, gaming, …) that the
/// read side never registered, so they silently collapsed to "focus" and vanished from the
/// Control Center. Defining the kinds in ONE place — referenced by Core, LocalApi and the
/// Control Center alike — makes that class of drift impossible by construction.
///
/// Privacy-safe: these are coarse behavioural labels only; no raw app/window content.
/// </summary>
public static class AttentionSegmentKinds
{
    // ── Non-distraction states ──────────────────────────────────────────────
    public const string Focus            = "focus";
    public const string Away             = "away";
    public const string ContextExcursion = "context_excursion";

    // ── Distraction kinds: original ("Block F") set ─────────────────────────
    public const string OffTaskDistraction        = "off_task_distraction";
    public const string FragmentationDistraction  = "fragmentation_distraction";
    public const string RollingMicroBurst         = "rolling_micro_burst";
    public const string ReturnLoopDistraction     = "return_loop_distraction";
    public const string UnfocusedSession          = "unfocused_session";
    public const string ShallowDwellDistraction   = "shallow_dwell_distraction";
    public const string FocusDepartureDistraction = "focus_departure_distraction";

    // ── Distraction kinds: category / Phase-3 set ───────────────────────────
    public const string SocialMediaDistraction    = "social_media_distraction";
    public const string SocialMediaLoop           = "social_media_loop";
    public const string GamingLauncherDistraction = "gaming_launcher_distraction";
    public const string ActiveGamingDistraction   = "active_gaming_distraction";
    public const string PassiveConsumption        = "passive_consumption";
    public const string CommMonitoringLoop        = "comm_monitoring_loop";
    public const string FocusCategoryAbsent       = "focus_category_absent";
    public const string BadSessionStart           = "bad_session_start";

    /// <summary>Every distraction kind the agent can emit.</summary>
    public static readonly IReadOnlyList<string> AllDistractionKinds = new[]
    {
        OffTaskDistraction,
        FragmentationDistraction,
        RollingMicroBurst,
        ReturnLoopDistraction,
        UnfocusedSession,
        ShallowDwellDistraction,
        FocusDepartureDistraction,
        SocialMediaDistraction,
        SocialMediaLoop,
        GamingLauncherDistraction,
        ActiveGamingDistraction,
        PassiveConsumption,
        CommMonitoringLoop,
        FocusCategoryAbsent,
        BadSessionStart,
    };

    /// <summary>Every attention-segment kind the agent can emit (distraction + focus/away/excursion).</summary>
    public static readonly IReadOnlyList<string> AllKinds =
        new[] { Focus, Away, ContextExcursion }.Concat(AllDistractionKinds).ToArray();

    private static readonly HashSet<string> DistractionSet = new(AllDistractionKinds, StringComparer.Ordinal);
    private static readonly HashSet<string> KnownSet = new(AllKinds, StringComparer.Ordinal);

    /// <summary>Trim/lower-case a raw value to its canonical form, or "focus" if unrecognized.</summary>
    public static string NormalizeKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Focus;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        return KnownSet.Contains(trimmed) ? trimmed : Focus;
    }

    /// <summary>True when the kind is any genuine distraction episode.</summary>
    public static bool IsDistraction(string? kind) =>
        kind is not null && DistractionSet.Contains(kind.Trim().ToLowerInvariant());

    public static bool IsAway(string? kind) =>
        string.Equals(NormalizeKind(kind), Away, StringComparison.Ordinal);

    public static bool IsContextExcursion(string? kind) =>
        string.Equals(NormalizeKind(kind), ContextExcursion, StringComparison.Ordinal);

    /// <summary>True only for steady focus (i.e. not distraction, away, or context excursion).</summary>
    public static bool IsFocus(string? kind) =>
        string.Equals(NormalizeKind(kind), Focus, StringComparison.Ordinal);

    /// <summary>True when the kind is one the read/display side knows how to render.</summary>
    public static bool IsKnown(string? kind) =>
        kind is not null && KnownSet.Contains(kind.Trim().ToLowerInvariant());
}
