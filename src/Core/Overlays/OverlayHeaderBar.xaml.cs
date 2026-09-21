using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The header every full-window in-app overlay shares: icon, a hyperlinked title, optional
/// Back/Forward, Reload, an optional back-to-top button, a static guide sentence, a slot for the host's own controls
/// (<see cref="Actions"/>), and the big accent Close button. Knows nothing about what it's
/// showing or what closing means - it just raises events and exposes setters, so
/// <see cref="WebImportOverlay"/> and <see cref="MarkdownOverlay"/> can configure the same
/// visual block differently (the former needs Back/Forward, the latter its search).
/// </summary>
public sealed partial class OverlayHeaderBar : UserControl
{
    /// <summary>The title, which is a hyperlink - the host decides what it points at.</summary>
    public event RoutedEventHandler? TitleClick;

    /// <summary>Only raised while the nav buttons are shown (<see cref="SetNavButtonsVisible"/>).</summary>
    public event RoutedEventHandler? BackClick;

    /// <inheritdoc cref="BackClick"/>
    public event RoutedEventHandler? ForwardClick;

    /// <summary>Not raised while a cooldown is running - see <see cref="SetReloadCooldown"/>.</summary>
    public event RoutedEventHandler? ReloadClick;

    /// <summary>Only raised while the button is shown (<see cref="SetScrollTopButtonVisible"/>).</summary>
    public event RoutedEventHandler? ScrollTopClick;

    /// <summary>
    /// The primary accent button. What "close" means is the host's business: the web overlay
    /// treats it as Done and imports what was downloaded, the markdown overlay just returns.
    /// </summary>
    public event RoutedEventHandler? CloseClick;

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(OverlayHeaderBar),
        new PropertyMetadata(null, (d, e) => ((OverlayHeaderBar)d).ActionsHost.Content = e.NewValue));

    /// <summary>
    /// The host's own controls, shown right against the Close button. Set in the host's XAML
    /// (<c>&lt;local:OverlayHeaderBar.Actions&gt;</c>) rather than built here, so their
    /// <c>x:Name</c> fields and handlers belong to the host - this control only gives them a
    /// place. Null leaves no gap.
    /// </summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public OverlayHeaderBar()
    {
        InitializeComponent();

        // The Close button's accent bevel is an imperative color choice (ThemeService.
        // GetBevelColor), not a ThemeResource that re-resolves itself, so it has to be
        // recomputed by hand on every theme change - exactly like MainWindow's Preview toggle.
        ApplyCloseButtonBevel(ThemeService.ResolveInitialTheme());
        ThemeService.ThemeChanged += ApplyCloseButtonBevel;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= ApplyCloseButtonBevel;
    }

    /// <summary>Repaints the Close button's accent seam for a theme. See the constructor for why by hand.</summary>
    private void ApplyCloseButtonBevel(ElementTheme theme) =>
        CloseButtonBevel.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: true));

    /// <summary>The leading glyph, as a Segoe Fluent character - an empty string leaves a blank slot.</summary>
    public void SetIcon(string glyph) => HeaderIcon.Glyph = glyph;

    /// <summary>The hyperlinked title text. Clicking it raises <see cref="TitleClick"/>.</summary>
    public void SetTitleText(string text) => HeaderTitleText.Text = text;

    /// <summary>
    /// The static sentence telling the user what "done" means for this particular page.
    /// An empty string collapses the row rather than leaving a gap, so a plain document
    /// viewer with nothing to guide reads as deliberately bare.
    /// </summary>
    public void SetGuideText(string text)
    {
        GuideText.Text = text;
        GuideText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Only <see cref="WebImportOverlay"/> has anywhere to go back/forward to - a plain document viewer collapses both, which also reclaims their column width since it's Auto-sized.</summary>
    public void SetNavButtonsVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = visibility;
        ForwardButton.Visibility = visibility;
    }

    /// <summary>
    /// The back-to-top button beside Reload. Hidden by default: only a host with a long page
    /// has a top to go back to, and it shows the button only while that page is on screen.
    /// </summary>
    public void SetScrollTopButtonVisible(bool visible) =>
        ScrollTopButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Greys Back/Forward against the host's real history. Separate from
    /// <see cref="SetNavButtonsVisible"/> because they answer different questions: whether
    /// this overlay navigates at all, versus whether there is anywhere to go right now.
    /// </summary>
    public void SetNavButtonsEnabled(bool canGoBack, bool canGoForward)
    {
        BackButton.IsEnabled = canGoBack;
        ForwardButton.IsEnabled = canGoForward;
    }

    /// <summary>
    /// Null (or &lt;= 0) shows the plain reload icon, enabled. A positive value disables the
    /// button and shows that many seconds as a number in its place instead - see
    /// <see cref="MarkdownOverlay"/>'s reload cooldown for the caller that uses this.
    /// </summary>
    public void SetReloadCooldown(int? secondsRemaining)
    {
        if (secondsRemaining is int s && s > 0)
        {
            ReloadIcon.Visibility = Visibility.Collapsed;
            ReloadCountdownText.Visibility = Visibility.Visible;
            ReloadCountdownText.Text = s.ToString();
            ReloadButton.IsEnabled = false;
            ToolTipService.SetToolTip(ReloadButton, $"You can reload again in {s}s");
        }
        else
        {
            ReloadIcon.Visibility = Visibility.Visible;
            ReloadCountdownText.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
            ToolTipService.SetToolTip(ReloadButton, "Reload");
        }
    }

    /// <summary>
    /// Relabels the primary button, which is the only cue the user gets about what closing
    /// will do - "Done" when something will be imported on the way out, "Return" when
    /// nothing will.
    /// </summary>
    public void SetCloseButton(string glyph, string text, string tooltip)
    {
        CloseButtonIcon.Glyph = glyph;
        CloseButtonText.Text = text;
        ToolTipService.SetToolTip(CloseButton, tooltip);
    }

    /// <summary>XAML handler. Forwarded as <see cref="TitleClick"/>; this control has no opinion on what it means.</summary>
    private void HeaderTitleLink_Click(object sender, RoutedEventArgs e) => TitleClick?.Invoke(this, e);

    /// <summary>XAML handler. Forwarded as <see cref="BackClick"/>.</summary>
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, e);

    /// <summary>XAML handler. Forwarded as <see cref="ForwardClick"/>.</summary>
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, e);

    /// <summary>XAML handler. Forwarded as <see cref="ReloadClick"/>.</summary>
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadClick?.Invoke(this, e);

    /// <summary>XAML handler. Forwarded as <see cref="ScrollTopClick"/>.</summary>
    private void ScrollTopButton_Click(object sender, RoutedEventArgs e) => ScrollTopClick?.Invoke(this, e);

    /// <summary>XAML handler. Forwarded as <see cref="CloseClick"/>.</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseClick?.Invoke(this, e);
}
