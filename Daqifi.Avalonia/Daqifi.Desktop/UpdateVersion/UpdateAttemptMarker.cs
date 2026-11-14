namespace Daqifi.Desktop.UpdateVersion;

/// <summary>
/// Remembers, across the restart an update makes, which version this copy last tried to install.
/// Installing hands over to the updater and the app exits, so when the update does not happen (an
/// administrator prompt cancelled, or no one there who can approve it) the copy that starts again
/// is a new process that would otherwise just offer the same update with no word of why.
/// </summary>
/// <remarks>Best effort: a marker that cannot be written or read only loses that one message.</remarks>
public sealed class UpdateAttemptMarker(string path)
{
    /// <summary>Records that <paramref name="version"/> is about to be installed.</summary>
    public void Remember(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, version);
        }
        catch (Exception)
        {
            // See the remarks.
        }
    }

    /// <summary>The version last recorded, or <c>null</c>; it is forgotten either way.</summary>
    public string? Take()
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            var version = File.ReadAllText(path).Trim();
            File.Delete(path);
            return version.Length == 0 ? null : version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
