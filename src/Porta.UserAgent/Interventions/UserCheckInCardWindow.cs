namespace Porta.UserAgent.Interventions;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

// Workstream D (C2 channel) — the compact bottom-right check-in card. Dark "v1" style, ~1/5 of
// the screen, always-on-top, never full-screen (that is the break overlay's job). Renders the
// morning / midday / end-of-day surfaces and raises a single UserCheckInResponse. It is shown
// through the C2 always-on-top channel so it fires even when the widget is hidden.
internal sealed class UserCheckInCardWindow : Window
{
    // ── the design values, given exactly, 2026-08-10 ──
    // Two weights only: Normal and Medium. Nothing bold, no gradients, no shadows.
    private static readonly Color CardBg = FromHex("#14171C");
    private static readonly Color CardBorder = FromHex("#2A3038");   // also the hairline
    private static readonly Color HeadlineFg = FromHex("#F2F4F7");   // headline + input text
    private static readonly Color AiLineFg = FromHex("#8B929E");     // the AI line, unselected chip text
    private static readonly Color LabelFg = FromHex("#6F7885");      // section labels, hint, meta
    private static readonly Color TitleLiveFg = FromHex("#DFE3E9");  // a task title that is live
    private static readonly Color TitleFadedFg = FromHex("#78808C"); // a task title that is faded
    private static readonly Color AccentGreen = FromHex("#3FBF7F");  // finished check, finished bar, button
    private static readonly Color OnAccentFg = FromHex("#0E2A1C");   // text on the green button
    private static readonly Color BarTrackFg = FromHex("#232931");
    private static readonly Color BarUnfinishedFg = FromHex("#5A626D");
    // Chips are NEUTRAL (Yadin, 2026-08-10): a selected chip is a solid light fill, never green, so
    // green is left meaning finished and the primary action only.
    private static readonly Color ChipSelectedFill = FromHex("#2E353E");
    private static readonly Color ChipSelectedFg = FromHex("#F2F4F7");
    // The refused-keyboard border. NOT green: green means the finished check, the finished bar and
    // the primary button, and nothing else touches it - not chips, not inputs.
    private static readonly Color RefusedBorderFg = FromHex("#8B929E");

    private const double HairlineThickness = 0.5d;
    private const double BarColumnWidth = 62d;
    private const double BarHeight = 5d;
    private const double BarRadius = 3d;
    private const double ValueColumnWidth = 42d;
    private const double TaskRowFontSize = 12d;
    private const double CheckFontSize = 13d;
    private const double CardWidth = 380d;
    private const double ScreenMargin = 20d;

    private static Color FromHex(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;

    private static readonly TimeSpan MessageFadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly Action<UserCheckInResponse> _onResponse;
    private readonly Border _cardBorder;
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly StackPanel _bodyStack;   // mode-specific content is rebuilt here each ShowPrompt
    // Step 9a — the card scrolls instead of clipping once it hits MaxHeight. See the constructor.
    private readonly ScrollViewer _bodyScroll;
    // Step 9a — the picker currently expanded, or null. Only ONE may be open: measured, three open
    // at once would be 673 DIPs against a 632 ceiling.
    private Action? _closeOpenProjectPicker;
    // Visible only when the keyboard grab was refused; see the constructor.
    private readonly TextBlock _focusHint;
    // Where the hint lives when a surface has not claimed it. Step 8 made the card tall enough that
    // a notice pinned to the bottom can sit far below the box it is about, so the morning surface
    // moves it up to sit directly above the first input.
    private readonly Panel _focusHintHome;
    // The armed box, remembered so its border can be restored when the next card is built.
    private TextBox? _accentedBox;
    private UserCheckInKind _kind;
    private bool _responded;
    // Confirmation mode: the card is echoing an acknowledgement (no further reply expected), so the
    // ✕ / Escape just hides rather than raising another dismissed response.
    private bool _confirmationMode;
    // The control that should own the caret for the card currently on screen, re-armed by every
    // ShowPrompt. Null for kinds with no text input (midday) and for confirmations. The window is
    // reused across shows (UserCheckInCardPresenter.EnsureWindowOnCurrentDispatcher keeps it
    // alive), so this MUST NOT be wired through Loaded — that fires once per window lifetime, not
    // once per show, which is why only the first card of the process ever took the caret.
    private Control? _focusTarget;
    // Part 5 (async narrative) — true once the user has typed in this card's box or picked a
    // chip. The late LLM line must never rewrite copy underneath someone who has started
    // answering, so this latches on first real interaction and is cleared on the next ShowPrompt.
    private bool _userInteracted;

    // Test seam, mirroring WidgetSizeLog.PathOverride: unit tests exercise the per-show re-arming
    // without letting a card seize foreground on the developer's desktop. Foreground acquisition
    // itself cannot be unit-tested and is confirmed manually.
    internal static bool SuppressForegroundAcquisition;

    /// <summary>The control armed to receive the caret for the card currently shown. Test-only.</summary>
    internal Control? FocusTarget => _focusTarget;

    /// <summary>
    /// Test-only: how many times this card has asserted ownership of the keyboard (foreground
    /// acquisition + caret). Step 1 made that happen when a card APPEARS; the count exists so a test
    /// can prove it also happens when a new box opens on a card already on screen, which is a
    /// different moment and was not covered.
    /// </summary>
    internal int FocusAssertions => _focusAssertions;

    private int _focusAssertions;

    public UserCheckInCardWindow(Action<UserCheckInResponse> onResponse)
    {
        _onResponse = onResponse;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        // Activatable: the morning/EOD text boxes need real keyboard focus. It parks in the
        // corner and never seizes the whole screen, so this is a deliberate, quiet interaction.
        ShowActivated = true;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _cardBorder = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(HairlineThickness)
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // Step 9a — the card scrolls rather than CLIPS once it reaches MaxHeight. Measured before
        // building the picker: a three-task card with yesterday's block is 566 DIPs against a 632
        // ceiling, and content past that used to be cut off silently — with "Start the day", the
        // last thing in the stack, the first casualty. The picker is bounded so this should never
        // fire (see MakeProjectPicker), but "should never" is not the same as cannot, and the
        // button the whole loop depends on must not be reachable only by luck.
        //
        // Nothing scrolls on a card that fits, so every existing card looks exactly as it did.
        _bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };
        _cardBorder.Child = _bodyScroll;

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _eyebrow = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(LabelFg),
            Text = "Ari"
        };
        Grid.SetColumn(_eyebrow, 0);
        headerRow.Children.Add(_eyebrow);

        Button dismiss = MakeTextButton("✕", LabelFg);
        dismiss.Click += (_, _) => DismissClicked();
        Grid.SetColumn(dismiss, 1);
        headerRow.Children.Add(dismiss);
        stack.Children.Add(headerRow);

        _title = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(HeadlineFg),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(_title);

        _message = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(AiLineFg),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,                       // 12px * 1.5
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(_message);

        _bodyStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(_bodyStack);

        // Shown ONLY when Windows refused the keyboard. Ari cannot always win the foreground —
        // SPI_FOREGROUNDLOCKTIMEOUT is a deliberate OS protection and overriding it is a
        // system-settings change that is not Ari's to make. What it must never do is look focused
        // while it isn't: that is how a whole task got typed into another application unnoticed.
        // Telling the truth costs one quiet line; staying silent costs the user their input.
        _focusHint = new TextBlock
        {
            FontSize = 11,
            // NOT green. Green on this card means one thing — finished — plus the primary button
            // and, when Windows refuses the keyboard, the border of the box that wants your click.
            // A green sentence here would make the colour mean "notice me" as well, which is how a
            // palette stops carrying information.
            Foreground = new SolidColorBrush(LabelFg),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Text = "Click the box to type — Windows kept the keyboard.",
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_focusHint);
        _focusHintHome = stack;

        Content = _cardBorder;
        Loaded += (_, _) => PositionBottomRight();
        SizeChanged += (_, _) => PositionBottomRight();
        // Registered ONCE, here — not in BuildMorning/BuildEndOfDay. Subscribing per-show leaked a
        // handler on every ShowPrompt and closed over a TextBox already dropped from the visual
        // tree. Activated fires on every activation of a reused window, so this is the per-show
        // hook Loaded was mistaken for; it also backstops the case where activation lands after
        // ShowPrompt has already returned.
        Activated += (_, _) => ApplyFocusTarget();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DismissClicked();
                e.Handled = true;
            }
        };
    }

    private void DismissClicked()
    {
        if (_confirmationMode)
        {
            Hide();
            return;
        }

        Respond(UserCheckInResponse.DismissedFor(_kind));
    }

    public void ShowPrompt(UserCheckInPrompt prompt)
    {
        _kind = prompt.Kind;
        _responded = false;
        _confirmationMode = false;
        // Header reads "Ari" for every kind (matching confirmation mode) — the words
        // morning/midday/end-of-day no longer appear on the card.
        _eyebrow.Text = "Ari";
        _title.Text = prompt.Title;
        _message.Text = prompt.Message;
        _bodyStack.Children.Clear();
        // Cleared before the rebuild so a kind with no text input (midday) can never inherit the
        // previous card's box, which is no longer in the visual tree.
        SetBoxAccent(false);
        _focusTarget = null;
        _userInteracted = false;
        // The previous card's picker belongs to controls that have just left the tree.
        _closeOpenProjectPicker = null;
        _focusHint.Visibility = Visibility.Collapsed;
        MoveFocusHintTo(_focusHintHome);
        // A previous card may have faded its message out mid-upgrade; make sure this one renders
        // opaque regardless of where the last animation left the property.
        _message.BeginAnimation(UIElement.OpacityProperty, null);
        _message.Opacity = 1d;

        switch (prompt.Kind)
        {
            case UserCheckInKind.Morning:
                BuildMorning(prompt);
                break;
            case UserCheckInKind.Midday:
                BuildMidday(prompt);
                break;
            case UserCheckInKind.MiddayClose:
                BuildMiddayClose(prompt);
                break;
            case UserCheckInKind.EndOfDay:
                BuildEndOfDay(prompt);
                break;
        }

        // Chat 2 — the day-off tap goes LAST on every card, under the primary action. It applies to
        // all four kinds because the first card of a day is whichever one happens to fire first, and
        // it sits at the bottom because it is the exception: the card's own answer is the main road.
        if (prompt.OffersNotWorkingToday)
        {
            AddNotWorkingTodayTap();
        }

        if (!IsVisible)
        {
            Show();
        }

        PositionBottomRight();
        Topmost = true;
        AcquireForegroundAndFocus();
    }

    // Echoes a short acknowledgement on the same card after the user answered (morning "locked in",
    // midday follow-up, EOD "saved"). No inputs, no reply — the presenter auto-hides it shortly. This
    // keeps the whole check-in conversation on the always-on-top card instead of a separate toast.
    public void ShowConfirmation(UserCheckInKind kind, string message, string? actionLabel = null, Action? onAction = null)
    {
        _kind = kind;
        _responded = true;        // the exchange is over; the ✕ now just hides
        _confirmationMode = true;
        _eyebrow.Text = "Ari";
        _title.Text = message;
        _message.Text = string.Empty;
        _bodyStack.Children.Clear();
        // A confirmation has no input to type into; don't leave the answered card's box armed.
        _focusTarget = null;
        _focusHint.Visibility = Visibility.Collapsed;

        // Part 5c — "has power": the confirmation can carry ONE action wired to an existing
        // capability (e.g. a reminder). One button, one action, then the card hides.
        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction != null)
        {
            Button action = MakeChip(actionLabel);
            action.Margin = new Thickness(0, 10, 0, 0);
            action.Click += (_, _) =>
            {
                try
                {
                    onAction();
                }
                catch
                {
                }

                Hide();
            };
            _bodyStack.Children.Add(action);
        }

        if (!IsVisible)
        {
            Show();
        }

        PositionBottomRight();
        Topmost = true;
    }

    /// <summary>
    /// A section label: small, muted, with a hairline under it. The label is always quieter than
    /// the values beneath it — it is scaffolding, not content.
    /// </summary>
    private void AddSectionLabel(string text, double topMargin)
    {
        _bodyStack.Children.Add(new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(LabelFg),
            // Uppercased here rather than relying on the caller, so every label is consistent.
            // Letter-spacing was specced at 0.07em and deliberately DROPPED (Yadin, 2026-08-10):
            // WPF TextBlock has no letter-spacing property, and 0.7px at this size does not
            // justify per-glyph Glyphs machinery. Everything else on the label is to spec.
            Text = text.ToUpperInvariant(),
            Margin = new Thickness(0, topMargin, 0, 5)
        });

        _bodyStack.Children.Add(new Border
        {
            Height = HairlineThickness,
            Background = new SolidColorBrush(CardBorder),
            Margin = new Thickness(0, 0, 0, 9)
        });
    }

    /// <summary>
    /// Step 8 — yesterday, as three columns: what it was, how far it got, how long it took.
    /// </summary>
    /// <remarks>
    /// The bar is the "simple graphic of the day" the plan asks for, resolved per row rather than
    /// as a separate chart: measured against PLANNED, so twenty seconds of an intended three hours
    /// reads on its own without a caption. A floor of two pixels keeps that case visible — a bar
    /// rounded to nothing would say "no data" when the truth is "barely started".
    ///
    /// Numbers sit in a fixed right-aligned column with tabular figures, so 1h53m and 20s line up
    /// on their digits and the column can be read down rather than across.
    /// </remarks>
    private void AddTaskRow(TaskBoardLine line, bool fadeUnfinished)
    {
        bool done = line.IsDone;
        // THE ONE DIFFERENCE BETWEEN THE THREE CARDS, and it is deliberate. Fading is TONE, not
        // state. On yesterday's block, "not done" means "you dropped this" and receding is right.
        // On today's board, "not done" means "this is the live work you are about to act on" —
        // fading it would make the interactive rows the quietest thing on the card and would read
        // as a reproach for work that is simply still in progress. Same renderer, same columns,
        // same numbers; only the weight of unfinished work changes.
        bool quiet = !done && fadeUnfinished;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });

        var title = new TextBlock
        {
            FontSize = TaskRowFontSize,
            // Finished work reads at full strength; dropped work fades back. The state is carried
            // by weight and colour rather than by a cross, which would read as an error.
            Foreground = new SolidColorBrush(quiet ? TitleFadedFg : TitleLiveFg),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (done)
        {
            title.Inlines.Add(new System.Windows.Documents.Run("✓ ")
            {
                Foreground = new SolidColorBrush(AccentGreen),
                FontSize = CheckFontSize
            });
        }

        title.Inlines.Add(new System.Windows.Documents.Run(line.Title));

        // Step 9a (2026-08-13) — the project, quietly, AFTER the title. Costs ZERO height by
        // construction: this TextBlock is NoWrap with character ellipsis, so an extra Run at the
        // same font size cannot add a line. Measured either way before shipping.
        //
        // After rather than before the title, because the ellipsis truncates from the END: with a
        // prefix the project would survive and eat the task name, and the task name is the thing
        // the user is about to act on. This way a cramped row loses the context, never the work.
        if (line.ProjectName.Length > 0)
        {
            title.Inlines.Add(new System.Windows.Documents.Run($"  ·  {line.ProjectName}")
            {
                // Muted, and one step quieter than a faded title so it never competes with the
                // task even on a row that is itself faded.
                Foreground = new SolidColorBrush(LabelFg)
            });
        }

        Grid.SetColumn(title, 0);
        row.Children.Add(title);

        var track = new Border
        {
            Height = BarHeight,
            Width = BarColumnWidth,
            CornerRadius = new CornerRadius(BarRadius),
            Background = new SolidColorBrush(BarTrackFg),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var fill = new Border
        {
            Height = BarHeight,
            Width = Math.Max(2d, BarColumnWidth * line.ProgressFraction),
            CornerRadius = new CornerRadius(BarRadius),
            // Green fills only for finished work, on every card. An in-progress bar is neutral.
            Background = new SolidColorBrush(done ? AccentGreen : BarUnfinishedFg),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        barHost.Children.Add(track);
        barHost.Children.Add(fill);
        Grid.SetColumn(barHost, 1);
        row.Children.Add(barHost);

        var value = new TextBlock
        {
            FontSize = TaskRowFontSize,
            // Same colour as its title, so a row reads as one object.
            Foreground = new SolidColorBrush(quiet ? TitleFadedFg : TitleLiveFg),
            Text = line.MeasuredLabel,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Tabular figures: proportional digits would stop the column lining up, which is the
        // entire reason it is a column.
        System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        _bodyStack.Children.Add(row);
    }

    // Step 3 — the morning card stops collecting one sentence and starts collecting the day's task
    // list: up to three things, each with a duration the user TAPS. The card opens with a single
    // row so it is no taller than it was before; "+ add" reveals the second and third.
    //
    // The duration matters because it is what makes these real tasks rather than notes — the store
    // rejects a task without a legal planned duration, and step 5 shows planned against measured.
    // It is never typed: the whole point of this card is that answering it costs three seconds.
    private void BuildMorning(UserCheckInPrompt prompt)
    {
        // Step 6 — said once, plainly, above the list it is about. Not a nag: no escalation, no
        // repetition beyond this one line, and it never blocks anything.
        if (!string.IsNullOrWhiteSpace(prompt.CarriedNotice))
        {
            AddMutedLine(prompt.CarriedNotice, bottomMargin: 10);
        }

        // Step 8 — yesterday, quiet, above today. History first so the decision below it is made
        // with the evidence in view; rendered a step dimmer and smaller throughout, and with no
        // control on it at all, because it is not the thing to act on.
        // Yesterday is context; today is what gets acted on. When today already carries three rows
        // the card is at its tallest, so yesterday gives ground first (Yadin, 2026-08-10). The
        // summary line above still counts everything, so the day is never misreported - only fewer
        // of its rows are itemised.
        int proposedToday = prompt.Tasks?.Count(t => t.IsOpen) ?? 0;
        IReadOnlyList<TaskBoardLine> yesterday = (prompt.Yesterday ?? Array.Empty<TaskBoardLine>())
            .Take(proposedToday >= UserCheckInTaskPlanner.MaxMorningTasks ? 2 : int.MaxValue)
            .ToList();
        if (yesterday.Count > 0)
        {
            AddSectionLabel("YESTERDAY", topMargin: 0);
            if (!string.IsNullOrWhiteSpace(prompt.YesterdaySummary))
            {
                _bodyStack.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    Foreground = new SolidColorBrush(TitleLiveFg),
                    TextWrapping = TextWrapping.Wrap,
                    Text = prompt.YesterdaySummary,
                    Margin = new Thickness(0, 0, 0, 9)
                });
            }

            foreach (TaskBoardLine line in yesterday)
            {
                // History: dropped work recedes.
                AddTaskRow(line, fadeUnfinished: true);
            }

            AddSectionLabel("TODAY", topMargin: 12);
        }

        // The refusal hint belongs with the box it is about, not at the foot of a tall card.
        MoveFocusHintTo(_bodyStack);

        var rows = new List<MorningTaskRow>();
        var rowsPanel = new StackPanel { Orientation = Orientation.Vertical };
        _bodyStack.Children.Add(rowsPanel);

        // Step 9a — what the picker offers. Empty list = no picker chip at all, so the very first
        // morning (before any project exists) still shows only "+ project", and a build with the
        // feature switched off upstream looks exactly like 1.2.142.
        IReadOnlyList<UserCheckInProject> projects = prompt.Projects ?? Array.Empty<UserCheckInProject>();

        Button addRow = MakeTextButton("+ add", LabelFg);
        addRow.HorizontalAlignment = HorizontalAlignment.Left;
        addRow.Margin = new Thickness(0, 8, 0, 0);

        string chosenPref = UserCheckInChoices.MorningTracking[0].Value;

        // Declared before the rows so a row's Enter key can raise the same response the button does.
        UserCheckInResponse BuildResponse()
        {
            var entries = new List<UserCheckInTaskEntry>();
            foreach (MorningTaskRow row in rows)
            {
                string title = row.Box.Text.Trim();
                if (title.Length > 0)
                {
                    entries.Add(new UserCheckInTaskEntry(
                        title, row.PlannedSeconds, row.ProjectId, row.ProjectName));
                }
            }

            // TextReply stays the joined sentence: it is what daily_goals records and what every
            // pre-step-3 consumer of the morning goal already reads.
            return new UserCheckInResponse(
                UserCheckInKind.Morning,
                UserCheckInTaskPlanner.JoinTitles(entries.Select(e => e.Title)),
                chosenPref,
                Dismissed: false,
                Tasks: entries);
        }

        // Keeps the "+ add" affordance and the per-row ✕ honest after any add or remove: three is
        // the ceiling, and the last remaining row can never be removed (a card with no box to type
        // in is not a card). Also restates the top margins, which are positional.
        void UpdateRowChrome()
        {
            addRow.Visibility = rows.Count >= UserCheckInTaskPlanner.MaxMorningTasks
                ? Visibility.Collapsed
                : Visibility.Visible;

            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Remove.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                rows[i].Panel.Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0);
            }
        }

        void RemoveRow(MorningTaskRow row)
        {
            if (rows.Count <= 1)
            {
                return;
            }

            _userInteracted = true;
            rowsPanel.Children.Remove(row.Panel);
            rows.Remove(row);
            UpdateRowChrome();

            // If the row being removed owned the caret, hand it to a box that still exists. Leaving
            // _focusTarget pointing at a TextBox no longer in the visual tree is exactly the defect
            // step 1 fixed, reached from the other direction.
            if (ReferenceEquals(_focusTarget, row.Box))
            {
                ArmFocus(rows[^1].Box);
            }
        }

        MorningTaskRow AddRow(string prefill)
        {
            var row = new MorningTaskRow(
                MakeInputBox(rows.Count == 0 ? "What do you want to get done today?" : "And after that?"),
                UserCheckInChoices.MorningTaskDurations[0].Seconds);

            // Part 3 — last night's plan pre-fills the first box so confirming it is one Enter.
            if (prefill.Length > 0)
            {
                row.Box.Text = prefill;
                row.Box.CaretIndex = row.Box.Text.Length;
            }

            // Subscribed AFTER the prefill so seeding the box does not count as the user typing.
            row.Box.TextChanged += (_, _) => _userInteracted = true;
            row.Box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Respond(BuildResponse());
                    e.Handled = true;
                }
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, rows.Count == 0 ? 0 : 10, 0, 0)
            };

            // The box and its ✕ share a line, matching the widget's per-task remove. Changing your
            // mind about a row you just added has to cost one tap, not a dismissed card.
            var boxLine = new Grid();
            boxLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            boxLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(row.Box, 0);
            boxLine.Children.Add(row.Box);

            row.Remove.Click += (_, _) => RemoveRow(row);
            Grid.SetColumn(row.Remove, 1);
            boxLine.Children.Add(row.Remove);
            panel.Children.Add(boxLine);

            var durationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var durationChips = new List<Button>();
            foreach ((int seconds, string label) in UserCheckInChoices.MorningTaskDurations)
            {
                Button chip = MakeChip(label);
                durationChips.Add(chip);
                chip.Click += (_, _) =>
                {
                    _userInteracted = true;
                    row.PlannedSeconds = seconds;
                    for (int i = 0; i < durationChips.Count; i++)
                    {
                        HighlightChip(durationChips[i], durationChips[i] == chip);
                    }
                };
                durationRow.Children.Add(chip);
            }

            HighlightChip(durationChips[0], true);

            // ORDER IS LOAD-BEARING. The duration row goes in FIRST so the picker lands BENEATH
            // it, which is what the brief specifies and what the press needs to look like.
            //
            // It was the other way round in 1.2.143/1.2.144 and the picker opened ABOVE the row,
            // between the text box and the chips. Measured on a rendered card: the options landed
            // 53 px above the chip that was pressed, while the chip itself stayed put and changed
            // nothing about its appearance — so pressing it looked like it did nothing at all.
            // That is the whole bug; the handler was always wired.
            panel.Children.Add(durationRow);

            // Step 9a — the project picker rides the SAME row as the durations, to their right.
            // At rest it costs zero height (measured: a three-task card is 566.36 DIPs with or
            // without the chip), which is the constraint Yadin set.
            AddProjectPicker(row, durationRow, panel, projects);

            row.Panel = panel;
            rowsPanel.Children.Add(panel);
            rows.Add(row);
            UpdateRowChrome();
            return row;
        }

        // Step 4 — propose, never present an empty box. Yesterday's unfinished work fills the rows
        // so the common morning is a glance and a tap. The carried plan is the fallback for a day
        // with no open tasks at all (the first day, or a day after everything was finished).
        MorningTaskRow first;
        if (prompt.Tasks is { Count: > 0 } proposed)
        {
            first = AddRow(proposed[0].Title);
            foreach (TaskBoardLine line in proposed.Skip(1).Take(UserCheckInTaskPlanner.MaxMorningTasks - 1))
            {
                AddRow(line.Title);
            }
        }
        else
        {
            first = AddRow((prompt.PrefillGoalText ?? string.Empty).Trim());
        }

        addRow.Click += (_, _) =>
        {
            _userInteracted = true;
            MorningTaskRow added = AddRow(string.Empty);
            ArmFocus(added.Box);
        };
        _bodyStack.Children.Add(addRow);

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var chips = new List<Button>();
        foreach ((string value, string label) in UserCheckInChoices.MorningTracking)
        {
            Button chip = MakeChip(label);
            chips.Add(chip);
            chip.Click += (_, _) =>
            {
                _userInteracted = true;
                chosenPref = value;
                for (int i = 0; i < chips.Count; i++)
                {
                    HighlightChip(chips[i], chips[i] == chip);
                }
            };
            chipRow.Children.Add(chip);
        }

        HighlightChip(chips[0], true);
        _bodyStack.Children.Add(chipRow);

        // "Start the day" — the card is no longer a form you submit to Ari, it is the moment the
        // day begins, and the first task starts running the instant it is pressed.
        Button share = MakePrimaryButton("Start the day");
        share.Click += (_, _) => Respond(BuildResponse());
        _bodyStack.Children.Add(share);

        // Record the intent only. The window-level Activated hook applies it on every show.
        _focusTarget = first.Box;
    }

    // One line of the morning list. Panel is the whole row (box + ✕ + duration chips) so removing it
    // is one operation on the parent; Remove is kept on the row so the chrome pass can hide it when
    // only one row is left.
    private sealed class MorningTaskRow(TextBox box, int plannedSeconds)
    {
        public TextBox Box { get; } = box;

        public int PlannedSeconds { get; set; } = plannedSeconds;

        public Button Remove { get; } = MakeRemoveButton();

        public StackPanel Panel { get; set; } = new();

        // Step 9a — the project picked on this row. Both empty = untagged, which is the default
        // and stays the default: Ari never fills either of these in on the user's behalf.
        // ProjectId names one that already exists; ProjectName carries one typed into "+ new",
        // which the host resolves through create-or-get so typing it twice cannot make two.
        public long ProjectId { get; set; }

        public string ProjectName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step 9a — the project picker for one morning row.
    ///
    /// At rest it is a single chip on the duration row and costs ZERO height, which is the
    /// constraint that does not move. Expanded it adds a bounded strip beneath: a wrapping list of
    /// up to six most-recently-used projects plus "+ new".
    ///
    /// The bound is the point. Measured on the target display, the card ceiling is 632 DIPs and a
    /// three-task card with yesterday's block is already 566.36. One wrapped line of chips costs
    /// 35.6; three lines put the bottom of "Start the day" 12.75 DIPs off the window. So the strip
    /// is capped at two rows and scrolls inside itself past that — its contribution to the card is
    /// a constant no matter how many projects exist or how long their names are.
    /// </summary>
    private void AddProjectPicker(
        MorningTaskRow row,
        Panel durationRow,
        Panel rowPanel,
        IReadOnlyList<UserCheckInProject> projects)
    {
        Button chip = MakeDashedChip("+ project");
        durationRow.Children.Add(chip);

        // Collapsed, so at rest this adds nothing at all — not a margin, not a pixel.
        var pickerHost = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Visibility = Visibility.Collapsed
        };
        rowPanel.Children.Add(pickerHost);

        // A pressed control MUST change. Without this the chip looked identical open and closed,
        // and since the card grows upward the chip does not even move on screen — so the only
        // evidence of the press was elsewhere on the card. "Nothing happened" is what that reads
        // as, and it is what was reported.
        //
        // The neutral fill, never the accent: green means finished work and the primary button.
        void ShowOpen(bool open)
        {
            chip.Background = open ? new SolidColorBrush(ChipSelectedFill) : Brushes.Transparent;
            chip.Foreground = new SolidColorBrush(open ? ChipSelectedFg : LabelFg);
        }

        void Collapse()
        {
            pickerHost.Children.Clear();
            pickerHost.Visibility = Visibility.Collapsed;
            ShowOpen(false);
            _closeOpenProjectPicker = null;
        }

        void Pick(long id, string name)
        {
            _userInteracted = true;
            row.ProjectId = id;
            row.ProjectName = name;
            // Collapse FIRST: it restores the chip to its rest appearance, so styling the picked
            // state before it would be immediately undone.
            Collapse();
            ShowPickedProject(chip, name);
        }

        void OpenNewProjectBox()
        {
            pickerHost.Children.Clear();
            TextBox box = MakeInputBox("Project name");
            box.FontSize = 12;

            // A VISIBLE confirm, added 2026-08-13. The box shipped with Enter as the only way to
            // commit it, so the only user who could finish naming a project was one who already
            // knew that. Enter still works and still must not start the day.
            //
            // A button rather than commit-on-blur: blur fires when you click ANYTHING, including
            // "Start the day", so blur-to-commit would race the submit and would also commit a
            // half-typed name the moment you looked away. A button is the honest version.
            Button confirm = MakeChip("Add");
            confirm.Margin = new Thickness(6, 0, 0, 0);

            void Commit()
            {
                string typed = box.Text.Trim();
                if (typed.Length > 0)
                {
                    // Id 0 with a name = "create or get this one", resolved by the host.
                    Pick(0, typed);
                }
                else
                {
                    Collapse();
                }
            }

            confirm.Click += (_, _) => Commit();

            // Enter here must NOT start the day. Every other box on this card submits on Enter,
            // so without swallowing it a user typing a project name would launch the morning with
            // a half-made project — precisely the class of bug the caret week was lost to.
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Collapse();
                    e.Handled = true;
                }
            };

            box.TextChanged += (_, _) => _userInteracted = true;

            // Box and button share one line, so the confirm costs no height over the box alone.
            var line = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(confirm, 1);
            line.Children.Add(box);
            line.Children.Add(confirm);

            pickerHost.Children.Add(line);
            ArmFocus(box);
        }

        void Expand()
        {
            // One picker at a time. Three open on a three-task card measures 673 DIPs against a
            // 632 ceiling, so this is not tidiness — it is the height budget.
            _closeOpenProjectPicker?.Invoke();

            pickerHost.Children.Clear();
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

            foreach (UserCheckInProject project in projects.Take(MaxPickerProjects))
            {
                Button option = MakeChip(project.Name);
                option.Margin = new Thickness(0, 0, 6, 6);
                option.MaxWidth = ProjectChipMaxWidth;
                UserCheckInProject captured = project;
                option.Click += (_, _) => Pick(captured.Id, captured.Name);
                wrap.Children.Add(option);
            }

            // Clearing the tag has to be reachable: untagged is a choice, not an error, so it
            // gets the same one tap as picking. Only offered once something is picked.
            if (row.ProjectId != 0 || row.ProjectName.Length > 0)
            {
                Button clear = MakeChip("None");
                clear.Margin = new Thickness(0, 0, 6, 6);
                clear.Click += (_, _) =>
                {
                    _userInteracted = true;
                    row.ProjectId = 0;
                    row.ProjectName = string.Empty;
                    Collapse();
                    ShowEmptyProject(chip);
                };
                wrap.Children.Add(clear);
            }

            Button addNew = MakeChip("+ new");
            addNew.Margin = new Thickness(0, 0, 6, 6);
            addNew.Click += (_, _) =>
            {
                _userInteracted = true;
                OpenNewProjectBox();
            };
            wrap.Children.Add(addNew);

            // The bound: two rows of chips, then it scrolls inside itself. This is what makes
            // "Start the day cannot leave the screen" a property of the layout rather than a hope
            // about how many projects the user happens to have.
            var bounded = new ScrollViewer
            {
                MaxHeight = PickerMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = wrap
            };

            pickerHost.Children.Add(bounded);
            pickerHost.Visibility = Visibility.Visible;
            ShowOpen(true);
            _closeOpenProjectPicker = Collapse;
        }

        chip.Click += (_, _) =>
        {
            _userInteracted = true;
            if (pickerHost.Visibility == Visibility.Visible)
            {
                Collapse();
                return;
            }

            Expand();
        };
    }

    // Six, per Yadin (2026-08-12): beyond that the user manages projects in Control Center. The
    // list is most-recently-used first, so the week's work floats to the top on its own.
    private const int MaxPickerProjects = 6;

    // Two chip rows (23.63 each) plus their 6px gutters, measured. Past this the strip scrolls
    // rather than growing the card.
    private const double PickerMaxHeight = 60d;

    // A long name is ellipsised, never allowed to push the row wider than it has: the duration row
    // has 347 DIPs and 1h/2h/3h already use 116.5 of them (measured).
    private const double ProjectChipMaxWidth = 150d;

    /// <summary>
    /// The picked state: a single chip carrying the name, in the NEUTRAL selected fill. Never the
    /// accent — green means finished work and the primary button, and nothing else, ever.
    /// </summary>
    private static void ShowPickedProject(Button chip, string name)
    {
        chip.Content = name;
        chip.MaxWidth = ProjectChipMaxWidth;
        ApplyButtonTemplate(chip, 5d);
        HighlightChip(chip, selected: true);
    }

    private static void ShowEmptyProject(Button chip)
    {
        chip.Content = "+ project";
        chip.MaxWidth = double.PositiveInfinity;
        ApplyDashedTemplate(chip, 5d);
        chip.Background = Brushes.Transparent;
        chip.Foreground = new SolidColorBrush(LabelFg);
    }

    /// <summary>
    /// The empty state: a DASHED muted chip, so "nothing chosen" reads as an invitation rather
    /// than as a selection. WPF's Border cannot draw a dashed edge, so the chrome is a Rectangle
    /// with a dash array instead of the usual templated Border.
    /// </summary>
    private static Button MakeDashedChip(string label)
    {
        var chip = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(LabelFg),
            BorderThickness = new Thickness(0)
        };
        ApplyDashedTemplate(chip, 5d);
        return chip;
    }

    private static void ApplyDashedTemplate(Button button, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Grid));

        var outline = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        outline.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(CardBorder));
        outline.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1d);
        outline.SetValue(
            System.Windows.Shapes.Shape.StrokeDashArrayProperty,
            new DoubleCollection(new[] { 3d, 2d }));
        outline.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, radius);
        outline.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, radius);
        outline.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.Transparent);
        root.AppendChild(outline);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(FrameworkElement.MarginProperty, TemplateBinding("Padding"));
        root.AppendChild(presenter);

        template.VisualTree = root;
        button.Template = template;
    }

    /// <summary>Moves the refusal hint so it sits with the input it is about.</summary>
    private void MoveFocusHintTo(Panel target)
    {
        if (ReferenceEquals(_focusHint.Parent, target))
        {
            return;
        }

        (_focusHint.Parent as Panel)?.Children.Remove(_focusHint);
        target.Children.Add(_focusHint);
    }

    /// <summary>
    /// Marks the armed box when Windows refused the keyboard. The hint says what to do; this says
    /// WHERE — without it the box looks identical whether it has the caret or not, which is the
    /// whole reason a task went into another application unnoticed.
    /// </summary>
    private void SetBoxAccent(bool refused)
    {
        if (_accentedBox is { } previous)
        {
            previous.BorderBrush = new SolidColorBrush(CardBorder);
            previous.BorderThickness = new Thickness(HairlineThickness);
            _accentedBox = null;
        }

        if (refused && _focusTarget is TextBox box)
        {
            // A box that HOLDS the keyboard needs no colour at all - the caret already says so.
            // Only a REFUSED box gets marked, and it is marked in grey: it is pointing, not
            // celebrating (Yadin, 2026-08-10, correcting the earlier spec).
            box.BorderBrush = new SolidColorBrush(RefusedBorderFg);
            box.BorderThickness = new Thickness(1d);
            _accentedBox = box;
        }
    }

    /// <summary>
    /// The rule, after a task was typed into another application unnoticed on 2026-08-09: while a
    /// card with a text box is open, the keyboard belongs to Ari — and a box that OPENS LATER is the
    /// same moment as the card appearing, so it runs the same routine.
    /// </summary>
    /// <remarks>
    /// Two things the old inline <c>Keyboard.Focus</c> skipped. It never re-acquired foreground, so
    /// on a card that had lost it the caret moved but the keystrokes did not. And it ran
    /// synchronously against a TextBox added to the tree microseconds earlier, before layout — WPF
    /// will not focus a control that is not yet visible, so the request was silently dropped and
    /// focus stayed on the "+ add" button. <see cref="ApplyFocusTarget"/> queues at Input priority,
    /// which is after layout.
    /// </remarks>
    private void ArmFocus(Control target)
    {
        _focusTarget = target;
        AcquireForegroundAndFocus();
    }

    // Step 5 — one-tap done. The midday card stops asking one vague question about "the goal" and
    // lists today's actual tasks with the MEASURED time already beside each one. Tick what is done,
    // press once. Nothing to type, nothing to remember.
    //
    // Falls back to the original three-chip question whenever there are no tasks to show, so a day
    // that predates step 3 — or a user who dismissed the morning card — is no worse off than before.
    private void BuildMidday(UserCheckInPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.ReportLine))
        {
            AddMutedLine(prompt.ReportLine, bottomMargin: 10);
        }

        IReadOnlyList<TaskBoardLine> open = OpenLines(prompt);
        if (open.Count == 0)
        {
            AddQuestionLabel(prompt.QuestionLabel);
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach ((string value, string label) in UserCheckInChoices.MiddayAnswers)
            {
                Button chip = MakeChip(label);
                chip.Click += (_, _) => Respond(new UserCheckInResponse(
                    UserCheckInKind.Midday, TextReply: null, Choice: value, Dismissed: false));
                chipRow.Children.Add(chip);
            }

            _bodyStack.Children.Add(chipRow);
            return;
        }

        AddSectionLabel("TODAY", topMargin: 0);
        AddQuestionLabel(prompt.QuestionLabel);
        List<TaskOutcomeRow> rows = AddTaskBoard(open, includeDrop: false);
        AddMeasuredFootnote();

        Button save = MakePrimaryButton("Save");
        save.Click += (_, _) => Respond(new UserCheckInResponse(
            UserCheckInKind.Midday,
            TextReply: null,
            // Step 5 — the yes/partly/not_yet answer is now DERIVED from what was ticked rather
            // than asked separately, so daily_goals stays a view over the tasks.
            Choice: UserCheckInTaskBoard.MiddayAnswerFor(CollectOutcomes(rows)),
            Dismissed: false,
            TaskOutcomes: CollectOutcomes(rows)));
        _bodyStack.Children.Add(save);
    }

    /// <summary>
    /// Complaint 3 — the close-out card. Deliberately a DIFFERENT question from the first midday:
    /// that one asks what is done, this one asks what is left and whether it still lands today.
    /// Cloning card one is what teaches a person to ignore both.
    /// </summary>
    private void BuildMiddayClose(UserCheckInPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.ReportLine))
        {
            AddMutedLine(prompt.ReportLine, bottomMargin: 10);
        }

        IReadOnlyList<TaskBoardLine> open = OpenLines(prompt);
        if (open.Count == 0)
        {
            // Nothing open. There is no forward-looking question to ask, so do not invent one -
            // close the card with a single acknowledgement rather than manufacturing a choice.
            AddQuestionLabel(prompt.QuestionLabel);
            Button ok = MakePrimaryButton("Nothing left");
            ok.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.MiddayClose, TextReply: null, Choice: null, Dismissed: false));
            _bodyStack.Children.Add(ok);
            return;
        }

        AddSectionLabel("STILL OPEN", topMargin: 0);
        AddQuestionLabel(prompt.QuestionLabel);

        // Today / Tomorrow, not Done / Not yet. Neither writes anything to the task - an open task
        // carries by staying open - so answering this card can never lose work.
        List<TaskOutcomeRow> rows = AddTaskBoard(
            open,
            includeDrop: false,
            choices: new List<(string Action, string Label)>
            {
                (UserCheckInTaskOutcome.NotYet, "Today"),
                (UserCheckInTaskOutcome.Defer, "Tomorrow")
            },
            defaultIndex: 0);
        AddMeasuredFootnote();

        Button save = MakePrimaryButton("Save");
        save.Click += (_, _) => Respond(new UserCheckInResponse(
            UserCheckInKind.MiddayClose,
            TextReply: null,
            // No yes/partly/not_yet here: that is the FIRST midday's question, and answering it
            // from this card would let the close-out silently stand in for a check-in it is not.
            Choice: null,
            Dismissed: false,
            TaskOutcomes: CollectOutcomes(rows)));
        _bodyStack.Children.Add(save);
    }

    /// <summary>
    /// Steps 5 + 7 — renders one row per task: the title, the measured time, and the taps. Returns
    /// the rows so the caller can read what was chosen when the card is answered.
    ///
    /// Complaint 3 — the choice set is a parameter so the close-out card can ask Today/Tomorrow
    /// through the SAME row renderer. Three cards rendering the same object three ways is how they
    /// drift; that is why AddTaskRow is shared, and the chips now follow the same rule.
    /// </summary>
    private List<TaskOutcomeRow> AddTaskBoard(
        IReadOnlyList<TaskBoardLine> lines,
        bool includeDrop,
        List<(string Action, string Label)>? choices = null,
        int defaultIndex = 1)
    {
        var rows = new List<TaskOutcomeRow>();
        foreach (TaskBoardLine line in lines)
        {
            var row = new TaskOutcomeRow(line);

            // The SAME row renderer the morning card uses — title, bar of actual against planned,
            // and the measured time in the fixed tabular column. Sharing it is the point: three
            // cards rendering the same object three ways is how they drift.
            //
            // fadeUnfinished: false — this is today's live work, not history. See AddTaskRow.
            AddTaskRow(line, fadeUnfinished: false);

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };

            if (line.DaysCarried >= UserCheckInTaskBoard.CarriedMentionThreshold)
            {
                AddMutedLine($"carried {line.DaysCarried} days", bottomMargin: 0, parent: panel, fontSize: 11);
            }

            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var rowChoices = choices is { Count: > 0 }
                ? new List<(string Action, string Label)>(choices)
                : new List<(string Action, string Label)>
                {
                    (UserCheckInTaskOutcome.Done, "Done"),
                    (UserCheckInTaskOutcome.NotYet, includeDrop ? "Keep" : "Not yet")
                };

            if (includeDrop && choices is null)
            {
                // Step 7 — unfinished work is never silently dropped, and never silently carried
                // forever either. One tap says which.
                rowChoices.Add((UserCheckInTaskOutcome.Drop, "Drop it"));
            }

            var chips = new List<Button>();
            foreach ((string action, string label) in rowChoices)
            {
                Button chip = MakeChip(label);
                chips.Add(chip);
                chip.Click += (_, _) =>
                {
                    _userInteracted = true;
                    row.Action = action;
                    for (int i = 0; i < chips.Count; i++)
                    {
                        HighlightChip(chips[i], chips[i] == chip);
                    }
                };
                chipRow.Children.Add(chip);
            }

            // Default selection is the harmless one: still open, still carried, nothing claimed.
            // On the close-out card that is "Today" at index 0 — leaving the day's plan as it
            // stands. Either way the default writes nothing.
            HighlightChip(chips[Math.Clamp(defaultIndex, 0, chips.Count - 1)], true);
            panel.Children.Add(chipRow);
            _bodyStack.Children.Add(panel);
            rows.Add(row);
        }

        return rows;
    }

    private static IReadOnlyList<UserCheckInTaskOutcome> CollectOutcomes(List<TaskOutcomeRow> rows)
        => rows.Select(r => new UserCheckInTaskOutcome(r.Line.Id, r.Action)).ToList();

    /// <summary>
    /// The open work a card should ask about. Complaint 3 — excludes anything the close-out card
    /// already moved to a later day, so no surface can re-ask about a commitment just made.
    /// </summary>
    private IReadOnlyList<TaskBoardLine> OpenLines(UserCheckInPrompt prompt)
        => prompt.Tasks is { } lines
            ? UserCheckInTaskBoard.StillToAsk(lines)
            : Array.Empty<TaskBoardLine>();

    /// <summary>
    /// The measured figure is present-and-working time, not wall clock — roughly two thirds of it in
    /// practice, because time away credits nothing. Saying so is not a disclaimer: an unexplained
    /// number that reads low is a number people stop trusting, and this one has to be trustworthy
    /// enough to put in front of a client.
    /// </summary>
    private void AddMeasuredFootnote()
        => AddMutedLine("Time counted while you were at the machine.", bottomMargin: 0, fontSize: 11, topMargin: 10);

    private void AddMutedLine(
        string text,
        double bottomMargin,
        Panel? parent = null,
        double fontSize = 11,
        double topMargin = 0)
    {
        (parent ?? _bodyStack).Children.Add(new TextBlock
        {
            FontSize = fontSize,
            Foreground = new SolidColorBrush(LabelFg),
            TextWrapping = TextWrapping.Wrap,
            Text = text,
            Margin = new Thickness(0, topMargin, 0, bottomMargin)
        });
    }

    // One task's row on the midday/evening board. Defaults to "still open": a card the user closes
    // without touching must never mark work done or drop it.
    private sealed class TaskOutcomeRow(TaskBoardLine line)
    {
        public TaskBoardLine Line { get; } = line;

        public string Action { get; set; } = UserCheckInTaskOutcome.NotYet;
    }

    // Part 2b — renders the prompt's fixed question label above the answer controls. Skipped when the
    // label is blank so nothing changes for kinds that don't carry one.
    private void AddQuestionLabel(string questionLabel)
    {
        if (string.IsNullOrWhiteSpace(questionLabel))
        {
            return;
        }

        _bodyStack.Children.Add(new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(TitleLiveFg),
            TextWrapping = TextWrapping.Wrap,
            Text = questionLabel.Trim(),
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    // Step 7 — the evening CLOSES the day instead of opening a new one. The "what's tomorrow's
    // task?" box is gone: Yadin stopped answering it, which is the clearest signal available, and
    // unfinished work now carries itself, so asking was buying nothing.
    //
    // What is left is one tap per unfinished item — keep it or drop it — which makes tomorrow's
    // morning proposal (step 4) free. Finished work is shown but not asked about.
    private void BuildEndOfDay(UserCheckInPrompt prompt)
    {
        // The measured day summary renders as its own block (mirrors midday) so it stays visible
        // whether the message above it is the template or the LLM line.
        if (!string.IsNullOrWhiteSpace(prompt.ReportLine))
        {
            AddMutedLine(prompt.ReportLine, bottomMargin: 10);
        }

        IReadOnlyList<TaskBoardLine> board = prompt.Tasks ?? Array.Empty<TaskBoardLine>();
        IReadOnlyList<TaskBoardLine> open = OpenLines(prompt);

        // Phase 5 — the correction, directly under the measured line and above the day's work. It
        // sits ABOVE because it is about the card's own premise: if Ari has the day wrong, nothing
        // below it is worth answering, and the user should not have to read a whole board to find
        // the way out.
        //
        // Chat 2 (2026-08-24) — rendered on EVERY evening card, including the no-tasks branch. It
        // used to be gated on there being a board, which left the one day that most needs it with no
        // way out of the card at all: a day with no tasks is exactly the day "wasn't working today"
        // is for, and the branch below offers only a box asking what is first tomorrow.
        AddCorrectionBlock();

        if (board.Count == 0)
        {
            // Chat 6 (2026-08-24) — the empty question is gone.
            //
            // This asked "What's first tomorrow?" into a blank box with nothing prefilled and
            // nothing computed to suggest. On a day with no tasks Ari knows nothing about tomorrow,
            // so the box was Ari asking the user to do the work of proposing and then calling the
            // answer a plan. Ask only when there is something to propose.
            //
            // What is left is a card that still does its job: the measured line, the correction
            // block above (which is how you say the day is not over), and a way to close the day.
            // That is more honest than a text box nobody filled in.
            //
            // The cost is real and accepted: this box was the only way a task-less day recorded
            // plan_tomorrow, so a "just track" day no longer carries a typed sentence into the
            // morning. It was carrying one the user was prompted to invent, which is the thing
            // being removed.
            Button closeDay = MakePrimaryButton("Close the day");
            closeDay.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.EndOfDay,
                TextReply: null,
                Choice: null,
                Dismissed: false));
            _bodyStack.Children.Add(closeDay);
            return;
        }

        // Finished work: shown, counted, not asked about. Seeing it is the point — and it goes
        // through the same renderer as everywhere else, so the check, the bar and the number column
        // line up with the morning card rather than being a second dialect.
        IReadOnlyList<TaskBoardLine> finished = board.Where(line => line.IsDone).ToList();
        if (finished.Count > 0)
        {
            AddSectionLabel("FINISHED", topMargin: 0);
            foreach (TaskBoardLine done in finished)
            {
                AddTaskRow(done, fadeUnfinished: false);
            }
        }

        if (open.Count == 0)
        {
            AddMutedLine("Nothing left open. That is the whole day closed.", bottomMargin: 0, topMargin: 6);
            Button finish = MakePrimaryButton("Good night");
            finish.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.EndOfDay,
                TextReply: null,
                Choice: null,
                Dismissed: false,
                TaskOutcomes: Array.Empty<UserCheckInTaskOutcome>()));
            _bodyStack.Children.Add(finish);
            return;
        }

        AddSectionLabel("STILL OPEN", topMargin: finished.Count > 0 ? 12 : 0);
        AddQuestionLabel(prompt.QuestionLabel);
        List<TaskOutcomeRow> rows = AddTaskBoard(open, includeDrop: true);
        AddMeasuredFootnote();

        Button save = MakePrimaryButton("Close the day");
        save.Click += (_, _) => Respond(new UserCheckInResponse(
            UserCheckInKind.EndOfDay,
            TextReply: null,
            Choice: null,
            Dismissed: false,
            TaskOutcomes: CollectOutcomes(rows)));
        _bodyStack.Children.Add(save);
    }

    /// <summary>
    /// Phase 5 — "Ari thinks your day is done." and the one tap that says otherwise.
    ///
    /// Dashed, like every other "nothing chosen yet" surface on this card, so it reads as an
    /// invitation rather than as a selection. Neither chip is preselected and neither is the
    /// primary action: the card's own answer is still "Close the day", and a correction is the
    /// exception. One tap responds immediately — a correction that needed confirming would be two
    /// taps to say the card was wrong, which is worse than not offering it.
    /// </summary>
    private void AddCorrectionBlock()
    {
        var inner = new StackPanel { Margin = new Thickness(11, 9, 11, 10) };
        AddMutedLine(UserCheckInCorrections.AssumptionLabel, bottomMargin: 7, parent: inner, fontSize: 11.5);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach ((string value, string label) in UserCheckInCorrections.Options)
        {
            Button chip = MakeChip(label);
            // Captured per iteration on purpose: every chip must send its OWN value.
            string correction = value;
            chip.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.EndOfDay,
                TextReply: null,
                Choice: null,
                Dismissed: false,
                // No outcomes. The day is not being closed, so nothing on the board is being
                // answered — sending an empty list here would read downstream as "asked and
                // nothing ticked", which is a different and untrue statement.
                TaskOutcomes: null,
                Correction: correction));
            chips.Children.Add(chip);
        }

        inner.Children.Add(chips);

        var frame = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        frame.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(CardBorder),
            StrokeThickness = 1d,
            StrokeDashArray = new DoubleCollection(new[] { 3d, 2d }),
            RadiusX = 7d,
            RadiusY = 7d,
            Fill = Brushes.Transparent
        });
        frame.Children.Add(inner);
        _bodyStack.Children.Add(frame);
    }

    /// <summary>
    /// Chat 2 — "not working today", offered once on the first card of the day.
    ///
    /// Quiet by construction: a muted, left-aligned chip below the primary action, not a second
    /// button competing with it. Someone who IS working should be able to ignore it without reading
    /// it, and someone who is not should find it without hunting.
    /// </summary>
    private void AddNotWorkingTodayTap()
    {
        Button chip = MakeChip(UserCheckInDayOff.Label);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.Margin = new Thickness(0, 10, 0, 0);
        chip.Foreground = new SolidColorBrush(LabelFg);
        chip.Click += (_, _) => Respond(new UserCheckInResponse(
            _kind,
            TextReply: null,
            Choice: null,
            // Not a dismissal: a dismissal says nothing about the day, and this says the day is not
            // a working one. The host needs to tell them apart to know whether to mute.
            Dismissed: false,
            TaskOutcomes: null,
            Correction: null,
            NotWorkingToday: true));
        _bodyStack.Children.Add(chip);
    }

    private void Respond(UserCheckInResponse response)
    {
        if (_responded)
        {
            return;
        }

        _responded = true;
        Hide();
        _onResponse(response);
    }

    // Part 5 (async narrative) — replace the templated message with the LLM line after the card
    // is already on screen. The card is shown instantly with the honest template and the line
    // upgrades it when it lands, so a slow model never spends the three seconds of willingness
    // the morning card depends on.
    //
    // Refuses (returning false, silently) whenever swapping would be wrong rather than merely
    // unnecessary: a different kind is showing, the exchange is over, the card is hidden, or the
    // user has started answering. Refusal is a normal outcome, not an error — the template is
    // honest copy in its own right.
    //
    // The swap is safe to do live because the card is bottom-right anchored: PositionBottomRight
    // pins the bottom edge, so a taller or shorter message moves only the TOP edge. Everything
    // below the message — the input box, the chips, the primary button — stays pixel-stable, so
    // there is nothing for the user to chase. The cross-fade covers the text change itself.
    internal bool TryUpgradeMessage(UserCheckInKind kind, string line)
    {
        string clean = (line ?? string.Empty).Trim();
        if (clean.Length == 0
            || !IsVisible
            || _responded
            || _confirmationMode
            || _kind != kind
            || HasUserEngaged()
            || string.Equals(_message.Text, clean, StringComparison.Ordinal))
        {
            return false;
        }

        CrossFadeMessageTo(clean);
        return true;
    }

    /// <summary>
    /// True once this card has been answered in any way the late line must not disturb: a real
    /// edit to the text box, a chip selection, or a focused box the user has already typed into.
    /// </summary>
    private bool HasUserEngaged()
    {
        if (_userInteracted)
        {
            return true;
        }

        return _focusTarget is TextBox { IsKeyboardFocused: true } box && box.Text.Trim().Length > 0;
    }

    /// <summary>
    /// Fades the message out, swaps the text, fades it back in. Both animations carry an explicit
    /// From — an omitted From is what caused the widget strip bug fixed in 1.2.114, because the
    /// animation then starts from whatever the property happens to hold rather than a known value.
    /// </summary>
    private void CrossFadeMessageTo(string line)
    {
        var fadeOut = new DoubleAnimation(1d, 0d, new Duration(MessageFadeDuration));
        fadeOut.Completed += (_, _) =>
        {
            _message.Text = line;
            _message.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0d, 1d, new Duration(MessageFadeDuration)));
        };

        _message.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // Step 1 — the card must accept typing the instant it appears. Three defects sat behind the
    // single missing focus call the plan described; this is the third. Logical focus is not
    // foreground: Show() + Activate() is refused by Windows for a process that does not already
    // own the foreground, so keystrokes went to whatever app was actually in front. Clicking the
    // box worked only because the click itself granted foreground.
    //
    // Fallback is deliberate and load-bearing: when foreground acquisition fails we still Activate
    // and still focus the box, so a click-then-type user is never worse off than before this fix.
    private void AcquireForegroundAndFocus(int tryNumber = 0)
    {
        // Only seize foreground when there is something to type into. Midday is chips-only, so
        // taking foreground there would spend an interruption to buy nothing; it keeps the old
        // Activate-only behaviour and stays as quiet as it is today.
        _focusAssertions++;
        ForegroundAttempt result = _focusTarget != null
            ? TryAcquireForeground()
            : new ForegroundAttempt(false, "not_attempted_no_input");
        Activate();
        ApplyFocusTarget();

        UserAgentFileLog.Info(
            $"checkin_card_focus kind={_kind} try={tryNumber} foreground={result.Acquired}"
            + $" reason={result.Reason} target={(_focusTarget == null ? "none" : "input")}");

        if (result.Acquired || _focusTarget == null)
        {
            _focusHint.Visibility = Visibility.Collapsed;
            SetBoxAccent(false);
            return;
        }

        // The retry IS the experiment. Windows lists eight conditions under which it will grant
        // foreground; several are transient (a menu is open, a window is mid-activation, the
        // foreground app is between input events) and several are not. Which one refused a given
        // card is not derivable from the return value, and the morning card wins where the evening
        // card sometimes does not — so rather than assume, try again shortly and log which attempt
        // won. A `try=1` success in the log means timing and the retry earned its place; three
        // identical refusals mean the cause is structural and the honest hint is all there is.
        //
        // Bounded to two extra attempts inside ~half a second, and abandoned the moment the user
        // starts answering: this must never become a fight for a keyboard someone else is using.
        if (tryNumber < ForegroundRetryDelays.Length && !SuppressForegroundAcquisition)
        {
            ScheduleForegroundRetry(tryNumber);
            return;
        }

        _focusHint.Visibility = Visibility.Visible;
        SetBoxAccent(true);
    }

    // Two extra attempts, close together. Long enough to outlast a menu closing or a window
    // finishing activation; short enough that a user who is about to type notices nothing.
    private static readonly TimeSpan[] ForegroundRetryDelays =
    {
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(350)
    };

    private void ScheduleForegroundRetry(int tryNumber)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = ForegroundRetryDelays[tryNumber]
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // Abandon silently if the moment has passed: the card is gone, was answered, or the
            // user has started typing — in which case they already own the keyboard and grabbing
            // it again would be taking it from them.
            if (!IsVisible || _responded || _confirmationMode || _focusTarget == null || HasUserEngaged())
            {
                return;
            }

            AcquireForegroundAndFocus(tryNumber + 1);
        };

        timer.Start();
    }

    /// <summary>
    /// Hands the caret to the armed control once activation has settled. Queued at Input priority
    /// because WPF will otherwise grant logical focus before the HWND is actually foreground, which
    /// looks identical to the bug being fixed.
    /// </summary>
    private void ApplyFocusTarget()
    {
        if (_focusTarget is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_focusTarget is { } target && IsVisible)
            {
                Keyboard.Focus(target);
            }
        });
    }

    /// <summary>
    /// The standard AttachThreadInput dance: Windows only grants SetForegroundWindow to a process
    /// that already owns the foreground or shares an input queue with the one that does. The card
    /// fires on a timer, not on a click, so it never qualifies on its own. Returns false on any
    /// failure — the caller degrades to the old Activate-and-focus path rather than throwing.
    /// </summary>
    /// <summary>The outcome of one foreground grab, with a reason code for every refusal.</summary>
    private readonly record struct ForegroundAttempt(bool Acquired, string Reason);

    private ForegroundAttempt TryAcquireForeground()
    {
        if (SuppressForegroundAcquisition)
        {
            return new ForegroundAttempt(false, "suppressed_for_test");
        }

        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return new ForegroundAttempt(false, "no_hwnd");
            }

            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            uint ourThread = NativeMethods.GetCurrentThreadId();
            uint foregroundThread = 0u;
            uint foregroundPid = 0u;
            if (foregroundWindow != IntPtr.Zero)
            {
                foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out foregroundPid);
            }

            bool attached = foregroundThread != 0
                && foregroundThread != ourThread
                && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
            try
            {
                bool reported = NativeMethods.SetForegroundWindow(hwnd);
                int lastError = reported ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();

                // DO NOT TRUST THE RETURN VALUE (2026-08-11). Windows can return nonzero while the
                // window never actually reaches the foreground - under an active foreground lock it
                // may only flash the taskbar button. That is what happened on the morning of
                // 2026-08-11: the log recorded foreground=True, so no hint and no grey border were
                // shown, and the typing went somewhere else with nothing on screen to explain it.
                // A silent failure is worse than a refusal, because the refusal already has a
                // visible state. Ask the OS who actually holds it.
                bool actuallyForeground = reported && NativeMethods.GetForegroundWindow() == hwnd;
                if (actuallyForeground)
                {
                    return new ForegroundAttempt(true, foregroundThread == 0
                        ? "ok_no_foreground"
                        : attached ? "ok_attached" : "ok_direct");
                }

                if (reported)
                {
                    // Reported success, did not get it. Treated exactly as a refusal so the card
                    // shows the grey border and the hint rather than looking ready to type into.
                    return new ForegroundAttempt(false,
                        $"refused_reported_ok_but_not_foreground:fg={DescribeProcess(ForegroundPid())}"
                        + $",locktimeout_ms={ReadForegroundLockTimeout()}");
                }

                // The refusal detail. Windows grants SetForegroundWindow only under a short list of
                // conditions; which one we failed is not derivable from the return value alone, so
                // record everything that distinguishes them: whether the input-queue attach worked,
                // the Win32 error, who actually held the foreground, and the system's foreground
                // lock timeout (a non-zero value is itself a documented reason for refusal).
                //
                // Process NAME only, never a window title — titles carry document names and message
                // subjects. The name is the datum that identifies the culprit; the title adds
                // nothing here and would put user content in a durable log.
                return new ForegroundAttempt(false,
                    $"refused_{(attached ? "attached" : foregroundThread == 0 ? "no_fg" : "unattached")}"
                    + $":err={lastError},fg={DescribeProcess(foregroundPid)},locktimeout_ms={ReadForegroundLockTimeout()}");
            }
            finally
            {
                if (attached)
                {
                    _ = NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            return new ForegroundAttempt(false, $"exception:{ex.GetType().Name}");
        }
    }

    /// <summary>Who holds the foreground right now, asked fresh rather than remembered.</summary>
    private static uint ForegroundPid()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0u;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>Process name for the pid that held the foreground, or a marker. Never a title.</summary>
    private static string DescribeProcess(uint pid)
    {
        if (pid == 0)
        {
            return "none";
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid{pid}";
        }
    }

    /// <summary>
    /// Reads (never writes) SPI_GETFOREGROUNDLOCKTIMEOUT. A non-zero value means Windows is holding
    /// a foreground lock, which refuses SetForegroundWindow outright and merely flashes the taskbar
    /// button — a window that is Topmost still draws on top, so the card would be visible while the
    /// keystrokes went elsewhere. That is exactly the reported symptom, so the number is worth
    /// having before anyone proposes a remedy. -1 = the query itself failed.
    /// </summary>
    private static int ReadForegroundLockTimeout()
    {
        try
        {
            uint timeout = 0;
            return NativeMethods.SystemParametersInfo(
                NativeMethods.SpiGetForegroundLockTimeout, 0, ref timeout, 0)
                ? (int)timeout
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void PositionBottomRight()
    {
        Rect work = SystemParameters.WorkArea;

        // Step 8 guard. The card grows upward because the BOTTOM edge is pinned, so content that
        // outgrows the work area pushes the header off the top of the screen rather than off the
        // bottom — invisible, and the "Ari" row and the ✕ are the first things lost. Measured on a
        // 1920x1080 @150% display the work area is 672 DIPs and a three-task morning card is 452,
        // so this should never fire there; it is the difference between a safety net and a bug on
        // the next screen (a 1366x768 laptop, or a title that wraps to three lines).
        MaxHeight = Math.Max(240d, work.Height - (ScreenMargin * 2d));

        Left = work.Right - ActualWidth - ScreenMargin;
        Top = Math.Max(work.Top + ScreenMargin, work.Bottom - ActualHeight - ScreenMargin);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Tool window: keep it off Alt-Tab / taskbar even while activatable.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle, ex | NativeMethods.WsExToolWindow);
    }

    // ── styled control factories (dark v1) ──

    private static TextBox MakeInputBox(string placeholder)
    {
        var box = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(CardBg),
            Foreground = new SolidColorBrush(HeadlineFg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(HairlineThickness),
            CaretBrush = new SolidColorBrush(HeadlineFg),
            TextWrapping = TextWrapping.Wrap,
            Tag = placeholder
        };
        ApplyInputTemplate(box, 7d);
        return box;
    }

    private static Button MakeChip(string label)
    {
        var chip = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand
        };
        ApplyButtonTemplate(chip, 5d);
        HighlightChip(chip, selected: false);
        return chip;
    }

    /// <summary>
    /// Selection is a solid LIGHT fill, never green (Yadin, 2026-08-10). Green had drifted into a
    /// third meaning - finished, the primary action, AND "this chip is chosen" - and on a card with
    /// three duration rows that put seven green things on screen, exactly one of which meant
    /// finished. Neutral selection gives the colour back its single meaning.
    /// </summary>
    private static void HighlightChip(Button chip, bool selected)
    {
        chip.Background = selected ? new SolidColorBrush(ChipSelectedFill) : Brushes.Transparent;
        chip.Foreground = new SolidColorBrush(selected ? ChipSelectedFg : AiLineFg);
        chip.BorderBrush = selected ? Brushes.Transparent : new SolidColorBrush(CardBorder);
        chip.BorderThickness = new Thickness(selected ? 0d : HairlineThickness);
    }

    private static Button MakePrimaryButton(string label)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(AccentGreen),
            Foreground = new SolidColorBrush(OnAccentFg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        ApplyButtonTemplate(button, 7d);
        return button;
    }

    // WPF's stock Button and TextBox templates draw their own chrome, so a corner radius cannot be
    // set as a property - it has to come from the template. These are the smallest templates that
    // honour Background / BorderBrush / BorderThickness / Padding from the control itself, so the
    // rest of the styling stays where it is readable.
    private static void ApplyButtonTemplate(Button button, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        FrameworkElementFactory border = ChromeBorder(radius);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(FrameworkElement.MarginProperty, TemplateBinding("Padding"));
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
    }

    private static void ApplyInputTemplate(TextBox box, double radius)
    {
        var template = new ControlTemplate(typeof(TextBox));
        FrameworkElementFactory border = ChromeBorder(radius);
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        host.SetBinding(Control.PaddingProperty, TemplateBinding("Padding"));
        border.AppendChild(host);
        template.VisualTree = border;
        box.Template = template;
    }

    private static FrameworkElementFactory ChromeBorder(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        border.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        border.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        return border;
    }

    private static System.Windows.Data.Binding TemplateBinding(string path)
        => new(path) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent };

    // Per-row remove, matching the ✕ the widget already puts on every task. Tagged so a test can
    // tell it apart from the card's own dismiss ✕ in the header.
    private static Button MakeRemoveButton()
    {
        Button remove = MakeTextButton("✕", LabelFg);
        remove.Tag = "remove-row";
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.ToolTip = "Remove this one";
        return remove;
    }

    private static Button MakeTextButton(string label, Color fg)
    {
        return new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(fg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
    }

    private static class NativeMethods
    {
        public const int GwlExStyle = -20;
        public const int WsExToolWindow = 0x00000080;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // Read-only diagnostic. This code never calls the SET counterpart: changing the machine's
        // foreground lock is a system-settings change and is not Ari's to make.
        public const uint SpiGetForegroundLockTimeout = 0x2000;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
    }
}
