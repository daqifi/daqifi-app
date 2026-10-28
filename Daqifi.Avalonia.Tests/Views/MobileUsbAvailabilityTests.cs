using System.ComponentModel;
using Daqifi.Avalonia.Services;
using Daqifi.Avalonia.Views;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// The visibility gate behind the mobile shell's "Connect via USB (OTG)" button:
/// <c>MobileShellViewModel.IsUsbAvailable</c>, bound to <c>IsVisible</c> in
/// <c>MobileShellView.axaml</c>.
///
/// <para>
/// The property is a get-only computed read of a mutable static
/// (<see cref="MobileUsbConnector.Current"/>). A one-way binding reads such a property exactly
/// once — when the DataContext is assigned — and thereafter only when the source raises
/// <c>PropertyChanged</c> for that name. Before #460 nothing ever did, so whatever
/// <c>Current</c> happened to be at DataContext-assignment time was latched for the life of the
/// view.
/// </para>
///
/// <para>
/// On the Android head that moment is <b>before</b> an activity-scoped registration: Avalonia 12
/// runs framework initialization from <c>AvaloniaAndroidApplication&lt;TApp&gt;.OnCreate</c>,
/// which Android calls before any activity's <c>OnCreate</c>. Measured on a Galaxy A16 running
/// the pre-fix build: <c>MobileShellView</c>'s DataContext was assigned 161 ms before
/// <c>MainActivity.OnCreate</c> was entered and 171 ms before it registered the connector — and
/// the view never re-read it. At attach, <c>IsUsbAvailable</c> was already <c>true</c> while the
/// button's <c>IsVisible</c> was still <c>false</c>.
/// </para>
///
/// <para>
/// The fix has two halves and this file guards the load-bearing one.
/// <see cref="Registering_a_connector_after_construction_notifies_the_binding"/> is red on
/// <c>HEAD~</c>. Registering the connector earlier — which the Android head now also does — is
/// not sufficient on its own, because without notification any future re-ordering silently
/// reintroduces the same bug, and no gate in this repo can see it: the XAML guard in
/// <c>MobileShellViewBindingTests</c> only asserts the binding's text, and the visual-parity
/// capture renders this view on the desktop head, where the connector is null by design and the
/// button is correctly absent from baseline and capture alike.
/// </para>
/// </summary>
public sealed class MobileUsbAvailabilityTests : IDisposable
{
    private readonly IMobileUsbConnector? _registeredBefore = MobileUsbConnector.Current;

    public void Dispose() => MobileUsbConnector.Current = _registeredBefore;

    /// <summary>A head with no USB-host path (desktop / iOS) hides the affordance.</summary>
    [Fact]
    public void No_registered_connector_means_the_affordance_is_unavailable()
    {
        MobileUsbConnector.Current = null;
        using var shell = new MobileShellViewModel();

        Assert.False(MobileUsbConnector.IsAvailable);
        Assert.False(shell.IsUsbAvailable);
    }

    /// <summary>A head that registers one offers it — read at any point after registration.</summary>
    [Fact]
    public void A_registered_connector_makes_the_affordance_available()
    {
        MobileUsbConnector.Current = new StubConnector();
        using var shell = new MobileShellViewModel();

        Assert.True(MobileUsbConnector.IsAvailable);
        Assert.True(shell.IsUsbAvailable);
    }

    /// <summary>
    /// The defect. A connector registered <i>after</i> the view-model exists — which was the real
    /// Android order — must raise <c>PropertyChanged</c> for <c>IsUsbAvailable</c>, or the bound
    /// <c>IsVisible</c> stays at the <c>false</c> it latched and the button never renders.
    /// </summary>
    [Fact]
    public void Registering_a_connector_after_construction_notifies_the_binding()
    {
        MobileUsbConnector.Current = null;
        using var shell = new MobileShellViewModel();
        Assert.False(shell.IsUsbAvailable);   // what the binding reads and latches

        var notified = new List<string?>();
        void OnChanged(object? _, PropertyChangedEventArgs e) => notified.Add(e.PropertyName);
        shell.PropertyChanged += OnChanged;
        try
        {
            MobileUsbConnector.Current = new StubConnector();
        }
        finally
        {
            shell.PropertyChanged -= OnChanged;
        }

        Assert.True(shell.IsUsbAvailable);
        Assert.Contains(nameof(MobileShellViewModel.IsUsbAvailable), notified);
    }

    /// <summary>
    /// Unregistering notifies too, and — the half that is easy to omit — a disposed view-model
    /// stops hearing about it. The subscription is to a <b>static</b> event, so without the
    /// unhook in <c>Dispose</c> every mobile shell ever constructed stays reachable for the life
    /// of the process, and each one would re-raise <c>PropertyChanged</c> into a detached view.
    /// </summary>
    [Fact]
    public void A_disposed_view_model_stops_listening()
    {
        MobileUsbConnector.Current = new StubConnector();
        var shell = new MobileShellViewModel();

        var notified = new List<string?>();
        shell.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        shell.Dispose();
        MobileUsbConnector.Current = null;

        Assert.DoesNotContain(nameof(MobileShellViewModel.IsUsbAvailable), notified);
    }

    private sealed class StubConnector : IMobileUsbConnector
    {
        public bool IsDeviceAttached => false;

        public Task<UsbConnectResult> ConnectAsync() =>
            Task.FromResult(new UsbConnectResult(null, "stub"));
    }
}
