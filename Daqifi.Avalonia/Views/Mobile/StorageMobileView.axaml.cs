using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Storage tab pivot host (#7): APP LOGS (locally-logged sessions) / DEVICE LOGS
/// (SD-card offload). Each half owns its own DataContext; the SD half is built
/// lazily on first open, mirroring the shell's lazy pane pattern.
/// </summary>
public partial class StorageMobileView : UserControl
{
    private bool _deviceLogsReady;

    public StorageMobileView()
    {
        InitializeComponent();
        AppLogsHost.DataContext = new LoggedSessionsMobileViewModel();
    }

    /// <summary>Re-hydrate the locally-logged session list. Called each time the APP LOGS half
    /// becomes visible: by the shell when the Storage pane is shown, and by <see cref="OnAppLogs"/>
    /// when the user pivots back from DEVICE LOGS. The pane is cached (built once), so its ctor
    /// reload runs only once — without this, a session logged since the last visit stays hidden
    /// until the user manually taps Refresh.</summary>
    public void Refresh()
    {
        if (AppLogsHost.DataContext is LoggedSessionsMobileViewModel vm && vm.ReloadCommand.CanExecute(null))
        {
            vm.ReloadCommand.Execute(null);
        }
    }

    private void OnAppLogs(object? sender, RoutedEventArgs e)
    {
        AppLogsHost.IsVisible = true;
        DeviceLogsHost.IsVisible = false;

        // The pivot is the one route into this half that the shell's Refresh does not cover, and
        // it is the route an SD import takes: importing happens on the DEVICE LOGS half, over its
        // own DataContext, and nothing bridges the two (#463). Without this the user comes back to
        // "NO LOGGED SESSIONS" and is told the import produced nothing, when it produced a session
        // that is already persisted. Reload is safe to re-run — it reads GetLoggingSessionsSnapshot,
        // which only SELECTs and purges nothing — and it is also what drops a viewer or confirm
        // overlay left open before the pivot, which LoggedSessionsMobileViewModel.Reload's own
        // comment already assumes happens on this path.
        Refresh();
    }

    private void OnDeviceLogs(object? sender, RoutedEventArgs e)
    {
        // Build the SD pane on demand. Its DeviceLogsViewModel touches the device layer,
        // which can throw if that layer isn't ready — so fall back to a placeholder rather
        // than letting this click handler crash the app (mirrors the shell's defensive
        // BuildPage pattern). A failed build stays RETRYABLE: we only latch _deviceLogsReady
        // on success, so a later tap rebuilds once the layer is available (the placeholder
        // is not cached as the permanent content).
        if (!_deviceLogsReady)
        {
            try
            {
                DeviceLogsHost.Content = new DeviceLogsMobileView { DataContext = new DeviceLogsViewModel() };
                _deviceLogsReady = true;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "Mobile: failed to build the Device Logs pane");
                DeviceLogsHost.Content = new TextBlock
                {
                    Text = "Device logs are unavailable right now. Tap again to retry.",
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
        }
        AppLogsHost.IsVisible = false;
        DeviceLogsHost.IsVisible = true;
    }
}
