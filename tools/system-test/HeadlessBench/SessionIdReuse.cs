using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Daqifi.Desktop.ViewModels;
using EFCore.BulkExtensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DesktopApp = Daqifi.Desktop.App;

// A session that gets the ID of a deleted session (issue #680), kept out of Program.cs like
// SessionNameBox.cs, so the rows other PRs edit there do not collide with this one.
//
//   * --scripted session-id-reuse: no board. Seeds "Old zero", "Old one" and "Old two" (IDs 0, 1
//     and 2). Deletes "Old two" with its row's DELETE button, then imports a CSV log through the
//     app's own SD card importer, which gives the new session ID 2 again. DELETE ALL, then three
//     imports, give IDs 0, 1 and 2 again. Last, a row's DELETE confirmation stays open while a second
//     writer replaces that recording with a new one at the same ID.
//
// Before #680 was fixed, LoggingSession.Equals compared only the ID. The session list's ListBox gave
// the new session the row container of the deleted one, the two were equal, and the row kept the
// deleted session: its name, its sample count, and its DELETE button's parameter. That DELETE asked
// "Are you sure you want to delete Old two?" and then deleted the new session.

internal static partial class HeadlessBench
{
    private const string StateSessionIdReuse = "session-id-reuse";

    /// <summary>The names <see cref="StateSessionIdReuse"/> seeds, for session IDs 0, 1 and 2.</summary>
    private static readonly string[] ReuseSeedNames = ["Old zero", "Old one", "Old two"];

    /// <summary>Samples per seeded session. Not zero: the app deletes a sample-less session at
    /// startup as litter from an abandoned run. Not <see cref="ReuseImportSamples"/>: the row's
    /// SAMPLES chip must show which session it holds.</summary>
    private const int ReuseSeedSamples = 20;

    /// <summary>Samples in each CSV log the row imports.</summary>
    private const int ReuseImportSamples = 50;

    /// <summary>How long a delete, a DELETE ALL or an import may take before the row reports it.
    /// Generous on purpose: a busy CI runner is slow, and each is milliseconds on a laptop.</summary>
    private static readonly TimeSpan ReuseStepTimeout = TimeSpan.FromSeconds(30);

    private static (int Sessions, long Samples) SeedSessionIdReuse(LoggingContext db, DateTime start)
    {
        for (var id = 0; id < ReuseSeedNames.Length; id++)
        {
            db.Sessions.Add(NewSession(id, start.AddMinutes(id), ReuseSeedNames[id], ReuseSeedSamples));
            db.SessionDeviceMetadata.Add(NewMetadata(id));
        }
        db.SaveChanges();
        db.BulkInsert(Enumerable.Range(0, ReuseSeedNames.Length)
            .SelectMany(id => Enumerable.Range(0, ReuseSeedSamples)
                .Select(i => NewSample(id, start.AddMinutes(id).AddSeconds(i), i * 0.01)))
            .ToList());
        return (ReuseSeedNames.Length, (long)ReuseSeedNames.Length * ReuseSeedSamples);
    }

    /// <summary>
    /// LOGGED-DEL: a session that gets the ID of a deleted session. Two checks:
    /// <list type="bullet">
    /// <item><c>reused</c> — delete the newest session, import a new one (it gets the same ID): its
    /// row shows the new session, the row's DELETE asks about the new session, and that DELETE
    /// removes only the new session from the list and the database.</item>
    /// <item><c>after-all</c> — DELETE ALL, then three imports (IDs 0, 1 and 2 again): each row shows
    /// its own session, and each row's DELETE has its own session as the parameter.</item>
    /// <item><c>replaced</c> — #693 review: while a row's DELETE confirmation is open, a second writer
    /// deletes that recording and imports a new one, which gets the same ID. Accepting the
    /// confirmation deletes nothing, says so, and the row then shows the stored recording.</item>
    /// </list>
    /// </summary>
    private static void RunSessionIdReuseRow(Window main, DaqifiViewModel shell)
    {
        shell.SelectedIndex = LoggedDataTabIndex;
        PumpFor(TimeSpan.FromMilliseconds(500));

        var seeded = Enumerable.Range(0, ReuseSeedNames.Length)
            .Select(id => shell.LoggingSessions.FirstOrDefault(s => s.ID == id && s.Name == ReuseSeedNames[id]))
            .ToArray();
        if (shell.LoggingSessions.Count != ReuseSeedNames.Length || seeded.Any(s => s is null))
        {
            var lost = $"the three seeded sessions did not all reach the list ({ListedSessions(shell)})";
            Step(1, "LOGGED-DEL", "reused", false, lost, null);
            Step(1, "LOGGED-DEL", "after-all", false, lost, null);
            return;
        }
        var (oldZero, oldOne, oldTwo) = (seeded[0]!, seeded[1]!, seeded[2]!);

        var logs = Path.Combine(_out, "reuse-logs");
        Directory.CreateDirectory(logs);

        // ---- reused: the issue's steps 3 to 7.
        var evidence = new List<string>();
        var reusedOk = true;

        // Step 3: delete "Old two" with its row's DELETE. The control: the confirmation names it.
        var first = ClickRowDelete(main, shell, 2);
        var firstDeleted = first.Confirmation == $"Are you sure you want to delete {oldTwo.Name}?"
                           && first.Finished && ListIs(shell, oldZero, oldOne) && StoredSessionIds().SequenceEqual([0, 1]);
        reusedOk &= firstDeleted;
        evidence.Add($"row 3 DELETE asked '{first.Confirmation}', list after: {ListedSessions(shell)}, " +
                     $"database IDs: [{string.Join(", ", StoredSessionIds())}]");

        // Step 4: a new session. The app's own importer picks its ID.
        var reused = ImportLog(shell, logs, "new_alpha");
        var gotSameId = reused?.ID == oldTwo.ID;
        reusedOk &= gotSameId;
        evidence.Add(reused is null
            ? "the import of new_alpha persisted no session"
            : $"imported '{reused.Name}' with {reused.SampleCount} samples got ID {reused.ID} " +
              $"(the deleted session's ID={gotSameId})");

        // Step 5: the third row.
        var row = ReadRow(main, 2);
        var rowOk = reused is not null && row.Name == reused.Name && ReferenceEquals(row.DeleteParameter, reused)
                    && ReferenceEquals(row.Item, reused);
        reusedOk &= rowOk;
        evidence.Add($"row 3 holds '{(row.Item as LoggingSession)?.Name}', shows '{row.Name}' [{row.Texts}], " +
                     $"its DELETE parameter is '{(row.DeleteParameter as LoggingSession)?.Name}' " +
                     $"(the listed session={ReferenceEquals(row.DeleteParameter, reused)})");
        var reusedShot = Capture(main, "t1-reuse-row");

        // Steps 6 and 7: that row's DELETE names the session it deletes, and deletes only that one.
        if (reused is not null)
        {
            var second = ClickRowDelete(main, shell, 2);
            var secondOk = second.Confirmation == $"Are you sure you want to delete {reused.Name}?"
                           && second.Finished && ListIs(shell, oldZero, oldOne)
                           && StoredSessionIds().SequenceEqual([0, 1])
                           && StoredSampleCount(0) == ReuseSeedSamples && StoredSampleCount(1) == ReuseSeedSamples;
            reusedOk &= secondOk;
            evidence.Add($"row 3 DELETE asked '{second.Confirmation}', list after: {ListedSessions(shell)}, " +
                         $"database IDs: [{string.Join(", ", StoredSessionIds())}] with " +
                         $"{StoredSampleCount(0)} and {StoredSampleCount(1)} samples in IDs 0 and 1");
        }

        Step(1, "LOGGED-DEL", "reused", reusedOk, string.Join("; ", evidence), reusedShot);

        // ---- after-all: DELETE ALL starts the IDs again at 0.
        var allEvidence = new List<string>();
        shell.DeleteAllLoggingSessionCommand.Execute(null);
        var askedAll = PumpUntil(() => shell.ConfirmOverlay.IsOpen, ReuseStepTimeout);
        var allConfirmation = shell.ConfirmOverlay.Message;
        shell.ConfirmOverlay.AffirmativeCommand.Execute(null);
        var emptied = PumpUntil(() => !shell.IsLoggedDataBusy && shell.LoggingSessions.Count == 0
                                      && !shell.DeleteAllLoggingSessionCommand.IsRunning, ReuseStepTimeout);
        var allOk = askedAll && emptied && StoredSessionIds().Count == 0;
        allEvidence.Add($"DELETE ALL asked '{allConfirmation}', list after: {ListedSessions(shell)}, " +
                        $"database IDs: [{string.Join(", ", StoredSessionIds())}]");

        var imported = new[] { "new_alpha", "new_beta", "new_gamma" }.Select(name => ImportLog(shell, logs, name)).ToArray();
        var ids = imported.Select(s => s?.ID).ToArray();
        var restarted = ids.SequenceEqual(new int?[] { 0, 1, 2 });
        allOk &= restarted;
        allEvidence.Add($"three imports got IDs [{string.Join(", ", ids)}] (start again at 0={restarted})");

        for (var index = 0; index < imported.Length; index++)
        {
            var session = imported[index];
            var shown = ReadRow(main, index);
            var shownOk = session is not null && shown.Name == session.Name
                          && ReferenceEquals(shown.Item, session) && ReferenceEquals(shown.DeleteParameter, session);
            allOk &= shownOk;
            allEvidence.Add($"row {index + 1} holds '{(shown.Item as LoggingSession)?.Name}', shows '{shown.Name}' " +
                            $"[{shown.Texts}], DELETE parameter '{(shown.DeleteParameter as LoggingSession)?.Name}'");
        }

        Step(1, "LOGGED-DEL", "after-all", allOk, string.Join("; ", allEvidence), Capture(main, "t1-reuse-after-all"));

        // ---- replaced: the confirmation of row 3 stays open while another writer replaces it.
        var gamma = imported.Length == 3 ? imported[2] : null;
        LoggingSession? replacement = null;
        var replaced = ClickRowDelete(main, shell, 2, whileConfirmOpen: () =>
        {
            ExecuteSql($"DELETE FROM Samples WHERE LoggingSessionID = {gamma?.ID}; " +
                       $"DELETE FROM SessionDeviceMetadata WHERE LoggingSessionID = {gamma?.ID}; " +
                       $"DELETE FROM Sessions WHERE ID = {gamma?.ID};");
            replacement = ImportLog(shell, logs, "independent_writer", addToList: false);
        });
        PumpFor(TimeSpan.FromMilliseconds(300));
        var after = ReadRow(main, 2);
        var sameId = replacement is not null && gamma is not null && replacement.ID == gamma.ID;
        var survived = replacement is not null && StoredSessionIds().Contains(replacement.ID)
                       && StoredSampleCount(replacement.ID) == ReuseImportSamples;
        var told = replaced.Dialog?.Contains("nothing was deleted", StringComparison.Ordinal) == true;
        var shownNow = replacement is not null && after.Name == replacement.Name
                       && (after.Item as LoggingSession)?.ID == replacement.ID;
        Step(1, "LOGGED-DEL", "replaced", gamma is not null && sameId && replaced.Finished && survived && told && shownNow,
             $"row 3 DELETE asked '{replaced.Confirmation}'; while it was open, another writer replaced ID {gamma?.ID} with " +
             $"'{replacement?.Name}' (ID {replacement?.ID}, the same ID={sameId}); after DELETE: the dialog said " +
             $"'{replaced.Dialog}', the database holds IDs [{string.Join(", ", StoredSessionIds())}] with " +
             $"{(replacement is null ? 0 : StoredSampleCount(replacement.ID))} samples in ID {replacement?.ID}, " +
             $"row 3 shows '{after.Name}' [{after.Texts}]",
             Capture(main, "t1-reuse-replaced"));
    }

    /// <summary>What a realized row of the pane's SessionList holds and shows.</summary>
    private readonly record struct ShownRow(object? Item, string? Name, object? DeleteParameter, string Texts);

    /// <summary>Reads the realized row at <paramref name="index"/>: its container's item, the name it
    /// shows, its DELETE button's parameter, and all of its text (for the evidence).</summary>
    private static ShownRow ReadRow(Window main, int index)
    {
        Pump();
        var item = SessionList(main)?.ContainerFromIndex(index) as ListBoxItem;
        if (item is null) { return new ShownRow(null, null, null, "no realized row"); }

        var texts = item.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text);
        return new ShownRow(
            item.DataContext,
            item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "SessionNameText")?.Text,
            RowDeleteButton(item)?.CommandParameter,
            string.Join(" | ", texts));
    }

    /// <summary>The outcome of a row's DELETE: what the confirmation asked, whether the delete then
    /// finished, and the text of the message dialog it showed, if any.</summary>
    private readonly record struct RowDelete(string? Confirmation, bool Finished, string? Dialog = null);

    /// <summary>
    /// Clicks the DELETE button of the realized row at <paramref name="index"/> the way a click does
    /// (its own command with its own parameter), runs <paramref name="whileConfirmOpen"/>, answers
    /// DELETE in the confirmation, and waits for the delete to finish. A message dialog the delete
    /// shows is read and closed, as the user would.
    /// </summary>
    private static RowDelete ClickRowDelete(Window main, DaqifiViewModel shell, int index, Action? whileConfirmOpen = null)
    {
        Pump();
        var item = SessionList(main)?.ContainerFromIndex(index) as ListBoxItem;
        var button = item is null ? null : RowDeleteButton(item);
        if (button?.Command is not { } command || !command.CanExecute(button.CommandParameter))
        {
            return new RowDelete($"(row {index + 1} has no DELETE button that can run)", false);
        }

        command.Execute(button.CommandParameter);
        if (!PumpUntil(() => shell.ConfirmOverlay.IsOpen, ReuseStepTimeout))
        {
            return new RowDelete("(no confirmation opened)", false);
        }

        var asked = shell.ConfirmOverlay.Message;
        whileConfirmOpen?.Invoke();
        shell.ConfirmOverlay.AffirmativeCommand.Execute(null);
        string? dialogText = null;
        var running = (IAsyncRelayCommand)shell.DeleteLoggingSessionCommand;
        var finished = PumpUntil(() =>
        {
            if (main.OwnedWindows.FirstOrDefault() is { } dialog)
            {
                dialogText = string.Join(" | ", dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => !string.IsNullOrEmpty(t.Text)).Select(t => t.Text));
                dialog.Close();
            }
            return !running.IsRunning && !shell.IsLoggedDataBusy;
        }, ReuseStepTimeout);
        return new RowDelete(asked, finished, dialogText);
    }

    private static ListBox? SessionList(Window main) =>
        main.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "SessionList");

    private static Button? RowDeleteButton(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "DeleteSessionButton");

    /// <summary>
    /// Imports a small CSV log the way the Logged Data pane's IMPORT SD LOG does after its file picker:
    /// the app's own <see cref="SdCardSessionImporter"/> off the UI thread, then the persisted session
    /// added to the list. Returns that session, or null when the import persisted none.
    /// </summary>
    private static LoggingSession? ImportLog(DaqifiViewModel shell, string directory, string name, bool addToList = true)
    {
        var path = Path.Combine(directory, name + ".csv");
        var csv = new StringBuilder()
            .AppendLine("# Device: Nyquist 1")
            .AppendLine("# Serial Number: " + SeedSerial)
            .AppendLine("# Timestamp Tick Rate: 1000 Hz")
            .AppendLine("ain0_ts,ain0_val");
        for (var i = 0; i < ReuseImportSamples; i++)
        {
            csv.Append((i * 10).ToString(CultureInfo.InvariantCulture)).Append(',')
               .AppendLine((1000 + i).ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, csv.ToString());

        var contexts = (IDbContextFactory<LoggingContext>)DesktopApp.ServiceProvider
            .GetService(typeof(IDbContextFactory<LoggingContext>))!;
        var importer = new SdCardSessionImporter(contexts);

        // Never block on it: headless, the dispatcher IS this thread.
        var import = Task.Run(() => importer.ImportFromFileAsync(path, null, CancellationToken.None));
        if (!PumpUntil(() => import.IsCompleted, ReuseStepTimeout) || !import.IsCompletedSuccessfully
            || !import.Result.SessionPersisted)
        {
            return null;
        }

        if (addToList)
        {
            shell.LoggingSessions.Add(import.Result.Session);
            PumpFor(TimeSpan.FromMilliseconds(200));
        }
        return import.Result.Session;
    }

    private static bool ListIs(DaqifiViewModel shell, params LoggingSession[] expected) =>
        shell.LoggingSessions.Count == expected.Length
        && shell.LoggingSessions.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second));

    private static string ListedSessions(DaqifiViewModel shell) =>
        "[" + string.Join(", ", shell.LoggingSessions.Select(s => $"{s.ID} '{s.Name}'")) + "]";

    private static void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data source={DesktopApp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<int> StoredSessionIds()
    {
        using var connection = new SqliteConnection($"Data source={DesktopApp.DatabasePath};Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ID FROM Sessions ORDER BY ID";
        using var reader = query.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) { ids.Add(reader.GetInt32(0)); }
        return ids;
    }

    private static long StoredSampleCount(int sessionId)
    {
        using var connection = new SqliteConnection($"Data source={DesktopApp.DatabasePath};Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = $id";
        query.Parameters.AddWithValue("$id", sessionId);
        return (long)query.ExecuteScalar()!;
    }
}
