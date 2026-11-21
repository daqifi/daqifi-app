using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>LoggedDataPanePrototype.axaml</c>'s 102 bindings the XAML compiler's business
/// rather than nobody's (issue #327). The compiler owns the member names; what it cannot see is the
/// declarations being dropped or repointed, or an escape hatch reopened beneath them.
/// </summary>
public class LoggedDataPanePrototypeBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LoggedDataPanePrototype.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 102 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_pane_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Two nested scopes are claims the compiler trusts without checking them against the object the
    /// template will meet: OxyPlot's <c>PlotBase.ShowTracker</c> assigns a <c>TrackerHitResult</c>, and
    /// a <c>ListBoxItem</c>'s DataContext is its item from <c>LoggingSessions</c>. Pinned so a
    /// plausible-looking repoint cannot quietly re-scope them.
    ///
    /// <para>
    /// Read from the parsed markup, not the raw text, so a copy of either tag in a comment cannot
    /// stand in for the live declaration (Qodo round 1 on PR #396). Every non-root <c>x:DataType</c>
    /// in the file is listed, so a third one appearing is a failure too, not a silent addition.
    /// </para>
    /// </summary>
    [Fact]
    public void The_nested_scopes_name_what_the_runtime_assigns()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = XDocument.Parse(BindingFacts.Source(View)).Root!;

        var nested = root.Descendants()
            .Where(element => element.Attribute(x + "DataType") is not null)
            .Select(element =>
                $"{element.Parent?.Name.LocalName}/{element.Name.LocalName}"
                + $"[{element.Attribute("TargetType")?.Value}] {element.Attribute(x + "DataType")!.Value}")
            .OrderBy(declaration => declaration, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "ListBox.ItemContainerTheme/ControlTheme[ListBoxItem] logger:LoggingSession",
                "PlotView.DefaultTrackerTemplate/ControlTemplate[] oxycore:TrackerHitResult",
            ],
            nested);
    }

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);

    /// <summary>
    /// The session drawer's NAME box must not bind its text to <c>LoggingSession.Name</c> (#681).
    /// That getter returns "Session {ID}" for an empty name, so a binding filled the box again the
    /// moment the user emptied it, with the caret at the start, and the next name typed was saved as
    /// "Pump testSession 2". The code-behind shows the name instead, and only when an edit ends: on
    /// LostFocus, on Enter, or when the drawer closes.
    ///
    /// <para>
    /// Markup only. What the box does under real key events is HeadlessBench's
    /// <c>--scripted session-name-box</c> (row LOGGED-NAME), which CI runs; this row is the quick
    /// signal that the binding came back, or that one of the two handlers that end an edit fell off.
    /// </para>
    /// </summary>
    [Fact]
    public void The_session_name_box_is_not_bound_to_the_name_getter_and_ends_an_edit_on_leave_and_enter()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var box = XDocument.Parse(BindingFacts.Source(View)).Root!
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "SessionNameTextBox");

        Assert.Equal("TextBox", box.Name.LocalName);
        Assert.Null(box.Attribute("Text"));
        Assert.DoesNotContain(box.Elements(), element => element.Name.LocalName == "TextBox.Text");
        // TextChanging, not TextChanged: TextChanged arrives later, through the dispatcher, after the
        // pane's own write has ended, so the handler cannot tell that write from typing. Two such
        // deferred events saved names the user never typed (#681, and in review of #684 an empty
        // name over a saved one on a tab switch).
        Assert.Equal("OnSessionNameChanged", box.Attribute("TextChanging")?.Value);
        Assert.Null(box.Attribute("TextChanged"));
        Assert.Equal("OnSessionNameLostFocus", box.Attribute("LostFocus")?.Value);
        Assert.Equal("OnSessionNameKeyDown", box.Attribute("KeyDown")?.Value);
    }
}
