using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Phase 3 rebuilt (2026-08-18) — derived and asked.
///
/// A derived field is worked out from measured work and can be overridden. An asked field is one
/// only the user can answer and Ari never invents. The rule that matters most is the override:
/// once a value is stored it wins permanently and derivation stops, and nothing anywhere may
/// rewrite it in the background.
/// </summary>
public sealed class DerivedProjectFieldsTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 18, 09, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewProjectDerivesActive_AndSaysWhy()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;
            AgentProjectCard card = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);

            Assert.Equal("active", card.Status);
            Assert.True(card.StatusIsDerived);
            Assert.Equal("No recorded work yet.", card.StatusReason);

            // Derived means computed on read, NOT written. The stored column stays empty, which is
            // what keeps a later override distinguishable from a value Ari worked out.
            Assert.Equal(string.Empty, card.Project.Status);
        });
    }

    [Fact]
    public void AStoredStatusWins_AndStopsDerivationPermanently()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            store.SetProjectEngagement(project.Id, status: "complete");
            AgentProjectCard card = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);

            Assert.Equal("complete", card.Status);
            Assert.False(card.StatusIsDerived);
            // No reason shown: the user said it, so there is nothing to explain.
            Assert.Equal(string.Empty, card.StatusReason);

            // And re-reading never quietly reverts it. This is the property a background job would
            // destroy, which is why there is no such job.
            Assert.Equal("complete", store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id).Status);
        });
    }

    [Fact]
    public void FrequencyDerivesWeekly_UntilItIsGiven()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;

            AgentProjectCard derived = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);
            Assert.Equal("week", derived.ReportingFrequency);
            Assert.True(derived.ReportingFrequencyIsDerived);

            store.SetProjectEngagement(project.Id, reportingFrequency: "month");
            AgentProjectCard given = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);
            Assert.Equal("month", given.ReportingFrequency);
            Assert.False(given.ReportingFrequencyIsDerived);
        });
    }

    [Fact]
    public void TheAskedFieldsAreNeverInvented()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;
            AgentProjectCard card = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);

            // The client-facing name and the completion date are the two things on a project that
            // only the user can say. Ari must never fill either, however much it knows.
            Assert.Equal(string.Empty, card.Project.ClientName);
            Assert.Equal(string.Empty, card.Project.CompletionDay);

            // And a derived status must never be "complete": finished is a claim, not a measurement.
            Assert.NotEqual("complete", card.Status);
        });
    }

    [Fact]
    public void ProgressIsACount_AndNoPercentageExistsAnywhereInTheStack()
    {
        // Yadin's rule: tasks are not equal in size, so a percentage is a claim the data cannot
        // support. Asserted against the types themselves so nobody can add one back by accident.
        foreach (Type t in new[] { typeof(AgentProjectCard), typeof(AgentProjectStats) })
        {
            Assert.DoesNotContain(
                t.GetProperties(),
                p => p.Name.Contains("Percent", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Pct", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void RecipientsDeriveFromNothing_WhenNothingWasEverSent()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Kestrel", Now)!;
            AgentProjectCard card = store.ReadProjectCards(true).Single(c => c.Project.Id == project.Id);

            // Empty is the honest answer. It is not an error and the page must not treat it as one.
            Assert.Empty(card.Recipients);
            Assert.True(card.RecipientsAreDerived);
        });
    }

    private static void RunWithStore(Action<SqliteBrainStateStore> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-derived-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            body(new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: Path.Combine(dir, "core-state.db")));
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
