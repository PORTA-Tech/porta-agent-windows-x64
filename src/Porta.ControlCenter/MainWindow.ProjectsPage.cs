namespace Porta.ControlCenter;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Porta.Agent.LocalApi;

/// <summary>
/// Phase 3 rebuilt (2026-08-18) — the Projects page, built to Ari_ControlCenter_Pages_Mockup.html.
///
/// The client-facing name is the headline; the local name is the small line beneath, because the
/// local one is typed in a private hurry ("nolan - chase invoice") and the client one is the
/// biggest text on a document somebody pays for.
///
/// Four figures, a progress bar, one quiet line of facts with an Edit details link, the work in the
/// project, then the actions. The drawer starts CLOSED — the page is quiet when things are fine.
/// The only marker outside the drawer is the amber banner on a project with no client-facing name,
/// which is the one thing that genuinely stops a report existing.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<long> _openProjectDrawers = new();

    private static readonly (string Key, string Label)[] StatusOptions =
    {
        ("active", "Active"), ("paused", "Paused"), ("complete", "Complete")
    };

    private static readonly (string Key, string Label)[] FrequencyOptions =
    {
        ("day", "Day"), ("week", "Week"), ("fortnight", "Two weeks"), ("month", "Month")
    };

    private async Task RefreshProjectsAsync()
    {
        try
        {
            LocalApiProjectsResponse? response = await _apiClient
                .GetProjectsAsync(includeArchived: false)
                .ConfigureAwait(true);

            RenderProjectsPage(response?.Projects ?? System.Array.Empty<LocalApiProject>());
        }
        catch
        {
            // The agent being down is a normal state for this window and the rest of it already
            // says so loudly. An empty list here must never read as "you have no projects".
            RenderProjectsPage(null);
        }
    }

    internal void RenderProjectsPage(IReadOnlyList<LocalApiProject>? projects)
    {
        ProjectsPageHost.Children.Clear();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(PageKit.PageTitle("Projects"));
        titles.Children.Add(PageKit.Text(
            "Ari keeps these up to date from your recorded work. It only asks you for the things it cannot know.",
            12.5, PageKit.Ink2, margin: new Thickness(0, 7, 0, 0), wrap: true));
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        Button add = PageKit.GhostButton("New project", () => _ = CreateProjectFromPageAsync());
        add.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        ProjectsPageHost.Children.Add(header);

        if (projects is null)
        {
            ProjectsPageHost.Children.Add(PageKit.Card(PageKit.Empty(
                "Can't reach the agent.",
                "The list may be out of date. This is not the same as having no projects.")));
            return;
        }

        if (projects.Count == 0)
        {
            ProjectsPageHost.Children.Add(PageKit.Card(PageKit.Empty(
                "No projects yet.",
                "Add one above, or tap “+ project” on the morning card when you name a task.")));
            return;
        }

        foreach (LocalApiProject project in projects)
        {
            ProjectsPageHost.Children.Add(BuildProjectCard(project));
        }

        ProjectsPageHost.Children.Add(PageKit.Text(
            "Your own project names, your recipients and everything on this page stay on this machine. "
            + "Only the one address on a report you actually send ever leaves it.",
            11.5, PageKit.Ink3, margin: new Thickness(0, 22, 0, 0), wrap: true));
    }

    private UIElement BuildProjectCard(LocalApiProject p)
    {
        var stack = new StackPanel();
        bool missingClientName = p.ClientName.Trim().Length == 0;

        // The ONE amber marker outside the drawer, and only when a report is genuinely impossible.
        if (missingClientName)
        {
            stack.Children.Add(PageKit.NeedsYouBanner(
                "Needs a name your client will recognise",
                "Until then this project cannot be reported.",
                "Name it",
                () => ToggleProjectDrawer(p.Id)));
        }

        var body = new StackPanel { Margin = PageKit.CardPad };

        // ── Headline: what the client sees, then your own name for it.
        body.Children.Add(PageKit.Text(
            missingClientName ? p.Name : p.ClientName, 21, PageKit.Ink, FontWeights.SemiBold));

        var sub = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        sub.Children.Add(PageKit.Text(
            missingClientName ? "no client-facing name yet" : p.Name, 12.5,
            missingClientName ? PageKit.Warn : PageKit.Ink3));
        if (!missingClientName)
        {
            sub.Children.Add(PageKit.Text(
                "  ·  your name for it, never leaves this machine", 12.5, PageKit.Ink3));
        }

        body.Children.Add(sub);
        body.Children.Add(PageKit.Text(
            StatusLabel(p.Status), 12.5, StatusInk(p.Status), margin: new Thickness(0, 14, 0, 0)));

        // ── The four figures.
        var figures = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        figures.Children.Add(PageKit.Figure($"{p.DoneTasks} / {p.TotalTasks}", "tasks complete"));
        figures.Children.Add(PageKit.Figure(
            p.Hours > 0 ? $"{p.Hours:0.#}h" : "—",
            p.DaysWorked == 1 ? "over 1 day" : $"over {p.DaysWorked} days"));
        figures.Children.Add(PageKit.Figure(
            p.BlockedTasks > 0 ? p.BlockedTasks.ToString() : "—", "blocked",
            p.BlockedTasks > 0 ? PageKit.Stop : (System.Windows.Media.Color?)null));
        figures.Children.Add(PageKit.Figure(
            p.NextReportDue.Length > 0 ? p.NextReportDue : "—", "next report"));
        body.Children.Add(figures);

        body.Children.Add(PageKit.ProgressBar(p.DoneTasks, p.TotalTasks));

        // ── One quiet line of facts, and the way into the form.
        var facts = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (string fact in ProjectFacts(p))
        {
            if (facts.Children.Count > 0)
            {
                facts.Children.Add(PageKit.Text("  ·  ", 12.5, PageKit.Ink3));
            }

            facts.Children.Add(PageKit.Text(fact, 12.5, PageKit.Ink2));
        }

        facts.Children.Add(PageKit.Text("     ", 12.5, PageKit.Ink3));
        facts.Children.Add(PageKit.Link("Edit details", () => ToggleProjectDrawer(p.Id)));
        body.Children.Add(facts);

        if (_openProjectDrawers.Contains(p.Id))
        {
            body.Children.Add(BuildProjectDrawer(p));
        }

        stack.Children.Add(body);

        // ── The work in the project.
        if (p.Tasks is { Count: > 0 })
        {
            stack.Children.Add(PageKit.Hair());
            foreach (LocalApiProjectTask t in p.Tasks)
            {
                stack.Children.Add(BuildProjectTaskRow(t));
            }
        }

        // ── The actions.
        stack.Children.Add(PageKit.Hair());
        var actions = new WrapPanel { Margin = new Thickness(26, 16, 26, 18) };
        actions.Children.Add(PageKit.PrimaryButton("Build a report", () => ShowSection(UiSection.Reports)));
        actions.Children.Add(PageKit.GhostButton("Archive", () => _ = SetProjectArchivedAsync(p.Id, true)));
        if (p.LastSentLabel.Length > 0)
        {
            var last = PageKit.Text(p.LastSentLabel, 11.5, PageKit.Ink3);
            last.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(last);
        }

        stack.Children.Add(actions);
        return PageKit.Card(stack);
    }

    private static IEnumerable<string> ProjectFacts(LocalApiProject p)
    {
        if (p.StartDay.Length > 0)
        {
            yield return $"Started {FriendlyDay(p.StartDay)}";
        }

        yield return p.Status == "paused" && p.LastWorkedLabel.Length > 0
            ? p.LastWorkedLabel
            : $"reports {FrequencyWord(p.ReportingFrequency)}";

        if (p.Recipients is { Count: > 0 })
        {
            yield return $"goes to {string.Join(", ", p.Recipients)}";
        }
    }

    private UIElement BuildProjectDrawer(LocalApiProject p)
    {
        var drawer = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };

        // ── ASKED. Only the user can say this, so its hint is amber when it is still missing.
        drawer.Children.Add(PageKit.FieldLabel("Name the client sees"));
        drawer.Children.Add(EditBox(p.ClientName, "The name at the top of a report",
            v => _ = SetProjectClientNameAsync(p.Id, v)));
        drawer.Children.Add(PageKit.Hint("Only you can say this. Ari never invents it.",
            asked: p.ClientName.Trim().Length == 0));

        // ── DERIVED. Overriding one stops Ari deriving it, permanently.
        drawer.Children.Add(Spacer());
        drawer.Children.Add(PageKit.FieldLabel("Status"));
        drawer.Children.Add(PageKit.Segmented(StatusOptions, p.Status,
            key => _ = SetProjectEngagementAsync(p.Id, status: key)));
        if (p.StatusReason.Length > 0)
        {
            drawer.Children.Add(PageKit.Hint(p.StatusReason, asked: false));
        }

        drawer.Children.Add(Spacer());
        var dates = new Grid();
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var startCell = new StackPanel { Margin = new Thickness(0, 0, 13, 0) };
        startCell.Children.Add(PageKit.FieldLabel("Started"));
        startCell.Children.Add(EditBox(p.StartDay, "yyyy-mm-dd",
            v => _ = SetProjectEngagementAsync(p.Id, startDay: v)));
        startCell.Children.Add(PageKit.Hint(
            p.StartDayReason.Length > 0 ? p.StartDayReason : "Your first recorded work on this project.",
            asked: false));
        Grid.SetColumn(startCell, 0);
        dates.Children.Add(startCell);

        var endCell = new StackPanel();
        endCell.Children.Add(PageKit.FieldLabel("Completed"));
        endCell.Children.Add(EditBox(p.CompletionDay, "yyyy-mm-dd",
            v => _ = SetProjectEngagementAsync(p.Id, completionDay: v)));
        // ASKED: a completion date is a claim, and Ari must never make it.
        endCell.Children.Add(PageKit.Hint("Only you can say a project is done.", asked: true));
        Grid.SetColumn(endCell, 1);
        dates.Children.Add(endCell);
        drawer.Children.Add(dates);

        drawer.Children.Add(Spacer());
        drawer.Children.Add(PageKit.FieldLabel("Report every"));
        drawer.Children.Add(PageKit.Segmented(FrequencyOptions, p.ReportingFrequency,
            key => _ = SetProjectEngagementAsync(p.Id, reportingFrequency: key)));

        drawer.Children.Add(Spacer());
        drawer.Children.Add(PageKit.FieldLabel("Report goes to"));
        drawer.Children.Add(EditBox(string.Join(", ", p.Recipients ?? new List<string>()),
            "nobody yet",
            v => _ = SetProjectEngagementAsync(p.Id, recipients: SplitAddresses(v))));
        drawer.Children.Add(PageKit.Hint(
            p.RecipientsReason.Length > 0 ? p.RecipientsReason : "Stays on this machine.",
            asked: false));

        return drawer;
    }

    private UIElement BuildProjectTaskRow(LocalApiProjectTask t)
    {
        var grid = new Grid { Margin = PageKit.RowPad };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        UIElement swatch = PageKit.Swatch(t.ColourSlot);
        Grid.SetColumn(swatch, 0);
        grid.Children.Add(swatch);

        var names = new StackPanel();
        names.Children.Add(PageKit.Text(t.Label, 14, PageKit.Ink));

        // The result line sits UNDER the name. Where it was skipped, the amber control that
        // recovers it — right now a skipped line can never be recovered, which is a hole in a
        // client's report.
        if (t.ResultLine.Trim().Length > 0)
        {
            names.Children.Add(PageKit.Text(t.ResultLine, 12.5,
                t.Status == "blocked" ? PageKit.Stop : PageKit.Ink2,
                margin: new Thickness(0, 4, 0, 0), wrap: true));
        }
        else
        {
            names.Children.Add(PageKit.Link("No line about what came of this ›",
                () => OpenResultLineEditor(t.TaskId, t.Label),
                PageKit.Warn));
        }

        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        UIElement tag = PageKit.StatusTag(t.Status);
        tag.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        Grid.SetColumn(tag, 2);
        grid.Children.Add(tag);

        TextBlock dates = PageKit.Text(t.Dates, 12.5, PageKit.Ink3);
        dates.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(dates, 3);
        grid.Children.Add(dates);

        // Hours sit in a column under each other, so these get tabular figures.
        TextBlock hours = PageKit.Text(t.Hours > 0 ? $"{t.Hours:0.#}h" : "—", 12.5, PageKit.Ink2);
        hours.TextAlignment = TextAlignment.Right;
        hours.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hours, 4);
        grid.Children.Add(hours);

        return grid;
    }

    private void ToggleProjectDrawer(long id)
    {
        if (!_openProjectDrawers.Remove(id))
        {
            _openProjectDrawers.Add(id);
        }

        _ = RefreshProjectsAsync();
    }

    private static string[] SplitAddresses(string raw)
        => raw.Split(new[] { ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
              .Select(a => a.Trim())
              .Where(a => a.Length > 0)
              .ToArray();

    private static string StatusLabel(string status) => status switch
    {
        "paused" => "Paused",
        "complete" => "Complete",
        _ => "Active"
    };

    private static System.Windows.Media.Color StatusInk(string status) => status switch
    {
        "complete" => PageKit.Accent,
        "paused" => PageKit.Ink3,
        _ => PageKit.Ink2
    };

    private static string FrequencyWord(string f) => f switch
    {
        "day" => "daily",
        "fortnight" => "every two weeks",
        "month" => "monthly",
        _ => "weekly"
    };

    private static string FriendlyDay(string day)
        => System.DateTime.TryParseExact(
               day, "yyyy-MM-dd",
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None,
               out System.DateTime d)
            ? d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)
            : day;

    private static UIElement Spacer() => new Border { Height = 18 };

    /// <summary>A field that writes on Enter or on losing focus, and only when it actually changed.</summary>
    private TextBox EditBox(string value, string placeholder, System.Action<string> onCommit)
    {
        var box = new TextBox
        {
            Text = value,
            FontSize = 13,
            Foreground = PageKit.B(PageKit.Ink),
            CaretBrush = PageKit.B(PageKit.Ink),
            Background = PageKit.B(PageKit.Raised),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(11, 8, 11, 8),
            Tag = placeholder
        };

        void Commit()
        {
            string typed = box.Text.Trim();
            if (!string.Equals(typed, value, System.StringComparison.Ordinal))
            {
                onCommit(typed);
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    private async Task CreateProjectFromPageAsync()
    {
        LocalApiProjectResponse? response = await _apiClient
            .CreateProjectAsync(new LocalApiProjectCreateRequest(SchemaVer: 1, Name: "New project"))
            .ConfigureAwait(true);

        if (response is { Success: true, Project: not null })
        {
            _openProjectDrawers.Add(response.Project.Id);
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private async Task SetProjectEngagementAsync(
        long id,
        string? status = null,
        string? startDay = null,
        string? completionDay = null,
        IReadOnlyList<string>? recipients = null,
        string? reportingFrequency = null)
    {
        await _apiClient
            .UpdateProjectEngagementAsync(new LocalApiProjectEngagementRequest(
                SchemaVer: 1, Id: id, Status: status, StartDay: startDay,
                CompletionDay: completionDay, Recipients: recipients,
                ReportingFrequency: reportingFrequency))
            .ConfigureAwait(true);

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private async Task SetProjectClientNameAsync(long id, string clientName)
    {
        await _apiClient
            .UpdateProjectClientAsync(new LocalApiProjectClientRequest(
                SchemaVer: 1, Id: id, ClientName: clientName,
                UseClientLabels: null, RateMinor: null, RateCurrency: null))
            .ConfigureAwait(true);

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private async Task SetProjectArchivedAsync(long id, bool archived)
    {
        await _apiClient
            .UpdateProjectAsync(new LocalApiProjectUpdateRequest(
                SchemaVer: 1, Id: id, Name: null, Archived: archived))
            .ConfigureAwait(true);

        await RefreshProjectsAsync().ConfigureAwait(true);
    }
}
