using System;
using Avalonia;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// How far the on-screen keyboard reaches into the phone shell's content area (#589).
/// </summary>
/// <remarks>
/// On Android 15 and later the app is edge-to-edge, so the window does not resize for the keyboard.
/// The keyboard is drawn over the app, and a text box low on the screen (the manual IP row on the
/// Live Graph tab) is hidden under it. The shell lifts its content area by this distance, which
/// shrinks the pane's scroll viewer to the part of the screen that the user can see.
/// </remarks>
// Downstream-only: upstream has no phone shell.
internal static class KeyboardInset
{
    /// <summary>
    /// The height of the part of the content area that the keyboard covers.
    /// </summary>
    /// <param name="contentBottom">
    /// The bottom edge of the content area's layout slot, in the coordinates of the top level. Use
    /// the slot, not the area: the inset that this returns moves the area's own bottom edge up.
    /// </param>
    /// <param name="keyboard">
    /// The area that the keyboard covers, in the same coordinates. An empty rectangle when the
    /// keyboard is closed.
    /// </param>
    /// <returns>Zero when the keyboard is closed or is fully below the content area.</returns>
    internal static double Overlap(double contentBottom, Rect keyboard)
    {
        if (keyboard.Height <= 0) { return 0; }
        return Math.Max(0, contentBottom - keyboard.Top);
    }
}
