using Avalonia;
using Daqifi.Avalonia.Views.Mobile;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #589: on Android the on-screen keyboard covered the manual IP field, the port field and
/// Connect, and the user typed the address blind.
///
/// <para>
/// The first half tests <see cref="KeyboardInset.Overlap"/>, the distance the phone shell lifts its
/// content area. The numbers are the ones the Galaxy A16 reported (Android 16, 384 x 832 dp in
/// portrait). The second half reads the shell's source: this test project cannot host an Avalonia
/// control (no lifetime), and a subscription that goes away is the whole defect, so the call sites
/// are guarded as source, the way <see cref="MobileProfileEditSaveTests"/> guards #560.
/// </para>
/// </summary>
public sealed class KeyboardInsetTests
{
    private const string MainView = "Daqifi.Avalonia/Views/MobileMainView.axaml.cs";
    private const string ShellView = "Daqifi.Avalonia/Views/MobileShellView.axaml";

    /// <summary>
    /// Portrait on the phone: the content area ends at 728.9 dp (above the bottom nav) and the
    /// keyboard starts at 473.6 dp. The area must be lifted by the difference.
    /// </summary>
    [Fact]
    public void An_open_keyboard_lifts_the_area_by_the_part_it_covers() =>
        Assert.Equal(728.9 - 473.6, KeyboardInset.Overlap(728.9, new Rect(0, 473.6, 384, 310.4)), 3);

    /// <summary>
    /// Landscape: no bottom nav, so the content area ends at the bottom of the screen, and the
    /// whole keyboard height is covered.
    /// </summary>
    [Fact]
    public void In_landscape_the_whole_keyboard_height_is_covered() =>
        Assert.Equal(208.4, KeyboardInset.Overlap(384, new Rect(0, 175.6, 832, 208.4)), 3);

    /// <summary>A closed keyboard reports an empty rectangle at the bottom edge.</summary>
    [Fact]
    public void A_closed_keyboard_lifts_nothing() =>
        Assert.Equal(0, KeyboardInset.Overlap(728.9, new Rect(0, 832, 384, 0)));

    /// <summary>
    /// A short keyboard (a floating or a hardware-assisted one) can end below the content area.
    /// The area must not be pushed down.
    /// </summary>
    [Fact]
    public void A_keyboard_below_the_area_lifts_nothing() =>
        Assert.Equal(0, KeyboardInset.Overlap(728.9, new Rect(0, 780, 384, 52)));

    /// <summary>
    /// The shell must listen to the keyboard while it is on screen, and stop when it leaves, so a
    /// shell that is gone is not kept alive by the input pane.
    /// </summary>
    [Fact]
    public void The_shell_listens_to_the_keyboard_and_stops_when_it_leaves()
    {
        var code = BindingFacts.CodeOnly(BindingFacts.Source(MainView));
        Assert.Contains("StateChanged += OnInputPaneStateChanged", code, StringComparison.Ordinal);
        Assert.Contains("StateChanged -= OnInputPaneStateChanged", code, StringComparison.Ordinal);
        Assert.Contains("KeyboardInset.Overlap(", code, StringComparison.Ordinal);
        Assert.Contains("BringIntoView()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The address box must open the number pad that has the dot, and the port box a digit pad.
    /// The default letter keyboard made the user change pages for every digit.
    /// </summary>
    [Fact]
    public void The_address_and_port_boxes_open_a_number_pad()
    {
        var view = BindingFacts.Source(ShellView);
        Assert.Matches(@"Text=""\{Binding ManualIp\}""\s+TextInputOptions\.ContentType=""Number""", view);
        Assert.Matches(@"Text=""\{Binding ManualPort\}""\s+TextInputOptions\.ContentType=""Digits""", view);
    }
}
