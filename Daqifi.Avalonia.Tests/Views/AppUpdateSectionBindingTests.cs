using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// The UPDATES section of the settings drawer (#346). <c>MainWindow.axaml</c> compiles its bindings,
/// so the compiler checks each member name against <c>AppUpdateViewModel</c>. It does not check which
/// attribute a binding feeds, and a deleted <c>IsVisible</c> binding leaves the control at its default,
/// which is visible: "Check now" would then sit beside an offered update, or the update state would
/// show on a platform that does not update itself. These rows pin those three.
/// </summary>
public class AppUpdateSectionBindingTests
{
    private const string Drawer = "Daqifi.Avalonia/Daqifi.Desktop/MainWindow.axaml";

    [Theory]
    [InlineData("IsVisible=\"{Binding AppUpdate.HasStatus}\"")]
    [InlineData("IsVisible=\"{Binding AppUpdate.CanCheckNow}\"")]
    [InlineData("IsVisible=\"{Binding AppUpdate.HasAction}\"")]
    public void The_section_hides_what_does_not_apply(string binding) =>
        BindingFacts.AssertBinds(Drawer, binding);

    /// <summary>The mobile heads update through their app stores, so their settings have no UPDATES section.</summary>
    [Fact]
    public void The_mobile_settings_have_no_update_section() =>
        Assert.DoesNotContain(
            "AppUpdate", BindingFacts.Source("Daqifi.Avalonia/Views/MobileMainView.axaml"), StringComparison.Ordinal);
}
