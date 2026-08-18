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
        ["upcoming"] = "Start dates ahead of today. Ari never starts these for you.",
        ["prog"] = "Time recorded against them, and you have not marked them finished.",
        ["block"] = "Blocked is the one status a client acts on, so it is the one Ari never sets for you.",
        ["done"] = "Finished work. Amber means the one line about what came of it is still missing.",
        ["all"] = "Everything, including untagged work that can never reach a client report."
    };

    private async Task RefreshTasksPageAsync()
    {
        try
        {
            LocalApiTaskRowsResponse? response = await _apiClient.GetTaskRowsAsync().ConfigureAwait(true);
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

    private bool MatchesFilter(LocalApiTaskRow t) => _taskFilter switch
    {
        "all" => true,

        // "Time recorded against them, and you have not marked them finished" — the filter's own
        // description, which is what it now does. It used to require status == 'in_progress', a
        // value almost nothing carries, so the tab was empty while real work was underway.
        "prog" => t.Status != "done" && t.Status != "blocked" && t.Hours > 0,

        "block" => t.Status == "blocked",
        "done" => t.Status == "done",

        // A task you POSTPONED is the main thing that belongs here, and it was the one thing this
        // could not see: it read the expected-finish date and never the deferred-until day, so
        // "not today, tomorrow" vanished from every filter instead of appearing under Upcoming.
        "upcoming" => t.Status != "done" && IsAhead(t.DeferredUntilDay, t.ExpectedFinishDay),

        "week" => WorkedWithin(t, 7) || IsOpen(t),
        _ => WorkedWithin(t, 0) || IsOpen(t)
    };

    /// <summary>Still open: not finished, and not parked for a later day.</summary>
    private static bool IsOpen(LocalApiTaskRow t)
        => t.Status != "done" && !IsAhead(t.DeferredUntilDay, t.ExpectedFinishDay);

    private static bool WorkedWithin(LocalApiTaskRow t, int days)
        => System.DateTime.TryParseExact(
               t.LastWorkedDay, "yyyy-MM-dd",
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None,
               out System.DateTime d)
           && (System.DateTime.Now.Date - d.Date).TotalDays <= days;

    /// <summary>True if either day is set and lands after today. Deferral wins when both are.</summary>
    private static bool IsAhead(params string[] days)
    {
        foreach (string day in days)
        {
            if (System.DateTime.TryParseExact(
                    day, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out System.DateTime d)
                && d.Date > System.DateTime.Now.Date)
            {
                return true;
            }
        }

        return false;
    }

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

        if (t.ResultLine.Trim().Length > 0)
        {
            names.Children.Add(PageKit.Text(
                t.ResultLine, 12.5,
                t.Status == "blocked" ? PageKit.Stop
                    : t.IsUntagged ? PageKit.Ink3 : PageKit.Ink2,
                margin: new Thickness(0, 4, 0, 0), wrap: true));
        }
        else if (t.IsUntagged)
        {
            names.Children.Add(PageKit.Text(
                "No project, so it can never appear in a client report.",
                12.5, PageKit.Ink3, margin: new Thickness(0, 4, 0, 0), wrap: true));
        }
        else
        {
            names.Children.Add(PageKit.Link(
                "No line about what came of this ›",
                () => OpenResultLineEditor(t.TaskId, t.Label),
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

        return outer;
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
        _ = RefreshTasksPageAsync();
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
