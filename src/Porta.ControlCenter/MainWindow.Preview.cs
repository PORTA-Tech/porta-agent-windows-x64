using System.Windows;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

/// <summary>
/// The seams <see cref="ReportsPagePreview"/> needs, and nothing else. They live on MainWindow so
/// the harness renders the real page through the real render paths — the moment a harness grows its
/// own copy of the layout, the picture stops being evidence of anything.
///
/// Nothing here is reachable from the UI, and none of it touches the agent.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Set only by <see cref="ReportsPagePreview"/>. Suppresses the Loaded-time diagnostics poll,
    /// which needs an agent, disposes between windows, and would overwrite the sample data the
    /// picture is being taken of.
    /// </summary>
    internal static bool PreviewMode { get; set; }

    /// <summary>
    /// Put the window on a settings sub-page and fill it with sample data, without showing it.
    /// The window is never shown, so <c>Loaded</c> never fires and no network call is made.
    /// </summary>
    internal void ShowPreviewPage(string settingsSection, Action<MainWindow> fill)
    {
        ShowSection(UiSection.Settings);
        ShowSettingsSection(settingsSection);
        fill(this);
        UpdateLayout();
    }

    /// <summary>
    /// The page body on its own, so it can be rendered at its natural height. A window is clamped
    /// to the work area, so a long report is cut off in a full-window shot no matter how tall the
    /// window is asked to be.
    /// </summary>
    internal FrameworkElement PreviewPageBody(string settingsSection)
        => settingsSection == "Projects" ? SettingsSectionProjects : SettingsSectionReports;

    internal void PreviewFillProjects(IReadOnlyList<LocalApiProject> projects)
        => RenderProjects(projects);

    internal void PreviewFillReport(LocalApiReportDraft draft)
    {
        // Sample projects in the picker too, so the picture never shows whatever the live agent
        // happens to hold.
        ReportProjectPicker.ItemsSource = new[]
        {
            new LocalApiProject(draft.ProjectId, draft.ProjectName, false, DateTimeOffset.UnixEpoch)
        };
        ReportProjectPicker.SelectedIndex = 0;
        ReportFromPicker.SelectedDate = DateTime.Parse(
            draft.RangeStartLocal, System.Globalization.CultureInfo.InvariantCulture);
        ReportToPicker.SelectedDate = DateTime.Parse(
            draft.RangeEndLocal, System.Globalization.CultureInfo.InvariantCulture);

        _reportDraft = draft;
        _excludedLines.Clear();
        ReportDraftPanel.Visibility = Visibility.Visible;
        ReportStatusText.Text = string.Empty;
        RenderReportDraft();
    }
}
