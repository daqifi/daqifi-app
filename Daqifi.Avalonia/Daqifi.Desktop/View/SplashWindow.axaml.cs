using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Daqifi.Desktop.View;

/// <summary>
/// The startup splash: the Avalonia counterpart of the WPF <c>SplashScreen</c> upstream shows from
/// <c>App.ShowSplashScreen</c> (#501).
/// </summary>
/// <remarks>
/// WPF's splash is a native layered window that paints without a message loop. An Avalonia window
/// paints only when the dispatcher runs, and startup runs synchronously on the UI thread before the
/// main loop starts, so <see cref="ShowAndWaitForPaint"/> pumps the dispatcher until the window has
/// been drawn.
/// </remarks>
public partial class SplashWindow : Window
{
    /// <summary>How long the fade-out lasts, matching WPF's auto-close fade.</summary>
    internal const double FADE_MS = 300;

    /// <summary>The longest startup waits for the first paint: a cosmetic must not stall the app.</summary>
    private const double PAINT_TIMEOUT_MS = 500;

    private const double FADE_TICK_MS = 16;

    /// <summary>Animation frames to wait: the first lays the window out, the next has drawn it.</summary>
    private const int FRAMES_UNTIL_PAINTED = 2;

    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>The window's opacity at a point in the fade-out.</summary>
    internal static double FadeOpacity(double elapsedMs) => Math.Clamp(1 - elapsedMs / FADE_MS, 0, 1);

    /// <summary>
    /// Shows the window and returns once it has been drawn, or after a short timeout.
    /// </summary>
    public void ShowAndWaitForPaint()
    {
        Show();

        var frame = new DispatcherFrame();
        var framesLeft = FRAMES_UNTIL_PAINTED;

        void OnAnimationFrame(TimeSpan _)
        {
            if (--framesLeft > 0)
            {
                RequestAnimationFrame(OnAnimationFrame);
                return;
            }
            frame.Continue = false;
        }

        RequestAnimationFrame(OnAnimationFrame);
        using var timeout = DispatcherTimer.RunOnce(
            () => frame.Continue = false, TimeSpan.FromMilliseconds(PAINT_TIMEOUT_MS));
        Dispatcher.UIThread.PushFrame(frame);
    }

    /// <summary>Fades the window out, then closes it.</summary>
    public void FadeOutAndClose()
    {
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FADE_TICK_MS) };
        timer.Tick += (_, _) =>
        {
            var opacity = FadeOpacity(elapsed.Elapsed.TotalMilliseconds);
            Opacity = opacity;
            if (opacity <= 0)
            {
                timer.Stop();
                Close();
            }
        };
        timer.Start();
    }
}
