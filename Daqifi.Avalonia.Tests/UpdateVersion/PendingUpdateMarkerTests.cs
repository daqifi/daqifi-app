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

    /// <summary>An updater whose path this copy cannot read, as for another user's or an elevated process.</summary>
    private static SessionProcess Updater(int pid, int session) => new(pid, PendingUpdateMarker.UPDATER_PROCESS_NAME, session);

    private static SessionProcess UpdaterAt(int pid, int session, string imagePath) =>
        new(pid, PendingUpdateMarker.UPDATER_PROCESS_NAME, session, imagePath);

    private const string PROBLEM = "problem: ";

    /// <summary>The version that stops the start, <see cref="PROBLEM"/> and what could not be read, or null.</summary>
    private static string? Describe(PendingUpdate? pending) =>
        pending is null ? null : pending.Version ?? PROBLEM + pending.Problem;

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
            return Describe(For(copy).CheckAtStart(args ?? NoArgs, restartedByUpdater, () => _running.ToList(),
                wait ?? (_ => throw new InvalidOperationException("waited"))));
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

    /// <summary>Only the writer's own process keeps the note, not any other copy of DAQiFi that runs.</summary>
    [Fact]
    public void AnotherCopyOfDaqifi_DoesNotKeepTheNote()
    {
        AliceHandsOver();
        _running = [new SessionProcess(5001, "DAQiFi", ALICE_SESSION)];

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
        Assert.Null(For(Bob, other).CheckAtStart(NoArgs, false, () => _running.ToList()));
    }

    /// <summary>Windows paths ignore case, and a folder can come with or without a final separator.</summary>
    [Fact]
    public void SameInstall_WrittenDifferently_IsTheSameInstall()
    {
        AliceHandsOver();
        _running.Add(Process(Bob));

        var sameFolder = _install.ToLowerInvariant() + Path.DirectorySeparatorChar;
        Assert.Equal(NEW, For(Bob, sameFolder).CheckAtStart(NoArgs, false, () => _running.ToList())?.Version);
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

        Assert.Null(For(Bob).CheckAtStart(NoArgs, false, () => { listed++; return []; }));
        Assert.Equal(0, listed);
    }

    /// <summary>When the process list cannot be read, a note counts: closing someone's work is worse than starting later.</summary>
    [Fact]
    public void ProcessListThatFails_WithANote_StopsTheStart()
    {
        AliceHandsOver();

        Assert.Equal(NEW, For(Bob).CheckAtStart(NoArgs, false, () => throw new InvalidOperationException())?.Version);
    }

    // ---- Notes that cannot be read (review round 2, thread 1) ----------------------------------

    /// <summary>Counts the waits, and fails a test that would wait forever.</summary>
    private Action<TimeSpan> CountWaits(Action<int>? onWait = null) => _ =>
    {
        _waits++;
        if (_waits > 1000) { throw new InvalidOperationException("waited forever"); }
        onWait?.Invoke(_waits);
    };

    private int _waits;

    /// <summary>
    /// Round 2, thread 1: a valid note that another process holds open while the updater runs. A note
    /// that cannot be read is no evidence that the update finished, so the start stops, says why, and
    /// the note stays.
    /// </summary>
    [Fact]
    public void NoteThatCannotBeOpened_StopsTheStart_AndIsKept()
    {
        AliceHandsOver();
        var note = Assert.Single(Directory.GetFiles(_notes));

        string? seen;
        using (new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            seen = Start(Bob, wait: CountWaits());
        }

        Assert.StartsWith(PROBLEM, seen);
        Assert.Contains(note, seen);
        Assert.Equal(PendingUpdateMarker.READ_RETRIES, _waits);
        Assert.True(File.Exists(note));
    }

    /// <summary>A read that fails for a moment: the start looks again, and then decides from the note.</summary>
    [Fact]
    public void NoteThatCannotBeOpenedForAMoment_IsReadOnTheNextLook()
    {
        AliceHandsOver();
        var note = Assert.Single(Directory.GetFiles(_notes));
        var holder = new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        try
        {
            Assert.Equal(NEW, Start(Bob, wait: CountWaits(n => { if (n == 2) { holder.Dispose(); } })));
            Assert.Equal(2, _waits);
        }
        finally
        {
            holder.Dispose();
        }
    }

    private const string FOLDER_NEEDS_ENFORCED_PERMISSIONS =
        "Needs a folder that this user cannot list: Windows has no UnixFileMode, and a run as root ignores it.";

    /// <summary>
    /// Round 2, thread 1: a notes folder that is there but cannot be listed stops the start too.
    /// Only a folder that is not there means that no update was ever handed over.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(FOLDER_NEEDS_ENFORCED_PERMISSIONS)]
    public void NotesFolderThatCannotBeListed_StopsTheStart()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(FOLDER_NEEDS_ENFORCED_PERMISSIONS); return; }
        AliceHandsOver();
        File.SetUnixFileMode(_notes, UnixFileMode.None);
        try
        {
            var seen = Describe(For(Bob).CheckAtStart(NoArgs, false, () => _running.ToList(), CountWaits()));

            Assert.StartsWith(PROBLEM, seen);
            Assert.Contains(_notes, seen);
            Assert.Equal(PendingUpdateMarker.READ_RETRIES, _waits);
        }
        finally
        {
            File.SetUnixFileMode(_notes, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A note whose content cannot be read stops the start and stays: nothing shows that its update
    /// has finished. Deleting a file is for an administrator, whom the message tells.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("3.4.0")]
    [InlineData("\n1\n1001\nDAQiFi")]
    [InlineData("3.4.0\nnot a session\n1001\nDAQiFi")]
    [InlineData("3.4.0\n1\nnot a process\nDAQiFi")]
    public void NoteWithContentThatCannotBeRead_StopsTheStart_AndIsKept(string content)
    {
        AliceHandsOver();
        var note = Assert.Single(Directory.GetFiles(_notes));
        File.WriteAllText(note, content);

        // Any note that could be read would count here: session 0, process 0, and Alice's session all run something.
        _running = [Updater(UPDATER_PID, ALICE_SESSION), Updater(UPDATER_PID + 1, 0), new SessionProcess(0, "DAQiFi", 0), new SessionProcess(1001, "DAQiFi", 0)];
        var seen = Start(Bob, wait: CountWaits());

        Assert.StartsWith(PROBLEM, seen);
        Assert.Contains(note, seen);
        Assert.True(File.Exists(note));
    }

    /// <summary>A note that counts gives the clearer message, even when another note cannot be read.</summary>
    [Fact]
    public void NoteThatCounts_IsReported_BeforeANoteThatCannotBeRead()
    {
        AliceHandsOver();
        File.WriteAllText(Path.Combine(_notes, Path.GetFileName(Assert.Single(Directory.GetFiles(_notes))) + "-other"), "garbage");

        Assert.Equal(NEW, Start(Bob, wait: CountWaits()));
    }

    /// <summary>A note that was read and no longer counts is removed, even while another note cannot be read.</summary>
    [Fact]
    public void FinishedNote_IsRemoved_WhileANoteThatCannotBeReadStays()
    {
        AliceHandsOver();
        var finished = Assert.Single(Directory.GetFiles(_notes));
        var unreadable = finished + "-other";
        File.WriteAllText(unreadable, "garbage");
        _running = [];

        Assert.StartsWith(PROBLEM, Start(Bob, wait: CountWaits()));
        Assert.False(File.Exists(finished));
        Assert.True(File.Exists(unreadable));
    }

    [Fact]
    public void ProcessListThatFails_WithOnlyUnreadableNotes_StopsTheStart()
    {
        AliceHandsOver();
        File.WriteAllText(Assert.Single(Directory.GetFiles(_notes)), "garbage");

        var seen = Describe(For(Bob).CheckAtStart(NoArgs, false, () => throw new InvalidOperationException(), CountWaits()));

        Assert.StartsWith(PROBLEM, seen);
    }

    [Fact]
    public void Messages_SayWhatStopsTheStart()
    {
        Assert.Contains("DAQiFi 3.4.0 is being installed", new PendingUpdate(NEW, null).Message);

        var unknown = new PendingUpdate(null, "The note X could not be read (locked).").Message;
        Assert.Contains("cannot tell if an update is being installed", unknown);
        Assert.Contains("The note X could not be read (locked).", unknown);
    }

    // ---- Which updater keeps a note (review round 2, thread 2) ---------------------------------

    /// <summary>When its path can be read, an updater keeps the note only if it is this install's <c>Update.exe</c>.</summary>
    [Fact]
    public void ThisInstallsUpdater_WithAPathThatCanBeRead_KeepsTheNote()
    {
        AliceHandsOver();
        _running = [UpdaterAt(UPDATER_PID, ALICE_SESSION, Path.Combine(_install.ToLowerInvariant(), "update.exe"))];

        Assert.Equal(NEW, Start(Bob));
    }

    /// <summary>
    /// Round 2, thread 2: DAQiFi's updater and another program's <c>Update.exe</c> run in Alice's
    /// session. When DAQiFi's updater exits, the other one does not keep the note.
    /// </summary>
    [Fact]
    public void AnotherInstallsUpdater_InTheWritersSession_DoesNotKeepTheNote()
    {
        AliceHandsOver();
        var other = UpdaterAt(2009, ALICE_SESSION, Path.Combine(Path.GetTempPath(), "SomeOtherApp", "Update.exe"));
        _running = [UpdaterAt(UPDATER_PID, ALICE_SESSION, Path.Combine(_install, "Update.exe")), other];
        Assert.Equal(NEW, Start(Bob));

        _running = [other];
        Assert.Null(Start(Bob));
    }

    /// <summary>Round 2, thread 2: Alice's restart can read her own processes' paths, so another program's updater does not hold it.</summary>
    [Fact]
    public void RestartWhileAnotherProgramsUpdaterRuns_StartsAndEndsTheNote()
    {
        AliceHandsOver();
        _running = [UpdaterAt(2009, ALICE_SESSION, Path.Combine(Path.GetTempPath(), "SomeOtherApp", "Update.exe"))];

        Assert.Null(Start(Alice, restartedByUpdater: true, wait: CountWaits()));
        Assert.Equal(0, _waits);
        Assert.Empty(Directory.GetFiles(_notes));
    }

    /// <summary>A path that is there but cannot be understood could still be this install's updater.</summary>
    [Fact]
    public void UpdaterWithAPathThatCannotBeUnderstood_KeepsTheNote()
    {
        AliceHandsOver();
        _running = [UpdaterAt(UPDATER_PID, ALICE_SESSION, "\0")];

        Assert.Equal(NEW, Start(Bob));
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

        var checkBody = Between(program, "private static PendingUpdate? PendingUpdateCheck(", "private static void SayUpdateInProgress(");
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
