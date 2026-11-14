using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #493, review thread on <c>DeviceLogsViewModel.cs</c>: the WiFi module notice names the
/// board by the serial the user sees everywhere else — the hex form <c>*IDN?</c> reports — while
/// the notice still carries the stored decimal serial, which is what de-duplication and the
/// per-device pruning compare.
/// </summary>
/// <remarks>
/// The probe that calls <c>AddWifiNotification</c> talks to a board, so these call the method
/// directly, as <c>ScaleResultsRejectedNoticeTests</c> does for its notice.
/// </remarks>
[Collection(AppHostCollection.Name)]
public class WifiModuleNoticeSerialTests
{
    private const string BenchDecimal = "9090539562006014104";
    private const string BenchIdnHex = "7E2815916200E898";

    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    private static SerialStreamingDevice Board(string serial, string friendlyName = "") =>
        new("COM9") { DeviceSerialNo = serial, FriendlyName = friendlyName };

    [Fact]
    public void The_notice_names_the_board_by_its_hex_serial()
    {
        var viewModel = NewViewModel();

        viewModel.AddWifiNotification(Board(BenchDecimal), "Unknown");

        var notice = Assert.Single(viewModel.NotificationList);
        Assert.StartsWith($"Device {BenchIdnHex}: WiFi module firmware could not be read", notice.Message);
        Assert.DoesNotContain(BenchDecimal, notice.Message);
    }

    [Fact]
    public void The_notice_keeps_the_stored_serial_as_its_identity()
    {
        var viewModel = NewViewModel();

        viewModel.AddWifiNotification(Board(BenchDecimal), "Unknown");

        Assert.Equal(BenchDecimal, Assert.Single(viewModel.NotificationList).DeviceSerialNo);
    }

    [Fact]
    public void A_second_probe_of_the_same_board_is_still_one_notice()
    {
        var viewModel = NewViewModel();

        viewModel.AddWifiNotification(Board(BenchDecimal), "Unknown");
        viewModel.AddWifiNotification(Board(BenchDecimal), "19.5.4");

        var notice = Assert.Single(viewModel.NotificationList);
        Assert.StartsWith($"Device {BenchIdnHex}: WiFi module firmware is out of date (19.5.4)", notice.Message);
    }

    /// <summary>
    /// The notice is about one serial, so it uses the serial and not the friendly name; the rule is
    /// the display rule for a serial and nothing else.
    /// </summary>
    [Fact]
    public void A_board_whose_serial_has_no_hex_form_is_named_as_stored()
    {
        var viewModel = NewViewModel();

        viewModel.AddWifiNotification(Board("SN-0001", friendlyName: "12345"), "Unknown");

        Assert.StartsWith("Device SN-0001: ", Assert.Single(viewModel.NotificationList).Message);
    }
}
