using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards issue #630 in the markup of the desktop main window, its five panes and its drawers: every
/// control that a user can act on must have an accessible name.
///
/// <para>
/// Two checks share this job. The capture harness (<c>tools/parity-audit/AvaloniaCapture</c>, run on
/// every pull request by the macOS job) walks the real visual tree of each main view and reads each
/// control's runtime name from its automation peer, which is the value VoiceOver gets. But it can only
/// see what the empty app shows: no device, no channel, no logged session. A channel tile's gear, a
/// session row's delete button and a device tile's gear exist only with data. This test reads the
/// markup instead, so it also covers the controls inside every <c>DataTemplate</c>.
/// </para>
///
/// <para>
/// The rule follows what Avalonia 12.1.6 does at run time (<c>ContentControlAutomationPeer.GetNameCore</c>):
/// the name is <c>AutomationProperties.Name</c>, else the content's text, else the first
/// <c>TextBlock</c> in the content. So a control is named when it sets the attribute, has string or
/// bound <c>Content</c>, holds literal text, or holds a <c>TextBlock</c>. Three cases fail:
/// </para>
/// <list type="bullet">
/// <item>An icon-only control (an <c>i:Icon</c>, a <c>Path</c>, a shape) and an empty control, such
/// as a switch with no content. Its peer has no name, and VoiceOver reads only "button". A
/// <c>ToolTip.Tip</c> does not help: Avalonia uses it as help text, not as the name.</item>
/// <item>A <c>TabItem</c> without the attribute. Its peer reads the tab's <c>Content</c>, not its
/// <c>Header</c>, so a tab whose label is in the header had no name, or the name of the first text
/// in its page (the Live Graph tab was "LIVE").</item>
/// <item>A control whose only text is a number. The bell button was named "0", the text of its
/// unread badge. Such a control must set the attribute.</item>
/// </list>
/// <para>
/// A control that is taken out of the accessibility tree with
/// <c>AutomationProperties.AccessibilityView="Raw"</c>, such as the scrim behind a confirmation card,
/// is not examined.
/// </para>
/// </summary>
public class DesktopAutomationNameTests
{
    private static readonly HashSet<string> Actionable =
    [
        "Button", "RepeatButton", "ToggleButton", "SplitButton", "ToggleSplitButton",
        "DropDownButton", "HyperlinkButton", "CheckBox", "RadioButton", "ToggleSwitch",
        "MenuItem", "TabItem", "InvokableTabItem", "ListBoxItem", "Slider", "NumericUpDown", "ComboBox",
    ];

    /// <summary>Controls whose peer does not derive a name from content: only the attribute names them.</summary>
    private static readonly HashSet<string> NamedOnlyByAttribute =
    [
        "TabItem", "InvokableTabItem", "Slider", "NumericUpDown", "ComboBox", "ToggleSwitch",
    ];

    /// <summary>Elements whose text the runtime takes as the name.</summary>
    private static readonly HashSet<string> TextElements = ["TextBlock", "SelectableTextBlock"];

    public static TheoryData<string> DesktopMainViews =>
    [
        "Daqifi.Avalonia/Daqifi.Desktop/MainWindow.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LiveGraphPane.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LoggedDataPanePrototype.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/ChannelsPanePrototype.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/ProfilesPane.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/DeviceLogsView.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/LiveGraphFlyout.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/SummaryFlyout.axaml",
        "Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/NotificationsFlyout.axaml",
    ];

    [Theory]
    [MemberData(nameof(DesktopMainViews))]
    public void Every_actionable_control_has_an_accessible_name(string view)
    {
        var root = XDocument.Parse(BindingFacts.Source(view), LoadOptions.SetLineInfo).Root!;
        var namingSelectors = SelectorsThatSetAnAutomationName(root);

        var controls = root.DescendantsAndSelf()
            .Where(e => Actionable.Contains(e.Name.LocalName))
            .Where(e => !IsTakenOutOfTheAccessibilityTree(e))
            .ToList();
        var unnamed = controls
            .Where(e => !IsNamed(e, namingSelectors))
            .Select(Describe)
            .ToList();

        Assert.True(controls.Count > 0, $"{view}: the walk found no actionable control, so it is not reading the view.");
        Assert.True(unnamed.Count == 0,
            $"{view}: {unnamed.Count} actionable control(s) have no accessible name, so VoiceOver reads " +
            "only their role (#630). Set AutomationProperties.Name to the label or tooltip a sighted " +
            $"user reads:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", unnamed)}");
    }

    [Theory]
    [InlineData(0, "Notifications")]
    [InlineData(1, "Notifications, 1 unread")]
    [InlineData(12, "Notifications, 12 unread")]
    public void The_bell_button_is_named_for_what_it_opens_and_how_many_are_unread(int unread, string expected)
    {
        Assert.Equal(expected, DaqifiViewModel.NotificationsButtonNameFor(unread));
    }

    [Fact]
    public void The_main_tabs_are_tabs_that_an_accessibility_press_selects()
    {
        // A plain TabItem's peer is only a selection-item provider, and on macOS VoiceOver's press
        // (VO-Space) did nothing on it. The capture harness proves the invoke selects the tab; this
        // pins that the five tabs are still the type whose peer has the invoke.
        var root = XDocument.Parse(BindingFacts.Source("Daqifi.Avalonia/Daqifi.Desktop/MainWindow.axaml")).Root!;
        var tabControl = root.Descendants().Single(e => e.Name.LocalName == "TabControl");
        var tabs = tabControl.Elements().Where(e => !e.Name.LocalName.Contains('.')).Select(e => e.Name.LocalName).ToList();

        Assert.Equal(5, tabs.Count);
        Assert.All(tabs, name => Assert.Equal("InvokableTabItem", name));
    }

    private static bool IsTakenOutOfTheAccessibilityTree(XElement control) =>
        control.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AccessibilityView" && a.Value == "Raw");

    private static bool IsNamed(XElement control, IReadOnlyList<IReadOnlySet<string>> namingSelectors)
    {
        if (control.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"))
        {
            return true;
        }

        var carried = ClassTokens(control).ToHashSet(StringComparer.Ordinal);
        if (namingSelectors.Any(required => carried.IsSupersetOf(required)))
        {
            return true;
        }

        if (NamedOnlyByAttribute.Contains(control.Name.LocalName))
        {
            return false;
        }

        if (control.Attribute("Content") is { } content)
        {
            return HasLetter(content.Value) || content.Value.StartsWith('{');
        }

        if (HasLetter(control.Nodes().OfType<XText>().Aggregate(string.Empty, (text, node) => text + node.Value)))
        {
            return true;
        }

        // The first TextBlock in the content, as the runtime reads it. A TextBlock that shows only a
        // number, or a bound one inside a control that also holds a badge, is not a name to trust, so
        // a literal text with a letter or a binding outside a badge counts.
        return control.Elements()
            .Where(child => !child.Name.LocalName.Contains('.'))
            .SelectMany(child => child.DescendantsAndSelf())
            .Where(e => TextElements.Contains(e.Name.LocalName))
            .Select(e => e.Attribute("Text")?.Value ?? e.Value)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) is { } first
            && (HasLetter(first) || first.StartsWith('{'))
            && !IsNumberBinding(first);
    }

    private static bool HasLetter(string text) => text.Any(char.IsLetter) && !text.StartsWith('{');

    /// <summary>A binding to a count. The bell's badge bound <c>NotificationCount</c>, and that became the name.</summary>
    private static bool IsNumberBinding(string text) =>
        Regex.IsMatch(text, @"^\{Binding\s+(Path=)?\w*(Count|Number|Total)\b", RegexOptions.IgnoreCase);

    private static IEnumerable<string> ClassTokens(XElement control)
    {
        foreach (var attribute in control.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (name == "Classes")
            {
                foreach (var token in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return token;
                }
            }
            else if (name.StartsWith("Classes.", StringComparison.Ordinal))
            {
                yield return name["Classes.".Length..];
            }
        }
    }

    /// <summary>One entry per style in the view that sets an automation name, holding the classes it requires.</summary>
    private static IReadOnlyList<IReadOnlySet<string>> SelectorsThatSetAnAutomationName(XElement root)
    {
        var selectors = new List<IReadOnlySet<string>>();
        foreach (var style in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Style"))
        {
            var selector = style.Attribute("Selector")?.Value;
            var setsName = style.Elements().Any(setter =>
                setter.Name.LocalName == "Setter"
                && setter.Attribute("Property")?.Value.Trim('(', ')') == "AutomationProperties.Name");
            if (selector is null || !setsName)
            {
                continue;
            }

            var required = Regex.Matches(selector, @"\.([A-Za-z_][A-Za-z0-9_]*)")
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            if (required.Count > 0)
            {
                selectors.Add(required);
            }
        }

        return selectors;
    }

    private static string Describe(XElement control)
    {
        var line = ((IXmlLineInfo)control).HasLineInfo() ? ((IXmlLineInfo)control).LineNumber : 0;
        var content = control.Elements().FirstOrDefault(child => !child.Name.LocalName.Contains('.'))?.Name.LocalName;
        var tip = control.Attribute(XName.Get("ToolTip.Tip"))?.Value;
        return $"line {line}: <{control.Name.LocalName}>" +
               (content is null ? " with no content" : $" with <{content}> content") +
               (tip is null ? "" : $" (tooltip '{tip}')");
    }
}
