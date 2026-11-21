using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using EFCore.BulkExtensions;
using Microsoft.Data.Sqlite;
using DesktopApp = Daqifi.Desktop.App;

// The Logged Data NAME box (issue #681), kept out of Program.cs like LivePlotLoad.cs so the rows
// other PRs edit there do not collide with this one.
//
//   * --scripted session-name-box: no board. Seeds three named sessions, opens each one's settings
//     drawer and edits its NAME box with real key events on the headless window, so the real
//     TextBox and the pane's own handlers run under a dispatcher that fires its timers (Pump).
//
// Before #681 was fixed, an empty box was refilled with "Session {ID}" at once, with the caret at
// the start, so the next name the user typed was saved as "Pump testSession 2". The three ways to
// empty the box below are the three rows of that issue's table.

internal static partial class HeadlessBench
{
    private const string StateSessionNameBox = "session-name-box";

    /// <summary>The names <see cref="StateSessionNameBox"/> seeds, for session IDs 1, 2 and 3.</summary>
    private static readonly string[] NameBoxSeedNames = ["Bench run 1", "Bench run 2", "Bench run 3"];

    /// <summary>Samples per seeded session. Not zero: the app deletes a sample-less session at
    /// startup as litter from an abandoned run, so the row would be measuring the purge.</summary>
    private const int NameBoxSeedSamples = 20;

    /// <summary>Longer than the pane's 250 ms rename debounce, so a pump this long lets a pending
    /// database write land and lets any timer the box depends on fire before the box is read.</summary>
    private static readonly TimeSpan PastRenameDebounce = TimeSpan.FromMilliseconds(600);

    /// <summary>How long to wait for a rename to reach the database before the row reads what is
    /// there. Generous on purpose: a busy CI runner is slower than the 250 ms debounce.</summary>
    private static readonly TimeSpan RenameStoreTimeout = TimeSpan.FromSeconds(5);

    private static (int Sessions, long Samples) SeedSessionNameBox(LoggingContext db, DateTime start)
    {
        for (var id = 1; id <= NameBoxSeedNames.Length; id++)
        {
            db.Sessions.Add(NewSession(id, start.AddMinutes(id), NameBoxSeedNames[id - 1], NameBoxSeedSamples));
            db.SessionDeviceMetadata.Add(NewMetadata(id));
        }
        db.SaveChanges();
        db.BulkInsert(Enumerable.Range(1, NameBoxSeedNames.Length)
            .SelectMany(id => Enumerable.Range(0, NameBoxSeedSamples)
                .Select(i => NewSample(id, start.AddMinutes(id).AddSeconds(i), i * 0.01)))
            .ToList());
        return (NameBoxSeedNames.Length, (long)NameBoxSeedNames.Length * NameBoxSeedSamples);
    }

    /// <summary>
    /// LOGGED-NAME: the NAME box in a logged session's settings drawer. Three checks:
    /// <list type="bullet">
    /// <item><c>empty</c> — each of three ways to empty the box leaves it empty, at once and after
    /// the rename debounce, while the user is still in it (#681).</item>
    /// <item><c>works</c> — the name typed into the emptied box is exactly what the box, the session,
    /// the list row and the database hold.</item>
    /// <item><c>commit</c> — an empty name is committed by Enter, by Tab out of the box, and by the
    /// drawer closing: the box then shows "Session {ID}", the database stores an empty name (the
    /// #427 case), a fresh read of the database renders the default, and a name typed after Enter
    /// replaces the default rather than going in front of it.</item>
    /// </list>
    /// </summary>
    private static void RunSessionNameBoxRow(Window main, DaqifiViewModel shell)
    {
        shell.SelectedIndex = LoggedDataTabIndex;
        PumpFor(TimeSpan.FromMilliseconds(500));

        var sessions = Enumerable.Range(1, NameBoxSeedNames.Length)
            .Select(id => shell.LoggingSessions.FirstOrDefault(s => s.ID == id))
            .ToArray();
        if (sessions.Any(s => s is null))
        {
            const string lost = "the three seeded sessions did not all survive startup";
            Step(1, "LOGGED-NAME", "empty", false, lost, null);
            Step(1, "LOGGED-NAME", "works", false, lost, null);
            Step(1, "LOGGED-NAME", "commit", false, lost, null);
            return;
        }
        var (first, second, third) = (sessions[0]!, sessions[1]!, sessions[2]!);

        // ---- empty + works: the three rows of #681's table.
        var ways = new (LoggingSession Session, string How, Action<TextBox> Empty, string Typed)[]
        {
            (second, "select all + Backspace", box =>
            {
                box.SelectAll();
                Pump();
                PressKey(main, Key.Back, PhysicalKey.Backspace);
            }, "Pump test"),
            (third, "Backspace from the end to the start", box =>
            {
                box.CaretIndex = box.Text?.Length ?? 0;
                Pump();
                var length = box.Text?.Length ?? 0;
                for (var i = 0; i < length; i++) { PressKey(main, Key.Back, PhysicalKey.Backspace); }
            }, "Tank 4"),
            (first, "select all + Delete", box =>
            {
                box.SelectAll();
                Pump();
                PressKey(main, Key.Delete, PhysicalKey.Delete);
            }, "Oven"),
        };

        var emptyOk = true;
        var worksOk = true;
        var emptyEvidence = new List<string>();
        var worksEvidence = new List<string>();
        string? emptiedShot = null;
        string? typedShot = null;
        foreach (var way in ways)
        {
            var box = OpenNameBox(main, shell, way.Session);
            if (box is null)
            {
                emptyOk = worksOk = false;
                emptyEvidence.Add($"{way.How}: the drawer for session {way.Session.ID} shows no SessionNameTextBox");
                continue;
            }

            // The box shows the session it was opened for: the box has no binding, so this is the pane
            // following the selected session, and an edit would otherwise start from a stale name.
            var before = box.Text;
            var shownOnOpen = before == way.Session.Name;
            way.Empty(box);
            Pump();
            var atOnce = box.Text;
            PumpFor(PastRenameDebounce);
            var settled = box.Text;
            var focused = box.IsKeyboardFocusWithin;
            emptiedShot ??= Capture(main, "t1-namebox-emptied");
            var emptied = shownOnOpen && atOnce == string.Empty && settled == string.Empty && focused;
            emptyOk &= emptied;
            emptyEvidence.Add($"{way.How} on '{before}' (the session's own name={shownOnOpen}): box '{atOnce}' at once, '{settled}' after " +
                              $"{PastRenameDebounce.TotalMilliseconds:F0} ms (caret {box.CaretIndex}, " +
                              $"still in the box={focused})");

            // The box's own Cut/Copy/Paste menu: opening it is part of the edit (the user is about
            // to paste), not the end of it, so the empty box must stay empty while it is open.
            if (ReferenceEquals(way.Session, second))
            {
                var flyout = box.ContextFlyout;
                flyout?.ShowAt(box);
                PumpFor(TimeSpan.FromMilliseconds(200));
                var opened = flyout?.IsOpen == true;
                var focusInBox = box.IsKeyboardFocusWithin;
                var whileOpen = box.Text;
                flyout?.Hide();
                PumpFor(TimeSpan.FromMilliseconds(200));
                var afterHide = box.Text;
                var focusBack = box.IsKeyboardFocusWithin;
                box.Focus();
                Pump();
                emptyOk &= opened && whileOpen == string.Empty && afterHide == string.Empty;
                emptyEvidence.Add($"the box's menu opened={opened} (focus in the box while open={focusInBox}): " +
                                  $"box '{whileOpen}' while open, '{afterHide}' after it closed " +
                                  $"(focus back in the box={focusBack})");
            }

            main.KeyTextInput(way.Typed);
            Pump();
            var stored = WaitForStoredSessionName(way.Session.ID, way.Typed);
            typedShot ??= Capture(main, "t1-namebox-typed");
            var row = ListedSessionName(main, way.Session);
            var exact = box.Text == way.Typed && way.Session.Name == way.Typed
                        && stored == way.Typed && row == way.Typed;
            worksOk &= exact;
            worksEvidence.Add($"{way.How}, then typed '{way.Typed}': box '{box.Text}', session '{way.Session.Name}', " +
                              $"list row '{row}', database '{stored}'");

            shell.CloseLoggedSessionSettingsCommand.Execute(null);
            Pump();
        }

        Step(1, "LOGGED-NAME", "empty", emptyOk, string.Join("; ", emptyEvidence), emptiedShot);
        Step(1, "LOGGED-NAME", "works", worksOk, string.Join("; ", worksEvidence), typedShot);

        // ---- commit: an empty name, committed three ways.
        var commitOk = true;
        var commitEvidence = new List<string>();

        // 1. Enter. Then a new name typed straight away must replace the default, not go in front of it.
        var enterBox = OpenNameBox(main, shell, second);
        if (enterBox is null)
        {
            commitOk = false;
            commitEvidence.Add("Enter: no SessionNameTextBox");
        }
        else
        {
            EmptyBySelectAll(main, enterBox);
            var beforeEnter = enterBox.Text;
            PressKey(main, Key.Enter, PhysicalKey.Enter);
            Pump();
            var shown = enterBox.Text;
            var named = second.Name;
            var stored = WaitForStoredSessionName(second.ID, string.Empty);
            var expected = $"Session {second.ID}";
            var enterOk = beforeEnter == string.Empty && shown == expected && named == expected
                          && stored == string.Empty;
            commitEvidence.Add($"Enter on an empty box: '{beforeEnter}' -> box '{shown}', session '{named}', " +
                               $"database '{stored}'");

            main.KeyTextInput("Kiln");
            Pump();
            var storedKiln = WaitForStoredSessionName(second.ID, "Kiln");
            var replaced = enterBox.Text == "Kiln" && second.Name == "Kiln" && storedKiln == "Kiln";
            commitEvidence.Add($"then typed 'Kiln': box '{enterBox.Text}', session '{second.Name}', database '{storedKiln}'");
            commitOk &= enterOk && replaced;

            shell.CloseLoggedSessionSettingsCommand.Execute(null);
            Pump();
        }

        // 2. Tab out of the box, so it loses keyboard focus while the drawer stays open.
        var tabBox = OpenNameBox(main, shell, third);
        if (tabBox is null)
        {
            commitOk = false;
            commitEvidence.Add("Tab: no SessionNameTextBox");
        }
        else
        {
            EmptyBySelectAll(main, tabBox);
            var beforeTab = tabBox.Text;
            PressKey(main, Key.Tab, PhysicalKey.Tab);
            Pump();
            var left = !tabBox.IsKeyboardFocusWithin;
            var shown = tabBox.Text;
            var stored = WaitForStoredSessionName(third.ID, string.Empty);
            var expected = $"Session {third.ID}";
            commitOk &= beforeTab == string.Empty && left && shown == expected && third.Name == expected
                        && stored == string.Empty;
            commitEvidence.Add($"Tab out of an empty box: '{beforeTab}' -> left the box={left}, box '{shown}', " +
                               $"session '{third.Name}', database '{stored}'");

            shell.CloseLoggedSessionSettingsCommand.Execute(null);
            Pump();
        }

        // 3. The drawer closes while the box is empty and still has focus — what a click on the scrim
        // does, since the scrim is not focusable and takes no focus from the box.
        var closeBox = OpenNameBox(main, shell, first);
        if (closeBox is null)
        {
            commitOk = false;
            commitEvidence.Add("drawer close: no SessionNameTextBox");
        }
        else
        {
            EmptyBySelectAll(main, closeBox);
            var beforeClose = closeBox.Text;
            shell.CloseLoggedSessionSettingsCommand.Execute(null);
            Pump();
            var focusAfterClose = closeBox.IsKeyboardFocusWithin;
            shell.OpenLoggingSessionSettingsCommand.Execute(first);
            PumpFor(TimeSpan.FromMilliseconds(400));
            var shown = closeBox.Text;
            var stored = WaitForStoredSessionName(first.ID, string.Empty);
            var expected = $"Session {first.ID}";
            commitOk &= beforeClose == string.Empty && shown == expected && first.Name == expected
                        && stored == string.Empty;
            commitEvidence.Add($"drawer closed on an empty box: '{beforeClose}' -> box still focused={focusAfterClose}; " +
                               $"reopened: box '{shown}', session '{first.Name}', database '{stored}'");

            shell.CloseLoggedSessionSettingsCommand.Execute(null);
            Pump();
        }

        // At the end, with every edit settled: the stored column, then what the next launch sees (a
        // fresh context, whose getter renders a stored blank as the default). The getter alone cannot
        // tell an empty name from "Session {ID}" saved as text, so the raw column is read as well.
        PumpFor(PastRenameDebounce);
        var raw = Enumerable.Range(1, NameBoxSeedNames.Length).ToDictionary(id => id, StoredSessionName);
        var rawOk = raw[first.ID] == string.Empty && raw[second.ID] == "Kiln" && raw[third.ID] == string.Empty;
        var reloaded = ReloadedSessionNames();
        var reloadOk = reloaded.Count == 3 && reloaded[first.ID] == $"Session {first.ID}"
                       && reloaded[second.ID] == "Kiln" && reloaded[third.ID] == $"Session {third.ID}";
        commitOk &= rawOk && reloadOk;
        commitEvidence.Add("at the end the database stores " +
                           string.Join(", ", raw.Select(p => $"{p.Key}='{p.Value}'")) +
                           " and a fresh read gives " +
                           string.Join(", ", reloaded.OrderBy(p => p.Key).Select(p => $"{p.Key}='{p.Value}'")));

        Step(1, "LOGGED-NAME", "commit", commitOk, string.Join("; ", commitEvidence),
             Capture(main, "t1-namebox-committed"));

        RunSessionNameLeaveCheck(main, shell, first, second, third);
    }

    /// <summary>
    /// LOGGED-NAME/leave: going to another tab and back, with no edit, changes no stored name. Found
    /// in review of #684: the pane wrote an empty string into the box when it unloaded, and the
    /// deferred TextChanged that followed saved it, so a visit to Live Graph erased a saved "Kiln".
    /// The pane must really unload for this to mean anything, so that is asserted too.
    /// </summary>
    private static void RunSessionNameLeaveCheck(
        Window main, DaqifiViewModel shell, LoggingSession first, LoggingSession second, LoggingSession third)
    {
        var ids = new[] { first.ID, second.ID, third.ID };
        string Stored() => string.Join(", ", ids.Select(id => $"{id}='{StoredSessionName(id)}'"));

        // The reviewer's order: the named session is the selected one, its drawer was open and is
        // now closed. The session that is selected is the one an unload could write to, so with an
        // already-empty session selected this check would pass on the broken build (measured).
        shell.OpenLoggingSessionSettingsCommand.Execute(second);
        PumpFor(TimeSpan.FromMilliseconds(400));
        shell.CloseLoggedSessionSettingsCommand.Execute(null);
        PumpFor(PastRenameDebounce);
        var selected = ReferenceEquals(shell.SelectedLoggingSession, second);

        // Judged against what is stored now, not against fixed values, so this check measures the
        // visit only: a fault in an earlier check cannot fail it, or hide behind it.
        var snapshot = ids.ToDictionary(id => id, StoredSessionName);
        var saved = snapshot[second.ID] ?? string.Empty;
        var named = saved.Length > 0 && second.Name == saved;
        bool StoredAsExpected() => ids.All(id => StoredSessionName(id) == snapshot[id]);
        var before = Stored();
        var paneBefore = LoggedDataPaneShown(main);

        shell.SelectedIndex = LiveGraphTabIndex;
        PumpFor(PastRenameDebounce);
        PumpFor(PastRenameDebounce);
        var paneUnloaded = !LoggedDataPaneShown(main);
        var awayOk = StoredAsExpected() && second.Name == saved;
        var away = Stored();

        shell.SelectedIndex = LoggedDataTabIndex;
        PumpFor(PastRenameDebounce);
        PumpFor(PastRenameDebounce);
        var backOk = StoredAsExpected() && second.Name == saved;
        var back = Stored();

        // And opening the drawer again shows the saved name and writes nothing.
        shell.OpenLoggingSessionSettingsCommand.Execute(second);
        PumpFor(TimeSpan.FromMilliseconds(400));
        var box = main.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "SessionNameTextBox" && t.IsEffectivelyVisible);
        var shown = box?.Text;
        PumpFor(PastRenameDebounce);
        var reopenOk = shown == saved && StoredAsExpected() && second.Name == saved;
        var reopened = Stored();
        shell.CloseLoggedSessionSettingsCommand.Execute(null);
        Pump();

        Step(1, "LOGGED-NAME", "leave", selected && named && paneBefore && paneUnloaded && awayOk && backOk && reopenOk,
             $"session 2 selected={selected}, drawer closed, stored {before} (Logged Data pane shown={paneBefore}); " +
             $"on Live Graph after {2 * PastRenameDebounce.TotalMilliseconds:F0} ms: pane unloaded={paneUnloaded}, " +
             $"stored {away}, session 2 '{second.Name}'; back on Logged Data: stored {back}; " +
             $"drawer reopened on session 2: box '{shown}', stored {reopened}",
             null);
    }

    private static bool LoggedDataPaneShown(Window main) =>
        main.GetVisualDescendants().OfType<Daqifi.Desktop.View.Prototype.LoggedDataPanePrototype>().Any();

    /// <summary>Opens <paramref name="session"/>'s settings drawer the way the row's settings button
    /// does, and puts keyboard focus in its NAME box the way a click in the box does.</summary>
    private static TextBox? OpenNameBox(Window main, DaqifiViewModel shell, LoggingSession session)
    {
        shell.OpenLoggingSessionSettingsCommand.Execute(session);
        PumpFor(TimeSpan.FromMilliseconds(400));
        var box = main.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "SessionNameTextBox" && t.IsEffectivelyVisible);
        if (box is null) { return null; }
        box.Focus();
        Pump();
        return box;
    }

    private static void EmptyBySelectAll(Window main, TextBox box)
    {
        box.SelectAll();
        Pump();
        PressKey(main, Key.Back, PhysicalKey.Backspace);
        PumpFor(PastRenameDebounce);
    }

    /// <summary>One key down and up on the headless window, so the focused control's own key
    /// handling runs — not a property write that skips it.</summary>
    private static void PressKey(Window main, Key key, PhysicalKey physical)
    {
        main.KeyPress(key, RawInputModifiers.None, physical, null);
        main.KeyRelease(key, RawInputModifiers.None, physical, null);
        Pump();
    }

    /// <summary>The name the session's row in the pane's own SessionList shows.</summary>
    private static string? ListedSessionName(Window main, LoggingSession session) =>
        main.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "SessionList")?
            .GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, session))?
            .GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "SessionNameText")?.Text;

    /// <summary>Waits for the session's stored name to read <paramref name="expected"/>, then pumps
    /// past the rename debounce once more and returns what is stored then, which is the evidence
    /// either way.</summary>
    /// <remarks>The second pump is not padding. <c>TextBox.TextChanged</c> arrives through the
    /// dispatcher, after the text has changed, so a rename the box's own write sets off lands a
    /// debounce LATER than the moment the expected value is first seen. Reading at that first moment
    /// passed a build that saved "Session {ID}" as text over an empty name 250 ms afterwards.</remarks>
    private static string? WaitForStoredSessionName(int sessionId, string expected)
    {
        PumpUntil(() => StoredSessionName(sessionId) == expected, RenameStoreTimeout);
        PumpFor(PastRenameDebounce);
        return StoredSessionName(sessionId);
    }

    /// <summary>The Name column as stored, read straight from SQLite: an entity read through EF would
    /// go through <see cref="LoggingSession.Name"/>'s getter, which renders a blank as the default
    /// label and so cannot tell an empty name from "Session {ID}" written as text.</summary>
    private static string? StoredSessionName(int sessionId)
    {
        using var connection = new SqliteConnection($"Data source={DesktopApp.DatabasePath};Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Name FROM Sessions WHERE ID = $id";
        query.Parameters.AddWithValue("$id", sessionId);
        return query.ExecuteScalar() as string;
    }

    /// <summary>Each session's name as the next launch shows it: through a fresh context and the
    /// entity's own getter.</summary>
    private static Dictionary<int, string> ReloadedSessionNames()
    {
        using var db = new SeedContextFactory(DesktopApp.DatabasePath).CreateDbContext();
        return db.Sessions.AsEnumerable().ToDictionary(s => s.ID, s => s.Name);
    }
}
