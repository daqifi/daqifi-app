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
/// <b>Repeatable.</b> A session is identified by its <c>SessionStart</c> text, which this import
/// copies unchanged, and which a rename does not touch. Sessions already present (and complete)
/// are skipped. Each new session is written as <see cref="SessionStatus.Importing"/> and marked
/// <see cref="SessionStatus.Complete"/> only once all its samples are in. If the process dies
/// part-way, startup turns the leftover into <see cref="SessionStatus.ImportFailed"/> (see
/// <c>LoggingManager.MarkAbandonedImportsFailed</c>), and the next import deletes that partial copy
/// and starts the session again.
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
    public bool HasSessionsToImport()
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
                "AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE m.SessionStart = o.SessionStart AND m.Status = 0)") > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies every complete session this user's database lacks. Reports progress from 0 to 1.
    /// </summary>
    /// <returns>The IDs of the sessions imported, in this user's database.</returns>
    /// <exception cref="NotEnoughDiskSpaceException">The disk may not hold the copy.</exception>
    /// <exception cref="InvalidOperationException">The old database cannot be imported.</exception>
    public async Task<IReadOnlyList<int>> ImportAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsCandidate())
        {
            throw new InvalidOperationException("There is no earlier database to import from.");
        }

        await Task.Yield();
        CheckDiskSpace();

        using var connection = OpenWithPreviousAttached();
        if (!PreviousSchemaIsUsable(connection))
        {
            throw new InvalidOperationException($"'{_previousDatabase}' does not have the expected sessions and samples tables.");
        }

        var complete = PreviousIsComplete(connection);
        var hasSampleCount = HasColumn(connection, "old", "Sessions", "SampleCount");
        var hasMetadata = HasTable(connection, "old", "SessionDeviceMetadata");

        RemovePartialImports(connection, complete);

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
                   "AND NOT EXISTS (SELECT 1 FROM main.Sessions m WHERE m.SessionStart = o.SessionStart) ORDER BY o.ID"))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
            }
        }

        var total = pending.Sum(s => s.Last - s.First + 1);
        long done = 0;
        var imported = new List<int>();
        progress?.Report(0);

        foreach (var (oldId, start, name, count, first, last) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The row goes in only now, so an interruption leaves at most this one session partial.
            long newId;
            using (var transaction = connection.BeginTransaction())
            {
                using var insert = Command(connection,
                    "INSERT INTO main.Sessions (SessionStart, Name, SampleCount, Status) VALUES ($start, $name, $count, $status); " +
                    "SELECT last_insert_rowid();", transaction);
                insert.Parameters.AddWithValue("$start", start);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$count", (object?)count ?? DBNull.Value);
                insert.Parameters.AddWithValue("$status", (int)SessionStatus.Importing);
                newId = Convert.ToInt64(insert.ExecuteScalar());
                transaction.Commit();
            }

            long copied = 0;
            for (var from = first; from <= last; from += BatchSpan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var to = Math.Min(last, from + BatchSpan - 1);

                using (var transaction = connection.BeginTransaction())
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

            using (var transaction = connection.BeginTransaction())
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

            imported.Add((int)newId);
        }

        progress?.Report(1);
        return imported;
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
    /// Refuses to start unless the disk has room for the whole old database file: an upper bound on
    /// what the copy can need, so an import cannot fill the disk the app logs to.
    /// </summary>
    private void CheckDiskSpace()
    {
        var needed = new FileInfo(_previousDatabase!).Length;
        var root = Path.GetPathRoot(Path.GetFullPath(_currentDatabase));
        if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < needed)
        {
            throw new NotEnoughDiskSpaceException(needed);
        }
    }

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
            DefaultTimeout = 30,
        }.ToString());

        try
        {
            connection.Open();
            using (var pragma = Command(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;"))
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

    /// <summary>Deletes copies a previous, interrupted import left behind, so they are redone.</summary>
    private static void RemovePartialImports(SqliteConnection connection, string complete)
    {
        using var transaction = connection.BeginTransaction();
        const string partial =
            "SELECT m.ID FROM main.Sessions m WHERE m.Status <> 0 AND EXISTS " +
            "(SELECT 1 FROM old.Sessions o WHERE o.SessionStart = m.SessionStart AND {0})";
        foreach (var table in new[] { "Samples", "SessionDeviceMetadata" })
        {
            using var delete = Command(connection,
                $"DELETE FROM main.{table} WHERE LoggingSessionID IN ({string.Format(partial, complete)})", transaction);
            delete.ExecuteNonQuery();
        }

        using var sessions = Command(connection,
            $"DELETE FROM main.Sessions WHERE ID IN ({string.Format(partial, complete)})", transaction);
        sessions.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Only sessions with samples count: the app drops sample-less sessions as litter on load, so
    /// offering one would only bring it back to be deleted.
    /// </summary>
    private const string HasSamples = "EXISTS (SELECT 1 FROM old.Samples s WHERE s.LoggingSessionID = o.ID)";

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
        command.CommandTimeout = 0;
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

/// <summary>The disk does not have room for an import of earlier sessions.</summary>
public sealed class NotEnoughDiskSpaceException(long bytesNeeded)
    : IOException($"Importing the earlier sessions needs up to {bytesNeeded:N0} bytes of free disk space.")
{
    /// <summary>The space the import may need.</summary>
    public long BytesNeeded { get; } = bytesNeeded;
}
