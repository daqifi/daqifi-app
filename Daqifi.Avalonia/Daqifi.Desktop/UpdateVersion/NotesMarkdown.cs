// Downstream-only: upstream has no self-update and no release notes in the app (#347).

using System.Text;
using System.Text.RegularExpressions;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>How a piece of release-note text is drawn.</summary>
[Flags]
public enum NoteStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>A piece of text in one style. A span with a <see cref="Link"/> is a link, always to an http or https address.</summary>
public sealed record NoteSpan(string Text, NoteStyle Style = NoteStyle.None, string? Link = null);

/// <summary>A block of release notes: a heading, a paragraph, a list item or code.</summary>
public abstract record NoteBlock(IReadOnlyList<NoteSpan> Spans);

/// <summary>A heading, <c># text</c> to <c>###### text</c>.</summary>
public sealed record NoteHeading(int Level, IReadOnlyList<NoteSpan> Spans) : NoteBlock(Spans);

/// <summary>A paragraph: the lines up to the next blank line or block.</summary>
public sealed record NoteParagraph(IReadOnlyList<NoteSpan> Spans) : NoteBlock(Spans);

/// <summary>One item of a list. <see cref="Marker"/> is "•" or the item's number, as in "2.".</summary>
public sealed record NoteListItem(string Marker, int Depth, IReadOnlyList<NoteSpan> Spans) : NoteBlock(Spans)
{
    /// <summary>The space before the marker, for a nested item.</summary>
    public double Indent => Depth * 14;
}

/// <summary>A fenced code block, shown as written.</summary>
public sealed record NoteCode(string Text) : NoteBlock([new NoteSpan(Text, NoteStyle.Code)]);

/// <summary>
/// Turns the Markdown of a GitHub release body into <see cref="NoteBlock"/>s (#347). Hand-written
/// for a small subset: headings, bold and italic, lists, code, links and paragraphs.
/// </summary>
/// <remarks>
/// The body is remote content, so this is also the security boundary. What it returns is only text
/// with a style, and links whose address is http or https (<see cref="IsWebLink"/>). Nothing is
/// interpreted that is not in the subset: raw HTML, a link to another scheme, an image or a table
/// stays as the characters it is written with, and the view shows those characters as text.
/// There is no HTML path to leave open, because there is no HTML handling at all.
/// </remarks>
public static partial class NotesMarkdown
{
    private const int MAX_DEPTH = 3;

    /// <summary>The blocks of <paramref name="markdown"/>.</summary>
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) { return blocks; }

        var paragraph = new List<string>();
        List<string>? code = null; // the lines of an open code fence
        string? fence = null;
        (string Marker, int Depth, List<string> Lines)? item = null; // the list item that later lines continue

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new NoteParagraph(Inlines(string.Join(' ', paragraph))));
                paragraph.Clear();
            }

            if (item is { } open)
            {
                blocks.Add(new NoteListItem(open.Marker, open.Depth, Inlines(string.Join(' ', open.Lines))));
                item = null;
            }
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            var text = line.TrimStart();
            var indent = line.Length - text.Length;

            if (code is not null)
            {
                if (text.StartsWith(fence!, StringComparison.Ordinal) && text.Trim().Trim(fence![0]).Length == 0)
                {
                    blocks.Add(new NoteCode(string.Join('\n', code)));
                    code = null;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (text.Length == 0)
            {
                Flush();
                continue;
            }

            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                fence = text[..3];
                code = [];
                continue;
            }

            if (HeadingLine().Match(text) is { Success: true } heading)
            {
                Flush();
                blocks.Add(new NoteHeading(heading.Groups[1].Length, Inlines(heading.Groups[2].Value)));
                continue;
            }

            if (ListLine().Match(text) is { Success: true } listed)
            {
                Flush();
                var number = listed.Groups[1].Value;
                var marker = number.Length > 0 ? $"{number}." : "•";
                item = (marker, Math.Min(indent / 2, MAX_DEPTH), [listed.Groups[3].Value]);
                continue;
            }

            // A line straight after a list item, with no blank line between, continues the item.
            if (item is { } continued)
            {
                continued.Lines.Add(text);
                continue;
            }

            paragraph.Add(text.TrimEnd());
        }

        if (code is not null) { blocks.Add(new NoteCode(string.Join('\n', code))); }
        Flush();
        return blocks;
    }

    /// <summary>True when <paramref name="address"/> is an absolute http or https address.</summary>
    public static bool IsWebLink(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>The spans of one block's text.</summary>
    private static List<NoteSpan> Inlines(string text)
    {
        var spans = new List<NoteSpan>();
        AddInlines(text, NoteStyle.None, spans);
        return spans;
    }

    private static void AddInlines(string text, NoteStyle style, List<NoteSpan> spans)
    {
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) { return; }
            spans.Add(new NoteSpan(plain.ToString(), style));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // A backslash before punctuation writes the punctuation as text.
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindCodeClose(text, i + run, run);
                if (close < 0)
                {
                    plain.Append('`', run);
                    i += run;
                    continue;
                }

                var content = text[(i + run)..close];
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0)
                {
                    content = content[1..^1];
                }

                FlushPlain();
                spans.Add(new NoteSpan(content, style | NoteStyle.Code));
                i = close + run;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var label, out var address, out var end))
            {
                FlushPlain();
                if (IsWebLink(address))
                {
                    var labelSpans = new List<NoteSpan>();
                    AddInlines(label, style, labelSpans);
                    spans.Add(new NoteSpan(string.Concat(labelSpans.Select(s => s.Text)), style, address));
                }
                else
                {
                    // Not a web address: the label stays, as text, and nothing links anywhere.
                    AddInlines(label, style, spans);
                }

                i = end;
                continue;
            }

            if (c is '*' or '_')
            {
                var run = RunLength(text, i, c);
                var width = Math.Min(run, 3);
                if (CanOpen(text, i, run, c) && FindEmphasisClose(text, i + width, width, c) is var close and >= 0)
                {
                    FlushPlain();
                    var emphasis = width switch
                    {
                        1 => NoteStyle.Italic,
                        2 => NoteStyle.Bold,
                        _ => NoteStyle.Bold | NoteStyle.Italic,
                    };
                    AddInlines(text[(i + width)..close], style | emphasis, spans);
                    i = close + width;
                    continue;
                }

                plain.Append(c, run);
                i += run;
                continue;
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
    }

    private static bool IsAsciiPunctuation(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static int RunLength(string text, int start, char c)
    {
        var end = start;
        while (end < text.Length && text[end] == c) { end++; }
        return end - start;
    }

    /// <summary>Where a run of exactly <paramref name="run"/> backticks closes the code that starts at <paramref name="from"/>, or -1.</summary>
    private static int FindCodeClose(string text, int from, int run)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] != '`') { continue; }
            var length = RunLength(text, j, '`');
            if (length == run) { return j; }
            j += length - 1;
        }

        return -1;
    }

    /// <summary>
    /// True when the run of <paramref name="c"/> at <paramref name="at"/> can open emphasis: text
    /// follows it, and an underscore does not stand inside a word (snake_case stays as written).
    /// </summary>
    private static bool CanOpen(string text, int at, int run, char c)
    {
        var next = at + run;
        if (next >= text.Length || char.IsWhiteSpace(text[next])) { return false; }
        return c != '_' || at == 0 || !char.IsLetterOrDigit(text[at - 1]);
    }

    /// <summary>Where a run of exactly <paramref name="width"/> marks closes the emphasis that starts at <paramref name="from"/>, or -1.</summary>
    private static int FindEmphasisClose(string text, int from, int width, char c)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] == '`')
            {
                // Code inside emphasis: marks in it do not close anything.
                var run = RunLength(text, j, '`');
                var codeClose = FindCodeClose(text, j + run, run);
                j = codeClose < 0 ? j + run - 1 : codeClose + run - 1;
                continue;
            }

            if (text[j] != c) { continue; }
            var length = RunLength(text, j, c);
            var end = j + length;
            var closes = length == width
                         && j > from
                         && !char.IsWhiteSpace(text[j - 1])
                         && (c != '_' || end >= text.Length || !char.IsLetterOrDigit(text[end]));
            if (closes) { return j; }
            j = end - 1;
        }

        return -1;
    }

    /// <summary>Reads <c>[label](address)</c> at <paramref name="start"/>. An optional title after the address is dropped.</summary>
    private static bool TryLink(string text, int start, out string label, out string address, out int end)
    {
        label = address = string.Empty;
        end = start;

        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') { return false; }

        // The address may hold parentheses of its own, as long as they pair.
        var depth = 0;
        for (var j = close + 2; j < text.Length; j++)
        {
            if (text[j] == '(') { depth++; }
            else if (text[j] == ')' && depth-- == 0)
            {
                label = text[(start + 1)..close];
                var target = text[(close + 2)..j].Trim();
                var space = target.IndexOfAny([' ', '\t']);
                address = space < 0 ? target : target[..space];
                if (address.StartsWith('<') && address.EndsWith('>')) { address = address[1..^1]; }
                end = j + 1;
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex HeadingLine();

    // "- item", "* item", "+ item", "1. item" or "1) item". "**bold**" and "---" are no items: a space must follow the mark.
    [GeneratedRegex(@"^(?:[-*+]|(\d{1,9})[.)])([ \t]+)(.*)$")]
    private static partial Regex ListLine();
}
