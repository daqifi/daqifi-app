using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Pins how both Channels views show an active channel tile (issue #494): an "active" class bound to
/// <c>IsActive</c>, a style that gives that class a 2 px border in the channel's type colour, and an
/// ACTIVE pill that both heads share.
///
/// <para>
/// Compiled bindings check the member names here, not which attribute a binding feeds or whether a
/// style can win. The trap these rows exist for is a local value: put <c>Background</c>,
/// <c>BorderBrush</c> or <c>BorderThickness</c> back on the tile element and it beats every style
/// setter, so the active state renders exactly like the inactive one while every head builds green.
/// Parsed, not searched, because the comments in both views name the same attributes.
/// </para>
/// </summary>
public class ChannelTileActiveStateTests
{
    private const string DesktopView = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/ChannelsPanePrototype.axaml";
    private const string MobileView = "Daqifi.Avalonia/Views/Mobile/ChannelsMobileView.axaml";

    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XElement Root(string view) =>
        XDocument.Parse(BindingFacts.Source(view)).Root
        ?? throw new InvalidOperationException($"{view} has no root element.");

    /// <summary>The one element carrying the channelTile class: the tile itself.</summary>
    private static XElement Tile(string view)
    {
        var tiles = Root(view).Descendants(Avalonia + "Border")
            .Where(e => ((string?)e.Attribute("Classes"))?.Split(' ').Contains("channelTile") == true)
            .ToList();
        Assert.True(tiles.Count == 1, $"{view}: expected one channelTile Border, found {tiles.Count}.");
        return tiles[0];
    }

    [Theory]
    [InlineData(DesktopView)]
    [InlineData(MobileView)]
    public void The_tile_takes_the_active_class_from_IsActive(string view) =>
        Assert.Equal("{Binding IsActive}", (string?)Tile(view).Attribute("Classes.active"));

    [Theory]
    [InlineData(DesktopView, "Background")]
    [InlineData(DesktopView, "BorderBrush")]
    [InlineData(DesktopView, "BorderThickness")]
    [InlineData(DesktopView, "Padding")]
    [InlineData(MobileView, "Background")]
    [InlineData(MobileView, "BorderBrush")]
    [InlineData(MobileView, "BorderThickness")]
    [InlineData(MobileView, "Padding")]
    public void The_tile_sets_no_local_value_the_active_style_must_override(string view, string property) =>
        Assert.True(
            Tile(view).Attribute(property) is null,
            $"{view}: the tile sets {property} locally, which beats the Border.channelTile.active style.");

    [Theory]
    [InlineData(DesktopView)]
    [InlineData(MobileView)]
    public void The_active_style_draws_a_2px_border_in_the_type_colour(string view)
    {
        var style = Assert.Single(
            Root(view).Descendants(Avalonia + "Style"),
            s => (string?)s.Attribute("Selector") == "Border.channelTile.active");
        string? Setter(string property) => style.Elements(Avalonia + "Setter")
            .Where(s => (string?)s.Attribute("Property") == property)
            .Select(s => (string?)s.Attribute("Value"))
            .SingleOrDefault();

        Assert.Equal("{Binding StripeBrush}", Setter("BorderBrush"));
        Assert.Equal("2", Setter("BorderThickness"));
    }

    /// <summary>
    /// Both heads show the same word for the same state. The pill must sit under an element whose
    /// visibility follows <c>IsActive</c>; a pill shown on every tile would say nothing.
    /// </summary>
    [Theory]
    [InlineData(DesktopView)]
    [InlineData(MobileView)]
    public void The_tile_shows_an_ACTIVE_pill_only_while_active(string view)
    {
        var pill = Assert.Single(
            Tile(view).Descendants(Avalonia + "TextBlock"),
            t => (string?)t.Attribute("Text") == "ACTIVE");
        Assert.Equal("{Binding IsActive}", (string?)pill.Parent?.Attribute("IsVisible"));
    }
}
