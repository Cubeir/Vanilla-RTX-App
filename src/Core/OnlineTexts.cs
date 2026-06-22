using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App;
using System.IO;
using Windows.Storage;

namespace Vanilla_RTX_App.Core;

// =====================================================================================================================
// PsaItem — A single announcement entry.
// =====================================================================================================================

public record PsaItem(
    string Text,
    PsaKind Kind,
    string? Glyph = null,
    double? FontSize = null,
    double? Opacity = null,
    int? CooldownMinutes = null
);


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
// PsaKind — Controls dismiss behaviour and button visibility for a PsaItem.
//
//   Pinned    (#  body)  — No dismiss button. Always shown. Cannot be hidden by the user.
//   Timed     (## body)  — Dismiss button with "Dismiss for a day" tooltip.
//                          Reappears after cooldown. Not added to the permanent blacklist.
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
// OnlineTexts Usage Documentation
//
// ── .md FILE FORMAT ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   # PropertyName          ← Section header. Must match a property in OnlineTextsContent (case-insensitive).
//                             Spaces before/after the name are trimmed automatically.
//
//   Pinned text here.       ← PINNED: no dismiss button, always shown.
//                              This is any text that appears before the first ## or ### in the section.
//
//   ## (any title)          ← Opens a TIMED block. Title is ignored beyond modifier extraction.
//   Timed text here.        ← TIMED: dismiss button says "Dismiss for a day", reappears after cooldown.
//
//   ### (any title)         ← Opens a PERMANENT block. Title is ignored beyond modifier extraction.
//   Permanent text here.    ← PERMANENT: dismiss button says "Dismiss", gone forever once dismissed.
//
// ── RULES ───────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   • Single # only = section header.
//   • ## (not ###) = Timed item separator.
//   • ### or deeper = Permanent item separator.
//   • Separators are checked ### first, then ## to avoid misclassification.
//   • Text before the first separator in a section = Pinned.
//   • Empty / whitespace-only blocks are discarded.
//   • Properties whose # header is absent from the file are set to null.
//
// ── MODIFIER FIELDS ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Optional fields can be embedded anywhere in a section or item title line.
//   They are extracted and removed before the title is used for anything else,
//   so they never appear in displayed text and never break section name resolution.
//
//   Format:  [key:"value"]
//
//   Supported fields (all optional, order-independent, case-insensitive key):
//
//     [glyph:"E946"]      — Replaces the default info icon (&#xE946;) with the given Segoe Fluent
//     [size:"13"]         — Font size override for the item text (positive number).
//     [opacity:"0.75"]    — Text opacity override (0.0–1.0 inclusive).
//     [cd:"120"]          — Cooldown in minutes before a Timed item reappears after being dismissed.
//
//   Examples:
//     # PackUpdateAnnouncements [glyph:"E7BA"]
//     ## Chaos Cubes [cd:"60"] [glyph:"E946"]
//     ### Update [glyph:"EF2C"] [opacity:"0.6"] [size:"13"]
//     ##  [glyph:"F003"] [cd:"1440"]          ← title can be empty after modifiers are stripped
//
//   Failures are ignored and logged, defaults apply instead.
//
// ── DISMISS SYSTEM ──────────────────────────────────────────────────────────────────────────────────────────────────
//
//   OnlineTexts.Dismiss(text)                     — Permanently blacklists text.
//   OnlineTexts.DismissTimed(text, cooldownMins?) — Records expiry. Pass item.CooldownMinutes.
//   OnlineTexts.GetFiltered(…)                    — Returns only items that should currently show.
//
//   PsaCard calls the correct method automatically — you never call these directly.
//
// ── DISMISS CLEANUP ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   After every successful fresh fetch, orphaned dismiss hashes are pruned automatically.
//   A hash is "orphaned" when the PSA it was dismissing no longer exists in the current .md.
//   Only permanent dismissals that have no matching item in the current content are removed.
//   Timed dismissals are already self-expiring and are pruned on load — no extra cleanup needed.
//
//   This runs only after a confirmed successful network fetch, never when applying stale cache,
//   so a temporary fetch failure can never accidentally wipe valid dismissals.
//
// ── COOLDOWN ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   3 hours. Stale cache is applied immediately on startup; fresh fetch runs in the
//   background with retries.
// =====================================================================================================================

public static class OnlineTexts
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const string URL =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/IN-APP-ANNOUNCEMENTS.md";

    private const string KEY_TIMESTAMP = "OnlineTexts_Timestamp";
    private const string KEY_DISMISSED = "OnlineTexts_Dismissed";
    private const string KEY_TIMED_DISMISSED = "OnlineTexts_TimedDismissed";

#if DEBUG
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TIMED_DURATION = TimeSpan.FromSeconds(5);
#else
    private static readonly TimeSpan COOLDOWN       = TimeSpan.FromHours(3);
    private static readonly TimeSpan TIMED_DURATION = TimeSpan.FromDays(1);
#endif

    private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);
    private const int MAX_RETRIES = 2;

    // ── Reflection map: lowercase property name → PropertyInfo ────────────────

    private static readonly Dictionary<string, PropertyInfo> _propMap =
        typeof(OnlineTextsContent)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(p => p.Name.ToLowerInvariant(), p => p, StringComparer.Ordinal);

    // ── Modifier regex ────────────────────────────────────────────────────────
    // Matches [key:"value"] anywhere in a line. Key is word chars; value is anything except ".

    private static readonly Regex _modifierRegex = new(
        @"\[(\w+):""([^""]*)""\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
                CleanupOrphanedDismissals();
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
    /// Filters <paramref name="source"/> down to items that should currently be shown,
    /// according to each item's PsaKind and the user's dismiss history.
    /// <para>
    /// Pinned    — always passes through.
    /// Timed     — passes through once its cooldown has elapsed.
    /// Permanent — passes through only if never permanently dismissed.
    /// </para>
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
    /// It will never appear in <see cref="GetFiltered"/> results again until the text changes.
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
    /// Hides <paramref name="text"/> for the item's cooldown duration (or the global default).
    /// After the window elapses it will reappear in <see cref="GetFiltered"/> results.
    /// Only call for Timed items — PsaCard handles this automatically.
    /// Pass <paramref name="cooldownMinutes"/> from <see cref="PsaItem.CooldownMinutes"/> to
    /// respect per-item [cd:""] overrides from the .md file.
    /// </summary>
    public static void DismissTimed(string text, int? cooldownMinutes = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_timedDismissLock)
            {
                var d = GetTimedDismissed();
                var hash = DismissHash(text);
                var duration = cooldownMinutes.HasValue
                    ? TimeSpan.FromMinutes(cooldownMinutes.Value)
                    : TIMED_DURATION;
                d[hash] = DateTime.UtcNow.Add(duration);
                SaveTimedDismissed(d);
                Trace.WriteLine($"[OnlineTexts] Timed dismiss until {d[hash]:HH:mm:ss} " +
                    $"(cd={duration.TotalMinutes:F0}min): \"{text.Substring(0, Math.Min(60, text.Length))}\"");
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
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
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
    // Modifier parser
    // =========================================================================

    /// <summary>
    /// Extracts all [key:"value"] modifier fields from a title line.
    /// Returns the cleaned title (fields removed, trimmed) plus parsed modifier values.
    /// Any field that fails to parse is logged and skipped; defaults remain null.
    /// </summary>
    private static (string cleanTitle, string? glyph, double? fontSize, double? opacity, int? cooldownMinutes)
        ExtractModifiers(string titleLine)
    {
        string? glyph = null;
        double? fontSize = null;
        double? opacity = null;
        int? cooldownMinutes = null;

        var clean = _modifierRegex.Replace(titleLine, match =>
        {
            var key = match.Groups[1].Value.ToLowerInvariant();
            var val = match.Groups[2].Value.Trim();
            try
            {
                switch (key)
                {
                    case "glyph":
                        // Accept 4–5 hex digit codes, e.g. E946, EF2C, F003F
                        if (val.Length is >= 4 and <= 5 &&
                            uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out _))
                            glyph = val.ToUpperInvariant();
                        else
                            Trace.WriteLine($"[OnlineTexts] Invalid glyph value: '{val}' — must be 4–5 hex digits, ignored");
                        break;

                    case "size":
                        if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var sz) && sz > 0)
                            fontSize = sz;
                        else
                            Trace.WriteLine($"[OnlineTexts] Invalid size value: '{val}' — must be a positive number, ignored");
                        break;

                    case "opacity":
                        if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var op) && op is >= 0.0 and <= 1.0)
                            opacity = op;
                        else
                            Trace.WriteLine($"[OnlineTexts] Invalid opacity value: '{val}' — must be 0.0–1.0, ignored");
                        break;

                    case "cd":
                        if (int.TryParse(val, out var cd) && cd > 0)
                            cooldownMinutes = cd;
                        else
                            Trace.WriteLine($"[OnlineTexts] Invalid cd value: '{val}' — must be a positive integer (minutes), ignored");
                        break;

                    default:
                        Trace.WriteLine($"[OnlineTexts] Unknown modifier key: '{key}' — ignored");
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[OnlineTexts] Modifier parse error for [{key}:\"{val}\"]: {ex.Message}");
            }

            return string.Empty; // remove the field token from the title string
        });

        return (clean.Trim(), glyph, fontSize, opacity, cooldownMinutes);
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
                    .Select(b => new PsaItem(
                        b.text.Trim(),
                        b.kind,
                        Glyph: b.glyph,
                        FontSize: b.fontSize,
                        Opacity: b.opacity,
                        CooldownMinutes: b.cooldownMinutes))
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
    /// Core parser. Returns: lowercase-section-name → ordered list of block tuples.
    ///
    /// Single #     = opens a new section. Modifiers on this line apply to the Pinned block.
    ///               A [glyph:] on a # line also becomes the section-level glyph default,
    ///               inherited by any ## / ### blocks that don't specify their own.
    /// ###           = Permanent item separator (checked before ## to avoid misclassification).
    /// ## (not ###)  = Timed item separator.
    /// No separator  = entire section body is one Pinned item.
    /// Separator titles are ignored beyond modifier extraction.
    /// </summary>
    private static Dictionary<string, List<(string text, PsaKind kind, string? glyph, double? fontSize, double? opacity, int? cooldownMinutes)>>
        Parse(string raw)
    {
        var result = new Dictionary<string, List<(string, PsaKind, string?, double?, double?, int?)>>(StringComparer.Ordinal);
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        List<(string text, PsaKind kind, string? glyph, double? fontSize, double? opacity, int? cooldownMinutes)>? currentBlocks = null;
        var block = new StringBuilder();

        // Per-block state
        var currentKind = PsaKind.Pinned;
        string? currentGlyph = null;
        double? currentFontSize = null;
        double? currentOpacity = null;
        int? currentCooldown = null;

        // Section-level glyph default (set by [glyph:] on the # line, inherited by child blocks)
        string? sectionGlyphDefault = null;

        void CommitBlock()
        {
            if (currentBlocks is null) return;
            currentBlocks.Add((
                block.ToString(),
                currentKind,
                currentGlyph ?? sectionGlyphDefault, // per-item wins, fall back to section default
                currentFontSize,
                currentOpacity,
                currentCooldown));
            block.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // ── Single # header ───────────────────────────────────────────────
            if (line.StartsWith("# ") && !line.StartsWith("## ") && line.Length > 2)
            {
                CommitBlock();

                var (cleanTitle, glyph, fontSize, opacity, _) = ExtractModifiers(line.Substring(2));
                // cd is intentionally ignored on # lines (Pinned blocks are never dismissed)

                var name = cleanTitle.ToLowerInvariant();
                if (string.IsNullOrEmpty(name)) continue;

                currentBlocks = new List<(string, PsaKind, string?, double?, double?, int?)>();
                result[name] = currentBlocks;
                block.Clear();
                currentKind = PsaKind.Pinned;
                sectionGlyphDefault = glyph;   // becomes the default for all child blocks
                currentGlyph = glyph;   // also applies to the Pinned block itself
                currentFontSize = fontSize;
                currentOpacity = opacity;
                currentCooldown = null;

                Trace.WriteLine($"[OnlineTexts] Section: '{name}'" +
                    (glyph != null ? $" glyph={glyph}" : "") +
                    (fontSize != null ? $" size={fontSize}" : "") +
                    (opacity != null ? $" opacity={opacity}" : ""));
            }
            // ── ### or deeper → Permanent ─────────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("###"))
            {
                CommitBlock();
                currentKind = PsaKind.Permanent;

                var (_, glyph, fontSize, opacity, cd) = ExtractModifiers(line.Substring(3));
                currentGlyph = glyph;
                currentFontSize = fontSize;
                currentOpacity = opacity;
                currentCooldown = cd;

                Trace.WriteLine($"[OnlineTexts] ### → Permanent" +
                    (glyph != null ? $" glyph={glyph}" : "") +
                    (cd != null ? $" cd={cd}" : ""));
            }
            // ── ## (exactly, not ###) → Timed ─────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("##"))
            {
                CommitBlock();
                currentKind = PsaKind.Timed;

                var (_, glyph, fontSize, opacity, cd) = ExtractModifiers(line.Substring(2));
                currentGlyph = glyph;
                currentFontSize = fontSize;
                currentOpacity = opacity;
                currentCooldown = cd;

                Trace.WriteLine($"[OnlineTexts] ## → Timed" +
                    (glyph != null ? $" glyph={glyph}" : "") +
                    (cd != null ? $" cd={cd}" : ""));
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
    /// Removes permanently dismissed hashes that no longer match any item in the current .md.
    /// Called automatically after every successful fresh fetch — never on stale cache.
    ///
    /// A dismissal is orphaned when the PSA it suppressed has been removed or reworded in the .md.
    /// Since the hash is derived from the item text, any text change produces a new hash,
    /// making the old dismissal inert. This pass finds and removes those inert entries.
    ///
    /// Safety: if anything fails (content unset, storage error, etc.) the method returns silently.
    /// Existing dismissals are never touched unless they are confirmed to be orphaned.
    /// </summary>
    private static void CleanupOrphanedDismissals()
    {
        try
        {
            // Collect hashes of every Permanent item currently in content.
            // Only Permanent items are ever added to the dismissed set, so we only
            // need to consider those — Pinned and Timed items are irrelevant here.
            var liveHashes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prop in _propMap.Values)
            {
                if (prop.GetValue(null) is not PsaItem[] items) continue;
                foreach (var item in items)
                {
                    if (item.Kind == PsaKind.Permanent && !string.IsNullOrWhiteSpace(item.Text))
                        liveHashes.Add(DismissHash(item.Text));
                }
            }

            lock (_dismissLock)
            {
                var d = GetDismissed();
                var before = d.Count;
                var removed = d.RemoveWhere(hash => !liveHashes.Contains(hash));

                if (removed > 0)
                {
                    SaveDismissed(d);
                    Trace.WriteLine($"[OnlineTexts] Cleanup: removed {removed} orphaned dismissal(s) " +
                        $"({before} → {d.Count})");
                }
                else
                {
                    Trace.WriteLine($"[OnlineTexts] Cleanup: no orphaned dismissals ({d.Count} current)");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] CleanupOrphanedDismissals failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug method: clears the cache timestamp to force a fresh fetch on next TriggerUpdate call.
    /// </summary>
    public static void ForceExpireCache()
    {
        try { ApplicationData.Current.LocalSettings.Values.Remove(KEY_TIMESTAMP); } catch { }
        Trace.WriteLine("[OnlineTexts] Cache timestamp cleared");
    }
}
