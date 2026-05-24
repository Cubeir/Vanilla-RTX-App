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
using Windows.Storage;

// =====================================================================================================================
// PsaItem — A single announcement entry with a permanent/ephemeral flag.
//
// Permanent  (IsEphemeral = false): inside a ## or ### block.
//            Dismissed once → gone forever (stored in LocalSettings).
//
// Ephemeral  (IsEphemeral = true):  text that appears BEFORE the first ## / ### in a section.
//            Dismissed during the session → hidden until next launch.
//            Never written to the permanent dismissed list.
//
// Example .md section that produces both:
//
//   # LutManagerAnnouncements
//   This is ephemeral — always comes back on relaunch.      ← ephemeral (no separator yet)
//   ## First item
//   This is permanent — dismissed once, gone forever.       ← permanent
//   ## Second item
//   Another permanent item.                                  ← permanent
// =====================================================================================================================

public record PsaItem(string Text, bool IsEphemeral);


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
//   GetFiltered strips permanently dismissed entries automatically.
//   Ephemeral entries are never in the dismissed list — they always pass through.
// =====================================================================================================================

public static class OnlineTextsContent
{
    public static PsaItem[]? Credits { get; set; }
    public static PsaItem[]? PSA { get; set; }
    public static PsaItem[]? BetterRTXAnnouncements { get; set; }
    public static PsaItem[]? PackUpdateAnnouncements { get; set; }
    public static PsaItem[]? LutManagerAnnouncements { get; set; }
}


// =====================================================================================================================
// OnlineTexts — Online text retrieval, caching, and per-user dismiss tracking.
//
// ── .md FILE FORMAT ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   # PropertyName          ← Must match a property name in OnlineTextsContent (case-insensitive, trimmed).
//   Ephemeral text here.    ← EPHEMERAL: shown every launch, dismissed only for the session.
//   ## (any title)          ← Commits the current block, opens a new one. Title is ignored.
//   Permanent text here.    ← PERMANENT: dismissed once → gone forever.
//   ### (any title)         ← Another separator; same rules apply.
//   More permanent text.
//
// ── RULES ───────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   • Single # only = section header. ## and deeper = item separator (title ignored).
//   • Text before the first ## / ### in a section = ephemeral.
//   • Text inside ## / ### blocks = permanent.
//   • Empty / whitespace-only blocks are discarded.
//   • Properties whose # header is absent from the file are set to null.
//
// ── DISMISS SYSTEM ──────────────────────────────────────────────────────────────────────────────────────────────────
//
//   OnlineTexts.Dismiss(string)         — permanently hides a specific string for this user.
//                                         Call only for permanent (non-ephemeral) items.
//   OnlineTexts.GetFiltered(PsaItem[]?) — returns only non-dismissed items.
//                                         Ephemeral items are never in the dismissed list;
//                                         they always pass through GetFiltered.
//
// ── COOLDOWN ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   6 h (0 in DEBUG). Stale cache is applied immediately on startup; fresh fetch runs
//   in the background with up to 2 retries spaced 5 s apart.
// =====================================================================================================================

public static class OnlineTexts
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const string URL =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/IN-APP-ANNOUNCEMENTS.md";

    private const string KEY_CONTENT = "OnlineTexts_Content";
    private const string KEY_TIMESTAMP = "OnlineTexts_Timestamp";
    private const string KEY_DISMISSED = "OnlineTexts_Dismissed";

#if DEBUG
    private static readonly TimeSpan COOLDOWN = TimeSpan.Zero;
#else
    private static readonly TimeSpan COOLDOWN    = TimeSpan.FromHours(6);
#endif
    private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);
    private const int MAX_RETRIES = 2;

    // ── Reflection map: lowercase property name → PropertyInfo ────────────────
    // Built once at startup. Adding a property to OnlineTextsContent is all that's needed.

    private static readonly Dictionary<string, PropertyInfo> _propMap =
        typeof(OnlineTextsContent)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(p => p.Name.ToLowerInvariant(), p => p, StringComparer.Ordinal);

    // ── Concurrency ───────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static volatile bool _fetching;

    // ── Dismissed set — lazy, in-memory mirror of LocalSettings ──────────────

    private static HashSet<string>? _dismissed;
    private static readonly object _dismissLock = new();

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
    /// Filters <paramref name="source"/> down to items whose text has not been
    /// permanently dismissed. Ephemeral items are never in the dismissed list, so
    /// they always pass through. Returns null if nothing survives.
    /// <para>
    /// Always use this instead of reading OnlineTextsContent properties directly.
    /// </para>
    /// </summary>
    public static PsaItem[]? GetFiltered(PsaItem[]? source)
    {
        if (source is null || source.Length == 0) return null;

        var dismissed = GetDismissed();
        var kept = source
            .Where(item => !string.IsNullOrWhiteSpace(item.Text) && !dismissed.Contains(item.Text))
            .ToArray();

        return kept.Length > 0 ? kept : null;
    }

    /// <summary>
    /// Permanently dismisses <paramref name="text"/> for this user.
    /// It will never appear in <see cref="GetFiltered"/> results again,
    /// even across restarts — until the string changes in the .md file.
    /// <para>
    /// Only call this for permanent (non-ephemeral) items.
    /// PsaCard handles this distinction automatically.
    /// </para>
    /// </summary>
    public static void Dismiss(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_dismissLock)
        {
            var d = GetDismissed();
            if (d.Add(text))
            {
                SaveDismissed(d);
                Trace.WriteLine($"[OnlineTexts] Dismissed: \"{text.Substring(0, Math.Min(60, text.Length))}…\"");
            }
        }
    }

    // =========================================================================
    // Dismiss storage
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
    // Cache
    // =========================================================================

    private static void TryApplyCache()
    {
        try
        {
            var cached = ApplicationData.Current.LocalSettings.Values[KEY_CONTENT] as string;
            if (!string.IsNullOrWhiteSpace(cached))
            {
                Trace.WriteLine($"[OnlineTexts] Applying cache ({cached.Length} chars)");
                ParseAndApply(cached);
            }
            else
            {
                Trace.WriteLine("[OnlineTexts] No cache found");
            }
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
            var s = ApplicationData.Current.LocalSettings;
            s.Values[KEY_CONTENT] = raw;
            s.Values[KEY_TIMESTAMP] = DateTime.UtcNow.ToString("O");
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
                    // Timestamp is in the future — clock skew or bad write. Reset.
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
                    .Select(b => new PsaItem(b.text.Trim(), b.ephemeral))
                    .ToArray();

                prop.SetValue(null, items.Length > 0 ? items : null);
                Trace.WriteLine($"[OnlineTexts] '{key}' → {items.Length} item(s) " +
                    $"({items.Count(i => i.IsEphemeral)} ephemeral, {items.Count(i => !i.IsEphemeral)} permanent)");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] ParseAndApply exception: {ex}");
        }
    }

    /// <summary>
    /// Core parser. Returns: lowercase-section-name → ordered list of (text, ephemeral) blocks.
    ///
    /// Single #    = opens a new section.
    /// ## or ###+  = item separator; commits the current block, opens a new one.
    ///               The inline title after the hashes is ignored.
    ///
    /// Ephemerality rule:
    ///   The block accumulated BEFORE the first separator in a section is ephemeral.
    ///   All blocks accumulated AFTER a separator are permanent.
    ///   If a section has no separators at all, its single block is ephemeral.
    /// </summary>
    private static Dictionary<string, List<(string text, bool ephemeral)>> Parse(string raw)
    {
        var result = new Dictionary<string, List<(string, bool)>>(StringComparer.Ordinal);
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        List<(string text, bool ephemeral)>? currentBlocks = null;
        var block = new StringBuilder();
        bool sectionHadSeparator = false; // tracks whether we've hit a ## / ### yet

        void CommitBlock()
        {
            if (currentBlocks is null) return;
            // ephemeral = we have NOT yet seen a separator in this section
            currentBlocks.Add((block.ToString(), ephemeral: !sectionHadSeparator));
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

                currentBlocks = new List<(string, bool)>();
                result[name] = currentBlocks;
                block.Clear();
                sectionHadSeparator = false; // reset for the new section
                Trace.WriteLine($"[OnlineTexts] Section: '{name}'");
            }
            // ── ## or deeper separator ────────────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("##"))
            {
                CommitBlock();
                sectionHadSeparator = true; // all blocks from here on are permanent
                Trace.WriteLine($"[OnlineTexts] Separator in '{result.Keys.Last()}' (permanent from here)");
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
}
