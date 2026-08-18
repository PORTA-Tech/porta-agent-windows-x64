using System.Linq;
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
        // Phase 3 rebuilt — Projects and Tasks are MAIN pages now, not settings sections. Routing
        // them through Settings is why every projects render came out byte-identical and every
        // page shot came out blank: the panel being photographed was never made visible.
        switch (settingsSection)
        {
            case "Projects":
                ShowSection(UiSection.Projects);
                break;
            case "Tasks":
                ShowSection(UiSection.Tasks);
                break;
            default:
                ShowSection(UiSection.Settings);
                ShowSettingsSection(settingsSection);
                break;
        }

        fill(this);
        UpdateLayout();
    }

    /// <summary>
    /// The page body on its own, so it can be rendered at its natural height. A window is clamped
    /// to the work area, so a long report is cut off in a full-window shot no matter how tall the
    /// window is asked to be.
    /// </summary>
    internal FrameworkElement PreviewPageBody(string settingsSection)
        => settingsSection switch
        {
            "Projects" => ProjectsPageHost,
            "Tasks" => TasksPageHost,
            _ => ReportsPanel
        };

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

    /// <summary>
    /// The confirm step (§6b), filled the way the real send path will fill it. Rendered before it is
    /// wired up, because every UI surface this week has needed a second pass and this is the last
    /// screen before a client sees anything.
    /// </summary>
    internal void PreviewFillConfirm(
        string fromName,
        string replyTo,
        string recipientEmail,
        string recipientName,
        Porta.Agent.Core.Outputs.ReportSendVerdict verdict,
        LocalApiReportDraft draft)
    {
        ReportDraftPanel.Visibility = Visibility.Collapsed;
        ReportConfirmPanel.Visibility = Visibility.Visible;

        ReportConfirmSubtitle.Text =
            "Check the addresses. Sending cannot be undone, and this is the last screen before your " +
            "client sees the report.";

        ConfirmFromNameBox.Text = fromName;
        ConfirmReplyToBox.Text = replyTo;
        ConfirmToBox.Text = recipientName.Length > 0
            ? $"{recipientName} · {recipientEmail}"
            : recipientEmail;

        // Same rule as the live confirm screen.
        ConfirmSenderNote.Text =
            string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(replyTo)
                ? "Ari couldn't read your name and email from your account, so fill them in here. "
                  + "They are what your client sees and replies to."
                : "Your client reads your name first, and replying reaches you. Changes here are "
                  + "remembered — you won't be asked again.";

        // Same rule as the live confirm screen: a missing client-facing name must look missing.
        ConfirmDocumentHeadline.Text = draft.ClientName.Length > 0
            ? draft.ClientName
            : "This project has no name your client will recognise";
        ConfirmDocumentHeadline.Foreground = draft.ClientName.Length > 0
            ? (System.Windows.Media.Brush)FindResource("RpTextPrimary")
            : (System.Windows.Media.Brush)FindResource("RpTextMuted");
        ConfirmDocumentRange.Text =
            $"{FriendlyDate(draft.RangeStartLocal)} – {FriendlyDate(draft.RangeEndLocal)}";
        ConfirmDocumentFigures.Text =
            $"{draft.TotalHours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}h   ·   " +
            $"{draft.DaysWorked} days worked   ·   " +
            $"{draft.LinesFinished} of {draft.Lines.Count} completed";

        bool refused = !verdict.CanSend;
        ConfirmRefusalPanel.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
        ConfirmSendButton.IsEnabled = !refused;

        if (refused)
        {
            ConfirmRefusalText.Text = string.Join(
                "\n",
                verdict.Refusals.Select(RefusalSentence).Select(s => "· " + s));
            ConfirmRefusalHelp.Text =
                "Fix it above and this clears. Nothing has been sent.";
            ConfirmSendNote.Text = string.Empty;
        }
        else
        {
            ConfirmSendNote.Text = "Goes out immediately. There is no undo.";
        }

        UpdateLayout();
    }

    /// <summary>
    /// The words for each refusal. The rule lives in <c>ReportSendPreflight</c> as a code; the
    /// sentence lives here, where the user reads it.
    /// </summary>
    private static string RefusalSentence(Porta.Agent.Core.Outputs.ReportSendRefusal refusal)
        => refusal switch
        {
            Porta.Agent.Core.Outputs.ReportSendRefusal.DocumentNotReady =>
                "the report still has something unnamed — go back to the draft",
            Porta.Agent.Core.Outputs.ReportSendRefusal.FromNameMissing =>
                "your name is empty, so the report would arrive from nobody",
            // The one that is a refusal rather than a warning. Say what would actually happen,
            // not that a rule was violated.
            Porta.Agent.Core.Outputs.ReportSendRefusal.ReplyToIsSynthetic =>
                "Reply-to is an internal Ari address that cannot receive mail. If your client " +
                "replied, the reply would vanish and neither of you would know. Put your own " +
                "email address in Reply-to.",
            Porta.Agent.Core.Outputs.ReportSendRefusal.ReplyToInvalid =>
                "Reply-to is not an email address, so replies would go nowhere",
            Porta.Agent.Core.Outputs.ReportSendRefusal.RecipientIsSynthetic =>
                "the recipient is an internal Ari address, not a real client",
            Porta.Agent.Core.Outputs.ReportSendRefusal.RecipientInvalid =>
                "the recipient is not an email address",
            _ => "something is not right yet"
        };

    internal void PreviewFillProjects(IReadOnlyList<LocalApiProject> projects)
        => RenderProjectsPage(projects);

    /// <summary>The same page with every drawer open, so the form itself can be looked at.</summary>
    internal void PreviewFillProjectsWithDrawer(IReadOnlyList<LocalApiProject> projects)
    {
        foreach (LocalApiProject p in projects)
        {
            _openProjectDrawers.Add(p.Id);
        }

        RenderProjectsPage(projects);
    }

    /// <summary>The Tasks page at a chosen filter, so a broken filter is visible in a picture.</summary>
    internal void PreviewFillTasks(IReadOnlyList<LocalApiTaskRow> tasks, string filter)
    {
        _taskFilter = filter;
        RenderTasksPage(tasks);
    }

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

    /// <summary>
    /// Step 10 (2026-08-17) — SEND TO, CHECKED RATHER THAN LOOKED AT.
    ///
    /// A picture cannot show a caret. The field rendered perfectly while being impossible to type
    /// into, because <c>RpComboBox</c> had no <c>PART_EditableTextBox</c> and <c>IsEditable="True"</c>
    /// is silently inert without it — a screenshot of that bug and a screenshot of the fix are the
    /// same image. This is the same test gap that let the caret bug through in §5: every focus test
    /// asserted which control was armed and none asserted that the caret actually arrives.
    ///
    /// So this asserts the mechanism, not the appearance:
    /// the part exists, it can take focus and text, the text reaches the ComboBox, and the chevron
    /// does not sit over the field. It also checks the PROJECT picker still has no editable part,
    /// which is the guard that the shared style was left alone rather than "fixed" for both.
    /// </summary>
    /// <summary>
    /// Step 10 (2026-08-17) — the gate in front of the confirm screen, and the words on a failure.
    ///
    /// Both are things a picture cannot show and both cost real time when wrong: a bare name walked
    /// through SEND TO as if it were an address, and every distinct failure printed the same
    /// sentence. These assert the rule and the vocabulary rather than the appearance.
    /// </summary>
    internal IReadOnlyList<(string Check, bool Ok, string Detail)> PreviewSendGateChecks()
    {
        var results = new List<(string, bool, string)>();

        // --- SEND TO applies the server's address rule, not "is it non-empty" -----------------
        foreach ((string typed, bool shouldPass) in new[]
                 {
                     ("Joel", false),
                     ("Joel Nolan", false),
                     ("joel@", false),
                     ("joel@example", false),
                     ("two@at@example.test", false),
                     ("joel@example.test.", false),
                     ("displaced-acct@porta.internal", false),
                     ("joel@example.test", true)
                 })
        {
            bool accepted = Porta.Agent.Core.Outputs.ReportSendPreflight.LooksLikeAddress(typed)
                            && !Porta.Agent.Core.Outputs.ReportSendPreflight.IsSynthetic(typed);
            results.Add(($"SEND TO {(shouldPass ? "accepts" : "rejects")} \"{typed}\"",
                accepted == shouldPass, accepted ? "accepted" : "rejected"));
        }

        // The handler itself, not just the rule: a rejected value must not open the confirm screen.
        ReportConfirmPanel.Visibility = Visibility.Collapsed;
        ReportRecipientPicker.Text = "Joel";
        ReportSendToButton_Click(this, new RoutedEventArgs());
        results.Add(("a bare name does not open the confirm screen",
            ReportConfirmPanel.Visibility != Visibility.Visible,
            ReportRecipientNote.Text));
        ReportRecipientPicker.Text = string.Empty;

        // --- every reason is named, and an unknown one prints its code ------------------------
        string[] reasons =
        [
            "refused", "invalid_payload_or_schema", "project_and_range_required", "invalid_report",
            "preview_failed", "unreachable", "error", "account_id_required", "account_not_found",
            "from_name_missing", "reply_to_synthetic", "reply_to_invalid", "recipient_invalid",
            "client_disabled:no_credential"
        ];

        int unnamed = 0;
        foreach (string r in reasons)
        {
            string sentence = PreviewFailureSentence(r, null);
            // "Named" means it produced something other than the bare generic fallback.
            if (sentence.StartsWith("the email couldn't be rendered (reason:", StringComparison.Ordinal))
            {
                unnamed++;
            }
        }

        results.Add(($"preview names all {reasons.Length} known reasons", unnamed == 0,
            unnamed == 0 ? "all named" : $"{unnamed} fell through to the generic sentence"));

        string unknown = PreviewFailureSentence("some_new_server_reason", null);
        results.Add(("an unknown reason prints its raw code",
            unknown.Contains("some_new_server_reason", StringComparison.Ordinal), unknown));

        string refusedList = PreviewFailureSentence("refused", new[] { "DocumentNotReady", "FromNameMissing" });
        results.Add(("\"refused\" resolves to the actual refusals",
            refusedList.Contains("unnamed", StringComparison.Ordinal)
            && refusedList.Contains("nobody", StringComparison.Ordinal), refusedList));

        string sendUnknown = SendFailureSentence("some_new_send_reason", null);
        results.Add(("send: an unknown reason prints its raw code",
            sendUnknown.Contains("some_new_send_reason", StringComparison.Ordinal), sendUnknown));

        string newRefusal = PreviewFailureSentence("refused", new[] { "SomeFutureRefusal" });
        results.Add(("an unmapped refusal code is printed, not swallowed",
            newRefusal.Contains("SomeFutureRefusal", StringComparison.Ordinal), newRefusal));

        return results;
    }

    internal IReadOnlyList<(string Check, bool Ok, string Detail)> PreviewSendToCaretChecks()
    {
        var results = new List<(string, bool, string)>();

        ReportRecipientPicker.ApplyTemplate();
        var edit = ReportRecipientPicker.Template
            .FindName("PART_EditableTextBox", ReportRecipientPicker) as TextBox;

        results.Add(("PART_EditableTextBox exists", edit is not null,
            edit is null ? "missing — typing is impossible by construction" : "found"));

        if (edit is not null)
        {
            results.Add(("field accepts input", !edit.IsReadOnly && edit.Focusable && edit.IsHitTestVisible,
                $"readonly={edit.IsReadOnly} focusable={edit.Focusable} hittest={edit.IsHitTestVisible}"));

            bool took = edit.Focus();
            results.Add(("caret lands in the field", took && edit.IsKeyboardFocused,
                $"Focus()={took} keyboardFocused={edit.IsKeyboardFocused}"));

            edit.Text = "accounts@example-client.test";
            results.Add(("typed text reaches the ComboBox",
                ReportRecipientPicker.Text == "accounts@example-client.test",
                $"picker.Text=\"{ReportRecipientPicker.Text}\""));

            // ParseRecipient is what Continue actually calls; prove the typed value survives it.
            (string name, string email) = ParseRecipient(ReportRecipientPicker.Text);
            results.Add(("Continue would accept it", email.Length > 0,
                $"name=\"{name}\" email=\"{email}\""));

            edit.Text = string.Empty;
        }

        // The chevron must be its own 30-DIP column. In RpComboBox it spans the control, which is
        // why every click there opens the list instead of landing a caret.
        var toggle = ReportRecipientPicker.Template
            .FindName("PART_Toggle", ReportRecipientPicker) as System.Windows.Controls.Primitives.ToggleButton;
        results.Add(("chevron does not cover the field",
            toggle is not null && Grid.GetColumn(toggle) == 1 && Grid.GetColumnSpan(toggle) == 1,
            toggle is null ? "PART_Toggle missing" : $"column={Grid.GetColumn(toggle)} span={Grid.GetColumnSpan(toggle)}"));

        // The shared style must NOT have gained an editable part: the project picker is correct as
        // it is, and must stay untypable.
        ReportProjectPicker.ApplyTemplate();
        bool projectStillPlain = ReportProjectPicker.Template
            .FindName("PART_EditableTextBox", ReportProjectPicker) is null;
        results.Add(("project picker left alone (RpComboBox untouched)", projectStillPlain,
            projectStillPlain ? "no editable part, as intended" : "shared style was modified"));

        return results;
    }
}
