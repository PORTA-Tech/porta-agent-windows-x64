using System.Windows;
using System.Windows.Controls;
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

    /// <summary>
    /// Open the "from" date picker's calendar and put it in a given mode, so the popup can be
    /// rendered. The page harness could not see inside a Popup — it is a separate visual tree in
    /// its own window — which is exactly why a calendar that rendered nothing at all shipped in
    /// 1.2.151. Anything that only appears on a click needs its own picture.
    /// </summary>
    internal System.Windows.Controls.Primitives.Popup PreviewOpenCalendar()
    {
        ReportFromPicker.IsDropDownOpen = true;
        UpdateLayout();

        return (System.Windows.Controls.Primitives.Popup)ReportFromPicker
            .Template.FindName("PART_Popup", ReportFromPicker);
    }

    /// <summary>
    /// Set the calendar's display mode AFTER the popup has finished opening. DatePicker forces
    /// DisplayMode back to Month as part of opening the drop-down, so setting it in the same breath
    /// is silently undone — which is how three "different" calendar shots came out identical.
    /// Returns the mode that actually took, so the harness can report a mismatch instead of writing
    /// a misleading picture.
    /// </summary>
    internal CalendarMode? PreviewSetCalendarMode(
        System.Windows.Controls.Primitives.Popup popup, CalendarMode mode)
    {
        if (popup.Child is not Calendar calendar)
        {
            return null;
        }

        calendar.DisplayMode = mode;
        calendar.UpdateLayout();
        UpdateLayout();
        return calendar.DisplayMode;
    }

    /// <summary>
    /// Drive a date through the CALENDAR, the way a person does, then close the drop-down. This is
    /// the path that broke the format in 1.2.151 and the one a programmatic SelectedDate never
    /// exercises. Returns what the two fields actually read afterwards.
    /// </summary>
    internal void PreviewPickThroughCalendar(DateTime date)
    {
        System.Windows.Controls.Primitives.Popup popup = PreviewOpenCalendar();
        if (popup.Child is Calendar calendar)
        {
            calendar.SelectedDate = date;
            calendar.UpdateLayout();
        }

        ReportFromPicker.IsDropDownOpen = false;
        UpdateLayout();
    }

    /// <summary>
    /// Read AFTER flushing the dispatcher — the reformat is queued, so reading in the same breath
    /// as the pick reports the value before it has been corrected.
    /// </summary>
    internal (string From, string To) PreviewPickerTexts
        => (PreviewPickerText(ReportFromPicker), PreviewPickerText(ReportToPicker));

    private static string PreviewPickerText(DatePicker picker)
        => picker.Template?.FindName("PART_TextBox", picker) is TextBox box
            ? box.Text
            : "<no textbox>";

    internal bool PreviewStaleNoticeShown => ReportStaleText.Visibility == Visibility.Visible;

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
