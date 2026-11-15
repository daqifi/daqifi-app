using Avalonia.Platform.Storage;
using Daqifi.Avalonia.Services;
using Foundation;

namespace Daqifi.Avalonia.iOS;

// NOTE: see Program.cs — `Avalonia.*`-qualified names must not be written inside
// this namespace; the usings above are the safe path.

/// <summary>
/// Gives the export writer the security scope of a picked folder outside the app sandbox, so that
/// Export all can create its file with a create-if-absent instead of Avalonia's replacing
/// <c>CreateFileAsync</c> (review of PR #603).
/// </summary>
/// <remarks>
/// <para>
/// What Avalonia.iOS 12.1.6 does (<c>src/iOS/Avalonia.iOS/Storage</c>):
/// <c>OpenFolderPickerAsync</c> wraps each security-scoped <c>NSUrl</c> the picker returns in an
/// <c>IOSStorageFolder</c>, whose internal <c>SecurityScopedAncestorUrl</c> is that URL. Every
/// operation starts the scope and stops it again in a <c>finally</c> (<c>CreateFileAsync</c>,
/// <c>GetItem</c>, <c>DeleteAsync</c>, <c>SaveBookmarkAsync</c>); nothing holds it in between.
/// <c>Path</c> is the <c>file://</c> URL, so <c>TryGetLocalPath()</c> returns a path that the
/// sandbox refuses outside those calls. And <c>CreateFileAsync</c> is
/// <c>NSFileManager.CreateFile(path, new NSData(), null)</c>, which replaces a file of that name.
/// </para>
/// <para>
/// The scope belongs to the picker's <c>NSUrl</c>. A URL rebuilt from <c>Path</c> does not carry
/// it, and the property that holds it is internal. <c>SaveBookmarkAsync</c> is public, and it
/// creates the bookmark while the scope is active. Resolving that bookmark with
/// <c>NSUrl.FromBookmarkData</c> gives a URL on which <c>StartAccessingSecurityScopedResource</c>
/// works again. That is Apple's documented way to keep access to a picked folder ("Providing access
/// to directories"), and it is how Avalonia's own <c>OpenFolderBookmarkAsync</c> gets its URL.
/// </para>
/// <para>
/// NOT verified on an iOS device or simulator: this head cannot run in the unit suite. The shared
/// half — the create-if-absent inside a scope, and the bookmark decode — is unit-tested in
/// <c>StorageExportWriterTests</c>.
/// </para>
/// </remarks>
internal sealed class BookmarkScopedFolderAccess : IScopedFolderAccess
{
    public async Task<IScopedFolder?> TryResolveAsync(IStorageFolder folder)
    {
        if (folder is not IStorageBookmarkFolder { CanBookmark: true } bookmarkable)
        {
            return null;
        }

        // Null when Avalonia could not start the scope to make the bookmark: the URL is not a
        // security-scoped one, so there is no scope to resolve.
        var native = AvaloniaStorageBookmark.TryGetNativeBookmark(
            await bookmarkable.SaveBookmarkAsync().ConfigureAwait(false),
            AvaloniaStorageBookmark.IosPlatformKey);
        if (native is null)
        {
            return null;
        }

        using var data = NSData.FromArray(native);
        var url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null, out _, out var error);
        if (error is not null)
        {
            throw new IOException($"could not resolve the folder's bookmark: {error.LocalizedDescription}");
        }

        return url?.Path is { } path ? new Scope(url, path) : null;
    }

    private sealed class Scope(NSUrl url, string path) : IScopedFolder
    {
        public string LocalPath => path;

        // When the start fails, the file call that follows fails on its own, with the sandbox's
        // error, and the writer reports it. Avalonia ignores the result in the same way.
        public IDisposable Enter() => new Access(url, url.StartAccessingSecurityScopedResource());
    }

    /// <summary>Stops the scope once, and only if this access started it (Apple: balance each
    /// successful start with one stop).</summary>
    private sealed class Access(NSUrl url, bool started) : IDisposable
    {
        private int _stopped;

        public void Dispose()
        {
            if (started && Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                url.StopAccessingSecurityScopedResource();
            }
        }
    }
}
