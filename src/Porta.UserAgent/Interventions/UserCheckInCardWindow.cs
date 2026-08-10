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
    private static readonly Color CardBg = Color.FromRgb(20, 25, 31);
    private static readonly Color CardBorder = Color.FromRgb(51, 63, 77);
    private static readonly Color TitleFg = Color.FromRgb(238, 243, 248);
    private static readonly Color BodyFg = Color.FromRgb(202, 214, 226);
    private static readonly Color MutedFg = Color.FromRgb(144, 168, 191);
    private static readonly Color AccentGreen = Color.FromRgb(0x16, 0xC7, 0x84);
    // Step 8 palette. Green means ONE thing on this card — finished — and the primary button. There
    // is deliberately no red anywhere: a task the user chose to drop is a decision, not an error,
    // and colouring it like a failure would be the card arguing with them.
    private static readonly Color HairlineFg = Color.FromRgb(0x27, 0x31, 0x3C);  // section rule
    private static readonly Color QuietFg = Color.FromRgb(0x6E, 0x7E, 0x91);     // dropped: faded, not alarming
    private static readonly Color BarTrackFg = Color.FromRgb(0x1C, 0x24, 0x2D);  // the unfilled remainder
    private static readonly Color BarQuietFg = Color.FromRgb(0x3E, 0x4B, 0x5A);  // a dropped task's own bar
    // Yesterday is history and today is the decision, so yesterday is rendered a step quieter
    // throughout — smaller, dimmer, no interaction. Quiet yesterday, loud today.
    private const double YesterdayRowFontSize = 11.5;
    private const double BarColumnWidth = 62d;
    private const double ValueColumnWidth = 56d;
    private const double BarHeight = 4d;
    private const double CardWidth = 380d;
    private const double ScreenMargin = 20d;
    // Long enough to read as a deliberate change, short enough not to feel like a reload.
    private static readonly TimeSpan MessageFadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly Action<UserCheckInResponse> _onResponse;
    private readonly Border _cardBorder;
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly StackPanel _bodyStack;   // mode-specific content is rebuilt here each ShowPrompt
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
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1.25)
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        _cardBorder.Child = stack;

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _eyebrow = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MutedFg),
            Text = "Ari"
        };
        Grid.SetColumn(_eyebrow, 0);
        headerRow.Children.Add(_eyebrow);

        Button dismiss = MakeTextButton("✕", MutedFg);
        dismiss.Click += (_, _) => DismissClicked();
        Grid.SetColumn(dismiss, 1);
        headerRow.Children.Add(dismiss);
        stack.Children.Add(headerRow);

        _title = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TitleFg),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(_title);

        _message = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(BodyFg),
            TextWrapping = TextWrapping.Wrap,
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
            Foreground = new SolidColorBrush(BodyFg),
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
            case UserCheckInKind.EndOfDay:
                BuildEndOfDay(prompt);
                break;
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
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MutedFg),
            Text = text,
            Margin = new Thickness(0, topMargin, 0, 5)
        });

        _bodyStack.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(HairlineFg),
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
    private void AddYesterdayRow(TaskBoardLine line)
    {
        bool done = line.IsDone;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });

        var title = new TextBlock
        {
            FontSize = YesterdayRowFontSize,
            // Finished work reads at full strength; dropped work fades back. The state is carried
            // by weight and colour rather than by a cross, which would read as an error.
            Foreground = new SolidColorBrush(done ? TitleFg : QuietFg),
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
                FontWeight = FontWeights.Bold
            });
        }

        title.Inlines.Add(new System.Windows.Documents.Run(line.Title));
        Grid.SetColumn(title, 0);
        row.Children.Add(title);

        var track = new Border
        {
            Height = BarHeight,
            Width = BarColumnWidth,
            CornerRadius = new CornerRadius(BarHeight / 2d),
            Background = new SolidColorBrush(BarTrackFg),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var fill = new Border
        {
            Height = BarHeight,
            Width = Math.Max(2d, BarColumnWidth * line.ProgressFraction),
            CornerRadius = new CornerRadius(BarHeight / 2d),
            Background = new SolidColorBrush(done ? AccentGreen : BarQuietFg),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        barHost.Children.Add(track);
        barHost.Children.Add(fill);
        Grid.SetColumn(barHost, 1);
        row.Children.Add(barHost);

        var value = new TextBlock
        {
            FontSize = YesterdayRowFontSize,
            Foreground = new SolidColorBrush(done ? BodyFg : QuietFg),
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
        IReadOnlyList<TaskBoardLine> yesterday = prompt.Yesterday ?? Array.Empty<TaskBoardLine>();
        if (yesterday.Count > 0)
        {
            AddSectionLabel("YESTERDAY", topMargin: 0);
            if (!string.IsNullOrWhiteSpace(prompt.YesterdaySummary))
            {
                _bodyStack.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    Foreground = new SolidColorBrush(BodyFg),
                    TextWrapping = TextWrapping.Wrap,
                    Text = prompt.YesterdaySummary,
                    Margin = new Thickness(0, 0, 0, 9)
                });
            }

            foreach (TaskBoardLine line in yesterday)
            {
                AddYesterdayRow(line);
            }

            AddSectionLabel("TODAY", topMargin: 12);
        }

        // The refusal hint belongs with the box it is about, not at the foot of a tall card.
        MoveFocusHintTo(_bodyStack);

        var rows = new List<MorningTaskRow>();
        var rowsPanel = new StackPanel { Orientation = Orientation.Vertical };
        _bodyStack.Children.Add(rowsPanel);

        Button addRow = MakeTextButton("+ add", MutedFg);
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
                    entries.Add(new UserCheckInTaskEntry(title, row.PlannedSeconds));
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
            panel.Children.Add(durationRow);
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
            previous.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50));
            previous.BorderThickness = new Thickness(1);
            _accentedBox = null;
        }

        if (refused && _focusTarget is TextBox box)
        {
            box.BorderBrush = new SolidColorBrush(AccentGreen);
            box.BorderThickness = new Thickness(1.5);
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
    /// Steps 5 + 7 — renders one row per task: the title, the measured time, and the taps. Returns
    /// the rows so the caller can read what was chosen when the card is answered.
    /// </summary>
    private List<TaskOutcomeRow> AddTaskBoard(IReadOnlyList<TaskBoardLine> lines, bool includeDrop)
    {
        var rows = new List<TaskOutcomeRow>();
        foreach (TaskBoardLine line in lines)
        {
            var row = new TaskOutcomeRow(line);
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, rows.Count == 0 ? 0 : 10, 0, 0)
            };

            // Title and the measured hours on one line: this is the pairing that makes Ari the only
            // tool where "done" arrives with the time behind it.
            panel.Children.Add(new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TitleFg),
                TextWrapping = TextWrapping.Wrap,
                Text = $"{line.Title} — {line.MeasuredLabel}"
            });

            if (line.DaysCarried >= UserCheckInTaskBoard.CarriedMentionThreshold)
            {
                AddMutedLine($"carried {line.DaysCarried} days", bottomMargin: 0, parent: panel, fontSize: 11);
            }

            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var choices = new List<(string Action, string Label)>
            {
                (UserCheckInTaskOutcome.Done, "Done"),
                (UserCheckInTaskOutcome.NotYet, includeDrop ? "Keep" : "Not yet")
            };

            if (includeDrop)
            {
                // Step 7 — unfinished work is never silently dropped, and never silently carried
                // forever either. One tap says which.
                choices.Add((UserCheckInTaskOutcome.Drop, "Drop it"));
            }

            var chips = new List<Button>();
            foreach ((string action, string label) in choices)
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
            HighlightChip(chips[1], true);
            panel.Children.Add(chipRow);
            _bodyStack.Children.Add(panel);
            rows.Add(row);
        }

        return rows;
    }

    private static IReadOnlyList<UserCheckInTaskOutcome> CollectOutcomes(List<TaskOutcomeRow> rows)
        => rows.Select(r => new UserCheckInTaskOutcome(r.Line.Id, r.Action)).ToList();

    private IReadOnlyList<TaskBoardLine> OpenLines(UserCheckInPrompt prompt)
        => prompt.Tasks?.Where(t => t.IsOpen).ToList() ?? (IReadOnlyList<TaskBoardLine>)Array.Empty<TaskBoardLine>();

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
        double fontSize = 12,
        double topMargin = 0)
    {
        (parent ?? _bodyStack).Children.Add(new TextBlock
        {
            FontSize = fontSize,
            Foreground = new SolidColorBrush(MutedFg),
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
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(BodyFg),
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

        if (board.Count == 0)
        {
            // No tasks for the day at all — nothing to close. Keep the old plan box so the evening
            // card is never an empty shell, and so a pre-step-3 day still records something.
            AddQuestionLabel(prompt.QuestionLabel);
            TextBox planBox = MakeInputBox("Anything to note before you stop?");
            planBox.TextChanged += (_, _) => _userInteracted = true;
            _bodyStack.Children.Add(planBox);

            Button saveNote = MakePrimaryButton("Save");
            saveNote.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.EndOfDay, planBox.Text.Trim(), Choice: null, Dismissed: false));
            planBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Respond(new UserCheckInResponse(UserCheckInKind.EndOfDay, planBox.Text.Trim(), null, Dismissed: false));
                    e.Handled = true;
                }
            };

            _bodyStack.Children.Add(saveNote);
            _focusTarget = planBox;
            return;
        }

        // Finished work: shown, counted, not asked about. Seeing it is the point.
        foreach (TaskBoardLine done in board.Where(line => line.IsDone))
        {
            AddMutedLine($"✓ {done.Title} — {done.MeasuredLabel}", bottomMargin: 4);
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
                bool ok = NativeMethods.SetForegroundWindow(hwnd);
                int lastError = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (ok)
                {
                    return new ForegroundAttempt(true, foregroundThread == 0
                        ? "ok_no_foreground"
                        : attached ? "ok_attached" : "ok_direct");
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
            MinHeight = 30,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xF3)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(AccentGreen),
            TextWrapping = TextWrapping.Wrap,
            Tag = placeholder
        };
        return box;
    }

    private static Button MakeChip(string label)
    {
        return new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(MutedFg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    private static void HighlightChip(Button chip, bool selected)
    {
        chip.Foreground = new SolidColorBrush(selected ? AccentGreen : MutedFg);
        chip.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0x1E, 0x4D, 0x3B) : Color.FromRgb(0x25, 0x25, 0x35));
    }

    private static Button MakePrimaryButton(string label)
    {
        return new Button
        {
            Content = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x3C, 0x30)),
            Foreground = new SolidColorBrush(AccentGreen),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x4D, 0x3B)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    // Per-row remove, matching the ✕ the widget already puts on every task. Tagged so a test can
    // tell it apart from the card's own dismiss ✕ in the header.
    private static Button MakeRemoveButton()
    {
        Button remove = MakeTextButton("✕", MutedFg);
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
