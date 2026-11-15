using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// What the Android Back gesture closes inside one phone pane (#588).
/// </summary>
/// <remarks>
/// <para>
/// Before #588 nothing in the app handled Back, so Avalonia gave the event to Android, and Android
/// moved the whole app to the background, also when a panel was open. The shell
/// (<c>MobileMainView.OnBackRequested</c>) now closes the top-most open item first. It closes its
/// own overlays, then asks the visible pane through these methods, then goes back to Live Graph.
/// Only on Live Graph with nothing open does it leave the event to Android.
/// </para>
/// <para>
/// Each method closes ONE item through the same path as the item's own close control, and returns
/// <c>true</c> when it did. A confirm overlay is above its pane's other content, so it closes first.
/// </para>
/// </remarks>
// Downstream-only: upstream has no phone shell.
internal static class MobileBackNavigation
{
    /// <summary>Cancels an open confirm overlay, as its CANCEL button and its scrim do.</summary>
    internal static bool Close(ConfirmOverlayViewModel overlay)
    {
        if (!overlay.IsOpen) { return false; }
        overlay.Cancel();
        return true;
    }

    /// <summary>
    /// Profiles: the confirm overlay, then the new or edit drawer. The drawer closes through its
    /// close button's command, which saves an open edit first (#541). When that save fails, the
    /// drawer stays open with the reason, and Back is still handled: the user sees why.
    /// </summary>
    internal static bool Close(ProfilesPaneViewModel profiles)
    {
        if (Close(profiles.ConfirmOverlay)) { return true; }
        if (!profiles.IsDrawerOpen) { return false; }
        profiles.CloseDrawerCommand.Execute(null);
        return true;
    }

    /// <summary>Channels: the channel settings drawer, through its close button's command.</summary>
    internal static bool Close(ChannelsPaneViewModel channels)
    {
        if (!channels.IsSettingsOpen) { return false; }
        channels.CloseSettingsCommand.Execute(null);
        return true;
    }

    /// <summary>
    /// Logged Data, APP LOGS half: the confirm overlay, then the full-screen session plot, through
    /// its Close button's command.
    /// </summary>
    internal static bool Close(LoggedSessionsMobileViewModel sessions)
    {
        if (Close(sessions.ConfirmOverlay)) { return true; }
        if (!sessions.IsViewerOpen) { return false; }
        sessions.CloseViewerCommand.Execute(null);
        return true;
    }
}
