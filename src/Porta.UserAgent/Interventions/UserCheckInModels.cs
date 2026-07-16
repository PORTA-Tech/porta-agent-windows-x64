namespace Porta.UserAgent.Interventions;

// Workstream D — conversational check-ins. Three surfaces render in the compact bottom-right
// corner card (C2 channel): morning goal, midday progress, end-of-day wrap.
internal enum UserCheckInKind
{
    Morning,
    Midday,
    EndOfDay
}

// One frame of a check-in the card should render. Fields not relevant to a kind are empty.
internal sealed record UserCheckInPrompt(
    UserCheckInKind Kind,
    string Title,
    string Message,
    string GoalText = "",       // midday/EOD: the goal the user stated in the morning
    string ReportLine = "");    // midday: the measured report (top apps + focused/off-track/away)

// The user's answer. TextReply carries the morning goal or the EOD plan; Choice carries the
// morning tracking preference (remind_me|keep_on_track|just_track) or the midday answer
// (yes|partly|not_yet). Dismissed = closed without answering.
internal sealed record UserCheckInResponse(
    UserCheckInKind Kind,
    string? TextReply,
    string? Choice,
    bool Dismissed)
{
    public static UserCheckInResponse DismissedFor(UserCheckInKind kind)
        => new(kind, TextReply: null, Choice: null, Dismissed: true);
}

// The morning tracking-preference chips and the midday answer chips — the locked copy/values.
internal static class UserCheckInChoices
{
    public static readonly (string Value, string Label)[] MorningTracking =
    {
        ("remind_me", "Remind me"),
        ("keep_on_track", "Keep me on track"),
        ("just_track", "Just track"),
    };

    public static readonly (string Value, string Label)[] MiddayAnswers =
    {
        ("yes", "Yes"),
        ("partly", "Partly"),
        ("not_yet", "Not yet"),
    };
}
