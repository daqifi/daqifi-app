using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace Daqifi.Avalonia.Services;

/// <summary>
/// Platform hook that gives the export writer file-system access to a picked folder that the app
/// can reach only inside an OS security scope — an iOS folder outside the app sandbox (review of
/// PR #603).
/// </summary>
/// <remarks>
/// <para>
/// Why a hook: on iOS, Avalonia 12.1.6 gives a picked folder a <c>file://</c>
/// <see cref="IStorageItem.Path"/>, so <c>TryGetLocalPath()</c> returns a path, but it holds the
/// folder's security scope only for the length of each of its own calls
/// (<c>IOSStorageItem.cs</c>: <c>CreateFileAsync</c>, <c>GetItem</c>, <c>DeleteAsync</c> each wrap
/// <c>StartAccessingSecurityScopedResource</c> / <c>StopAccessingSecurityScopedResource</c> in
/// try/finally). Outside those calls the sandbox refuses the path, so the writer's own
/// create-if-absent (<see cref="System.IO.FileMode.CreateNew"/>) fails. The one create Avalonia
/// offers there, <c>IOSStorageFolder.CreateFileAsync</c>, is <c>NSFileManager.CreateFile</c>,
/// which REPLACES a file of that name.
/// </para>
/// <para>
/// The scope belongs to the <c>NSUrl</c> the picker returned, which Avalonia keeps in an internal
/// property. A URL rebuilt from <see cref="IStorageItem.Path"/> does not carry it. So the iOS head
/// recovers a scoped URL through public API: <see cref="IStorageBookmarkItem.SaveBookmarkAsync"/>
/// makes the bookmark while the scope is active, and resolving that bookmark gives a URL that can
/// start the scope again (see <see cref="AvaloniaStorageBookmark"/>).
/// </para>
/// </remarks>
public interface IScopedFolderAccess
{
    /// <summary>
    /// Resolves the security scope of <paramref name="folder"/>.
    /// </summary>
    /// <returns>The folder's scope, or null when this platform has none for this folder.</returns>
    Task<IScopedFolder?> TryResolveAsync(IStorageFolder folder);
}

/// <summary>A picked folder whose file-system path is usable only while its scope is entered.</summary>
public interface IScopedFolder
{
    /// <summary>The folder's file-system path.</summary>
    string LocalPath { get; }

    /// <summary>
    /// Starts access to the folder. Disposing the result stops it. Every file-system call on
    /// <see cref="LocalPath"/> must run between the two. Calls may nest.
    /// </summary>
    IDisposable Enter();
}

/// <summary>
/// Static registration point: the iOS head sets <see cref="Current"/> at startup. It stays null on
/// the desktop (every folder has an accessible path) and on Android (a picked folder is a
/// <c>content://</c> URI, which <see cref="ExclusiveFileCreator"/> serves).
/// </summary>
public static class ScopedFolderAccess
{
    public static IScopedFolderAccess? Current { get; set; }
}

/// <summary>
/// Reads the native bookmark out of a bookmark string that Avalonia's
/// <see cref="IStorageBookmarkItem.SaveBookmarkAsync"/> returned (review of PR #603).
/// </summary>
/// <remarks>
/// <para>
/// Avalonia encodes it in its internal <c>StorageBookmarkHelper</c> (Avalonia.Base 12.1.6,
/// <c>Platform/Storage/FileIO/StorageBookmarkHelper.cs</c>): Base64 of a 16-byte header — the
/// ASCII prefix <c>ava.v1.</c>, then the platform key (<c>ios</c> on iOS), zero-padded — followed
/// by the platform's own bookmark bytes. The helper is internal, so this repeats the decode. It is
/// a persisted format (apps store these strings across releases, and Avalonia still decodes its
/// 11.0 format), so it is less likely to change than an internal property would be.
/// </para>
/// <para>
/// Platform-neutral on purpose: it is plain byte handling, and here a unit test can reach it.
/// </para>
/// </remarks>
public static class AvaloniaStorageBookmark
{
    private const int HeaderLength = 16;
    private static ReadOnlySpan<byte> Prefix => "ava.v1."u8;

    /// <summary>The platform key Avalonia.iOS writes into its bookmarks.</summary>
    public const string IosPlatformKey = "ios";

    /// <summary>
    /// The native bookmark bytes in <paramref name="bookmark"/>, or null when it is not an Avalonia
    /// v1 bookmark for <paramref name="platformKey"/>.
    /// </summary>
    public static byte[]? TryGetNativeBookmark(string? bookmark, string platformKey)
    {
        if (string.IsNullOrEmpty(bookmark) || platformKey.Length == 0
            || Prefix.Length + platformKey.Length > HeaderLength)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(bookmark);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length <= HeaderLength || !bytes.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
        {
            return null;
        }

        var platform = bytes.AsSpan(Prefix.Length, HeaderLength - Prefix.Length);
        for (var i = 0; i < platform.Length; i++)
        {
            // The key, then zero padding to the end of the header.
            var expected = i < platformKey.Length ? (byte)platformKey[i] : (byte)0;
            if (platform[i] != expected)
            {
                return null;
            }
        }

        return bytes.AsSpan(HeaderLength).ToArray();
    }
}
