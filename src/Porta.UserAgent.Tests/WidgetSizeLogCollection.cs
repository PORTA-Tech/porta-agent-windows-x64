namespace Porta.UserAgent.Tests;

/// <summary>
/// The widget-window test classes drive the same STATIC seam, <c>WidgetSizeLog.PathOverride</c>.
/// Each one points it at its own temp file, asserts on what was written, and restores it — so when
/// xUnit runs two of these classes in parallel, one class's override is live while the other is
/// mid-scenario, and the evidence line the second is asserting on lands in the first one's file.
/// The assertion then sees an empty log and fails.
///
/// <para>Found 2026-08-12, by the new CI test gate on its first run:
/// <c>Heal_CollapsedDesyncWithSummaryVisible_ForcesRealCollapse</c> failed in a full run, then
/// passed alone and passed in two further full runs. The defect is pre-existing — nothing in that
/// day's work touches WidgetSizeLog — but adding test classes to the assembly increases the number
/// of parallel collections, and therefore how often the race is lost.</para>
///
/// <para>Same shape and same fix as <see cref="CheckInCardCollection"/>: serialise the family that
/// shares the seam, rather than patching the one test that happened to expose it. A flaky gate is
/// worse than no gate — it teaches you to ignore a red tick, which is exactly what the gate exists
/// to prevent.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WidgetSizeLogCollection
{
    public const string Name = "widget window (shared static WidgetSizeLog.PathOverride seam)";
}
