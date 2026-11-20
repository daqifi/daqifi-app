using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// #509: on an install for all users, the updater closes every copy of DAQiFi in every session once
/// its administrator prompt is approved. A copy that another user starts while the prompt waits must
/// stop before it starts work. These tests run two "users" against one notes folder in a temporary
/// directory. What they cannot show is Windows itself: the prompt, the updater closing processes, and
/// the <c>%ProgramData%</c> permissions that let one user read another user's note.
/// </summary>
public sealed class PendingUpdateMarkerTests : IDisposable
{
    private const string OLD = "3.3.0";
    private const string NEW = "3.4.0";

    private readonly string _notes = Path.Combine(Path.GetTempPath(), $"daqifi-pending-update-{Guid.NewGuid():N}");
    private readonly string _install = Path.Combine(Path.GetTempPath(), "daqifi-install-509", "DAQiFi");
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_notes, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private PendingUpdateMarker For(string user, string? installRoot = null) =>
        new(_notes, installRoot ?? _install, user, () => _now);

    /// <summary>User A installs for all users: the note is in place before the updater is started.</summary>
    private void InstallAsAlice(string version = NEW) =>
        For(@"PC\alice").Apply(version, needsElevation: true, () => false, () => { });

    private string? StartAsBob(string version = OLD, bool restartedByUpdater = false) =>
        For(@"PC\bob").VersionBeingInstalledByAnotherUser(version, restartedByUpdater);

    [Fact]
    public void AllUsersInstall_StopsACopyAnotherUserStarts()
    {
        InstallAsAlice();

        Assert.Equal(NEW, StartAsBob());
    }

    /// <summary>
    /// The note must exist when the updater starts, because the updater's prompt comes next and a
    /// copy can start at any moment after that.
    /// </summary>
    [Fact]
    public void Note_IsWrittenBeforeTheUpdaterStarts()
    {
        string? seenByBobAtHandOver = null;

        For(@"PC\alice").Apply(NEW, needsElevation: true, () => false, () => seenByBobAtHandOver = StartAsBob());

        Assert.Equal(NEW, seenByBobAtHandOver);
    }

    /// <summary>An install for one user has no prompt and closes only that user's copies: no note.</summary>
    [Fact]
    public void SingleUserInstall_LeavesNoNote_AndDoesNotLookAgainForCopies()
    {
        var applied = 0;
        var looked = 0;

        For(@"PC\alice").Apply(NEW, needsElevation: false, () => { looked++; return true; }, () => applied++);

        Assert.Equal(1, applied);
        Assert.Equal(0, looked);
        Assert.Null(StartAsBob());
    }

    /// <summary>
    /// A copy that started after the notifier's last check but before the note was written cannot see
    /// the note. The look after writing it finds that copy, and nothing is installed.
    /// </summary>
    [Fact]
    public void CopyThatStartedBeforeTheNote_StopsTheInstall_AndRemovesTheNote()
    {
        var applied = 0;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            For(@"PC\alice").Apply(NEW, needsElevation: true, () => true, () => applied++));

        Assert.Contains("Another copy of DAQiFi", ex.Message);
        Assert.Equal(0, applied);
        Assert.Null(StartAsBob());
    }

    [Fact]
    public void UpdaterThatDoesNotStart_RemovesTheNote_AndTheErrorReachesTheCaller()
    {
        var failure = new IOException("Update.exe missing");

        var thrown = Assert.Throws<IOException>(() =>
            For(@"PC\alice").Apply(NEW, needsElevation: true, () => false, () => throw failure));

        Assert.Same(failure, thrown);
        Assert.Null(StartAsBob());
    }

    /// <summary>A failed look for other copies counts as one: closing someone's work is worse than not installing.</summary>
    [Fact]
    public void LookForCopiesThatFails_StopsTheInstall_AndRemovesTheNote()
    {
        var applied = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            For(@"PC\alice").Apply(NEW, needsElevation: true, () => throw new UnauthorizedAccessException(), () => applied++));

        Assert.Equal(0, applied);
        Assert.Null(StartAsBob());
    }

    /// <summary>
    /// The user who asked for the update is not stopped by their own note, and starting a copy does
    /// not end the note for everyone else.
    /// </summary>
    [Fact]
    public void WritersOwnStart_IsNotStopped_AndKeepsTheNoteForOthers()
    {
        InstallAsAlice();

        Assert.Null(For(@"PC\alice").VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: false));
        Assert.Equal(NEW, StartAsBob());
    }

    /// <summary>
    /// The updater restarts the writer's copy when it finishes, also when the prompt was refused and
    /// the old version comes back. That ends the note, so other users can start again at once.
    /// </summary>
    [Fact]
    public void UpdaterRestartOfTheWriter_EndsTheNote()
    {
        InstallAsAlice();

        Assert.Null(For(@"PC\alice").VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: true));
        Assert.Null(StartAsBob());
    }

    /// <summary>A restart ends only its own user's note. Another user's update can still close this copy.</summary>
    [Fact]
    public void UpdaterRestart_DoesNotEndAnotherUsersNote()
    {
        For(@"PC\carol").Apply("3.5.0", needsElevation: true, () => false, () => { });

        Assert.Equal("3.5.0", For(@"PC\alice").VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: true));
    }

    /// <summary>Once the new version runs, the updater has nothing left to close.</summary>
    [Fact]
    public void CopyOfTheNewVersion_IsNotStopped()
    {
        InstallAsAlice();

        Assert.Null(StartAsBob(NEW));
    }

    [Fact]
    public void Note_StopsCountingAfterItsLifetime()
    {
        InstallAsAlice();

        _now += PendingUpdateMarker.Lifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(NEW, StartAsBob());

        _now += TimeSpan.FromSeconds(1);
        Assert.Null(StartAsBob());
    }

    /// <summary>A clock set back after the note was written cannot make the note count for longer than its lifetime.</summary>
    [Fact]
    public void Note_FromTheFuture_StopsCountingAfterItsLifetime()
    {
        InstallAsAlice();

        _now -= PendingUpdateMarker.Lifetime;
        Assert.Null(StartAsBob());
    }

    [Fact]
    public void NoteForAnotherInstall_StopsNothing()
    {
        InstallAsAlice();

        var other = Path.Combine(Path.GetTempPath(), "daqifi-install-509", "DAQiFi-beta");
        Assert.Null(For(@"PC\bob", other).VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: false));
    }

    /// <summary>Windows paths ignore case, and a folder can come with or without a final separator.</summary>
    [Fact]
    public void SameInstall_WrittenDifferently_IsTheSameInstall()
    {
        InstallAsAlice();

        var sameFolder = _install.ToLowerInvariant() + Path.DirectorySeparatorChar;
        Assert.Equal(NEW, For(@"PC\bob", sameFolder).VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: false));
    }

    /// <summary>User names ignore case on Windows.</summary>
    [Fact]
    public void SameUser_WrittenDifferently_IsTheSameUser()
    {
        InstallAsAlice();

        Assert.Null(For(@"pc\ALICE").VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: false));
    }

    /// <summary>
    /// Each user has a note of their own: on Windows a user cannot replace another user's file, and
    /// one user's note must not end another's.
    /// </summary>
    [Fact]
    public void TwoUsersNotes_AreSeparate()
    {
        InstallAsAlice();
        For(@"PC\bob").Apply("3.5.0", needsElevation: true, () => false, () => { });

        // Bob's note did not replace Alice's, and his own does not stop him.
        Assert.Equal(NEW, StartAsBob());

        // Alice's restart ends her note only. Bob's still counts.
        Assert.Equal("3.5.0", For(@"PC\alice").VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: true));
        Assert.Null(StartAsBob());
    }

    [Fact]
    public void NoNotesFolder_StopsNothing() => Assert.Null(StartAsBob());

    /// <summary>A note that cannot be read never stops a start, since nothing could ever end it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("3.4.0")]
    [InlineData("\n2026-11-20T12:00:00.0000000+00:00")]
    [InlineData("3.4.0\nnot a time")]
    public void UnreadableNote_StopsNothing(string content)
    {
        InstallAsAlice();
        var note = Assert.Single(Directory.GetFiles(_notes));
        File.WriteAllText(note, content);

        Assert.Null(StartAsBob());
    }

    /// <summary>Best effort: a folder that cannot be written loses the note, never the install.</summary>
    [Fact]
    public void NotesFolderThatCannotBeWritten_StillInstalls()
    {
        var applied = 0;
        var marker = new PendingUpdateMarker(Path.Combine(Path.GetTempPath(), "\0bad"), _install, @"PC\alice", () => _now);

        marker.Apply(NEW, needsElevation: true, () => false, () => applied++);

        Assert.Equal(1, applied);
        Assert.Null(marker.VersionBeingInstalledByAnotherUser(OLD, restartedByUpdater: false));
    }
}
