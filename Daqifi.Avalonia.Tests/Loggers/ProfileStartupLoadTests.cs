using System.Text.RegularExpressions;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #544: on Android and iOS the Profiles tab showed <b>0 SAVED</b> after a restart, while
/// the profiles file on the phone still held every profile.
///
/// <para>
/// Saving was fine. The file was read at startup by one line in <c>DaqifiViewModel</c>, and only
/// the desktop <c>MainWindow</c> makes a <c>DaqifiViewModel</c>, so the mobile heads never read
/// it. The fix moves that read into the app's <c>LoggingManager</c> itself: the production
/// instance loads the file when it is created, which happens exactly once per process (it is a
/// <c>Lazy</c> singleton) on every head.
/// </para>
///
/// <para>
/// "The app starts again" is modelled by building a second production <c>LoggingManager</c>
/// through its private constructor — the one the singleton uses — rather than reading
/// <c>LoggingManager.Instance</c>. The singleton is created once per test process, by whichever
/// test touches it first, so a test that seeded the file and then read <c>Instance</c> would pass
/// or fail depending on test order. The second instance reads the same file the singleton writes
/// (the throwaway data directory the assembly's module initializer points
/// <c>DAQIFI_DATA_DIR</c> at), and is otherwise unused.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class ProfileStartupLoadTests : IDisposable
{
    private readonly List<Profile> _saved = [];

    public ProfileStartupLoadTests() => Daqifi.Desktop.App.InitializeMobile();

    /// <summary>The singleton and the file are shared with the rest of the run; leave neither changed.</summary>
    public void Dispose()
    {
        foreach (var profile in _saved)
        {
            try { LoggingManager.Instance.UnsubscribeProfile(profile); } catch { /* best-effort cleanup */ }
        }
    }

    private static Profile NewProfile(string name) => new()
    {
        Name = name,
        ProfileId = Guid.NewGuid(),
        CreatedOn = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Unspecified),
        Devices =
        [
            new ProfileDevice
            {
                DeviceName = "Nyquist 1",
                DevicePartName = "Nq1",
                MacAddress = "00:11:22:33:44:55",
                DeviceSerialNo = "SERIAL-544",
                SamplingFrequency = 100,
                Channels = [new ProfileChannel { Name = "AI0", Type = "Analog", IsChannelActive = true, SerialNo = "SERIAL-544" }],
            },
        ],
    };

    /// <summary>What the next launch of the app creates: a production <c>LoggingManager</c>.</summary>
    private static LoggingManager StartAgain() =>
        (LoggingManager)Activator.CreateInstance(typeof(LoggingManager), nonPublic: true)!;

    /// <summary>
    /// The defect, end to end on the shared code: two profiles saved through the app's own
    /// manager are both listed by the manager of the next launch, with no view model involved.
    /// On the unfixed code the next launch lists none of them.
    /// </summary>
    [Fact]
    public void Profiles_saved_in_one_session_are_listed_when_the_app_starts_again()
    {
        var first = NewProfile("Issue 544 A");
        var second = NewProfile("Issue 544 B");
        foreach (var profile in new[] { first, second })
        {
            Assert.True(LoggingManager.Instance.SubscribeProfile(profile));
            _saved.Add(profile);
        }

        var ids = StartAgain().SubscribedProfiles.Select(p => p.ProfileId).ToList();

        Assert.Contains(first.ProfileId, ids);
        Assert.Contains(second.ProfileId, ids);
    }

    /// <summary>
    /// The load must stay in one place. A second call anywhere — a view model, a pane, a head's
    /// startup — would run after the panes had bound to the profiles and replace every
    /// <see cref="Profile"/> object under them, which drops a selection or an open edit. On the
    /// unfixed code this lists <c>DaqifiViewModel.cs</c>.
    /// </summary>
    [Fact]
    public void Only_the_logging_manager_reads_the_profiles_file()
    {
        var call = new Regex(@"\bLoadProfilesFromXml\s*\(");
        var root = BindingFacts.RepoRoot();
        var heads = new[] { "Daqifi.Avalonia", "Daqifi.Avalonia.Desktop", "Daqifi.Avalonia.Android", "Daqifi.Avalonia.iOS" };

        var callers = heads
            .Select(head => Path.Combine(root, head))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("//") && call.IsMatch(line)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        // Positive control: the scan reaches the file that defines the method, so an empty
        // result below cannot come from a scan that read nothing.
        Assert.Contains(callers, file => file.EndsWith("LoggingManager.cs", StringComparison.Ordinal));
        Assert.Equal(["LoggingManager.cs"], callers.Select(Path.GetFileName));
    }
}
