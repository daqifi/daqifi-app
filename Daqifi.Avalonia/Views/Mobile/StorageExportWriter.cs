using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Daqifi.Avalonia.Services;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Exporter;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Writes a finished CSV export into a file or folder that the platform picker returned, through
/// the storage item's own stream (<see cref="IStorageFile.OpenWriteAsync"/>) and never by requiring a
/// file-system path (issue #587). A new file in a picked folder is made by a create that never
/// replaces (<see cref="CreateNewFileRoutedAsync"/>), not by <see cref="IStorageFolder.CreateFileAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why not a path: on Android every pick is a <c>content://</c> URI, for which
/// <c>TryGetLocalPath()</c> returns null, and on iOS a pick outside the sandbox is a
/// security-scoped URL that only the storage item knows how to open. The mobile Storage tab used to
/// require a path, treated the null as "the user cancelled", and so exported nothing and said
/// nothing — while the Android picker had already created an empty destination file.
/// </para>
/// <para>
/// Why a staging file: the CSV is written completely to a private file first, and the destination
/// is opened only once that has succeeded. A database or CSV failure therefore never touches the
/// destination — the same promise <see cref="Daqifi.Desktop.Exporter.OptimizedLoggingSessionExporter"/>
/// keeps for a path (issue #236) — and the exporter itself stays the one tested CSV writer for both
/// the desktop and the phone.
/// </para>
/// <para>
/// No empty file is left on a failure. A pick on Android and on iOS creates the destination before
/// the picker returns, so a failure before the copy deletes it when it is still empty (an empty
/// file holds nothing to lose; a non-empty one is a prior file and stays). A failure during the
/// copy deletes the partial file.
/// </para>
/// </remarks>
internal sealed class StorageExportWriter
{
    /// <summary>Copy buffer, the same 1 MB the exporter writes with.</summary>
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>Prefix of the private staging file, so a human who finds an orphan knows what it is.</summary>
    private const string StagingPrefix = "daqifi-export-";

    private readonly IAppLogger _logger;
    private readonly string _stagingDirectory;

    /// <param name="logger">Receives every failure, with the destination's name.</param>
    /// <param name="stagingDirectory">Where the CSV is written before it is copied. The app's own
    /// temp directory by default (the cache directory on Android), never the destination.</param>
    internal StorageExportWriter(IAppLogger logger, string? stagingDirectory = null)
    {
        _logger = logger;
        _stagingDirectory = stagingDirectory ?? Path.GetTempPath();
    }

    /// <summary>
    /// Exports into a file that the save picker returned.
    /// </summary>
    /// <param name="destination">The picked file. Not disposed here.</param>
    /// <param name="exportToPath">Writes the CSV to the local path it is given and returns true, or
    /// returns false (nothing to export) or throws (failure).</param>
    /// <returns>True when <paramref name="destination"/> holds the complete CSV.</returns>
    internal Task<bool> WriteAsync(IStorageFile destination, Func<string, bool> exportToPath) =>
        WriteAsync(new StorageItemFile(destination), exportToPath);

    /// <summary>As <see cref="WriteAsync(IStorageFile, Func{string, bool})"/>, over the seam.</summary>
    internal async Task<bool> WriteAsync(IExportFile destination, Func<string, bool> exportToPath)
    {
        var staging = NewStagingPath();
        try
        {
            if (!await ExportToStagingAsync(exportToPath, staging, destination.Name).ConfigureAwait(false))
            {
                await DeleteIfEmptyAsync(destination).ConfigureAwait(false);
                return false;
            }

            return await CopyAsync(staging, destination).ConfigureAwait(false);
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    /// <summary>
    /// Exports into a NEW file in a folder that the folder picker returned, called
    /// <paramref name="fileName"/> or, when that name is taken, <c>name (2).csv</c> and so on. The file
    /// is created only after the CSV is complete, so a failed export creates nothing in the folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never over an existing file. <see cref="IStorageFolder.CreateFileAsync"/> truncates a file of
    /// that name (the desktop and iOS providers, and Android's too), so creating over it would
    /// destroy a previous export before this one had written a byte, and the cleanup after a failed
    /// copy would then delete it. The path exporter this replaced only ever replaced a file once the
    /// new CSV was complete; picking a free name keeps every existing file safe instead.
    /// </para>
    /// <para>
    /// A file can also appear AFTER the name check — cloud sync, another app (review of PR #603). So
    /// the create itself must not replace: <see cref="IExportFolder.CreateNewFileAsync"/> returns null
    /// for a name that is taken by then, and the writer checks the names again and takes the next
    /// free one. Because every file the writer copies into is one that call created, the cleanup
    /// after a failed copy can only ever delete this export's own output. How each platform keeps
    /// that promise, and the refusal where none can, is on <see cref="CreateNewFileRoutedAsync"/>.
    /// </para>
    /// </remarks>
    /// <returns>The name of the file written — which the provider can still change — or null when
    /// the export failed.</returns>
    internal Task<string?> WriteNewAsync(IStorageFolder folder, string fileName, Func<string, bool> exportToPath) =>
        WriteNewAsync(new StorageItemFolder(folder), fileName, exportToPath);

    /// <summary>As <see cref="WriteNewAsync(IStorageFolder, string, Func{string, bool})"/>, over the seam.</summary>
    internal async Task<string?> WriteNewAsync(IExportFolder folder, string fileName, Func<string, bool> exportToPath)
    {
        var staging = NewStagingPath();
        try
        {
            if (!await ExportToStagingAsync(exportToPath, staging, fileName).ConfigureAwait(false))
            {
                return null;
            }

            IExportFile? file = null;
            var candidate = fileName;
            try
            {
                for (var attempt = 0; file is null; attempt++)
                {
                    var freeName = attempt < MaxCreateAttempts
                        ? await FreeNameAsync(folder, fileName).ConfigureAwait(false)
                        : null;
                    if (freeName is null)
                    {
                        _logger.Error($"Export: no free name like '{fileName}' in '{folder.Name}'");
                        return null;
                    }

                    candidate = freeName;
                    // Null: a file took the name after the check. Check again; that name is now seen.
                    file = await folder.CreateNewFileAsync(candidate).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Export: could not create '{candidate}' in '{folder.Name}'");
                return null;
            }

            return await CopyAsync(staging, file).ConfigureAwait(false) ? file.Name : null;
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    /// <summary>Upper bound on the " (n)" suffixes tried, so a folder that claims every name exists
    /// fails the export instead of looping forever.</summary>
    private const int MaxNameAttempts = 1000;

    /// <summary>Upper bound on creates that find their checked name taken, so a folder whose names
    /// are taken as fast as they are checked fails the export instead of looping forever.</summary>
    private const int MaxCreateAttempts = 10;

    /// <summary>
    /// Creates an empty file at <paramref name="directory"/>/<paramref name="name"/> only if nothing
    /// has that name — one create-if-absent operation in the OS (<see cref="FileMode.CreateNew"/>,
    /// O_CREAT|O_EXCL), so there is no gap in which a file can appear and be replaced.
    /// </summary>
    /// <returns>True when this call created the file; false when the name is taken (a file or a
    /// folder); null when the path is not accessible (an iOS folder outside the sandbox, while its
    /// security scope is not active).</returns>
    internal static bool? TryCreateNewLocalFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return false;
            }

            if (ex is UnauthorizedAccessException)
            {
                return null;
            }

            throw;
        }
    }

    /// <summary>What a suffixed name starts with when not even one character of the stem fits beside
    /// the suffix. The writer does not know the session's id, which the namer's own fallback uses.</summary>
    private const string FallbackStem = "Session";

    /// <summary><paramref name="fileName"/> if nothing in the folder has that name, else the first
    /// free <c>stem (n).ext</c> from n = 2, or null. The suffix is <see cref="ExportFileNamer.WithSuffix"/>'s,
    /// the one the namer uses, so the stem gives up the bytes the suffix needs and the name still fits
    /// the file system (review of PR #685: the full stem plus " (2)" was 259 bytes, and the create threw).</summary>
    private static async Task<string?> FreeNameAsync(IExportFolder folder, string fileName)
    {
        if (!await folder.ContainsAsync(fileName).ConfigureAwait(false))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var first = 2;
        // A name the namer already suffixed ("Run (2)") continues the count — "Run (3)", not
        // "Run (2) (2)". ASCII digits only, because those are the only ones a suffix is written
        // with: `\d` matches every Unicode digit, so a take number the user typed as "(２)" was
        // read as a suffix, int.Parse then threw, and the session was not exported (review of
        // PR #685). Any other number is the user's text and stays in the stem.
        var suffixed = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) \(([0-9]{1,4})\)$");
        if (suffixed.Success)
        {
            stem = suffixed.Groups[1].Value;
            first = int.Parse(suffixed.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
        }

        for (var n = first; n < first + MaxNameAttempts; n++)
        {
            var candidate = ExportFileNamer.WithSuffix(stem, n, extension, FallbackStem) + extension;
            if (!await folder.ContainsAsync(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private string NewStagingPath() =>
        Path.Combine(_stagingDirectory, StagingPrefix + Path.GetRandomFileName());

    /// <summary>Runs the exporter off the calling thread: it reads the whole session from the
    /// database synchronously, and the caller is the UI thread.</summary>
    private async Task<bool> ExportToStagingAsync(Func<string, bool> exportToPath, string staging, string destinationName)
    {
        try
        {
            return await Task.Run(() => exportToPath(staging)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Export: could not write the CSV for '{destinationName}'");
            return false;
        }
    }

    private async Task<bool> CopyAsync(string staging, IExportFile destination)
    {
        var opened = false;
        try
        {
            await Task.Run(async () =>
            {
                await using var source = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read,
                    CopyBufferSize, FileOptions.SequentialScan);
                var target = await destination.OpenWriteAsync().ConfigureAwait(false);
                opened = true;
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, CopyBufferSize).ConfigureAwait(false);

                    // A stream that does not truncate on open would keep the tail of a longer prior
                    // file past the new end. The Android stream ("wt") truncates and cannot seek;
                    // this is for any provider that opens without truncating.
                    if (target.CanSeek && target.Length > target.Position)
                    {
                        target.SetLength(target.Position);
                    }

                    await target.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Export: could not write '{destination.Name}'");
            if (opened)
            {
                // Partly written: the old content is already gone, and a partial CSV looks complete.
                await TryDeleteAsync(destination).ConfigureAwait(false);
            }
            else
            {
                await DeleteIfEmptyAsync(destination).ConfigureAwait(false);
            }

            return false;
        }
    }

    /// <summary>Deletes the destination only when it is known to be empty — the file the picker
    /// created. An unknown size or a non-empty file is left as it is.</summary>
    private async Task DeleteIfEmptyAsync(IExportFile destination)
    {
        try
        {
            if (await destination.GetSizeAsync().ConfigureAwait(false) == 0)
            {
                await destination.DeleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Export: could not remove the empty file '{destination.Name}'");
        }
    }

    private async Task TryDeleteAsync(IExportFile destination)
    {
        try
        {
            await destination.DeleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Export: could not remove the partial file '{destination.Name}'");
        }
    }

    private void DeleteStaging(string staging)
    {
        try
        {
            File.Delete(staging);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Export: could not remove the staging file '{staging}'");
        }
    }

    /// <summary>
    /// The part of a picked file the writer uses. A seam because Avalonia 12 marks
    /// <see cref="IStorageFile"/> as not implementable by user code, so a test cannot stand in a
    /// content-URI file for it; <see cref="StorageItemFile"/> is the production adapter.
    /// </summary>
    internal interface IExportFile
    {
        string Name { get; }

        Task<Stream> OpenWriteAsync();

        /// <summary>The size in bytes, or null when the provider does not report it.</summary>
        Task<ulong?> GetSizeAsync();

        Task DeleteAsync();
    }

    /// <summary>The part of a picked folder the writer uses (see <see cref="IExportFile"/>).</summary>
    internal interface IExportFolder
    {
        string Name { get; }

        /// <summary>True when the folder already holds a file or a folder called <paramref name="name"/>.</summary>
        Task<bool> ContainsAsync(string name);

        /// <summary>Creates a file called <paramref name="name"/> that did not exist and returns it, or
        /// returns null when the name is taken — never replaces an existing file. Throws when the
        /// folder refuses to create it.</summary>
        Task<IExportFile?> CreateNewFileAsync(string name);
    }

    /// <summary>Adapts a picked <see cref="IStorageFile"/>. Does not dispose it: the caller owns it.</summary>
    private sealed class StorageItemFile(IStorageFile file) : IExportFile
    {
        public string Name => file.Name;

        public Task<Stream> OpenWriteAsync() => file.OpenWriteAsync();

        public async Task<ulong?> GetSizeAsync() => (await file.GetBasicPropertiesAsync().ConfigureAwait(false)).Size;

        public Task DeleteAsync() => file.DeleteAsync();
    }

    /// <summary>Adapts a picked <see cref="IStorageFolder"/>. Does not dispose it: the caller owns it.</summary>
    /// <remarks>
    /// What "never replaces" can and cannot mean here (review of PR #603, against the Avalonia 12.1.6
    /// source). <see cref="IStorageFolder"/> has no create-if-absent operation, and its
    /// <see cref="IStorageFolder.CreateFileAsync"/> replaces on every provider: the desktop's
    /// <c>BclStorageFolder</c> calls <c>FileInfo.Create()</c> (truncates); iOS calls
    /// <c>NSFileManager.CreateFile</c> (replaces); Android's <c>AndroidStorageFolder</c> looks the
    /// name up itself, truncates the file it finds, and only otherwise calls
    /// <c>DocumentsContract.CreateDocument</c> (which never replaces — a provider renames instead).
    /// So the writer never calls it. <see cref="CreateNewFileRoutedAsync"/> lists the three creates
    /// it uses instead, and it refuses the export where none of them applies.
    /// </remarks>
    private sealed class StorageItemFolder(IStorageFolder folder) : IExportFolder
    {
        public string Name => folder.Name;

        public async Task<bool> ContainsAsync(string name) =>
            await folder.GetFileAsync(name).ConfigureAwait(false) is not null
            || await folder.GetFolderAsync(name).ConfigureAwait(false) is not null;

        public Task<IExportFile?> CreateNewFileAsync(string name) =>
            CreateNewFileRoutedAsync(
                folder.TryGetLocalPath(),
                name,
                async () => await folder.GetFileAsync(name).ConfigureAwait(false) is { } created
                    ? new StorageItemFile(created)
                    : null,
                () => ScopedFolderAccess.Current?.TryResolveAsync(folder)
                      ?? Task.FromResult<IScopedFolder?>(null),
                () => ExclusiveFileCreator.Current?.TryCreateNewAsync(folder, name)
                      ?? Task.FromResult<ExclusiveFile?>(null));
    }

    /// <summary>
    /// Creates a file called <paramref name="name"/> in a picked folder only if nothing has that
    /// name, by the first of these that applies to the folder (review of PR #603):
    /// <list type="number">
    /// <item>The folder has a local path that the app can use (the desktop, an iOS folder inside the
    /// app sandbox): <see cref="TryCreateNewLocalFile"/>, an atomic create-if-absent.</item>
    /// <item>The folder has a local path that the sandbox refuses outside the folder's security
    /// scope (an iOS folder outside the sandbox: Avalonia holds that scope only during its own
    /// calls): the same create-if-absent, inside the scope that the platform's
    /// <see cref="ScopedFolderAccess"/> resolves. Every later write, size check and delete of that
    /// file also runs inside the scope.</item>
    /// <item>The platform has a non-replacing create for the folder (every Android
    /// <c>content://</c> pick): the platform's <see cref="ExclusiveFileCreator"/>, whose provider
    /// renames the new file instead of replacing (<c>Run (1).csv</c>).</item>
    /// </list>
    /// Otherwise the create is REFUSED. It used to fall back to
    /// <see cref="IStorageFolder.CreateFileAsync"/>, which replaces a file of that name: a file that
    /// appeared after the name check could be emptied, and the cleanup after a failed copy would
    /// then delete it. A failed export can be done again; a replaced file cannot be recovered.
    /// </summary>
    /// <param name="directory">The folder's local path (<c>TryGetLocalPath()</c>), or null.</param>
    /// <param name="name">The name to create.</param>
    /// <param name="openLocal">Opens the file that step 1 created, through the folder.</param>
    /// <param name="resolveScope">The platform's scope for the folder, or null (step 2).</param>
    /// <param name="platformCreate">The platform's non-replacing create, or null (step 3).</param>
    /// <returns>The new file, or null when the name is taken.</returns>
    /// <exception cref="IOException">The folder refused the create, or no non-replacing create
    /// applies to it.</exception>
    /// <remarks>Static, with the folder's parts passed in, so a test can drive every route:
    /// Avalonia 12 does not let user code implement <see cref="IStorageFolder"/>.</remarks>
    internal static async Task<IExportFile?> CreateNewFileRoutedAsync(
        string? directory,
        string name,
        Func<Task<IExportFile?>> openLocal,
        Func<Task<IScopedFolder?>> resolveScope,
        Func<Task<ExclusiveFile?>> platformCreate)
    {
        if (directory is not null)
        {
            switch (TryCreateNewLocalFile(directory, name))
            {
                case false:
                    return null;
                case true:
                    return await openLocal().ConfigureAwait(false)
                           ?? throw new IOException($"created '{name}' but the folder cannot open it");
            }

            // Null: the path is refused. On iOS that is a folder outside the sandbox, whose
            // security scope is not active between Avalonia's own calls.
            if (await resolveScope().ConfigureAwait(false) is { } scope)
            {
                return CreateNewInScope(scope, name);
            }
        }

        if (await platformCreate().ConfigureAwait(false) is { } created)
        {
            return new ExclusiveExportFile(created);
        }

        throw new IOException(
            $"this folder offers no way to create '{name}' that cannot replace an existing file, so the export was not written");
    }

    /// <summary>
    /// <see cref="TryCreateNewLocalFile"/> inside the folder's security scope. The file it returns
    /// enters the scope again for each later operation.
    /// </summary>
    /// <returns>The new file, or null when the name is taken.</returns>
    /// <exception cref="IOException">The path is refused even inside the scope.</exception>
    internal static IExportFile? CreateNewInScope(IScopedFolder scope, string name)
    {
        bool? created;
        using (scope.Enter())
        {
            created = TryCreateNewLocalFile(scope.LocalPath, name);
        }

        return created switch
        {
            true => new ScopedLocalExportFile(scope, name),
            false => null,
            null => throw new IOException($"the folder refused to create '{name}' inside its security scope"),
        };
    }

    /// <summary>A file that <see cref="CreateNewInScope"/> created. Each operation runs inside the
    /// folder's scope. The write stream keeps the scope until it is disposed, as Avalonia's own
    /// <c>IOSStorageFile.OpenWriteAsync</c> does.</summary>
    private sealed class ScopedLocalExportFile(IScopedFolder scope, string name) : IExportFile
    {
        private readonly string _path = Path.Combine(scope.LocalPath, name);

        public string Name => name;

        public Task<Stream> OpenWriteAsync()
        {
            var access = scope.Enter();
            try
            {
                // Open, not Create: this is the empty file that this export created. If it has gone,
                // fail; do not create it again outside the create-if-absent.
                var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.None);
                return Task.FromResult<Stream>(new ScopeHoldingStream(stream, access));
            }
            catch
            {
                access.Dispose();
                throw;
            }
        }

        public Task<ulong?> GetSizeAsync()
        {
            using (scope.Enter())
            {
                var info = new FileInfo(_path);
                return Task.FromResult(info.Exists ? (ulong?)info.Length : null);
            }
        }

        public Task DeleteAsync()
        {
            using (scope.Enter())
            {
                File.Delete(_path);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Passes every call to <paramref name="inner"/>, and stops the scope when the stream
    /// is closed.</summary>
    private sealed class ScopeHoldingStream(Stream inner, IDisposable access) : Stream
    {
        private bool _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { inner.Dispose(); }
                finally { access.Dispose(); }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { access.Dispose(); }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Adapts a file the platform's <see cref="IExclusiveFileCreator"/> created.</summary>
    private sealed class ExclusiveExportFile(ExclusiveFile file) : IExportFile
    {
        public string Name => file.Name;

        public Task<Stream> OpenWriteAsync() => file.OpenWriteAsync();

        public Task<ulong?> GetSizeAsync() => file.GetSizeAsync();

        public Task DeleteAsync() => file.DeleteAsync();
    }
}
