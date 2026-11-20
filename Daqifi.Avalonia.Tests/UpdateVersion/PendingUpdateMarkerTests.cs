using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// #509: on an install for all users, the elevated updater closes every copy of DAQiFi in every
/// session once its administrator prompt is approved. A copy that starts while the updater can still
/// do that must stop before it starts work. These tests run several copies against one notes folder
/// in a temporary directory, with a process list the test controls. What they cannot show is Windows
/// itself: the prompt, the updater closing processes, the process list that one user sees of
/// another user's session, and the <c>%ProgramData%</c> permissions.
/// </summary>
public sealed class PendingUpdateMarkerTests : IDisposable
{
    private const string NEW = "3.4.0";
    private const int ALICE_SESSION = 1;
    private const int BOB_SESSION = 2;
    private const int ALICE_PID = 1001;
    private const int UPDATER_PID = 2001;
    private const int ELEVATED_UPDATER_PID = 2002;

    private static readonly AppCopy Alice = new(@"PC\alice", ALICE_SESSION, ALICE_PID, "DAQiFi");
    private static readonly AppCopy Bob = new(@"PC\bob", BOB_SESSION, 3001, "DAQiFi");

    private static readonly string[] NoArgs = [];

    private readonly string _notes = Path.Combine(Path.GetTempPath(), $"daqifi-pending-update-{Guid.NewGuid():N}");
    private readonly string _install = Path.Combine(Path.GetTempPath(), "daqifi-install-509", "DAQiFi");

    /// <summary>What every copy sees in the process list. Tests change it as processes start and end.</summary>
    private List<SessionProcess> _running = [];

    public void Dispose()
    {
        try { Directory.Delete(_notes, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private PendingUpdateMarker For(AppCopy copy, string? installRoot = null) => new(_notes, installRoot ?? _install, copy);

    private static SessionProcess Process(AppCopy copy) => new(copy.ProcessId, copy.ProcessName, copy.SessionId);

    private static SessionProcess Updater(int pid, int session) => new(pid, PendingUpdateMarker.UPDATER_PROCESS_NAME, session);

    /// <summary>
    /// Alice hands an all-users update over: her copy runs while it writes the note, then the updater
    /// takes over in her session, and her copy exits.
    /// </summary>
    private void AliceHandsOver()
    {
        _running = [Process(Alice)];
        PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: true, () => false, () => { });
        _running = [Updater(UPDATER_PID, ALICE_SESSION)];
    }

    private string? Start(AppCopy copy, bool restartedByUpdater = false, string[]? args = null, Action<TimeSpan>? wait = null)
    {
        _running.Add(Process(copy));
        try
        {
            return For(copy).VersionBeingInstalled(args ?? NoArgs, restartedByUpdater, () => _running.ToList(),
                wait ?? (_ => throw new InvalidOperationException("waited")));
        }
        finally
        {
            _running.RemoveAll(p => p.Id == copy.ProcessId);
        }
    }

    // ---- Handing over -------------------------------------------------------------------------

    [Fact]
    public void AllUsersUpdate_StopsACopyAnotherUserStarts()
    {
        AliceHandsOver();

        Assert.Equal(NEW, Start(Bob));
    }

    /// <summary>The note must count from before the updater starts, while Alice's copy still runs.</summary>
    [Fact]
    public void Note_CountsBeforeTheUpdaterStarts()
    {
        string? seenByBobAtHandOver = null;
        _running = [Process(Alice)];

        PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: true, () => false, () => seenByBobAtHandOver = Start(Bob));

        Assert.Equal(NEW, seenByBobAtHandOver);
    }

    /// <summary>An install for one user has no prompt and closes only that user's copies: no note.</summary>
    [Fact]
    public void SingleUserInstall_LeavesNoNote_AndDoesNotLookAgainForCopies()
    {
        var applied = 0;
        var looked = 0;

        PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: false, () => { looked++; return true; }, () => applied++);

        Assert.Equal(1, applied);
        Assert.Equal(0, looked);
        Assert.False(Directory.Exists(_notes));
    }

    /// <summary>Review thread 1: without a note there is nothing to stop another user, so nothing is installed.</summary>
    [Fact]
    public void AllUsersUpdate_WithANoteThatCannotBeWritten_IsNotHandedOver()
    {
        var applied = 0;
        var marker = new PendingUpdateMarker(Path.Combine(Path.GetTempPath(), "\0bad"), _install, Alice);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PendingUpdateMarker.HandOver(marker, NEW, needsElevation: true, () => false, () => applied++));

        Assert.Contains("could not be told", ex.Message);
        Assert.Equal(0, applied);
    }

    /// <summary>Review thread 1: the same when there is no place for a note at all.</summary>
    [Fact]
    public void AllUsersUpdate_WithNoPlaceForANote_IsNotHandedOver()
    {
        var applied = 0;

        Assert.Throws<InvalidOperationException>(() =>
            PendingUpdateMarker.HandOver(null, NEW, needsElevation: true, () => false, () => applied++));

        Assert.Equal(0, applied);
    }

    [Fact]
    public void SingleUserInstall_NeedsNoPlaceForANote()
    {
        var applied = 0;

        PendingUpdateMarker.HandOver(null, NEW, needsElevation: false, () => false, () => applied++);

        Assert.Equal(1, applied);
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
            PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: true, () => true, () => applied++));

        Assert.Contains("Another copy of DAQiFi", ex.Message);
        Assert.Equal(0, applied);
        Assert.Empty(Directory.GetFiles(_notes));
    }

    [Fact]
    public void UpdaterThatDoesNotStart_RemovesTheNote_AndTheErrorReachesTheCaller()
    {
        var failure = new IOException("Update.exe missing");

        var thrown = Assert.Throws<IOException>(() =>
            PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: true, () => false, () => throw failure));

        Assert.Same(failure, thrown);
        Assert.Empty(Directory.GetFiles(_notes));
    }

    /// <summary>A failed look for other copies counts as one: closing someone's work is worse than not installing.</summary>
    [Fact]
    public void LookForCopiesThatFails_StopsTheInstall_AndRemovesTheNote()
    {
        var applied = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PendingUpdateMarker.HandOver(For(Alice), NEW, needsElevation: true, () => throw new UnauthorizedAccessException(), () => applied++));

        Assert.Equal(0, applied);
        Assert.Empty(Directory.GetFiles(_notes));
    }

    // ---- How long the note counts (review thread 2) -------------------------------------------

    /// <summary>
    /// Review thread 2: the note has no time limit. A prompt that waits for hours, or a slow
    /// prerequisite install, still stops a new copy for as long as the updater runs.
    /// </summary>
    [Fact]
    public void Note_CountsForAsLongAsTheUpdaterRuns_WithNoTimeLimit()
    {
        AliceHandsOver();

        // The updater has asked Windows for an elevated copy of itself: both run in Alice's session.
        _running.Add(Updater(ELEVATED_UPDATER_PID, ALICE_SESSION));
        Assert.Equal(NEW, Start(Bob));

        _running.RemoveAll(p => p.Id == UPDATER_PID);
        Assert.Equal(NEW, Start(Bob));
    }

    [Fact]
    public void Note_StopsCountingWhenTheUpdaterAndItsElevatedCopyHaveExited()
    {
        AliceHandsOver();
        _running = [];

        Assert.Null(Start(Bob));
    }

    /// <summary>An updater in another session belongs to some other update, not to this note.</summary>
    [Fact]
    public void UpdaterInAnotherSession_DoesNotKeepTheNote()
    {
        AliceHandsOver();
        _running = [Updater(UPDATER_PID, BOB_SESSION)];

        Assert.Null(Start(Bob));
    }

    /// <summary>The writer's process ID counts only while the process with it has the writer's name.</summary>
    [Fact]
    public void WritersProcessId_ReusedByAnotherProgram_DoesNotKeepTheNote()
    {
        AliceHandsOver();
        _running = [new SessionProcess(ALICE_PID, "notepad", ALICE_SESSION)];

        Assert.Null(Start(Bob));
    }

    /// <summary>
    /// Review thread 2: Velopack's updater waits 10 minutes for its elevated copy, then restarts Alice
    /// while that copy still runs. Her restart must not end the note, and must not start either.
    /// </summary>
    [Fact]
    public void RestartAfterTheUpdaterTimesOut_WhileItsElevatedCopyRuns_KeepsTheNote()
    {
        AliceHandsOver();
        _running = [Updater(ELEVATED_UPDATER_PID, ALICE_SESSION)];
        var waits = 0;

        Assert.Equal(NEW, Start(Alice, restartedByUpdater: true, wait: _ =>
        {
            if (++waits > 1000) { throw new InvalidOperationException("waited forever"); }
        }));
        Assert.Equal((int)(PendingUpdateMarker.RestartWait / PendingUpdateMarker.RestartPoll), waits);
        Assert.Equal(NEW, Start(Bob));
    }

    /// <summary>The updater starts Alice's copy, then exits. Her copy waits for that, then starts and ends the note.</summary>
    [Fact]
    public void RestartByTheUpdater_WaitsForItToExit_ThenEndsTheNote()
    {
        AliceHandsOver();
        var waits = 0;

        var seen = Start(Alice, restartedByUpdater: true, wait: _ =>
        {
            if (++waits == 2) { _running.RemoveAll(p => p.Id == UPDATER_PID); }
        });

        Assert.Null(seen);
        Assert.Equal(2, waits);
        Assert.Empty(Directory.GetFiles(_notes));
    }

    /// <summary>Only a copy the updater restarted waits; any other copy is told at once.</summary>
    [Fact]
    public void OrdinaryStart_DoesNotWait()
    {
        AliceHandsOver();

        Assert.Equal(NEW, Start(Bob, restartedByUpdater: false));
    }

    /// <summary>After a restart, this copy can have the writer's old process ID. It is never the writer.</summary>
    [Fact]
    public void RestartedCopyWithTheWritersOldProcessId_IsNotTheWriter()
    {
        AliceHandsOver();
        _running = [];

        Assert.Null(Start(Alice, restartedByUpdater: true, wait: _ => { }));
    }

    // ---- Who is stopped (review thread 3) -----------------------------------------------------

    /// <summary>Review thread 3: Alice signed in twice. Her other session can start work the update would end.</summary>
    [Fact]
    public void SameAccountInAnotherSession_IsStopped()
    {
        AliceHandsOver();

        Assert.Equal(NEW, Start(Alice with { SessionId = BOB_SESSION, ProcessId = 4001 }));
    }

    /// <summary>Review thread 3: an ordinary start in the writer's own session is stopped too.</summary>
    [Fact]
    public void OrdinaryStartOfTheWriter_IsStopped()
    {
        AliceHandsOver();

        Assert.Equal(NEW, Start(Alice with { ProcessId = 4002 }));
    }

    /// <summary>The updater starts DAQiFi with hook arguments during the install. Those runs are part of the update.</summary>
    [Theory]
    [InlineData("--veloapp-obsolete")]
    [InlineData("--veloapp-updated")]
    [InlineData("--squirrel-install")]
    public void InstallerHookRun_IsNeverStopped(string hook)
    {
        AliceHandsOver();

        Assert.Null(Start(Bob, args: [hook, NEW]));
    }

    [Fact]
    public void OtherArguments_DoNotPassTheCheck()
    {
        AliceHandsOver();

        Assert.Equal(NEW, Start(Bob, args: ["--profile", "lab"]));
    }

    // ---- Keys and files ------------------------------------------------------------------------

    [Fact]
    public void NoteForAnotherInstall_StopsNothing()
    {
        AliceHandsOver();
        _running.Add(Process(Bob));

        var other = Path.Combine(Path.GetTempPath(), "daqifi-install-509", "DAQiFi-beta");
        Assert.Null(For(Bob, other).VersionBeingInstalled(NoArgs, false, () => _running.ToList()));
    }

    /// <summary>Windows paths ignore case, and a folder can come with or without a final separator.</summary>
    [Fact]
    public void SameInstall_WrittenDifferently_IsTheSameInstall()
    {
        AliceHandsOver();
        _running.Add(Process(Bob));

        var sameFolder = _install.ToLowerInvariant() + Path.DirectorySeparatorChar;
        Assert.Equal(NEW, For(Bob, sameFolder).VersionBeingInstalled(NoArgs, false, () => _running.ToList()));
    }

    /// <summary>
    /// One note per user and session. User names ignore case on Windows, so a second hand-over by the
    /// same account in the same session replaces the first note rather than adding one.
    /// </summary>
    [Fact]
    public void Notes_AreOnePerUserAndSession()
    {
        AliceHandsOver();
        PendingUpdateMarker.HandOver(For(Alice with { User = @"pc\ALICE" }), NEW, true, () => false, () => { });
        Assert.Single(Directory.GetFiles(_notes));

        PendingUpdateMarker.HandOver(For(Alice with { SessionId = BOB_SESSION }), NEW, true, () => false, () => { });
        PendingUpdateMarker.HandOver(For(Bob), NEW, true, () => false, () => { });
        Assert.Equal(3, Directory.GetFiles(_notes).Length);
    }

    [Fact]
    public void NoNotesFolder_StopsNothing_AndDoesNotListProcesses()
    {
        var listed = 0;

        Assert.Null(For(Bob).VersionBeingInstalled(NoArgs, false, () => { listed++; return []; }));
        Assert.Equal(0, listed);
    }

    /// <summary>A note that cannot be read never stops a start, since nothing could ever end it, and it is removed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("3.4.0")]
    [InlineData("\n1\n1001\nDAQiFi")]
    [InlineData("3.4.0\nnot a session\n1001\nDAQiFi")]
    [InlineData("3.4.0\n1\nnot a process\nDAQiFi")]
    public void UnreadableNote_StopsNothing_AndIsRemoved(string content)
    {
        AliceHandsOver();
        var note = Assert.Single(Directory.GetFiles(_notes));
        File.WriteAllText(note, content);

        // Any note that could be read would count here: session 0, process 0, and Alice's session all run something.
        _running = [Updater(UPDATER_PID, ALICE_SESSION), Updater(UPDATER_PID + 1, 0), new SessionProcess(0, "DAQiFi", 0), new SessionProcess(1001, "DAQiFi", 0)];
        Assert.Null(Start(Bob));
        Assert.Empty(Directory.GetFiles(_notes));
    }

    /// <summary>When the process list cannot be read, a note counts: closing someone's work is worse than starting later.</summary>
    [Fact]
    public void ProcessListThatFails_WithANote_StopsTheStart()
    {
        AliceHandsOver();

        Assert.Equal(NEW, For(Bob).VersionBeingInstalled(NoArgs, false, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void ProcessListThatFails_WithOnlyUnreadableNotes_StopsNothing()
    {
        AliceHandsOver();
        File.WriteAllText(Assert.Single(Directory.GetFiles(_notes)), "garbage");

        Assert.Null(For(Bob).VersionBeingInstalled(NoArgs, false, () => throw new InvalidOperationException()));
    }

    // ---- The start check comes first (review thread 4) -----------------------------------------

    /// <summary>
    /// Review thread 4: the desktop head reads the note before, and apart from, any updater setup, so
    /// a failed Velopack start or update service cannot skip it. The head has no test project of its
    /// own, so this pins the order in its source; the check itself needs only the note and the
    /// process list (see the tests above).
    /// </summary>
    [Fact]
    public void DesktopHead_ChecksForAPendingUpdate_BeforeAnyUpdaterSetup()
    {
        var program = CodeOnly(BindingFacts.Source("Daqifi.Avalonia.Desktop/Program.cs"));
        var main = Between(program, "public static void Main(", "BuildAvaloniaApp().StartWithClassicDesktopLifetime");
        var check = main.IndexOf("PendingUpdateCheck(args)", StringComparison.Ordinal);
        var velopack = main.IndexOf("TryRunVelopack(args)", StringComparison.Ordinal);
        Assert.True(check >= 0 && velopack > check, $"Main: check at {check}, updater setup at {velopack}");

        var checkBody = Between(program, "private static string? PendingUpdateCheck(", "private static void SayUpdateInProgress(");
        Assert.DoesNotContain("VelopackApp.", checkBody);
        Assert.DoesNotContain("new VelopackAppUpdateService", checkBody);
        Assert.DoesNotContain("AppUpdates.Current", checkBody);

        var service = CodeOnly(BindingFacts.Source("Daqifi.Avalonia.Desktop/VelopackAppUpdateService.cs"));
        Assert.Contains("internal static PendingUpdateMarker? PendingUpdateNote()", service);
    }

    private static string CodeOnly(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return text[from..to];
    }
}
