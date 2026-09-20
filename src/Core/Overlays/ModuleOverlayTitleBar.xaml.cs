using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The titlebar half of a feature module: the strip of buttons the open module owns, and the
/// button that closes it. It is its own control rather than markup inside MainWindow because
/// the two clusters carry real behaviour of their own - taking a module's buttons and giving
/// them back, and a divider that follows whether anything ended up on screen.
///
/// <para><b>MainWindow supplies only the position.</b> Its Margin is the width of MainWindow's
/// own titlebar cluster on the left and of the system's caption buttons on the right - the two
/// things this has to sit between and neither of which this control can know. Everything else,
/// the sizes and the dividers included, is declared here so the two clusters cannot drift from
/// the one they are pretending to be part of.</para>
///
/// <para><b>Both clusters are hidden unless a module is open.</b> They are shown by
/// <see cref="Strip"/> and <see cref="ShowReturn"/>, which MainWindow calls as it opens and
/// closes one.</para>
/// </summary>
public sealed partial class ModuleOverlayTitleBar : UserControl
{
    public ModuleOverlayTitleBar() => InitializeComponent();

    /// <summary>Raised when the user asks to leave the open module.</summary>
    public event EventHandler? ReturnRequested;

    /// <summary>
    /// The open module's own titlebar buttons, or null when it has none and when none is
    /// open. The element is the module's, not a copy of it, so its <c>x:Name</c> field and
    /// every <c>Click</c> handler on it keep working - which is why those buttons are still
    /// declared in the module's own XAML. It arrives already detached from that markup; see
    /// <see cref="ModuleOverlay.PrepareContent"/> for why that has to happen there.
    /// </summary>
    public FrameworkElement? Strip
    {
        get => StripHost.Child as FrameworkElement;
        set
        {
            StripHost.Child = value;
            StripCluster.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Shows or hides the return button and the hairline that fences it off from the system's caption buttons.</summary>
    public void ShowReturn(bool show) =>
        ReturnCluster.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    // An empty strip gets no divider. A module can hand over buttons that are themselves
    // hidden, and the host is the only thing that knows whether anything ended up on screen.
    private void StripHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        StripSeparator.Visibility = e.NewSize.Width > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ReturnButton_Click(object sender, RoutedEventArgs e) =>
        ReturnRequested?.Invoke(this, EventArgs.Empty);
}
