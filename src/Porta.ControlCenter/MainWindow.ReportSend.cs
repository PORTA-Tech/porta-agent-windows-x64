namespace Porta.ControlCenter;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

    private void ReportSendToButton_Click(object sender, RoutedEventArgs e)
    {
        (string name, string email) = ParseRecipient(ReportRecipientPicker.Text);

        if (email.Length == 0)
        {
            ReportRecipientNote.Text = "Type the client's email address first.";
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
        ConfirmToBox.Text = _confirmRecipientName.Length > 0
            ? $"{_confirmRecipientName} · {_confirmRecipientEmail}"
            : _confirmRecipientEmail;

        ConfirmDocumentHeadline.Text = draft.ClientName.Length > 0 ? draft.ClientName : draft.ProjectName;
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
            Previewed: _confirmPreviewed);

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
        ConfirmSendNote.Text = "Rendering…";

        LocalApiReportPreviewResponse? preview = await _apiClient
            .PreviewReportAsync(BuildSendRequest())
            .ConfigureAwait(true);

        if (preview is null || !preview.Success || string.IsNullOrWhiteSpace(preview.HtmlPath))
        {
            ConfirmRefusalPanel.Visibility = Visibility.Visible;
            ConfirmRefusalText.Text = "· " + PreviewFailureSentence(preview?.Reason);
            ConfirmRefusalHelp.Text = "Nothing has been sent.";
            ConfirmSendNote.Text = string.Empty;
            return;
        }

        try
        {
            // The browser, not an embedded view: it is the closest thing to what a client's mail
            // client will do with the same HTML, and it needs no new dependency.
            Process.Start(new ProcessStartInfo(preview.HtmlPath!) { UseShellExecute = true });
        }
        catch
        {
            ConfirmSendNote.Text = "Couldn't open your browser. The file is at " + preview.HtmlPath;
            return;
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

    private static string PreviewFailureSentence(string? reason)
        => reason switch
        {
            "reply_to_synthetic" =>
                "Reply-to is an internal Ari address that cannot receive mail. If your client " +
                "replied, the reply would vanish and neither of you would know.",
            "from_name_missing" => "your name is empty, so the report would arrive from nobody",
            "account_not_found" => "Ari can't find your account, so it can't tell a client who this is from",
            "unreachable" or "client_disabled" => "Ari couldn't reach the server to render the email",
            _ => "the email couldn't be rendered"
        };

    private static string SendFailureSentence(string? reason, IReadOnlyList<string>? refusals)
    {
        if (refusals is { Count: > 0 })
        {
            return string.Join("; ", refusals.Select(RefusalCodeSentence));
        }

        return reason switch
        {
            "preview_required" => "look at the email first — this is the first report to this client",
            "already_recorded" => "this report has already been recorded, so it was not sent again",
            "already_sent" => "this report was already sent, so it was not sent a second time",
            "send_failed" => "the mail service rejected it",
            "recipient_invalid" => "the recipient isn't a working email address",
            _ => "it couldn't be sent"
        };
    }

    private static string RefusalCodeSentence(string code)
        => code switch
        {
            "ReplyToIsSynthetic" =>
                "Reply-to is an internal Ari address that cannot receive mail — put your own address in",
            "ReplyToInvalid" => "Reply-to is not an email address, so replies would go nowhere",
            "RecipientIsSynthetic" => "the recipient is an internal Ari address, not a real client",
            "RecipientInvalid" => "the recipient is not an email address",
            "FromNameMissing" => "your name is empty, so the report would arrive from nobody",
            "DocumentNotReady" => "the report still has something unnamed — go back to the draft",
            _ => code
        };
}
