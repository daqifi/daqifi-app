using Android.Content;
using Android.Provider;
using Android.Webkit;
using Avalonia.Platform.Storage;
using Daqifi.Avalonia.Services;
using AndroidUri = Android.Net.Uri;

namespace Daqifi.Avalonia.Android;

/// <summary>
/// Creates a new document in a picked Storage Access Framework folder with
/// <see cref="DocumentsContract.CreateDocument"/>, which never replaces an existing document
/// (review of PR #603).
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12.1.6's <c>AndroidStorageFolder.CreateFileAsync</c> looks the name up first and
/// truncates a document it finds. It calls <c>CreateDocument</c> only when it finds nothing. So a
/// document that appeared after the export's name check (cloud sync, another app) was emptied. This
/// calls <c>CreateDocument</c> unconditionally. When the name is taken, the provider gives the new
/// document another name (the external-storage provider uses <c>name (1).ext</c>), and the name
/// returned is the one the provider chose.
/// </para>
/// <para>
/// The parent is worked out the way Avalonia works it out, from the URI the picked folder carries
/// in <see cref="IStorageItem.Path"/>: a tree URI (the folder the user picked) becomes the document
/// URI of its root, and a document URI under a tree (a subfolder) is used as it is.
/// </para>
/// </remarks>
internal sealed class ContentExclusiveFileCreator(Context context) : IExclusiveFileCreator
{
    private readonly Context _context = context.ApplicationContext ?? context;

    public Task<ExclusiveFile?> TryCreateNewAsync(IStorageFolder folder, string name)
    {
        var path = folder.Path;
        if (!path.IsAbsoluteUri || !string.Equals(path.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ExclusiveFile?>(null);
        }

        // OriginalString, not ToString(): System.Uri.ToString() unescapes, and a document id such
        // as "primary%3ADocuments" must reach DocumentsContract escaped.
        var folderUri = AndroidUri.Parse(path.OriginalString);
        if (folderUri is null || ParentDocumentUri(folderUri) is not { } parent)
        {
            return Task.FromResult<ExclusiveFile?>(null);
        }

        var resolver = _context.ContentResolver
                       ?? throw new IOException("no content resolver to create the export file with");
        // The extension from the name itself, not MimeTypeMap.GetFileExtensionFromUrl, which is what
        // Avalonia uses: that parses a URL and returns "" for a name with a space in it ("SD Import -
        // log_….csv"). The MIME type was then application/octet-stream, so the provider could not
        // tell where the extension began and named the renamed file "… .csv (1)". Measured on a
        // Galaxy A16: with the extension from the name, it names it "… (1).csv".
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var mimeType = (extension.Length > 0 ? MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) : null)
                       ?? "application/octet-stream";

        AndroidUri created;
        try
        {
            created = DocumentsContract.CreateDocument(resolver, parent, mimeType, name)
                      ?? throw new IOException($"the folder refused to create '{name}'");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // A provider reports a refusal as a Java exception (for example SecurityException once
            // the folder grant is gone). The writer expects an IOException for "the folder refused".
            throw new IOException($"the folder refused to create '{name}': {ex.Message}", ex);
        }

        return Task.FromResult<ExclusiveFile?>(new ExclusiveFile(
            Query(resolver, created, DocumentsContract.Document.ColumnDisplayName) ?? name,
            () => Task.FromResult(resolver.OpenOutputStream(created, "wt")
                                  ?? throw new IOException($"could not open '{name}' for writing")),
            () => Task.FromResult(
                ulong.TryParse(Query(resolver, created, DocumentsContract.Document.ColumnSize), out var size)
                    ? size
                    : (ulong?)null),
            () =>
            {
                DocumentsContract.DeleteDocument(resolver, created);
                return Task.CompletedTask;
            }));
    }

    private AndroidUri? ParentDocumentUri(AndroidUri folderUri)
    {
        if (DocumentsContract.IsDocumentUri(_context, folderUri))
        {
            return folderUri;
        }

        return DocumentsContract.IsTreeUri(folderUri)
               && DocumentsContract.GetTreeDocumentId(folderUri) is { } treeDocumentId
            ? DocumentsContract.BuildDocumentUriUsingTree(folderUri, treeDocumentId)
            : null;
    }

    private static string? Query(ContentResolver resolver, AndroidUri document, string column)
    {
        using var cursor = resolver.Query(document, [column], null, null, null);
        return cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0) ? cursor.GetString(0) : null;
    }
}
