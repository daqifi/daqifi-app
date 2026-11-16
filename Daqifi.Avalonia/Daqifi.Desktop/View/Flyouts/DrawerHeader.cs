using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace Daqifi.Desktop.View.Flyouts;

/// <summary>
/// The one header of the four right-side panels — Settings, Notifications, Plot Settings and Log
/// Summary (#615): a small capitals overline, a large light title, and a round close button.
/// </summary>
/// <remarks>
/// Before this, the settings drawer wrote its own header and the three SplitView flyouts each wrote
/// a different one, at a different size and height, so four panels that open from the same edge
/// looked like two designs. The look lives in one place, the <c>ControlTheme</c> keyed by this type
/// in <c>Resources/Shell.axaml</c>; a panel only says what it is called and how it closes.
/// </remarks>
public class DrawerHeader : TemplatedControl
{
    /// <summary>The capitals line over the title, for example PREFERENCES.</summary>
    public static readonly StyledProperty<string?> OverlineProperty =
        AvaloniaProperty.Register<DrawerHeader, string?>(nameof(Overline));

    /// <summary>The panel title, for example Settings.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DrawerHeader, string?>(nameof(Title));

    /// <summary>What the close button runs. Each panel clears its own open flag.</summary>
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DrawerHeader, ICommand?>(nameof(CloseCommand));

    public string? Overline
    {
        get => GetValue(OverlineProperty);
        set => SetValue(OverlineProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }
}
