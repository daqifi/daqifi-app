using Daqifi.Desktop.Common;
using Xunit;

namespace Daqifi.Avalonia.Tests.Configuration;

/// <summary>
/// The one-time notice about a database left in the old machine-wide folder (#353). Each test
/// uses throwaway folders standing in for <c>%ProgramData%\DAQiFi</c> and the per-user folder.
/// </summary>
public sealed class PreviousDataFolderNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daqifi-notice-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous;
    private readonly string _current;

    public PreviousDataFolderNoticeTests()
    {
        _previous = Path.Combine(_root, "machine-wide", "DAQiFi");
        _current = Path.Combine(_root, "per-user", "DAQiFi");
        Directory.CreateDirectory(_previous);
        Directory.CreateDirectory(_current);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string PreviousDatabase => Path.Combine(_previous, PreviousDataFolderNotice.DatabaseFileName);

    private void CreatePreviousDatabase(string content = "sqlite-bytes") => File.WriteAllText(PreviousDatabase, content);

    [Fact]
    public void Names_both_folders_when_the_old_folder_holds_a_database()
    {
        CreatePreviousDatabase();

        var message = new PreviousDataFolderNotice(_previous, _current).TakeNotice();

        Assert.NotNull(message);
        Assert.Contains(_previous, message);
        Assert.Contains(_current, message);
        Assert.Contains(PreviousDataFolderNotice.DatabaseFileName, message);
    }

    [Fact]
    public void Is_shown_only_once()
    {
        CreatePreviousDatabase();

        Assert.NotNull(new PreviousDataFolderNotice(_previous, _current).TakeNotice());
        Assert.Null(new PreviousDataFolderNotice(_previous, _current).TakeNotice());
        Assert.True(File.Exists(Path.Combine(_current, PreviousDataFolderNotice.MarkerFileName)));
    }

    [Fact]
    public void Says_nothing_when_the_old_folder_has_no_database()
    {
        File.WriteAllText(Path.Combine(_previous, "DAQifiConfiguration.xml"), "<x/>");

        Assert.Null(new PreviousDataFolderNotice(_previous, _current).TakeNotice());
        Assert.False(File.Exists(Path.Combine(_current, PreviousDataFolderNotice.MarkerFileName)),
            "No notice was shown, so none may be recorded: a database that appears later must still be reported.");
    }

    [Fact]
    public void Says_nothing_when_the_old_folder_does_not_exist()
    {
        Directory.Delete(_previous, recursive: true);

        Assert.Null(new PreviousDataFolderNotice(_previous, _current).TakeNotice());
    }

    [Fact]
    public void Says_nothing_with_no_old_folder_to_check()
    {
        Assert.Null(new PreviousDataFolderNotice(null, _current).TakeNotice());
    }

    [Fact]
    public void Says_nothing_when_the_old_and_current_folders_are_the_same()
    {
        CreatePreviousDatabase();

        Assert.Null(new PreviousDataFolderNotice(_previous, _previous + Path.DirectorySeparatorChar).TakeNotice());
    }

    [Fact]
    public void Leaves_the_old_database_untouched()
    {
        CreatePreviousDatabase("original");
        var before = File.GetLastWriteTimeUtc(PreviousDatabase);
        var filesBefore = Directory.GetFiles(_previous);

        _ = new PreviousDataFolderNotice(_previous, _current).TakeNotice();

        Assert.Equal("original", File.ReadAllText(PreviousDatabase));
        Assert.Equal(before, File.GetLastWriteTimeUtc(PreviousDatabase));
        Assert.Equal(filesBefore, Directory.GetFiles(_previous));
        Assert.False(File.Exists(Path.Combine(_current, PreviousDataFolderNotice.DatabaseFileName)),
            "The notice must not copy the shared database into the per-user folder.");
    }

    [Fact]
    public void Still_returns_the_notice_when_the_marker_cannot_be_written()
    {
        CreatePreviousDatabase();
        // A FILE where the current data folder should be: creating the directory, and so the
        // marker, fails.
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, string.Empty);

        var message = new PreviousDataFolderNotice(_previous, blocked).TakeNotice();

        Assert.NotNull(message);
    }

    [Fact]
    public void Never_throws_on_an_unusable_path()
    {
        Assert.Null(new PreviousDataFolderNotice("\0bad", _current).TakeNotice());
    }

    [Fact]
    public void Is_inert_for_this_test_run_because_the_data_folder_is_overridden()
    {
        // The test assembly sets DAQIFI_DATA_DIR (TestDataDirectory), which is exactly the
        // isolated-run case ForThisPc must ignore, so the real machine's data is never reported.
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAQIFI_DATA_DIR")));

        Assert.Null(PreviousDataFolderNotice.ForThisPc().TakeNotice());
    }
}
