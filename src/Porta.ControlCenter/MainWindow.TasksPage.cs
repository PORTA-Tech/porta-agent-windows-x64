namespace Porta.ControlCenter;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Porta.Agent.LocalApi;

/// <summary>
/// Phase 3 rebuilt (2026-08-18) — the Tasks page, built to the mockup.
///
/// One list, not four, because how long a task takes and how long it lives are different things.
/// ROWS, not a table grid: a table implies every column matters equally and they do not.
///
/// The result line sits under the task name. Where it was skipped, the amber control that opens an
/// inline editor — THE most important thing on this page. Before it, a skipped line could never be
/// recovered, and that is a hole in a client's report: the report body is built from these lines.
/// </summary>
public partial class MainWindow
{
    private string _taskFilter = "today";
    private long _editingResultLineFor;

    // Chat 5 — which row has its project picker open, and the projects it offers. The list is read
    // alongside the tasks so the picker can never show a stale set, and it is empty rather than
    // absent when the read fails: a picker with nothing in it says so, where a missing picker would
    // look like the feature is gone.
    private long _assigningProjectFor;
    private IReadOnlyList<LocalApiProject> _taskPageProjects = System.Array.Empty<LocalApiProject>();

    private static readonly (string Key, string Label)[] TaskFilters =
    {
        ("today", "Today"), ("week", "This week"), ("upcoming", "Upcoming"),
        ("prog", "In progress"), ("block", "Blocked"), ("done", "Completed"), ("all", "All")
    };

    /// <summary>The one-line explanation under each filter. Says what the filter MEANS, not what it did.</summary>
    private static readonly Dictionary<string, string> FilterNotes = new()
    {
        ["today"] = "What you have touched today, plus anything still open. Decided from measured work, not from a list you keep.",
        ["week"] = "Monday to today. This is the set a weekly report is built from.",
        // Chat 4 (2026-08-24) — both of these described the OLD rule and would now be untrue. The
        // In-progress note in particular said "time recorded against them", which was the bug: the
        // tab gated on hours and so could never show a task you had just moved into it.
        ["upcoming"] = "Dated ahead of today and not started yet. Ari never starts these for you.",
        ["prog"] = "Started and not finished — either you moved it here, or time landed on it.",
        ["block"] = "Blocked is the one status a client acts on, so it is the one Ari never sets for you.",
        ["done"] = "Finished work. Amber means the one line about what came of it is still missing.",
        ["all"] = "Everything, including untagged work that can never reach a client report."
    };

    private async Task RefreshTasksPageAsync()
    {
        try
        {
            LocalApiTaskRowsResponse? response = await _apiClient.GetTaskRowsAsync().ConfigureAwait(true);

            // Chat 5 — the projects the "give it a project" picker offers. A failure here must not
            // take the page down with it: the tasks are the page, and an empty picker is a smaller
            // problem than no page at all.
            try
            {
                LocalApiProjectsResponse? projects = await _apiClient.GetProjectsAsync().ConfigureAwait(true);
                _taskPageProjects = projects?.Projects?.Where(p => !p.Archived).ToList()
                    ?? (IReadOnlyList<LocalApiProject>)System.Array.Empty<LocalApiProject>();
            }
            catch
            {
                _taskPageProjects = System.Array.Empty<LocalApiProject>();
            }

            RenderTasksPage(response?.Tasks ?? System.Array.Empty<LocalApiTaskRow>());
        }
        catch
        {
            RenderTasksPage(null);
        }
    }

    internal void RenderTasksPage(IReadOnlyList<LocalApiTaskRow>? tasks)
    {
        TasksPageHost.Children.Clear();
        TasksPageHost.Children.Add(PageKit.PageTitle("Tasks"));
        TasksPageHost.Children.Add(PageKit.Text(
            "Times, projects and status come from your recorded work. The one line about what came "
            + "of a task is the only thing Ari asks you for.",
            12.5, PageKit.Ink2, margin: new Thickness(0, 7, 0, 0), wrap: true));

        var chips = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        chips.Children.Add(PageKit.Segmented(TaskFilters, _taskFilter, key =>
        {
            _taskFilter = key;
            _ = RefreshTasksPageAsync();
        }));
        TasksPageHost.Children.Add(chips);

        if (tasks is null)
        {
            TasksPageHost.Children.Add(PageKit.Card(PageKit.Empty(
                "Can't reach the agent.", "This is not the same as having no tasks.")));
            return;
        }

        List<LocalApiTaskRow> rows = tasks.Where(MatchesFilter).ToList();
        var list = new StackPanel();

        if (rows.Count == 0)
        {
            list.Children.Add(PageKit.Empty(
                "Nothing here.", "That is a real answer, not a missing one."));
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    list.Children.Add(PageKit.Hair());
                }

                list.Children.Add(BuildTaskRow(rows[i]));
            }
        }

        TasksPageHost.Children.Add(PageKit.Card(list));
        TasksPageHost.Children.Add(PageKit.Text(
            FilterNotes[_taskFilter], 11.5, PageKit.Ink3,
            margin: new Thickness(0, 14, 0, 0), wrap: true));
    }

    // Chat 4 (2026-08-24) — the rules moved to TaskFilterRules so they could be tested. They were
    // wrong in a way only clicking revealed; see that file for what and why.
    private bool MatchesFilter(LocalApiTaskRow t)
        => TaskFilterRules.Matches(_taskFilter, t, System.DateTime.Now);

    private static bool IsAhead(params string[] days)
        => TaskFilterRules.IsAhead(System.DateTime.Now, days);

    private UIElement BuildTaskRow(LocalApiTaskRow t)
    {
        var outer = new StackPanel();
        var grid = new Grid { Margin = PageKit.RowPad };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        UIElement swatch = PageKit.Swatch(t.ColourSlot);
        Grid.SetColumn(swatch, 0);
        grid.Children.Add(swatch);

        var names = new StackPanel();
        names.Children.Add(PageKit.Text(t.Label, 14, PageKit.Ink));

        // Chat 5 (2026-08-24) — see TaskRowAffordances for what each of these is and why.
        TaskRowAffordances offers = TaskRowAffordances.For(t);

        if (offers.EditableResultLine)
        {
            names.Children.Add(PageKit.EditableLine(
                t.ResultLine,
                t.Status == "blocked" ? PageKit.Stop
                    : t.IsUntagged ? PageKit.Ink3 : PageKit.Ink2,
                () => OpenResultLineEditor(t.TaskId, t.Label),
                "Click to edit the line the client reads."));
        }
        else if (offers.PromptForMissingLine)
        {
            names.Children.Add(PageKit.Link(
                "No line about what came of this ›",
                () => OpenResultLineEditor(t.TaskId, t.Label),
                PageKit.Warn));
        }

        if (offers.OfferProject)
        {
            names.Children.Add(PageKit.Text(
                "No project, so it can never appear in a client report.",
                12.5, PageKit.Ink3, margin: new Thickness(0, 4, 0, 0), wrap: true));

            names.Children.Add(PageKit.Link(
                _assigningProjectFor == t.TaskId ? "Pick a project below ›" : "Give it a project ›",
                () => ToggleProjectAssign(t.TaskId),
                PageKit.Warn));
        }

        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        TextBlock project = PageKit.Text(
            t.IsUntagged ? "untagged" : t.ProjectName, 12.5, PageKit.Ink3);
        project.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(project, 2);
        grid.Children.Add(project);

        // Status is editable HERE too — clicking the pill cycles it through the four.
        Border tag = PageKit.StatusTag(t.Status);
        tag.HorizontalAlignment = HorizontalAlignment.Left;
        tag.VerticalAlignment = VerticalAlignment.Center;
        tag.Cursor = Cursors.Hand;
        tag.ToolTip = "Click to change. Blocked and its reason are only ever yours.";
        tag.MouseLeftButtonUp += (_, _) => _ = CycleTaskStatusAsync(t);
        Grid.SetColumn(tag, 3);
        grid.Children.Add(tag);

        TextBlock dates = PageKit.Text(
            t.DeferredUntilDay.Length > 0 && IsAhead(t.DeferredUntilDay)
                ? $"put off to {FriendlyDay(t.DeferredUntilDay)}"
                : t.Dates,
            12.5, PageKit.Ink3);
        dates.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(dates, 4);
        grid.Children.Add(dates);

        TextBlock hours = PageKit.Text(t.Hours > 0 ? $"{t.Hours:0.#}h" : "—", 12.5, PageKit.Ink2);
        hours.TextAlignment = TextAlignment.Right;
        hours.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hours, 5);
        grid.Children.Add(hours);

        outer.Children.Add(grid);

        if (_editingResultLineFor == t.TaskId)
        {
            outer.Children.Add(BuildResultLineEditor(t));
        }

        if (_assigningProjectFor == t.TaskId)
        {
            outer.Children.Add(BuildProjectAssignPanel(t));
        }

        return outer;
    }

    /// <summary>
    /// Chat 5 — the project picker behind "give it a project". Deliberately the projects that
    /// already exist and nothing else: creating one is the Projects page's job, and a text box here
    /// would be a second way to make a project that could quietly make a duplicate.
    ///
    /// No cap on how many are listed. The widget's picker truncates because a card is 380px wide;
    /// this is a full page and hiding a project the user is looking for would be the same dead end
    /// in a new place.
    /// </summary>
    private UIElement BuildProjectAssignPanel(LocalApiTaskRow t)
    {
        var panel = new StackPanel { Margin = new Thickness(48, 0, 26, 16) };
        panel.Children.Add(PageKit.FieldLabel("Which project does this belong to"));

        if (_taskPageProjects.Count == 0)
        {
            panel.Children.Add(PageKit.Text(
                "No projects yet. Make one on the Projects page and it will appear here.",
                12.5, PageKit.Ink3, margin: new Thickness(0, 4, 0, 0), wrap: true));
            return panel;
        }

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (LocalApiProject project in _taskPageProjects)
        {
            long id = project.Id;
            Button chip = PageKit.GhostButton(project.Name, () => _ = AssignProjectAsync(t.TaskId, id));
            // Room underneath as well as beside: with no cap on the list these wrap onto a second
            // row, and the default margin leaves the rows touching.
            chip.Margin = new Thickness(0, 0, 9, 9);
            chips.Children.Add(chip);
        }

        panel.Children.Add(chips);
        return panel;
    }

    private void ToggleProjectAssign(long taskId)
    {
        _assigningProjectFor = _assigningProjectFor == taskId ? 0 : taskId;
        _ = RefreshTasksPageAsync();
    }

    private async Task AssignProjectAsync(long taskId, long projectId)
    {
        await _apiClient
            .SetTaskProjectAsync(new LocalApiTaskProjectRequest(
                SchemaVer: 1, TaskId: taskId, ProjectId: projectId))
            .ConfigureAwait(true);

        _assigningProjectFor = 0;

        // Straight into the line editor: assigning a project is only ever a step towards writing
        // the line, and making the user find the row again would be the dead end in two parts.
        _editingResultLineFor = taskId;
        await RefreshTasksPageAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The inline editor behind the amber control. This is the only way a skipped result line can
    /// ever be recovered, and the report body is built from these lines — so it saves what the user
    /// types and nothing else. Ari never drafts one here.
    /// </summary>
    private UIElement BuildResultLineEditor(LocalApiTaskRow t)
    {
        var panel = new StackPanel { Margin = new Thickness(48, 0, 26, 16) };
        panel.Children.Add(PageKit.FieldLabel("What came of it"));

        var box = new TextBox
        {
            Text = t.ResultLine,
            FontSize = 13,
            Foreground = PageKit.B(PageKit.Ink),
            CaretBrush = PageKit.B(PageKit.Ink),
            Background = PageKit.B(PageKit.Raised),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(11, 8, 11, 8),
            AcceptsReturn = false
        };
        box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        panel.Children.Add(box);

        panel.Children.Add(PageKit.Text(
            "One line. It goes to the client exactly as written, and Ari never writes it for you.",
            11.5, PageKit.Ink3, margin: new Thickness(0, 6, 0, 0), wrap: true));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(PageKit.PrimaryButton("Save", () => _ = SaveResultLineAsync(t.TaskId, box.Text)));
        buttons.Children.Add(PageKit.GhostButton("Cancel", () =>
        {
            _editingResultLineFor = 0;
            _ = RefreshTasksPageAsync();
        }));
        panel.Children.Add(buttons);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = SaveResultLineAsync(t.TaskId, box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _editingResultLineFor = 0;
                _ = RefreshTasksPageAsync();
                e.Handled = true;
            }
        };

        return panel;
    }

    private void OpenResultLineEditor(long taskId, string label)
    {
        _editingResultLineFor = taskId;
        ShowSection(UiSection.Tasks);
    }

    private async Task SaveResultLineAsync(long taskId, string line)
    {
        await _apiClient
            .SetTaskReportFieldsAsync(new LocalApiTaskReportFieldsRequest(
                SchemaVer: 1, TaskId: taskId, Status: null, ResultLine: line.Trim(),
                BlockedReason: null, ExpectedFinishDay: null))
            .ConfigureAwait(true);

        _editingResultLineFor = 0;
        await RefreshTasksPageAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Cycles a status. Blocked is included, but its REASON is asked separately and never invented:
    /// "blocked" with no reason is worse than silence, so the page prompts for one.
    /// </summary>
    private async Task CycleTaskStatusAsync(LocalApiTaskRow t)
    {
        string next = t.Status switch
        {
            "planned" => "in_progress",
            "in_progress" => "done",
            "done" => "blocked",
            _ => "planned"
        };

        await _apiClient
            .SetTaskReportFieldsAsync(new LocalApiTaskReportFieldsRequest(
                SchemaVer: 1, TaskId: t.TaskId, Status: next, ResultLine: null,
                BlockedReason: null, ExpectedFinishDay: null))
            .ConfigureAwait(true);

        await RefreshTasksPageAsync().ConfigureAwait(true);
    }
}
