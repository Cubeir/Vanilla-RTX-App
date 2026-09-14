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

    // Manual refresh cooldown - independent of BugTracker's own 30-minute auto-refresh
    // cooldown, and enforced entirely here: BugTracker.ForceRefreshAsync will happily fire
    // as often as it's called, this is what stops the button from being smashed.
    private const int MANUAL_REFRESH_COOLDOWN_SECONDS = 60;
    private DateTime _manualRefreshCooldownUntilUtc = DateTime.MinValue;
    private DispatcherTimer? _refreshCooldownTimer;

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

        // The cooldown deadline itself keeps ticking in real time regardless - this only
        // resumes the timer that drives its on-screen countdown, which was stopped in Close().
        // If the deadline already passed while closed, restore the normal icon instead - no
        // tick ever ran to do it while the timer was stopped.
        if (DateTime.UtcNow < _manualRefreshCooldownUntilUtc)
        {
            UpdateRefreshCountdownText();
            _refreshCooldownTimer?.Start();
            UpdateRefreshButtonAvailability();
        }
        else
        {
            EndManualRefreshCooldown();
        }

        _ = LoadContentAsync();
    }

    private void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;

        _cts?.Cancel();
        _refreshCooldownTimer?.Stop();
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
                onBackgroundUpdate: markdown => _ = ApplyBackgroundUpdateAsync(markdown, token),
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

    /// <summary>
    /// Fired by <see cref="BugTracker.GetListAsync"/> when a quiet cooldown-expired background
    /// refresh actually landed new content. The cache was already showing by this point - this
    /// just swaps the rendered page for the fresher one, with no loading/error state involved.
    /// </summary>
    private async Task ApplyBackgroundUpdateAsync(string markdown, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) return;

            await EnsureWebViewAsync();
            if (token.IsCancellationRequested || !_webViewReady) return;

            var html = BugTracker.ToHtml(markdown, ActualTheme == ElementTheme.Dark);
            await NavigateAndWaitAsync(html, token);
            if (!token.IsCancellationRequested) ShowContent();
        }
        catch (OperationCanceledException)
        {
            // Overlay was closed before the background refresh landed.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTrackerOverlay] ApplyBackgroundUpdateAsync failed: {ex.Message}");
        }
    }

    // MarkdownWebView itself is never hidden via Visibility - see the XAML comment on the
    // content Grid. Loading/Error are opaque covers stacked on top of it instead.

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingState.Visibility = Visibility.Visible;
        ErrorState.Visibility = Visibility.Collapsed;
        UpdateRefreshButtonAvailability();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorState.Visibility = Visibility.Visible;
        LoadingState.Visibility = Visibility.Collapsed;
        UpdateRefreshButtonAvailability();
    }

    private void ShowContent()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        UpdateRefreshButtonAvailability();
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

    // =========================================================================
    // Manual refresh
    // =========================================================================

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // The button is disabled for the entire cooldown, so this is a defensive check rather
        // than the actual enforcement.
        if (DateTime.UtcNow < _manualRefreshCooldownUntilUtc) return;

        StartManualRefreshCooldown();
        _ = ManualRefreshAsync();
    }

    private async Task ManualRefreshAsync()
    {
        var token = _cts?.Token ?? default;
        try
        {
            var result = await BugTracker.ForceRefreshAsync(token);
            if (token.IsCancellationRequested) return;

            if (result.Status != BugTrackerStatus.Success || result.Markdown is null)
            {
                // Nothing to swap to - leave whatever is already on screen (cache content, or
                // the error state) exactly as it is.
                return;
            }

            await EnsureWebViewAsync();
            if (token.IsCancellationRequested || !_webViewReady) return;

            var html = BugTracker.ToHtml(result.Markdown, ActualTheme == ElementTheme.Dark);
            await NavigateAndWaitAsync(html, token);
            if (!token.IsCancellationRequested) ShowContent();
        }
        catch (OperationCanceledException)
        {
            // Overlay was closed mid-refresh.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTrackerOverlay] ManualRefreshAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts (or resumes, on reopen) the 60-second cooldown. Driven off a wall-clock deadline
    /// rather than a tick count, so it keeps counting down in real time even while the overlay
    /// is closed - re-showing the panel mid-cooldown resumes it instead of resetting it.
    /// </summary>
    private void StartManualRefreshCooldown()
    {
        _manualRefreshCooldownUntilUtc = DateTime.UtcNow.AddSeconds(MANUAL_REFRESH_COOLDOWN_SECONDS);

        RefreshIcon.Visibility = Visibility.Collapsed;
        RefreshCountdownText.Visibility = Visibility.Visible;
        UpdateRefreshCountdownText();
        UpdateRefreshButtonAvailability();

        _refreshCooldownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshCooldownTimer.Tick -= RefreshCooldownTimer_Tick;
        _refreshCooldownTimer.Tick += RefreshCooldownTimer_Tick;
        _refreshCooldownTimer.Start();
    }

    private void RefreshCooldownTimer_Tick(object? sender, object e)
    {
        if (DateTime.UtcNow >= _manualRefreshCooldownUntilUtc)
        {
            EndManualRefreshCooldown();
            return;
        }
        UpdateRefreshCountdownText();
    }

    private void EndManualRefreshCooldown()
    {
        _refreshCooldownTimer?.Stop();
        RefreshIcon.Visibility = Visibility.Visible;
        RefreshCountdownText.Visibility = Visibility.Collapsed;
        UpdateRefreshButtonAvailability();
    }

    private void UpdateRefreshCountdownText()
    {
        var remaining = (int)Math.Ceiling((_manualRefreshCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);
        RefreshCountdownText.Text = Math.Max(remaining, 0).ToString();
    }

    /// <summary>
    /// Refresh is only ever off-limits while there's nothing yet to refresh from (the initial
    /// load is still in flight) or while its own cooldown is running - it stays available over
    /// both the content and error states.
    /// </summary>
    private void UpdateRefreshButtonAvailability()
    {
        var loading = LoadingState.Visibility == Visibility.Visible;
        var coolingDown = DateTime.UtcNow < _manualRefreshCooldownUntilUtc;
        RefreshButton.IsEnabled = !loading && !coolingDown;
    }

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
