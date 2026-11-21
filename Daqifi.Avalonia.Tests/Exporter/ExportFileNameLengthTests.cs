using System.Globalization;
using System.Text;
using Daqifi.Desktop.Exporter;
using Xunit;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins that every export file name fits the file system's limit of 255 bytes, with its
/// <c>" (n)"</c> and its <c>.csv</c> (issue #683).
///
/// <para>
/// The regression: <see cref="ExportFileNamer.SafeName"/> replaced the characters a file name cannot
/// hold and did not limit the length, and the session NAME box has no maximum. A session name of
/// more than 251 ASCII characters, or more than 83 Chinese characters, made a file name the file
/// system refused. The export of that session failed with a <see cref="PathTooLongException"/>, the
/// dialog said only "Export failed. Please try again.", and Export All stopped at it.
/// </para>
///
/// <para>
/// Asserted against the namer directly, and against the bytes of each result, because the
/// end-to-end form (<c>ExportFailureReportingTests</c>) passes on any file system that happens to
/// take the name — and the HFS+ case below cannot be shown end to end on CI's Linux runner at all.
/// </para>
/// </summary>
public sealed class ExportFileNameLengthTests
{
    /// <summary>The two long names of the reported case A: 300 ASCII characters, and 90 Chinese
    /// characters (270 bytes of UTF-8).</summary>
    public static TheoryData<string> LongNames => new()
    {
        new string('x', 300),
        string.Concat(Enumerable.Repeat("数据", 45)),
    };

    [Theory]
    [MemberData(nameof(LongNames))]
    public void A_long_session_name_is_cut_so_the_csv_name_fits_255_bytes(string sessionName)
    {
        var name = new ExportFileNamer().NextCsvName(sessionName, 4);

        Assert.True(Utf8(name) <= 255, $"{Utf8(name)} bytes: {name}");
        Assert.EndsWith(".csv", name, StringComparison.Ordinal);
        // A cut, not a rewrite: what is left is the start of the user's own name.
        Assert.StartsWith(name[..^".csv".Length], sessionName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cut keeps as much of the name as fits, not some safe-looking smaller amount: 251 bytes
    /// for the base, which with <c>.csv</c> is exactly 255. And a name of exactly 251 bytes is not
    /// cut at all, while 252 bytes is — the boundary, both sides of it.
    /// </summary>
    [Theory]
    [InlineData(250, 250)]
    [InlineData(251, 251)]
    [InlineData(252, 251)]
    [InlineData(300, 251)]
    public void The_cut_keeps_as_much_of_the_name_as_fits(int length, int expected)
    {
        var name = ExportFileNamer.SafeName(new string('x', length), 4);

        Assert.Equal(expected, name.Length);
    }

    /// <summary>
    /// 90 three-byte characters are 270 bytes; the most that fits in 251 is 83 of them (249 bytes),
    /// and the cut must not stop short of that by counting characters instead of bytes.
    /// </summary>
    [Fact]
    public void A_name_of_multi_byte_characters_is_cut_by_bytes()
    {
        var name = ExportFileNamer.SafeName(new string('数', 90), 4);

        Assert.Equal(new string('数', 83), name);
    }

    /// <summary>
    /// The cut is on a text element boundary. Each emoji is a surrogate pair, and each <c>é</c>
    /// below is an <c>e</c> and a combining accent; a cut by UTF-16 unit would leave half of one,
    /// or an accent with no letter under it on the next export.
    /// </summary>
    [Theory]
    [InlineData("\U0001F4C8")]
    [InlineData("é")]
    public void The_cut_never_splits_a_character(string element)
    {
        var sessionName = string.Concat(Enumerable.Repeat(element, 100));

        var name = ExportFileNamer.SafeName(sessionName, 4);

        Assert.True(Utf8(name) <= ExportFileNamer.MaxBaseNameBytes, $"{Utf8(name)} bytes");
        Assert.Equal(0, name.Length % element.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat(element, name.Length / element.Length)), name);
        Assert.Equal(name.Length / element.Length, new StringInfo(name).LengthInTextElements);
    }

    /// <summary>
    /// HFS+ stores a name decomposed and counts UTF-16 units of the result. <c>ΐ</c> (U+0390) is
    /// two bytes of UTF-8 but three code points decomposed, so 120 of them are only 240 bytes and
    /// would pass a plain UTF-8 count — and still be 360 units on HFS+, which refuses them. The limit
    /// is therefore applied to the decomposed form too.
    /// </summary>
    [Fact]
    public void A_name_that_grows_when_decomposed_is_cut_to_fit_its_decomposed_form()
    {
        var sessionName = new string('ΐ', 120);
        Assert.True(Utf8(sessionName) < ExportFileNamer.MaxBaseNameBytes, "the precondition: a plain UTF-8 count passes it");

        var name = ExportFileNamer.SafeName(sessionName, 4);
        var decomposed = name.Normalize(NormalizationForm.FormD);

        Assert.True(Utf8(decomposed) <= ExportFileNamer.MaxBaseNameBytes, $"{Utf8(decomposed)} bytes decomposed");
        Assert.True(decomposed.Length + ".csv".Length <= 255, $"{decomposed.Length} UTF-16 units decomposed");
        Assert.StartsWith(name, sessionName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two long names that differ only after the cut must still get two files: shortening comes
    /// before disambiguation, as sanitizing does. And the <c>" (2)"</c> is inside the limit too, so
    /// the base name gives up the bytes it needs.
    /// </summary>
    [Fact]
    public void Two_long_names_that_are_the_same_after_the_cut_get_different_files()
    {
        var namer = new ExportFileNamer();
        var first = namer.NextCsvName(new string('x', 299) + "A", 1);
        var second = namer.NextCsvName(new string('x', 299) + "B", 2);

        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new string('x', 251) + ".csv", first);
        Assert.Equal(new string('x', 247) + " (2).csv", second);
        Assert.True(Utf8(second) <= 255, $"{Utf8(second)} bytes");
    }

    /// <summary>
    /// Many of them, so the suffix grows past one digit and the base has to give up more again.
    /// Every name is distinct and every name fits.
    /// </summary>
    [Fact]
    public void Many_long_names_that_are_the_same_after_the_cut_all_get_distinct_names_that_fit()
    {
        var namer = new ExportFileNamer();

        var names = Enumerable.Range(1, 120)
            .Select(id => namer.NextCsvName(new string('x', 300) + id, id))
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.True(Utf8(name) <= 255, $"{Utf8(name)} bytes: {name}"));
        Assert.EndsWith(" (120).csv", names[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// One text element larger than the whole limit — a letter carrying hundreds of combining
    /// marks — leaves nothing after a cut on element boundaries. The session falls back to the name
    /// it gets when it has none, rather than to a file called <c>.csv</c>.
    /// </summary>
    [Fact]
    public void A_single_character_larger_than_the_limit_falls_back_to_the_session_number()
    {
        var sessionName = "a" + new string('́', 200);
        Assert.Equal(1, new StringInfo(sessionName).LengthInTextElements);

        Assert.Equal("Session_7", ExportFileNamer.SafeName(sessionName, 7));
        Assert.Equal("Session_7.csv", new ExportFileNamer().NextCsvName(sessionName, 7));
    }

    /// <summary>
    /// The same fallback when the " (2)" leaves no room for the one element: the name is still
    /// distinct, still fits, and is still not a bare suffix.
    /// </summary>
    [Fact]
    public void The_suffix_falls_back_to_the_session_number_when_it_leaves_no_room()
    {
        // 1 + 124 × 2 = 249 bytes: one text element that fits the base limit but not the base
        // limit less the 4 bytes of " (2)".
        var sessionName = "a" + new string('́', 124);
        var namer = new ExportFileNamer();

        Assert.Equal(sessionName + ".csv", namer.NextCsvName(sessionName, 1));
        Assert.Equal("Session_2 (2).csv", namer.NextCsvName(sessionName, 2));
    }

    /// <summary>
    /// Not well-formed UTF-16 — an unpaired surrogate, which a pasted name can carry — has no
    /// decomposed form. The count falls back to plain UTF-8 rather than faulting the export.
    /// </summary>
    [Fact]
    public void A_malformed_long_name_is_cut_without_throwing()
    {
        var sessionName = "\uD800" + new string('x', 300);

        var name = ExportFileNamer.SafeName(sessionName, 4);

        Assert.True(Utf8(name) <= ExportFileNamer.MaxBaseNameBytes, $"{Utf8(name)} bytes");
        Assert.StartsWith("\uD800", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary case, so the fix cannot be "cut everything": a name that fits keeps every
    /// character, and two that collide get the same " (2)" they always did.
    /// </summary>
    [Fact]
    public void An_ordinary_name_is_not_changed()
    {
        var namer = new ExportFileNamer();

        Assert.Equal("Morning run.csv", namer.NextCsvName("Morning run", 1));
        Assert.Equal("Morning run (2).csv", namer.NextCsvName("Morning run", 2));
        Assert.Equal("Session_3", ExportFileNamer.SafeName("   ", 3));
    }

    /// <summary>
    /// The mobile save picker suggests <see cref="ExportFileNamer.SafeName"/> and adds the
    /// <c>.csv</c> itself (#587), so that name has to leave room for it too.
    /// </summary>
    [Fact]
    public void The_mobile_suggested_name_leaves_room_for_the_extension_the_picker_adds()
    {
        var suggested = Daqifi.Avalonia.Views.Mobile.LoggedSessionsMobileViewModel.SuggestedSaveName(new string('x', 400), 4);

        Assert.True(Utf8(suggested + "." + ExportFileNamer.CsvExtension) <= 255, $"{Utf8(suggested)} bytes before the extension");
    }

    private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);
}
