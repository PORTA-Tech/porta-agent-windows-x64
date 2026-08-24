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

    /// <summary>Shown under the drawer when something the user typed could not be read.</summary>
    private string _projectFieldNotice = string.Empty;

    /// <summary>True while the "what do you call it" box is open.</summary>
    private bool _addingProject;

    /// <summary>Archive asks once. It is one click beside the primary action.</summary>
    private long _confirmingArchiveFor;

    private static readonly (string Key, string Label)[] StatusOptions =
    {
        ("active", "Active"), ("paused", "Paused"), ("complete", "Finished")
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
                // Archived projects are LISTED, not hidden. Loading only the active ones made one
                // click on Archive permanently remove a project from the app with no way back.
                .GetProjectsAsync(includeArchived: true)
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

        Button add = PageKit.GhostButton(
            _addingProject ? "Cancel" : "New project",
            () => { _addingProject = !_addingProject; _ = RefreshProjectsAsync(); });
        add.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        ProjectsPageHost.Children.Add(header);

        // Creating one used to make a project called "New project" with no way to rename it, so
        // every project was stuck with that name forever. It asks now.
        if (_addingProject)
        {
            var box = new StackPanel { Margin = PageKit.CardPad };
            box.Children.Add(Question(
                "What do you call this project?",
                "Your own name, for you. Your client never sees it — you give them a title separately."));
            box.Children.Add(PlaceholderBox(string.Empty, "e.g. PORTA", v => _ = CreateProjectFromPageAsync(v)));
            ProjectsPageHost.Children.Add(PageKit.Card(box));
        }

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
        if (p.Archived)
        {
            return BuildArchivedRow(p);
        }

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

        // ── Behaviour 1. A project nobody has worked on yet must not answer with "0 / 0 tasks
        // complete · over 0 days" and a row of dashes. Four zeroes and four dashes read as a
        // broken page, not as an empty one, and they tell the user nothing about what to do.
        if (p.TotalTasks == 0 && p.Hours <= 0)
        {
            body.Children.Add(PageKit.Text(
                $"No work recorded on this project yet. Start a task, tag it to {p.Name}, and it "
                + "will appear here.",
                13, PageKit.Ink2, margin: new Thickness(0, 18, 0, 0), wrap: true));
        }
        else
        {
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
        }

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
        bool canReport = p.ClientName.Trim().Length > 0 && (p.Recipients?.Count ?? 0) > 0;
        Button build = PageKit.PrimaryButton("Build a report", () =>
        {
            // Straight into the DRAFT BUILDER — the screen with the range, the preview and the
            // send confirm. It used to open the Reports history page, which builds nothing: the
            // button was named for one thing and did another, and landed on whichever tab happened
            // to be selected. Opening a history list is not building a report.
            ShowSection(UiSection.SendFlow);
            if (!PreviewMode)
            {
                _ = OpenReportBuilderForAsync(p.Id);
            }
        });
        if (!canReport)
        {
            // Disabled rather than hidden: a missing button is a mystery, a disabled one with a
            // reason under it is an instruction.
            build.IsEnabled = false;
            build.Opacity = 0.4;
        }

        actions.Children.Add(build);
        if (_confirmingArchiveFor == p.Id)
        {
            actions.Children.Add(PageKit.GhostButton("Yes, archive it", () =>
            {
                _confirmingArchiveFor = 0;
                _ = SetProjectArchivedAsync(p.Id, true);
            }));
            actions.Children.Add(PageKit.GhostButton("Keep it", () =>
            {
                _confirmingArchiveFor = 0;
                _ = RefreshProjectsAsync();
            }));
        }
        else
        {
            // It sits one click from the primary action, and it used to take a project out of the
            // app with nothing asking and nothing to undo it.
            actions.Children.Add(PageKit.GhostButton("Archive", () =>
            {
                _confirmingArchiveFor = p.Id;
                _ = RefreshProjectsAsync();
            }));
        }
        if (p.LastSentLabel.Length > 0)
        {
            var last = PageKit.Text(p.LastSentLabel, 11.5, PageKit.Ink3);
            last.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(last);
        }

        stack.Children.Add(actions);

        if (!canReport)
        {
            stack.Children.Add(PageKit.Text(
                "Add the name your client sees and an address to send to.",
                12.5, PageKit.Ink3, margin: new Thickness(26, 0, 26, 18), wrap: true));
        }

        return PageKit.Card(stack);
    }

    /// <summary>
    /// An archived project, still visible and one click from coming back. It keeps its tasks and
    /// its hours the whole time — archiving only ever hid it.
    /// </summary>
    private UIElement BuildArchivedRow(LocalApiProject p)
    {
        var grid = new Grid { Margin = PageKit.CardPad };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(PageKit.Text(
            p.ClientName.Trim().Length > 0 ? p.ClientName : p.Name, 15, PageKit.Ink2, FontWeights.Medium));
        left.Children.Add(PageKit.Text(
            $"Archived · {p.Name} · {p.TotalTasks} {(p.TotalTasks == 1 ? "task" : "tasks")} kept",
            12.5, PageKit.Ink3, margin: new Thickness(0, 4, 0, 0)));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        Button restore = PageKit.GhostButton("Restore", () => _ = SetProjectArchivedAsync(p.Id, false));
        restore.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(restore, 1);
        grid.Children.Add(restore);

        return PageKit.Card(grid);
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

    /// <summary>
    /// Every label is a plain question in sentence case, and the explanation sits ABOVE the input.
    /// No field is presented without saying what it is for before the user reaches it — reading a
    /// hint underneath, after you have already typed, is reading it too late.
    ///
    /// The first two fields come first because they are the only two that block a report.
    /// </summary>
    private UIElement BuildProjectDrawer(LocalApiProject p)
    {
        var drawer = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };

        // 0 — your own name for it. Not in the original six because it was assumed to exist
        // already; a project created from this page had no way to be renamed at all without it.
        drawer.Children.Add(Field(
            "What do you call this project?",
            "Your own name, for you. Your client never sees it.",
            p.Name,
            "e.g. PORTA",
            v => _ = RenameProjectAsync(p.Id, v)));

        // 1 — the one thing that makes a report possible at all.
        drawer.Children.Add(Field(
            "What should your client call this project?",
            $"This is the title on the report they receive. Your own name for it, {p.Name}, stays on "
            + "this machine and they never see it.",
            // NEVER prefilled from the local name. Prefilling it with the private name is exactly
            // the leak this field exists to prevent, and it defeats the whole purpose of asking.
            p.ClientName,
            "e.g. Website redesign",
            v => _ = SetProjectClientNameAsync(p.Id, v)));

        // 2 — the other blocker.
        drawer.Children.Add(Field(
            "Who should receive the report?",
            "Your client's email address. Their reply comes straight back to you, not to Ari.",
            string.Join(", ", p.Recipients ?? new List<string>()),
            "name@their-company.example",
            v => _ = SetProjectEngagementAsync(p.Id, recipients: SplitAddresses(v))));

        // 3
        drawer.Children.Add(Question("How often should Ari write a report for this?",
            "Ari writes the draft and tells you it's ready. It never sends it. You do."));
        drawer.Children.Add(PageKit.Segmented(FrequencyOptions, p.ReportingFrequency,
            key => _ = SetProjectEngagementAsync(p.Id, reportingFrequency: key)));
        drawer.Children.Add(Gap());

        // 4
        drawer.Children.Add(Question("Is this project running?",
            "Ari sets this from your recent work. Change it if it's wrong."));
        drawer.Children.Add(PageKit.Segmented(StatusOptions, p.Status,
            key => _ = SetProjectEngagementAsync(p.Id, status: key)));
        drawer.Children.Add(Gap());

        // 5
        drawer.Children.Add(Field(
            "When did work on this begin?",
            "Filled in from your first recorded work. Change it if the project really started earlier.",
            p.StartDay,
            "no work recorded yet",
            v => _ = SetProjectDayAsync(p.Id, v, isStart: true)));

        // 6 — asked, never decided.
        drawer.Children.Add(Field(
            "Has it finished?",
            "Leave this empty while the project is still running. Ari never decides this for you.",
            p.CompletionDay,
            "still running",
            v => _ = SetProjectDayAsync(p.Id, v, isStart: false)));

        if (_projectFieldNotice.Length > 0)
        {
            drawer.Children.Add(PageKit.Text(
                _projectFieldNotice, 12.5, PageKit.Warn, margin: new Thickness(0, 0, 0, 6), wrap: true));
        }

        return drawer;
    }

    /// <summary>A plain question and the sentence that explains it, both above the input.</summary>
    private static UIElement Question(string question, string help)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        s.Children.Add(PageKit.Text(question, 13.5, PageKit.Ink, FontWeights.Medium, wrap: true));
        s.Children.Add(PageKit.Text(help, 12.5, PageKit.Ink2,
            margin: new Thickness(0, 5, 0, 0), wrap: true));
        return s;
    }

    private UIElement Field(string question, string help, string value, string placeholder,
                            System.Action<string> onCommit)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        s.Children.Add(Question(question, help));
        s.Children.Add(PlaceholderBox(value, placeholder, onCommit));
        return s;
    }

    private static UIElement Gap() => new Border { Height = 22 };

    private UIElement BuildProjectTaskRow(LocalApiProjectTask t)
    {
        var grid = new Grid { Margin = PageKit.RowPad };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The fixed columns are sized so the LAST one still lands inside the card. Hours were
        // dropping off the right edge entirely, which silently removed the one number on the row
        // that says how much work it was.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        UIElement swatch = PageKit.Swatch(t.ColourSlot);
        Grid.SetColumn(swatch, 0);
        grid.Children.Add(swatch);

        var names = new StackPanel();
        names.Children.Add(PageKit.Text(t.Label, 14, PageKit.Ink));

        // The result line sits UNDER the name. Where it was skipped, the amber control that
        // recovers it — right now a skipped line can never be recovered, which is a hole in a
        // client's report.
        // Chat 5 (2026-08-24) — same fix as the Tasks page, because this was the same bug: a saved
        // line rendered as plain text and the editor was reachable only while it was empty. It
        // routes to the same editor, which lives on the Tasks page.
        if (t.ResultLine.Trim().Length > 0)
        {
            names.Children.Add(PageKit.EditableLine(
                t.ResultLine,
                t.Status == "blocked" ? PageKit.Stop : PageKit.Ink2,
                () => OpenResultLineEditor(t.TaskId, t.Label),
                "Click to edit the line the client reads."));
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
        "complete" => "Finished",
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

    /// <summary>
    /// A field with a real placeholder. The placeholder says what an EMPTY field means — "still
    /// running", "no work recorded yet" — so an empty box is an answer rather than a gap.
    /// </summary>
    private UIElement PlaceholderBox(string value, string placeholder, System.Action<string> onCommit)
    {
        TextBox box = EditBox(value, placeholder, onCommit);
        TextBlock ghost = PageKit.Text(placeholder, 13, PageKit.Ink3);
        ghost.Margin = new Thickness(12, 0, 0, 0);
        ghost.IsHitTestVisible = false;
        ghost.VerticalAlignment = VerticalAlignment.Center;
        ghost.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
            ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(ghost);
        return grid;
    }

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

    private async Task CreateProjectFromPageAsync(string name)
    {
        string clean = name.Trim();
        if (clean.Length == 0)
        {
            return;
        }

        LocalApiProjectResponse? response = await _apiClient
            .CreateProjectAsync(new LocalApiProjectCreateRequest(SchemaVer: 1, Name: clean))
            .ConfigureAwait(true);

        _addingProject = false;

        if (response is { Success: true, Project: not null })
        {
            // Naming one that already exists — including an archived one — brings that project
            // back rather than making a second with the same name, which is what the store does.
            _openProjectDrawers.Add(response.Project.Id);
        }
        else
        {
            _projectFieldNotice = $"Couldn't create “{clean}”.";
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Renames the LOCAL name — the small line under the headline. Without this a project created
    /// from this page could never be renamed, so it kept whatever it was called forever.
    /// </summary>
    private async Task RenameProjectAsync(long id, string name)
    {
        if (name.Trim().Length == 0)
        {
            return;
        }

        await _apiClient
            .UpdateProjectAsync(new LocalApiProjectUpdateRequest(
                SchemaVer: 1, Id: id, Name: name.Trim(), Archived: null))
            .ConfigureAwait(true);

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

    /// <summary>
    /// Opens the draft builder already pointed at the project the user pressed the button on.
    /// Landing on the builder with a different project selected is its own small betrayal.
    /// </summary>
    private async Task OpenReportBuilderForAsync(long projectId)
    {
        await OpenReportsPageAsync().ConfigureAwait(true);

        if (ReportProjectPicker.ItemsSource is IEnumerable<LocalApiProject> items)
        {
            int index = items.ToList().FindIndex(x => x.Id == projectId);
            if (index >= 0)
            {
                ReportProjectPicker.SelectedIndex = index;
            }
        }
    }

    /// <summary>
    /// Saves one of the two dates, and NEVER blanks the field in silence. If the store cannot read
    /// what was typed it says so and leaves the text where it is, so the user can correct it rather
    /// than watch it disappear.
    /// </summary>
    private async Task SetProjectDayAsync(long id, string typed, bool isStart)
    {
        if (typed.Trim().Length > 0
            && !Porta.Agent.Core.SqliteBrainStateStore.TryParseDayKey(typed, out _))
        {
            _projectFieldNotice = $"Couldn't read “{typed.Trim()}” as a date. Try 18 Aug 2026, or 2026-08-18.";
            await RefreshProjectsAsync().ConfigureAwait(true);
            return;
        }

        _projectFieldNotice = string.Empty;
        await SetProjectEngagementAsync(
            id,
            startDay: isStart ? typed : null,
            completionDay: isStart ? null : typed).ConfigureAwait(true);
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
