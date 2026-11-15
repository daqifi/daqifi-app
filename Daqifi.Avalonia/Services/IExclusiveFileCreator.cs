using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace Daqifi.Avalonia.Services;

/// <summary>
/// Platform hook that creates a NEW file in a picked folder and never replaces one that is already
/// there (review of PR #603).
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12.1.6's <see cref="IStorageFolder.CreateFileAsync"/> replaces a file of the same name
/// on every platform. On Android, <c>AndroidStorageFolder.CreateFileAsync</c> looks the name up
/// itself and truncates the file it finds; only when it finds nothing does it call
/// <c>DocumentsContract.CreateDocument</c>. So an export that checked that a name was free, and then
/// created it, could still empty a file that appeared between the two calls (cloud sync, another
/// app). Shared code cannot close that gap through <see cref="IStorageFolder"/>.
/// </para>
/// <para>
/// The Android head registers an implementation that calls <c>DocumentsContract.CreateDocument</c>
/// directly. A documents provider does not replace on that call: when the name is taken, the
/// external-storage provider gives the new document the name <c>name (1).ext</c>. So the returned
/// <see cref="ExclusiveFile.Name"/> can differ from the name asked for.
/// </para>
/// </remarks>
public interface IExclusiveFileCreator
{
    /// <summary>
    /// Creates a new, empty file in <paramref name="folder"/> without replacing any existing file.
    /// </summary>
    /// <returns>The created file, or null when this platform cannot create one in this kind of
    /// folder, so the caller must use another way.</returns>
    /// <exception cref="IOException">The folder refused to create the file.</exception>
    Task<ExclusiveFile?> TryCreateNewAsync(IStorageFolder folder, string name);
}

/// <summary>
/// A file that <see cref="IExclusiveFileCreator.TryCreateNewAsync"/> created. It is not an
/// <see cref="IStorageFile"/>, because Avalonia 12 does not let user code implement one.
/// </summary>
/// <param name="Name">The name the provider gave the file. It can differ from the name asked for.</param>
/// <param name="OpenWriteAsync">Opens the file for writing.</param>
/// <param name="GetSizeAsync">The size in bytes, or null when the provider does not report it.</param>
/// <param name="DeleteAsync">Deletes the file.</param>
public sealed record ExclusiveFile(
    string Name,
    Func<Task<Stream>> OpenWriteAsync,
    Func<Task<ulong?>> GetSizeAsync,
    Func<Task> DeleteAsync);

/// <summary>
/// Static registration point: a platform head sets <see cref="Current"/> at startup. It stays null
/// where no hook is needed: the writer creates a file in a folder with a local path (desktop, and
/// iOS inside the app sandbox) with an atomic create-if-absent.
/// </summary>
public static class ExclusiveFileCreator
{
    public static IExclusiveFileCreator? Current { get; set; }
}
