// Downstream-only: upstream has no self-update and no release notes in the app (#347).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Daqifi.Desktop.UpdateVersion;
using Daqifi.Desktop.View.Flyouts;

namespace Daqifi.Desktop.View;

/// <summary>
/// Fills a <see cref="TextBlock"/> with the spans of one block of release notes (#347):
/// <c>local:ReleaseNotesText.Spans="{Binding Spans}"</c>. The UPDATES section's templates in
/// <c>MainWindow.axaml</c> lay out the blocks; this builds the text inside each.
/// </summary>
/// <remarks>
/// What it builds is the security boundary's last step. A span becomes a <see cref="Run"/>, whose
/// text is drawn as the characters it holds, so raw HTML in a release body is shown, not run. A
/// link becomes a button that carries its address in <c>Tag</c>, the way the notifications flyout's
/// links do, and its <c>Click</c> is the flyout's own handler, which opens http and https addresses
/// in the system browser. Nothing here opens an address in any other way, or without a click.
/// </remarks>
public static class ReleaseNotesText
{
    public static readonly AttachedProperty<IReadOnlyList<NoteSpan>?> SpansProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<NoteSpan>?>("Spans", typeof(ReleaseNotesText));

    static ReleaseNotesText() =>
        SpansProperty.Changed.AddClassHandler<TextBlock>((block, e) => Fill(block, e.NewValue as IReadOnlyList<NoteSpan>));

    public static IReadOnlyList<NoteSpan>? GetSpans(TextBlock block) => block.GetValue(SpansProperty);

    public static void SetSpans(TextBlock block, IReadOnlyList<NoteSpan>? spans) => block.SetValue(SpansProperty, spans);

    private static void Fill(TextBlock block, IReadOnlyList<NoteSpan>? spans)
    {
        var inlines = new InlineCollection();
        foreach (var span in spans ?? [])
        {
            // TextBottom: the button's text then sits on the line's baseline, not above it.
            inlines.Add(span.Link is { } link
                ? new InlineUIContainer(LinkButton(span.Text, link)) { BaselineAlignment = BaselineAlignment.TextBottom }
                : Styled(new Run(span.Text), span.Style));
        }

        block.Inlines = inlines;
    }

    private static Run Styled(Run run, NoteStyle style)
    {
        if (style.HasFlag(NoteStyle.Bold)) { run.FontWeight = FontWeight.SemiBold; }
        if (style.HasFlag(NoteStyle.Italic)) { run.FontStyle = FontStyle.Italic; }
        if (style.HasFlag(NoteStyle.Code)) { run.Classes.Add("noteCode"); }
        return run;
    }

    /// <summary>
    /// A link: the flyout's link button, with its label in a <see cref="TextBlock"/> so that an
    /// underscore in it is text, not an access key that Alt could press without a click.
    /// </summary>
    private static Button LinkButton(string text, string link)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            Tag = link,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("noteLink");
        ToolTip.SetTip(button, link);
        button.Click += NotificationsFlyout.Hyperlink_RequestNavigate;
        return button;
    }
}
