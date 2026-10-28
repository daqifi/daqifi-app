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

    /// <summary>Re-hydrate the locally-logged session list. Called by the shell each time the
    /// Storage pane is shown, and by <see cref="OnAppLogs"/> each time the user pivots back to
    /// this half: the pane is cached (built once), so its ctor reload runs only once — without
    /// this, a session logged or imported since the last visit stays hidden until the user
    /// manually taps Refresh.</summary>
    /// <remarks>
    /// Safe to call as often as this. <c>LoggedSessionsMobileViewModel.Reload</c> reads through
    /// <c>GetLoggingSessionsSnapshot</c>, which only SELECTs — it does not purge empty-session
    /// rows the way <c>LoadPersistedLoggingSessions</c> does, so a reload cannot disturb an
    /// import that is still in flight.
    /// </remarks>
    public void Refresh()
    {
        if (AppLogsHost.DataContext is LoggedSessionsMobileViewModel vm && vm.ReloadCommand.CanExecute(null))
        {
            vm.ReloadCommand.Execute(null);
        }
    }

    private void OnAppLogs(object? sender, RoutedEventArgs e)
    {
        // Reload on the way back in, not just when the Storage TAB is entered (#463). The two
        // halves own separate DataContexts and nothing bridges them, so an SD import — which can
        // only be started from the other half — leaves this list exactly as it was when the pane
        // was last shown. It read "NO LOGGED SESSIONS" directly after an import of 4,876 samples,
        // telling the user their import produced nothing. Refresh() already existed for this
        // failure; it was simply never wired to the one route that reaches it.
        Refresh();
        AppLogsHost.IsVisible = true;
        DeviceLogsHost.IsVisible = false;
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
