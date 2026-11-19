using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards issue #631: a theme that sets <c>Padding</c> and replaces the <c>Template</c> must apply
/// that padding inside the template.
///
/// <para>
/// <c>Padding</c> is an ordinary styled property. Nothing reads it unless the template binds it to a
/// <c>Border</c> or a <c>ContentPresenter</c>. <c>DarkSecondaryButton</c> and <c>DarkPrimaryButton</c>
/// set <c>Padding="16,6"</c>, but their templates did not bind it. A button with a fixed
/// <c>Width</c> and <c>Height</c> hid the defect. The Debug Console's Clear and Close buttons and the
/// export dialog's Browse button have no fixed size, so they shrank to the width of their text and
/// the text touched the border.
/// </para>
///
/// <para>
/// The rule is checked in every view and resource dictionary, not only in
/// <c>DarkDialog.axaml</c>. A theme that sets <c>Padding</c> to <c>0</c> is left alone: it asks for
/// no space, so a template that ignores it draws the requested result.
/// </para>
/// </summary>
public class TemplatePaddingTests
{
    [Fact]
    public void Every_theme_that_sets_a_padding_applies_it_in_its_template()
    {
        var root = BindingFacts.RepoRoot();
        var offenders = new List<string>();
        var themeCount = 0;

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "Daqifi.Avalonia"), "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var document = XDocument.Parse(File.ReadAllText(path), LoadOptions.SetLineInfo);
            foreach (var theme in document.Descendants().Where(e => e.Name.LocalName is "ControlTheme" or "Style"))
            {
                var setters = theme.Elements().Where(e => e.Name.LocalName == "Setter").ToList();
                var padding = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "Padding");
                var template = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "Template");
                if (padding is null || template is null || IsZero(padding.Attribute("Value")?.Value))
                {
                    continue;
                }

                themeCount++;
                var bindsPadding = template.Descendants()
                    .SelectMany(e => e.Attributes())
                    .Any(a => a.Name.LocalName == "Padding" && a.Value.Replace(" ", string.Empty) == "{TemplateBindingPadding}");
                if (!bindsPadding)
                {
                    var line = ((IXmlLineInfo)theme).LineNumber;
                    var key = theme.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value
                              ?? theme.Attribute("Selector")?.Value
                              ?? "(unkeyed)";
                    offenders.Add($"{relative}:{line} {key} sets Padding=\"{padding.Attribute("Value")?.Value}\"");
                }
            }
        }

        // DarkSecondaryButton, DarkPrimaryButton and the pill buttons at minimum. A walk that finds
        // none has stopped reading the files, and would pass for that reason alone.
        Assert.True(themeCount >= 5, $"The markup walk found only {themeCount} themes with a padding and a template.");
        Assert.True(offenders.Count == 0,
            "These themes set a Padding that their template never applies, so a button without a fixed " +
            "size shrinks to its text (#631). Add Padding=\"{TemplateBinding Padding}\" to the template's " +
            "Border or ContentPresenter:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsZero(string? value) =>
        value is not null
        && value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .All(part => double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var n) && n == 0);
}
