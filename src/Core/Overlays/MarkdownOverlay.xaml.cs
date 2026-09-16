using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Modules;
using Windows.System;
using Windows.UI.Core;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// A full-window overlay that fetches a GitHub markdown file's raw content and renders it as
/// plain WinUI elements - the read-only sibling of <see cref="WebImportOverlay"/>. Shares that
/// control's <see cref="OverlayHeaderBar"/> chrome (icon, hyperlinked title, Reload, Close) but
/// never spins up a WebView2 and never hands anything back to the caller: there is nothing to
/// import from a document, so <c>Show</c> takes just a URL, a title and a glyph.
///
/// <para><b>Why this exists instead of just pointing WebImportOverlay at the GitHub page.</b>
/// The Help and Bug-tracker buttons only ever showed a single README - paying for a full
/// Chromium instance (and GitHub's own page chrome, ads-adjacent trackers, and layout shift) to
/// display text that fits in a <see cref="Markdig.MarkdownPipeline"/> parse and a handful of
/// <see cref="TextBlock"/>s was the wrong tool for a job this small. Rendering the markdown
/// directly is also what makes the in-memory cache below possible - there's a single string to
/// cache, not a browser profile.</para>
///
/// <para><b>Caching is deliberately just a static <see cref="Dictionary{TKey,TValue}"/>, keyed
/// by the page URL the caller passed to <see cref="Show"/>.</b> It lives for exactly the
/// process's lifetime - repeatedly opening Help or the bug tracker in one session never
/// re-fetches, but a fresh launch always does. That matches what these two buttons need: the
/// content changes rarely enough that a session-long cache is free, but it isn't worth the
/// complexity of <see cref="AssetUpdater"/>-style cooldowns and on-disk persistence for two
/// small text files nobody needs available offline.</para>
///
/// <para><b>Reload is rate-limited, deliberately, and this is not the same cache as above.</b>
/// A WebView2 "reload" is a browser doing what browsers already do constantly; this is a direct,
/// unconditional request to a git host's raw-content server, and a caller mashing the button has
/// no throttle standing between them and that server. <see cref="_reloadCooldownUntil"/> is a
/// second static dictionary, keyed by page URL and storing *next allowed reload time* rather
/// than *last reload time* (the pattern <c>AssetUpdater</c> already uses) - that's what makes the
/// cooldown survive closing and reopening the overlay: it isn't instance state at all.</para>
///
/// <para><b>Clicking Help/Bug again is a toggle, and switching documents closes the one that was
/// open first.</b> <see cref="Show"/> re-clicked with the same URL while already open just closes
/// it (see <see cref="Close"/>). A *different* URL while one is open plays the same close
/// animation and only then opens the new document - not a silent content swap under a panel
/// that's still visibly there, and not two overlays stacking.</para>
///
/// <para><b>Rendering never throws</b> - see <see cref="MarkdownRenderer"/>'s own doc. Every
/// other failure mode (no internet, a 404 from a renamed branch, a malformed URL) is caught here
/// and shown as the same friendly <c>ErrorState</c> panel <see cref="WebImportOverlay"/> uses,
/// with a Try Again button that re-fetches without ever leaving the user looking at nothing.
/// </para>
/// </summary>
public sealed partial class MarkdownOverlay : UserControl
{
    /// <summary>Raw markdown text, keyed by the page URL passed to <see cref="Show"/>. Process-lifetime only - see the class doc.</summary>
    private static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>Next allowed Reload time per page URL - see the class doc. Process-lifetime, independent of any single overlay instance's open/closed state.</summary>
    private static readonly Dictionary<string, DateTime> _reloadCooldownUntil = new(StringComparer.Ordinal);
    private const int ReloadCooldownSeconds = 30;

    private bool _isOpen;
    private string _pageUrl = string.Empty;
    private string _rawUrl = string.Empty;
    private string _blobBaseUrl = string.Empty;
    private string _rawBaseUrl = string.Empty;
    private string? _pendingFragment;
    private IReadOnlyDictionary<string, FrameworkElement>? _anchors;
    private IReadOnlyList<MarkdownSearchEntry> _searchEntries = Array.Empty<MarkdownSearchEntry>();
    private readonly List<MarkdownSearchEntry> _searchMatches = new();
    private int _searchMatchIndex = -1;
    private CancellationTokenSource? _fetchCts;
    private DispatcherTimer? _cooldownTimer;
    private Storyboard? _fadeStoryboard;

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;
    /// <summary>
    /// Fade duration for the whole overlay, bounded at both ends: below ~100ms a 60Hz display
    /// has too few frames left for the ease to read as anything but a snap, and much above it
    /// the overlay feels slow to open on a high-refresh display. 100ms is 6 frames at 60Hz
    /// and 14 at 144Hz.
    /// </summary>
    private const double FADE_MS = 100;

    public MarkdownOverlay()
    {
        InitializeComponent();

        Header.SetNavButtonsVisible(false);

        // Opens whatever we actually fetched, not the human-readable blob page - the title link
        // is supposed to reflect the real request this overlay made, and that request is always
        // to raw.githubusercontent.com. See ResolveGithubUrls.
        Header.TitleClick += (_, _) =>
        {
            if (Uri.TryCreate(_rawUrl, UriKind.Absolute, out var uri))
                _ = Launcher.LaunchUriAsync(uri);
        };

        Header.ReloadClick += (_, _) =>
        {
            // The button should already be disabled for the duration, but a click that slips in
            // right on the boundary (or one queued before SetReloadCooldown took effect) must
            // still be refused - the cooldown is the invariant, not the button's IsEnabled.
            if (_reloadCooldownUntil.TryGetValue(_pageUrl, out var until) && until > DateTime.UtcNow)
                return;

            _reloadCooldownUntil[_pageUrl] = DateTime.UtcNow.AddSeconds(ReloadCooldownSeconds);
            RefreshReloadCooldownUI();
            _ = LoadAsync(bypassCache: true);
        };

        Header.CloseClick += (_, _) => Close();
    }

    /// <summary>
    /// Opens the overlay and shows <paramref name="url"/> - a normal <c>github.com/.../blob/...</c>
    /// page URL (the same kind you'd paste into a browser address bar), not a raw content URL.
    /// The raw file, and the base URLs relative links/images resolve against, are derived from it -
    /// see <see cref="ResolveGithubUrls"/>. A URL fragment (<c>#some-heading</c>) is honored:
    /// once rendered, the overlay scrolls to the matching heading the same way GitHub itself would.
    ///
    /// <para>Calling this again with the URL currently showing closes the overlay (a toggle).
    /// Calling it with a different URL while one is open closes the current document first, then
    /// opens the new one - see the class doc.</para>
    /// </summary>
    public void Show(string url, string title, string glyph)
    {
        if (_isOpen && string.Equals(_pageUrl, url, StringComparison.Ordinal))
        {
            Close();
            return;
        }

        if (_isOpen)
        {
            CloseInternal(() => OpenAndLoad(url, title, glyph));
            return;
        }

        OpenAndLoad(url, title, glyph);
    }

    private void OpenAndLoad(string url, string title, string glyph)
    {
        _isOpen = true;

        Header.SetTitleText(title);
        Header.SetIcon(glyph);
        Header.SetCloseButton("", "Return", "Close this page");

        _pageUrl = url;
        _anchors = null;
        _searchEntries = Array.Empty<MarkdownSearchEntry>();

        try
        {
            (_rawUrl, _blobBaseUrl, _rawBaseUrl, _pendingFragment) = ResolveGithubUrls(url);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownOverlay] Couldn't parse URL '{url}': {ex.Message}");
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            AnimateOpacity(1.0, null);
            ShowError("This page's address couldn't be understood.");
            return;
        }

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);
        UpdateLayout();

        RefreshReloadCooldownUI();
        _ = LoadAsync(bypassCache: false);
    }

    private void Close() => CloseInternal(null);

    /// <summary>The guts of closing - optionally continues into <paramref name="onClosed"/> once the fade-out finishes, which is how <see cref="Show"/> chains "close this document, then open the next one" through the same real animation a Close click gets.</summary>
    private void CloseInternal(Action? onClosed)
    {
        if (!_isOpen) return;
        _isOpen = false;
        _fetchCts?.Cancel();
        _cooldownTimer?.Stop();
        CloseSearchBar();

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () =>
        {
            Visibility = Visibility.Collapsed;
            // Release the rendered visual tree (and everything it's holding - Image sources,
            // Hyperlink closures) rather than letting a large README sit alive in memory
            // between visits. The cached markdown *string* is untouched; re-opening just re-renders it.
            MarkdownHost.Content = null;
            onClosed?.Invoke();
        });
    }

    /// <summary>The host window's own Closed handler should call this - see WebImportOverlay.CloseIfOpen for why. A markdown fetch has no staging folder to clean up, only an in-flight request and a countdown timer worth stopping.</summary>
    public void CloseIfOpen()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _fetchCts?.Cancel();
        _cooldownTimer?.Stop();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(bypassCache: false);

    // =========================================================================
    // Loading and rendering
    // =========================================================================

    private async Task LoadAsync(bool bypassCache)
    {
        ShowLoading("Loading...");

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _fetchCts = cts;

        try
        {
            string? markdown = bypassCache ? null : GetCached(_pageUrl);

            if (markdown is null)
            {
                markdown = await FetchRawMarkdownAsync(_rawUrl, cts.Token);
                if (markdown is null)
                {
                    if (!cts.Token.IsCancellationRequested)
                        ShowError("Couldn't load this page. Check your internet connection and try again.");
                    return;
                }
                SetCached(_pageUrl, markdown);
            }

            if (!_isOpen || cts.Token.IsCancellationRequested) return;
            RenderMarkdown(markdown);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Show()/Reload/Retry, or the overlay closed mid-fetch.
        }
    }

    private async Task<string?> FetchRawMarkdownAsync(string rawUrl, CancellationToken token)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await Helpers.SharedHttpClient.GetAsync(rawUrl, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[MarkdownOverlay] HTTP {(int)response.StatusCode} fetching {rawUrl}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Trace.WriteLine($"[MarkdownOverlay] Timed out fetching {rawUrl}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Trace.WriteLine($"[MarkdownOverlay] Network error fetching {rawUrl}: {ex.Message}");
            return null;
        }
    }

    private void RenderMarkdown(string markdown)
    {
        try
        {
            var result = MarkdownRenderer.Render(markdown, _blobBaseUrl, _rawBaseUrl, OnLinkActivated);
            _anchors = result.Anchors;
            _searchEntries = result.SearchEntries;
            MarkdownHost.Content = result.Content;
            ShowContent();

            if (!string.IsNullOrEmpty(_pendingFragment))
            {
                var slug = _pendingFragment;
                _pendingFragment = null;
                // The content isn't laid out yet on this same tick - give it one dispatcher
                // pass before asking a heading to scroll itself into view.
                DispatcherQueue.TryEnqueue(() => ScrollToAnchor(slug));
            }
        }
        catch (Exception ex)
        {
            // MarkdownRenderer.Render is documented to never throw, but this is a full-window
            // overlay with nothing else to fall back to - a defensive backstop costs nothing.
            Trace.WriteLine($"[MarkdownOverlay] Render failed: {ex}");
            ShowError("This page couldn't be displayed.");
        }
    }

    private void OnLinkActivated(string target)
    {
        if (target.StartsWith('#'))
        {
            ScrollToAnchor(target.TrimStart('#'));
            return;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }

    private void ScrollToAnchor(string slug)
    {
        if (_anchors is null || string.IsNullOrEmpty(slug)) return;

        var decoded = Uri.UnescapeDataString(slug);
        if (_anchors.TryGetValue(decoded, out var element) || _anchors.TryGetValue(slug, out element))
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
        else
            Trace.WriteLine($"[MarkdownOverlay] No heading matched anchor '#{slug}' - see MarkdownRenderer's anchor-slug caveat.");
    }

    // =========================================================================
    // Reload cooldown
    // =========================================================================

    /// <summary>
    /// Paints the header's Reload button for the current cooldown state and, while one is
    /// active, keeps a one-second timer running to count it down - called from
    /// <see cref="OpenAndLoad"/> (so reopening mid-cooldown shows it immediately) and by the
    /// timer itself each tick.
    /// </summary>
    private void RefreshReloadCooldownUI()
    {
        if (_reloadCooldownUntil.TryGetValue(_pageUrl, out var until))
        {
            var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            if (remaining > 0)
            {
                Header.SetReloadCooldown(remaining);
                if (_cooldownTimer is null)
                {
                    _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _cooldownTimer.Tick += (_, _) => RefreshReloadCooldownUI();
                    _cooldownTimer.Start();
                }
                return;
            }
            _reloadCooldownUntil.Remove(_pageUrl);
        }

        _cooldownTimer?.Stop();
        _cooldownTimer = null;
        Header.SetReloadCooldown(null);
    }

    // =========================================================================
    // Cache
    // =========================================================================

    private static string? GetCached(string key) =>
        _cache.TryGetValue(key, out var value) ? value : null;

    private static void SetCached(string key, string value) => _cache[key] = value;

    // =========================================================================
    // GitHub URL resolution
    // =========================================================================

    /// <summary>
    /// Given a normal <c>github.com/{owner}/{repo}/blob/{branch}/{path}[#fragment]</c> page URL,
    /// returns the equivalent <c>raw.githubusercontent.com</c> URL to fetch, the directory-level
    /// blob and raw base URLs relative links/images resolve against (see
    /// <see cref="MarkdownRenderer"/>), and the fragment (if any) as a plain anchor slug.
    /// <para>
    /// A URL that isn't a recognizable github.com blob link (already a raw URL, or something
    /// else entirely) is treated as the raw content itself, fetched as-is, with its own
    /// directory as both base URL - there's no separate "human view" URL to derive a different
    /// one from.
    /// </para>
    /// </summary>
    private static (string RawUrl, string BlobBaseUrl, string RawBaseUrl, string? Fragment) ResolveGithubUrls(string pageUrl)
    {
        var uri = new Uri(pageUrl);
        var fragment = string.IsNullOrEmpty(uri.Fragment) ? null : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
            {
                var owner = segments[0];
                var repo = segments[1];
                var branch = segments[3];
                var pathSegments = segments.Skip(4).ToArray();
                var path = string.Join('/', pathSegments);
                var dir = pathSegments.Length > 1 ? string.Join('/', pathSegments[..^1]) + "/" : "";

                var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
                var blobBase = $"https://github.com/{owner}/{repo}/blob/{branch}/{dir}";
                var rawBase = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{dir}";
                return (rawUrl, blobBase, rawBase, fragment);
            }
        }

        var directUrl = new UriBuilder(uri) { Fragment = "" }.Uri.ToString();
        var lastSlash = directUrl.LastIndexOf('/');
        var directoryUrl = lastSlash >= 0 ? directUrl[..(lastSlash + 1)] : directUrl;
        return (directUrl, directoryUrl, directoryUrl, fragment);
    }

    // =========================================================================
    // Search
    // =========================================================================

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e) => OpenSearchBar();

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isOpen || ContentScrollViewer.Visibility != Visibility.Visible) return;
        OpenSearchBar();
        args.Handled = true;
    }

    private void OpenSearchBar()
    {
        SearchToggleButton.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void SearchCloseButton_Click(object sender, RoutedEventArgs e) => CloseSearchBar();

    private void CloseSearchBar()
    {
        ClearCurrentHighlight();
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        SearchBox.Text = string.Empty;
        SearchMatchCountText.Text = string.Empty;
        SearchBar.Visibility = Visibility.Collapsed;
        if (ContentScrollViewer.Visibility == Visibility.Visible)
            SearchToggleButton.Visibility = Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(CoreVirtualKeyStates.Down);
                MoveToMatch(shiftDown ? -1 : 1);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                CloseSearchBar();
                e.Handled = true;
                break;
        }
    }

    private void SearchNextButton_Click(object sender, RoutedEventArgs e) => MoveToMatch(1);
    private void SearchPreviousButton_Click(object sender, RoutedEventArgs e) => MoveToMatch(-1);

    private void ApplySearchFilter(string query)
    {
        ClearCurrentHighlight();
        _searchMatches.Clear();
        _searchMatchIndex = -1;

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var entry in _searchEntries)
                if (entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    _searchMatches.Add(entry);
        }

        if (_searchMatches.Count > 0)
        {
            _searchMatchIndex = 0;
            HighlightCurrentMatch(scrollTo: true);
        }

        SearchMatchCountText.Text = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : _searchMatches.Count == 0
                ? "No results"
                : $"{_searchMatchIndex + 1}/{_searchMatches.Count}";
    }

    private void MoveToMatch(int delta)
    {
        if (_searchMatches.Count == 0) return;

        ClearCurrentHighlight();
        _searchMatchIndex = ((_searchMatchIndex + delta) % _searchMatches.Count + _searchMatches.Count) % _searchMatches.Count;
        HighlightCurrentMatch(scrollTo: true);
        SearchMatchCountText.Text = $"{_searchMatchIndex + 1}/{_searchMatches.Count}";
    }

    private void ClearCurrentHighlight()
    {
        if (_searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Count)
            MarkdownRenderer.SetSearchHighlight(_searchMatches[_searchMatchIndex].Element, highlighted: false);
    }

    private void HighlightCurrentMatch(bool scrollTo)
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count) return;

        var element = _searchMatches[_searchMatchIndex].Element;
        MarkdownRenderer.SetSearchHighlight(element, highlighted: true);
        if (scrollTo)
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
    }

    // =========================================================================
    // Loading/Error/Content state
    // =========================================================================

    private void ShowLoading(string text)
    {
        CloseSearchBar();
        LoadingText.Text = text;
        LoadingState.Visibility = Visibility.Visible;
        ErrorState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        SearchToggleButton.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorState.Visibility = Visibility.Visible;
        LoadingState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        SearchToggleButton.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Visible;
        SearchToggleButton.Visibility = Visibility.Visible;
    }

    // =========================================================================
    // Fade animation - identical to WebImportOverlay's; see that class for the reasoning.
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

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
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
