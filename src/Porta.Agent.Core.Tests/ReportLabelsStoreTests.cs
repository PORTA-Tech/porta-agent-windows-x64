using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase A — client-facing labels, the project's client-facing name, and the user-side
/// rate. Everything here is LOCAL: phase A adds no field to any payload, which is what makes it
/// shippable on its own.
///
/// These guard the promises in <c>Step10_Report_Spec.md</c> §3, §7 and §12.7 — most of all the one
/// that is easiest to break by accident, which is a setting silently overwriting words the user
/// wrote for a paying client.
/// </summary>
public sealed class ReportLabelsStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATaskStartsWithNoClientLabel_AndThatIsNotAnError()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("finish my writting part \"all the way in\"", 3600, Now)!;

            Assert.Equal(string.Empty, task.ClientLabel);
        });
    }

    /// <summary>
    /// The whole point of §3: the private note survives, and the client sees something else.
    /// </summary>
    [Fact]
    public void LabellingATask_LeavesItsRawTitleExactlyAsTyped()
    {
        RunWithStore(store =>
        {
            const string RawTitle = "finish my writting part \"all the way in\"";
            AgentTask task = store.CreateTask(RawTitle, 3600, Now)!;

            AgentTask? labelled = store.SetTaskClientLabel(task.Id, "Chapter three — first draft");

            Assert.NotNull(labelled);
            Assert.Equal("Chapter three — first draft", labelled!.ClientLabel);
            Assert.Equal(RawTitle, labelled.Title);
        });
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Yadin, 2026-08-13: a label written for a client is never overwritten
    /// by the project default changing later. There is deliberately no code path from the default
    /// to a task's label, and this fails if one is ever added.
    /// </summary>
    [Fact]
    public void ALabelSurvivesTheProjectDefaultChanging_InBothDirections()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Nolan", Now)!;
            AgentTask task = store.CreateTask("working on appolo leads", 3600, Now)!;
            store.SetTaskProject(task.Id, project.Id);
            store.SetTaskClientLabel(task.Id, "Lead research");

            store.SetProjectLabelStyle(project.Id, useClientLabels: true);
            Assert.Equal("Lead research", ReadTask(store, task.Id).ClientLabel);

            store.SetProjectLabelStyle(project.Id, useClientLabels: false);
            Assert.Equal("Lead research", ReadTask(store, task.Id).ClientLabel);
        });
    }

    /// <summary>
    /// Re-tagging is allowed at any time and moves every measured minute whole. The label hangs off
    /// the task, so it travels with it — the alternative, labels stored per project, would silently
    /// rename a line when its project changed.
    /// </summary>
    [Fact]
    public void ALabelTravelsWithItsTask_WhenTheTaskIsRetagged()
    {
        RunWithStore(store =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Now)!;
            AgentProject apollo = store.CreateProject("Apollo", Now)!;
            AgentTask task = store.CreateTask("continue to improve the system", 3600, Now)!;
            store.SetTaskProject(task.Id, nolan.Id);
            store.SetTaskClientLabel(task.Id, "Structural edit, part one");

            store.SetTaskProject(task.Id, apollo.Id);

            AgentTask moved = ReadTask(store, task.Id);
            Assert.Equal(apollo.Id, moved.ProjectId);
            Assert.Equal("Structural edit, part one", moved.ClientLabel);
        });
    }

    /// <summary>
    /// "Not named yet" and "named, and I want that undone" are the same state. A label that could
    /// not be removed would be a worse trap than one that has to be typed twice.
    /// </summary>
    [Fact]
    public void AnEmptyLabelClearsIt()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("Pricing page", 3600, Now)!;
            store.SetTaskClientLabel(task.Id, "Pricing page copy");

            AgentTask? cleared = store.SetTaskClientLabel(task.Id, "   ");

            Assert.NotNull(cleared);
            Assert.Equal(string.Empty, cleared!.ClientLabel);
        });
    }

    [Fact]
    public void LabellingATaskThatDoesNotExist_ReturnsNullRatherThanPretending()
    {
        RunWithStore(store => Assert.Null(store.SetTaskClientLabel(9999, "Anything")));
    }

    [Fact]
    public void AClientLabelIsTrimmedCollapsedAndClamped()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("Pricing page", 3600, Now)!;

            Assert.Equal(
                "Chapter three first draft",
                store.SetTaskClientLabel(task.Id, "  Chapter   three\tfirst  draft  ")!.ClientLabel);

            string absurd = new('x', 400);
            Assert.Equal(120, store.SetTaskClientLabel(task.Id, absurd)!.ClientLabel.Length);
        });
    }

    // ── the project's own client-facing name (§12.7) ────────────────────────────────────────────

    [Fact]
    public void AProjectStartsWithNoClientName_AndKeepsItsLocalNameWhenGivenOne()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("nolan - chase invoice", Now)!;
            Assert.Equal(string.Empty, project.ClientName);

            AgentProject? named = store.SetProjectClientName(project.Id, "Nolan Studio");

            Assert.NotNull(named);
            Assert.Equal("Nolan Studio", named!.ClientName);
            Assert.Equal("nolan - chase invoice", named.Name);
        });
    }

    /// <summary>
    /// The unique index on <c>name</c> exists so one client cannot become two projects and halve a
    /// total. The client-facing NAME carries no such index on purpose: two engagements for the same
    /// client are two projects, reported separately, and nothing is split.
    /// </summary>
    [Fact]
    public void TwoProjectsMayShareOneClientFacingName()
    {
        RunWithStore(store =>
        {
            AgentProject first = store.CreateProject("Nolan book", Now)!;
            AgentProject second = store.CreateProject("Nolan site", Now)!;

            Assert.NotNull(store.SetProjectClientName(first.Id, "Nolan Studio"));
            Assert.NotNull(store.SetProjectClientName(second.Id, "Nolan Studio"));

            Assert.Equal(2, store.ReadProjects().Count);
        });
    }

    [Fact]
    public void TheDefaultIsRawTitles_AndItPersists()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Nolan", Now)!;
            Assert.False(project.UseClientLabels);

            Assert.True(store.SetProjectLabelStyle(project.Id, useClientLabels: true)!.UseClientLabels);
            Assert.True(store.ReadProjects().Single(p => p.Id == project.Id).UseClientLabels);
        });
    }

    // ── the rate (§7) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AProjectHasNoRateUntilOneIsSet_AndItRoundTripsInMinorUnits()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Nolan", Now)!;
            Assert.Equal(0, project.RateMinor);
            Assert.Equal(string.Empty, project.RateCurrency);

            AgentProject? rated = store.SetProjectRate(project.Id, 12550, "USD");

            Assert.NotNull(rated);
            Assert.Equal(12550, rated!.RateMinor);
            Assert.Equal("USD", rated.RateCurrency);
        });
    }

    /// <summary>
    /// A negative rate can only be a typo, and the one number this product must never get wrong is
    /// the one that looks like money.
    /// </summary>
    [Fact]
    public void ANegativeRateIsRefused_AndLeavesTheStoredOneAlone()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Nolan", Now)!;
            store.SetProjectRate(project.Id, 12550, "USD");

            Assert.Null(store.SetProjectRate(project.Id, -1, "USD"));
            Assert.Equal(12550, store.ReadProjects().Single(p => p.Id == project.Id).RateMinor);
        });
    }

    [Fact]
    public void ARateOfZeroClearsIt()
    {
        RunWithStore(store =>
        {
            AgentProject project = store.CreateProject("Nolan", Now)!;
            store.SetProjectRate(project.Id, 12550, "USD");

            AgentProject? cleared = store.SetProjectRate(project.Id, 0, string.Empty);

            Assert.NotNull(cleared);
            Assert.Equal(0, cleared!.RateMinor);
            Assert.Equal(string.Empty, cleared.RateCurrency);
        });
    }

    // ── the migration itself ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// v28 is additive and defaulted, so a database written by 1.2.149 keeps working untouched.
    /// Every column is NOT NULL with a default for the same reason project_id and
    /// deferred_until_day are: no caller anywhere has to handle a null.
    /// </summary>
    [Fact]
    public void V28AddsFiveColumns_AllNotNullAndDefaulted()
    {
        RunWithDatabase((store, dbPath) =>
        {
            // Touching the store is what runs the migration.
            store.CreateProject("Nolan", Now);

            Assert.Equal(
                ("TEXT", true, "''"),
                ColumnShape(dbPath, "tasks", "client_label"));
            Assert.Equal(
                ("TEXT", true, "''"),
                ColumnShape(dbPath, "projects", "client_name"));
            Assert.Equal(
                ("INTEGER", true, "0"),
                ColumnShape(dbPath, "projects", "label_style"));
            Assert.Equal(
                ("INTEGER", true, "0"),
                ColumnShape(dbPath, "projects", "rate_minor"));
            Assert.Equal(
                ("TEXT", true, "''"),
                ColumnShape(dbPath, "projects", "rate_currency"));
        });
    }

    private static (string Type, bool NotNull, string Default) ColumnShape(
        string dbPath, string table, string column)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('{table}');";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(0), column, StringComparison.OrdinalIgnoreCase))
            {
                return (reader.GetString(1), reader.GetInt32(2) == 1, reader.GetString(3));
            }
        }

        throw new InvalidOperationException($"{table}.{column} does not exist.");
    }

    private static AgentTask ReadTask(SqliteBrainStateStore store, long id)
        => store.ReadTasks(Now.AddHours(-1), Now).Single(t => t.Id == id);

    private static void RunWithStore(Action<SqliteBrainStateStore> body)
        => RunWithDatabase((store, _) => body(store));

    private static void RunWithDatabase(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-report-label-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
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
