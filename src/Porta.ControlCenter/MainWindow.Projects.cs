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

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

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
}
