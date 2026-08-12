using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 9a — projects. These guard the two things that would quietly corrupt a client invoice:
/// one name never becoming two projects, and a minute never belonging to two projects.
/// </summary>
public sealed class ProjectsStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewTasksAreUntagged_AndUntaggedIsNotAnError()
    {
        RunWithStore(store =>
        {
            AgentTask? task = store.CreateTask("Pricing page", 3600, Now);

            Assert.NotNull(task);
            Assert.Equal(0, task!.ProjectId);
            Assert.Equal(string.Empty, task.ProjectName);
        });
    }

    // THE invoice defect, in its most likely disguise: the same client typed with different
    // capitals on two different mornings. Two rows would halve every total that names them.
    [Theory]
    [InlineData("Nolan", "nolan")]
    [InlineData("Nolan", "NOLAN")]
    [InlineData("Nolan", "  Nolan  ")]
    [InlineData("Apollo Rebrand", "Apollo  Rebrand")]
    public void SameNameInAnyCasingOrSpacing_IsOneProject(string first, string second)
    {
        RunWithStore(store =>
        {
            AgentProject? a = store.CreateProject(first, Now);
            AgentProject? b = store.CreateProject(second, Now);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a!.Id, b!.Id);
            Assert.Single(store.ReadProjects());
        });
    }

    [Fact]
    public void CreateProject_RejectsEmptyAndClampsAbsurdLength()
    {
        RunWithStore(store =>
        {
            Assert.Null(store.CreateProject("   ", Now));
            Assert.Null(store.CreateProject(string.Empty, Now));

            AgentProject? long1 = store.CreateProject(new string('x', 300), Now);
            Assert.NotNull(long1);
            Assert.True(long1!.Name.Length <= 60);
        });
    }

    [Fact]
    public void TaskCarriesItsProjectName_WithoutASecondRead()
    {
        RunWithStore(store =>
        {
            AgentProject? project = store.CreateProject("Nolan", Now);
            AgentTask? task = store.CreateTask("Pricing page", 3600, Now, project!.Id);

            Assert.Equal(project.Id, task!.ProjectId);
            Assert.Equal("Nolan", task.ProjectName);

            IReadOnlyList<AgentTask> read = store.ReadTasks(Now.AddHours(-1), Now);
            Assert.Equal("Nolan", read.Single().ProjectName);
        });
    }

    // Yadin's decision (2026-08-12): re-tagging is allowed at any time. What must hold is that the
    // measured time moves WHOLE — all of it or none — so a minute still belongs to one project.
    [Fact]
    public void Retagging_MovesEveryMeasuredMinute_NeverSplitsThem()
    {
        RunWithStore(store =>
        {
            AgentProject? nolan = store.CreateProject("Nolan", Now);
            AgentProject? apollo = store.CreateProject("Apollo", Now);
            AgentTask? task = store.CreateTask("Pricing page", 3600, Now, nolan!.Id);
            store.ActOnTask(task!.Id, "start", Now);

            AgentTask? moved = store.SetTaskProject(task.Id, apollo!.Id);

            Assert.Equal(apollo.Id, moved!.ProjectId);
            Assert.Equal("Apollo", moved.ProjectName);
            // One task, one project. There is no second row holding the earlier share.
            Assert.Single(store.ReadTasks(Now.AddHours(-1), Now));
        });
    }

    [Fact]
    public void ClearingTheTag_IsAlwaysReachable()
    {
        RunWithStore(store =>
        {
            AgentProject? nolan = store.CreateProject("Nolan", Now);
            AgentTask? task = store.CreateTask("Pricing page", 3600, Now, nolan!.Id);

            AgentTask? cleared = store.SetTaskProject(task!.Id, 0);

            Assert.Equal(0, cleared!.ProjectId);
            Assert.Equal(string.Empty, cleared.ProjectName);
        });
    }

    // A stale id from a card left open across an archive must not invent a tag.
    [Fact]
    public void AnUnknownOrArchivedProjectId_StoresAsUntagged_NotAsADanglingPointer()
    {
        RunWithStore(store =>
        {
            AgentTask? unknown = store.CreateTask("Invoice chase", 1800, Now, projectId: 4242);
            Assert.Equal(0, unknown!.ProjectId);

            AgentProject? gone = store.CreateProject("Gone", Now);
            store.SetProjectArchived(gone!.Id, archived: true, Now);
            AgentTask? archived = store.CreateTask("Another thing", 1800, Now, gone.Id);
            Assert.Equal(0, archived!.ProjectId);
        });
    }

    [Fact]
    public void Archiving_HidesFromThePicker_ButKeepsTheTimeAttached()
    {
        RunWithStore(store =>
        {
            AgentProject? nolan = store.CreateProject("Nolan", Now);
            AgentTask? task = store.CreateTask("Pricing page", 3600, Now, nolan!.Id);

            store.SetProjectArchived(nolan.Id, archived: true, Now);

            Assert.Empty(store.ReadProjects());
            Assert.Single(store.ReadProjects(includeArchived: true));

            // The task still points at it and still reports its name: archiving is not deleting,
            // and time already reported must not lose its label.
            AgentTask still = store.ReadTasks(Now.AddHours(-1), Now).Single();
            Assert.Equal(nolan.Id, still.ProjectId);
            Assert.Equal("Nolan", still.ProjectName);
            Assert.Equal(task!.Id, still.Id);
        });
    }

    // Typing an archived project's name again is asking for it back, not a unique-index collision.
    [Fact]
    public void TypingAnArchivedNameAgain_BringsItBack_RatherThanFailing()
    {
        RunWithStore(store =>
        {
            AgentProject? nolan = store.CreateProject("Nolan", Now);
            store.SetProjectArchived(nolan!.Id, archived: true, Now);

            AgentProject? again = store.CreateProject("nolan", Now);

            Assert.NotNull(again);
            Assert.Equal(nolan.Id, again!.Id);
            Assert.False(again.Archived);
            Assert.Single(store.ReadProjects(includeArchived: true));
        });
    }

    [Fact]
    public void Rename_RefusesANameAnotherProjectAlreadyHas()
    {
        RunWithStore(store =>
        {
            AgentProject? nolan = store.CreateProject("Nolan", Now);
            AgentProject? apollo = store.CreateProject("Apollo", Now);

            // Refused, because merging two clients' totals is not a rename.
            Assert.Null(store.RenameProject(apollo!.Id, "nolan"));
            // Renaming to its own name in different casing is fine.
            Assert.NotNull(store.RenameProject(nolan!.Id, "NOLAN"));
            Assert.Equal("NOLAN", store.ReadProjects().Single(p => p.Id == nolan.Id).Name);
        });
    }

    // The picker shows six; most-recently-USED first is what makes six enough.
    [Fact]
    public void ReadProjects_OrdersByMostRecentlyUsed()
    {
        RunWithStore(store =>
        {
            AgentProject? oldest = store.CreateProject("Oldest", Now);
            AgentProject? middle = store.CreateProject("Middle", Now.AddMinutes(1));
            AgentProject? newest = store.CreateProject("Newest", Now.AddMinutes(2));

            // Using the oldest project floats it to the top, ahead of two newer projects.
            store.CreateTask("Something", 3600, Now.AddHours(5), oldest!.Id);

            IReadOnlyList<AgentProject> ordered = store.ReadProjects();
            Assert.Equal(oldest.Id, ordered[0].Id);
            Assert.Equal(newest!.Id, ordered[1].Id);
            Assert.Equal(middle!.Id, ordered[2].Id);
        });
    }

    private static void RunWithStore(Action<SqliteBrainStateStore> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-project-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
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
