using Daqifi.Desktop.Exporter;
using Xunit;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the wording and the Warning-vs-Error split that every user-chosen destination in the app
/// now shares — the CSV export dialog and both graph-save commands.
///
/// <para>
/// Constructed exceptions rather than real file operations, on purpose. Which errno a given vector
/// produces varies by platform, by file system, and by whether the run is root — the integration
/// tests in <c>GraphImageSaveTests</c> cover that, and one of them has to stand down on a root CI
/// runner. This class asks the narrower question those cannot answer portably: given an exception,
/// is it the destination's fault, and what is the user told?
/// </para>
/// </summary>
public sealed class DestinationFailureClassifierTests
{
    /// <summary>
    /// The failures that are the destination's fault, and are therefore reported to the user
    /// as something they can act on and logged at Warning rather than raising a Sentry issue.
    /// </summary>
    [Fact]
    public void Destination_faults_are_recognised_as_blocked()
    {
        Assert.True(DestinationFailureClassifier.IsBlocked(new UnauthorizedAccessException()));
        Assert.True(DestinationFailureClassifier.IsBlocked(new DirectoryNotFoundException()));
        // A path too long for the file system is the user's data, not an app fault (issue #683).
        Assert.True(DestinationFailureClassifier.IsBlocked(new PathTooLongException()));
    }

    /// <summary>
    /// A full disk is recognised by the operating system's own code, which .NET puts in the
    /// HResult: the raw errno on macOS and Linux, a FACILITY_WIN32 HResult on Windows. And it is
    /// NOT a destination fault: it keeps its Error treatment, which issue #683 left as it was.
    /// </summary>
    [Fact]
    public void A_full_disk_is_recognised_but_is_not_a_destination_fault()
    {
        var diskFull = DiskFull();

        Assert.True(DestinationFailureClassifier.IsDiskFull(diskFull));
        Assert.False(DestinationFailureClassifier.IsBlocked(diskFull));
    }

    /// <summary>
    /// The negative half, without which the positive one proves nothing: an I/O error with any
    /// other code — the default one, or another errno — is not a full disk, and neither is an
    /// exception of another type that happens to carry the same number.
    /// </summary>
    [Fact]
    public void Other_failures_are_not_a_full_disk()
    {
        Assert.False(DestinationFailureClassifier.IsDiskFull(new IOException("There is not enough space on the disk.")));
        Assert.False(DestinationFailureClassifier.IsDiskFull(new IOException("Input/output error", OperatingSystem.IsWindows() ? unchecked((int)0x8007001F) : 5)));
        Assert.False(DestinationFailureClassifier.IsDiskFull(new InvalidOperationException("No space left on device") { HResult = DiskFull().HResult }));
    }

    [Fact]
    public void A_full_disk_tells_the_user_the_disk_is_full()
    {
        var message = DestinationFailureClassifier.Describe(DiskFull(), "/some/folder/Big B.csv", "the export file");

        Assert.Equal(
            "Could not write 'Big B.csv' — the disk is full. Make space on the disk, or choose a different location.",
            message);
    }

    /// <summary>
    /// The system's own message for a too-long path names the path, which for the export is a
    /// staging file the user never sees. The sentence names the file and what to do instead.
    /// </summary>
    [Fact]
    public void A_path_too_long_tells_the_user_to_choose_a_shorter_one()
    {
        var message = DestinationFailureClassifier.Describe(
            new PathTooLongException("The path '/x/daqifi-exporting-abc.def' is too long"), "/some/folder/run.csv", "the export file");

        Assert.Equal(
            "Could not write 'run.csv' — its name or its folder path is too long. Choose a folder with a shorter path.",
            message);
    }

    /// <summary>What the runtime raises for a write to a full disk on this host.</summary>
    private static IOException DiskFull() =>
        new("No space left on device", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);

    /// <summary>
    /// The narrowness is the point: a bare <see cref="IOException"/> covers a full disk, a failing
    /// drive and an EF/SQLite read error, so it must keep the Error/Sentry path. Treating it as a
    /// destination fault would bury real defects under "choose a different folder".
    /// </summary>
    [Fact]
    public void Generic_failures_are_not_treated_as_the_destinations_fault()
    {
        // On a non-Windows host this is also the sharing-violation branch's answer, since that
        // check is gated to Windows; on Windows it is an HResult with no FACILITY_WIN32 facility.
        Assert.False(DestinationFailureClassifier.IsBlocked(new IOException("There is not enough space on the disk.")));
        Assert.False(DestinationFailureClassifier.IsBlocked(new InvalidOperationException()));
        Assert.False(DestinationFailureClassifier.IsBlocked(new TimeoutException()));
    }

    /// <summary>
    /// Each blocked failure names the file and says what to do about it. These sentences are what
    /// the user actually reads, on both the export dialog and the graph-save dialog.
    /// </summary>
    [Theory]
    [InlineData("access was denied")]
    [InlineData("Choose a different folder")]
    public void Denied_access_tells_the_user_to_pick_elsewhere(string expected)
    {
        var message = DestinationFailureClassifier.Describe(
            new UnauthorizedAccessException(), "/some/folder/graph.png", "the graph image");

        Assert.Contains("'graph.png'", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_folder_tells_the_user_the_location_is_gone()
    {
        var message = DestinationFailureClassifier.Describe(
            new DirectoryNotFoundException(), "/gone/graph.png", "the graph image");

        Assert.Contains("no longer exists", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unclassified failure still reaches the user, carrying whatever the system said, rather
    /// than being swallowed into a shrug.
    /// </summary>
    [Fact]
    public void Unclassified_failures_pass_the_systems_own_words_through()
    {
        var message = DestinationFailureClassifier.Describe(
            new IOException("There is not enough space on the disk."), "/some/folder/graph.png", "the graph image");

        Assert.Contains("Could not write 'graph.png'", message, StringComparison.Ordinal);
        Assert.Contains("There is not enough space on the disk.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no path to name, the message uses the caller's own word for what it was writing — so a
    /// graph-save failure never calls the file "the export file", which is what a single shared
    /// fallback would have done once this classifier gained a second caller.
    /// </summary>
    [Theory]
    [InlineData("the graph image")]
    [InlineData("the export file")]
    public void A_blank_path_falls_back_to_the_callers_own_name_for_the_file(string fallback)
    {
        var message = DestinationFailureClassifier.Describe(new UnauthorizedAccessException(), "", fallback);

        Assert.Contains($"Could not write {fallback}", message, StringComparison.Ordinal);
    }
}
