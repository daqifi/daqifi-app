// Downstream addition (#353) — no upstream counterpart, so no `// @port:` marker.

using Daqifi.Desktop.Common;
using Microsoft.Data.Sqlite;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Copies logged sessions from the machine-wide <c>%ProgramData%\DAQiFi</c> database into this
/// user's database, so the user gets them back with one click.
/// </summary>
/// <remarks>
/// <para>
/// Before #353 an elevated launch of this app kept its data in <c>%ProgramData%\DAQiFi</c>. It now
/// always uses the per-user folder (<see cref="AppDataPaths"/>), so someone who ran an earlier build
/// as administrator would open an emptier database and their sessions would seem to be gone. The
/// release DAQiFi Desktop (WPF) app keeps its database in the same folder, with the same schema, so
/// its sessions can be brought over the same way.
/// </para>
/// <para>
/// <b>The old database is only ever read.</b> It is attached with SQLite's <c>mode=ro</c>, so
/// SQLite itself refuses any write to it. It can be very large (over 100 GB has been seen), is
/// shared with another app, and is never moved, copied as a file, migrated or deleted.
/// </para>
/// <para>
/// <b>Repeatable.</b> A session is identified by its <c>SessionStart</c> text and its first sample,
/// which this import copies unchanged, and which a rename does not touch. The start alone is not
/// enough: two SD card sessions can start in the same second (see <c>IsCopy</c>). Sessions already
/// present (and complete) are skipped. Each new session is written as <see cref="SessionStatus.Importing"/> and marked
/// <see cref="SessionStatus.Complete"/> only once all its samples are in. If the process dies
/// part-way, startup turns the leftover into <see cref="SessionStatus.ImportFailed"/> (see
/// <c>LoggingManager.MarkAbandonedImportsFailed</c>), and the next import deletes that partial copy
/// and starts the session again. A failure this import sees itself is flagged the same way at once.
/// Only <see cref="SessionStatus.ImportFailed"/> copies are replaced: an <c>Importing</c> one may be
/// another instance's import still running. <see cref="ImportLease"/> keeps startup from failing a
/// live import, and lets only one copy of the app import earlier sessions at a time.
/// </para>
/// <para>
/// <b>Guarding against cut-short sessions.</b> The other app may still be recording. A session that
/// gains samples during its copy is dropped, and so is the newest session when the old file was
/// written in the last couple of minutes. Either is left for later: the offer is only retired once
/// nothing is left to import, so it comes back at the next launch. Not a guarantee: an older session
/// still being recorded (two recorders at once) that adds no sample during its own copy can be copied
/// short. That is a known limit, listed for a follow-up.
/// </para>
/// <para>
/// <b>Short locks.</b> Samples are copied in small batches, each in its own transaction, so the
/// app's own logging into the same database is never held up for long.
/// </para>
/// <para>
/// Once an import finishes, or the user dismisses the offer, a marker file in the per-user data
/// folder stops it from being offered again.
/// </para>
/// </remarks>
public sealed class PreviousSessionImport
{
    /// <summary>What the notification says when there is something to import.</summary>
    public const string OfferMessage = "Found sessions from an earlier DAQiFi version.";

    /// <summary>Marks the offer as finished with (imported or dismissed), in the per-user data folder.</summary>
    internal const string MarkerFileName = ".previous-sessions-import-done";

    /// <summary>How many sample IDs one batch covers. Small enough that each lock is brief.</summary>
    internal const long BatchSpan = 50_000;

    private static readonly string[] RequiredSampleColumns =
        ["LoggingSessionID", "Value", "TimestampTicks", "DeviceName", "ChannelName", "DeviceSerialNo", "Color", "Type"];

    private readonly string? _previousDatabase;
    private readonly string _currentDatabase;
    private readonly string _markerDirectory;

    /// <param name="previousDatabase">
    /// The old machine-wide database, or <c>null</c> when there is none to check (not Windows, or a
    /// <c>DAQIFI_DATA_DIR</c> override is in effect).
    /// </param>
    /// <param name="currentDatabase">This user's database, which must already exist.</param>
    /// <param name="markerDirectory">Where the "finished with" marker is kept.</param>
    internal PreviousSessionImport(string? previousDatabase, string currentDatabase, string markerDirectory)
    {
        _previousDatabase = previousDatabase;
        _currentDatabase = currentDatabase;
        _markerDirectory = markerDirectory;
    }

    /// <summary>
    /// The import for this PC. Windows only: no other platform ever used <c>%ProgramData%</c>. Inert
    /// when <c>DAQIFI_DATA_DIR</c> is set, because that override marks an isolated test or tooling
    /// run, which must never pick up the real machine's data.
    /// </summary>
    public static PreviousSessionImport ForThisPc(string currentDatabase)
    {
        string? previous = null;
        try
        {
            if (OperatingSystem.IsWindows()
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAQIFI_DATA_DIR")))
            {
                var machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(machineWide))
                {
                    previous = Path.Combine(machineWide, "DAQiFi", "DAQiFiDatabase.db");
                }
            }
        }
        catch
        {
            // Nothing to check means nothing to offer, never a failed startup.
        }

        return new PreviousSessionImport(previous, currentDatabase, AppDataPaths.DataDirectory);
    }

    /// <summary>
    /// Whether to offer the import: it has not been finished with, and the old database holds at
    /// least one complete session this user's database lacks. Reads both databases; never throws.
    /// </summary>
    public bool HasSessionsToImport() => CheckForSessionsToImport() == true;

    /// <summary>
    /// <see cref="HasSessionsToImport"/>, but <c>null</c> when the check itself failed (the old file
    /// locked or unreadable, say). Only <c>false</c> means nothing is left, so only that may retire
    /// the offer for good. Never throws.
    /// </summary>
    public bool? CheckForSessionsToImport()
    {
        try
        {
            if (!IsCandidate())
            {
                return false;
            }

            using var connection = OpenWithPreviousAttached();
            if (!PreviousSchemaIsUsable(connection))
            {
                return false;
            }

            return Scalar<long>(connection,
                $"SELECT COUNT(*) FROM old.Sessions o WHERE {PreviousIsComplete(connection)} AND {HasSamples} " +
                $"AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE m.Status = 0 AND {IsCopy})") > 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies every complete session this user's database lacks. Reports progress from 0 to 1.
    /// </summary>
    /// <param name="outcome">
    /// Filled in as the import goes, so a caller can bring the visible session list up to date even
    /// when the import fails part-way.
    /// </param>
    /// <exception cref="NotEnoughDiskSpaceException">The disk may not hold the copy.</exception>
    /// <exception cref="InvalidOperationException">The old database cannot be imported.</exception>
    public async Task ImportAsync(
        ImportOutcome outcome, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsCandidate())
        {
            throw new InvalidOperationException("There is no earlier database to import from.");
        }

        await Task.Yield();

        // One import of earlier sessions at a time across every copy of the app, or two would copy
        // the same sessions. And held as a live import throughout, so no other copy's startup
        // cleanup takes this one's Importing row for an abandoned one (#510).
        using var exclusive = ImportLease.TryHoldPreviousSessionsImport(_currentDatabase)
                              ?? throw new ImportAlreadyRunningException();
        using var live = ImportLease.HoldLive(_currentDatabase);

        using var connection = OpenWithPreviousAttached();
        if (!PreviousSchemaIsUsable(connection))
        {
            throw new InvalidOperationException($"'{_previousDatabase}' does not have the expected sessions and samples tables.");
        }

        var complete = PreviousIsComplete(connection);
        var hasSampleCount = HasColumn(connection, "old", "Sessions", "SampleCount");
        var hasMetadata = HasTable(connection, "old", "SessionDeviceMetadata");

        // Taken before any deletion: the logger or SD importer may already have read the next ID
        // from the current maximum, and deleting the highest rows below would lower it. Raised after
        // every allocation for the same reason (a dropped copy is deleted again).
        long idFloor;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            idFloor = SessionIdAllocator.NextSessionId(connection, transaction);
            transaction.Commit();
        }

        outcome.Removed.AddRange(RemoveFailedImports(connection, complete));

        // The sessions to bring over and the sample ID range each spans in the old database.
        var pending = new List<(long Id, string Start, string Name, long? Count, long First, long Last)>();
        using (var read = Command(connection,
                   $"SELECT o.ID, o.SessionStart, o.Name, {(hasSampleCount ? "o.SampleCount" : "NULL")}, " +
                   // Two subqueries, not MIN(ID), MAX(ID) together: each alone is a single index
                   // lookup, while the pair scans every sample of the session (minutes for the
                   // largest real sessions, which hold hundreds of millions of samples).
                   "(SELECT MIN(ID) FROM old.Samples WHERE LoggingSessionID = o.ID), " +
                   "(SELECT MAX(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) " +
                   $"FROM old.Sessions o WHERE {complete} AND {HasSamples} " +
                   $"AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE {IsCopy}) ORDER BY o.ID"))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
            }
        }

        // The other app may be recording into its newest session right now; a copy of it would be
        // a prefix, marked complete. Leave it for later if the file was written very recently.
        if (pending.Count > 0 && RecentlyWritten(_previousDatabase!)
            && pending[^1].Id == Scalar<long>(connection, "SELECT MAX(ID) FROM old.Sessions"))
        {
            pending.RemoveAt(pending.Count - 1);
            outcome.LeftForLater++;
        }

        var total = pending.Sum(s => s.Last - s.First + 1);
        CheckDiskSpace(connection, total);
        long done = 0;
        progress?.Report(0);

        foreach (var (oldId, start, name, count, first, last) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The row goes in only now, so an interruption leaves at most this one session partial.
            // Its ID comes from the allocator the app's other writers use, so it can never pick up
            // orphan samples or metadata an old delete left behind. It is also kept above idFloor,
            // the highest "next ID" anyone may have read: the logger and the SD card importer read
            // the maximum and insert a moment later, outside any shared transaction, so that ID may
            // already be promised to one of them.
            // BEGIN IMMEDIATE (deferred: false, which is also the library's default; spelled out
            // here and on every transaction in this class): the write lock is taken up front, where
            // the busy timeout applies, never upgraded from a read lock part-way through.
            long newId;
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                newId = Math.Max(idFloor, SessionIdAllocator.NextSessionId(connection, transaction)) + 1;
                idFloor = newId + 1;
                using var insert = Command(connection,
                    "INSERT INTO main.Sessions (ID, SessionStart, Name, SampleCount, Status) VALUES ($id, $start, $name, $count, $status)",
                    transaction);
                insert.Parameters.AddWithValue("$id", newId);
                insert.Parameters.AddWithValue("$start", start);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$count", (object?)count ?? DBNull.Value);
                insert.Parameters.AddWithValue("$status", (int)SessionStatus.Importing);
                insert.ExecuteNonQuery();
                transaction.Commit();
            }

            try
            {
                long copied = 0;
                for (var from = first; from <= last; from += BatchSpan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var to = Math.Min(last, from + BatchSpan - 1);

                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        using var copy = Command(connection,
                            "INSERT INTO main.Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
                            "SELECT $new, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type FROM old.Samples " +
                            "WHERE ID BETWEEN $from AND $to AND LoggingSessionID = $old ORDER BY ID", transaction);
                        copy.Parameters.AddWithValue("$new", newId);
                        copy.Parameters.AddWithValue("$old", oldId);
                        copy.Parameters.AddWithValue("$from", from);
                        copy.Parameters.AddWithValue("$to", to);
                        copied += copy.ExecuteNonQuery();
                        transaction.Commit();
                    }

                    done += to - from + 1;
                    progress?.Report((double)done / total);
                }

                // Samples arrived during the copy: the session is still being recorded. Drop the
                // copy rather than keep a prefix marked complete; it is offered again later.
                using (var grown = Command(connection, "SELECT MAX(ID) FROM old.Samples WHERE LoggingSessionID = $old"))
                {
                    grown.Parameters.AddWithValue("$old", oldId);
                    if (Convert.ToInt64(grown.ExecuteScalar()) > last)
                    {
                        // Flagged failed first, so a crash during the deletion is cleaned up next time.
                        using (var flag = Command(connection, "UPDATE main.Sessions SET Status = $failed WHERE ID = $new"))
                        {
                            flag.Parameters.AddWithValue("$failed", (int)SessionStatus.ImportFailed);
                            flag.Parameters.AddWithValue("$new", newId);
                            flag.ExecuteNonQuery();
                        }

                        DeleteSessions(connection, $"{newId}");
                        outcome.LeftForLater++;
                        continue;
                    }
                }

                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    if (hasMetadata)
                    {
                        using var metadata = Command(connection,
                            "INSERT OR IGNORE INTO main.SessionDeviceMetadata (LoggingSessionID, DeviceSerialNo, DeviceName, SamplingFrequencyHz) " +
                            "SELECT $new, DeviceSerialNo, DeviceName, SamplingFrequencyHz FROM old.SessionDeviceMetadata WHERE LoggingSessionID = $old",
                            transaction);
                        metadata.Parameters.AddWithValue("$new", newId);
                        metadata.Parameters.AddWithValue("$old", oldId);
                        metadata.ExecuteNonQuery();
                    }

                    // The rows actually copied, so the session list never has to count samples on load.
                    using var finish = Command(connection,
                        "UPDATE main.Sessions SET Status = $complete, SampleCount = $count WHERE ID = $new", transaction);
                    finish.Parameters.AddWithValue("$complete", (int)SessionStatus.Complete);
                    finish.Parameters.AddWithValue("$count", copied);
                    finish.Parameters.AddWithValue("$new", newId);
                    finish.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            catch
            {
                // Flag the partial copy now, as startup would after a crash, so the next Import
                // (in this run or a later one) replaces it. Best effort: if even this fails,
                // startup still flags it.
                try
                {
                    using var failed = Command(connection, "UPDATE main.Sessions SET Status = $failed WHERE ID = $new AND Status = $importing");
                    failed.Parameters.AddWithValue("$failed", (int)SessionStatus.ImportFailed);
                    failed.Parameters.AddWithValue("$importing", (int)SessionStatus.Importing);
                    failed.Parameters.AddWithValue("$new", newId);
                    failed.ExecuteNonQuery();
                }
                catch
                {
                    // See above.
                }

                throw;
            }

            outcome.Imported.Add((int)newId);
        }

        progress?.Report(1);
    }

    /// <summary>Records that the offer is finished with, so it is not made again. Best effort.</summary>
    public void MarkDone()
    {
        try
        {
            Directory.CreateDirectory(_markerDirectory);
            File.WriteAllText(Path.Combine(_markerDirectory, MarkerFileName), string.Empty);
        }
        catch
        {
            // Offered again at the next launch; harmless.
        }
    }

    private bool IsCandidate() =>
        _previousDatabase is not null
        && !File.Exists(Path.Combine(_markerDirectory, MarkerFileName))
        && File.Exists(_previousDatabase)
        && File.Exists(_currentDatabase)
        && !SameFile(_previousDatabase, _currentDatabase);

    /// <summary>
    /// Refuses to start unless the disk has room for the sessions about to be copied, so an import
    /// cannot fill the disk the app logs to.
    /// </summary>
    /// <param name="connection">The connection with the old database attached.</param>
    /// <param name="idsToCopy">The sample IDs the pending sessions span in the old database.</param>
    private void CheckDiskSpace(SqliteConnection connection, long idsToCopy)
    {
        var needed = BytesNeeded(connection, idsToCopy);
        if (needed > 0 && FreeSpace(_currentDatabase) < needed)
        {
            throw new NotEnoughDiskSpaceException(needed);
        }
    }

    /// <summary>
    /// The old file's size scaled by the share of its sample IDs the import copies. Samples are
    /// nearly all of the file, so this is the size of what is copied, indexes included. It errs
    /// high: a session's ID range can include other sessions' samples recorded at the same time.
    /// Counting rows instead would read every sample of a file that can hold over a billion, while
    /// the ID bounds are index lookups. A retry needs less, because sessions already copied are no
    /// longer pending.
    /// </summary>
    private long BytesNeeded(SqliteConnection connection, long idsToCopy)
    {
        if (idsToCopy <= 0)
        {
            return 0;
        }

        var span = Scalar<long>(connection,
            "SELECT COALESCE((SELECT MAX(ID) FROM old.Samples) - (SELECT MIN(ID) FROM old.Samples) + 1, 0)");
        var fileLength = new FileInfo(_previousDatabase!).Length;
        return span <= 0 ? fileLength : (long)Math.Ceiling(fileLength * Math.Min(1.0, (double)idsToCopy / span));
    }

    /// <summary>Free bytes on the drive holding a path. Replaceable in tests.</summary>
    internal Func<string, long> FreeSpace { get; init; } = path =>
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
    };

    /// <summary>
    /// Opens this user's database (never creating it) with the old one attached read-only.
    /// </summary>
    internal SqliteConnection OpenWithPreviousAttached()
    {
        // URI filenames: the main connection must be opened with one for SQLite to honour the
        // `mode=ro` URI in the ATTACH below.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FileUri(_currentDatabase),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            // Every command inherits this. In Microsoft.Data.Sqlite it bounds only how long a
            // command keeps retrying a locked database, never how long a query may run, so the
            // longest copies are unaffected while a lock held elsewhere fails the import (to be
            // retried) instead of leaving it stuck.
            DefaultTimeout = LockTimeoutSeconds,
        }.ToString());

        try
        {
            connection.Open();
            using (var pragma = Command(connection, $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {LockTimeoutSeconds * 1000};"))
            {
                pragma.ExecuteNonQuery();
            }

            using var attach = Command(connection, "ATTACH DATABASE $uri AS old");
            attach.Parameters.AddWithValue("$uri", FileUri(_previousDatabase!) + "?mode=ro");
            attach.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes copies an earlier, interrupted import left behind (flagged
    /// <see cref="SessionStatus.ImportFailed"/>), so they are redone. Rows still marked
    /// <see cref="SessionStatus.Importing"/> may belong to an import running in another copy of the
    /// app and are left alone. Startup turns them into failed ones only when no import holds
    /// <see cref="ImportLease"/>, so a failed row here is never a live import's. A failed SD card
    /// import of a different session from the same second is not a copy (<c>IsCopy</c>), so it and
    /// its samples are kept.
    /// </summary>
    /// <returns>The IDs deleted.</returns>
    private static List<int> RemoveFailedImports(SqliteConnection connection, string complete)
    {
        var ids = new List<int>();
        using (var find = Command(connection,
                   $"SELECT m.ID FROM main.Sessions m WHERE m.Status = {(int)SessionStatus.ImportFailed} AND EXISTS " +
                   $"(SELECT 1 FROM old.Sessions o WHERE {complete} AND {HasSamples} AND {IsCopy})"))
        using (var reader = find.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetInt32(0));
            }
        }

        if (ids.Count > 0)
        {
            DeleteSessions(connection, string.Join(",", ids));
        }

        return ids;
    }

    /// <summary>
    /// Deletes sessions of this user's database, with their samples and metadata. Samples go in
    /// batches, each its own transaction, for the same reason the copy is batched: a partial copy can
    /// hold hundreds of millions of samples, and one transaction over them would hold the write lock
    /// (and grow the journal) for minutes. The session rows go last, so until then they stay marked
    /// failed and a crash part-way is finished off by the next import.
    /// </summary>
    private static void DeleteSessions(SqliteConnection connection, string idList)
    {
        int deleted;
        do
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var batch = Command(connection,
                $"DELETE FROM main.Samples WHERE ID IN (SELECT ID FROM main.Samples WHERE LoggingSessionID IN ({idList}) LIMIT {BatchSpan})",
                transaction);
            deleted = batch.ExecuteNonQuery();
            transaction.Commit();
        }
        while (deleted > 0);

        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using var metadata = Command(connection, $"DELETE FROM main.SessionDeviceMetadata WHERE LoggingSessionID IN ({idList})", transaction);
            metadata.ExecuteNonQuery();
            using var sessions = Command(connection, $"DELETE FROM main.Sessions WHERE ID IN ({idList})", transaction);
            sessions.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>How long to wait for a database another connection has locked. Shorter in tests.</summary>
    internal int LockTimeoutSeconds { get; init; } = 30;

    /// <summary>How recently the old file must have been written to suspect a recording in progress.</summary>
    internal TimeSpan RecordingWindow { get; init; } = TimeSpan.FromMinutes(2);

    private bool RecentlyWritten(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RecordingWindow;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Only sessions with samples count: the app drops sample-less sessions as litter on load, so
    /// offering one would only bring it back to be deleted.
    /// </summary>
    private const string HasSamples = "EXISTS (SELECT 1 FROM old.Samples s WHERE s.LoggingSessionID = o.ID)";

    /// <summary>
    /// The condition that session <c>m</c> of this user's database is a copy of old session <c>o</c>.
    /// </summary>
    /// <remarks>
    /// The start time alone is not enough (#511). An SD card session starts on a whole second (the
    /// time in the log's file name), so two different card logs can share one, for example from two
    /// devices started together. A copy also holds the old session's samples unchanged and in the
    /// same order, so its first sample (lowest ID) is the old session's first sample. Two sessions
    /// with the same start are different when those first samples differ in device, channel,
    /// timestamp or value. A copy that has no sample yet (stopped before its first batch) is matched
    /// on its start alone: it holds no data, so deleting it loses nothing, and skipping for it only
    /// delays the session to the next import. Each first sample is one index lookup.
    /// </remarks>
    private const string IsCopy =
        "m.SessionStart = o.SessionStart AND NOT EXISTS (SELECT 1 FROM main.Samples ms, old.Samples os " +
        "WHERE ms.ID = (SELECT MIN(ID) FROM main.Samples WHERE LoggingSessionID = m.ID) " +
        "AND os.ID = (SELECT MIN(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) " +
        "AND NOT (ms.DeviceSerialNo IS os.DeviceSerialNo AND ms.ChannelName IS os.ChannelName " +
        "AND ms.TimestampTicks IS os.TimestampTicks AND ms.Value IS os.Value))";

    private static bool PreviousSchemaIsUsable(SqliteConnection connection) =>
        HasColumn(connection, "old", "Sessions", "ID")
        && HasColumn(connection, "old", "Sessions", "SessionStart")
        && HasColumn(connection, "old", "Sessions", "Name")
        && RequiredSampleColumns.All(column => HasColumn(connection, "old", "Samples", column));

    /// <summary>
    /// The condition for an old session worth importing. A database from before the status column
    /// holds only finished sessions; one with it may also hold an unfinished SD card import.
    /// </summary>
    private static string PreviousIsComplete(SqliteConnection connection) =>
        HasColumn(connection, "old", "Sessions", "Status") ? "o.Status = 0" : "1 = 1";

    private static bool HasTable(SqliteConnection connection, string schema, string table) =>
        Scalar<long>(connection, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = '{table}'") > 0;

    private static bool HasColumn(SqliteConnection connection, string schema, string table, string column) =>
        Scalar<long>(connection, $"SELECT COUNT(*) FROM pragma_table_info('{table}', '{schema}') WHERE name = '{column}'") > 0;

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static bool SameFile(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>What an import of earlier sessions did, filled in as it goes.</summary>
public sealed class ImportOutcome
{
    /// <summary>Sessions now in this user's database, complete.</summary>
    public List<int> Imported { get; } = [];

    /// <summary>Partial copies from an earlier, interrupted import that were deleted to be redone.</summary>
    public List<int> Removed { get; } = [];

    /// <summary>Sessions not imported because they may still be recording; offered again later.</summary>
    public int LeftForLater { get; set; }
}

/// <summary>Another copy of the app is already importing the earlier sessions.</summary>
public sealed class ImportAlreadyRunningException()
    : IOException("Another copy of the app is already importing the earlier sessions.");

/// <summary>The disk does not have room for an import of earlier sessions.</summary>
public sealed class NotEnoughDiskSpaceException(long bytesNeeded)
    : IOException($"Importing the earlier sessions needs up to {bytesNeeded:N0} bytes of free disk space.")
{
    /// <summary>The space the import may need.</summary>
    public long BytesNeeded { get; } = bytesNeeded;
}
