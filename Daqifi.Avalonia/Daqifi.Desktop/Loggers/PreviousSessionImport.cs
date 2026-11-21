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
/// <b>Repeatable, and nothing is deleted.</b> Each copy records the ID of its old session's first
/// sample (<c>PreviousFirstSampleId</c>), which a rename does not touch. A session that only looks
/// like a copy (an SD card log of another board from the same second, a recording in a new database
/// at the same path) is never taken for one (see <c>IsCopy</c>). Sessions already present are
/// skipped. When the old database can give a sample ID twice (no AUTOINCREMENT), no copy is proved,
/// so nothing is skipped, and an import ends the offer itself (see <c>ProvedCopy</c>). A new copy is
/// written as <see cref="SessionStatus.Importing"/>, and each batch records the last old sample in
/// it (<c>PreviousLastSampleId</c>) with its samples. The copy is marked
/// <see cref="SessionStatus.Complete"/> only once all its samples are in. If the process dies
/// part-way, startup turns the leftover into <see cref="SessionStatus.ImportFailed"/> (see
/// <c>LoggingManager.MarkAbandonedImportsFailed</c>), and the next import continues it from its last
/// sample ID. A failure this import sees itself is flagged the same way at once. The import never
/// deletes a session or a sample: a copy that does not continue from its old session (the old
/// database was replaced, say) is left as it is, and the session is copied again beside it. An
/// <c>Importing</c> copy may be another instance's import still running and is left alone.
/// <see cref="ImportLease"/> keeps startup from failing a live import, and lets only one copy of the
/// app import earlier sessions at a time.
/// </para>
/// <para>
/// <b>Sessions still being recorded.</b> The other app may still be recording, and two copies of it
/// can record at once, so any session may be recording, not only the newest (#514). Nothing in the
/// old database proves that a session has ended (a second copy of the app writes the sample count at
/// start-up, for a recording session too), and no check at import time can see a sample that comes
/// later. So the import never decides that a session has ended. Each copy records the ID of the last
/// old sample it holds (<c>PreviousLastSampleId</c>). Sample IDs only grow (AUTOINCREMENT), so a
/// copy is behind exactly when its old session has a sample with a higher ID. The check for the
/// offer and every import compare this again (see <c>CopiesToContinue</c>), also after the offer of new
/// sessions is retired, and an import copies the missing samples into the copy. A session that is
/// still being recorded is copied as far as it goes, and a later import copies the rest. A copy is
/// continued only from the same recording: its old session has the same first sample ID, start and
/// first sample, and the old sample at the copy's last sample ID is the copy's last sample (see
/// <c>Continues</c>).
/// Sample IDs are unique only within one database file, so they alone cannot show this.
/// </para>
/// <para>
/// <b>Short locks.</b> Samples are copied in small batches, each in its own transaction, so the
/// app's own logging into the same database is never held up for long.
/// </para>
/// <para>
/// Once an import finishes, or the user dismisses the offer, a marker file in the per-user data
/// folder stops new sessions from being offered again. Samples that a copied session gets later are
/// still offered.
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
    /// Whether to offer the import: a copy made earlier is behind its old session, or the offer has
    /// not been finished with and the old database holds at least one complete session this user's
    /// database lacks. Reads both databases; never throws.
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

            // A copy that is behind its old session, or stopped part-way, is offered even once the
            // offer is retired: the user chose to import that session, and the copy is short of it.
            if (SampleIdsAreNeverReused(connection)
                && Scalar<long>(connection, $"SELECT COUNT(*) {CopiesToContinue(PreviousIsComplete(connection))}") > 0)
            {
                return true;
            }

            return !OfferRetired && Scalar<long>(connection,
                $"SELECT COUNT(*) FROM old.Sessions o WHERE {PreviousIsComplete(connection)} AND {HasSamples} " +
                $"AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE m.Status = 0 AND {ProvedCopy(connection)})") > 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies every complete session this user's database lacks (none once the offer is retired),
    /// and the samples each copy made earlier is behind its old session. Reports progress from 0 to 1.
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
        var copyOf = ProvedCopy(connection);
        var hasMetadata = HasTable(connection, "old", "SessionDeviceMetadata");

        // Taken first: the logger or SD importer may already have read the next ID from the current
        // maximum. Raised after every allocation for the same reason.
        long idFloor;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            idFloor = SessionIdAllocator.NextSessionId(connection, transaction);
            transaction.Commit();
        }

        // The sessions to bring over and the sample ID range each spans in the old database. None
        // once the offer is retired: then only the copies made earlier are continued. A session that
        // has a copy is skipped, unless that copy failed and does not continue from it (see
        // CopiesToContinue). Such a copy is left as it is, and the session is copied again beside it.
        var pending = new List<(long Id, string Start, string Name, long First, long Last)>();
        if (!OfferRetired)
        {
            using (var read = Command(connection,
                       "SELECT o.ID, o.SessionStart, o.Name, " +
                       // Two subqueries, not MIN(ID), MAX(ID) together: each alone is a single index
                       // lookup, while the pair scans every sample of the session (minutes for the
                       // largest real sessions, which hold hundreds of millions of samples).
                       "(SELECT MIN(ID) FROM old.Samples WHERE LoggingSessionID = o.ID), " +
                       "(SELECT MAX(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) " +
                       $"FROM old.Sessions o WHERE {complete} AND {HasSamples} " +
                       $"AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE {copyOf} AND (m.Status <> {(int)SessionStatus.ImportFailed} " +
                       $"OR {Continues("m.ID", "o.ID", "m.PreviousLastSampleId")})) ORDER BY o.ID"))
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
                }
            }
        }

        // The copies made earlier to continue: behind their old session, or stopped part-way. With
        // the last old sample each holds, the old session's last sample now, and the samples in
        // between. Those are counted (an index range): other sessions can hold most of the IDs.
        var unfinished = new List<(long CopyId, long OldId, long Cursor, long Last, long Missing)>();
        if (SampleIdsAreNeverReused(connection))
        {
            using var read = Command(connection,
                "SELECT m.ID, x.Id, m.PreviousLastSampleId, x.LastId, " +
                $"(SELECT COUNT(*) FROM old.Samples WHERE LoggingSessionID = x.Id AND ID > m.PreviousLastSampleId) {CopiesToContinue(complete)} ORDER BY m.ID");
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                unfinished.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        var total = pending.Sum(s => s.Last - s.First + 1) + unfinished.Sum(c => c.Missing);
        CheckDiskSpace(connection, total);
        long done = 0;
        progress?.Report(0);

        // The copy being written for a new session, so a failure can flag it.
        long writing = 0;

        foreach (var (oldId, start, name, first, last) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writing = 0;
            try
            {
                var (copyId, at) = CopySamples(0, oldId, first - 1, last, (start, name, first));
                if (copyId != 0 && MarkComplete(copyId, at))
                {
                    outcome.Imported.Add((int)copyId);
                }
            }
            catch
            {
                // Flag the partial copy now, as startup would after a crash. The next import goes
                // on from its last sample ID. Best effort: if even this fails, startup still flags it.
                try
                {
                    using var failed = Command(connection, "UPDATE main.Sessions SET Status = $failed WHERE ID = $copy AND Status = $importing");
                    failed.Parameters.AddWithValue("$failed", (int)SessionStatus.ImportFailed);
                    failed.Parameters.AddWithValue("$importing", (int)SessionStatus.Importing);
                    failed.Parameters.AddWithValue("$copy", writing);
                    failed.ExecuteNonQuery();
                }
                catch
                {
                    // See above.
                }

                throw;
            }
        }

        foreach (var (copyId, oldId, cursor, last, _) in unfinished)
        {
            var (continued, at) = CopySamples(copyId, oldId, cursor, last, null);
            if (continued != 0 && MarkComplete(copyId, at))
            {
                outcome.Imported.Add((int)copyId);
            }
        }

        // Nothing here can be proved a copy, so a later check would offer every session again and
        // a later import would copy each one again: the import ends the offer itself. No copy can be
        // compared with its old session either, so samples a session gets later are not offered.
        if (!SampleIdsAreNeverReused(connection))
        {
            MarkDone();
        }

        progress?.Report(1);
        return;

        // Copies the old session's samples with IDs after `at` up to `last` into the copy, in
        // batches. Each batch, in one transaction, adds its samples, the count and the devices, and
        // moves the copy's last sample ID to the last sample it adds: a crash leaves them in step,
        // and the next import goes on from there. A new copy (copy 0) gets its row in the first
        // batch's transaction, so it never exists without its first samples. Each later batch first
        // checks that the copy still has the last sample ID it had, and still continues from the old
        // session (see Continues): the user can delete the copy meanwhile, and a new session can get
        // its ID. Returns the copy and its last sample ID, or copy 0 when that check fails: the copy
        // is then left as it is.
        (long Copy, long At) CopySamples(long copy, long oldId, long at, long last, (string Start, string Name, long First)? newSession)
        {
            for (var from = at + 1; from <= last; from += BatchSpan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var to = Math.Min(last, from + BatchSpan - 1);

                long? next;
                using (var find = Command(connection, "SELECT MAX(ID) FROM old.Samples WHERE LoggingSessionID = $old AND ID BETWEEN $from AND $to"))
                {
                    find.Parameters.AddWithValue("$old", oldId);
                    find.Parameters.AddWithValue("$from", from);
                    find.Parameters.AddWithValue("$to", to);
                    next = find.ExecuteScalar() is long id ? id : null;
                }

                long copied = 0;
                if (next is not null)
                {
                    // BEGIN IMMEDIATE (deferred: false, which is also the library's default; spelled
                    // out on every transaction in this class): the write lock is taken up front, where
                    // the busy timeout applies, never upgraded from a read lock part-way through.
                    using var transaction = connection.BeginTransaction(deferred: false);
                    if (copy == 0)
                    {
                        // Its ID comes from the allocator the app's other writers use, so it can never
                        // pick up orphan samples or metadata an old delete left behind. It is also kept
                        // above idFloor, the highest "next ID" anyone may have read: the logger and the
                        // SD card importer read the maximum and insert a moment later, outside any
                        // shared transaction, so that ID may already be promised to one of them.
                        copy = Math.Max(idFloor, SessionIdAllocator.NextSessionId(connection, transaction)) + 1;
                        idFloor = copy + 1;
                        using var insert = Command(connection,
                            "INSERT INTO main.Sessions (ID, SessionStart, Name, SampleCount, Status, PreviousFirstSampleId, PreviousLastSampleId) " +
                            "VALUES ($id, $start, $name, 0, $status, $first, $next)",
                            transaction);
                        insert.Parameters.AddWithValue("$id", copy);
                        insert.Parameters.AddWithValue("$start", newSession!.Value.Start);
                        insert.Parameters.AddWithValue("$name", newSession.Value.Name);
                        insert.Parameters.AddWithValue("$status", (int)SessionStatus.Importing);
                        insert.Parameters.AddWithValue("$first", newSession.Value.First);
                        insert.Parameters.AddWithValue("$next", next.Value);
                        insert.ExecuteNonQuery();
                        writing = copy;
                    }
                    else
                    {
                        using var move = Command(connection,
                            "UPDATE main.Sessions SET PreviousLastSampleId = $next WHERE ID = $copy AND PreviousLastSampleId = $at " +
                            $"AND {Continues("$copy", "$old", "$at")}",
                            transaction);
                        move.Parameters.AddWithValue("$next", next.Value);
                        move.Parameters.AddWithValue("$copy", copy);
                        move.Parameters.AddWithValue("$old", oldId);
                        move.Parameters.AddWithValue("$at", at);
                        if (move.ExecuteNonQuery() == 0)
                        {
                            return (0, at);
                        }
                    }

                    using (var insert = Command(connection,
                               "INSERT INTO main.Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
                               "SELECT $copy, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type FROM old.Samples " +
                               "WHERE ID BETWEEN $from AND $next AND LoggingSessionID = $old ORDER BY ID", transaction))
                    {
                        insert.Parameters.AddWithValue("$copy", copy);
                        insert.Parameters.AddWithValue("$old", oldId);
                        insert.Parameters.AddWithValue("$from", from);
                        insert.Parameters.AddWithValue("$next", next.Value);
                        copied = insert.ExecuteNonQuery();
                    }

                    // The rows actually copied, so the session list never has to count samples on load.
                    using (var count = Command(connection,
                               "UPDATE main.Sessions SET SampleCount = COALESCE(SampleCount, 0) + $copied WHERE ID = $copy", transaction))
                    {
                        count.Parameters.AddWithValue("$copied", copied);
                        count.Parameters.AddWithValue("$copy", copy);
                        count.ExecuteNonQuery();
                    }

                    // The devices, also one the recording picked up after an earlier batch.
                    if (hasMetadata)
                    {
                        using var metadata = Command(connection,
                            "INSERT OR IGNORE INTO main.SessionDeviceMetadata (LoggingSessionID, DeviceSerialNo, DeviceName, SamplingFrequencyHz) " +
                            "SELECT $copy, DeviceSerialNo, DeviceName, SamplingFrequencyHz FROM old.SessionDeviceMetadata WHERE LoggingSessionID = $old",
                            transaction);
                        metadata.Parameters.AddWithValue("$copy", copy);
                        metadata.Parameters.AddWithValue("$old", oldId);
                        metadata.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    at = next.Value;
                }

                // A new session is sized by its ID span, a copy to continue by its rows.
                done += newSession is null ? copied : to - from + 1;
                progress?.Report((double)done / total);
            }

            return (copy, at);
        }

        // Every old sample of the session up to the last one listed is in the copy now. Only if the
        // copy still has that last sample ID: else it is not this import's copy any more.
        bool MarkComplete(long copy, long at)
        {
            using var finish = Command(connection,
                "UPDATE main.Sessions SET Status = $complete WHERE ID = $copy AND PreviousLastSampleId = $at");
            finish.Parameters.AddWithValue("$complete", (int)SessionStatus.Complete);
            finish.Parameters.AddWithValue("$copy", copy);
            finish.Parameters.AddWithValue("$at", at);
            return finish.ExecuteNonQuery() == 1;
        }
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

    /// <summary>
    /// The offer of new sessions is finished with (imported or dismissed). Copies made earlier are
    /// still brought up to date.
    /// </summary>
    private bool OfferRetired => File.Exists(Path.Combine(_markerDirectory, MarkerFileName));

    private bool IsCandidate() =>
        _previousDatabase is not null
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

    /// <summary>How long to wait for a database another connection has locked. Shorter in tests.</summary>
    internal int LockTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// The copies to continue: copies that continue from their old session (see
    /// <see cref="Continues"/>) and are behind it (it has a sample with a higher ID than the last one
    /// the copy holds) or stopped part-way (<see cref="SessionStatus.ImportFailed"/>). A <c>FROM</c>
    /// clause with the copy as <c>m</c>, and its old session as <c>x</c> with <c>x.Id</c> and
    /// <c>x.LastId</c>. A copy another import may still be writing (<see cref="SessionStatus.Importing"/>)
    /// is left alone. Only for an old database that never gives a sample ID twice (see
    /// <see cref="SampleIdsAreNeverReused"/>): there a new sample always gets a higher ID, so every
    /// old sample at or below a copy's last sample ID is in the copy. A copy with no last sample ID
    /// (none made by a release) is never continued.
    /// </summary>
    /// <remarks>
    /// The first and last sample of each old session are two index lookups, and a copy is matched to
    /// its old session as <c>IsCopy</c> does: the first sample ID, the start, and the first sample.
    /// </remarks>
    private static string CopiesToContinue(string complete) =>
        "FROM (SELECT o.ID AS Id, o.SessionStart AS Start, (SELECT MIN(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) AS FirstId, " +
        $"(SELECT MAX(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) AS LastId FROM old.Sessions o WHERE {complete}) x " +
        "JOIN main.Sessions m ON m.PreviousFirstSampleId = x.FirstId AND m.SessionStart = x.Start " +
        $"WHERE m.Status <> {(int)SessionStatus.Importing} " +
        $"AND (m.PreviousLastSampleId < x.LastId OR m.Status = {(int)SessionStatus.ImportFailed}) " +
        $"AND {SameSample("x.FirstId", "x.Id", "m.ID", "MIN")} AND {Continues("m.ID", "x.Id", "m.PreviousLastSampleId")}";

    /// <summary>
    /// The condition that copy <paramref name="copy"/> continues from old session
    /// <paramref name="old"/> where it ends: the old sample at the copy's last sample ID
    /// (<paramref name="cursor"/>) belongs to that session and is the copy's last sample.
    /// </summary>
    /// <remarks>
    /// Sample IDs are unique only within one database file. A new database at the same path (a
    /// reinstall, a restored backup) starts them again at 1, and this user's database can give a
    /// deleted copy's ID to a new session. Either way the IDs still match, but the samples do not.
    /// When this does not hold, the copy is left as it is.
    /// </remarks>
    private static string Continues(string copy, string old, string cursor) => SameSample(cursor, old, copy, "MAX");

    /// <summary>
    /// The condition that old sample <paramref name="oldSample"/> belongs to old session
    /// <paramref name="old"/> and is the same as the first (<c>MIN</c>) or last (<c>MAX</c>) sample of
    /// copy <paramref name="copy"/>: the same time, value, channel and board serial number. The board
    /// serial number is what the old schema keeps that is unique to a board. Three index lookups.
    /// </summary>
    private static string SameSample(string oldSample, string old, string copy, string end) =>
        "EXISTS (SELECT 1 FROM old.Samples s JOIN main.Samples c " +
        $"ON c.ID = (SELECT {end}(ID) FROM main.Samples WHERE LoggingSessionID = {copy}) " +
        $"WHERE s.ID = {oldSample} AND s.LoggingSessionID = {old} AND s.TimestampTicks = c.TimestampTicks " +
        "AND s.Value IS c.Value AND s.ChannelName = c.ChannelName AND s.DeviceSerialNo = c.DeviceSerialNo)";

    /// <summary>
    /// Only sessions with samples count: the app drops sample-less sessions as litter on load, so
    /// offering one would only bring it back to be deleted.
    /// </summary>
    private const string HasSamples = "EXISTS (SELECT 1 FROM old.Samples s WHERE s.LoggingSessionID = o.ID)";

    /// <summary>
    /// The condition that session <c>m</c> of this user's database is this import's copy of old
    /// session <c>o</c>.
    /// </summary>
    /// <remarks>
    /// Each copy records the ID of its old session's first sample (<c>PreviousFirstSampleId</c>),
    /// and nothing else writes that column. Its samples cannot get the same IDs again when the
    /// samples table has AUTOINCREMENT (see <see cref="SampleIdsAreNeverReused"/>), but only within
    /// one database file: a new database at the same path (a reinstall, a restored backup) starts
    /// them again at 1. The start is not enough either: an SD card session starts on a whole second
    /// (the time in the log's file name), so card logs of two boards can share it (#511). So the
    /// copy's first sample must also be the old first sample: the same time, value, channel and board
    /// serial number. A copy always keeps its first sample: the import never deletes, and the first
    /// batch writes the copy's row together with its first samples. The import writes the old start
    /// unchanged, and nothing rewrites it. Each part is an index lookup.
    /// </remarks>
    private static readonly string IsCopy =
        "m.PreviousFirstSampleId = (SELECT MIN(ID) FROM old.Samples WHERE LoggingSessionID = o.ID) AND m.SessionStart = o.SessionStart " +
        $"AND {SameSample("m.PreviousFirstSampleId", "o.ID", "m.ID", "MIN")}";

    /// <summary>
    /// The one rule that decides if session <c>m</c> is proved to be this import's copy of old
    /// session <c>o</c>, for both uses: the offer and the list of sessions to copy. It is
    /// <c>IsCopy</c> when the old samples table never gives an ID twice, and false otherwise: there a
    /// recreated old session can have its predecessor's first sample ID, so a copy proves nothing. The
    /// import then skips nothing; a duplicate copy is better than a lost session.
    /// </summary>
    private static string ProvedCopy(SqliteConnection connection) =>
        SampleIdsAreNeverReused(connection) ? IsCopy : "0 = 1";

    /// <summary>
    /// Whether the old samples table has AUTOINCREMENT, so SQLite never gives a sample ID twice,
    /// even after the highest samples are deleted. Every database the earlier version's migrations
    /// made has it. Without it, a recreated old session can have its predecessor's first sample ID,
    /// so <c>IsCopy</c> cannot prove a copy's source.
    /// </summary>
    private static bool SampleIdsAreNeverReused(SqliteConnection connection) =>
        Scalar<long>(connection,
            "SELECT COUNT(*) FROM old.sqlite_master WHERE type = 'table' AND name = 'Samples' " +
            "AND sql LIKE '%AUTOINCREMENT%'") > 0;

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
    /// <summary>
    /// Sessions now in this user's database, complete: new copies, and copies made earlier that got
    /// the samples their old session had added since.
    /// </summary>
    public List<int> Imported { get; } = [];

    /// <summary>
    /// Sessions this import deleted. Always empty: the import never deletes (#514). Kept so that
    /// callers can still apply it.
    /// </summary>
    public List<int> Removed { get; } = [];
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
