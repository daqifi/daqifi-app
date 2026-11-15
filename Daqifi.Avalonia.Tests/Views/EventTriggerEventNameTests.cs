using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Every <c>EventTriggerBehavior</c> in the app's views names an event that its control has
/// (issue #578).
/// </summary>
/// <remarks>
/// <para>
/// <c>EventName</c> is a string. The XAML compiler does not resolve it, and the behavior looks the
/// event up by reflection only when it attaches at run time. The desktop LOGGING MODE buttons named
/// <c>Checked</c>, the WPF event, which Avalonia's <c>RadioButton</c> does not have. Every head built
/// green, the app logged nothing, and a click on LOG TO DEVICE moved the button but did not change
/// the logging mode, so the user believed the device logged to its SD card when it did not.
/// </para>
/// <para>
/// This test does the same lookup the behavior does, <c>GetRuntimeEvent</c>, on the type of the
/// element that holds the behavior. A behavior with a <c>SourceObject</c> listens to a different
/// element, which a static read cannot resolve, so it fails here and must be checked another way.
/// </para>
/// </remarks>
public class EventTriggerEventNameTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    public static TheoryData<string> Views()
    {
        var root = BindingFacts.RepoRoot();
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "Daqifi.Avalonia"), "*.axaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Every_event_trigger_names_an_event_its_control_has(string view)
    {
        var document = XDocument.Parse(BindingFacts.Source(view), LoadOptions.SetLineInfo);
        var problems = new List<string>();

        foreach (var trigger in document.Descendants().Where(e => e.Name.LocalName == "EventTriggerBehavior"))
        {
            var eventName = (string?)trigger.Attribute("EventName");
            var line = ((System.Xml.IXmlLineInfo)trigger).LineNumber;

            if (trigger.Attribute("SourceObject") is not null)
            {
                problems.Add($"line {line}: SourceObject is set, so this test cannot tell which element raises '{eventName}'.");
                continue;
            }

            // EventTriggerBehavior > Interaction.Behaviors > the element that holds it.
            var owner = trigger.Parent?.Parent;
            if (owner is null || string.IsNullOrEmpty(eventName))
            {
                problems.Add($"line {line}: no EventName, or no element holds the behavior.");
                continue;
            }

            var type = Resolve(owner.Name);
            if (type is null)
            {
                problems.Add($"line {line}: cannot find the type of <{owner.Name.LocalName}> to look up '{eventName}' on.");
                continue;
            }

            if (type.GetRuntimeEvent(eventName) is null)
            {
                problems.Add($"line {line}: {type.Name} has no event named '{eventName}', so the behavior never fires.");
            }
        }

        Assert.True(problems.Count == 0, $"{view}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>The CLR type an element name stands for: the Avalonia namespace, or a
    /// <c>using:</c> namespace whose types are in an assembly the app loads.</summary>
    private static Type? Resolve(XName element)
    {
        // Load the assemblies the views draw their controls from.
        var assemblies = new[]
        {
            typeof(global::Avalonia.Controls.Control).Assembly,
            typeof(global::Avalonia.Visual).Assembly,
            typeof(Daqifi.Desktop.App).Assembly,
        }.Concat(AppDomain.CurrentDomain.GetAssemblies()).Distinct();

        var types = assemblies.SelectMany(SafeTypes).Where(t => t.Name == element.LocalName && t.IsPublic);

        if (element.Namespace == Avalonia)
        {
            return types.FirstOrDefault(t => t.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        }

        const string usingPrefix = "using:";
        var ns = element.NamespaceName;
        if (ns.StartsWith(usingPrefix, StringComparison.Ordinal))
        {
            var clrNamespace = ns[usingPrefix.Length..];
            return types.FirstOrDefault(t => t.Namespace == clrNamespace);
        }

        return null;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}
