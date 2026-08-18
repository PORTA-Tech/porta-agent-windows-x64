using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 3.2 (2026-08-18) — the engagement fields behind the Projects page.
//
// These columns were added by the v30 migration and then had no reader and no setter, so the
// report could describe a date range but never say where that range sat inside the job. These
// tests pin the round-trip, the refusals, and the one rule that matters most on the page: progress
// is a COUNT and the store never offers a percentage for anyone to bind to.
public sealed class ProjectEngagementTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 18, 09, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AProjectStartsActive_WithNothingElseClaimed()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            // Active is the only safe default: a project you just made is one you are doing.
            Assert.Equal("active", project.Status);
            // Everything else is a statement the user has not made yet, so it says nothing.
            Assert.Equal(string.Empty, project.StartDay);
            Assert.Equal(string.Empty, project.CompletionDay);
            Assert.Equal("none", project.ReportingFrequency);
            Assert.Empty(project.Recipients ?? Array.Empty<string>());
        });
    }

    [Fact]
    public void TheEngagementFieldsRoundTrip()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            AgentProject? saved = store.SetProjectEngagement(
                project.Id,
                status: "paused",
                startDay: "2026-08-01",
                completionDay: "2026-09-30",
                recipients: new[] { "client@example.com", "cc@example.com" },
                reportingFrequency: "fortnightly");

            Assert.NotNull(saved);
            Assert.Equal("paused", saved!.Status);
            Assert.Equal("2026-08-01", saved.StartDay);
            Assert.Equal("2026-09-30", saved.CompletionDay);
            Assert.Equal("fortnightly", saved.ReportingFrequency);
            Assert.Equal(new[] { "client@example.com", "cc@example.com" }, saved.Recipients);

            // And it survives a re-read, rather than only living in the returned object.
            AgentProject reread = store.ReadProjects(includeArchived: true).Single(p => p.Id == project.Id);
            Assert.Equal("paused", reread.Status);
            Assert.Equal(new[] { "client@example.com", "cc@example.com" }, reread.Recipients);
        });
    }

    [Fact]
    public void SavingOneFieldLeavesTheOthersExactlyAsTheyWere()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;
            store.SetProjectEngagement(
                project.Id, "active", "2026-08-01", "", new[] { "client@example.com" }, "weekly");

            // The page saves the field that changed. A status change must not blank a date, and it
            // must not clear a recipient list somebody typed.
            AgentProject? afterStatus = store.SetProjectEngagement(
                project.Id, status: "complete",
                startDay: null, completionDay: null, recipients: null, reportingFrequency: null);

            Assert.Equal("complete", afterStatus!.Status);
            Assert.Equal("2026-08-01", afterStatus.StartDay);
            Assert.Equal("weekly", afterStatus.ReportingFrequency);
            Assert.Equal(new[] { "client@example.com" }, afterStatus.Recipients);
        });
    }

    [Fact]
    public void CompletionAndStatus_AreNeverInferredFromEachOther()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            // Marking it complete must not invent a completion date to print to a client.
            AgentProject? complete = store.SetProjectEngagement(project.Id, status: "complete");
            Assert.Equal("complete", complete!.Status);
            Assert.Equal(string.Empty, complete.CompletionDay);

            // And typing a date must not silently declare the engagement finished.
            AgentProject? dated = store.SetProjectEngagement(project.Id, status: "active");
            dated = store.SetProjectEngagement(dated!.Id, completionDay: "2026-09-30");
            Assert.Equal("active", dated!.Status);
            Assert.Equal("2026-09-30", dated.CompletionDay);
        });
    }

    [Fact]
    public void AnUnknownStatusOrFrequencyIsRefused_RatherThanStored()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            // An unrecognised value would group wrongly in a client's report instead of failing
            // visibly here, so the store refuses it and the page says nothing was saved.
            Assert.Null(store.SetProjectEngagement(project.Id, status: "archived"));
            Assert.Null(store.SetProjectEngagement(project.Id, reportingFrequency: "daily"));

            Assert.Equal("active", store.ReadProjects(true).Single(p => p.Id == project.Id).Status);
        });
    }

    [Fact]
    public void AHalfTypedDateIsStoredAsNothing_NeverAsItself()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            AgentProject? saved = store.SetProjectEngagement(project.Id, startDay: "2026-8");
            // A date printed to a paying client is either real or absent.
            Assert.Equal(string.Empty, saved!.StartDay);

            saved = store.SetProjectEngagement(project.Id, startDay: "not a date");
            Assert.Equal(string.Empty, saved!.StartDay);
        });
    }

    [Fact]
    public void RecipientsAreDeduplicatedAndTrimmed_AndCanBeCleared()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            AgentProject? saved = store.SetProjectEngagement(
                project.Id,
                recipients: new[] { "  client@example.com ", "CLIENT@example.com", "", "cc@example.com" });

            Assert.Equal(new[] { "client@example.com", "cc@example.com" }, saved!.Recipients);

            // Clearing must be possible: removing the last address is how a person says this one
            // is not going to anybody yet.
            saved = store.SetProjectEngagement(project.Id, recipients: Array.Empty<string>());
            Assert.Empty(saved!.Recipients ?? Array.Empty<string>());
        });
    }

    [Fact]
    public void ProgressIsACount_AndTheStoreOffersNoPercentage()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            AgentTask a = store.CreateTask("one", 3600, Now)!;
            AgentTask b = store.CreateTask("two", 3600, Now)!;
            AgentTask c = store.CreateTask("three", 3600, Now)!;
            foreach (AgentTask t in new[] { a, b, c })
            {
                store.SetTaskProject(t.Id, project.Id);
            }

            store.SetTaskStatus(a.Id, "done", string.Empty);
            store.SetTaskStatus(b.Id, "blocked", "waiting on the client");

            AgentProjectStats stats = store.ReadProjectStats(project.Id);

            Assert.Equal(3, stats.TotalTasks);
            Assert.Equal(1, stats.DoneTasks);
            Assert.Equal(1, stats.BlockedTasks);

            // The page prints "1 of 3 tasks complete". There is deliberately no percentage on this
            // record for anyone to bind to: tasks are not equal in size and a percentage claims
            // they are.
            Assert.DoesNotContain(
                typeof(AgentProjectStats).GetProperties(),
                p => p.Name.Contains("Percent", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void StatsForAProjectWithNothingInIt_AreZeroRatherThanAnError()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Empty", Now)!;
            AgentProjectStats stats = store.ReadProjectStats(project.Id);

            Assert.Equal(0, stats.TotalTasks);
            Assert.Equal(0d, stats.Hours);

            // And a project id that does not exist is still an answer, not a throw: the page reads
            // this while the list is being rebuilt.
            Assert.Equal(0, store.ReadProjectStats(999_999).TotalTasks);
        });
    }

    private static void RunWithStore(Action<SqliteBrainStateStore> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-engagement-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: Path.Combine(dir, "core-state.db"));
            body(store);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
