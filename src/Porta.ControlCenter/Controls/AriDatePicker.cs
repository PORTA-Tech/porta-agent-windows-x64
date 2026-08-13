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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_textBox is not null)
        {
            _textBox.LostFocus -= OnTextBoxLostFocus;
        }

        _textBox = GetTemplateChild("PART_TextBox") as TextBox;

        if (_textBox is not null)
        {
            // The base control rewrites the text itself when focus leaves, after parsing whatever
            // was typed. Re-apply afterwards or the field reverts to the culture's short date.
            _textBox.LostFocus += OnTextBoxLostFocus;
        }

        ApplyFormat();
    }

    protected override void OnSelectedDateChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectedDateChanged(e);
        ApplyFormat();
    }

    private void OnTextBoxLostFocus(object sender, System.Windows.RoutedEventArgs e) => ApplyFormat();

    private void ApplyFormat()
    {
        if (_textBox is null)
        {
            return;
        }

        string text = SelectedDate is { } date
            ? date.ToString(UnambiguousFormat, CultureInfo.InvariantCulture)
            : string.Empty;

        // Only when it differs: assigning Text re-enters the base control's parse path, and a
        // needless write there is a loop waiting to be found by someone else.
        if (!string.Equals(_textBox.Text, text, StringComparison.Ordinal))
        {
            _textBox.Text = text;
        }
    }
}
