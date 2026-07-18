namespace Porta.UserAgent.Interventions;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
    private const double CardWidth = 380d;
    private const double ScreenMargin = 20d;

    private readonly Action<UserCheckInResponse> _onResponse;
    private readonly Border _cardBorder;
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly StackPanel _bodyStack;   // mode-specific content is rebuilt here each ShowPrompt
    private UserCheckInKind _kind;
    private bool _responded;
    // Confirmation mode: the card is echoing an acknowledgement (no further reply expected), so the
    // ✕ / Escape just hides rather than raising another dismissed response.
    private bool _confirmationMode;

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

        Content = _cardBorder;
        Loaded += (_, _) => PositionBottomRight();
        SizeChanged += (_, _) => PositionBottomRight();
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
        _eyebrow.Text = prompt.Kind switch
        {
            UserCheckInKind.Morning => "Good morning",
            UserCheckInKind.Midday => "Midday check-in",
            _ => "End of day"
        };
        _title.Text = prompt.Title;
        _message.Text = prompt.Message;
        _bodyStack.Children.Clear();

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
        Activate();
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

    private void BuildMorning(UserCheckInPrompt prompt)
    {
        TextBox goalBox = MakeInputBox("What do you want to get done today?");
        // Part 3 — last night's plan pre-fills the box so confirming it is a single click/Enter.
        if (!string.IsNullOrWhiteSpace(prompt.PrefillGoalText))
        {
            goalBox.Text = prompt.PrefillGoalText.Trim();
            goalBox.CaretIndex = goalBox.Text.Length;
        }

        _bodyStack.Children.Add(goalBox);

        string chosenPref = UserCheckInChoices.MorningTracking[0].Value;
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var chips = new List<Button>();
        foreach ((string value, string label) in UserCheckInChoices.MorningTracking)
        {
            Button chip = MakeChip(label);
            chips.Add(chip);
            chip.Click += (_, _) =>
            {
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

        Button share = MakePrimaryButton("Share with Ari");
        share.Click += (_, _) => Respond(new UserCheckInResponse(
            UserCheckInKind.Morning, goalBox.Text.Trim(), chosenPref, Dismissed: false));
        goalBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Respond(new UserCheckInResponse(UserCheckInKind.Morning, goalBox.Text.Trim(), chosenPref, Dismissed: false));
                e.Handled = true;
            }
        };
        _bodyStack.Children.Add(share);

        Loaded += (_, _) => goalBox.Focus();
    }

    private void BuildMidday(UserCheckInPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.ReportLine))
        {
            _bodyStack.Children.Add(new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(MutedFg),
                TextWrapping = TextWrapping.Wrap,
                Text = prompt.ReportLine,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach ((string value, string label) in UserCheckInChoices.MiddayAnswers)
        {
            Button chip = MakeChip(label);
            chip.Click += (_, _) => Respond(new UserCheckInResponse(
                UserCheckInKind.Midday, TextReply: null, Choice: value, Dismissed: false));
            chipRow.Children.Add(chip);
        }

        _bodyStack.Children.Add(chipRow);
    }

    private void BuildEndOfDay(UserCheckInPrompt prompt)
    {
        // The measured day summary renders as its own block (mirrors midday) so it stays visible
        // whether the message above it is the template or the LLM line.
        if (!string.IsNullOrWhiteSpace(prompt.ReportLine))
        {
            _bodyStack.Children.Add(new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(MutedFg),
                TextWrapping = TextWrapping.Wrap,
                Text = prompt.ReportLine,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        TextBox planBox = MakeInputBox("Plan tomorrow — one line");
        _bodyStack.Children.Add(planBox);

        Button save = MakePrimaryButton("Save plan");
        save.Click += (_, _) => Respond(new UserCheckInResponse(
            UserCheckInKind.EndOfDay, planBox.Text.Trim(), Choice: null, Dismissed: false));
        planBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Respond(new UserCheckInResponse(UserCheckInKind.EndOfDay, planBox.Text.Trim(), null, Dismissed: false));
                e.Handled = true;
            }
        };
        _bodyStack.Children.Add(save);
        Loaded += (_, _) => planBox.Focus();
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

    private void PositionBottomRight()
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - ScreenMargin;
        Top = work.Bottom - ActualHeight - ScreenMargin;
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
    }
}
