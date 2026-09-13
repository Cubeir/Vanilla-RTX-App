using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.System;

namespace Vanilla_RTX_App.Modules.BugTracker;

/// <summary>
/// The bug tracker's chrome: a modal acrylic blocker over the whole window, holding a panel
/// that renders <see cref="BugTracker"/>'s markdown through a WebView2. Fetching, caching and
/// HTML rendering all live on <see cref="BugTracker"/> - this only ever displays what it hands
/// back and reacts to the loading/error states it can produce.
///
/// Embedded directly in MainWindow.xaml as a plain sibling (à la SplashOverlay) rather than a
/// separate WinUIEx Window, since the ask was an in-window modal, not another top-level window.
/// </summary>
public sealed partial class BugTrackerOverlay : UserControl
{
    private bool _isOpen;
    private bool _webViewReady;
    private CancellationTokenSource? _cts;
    private Storyboard? _fadeStoryboard;

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;
    private const double FADE_MS = 100;

    public BugTrackerOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Opens the overlay if closed, closes it if open - a third way to dismiss it, alongside the X button and clicking outside the panel.</summary>
    public void Toggle()
    {
        if (_isOpen) Close();
        else Show();
    }

    /// <summary>Opens the overlay and kicks off content loading. Safe to call repeatedly - a re-open while already open is a no-op.</summary>
    public void Show()
    {
        if (_isOpen) return;
        _isOpen = true;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);

        // Forces measure/arrange to actually run before WebView2 initialization gets anywhere
        // near it. Nothing else guarantees a layout pass has happened yet at this point - the
        // cache-hit path in LoadContentAsync can resolve synchronously with no intervening
        // yield back to the dispatcher, and a WebView2 with no real bounds can fail to
        // initialize natively.
        UpdateLayout();

        _ = LoadContentAsync();
    }

    private void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;

        _cts?.Cancel();
        IsHitTestVisible = false;
        AnimateOpacity(0.0, () => Visibility = Visibility.Collapsed);
    }

    // =========================================================================
    // Content loading
    // =========================================================================

    private async Task LoadContentAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        ShowLoading("Loading the list...");

        try
        {
            var result = await BugTracker.GetListAsync(
                onFetching: text => LoadingText.Text = text,
                token: token);

            if (token.IsCancellationRequested) return;

            if (result.Status != BugTrackerStatus.Success || result.Markdown is null)
            {
                ShowError("An internet connection is required to fetch the list. Please check your connection and try again.");
                return;
            }

            await EnsureWebViewAsync();
            if (token.IsCancellationRequested) return;

            if (!_webViewReady)
            {
                ShowError("Could not initialize the embedded browser needed to display this list.");
                return;
            }

            var html = BugTracker.ToHtml(result.Markdown, ActualTheme == ElementTheme.Dark);
            await NavigateAndWaitAsync(html, token);
            if (token.IsCancellationRequested) return;

            ShowContent();
        }
        catch (OperationCanceledException)
        {
            // Overlay was closed mid-fetch - nothing to show.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTrackerOverlay] LoadContentAsync failed: {ex.Message}");
            ShowError("An internet connection is required to fetch the list. Please check your connection and try again.");
        }
    }

    // MarkdownWebView itself is never hidden via Visibility - see the XAML comment on the
    // content Grid. Loading/Error are opaque covers stacked on top of it instead.

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingState.Visibility = Visibility.Visible;
        ErrorState.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorState.Visibility = Visibility.Visible;
        LoadingState.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // WebView2
    // =========================================================================

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        try
        {
            var userDataFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            // The WinRT-projected CreateWithOptionsAsync marshals a C# null string to an
            // empty HSTRING rather than a true null, and an empty browserExecutableFolder
            // is what was producing "<blank> is not a valid Win32 application" (0x800700C1)
            // here - the plain .NET wrapper's null-means-null contract doesn't hold for this
            // projection. string.Empty is what actually means "use the installed runtime".
            var options = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, userDataFolder, options);
            await MarkdownWebView.EnsureCoreWebView2Async(env);

            MarkdownWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            MarkdownWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            MarkdownWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

            _webViewReady = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTrackerOverlay] WebView2 init failed: {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}");
            Trace.WriteLine(ex.ToString());
        }
    }

    /// <summary>
    /// Navigates and waits for the page to actually finish rendering before returning, so the
    /// caller can keep the loading spinner up until then instead of swapping to a blank/white
    /// WebView2 a frame before its content paints.
    /// </summary>
    private async Task NavigateAndWaitAsync(string html, CancellationToken token)
    {
        var navigationDone = new TaskCompletionSource<bool>();
        void OnNavigationCompleted(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs a) => navigationDone.TrySetResult(true);

        MarkdownWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            MarkdownWebView.NavigateToString(html);
            using (token.Register(() => navigationDone.TrySetCanceled(token)))
            {
                await navigationDone.Task;
            }
        }
        finally
        {
            MarkdownWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    /// <summary>
    /// Links in the rendered markdown must open in the user's real browser, not inside this
    /// panel, which has no navigation chrome to get back with. NavigateToString's own load
    /// never carries an http(s) Uri, so filtering on scheme is enough to leave it untouched.
    /// </summary>
    private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        _ = Launcher.LaunchUriAsync(new Uri(e.Uri));
    }

    private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _ = Launcher.LaunchUriAsync(new Uri(e.Uri));
    }

    // =========================================================================
    // Chrome
    // =========================================================================

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Blocker_PointerPressed(object sender, PointerRoutedEventArgs e) => Close();

    /// <summary>Stops the click from bubbling to the blocker - clicking inside the panel must never close it.</summary>
    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadContentAsync();

    private void AnimateOpacity(double to, Action? onCompleted)
    {
        if (AnimationsSuspended)
        {
            _fadeStoryboard?.Stop();
            Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        // Storyboard.Stop() reverts its target property to its pre-animation base value,
        // not to wherever the animation currently sits - without pinning the live value
        // first, stopping the fade-in storyboard here snapped Opacity straight back to its
        // XAML base of 0 before the fade-out animation even started, so "fading out" was
        // really a 0-to-0 animation. Same gotcha ReactorAnimator.StopTile works around.
        if (_fadeStoryboard is not null)
        {
            var current = Opacity;
            _fadeStoryboard.Stop();
            Opacity = current;
        }

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FADE_MS)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, this);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (onCompleted is not null)
            sb.Completed += (_, _) => onCompleted();

        _fadeStoryboard = sb;
        sb.Begin();
    }
}
