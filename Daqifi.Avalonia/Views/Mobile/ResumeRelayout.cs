using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Lays out the phone shell again from the start when the app comes back to the foreground (#590).
/// </summary>
/// <remarks>
/// <para>
/// A layout change made while the app is in the background is not drawn correctly on resume. Two
/// known triggers: the Profiles drawer that <c>OnAppDeactivated</c> closes to save an edit (#560),
/// and the busy overlay of Logged Data that changes while the system save picker is open.
/// </para>
/// <para>
/// Measured on a Galaxy A16 (Android 16), Profiles pane: the layout pass that ran in the background
/// measured every text block with a width of 0, so the pane content got a desired width of 0.36 dp
/// and was arranged 0 dp wide in the centre. After the resume the text blocks measured correctly
/// again, but the arrange was not done again: the content stayed 0 dp wide, and the pane showed
/// nothing under its header until a rotation, which measures and arranges every control again.
/// </para>
/// <para>
/// This does what the rotation does: it invalidates the measure (and so the arrange) of every
/// control in the tree, so the next layout pass starts from the start.
/// </para>
/// <para>
/// One pass is not enough. In the save picker case the wrong pass can occur just AFTER the resume:
/// on the phone the pane was correct when the app was activated, and a layout pass on the first frame
/// arranged it 0 dp wide again. The desired sizes were correct 50 ms later, but nothing arranged the
/// pane again. So the relayout runs on the first frame and again after <see cref="RepeatDelays"/>.
/// Each pass costs one layout of about 400 controls, once per resume.
/// </para>
/// </remarks>
// Downstream-only: upstream has no phone shell.
internal static class ResumeRelayout
{
    /// <summary>
    /// The later passes, from the moment the app is activated. The wrong pass that was measured on
    /// the phone was over in less than 50 ms; these leave a wide margin.
    /// </summary>
    internal static readonly TimeSpan[] RepeatDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)];

    /// <summary>
    /// Lays out <paramref name="root"/> again on the first frame after the resume, and again after
    /// each of <see cref="RepeatDelays"/>.
    /// </summary>
    /// <remarks>
    /// Not at once: Android raises Activated from <c>OnStart</c>, before the surface exists again.
    /// The first frame is the first point where the app draws again.
    /// </remarks>
    internal static void ScheduleAfterResume(TopLevel top, Visual root)
    {
        top.RequestAnimationFrame(_ => InvalidateTree(root));
        foreach (var delay in RepeatDelays)
        {
            DispatcherTimer.RunOnce(() => InvalidateTree(root), delay);
        }
    }

    /// <summary>
    /// Invalidates the measure of <paramref name="root"/> and of every layoutable control under it.
    /// </summary>
    /// <returns>The number of controls invalidated.</returns>
    internal static int InvalidateTree(Visual root)
    {
        var count = 0;
        foreach (var layoutable in root.GetSelfAndVisualDescendants().OfType<Layoutable>())
        {
            layoutable.InvalidateMeasure();
            count++;
        }
        root.InvalidateVisual();
        return count;
    }
}
