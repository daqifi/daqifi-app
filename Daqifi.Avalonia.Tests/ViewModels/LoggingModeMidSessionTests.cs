using System.ComponentModel;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #536: a change of logging mode during a session stopped all recording while the toggle
/// kept reading LOGGING ON.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="DaqifiViewModel.SelectedLoggingMode"/> setter did not read
/// <see cref="DaqifiViewModel.IsLogging"/>. It called <c>LoggingFleet.SwitchMode</c>, which stops
/// each device's stream or SD log before it changes the mode, and then wrote
/// <c>LoggingManager.CurrentMode</c>. Toward LOG TO DEVICE that write set
/// <c>LoggingManager.Active</c> false and closed the session; toward STREAM TO APP the app had no
/// database session to write to. Nothing set the toggle OFF.
/// </para>
/// <para>
/// The fix refuses the change while a session is ON. These tests pin both directions of the
/// refusal and the direction that must still work: with no session, a mode change behaves as
/// before.
/// </para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public class LoggingModeMidSessionTests : IDisposable
{
    private const string StreamToApp = "Stream to App";
    private const string LogToDevice = "Log to Device";
    private const string RefusalMessage = "Stop logging before you change the logging mode.";

    private readonly DaqifiViewModel _viewModel;

    public LoggingModeMidSessionTests()
    {
        // Gives LoggingManager.Instance its context factory, against the throwaway data directory
        // the assembly's module initializer already points DAQIFI_DATA_DIR at. Idempotent.
        Daqifi.Desktop.App.InitializeMobile();

        // LoggingManager is a process-wide singleton, and other tests leave it in SD-card mode. A
        // new view model starts at "Stream to App", so put the manager there too.
        LoggingManager.Instance.Active = false;
        LoggingManager.Instance.CurrentMode = LoggingMode.Stream;

        _viewModel = new DaqifiViewModel(new NullDialogService());
    }

    public void Dispose()
    {
        try
        {
            LoggingManager.Instance.Active = false;
            LoggingManager.Instance.CurrentMode = LoggingMode.Stream;
        }
        catch
        {
            // best-effort cleanup of the singleton
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The names of the properties the view model announces while <paramref name="act"/> runs.</summary>
    private List<string?> Announced(Action act)
    {
        var names = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => names.Add(e.PropertyName);
        _viewModel.PropertyChanged += handler;
        try
        {
            act();
        }
        finally
        {
            _viewModel.PropertyChanged -= handler;
        }

        return names;
    }

    /// <summary>
    /// Procedure A of the issue: a streaming session is ON and the user clicks LOG TO DEVICE.
    /// </summary>
    [Fact]
    public void ChangingToLogToDevice_WhileStreamingASession_IsRefused()
    {
        var device = new RecordingStreamingDevice("Nq1-A", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(device);

        _viewModel.IsLogging = true;
        Assert.True(LoggingManager.Instance.Active);
        device.Commands.Clear();

        var announced = Announced(() => _viewModel.SelectedLoggingMode = LogToDevice);

        // The device was not told anything, so it is still streaming...
        Assert.Empty(device.Commands);
        Assert.Equal(DeviceMode.StreamToApp, device.Mode);

        // ...the session is still open and the app still says what it is doing...
        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);
        Assert.Equal(LoggingMode.Stream, LoggingManager.Instance.CurrentMode);
        Assert.Equal(StreamToApp, _viewModel.SelectedLoggingMode);
        Assert.False(_viewModel.IsLogToDeviceMode);

        // ...the radio buttons are told to move back: they bind IsLogToDeviceMode, one way...
        Assert.Contains(nameof(DaqifiViewModel.SelectedLoggingMode), announced);
        Assert.Contains(nameof(DaqifiViewModel.IsLogToDeviceMode), announced);

        // ...and the user is told why.
        Assert.Contains(_viewModel.NotificationList, n => n.Message == RefusalMessage);
    }

    /// <summary>
    /// Procedure B of the issue: an SD-card session is ON and the user clicks STREAM TO APP. The
    /// device must not be told to stop its SD log.
    /// </summary>
    [Fact]
    public void ChangingToStreamToApp_WhileLoggingToTheDevice_IsRefused()
    {
        var device = new RecordingStreamingDevice("Nq1-B", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(device);

        _viewModel.SelectedLoggingMode = LogToDevice;
        _viewModel.IsLogging = true;
        Assert.True(device.IsLoggingToSdCard);
        device.Commands.Clear();

        var announced = Announced(() => _viewModel.SelectedLoggingMode = StreamToApp);

        Assert.Empty(device.Commands);
        Assert.Equal(DeviceMode.LogToDevice, device.Mode);
        Assert.True(device.IsLoggingToSdCard);

        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);
        Assert.Equal(LoggingMode.SdCard, LoggingManager.Instance.CurrentMode);
        Assert.Equal(LogToDevice, _viewModel.SelectedLoggingMode);
        Assert.True(_viewModel.IsLogToDeviceMode);

        Assert.Contains(nameof(DaqifiViewModel.SelectedLoggingMode), announced);
        Assert.Contains(nameof(DaqifiViewModel.IsLogToDeviceMode), announced);
        Assert.Contains(_viewModel.NotificationList, n => n.Message == RefusalMessage);
    }

    /// <summary>
    /// A session the toggle did not start: a device that kept logging to its SD card across a
    /// reconnect. <see cref="DaqifiViewModel.IsLogging"/> reads true from the device, and the
    /// toggle shows ON, so the refusal must read the same property the toggle reads — not only the
    /// flag the toggle writes.
    /// </summary>
    [Fact]
    public void ChangingMode_WhileADeviceLogsToItsSdCardOnItsOwn_IsRefused()
    {
        var device = new RecordingStreamingDevice("Nq1-SD", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(device);
        _viewModel.SelectedLoggingMode = LogToDevice;

        // The device logs, but the toggle was never set: the reconnect case.
        device.StartSdCardLogging();
        device.Commands.Clear();
        Assert.True(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);

        _viewModel.SelectedLoggingMode = StreamToApp;

        Assert.Empty(device.Commands);
        Assert.True(device.IsLoggingToSdCard);
        Assert.Equal(LogToDevice, _viewModel.SelectedLoggingMode);
        Assert.Contains(_viewModel.NotificationList, n => n.Message == RefusalMessage);
    }

    /// <summary>
    /// The direction that must keep working: with no session, both mode changes reach the device
    /// and change the app, and nothing is reported.
    /// </summary>
    [Fact]
    public void ChangingMode_WithNoSession_WorksInBothDirections()
    {
        var device = new RecordingStreamingDevice("Nq1-IDLE", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(device);

        _viewModel.SelectedLoggingMode = LogToDevice;

        Assert.Equal(DeviceMode.LogToDevice, device.Mode);
        Assert.Equal(LogToDevice, _viewModel.SelectedLoggingMode);
        Assert.True(_viewModel.IsLogToDeviceMode);
        Assert.Equal(LoggingMode.SdCard, LoggingManager.Instance.CurrentMode);

        _viewModel.SelectedLoggingMode = StreamToApp;

        Assert.Equal(DeviceMode.StreamToApp, device.Mode);
        Assert.Equal(StreamToApp, _viewModel.SelectedLoggingMode);
        Assert.False(_viewModel.IsLogToDeviceMode);
        Assert.Equal(LoggingMode.Stream, LoggingManager.Instance.CurrentMode);

        Assert.Equal(["SwitchMode(LogToDevice)", "SwitchMode(StreamToApp)"], device.Commands);
        Assert.Empty(_viewModel.NotificationList);
    }

    /// <summary>
    /// The refusal belongs to the session, not to the view model: once the user stops logging, the
    /// same click changes the mode.
    /// </summary>
    [Fact]
    public void ChangingMode_AfterTheSessionStops_Works()
    {
        var device = new RecordingStreamingDevice("Nq1-AFTER", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(device);

        _viewModel.IsLogging = true;
        _viewModel.SelectedLoggingMode = LogToDevice;
        Assert.Equal(StreamToApp, _viewModel.SelectedLoggingMode);

        _viewModel.IsLogging = false;
        device.Commands.Clear();

        _viewModel.SelectedLoggingMode = LogToDevice;

        Assert.Equal(["SwitchMode(LogToDevice)"], device.Commands);
        Assert.Equal(LogToDevice, _viewModel.SelectedLoggingMode);
        Assert.True(_viewModel.IsLogToDeviceMode);
    }
}
