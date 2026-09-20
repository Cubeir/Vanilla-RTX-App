using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The acrylic frame wrapped around every feature module's own markup. One copy, reached
/// through <see cref="ModuleOverlay.AttachChrome"/> rather than repeated in six XAML files,
/// so the six cannot drift apart.
/// </summary>
public sealed partial class ModuleOverlayChrome : UserControl
{
    public ModuleOverlayChrome() => InitializeComponent();

    /// <summary>The module's markup, which this frames.</summary>
    public UIElement? Body
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }
}
