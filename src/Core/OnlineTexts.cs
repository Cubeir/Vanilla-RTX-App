using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App;
using System.IO;
using Windows.Storage;

// =====================================================================================================================
// PsaKind — Controls dismiss behaviour and button visibility for a PsaItem.
//
//   Pinned    (#  body)  — No dismiss button. Always shown. Cannot be hidden by the user.
//   Timed     (## body)  — Dismiss button with "Dismiss for now" tooltip.
//                          Reappears after CooldownMinutes (default 5, override with [cd:N] in the ## title).
//   Permanent (### body) — Dismiss button with "Dismiss" tooltip.
//                          Dismissed once → gone forever (until text changes in the .md).
// =====================================================================================================================

public enum PsaKind
{
    Pinned,
    Timed,
    Permanent
}


// =====================================================================================================================
// PsaItem — A single announcement entry.
//
//   CooldownMinutes — only meaningful for Timed items. Parsed from [cd:N] in the ## title.
//                     Defaults to 5 if the tag is absent or unparseable.
// =====================================================================================================================

public record PsaItem(string Text, PsaKind Kind, int CooldownMinutes = 5);


// =====================================================================================================================
// OnlineTextsContent — Static store populated by OnlineTexts.
//
// HOW TO ADD A NEW VARIABLE:
//   1. Add a public static PsaItem[]? property here.
//   2. Add a matching # Section to IN-APP-ANNOUNCEMENTS.md.
//      The section name must match the property name exactly (trimmed, case-insensitive).
//
// null always means "nothing to show" — absent section, empty content, or fetch failed.
//
// READING VALUES:
//   Always call OnlineTexts.GetFiltered(OnlineTextsContent.YourProperty).
//   GetFiltered strips dismissed entries automatically according to each item's PsaKind.
// =====================================================================================================================

public static class OnlineTextsContent
{
    public static PsaItem[]? Credits { get; set; }
    public static PsaItem[]? PSA { get; set; }
    public static PsaItem[]? PackUpdateAnnouncements { get; set; }
    public static PsaItem[]? BetterRTXAnnouncements { get; set; }
    public static PsaItem[]? LutManagerAnnouncements { get; set; }
    public static PsaItem[]? DLSSAnnouncements { get; set; }
    public static PsaItem[]? ResourcePackSelectionAnnouncements { get; set; }
}


// =====================================================================================================================
// OnlineTexts — Online text retrieval, caching, and per-user dismiss tracking.
//
// ── .md FILE FORMAT ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   # PropertyName            ← Section header. Must match a property in OnlineTextsContent (case-insensitive).
//
//   Pinned text here.         ← PINNED: no dismiss button, always shown.
//                                Any text before the first ## or ### in the section.
//
//   ## Title [cd:N]           ← Opens a TIMED block. Title and [cd:N] tag are both ignored in the body.
//   Timed text here.          ← TIMED: "Dismiss for now", reappears after N minutes (default 5).
//
//   ## Title                  ← No [cd:N] tag — uses the default 5-minute cooldown.
//   More timed text.
//
//   ### Title                 ← Opens a PERMANENT block. Title is ignored.
//   Permanent text here.      ← PERMANENT: "Dismiss", gone forever once dismissed.
//
// ── [cd:N] TAG ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Only meaningful on ## lines. N is the cooldown in minutes (positive integer).
//   Can appear anywhere in the title — everything outside the tag is ignored.
//   If absent, malformed, zero, or negative: defaults to 5 minutes.
//
//   Examples:
//     ## Some title [cd:30]        → 30-minute cooldown
//     ## [cd:1] quick notice       → 1-minute cooldown
//     ## no tag here               → 5-minute cooldown (default)
//     ## [cd:abc]                  → 5-minute cooldown (parse failure → default)
//
// ── RULES ───────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   • Single # only = section header.
//   • ### or deeper = Permanent item separator (checked before ## to avoid misclassification).
//   • ## (not ###)  = Timed item separator. [cd:N] tag controls cooldown duration.
//   • Text before the first separator in a section = Pinned.
//   • Empty / whitespace-only blocks are discarded.
//   • Properties whose # header is absent from the file are set to null.
//
// ── DISMISS SYSTEM ──────────────────────────────────────────────────────────────────────────────────────────────────
//
//   OnlineTexts.Dismiss(text)                    — Permanently blacklists text.
//   OnlineTexts.DismissTimed(text, cooldownMins) — Records expiry at UtcNow + cooldownMins.
//   OnlineTexts.GetFiltered(…)                   — Returns only items that should currently be shown.
//
//   PsaCard calls the correct method automatically — you never call these directly.
//
// ── COOLDOWN ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   110 minutes between remote fetches (0 in DEBUG).
//   Stale cache applied immediately on startup; fresh fetch runs in the background
//   with up to 2 retries spaced 5 seconds apart.
// =====================================================================================================================

public static class OnlineTexts
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const string URL =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/IN-APP-ANNOUNCEMENTS.md";

    private const string KEY_TIMESTAMP = "OnlineTexts_Timestamp";
    private const string KEY_DISMISSED = "OnlineTexts_Dismissed";
    private const string KEY_TIMED_DISMISSED = "OnlineTexts_TimedDismissed";

    private const int DEFAULT_TIMED_COOLDOWN_MINUTES = 5;

#if DEBUG
    private static readonly TimeSpan COOLDOWN = TimeSpan.Zero;
#else
    private static readonly TimeSpan COOLDOWN    = TimeSpan.FromMinutes(110);
#endif
    private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);
    private const int MAX_RETRIES = 2;

    // ── Reflection map: lowercase property name → PropertyInfo ────────────────

    private static readonly Dictionary<string, PropertyInfo> _propMap =
        typeof(OnlineTextsContent)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(p => p.Name.ToLowerInvariant(), p => p, StringComparer.Ordinal);

    // ── Concurrency ───────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static volatile bool _fetching;

    // ── Permanent dismissed set ───────────────────────────────────────────────

    private static HashSet<string>? _dismissed;
    private static readonly object _dismissLock = new();

    // ── Timed dismissed dictionary: hash → expiry UTC ─────────────────────────

    private static Dictionary<string, DateTime>? _timedDismissed;
    private static readonly object _timedDismissLock = new();

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Fire-and-forget startup call. Applies any cached content immediately,
    /// then re-fetches in the background if the cooldown has expired.
    /// Safe to call from any thread. Never throws.
    /// </summary>
    public static void TriggerUpdate() => _ = TriggerUpdateAsync();

    /// <summary>
    /// Awaitable version of <see cref="TriggerUpdate"/>.
    /// Returns true if a fresh network fetch succeeded, false otherwise.
    /// </summary>
    public static async Task<bool> TriggerUpdateAsync()
    {
        Trace.WriteLine("[OnlineTexts] TriggerUpdateAsync");
        TryApplyCache();

        if (!IsCooldownExpired())
        {
            Trace.WriteLine("[OnlineTexts] Cooldown active — using cache");
            return false;
        }

        if (_fetching || !await _lock.WaitAsync(0))
        {
            Trace.WriteLine("[OnlineTexts] Fetch already in progress — skipping");
            return false;
        }

        _fetching = true;
        try
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    Trace.WriteLine($"[OnlineTexts] Retry {attempt}, waiting {RETRY_DELAY.TotalSeconds}s…");
                    await Task.Delay(RETRY_DELAY);
                }

                Trace.WriteLine($"[OnlineTexts] Fetch attempt {attempt + 1}/{MAX_RETRIES + 1}");
                var raw = await FetchAsync();

                if (raw is null)
                {
                    Trace.WriteLine($"[OnlineTexts] Attempt {attempt + 1} returned null");
                    continue;
                }

                ParseAndApply(raw);
                CacheContent(raw);
                Trace.WriteLine("[OnlineTexts] Fetch and parse succeeded");
                return true;
            }

            Trace.WriteLine("[OnlineTexts] All attempts failed — staying on cache");
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] Unexpected error: {ex}");
            return false;
        }
        finally
        {
            _fetching = false;
            _lock.Release();
        }
    }

    /// <summary>Expires the cache timestamp and immediately triggers a fresh fetch.</summary>
    public static void ForceRefresh()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[KEY_TIMESTAMP] =
                DateTime.UtcNow.AddDays(-1).ToString("O");
        }
        catch { }

        TriggerUpdate();
    }

    /// <summary>
    /// Filters <paramref name="source"/> down to items that should currently be shown.
    /// Pinned   — always passes through.
    /// Timed    — passes through once the dismiss cooldown has elapsed.
    /// Permanent — passes through only if never permanently dismissed.
    /// Always use this instead of reading OnlineTextsContent properties directly.
    /// </summary>
    public static PsaItem[]? GetFiltered(PsaItem[]? source)
    {
        if (source is null || source.Length == 0) return null;

        var dismissed = GetDismissed();
        var timedDismissed = GetTimedDismissed();
        var now = DateTime.UtcNow;

        var kept = source
            .Where(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Text)) return false;
                var hash = DismissHash(item.Text);
                return item.Kind switch
                {
                    PsaKind.Pinned => true,
                    PsaKind.Timed => !timedDismissed.TryGetValue(hash, out var expiry) || now >= expiry,
                    PsaKind.Permanent => !dismissed.Contains(hash),
                    _ => true
                };
            })
            .ToArray();

        return kept.Length > 0 ? kept : null;
    }

    /// <summary>
    /// Permanently blacklists <paramref name="text"/>.
    /// Only call for Permanent items — PsaCard handles this automatically.
    /// </summary>
    public static void Dismiss(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_dismissLock)
            {
                var d = GetDismissed();
                if (d.Add(DismissHash(text)))
                {
                    SaveDismissed(d);
                    Trace.WriteLine($"[OnlineTexts] Dismissed: \"{text.Substring(0, Math.Min(60, text.Length))}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] Dismiss failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Hides <paramref name="text"/> for <paramref name="cooldownMinutes"/> minutes.
    /// After the window elapses it reappears in <see cref="GetFiltered"/> results.
    /// Only call for Timed items — PsaCard handles this automatically.
    /// </summary>
    public static void DismissTimed(string text, int cooldownMinutes = DEFAULT_TIMED_COOLDOWN_MINUTES)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var safeMinutes = cooldownMinutes > 0 ? cooldownMinutes : DEFAULT_TIMED_COOLDOWN_MINUTES;
        try
        {
            lock (_timedDismissLock)
            {
                var d = GetTimedDismissed();
                var hash = DismissHash(text);
                d[hash] = DateTime.UtcNow.AddMinutes(safeMinutes);
                SaveTimedDismissed(d);
                Trace.WriteLine($"[OnlineTexts] Timed dismiss for {safeMinutes}m until {d[hash]:HH:mm:ss}: " +
                    $"\"{text.Substring(0, Math.Min(60, text.Length))}\"");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] DismissTimed failed: {ex.Message}");
        }
    }

    // ── Hash helper ───────────────────────────────────────────────────────────

    private static string DismissHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    // =========================================================================
    // Permanent dismiss storage
    // =========================================================================

    private static HashSet<string> GetDismissed()
    {
        if (_dismissed is not null) return _dismissed;
        lock (_dismissLock)
        {
            _dismissed ??= LoadDismissed();
        }
        return _dismissed;
    }

    private static HashSet<string> LoadDismissed()
    {
        try
        {
            var raw = ApplicationData.Current.LocalSettings.Values[KEY_DISMISSED] as string;
            if (!string.IsNullOrEmpty(raw))
            {
                var arr = JsonSerializer.Deserialize<string[]>(raw);
                if (arr is not null)
                    return new HashSet<string>(arr, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] LoadDismissed failed: {ex.Message}");
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static void SaveDismissed(HashSet<string> dismissed)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[KEY_DISMISSED] =
                JsonSerializer.Serialize(dismissed.ToArray());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] SaveDismissed failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Timed dismiss storage
    // =========================================================================

    private static Dictionary<string, DateTime> GetTimedDismissed()
    {
        if (_timedDismissed is not null) return _timedDismissed;
        lock (_timedDismissLock)
        {
            _timedDismissed ??= LoadTimedDismissed();
        }
        return _timedDismissed;
    }

    private static Dictionary<string, DateTime> LoadTimedDismissed()
    {
        try
        {
            var raw = ApplicationData.Current.LocalSettings.Values[KEY_TIMED_DISMISSED] as string;
            if (!string.IsNullOrEmpty(raw))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (dict is not null)
                {
                    var now = DateTime.UtcNow;
                    var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    foreach (var (k, v) in dict)
                        if (DateTime.TryParse(v, out var dt) && dt > now) // prune expired on load
                            result[k] = dt;
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] LoadTimedDismissed failed: {ex.Message}");
        }
        return new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    private static void SaveTimedDismissed(Dictionary<string, DateTime> dismissed)
    {
        try
        {
            var toStore = dismissed.ToDictionary(k => k.Key, v => v.Value.ToString("O"));
            ApplicationData.Current.LocalSettings.Values[KEY_TIMED_DISMISSED] =
                JsonSerializer.Serialize(toStore);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] SaveTimedDismissed failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Cache
    // =========================================================================

    private static string GetCacheFilePath() =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "OnlineTexts_Cache.md");

    private static void TryApplyCache()
    {
        try
        {
            var path = GetCacheFilePath();
            if (File.Exists(path))
            {
                var cached = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    Trace.WriteLine($"[OnlineTexts] Applying cache ({cached.Length} chars)");
                    ParseAndApply(cached);
                    return;
                }
            }
            Trace.WriteLine("[OnlineTexts] No cache found");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] TryApplyCache failed: {ex.Message}");
        }
    }

    private static void CacheContent(string raw)
    {
        try
        {
            File.WriteAllText(GetCacheFilePath(), raw);
            ApplicationData.Current.LocalSettings.Values[KEY_TIMESTAMP] =
                DateTime.UtcNow.ToString("O");
            Trace.WriteLine($"[OnlineTexts] Cached {raw.Length} chars");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] CacheContent failed: {ex.Message}");
        }
    }

    private static bool IsCooldownExpired()
    {
        try
        {
            var val = ApplicationData.Current.LocalSettings.Values[KEY_TIMESTAMP] as string;
            if (val is null)
            {
                Trace.WriteLine("[OnlineTexts] No timestamp — treating as expired");
                return true;
            }

            if (DateTime.TryParse(val, out var last))
            {
                var age = DateTime.UtcNow - last;
                if (age < TimeSpan.Zero)
                {
                    Trace.WriteLine("[OnlineTexts] Timestamp is in the future — resetting");
                    try { ApplicationData.Current.LocalSettings.Values.Remove(KEY_TIMESTAMP); } catch { }
                    return true;
                }
                var expired = age >= COOLDOWN;
                Trace.WriteLine($"[OnlineTexts] Cache age {age.TotalMinutes:F1} min, expired: {expired}");
                return expired;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] IsCooldownExpired failed: {ex.Message}");
        }
        return true;
    }

    // =========================================================================
    // Network
    // =========================================================================

    private static async Task<string?> FetchAsync(int timeoutSeconds = 8)
    {
        try
        {
            Trace.WriteLine($"[OnlineTexts] Fetching {URL}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestHeaders.Add("User-Agent",
                $"vanilla_rtx_app_updater/{TunerVariables.appVersion} " +
                "(https://github.com/Cubeir/Vanilla-RTX-App)");

            var response = await client.GetAsync(URL);
            Trace.WriteLine($"[OnlineTexts] HTTP {(int)response.StatusCode} {response.StatusCode}");
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync()
                : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] FetchAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // Parser
    // =========================================================================

    /// <summary>
    /// Nullifies all OnlineTextsContent properties, parses the raw .md,
    /// and writes results via reflection. Missing sections stay null.
    /// </summary>
    private static void ParseAndApply(string raw)
    {
        try
        {
            foreach (var prop in _propMap.Values)
                prop.SetValue(null, null);

            var sections = Parse(raw);

            foreach (var (key, blocks) in sections)
            {
                if (!_propMap.TryGetValue(key, out var prop))
                {
                    Trace.WriteLine($"[OnlineTexts] No property for section '{key}' — ignoring");
                    continue;
                }

                var items = blocks
                    .Where(b => !string.IsNullOrWhiteSpace(b.text))
                    .Select(b => new PsaItem(b.text.Trim(), b.kind, b.cooldownMinutes))
                    .ToArray();

                prop.SetValue(null, items.Length > 0 ? items : null);
                Trace.WriteLine($"[OnlineTexts] '{key}' → {items.Length} item(s) " +
                    $"(pinned={items.Count(i => i.Kind == PsaKind.Pinned)}, " +
                    $"timed={items.Count(i => i.Kind == PsaKind.Timed)}, " +
                    $"permanent={items.Count(i => i.Kind == PsaKind.Permanent)})");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] ParseAndApply exception: {ex}");
        }
    }

    /// <summary>
    /// Core parser. Returns: lowercase-section-name → ordered list of (text, kind, cooldownMinutes) blocks.
    ///
    /// Single #    = opens a new section.
    /// ###         = Permanent item separator (checked before ## to avoid misclassification).
    /// ## (not ###)= Timed item separator. [cd:N] in the title sets cooldown minutes.
    /// No separator= entire section body is one Pinned item.
    /// Separator titles (beyond the [cd:N] tag) are ignored.
    /// </summary>
    private static Dictionary<string, List<(string text, PsaKind kind, int cooldownMinutes)>> Parse(string raw)
    {
        var result = new Dictionary<string, List<(string, PsaKind, int)>>(StringComparer.Ordinal);
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        List<(string text, PsaKind kind, int cooldownMinutes)>? currentBlocks = null;
        var block = new StringBuilder();
        var currentKind = PsaKind.Pinned;
        var currentCooldown = DEFAULT_TIMED_COOLDOWN_MINUTES;

        void CommitBlock()
        {
            if (currentBlocks is null) return;
            currentBlocks.Add((block.ToString(), currentKind, currentCooldown));
            block.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // ── Single # header ───────────────────────────────────────────────
            if (line.StartsWith("# ") && !line.StartsWith("## ") && line.Length > 2)
            {
                CommitBlock();
                var name = line.Substring(2).Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(name)) continue;

                currentBlocks = new List<(string, PsaKind, int)>();
                result[name] = currentBlocks;
                block.Clear();
                currentKind = PsaKind.Pinned;
                currentCooldown = DEFAULT_TIMED_COOLDOWN_MINUTES;
                Trace.WriteLine($"[OnlineTexts] Section: '{name}'");
            }
            // ── ### or deeper → Permanent ─────────────────────────────────────
            // Must be checked BEFORE ## to avoid treating ### as a ## match.
            else if (currentBlocks is not null && line.StartsWith("###"))
            {
                CommitBlock();
                currentKind = PsaKind.Permanent;
                currentCooldown = DEFAULT_TIMED_COOLDOWN_MINUTES; // irrelevant for Permanent
                Trace.WriteLine($"[OnlineTexts] ### in '{result.Keys.Last()}' → Permanent");
            }
            // ── ## (exactly, not ###) → Timed ─────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("##"))
            {
                CommitBlock();
                currentKind = PsaKind.Timed;
                currentCooldown = ParseCooldownTag(line);
                Trace.WriteLine($"[OnlineTexts] ## in '{result.Keys.Last()}' → Timed, cooldown={currentCooldown}m");
            }
            // ── Body line ─────────────────────────────────────────────────────
            else if (currentBlocks is not null)
            {
                block.Append(line).Append('\n');
            }
        }

        CommitBlock();
        return result;
    }

    /// <summary>
    /// Extracts [cd:N] from a separator line title.
    /// N must be a positive integer. Returns the default if absent, malformed, or invalid.
    /// The tag can appear anywhere in the title — surrounding text is ignored.
    /// </summary>
    private static int ParseCooldownTag(string line)
    {
        var start = line.IndexOf("[cd:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return DEFAULT_TIMED_COOLDOWN_MINUTES;

        start += 4; // skip past "[cd:"
        var end = line.IndexOf(']', start);
        if (end < 0) return DEFAULT_TIMED_COOLDOWN_MINUTES;

        var numStr = line.Substring(start, end - start).Trim();
        return int.TryParse(numStr, out var n) && n > 0
            ? n
            : DEFAULT_TIMED_COOLDOWN_MINUTES;
    }
}
