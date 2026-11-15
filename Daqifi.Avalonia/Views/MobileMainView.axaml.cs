using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// Orientation-adaptive mobile shell. LANDSCAPE renders the desktop's left nav
/// rail + content (~1:1 with the desktop app); PORTRAIT falls back to a bottom
/// tab bar. The content area is SHARED between both layouts (Grid col 1, row 0) —
/// only the nav chrome swaps, so the live Stream view and the lazily-built panes
/// are never re-parented or torn down on rotation.
///
/// The Stream tab stays ALWAYS attached (IsVisible-toggled) so switching panes
/// never disposes its ViewModel / drops the connection. Secondary panes build
/// lazily + defensively: some shared pane ViewModels need the mobile data-layer
/// host (App.InitializeMobile); if one fails to construct, a placeholder shows
/// instead of crashing (only the Stream tab needs no data layer).
/// </summary>
public partial class MobileMainView : UserControl
{
    private Control? _storagePage;
    private Control? _channelsPage;
    private Control? _profilesPage;
    private TopLevel? _backSource;
    private Button[] _navButtons = Array.Empty<Button>();
    private Button[] _railButtons = Array.Empty<Button>();
    private IActivatableLifetime? _activatable;
    private IInputPane? _inputPane;

    // Standalone, WPF-free notifications VM (like the mobile SettingsViewModel). Shared between the
    // top-bar bell badge and the Notifications overlay list (#11).
    private readonly MobileNotificationsViewModel _notifications = new();

    public MobileMainView()
    {
        InitializeComponent();
        // Both the bell badge (in TopCommandBar) and the overlay list bind to the one VM instance.
        TopCommandBar.DataContext = _notifications;
        NotificationsOverlay.DataContext = _notifications;

        // App.InitializeMobile has already run by the time this view is constructed, so if it found
        // DAQiFiDatabase.db unreadable and moved it aside, that is settled and can be reported now.
        // Without this the phone's Storage pane is simply empty, which reads as deleted data rather
        // than a damaged file the app preserved. Same sentence the desktop notification uses.
        if (DatabaseMigrator.DescribeQuarantineForUser() is { } quarantineNotice)
        {
            _notifications.Add(new Notifications { Message = quarantineNotice });
        }
        _navButtons = [NavStream, NavStorage, NavChannels, NavProfiles];
        _railButtons = [RailStream, RailStorage, RailChannels, RailProfiles];
        ShowStream();
        SizeChanged += (_, _) => UpdateOrientation();
        UpdateOrientation();
    }

    /// <summary>
    /// The phone's half of #540: a channel's scale expression had no valid result for a sample, and
    /// the app dropped it. Subscribed while the shell is on screen, so a view the capture harness
    /// builds and throws away does not stay rooted by the static event.
    /// </summary>
    /// <remarks>
    /// Also listens for the app going to the background (#560). Android can stop a background app
    /// at any time after that and gives no further signal, so it is the last point an edit left
    /// open in the Profiles drawer can be saved. No lifetime offers the feature in the capture
    /// harness, so there it is simply not subscribed.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The Android Back gesture (#588). Only a phone top level raises it.
        _backSource = TopLevel.GetTopLevel(this);
        if (_backSource is not null) { _backSource.BackRequested += OnBackRequested; }
        AbstractChannel.ScaleResultsRejected += OnScaleResultsRejected;
        // The on-screen keyboard (#589). Only a phone top level has an input pane.
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane is not null) { _inputPane.StateChanged += OnInputPaneStateChanged; }
        _activatable = Application.Current?.TryGetFeature<IActivatableLifetime>();
        if (_activatable is not null) { _activatable.Deactivated += OnAppDeactivated; }
        if (_activatable is not null) { _activatable.Activated += OnAppActivated; }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_backSource is not null) { _backSource.BackRequested -= OnBackRequested; }
        _backSource = null;
        AbstractChannel.ScaleResultsRejected -= OnScaleResultsRejected;
        if (_inputPane is not null) { _inputPane.StateChanged -= OnInputPaneStateChanged; }
        _inputPane = null;
        if (_activatable is not null) { _activatable.Deactivated -= OnAppDeactivated; }
        if (_activatable is not null) { _activatable.Activated -= OnAppActivated; }
        _activatable = null;
        // A destroyed activity takes the shell with it; save the open edit as desktop does when its
        // pane detaches (#560).
        SaveProfileEdit();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The on-screen keyboard opened, closed or changed its height (#589).
    /// </summary>
    /// <remarks>
    /// On Android 15 and later the app is edge-to-edge, and the window does not resize for the
    /// keyboard. Without this the keyboard covers the manual IP row and its Connect button, and the
    /// user types the address blind. The content area is lifted by the part of it that the keyboard
    /// covers, so each pane's scroll viewer shrinks to the visible part of the screen. Then the
    /// focused text box is scrolled into view: it got the focus before the keyboard opened, when it
    /// was still on screen, so the scroll viewer had nothing to do at that time. The bottom nav stays
    /// under the keyboard, as in a native app.
    /// </remarks>
    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { return; }

        // The slot's bottom edge, not the area's: the margin below moves the area's own bottom edge.
        var slotBottom = ContentArea.TranslatePoint(
            new Point(0, ContentArea.Bounds.Height + ContentArea.Margin.Bottom), top);
        if (slotBottom is null) { return; }

        var keyboard = e.NewState == InputPaneState.Open ? e.EndRect : default;
        var overlap = KeyboardInset.Overlap(slotBottom.Value.Y, keyboard);
        ContentArea.Margin = new Thickness(0, 0, 0, overlap);
        if (overlap <= 0) { return; }

        // After the layout pass that the new margin starts, so the scroll viewer has its new height.
        Dispatcher.UIThread.Post(
            () => (top.FocusManager?.GetFocusedElement() as Control)?.BringIntoView(),
            DispatcherPriority.Background);
    }

    /// <summary>The app went to the background: HOME, the app switcher, a call (#560).</summary>
    private void OnAppDeactivated(object? sender, ActivatedEventArgs e)
    {
        SaveProfileEdit();
    }

    /// <summary>
    /// The app came back to the foreground (#590). A layout change made while it was in the
    /// background (the drawer that <see cref="OnAppDeactivated"/> closes, or the busy overlay that
    /// changes while the save picker is open) was arranged 0 wide, and the pane showed nothing
    /// until a rotation. This lays out the whole shell again, as a rotation does.
    /// </summary>
    /// <remarks>
    /// <see cref="ResumeRelayout.ScheduleAfterResume"/> says when, and why more than once.
    /// </remarks>
    private void OnAppActivated(object? sender, ActivatedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top) { ResumeRelayout.ScheduleAfterResume(top, this); }
    }

    /// <summary>
    /// Saves an edit left open in the Profiles drawer (#560). The Profiles page is built once and
    /// kept, so leaving it never detaches it and desktop's <c>Cleanup</c> never runs; this goes
    /// through the close button's path instead. A failed write keeps the drawer open with the
    /// reason, and the reason also goes to the notifications, because the drawer is not on screen.
    /// <c>AddOnce</c>: every later tab change tries again and would stack copies of one sentence.
    /// </summary>
    private void SaveProfileEdit()
    {
        if (_profilesPage is { DataContext: ProfilesPaneViewModel profiles } &&
            MobileProfileEdit.SaveOnLeave(profiles) is { } unsaved)
        {
            AppLogger.Instance.Warning(unsaved);
            _notifications.AddOnce(new Notifications { Message = unsaved });
        }
    }

    /// <summary>
    /// Raised from the streaming thread. <see cref="MobileNotificationsViewModel.AddOnce"/>
    /// marshals to the UI thread and does not stack a second copy of the same sentence.
    /// </summary>
    private void OnScaleResultsRejected(AbstractChannel channel) =>
        _notifications.AddOnce(new Notifications { Message = channel.RejectedScaleResultsNotification });

    /// <summary>Landscape → desktop-style left rail; portrait → bottom nav.</summary>
    private void UpdateOrientation()
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) { return; }
        var landscape = b.Width > b.Height;
        SideNav.IsVisible = landscape;
        BottomNav.IsVisible = !landscape;
    }

    private void ShowStream()
    {
        SaveProfileEdit();
        StreamView.IsVisible = true;
        SecondaryHost.IsVisible = false;
        SetActive(0);
    }

    private void ShowSecondary(Control page, int index)
    {
        // Leaving Profiles for another tab hides the cached page with its drawer still open (#560).
        // Also on a tap of Profiles itself: the edit is saved and the user sees the list.
        SaveProfileEdit();
        SecondaryHost.Content = page;
        StreamView.IsVisible = false;
        SecondaryHost.IsVisible = true;
        SetActive(index);
    }

    /// <summary>Opens the DAQiFi support page via the platform launcher (the
    /// desktop top bar's help button; Process.Start on desktop → LaunchUriAsync
    /// cross-platform).</summary>
    private async void OnHelp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri("https://www.daqifi.com/support"));
            }
        }
        catch
        {
            // best-effort: a missing browser / launcher must not crash the shell
        }
    }

    /// <summary>Opens the settings overlay (#11) — the mobile analog of the desktop
    /// settings drawer. Binds a standalone <see cref="SettingsViewModel"/> (backed by
    /// DaqifiSettings.Instance, no DaqifiViewModel host needed).</summary>
    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        // Only one full-screen overlay at a time — close the other so keyboard/switch-access
        // navigation can't leave both stacked (their scrims block touch but not Tab focus).
        NotificationsOverlay.IsVisible = false;
        SettingsOverlay.DataContext ??= new SettingsViewModel();
        SettingsOverlay.IsVisible = true;
    }

    private void OnCloseSettings(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.IsVisible = false;

    /// <summary>Opens the notifications overlay (#11) — the mobile analog of the desktop bell +
    /// flyout. The overlay's DataContext (the shared notifications VM) is set in the ctor.</summary>
    private void OnNotifications(object? sender, RoutedEventArgs e)
    {
        // Mutually exclusive with the settings overlay (see OnSettings).
        SettingsOverlay.IsVisible = false;
        NotificationsOverlay.IsVisible = true;
    }

    private void OnCloseNotifications(object? sender, RoutedEventArgs e) =>
        NotificationsOverlay.IsVisible = false;

    /// <summary>
    /// The Android Back gesture or key (#588). Closes the top-most open item and marks the event
    /// handled. With nothing open on Live Graph the event stays unhandled, and Android moves the app
    /// to the background, as before.
    /// </summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (CloseTopmost()) { e.Handled = true; }
    }

    /// <summary>
    /// Closes ONE item, the top-most first, and returns <c>true</c> when it did: the settings or
    /// notifications overlay (they cover the whole shell), then an item inside the visible pane
    /// (<see cref="MobileBackNavigation"/>), then a secondary tab, which goes back to Live Graph.
    /// The hidden panes are not touched: an item the user cannot see is not the top-most one.
    /// </summary>
    private bool CloseTopmost()
    {
        if (SettingsOverlay.IsVisible) { SettingsOverlay.IsVisible = false; return true; }
        if (NotificationsOverlay.IsVisible) { NotificationsOverlay.IsVisible = false; return true; }
        if (!SecondaryHost.IsVisible) { return false; }

        var closedInPane = SecondaryHost.Content switch
        {
            Control { DataContext: ProfilesPaneViewModel profiles } => MobileBackNavigation.Close(profiles),
            Control { DataContext: ChannelsPaneViewModel channels } => MobileBackNavigation.Close(channels),
            Mobile.StorageMobileView { VisibleAppLogs: { } sessions } => MobileBackNavigation.Close(sessions),
            _ => false,
        };
        if (!closedInPane) { ShowStream(); }
        return true;
    }

    /// <summary>Opens a notification's link (e.g. a firmware "learn more") via the platform launcher,
    /// mirroring the desktop flyout's link button. Only http/https schemes are launched (never file:,
    /// intent:, javascript:, …); failures are logged, not swallowed, and never crash the shell.</summary>
    private async void OnNotificationLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) { return; }

        // Restrict to web links — a notification's Link is data, so refuse a malformed URI or any
        // non-http(s) scheme. Distinct messages so a bad link is diagnosable.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AppLogger.Instance.Warning($"Ignored malformed notification link: {url}");
            return;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            AppLogger.Instance.Warning($"Ignored notification link with unsupported scheme '{uri.Scheme}': {url}");
            return;
        }

        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                AppLogger.Instance.Warning("No platform launcher available to open a notification link.");
                return;
            }
            // LaunchUriAsync returns false (it does NOT throw) when the OS has no handler for the URI —
            // e.g. no browser / no xdg-open association, or a locked-down device. Log that too, so the
            // most common real failure mode isn't silently swallowed.
            var launched = await launcher.LaunchUriAsync(uri);
            if (!launched)
            {
                AppLogger.Instance.Warning($"Platform launcher could not open notification link: {url}");
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a launch failure must not crash the shell, but log it for diagnosis.
            AppLogger.Instance.Error(ex, $"Failed to open notification link: {url}");
        }
    }

    private void OnStream(object? sender, RoutedEventArgs e) => ShowStream();

    private void OnStorage(object? sender, RoutedEventArgs e)
    {
        // Storage tab = the desktop Logged Data pane's APP LOGS / DEVICE LOGS pivot (#7);
        // StorageMobileView hosts both halves and builds its own DataContexts.
        _storagePage ??= BuildPage("Logged Data", static () => new Mobile.StorageMobileView());
        // Refresh the logged-session list on every visit: the pane is cached (built once),
        // so its ctor reload runs only once — a session logged since the last visit would
        // otherwise stay hidden until a manual Refresh.
        if (_storagePage is Mobile.StorageMobileView storage) { storage.Refresh(); }
        ShowSecondary(_storagePage, 1);
    }

    private void OnChannels(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _channelsPage ??= BuildPage(
                "Channels",
                static () => new ChannelsMobileView { DataContext = new ChannelsPaneViewModel() }),
            2);

    private void OnProfiles(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _profilesPage ??= BuildPage(
                "Profiles",
                static () => new ProfilesMobileView { DataContext = new ProfilesPaneViewModel() }),
            3);

    /// <summary>
    /// Build a projected pane, or a placeholder if its shared ViewModel fails to
    /// construct (e.g. the mobile data layer didn't initialize). A safety net so a
    /// pane can never crash the whole app.
    /// </summary>
    private static Control BuildPage(string title, Func<Control> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            return BuildPlaceholder(title, ex);
        }
    }

    private static Control BuildPlaceholder(string title, Exception ex)
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(24),
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This pane is temporarily unavailable — the on-device data layer "
                 + "didn't initialize. Streaming and Device Storage are unaffected.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 320,
        });
        return panel;
    }

    /// <summary>Highlight the active pane in BOTH nav chromes (bottom bar + rail).</summary>
    private void SetActive(int index)
    {
        for (var i = 0; i < _navButtons.Length; i++)
        {
            _navButtons[i].Classes.Set("active", i == index);
        }
        for (var i = 0; i < _railButtons.Length; i++)
        {
            _railButtons[i].Classes.Set("active", i == index);
        }
    }
}
