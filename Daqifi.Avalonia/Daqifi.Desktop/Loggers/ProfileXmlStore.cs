// Downstream addition — no upstream counterpart, so no `// @port:` markers here.
// Upstream's LoggingManager does its own File.Exists/XDocument.Load/doc.Save inline in each of
// the four profile methods; this type exists to hold that file handling in ONE place where it
// can be made recoverable and tested against a throwaway path.

using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Collections.ObjectModel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Helpers;
using Daqifi.Desktop.Models;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// The profiles file (<c>DAQifiProfilesConfiguration.xml</c>) — reading it, writing it, and
/// recovering from a damaged one.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of #184. Every recovery path in the old code was keyed on the file being
/// <b>absent</b>, so a file that was present but unparseable was never repaired: the load threw,
/// the pane went empty, and every subsequent save threw on the same
/// <see cref="XDocument.Load(string)"/> — for the life of that file, with nothing shown to the
/// user at any point. A power cut during <c>doc.Save</c> (a plain in-place overwrite) was enough
/// to produce it.
/// </para>
/// <para>
/// Three rules follow from that, and they are the whole point of this type:
/// </para>
/// <list type="number">
/// <item><description>
/// A file that cannot be read is <b>quarantined</b> — renamed aside, never deleted — so the next
/// write starts from a good document instead of failing for ever.
/// </description></item>
/// <item><description>
/// A write goes to a temporary file and is renamed over the real one, so an interrupted write
/// cannot leave the truncated file that starts this story.
/// </description></item>
/// <item><description>
/// A failure is <b>returned</b>, never swallowed into a log line the user never sees.
/// <see cref="TryLoad"/> says so with <c>false</c>; <see cref="Open"/> and <see cref="Save"/>
/// throw, and their callers in <see cref="LoggingManager"/> turn that into a <c>false</c> the
/// Profiles pane shows.
/// </description></item>
/// </list>
/// <para>
/// Not a singleton and not static: it takes its path, which is what lets the tests point it at a
/// temp directory instead of the developer's real
/// <c>~/Library/Application Support/DAQiFi</c>.
/// </para>
/// </remarks>
internal sealed class ProfileXmlStore
{
    private const string RootElementName = "Profiles";
    private const string ProfileElementName = "Profile";

    private readonly AppLogger _appLogger = AppLogger.Instance;

    /// <summary>
    /// Set when a damaged file has been moved aside and not yet reported to anyone. Read once,
    /// by <see cref="TakeQuarantineNotice"/> — see that method for why it is one-shot.
    /// </summary>
    private string? _pendingQuarantineNotice;

    internal ProfileXmlStore(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Full path of the profiles XML file this store reads and writes.</summary>
    internal string FilePath { get; }

    /// <summary>
    /// Reads every profile in the file.
    /// </summary>
    /// <param name="profiles">
    /// The profiles read, or an empty list when there were none or the read failed. Never null.
    /// </param>
    /// <returns>
    /// <c>true</c> when the file was read (including the fresh-install case where there is no file
    /// yet); <c>false</c> when it could not be, in which case the caller <b>must keep whatever it
    /// already had</b> rather than replacing it with the empty list. That is the #184 regression:
    /// the old code cleared its collection before attempting the load, so one unparseable file
    /// emptied the Profiles pane while the file on disk still held every profile.
    /// </returns>
    internal bool TryLoad(out List<Profile> profiles)
    {
        profiles = [];

        try
        {
            // No file at all is the ordinary fresh-install case, not a fault: no profiles, nothing
            // to recover, and nothing to quarantine (a new install must not be littered with
            // .corrupt- files).
            if (!File.Exists(FilePath))
            {
                return true;
            }

            profiles = ReadProfiles(XDocument.Load(FilePath));
            return true;
        }
        catch (Exception ex) when (IsUnreadableContent(ex))
        {
            _appLogger.Error(ex, $"The profiles file could not be read and is being moved aside: {FilePath}");
            Quarantine();
            profiles = [];
            return false;
        }
        catch (Exception ex)
        {
            // Reached the file but could not read it — locked by another process, a permission
            // problem, an unmounted volume. The CONTENT is not implicated, so it is emphatically
            // not quarantined; the caller keeps what it has and the next attempt may well work.
            _appLogger.Error(ex, $"Reading the profiles file failed: {FilePath}");
            profiles = [];
            return false;
        }
    }

    /// <summary>
    /// Reads the profiles in a file that this app does not own: the one an earlier DAQiFi version
    /// left in <c>%ProgramData%\DAQiFi</c>, which the release WPF app still uses (#512).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="TryLoad"/>, this never moves, repairs or creates the file. A damaged file
    /// stays where it is, because it is not this app's to move. The file is opened for reading only
    /// and shared for writing and deletion, so that the other app can still save it during the read.
    /// </remarks>
    /// <returns>The profiles in the file. An empty list when there is no file.</returns>
    /// <exception cref="Exception">
    /// The file could not be read. <see cref="IsUnreadableContent"/> tells whether its content is
    /// the cause (a damaged file) or the file could not be reached (locked, no access).
    /// </exception>
    internal static List<Profile> ReadWithoutRepair(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadProfiles(XDocument.Load(stream));
    }

    /// <summary>
    /// How long a writer waits for another writer of the same file. A write is milliseconds.
    /// </summary>
    internal static readonly TimeSpan WriteLockWait = TimeSpan.FromSeconds(10);

    /// <summary>The lock file that every writer of <see cref="FilePath"/> holds, empty and left in place.</summary>
    internal string WriteLockPath => FilePath + ".lock";

    /// <summary>
    /// Reads the document, lets <paramref name="change"/> change it, and writes it back. The lock
    /// that every writer of this file takes, in this copy of the app or another, is held for all
    /// three steps.
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> replaces the file atomically, which keeps the XML whole, but it cannot
    /// keep a second writer's change: two windows that each read the file and then write it back
    /// lose the save of the one that wrote first (#512 review). With the lock, the second writer
    /// reads only after the first has written. A file rather than a named mutex, for the reasons in
    /// <see cref="ImportLease"/>: the OS releases it when a process dies, and on macOS and Linux
    /// .NET maps <see cref="FileShare.None"/> onto <c>flock</c>.
    /// </remarks>
    /// <param name="change">Changes the document; returns whether there is anything to write.</param>
    /// <exception cref="IOException">
    /// The lock was still held after <see cref="WriteLockWait"/>, or the file could not be read or
    /// written. Nothing was written.
    /// </exception>
    internal void Edit(Func<XDocument, bool> change)
    {
        EnsureDirectory();
        using var held = HoldWriteLock();
        var document = Open();
        if (change(document))
        {
            Save(document);
        }
    }

    private FileStream HoldWriteLock()
    {
        var deadline = DateTime.UtcNow + WriteLockWait;
        while (true)
        {
            try
            {
                return new FileStream(WriteLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// Opens the document for a write, creating an empty one when the file is absent and
    /// quarantining it when it is not well-formed XML.
    /// </summary>
    /// <remarks>
    /// Deliberately a narrower recovery than <see cref="TryLoad"/>'s: this quarantines only on
    /// XML that does not parse, not on content a reader would reject. A writer needs a document
    /// to append to and nothing more — it never has to interpret the profiles already in the
    /// file, so it is not in a position to declare them unreadable, and destroying them on its
    /// judgement would be a worse bug than the one being fixed.
    /// </remarks>
    /// <exception cref="IOException">The directory or file could not be accessed.</exception>
    internal XDocument Open()
    {
        EnsureDirectory();

        if (!File.Exists(FilePath))
        {
            return NewDocument();
        }

        try
        {
            return XDocument.Load(FilePath);
        }
        catch (XmlException ex)
        {
            _appLogger.Error(ex, $"The profiles file is not well-formed XML and is being moved aside: {FilePath}");

            if (!Quarantine())
            {
                // The damaged file is still sitting at FilePath. Handing back an empty document
                // would let the caller's Save rename straight over it and destroy the only copy of
                // the user's profiles — the one thing this type promises never to do. Fail the
                // write instead: the pane reports it, and the file survives to be recovered by
                // hand or by a later attempt.
                throw new IOException(
                    $"The profiles file at {FilePath} could not be read and could not be moved " +
                    "aside, so it has been left untouched.", ex);
            }

            return NewDocument();
        }
    }

    /// <summary>
    /// Writes the document over the profiles file, atomically.
    /// </summary>
    /// <remarks>
    /// Via a temporary file and a rename, because <see cref="XDocument.Save(string)"/> truncates
    /// the real file first: a power cut, a full disk or a crash part-way through that write is
    /// exactly how the half-written, unparseable file in #184 comes about.
    /// <see cref="AppDataFile.WriteAtomically"/> holds that rule for every file the app owns, and
    /// the reasons behind the temporary name.
    /// </remarks>
    /// <exception cref="IOException">The file could not be written.</exception>
    internal void Save(XDocument document)
    {
        EnsureDirectory();
        AppDataFile.WriteAtomically(FilePath, document.Save);
    }

    /// <summary>
    /// The message to show the user once, the first time they look at their profiles after a
    /// damaged file was moved aside; <c>null</c> when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// One-shot — reading it clears it. The user needs to be told once why the pane they are
    /// looking at is empty and where their old file went; repeating it on every drawer open would
    /// be noise they cannot dismiss.
    /// </remarks>
    internal string? TakeQuarantineNotice() => Interlocked.Exchange(ref _pendingQuarantineNotice, null);

    /// <summary>
    /// Projects a profiles document into the model. Throws on content it cannot interpret, which
    /// is what <see cref="TryLoad"/> treats as a damaged file.
    /// </summary>
    private static List<Profile> ReadProfiles(XDocument document) =>
        document.Descendants(ProfileElementName).Select(p => new Profile
        {
            Name = (string)p.Element("Name"),
            ProfileId = (Guid)p.Element("ProfileID"),
            CreatedOn = (DateTime)p.Element("CreatedOn"),
            // <Devices> is defaulted the same way <Channels> is below, and for the same reason: a
            // container the writer may legitimately have left out is not a damaged file, and
            // passing the resulting null to ObservableCollection would throw and quarantine every
            // other profile in an otherwise readable file. A profile missing <ProfileID> or
            // <CreatedOn> still fails the read — those are not optional, and a profile without an
            // id can never be edited or deleted again.
            Devices = new ObservableCollection<ProfileDevice>(p.Element("Devices")?.Elements("Device").Select(d => new ProfileDevice
            {
                DeviceName = (string)d.Element("DeviceName"),
                DevicePartName = (string)d.Element("DevicePartNumber"),
                MacAddress = (string)d.Element("MACAddress"),
                DeviceSerialNo = (string)d.Element("DeviceSerialNo"),
                SamplingFrequency = (int)d.Element("SamplingFrequency"),
                // Files written before #184 omitted <Channels> entirely when a device had no
                // active channels (the two writers disagreed about it; they are now one). Default
                // to an empty list so those files still load and downstream code can call
                // .Where/.Select without a null check.
                Channels = d.Element("Channels")?.Elements("Channel").Select(c => new ProfileChannel
                {
                    Name = (string)c.Element("Name"),
                    Type = (string)c.Element("Type"),
                    IsChannelActive = (bool)c.Element("IsActive"),
                    SerialNo = (string)d.Element("DeviceSerialNo")
                }).ToList() ?? []
            }).ToList() ?? [])
        }).ToList();

    /// <summary>
    /// Whether an exception means the file's CONTENT is unusable — as opposed to the file being
    /// momentarily out of reach, which must never cost the user their profiles.
    /// </summary>
    /// <remarks>
    /// The list is explicit rather than a catch-all on purpose, because getting it wrong destroys
    /// data in one direction and leaves the permanent failure of #184 in place in the other:
    /// <list type="bullet">
    /// <item><description><see cref="XmlException"/> — not well-formed.</description></item>
    /// <item><description>
    /// <see cref="ArgumentNullException"/> — an <c>XElement</c> conversion on a missing element;
    /// <see cref="FormatException"/>, <see cref="OverflowException"/>,
    /// <see cref="InvalidCastException"/> — well-formed XML whose values are not the Guid,
    /// DateTime, int or bool the reader casts them to.
    /// </description></item>
    /// </list>
    /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are deliberately
    /// absent: a locked or unreadable file is a working file the process cannot see right now.
    /// </remarks>
    internal static bool IsUnreadableContent(Exception exception) =>
        exception is XmlException
            or FormatException
            or ArgumentNullException
            or OverflowException
            or InvalidCastException;

    /// <summary>
    /// Moves the damaged file aside so the next write starts from a good document.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the damaged file is safely out of the way. <c>false</c> means it is still
    /// sitting at <see cref="FilePath"/>, which callers that are about to write MUST treat as a
    /// refusal — see <see cref="Open"/>.
    /// </returns>
    /// <remarks>
    /// RENAMED, never deleted. It is the user's profiles: an unparseable file is very often still
    /// readable by hand (one bad character in an otherwise intact list), and a truncated one still
    /// holds most of what it held. The timestamp gives repeated recoveries different names;
    /// <see cref="AppDataFile.MoveAside"/> is what makes that a guarantee rather than a hope.
    /// </remarks>
    private bool Quarantine()
    {
        if (AppDataFile.MoveAside(FilePath, $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}") is not { } quarantinePath)
        {
            return false;
        }

        _appLogger.Warning($"The damaged profiles file was moved to {quarantinePath}; profiles will save normally from now on.");
        _pendingQuarantineNotice =
            $"Your saved profiles could not be read, so the damaged file was moved to {quarantinePath}. " +
            "Profiles you create from now on will save normally.";
        return true;
    }

    private static XDocument NewDocument() => new(new XElement(RootElementName));

    // Path.GetDirectoryName returns null for a root path and empty for a bare filename; neither
    // is a directory anything needs created, and Directory.CreateDirectory("") throws.
    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
