using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins the Fluent accent to the DAQiFi brand blue (#613).
///
/// <para>
/// Without a <c>ColorPaletteResources</c>, <c>FluentTheme</c> reads <c>SystemAccentColor</c> from the
/// operating system and falls back to Fluent's own <c>#0078D7</c>. Every stock control that draws an
/// accent — <c>ToggleSwitch</c>, <c>RadioButton</c>, <c>Slider</c>, <c>ProgressBar</c>, a checked
/// <c>ToggleButton</c>, <c>Button.accent</c> — then followed the user's System Settings colour while
/// the app's own templates drew the <c>Accent</c> token, so one screen showed two blues, or a blue and
/// a purple. Nothing renders an OS accent in the capture harness, so a dropped palette would only
/// show as Fluent's default blue in a baseline diff; this test names the cause directly.
/// </para>
///
/// <para>
/// Parsed, not searched, for the same reason as <see cref="ConverterRegistrationTests"/>: the comments
/// in these files name the attributes they explain.
/// </para>
/// </summary>
public class AccentPaletteTests
{
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";
    private static readonly XName KeyAttribute = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_fluent_palette_accent_is_the_design_token_accent(string variant)
    {
        var app = Parse("Daqifi.Avalonia/App.axaml");
        var palette = app.Descendants(XName.Get("ColorPaletteResources", AvaloniaNamespace))
            .SingleOrDefault(e => e.Attribute(KeyAttribute)?.Value == variant);

        Assert.NotNull(palette);
        Assert.Equal(TokenAccent(), palette.Attribute("Accent")?.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stock <c>Classes="accent"</c> button is the one control whose look the palette does not fully
    /// fix: its template left a fixed-width button's label at the left edge. <c>DarkPrimaryButton</c>
    /// (dialogs) and <c>AccentPillButton</c> (panes and flyouts) are the two primary styles.
    /// </summary>
    [Fact]
    public void No_view_uses_the_stock_fluent_accent_button_class()
    {
        var root = BindingFacts.RepoRoot();
        var offenders = new List<string>();
        var fileCount = 0;

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "Daqifi.Avalonia"), "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            fileCount++;
            foreach (var element in XDocument.Parse(File.ReadAllText(path)).Descendants())
            {
                var classes = element.Attribute("Classes")?.Value;
                if (classes is not null &&
                    classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("accent", StringComparer.Ordinal))
                {
                    offenders.Add($"{relative}: <{element.Name.LocalName} Classes=\"{classes}\">");
                }
            }
        }

        Assert.True(fileCount > 20, $"The markup walk found only {fileCount} .axaml files.");
        Assert.True(offenders.Count == 0, "Stock accent buttons:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The primary dialog button's label must stay readable in every enabled state: 4.5:1 is the WCAG
    /// minimum for text this small (12 px). It started as white on the brand blue, which is 3.03:1 at
    /// rest and 2.53:1 on hover. Colours are read from the markup and the design tokens, so a
    /// retuned shade is checked, not assumed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("^:pointerover /template/ Border#Bg")]
    [InlineData("^:pressed /template/ Border#Bg")]
    public void The_primary_dialog_button_label_has_4_5_to_1_contrast(string? state)
    {
        var theme = Parse("Daqifi.Avalonia/Daqifi.Desktop/Resources/DarkDialog.axaml")
            .Descendants(XName.Get("ControlTheme", AvaloniaNamespace))
            .Single(e => e.Attribute(KeyAttribute)?.Value == "DarkPrimaryButton");

        var setters = state is null
            ? theme.Elements(XName.Get("Setter", AvaloniaNamespace))
            : theme.Elements(XName.Get("Style", AvaloniaNamespace))
                .Single(e => e.Attribute("Selector")?.Value == state)
                .Elements(XName.Get("Setter", AvaloniaNamespace));
        string Setter(IEnumerable<XElement> from, string property) =>
            from.Single(e => e.Attribute("Property")?.Value == property).Attribute("Value")!.Value;

        var foreground = Resolve(Setter(theme.Elements(XName.Get("Setter", AvaloniaNamespace)), "Foreground"));
        var background = Resolve(Setter(setters, "Background"));
        var ratio = ContrastRatio(foreground, background);

        Assert.True(ratio >= 4.5, $"{state ?? "rest"}: {foreground} on {background} is {ratio:F2}:1, below 4.5:1.");
    }

    private static string Resolve(string value)
    {
        const string prefix = "{DynamicResource ";
        if (value == "White")
        {
            return "#FFFFFF";
        }

        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return value;
        }

        var key = value[prefix.Length..^1];
        return Parse("Daqifi.Avalonia/Daqifi.Desktop/Resources/DesignTokens.axaml")
            .Descendants(XName.Get("SolidColorBrush", AvaloniaNamespace))
            .First(e => e.Attribute(KeyAttribute)?.Value == key)
            .Attribute("Color")!.Value;
    }

    private static double ContrastRatio(string a, string b)
    {
        static double Luminance(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8)
            {
                hex = hex[2..]; // #AARRGGBB: every colour compared here is opaque
            }

            double Channel(int i)
            {
                var c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string TokenAccent()
    {
        var tokens = Parse("Daqifi.Avalonia/Daqifi.Desktop/Resources/DesignTokens.axaml");
        var accents = tokens.Descendants(XName.Get("SolidColorBrush", AvaloniaNamespace))
            .Where(e => e.Attribute(KeyAttribute)?.Value == "Accent")
            .Select(e => e.Attribute("Color")?.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One value across every theme dictionary, or "the token accent" means nothing.
        var accent = Assert.Single(accents);
        Assert.NotNull(accent);
        return accent;
    }

    private static XDocument Parse(string repoRelativePath) =>
        XDocument.Parse(File.ReadAllText(Path.Combine(BindingFacts.RepoRoot(), repoRelativePath.Replace('/', Path.DirectorySeparatorChar))));
}
