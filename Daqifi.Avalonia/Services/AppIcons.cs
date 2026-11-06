using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Daqifi.Avalonia.Services;

/// <summary>Registers the icon set every view uses ("mdi-..." values).</summary>
/// <remarks>
/// Called from <see cref="App.Initialize"/>, so every host that builds the app gets the icons,
/// not only the ones whose entry point remembered to register them. A host that built a window
/// without registering threw "No IIconProvider with prefix matching mdi-cog" from MainWindow.
/// Registering twice throws, so a provider a host already registered counts as done.
/// </remarks>
public static class AppIcons
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) { return; }

            try
            {
                IconProvider.Current.Register<MaterialDesignIconProvider>();
            }
            catch (ArgumentException)
            {
                // "Prefix mdi conflicts with existing icon provider": registered already.
            }

            _registered = true;
        }
    }
}
