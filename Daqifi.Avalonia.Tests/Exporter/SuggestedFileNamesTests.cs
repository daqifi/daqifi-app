using System.Text;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the names that the desktop save pickers suggest (#632). Before, Save as image and the export
/// dialog's Browse opened the macOS panel with an empty name field and a disabled Save button.
/// </summary>
public sealed class SuggestedFileNamesTests
{
    private static readonly DateTime SaveTime = new(2026, 11, 14, 20, 6, 0, DateTimeKind.Local);

    [Fact]
    public void The_live_graph_name_is_the_brand_and_the_local_time()
    {
        Assert.Equal("DAQiFi-graph-2026-11-14_20-06-00.png", SuggestedFileNames.GraphImage(null, SaveTime));
    }

    [Fact]
    public void A_logged_session_graph_name_starts_with_the_session_name()
    {
        Assert.Equal("Session_12-graph-2026-11-14_20-06-00.png", SuggestedFileNames.GraphImage("Session_12", SaveTime));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" . ")]
    public void A_blank_session_name_falls_back_to_the_brand(string sessionName)
    {
        Assert.Equal("DAQiFi-graph-2026-11-14_20-06-00.png", SuggestedFileNames.GraphImage(sessionName, SaveTime));
    }

    [Fact]
    public void Characters_that_a_file_name_cannot_contain_are_replaced()
    {
        var name = SuggestedFileNames.GraphImage("bench/run\0two", SaveTime);

        Assert.Equal("bench_run_two-graph-2026-11-14_20-06-00.png", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void The_time_has_no_colon_in_any_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // fa-IR has its own digits and calendar; the name must not follow it.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            Assert.Equal("DAQiFi-graph-2026-11-14_20-06-00.png", SuggestedFileNames.GraphImage(null, SaveTime));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_very_long_session_name_is_cut_so_the_whole_name_fits_255_bytes()
    {
        var name = SuggestedFileNames.GraphImage(new string('x', 1000), SaveTime);

        Assert.True(Encoding.UTF8.GetByteCount(name) <= 255, $"{Encoding.UTF8.GetByteCount(name)} bytes");
        Assert.EndsWith("-graph-2026-11-14_20-06-00.png", name, StringComparison.Ordinal);
        Assert.StartsWith(new string('x', SuggestedFileNames.MaxSessionPartBytes) + "-", name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cut_never_splits_a_character()
    {
        // Each emoji is a surrogate pair (4 UTF-8 bytes). A cut by UTF-16 unit would leave half of one.
        var name = SuggestedFileNames.GraphImage(string.Concat(Enumerable.Repeat("\U0001F4C8", 100)), SaveTime);
        var sessionPart = name[..name.IndexOf("-graph-", StringComparison.Ordinal)];

        Assert.True(Encoding.UTF8.GetByteCount(sessionPart) <= SuggestedFileNames.MaxSessionPartBytes);
        Assert.Equal(0, sessionPart.Length % 2);
        Assert.All(Enumerable.Range(0, sessionPart.Length / 2),
            i => Assert.True(char.IsSurrogatePair(sessionPart[2 * i], sessionPart[2 * i + 1])));
    }

    [Theory]
    [InlineData("Session_3", 3, "Session_3.csv")]
    [InlineData("run.csv", 3, "run.csv")]
    [InlineData("run.CSV", 3, "run.CSV")]
    [InlineData("run.csv.bak", 3, "run.csv.bak.csv")]
    [InlineData(null, 7, "Session_7.csv")]
    [InlineData("  ", 7, "Session_7.csv")]
    [InlineData("SD Import - log_20261104_104640", 9, "SD Import - log_20261104_104640.csv")]
    public void A_session_csv_name_has_exactly_one_csv_extension(string? sessionName, int sessionId, string expected)
    {
        Assert.Equal(expected, SuggestedFileNames.SessionCsv(sessionName, sessionId));
    }

    [Fact]
    public void A_session_csv_name_is_the_name_the_mobile_storage_tab_suggests()
    {
        // LoggedSessionsMobileViewModel suggests "{ExportFileNamer.SafeName(name, id)}.csv".
        const string session = "Bench run: 3/4";
        Assert.Equal($"{ExportFileNamer.SafeName(session, 4)}.csv", SuggestedFileNames.SessionCsv(session, 4));
    }

    [Fact]
    public void The_graph_picker_suggests_the_name_and_says_what_it_saves()
    {
        var options = GraphImageSaver.PickerOptions("DAQiFi-graph-2026-11-14_20-06-00.png");

        Assert.Equal("DAQiFi-graph-2026-11-14_20-06-00.png", options.SuggestedFileName);
        Assert.Equal("Save graph as image", options.Title);
        Assert.Equal("png", options.DefaultExtension);
    }

    [Fact]
    public void The_export_browse_picker_suggests_the_session_csv_name()
    {
        var contexts = TestDatabase.Contexts(Path.Combine(Path.GetTempPath(), $"daqifi-suggested-{Guid.NewGuid():N}.db"));
        var viewModel = new ExportDialogViewModel(contexts, 12, id => id == 12 ? "Pump test" : null);

        var options = viewModel.ExportFilePickerOptions();

        Assert.Equal("Pump test.csv", options.SuggestedFileName);
        Assert.Equal("Export logged data", options.Title);
        Assert.Equal("csv", options.DefaultExtension);
    }
}
