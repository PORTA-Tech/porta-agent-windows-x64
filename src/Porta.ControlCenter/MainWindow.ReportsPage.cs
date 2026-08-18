namespace Porta.ControlCenter;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Porta.Agent.LocalApi;

/// <summary>
/// Phase 3 rebuilt (2026-08-18) — the Reports page, built to the mockup.
///
/// It opens with what is WAITING, because that is the only thing on this page that needs the user:
/// Ari has drafted on the agreed rhythm, and it says how many result lines are still missing before
/// the draft is worth opening. Everything else is history.
///
/// A sent report is frozen with its permanent link. Nothing here sends anything — Ari drafts,
/// Yadin taps, and no button on this page changes that.
/// </summary>
public partial class MainWindow
{
    private string _reportTab = "sent";

    private static readonly (string Key, string Label)[] ReportTabs =
    {
        ("sent", "Sent"), ("ready", "Ready to send"), ("arch", "Archived")
    };

    private async Task OpenReportsPageV2Async()
    {
        try
        {
            LocalApiSentReportsResponse? sent = await _apiClient.GetSentReportsAsync().ConfigureAwait(true);
            LocalApiProjectsResponse? projects = await _apiClient
                .GetProjectsAsync(includeArchived: false).ConfigureAwait(true);

            RenderReportsPage(
                sent?.Reports ?? System.Array.Empty<LocalApiSentReport>(),
                projects?.Projects ?? System.Array.Empty<LocalApiProject>());
        }
        catch
        {
            RenderReportsPage(null, null);
        }
    }

    internal void RenderReportsPage(
        IReadOnlyList<LocalApiSentReport>? sent,
        IReadOnlyList<LocalApiProject>? projects)
    {
        ReportsPageHost.Children.Clear();
        ReportsPageHost.Children.Add(PageKit.PageTitle("Reports"));
        ReportsPageHost.Children.Add(PageKit.Text(
            "What a client is shown, and what has already gone out. Nothing here ever sends on its own.",
            12.5, PageKit.Ink2, margin: new Thickness(0, 7, 0, 0), wrap: true));

        if (sent is null || projects is null)
        {
            ReportsPageHost.Children.Add(PageKit.Card(PageKit.Empty(
                "Can't reach the agent.", "This is not the same as having no reports.")));
            return;
        }

        // ── What is waiting. Only shown when something genuinely is.
        LocalApiProject? due = projects.FirstOrDefault(p =>
            p.ClientName.Trim().Length > 0
            && p.TotalTasks > 0
            && (p.NextReportDue == "today" || p.NextReportDue == "overdue"));

        if (due is not null)
        {
            ReportsPageHost.Children.Add(BuildWaitingCard(due));
        }

        // ── Tabs.
        var tabs = new WrapPanel { Margin = new Thickness(0, 24, 0, 0) };
        tabs.Children.Add(PageKit.Segmented(ReportTabs, _reportTab, key =>
        {
            _reportTab = key;
            _ = OpenReportsPageV2Async();
        }));
        ReportsPageHost.Children.Add(tabs);

        // Sent and Archived are the same list split by one flag. They were both hardcoded empty
        // states before, so archiving appeared to do nothing and the tab was permanently blank.
        ReportsPageHost.Children.Add(_reportTab switch
        {
            "ready" => PageKit.Card(PageKit.Empty(
                "Nothing waiting to be sent.",
                "A report is written and sent in one step, so nothing parks here yet.")),
            "arch" => BuildSentList(sent.Where(r => r.Archived).ToList(), archivedView: true),
            _ => BuildSentList(sent.Where(r => !r.Archived).ToList(), archivedView: false)
        });
    }

    private UIElement BuildWaitingCard(LocalApiProject p)
    {
        int missingLines = p.Tasks?.Count(t => t.ResultLine.Trim().Length == 0) ?? 0;

        var body = new StackPanel { Margin = PageKit.CardPad };
        body.Children.Add(PageKit.Text("WAITING FOR YOU", 11, PageKit.Accent));
        body.Children.Add(PageKit.Text(
            $"{p.ClientName} · this week", 19, PageKit.Ink, FontWeights.SemiBold,
            new Thickness(0, 8, 0, 0)));

        string drafted = $"Ari drafted this from {p.TotalTasks} {(p.TotalTasks == 1 ? "task" : "tasks")} "
                         + $"and {p.Hours:0.#}h.";
        body.Children.Add(PageKit.Text(drafted, 12.5, PageKit.Ink2, margin: new Thickness(0, 7, 0, 0), wrap: true));

        // The count that decides whether opening the draft is worth it at all.
        if (missingLines > 0)
        {
            body.Children.Add(PageKit.Text(
                $"{missingLines} {(missingLines == 1 ? "task has" : "tasks have")} no line about what came of them.",
                12.5, PageKit.Warn, margin: new Thickness(0, 4, 0, 0), wrap: true));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(PageKit.PrimaryButton("Review and send", () =>
        {
            // Straight into the existing send flow, which is untouched: it has its own confirm
            // step, and the address is chosen on one screen and checked on the next because a
            // send cannot be undone.
            ShowSection(UiSection.SendFlow);
            if (!PreviewMode)
            {
                _ = OpenReportBuilderForAsync(p.Id);
            }
        }));
        if (missingLines > 0)
        {
            buttons.Children.Add(PageKit.GhostButton("Fill in the missing lines", () =>
            {
                _taskFilter = "done";
                ShowSection(UiSection.Tasks);
                _ = RefreshTasksPageAsync();
            }));
        }

        body.Children.Add(buttons);
        return PageKit.Card(body, topMargin: 22);
    }

    private UIElement BuildSentList(IReadOnlyList<LocalApiSentReport> sent, bool archivedView)
    {
        if (sent.Count == 0)
        {
            return PageKit.Card(archivedView
                ? PageKit.Empty(
                    "Nothing archived.",
                    "Archiving hides an old report from the Sent list. The link a client already has keeps working.")
                : PageKit.Empty(
                    "Nothing sent yet.",
                    "A report you send keeps its own permanent link, frozen exactly as the client saw it."));
        }

        var list = new StackPanel();
        for (int i = 0; i < sent.Count; i++)
        {
            if (i > 0)
            {
                list.Children.Add(PageKit.Hair());
            }

            list.Children.Add(BuildSentRow(sent[i], archivedView));
        }

        return PageKit.Card(list);
    }

    private UIElement BuildSentRow(LocalApiSentReport r, bool archivedView)
    {
        var grid = new Grid { Margin = new Thickness(26, 18, 26, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(PageKit.Text(
            $"{r.ClientName} · {r.RangeStartLocal} – {r.RangeEndLocal}", 14, PageKit.Ink, FontWeights.Medium));
        left.Children.Add(PageKit.Text(
            SentSummaryLine(r), 12.5, PageKit.Ink3, margin: new Thickness(0, 5, 0, 0), wrap: true));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var links = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };

        // A sent report is FROZEN. Open and copy are the only two things that can be done to it —
        // regenerating would make a new link, never a silent rewrite of one a client has read.
        if (false)
        {
            // Placeholder: see the note below on why no link is offered here yet.
        }
        else
        {
            // The permanent link is returned by the server at send time and is NOT stored in the
            // local sent_reports table, so this machine genuinely does not have it for a report
            // sent earlier. Saying so is the only honest thing here — inventing a link, or showing
            // a dead "Copy" that yields nothing, would be worse than the gap.
            links.Children.Add(PageKit.Text("link not stored locally", 12.5, PageKit.Ink3));
        }

        // The action that made the Archived tab a lie. It is local and cosmetic: it hides the row
        // here and the client's link keeps working, because a report somebody has read cannot be
        // withdrawn.
        {
            links.Children.Add(PageKit.Text("    ", 12.5, PageKit.Ink3));
            links.Children.Add(PageKit.Link(
                archivedView ? "Restore" : "Archive",
                () => _ = ArchiveSentReportAsync(r.ReportId, !archivedView)));
        }

        right.Children.Add(links);
        right.Children.Add(PageKit.Text(
            // An unknown outcome is NEVER shown as a failure.
            r.SendState == "sent" ? "delivered" : "outcome unknown", 11, PageKit.Ink3,
            margin: new Thickness(0, 5, 0, 0)));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    private static string SentSummaryLine(LocalApiSentReport r)
    {
        var parts = new List<string> { $"{r.TotalHours:0.#}h" };
        int total = r.Lines?.Count ?? 0;
        int done = r.Lines?.Count(l => l.Finished) ?? 0;
        parts.Add($"{done} of {total} complete");
        if (r.RecipientEmail.Length > 0)
        {
            parts.Add($"to {r.RecipientEmail}");
        }

        return string.Join("  ·  ", parts);
    }

    private async Task ArchiveSentReportAsync(string reportId, bool archived)
    {
        await _apiClient.ArchiveSentReportAsync(reportId, archived).ConfigureAwait(true);
        await OpenReportsPageV2Async().ConfigureAwait(true);
    }

    private static void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // A missing default browser is not worth a dialog here.
        }
    }
}
