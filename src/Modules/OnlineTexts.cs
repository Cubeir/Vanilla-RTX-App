// =====================================================================================================================
// OnlineTexts — Unified online text retrieval with caching.
// Fetches a single structured .md file, parses it, and populates OnlineTextsContent.
//
// .md file format:
//   # VariableName
//   Body text (treated as first/only block if no ### present)
//   ### Next array item
//   Its body text, can span multiple lines
//   ### Another array item
//
// Parsing rules:
//   - string property + multiple ### blocks  → only the first block is used
//   - string[] property + no ### markers     → only [0] is populated
//   - # header with no matching map entry    → silently discarded
//   - mapped property with no matching #     → set to null
//   - empty/whitespace content               → treated as null
//
// Cooldown: 6 hours. On expiry, stale cached values are applied immediately while
// a background re-fetch runs with up to 2 retries (20 s apart).
// =====================================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App;
using Windows.Storage;

public static class OnlineTexts
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const string ANNOUNCEMENTS_URL =
        "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/IN-APP-ANNOUNCEMENTS.md";

    private const string CACHE_CONTENT_KEY = "OnlineTexts_Content";
    private const string CACHE_TIMESTAMP_KEY = "OnlineTexts_Timestamp";
#if DEBUG
    private static readonly TimeSpan FETCH_COOLDOWN = TimeSpan.FromHours(0);
#else
    private static readonly TimeSpan FETCH_COOLDOWN = TimeSpan.FromHours(6);
#endif
    private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);
    private const int MAX_RETRIES = 2;

    // ── Explicit section-name → property mapping ──────────────────────────────
    // Keys must exactly match the normalized form of the # header in the .md file.
    // Normalisation: lowercase, only alphanumeric characters kept.
    // e.g.  "# Supporter Credits"  →  "supportercredits"
    //        "# PSA"               →  "psa"

    private static readonly Dictionary<string, Action<List<string>>> SectionMap =
        new(StringComparer.Ordinal)
        {
            ["credits"] = blocks => OnlineTextsContent.Credits =
                blocks.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b))?.Trim(),

            ["psa"] = blocks => OnlineTextsContent.PSA = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.Trim())
                .ToArray() is { Length: > 0 } arr ? arr : null,
        };

    // ── Concurrency guard ─────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _fetchLock = new(1, 1);
    private static volatile bool _isFetching = false;

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Fire-and-forget: loads stale cache immediately, then re-fetches in the
    /// background if the cooldown has expired. Safe to call from any thread.
    /// Never throws.
    /// </summary>
    public static void TriggerUpdate() => _ = TriggerUpdateAsync();

    /// <summary>
    /// Awaitable version of <see cref="TriggerUpdate"/>.
    /// Returns true if a fresh fetch succeeded, false otherwise.
    /// </summary>
    public static async Task<bool> TriggerUpdateAsync()
    {
        Trace.WriteLine("[OnlineTexts] TriggerUpdateAsync called");

        TryApplyCache();

        if (!IsCooldownExpired())
        {
            Trace.WriteLine("[OnlineTexts] Cooldown not expired, using cache");
            return false;
        }

        if (_isFetching)
        {
            Trace.WriteLine("[OnlineTexts] Already fetching, skipping");
            return false;
        }

        if (!await _fetchLock.WaitAsync(0))
        {
            Trace.WriteLine("[OnlineTexts] Could not acquire lock, skipping");
            return false;
        }

        _isFetching = true;
        try
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    Trace.WriteLine($"[OnlineTexts] Retry {attempt}, waiting {RETRY_DELAY.TotalSeconds}s...");
                    await Task.Delay(RETRY_DELAY);
                }

                Trace.WriteLine($"[OnlineTexts] Fetch attempt {attempt + 1}/{MAX_RETRIES + 1}");
                var raw = await FetchRawAsync();

                if (raw is null)
                {
                    Trace.WriteLine($"[OnlineTexts] Fetch attempt {attempt + 1} returned null");
                    continue;
                }

                Trace.WriteLine($"[OnlineTexts] Fetch succeeded ({raw.Length} chars), parsing...");
                ParseAndApply(raw);
                CacheContent(raw);
                Trace.WriteLine($"[OnlineTexts] Done. Credits={(OnlineTextsContent.Credits is null ? "null" : "set")}, PSA count={OnlineTextsContent.PSA?.Length ?? 0}");
                return true;
            }

            Trace.WriteLine("[OnlineTexts] All fetch attempts failed, staying on cache");
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] Unexpected exception: {ex}");
            return false;
        }
        finally
        {
            _isFetching = false;
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Expires the cache and triggers a fresh update immediately.
    /// </summary>
    public static void ForceRefresh()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[CACHE_TIMESTAMP_KEY] =
                DateTime.UtcNow.AddDays(-1).ToString("O");
        }
        catch { }

        TriggerUpdate();
    }

    // =========================================================================
    // Cache helpers
    // =========================================================================

    private static void TryApplyCache()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(CACHE_CONTENT_KEY, out var raw) &&
                raw is string s && !string.IsNullOrWhiteSpace(s))
            {
                Trace.WriteLine($"[OnlineTexts] Applying cache ({s.Length} chars)");
                ParseAndApply(s);
            }
            else
            {
                Trace.WriteLine("[OnlineTexts] No cache found");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] TryApplyCache failed: {ex}");
        }
    }
    private static void CacheContent(string raw)
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[CACHE_CONTENT_KEY] = raw;
            settings.Values[CACHE_TIMESTAMP_KEY] = DateTime.UtcNow.ToString("O");
            Trace.WriteLine($"[OnlineTexts] Cached {raw.Length} chars at {DateTime.UtcNow:O}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] CacheContent failed: {ex}");
        }
    }

    private static bool IsCooldownExpired()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Values.TryGetValue(CACHE_TIMESTAMP_KEY, out var val))
            {
                Trace.WriteLine("[OnlineTexts] No timestamp found, cooldown treated as expired");
                return true;
            }

            if (DateTime.TryParse(val?.ToString(), out var last))
            {
                var age = DateTime.UtcNow - last;
                // Negative age means timestamp is in the future (clock skew / bad write) — treat as expired
                if (age < TimeSpan.Zero)
                {
                    Trace.WriteLine($"[OnlineTexts] Timestamp is in the future — resetting and treating as expired");
                    try { ApplicationData.Current.LocalSettings.Values.Remove(CACHE_TIMESTAMP_KEY); } catch { }
                    return true;
                }
                var expired = age >= FETCH_COOLDOWN;
                Trace.WriteLine($"[OnlineTexts] Cache age: {age.TotalMinutes:F1} min, cooldown: {FETCH_COOLDOWN.TotalHours}h, expired: {expired}");
                return expired;
            }

            Trace.WriteLine("[OnlineTexts] Could not parse timestamp, treating as expired");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] IsCooldownExpired failed: {ex}");
            return true;
        }
    }

    // =========================================================================
    // Network
    // =========================================================================

    private static async Task<string?> FetchRawAsync(int timeoutSeconds = 8)
    {
        try
        {
            Trace.WriteLine($"[OnlineTexts] Fetching {ANNOUNCEMENTS_URL}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestHeaders.Add("User-Agent",
                $"vanilla_rtx_app_updater/{TunerVariables.appVersion} " +
                "(https://github.com/Cubeir/Vanilla-RTX-App)");

            var response = await client.GetAsync(ANNOUNCEMENTS_URL);
            Trace.WriteLine($"[OnlineTexts] HTTP {(int)response.StatusCode} {response.StatusCode}");

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync()
                : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] FetchRawAsync exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // Parser
    // =========================================================================

    /// <summary>
    /// Parses raw .md content and dispatches each section's blocks to the
    /// corresponding action in <see cref="SectionMap"/>.
    /// Properties whose # header is absent from the file are nullified.
    /// </summary>
    private static void ParseAndApply(string raw)
    {
        try
        {
            Trace.WriteLine("[OnlineTexts] ParseAndApply started");
            NullifyAll();
            var sections = ParseSections(raw);
            Trace.WriteLine($"[OnlineTexts] Parsed {sections.Count} section(s): {string.Join(", ", sections.Keys)}");

            foreach (var (header, blocks) in sections)
            {
                if (SectionMap.TryGetValue(header, out var apply))
                {
                    Trace.WriteLine($"[OnlineTexts] Applying section '{header}' with {blocks.Count} block(s)");
                    apply(blocks);
                }
                else
                {
                    Trace.WriteLine($"[OnlineTexts] Unknown section '{header}', discarding");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OnlineTexts] ParseAndApply exception: {ex}");
        }
    }

    /// <summary>
    /// Calls each mapped action with an empty list, which causes the setter
    /// to assign null (matching the "no data" contract).
    /// </summary>
    private static void NullifyAll()
    {
        foreach (var apply in SectionMap.Values)
            apply(new List<string>());
    }

    /// <summary>
    /// Splits the raw markdown into a dictionary:
    ///   normalised-header-name → ordered list of text blocks.
    ///
    /// One block per ### group (or the whole body if no ### are present).
    /// The ### line's own text becomes the first line of the new block.
    /// </summary>
    private static Dictionary<string, List<string>> ParseSections(string raw)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Normalize all line endings up front — no \r\n surprises anywhere downstream
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        List<string>? currentBlocks = null;
        string? currentHeader = null;
        var currentBlock = new StringBuilder();

        void CommitBlock()
        {
            if (currentBlocks is null) return;
            var text = currentBlock.ToString().Trim();
            Trace.WriteLine($"[OnlineTexts] Committing block for '{currentHeader}': " +
                (string.IsNullOrWhiteSpace(text)
                    ? "(empty)"
                    : $"{text.Length} chars: \"{text.Substring(0, Math.Min(60, text.Length))}...\""));
            currentBlocks.Add(text);
            currentBlock.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // Exactly "# Title" — single # followed by space and non-empty text only
            // Ignores ##, ###, ####, and bare "#" lines
            if (line.StartsWith("# ") &&
                !line.StartsWith("## ") &&
                line.Length > 2 &&
                !string.IsNullOrWhiteSpace(line.Substring(2)))
            {
                CommitBlock();
                currentHeader = line.Substring(2).Trim();
                var key = Normalise(currentHeader);
                Trace.WriteLine($"[OnlineTexts] New section: '{currentHeader}' → key '{key}'");
                currentBlocks = new List<string>();
                result[key] = currentBlocks;
                currentBlock.Clear();
            }
            // Exactly "### ..." — three hashes only, not #### or deeper
            // A bare "###" with nothing after is a clean delimiter too
            else if ((line == "###" || line.StartsWith("### ")) && currentBlocks is not null)
            {
                CommitBlock();
                var afterHash = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;
                Trace.WriteLine($"[OnlineTexts] ### delimiter, title: " +
                    $"'{(string.IsNullOrWhiteSpace(afterHash) ? "(none)" : afterHash)}'");
                if (!string.IsNullOrWhiteSpace(afterHash))
                    currentBlock.Append(afterHash).Append('\n');
            }
            // Everything else inside a section goes into the current block as-is
            else if (currentBlocks is not null)
            {
                currentBlock.Append(line).Append('\n');
            }
            // Lines before any # header are silently ignored
        }

        CommitBlock();
        return result;
    }

    private static string Normalise(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}


// =====================================================================================================================
// OnlineTextsContent — Static store populated by OnlineTexts.
//
// To add a new text variable:
//   1. Add a public static property here.
//   2. Add a matching # Section to IN-APP-ANNOUNCEMENTS.md.
//   3. Add an entry to OnlineTexts.SectionMap.
//
// null always means "nothing to show" — no data, empty section, or fetch failed.
// =====================================================================================================================

public static class OnlineTextsContent
{
    /// <summary>
    /// Supporter credits. Single string — only the first block of the # Credits
    /// section is used even if multiple ### entries exist.
    /// </summary>
    public static string? Credits { get; set; }

    /// <summary>
    /// Public service announcements. One entry per ### block in the # PSA section.
    /// [0] is the oldest / most persistent. Display in reverse order so [0] ends
    /// up at the top of the sidebar log (logged last = most visible).
    /// </summary>
    public static string[]? PSA { get; set; }
}
