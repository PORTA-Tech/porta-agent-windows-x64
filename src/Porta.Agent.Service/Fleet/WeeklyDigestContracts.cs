using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// The weekly digest payload, POSTed to /api/agent/weekly-digest.
///
/// <b>These property names are a contract with the control plane</b> (weeklyDigestBuilder.ts,
/// live since e170c1d). The server renders whatever it is handed and derives almost nothing, so a
/// renamed field here is a silently missing section there.
///
/// Nothing in this payload can be reconstructed on the server: it holds no task store and no
/// project store, which is the whole reason the weekly is agent-driven.
/// </summary>
internal sealed record WeeklyDigestReportPayload(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineId")] string MachineId,
    /// <summary>The Monday goal day, "yyyy-MM-dd".</summary>
    [property: JsonPropertyName("weekStartLocal")] string WeekStartLocal,
    /// <summary>The Sunday goal day. This identifies the week and keys the server's dedup.</summary>
    [property: JsonPropertyName("weekEndLocal")] string WeekEndLocal,
    [property: JsonPropertyName("timezoneOffsetMinutes")] int TimezoneOffsetMinutes,
    [property: JsonPropertyName("reportedAtUtc")] string ReportedAtUtc,
    /// <summary>
    /// Scored through the same rule as the evening card and the daily digest — planned is every
    /// line the week touched, finished is state == "done". Sent as NUMBERS, not counted from the
    /// lists below, precisely so the weekly and the daily can never disagree about "finished".
    /// </summary>
    [property: JsonPropertyName("plannedCount")] int PlannedCount,
    [property: JsonPropertyName("finishedCount")] int FinishedCount,
    [property: JsonPropertyName("trackedSeconds")] double TrackedSeconds,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("awaySeconds")] double AwaySeconds,
    /// <summary>Seven entries, Monday first.</summary>
    [property: JsonPropertyName("days")] IReadOnlyList<WeeklyDigestDayPayload> Days,
    [property: JsonPropertyName("finished")] IReadOnlyList<WeeklyDigestFinishedPayload> Finished,
    [property: JsonPropertyName("carried")] IReadOnlyList<WeeklyDigestCarriedPayload> Carried,
    [property: JsonPropertyName("projects")] IReadOnlyList<WeeklyDigestProjectPayload> Projects,
    /// <summary>Drafted from result lines and statuses only, never from app usage. Null omits it.</summary>
    [property: JsonPropertyName("ariTake")] string? AriTake = null,
    /// <summary>tracked | focus | away | bestDay — the email prints "no data" rather than a zero.</summary>
    [property: JsonPropertyName("missingFields")] IReadOnlyList<string>? MissingFields = null);

/// <summary>
/// One of the seven days.
///
/// <b><see cref="Worked"/> is not <c>FocusSeconds &gt; 0</c>.</b> A day at the machine with no
/// focused stretch is a worked day that draws a very short bar; a day the machine was off is an
/// empty track. The strip exists so those can be told apart, so they are sent apart.
/// </summary>
internal sealed record WeeklyDigestDayPayload(
    [property: JsonPropertyName("dateLocal")] string DateLocal,
    /// <summary>The three letters the email prints. The server never guesses a locale.</summary>
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("worked")] bool Worked);

/// <summary>A task that closed inside the week. Empty project = untagged, and prints no suffix.</summary>
internal sealed record WeeklyDigestFinishedPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("seconds")] double Seconds);

/// <summary>A task still open at the end of the week. DaysOpen is the point of the block.</summary>
internal sealed record WeeklyDigestCarriedPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("daysOpen")] int DaysOpen);

/// <summary>Measured time against one project. Untagged work is never a row here.</summary>
internal sealed record WeeklyDigestProjectPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seconds")] double Seconds);
