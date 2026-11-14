using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Writes a finished CSV export into a file or folder that the platform picker returned, through
/// the storage item's own stream (<see cref="IStorageFile.OpenWriteAsync"/>,
/// <see cref="IStorageFolder.CreateFileAsync"/>) and never through a file-system path (issue #587).
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
    /// Exports into a new file called <paramref name="fileName"/> in a folder that the folder picker
    /// returned. The file is created only after the CSV is complete, so a failed export creates
    /// nothing in the folder.
    /// </summary>
    /// <returns>The name of the file written — which the provider can change, for example to
    /// <c>name (1).csv</c> — or null when the export failed.</returns>
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

            IExportFile? file;
            try
            {
                file = await folder.CreateFileAsync(fileName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Export: could not create '{fileName}' in '{folder.Name}'");
                return null;
            }

            if (file is null)
            {
                _logger.Error($"Export: the folder '{folder.Name}' refused to create '{fileName}'");
                return null;
            }

            return await CopyAsync(staging, file).ConfigureAwait(false) ? file.Name : null;
        }
        finally
        {
            DeleteStaging(staging);
        }
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

        Task<IExportFile?> CreateFileAsync(string name);
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
    private sealed class StorageItemFolder(IStorageFolder folder) : IExportFolder
    {
        public string Name => folder.Name;

        public async Task<IExportFile?> CreateFileAsync(string name) =>
            await folder.CreateFileAsync(name).ConfigureAwait(false) is { } file ? new StorageItemFile(file) : null;
    }
}
