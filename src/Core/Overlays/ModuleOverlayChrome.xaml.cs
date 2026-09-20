using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The acrylic frame and close button wrapped around every feature module's own markup. One
/// copy, reached through <see cref="ModuleOverlay.AttachChrome"/> rather than repeated in six
/// XAML files, so the six cannot drift apart.
/// </summary>
public sealed partial class ModuleOverlayChrome : UserControl
{
    public ModuleOverlayChrome() => InitializeComponent();

    /// <summary>Raised by the close button. <see cref="ModuleOverlay"/> turns it into a close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The module's markup, which this frames.</summary>
    public UIElement? Body
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }

    /// <summary>
    /// The frame's own close button. Exposed because a module hides it while something of its
    /// own covers that corner, and XAML's generated name field is private to this class.
    /// </summary>
    internal Button CloseControl => CloseButton;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
