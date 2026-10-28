using System.Text.RegularExpressions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #463: importing an SD file on the Storage pane's DEVICE LOGS half and switching back to
/// APP LOGS showed <c>NO LOGGED SESSIONS</c> — directly after an import of 4,876 samples, on a
/// real Galaxy A16. The session was persisted; only the view was stale, and a manual Refresh tap
/// brought it straight back.
///
/// <para>
/// The two halves own separate DataContexts (<c>LoggedSessionsMobileViewModel</c> and
/// <c>DeviceLogsViewModel</c>) and nothing bridges them. <c>StorageMobileView.Refresh()</c> exists
/// precisely for this and was called when the Storage TAB is entered — but not by the pivot
/// handler that returns from DEVICE LOGS, which is the only route an import can take. The method's
/// own doc comment described the failure it was written to prevent, on the one path it did not
/// cover.
/// </para>
///
/// <para>
/// Read off the source rather than by constructing the view. <c>StorageMobileView</c> is a
/// <c>UserControl</c> whose constructor builds a <c>LoggedSessionsMobileViewModel</c>, which opens
/// the application database and fires a reload; these tests run with no Avalonia lifetime, where
/// <c>Dispatcher.UIThread</c> and <c>LoggingManager.Instance</c> are both unusable. The guard is
/// therefore the same shape as this repo's dialog call-site guard, and leans on the same
/// <c>BindingFacts.CodeOnly</c> so that a commented-out call cannot satisfy it.
/// </para>
/// </summary>
public class StorageMobilePivotRefreshTests
{
    private const string Host = "Daqifi.Avalonia/Views/Mobile/StorageMobileView.axaml.cs";

    /// <summary>
    /// The regression. Returning to APP LOGS must re-hydrate the list, or anything imported or
    /// logged while the user was on the other half stays invisible until they think to tap
    /// Refresh — which reads as "the import did nothing".
    /// </summary>
    [Fact]
    public void Pivoting_back_to_app_logs_reloads_the_session_list() =>
        Assert.True(
            BodyOf("OnAppLogs").Contains("Refresh()", StringComparison.Ordinal),
            "StorageMobileView.OnAppLogs must call Refresh(): the APP LOGS list is otherwise "
            + "whatever it was when the Storage pane was last entered, and an SD import on the "
            + "DEVICE LOGS half leaves it reading NO LOGGED SESSIONS (#463).");

    /// <summary>
    /// The call has to be in the handler, not merely in the file. Pins that the method this guard
    /// reads is the pivot handler and that it is non-empty, so a rename or a gutted body fails
    /// loudly here instead of passing on a stray match elsewhere in the class.
    /// </summary>
    [Fact]
    public void The_guard_is_reading_the_pivot_handler()
    {
        var body = BodyOf("OnAppLogs");

        Assert.Contains("AppLogsHost.IsVisible = true", body, StringComparison.Ordinal);
        Assert.Contains("DeviceLogsHost.IsVisible = false", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the code-only body of a handler, comments and string literals blanked, so a call
    /// written in a comment cannot satisfy the guard above. Fails loudly rather than returning
    /// nothing when the method is not found in the shape expected — silently checking a subset is
    /// the defect this repo's dialog guard was corrected for.
    /// </summary>
    private static string BodyOf(string method)
    {
        var source = BindingFacts.CodeOnly(BindingFacts.Source(Host));

        var declaration = Regex.Match(source, $@"\bvoid\s+{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{Host}: no method named {method}.");

        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0, $"{Host}: {method} has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[(open + 1)..i]; }
        }

        Assert.Fail($"{Host}: {method}'s body never closes.");
        return string.Empty;
    }
}
