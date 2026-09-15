using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The chrome every full-window in-app overlay shares: icon, a hyperlinked title, optional
/// Back/Forward, Reload, a static guide sentence, and the big accent Close button. Knows
/// nothing about what it's showing or what closing means - it just raises events and exposes
/// setters, so <see cref="WebImportOverlay"/> and <see cref="MarkdownOverlay"/> can configure
/// the same visual block differently (the former needs Back/Forward, the latter never does).
/// </summary>
public sealed partial class OverlayHeaderBar : UserControl
{
    public event RoutedEventHandler? TitleClick;
    public event RoutedEventHandler? BackClick;
    public event RoutedEventHandler? ForwardClick;
    public event RoutedEventHandler? ReloadClick;
    public event RoutedEventHandler? CloseClick;

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

    private void ApplyCloseButtonBevel(ElementTheme theme) =>
        CloseButtonBevel.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: true));

    public void SetIcon(string glyph) => HeaderIcon.Glyph = glyph;

    public void SetTitleText(string text) => HeaderTitleText.Text = text;

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

    public void SetCloseButton(string glyph, string text, string tooltip)
    {
        CloseButtonIcon.Glyph = glyph;
        CloseButtonText.Text = text;
        ToolTipService.SetToolTip(CloseButton, tooltip);
    }

    private void HeaderTitleLink_Click(object sender, RoutedEventArgs e) => TitleClick?.Invoke(this, e);
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, e);
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, e);
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadClick?.Invoke(this, e);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseClick?.Invoke(this, e);
}
