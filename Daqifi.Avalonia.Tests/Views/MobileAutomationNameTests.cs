using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards the rule issue #356 was opened against: an actionable control whose <c>Content</c> is a
/// visual tree rather than a string gets no automation name of its own.
///
/// <para>
/// The mechanism is Avalonia's, not this repo's. <c>ContentControlAutomationPeer</c> falls back to
/// <c>Content?.ToString()</c> when <c>AutomationProperties.Name</c> is unset, and
/// <c>ToString()</c> on a panel is its type name — so seven buttons on the mobile shell announced
/// <c>Avalonia.Controls.StackPanel</c> or <c>Avalonia.Controls.Panel</c> to TalkBack while the label
/// a sighted user reads sat beside them as a separate, non-clickable <c>TextBlock</c>. In landscape
/// that covered the whole navigation rail, which is the only way to change tabs there.
/// </para>
///
/// <para>
/// What this asserts is the markup-level invariant, not the resolved runtime value, and the
/// distinction is worth stating because the lesson of <c>MobileShellViewBindingTests</c> is that a
/// test which pins a binding's <i>spelling</i> passes happily while the runtime value is wrong. The
/// resolved value needs a constructed control inside a styled root, which needs an Avalonia
/// application lifetime; <c>Daqifi.Avalonia.Tests.csproj</c> says in its own header that it tests
/// library code only and that adding a headless harness is a separate decision with its own
/// dependency. So the runtime half of #356 is evidenced by <c>uiautomator</c> dumps taken on the
/// Galaxy A16 in both orientations, recorded in the pull request, and this test guards the part a
/// device dump cannot: that the <b>next</b> icon-and-label button added to a mobile view cannot
/// silently reintroduce the defect.
/// </para>
///
/// <para>
/// It is a structural rule rather than a list of expected labels on purpose. A list would have to be
/// edited in step with the views and would say nothing about a control nobody thought to add to it;
/// this fails on any new actionable control in these files whose content is an element tree and
/// which names neither itself nor a class that names it.
/// </para>
///
/// <para>
/// Scope is the mobile views, which is where #356 was found and where it was verified. The same
/// shape exists in the desktop views under <c>Daqifi.Desktop/View</c> — 42 controls at the time of
/// writing, most of them icon-only toolbar buttons carrying a <c>ToolTip.Tip</c> — and is reported
/// in the pull request rather than fixed here, because a tooltip feeds <c>HelpText</c>, not
/// <c>Name</c>, and none of it can be proven on this repo's phone rig. Widening the array below is
/// the whole change needed to bring those under the same guard.
/// </para>
/// </summary>
public class MobileAutomationNameTests
{
    /// <summary>
    /// Controls whose automation peer derives its name from <c>Content</c> (or, for a tab, from
    /// <c>Header</c>) and therefore falls back to a type name when that content is an element.
    /// </summary>
    private static readonly HashSet<string> Actionable =
    [
        "Button", "RepeatButton", "ToggleButton", "SplitButton", "ToggleSplitButton",
        "DropDownButton", "HyperlinkButton", "CheckBox", "RadioButton", "ToggleSwitch",
        "MenuItem", "TabItem", "ListBoxItem",
    ];

    public static TheoryData<string> MobileViews =>
    [
        "Daqifi.Avalonia/Views/MobileMainView.axaml",
        "Daqifi.Avalonia/Views/MobileShellView.axaml",
        "Daqifi.Avalonia/Views/Mobile/ChannelsMobileView.axaml",
        "Daqifi.Avalonia/Views/Mobile/DeviceLogsMobileView.axaml",
        "Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml",
        "Daqifi.Avalonia/Views/Mobile/LoggedSessionsMobileView.axaml",
        "Daqifi.Avalonia/Views/Mobile/ProfilesMobileView.axaml",
        "Daqifi.Avalonia/Views/Mobile/StorageMobileView.axaml",
    ];

    [Theory]
    [MemberData(nameof(MobileViews))]
    public void Every_actionable_control_whose_content_is_an_element_tree_is_named(string view)
    {
        var root = XDocument.Parse(BindingFacts.Source(view), LoadOptions.SetLineInfo).Root!;
        var namingSelectors = SelectorsThatSetAnAutomationName(root);

        var unnamed = root.DescendantsAndSelf()
            .Where(e => Actionable.Contains(e.Name.LocalName))
            .Where(NeedsAnExplicitName)
            .Where(e => !IsNamed(e, namingSelectors))
            .Select(Describe)
            .ToList();

        Assert.True(unnamed.Count == 0,
            $"{view}: {unnamed.Count} actionable control(s) put an element tree in Content without " +
            "declaring AutomationProperties.Name, so the automation peer will announce the content's " +
            "type name instead of the label (#356). Add AutomationProperties.Name, or — when the " +
            "label follows view-model state — a Classes.x binding plus a style that sets " +
            $"(AutomationProperties.Name):{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", unnamed)}");
    }

    /// <summary>
    /// True when the peer has no string to fall back on: the control sets neither <c>Content</c> nor
    /// <c>Header</c> as an attribute, and its content is at least one child element.
    ///
    /// <para>
    /// Property elements (<c>Button.Styles</c>, <c>TabItem.Header</c>, …) are not content, and a
    /// control holding only literal text — <c>&lt;Button&gt;OK&lt;/Button&gt;</c> — is a string
    /// content and so is already named. An empty control, such as the transparent scrim buttons that
    /// dismiss the mobile overlays, is left alone here: its peer reports no name rather than the
    /// wrong one, which is a different defect from the one #356 is about.
    /// </para>
    /// </summary>
    private static bool NeedsAnExplicitName(XElement control)
    {
        if (control.Attribute("Content") is not null || control.Attribute("Header") is not null)
        {
            return false;
        }

        if (control.Elements().Any(child => child.Name.LocalName.EndsWith(".Header", StringComparison.Ordinal)))
        {
            return false;
        }

        return control.Elements().Any(child => !child.Name.LocalName.Contains('.'));
    }

    /// <summary>
    /// A control is named when it says so itself, or when it carries <b>every</b> class some
    /// name-setting style asks for. Matching one class out of two would not do: a style selected by
    /// <c>Button.scanToggle.scanning</c> never runs on a button that lost its <c>scanToggle</c>, and
    /// a rule that accepted the partial match would pass a button no style can reach.
    /// </summary>
    private static bool IsNamed(XElement control, IReadOnlyList<IReadOnlySet<string>> namingSelectors)
    {
        if (control.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name"))
        {
            return true;
        }

        var carried = ClassTokens(control).ToHashSet(StringComparer.Ordinal);
        return namingSelectors.Any(required => carried.IsSupersetOf(required));
    }

    /// <summary>
    /// The classes this control carries, from both spellings Avalonia accepts: the static
    /// <c>Classes="a b"</c> list and the conditional <c>Classes.a="{Binding …}"</c> form.
    /// </summary>
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

    /// <summary>
    /// One entry per style in this view that sets an <c>AutomationProperties.Name</c>, holding the
    /// classes its selector requires.
    ///
    /// <para>
    /// A button whose visible label is swapped by view-model state — SCAN / STOP SCANNING, ACTIVATE /
    /// DEACTIVATE PROFILE — cannot carry one fixed name, because an accessible name that contradicts
    /// the visible label is its own failure. The repo's answer to a WPF DataTrigger is
    /// <c>Classes.x="{Binding …}"</c> plus a class style, and <c>LiveGraphPane.axaml</c> already uses
    /// exactly that to keep a UIA name in step with the text a style writes. This treats a control
    /// reached by such a style as named.
    /// </para>
    ///
    /// <para>
    /// A selector naming no class at all is skipped: it would be satisfied by every control in the
    /// file and so would switch this guard off rather than widen it.
    /// </para>
    /// </summary>
    private static IReadOnlyList<IReadOnlySet<string>> SelectorsThatSetAnAutomationName(XElement root)
    {
        var selectors = new List<IReadOnlySet<string>>();

        foreach (var style in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Style"))
        {
            var selector = style.Attribute("Selector")?.Value;
            if (selector is null || !SetsAnAutomationName(style))
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

    /// <summary>
    /// Whether a style sets the attached name, written either bare or parenthesised — Avalonia takes
    /// both, and this repo uses both.
    /// </summary>
    private static bool SetsAnAutomationName(XElement style) =>
        style.Elements().Any(setter =>
            setter.Name.LocalName == "Setter"
            && setter.Attribute("Property")?.Value.Trim('(', ')') == "AutomationProperties.Name");

    private static string Describe(XElement control)
    {
        var line = ((IXmlLineInfo)control).HasLineInfo() ? ((IXmlLineInfo)control).LineNumber : 0;
        var content = control.Elements().First(child => !child.Name.LocalName.Contains('.')).Name.LocalName;
        return $"line {line}: <{control.Name.LocalName}> with <{content}> content";
    }
}
