using Daqifi.Desktop.View.Flyouts;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// A link in the release notes (#347) opens in the system browser only when the user clicks it,
/// through the handler the notifications flyout already has (<c>Hyperlink_RequestNavigate</c>).
/// This project constructs no Avalonia control (see its csproj), so the click path is pinned in
/// two halves: what the handler does with the clicked control's address (<see cref="NotificationsFlyout.Navigate"/>),
/// and, in the renderer's source, that a link's Click is the only thing wired to that handler.
/// </summary>
public class ReleaseNotesLinkTests
{
    private const string Renderer = "Daqifi.Avalonia/Daqifi.Desktop/View/ReleaseNotesText.cs";
    private const string Flyout = "Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/NotificationsFlyout.axaml.cs";

    private static int Count(string source, string text)
    {
        var count = 0;
        for (var at = source.IndexOf(text, StringComparison.Ordinal); at >= 0; at = source.IndexOf(text, at + text.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void A_clicked_web_link_opens_in_the_system_browser()
    {
        var opened = new List<string>();

        Assert.True(NotificationsFlyout.Navigate("https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0", opened.Add));

        Assert.Equal(["https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0"], opened);
    }

    /// <summary>
    /// The notes are remote content. Shell-opening a <c>file:</c> address runs a program, and other
    /// schemes start other handlers, so the handler opens http and https addresses only.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("/relative/page")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(42)]
    public void A_clicked_link_to_anything_else_opens_nothing(object? target)
    {
        var opened = new List<string>();

        Assert.False(NotificationsFlyout.Navigate(target, opened.Add));

        Assert.Empty(opened);
    }

    /// <summary>
    /// The renderer's links are buttons whose Click is the flyout's handler, and the renderer has no
    /// other way to open anything: no launcher of its own, no <c>HyperlinkButton</c> (which opens its
    /// address by itself), and no handler that runs without a click.
    /// </summary>
    [Fact]
    public void The_notes_links_open_only_through_the_flyouts_handler_on_a_click()
    {
        var source = BindingFacts.Source(Renderer);

        Assert.Contains(".Click += NotificationsFlyout.Hyperlink_RequestNavigate;", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "Hyperlink_RequestNavigate"));
        foreach (var other in new[] { "Process.Start", "Launcher.", "HyperlinkButton", "NavigateUri", "PointerPressed", "PointerReleased", "Tapped", "Command =" })
        {
            Assert.DoesNotContain(other, source, StringComparison.Ordinal);
        }
    }

    /// <summary>The flyout starts a process in one place, behind the scheme check, so every link wired to its handler gets that check.</summary>
    [Fact]
    public void The_flyouts_handler_starts_the_browser_only_behind_the_scheme_check()
    {
        var source = BindingFacts.Source(Flyout);

        Assert.Equal(1, Count(source, "Process.Start("));
        Assert.Contains("Navigate((sender as Control)?.Tag);", source, StringComparison.Ordinal);
    }
}
