using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// The Markdown subset of the in-app release notes (#347): headings, bold and italic, lists, code,
/// links and paragraphs. A release body is remote content, so anything outside the subset, raw
/// HTML first, stays text: it is shown as written and never interpreted.
/// </summary>
public class NotesMarkdownTests
{
    /// <summary>One block as a line of text, with each styled or linked span marked.</summary>
    private static string Describe(NoteBlock block) => block switch
    {
        NoteHeading h => $"H{h.Level} {Spans(h.Spans)}",
        NoteListItem item => $"{new string(' ', item.Depth * 2)}{item.Marker} {Spans(item.Spans)}",
        NoteCode code => $"CODE {code.Text}",
        _ => $"P {Spans(block.Spans)}",
    };

    private static string Spans(IEnumerable<NoteSpan> spans) => string.Concat(spans.Select(span =>
        span.Link is { } link ? $"[{span.Text}]->{link}"
        : span.Style == NoteStyle.None ? span.Text
        : $"<{span.Style}:{span.Text}>"));

    private static string[] Describe(string markdown) => [.. NotesMarkdown.Parse(markdown).Select(Describe)];

    [Fact]
    public void A_release_body_gives_its_headings_paragraphs_and_lists()
    {
        const string body = """
            Second beta, 311 commits after beta.1. Built from `e48d644`.

            ## What's new

            **Live graph and windows**
            - The live graph redraws at about 20 fps
              instead of once a second (#497).
            - Window title bars show the small logo.

            1. First step
            2. Second step
            """;

        Assert.Equal(
        [
            "P Second beta, 311 commits after beta.1. Built from <Code:e48d644>.",
            "H2 What's new",
            "P <Bold:Live graph and windows>",
            "• The live graph redraws at about 20 fps instead of once a second (#497).",
            "• Window title bars show the small logo.",
            "1. First step",
            "2. Second step",
        ], Describe(body));
    }

    [Fact]
    public void Lines_of_one_paragraph_join_and_a_blank_line_ends_it()
    {
        Assert.Equal(["P one two", "P three"], Describe("one\ntwo\n\nthree"));
        Assert.Equal(["P one two"], Describe("one\r\ntwo\r\n"));
    }

    [Theory]
    [InlineData("# One", "H1 One")]
    [InlineData("### Three ###", "H3 Three")]
    [InlineData("###### Six", "H6 Six")]
    [InlineData("####### Seven", "P ####### Seven")]
    [InlineData("#NoSpace", "P #NoSpace")]
    public void Headings_have_one_to_six_marks_and_a_space(string markdown, string expected) =>
        Assert.Equal([expected], Describe(markdown));

    [Fact]
    public void Nested_items_keep_their_depth()
    {
        Assert.Equal(["• outer", "  • inner", "• next"], Describe("- outer\n  - inner\n* next"));
        Assert.Equal(14, Assert.IsType<NoteListItem>(NotesMarkdown.Parse("- a\n  - b")[1]).Indent);
    }

    [Theory]
    [InlineData("some **bold** text", "P some <Bold:bold> text")]
    [InlineData("some __bold__ text", "P some <Bold:bold> text")]
    [InlineData("some *italic* text", "P some <Italic:italic> text")]
    [InlineData("some _italic_ text", "P some <Italic:italic> text")]
    [InlineData("***both***", "P <Bold, Italic:both>")]
    [InlineData("**bold with *italic* inside**", "P <Bold:bold with ><Bold, Italic:italic><Bold: inside>")]
    public void Bold_and_italic(string markdown, string expected) =>
        Assert.Equal([expected], Describe(markdown));

    [Theory]
    [InlineData("snake_case_name stays", "P snake_case_name stays")]
    [InlineData("2 * 3 * 4 = 24", "P 2 * 3 * 4 = 24")]
    [InlineData("**never closed", "P **never closed")]
    [InlineData(@"\*escaped\*", "P *escaped*")]
    [InlineData("a ` lone backtick", "P a ` lone backtick")]
    public void Marks_that_do_not_pair_stay_as_written(string markdown, string expected) =>
        Assert.Equal([expected], Describe(markdown));

    [Fact]
    public void Code_is_shown_as_written()
    {
        Assert.Equal(["P run <Code:**not bold** <b>x</b>> now"], Describe("run `**not bold** <b>x</b>` now"));
        Assert.Equal(["P <Code:a ` b>"], Describe("`` a ` b ``"));
        Assert.Equal(
            ["P before", "CODE <script>alert(1)</script>\n**kept**", "P after"],
            Describe("before\n```html\n<script>alert(1)</script>\n**kept**\n```\nafter"));
    }

    [Fact]
    public void A_code_fence_that_never_closes_runs_to_the_end() =>
        Assert.Equal(["CODE line one\nline two"], Describe("```\nline one\nline two"));

    [Theory]
    [InlineData("see [the page](https://github.com/daqifi/daqifi-app) now", "P see [the page]->https://github.com/daqifi/daqifi-app now")]
    [InlineData("[plain http](http://example.com/a?b=1)", "P [plain http]->http://example.com/a?b=1")]
    [InlineData("[titled](https://example.com \"Title\")", "P [titled]->https://example.com")]
    [InlineData("[**bold label**](https://example.com)", "P [bold label]->https://example.com")]
    public void A_link_to_a_web_address_is_a_link(string markdown, string expected) =>
        Assert.Equal([expected], Describe(markdown));

    /// <summary>
    /// The security boundary (#347): raw HTML is shown as the characters it is made of. Nothing is
    /// dropped, interpreted or made into a link, so a script, an image with an error handler or a
    /// <c>javascript:</c> anchor is inert text.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<img src=\"x\" onerror=\"alert(document.cookie)\">")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("before <b>bold?</b> after")]
    public void Raw_html_is_inert_text(string html)
    {
        var block = Assert.Single(NotesMarkdown.Parse(html));

        Assert.IsType<NoteParagraph>(block);
        Assert.Equal(html, string.Concat(block.Spans.Select(s => s.Text)));
        Assert.All(block.Spans, span => Assert.Null(span.Link));
    }

    [Fact]
    public void An_html_block_over_several_lines_is_inert_text()
    {
        var blocks = NotesMarkdown.Parse("<details>\n<summary>More</summary>\n<img src=\"x\" onerror=\"alert(1)\">\n</details>");

        var block = Assert.Single(blocks);
        Assert.Equal(
            "<details> <summary>More</summary> <img src=\"x\" onerror=\"alert(1)\"> </details>",
            string.Concat(block.Spans.Select(s => s.Text)));
        Assert.All(block.Spans, span => Assert.Null(span.Link));
    }

    /// <summary>A Markdown link to anything but a web address keeps its label as text, and is no link.</summary>
    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](file:///C:/Windows/System32/calc.exe)")]
    [InlineData("[click](data:text/html,hello)")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](/relative/page)")]
    [InlineData("[click](ms-settings:privacy)")]
    public void A_link_to_anything_but_a_web_address_is_no_link(string markdown)
    {
        var spans = Assert.Single(NotesMarkdown.Parse(markdown)).Spans;

        Assert.All(spans, span => Assert.Null(span.Link));
        Assert.StartsWith("click", string.Concat(spans.Select(s => s.Text)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\n  ")]
    public void No_text_gives_no_blocks(string? markdown) =>
        Assert.Empty(NotesMarkdown.Parse(markdown));

    [Theory]
    [InlineData("https://github.com/daqifi/daqifi-app/releases", true)]
    [InlineData("http://example.com", true)]
    [InlineData("HTTPS://EXAMPLE.COM/A", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_addresses_are_web_links(string? address, bool expected) =>
        Assert.Equal(expected, NotesMarkdown.IsWebLink(address));
}
