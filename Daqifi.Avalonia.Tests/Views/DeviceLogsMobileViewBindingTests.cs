using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>DeviceLogsMobileView.axaml</c>'s 31 bindings the XAML compiler's business rather
/// than nobody's (issue #327). The compiler owns the member names; what it cannot see is the declarations
/// being dropped, or an escape hatch reopened beneath them.
/// </summary>
public class DeviceLogsMobileViewBindingTests
{
    private const string View = "Daqifi.Avalonia/Views/Mobile/DeviceLogsMobileView.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 31 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DeviceLogsViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);

    /// <summary>
    /// Issue #462: the CONNECTED DEVICE ComboBox declared no display member, so Avalonia rendered
    /// each item with <c>ToString()</c> and the pane read
    /// <c>Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice</c> — a CLR type name in front of the
    /// user. The WiFi device happens to override <c>ToString()</c>, which is why this survived for
    /// as long as WiFi was the only transport the mobile pane could reach.
    ///
    /// <para>
    /// This guard is deliberately textual, which is the opposite of the lesson from #460 — and the
    /// difference is worth stating, because the two look alike. There the binding existed and was
    /// correct, and only the value it read was wrong, so a text assertion was blind to it. Here the
    /// ABSENCE of a declaration is the whole defect, and text is exactly what can see that. Tests
    /// over <c>DeviceDisplayName</c> itself cannot: they pass against the broken markup, because
    /// the property was always right and nothing was reading it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_device_selector_renders_a_name_and_not_a_type() =>
        BindingFacts.AssertBinds(
            View, "DisplayMemberBinding=\"{Binding DeviceDisplayName, x:DataType=device:IStreamingDevice}\"");
}
