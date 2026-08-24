namespace Porta.ControlCenter;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>
/// Phase 3 rebuilt (2026-08-18) — the visual system for Today, Tasks, Projects and Reports,
/// taken from Ari_ControlCenter_Pages_Mockup.html, which is the spec.
///
/// THE PRINCIPLE, and every rule below follows from it: the page is quiet when things are fine and
/// loud only where something needs the user. A marker on every field is noise; a marker on the one
/// thing that is missing is information.
///
/// So: no borders on cards — a lighter surface on a darker page, separated by space, with hairlines
/// only INSIDE a card between rows. The accent is used for exactly three things, the primary
/// button, a completed status, and the progress fill, and nowhere else; that restraint is what
/// stops it looking like a toy. Amber appears only where something is genuinely missing.
/// </summary>
internal static class PageKit
{
    // ── Surfaces and ink ──────────────────────────────────────────────────────────────────────
    internal static readonly Color PageBg = Hex("#0E1013");
    internal static readonly Color Surface = Hex("#15181D");
    internal static readonly Color Raised = Hex("#1B1F25");
    internal static readonly Color Hairline = Hex("#242931");
    internal static readonly Color Ink = Hex("#F4F6F8");
    internal static readonly Color Ink2 = Hex("#9AA1AB");
    internal static readonly Color Ink3 = Hex("#666D77");

    // ── Accent and states ─────────────────────────────────────────────────────────────────────
    internal static readonly Color Accent = Hex("#3FBF7F");
    internal static readonly Brush AccentWash = new SolidColorBrush(Color.FromArgb(0x1A, 0x3F, 0xBF, 0x7F));
    internal static readonly Color Warn = Hex("#E3A83A");
    internal static readonly Color Stop = Hex("#E4706E");

    /// <summary>
    /// The eight task colours in fixed slot order. These are the dark-surface steps of the same
    /// system the report uses, checked for colourblind separation and for contrast against the card
    /// surface. DO NOT REORDER AND DO NOT SUBSTITUTE A HEX — the order is the safety mechanism, not
    /// a preference: a task keeps its colour for life, in the report and in every chart.
    ///
    /// Slot 6 is #008300. The mockup file renders it as #3F9E4D and that is an error in the file.
    /// Slot 0 is not here: it folds to the neutral grey, as in the report.
    /// </summary>
    internal static readonly Color[] Slots =
    {
        Hex("#3987E5"), Hex("#D95926"), Hex("#199E70"), Hex("#C98500"),
        Hex("#D55181"), Hex("#008300"), Hex("#9085E9"), Hex("#E66767")
    };

    internal static Brush SlotBrush(int slot)
        => new SolidColorBrush(slot >= 1 && slot <= Slots.Length ? Slots[slot - 1] : Ink3);

    // ── Spacing, from the mockup ──────────────────────────────────────────────────────────────
    internal static readonly Thickness CardPad = new(26, 24, 26, 24);
    internal static readonly Thickness RowPad = new(26, 13, 26, 13);
    internal const double FigureGap = 44;

    internal static Brush B(Color c) => new SolidColorBrush(c);

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // ── Building blocks ───────────────────────────────────────────────────────────────────────

    /// <summary>A card. No border, ever — tone and space do that job.</summary>
    internal static Border Card(UIElement child, double topMargin = 18)
        => new()
        {
            Background = B(Surface),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, topMargin, 0, 0),
            Child = child
        };

    /// <summary>A hairline between rows. Only ever inside a card.</summary>
    internal static Border Hair()
        => new() { Height = 1, Background = B(Hairline) };

    internal static TextBlock Text(
        string text, double size, Color colour, FontWeight? weight = null,
        Thickness? margin = null, bool wrap = false)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = B(colour),
            FontWeight = weight ?? FontWeights.Normal,
            Margin = margin ?? new Thickness(0),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };

    internal static TextBlock PageTitle(string t) => Text(t, 24, Ink, FontWeights.SemiBold);

    /// <summary>A small field label. Uppercase is allowed HERE and nowhere else on the page.</summary>
    internal static TextBlock FieldLabel(string t)
        => new()
        {
            Text = t.ToUpperInvariant(),
            FontSize = 11,
            Foreground = B(Ink3),
            Margin = new Thickness(0, 0, 0, 7)
        };

    /// <summary>
    /// One of the four figures across the top of a project. Standalone, so NOT tabular figures —
    /// those are for numbers that sit in a column under each other.
    /// </summary>
    internal static UIElement Figure(string value, string caption, Color? valueColour = null)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, FigureGap, 0) };
        s.Children.Add(Text(value, 25, valueColour ?? Ink, FontWeights.Medium));
        s.Children.Add(Text(caption, 11.5, Ink3, margin: new Thickness(0, 5, 0, 0)));
        return s;
    }

    /// <summary>
    /// The progress fill — one of the accent's three permitted uses. A COUNT drives it, and the
    /// page never prints a percentage: tasks are not equal in size, so a percentage would claim
    /// they are.
    /// </summary>
    internal static UIElement ProgressBar(int done, int total)
    {
        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = B(Raised),
            Margin = new Thickness(0, 18, 0, 0)
        };

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        double fraction = total > 0 ? (double)done / total : 0d;

        var fill = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = B(Accent),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        // Width is set from the track's actual width, so the bar is a picture of the count and
        // never a number anybody reads off it.
        track.SizeChanged += (_, e) => fill.Width = System.Math.Max(0, e.NewSize.Width * fraction);
        grid.Children.Add(fill);
        track.Child = grid;
        return track;
    }

    /// <summary>A status pill. Completed is the only one allowed to wear the accent.</summary>
    internal static Border StatusTag(string status)
    {
        (string label, Color ink, Brush bg) = status switch
        {
            "done" => ("Completed", Accent, AccentWash),
            "in_progress" => ("In progress", Ink2, B(Raised)),
            "blocked" => ("Blocked", Stop, B(Raised)),
            _ => ("Planned", Ink3, B(Raised))
        };

        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(label, 11.5, ink)
        };
    }

    /// <summary>The fixed colour swatch a task carries everywhere, report included.</summary>
    internal static UIElement Swatch(int slot)
        => new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(2),
            Background = SlotBrush(slot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 13, 0)
        };

    /// <summary>The primary button — the second of the accent's three uses.</summary>
    internal static Button PrimaryButton(string text, System.Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = B(Accent),
            Foreground = B(Hex("#0B1A12")),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(15, 8, 15, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 9, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    internal static Button GhostButton(string text, System.Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = B(Raised),
            Foreground = B(Ink2),
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(15, 8, 15, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 9, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A single-select segmented control, as in the drawer and the filter chips.</summary>
    internal static UIElement Segmented(
        IReadOnlyList<(string Key, string Label)> options,
        string selected,
        System.Action<string> onPick)
    {
        var wrap = new WrapPanel();
        foreach ((string key, string label) in options)
        {
            bool on = string.Equals(key, selected, System.StringComparison.Ordinal);
            var chip = new Border
            {
                Background = B(on ? Raised : PageBg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(13, 6, 13, 6),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = Text(label, 12.5, on ? Ink : Ink3)
            };
            chip.MouseLeftButtonUp += (_, _) => onPick(key);
            wrap.Children.Add(chip);
        }

        return wrap;
    }

    /// <summary>
    /// The reason under a field. Grey when Ari worked it out, amber when only the user can answer.
    /// These are the ONLY markers on the page, and they live inside the drawer.
    /// </summary>
    internal static UIElement Hint(string text, bool asked)
        => Text(text, 11.5, asked ? Warn : Ink3, margin: new Thickness(0, 6, 0, 0), wrap: true);

    /// <summary>
    /// The amber banner. This appears on exactly one thing in the whole product: a project with no
    /// client-facing name, which cannot be reported until it has one.
    /// </summary>
    internal static UIElement NeedsYouBanner(string title, string detail, string action, System.Action onAction)
    {
        var grid = new Grid { Margin = new Thickness(26, 15, 26, 15) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(Text(title, 13, Warn, FontWeights.Medium));
        left.Children.Add(Text(detail, 12.5, Ink2, margin: new Thickness(0, 4, 0, 0), wrap: true));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var link = Text(action, 12.5, Warn, FontWeights.Medium);
        link.VerticalAlignment = VerticalAlignment.Center;
        link.Cursor = System.Windows.Input.Cursors.Hand;
        link.MouseLeftButtonUp += (_, _) => onAction();
        Grid.SetColumn(link, 1);
        grid.Children.Add(link);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xE3, 0xA8, 0x3A)),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Child = grid
        };
    }

    /// <summary>An empty state. "Nothing here" is a real answer, and it is written like one.</summary>
    internal static UIElement Empty(string line1, string line2)
    {
        var s = new StackPanel { Margin = new Thickness(26, 34, 26, 34), HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(Text(line1, 13, Ink2));
        s.Children.Add(Text(line2, 12.5, Ink3, margin: new Thickness(0, 5, 0, 0)));
        return s;
    }

    /// <summary>A quiet clickable link, used for "Edit details", "Open", "Copy link".</summary>
    internal static TextBlock Link(string text, System.Action onClick, Color? colour = null)
    {
        TextBlock t = Text(text, 12.5, colour ?? Ink2);
        t.Cursor = System.Windows.Input.Cursors.Hand;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.MouseLeftButtonUp += (_, _) => onClick();
        return t;
    }

    /// <summary>
    /// Chat 5 (2026-08-24) — a line of CONTENT that is also the way to edit itself.
    ///
    /// The result line used to render as plain text with no handler, and the only route to the
    /// editor was the amber link shown when the line was EMPTY. So the moment a line was saved the
    /// editor became unreachable: no double click, no context menu, nothing. A typo in the one
    /// sentence a client reads was permanent.
    ///
    /// It keeps the colour and wrapping of the text it replaces, because it is still the line and
    /// must not start shouting for attention. The hand cursor and the tooltip are the affordance —
    /// the same ones <see cref="Link"/> uses.
    /// </summary>
    internal static TextBlock EditableLine(string text, Color colour, System.Action onClick, string tooltip)
    {
        TextBlock t = Text(text, 12.5, colour, margin: new Thickness(0, 4, 0, 0), wrap: true);
        t.Cursor = System.Windows.Input.Cursors.Hand;
        t.ToolTip = tooltip;
        t.MouseLeftButtonUp += (_, _) => onClick();
        return t;
    }
}
