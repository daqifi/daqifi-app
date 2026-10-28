using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Daqifi.Avalonia.Services;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Daqifi.Avalonia.Android;

// NOTE: never write `Android.*`-qualified names inside this namespace — the enclosing
// `Daqifi.Avalonia.Android` namespace shadows the `Android` root namespace; unqualified
// names resolved via the usings above are safe.

/// <summary>
/// Hosts Avalonia's app-builder customization for the Android head.
/// </summary>
/// <remarks>
/// Avalonia 12 moved the builder hooks off the activity: <c>AvaloniaMainActivity</c> is now
/// non-generic and declares only <c>OnResume</c>/<c>OnDestroy</c>, while
/// <c>CreateAppBuilder</c>/<c>CustomizeAppBuilder</c> live on
/// <see cref="AvaloniaAndroidApplication{TApp}"/> (verified by reflection against
/// Avalonia.Android 12.0.5). The application object also outlives any single activity, which is
/// the correct scope for process-wide registrations like the icon provider — registering it per
/// activity would re-run on every recreation.
/// <para>
/// It is also the only place a registration can be made ahead of the view tree. Avalonia 12
/// builds every view from this object's <c>OnCreate</c> (<c>InitializeAppLifetime</c> →
/// <c>SetupWithLifetime</c> → <c>App.OnFrameworkInitializationCompleted</c>), and Android calls
/// it before any activity's <c>OnCreate</c>. Measured on a Galaxy A16 (Android 15): this
/// object's <c>OnCreate</c> entered at t+0, <c>MobileShellView</c>'s DataContext was assigned at
/// t+3.024 s, <c>base.OnCreate()</c> returned at t+3.067 s, and <c>MainActivity.OnCreate</c> was
/// not entered until t+3.185 s — 161 ms after the view had already read its bindings. Anything a
/// view reads eagerly, such as a one-way binding over a static, must be registered here and
/// before <c>base.OnCreate()</c>; see <c>OnCreate</c> below (#460).
/// </para>
/// <para>
/// The remaining registrations in <see cref="MainActivity"/> stay there because they are read
/// lazily, not because the activity is the correct scope — see the comment at their call site.
/// </para>
/// </remarks>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // BEFORE base.OnCreate(): base is what runs Avalonia's framework initialization, which
        // constructs MobileMainView → MobileShellView and assigns its DataContext. The USB
        // affordance's visibility is a one-way binding over MobileUsbConnector.IsAvailable, so
        // the connector has to exist by then or the binding latches false and the button never
        // renders — #460, which is exactly what happened while this registration sat in
        // MainActivity.OnCreate, 171 ms too late (measured; see the remarks above).
        //
        // Passing `this` does not retain anything short-lived: AndroidUsbConnector promotes its
        // argument to ApplicationContext (which is this object) and keeps only that plus the
        // UsbManager system service. The base context is attached before OnCreate, so
        // GetSystemService is safe here — unlike in the constructor.
        MobileUsbConnector.Current = new Usb.AndroidUsbConnector(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register the Material Design icon pack so `i:Icon` (the desktop nav rail's mdi-* glyphs,
        // reused by the mobile landscape shell) renders on Android too.
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
