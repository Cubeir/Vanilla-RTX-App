using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.BugTracker;

public enum BugTrackerStatus
{
    /// <summary>Fresh content, a cache hit within cooldown, or a stale cache used as fallback after a failed refresh.</summary>
    Success,
    /// <summary>No cache existed and the fetch that was required to build one failed.</summary>
    NoInternet
}

public readonly record struct BugTrackerResult(BugTrackerStatus Status, string? Markdown);

/// <summary>
/// Retrieval, caching and markdown-to-HTML rendering for the community-maintained Minecraft
/// RTX bug list. <see cref="BugTrackerOverlay"/> owns chrome and display; everything that
/// touches a file, the network or LocalSettings lives here.
///
/// Cache-first. Exactly one network decision per call to <see cref="GetListAsync"/>, and the
/// cache is always what gets shown first - nothing ever makes the caller wait on a network
/// round-trip when there's something on disk to show immediately:
///
///   - No cache yet            - fetch regardless of cooldown, and this is the one case the
///                               caller actually has to wait on, since there is nothing to
///                               show in the meantime. Nothing to fall back to either, so a
///                               failure here is the only case reported as an error.
///   - Cache, cooldown active  - return it immediately. The network is never touched.
///   - Cache, cooldown expired - return the cache immediately (still no wait), and kick off a
///                               refresh in the background. <paramref name="onBackgroundUpdate"/>
///                               fires later, only if that refresh actually lands new content;
///                               a failure is silent and leaves the cache and its cooldown
///                               exactly as they were, so the next check simply tries again
///                               rather than being punished with a fresh cooldown for a fetch
///                               that never happened.
///
/// This is also the whole reason the design is cache-first at all: the backing file is a
/// single small request against a free GitHub endpoint, so every path above exists to avoid
/// hitting it more than once per cooldown window per user - opening the modal ten times in a
/// row should cost at most one real request, whether that request happens inline (no cache) or
/// quietly in the background (stale cache).
/// </summary>
public static class BugTracker
{
    private const string URL =
        "https://raw.githubusercontent.com/Cubeir/Minecraft-RTX-Bug-Tracking/refs/heads/master/README.md";

    private const string CACHE_FILE_NAME = "Bugs_Cache.md";
    private const string KEY_TIMESTAMP = "BugTracker_Timestamp";

    private static readonly TimeSpan COOLDOWN = TimeSpan.FromMinutes(30);

    private static readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Cache-first retrieval of the raw markdown. Never throws, never blocks on the network
    /// when there's a cache to show immediately.
    ///
    /// <paramref name="onFetching"/> fires synchronously, on the calling thread, only for the
    /// no-cache path, right before the one network attempt the caller actually has to wait on -
    /// it's what tells the caller when to show a loading state.
    ///
    /// <paramref name="onBackgroundUpdate"/> fires later - on whatever thread the network
    /// continuation lands on, normally the original caller's - only when a cooldown-expired
    /// background refresh actually landed new content. It never fires for a cache hit within
    /// cooldown, nor for a failed background refresh.
    /// </summary>
    public static async Task<BugTrackerResult> GetListAsync(
        Action<string>? onFetching = null,
        Action<string>? onBackgroundUpdate = null,
        CancellationToken token = default)
    {
        var cached = TryReadCache();

        if (cached is null)
        {
            Trace.WriteLine("[BugTracker] No cache - fetching regardless of cooldown");
            onFetching?.Invoke("Fetching the list of known issues...");
            var fresh = await FetchAsync(token);
            if (fresh is not null)
            {
                CacheContent(fresh);
                return new BugTrackerResult(BugTrackerStatus.Success, fresh);
            }

            Trace.WriteLine("[BugTracker] No cache and fetch failed - internet required");
            return new BugTrackerResult(BugTrackerStatus.NoInternet, null);
        }

        if (!IsCooldownExpired())
        {
            Trace.WriteLine("[BugTracker] Cooldown active - using cache, network not touched");
            return new BugTrackerResult(BugTrackerStatus.Success, cached);
        }

        Trace.WriteLine("[BugTracker] Cooldown expired - showing cache, refreshing in the background");
        _ = RefreshInBackgroundAsync(onBackgroundUpdate, token);
        return new BugTrackerResult(BugTrackerStatus.Success, cached);
    }

    /// <summary>
    /// Unconditionally fetches and caches a fresh copy, bypassing the cooldown entirely - the
    /// one deliberate exception to the cache-first rule, reserved for a user pressing an actual
    /// refresh button. The caller is expected to rate-limit how often this can be invoked on
    /// its own (a short, independent cooldown on the button itself); this method enforces none
    /// of that, only what happens once a call is actually allowed through.
    /// Falls back to the existing cache on failure, exactly like the background path - a failed
    /// manual refresh should not blank out a list that was already showing.
    /// </summary>
    public static async Task<BugTrackerResult> ForceRefreshAsync(CancellationToken token = default)
    {
        Trace.WriteLine("[BugTracker] Manual refresh - fetching regardless of cooldown");
        var fresh = await FetchAsync(token);
        if (fresh is not null)
        {
            CacheContent(fresh);
            return new BugTrackerResult(BugTrackerStatus.Success, fresh);
        }

        Trace.WriteLine("[BugTracker] Manual refresh failed - falling back to cache");
        var cached = TryReadCache();
        return cached is not null
            ? new BugTrackerResult(BugTrackerStatus.Success, cached)
            : new BugTrackerResult(BugTrackerStatus.NoInternet, null);
    }

    /// <summary>
    /// Fire-and-forget background refresh for the cooldown-expired-with-cache path. Silent on
    /// failure by design - the cache and its cooldown timestamp are left untouched, so the next
    /// check naturally retries instead of the failure being rewarded with a fresh cooldown.
    /// </summary>
    private static async Task RefreshInBackgroundAsync(Action<string>? onUpdated, CancellationToken token)
    {
        try
        {
            var updated = await FetchAsync(token);
            if (updated is null)
            {
                Trace.WriteLine("[BugTracker] Background refresh failed - keeping the existing cache");
                return;
            }

            CacheContent(updated);
            onUpdated?.Invoke(updated);
        }
        catch (OperationCanceledException)
        {
            // The view that asked for this went away before it landed - nothing to update.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTracker] RefreshInBackgroundAsync failed: {ex.Message}");
        }
    }

    /// <summary>Renders markdown as a self-contained HTML document styled to match the app's current theme.</summary>
    public static string ToHtml(string markdown, bool isDark)
    {
        var body = Markdown.ToHtml(markdown, _pipeline);
        return BuildHtmlDocument(body, isDark, GetAccentHex());
    }

    // =========================================================================
    // Cache
    // =========================================================================

    private static string GetCacheFilePath() =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, CACHE_FILE_NAME);

    private static string? TryReadCache()
    {
        try
        {
            var path = GetCacheFilePath();
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTracker] TryReadCache failed: {ex.Message}");
            return null;
        }
    }

    private static void CacheContent(string markdown)
    {
        try
        {
            File.WriteAllText(GetCacheFilePath(), markdown);
            ApplicationData.Current.LocalSettings.Values[KEY_TIMESTAMP] = DateTime.UtcNow.ToString("O");
            Trace.WriteLine($"[BugTracker] Cached {markdown.Length} chars");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTracker] CacheContent failed: {ex.Message}");
        }
    }

    private static bool IsCooldownExpired()
    {
        try
        {
            var val = ApplicationData.Current.LocalSettings.Values[KEY_TIMESTAMP] as string;
            if (val is null) return true;

            // RoundtripKind matters here - see OnlineTexts.IsCooldownExpired for the full
            // story: without it a UTC "O" stamp parses back as local time and the cooldown
            // never holds for anyone off UTC.
            if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
            {
                var age = DateTime.UtcNow - last;
                if (age < TimeSpan.Zero)
                {
                    try { ApplicationData.Current.LocalSettings.Values.Remove(KEY_TIMESTAMP); } catch { }
                    return true;
                }
                return age >= COOLDOWN;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTracker] IsCooldownExpired failed: {ex.Message}");
        }
        return true;
    }

    // =========================================================================
    // Network
    // =========================================================================

    private static async Task<string?> FetchAsync(CancellationToken externalToken, int timeoutSeconds = 10)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var response = await Helpers.SharedHttpClient.GetAsync(URL, cts.Token);
            Trace.WriteLine($"[BugTracker] HTTP {(int)response.StatusCode} {response.StatusCode}");
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(externalToken);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BugTracker] FetchAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // Rendering
    // =========================================================================

    /// <summary>Pulls the live OS accent color so links/headers tie into whatever the user's system (and the app) is themed with.</summary>
    private static string GetAccentHex()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch
        {
            return "#4CC2FF";
        }
    }

    private static string BuildHtmlDocument(string bodyHtml, bool isDark, string accentHex)
    {
        string bg = isDark ? "#242424" : "#fbfbfb";
        string fg = isDark ? "#e6e6e6" : "#1a1a1a";
        string muted = isDark ? "#a3a3a3" : "#5f6368";
        string border = isDark ? "#3a3a3a" : "#dcdcdc";
        string codeBg = isDark ? "#2f2f2f" : "#eff0f1";
        string colorScheme = isDark ? "dark" : "light";

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              :root { color-scheme: {{colorScheme}}; }
              html, body {
                margin: 0;
                background: {{bg}};
                color: {{fg}};
                font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
                font-size: 14px;
                line-height: 1.65;
              }
              body { padding: 22px 30px 44px; }
              a { color: {{accentHex}}; text-decoration: none; }
              a:hover { text-decoration: underline; }
              h1, h2, h3, h4, h5, h6 { font-weight: 600; line-height: 1.3; margin: 1.3em 0 0.6em; }
              h1:first-child, h2:first-child, h3:first-child { margin-top: 0; }
              h1 { font-size: 1.9em; border-bottom: 1px solid {{border}}; padding-bottom: 0.3em; }
              h2 { font-size: 1.5em; border-bottom: 1px solid {{border}}; padding-bottom: 0.3em; }
              h3 { font-size: 1.22em; }
              p, ul, ol, table, blockquote, pre { margin: 0.65em 0; }
              ul, ol { padding-left: 1.7em; }
              li + li { margin-top: 0.25em; }
              li > p { margin: 0.3em 0; }
              code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.9em; background: {{codeBg}}; padding: 0.15em 0.42em; border-radius: 4px; }
              pre { background: {{codeBg}}; padding: 12px 14px; border-radius: 8px; overflow-x: auto; }
              pre code { background: none; padding: 0; }
              blockquote { margin-left: 0; padding: 0.15em 1em; border-left: 4px solid {{accentHex}}77; color: {{muted}}; }
              hr { border: none; border-top: 1px solid {{border}}; margin: 1.7em 0; }
              table { border-collapse: collapse; width: 100%; }
              th, td { border: 1px solid {{border}}; padding: 6px 10px; text-align: left; }
              th { background: {{codeBg}}; }
              img { max-width: 100%; border-radius: 6px; }
              input[type=checkbox] { margin-right: 6px; }
              ::selection { background: {{accentHex}}55; }
              ::-webkit-scrollbar { width: 10px; height: 10px; }
              ::-webkit-scrollbar-thumb { background: {{border}}; border-radius: 6px; }
              ::-webkit-scrollbar-track { background: transparent; }
            </style>
            </head>
            <body>
            {{bodyHtml}}
            </body>
            </html>
            """;
    }
}
