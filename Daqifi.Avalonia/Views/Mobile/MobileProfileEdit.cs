using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// The phone's way out of a profile edit that is still open in the drawer (#560).
/// </summary>
/// <remarks>
/// <para>
/// On desktop the Profiles pane is a tab's content, so a tab change or a window close detaches it,
/// and <see cref="ProfilesPaneViewModel.Cleanup"/> saves the open edit (#541, #542). The phone shell
/// builds its Profiles page once and keeps it: a tab change only hides it, and Android stops a
/// background app without detaching anything. Nothing saved the edit, and it was gone at the next
/// launch while the list had shown it as saved.
/// </para>
/// <para>
/// The page cannot be cleaned up here, because it is used again when the user comes back. So this
/// goes through the close button's own path, <see cref="ProfilesPaneViewModel.CloseDrawerCommand"/>,
/// which saves through the same <c>SaveOpenEdit</c> as <c>Cleanup</c>, closes the drawer when the
/// write succeeds, and keeps the drawer open with the reason when it does not. No second save path.
/// </para>
/// </remarks>
// Downstream-only: upstream has no phone shell.
internal static class MobileProfileEdit
{
    /// <summary>Added to the drawer's reason when it goes to the phone's notifications.</summary>
    internal const string StillOpenSuffix = " The edit is still open on the Profiles tab.";

    /// <summary>
    /// Saves the profile open in the edit drawer, and closes the drawer when the save succeeds.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no edit is open or the save succeeded; otherwise a sentence for the phone's
    /// notifications. The drawer then stays open with the same reason, so the edit is kept and the
    /// close button can try again.
    /// </returns>
    /// <remarks>
    /// Only an edit of a saved profile is touched. The new-profile form has nothing to save until
    /// the user taps SAVE PROFILE, and the close button would discard it, so it stays open as the
    /// user left it. <see cref="ProfilesPaneViewModel.SelectedProfile"/> is set only while the edit
    /// drawer is open, and the new-profile form clears it.
    /// </remarks>
    internal static string? SaveOnLeave(ProfilesPaneViewModel profiles)
    {
        if (profiles.SelectedProfile is null) { return null; }

        profiles.CloseDrawerCommand.Execute(null);
        return profiles.IsDrawerOpen ? profiles.DrawerError + StillOpenSuffix : null;
    }
}
