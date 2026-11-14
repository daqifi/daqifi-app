using System.Text.RegularExpressions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #590: on Android, a pane showed nothing after the app came back to the foreground, until a
/// rotation. A layout change made in the background (the Profiles drawer that the save on
/// background closes, the Logged Data busy overlay around the save picker) was laid out with every
/// text 0 wide, and the arrange was not done again on resume.
///
/// <para>
/// The fix is on the phone shell: on resume it lays out the whole tree again, as a rotation does.
/// This test project cannot host an Avalonia control (no lifetime, so the dispatcher is unusable),
/// and a subscription that goes away is the whole defect, so the call sites are guarded as source,
/// the way <see cref="MobileProfileEditSaveTests"/> guards #560. The fix itself was verified on the
/// Galaxy A16.
/// </para>
/// </summary>
public sealed class ResumeRelayoutTests
{
    private const string Shell = "Daqifi.Avalonia/Views/MobileMainView.axaml.cs";
    private const string Helper = "Daqifi.Avalonia/Views/Mobile/ResumeRelayout.cs";

    [Fact]
    public void The_shell_listens_for_the_resume_and_stops_when_it_leaves()
    {
        Assert.Contains("Activated += OnAppActivated", BodyOf(Shell, "OnAttachedToVisualTree"), StringComparison.Ordinal);
        Assert.Contains("Activated -= OnAppActivated", BodyOf(Shell, "OnDetachedFromVisualTree"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The relayout waits for the first frame. Android raises Activated from <c>OnStart</c>, before
    /// the surface exists again. It runs again later, because on the phone the wrong layout pass also
    /// occurred just after the first frame (save picker case). It covers the whole shell, not one
    /// pane: the drawer and the picker are two known triggers, not the only ones.
    /// </summary>
    [Fact]
    public void The_resume_lays_out_the_whole_shell_again_on_the_first_frame_and_later()
    {
        Assert.Contains("ResumeRelayout.ScheduleAfterResume(top, this)", BodyOf(Shell, "OnAppActivated"), StringComparison.Ordinal);
        var schedule = BodyOf(Helper, "ScheduleAfterResume");
        Assert.Contains("RequestAnimationFrame(", schedule, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer.RunOnce(", schedule, StringComparison.Ordinal);
    }

    /// <summary>The later passes exist and are in time order, the last one within a few seconds.</summary>
    [Fact]
    public void The_later_passes_come_after_the_first_frame_and_soon()
    {
        var delays = Daqifi.Avalonia.Views.Mobile.ResumeRelayout.RepeatDelays;
        Assert.NotEmpty(delays);
        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A rotation fixes the pane because it measures and arranges every control again. Only the
    /// controls whose layout is invalid are laid out again, so the helper must reach every
    /// descendant, not only the root.
    /// </summary>
    [Fact]
    public void The_helper_invalidates_every_control_in_the_tree()
    {
        var body = BodyOf(Helper, "InvalidateTree");
        Assert.Contains("GetSelfAndVisualDescendants()", body, StringComparison.Ordinal);
        Assert.Contains(".InvalidateMeasure()", body, StringComparison.Ordinal);
    }

    private static string BodyOf(string file, string method)
    {
        var source = BindingFacts.CodeOnly(BindingFacts.Source(file));
        var declaration = Regex.Match(source, $@"\b(void|int)\s+{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{file}: no method named {method}.");
        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0, $"{file}: {method} has no body.");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[(open + 1)..i]; }
        }
        Assert.Fail($"{file}: {method}'s body never closes.");
        return string.Empty;
    }
}
