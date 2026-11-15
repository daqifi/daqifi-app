using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #596: when the device drawer refused a name, it showed the text of Core's
/// <see cref="ArgumentException"/>, which ends in "(Parameter 'name')".
///
/// <para>
/// The drawer now judges the name itself, with <see cref="FriendlyNameRule"/>: as the user types
/// (the inline error appears and SAVE NAME is disabled), and again when the command runs, before
/// anything is sent to the device. These tests drive the drawer's own bound members —
/// <c>PendingFriendlyName</c> (the NAME box), <c>SetFriendlyNameCommand</c> (SAVE NAME) and
/// <c>FriendlyNameError</c> (the red line under the box).
/// </para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class FriendlyNameErrorTests
{
    private const string DeviceName = "Lab Nyquist";

    /// <summary>The names the issue measured on the bench board, plus a backslash.</summary>
    public static TheoryData<string> RefusedNames => new()
    {
        "",
        "   ",
        "Lab \U0001F52C",
        "Lab \"A\"",
        "a\\b",
    };

    private static (DaqifiViewModel Shell, RecordingStreamingDevice Device) DrawerOpenOnADevice()
    {
        Daqifi.Desktop.App.InitializeMobile();
        var device = new RecordingStreamingDevice("SN-NAME");
        var shell = new DaqifiViewModel(new NullDialogService()) { SelectedDevice = device };
        // What DevicesPaneViewModel.OpenSettings does when the drawer opens.
        shell.SeedPendingFriendlyName(DeviceName);
        shell.FriendlyNameError = null;
        return (shell, device);
    }

    [Fact]
    public void Opening_the_drawer_shows_no_error_and_save_is_enabled()
    {
        var (shell, _) = DrawerOpenOnADevice();

        Assert.False(shell.HasFriendlyNameError);
        Assert.True(shell.SetFriendlyNameCommand.CanExecute(null));
    }

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public void Typing_a_refused_name_shows_why_and_disables_save(string name)
    {
        var (shell, _) = DrawerOpenOnADevice();

        shell.PendingFriendlyName = name;

        Assert.True(shell.HasFriendlyNameError);
        Assert.Equal(FriendlyNameRule.Describe(name.Trim()), shell.FriendlyNameError);
        Assert.DoesNotContain("Parameter", shell.FriendlyNameError);
        Assert.False(shell.SetFriendlyNameCommand.CanExecute(null));
    }

    [Fact]
    public void Correcting_the_name_clears_the_error_and_enables_save()
    {
        var (shell, _) = DrawerOpenOnADevice();
        shell.PendingFriendlyName = "Lab \"A\"";

        shell.PendingFriendlyName = "Lab A";

        Assert.False(shell.HasFriendlyNameError);
        Assert.True(shell.SetFriendlyNameCommand.CanExecute(null));
    }

    /// <summary>
    /// The command checks the name again when it runs, for any caller that executes it without
    /// asking <c>CanExecute</c> first. The device must not be asked to write the name at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedNames))]
    public async Task Saving_a_refused_name_writes_nothing_and_shows_no_developer_text(string name)
    {
        var (shell, device) = DrawerOpenOnADevice();
        shell.PendingFriendlyName = name;

        await shell.SetFriendlyName();

        Assert.Empty(device.FriendlyNameWrites);
        Assert.NotNull(shell.FriendlyNameError);
        Assert.DoesNotContain("Parameter", shell.FriendlyNameError);
        Assert.False(shell.FriendlyNameApplied);
    }

    [Fact]
    public async Task Saving_an_accepted_name_writes_it_trimmed()
    {
        var (shell, device) = DrawerOpenOnADevice();
        shell.PendingFriendlyName = "  Rig #2  ";

        await shell.SetFriendlyName();

        Assert.Equal(["Rig #2"], device.FriendlyNameWrites);
        Assert.False(shell.HasFriendlyNameError);
        Assert.True(shell.FriendlyNameApplied);
    }

    /// <summary>
    /// If Core's rule ever refuses a name the app's rule passed, the drawer still shows the app's
    /// sentence — never the exception text, whose parameter suffix is what the issue reported.
    /// </summary>
    [Fact]
    public async Task A_refusal_from_the_device_shows_the_rule_not_the_exception_text()
    {
        var (shell, device) = DrawerOpenOnADevice();
        device.FriendlyNameRefusal = new ArgumentException("Device name must be valid.", "name");
        shell.PendingFriendlyName = "Lab A";

        await shell.SetFriendlyName();

        Assert.Equal(FriendlyNameRule.RuleText, shell.FriendlyNameError);
        Assert.DoesNotContain("Parameter", shell.FriendlyNameError);
    }

    /// <summary>
    /// Review on #599: after a save, "Name saved" stayed for three seconds even when the user had
    /// already typed a refused name, so it read as a verdict on that name, next to its error.
    /// </summary>
    [Fact]
    public async Task Editing_after_a_save_clears_name_saved()
    {
        var (shell, _) = DrawerOpenOnADevice();
        shell.PendingFriendlyName = "Rig A";
        await shell.SetFriendlyName();
        Assert.True(shell.FriendlyNameApplied);

        shell.PendingFriendlyName = "Rig \"A\"";

        Assert.False(shell.FriendlyNameApplied);
        Assert.True(shell.HasFriendlyNameError);
    }

    /// <summary>
    /// The confirmation belongs to the edit that was written. If the user types while the write is
    /// still running, the write must neither re-seed the box over the new text nor show
    /// "Name saved" for it.
    /// </summary>
    [Fact]
    public async Task An_edit_during_a_slow_write_keeps_the_new_text_and_shows_no_name_saved()
    {
        var (shell, device) = DrawerOpenOnADevice();
        using var gate = new ManualResetEventSlim();
        device.FriendlyNameWriteGate = gate;
        shell.PendingFriendlyName = "Rig A";

        var save = shell.SetFriendlyName();
        Assert.True(device.FriendlyNameWriteStarted.Wait(TimeSpan.FromSeconds(10)));
        shell.PendingFriendlyName = "Rig \"B\"";
        gate.Set();
        await save;

        Assert.Equal(["Rig A"], device.FriendlyNameWrites);
        Assert.Equal("Rig \"B\"", shell.PendingFriendlyName);
        Assert.False(shell.FriendlyNameApplied);
        Assert.True(shell.HasFriendlyNameError);
    }

    /// <summary>A name the device reports is not judged as user input: seeding the box must not
    /// raise an error even when the stored name is one the rule would refuse.</summary>
    [Fact]
    public void Seeding_the_box_from_the_device_raises_no_error()
    {
        var (shell, _) = DrawerOpenOnADevice();

        shell.SeedPendingFriendlyName(string.Empty);

        Assert.False(shell.HasFriendlyNameError);
    }
}
