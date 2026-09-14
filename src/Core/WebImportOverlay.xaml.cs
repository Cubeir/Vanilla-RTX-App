using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.System;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// A full-window WebView2 overlay that lets the user browse a real, external site (TechPowerUp
/// for DLSS DLLs, bedrock.graphics/creator for BetterRTX presets) from inside the app, and hands
/// whatever they downloaded there back to the caller in one batch the moment they close it.
///
/// <para><b>Why this exists.</b> Both DLSS Swapper and BetterRTX Manager used to just launch the
/// user's real browser via a <c>HyperlinkButton</c> and leave them to find their way back with a
/// downloaded file in hand. Neither site has an API worth scraping (see the design notes this
/// replaced), so the reliable middle ground is: let the user do exactly what they'd do in a real
/// browser, just without leaving the app, and watch the one folder their downloads land in.</para>
///
/// <para><b>Deliberately detachable.</b> This control knows nothing about DLSS or BetterRTX - it
/// takes a URL, a set of file extensions to watch for, static instruction text, and a callback,
/// and that's the entire contract. Everything module-specific (what counts as a valid import, how
/// the result gets merged into a cache, how the list gets redrawn) stays in the caller.</para>
///
/// <para><b>Only watched extensions ever get intercepted.</b> A download whose extension isn't in
/// the watched set is left completely alone - it downloads to the user's real Downloads folder
/// through WebView2's own normal UI, exactly like it would in an actual browser. Silently
/// swallowing an unrelated download into a staging folder that gets deleted on close would mean a
/// user occasionally loses a file with no idea why; this way nothing is ever hidden from them.</para>
///
/// <para><b>Downloads only ever get imported once, at close.</b> Not as they arrive - the
/// <see cref="DownloadsShelf"/> is what tells the user, live, what's been downloaded and whether
/// it finished, so they know when it's safe to click Done. The actual import - and the cleanup of
/// the staged files afterward - happens in one batch right after.</para>
/// </summary>
public sealed partial class WebImportOverlay : UserControl
{
    private bool _isOpen;
    private bool _webViewReady;
    private Storyboard? _fadeStoryboard;

    private string _stagingFolder = string.Empty;
    private string _lastUrl = string.Empty;
    private IReadOnlyList<string> _watchedExtensions = Array.Empty<string>();
    private Func<IReadOnlyList<string>, Task>? _onFilesReady;

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;
    private const double FADE_MS = 150;

    public WebImportOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the overlay and navigates to <paramref name="url"/>. Any download whose extension is
    /// in <paramref name="watchedExtensions"/> is staged in a folder keyed by
    /// <paramref name="stagingTag"/> (so two overlay instances - e.g. the DLSS and BetterRTX
    /// windows both open at once - can never collide) and shown live in the downloads shelf; the
    /// full set of matches is handed to <paramref name="onFilesReady"/> in one call once the user
    /// closes the overlay. If nothing matched, <paramref name="onFilesReady"/> is never called at
    /// all - a session where the user just looked around and downloaded nothing behaves exactly
    /// as if this feature didn't exist. <paramref name="guideText"/> is a short, static sentence
    /// telling the user what "done" means for this particular site (e.g. when to click Done).
    /// </summary>
    public void Show(
        string url,
        string title,
        string glyph,
        string guideText,
        string stagingTag,
        IReadOnlyList<string> watchedExtensions,
        Func<IReadOnlyList<string>, Task> onFilesReady)
    {
        if (_isOpen) return;
        _isOpen = true;

        HeaderTitleText.Text = title;
        HeaderIcon.Glyph = glyph;
        GuideText.Text = guideText;
        _watchedExtensions = watchedExtensions;
        _onFilesReady = onFilesReady;
        _lastUrl = url;

        DownloadsListPanel.Children.Clear();
        DownloadsShelf.Visibility = Visibility.Collapsed;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);

        // Forces a layout pass before WebView2 initialization gets anywhere near it - see the
        // identical comment in BugTrackerOverlay.Show for why this matters.
        UpdateLayout();

        _ = LoadAsync(url, stagingTag);
    }

    private void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () =>
        {
            Visibility = Visibility.Collapsed;
            _ = FinalizeImportsAsync();
        });
    }

    /// <summary>
    /// The host window's own Closed handler should call this. The Done button is the normal
    /// close path, but nothing stops a user from closing the whole DLSS/BetterRTX window while
    /// still mid-browse - the system titlebar's close button stays reachable the entire time
    /// this overlay is open (see the XAML comment on <c>Panel</c>'s margin). Without this, a
    /// download that landed seconds before that would sit in the staging folder forever, never
    /// imported and never cleaned up - silently losing exactly the file the user just fetched.
    /// No fade-out here; the window is already on its way down.
    /// </summary>
    public void CloseIfOpen()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _ = FinalizeImportsAsync();
    }

    // =========================================================================
    // Loading the page
    // =========================================================================

    private async Task LoadAsync(string url, string stagingTag)
    {
        ShowLoading("Loading...");
        PrepareStagingFolder(stagingTag);

        await EnsureWebViewAsync();
        if (!_isOpen) return;

        if (!_webViewReady)
        {
            ShowError("Could not initialize the embedded browser needed to show this page.");
            return;
        }

        await NavigateAndWaitFirstAsync(url);
    }

    /// <summary>
    /// Waits for the very first navigation only, so the loading spinner covers the initial page
    /// load but never reappears over ordinary in-site browsing afterward - a link click on
    /// TechPowerUp shouldn't cover the page with our own spinner while the browser handles it
    /// exactly like it always does.
    /// </summary>
    private async Task NavigateAndWaitFirstAsync(string url)
    {
        var navigationDone = new TaskCompletionSource<bool>();
        void OnNavigationCompleted(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs a) =>
            navigationDone.TrySetResult(a.IsSuccess);

        ImportWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            ImportWebView.CoreWebView2.Navigate(url);
            var success = await navigationDone.Task;

            if (!_isOpen) return;

            if (success) ShowContent();
            else ShowError("Couldn't load the page. Check your internet connection and try again.");
        }
        finally
        {
            ImportWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

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

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = NavigateAndWaitFirstAsync(_lastUrl);

    // =========================================================================
    // WebView2
    // =========================================================================

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        try
        {
            // A dedicated profile folder, separate from BugTrackerOverlay's own WebView2 folder -
            // this overlay can be open on a DLSS or BetterRTX window at the same time the bug
            // tracker overlay is open on MainWindow, and two CoreWebView2Environments pointed at
            // the same user data folder from the same process is not a combination worth risking.
            var userDataFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WebView2_WebImport");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, userDataFolder, options);
            await ImportWebView.EnsureCoreWebView2Async(env);

            ImportWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ImportWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            ImportWebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
            ImportWebView.CoreWebView2.HistoryChanged += (_, _) => UpdateNavButtons();

            _webViewReady = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] WebView2 init failed: {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}");
            Trace.WriteLine(ex.ToString());
        }
    }

    /// <summary>
    /// Popups (target="_blank" links - social share buttons, "Sign in", etc.) open in the user's
    /// real browser rather than spawning a second in-app tab this overlay has no chrome for.
    /// </summary>
    private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _ = Launcher.LaunchUriAsync(new Uri(e.Uri));
    }

    /// <summary>
    /// Only a download whose extension is in the watched set gets redirected into the staging
    /// folder and tracked in the shelf. Everything else is left completely alone - no
    /// <see cref="CoreWebView2DownloadStartingEventArgs.Handled"/>, no touched
    /// <see cref="CoreWebView2DownloadStartingEventArgs.ResultFilePath"/> - so it downloads to the
    /// user's real Downloads folder through WebView2's own default UI exactly like it would in an
    /// actual browser. The alternative - capturing every download unconditionally - means an
    /// unrelated file (an export, a screenshot, anything not a DLSS/BetterRTX file) would vanish
    /// into a folder that gets deleted on close with no trace and no way back.
    /// </summary>
    private void CoreWebView2_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var suggestedName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggestedName))
                return;

            var ext = Path.GetExtension(suggestedName);
            if (!_watchedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(_stagingFolder);

            var destPath = Path.Combine(_stagingFolder, suggestedName);
            var nameNoExt = Path.GetFileNameWithoutExtension(destPath);
            var counter = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(_stagingFolder, $"{nameNoExt}-{counter}{ext}");
                counter++;
            }

            e.ResultFilePath = destPath;
            e.Handled = true;

            Trace.WriteLine($"[WebImportOverlay] Download starting -> {destPath}");

            var row = AddDownloadRow(suggestedName);
            var op = e.DownloadOperation;

            void UpdateProgress()
            {
                if (op.TotalBytesToReceive > 0)
                {
                    var percent = (int)(op.BytesReceived * 100 / op.TotalBytesToReceive);
                    row.ProgressText.Text = $"{percent}%";
                }
                else
                {
                    row.ProgressText.Text = $"{op.BytesReceived / 1024.0 / 1024.0:0.0} MB";
                }
            }

            op.BytesReceivedChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateProgress);
            op.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => OnDownloadStateChanged(op, row));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Error redirecting download: {ex.Message}");
        }
    }

    private void OnDownloadStateChanged(CoreWebView2DownloadOperation op, DownloadRow row)
    {
        switch (op.State)
        {
            case CoreWebView2DownloadState.Completed:
                row.StatusHost.Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 84, 178, 96))
                };
                row.ProgressText.Text = "Downloaded";
                Trace.WriteLine($"[WebImportOverlay] ✓ Download completed: {row.FileName}");
                break;

            case CoreWebView2DownloadState.Interrupted:
                row.StatusHost.Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 87, 76))
                };
                row.ProgressText.Text = "Failed";
                Trace.WriteLine($"[WebImportOverlay] ✗ Download interrupted: {row.FileName}");
                break;
        }
    }

    // =========================================================================
    // Downloads shelf
    // =========================================================================

    private readonly struct DownloadRow
    {
        public required string FileName { get; init; }
        public required ContentControl StatusHost { get; init; }
        public required TextBlock ProgressText { get; init; }
    }

    private DownloadRow AddDownloadRow(string fileName)
    {
        DownloadsShelf.Visibility = Visibility.Visible;

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statusHost = new ContentControl
        {
            Width = 18,
            Height = 18,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new ProgressRing { IsActive = true, Width = 14, Height = 14 }
        };
        Grid.SetColumn(statusHost, 0);
        row.Children.Add(statusHost);

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = false
        });

        var progressText = new TextBlock
        {
            Text = "Downloading...",
            FontSize = 11,
            Opacity = 0.65,
            IsTextScaleFactorEnabled = false
        };
        textPanel.Children.Add(progressText);

        Grid.SetColumn(textPanel, 1);
        row.Children.Add(textPanel);

        DownloadsListPanel.Children.Add(row);

        return new DownloadRow { FileName = fileName, StatusHost = statusHost, ProgressText = progressText };
    }

    // =========================================================================
    // Navigation chrome
    // =========================================================================

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady && ImportWebView.CoreWebView2.CanGoBack)
            ImportWebView.CoreWebView2.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady && ImportWebView.CoreWebView2.CanGoForward)
            ImportWebView.CoreWebView2.GoForward();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady)
            ImportWebView.CoreWebView2.Reload();
    }

    private void UpdateNavButtons()
    {
        if (!_webViewReady) return;
        BackButton.IsEnabled = ImportWebView.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = ImportWebView.CoreWebView2.CanGoForward;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // Staging folder and the close-time import pass
    // =========================================================================

    private void PrepareStagingFolder(string stagingTag)
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            _stagingFolder = Path.Combine(localFolder, "WebImportStaging", stagingTag);

            // A folder still here on entry can only be debris from a crash or a force-close
            // between a prior session's downloads and its close-time sweep - never something
            // live, since a normal Close() always empties this out itself.
            if (Directory.Exists(_stagingFolder))
                Directory.Delete(_stagingFolder, true);

            Directory.CreateDirectory(_stagingFolder);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Couldn't prepare staging folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs once the fade-out finishes. Hands every staged file matching the watched extensions
    /// to the caller in one batch, then always cleans the staging folder regardless of whether
    /// the callback succeeded, threw, or there was nothing to hand it at all - every file that
    /// went through the downloads shelf is gone from disk by the time this returns, imported or
    /// not, so nothing lingers in LocalState across sessions.
    /// </summary>
    private async Task FinalizeImportsAsync()
    {
        var callback = _onFilesReady;
        var stagingFolder = _stagingFolder;
        _onFilesReady = null;

        try
        {
            if (callback == null || string.IsNullOrEmpty(stagingFolder) || !Directory.Exists(stagingFolder))
                return;

            var matches = Directory.GetFiles(stagingFolder)
                .Where(f => _watchedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Trace.WriteLine("[WebImportOverlay] Closed with nothing matching to import");
                return;
            }

            Trace.WriteLine($"[WebImportOverlay] Handing {matches.Count} downloaded file(s) to the importer");
            await callback(matches);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Error finalizing downloads: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, true);
                    Trace.WriteLine("[WebImportOverlay] Staging folder cleared");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WebImportOverlay] Couldn't clean staging folder: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // Fade animation - identical mechanics to BugTrackerOverlay.AnimateOpacity
    // =========================================================================

    private void AnimateOpacity(double to, Action? onCompleted)
    {
        if (AnimationsSuspended)
        {
            _fadeStoryboard?.Stop();
            Opacity = to;
            onCompleted?.Invoke();
            return;
        }

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
