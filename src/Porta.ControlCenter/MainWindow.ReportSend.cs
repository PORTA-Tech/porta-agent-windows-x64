namespace Porta.ControlCenter;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;

/// <summary>
/// Step 10 phase C/D — the confirm step, wired.
///
/// Everything that decides whether a report may leave lives in the agent, not here: the preflight,
/// the record-before-send, the idempotency, the mandatory preview. This file collects what the user
/// typed, shows what came back, and is careful about one thing above all —
///
/// <b>an unknown outcome is never presented as a failure.</b> If the request reached the server and
/// the connection died, the mail may already be in the client's inbox. Telling the user it failed
/// invites a second press, and the second press is what a paying client sees twice.
/// </summary>
public partial class MainWindow
{
    private string _confirmRecipientEmail = string.Empty;
    private string _confirmRecipientName = string.Empty;
    private bool _confirmPreviewed;
    private bool _confirmSending;
    private string _previewFilePath = string.Empty;
    private string _sentReportUrl = string.Empty;

    /// <summary>
    /// Load the address book into the picker. Local only — this list never leaves the machine
    /// (§12.4), and only the one address on a report being sent ever does.
    /// </summary>
    private async Task LoadRecipientsAsync()
    {
        LocalApiReportRecipientsResponse? response = await _apiClient
            .GetRecipientsAsync()
            .ConfigureAwait(true);

        IReadOnlyList<LocalApiReportRecipient> recipients =
            response?.Recipients ?? Array.Empty<LocalApiReportRecipient>();

        ReportRecipientPicker.ItemsSource = recipients.Select(FormatRecipient).ToList();
    }

    private static string FormatRecipient(LocalApiReportRecipient r)
        => r.Name.Length > 0 ? $"{r.Name} <{r.Email}>" : r.Email;

    /// <summary>
    /// Accepts either a bare address or `Name &lt;address&gt;`, because the picker is editable and a
    /// person will paste whichever they have.
    /// </summary>
    private static (string Name, string Email) ParseRecipient(string typed)
    {
        string value = (typed ?? string.Empty).Trim();
        int open = value.LastIndexOf('<');
        int close = value.LastIndexOf('>');

        if (open >= 0 && close > open)
        {
            return (value[..open].Trim(), value[(open + 1)..close].Trim());
        }

        return (string.Empty, value);
    }

    /// <summary>
    /// The gate in front of the confirm screen.
    ///
    /// It used to check only that the box was not empty, so a bare name walked straight through as
    /// if it were an address and the refusal did not arrive until two screens later, from the
    /// preflight, phrased as a send failure. The rule here is now
    /// <see cref="ReportSendPreflight.LooksLikeAddress"/> — the same one the preflight and the
    /// control plane apply — so a typo is caught in the box the user is still looking at.
    /// </summary>
    private void ReportSendToButton_Click(object sender, RoutedEventArgs e)
    {
        (string name, string email) = ParseRecipient(ReportRecipientPicker.Text);

        if (email.Length == 0)
        {
            ReportRecipientNote.Text = "Type the client's email address first.";
            return;
        }

        // The one that is a hard block rather than a typo: a @porta.internal address is Ari's own
        // and can never receive mail, so a report sent there is silently lost.
        if (ReportSendPreflight.IsSynthetic(email))
        {
            ReportRecipientNote.Text =
                $"“{email}” is an internal Ari address, not a real client — mail sent there goes nowhere.";
            return;
        }

        if (!ReportSendPreflight.LooksLikeAddress(email))
        {
            // No example address here, deliberately: NoPersonalIdentifiersShipTests fails the build
            // on any email-shaped literal in shipped code, because an address in a string is an
            // address someone pastes into a payload later.
            ReportRecipientNote.Text =
                $"“{email}” isn't an email address — it needs an @ with the client's domain after it.";
            return;
        }

        ReportRecipientNote.Text = "Type a client's email, or pick one you've sent to before. Nothing is sent until you confirm.";
        _ = OpenReportConfirmAsync(email, name);
    }

    /// <summary>Open the confirm step for the drafted report.</summary>
    private async Task OpenReportConfirmAsync(string recipientEmail, string recipientName)
    {
        if (_reportDraft is not { } draft)
        {
            return;
        }

        _confirmRecipientEmail = (recipientEmail ?? string.Empty).Trim();
        _confirmRecipientName = (recipientName ?? string.Empty).Trim();
        _confirmPreviewed = false;
        _previewFilePath = string.Empty;
        ConfirmPreviewPathText.Visibility = Visibility.Collapsed;
        ConfirmPreviewPathText.Text = string.Empty;

        LocalApiSendContext? context = await _apiClient
            .GetSendContextAsync(_confirmRecipientEmail)
            .ConfigureAwait(true);

        ReportDraftPanel.Visibility = Visibility.Collapsed;
        ReportConfirmPanel.Visibility = Visibility.Visible;

        ReportConfirmSubtitle.Text =
            "Check the addresses. Sending cannot be undone, and this is the last screen before your " +
            "client sees the report.";

        ConfirmFromNameBox.Text = context?.FromName ?? string.Empty;
        ConfirmReplyToBox.Text = context?.ReplyTo ?? string.Empty;

        // THE AGENT CANNOT READ THE ACCOUNT PROFILE. ReportGateway.ReadIdentityAsync returns both of
        // these as empty by design — §12.2 put the sender identity on the server so there is only one
        // copy of it — but the local preflight refuses an empty From name and an empty Reply-to, and
        // the confirm screen sources both from the agent. So they arrive blank, every time, and the
        // send is refused until the user fills them in. Saying so is the difference between a form
        // that looks broken and one the user can finish. See the note handed over with 1.2.155: the
        // durable fix is for the agent to learn these once, and that is a decision, not a tidy-up.
        bool senderUnknown =
            string.IsNullOrWhiteSpace(ConfirmFromNameBox.Text)
            || string.IsNullOrWhiteSpace(ConfirmReplyToBox.Text);

        ConfirmSenderNote.Text = senderUnknown
            ? "Ari couldn't read your name and email from your account, so fill them in here. "
              + "They are what your client sees and replies to."
            : "Your client reads your name first, and replying reaches you. Changes here are "
              + "remembered — you won't be asked again.";
        ConfirmToBox.Text = _confirmRecipientName.Length > 0
            ? $"{_confirmRecipientName} · {_confirmRecipientEmail}"
            : _confirmRecipientEmail;

        // NEVER fall back to the local project name. It made a missing client-facing name look
        // identical to a set one on the last screen before a client sees the document — and the
        // local name is the private one ("nolan - chase invoice"), which is the exact thing §12.7
        // exists to keep out of the header. A missing name now says so.
        ConfirmDocumentHeadline.Text = draft.ClientName.Length > 0
            ? draft.ClientName
            : "This project has no name your client will recognise";
        ConfirmDocumentHeadline.Foreground = draft.ClientName.Length > 0
            ? (System.Windows.Media.Brush)FindResource("RpTextPrimary")
            : (System.Windows.Media.Brush)FindResource("RpTextMuted");
        ConfirmDocumentRange.Text = $"{FriendlyDate(draft.RangeStartLocal)} – {FriendlyDate(draft.RangeEndLocal)}";
        ConfirmDocumentFigures.Text =
            $"{draft.TotalHours.ToString("0.0", CultureInfo.InvariantCulture)}h   ·   " +
            $"{draft.DaysWorked} days worked   ·   " +
            $"{draft.LinesFinished} of {draft.Lines.Count} completed";

        // Mandatory for a first send to a new client, optional afterwards. The agent enforces it
        // too, so this is the courtesy of saying so rather than the enforcement.
        bool previewRequired = context?.PreviewRequired ?? true;
        UpdateConfirmSendState(previewRequired);
    }

    private void UpdateConfirmSendState(bool previewRequired)
    {
        bool blocked = previewRequired && !_confirmPreviewed;

        ConfirmSendButton.IsEnabled = !blocked && !_confirmSending;
        ConfirmRefusalPanel.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;

        if (blocked)
        {
            ConfirmRefusalText.Text =
                "· you haven't sent to this client before, so look at the email first";
            ConfirmRefusalHelp.Text =
                "Press “Preview what they receive”. It opens the real email in your browser — the " +
                "same rendering that would be sent, not an approximation.";
            ConfirmSendNote.Text = string.Empty;
        }
        else
        {
            ConfirmSendNote.Text = _confirmSending
                ? "Sending…"
                : "Goes out immediately. There is no undo.";
        }
    }

    private LocalApiReportSendRequest BuildSendRequest()
        => new(
            SchemaVer: 1,
            ProjectId: _reportDraft?.ProjectId ?? 0,
            From: _reportDraft?.RangeStartLocal ?? string.Empty,
            To: _reportDraft?.RangeEndLocal ?? string.Empty,
            RecipientEmail: _confirmRecipientEmail,
            RecipientName: _confirmRecipientName,
            FromName: ConfirmFromNameBox.Text.Trim(),
            ReplyTo: ConfirmReplyToBox.Text.Trim(),
            ExcludedTaskIds: _excludedLines.ToList(),
            Previewed: _confirmPreviewed,
            // Whatever the user left in the box. Ari drafted it; the user owns it.
            Summary: ReportSummaryBox.Text.Trim());

    /// <summary>
    /// Phase 2b — copy the permanent link. The clipboard is the whole point: the user sends it
    /// wherever they like, and the report needs no email at all.
    /// </summary>
    private void ReportCopyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sentReportUrl.Length == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_sentReportUrl);
            ReportStatusText.Text = "Link copied. Anyone with it can open the report.";
        }
        catch
        {
            // The clipboard can be held by another process. The link is on screen either way.
            ReportStatusText.Text = "Couldn't reach the clipboard — the link is shown above.";
        }
    }

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ReportConfirmPanel.Visibility = Visibility.Collapsed;
        ReportDraftPanel.Visibility = Visibility.Visible;
    }

    private void ConfirmPreviewButton_Click(object sender, RoutedEventArgs e) => _ = PreviewReportAsync();

    /// <summary>
    /// The SERVER renders it and we open that. Never a local re-render: a preview that could differ
    /// from what is sent would be a promise quietly broken.
    /// </summary>
    private async Task PreviewReportAsync()
    {
        // CLEAR THE LAST FAILURE FIRST. The panel used to persist across attempts, so a refusal from
        // an earlier press — "Reply-to is not an email address", back when the box was still empty —
        // stayed on screen after the user had fixed it and the next preview had succeeded. A stale
        // reason is worse than no reason: it sends you to fix something that is already right.
        ConfirmRefusalPanel.Visibility = Visibility.Collapsed;
        ConfirmRefusalText.Text = string.Empty;
        ConfirmRefusalHelp.Text = string.Empty;
        ConfirmPreviewPathText.Visibility = Visibility.Collapsed;
        ConfirmSendNote.Text = "Rendering…";

        LocalApiReportPreviewResponse? preview = await _apiClient
            .PreviewReportAsync(BuildSendRequest())
            .ConfigureAwait(true);

        if (preview is null || !preview.Success || string.IsNullOrWhiteSpace(preview.Html))
        {
            ConfirmRefusalPanel.Visibility = Visibility.Visible;
            ConfirmRefusalText.Text = "· " + PreviewFailureSentence(preview?.Reason, preview?.Refusals);
            ConfirmRefusalHelp.Text = "Nothing has been sent.";
            ConfirmSendNote.Text = string.Empty;
            return;
        }

        // WRITTEN HERE, IN CONTROL CENTER'S OWN TEMP, because this is the process that has to open
        // it. The agent used to write it as LocalSystem into C:\Windows\SystemTemp, where the user
        // could neither read nor open it.
        string path;
        try
        {
            path = ReportPreviewFile.Write(preview.Html!);
        }
        catch (Exception ex)
        {
            ConfirmRefusalPanel.Visibility = Visibility.Visible;
            ConfirmRefusalText.Text = "· the email rendered, but Ari couldn't save it to open it";
            ConfirmRefusalHelp.Text = ex.Message + " Nothing has been sent.";
            ConfirmSendNote.Text = string.Empty;
            return;
        }

        try
        {
            // The browser, not an embedded view: it is the closest thing to what a client's mail
            // client will do with the same HTML, and it needs no new dependency.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // A browser that will not open must not become a dead end. It used to `return` here
            // WITHOUT setting _confirmPreviewed, so Send stayed disabled for the rest of the
            // session with no way forward — the report was rendered and unsendable at the same
            // time. Explorer is the fallback; if that fails too, the path is shown and the user can
            // open it by hand, which still satisfies "look at it before a client does".
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Nothing else to try; the path below is the escape.
            }

            _previewFilePath = path;
            ConfirmSendNote.Text = "Couldn't open your browser — the email is saved at the path below.";
            ConfirmPreviewPathText.Text = path;
            ConfirmPreviewPathText.Visibility = Visibility.Visible;
        }

        _confirmPreviewed = true;
        UpdateConfirmSendState(previewRequired: true);
    }

    private void ConfirmSendButton_Click(object sender, RoutedEventArgs e) => _ = SendReportAsync();

    private async Task SendReportAsync()
    {
        if (_confirmSending)
        {
            return;
        }

        _confirmSending = true;
        ConfirmSendButton.IsEnabled = false;
        ConfirmSendNote.Text = "Sending…";

        LocalApiReportSendResponse? response = await _apiClient
            .SendReportAsync(BuildSendRequest())
            .ConfigureAwait(true);

        _confirmSending = false;

        if (response is null)
        {
            // The local agent could not be reached, so nothing was recorded and nothing was sent.
            // This one IS safe to retry.
            ConfirmSendButton.IsEnabled = true;
            ShowConfirmProblem("· can't reach Ari, so nothing was sent", "Try again in a moment.");
            return;
        }

        if (response.Success)
        {
            ReportConfirmPanel.Visibility = Visibility.Collapsed;
            ReportDraftPanel.Visibility = Visibility.Visible;
            ReportStatusText.Text =
                $"Sent to {_confirmRecipientEmail}. A record is kept on this machine, and the report " +
                "can be deleted from both sides later.";

            // Phase 2b — the permanent link, offered for copying so a user can send it through
            // WhatsApp or Slack without an email at all.
            _sentReportUrl = response.ReportUrl ?? string.Empty;
            if (_sentReportUrl.Length > 0)
            {
                ReportLinkText.Text = _sentReportUrl;
                ReportLinkRow.Visibility = Visibility.Visible;
            }

            // Remember the client so the address is never retyped.
            _ = _apiClient.SaveRecipientAsync(_confirmRecipientName, _confirmRecipientEmail);
            return;
        }

        // THE CASE THAT MATTERS. The request reached the server and we never learned the outcome.
        // The report may be in the client's inbox. Do not offer a retry, and do not call it failed.
        if (string.Equals(response.Reason, "unknown", StringComparison.Ordinal))
        {
            ConfirmSendButton.IsEnabled = false;
            ShowConfirmProblem(
                "· the connection dropped before Ari heard back, so it is not known whether this was sent",
                "Check the client's inbox, or your sent reports, before trying again — sending twice " +
                "cannot be undone. The report is recorded here as still pending.");
            return;
        }

        ConfirmSendButton.IsEnabled = true;
        ShowConfirmProblem("· " + SendFailureSentence(response.Reason, response.Refusals), "Nothing has been sent.");
    }

    private void ShowConfirmProblem(string what, string help)
    {
        ConfirmRefusalPanel.Visibility = Visibility.Visible;
        ConfirmRefusalText.Text = what;
        ConfirmRefusalHelp.Text = help;
        ConfirmSendNote.Text = string.Empty;
    }

    /// <summary>
    /// EVERY reason either side can return, and the raw code when one is not listed.
    ///
    /// The old version listed four and sent everything else to "the email couldn't be rendered".
    /// That single sentence stood for at least five distinct failures — most of them local refusals
    /// that never reached the server at all — and a failure that cannot be identified costs an hour
    /// every time it appears. The fallback now prints the code, so an unlisted reason is a five
    /// second lookup instead of an investigation.
    /// </summary>
    private static string PreviewFailureSentence(string? reason, IReadOnlyList<string>? refusals)
    {
        // "refused" means the local preflight stopped it before the server was ever asked. The list
        // is the answer; the word on its own is not.
        if (refusals is { Count: > 0 })
        {
            return string.Join("; ", refusals.Select(RefusalCodeSentence));
        }

        return reason switch
        {
            // --- local, from the agent -------------------------------------------------------
            "refused" =>
                "Ari stopped this before asking the server, but didn't say which rule — "
                + "check the draft and the three addresses (reason: refused, no detail)",
            "invalid_payload_or_schema" => "Ari and Control Center disagree about the request format (reason: invalid_payload_or_schema)",
            "project_and_range_required" => "the project or the date range is missing (reason: project_and_range_required)",
            "invalid_report" => "the report couldn't be assembled from this draft (reason: invalid_report)",
            "preview_failed" => "the server accepted the request but returned no email (reason: preview_failed)",
            "unreachable" => "Ari couldn't reach the server to render the email (reason: unreachable)",
            "error" => "something failed inside Ari while rendering (reason: error)",

            // --- server, from the control plane ----------------------------------------------
            "account_id_required" => "this machine sent no account id, so the server refused (reason: account_id_required)",
            "account_not_found" => "the server has no account for this machine (reason: account_not_found)",
            "from_name_missing" =>
                "your account has no display name on the server, so the report would arrive from nobody "
                + "(reason: from_name_missing)",
            "reply_to_synthetic" =>
                "Reply-to on your account is an internal Ari address that cannot receive mail. If your "
                + "client replied, the reply would vanish and neither of you would know "
                + "(reason: reply_to_synthetic)",
            "reply_to_invalid" => "Reply-to on your account is not an email address (reason: reply_to_invalid)",
            "recipient_invalid" => "the recipient isn't a working email address (reason: recipient_invalid)",

            null or "" => "the email couldn't be rendered, and no reason was given",

            // client_disabled carries its own why after a colon, so print it whole.
            _ when reason.StartsWith("client_disabled", StringComparison.Ordinal) =>
                $"Ari's connection to the server is switched off ({reason})",

            // NEVER a bare generic sentence again.
            _ => $"the email couldn't be rendered (reason: {reason})"
        };
    }

    /// <summary>Same contract as <see cref="PreviewFailureSentence"/>: name everything, print the code otherwise.</summary>
    private static string SendFailureSentence(string? reason, IReadOnlyList<string>? refusals)
    {
        if (refusals is { Count: > 0 })
        {
            return string.Join("; ", refusals.Select(RefusalCodeSentence));
        }

        return reason switch
        {
            // --- local ------------------------------------------------------------------------
            "refused" =>
                "Ari stopped this before asking the server, but didn't say which rule "
                + "(reason: refused, no detail)",
            "preview_required" => "look at the email first — this is the first report to this client (reason: preview_required)",
            "already_recorded" => "this report has already been recorded, so it was not sent again (reason: already_recorded)",
            "invalid_payload_or_schema" => "Ari and Control Center disagree about the request format (reason: invalid_payload_or_schema)",
            "project_and_range_required" => "the project or the date range is missing (reason: project_and_range_required)",
            "invalid_report" => "the report couldn't be assembled from this draft (reason: invalid_report)",
            "unreachable" => "Ari couldn't reach the server (reason: unreachable)",
            "error" => "something failed inside Ari while sending (reason: error)",

            // --- server -----------------------------------------------------------------------
            "already_sent" => "this report was already sent, so it was not sent a second time (reason: already_sent)",
            "send_failed" => "the mail service rejected it (reason: send_failed)",
            "missing_required_fields" => "the request reached the server without everything it needs (reason: missing_required_fields)",
            "account_id_required" => "this machine sent no account id, so the server refused (reason: account_id_required)",
            "account_not_found" => "the server has no account for this machine (reason: account_not_found)",
            "from_name_missing" =>
                "your account has no display name on the server, so the report would arrive from nobody "
                + "(reason: from_name_missing)",
            "reply_to_synthetic" =>
                "Reply-to on your account is an internal Ari address that cannot receive mail "
                + "(reason: reply_to_synthetic)",
            "reply_to_invalid" => "Reply-to on your account is not an email address (reason: reply_to_invalid)",
            "recipient_invalid" => "the recipient isn't a working email address (reason: recipient_invalid)",
            "not_your_report" => "the server does not have this report under your account (reason: not_your_report)",

            null or "" => "it couldn't be sent, and no reason was given",

            _ when reason.StartsWith("client_disabled", StringComparison.Ordinal) =>
                $"Ari's connection to the server is switched off ({reason})",

            _ => $"it couldn't be sent (reason: {reason})"
        };
    }

    /// <summary>
    /// The local preflight's codes. The fallback prints the code rather than swallowing it, for the
    /// same reason as above — a new refusal added to the enum must never render as silence.
    /// </summary>
    private static string RefusalCodeSentence(string code)
        => code switch
        {
            "ReplyToIsSynthetic" =>
                "Reply-to is an internal Ari address that cannot receive mail — put your own address in",
            "ReplyToInvalid" => "Reply-to is not an email address, so replies would go nowhere",
            "RecipientIsSynthetic" => "the recipient is an internal Ari address, not a real client",
            "RecipientInvalid" => "the recipient is not an email address",
            "FromNameMissing" => "your name is empty, so the report would arrive from nobody",
            "DocumentNotReady" =>
                "the report still has something unnamed — the project needs a name your client will "
                + "recognise, and so does every line. Go back to the draft",
            _ => $"refused: {code}"
        };
}
