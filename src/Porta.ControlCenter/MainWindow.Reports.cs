namespace Porta.ControlCenter;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Porta.Agent.LocalApi;

/// <summary>
/// Step 10 phase B — the Reports page. The first part of the report a person can actually look at.
///
/// EVERYTHING HERE IS LOCAL AND DERIVED. The draft is rebuilt from measured hours on every request
/// and nothing is stored; the stored thing is the snapshot taken when a report is sent, and sending
/// is phase C. There is deliberately no send button, no recipient field and no email anywhere on
/// this page — a control that looked like it might send a client something is the last thing to
/// leave to guesswork.
///
/// The point of shipping this before sending exists: if the draft does not tell the truth, then
/// connecting an email account is wasted work.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The first day any work could be attributed to a project. Before this the app-usage rollup
    /// exists but every row is task_id = 0 — the backfill could reconstruct WHICH APP, never which
    /// task — so a range reaching further back is honest but empty, and the page says so rather
    /// than letting it read as "you did nothing".
    /// </summary>
    private static readonly DateTime ProjectHistoryFloor = new(2026, 08, 12);

    // The agreed card values. These are the same numbers as Themes/ReportTheme.xaml — the rows and
    // bars below are built in code, so they cannot pick up a Style, and a second palette here is
    // how the page drifted off the values in the first place.
    private static readonly Color AccentGreen = Color.FromRgb(0x3F, 0xBF, 0x7F);
    private static readonly Color TrackGrey = Color.FromRgb(0x23, 0x29, 0x31);
    private static readonly Color UnfinishedGrey = Color.FromRgb(0x5A, 0x62, 0x6D);
    private static readonly Color TextPrimary = Color.FromRgb(0xF2, 0xF4, 0xF7);
    private static readonly Color TextSecondary = Color.FromRgb(0x8B, 0x92, 0x9E);
    private static readonly Color TextQuiet = Color.FromRgb(0x6F, 0x78, 0x85);
    private static readonly Color CardSurface = Color.FromRgb(0x14, 0x17, 0x1C);
    private static readonly Color Hairline = Color.FromRgb(0x2A, 0x30, 0x38);

    private LocalApiReportDraft? _reportDraft;

    // Lines the user has taken out of this draft. Excluding removes the hours from the total, and
    // the total has to reflect that — a report whose figures do not match its own lines is the
    // argument this feature exists to win. Draft-local: nothing is stored, so re-building forgets.
    private readonly HashSet<long> _excludedLines = [];

    private async Task OpenReportsPageAsync()
    {
        try
        {
            LocalApiProjectsResponse? response = await _apiClient
                .GetProjectsAsync(includeArchived: false)
                .ConfigureAwait(true);

            IReadOnlyList<LocalApiProject> projects =
                response?.Projects ?? System.Array.Empty<LocalApiProject>();

            ReportProjectPicker.ItemsSource = projects;
            // The picker's ItemTemplate is in the XAML. Setting DisplayMemberPath here as well
            // throws — WPF allows one or the other, never both.
            ReportProjectPicker.SelectedIndex = projects.Count > 0 ? 0 : -1;

            // A week, but never reaching back past the day project history begins: offering a
            // range that can only come back empty invites the wrong conclusion about the data.
            DateTime today = DateTime.Today;
            DateTime from = today.AddDays(-6);
            ReportFromPicker.SelectedDate = from < ProjectHistoryFloor ? ProjectHistoryFloor : from;
            ReportToPicker.SelectedDate = today;

            ReportStatusText.Text = projects.Count == 0
                ? "No projects yet. Tag some work to a project first — a report is per project."
                : string.Empty;
        }
        catch
        {
            ReportStatusText.Text = "Can't reach the agent, so there is nothing to draft from.";
        }
    }

    private void ReportBuildButton_Click(object sender, RoutedEventArgs e)
        => _ = BuildReportDraftAsync();

    /// <summary>
    /// A drafted report keeps showing the range it was BUILT with, which is correct — the figures
    /// belong to that range. What is not acceptable is showing it beside pickers that now say
    /// something else, with nothing to tell them apart. Two ranges on one screen, in a document
    /// about which days were worked, is the one thing this page cannot be.
    /// </summary>
    private void ReportRangeChanged(object sender, SelectionChangedEventArgs e) => UpdateReportStaleNotice();

    private void UpdateReportStaleNotice()
    {
        if (ReportStaleText is null)
        {
            return;
        }

        bool stale =
            _reportDraft is { } draft &&
            ReportFromPicker.SelectedDate is DateTime from &&
            ReportToPicker.SelectedDate is DateTime to &&
            (!string.Equals(LocalDate(from), draft.RangeStartLocal, StringComparison.Ordinal) ||
             !string.Equals(LocalDate(to), draft.RangeEndLocal, StringComparison.Ordinal));

        ReportStaleText.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task BuildReportDraftAsync()
    {
        if (ReportProjectPicker.SelectedItem is not LocalApiProject project ||
            ReportFromPicker.SelectedDate is not DateTime from ||
            ReportToPicker.SelectedDate is not DateTime to)
        {
            ReportStatusText.Text = "Pick a project and both dates first.";
            return;
        }

        if (to < from)
        {
            ReportStatusText.Text = "The end of the range is before the start.";
            return;
        }

        ReportStatusText.Text = "Building…";
        try
        {
            LocalApiReportDraftResponse? response = await _apiClient
                .GetReportDraftAsync(project.Id, LocalDate(from), LocalDate(to))
                .ConfigureAwait(true);

            if (response?.Draft is null)
            {
                ReportStatusText.Text = "Can't reach the agent, so there is nothing to draft from.";
                return;
            }

            // A rebuild is a fresh reading of measured time. Carrying the old exclusions across it
            // would silently apply a decision made about different numbers.
            _excludedLines.Clear();
            _reportDraft = response.Draft;
            ReportStatusText.Text = string.Empty;
            RenderReportDraft();

            // Fire and forget: the draft is already on screen and usable, and the summary upgrades
            // it in place whenever it arrives. It must never be something the user waits behind.
            _ = DraftSummaryAsync();
        }
        catch
        {
            ReportStatusText.Text = "Can't reach the agent, so there is nothing to draft from.";
        }
    }

    private void RenderReportDraft()
    {
        if (_reportDraft is not { } draft)
        {
            ReportDraftPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ReportDraftPanel.Visibility = Visibility.Visible;

        // Item 4 — the header is the largest text a client reads, and it is now edited here rather
        // than on the Projects page. It still NEVER falls back to the local name: an empty box with
        // a prompt is honest, where showing the private name in the client-facing slot is the exact
        // thing that put "nolan - chase invoice" one tap from a client.
        ReportClientNameBox.Text = draft.ClientName;
        ReportClientNameBox.Tag = draft.ClientName;
        ReportClientNamePlaceholder.Visibility =
            draft.ClientName.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        ReportLocalNameText.Text = $"your name for it: {draft.ProjectName}";

        ReportRangeText.Text =
            $"{FriendlyDate(draft.RangeStartLocal)} – {FriendlyDate(draft.RangeEndLocal)}";

        IReadOnlyList<LocalApiReportLine> included =
            draft.Lines.Where(l => !_excludedLines.Contains(l.TaskId)).ToList();

        double total = Math.Round(included.Sum(l => l.Hours), 1, MidpointRounding.AwayFromZero);

        ReportHoursValue.Text = total.ToString("0.0", CultureInfo.InvariantCulture) + "h";
        ReportDaysValue.Text = draft.DaysWorked.ToString(CultureInfo.InvariantCulture);
        ReportDoneValue.Text =
            $"{included.Count(l => l.Finished)} of {included.Count}";

        RenderDaysChart(draft);
        RenderLines(draft);
        RenderReadiness(draft, included);
        // CONDITION 2 — surfaced beside readiness, before anything can be sent.
        RenderClientSeesPanel(included);
        UpdateReportStaleNotice();
        _ = LoadRecipientsAsync();
    }

    /// <summary>
    /// A bar per day across the whole range, gaps included. §4: a chart that hides the days you did
    /// not work is the kind of thing a client notices, and it costs trust permanently. So a day
    /// with nothing on it is a visible empty column, never a missing one.
    ///
    /// The bars carry no printed number, on purpose. Per-day figures and the line figures are two
    /// roundings of the same seconds and would not add to the same total by hand; the billed number
    /// is the lines. The chart is the shape of the work.
    /// </summary>
    private void RenderDaysChart(LocalApiReportDraft draft)
    {
        ReportDaysChart.Children.Clear();

        double max = draft.Days.Count == 0 ? 0d : draft.Days.Max(d => d.Hours);
        foreach (LocalApiReportDay day in draft.Days)
        {
            var column = new StackPanel
            {
                Width = 34,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            double height = max <= 0d ? 0d : Math.Round(day.Hours / max * 64d, 0);
            column.Children.Add(new Border
            {
                // A 2px floor so a day with a little work is visibly different from a day with
                // none — the same rule the card bars already follow.
                Height = day.Hours > 0d ? Math.Max(2d, height) : 1d,
                // NOT the accent. Green means work that counted — a finished check, a finished
                // bar, the primary button. A day bar is the shape of the work, not a completion,
                // and colouring the whole chart accent is how the rule drifted to seven things
                // once before.
                Background = new SolidColorBrush(day.Hours > 0d ? UnfinishedGrey : TrackGrey),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom
            });

            column.Children.Add(new TextBlock
            {
                Text = DayLabel(day.DateLocal),
                FontSize = 9,
                Foreground = new SolidColorBrush(TextQuiet),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });

            ReportDaysChart.Children.Add(column);
        }

        bool reachesBeforeFloor =
            DateTime.TryParseExact(
                draft.RangeStartLocal, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime start) &&
            start < ProjectHistoryFloor;

        ReportFloorNote.Visibility = reachesBeforeFloor ? Visibility.Visible : Visibility.Collapsed;
        ReportFloorNote.Text =
            "Days before 12 August 2026 show no hours because Ari only began recording work against " +
            "projects that day. Those hours were measured, but which task they belonged to was not, " +
            "and guessing it afterwards is exactly what this report must never do.";
    }

    private void RenderLines(LocalApiReportDraft draft)
    {
        ReportLinesList.Children.Clear();
        ReportLinesEmptyText.Visibility =
            draft.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        double max = draft.Lines.Count == 0 ? 0d : draft.Lines.Max(l => l.Hours);
        foreach (LocalApiReportLine line in draft.Lines)
        {
            ReportLinesList.Children.Add(BuildLineRow(line, max));
        }
    }

    private UIElement BuildLineRow(LocalApiReportLine line, double maxHours)
    {
        bool excluded = _excludedLines.Contains(line.TaskId);

        // Each line is its own surface, so "the work" reads as a list of things rather than text
        // floating on the page. An excluded line drops to the page colour: still there, visibly
        // not part of the report.
        var row = new Border
        {
            Background = new SolidColorBrush(excluded ? Color.FromRgb(0x16, 0x19, 0x1F) : CardSurface),
            BorderBrush = new SolidColorBrush(Hairline),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        row.Child = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Include/exclude. Excluding takes the hours out of the total; it never edits them.
        var include = new CheckBox
        {
            IsChecked = !excluded,
            Style = (Style)FindResource("RpCheckBox"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Leave this line out of the report. The hours are never editable."
        };
        include.Click += (_, _) =>
        {
            if (include.IsChecked == true)
            {
                _excludedLines.Remove(line.TaskId);
            }
            else
            {
                _excludedLines.Add(line.TaskId);
            }

            RenderReportDraft();
        };
        Grid.SetColumn(include, 0);
        grid.Children.Add(include);

        var labelStack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        // The client-facing label, learned once. Empty means it has never been named, and the row
        // says so rather than showing the raw title as though it were fit to send.
        var labelBox = new TextBox
        {
            Text = line.ClientLabel,
            Style = (Style)FindResource("RpInlineEditBox"),
            Foreground = new SolidColorBrush(excluded ? UnfinishedGrey : TextPrimary),
            Tag = line.ClientLabel,
            ToolTip = line.ClientLabel.Length == 0
                ? "Name this line the way your client should read it."
                : "Click to rename. Only the label changes — never the hours."
        };

        void TrySave()
        {
            string typed = labelBox.Text.Trim();
            if (string.Equals(typed, (string)labelBox.Tag, StringComparison.Ordinal))
            {
                return;
            }

            labelBox.Tag = typed;
            _ = SetLineLabelAsync(line.TaskId, typed);
        }

        labelBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TrySave();
                e.Handled = true;
            }
        };
        labelBox.LostFocus += (_, _) => TrySave();

        // An unnamed line is the one thing on this page the user MUST act on, and an empty
        // borderless box is invisible — there is nothing to see and nothing to aim at. So an
        // unnamed line carries a prompt in the box, which disappears the moment anything is typed.
        var labelHost = new Grid();
        labelHost.Children.Add(labelBox);

        var placeholder = new TextBlock
        {
            Text = "Name this line for your client",
            FontSize = 13,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(TextQuiet),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = line.ClientLabel.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        labelBox.TextChanged += (_, _) =>
            placeholder.Visibility = labelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        labelHost.Children.Add(placeholder);

        labelStack.Children.Add(labelHost);

        // The raw title, quietly, so the user can tell which line is which. It never leaves the
        // machine and is never what a client reads.
        labelStack.Children.Add(new TextBlock
        {
            Text = line.ClientLabel.Length == 0
                ? $"not named yet · your title: {line.Title}"
                : $"your title: {line.Title}",
            FontSize = 11,
            Foreground = new SolidColorBrush(TextQuiet),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 5, 0, 0)
        });

        // Item 4 — the user's own line about what came of this task, editable HERE. It is written
        // days earlier on a card, the report's summary is built from it, and until now it was not
        // on this screen at all: the last chance to read it was the moment it stopped being visible.
        //
        // Blank is a first-class state and NOT a blocker — ReportSendReadiness has never required a
        // result line, and the prompt below says so rather than leaving an empty box looking unmet.
        labelStack.Children.Add(new TextBlock
        {
            Text = "What came of it?",
            FontSize = 11,
            Foreground = new SolidColorBrush(TextSecondary),
            Margin = new Thickness(8, 10, 0, 1)
        });
        labelStack.Children.Add(new TextBlock
        {
            Text = "One line, in your words. Your client reads this.",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(TextQuiet),
            Margin = new Thickness(8, 0, 0, 3)
        });

        var resultBox = new TextBox
        {
            Text = line.ResultLine,
            Style = (Style)FindResource("RpInlineEditBox"),
            Foreground = new SolidColorBrush(excluded ? UnfinishedGrey : TextPrimary),
            Tag = line.ResultLine,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = "The one line your client reads about this task. Only this sentence changes — never the hours."
        };

        void TrySaveResult()
        {
            string typed = resultBox.Text.Trim();
            if (string.Equals(typed, (string)(resultBox.Tag ?? string.Empty), StringComparison.Ordinal))
            {
                return;
            }

            resultBox.Tag = typed;
            _ = SetLineResultAsync(line.TaskId, typed);
        }

        resultBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TrySaveResult();
                e.Handled = true;
            }
        };
        resultBox.LostFocus += (_, _) => TrySaveResult();

        var resultHost = new Grid();
        resultHost.Children.Add(resultBox);

        var resultPlaceholder = new TextBlock
        {
            Text = "Leave it blank and the line still sends.",
            FontSize = 12.5,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(TextQuiet),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = line.ResultLine.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        resultBox.TextChanged += (_, _) =>
            resultPlaceholder.Visibility = resultBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        resultHost.Children.Add(resultPlaceholder);

        labelStack.Children.Add(resultHost);

        // The user's own diagnostic, never the client's: measured time on this task that carried no
        // identifiable application, so it is not in the hours beside it. Shown so a report that
        // reads lower than the widget explains itself here rather than in front of a client.
        //
        // Not measurable says so in words rather than printing a zero. A zero here would be read as
        // "nothing missing", which is a claim, and it is the one this diagnostic exists to avoid
        // making without evidence.
        if (!line.UnattributedMeasurable)
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = "unrecognised-app time can't be measured for this line — some of this task's " +
                       "work falls outside the dates above, or before Ari started recording it",
                FontSize = 11,
                Foreground = new SolidColorBrush(TextQuiet),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 0, 0)
            });
        }
        else if (line.UnattributedHours > 0d)
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = $"+{line.UnattributedHours.ToString("0.0", CultureInfo.InvariantCulture)}h measured " +
                       "on this task with no recognised app — not counted, and never shown to a client",
                FontSize = 11,
                Foreground = new SolidColorBrush(TextQuiet),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 0, 0)
            });
        }

        Grid.SetColumn(labelStack, 1);
        grid.Children.Add(labelStack);

        var bar = new Border
        {
            Height = 6,
            Width = maxHours <= 0d ? 2d : Math.Max(2d, Math.Round(line.Hours / maxHours * 110d, 0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            // Green means work that counted. An unfinished or excluded line is never green.
            Background = new SolidColorBrush(
                excluded ? TrackGrey : line.Finished ? AccentGreen : UnfinishedGrey)
        };
        Grid.SetColumn(bar, 2);
        grid.Children.Add(bar);

        // §4 — a check if finished, "in progress" and the hours so far if not. NEVER a time
        // remaining: the estimate was set by the user for themselves, and a remaining figure in a
        // client's inbox becomes a commitment nobody made.
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (line.Finished)
        {
            right.Children.Add(new TextBlock
            {
                Text = "✓",
                FontSize = 13,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                // One of the three things allowed to be green.
                Foreground = new SolidColorBrush(excluded ? UnfinishedGrey : AccentGreen)
            });
        }
        else
        {
            right.Children.Add(new TextBlock
            {
                Text = "in progress",
                FontSize = 11,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(TextQuiet)
            });
        }

        var hours = new TextBlock
        {
            Text = line.Hours.ToString("0.0", CultureInfo.InvariantCulture) + "h",
            FontSize = 13,
            Width = 54,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(excluded ? UnfinishedGrey : TextPrimary),
            FontFamily = new FontFamily("Segoe UI"),
            ToolTip = line.Finished ? "Finished" : "In progress"
        };
        // Tabular, so a column of hours lines up on the decimal. A client reads these as a column.
        System.Windows.Documents.Typography.SetNumeralAlignment(hours, FontNumeralAlignment.Tabular);
        right.Children.Add(hours);

        Grid.SetColumn(right, 3);
        grid.Children.Add(right);

        return row;
    }

    /// <summary>
    /// What is still missing before this could go to a client. Shown now, while sending does not
    /// exist, because the answer is the same either way and it is the honest thing to put on the
    /// page: an unnamed line or an unnamed project is the difference between a report and a leak
    /// of somebody's private notes.
    /// </summary>
    /// <summary>
    /// Phase 1.4 / 2b — Ari's draft of the summary, fetched when the draft is built.
    ///
    /// It appears ALREADY WRITTEN so the user edits rather than faces a blank box, and whatever is
    /// in the box at send time is what goes out. Every failure path leaves the box empty and says
    /// so in one quiet line: an unreachable server, no key, a refusal, or nothing worth summarising.
    /// It never blocks a send — an empty summary simply omits the section on the client's page.
    ///
    /// It does NOT overwrite text the user has already typed. A draft that clobbers your own
    /// sentence the moment you rebuild the range would be worse than no draft at all.
    /// </summary>
    private async Task DraftSummaryAsync()
    {
        if (_reportDraft is not { } draft)
        {
            return;
        }

        if (ReportSummaryBox.Text.Trim().Length > 0)
        {
            ReportSummaryNote.Text = "Your own summary. Ari won't overwrite it.";
            return;
        }

        ReportSummaryNote.Text = "Ari is writing a draft…";

        LocalApiReportSummaryResponse? response = await _apiClient
            .DraftReportSummaryAsync(new LocalApiReportSummaryRequest(
                SchemaVer: 1,
                ProjectId: draft.ProjectId,
                From: draft.RangeStartLocal,
                To: draft.RangeEndLocal,
                ExcludedTaskIds: _excludedLines.ToList()))
            .ConfigureAwait(true);

        if (response is { Success: true, Summary: { Length: > 0 } summary })
        {
            ReportSummaryBox.Text = summary;
            ReportSummaryNote.Text = "Ari's draft, from your result lines. Edit or replace it — it sends as it reads.";
            return;
        }

        ReportSummaryBox.Text = string.Empty;
        ReportSummaryNote.Text = response?.Reason switch
        {
            "nothing_to_summarise" =>
                "No result lines yet, so there is nothing for Ari to summarise. Write your own, or leave it out.",
            "draft_unavailable" =>
                "Ari couldn't write a draft this time. Write your own, or leave it out — the report sends either way.",
            _ =>
                "Ari couldn't write a draft. Write your own, or leave it out — the report sends either way."
        };
    }

    /// <summary>
    /// CONDITION 2 (Yadin, 2026-08-17) — everything collected days ago that is about to go out.
    ///
    /// Expected finish and blocked reason are both typed at a moment that is not this one, and both
    /// can embarrass someone if they travel unread — a blocked reason in particular often names the
    /// recipient as the blocker. So they are shown plainly, in the order the client reads them
    /// (completed, in progress, blocked), before anything can be sent.
    /// </summary>
    private void RenderClientSeesPanel(IReadOnlyList<LocalApiReportLine> included)
    {
        ReportClientSeesList.Children.Clear();

        string[] order = ["done", "in_progress", "blocked"];
        List<LocalApiReportLine> notable = included
            .Where(l => l.ExpectedFinishDay.Length > 0 || l.BlockedReason.Length > 0)
            .OrderBy(l => Array.IndexOf(order, l.Status))
            .ToList();

        if (notable.Count == 0)
        {
            ReportClientSeesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (LocalApiReportLine line in notable)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock
            {
                Text = line.ClientLabel.Length > 0 ? line.ClientLabel : line.Title,
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("RpTextPrimary"),
                TextWrapping = TextWrapping.Wrap
            });

            if (line.BlockedReason.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = $"Blocked: {line.BlockedReason}",
                    Style = (Style)FindResource("RpBody"),
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            if (line.ExpectedFinishDay.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = $"Expected to finish {FriendlyDate(line.ExpectedFinishDay)}",
                    Style = (Style)FindResource("RpBody"),
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            ReportClientSeesList.Children.Add(row);
        }

        ReportClientSeesPanel.Visibility = Visibility.Visible;
    }

    private void RenderReadiness(LocalApiReportDraft draft, IReadOnlyList<LocalApiReportLine> included)
    {
        var missing = new List<string>();

        if (draft.ClientName.Length == 0)
        {
            // Item 4 — the name is editable at the top of THIS screen now, so the instruction is to
            // look up, not to go elsewhere.
            missing.Add("this project needs a name your client will recognise — it is the biggest text on the page. Type it at the top of this screen");
        }

        int unnamed = included.Count(l => l.ClientLabel.Length == 0);
        if (unnamed > 0)
        {
            missing.Add(unnamed == 1
                ? "one line has no client-facing name"
                : $"{unnamed} lines have no client-facing name");
        }

        if (included.Count == 0)
        {
            // Two different situations, and telling the user the wrong one sends them looking for
            // a checkbox that isn't there.
            missing.Add(draft.Lines.Count == 0
                ? "no work is tagged to this project in these dates, so there is nothing to report"
                : "every line is excluded, so there is nothing to report");
        }

        ReportReadinessText.Text = missing.Count == 0
            ? "Nothing. Every line is named and the hours are measured."
            : string.Join("\n", missing.Select(m => "· " + m));

        // Reflects what is actually true right now. The line under this panel used to be a
        // hardcoded "sending is not built yet", which survived the build of sending and left the
        // page claiming the feature did not exist.
        ReportReadinessNote.Text = missing.Count == 0
            ? "The report goes out from your own name and address. It never sends until you tap send."
            : "Sending is ready and waiting on the above. The report goes out from your own name "
              + "and address, and never without you tapping send.";
    }

    /// <summary>
    /// Item 4 — hooks the client-name box up once, from the constructor path. Save on Enter and on
    /// losing focus, exactly like the per-line label box: the two fields on this screen behave the
    /// same way, or one of them feels broken.
    /// </summary>
    private void WireReportClientNameBox()
    {
        ReportClientNameBox.TextChanged += (_, _) =>
            ReportClientNamePlaceholder.Visibility =
                ReportClientNameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        void TrySave()
        {
            string typed = ReportClientNameBox.Text.Trim();
            if (string.Equals(typed, (string)(ReportClientNameBox.Tag ?? string.Empty), StringComparison.Ordinal))
            {
                return;
            }

            ReportClientNameBox.Tag = typed;
            _ = SaveReportClientNameAsync(typed);
        }

        ReportClientNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TrySave();
                e.Handled = true;
            }
        };
        ReportClientNameBox.LostFocus += (_, _) => TrySave();
    }

    /// <summary>
    /// Saves the project's client-facing name. Only the name: every other field on the request is
    /// null, and the endpoint applies a field only when present, so naming a project cannot
    /// accidentally clear its rate or its label style.
    /// </summary>
    private async Task SaveReportClientNameAsync(string clientName)
    {
        if (_reportDraft is not { } draft)
        {
            return;
        }

        try
        {
            LocalApiProjectResponse? updated = await _apiClient
                .UpdateProjectClientAsync(new LocalApiProjectClientRequest(
                    SchemaVer: 1,
                    Id: draft.ProjectId,
                    ClientName: clientName,
                    UseClientLabels: null,
                    RateMinor: null,
                    RateCurrency: null))
                .ConfigureAwait(true);

            if (updated?.Success != true)
            {
                ReportStatusText.Text = "That name didn't save. The draft still shows what is stored.";
                return;
            }

            // Re-read rather than patching in place: the store normalises what it keeps, and this
            // screen must show what is stored, not what was typed.
            await BuildReportDraftAsync().ConfigureAwait(true);
        }
        catch
        {
            ReportStatusText.Text = "That name didn't save. The draft still shows what is stored.";
        }
    }

    /// <summary>
    /// Item 4 — the user's own one-line result, saved from the draft screen. Status, blocked reason
    /// and expected finish are all sent as null so they are left exactly as they are: this screen
    /// edits the sentence a client reads, and nothing derived.
    /// </summary>
    private async Task SetLineResultAsync(long taskId, string resultLine)
    {
        try
        {
            LocalApiTaskActResponse? response = await _apiClient
                .SetTaskReportFieldsAsync(new LocalApiTaskReportFieldsRequest(
                    SchemaVer: 1,
                    TaskId: taskId,
                    Status: null,
                    ResultLine: resultLine,
                    BlockedReason: null,
                    ExpectedFinishDay: null))
                .ConfigureAwait(true);

            if (response?.Success != true)
            {
                ReportStatusText.Text = "That line didn't save. The draft still shows what is stored.";
                return;
            }

            await BuildReportDraftAsync().ConfigureAwait(true);
        }
        catch
        {
            ReportStatusText.Text = "That line didn't save. The draft still shows what is stored.";
        }
    }

    private async Task SetLineLabelAsync(long taskId, string label)
    {
        try
        {
            LocalApiTaskActResponse? response = await _apiClient
                .SetTaskClientLabelAsync(new LocalApiTaskClientLabelRequest(
                    SchemaVer: 1, TaskId: taskId, ClientLabel: label))
                .ConfigureAwait(true);

            if (response?.Success != true)
            {
                ReportStatusText.Text = "That name didn't save. The draft still shows what is stored.";
                return;
            }

            // Re-read rather than patching the row in place: the store normalises what it keeps,
            // and the page must show what is stored, not what was typed.
            await BuildReportDraftAsync().ConfigureAwait(true);
        }
        catch
        {
            ReportStatusText.Text = "That name didn't save. The draft still shows what is stored.";
        }
    }

    private static string LocalDate(DateTime value)
        => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FriendlyDate(string localDate)
        => DateTime.TryParseExact(
            localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
            : localDate;

    private static string DayLabel(string localDate)
        => DateTime.TryParseExact(
            localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.ToString("d MMM", CultureInfo.CurrentCulture)
            : localDate;
}
