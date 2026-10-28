using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Device.UsbDevice;
using Daqifi.Desktop.Device.WiFiDevice;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// What the CONNECTED DEVICE dropdown puts on screen for each transport (#462).
///
/// <para>
/// The defect was not a wrong binding — it was no binding. A <c>ComboBox</c> told neither
/// <c>DisplayMemberBinding</c> nor <c>ItemTemplate</c> renders each row with <c>ToString()</c>, and
/// of the device types the app can hand that list only <see cref="DaqifiStreamingDevice"/> (WiFi)
/// overrides it. So the mobile Storage tab's dropdown read
/// <c>Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice</c> the moment the USB path became
/// reachable again (#460) — and that one WiFi override is why months of mobile use never showed it.
/// </para>
///
/// <para>
/// Which is why this asserts the rendered text rather than the markup.
/// <see cref="WhatTheComboRenders"/> finds the view's own device ComboBox and resolves a row the
/// way Avalonia does — the display-member path when the view sets one, <c>ToString()</c> when it
/// sets nothing — so the guard fails against the old markup instead of merely describing the new.
/// A binding-text assertion ("DisplayMemberBinding is present and reads DeviceDisplayName") is
/// satisfied by a device type whose display name comes back blank, or still a type name, which is
/// the half that matters for the next transport to reach this control.
/// </para>
///
/// <para>
/// Both views over <c>DeviceLogsViewModel</c> are covered: the mobile one this was reported
/// against, and the desktop one that has bound <c>DeviceDisplayName</c> all along — the fact is
/// the same fact on either head, and pinning only the broken one invites the next divergence.
/// </para>
/// </summary>
public class DeviceSelectorDisplayNameTests
{
    private const string MobileView = "Daqifi.Avalonia/Views/Mobile/DeviceLogsMobileView.axaml";
    private const string DesktopView = "Daqifi.Avalonia/Daqifi.Desktop/View/DeviceLogsView.axaml";

    /// <summary>The connected-device list both views bind, and how the ComboBox is found.</summary>
    private const string DeviceList = "{Binding ConnectedDevices}";

    /// <summary>
    /// A whole-path binding with nothing else in it — no converter, no compound path. Anything
    /// richer is not resolvable by reflection alone, so this test says so rather than guessing.
    /// </summary>
    private static readonly Regex WholePathBinding =
        new(@"^\{Binding\s+(?<member>[A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every view that shows the dropdown, against every device type that can appear in it — one
    /// case per pair rather than a loop, so a transport that renders wrongly is its own red test
    /// instead of being hidden behind whichever one the loop reached first.
    /// </summary>
    public static TheoryData<string, string> EveryViewAndDevice()
    {
        var pairs = new TheoryData<string, string>();

        foreach (var view in new[] { MobileView, DesktopView })
        {
            foreach (var device in EveryConcreteDevice())
            {
                pairs.Add(view, device.GetType().FullName!);
            }
        }

        return pairs;
    }

    [Theory]
    [MemberData(nameof(EveryViewAndDevice))]
    public void The_device_dropdown_names_every_transport_it_can_be_handed(string view, string deviceType)
    {
        var device = EveryConcreteDevice().Single(candidate => candidate.GetType().FullName == deviceType);
        var type = device.GetType();
        var rendered = WhatTheComboRenders(view, device);

        Assert.False(
            string.IsNullOrWhiteSpace(rendered),
            $"{view}: the device dropdown renders a blank row for {type.Name}.");

        Assert.False(
            RendersItsOwnTypeName(rendered, type),
            $"{view}: the device dropdown renders {type.Name} as '{rendered}' — its own CLR type "
            + "name, which is what the row falls back to when the view tells the ComboBox nothing "
            + "and the item does not override ToString().");
    }

    /// <summary>
    /// The trap the fix was chosen to close, held open: overriding <c>ToString()</c> on one device
    /// would have cured the reported symptom and left the next transport to rediscover it. This
    /// fails when a device type is added to the app without being brought into the roster above,
    /// so "it renders a name" is re-asked of every transport rather than of the three that existed.
    /// </summary>
    [Fact]
    public void Every_device_type_the_app_can_put_in_that_list_is_covered_here()
    {
        var inTheApp = typeof(AbstractStreamingDevice).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && typeof(IStreamingDevice).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var covered = EveryConcreteDevice()
            .Select(device => device.GetType().FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(inTheApp, covered);
    }

    /// <summary>
    /// One instance of every concrete device type, each in the state that exercises the fallback
    /// chain rather than hiding it: no friendly name and no serial number, so
    /// <see cref="AbstractStreamingDevice.DeviceDisplayName"/> has to reach its last resort
    /// (<c>DisplayIdentifier</c>) to produce anything at all. The hardware report that opened #462
    /// had a serial number to fall back on; this is the harder case, one step further down.
    /// </summary>
    private static IEnumerable<IStreamingDevice> EveryConcreteDevice() =>
    [
        new DaqifiStreamingDevice(IPAddress.Parse("192.168.1.50"), 9760, "Nq1"),
        new SerialStreamingDevice("COM9"),
        new UsbStreamingDevice(new CapturingTransport(), "DAQiFi Nq1"),
    ];

    /// <summary>
    /// The text Avalonia will put in a dropdown row for <paramref name="device"/>, derived from the
    /// view's markup instead of assumed: the display-member path when the ComboBox declares one,
    /// and <c>ToString()</c> — Avalonia's own fallback, and the whole of this bug — when it does not.
    /// </summary>
    private static string WhatTheComboRenders(string view, IStreamingDevice device)
    {
        var combo = DeviceCombo(view);

        Assert.True(
            combo.Attribute("ItemTemplate") is null
            && combo.Elements().All(child => child.Name.LocalName != $"{combo.Name.LocalName}.ItemTemplate"),
            $"{view}: the device ComboBox declares an ItemTemplate, which takes precedence over "
            + "DisplayMemberBinding. This test no longer models what a row renders — teach it the "
            + "template, or keep the display-member binding.");

        if (combo.Attribute("DisplayMemberBinding")?.Value is not { } binding)
        {
            return device.ToString() ?? string.Empty;
        }

        var match = WholePathBinding.Match(binding);
        Assert.True(
            match.Success,
            $"{view}: the device ComboBox's DisplayMemberBinding is '{binding}', which this test "
            + "cannot resolve by reflection. Only a whole-path binding is modelled here.");

        var member = match.Groups["member"].Value;
        var property = device.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance);

        Assert.True(
            property?.CanRead == true,
            $"{view}: DisplayMemberBinding reads '{member}', which {device.GetType().Name} does not "
            + "expose as a readable public property — the row renders blank at runtime.");

        return property!.GetValue(device)?.ToString() ?? string.Empty;
    }

    /// <summary>The one ComboBox in a view that is handed the connected-device list.</summary>
    private static XElement DeviceCombo(string view)
    {
        var root = XDocument.Parse(BindingFacts.Source(view)).Root
                   ?? throw new InvalidOperationException($"{view} has no root element.");

        var combos = root.DescendantsAndSelf()
            .Where(element => element.Attribute("ItemsSource")?.Value == DeviceList)
            .ToList();

        Assert.True(
            combos.Count == 1,
            $"{view}: {combos.Count} elements are handed {DeviceList}, expected exactly one.");

        return combos[0];
    }

    /// <summary>
    /// Whether a rendered row is just the item's own type name — the shape of
    /// <c>object.ToString()</c>, in both the fully-qualified and the bare spelling.
    /// </summary>
    private static bool RendersItsOwnTypeName(string rendered, Type type) =>
        string.Equals(rendered, type.FullName, StringComparison.Ordinal)
        || string.Equals(rendered, type.Name, StringComparison.Ordinal);
}
