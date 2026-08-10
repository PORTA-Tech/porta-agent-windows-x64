namespace Porta.UserAgent.Tests;

/// <summary>
/// Every check-in card test class drives the same STATIC seam,
/// <c>UserCheckInCardWindow.SuppressForegroundAcquisition</c>. xUnit runs test classes in parallel
/// by default, so one class's cleanup could flip the flag back to false while another was mid-test
/// — the second card would then really try for the foreground, and an assertion about the refusal
/// hint would see a card that was never refused.
///
/// Found 2026-08-10: the step 8 hint test passed alone and failed in the full run. Putting these
/// classes in one collection serialises them, which is the fix for the whole family rather than
/// just the test that happened to expose it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CheckInCardCollection
{
    public const string Name = "check-in card (shared static foreground seam)";
}
