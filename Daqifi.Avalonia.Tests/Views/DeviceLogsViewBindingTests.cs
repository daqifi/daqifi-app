using System.Xml.Linq;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards the two attributes that make <c>DeviceLogsView.axaml</c>'s 35 bindings the XAML compiler's
/// business instead of nobody's, and the facts those two attributes now lean on (part of issue #327).
///
/// <para>
/// The view declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// every binding in it resolved by reflection at run time. Measured on the parent commit: six
/// deliberate typos, one in each of the file's six binding scopes, built with <c>0 Error(s)</c> and
/// <c>155 Warning(s)</c> — byte-identical to a clean build — and not one diagnostic named the file.
/// Each of those six is now <c>AVLN2000</c>, naming the type its own scope resolves against.
/// </para>
///
/// <para>
/// So the member names are no longer asserted here: the compiler owns them, for all 35 bindings at
/// once rather than a hand-picked few. What is left for this class is the four things the compiler
/// still cannot see — and one of them is new to this view, because five of its six scopes are
/// <b>inferred</b> rather than declared. See
/// <see cref="The_inferred_item_scopes_are_the_element_types_of_the_collections_beside_them"/>.
/// </para>
///
/// <para>
/// Every assertion below <b>parses</b> the markup; none of them searches it. The distinction is not
/// hypothetical on this file: its explanatory comment discusses <c>x:CompileBindings</c> and the
/// bindings by name, so a whole-file <c>Assert.Contains</c> can be satisfied by prose while the real
/// attribute is gone from the tag — which is how PR #325's substring guard was defeated in Qodo round
/// 2 there. Parsing also lets each assertion say <i>which element</i> a binding sits on, which a
/// file-wide search cannot.
/// </para>
/// </summary>
public class DeviceLogsViewBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/DeviceLogsView.axaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    private static IEnumerable<XElement> Templates(XElement scope) =>
        scope.Descendants().Where(element => element.Name.LocalName == "DataTemplate");

    /// <summary>The element carrying the given automation id — the ids the UI harness already uses.</summary>
    private static XElement ElementWithAutomationId(XElement root, string automationId)
    {
        var matches = root.Descendants()
            .Where(element => element.Attribute("AutomationProperties.AutomationId")?.Value == automationId)
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"{View}: {matches.Count} elements carry AutomationProperties.AutomationId=\"{automationId}\", expected 1.");
        return matches[0];
    }

    /// <summary>The element type of a view-model collection, as a binding would see its items.</summary>
    private static Type ItemTypeOf(string collectionName)
    {
        var property = typeof(DeviceLogsViewModel).GetProperty(collectionName);
        Assert.True(property is not null, $"DeviceLogsViewModel has no public {collectionName} property.");

        return property!.PropertyType.GetInterfaces()
            .Concat([property.PropertyType])
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .Single();
    }

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Deleting
    /// <c>x:CompileBindings</c> puts all 35 bindings back on reflection while both heads and every
    /// other test in this assembly stay green — so this is the only thing that would object.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DeviceLogsViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Gap 2, and the one specific to how this view is written: five of its six binding scopes are
    /// <b>inferred</b> from the collection beside them, not declared in the markup.
    ///
    /// <para>
    /// PRs #325 and #328 each declared an <c>x:DataType</c> on every <c>DataTemplate</c>. Measured
    /// here, on Avalonia 12.1, that is not what does the work: with the four cell templates scoped to
    /// <c>SdCardFile</c> and with the four declarations deleted, the same typo is the same
    /// <c>AVLN2000</c> naming the same type — the compiler takes the row scope from the grid's
    /// <c>ItemsSource</c>. The inferred scope is also authoritative rather than a fallback: a cell
    /// template binding a view-model member (<c>BusyMessage</c>) is an error against
    /// <c>SdCardFile</c> either way, so a row template cannot silently reach past its item. The same
    /// holds for the <c>ComboBox</c>'s <c>DisplayMemberBinding</c>, which resolves against
    /// <c>IStreamingDevice</c> with no <c>DataType=</c> written anywhere.
    /// </para>
    ///
    /// <para>
    /// The consequence is what this test pins. Because the scopes are inferred, the ELEMENT TYPES of
    /// these two collections are load-bearing markup even though the markup never names them:
    /// changing what <c>DeviceFiles</c> holds silently re-scopes four templates, and changing what
    /// <c>ConnectedDevices</c> holds silently re-scopes the device list. Under a declared
    /// <c>x:DataType</c> that would be a build error; under inference it is only an error if the new
    /// type happens not to carry the same member names.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(nameof(DeviceLogsViewModel.DeviceFiles), typeof(SdCardFile))]
    [InlineData(nameof(DeviceLogsViewModel.ConnectedDevices), typeof(IStreamingDevice))]
    public void The_inferred_item_scopes_are_the_element_types_of_the_collections_beside_them(
        string collectionName, Type expectedItemType)
    {
        Assert.Equal(expectedItemType, ItemTypeOf(collectionName));

        var list = Root().Descendants()
            .SingleOrDefault(element => element.Attribute("ItemsSource")?.Value == $"{{Binding {collectionName}}}");
        Assert.True(
            list is not null,
            $"{View}: no single element binds ItemsSource to {collectionName}, so nothing takes its "
            + "item scope from that collection any more.");
    }

    /// <summary>
    /// The structural half of the same fact: a template's scope comes from where it sits. Every
    /// <c>DataTemplate</c> in this view is inside the list whose items it renders; lifting one into
    /// <c>UserControl.Resources</c> is a <b>build error</b>, not a silent re-scope.
    ///
    /// <para>
    /// <b>The rule</b>, because the earlier wording here had it wrong (issue #336): under
    /// <c>x:CompileBindings="True"</c> a <c>DataTemplate</c>'s bindings resolve against its own
    /// <c>x:DataType</c> where it has one, which <b>overrides</b> everything else; else against a
    /// scope <b>inferred</b> from the <c>ItemsSource</c> beside it; else against
    /// <c>XamlX.TypeSystem.XamlPseudoType</c>, on which no member resolves at all. It never falls
    /// back to the ancestor or view-model scope. Measured on this view, Avalonia 12.1.4, one change
    /// at a time: the NAME cell template copied into <c>UserControl.Resources</c> with its member
    /// name <b>unchanged</b> is <c>AVLN2000: Unable to resolve property or method of name 'FileName'
    /// on type 'XamlX.TypeSystem.XamlPseudoType'</c>; and the same lifted template binding
    /// <c>BusyMessage</c> — a <c>DeviceLogsViewModel</c> member, the very type this view's root
    /// <c>x:DataType</c> names — fails identically, which is what rules the inherited scope out.
    /// </para>
    ///
    /// <para>
    /// So this guard is not what stops the lift; the compiler is. It exists so the failure arrives
    /// <b>explained rather than debugged</b>. <c>AVLN2000</c> against a pseudo-type reads like a
    /// broken toolchain rather than like a template in the wrong place, and the obvious way to make
    /// it go away is to write an <c>x:DataType</c> on the lifted template — which clears the error
    /// and leaves the template out of its list. This test fails first and names the move instead.
    /// </para>
    ///
    /// <para>
    /// Which is also why not to reach for <c>x:DataType</c> by reflex. An explicit declaration is an
    /// override, so a plausible-but-wrong one is <b>worse than none</b> — and what it actually costs
    /// is a <b>dead</b> binding, not a wrong value, which is the part worth being precise about.
    /// <c>x:DataType</c> sets the type the <i>compiler</i> resolves against; it does <b>not</b>
    /// change the cell's runtime <c>DataContext</c>, which the <c>DataGrid</c> still sets to the
    /// item. Measured on this view's NAME cell template, headless, with the real template:
    /// <c>{Binding BusyMessage}</c> while undeclared is
    /// <c>AVLN2000 … on type 'Daqifi.Desktop.Models.SdCardFile'</c>; add
    /// <c>x:DataType="vm:DeviceLogsViewModel"</c> and the build is <c>0 Error(s)</c> — and the cell
    /// then renders <b>blank</b>. <c>TextBlock.Text</c> is <c>null</c> while the view model's
    /// <c>BusyMessage</c> is set, the cell's <c>DataContext</c> is still <c>SdCardFile</c>, and the
    /// only trace is one binding-log warning: <c>Unable to cast object of type
    /// 'Daqifi.Desktop.Models.SdCardFile' to type 'Daqifi.Desktop.ViewModels.DeviceLogsViewModel'</c>.
    /// </para>
    ///
    /// <para>
    /// Which is the sharpest reason not to write one. No build gate reads the binding log, and a
    /// blank cell is exactly what a "does the pane still look right?" pass forgives — so a wrong
    /// declaration does not merely fail to protect, it re-creates the silent member-name failure
    /// that putting <c>x:CompileBindings</c> on this view (#327) was there to remove, while reading
    /// like the thing that removed it. Declare a scope where the build asks for one, or where a
    /// template needs to narrow a wider item type, and not otherwise. See
    /// <see cref="BindingFacts.AssertTemplateScopedTo"/> for the full shape-by-shape rule.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_data_template_sits_inside_the_list_whose_items_it_renders()
    {
        var root = Root();
        var templates = Templates(root).ToList();

        Assert.NotEmpty(templates);
        Assert.All(templates, template => Assert.True(
            template.Ancestors().Any(a => a.Attribute("ItemsSource")?.Value.StartsWith("{Binding", StringComparison.Ordinal) == true),
            $"{View}: a DataTemplate sits outside any list with a bound ItemsSource, so there is no "
            + "item scope for the compiler to infer and its bindings resolve against XamlPseudoType "
            + "— a hard AVLN2000 even with correct member names, not an inherited scope. Move the "
            + "template back inside its list; adding x:DataType would clear the build error and "
            + "leave it in the wrong place."));
    }

    /// <summary>
    /// Gap 3: which <b>attribute</b> a binding feeds, and which <b>element</b> it sits on. Compiled
    /// bindings type-check the path, not the target — moved from <c>IsVisible</c> to <c>IsEnabled</c>,
    /// or between two sibling panels, the markup compiles just as happily and the screen is wrong.
    ///
    /// <para>
    /// These five panels are siblings in one Grid cell and exactly one of them is meant to be
    /// showing. A failed <c>bool</c> binding does not blank its control, it leaves the property at its
    /// <b>default</b>, and <c>IsVisible</c> defaults to <c>true</c> — so a typo in any of the five
    /// pinned "NO SD CARD" or "SD CARD ERROR" permanently on top of whichever panel is correct. The
    /// compiler now catches the typo; what it cannot catch is the five gates being swapped between
    /// panels, which is what keying each one by the automation id inside it does.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DeviceLogsNoFilesTitle", "{Binding HasNoFiles}")]
    [InlineData("DeviceLogsNoDeviceTitle", "{Binding !CanAccessSdCard}")]
    [InlineData("DeviceLogsNoSdCardTitle", "{Binding HasSdCardNotPresent}")]
    [InlineData("DeviceLogsBusyTitle", "{Binding HasSdCardBusy}")]
    [InlineData("DeviceLogsErrorTitle", "{Binding HasSdCardError}")]
    [InlineData("SdCardFileList", "{Binding HasFiles}")]
    public void Each_state_panel_is_gated_on_the_member_that_selects_it(string automationId, string gate)
    {
        var element = ElementWithAutomationId(Root(), automationId);

        var gated = element.AncestorsAndSelf()
            .FirstOrDefault(candidate => candidate.Attribute("IsVisible") is not null);
        Assert.True(
            gated is not null,
            $"{View}: nothing from '{automationId}' up to the root sets IsVisible, so this panel is "
            + "always showing.");

        Assert.Equal(gate, gated!.Attribute("IsVisible")!.Value);
    }

    /// <summary>
    /// Gap 3 for the rest of the interactive surface, again anchored to the element rather than to the
    /// file. The IMPORT button's command is the interesting one: it reaches back out of the row scope
    /// through <c>DataContext</c>, and the compiler follows that to the ancestor scope on its own —
    /// an explicit <c>((vm:DeviceLogsViewModel))</c> cast was written, measured to be unnecessary, and
    /// removed. Pinning the path here keeps that traversal from being "simplified" onto the row.
    /// </summary>
    [Theory]
    [InlineData("RefreshSdCardFilesButton", "Command", "{Binding RefreshFilesCommand}")]
    [InlineData("RefreshSdCardFilesButton", "IsEnabled", "{Binding CanAccessSdCard}")]
    [InlineData("RefreshSdCardFilesButton", "ToolTip.Tip", "{Binding ConnectionTypeMessage}")]
    [InlineData("SdCardStatusText", "Text", "{Binding SdCardStatusLine}")]
    [InlineData("SdCardStatusText", "Classes.sdError", "{Binding HasSdCardError}")]
    [InlineData("SdCardFileList", "ItemsSource", "{Binding DeviceFiles}")]
    [InlineData("SdCardFileNameText", "Text", "{Binding FileName}")]
    [InlineData("ImportSdCardFileButton", "CommandParameter", "{Binding}")]
    [InlineData("ImportSdCardFileButton", "Command", "{Binding $parent[DataGrid].DataContext.ImportFileCommand}")]
    public void The_bindings_feed_the_attributes_they_are_supposed_to(
        string automationId, string attribute, string binding) =>
        Assert.Equal(binding, ElementWithAutomationId(Root(), automationId).Attribute(attribute)?.Value);

    /// <summary>
    /// The device list's per-item binding, which has no element of its own to be keyed by: it is a
    /// binding assigned to a <i>property</i> of the <c>ComboBox</c>, which is why no <c>x:DataType</c>
    /// could cover it and why its scope has to be inferred.
    /// </summary>
    [Fact]
    public void The_device_combo_renders_its_items_by_display_name()
    {
        var combo = Root().Descendants()
            .SingleOrDefault(element => element.Attribute("DisplayMemberBinding") is not null);
        Assert.True(combo is not null, $"{View}: nothing sets DisplayMemberBinding.");

        Assert.Equal("{Binding ConnectedDevices}", combo!.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            $"{{Binding {nameof(IStreamingDevice.DeviceDisplayName)}}}",
            combo.Attribute("DisplayMemberBinding")?.Value);
    }

    /// <summary>
    /// The headline: how many bindings this view actually has, counted by parsing rather than by
    /// searching for <c>{Binding</c>, which the file's own prose also contains. Every one of them is
    /// now compile-checked; before this change none of them was.
    ///
    /// <para>
    /// A tripwire, not a specification. Adding a binding here is fine — but it lands in one of six
    /// scopes and five of those are inferred, so the number is the prompt to say which.
    /// </para>
    ///
    /// <para>
    /// Counting is only half the claim, which is why
    /// <see cref="No_subtree_switches_compile_checking_back_off"/> sits beside it: a binding under a
    /// subtree that opted out still counts here.
    /// </para>
    /// </summary>
    [Fact]
    public void All_thirty_five_bindings_are_compile_checked()
    {
        var bindings = Root().DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Count(attribute => attribute.Value.TrimStart().StartsWith("{Binding", StringComparison.Ordinal));

        Assert.Equal(35, bindings);
    }

    /// <summary>
    /// The hole the two tests above leave between them, found by Qodo on this PR and confirmed by
    /// measurement: <c>x:CompileBindings</c> is <b>inherited and overridable per subtree</b>, and
    /// neither guard looks below the root. <see cref="The_view_declares_its_data_type"/> reads the
    /// root attribute only, and <see cref="All_thirty_five_bindings_are_compile_checked"/> counts
    /// binding text, which an opted-out binding still contributes to.
    ///
    /// <para>
    /// Measured on this view: with <c>x:CompileBindings="False"</c> added to the <c>DataGrid</c> and
    /// <c>{Binding CreatedDateDisplayZZZ}</c> — a member that does not exist — inside it, the build is
    /// <c>0 Error(s)</c> and all 22 tests in this class pass. That is the same shape as the defect
    /// this whole PR is about, one layer up: a guard that reads as protection while a dead binding
    /// builds clean. The project sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
    /// an opted-out subtree is not merely unchecked in the abstract — it is back on reflection.
    /// </para>
    ///
    /// <para>
    /// #327 does contemplate switching checking off <i>narrowly</i>, for a binding that is genuinely
    /// inexpressible. This test is not a veto on that; it is the requirement that doing so be
    /// deliberate. Turning it off means editing this test and saying which subtree and why, which is
    /// exactly the conversation an escape hatch should cost.
    /// </para>
    /// </summary>
    [Fact]
    public void No_subtree_switches_compile_checking_back_off() =>
        BindingFacts.AssertNoEscapeHatch(View);
}
