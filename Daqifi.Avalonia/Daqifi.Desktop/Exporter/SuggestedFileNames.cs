using System.Globalization;
using System.Text;

namespace Daqifi.Desktop.Exporter;

/// <summary>
/// The file names that the desktop save pickers put in the name field before the user types (#632).
/// </summary>
/// <remarks>
/// <para>
/// Without a suggested name, the macOS save panel opened with an empty field and a disabled
/// <b>Save</b> button, so the first push of Return did nothing. Each name here is complete: it has a
/// base name and exactly one extension, so the user can push Return to save.
/// </para>
/// <para>
/// The extension is part of the name. On macOS and Windows the picker does not add a second one when
/// the name already ends in the extension of the selected file type. A name without an extension is
/// what the Android picker needs (#587), but these pickers are desktop-only, and a desktop picker
/// that does not apply <c>DefaultExtension</c> would then save a file with no extension at all.
/// </para>
/// </remarks>
internal static class SuggestedFileNames
{
    /// <summary>Local date and time in a graph image name. Sorts by time, and has no colon.</summary>
    internal const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>The name part of a live graph image, which has no session.</summary>
    internal const string LiveGraphPrefix = "DAQiFi";

    /// <summary>
    /// The longest session part, in UTF-8 bytes. A file name can have at most 255 bytes on macOS and
    /// Linux, and a session name is free user text. 150 bytes leaves space for the graph suffix, the
    /// timestamp and the extension (30 bytes together).
    /// </summary>
    internal const int MaxSessionPartBytes = 150;

    /// <summary>
    /// The name for a PNG of a graph: <c>&lt;session&gt;-graph-&lt;local time&gt;.png</c>, or
    /// <c>DAQiFi-graph-&lt;local time&gt;.png</c> for the live graph.
    /// </summary>
    /// <param name="sessionName">The session on the plot, or null for the live graph.</param>
    /// <param name="localNow">The local time of the save.</param>
    internal static string GraphImage(string? sessionName, DateTime localNow)
    {
        var prefix = SessionPart(sessionName, LiveGraphPrefix);
        var stamp = localNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{prefix}-graph-{stamp}.png";
    }

    /// <summary>
    /// The name for a CSV export of one session: the session name and one <c>.csv</c>. For an
    /// ordinary session name this is the name that the mobile Storage tab and the desktop Export All
    /// give the same session, so one session gets one file name on each path. No timestamp: a CSV
    /// holds the whole session, so a second export of it is the same file.
    /// </summary>
    internal static string SessionCsv(string? sessionName, int sessionId) =>
        WithSingleExtension(SessionPart(sessionName, ExportFileNamer.SafeName(null, sessionId)), "csv");

    /// <summary>
    /// <paramref name="baseName"/> with <c>.<paramref name="extension"/></c> added, unless the name
    /// already ends with it. A session that the user renamed to <c>run.csv</c> thus gives
    /// <c>run.csv</c>, not <c>run.csv.csv</c>.
    /// </summary>
    internal static string WithSingleExtension(string baseName, string extension)
    {
        var suffix = "." + extension;
        return baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? baseName : baseName + suffix;
    }

    /// <summary>
    /// A session name made safe for a file name: no character that a file name cannot contain, no
    /// leading or trailing space or dot, and at most <see cref="MaxSessionPartBytes"/> bytes. The cut
    /// is on a text element boundary, so it never splits a surrogate pair or an accent from its
    /// letter. <paramref name="fallback"/> is used when nothing is left.
    /// </summary>
    private static string SessionPart(string? sessionName, string fallback)
    {
        var safe = string.IsNullOrWhiteSpace(sessionName)
            ? string.Empty
            : ExportFileNamer.SafeName(sessionName, 0).Trim(' ', '.');
        if (Encoding.UTF8.GetByteCount(safe) > MaxSessionPartBytes)
        {
            var elements = StringInfo.GetTextElementEnumerator(safe);
            var cut = new StringBuilder();
            var bytes = 0;
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                bytes += Encoding.UTF8.GetByteCount(element);
                if (bytes > MaxSessionPartBytes) { break; }
                cut.Append(element);
            }

            safe = cut.ToString().TrimEnd(' ', '.');
        }

        return safe.Length == 0 ? fallback : safe;
    }
}
