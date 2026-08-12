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
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = project.Name,
            FontSize = 13,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1A)),
            Foreground = new SolidColorBrush(project.Archived
                ? Color.FromRgb(0x64, 0x74, 0x8B)
                : Color.FromRgb(0xE2, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
            BorderThickness = new Thickness(1)
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

        // Archive, never delete. The project's tasks keep pointing at it and some of that time has
        // already been reported; deleting would orphan measured minutes.
        var toggle = new Button
        {
            Content = project.Archived ? "Restore" : "Archive",
            FontSize = 12,
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Click += (_, _) => _ = SetProjectArchivedAsync(project.Id, !project.Archived);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
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
