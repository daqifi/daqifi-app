using System.Globalization;
using System.IO;
using System.Text;

namespace Daqifi.Desktop.Exporter;

/// <summary>
/// Turns the sessions in one export into the file names it writes, and is the only place that
/// decides what those names are. Both export paths use it: the desktop Export dialog
/// (<see cref="Daqifi.Desktop.ViewModels.ExportDialogViewModel"/>) and the mobile Storage tab
/// (<c>LoggedSessionsMobileViewModel</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two sessions must never resolve to the same path. <c>OptimizedLoggingSessionExporter.RunExport</c>
/// opens its <see cref="StreamWriter"/> with <c>append: false</c>, so a repeat path does not merge or
/// fail — the later session silently truncates the earlier one, and both writes report success. That
/// is data loss the user is told did not happen, so the disambiguation belongs somewhere neither
/// caller can forget it (issue #186; the desktop dialog had shipped without it).
/// </para>
/// <para>
/// A session's name is arbitrary user text — it is renameable, and the SD-card importer generates
/// names of its own — so it is sanitized first and disambiguated afterwards, in that order.
/// Sanitizing second would let two names that are distinct beforehand (<c>a/b</c> and <c>a_b</c>)
/// converge on one file afterwards, which is exactly one of the reported collisions.
/// </para>
/// <para>
/// A name is also shortened to fit <see cref="MaxFileNameBytes"/>, with its <c>" (n)"</c> and its
/// <c>.csv</c>. The session NAME box has no length limit, and a file system refuses a longer name:
/// the export of that session failed with a <see cref="PathTooLongException"/>, and Export All
/// stopped at it (issue #683). Shortening comes before disambiguation for the same reason that
/// sanitizing does: two long names that differ only after the cut must still get two files.
/// </para>
/// </remarks>
internal sealed class ExportFileNamer
{
    /// <summary>
    /// The longest file name, in bytes, that every file system this app writes to accepts. Linux and
    /// APFS count 255 bytes of UTF-8. NTFS, exFAT and HFS+ count 255 UTF-16 code units, and a UTF-16
    /// code unit is never fewer bytes than one in UTF-8, so a name inside 255 UTF-8 bytes also fits
    /// there. See <see cref="FileNameBytes"/> for the one exception, HFS+ decomposition.
    /// </summary>
    internal const int MaxFileNameBytes = 255;

    /// <summary>The longest base name: <see cref="MaxFileNameBytes"/> less <c>"." + csv</c>.</summary>
    internal const int MaxBaseNameBytes = MaxFileNameBytes - 1 - 3;

    /// <summary>
    /// Base names already handed out by this instance — i.e. by this one export — each held in the
    /// form <see cref="CollisionKey"/> reduces it to.
    /// </summary>
    /// <remarks>
    /// Case-INSENSITIVE, so <c>Run</c> and <c>run</c> are disambiguated even though a
    /// case-sensitive file system (Linux, and an APFS volume formatted that way) would have
    /// tolerated both. Over-disambiguating costs a suffix; under-disambiguating costs a session.
    /// </remarks>
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file this session should be written to, inside <paramref name="directory"/>, given
    /// everything already named by this export.
    /// </summary>
    /// <param name="directory">Directory the export is writing into.</param>
    /// <param name="sessionName">The session's display name, or null when it could not be resolved.</param>
    /// <param name="sessionId">The session's id, used for the fallback name and to keep it unique.</param>
    internal string NextCsvPath(string directory, string? sessionName, int sessionId) =>
        Path.Combine(directory, NextCsvName(sessionName, sessionId));

    /// <summary>
    /// The extension every export file has, without the dot. One constant, because the mobile save
    /// picker ALSO appends it: the Android picker adds <c>"." + DefaultExtension</c> to the suggested
    /// name, so a suggestion that already ends in <c>.csv</c> became <c>.csv.csv</c> (issue #587).
    /// </summary>
    internal const string CsvExtension = "csv";

    /// <summary>
    /// As <see cref="NextCsvPath"/>, but returns only the file name (no directory) — for a destination
    /// that is a storage-provider folder rather than a path, such as an Android content URI, where the
    /// file is created by name (<c>IStorageFolder.CreateFileAsync</c>).
    /// </summary>
    internal string NextCsvName(string? sessionName, int sessionId) =>
        $"{NextName(sessionName, sessionId)}.{CsvExtension}";

    /// <summary>
    /// As <see cref="NextCsvPath"/>, but returns the bare base name (no directory, no extension).
    /// </summary>
    internal string NextName(string? sessionName, int sessionId)
    {
        var baseName = SafeName(sessionName, sessionId);
        var name = baseName;
        var n = 2;
        // The disambiguated name is itself recorded, so a session genuinely called "Run (2)"
        // alongside two called "Run" still gets a file of its own.
        while (!_used.Add(CollisionKey(name)))
        {
            // The suffix is inside the limit too, so a base name that already fills it gives up
            // the bytes the suffix needs. The loop then checks the result like any other name.
            var suffix = $" ({n++})";
            var stem = FitFileName(baseName, MaxBaseNameBytes - FileNameBytes(suffix));
            name = (stem.Length == 0 ? FallbackName(sessionId) : stem) + suffix;
        }

        return name;
    }

    /// <summary>
    /// The form a name is REMEMBERED by, which is not the form it is written as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed to NFC, because macOS resolves canonically equivalent names to the same file — HFS+
    /// and, since 10.13, APFS are normalization-insensitive — while ordinal comparison of the UTF-16
    /// does not. Without this, a session renamed by typing <c>Café</c> (composed) and one renamed by
    /// pasting the decomposed spelling of the same word are two distinct strings that open ONE file,
    /// and the truncating write loses the first: the very failure this type exists to prevent.
    /// </para>
    /// <para>
    /// Canonical (FormC), not compatibility (FormKC): no file system folds <c>ﬁ</c> into <c>fi</c>,
    /// so treating them as one name would disambiguate two files that were never in conflict.
    /// </para>
    /// <para>
    /// Only the key is normalized. The name actually written stays exactly as the user spelled it,
    /// so a file system that preserves the distinction keeps their spelling; the cost is that on
    /// such a file system the second of an equivalent pair takes a " (2)" it did not strictly need,
    /// which is the same conservative trade the case-insensitive comparer above makes.
    /// </para>
    /// </remarks>
    private static string CollisionKey(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed UTF-16 — a session name carrying an unpaired surrogate. There is
            // nothing to canonicalize, and faulting the whole export over a malformed name would be
            // a worse outcome than comparing it verbatim.
            return name;
        }
    }

    /// <summary>
    /// A session's display name reduced to a legal file name, with no disambiguation — for an
    /// export of ONE session, where there is nothing to collide with. The result has at most
    /// <see cref="MaxBaseNameBytes"/>, so the name with <c>.csv</c> fits <see cref="MaxFileNameBytes"/>.
    /// </summary>
    /// <remarks>
    /// The blank fallback is defensive rather than routine: <see cref="Logger.LoggingSession.Name"/>
    /// already substitutes <c>"Session {ID}"</c> for a blank stored name, so this fires only when
    /// the caller could not resolve a name at all — the desktop dialog looks the session up by id in
    /// a list it may no longer be in. The underscore spelling is that path's long-standing one. The
    /// same fallback applies when the cut leaves nothing, which needs one text element (a letter
    /// with a very long run of combining marks) that is larger than the whole limit.
    /// </remarks>
    // @port: Daqifi.Desktop.ViewModels.ExportDialogViewModel.MakeSafeFileName
    internal static string SafeName(string? sessionName, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionName)) { return FallbackName(sessionId); }

        var name = FitFileName(ReplaceInvalidChars(sessionName), MaxBaseNameBytes);
        return name.Length == 0 ? FallbackName(sessionId) : name;
    }

    /// <summary>
    /// <paramref name="name"/> with every character that a file name cannot contain replaced by
    /// <c>_</c>. No other change: no trim and no length limit.
    /// </summary>
    internal static string ReplaceInvalidChars(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    /// <summary>
    /// The longest start of <paramref name="name"/> whose <see cref="FileNameBytes"/> is at most
    /// <paramref name="maxBytes"/>. The cut is on a text element boundary, so it never splits a
    /// surrogate pair, or an accent from its letter. Returns <paramref name="name"/> itself when it
    /// fits, and an empty string when not even its first text element fits.
    /// </summary>
    internal static string FitFileName(string name, int maxBytes)
    {
        if (FileNameBytes(name) <= maxBytes) { return name; }

        var elements = StringInfo.GetTextElementEnumerator(name);
        var cut = new StringBuilder();
        var bytes = 0;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            // Counted per element, which is never less than the count of the joined text: the
            // decomposed form of a whole name is the decomposed forms of its text elements.
            bytes += FileNameBytes(element);
            if (bytes > maxBytes) { break; }
            cut.Append(element);
        }

        return cut.ToString();
    }

    /// <summary>
    /// How many bytes <paramref name="text"/> takes in a file name: its UTF-8 length, or the UTF-8
    /// length of its decomposed (NFD) form when that is larger.
    /// </summary>
    /// <remarks>
    /// The decomposed form is there for HFS+, which stores a name decomposed and counts the
    /// result in UTF-16 code units. A few characters grow more than that: <c>ΐ</c> (U+0390) is two
    /// bytes of UTF-8 but three code points when decomposed, so 120 of them are 240 bytes and still
    /// 360 code units, too long for HFS+. The UTF-8 length of the decomposed form is never less than
    /// its UTF-16 length, so the larger of the two numbers is safe on every one of these file
    /// systems.
    /// </remarks>
    internal static int FileNameBytes(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        try
        {
            return Math.Max(bytes, Encoding.UTF8.GetByteCount(text.Normalize(NormalizationForm.FormD)));
        }
        catch (ArgumentException)
        {
            // Not well-formed UTF-16 (an unpaired surrogate). There is no decomposed form, and the
            // UTF-8 count already gives the replacement character its three bytes.
            return bytes;
        }
    }

    /// <summary>The name of a session whose own name cannot be used.</summary>
    private static string FallbackName(int sessionId) => $"Session_{sessionId}";
}
