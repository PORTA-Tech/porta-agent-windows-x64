namespace Porta.ControlCenter;

using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Porta.Agent.LocalApi;

/// <summary>
/// Step 9a — the projects page. Deliberately small: a project is a NAME, and this is where one is
/// created, renamed, or put away. Client, colour and rate are not here because they are not
/// anywhere yet; adding them is a later decision, not a gap.
///
/// Everything on this page is LOCAL. No project name leaves the machine in 9a — not in the digest,
/// not in the narrative, not in the stats upload. That is what makes 9a tellable apart from 9b by
/// looking at the nightly email.
///
/// This is NOT the projects page from the brief's §4 (totals, per-day bars, where the time went).
/// That one needs task history that outlives 30 days on this machine and does not exist server-side
/// at all, and it is a later phase.
/// </summary>
public partial class MainWindow
{
    private async Task RefreshProjectsAsync()
    {
        try
        {
            LocalApiProjectsResponse? response = await _apiClient
                .GetProjectsAsync(includeArchived: true)
                .ConfigureAwait(true);

            RenderProjects(response?.Projects ?? System.Array.Empty<LocalApiProject>());
        }
        catch
        {
            // The agent being down is a normal state for this window; the rest of it already says
            // so loudly. An empty list here must not read as "you have no projects".
            ProjectStatusText.Text = "Can't reach the agent — the list may be out of date.";
        }
    }

    private void RenderProjects(IReadOnlyList<LocalApiProject> projects)
    {
        ProjectsList.Children.Clear();
        ProjectsEmptyText.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (LocalApiProject project in projects.OrderBy(p => p.Archived).ThenBy(p => p.Name))
        {
            ProjectsList.Children.Add(BuildProjectRow(project));
        }
    }

    private UIElement BuildProjectRow(LocalApiProject project)
    {
        // A ROW, not a field. The card surface and the hairline belong to the row; the name inside
        // it is text until you click into it. See RpInlineEditBox.
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Phase 3.2 — the card is now a header line plus the engagement beneath it, so a project's
        // name and what a project actually IS are one object on screen rather than two pages.
        var card = new StackPanel();
        row.Child = card;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Children.Add(grid);

        var nameBox = new TextBox
        {
            Text = project.Name,
            Style = (Style)FindResource("RpInlineEditBox"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Click to rename",
            Foreground = new SolidColorBrush(project.Archived
                ? Color.FromRgb(0x6F, 0x78, 0x85)
                : Color.FromRgb(0xF2, 0xF4, 0xF7))
        };

        // Rename on Enter or on losing focus, and only when the text actually changed — so simply
        // clicking through the page never issues a write.
        void TryRename()
        {
            string typed = nameBox.Text.Trim();
            if (typed.Length == 0 || string.Equals(typed, project.Name, System.StringComparison.Ordinal))
            {
                nameBox.Text = project.Name;
                return;
            }

            _ = RenameProjectAsync(project.Id, typed);
        }

        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TryRename();
                e.Handled = true;
            }
        };
        nameBox.LostFocus += (_, _) => TryRename();

        Grid.SetColumn(nameBox, 0);
        grid.Children.Add(nameBox);

        // §12.7 — the client-facing name. The header is the biggest text a client reads, and the
        // local name is typed on a morning card in the same private hurry as a task title: "nolan -
        // chase invoice" is a plausible local name and an unsendable header.
        //
        // The whole stack behind this existed and had NO on-screen box, so ClientName could only
        // ever be empty, ReportSendReadiness always answered CanSend=false, and no report could be
        // sent at all. This label is the last link.
        // DELIBERATELY NOT PREFILLED FROM project.Name. Ari never guesses what a client is shown —
        // a guessed header is exactly the private-note leak this field exists to prevent. Empty
        // until the user types it, and the Reports page says so rather than inventing one.
        var clientBox = new TextBox
        {
            Text = project.ClientName,
            Style = (Style)FindResource("RpInlineEditBox"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ToolTip = "The name at the top of a report. Blank means this project cannot be reported yet.",
            Foreground = new SolidColorBrush(project.Archived
                ? Color.FromRgb(0x6F, 0x78, 0x85)
                : Color.FromRgb(0xF2, 0xF4, 0xF7))
        };

        // The label lives INSIDE the empty field rather than beside it. Beside it, the same four
        // words repeat on every row and become furniture the eye stops reading; inside, the words
        // are only ever on screen for a project that actually still needs them, and they vanish the
        // moment one is typed. Same rule as omitting an empty digest section: never explain a thing
        // that is not on the page.
        var clientHint = new TextBlock
        {
            Text = "Name the client sees",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x78, 0x85)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = project.ClientName.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };

        // IsHitTestVisible=false above is what keeps this from becoming the SEND TO bug in another
        // place: the hint sits over the field, so if it could take a click the field beneath it
        // could never take the caret.
        clientBox.TextChanged += (_, _) =>
            clientHint.Visibility = clientBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var clientCell = new Grid();
        clientCell.Children.Add(clientHint);
        clientCell.Children.Add(clientBox);

        // Same shape as the rename above: write on Enter or on losing focus, and only when the text
        // actually changed, so clicking through the page never issues a write. Unlike the local
        // name, EMPTY IS ALLOWED — clearing it is how a user says "this one isn't ready to send",
        // and refusing that would strand a name they no longer want on a client's document.
        void TryRenameClient()
        {
            string typed = clientBox.Text.Trim();
            if (string.Equals(typed, project.ClientName, System.StringComparison.Ordinal))
            {
                clientBox.Text = project.ClientName;
                return;
            }

            _ = SetProjectClientNameAsync(project.Id, typed);
        }

        clientBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TryRenameClient();
                e.Handled = true;
            }
        };
        clientBox.LostFocus += (_, _) => TryRenameClient();

        Grid.SetColumn(clientCell, 1);
        grid.Children.Add(clientCell);

        // Archive, never delete. The project's tasks keep pointing at it and some of that time has
        // already been reported; deleting would orphan measured minutes.
        var toggle = new Button
        {
            Content = project.Archived ? "Restore" : "Archive",
            // Never the accent: archiving is not the primary action on this page, adding is.
            Style = (Style)FindResource("RpQuietButton"),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Click += (_, _) => _ = SetProjectArchivedAsync(project.Id, !project.Archived);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        // An archived project keeps its history and its report links, but it is not an engagement
        // anyone is running, so the fields that describe one are not offered on it.
        if (!project.Archived)
        {
            card.Children.Add(BuildProjectDetails(project));
        }

        return row;
    }

    /// <summary>
    /// Writes the client-facing name (§12.7). Only <c>ClientName</c> is sent: every other field on
    /// the request is null so the endpoint's apply-only-when-present rule leaves the label default
    /// and the rate untouched. The rate in particular must never be disturbed from here — it is the
    /// one number on this page that looks like money.
    /// </summary>
    private async Task SetProjectClientNameAsync(long id, string clientName)
    {
        LocalApiProjectResponse? response = await _apiClient
            .UpdateProjectClientAsync(new LocalApiProjectClientRequest(
                SchemaVer: 1,
                Id: id,
                ClientName: clientName,
                UseClientLabels: null,
                RateMinor: null,
                RateCurrency: null))
            .ConfigureAwait(true);

        ProjectStatusText.Text = response switch
        {
            { Success: true } when clientName.Length == 0 =>
                "Cleared. This project can't be reported until a client-facing name is set.",
            { Success: true } => $"Your client will see “{clientName}”.",
            _ => "Couldn't save that — the agent didn't accept it."
        };

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private async Task RenameProjectAsync(long id, string name)
    {
        LocalApiProjectResponse? response = await _apiClient
            .UpdateProjectAsync(new LocalApiProjectUpdateRequest(SchemaVer: 1, Id: id, Name: name, Archived: null))
            .ConfigureAwait(true);

        ProjectStatusText.Text = response switch
        {
            { Success: true } => $"Renamed to “{name}”.",
            // The name belongs to another project. Saying so beats a rename that silently did
            // nothing, and merging two projects is not something to do behind someone's back.
            { Reason: "name_taken" } => $"“{name}” is already a project.",
            _ => "Couldn't rename that — the agent didn't accept it."
        };

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private async Task SetProjectArchivedAsync(long id, bool archived)
    {
        LocalApiProjectResponse? response = await _apiClient
            .UpdateProjectAsync(new LocalApiProjectUpdateRequest(SchemaVer: 1, Id: id, Name: null, Archived: archived))
            .ConfigureAwait(true);

        ProjectStatusText.Text = response is { Success: true }
            ? archived
                // Said plainly, because "archive" in most software means "gone".
                ? "Archived. Its time is still counted; it just won't show in the picker."
                : "Back in the picker."
            : "Couldn't change that — the agent didn't accept it.";

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    private void ProjectAddButton_Click(object sender, RoutedEventArgs e) => _ = AddProjectAsync();

    private void ProjectNewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = AddProjectAsync();
            e.Handled = true;
        }
    }

    private async Task AddProjectAsync()
    {
        string name = ProjectNewNameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        LocalApiProjectResponse? response = await _apiClient
            .CreateProjectAsync(new LocalApiProjectCreateRequest(SchemaVer: 1, Name: name))
            .ConfigureAwait(true);

        if (response is { Success: true, Project: not null })
        {
            // Create-or-get, so typing a name that already exists is not an error — it just hands
            // back the one you meant. Two projects with one name would split a client in half.
            ProjectStatusText.Text = string.Equals(response.Project.Name, name, System.StringComparison.OrdinalIgnoreCase)
                ? $"“{response.Project.Name}” is ready."
                : $"Using the existing “{response.Project.Name}”.";
            ProjectNewNameBox.Text = string.Empty;
        }
        else
        {
            ProjectStatusText.Text = "Couldn't add that one.";
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    // ── Phase 3.2 (2026-08-18) — the engagement, under each project ────────────────────────────
    //
    // The row above is what a project IS CALLED. Everything below is what a project IS: whether it
    // is running, when it began, when it ended, who its reports go to, and how it is going.
    //
    // These columns were added by the v30 migration and then had no setter for a day, which is why
    // the report could describe a date range but never say where that range sat inside the job.
    //
    // Every control here writes only the field it owns. The endpoint applies a field only when it
    // is present, so editing a date can never disturb a status and clearing recipients can never
    // clear a start date.

    private static readonly (string Key, string Label)[] ProjectStatusChoices =
    {
        ("active", "Active"),
        ("paused", "Paused"),
        ("complete", "Complete")
    };

    private static readonly (string Key, string Label)[] ProjectFrequencyChoices =
    {
        ("none", "Not set"),
        ("weekly", "Weekly"),
        ("fortnightly", "Fortnightly"),
        ("monthly", "Monthly")
    };

    private UIElement BuildProjectDetails(LocalApiProject project)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        stack.Children.Add(new Border
        {
            Height = 0.5,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
            Margin = new Thickness(0, 0, 0, 10)
        });

        // ── How it is going. A COUNT, never a percentage: tasks are not equal in size, and a
        // percentage claims they are. Blocked is called out separately because it is the one number
        // on this page that means somebody has to do something.
        stack.Children.Add(BuildProjectProgressLine(project));

        // ── Status
        stack.Children.Add(SectionLabel("STATUS"));
        var statusRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach ((string key, string label) in ProjectStatusChoices)
        {
            statusRow.Children.Add(ChoiceChip(
                label,
                selected: string.Equals(project.Status, key, System.StringComparison.Ordinal),
                onClick: () => _ = SetProjectEngagementAsync(project.Id, status: key)));
        }

        stack.Children.Add(statusRow);

        // ── The two dates. Typed, never inferred: Ari does not get to decide when an engagement
        // began, and a guess here is printed to a paying client.
        var dates = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement startCell = LabelledDayBox(
            "STARTED",
            project.StartDay,
            value => _ = SetProjectEngagementAsync(project.Id, startDay: value));
        Grid.SetColumn(startCell, 0);
        dates.Children.Add(startCell);

        UIElement endCell = LabelledDayBox(
            "COMPLETED",
            project.CompletionDay,
            value => _ = SetProjectEngagementAsync(project.Id, completionDay: value));
        Grid.SetColumn(endCell, 1);
        dates.Children.Add(endCell);
        stack.Children.Add(dates);

        // ── Reporting frequency. A statement of intent and nothing more: no scheduler reads it and
        // none ever will, because the report never sends automatically. Ari drafts, you tap.
        stack.Children.Add(SectionLabel("REPORT EVERY"));
        var freqRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach ((string key, string label) in ProjectFrequencyChoices)
        {
            freqRow.Children.Add(ChoiceChip(
                label,
                selected: string.Equals(project.ReportingFrequency, key, System.StringComparison.Ordinal),
                onClick: () => _ = SetProjectEngagementAsync(project.Id, reportingFrequency: key)));
        }

        stack.Children.Add(freqRow);
        stack.Children.Add(new TextBlock
        {
            Text = "A reminder of the rhythm you agreed. Nothing sends on its own — you always tap send.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x78, 0x85)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        // ── Recipients. LOCAL ONLY. This list lives on this machine; only the single address on a
        // report actually being sent ever leaves it.
        stack.Children.Add(SectionLabel("REPORTS GO TO"));
        var recipientBox = new TextBox
        {
            Text = string.Join(", ", project.Recipients ?? new List<string>()),
            Style = (Style)FindResource("RpTextBox"),
            ToolTip = "Separate addresses with a comma. Stored on this machine only.",
            Margin = new Thickness(0, 0, 0, 4)
        };

        void SaveRecipients()
        {
            string[] typed = recipientBox.Text
                .Split(new[] { ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToArray();

            // Only on an actual change, so clicking through the page never issues a write.
            if (typed.SequenceEqual(project.Recipients ?? new List<string>(), System.StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _ = SetProjectEngagementAsync(project.Id, recipients: typed);
        }

        recipientBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SaveRecipients();
                e.Handled = true;
            }
        };
        recipientBox.LostFocus += (_, _) => SaveRecipients();
        stack.Children.Add(recipientBox);

        return stack;
    }

    /// <summary>
    /// "3 of 8 tasks complete · 1 blocked · 12.4h". A COUNT and never a percentage — Yadin's rule,
    /// and the reason is that tasks are not equal in size, so a percentage would be a claim the
    /// data cannot support.
    /// </summary>
    private static UIElement BuildProjectProgressLine(LocalApiProject project)
    {
        var parts = new List<string>();

        parts.Add(project.TotalTasks == 0
            ? "No tasks yet"
            : $"{project.DoneTasks} of {project.TotalTasks} {(project.TotalTasks == 1 ? "task" : "tasks")} complete");

        if (project.InProgressTasks > 0)
        {
            parts.Add($"{project.InProgressTasks} in progress");
        }

        if (project.Hours > 0)
        {
            parts.Add($"{project.Hours:0.#}h");
        }

        var line = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        line.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", parts),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC3, 0xCA, 0xD4)),
            VerticalAlignment = VerticalAlignment.Center
        });

        // Blocked is the one count that means somebody has to do something, so it is not folded
        // into the sentence above and it is the only thing on this row allowed to be red.
        if (project.BlockedTasks > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = $"  ·  {project.BlockedTasks} blocked",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return line;
    }

    private static TextBlock SectionLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x78, 0x85)),
            Margin = new Thickness(0, 0, 0, 6)
        };

    /// <summary>
    /// A date the user types, or leaves empty. Empty is a valid permanent answer and is never
    /// filled in from anything Ari knows: a project with no completion date is simply one that has
    /// not finished, and inventing a date would print it to a client.
    /// </summary>
    private UIElement LabelledDayBox(string label, string value, System.Action<string> onCommit)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        cell.Children.Add(SectionLabel(label));

        var box = new TextBox
        {
            Text = value,
            Style = (Style)FindResource("RpTextBox"),
            ToolTip = "Year-month-day, for example 2026-08-18. Leave empty if it has not happened."
        };

        var hint = new TextBlock
        {
            Text = "yyyy-mm-dd",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x64)),
            Margin = new Thickness(9, 0, 0, 0),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        box.TextChanged += (_, _) =>
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        void Commit()
        {
            string typed = box.Text.Trim();
            if (string.Equals(typed, value, System.StringComparison.Ordinal))
            {
                return;
            }

            // The store refuses anything that is not a real yyyy-MM-dd and stores empty instead, so
            // a half-typed date is never kept. Saying so here is kinder than silently blanking it.
            if (typed.Length > 0
                && !System.DateTime.TryParseExact(
                       typed,
                       "yyyy-MM-dd",
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None,
                       out _))
            {
                ProjectStatusText.Text = "That date needs to look like 2026-08-18. Nothing was saved.";
                box.Text = value;
                return;
            }

            onCommit(typed);
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

        var overlay = new Grid();
        overlay.Children.Add(box);
        overlay.Children.Add(hint);
        cell.Children.Add(overlay);
        return cell;
    }

    /// <summary>One of a small fixed set. Selected state is a filled chip, never a tick beside a word.</summary>
    private Button ChoiceChip(string label, bool selected, System.Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource(selected ? "RpPrimaryButton" : "RpQuietButton"),
            Margin = new Thickness(0, 0, 6, 6)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Phase 3.2 — writes ONE engagement field. Everything not passed stays null, and the endpoint
    /// applies a field only when it is present, so saving a status can never disturb a date and
    /// saving a date can never disturb the recipient list.
    /// </summary>
    private async Task SetProjectEngagementAsync(
        long id,
        string? status = null,
        string? startDay = null,
        string? completionDay = null,
        IReadOnlyList<string>? recipients = null,
        string? reportingFrequency = null)
    {
        LocalApiProjectResponse? response = await _apiClient
            .UpdateProjectEngagementAsync(new LocalApiProjectEngagementRequest(
                SchemaVer: 1,
                Id: id,
                Status: status,
                StartDay: startDay,
                CompletionDay: completionDay,
                Recipients: recipients,
                ReportingFrequency: reportingFrequency))
            .ConfigureAwait(true);

        if (response is not { Success: true })
        {
            // Never claim a save that did not happen.
            ProjectStatusText.Text = "Couldn't save that — the agent didn't answer. Nothing was changed.";
            return;
        }

        ProjectStatusText.Text = "Saved.";
        await RefreshProjectsAsync().ConfigureAwait(true);
    }

}
