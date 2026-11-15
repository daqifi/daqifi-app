using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Daqifi.Desktop.View;

/// <summary>
/// A <see cref="TabItem"/> that an accessibility press selects (#630).
/// </summary>
/// <remarks>
/// <para>
/// VoiceOver sends the press action (<c>AXPress</c>) when the user pushes VO-Space on a tab. In
/// Avalonia 12.1.6 the macOS accessibility node handles that action only for a peer that is an
/// invoke, expand/collapse or toggle provider (<c>accessibilityPerformPress</c> in
/// <c>native/Avalonia.Native/src/OSX/automation.mm</c>). A <see cref="TabItem"/> gets a
/// <see cref="ListItemAutomationPeer"/>, which is only a selection-item provider, so the press did
/// nothing and still reported success. A VoiceOver user could not open the Devices tab, and so could
/// not connect a device.
/// </para>
/// <para>
/// This tab's peer is also an <see cref="IInvokeProvider"/>, and an invoke selects the tab. Nothing
/// else changes: the style key is still <see cref="TabItem"/>, so every <c>ControlTheme</c> and
/// <c>ItemContainerTheme</c> that targets <see cref="TabItem"/> applies as before.
/// </para>
/// </remarks>
public class InvokableTabItem : TabItem
{
    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(TabItem);

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new InvokableTabItemAutomationPeer(this);
}

/// <summary>
/// The peer of an <see cref="InvokableTabItem"/>: a list item peer whose invoke selects the tab.
/// </summary>
public class InvokableTabItemAutomationPeer : ListItemAutomationPeer, IInvokeProvider
{
    /// <summary>Creates the peer for <paramref name="owner"/>.</summary>
    public InvokableTabItemAutomationPeer(TabItem owner) : base(owner)
    {
    }

    /// <summary>Selects the tab, as a click does.</summary>
    public void Invoke() => Select();
}
