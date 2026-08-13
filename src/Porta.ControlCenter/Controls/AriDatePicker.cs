using System.Globalization;
using System.Windows.Controls;

namespace Porta.ControlCenter.Controls;

/// <summary>
/// A <see cref="DatePicker"/> that always writes its date as <c>12 Aug 2026</c>.
///
/// <b>Why this exists at all.</b> WPF's DatePicker offers exactly two formats, Short and Long, and
/// both come from the reader's culture. Short on this machine is <c>8/12/2026</c>, which means two
/// different days depending on where the person reading it lives. This control sits on a document
/// about WHICH DAYS WERE WORKED, sent to a client who may be anywhere, and the one thing that
/// document cannot be is ambiguous about dates.
///
/// The format cannot be reached through culture: DatePicker resolves its culture from
/// <c>Language.GetSpecificCulture()</c>, which builds a fresh CultureInfo from the language name,
/// so a modified <c>ShortDatePattern</c> on the current culture is ignored. Hence a subclass that
/// rewrites the text after the base control has set it.
///
/// Typing still works. The month name is written with the invariant culture so the text this
/// control produces is always text it can parse back.
/// </summary>
public class AriDatePicker : DatePicker
{
    private const string UnambiguousFormat = "dd MMM yyyy";

    private TextBox? _textBox;
    private bool _formatting;
    private bool _pending;
    private bool _userEditing;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_textBox is not null)
        {
            _textBox.TextChanged -= OnTextBoxChanged;
            _textBox.LostFocus -= OnTextBoxLostFocus;
            _textBox.PreviewTextInput -= OnUserEdited;
            _textBox.PreviewKeyDown -= OnUserEdited;
        }

        _textBox = GetTemplateChild("PART_TextBox") as TextBox;

        if (_textBox is not null)
        {
            // TextChanged is the one hook that catches EVERY path, and catching every path is the
            // whole problem: the base control rewrites this text on selection, on losing focus, on
            // the popup closing, and again when the drop-down reopens. Hooking only the events we
            // could think of is what shipped in 1.2.151 — one picker held the format and the other
            // did not, and both reverted to slashes after using the calendar.
            _textBox.TextChanged += OnTextBoxChanged;
            _textBox.LostFocus += OnTextBoxLostFocus;

            // Typing, specifically — not merely holding focus. Closing the drop-down hands focus
            // back to this box, and treating that as "the user is mid-word" is what left one picker
            // showing slashes after a calendar pick while the other held the format.
            _textBox.PreviewTextInput += OnUserEdited;
            _textBox.PreviewKeyDown += OnUserEdited;
        }

        ApplyFormat();
    }

    protected override void OnSelectedDateChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectedDateChanged(e);

        // The date came from somewhere authoritative — the calendar, or code. Whatever was being
        // typed is finished with.
        _userEditing = false;
        ApplyFormat();
    }

    private void OnTextBoxChanged(object sender, System.Windows.RoutedEventArgs e) => ApplyFormat();

    private void OnUserEdited(object sender, System.Windows.RoutedEventArgs e) => _userEditing = true;

    private void OnTextBoxLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        _userEditing = false;
        ApplyFormat();
    }

    /// <summary>
    /// Queue the rewrite rather than doing it inside the event that asked for it. Writing Text from
    /// a TextChanged handler re-enters the base control's parse-and-rewrite path synchronously and
    /// overflows the stack — measured, not guessed. Deferring to the dispatcher breaks the cycle,
    /// and the pending flag coalesces the burst of changes a single date pick produces.
    /// </summary>
    private void ApplyFormat()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _pending = false;
                ApplyFormatCore();
            }));
    }

    private void ApplyFormatCore()
    {
        if (_textBox is null || _formatting)
        {
            return;
        }

        // Never while the user is mid-word — rewriting under the caret moves it to the end and makes
        // the field impossible to type in. Having focus is not the same as typing into it.
        if (_userEditing && _textBox.IsKeyboardFocused)
        {
            return;
        }

        string text = SelectedDate is { } date
            ? date.ToString(UnambiguousFormat, CultureInfo.InvariantCulture)
            : string.Empty;

        if (string.Equals(_textBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        // Assigning Text re-enters the base control's parse path, which raises TextChanged again.
        _formatting = true;
        try
        {
            _textBox.Text = text;
        }
        finally
        {
            _formatting = false;
        }
    }
}
