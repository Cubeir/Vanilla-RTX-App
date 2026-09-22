using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.BetterRTX;

// the whole code path related to backing up defaults can be pruned.
// instead, adapt another approach that redirects mat.bin files via materials.index.json in the materials folder?
// How will that work for reliability? the game might update that json file with updates, current design has a lot of control over things
// think it through later. for now, things are tesetd and good, no reason to change the approach, it is robust, it works, it backs up the files literally!


internal class ApiPresetData
{
    public ApiPresetData() { }

    public string? Uuid { get; set; }
    public string? Slug { get; set; }
    public string? Name { get; set; }
    public string? Stub { get; set; }
    public string? Tonemapping { get; set; }
    public string? Bloom { get; set; }
}

internal class LocalPresetData
{
    public LocalPresetData() { }

    public string? Uuid { get; set; }
    public string? Name { get; set; }
    public string? PresetPath { get; set; }
    public BitmapImage? Icon { get; set; }
    public List<string>? BinFiles { get; set; }
    public Dictionary<string, string>? FileHashes { get; set; }
}

internal class DisplayPresetData
{
    public DisplayPresetData() { }

    public string? Uuid { get; set; }
    public string? Name { get; set; }
    public bool IsDownloaded { get; set; }
    public bool IsCustomImport { get; set; }
    public BitmapImage? Icon { get; set; }
    public string? PresetPath { get; set; }
    public List<string>? BinFiles { get; set; }
    public Dictionary<string, string>? FileHashes { get; set; }
}

/// <summary>
/// Everything the BetterRTX preset manager does that isn't drawing: the local preset cache
/// and its layout, the bedrock.graphics API and its staleness rules, reading presets off
/// disk, downloading and importing them, hashing to work out which one the game is actually
/// running, and the elevated write that installs one.
///
/// <para><b>Why it's separate from the window.</b> The proof is
/// <see cref="ImportPresetFilesHeadlessAsync"/>: a .rtpack double-clicked in Explorer has to
/// land in the cache with no UI involved at all, and while this logic lived on the window
/// that meant constructing a Window and then defusing its own startup by hand. None of this
/// ever needed a window; it needed a folder. Split the same way Alchitex keeps its pipeline
/// out of AlchitexOverlay.</para>
///
/// <para>Bound to one Minecraft install by <see cref="TryAttach"/>. The import path is the
/// one exception - it sets <see cref="CacheFolder"/> alone and uses nothing else.</para>
/// </summary>
internal sealed class BetterRTXManager
{
    #region Identity and cache layout

    /// <summary>
    /// The four files in <c>data\renderer\materials</c> that ray tracing is made of, and the
    /// entire surface this feature touches. All four are backed up, all four are written on
    /// every install, and nothing outside this list is ever read or replaced - the folder
    /// holds ~150 other .material.bin files that have nothing to do with RTX.
    ///
    /// <para><b>There is no required/optional split.</b> A preset is a consistent set of
    /// these four, so one of them coming from a different preset than the other three is a
    /// combination nobody authored. That is why installing underlays the Default backup
    /// (<see cref="BuildInstallSet"/>) and why <see cref="EnsureDefaultBackedUp"/> refuses to
    /// report Ready unless the game has all four.</para>
    ///
    /// <para><b>Presets do not ship all four.</b> The bedrock.graphics API exposes exactly
    /// three per preset - stub, tonemapping and bloom - and every custom .rtpack seen so far
    /// carries the same three. <c>RTXPostFX.material.bin</c> is currently shipped by nothing,
    /// so on every install today it comes from the backup. The underlay is load-bearing in
    /// the normal case, not a guard against a hypothetical one.</para>
    /// </summary>
    public static readonly string[] RTXFiles =
    [
       "RTXPostFX.Bloom.material.bin",
       "RTXPostFX.material.bin",
       "RTXPostFX.Tonemapping.material.bin",
       "RTXStub.material.bin"
    ];
    public static readonly string[] SupportedCustomPresetExtensions = [".rtpack"];

    /// <summary>
    /// The Release backup folder. Its Preview counterpart is
    /// <see cref="DEFAULT_PREVIEW_PRESET_FOLDER_NAME"/>.
    ///
    /// <para><b>The two are never shared.</b> The rest of the cache - API JSON, downloaded
    /// presets, imported .rtpacks - is the same third party's files whichever edition
    /// installs them. A Default backup is a copy of <i>this</i> install's own shipped .bin
    /// files, and Preview is routinely a different build from Release, so one folder for both
    /// would let whichever edition reaches <see cref="ApplyPresetAsync"/> first define
    /// "default" for the other and make a rollback install the wrong game's shaders.</para>
    /// </summary>
    public const string DEFAULT_PRESET_FOLDER_NAME = "__DEFAULT";

    /// <summary>Preview's own backup folder - see <see cref="DEFAULT_PRESET_FOLDER_NAME"/>.</summary>
    public const string DEFAULT_PREVIEW_PRESET_FOLDER_NAME = "__DEFAULT_PREVIEW";

    private const string CacheFolderName = "RTX_Cache";
    private const string ApiCacheFileName = "betterrtx_api_cache.json";

    public const string BETTERRTX_DISCLAIMER_KEY = $"BetterRTXDisclaimerAgreed_Key";

    private const string API_LAST_FETCH_KEY = "BetterRTXManager_ApiLastFetchTimestamp";
    private const int API_REFETCH_INTERVAL_HOURS = 1;
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether opening this manager already did what the refresh button does: cleared the
    /// downloaded presets and rebuilt the index from a fresh fetch.
    ///
    /// <para><b>Fetching the index is not enough to set it</b>, and that is the point. The hourly
    /// check asks the API and then wipes <i>only</i> if the hash moved, while the button wipes
    /// regardless - which is what it is for, since the API can ship new files without its
    /// payload changing. Putting the button on cooldown for a check that wiped nothing would
    /// claim work that didn't happen, and would lock out the one action the button offers for
    /// exactly that case.</para>
    /// </summary>
    public bool RefreshedSinceAttach { get; private set; }

    /// <summary>
    /// Which edition this instance is attached to, as decided by <see cref="TryAttachAsync"/>.
    /// The headless import path never attaches and never needs this - dropping a preset into
    /// the shared cache is the same operation either way.
    /// </summary>
    public bool IsPreview { get; private set; }

    /// <summary>data\renderer\materials inside the game install - where the .bin files go.</summary>
    public string GameMaterialsPath { get; private set; } = string.Empty;

    /// <summary>
    /// LocalState\RTX_Cache. Set by <see cref="TryAttachAsync"/>, or on its own by
    /// <see cref="ImportPresetFilesHeadlessAsync"/>, which needs nothing else.
    /// </summary>
    public string CacheFolder { get; private set; } = string.Empty;

    /// <summary>
    /// This edition's Default folder: a copy of the game's own .bin files, made before the
    /// first install. Release and Preview each get their own - see
    /// <see cref="DEFAULT_PRESET_FOLDER_NAME"/> for why.
    /// </summary>
    public string DefaultFolder { get; private set; } = string.Empty;

    /// <summary>The folder name this edition's Default backup lives under, inside the cache.</summary>
    public string DefaultFolderName => GetDefaultFolderName(IsPreview);

    public static string GetDefaultFolderName(bool isPreview) =>
        isPreview ? DEFAULT_PREVIEW_PRESET_FOLDER_NAME : DEFAULT_PRESET_FOLDER_NAME;

    /// <summary>
    /// True for <i>either</i> edition's Default folder. Every sweep over the cache must ask
    /// this rather than compare against one name: a Release sweep that only spared "__DEFAULT"
    /// deletes Preview's backup, which is that install's only way back to its own shaders.
    /// </summary>
    public static bool IsDefaultFolderName(string folderName) =>
        folderName.Equals(DEFAULT_PRESET_FOLDER_NAME, StringComparison.OrdinalIgnoreCase) ||
        folderName.Equals(DEFAULT_PREVIEW_PRESET_FOLDER_NAME, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where an edition's Default backup sits, resolved without attaching to anything, for
    /// callers that have no manager instance - DefaultsGuard runs before a hard wipe.
    /// </summary>
    public static string? GetDefaultFolderPath(bool isPreview)
    {
        try
        {
            return Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                CacheFolderName,
                GetDefaultFolderName(isPreview));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Could not resolve default folder path: {ex.Message}");
            return null;
        }
    }

    public string ApiCachePath { get; private set; } = string.Empty;

    public List<ApiPresetData>? ApiPresets { get; private set; }
    public Dictionary<string, LocalPresetData>? LocalPresets { get; private set; }

    private string? _cachedApiHash = null;

    /// <summary>
    /// Raised after a soft wipe. The download queue and its status map live on the window,
    /// so it is what has to forget anything that pointed at a folder just deleted.
    /// </summary>
    public Action? DownloadTrackingReset;

    /// <summary>Why <see cref="TryAttachAsync"/> failed, when it does.</summary>
    public enum AttachFailure { None, MaterialsFolderMissing, CacheFolderUnavailable }

    /// <summary>
    /// What <see cref="EnsureDefaultBackedUp"/> found. Anything but <see cref="Ready"/> means
    /// there is no verified way back to this install's own shaders, which is the one condition
    /// under which nothing may be installed.
    /// </summary>
    public enum DefaultBackupState
    {
        /// <summary>All four of <see cref="RTXFiles"/> are in this edition's backup folder.</summary>
        Ready,

        /// <summary>
        /// The game is missing one or more of <see cref="RTXFiles"/>, so no complete backup
        /// can be taken from it. Reached before anything is copied - a partial backup is not a
        /// rollback.
        /// </summary>
        GameFilesIncomplete,

        /// <summary>
        /// Some of <see cref="RTXFiles"/> are backed up and some aren't, and what is held
        /// differs from what is installed - so the game is running something other than its
        /// own defaults, and completing the backup from it would record another preset as
        /// this install's.
        /// </summary>
        BackupUnverifiable,

        /// <summary>The copy itself failed - disk, permissions, a file locked by the game.</summary>
        BackupFailed
    }

    /// <summary>The result of the last <see cref="EnsureDefaultBackedUp"/> call.</summary>
    public DefaultBackupState DefaultBackup { get; private set; } = DefaultBackupState.GameFilesIncomplete;

    /// <summary>
    /// Points this instance at one edition's Minecraft install: locates the materials
    /// folder, creates the cache, and clears what that edition updating has invalidated -
    /// stale .bin files from a previous game version are worse than none.
    ///
    /// <para><paramref name="isPreview"/> decides which Default folder this instance owns
    /// and which stored config hash it compares against. Both are tracked per edition, so
    /// Preview updating - which it does weekly - says nothing about Release and cannot
    /// invalidate anything of Release's.</para>
    /// </summary>
    public async Task<AttachFailure> TryAttachAsync(string minecraftPath, bool isPreview)
    {
        RefreshedSinceAttach = false;
        IsPreview = isPreview;
        GameMaterialsPath = Path.Combine(minecraftPath, "data", "renderer", "materials");

        if (!Directory.Exists(GameMaterialsPath))
            return AttachFailure.MaterialsFolderMissing;

        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
            return AttachFailure.CacheFolderUnavailable;

        CacheFolder = cacheFolder;

        DefaultFolder = Path.Combine(CacheFolder, DefaultFolderName);
        ApiCachePath = Path.Combine(CacheFolder, ApiCacheFileName);

        bool versionChanged = await GameVersionDetector.HasGameVersionChanged(minecraftPath, isPreview);

        // What an update invalidates depends on which edition updated:
        //
        //   This edition's Default backup - always. It is a copy of this install's own files,
        //   which the update just replaced. The other edition's copy stays: that game did not
        //   change, and deleting its backup strands it on whatever preset it is running.
        //
        //   The shared cache (downloads, imports, API JSON) - Release only. Its contents come
        //   from bedrock.graphics, which builds against stable Minecraft, so a Release update
        //   is grounds to suspect they are now for the wrong game. A Preview update is not,
        //   and Preview ships weekly, so wiping on it would re-download every preset the user
        //   owns most weeks for nothing.
        bool sharedCacheWiped = false;

        if (versionChanged)
        {
            Trace.WriteLine($"[BetterRTX] ⚠🔥 GAME VERSION CHANGED ({(isPreview ? "Preview" : "Release")}) 🔥⚠");
            WipeDefaultPresetCache();

            if (!isPreview)
            {
                await WipeNonDefaultPresetsCacheAsync();
                sharedCacheWiped = true;
            }
        }

        // Skipped only when the wipe above has just deleted the API cache this compares
        // against. It is what notices BetterRTX shipping new files, so it still runs after a
        // Preview update, which leaves the cache in place.
        if (!sharedCacheWiped)
            await CheckApiStalenessOnStartupAsync();

        Directory.CreateDirectory(DefaultFolder);

        return AttachFailure.None;
    }

    /// <summary>Drops whatever was loaded, so the next load has to go back to disk/the API.</summary>
    public void ForgetLoadedPresets()
    {
        ApiPresets = null;
        LocalPresets = null;
    }

    /// <summary>
    /// Creates (or confirms) the local preset cache. Static and free of any other state so
    /// <see cref="ImportPresetFilesHeadlessAsync"/> can resolve it on its own.
    /// </summary>
    public static string? EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, CacheFolderName);

            Trace.WriteLine($"[BetterRTX] Cache location: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"[BetterRTX] ✓ Cache established");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Failed to create cache: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Cache wiping

    /// <summary>
    /// Drops <b>this edition's</b> Default backup so the next install re-takes it from the
    /// game's current files. Only a version change for this edition warrants that: the backup
    /// is the only route back to that install's own shaders, and the other edition's copy
    /// belongs to a game this update did not touch.
    /// </summary>
    public void WipeDefaultPresetCache()
    {
        try
        {
            if (Directory.Exists(DefaultFolder))
            {
                Trace.WriteLine($"[BetterRTX] Deleting {DefaultFolderName} backup: {DefaultFolder}");
                Directory.Delete(DefaultFolder, true);
                Trace.WriteLine("[BetterRTX] ✓ Default backup cleared - it will be re-taken from the updated game files");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error clearing default backup: {ex.Message}");
        }
    }
    /// <summary>
    /// Soft wipe: deletes every downloaded and custom-imported preset folder and the API cache
    /// JSON. <b>Both</b> editions' Default folders are preserved - only a game version change
    /// clears one, and only for the edition that changed (see <see cref="IsDefaultFolderName"/>).
    /// </summary>
    public async Task WipeNonDefaultPresetsCacheAsync()
    {
        Trace.WriteLine("[BetterRTX] [SoftWipe] Starting soft cache wipe...");

        int deletedCount = 0;

        if (Directory.Exists(CacheFolder))
        {
            var allFolders = Directory.GetDirectories(CacheFolder)
                .Where(d => !IsDefaultFolderName(Path.GetFileName(d)))
                .ToList();

            foreach (var folder in allFolders)
            {
                try
                {
                    Directory.Delete(folder, true);
                    deletedCount++;
                    Trace.WriteLine($"[BetterRTX] [SoftWipe] Deleted: {Path.GetFileName(folder)}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] [SoftWipe] Error deleting {Path.GetFileName(folder)}: {ex.Message}");
                }
            }
        }

        Trace.WriteLine($"[BetterRTX] [SoftWipe] Deleted {deletedCount} preset folder(s) (downloaded + custom-imported alike)");

        // Delete API cache JSON itself
        if (File.Exists(ApiCachePath))
        {
            File.Delete(ApiCachePath);
            Trace.WriteLine("[BetterRTX] [SoftWipe] API cache deleted");
        }

        // Every path that does the refresh button's job comes through here, so this is where
        // "the window opened already refreshed" is decided - see RefreshedSinceAttach.
        RefreshedSinceAttach = true;

        // Clear in-memory tracking. The download queue itself lives on the window, so it
        // gets told to drop anything still pointing at a folder this just deleted.
        _cachedApiHash = null;
        DownloadTrackingReset?.Invoke();

        Trace.WriteLine($"[BetterRTX] [SoftWipe] ✓ Done — {DEFAULT_PRESET_FOLDER_NAME} and {DEFAULT_PREVIEW_PRESET_FOLDER_NAME} preserved");
    }

    #endregion

    #region BetterRTX API

    /// <summary>
    /// Called once per window open. If an hour has elapsed since the last API fetch,
    /// fetches fresh JSON and compares its hash to what we last cached.
    ///  Same hash? nothing, LoadApiDataAsync will load the cache normally
    ///  Different hash? soft wipe so stale downloaded presets are cleared
    ///  Fetch failed? defer silently to next launch (do not wipe)
    ///  Undetermined? soft wipe (safe default)
    ///
    /// <para><b>The JSON it fetched is what gets cached</b>, written back after the wipe that
    /// deleted the previous copy. The wipe is what makes this necessary - it clears the API
    /// cache along with the presets - and without the write-back <see cref="LoadApiDataAsync"/>
    /// finds no cache a moment later and fetches the identical file a second time, on every
    /// change and on every first run.</para>
    /// </summary>
    private async Task CheckApiStalenessOnStartupAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;

            // Check if an hour has elapsed since last fetch
            if (settings.Values.TryGetValue(API_LAST_FETCH_KEY, out var raw) && raw is long ticks)
            {
                var lastFetch = new DateTime(ticks, DateTimeKind.Utc);
                if ((DateTime.UtcNow - lastFetch).TotalHours < API_REFETCH_INTERVAL_HOURS)
                {
                    Trace.WriteLine("[BetterRTX] [StalenessCheck] Within hour window — skipping API check");
                    return;
                }
            }

            Trace.WriteLine("[BetterRTX] [StalenessCheck] Hour elapsed — fetching latest API JSON for comparison...");

            var freshJson = await FetchApiDataAsync();

            // Fetch failed entirely — defer, do NOT wipe
            if (string.IsNullOrWhiteSpace(freshJson))
            {
                Trace.WriteLine("[BetterRTX] [StalenessCheck] API unreachable — deferring to next launch");
                return;
            }

            // Stamp the successful fetch time now
            settings.Values[API_LAST_FETCH_KEY] = DateTime.UtcNow.Ticks;

            // Compute hash of fresh response
            string freshHash;
            using (var sha256 = SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(freshJson);
                freshHash = BitConverter.ToString(sha256.ComputeHash(bytes))
                                        .Replace("-", "")
                                        .ToLowerInvariant();
            }

            Trace.WriteLine($"[BetterRTX] [StalenessCheck] Fresh hash : {freshHash[..16]}...");
            Trace.WriteLine($"[BetterRTX] [StalenessCheck] Cached hash: {(_cachedApiHash != null ? _cachedApiHash[..16] + "..." : "none yet")}");

            // Load existing cache hash if we don't have it in memory yet
            // (e.g. first run of this method before LoadApiDataAsync has set _cachedApiHash)
            if (_cachedApiHash == null && File.Exists(ApiCachePath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(ApiCachePath);
                    using var sha256 = SHA256.Create();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(existingJson);
                    _cachedApiHash = BitConverter.ToString(sha256.ComputeHash(bytes))
                                                 .Replace("-", "")
                                                 .ToLowerInvariant();
                    Trace.WriteLine($"[BetterRTX] [StalenessCheck] Loaded cache hash from disk: {_cachedApiHash[..16]}...");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] [StalenessCheck] Couldn't read existing cache for comparison: {ex.Message}");
                    // _cachedApiHash stays null — falls through to undetermined → wipe
                }
            }

            if (_cachedApiHash == null)
            {
                // No prior cache to compare against — undetermined, wipe to be safe
                Trace.WriteLine("[BetterRTX] [StalenessCheck] No prior cache hash — undetermined, soft wipe (safe default)");
                await WipeNonDefaultPresetsCacheAsync();
                await SaveApiCacheAsync(freshJson, freshHash);
                return;
            }

            if (freshHash == _cachedApiHash)
            {
                Trace.WriteLine("[BetterRTX] [StalenessCheck] ✓ API unchanged — no wipe needed");
                return;
            }

            // Content changed — wipe downloaded presets so stale files don't linger
            Trace.WriteLine("[BetterRTX] [StalenessCheck] ⚠ API changed — soft wiping downloaded presets");
            await WipeNonDefaultPresetsCacheAsync();
            await SaveApiCacheAsync(freshJson, freshHash);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [StalenessCheck] ✗ Unexpected error: {ex.Message} — soft wipe (safe default)");
            try { await WipeNonDefaultPresetsCacheAsync(); } catch { }
        }
    }

    /// <summary>
    /// Writes API JSON already in hand to the cache and remembers its hash, so the next read
    /// comes off disk. Failing to write is not worth failing the open over - the next read
    /// fetches, which is what would have happened anyway.
    /// </summary>
    private async Task SaveApiCacheAsync(string json, string hash)
    {
        try
        {
            await File.WriteAllTextAsync(ApiCachePath, json);
            _cachedApiHash = hash;
            Trace.WriteLine("[BetterRTX] [StalenessCheck] Cached the JSON this check fetched");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [StalenessCheck] Couldn't cache fetched JSON: {ex.Message}");
        }
    }

    public async Task LoadApiDataAsync()
    {
        try
        {
            string? jsonData = null;
            bool loadedFromCache = false;

            // Check if cache exists and is valid
            if (File.Exists(ApiCachePath))
            {
                Trace.WriteLine("[BetterRTX] ✓ Loading API data from cache...");
                try
                {
                    jsonData = await File.ReadAllTextAsync(ApiCachePath);

                    if (!string.IsNullOrWhiteSpace(jsonData))
                    {
                        // Parse ONCE and validate
                        var parsedPresets = ParseApiData(jsonData);
                        if (parsedPresets != null && parsedPresets.Count > 0)
                        {
                            ApiPresets = parsedPresets;
                            loadedFromCache = true;
                            Trace.WriteLine($"[BetterRTX] ✓ Cache is valid with {ApiPresets.Count} presets");
                        }
                        else
                        {
                            Trace.WriteLine("[BetterRTX] ⚠ Cache exists but is empty or invalid - will fetch fresh data");
                            jsonData = null;
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[BetterRTX] ⚠ Cache file is empty - will fetch fresh data");
                        jsonData = null;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] ⚠ Error reading/parsing cache: {ex.Message} - will fetch fresh data");
                    jsonData = null;
                    loadedFromCache = false;
                }
            }

            // If no valid cache, fetch from API
            if (!loadedFromCache)
            {
                Trace.WriteLine("[BetterRTX] Fetching API data from server...");
                jsonData = await FetchApiDataAsync();

                if (jsonData != null && !string.IsNullOrWhiteSpace(jsonData))
                {
                    // Parse ONCE and validate
                    var parsedPresets = ParseApiData(jsonData);
                    if (parsedPresets != null && parsedPresets.Count > 0)
                    {
                        ApiPresets = parsedPresets;

                        // Save to cache
                        try
                        {
                            await File.WriteAllTextAsync(ApiCachePath, jsonData);
                            Trace.WriteLine("[BetterRTX] ✓ API data cached successfully");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[BetterRTX] ⚠ Failed to save cache: {ex.Message}");
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[BetterRTX] ⚠ Fetched data is empty or invalid - not caching");
                        ApiPresets = new List<ApiPresetData>();
                    }
                }
                else
                {
                    Trace.WriteLine("[BetterRTX] ⚠ Failed to fetch API data and no valid cache available");
                    ApiPresets = new List<ApiPresetData>();
                }
            }

            Trace.WriteLine($"[BetterRTX] ✓ Loaded {ApiPresets?.Count ?? 0} presets total");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error in LoadApiDataAsync: {ex.Message}");
            ApiPresets = new List<ApiPresetData>();
        }
    }

    private async Task<string?> FetchApiDataAsync()
    {
        try
        {
            var client = Helpers.SharedHttpClient;

            // Both shared clients have their own timeout disabled, so a deadline has to come from
            // the caller - without one a server that accepts the connection and then says nothing
            // leaves the manager on its loading screen for as long as the window is open.
            using var cts = new CancellationTokenSource(ApiRequestTimeout);
            var response = await client.GetAsync("https://bedrock.graphics/api", cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[BetterRTX] ⚠ API returned status code: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                Trace.WriteLine("[BetterRTX] ⚠ API returned empty response");
                return null;
            }

            return content;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Trace.WriteLine("[BetterRTX] ⚠ API request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ⚠ Error fetching API data: {ex.Message}");
            return null;
        }
    }

    private List<ApiPresetData> ParseApiData(string jsonData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Cannot parse null or empty JSON data");
                return new List<ApiPresetData>();
            }

            var presets = new List<ApiPresetData>();

            // Walked as nodes rather than deserialized into ApiPresetData: the DTO's setters are
            // only ever called reflectively, which a trimmed Release build strips, and the API is
            // a third party's - an entry of an unexpected shape should cost that entry, not the
            // whole list.
            var jsonArray = MinecraftJson.ParseArray(jsonData);
            if (jsonArray == null)
            {
                Trace.WriteLine("[BetterRTX] ⚠ API response was not a JSON array");
                return presets;
            }

            foreach (var item in jsonArray)
            {
                if (item is not System.Text.Json.Nodes.JsonObject entry) continue;

                presets.Add(new ApiPresetData
                {
                    Uuid = MinecraftJson.GetString(entry["uuid"]),
                    Slug = MinecraftJson.GetString(entry["slug"]),
                    Name = MinecraftJson.GetString(entry["name"]),
                    Stub = MinecraftJson.GetString(entry["stub"]),
                    Tonemapping = MinecraftJson.GetString(entry["tonemapping"]),
                    Bloom = MinecraftJson.GetString(entry["bloom"])
                });
            }

            return presets;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error parsing API data: {ex.Message}");
            return new List<ApiPresetData>();
        }
    }

    #endregion

    #region Presets on disk

    public async Task LoadLocalPresetsAsync()
    {
        LocalPresets = new Dictionary<string, LocalPresetData>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(CacheFolder))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Cache folder doesn't exist - no local presets");
                return;
            }

            // Every folder except the two Default backups. Excluding them by name is what
            // guarantees the other edition's default can never surface in this one's list,
            // whatever its contents look like.
            var presetFolders = Directory.GetDirectories(CacheFolder)
                .Where(d => !IsDefaultFolderName(Path.GetFileName(d)))
                .ToList();

            foreach (var folder in presetFolders)
            {
                var localPreset = await ParseLocalPresetAsync(folder);
                if (localPreset != null && !string.IsNullOrEmpty(localPreset.Uuid))
                {
                    LocalPresets[localPreset.Uuid] = localPreset;
                    Trace.WriteLine($"[BetterRTX] ✓ Loaded local preset: {localPreset.Name} (UUID: {localPreset.Uuid})");
                }
            }

            Trace.WriteLine($"[BetterRTX] ✓ Loaded {LocalPresets.Count} local presets");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error loading local presets: {ex.Message}");
        }
    }

    private async Task<LocalPresetData?> ParseLocalPresetAsync(string presetFolder)
    {
        try
        {
            var manifestFiles = Directory.GetFiles(presetFolder, "manifest.json", SearchOption.AllDirectories);

            if (manifestFiles.Length == 0)
            {
                Trace.WriteLine($"[BetterRTX] No manifest found in: {presetFolder}");
                return null;
            }

            var manifestPath = manifestFiles[0];
            var manifestDir = Path.GetDirectoryName(manifestPath);

            var manifest = await PackManifest.FromFileAsync(manifestPath);
            if (manifest == null)
            {
                Trace.WriteLine($"[BetterRTX] ⚠ Unreadable manifest: {manifestPath}");
                return null;
            }

            string? uuid = manifest.HeaderUuid;
            string name = Path.GetFileName(presetFolder);

            if (!string.IsNullOrWhiteSpace(manifest.HeaderName))
                name = manifest.HeaderName;

            if (string.IsNullOrEmpty(uuid))
            {
                Trace.WriteLine($"[BetterRTX] ⚠ No UUID in manifest: {presetFolder}");
                return null;
            }

            // manifestDir may be null if manifestPath has no directory component (extremely unlikely for a file found via GetFiles,
            // but we guard anyway by falling back to presetFolder)
            var icon = await LoadIconAsync(manifestDir ?? presetFolder) ?? await LoadIconAsync(presetFolder);
            var binFiles = Directory.GetFiles(presetFolder, "*.bin", SearchOption.AllDirectories).ToList();

            var presetHashes = GetExpectedInstalledHashes(binFiles);

            return new LocalPresetData
            {
                Uuid = uuid,
                Name = name,
                PresetPath = presetFolder,
                Icon = icon,
                BinFiles = binFiles,
                FileHashes = presetHashes
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error parsing local preset {presetFolder}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decode ceiling for preset icons, in physical pixels: the list draws them at 96x96, so
    /// this is 2x that for 200% scale. A decoded image costs width x height x 4 bytes of
    /// graphics memory regardless of its file size, and these come out of third-party
    /// .rtpack archives at whatever resolution their author chose - every loaded preset holds
    /// one for the module's lifetime.
    /// </summary>
    private const int IconDecodeWidth = 192;

    private async Task<BitmapImage?> LoadIconAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        var iconFiles = Directory.GetFiles(directory, "pack_icon.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga";
            })
            .ToArray();

        foreach (var iconPath in iconFiles)
        {
            try
            {
                // DecodePixelWidth is ignored unless it is set before the source is handed over.
                var bitmap = new BitmapImage { DecodePixelWidth = IconDecodeWidth };

                using (var fileStream = File.OpenRead(iconPath))
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        await fileStream.CopyToAsync(memoryStream);
                        memoryStream.Position = 0;

                        var randomAccessStream = memoryStream.AsRandomAccessStream();
                        await bitmap.SetSourceAsync(randomAccessStream);
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }
    /// <summary>
    /// Makes sure this edition's Default folder holds a copy of all four of the game's own
    /// <see cref="RTXFiles"/> - the only route back to stock once a preset is installed.
    ///
    /// <para><b>Called at window open, before the list is drawn.</b> Failing to take this
    /// backup means something is wrong with the game folder, which the user needs to know
    /// before a preset is offered rather than after one has been written - the window turns
    /// any state but <see cref="DefaultBackupState.Ready"/> into a disabled list. Ready means
    /// all four of <see cref="RTXFiles"/> are held; a partial backup is not a rollback.</para>
    ///
    /// <para><b>The assumption:</b> when the backup folder is empty, whatever the game holds
    /// is this install's defaults. Nothing in a .bin file says otherwise, so it cannot be
    /// verified. A <i>partial</i> backup is the case that can be: if what is held differs
    /// from what is installed, the game is running something else and completing the set from
    /// it would bake that in permanently, so it reports
    /// <see cref="DefaultBackupState.BackupUnverifiable"/> and writes nothing.</para>
    ///
    /// <para>Idempotent and cheap once complete - the common case is four File.Exists calls.</para>
    /// </summary>
    public DefaultBackupState EnsureDefaultBackedUp()
    {
        try
        {
            Directory.CreateDirectory(DefaultFolder);

            var alreadyBackedUp = RTXFiles
                .Where(f => File.Exists(Path.Combine(DefaultFolder, f)))
                .ToList();

            if (alreadyBackedUp.Count == RTXFiles.Length)
            {
                Trace.WriteLine($"[BetterRTX] [Default] ✓ {DefaultFolderName} already holds all {RTXFiles.Length} RTX files");
                return DefaultBackup = DefaultBackupState.Ready;
            }

            var missingFromGame = RTXFiles
                .Where(f => !File.Exists(Path.Combine(GameMaterialsPath, f)))
                .ToList();

            if (missingFromGame.Count > 0)
            {
                Trace.WriteLine($"[BetterRTX] [Default] ✗ Game is missing {missingFromGame.Count} RTX file(s): {string.Join(", ", missingFromGame)}");
                return DefaultBackup = DefaultBackupState.GameFilesIncomplete;
            }

            // Only safe to finish a partial backup if the files already held are still the
            // ones installed, which is what establishes that the game is on its own defaults.
            foreach (var fileName in alreadyBackedUp)
            {
                var backedUpHash = ComputeFileHash(Path.Combine(DefaultFolder, fileName));
                var gameHash = ComputeFileHash(Path.Combine(GameMaterialsPath, fileName));

                if (backedUpHash == null || gameHash == null || backedUpHash != gameHash)
                {
                    Trace.WriteLine($"[BetterRTX] [Default] ✗ Partial backup disagrees with the game on {fileName} - refusing to complete it from files that aren't default");
                    return DefaultBackup = DefaultBackupState.BackupUnverifiable;
                }
            }

            foreach (var fileName in RTXFiles)
            {
                var destination = Path.Combine(DefaultFolder, fileName);
                if (File.Exists(destination)) continue;

                File.Copy(Path.Combine(GameMaterialsPath, fileName), destination, false);
                Trace.WriteLine($"[BetterRTX] [Default]   ✓ Backed up: {fileName}");
            }

            Trace.WriteLine($"[BetterRTX] [Default] ✓ {DefaultFolderName} complete");
            return DefaultBackup = DefaultBackupState.Ready;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [Default] ✗ Could not back up default files: {ex.Message}");
            return DefaultBackup = DefaultBackupState.BackupFailed;
        }
    }

    public LocalPresetData? CreateDefaultPreset()
    {
        if (!Directory.Exists(DefaultFolder))
            return null;

        var binFiles = Directory.GetFiles(DefaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();

        if (binFiles.Count == 0)
            return null;

        var presetHashes = GetExpectedInstalledHashes(binFiles);

        return new LocalPresetData
        {
            // The folder name doubles as the UUID, keeping Release's and Preview's entries
            // distinct in every dictionary keyed by it.
            Uuid = DefaultFolderName,
            Name = IsPreview ? "Default RTX (Preview)" : "Default RTX",
            PresetPath = DefaultFolder,
            Icon = null,
            BinFiles = binFiles,
            FileHashes = presetHashes
        };
    }
    /// <summary>
    /// Removes one preset folder from the cache. Only ever called for custom imports and
    /// downloads - __DEFAULT is not deletable from the UI at all.
    /// </summary>
    public async Task<bool> DeletePresetAsync(string presetPath, string? displayName)
    {
        try
        {
            Trace.WriteLine($"[BetterRTX] [Delete] Deleting custom preset \"{displayName}\" at: {presetPath}");
            await Task.Run(() => Directory.Delete(presetPath, true));
            Trace.WriteLine($"[BetterRTX] [Delete] ✓ Deleted \"{displayName}\"");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [Delete] ✗ Error deleting \"{displayName}\": {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Downloading and importing

    /// <summary>
    /// Fetches one preset from the API and unpacks it into its own folder in the cache,
    /// named after its UUID so re-downloading replaces it in place.
    /// </summary>
    /// <param name="cancellationToken">
    /// The caller's lifetime, not ours - a three-minute download has to stop when the
    /// window it was started from closes.
    /// </param>
    public async Task<bool> DownloadPresetAsync(string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://bedrock.graphics/pack/{uuid}/release";
            Trace.WriteLine($"[BetterRTX] Downloading from: {url}");

            var (success, downloadedPath) = await Helpers.Download(url, cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(3));

            if (!success || string.IsNullOrEmpty(downloadedPath))
            {
                Trace.WriteLine($"[BetterRTX] ✗ Download failed");
                return false;
            }

            Trace.WriteLine($"[BetterRTX] ✓ Downloaded to: {downloadedPath}");

            // Extract to RTX_Cache
            var sanitizedName = SanitizePresetName(uuid);
            var destinationFolder = Path.Combine(CacheFolder, sanitizedName);

            // Delete existing folder if present
            if (Directory.Exists(destinationFolder))
            {
                Directory.Delete(destinationFolder, true);
            }

            Directory.CreateDirectory(destinationFolder);

            // Extract the archive
            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(downloadedPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            var destPath = Path.Combine(destinationFolder, entry.FullName);
                            var destDir = Path.GetDirectoryName(destPath);

                            if (!string.IsNullOrEmpty(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }

                            entry.ExtractToFile(destPath, true);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[BetterRTX] Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            Trace.WriteLine($"[BetterRTX] ✓ Extracted to: {destinationFolder}");

            // Clean up downloaded file
            try
            {
                File.Delete(downloadedPath);
            }
            catch { }

            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Error downloading {uuid}: {ex.Message}");
            return false;
        }
    }
    // Only ever touches CacheFolder, which is why the headless import path can run this
    // on an instance that was never attached to a Minecraft install at all.
    //
    // Returns a message alongside success/failure - unlike ExpImpDel this has no
    // ImportStatusChanged event of its own to relay, and the headless caller has no window
    // UI to fall back on, so this is the only way it can tell the user *why* something
    // failed rather than just that it did.
    public async Task<(bool Success, string Message)> ImportCustomPresetAsync(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        string? stagingFolder = null;
        try
        {
            if (!File.Exists(archivePath))
            {
                Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ File not found: {archivePath}");
                return (false, $"'{fileName}' not found.");
            }

            Trace.WriteLine($"[BetterRTX] [CustomImport] Importing: {archivePath}");

            stagingFolder = Path.Combine(CacheFolder, $"__staging_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingFolder);

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        var destPath = Path.Combine(stagingFolder, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);

                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        entry.ExtractToFile(destPath, true);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[BetterRTX] [CustomImport] Error extracting {entry.FullName}: {ex.Message}");
                    }
                }
            });

            var parsed = await ParseLocalPresetAsync(stagingFolder);
            if (parsed == null || string.IsNullOrEmpty(parsed.Uuid))
            {
                Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ Not a valid BetterRTX preset: {archivePath}");
                return (false, $"'{fileName}' is not a valid BetterRTX preset (no readable manifest).");
            }

            // Same convention downloaded presets use: folder named after the pack's
            // own UUID, so re-importing the same file just replaces it in place.
            var destinationFolder = Path.Combine(CacheFolder, SanitizePresetName(parsed.Uuid));

            if (Directory.Exists(destinationFolder))
                Directory.Delete(destinationFolder, true);

            Directory.Move(stagingFolder, destinationFolder);
            stagingFolder = null; // moved successfully, nothing left to clean up

            Trace.WriteLine($"[BetterRTX] [CustomImport] ✓ Imported \"{parsed.Name}\" to: {destinationFolder}");
            return (true, $"Imported \"{parsed.Name}\".");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ Error importing {archivePath}: {ex.Message}");
            return (false, $"'{fileName}' failed to import: {ex.Message}");
        }
        finally
        {
            if (stagingFolder != null && Directory.Exists(stagingFolder))
            {
                try { Directory.Delete(stagingFolder, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Headless entry point for .rtpack file-type-association activation (see App.xaml.cs /
    /// MainWindow.ImportBetterRTXPresetFilesAsync). Imports straight into the app's local
    /// preset cache without ever showing a window - not a "patch it to work anyway", but
    /// because importing a custom preset genuinely never needed anything beyond
    /// <see cref="CacheFolder"/> (see <see cref="ImportCustomPresetAsync"/>): no Minecraft
    /// install path, no BetterRTX API fetch, no on-screen list to populate. All of that
    /// exists to let the window show and apply presets, which this doesn't do - applying one
    /// still only happens through the window itself, disclaimer and all, completely
    /// unaffected by this method.
    ///
    /// <para><b>Never route this through the window.</b> A <c>BetterRTXManagerOverlay</c> runs
    /// its Loaded handler even on an instance that is never Activate()d, which starts the full
    /// startup pipeline - including a scan of this very cache folder - concurrently with the
    /// import loop below. That race surfaces as "pack_icon.png is being used by another
    /// process" and "could not find a part of the path ...__staging_...". Keeping the import
    /// here means there is no window to construct and no pipeline that could run.</para>
    ///
    /// <para>Files are processed strictly one at a time - MainWindow.ImportBetterRTXPresetFilesAsync
    /// already serializes calls to this method globally, but the loop below is what makes a
    /// single call importing several presets itself patient rather than firing every
    /// extraction into the cache folder at once. <paramref name="onStatus"/>, if given, is
    /// called once per file with a human-readable result - the caller's route to real
    /// per-file feedback, since this has no ImportStatusChanged-style event of its own the
    /// way ExpImpDel does.</para>
    /// </summary>
    public static async Task<(int Succeeded, int Total)> ImportPresetFilesHeadlessAsync(
        IReadOnlyList<string> filePaths, Action<string>? onStatus = null)
    {
        var candidates = filePaths
            .Where(p => SupportedCustomPresetExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return (0, 0);

        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
        {
            Trace.WriteLine("[BetterRTX] [CustomImport] Headless import aborted - could not establish cache folder.");
            onStatus?.Invoke("Could not locate the preset cache folder - import aborted.");
            return (0, candidates.Count);
        }

        // Best-effort sweep of anything a previous run left behind - a __staging_ folder is,
        // by construction, either mid-import or abandoned (see ImportCustomPresetAsync's own
        // cleanup), so one still sitting here on entry can only be a crash/interruption from
        // before this process started, never something live.
        try
        {
            foreach (var stale in Directory.EnumerateDirectories(cacheFolder, "__staging_*"))
            {
                try { Directory.Delete(stale, true); }
                catch (Exception ex) { Trace.WriteLine($"[BetterRTX] [CustomImport] Couldn't sweep stale staging folder '{stale}': {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] Staging sweep failed: {ex.Message}");
        }

        // Nothing but the cache folder is needed, so this instance is deliberately never
        // attached to a Minecraft install.
        var manager = new BetterRTXManager();
        manager.CacheFolder = cacheFolder;

        var succeeded = 0;
        foreach (var path in candidates)
        {
            var (ok, message) = await manager.ImportCustomPresetAsync(path);
            if (ok) succeeded++;

            onStatus?.Invoke(message);
        }

        Trace.WriteLine($"[BetterRTX] [CustomImport] Headless import: {succeeded}/{candidates.Count} preset(s) imported.");
        return (succeeded, candidates.Count);
    }
    public static string SanitizePresetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unnamed_Preset";

        var sanitized = name;

        var badChars = new HashSet<char>(Path.GetInvalidFileNameChars())
        {
            '\'', '`', '$', ';', '&', '|', '<', '>', '(', ')', '{', '}', '[', ']',
            '"', '~', '!', '@', '#', '%', '^'
        };

        var chars = sanitized.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (badChars.Contains(chars[i]) || char.IsControl(chars[i]))
            {
                chars[i] = '_';
            }
        }
        sanitized = new string(chars);

        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        while (sanitized.Contains("  "))
            sanitized = sanitized.Replace("  ", " ");

        sanitized = sanitized.Trim('_', ' ', '.');

        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                       "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                       "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = sanitized.ToUpperInvariant();
        if (reserved.Contains(upperName) || reserved.Any(r => upperName.StartsWith(r + ".")))
        {
            sanitized = "_" + sanitized;
        }

        if (string.IsNullOrWhiteSpace(sanitized))
            return "Unnamed_Preset";

        if (sanitized.Length > 200)
            sanitized = sanitized.Substring(0, 150).TrimEnd('_', ' ', '.');

        return sanitized;
    }

    #endregion

    #region Installing into the game

    public async Task<bool> ApplyPresetAsync(LocalPresetData preset)
    {
        try
        {
            Trace.WriteLine($"[BetterRTX] === APPLYING PRESET: {preset.Name} ===");

            // Enforces the invariant at the point of the write rather than only where the
            // button is drawn: nothing reaches the game without a verified way back. Cheap to
            // re-run - the window has already done this at open.
            //
            // Ready means all four RTX files are backed up, which is also what makes the
            // underlay below able to fill whatever the preset doesn't ship.
            var backup = EnsureDefaultBackedUp();
            if (backup != DefaultBackupState.Ready)
            {
                Trace.WriteLine($"[BetterRTX] ✗ Refusing to install - default backup is not usable ({backup})");
                return false;
            }

            var filesToApply = BuildInstallSet(preset);

            var success = await Helpers.ReplaceFilesWithElevation(filesToApply, "[BetterRTX]", "betterrtx_install");
            return success;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error in ApplyPresetAsync: {ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// The exact set of files an install writes: all four of <see cref="RTXFiles"/>, each
    /// taken from the preset where it ships one and from this edition's Default backup where
    /// it doesn't.
    ///
    /// <para><b>The underlay is what stops presets accumulating.</b> Writing only what a
    /// preset happens to contain leaves the rest of the previous preset installed, so a
    /// preset shipping three files followed by one shipping two runs the game on two files
    /// from one and two from the other - a combination nobody authored or tested, out of
    /// binaries that have to agree with each other.</para>
    ///
    /// <para>One merged list rather than installing Default and then the preset: same end
    /// state, one elevated write, one UAC prompt, and no window in which the game holds a
    /// half-reverted set. The Default preset resolves to its own files either way, so it
    /// needs no special case.</para>
    ///
    /// <para><b>A .bin outside <see cref="RTXFiles"/> is ignored, deliberately.</b> There is
    /// no backup of it and therefore no way to undo it, which is the one thing this feature
    /// may never do (see <see cref="EnsureDefaultBackedUp"/>). Installing one would also put
    /// a file in the game that no rollback, not even Default, could ever clear.</para>
    /// </summary>
    private List<(string sourcePath, string destPath)> BuildInstallSet(LocalPresetData preset) =>
        BuildInstallSet(preset.BinFiles ?? new List<string>());

    /// <inheritdoc cref="BuildInstallSet(LocalPresetData)"/>
    private List<(string sourcePath, string destPath)> BuildInstallSet(List<string> presetFiles)
    {
        var filesToApply = new List<(string sourcePath, string destPath)>();

        foreach (var fileName in RTXFiles)
        {
            var fromPreset = presetFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

            var source = fromPreset ?? Path.Combine(DefaultFolder, fileName);
            if (!File.Exists(source)) continue;

            if (fromPreset == null)
                Trace.WriteLine($"[BetterRTX]   {fileName} <- {DefaultFolderName} (preset does not ship it)");

            filesToApply.Add((source, Path.Combine(GameMaterialsPath, fileName)));
        }

        foreach (var binFilePath in presetFiles)
        {
            var binFileName = Path.GetFileName(binFilePath);
            if (RTXFiles.Contains(binFileName, StringComparer.OrdinalIgnoreCase)) continue;

            Trace.WriteLine($"[BetterRTX]   ⚠ Ignoring {binFileName}: outside the four RTX files, so it could never be undone");
        }

        return filesToApply;
    }

    /// <summary>
    /// SHA-256 of a file as lowercase hex, or null if it is missing or unreadable. Null is
    /// never treated as a match by <see cref="AreHashesMatching"/>, so an unreadable file
    /// reads as "not this preset" rather than as an accidental equality.
    /// </summary>
    public static string? ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error computing hash: {ex.Message}");
            return null;
        }
    }

    public Dictionary<string, string> GetCurrentlyInstalledHashes() => GetCurrentlyInstalledHashes(GameMaterialsPath);

    public static Dictionary<string, string> GetCurrentlyInstalledHashes(string gameMaterialsPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in RTXFiles)
        {
            var filePath = Path.Combine(gameMaterialsPath, fileName);
            if (File.Exists(filePath))
            {
                var hash = ComputeFileHash(filePath);
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes[fileName] = hash;
                    Trace.WriteLine($"[BetterRTX]   📊 {fileName}: {hash.Substring(0, 8)}...");
                }
            }
        }

        Trace.WriteLine($"[BetterRTX] 📊 Current game has {hashes.Count}/{RTXFiles.Length} RTX files");
        return hashes;
    }

    /// <summary>
    /// Hashes of what installing these files would leave in the game: the preset's own file
    /// for every <see cref="RTXFiles"/> entry it ships, and this edition's Default backup for
    /// the rest.
    ///
    /// <para><b>Derived from <see cref="BuildInstallSet(List{string})"/>, which is what the
    /// install itself writes</b>, so "is this preset installed" and "what does installing it
    /// do" can never drift apart. Comparing a preset's own three files alone would report it
    /// as installed even when the fourth had been changed by something else - a state no
    /// install of that preset produces.</para>
    ///
    /// <para>On an instance that was never attached to a game there is no backup to fall back
    /// on, so this degrades to the preset's own files. That path only feeds the headless
    /// importer, which never compares anything.</para>
    /// </summary>
    public Dictionary<string, string> GetExpectedInstalledHashes(List<string> binFiles)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sourcePath, destPath) in BuildInstallSet(binFiles))
        {
            var hash = ComputeFileHash(sourcePath);
            if (!string.IsNullOrEmpty(hash))
                hashes[Path.GetFileName(destPath)] = hash;
        }

        return hashes;
    }

    /// <summary>
    /// Hashes of the <see cref="RTXFiles"/> present in <paramref name="binFiles"/>, with no
    /// Default underlay. For callers holding a complete set already - DefaultsGuard hashing
    /// the backup folder to ask whether the game still matches it.
    /// </summary>
    public static Dictionary<string, string> GetPresetHashes(List<string> binFiles)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in RTXFiles)
        {
            var matchingFile = binFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (matchingFile != null && File.Exists(matchingFile))
            {
                var hash = ComputeFileHash(matchingFile);
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes[fileName] = hash;
                }
            }
        }

        return hashes;
    }

    /// <summary>
    /// Whether the game is currently running this preset: true when every file the two sets
    /// have in common matches.
    ///
    /// <para>Every file the expected set names has to be present in the game and identical.
    /// Since the expected set comes from <see cref="GetExpectedInstalledHashes"/> it normally
    /// names all four, so this is a byte-for-byte comparison of the whole RTX file set. Two
    /// empty or disjoint sets answer false: no evidence is not a match.</para>
    /// </summary>
    public static bool AreHashesMatching(Dictionary<string, string> currentHashes, Dictionary<string, string> presetHashes)
    {
        if (currentHashes == null || presetHashes == null)
        {
            Trace.WriteLine("[BetterRTX] ⚠ Cannot compare - one or both hash sets are null");
            return false;
        }

        if (currentHashes.Count == 0 || presetHashes.Count == 0)
        {
            Trace.WriteLine("[BetterRTX] ⚠ Cannot compare - one or both hash sets are empty");
            return false;
        }

        Trace.WriteLine($"[BetterRTX] 🔍 Comparing {presetHashes.Count} expected file(s) against the game:");

        foreach (var (fileName, expectedHash) in presetHashes)
        {
            // Absent counts as a mismatch, not as nothing to compare: the game is missing a
            // file this preset would have written, so it is not in the state installing it
            // produces.
            if (!currentHashes.TryGetValue(fileName, out var currentHash))
            {
                Trace.WriteLine($"[BetterRTX]   ✗ {fileName}: not in the game");
                return false;
            }

            if (currentHash != expectedHash)
            {
                Trace.WriteLine($"[BetterRTX]   ✗ {fileName}: MISMATCH");
                return false;
            }

            Trace.WriteLine($"[BetterRTX]   ✓ {fileName}: Match");
        }

        Trace.WriteLine("[BetterRTX]   ✓✓✓ Game matches this preset exactly");
        return true;
    }

    #endregion
}


/// <summary>
/// Smart preset sorter: A-Z alphabetically, but version numbers in descending order (9-1)
/// </summary>
public static class SmartPresetSorter
{
    /// <summary>
    /// Orders a sequence by name using <see cref="ComparePresetNames"/>, with a
    /// safety net around the ordering call itself.
    ///
    /// ComparePresetNames already catches its own internal exceptions and falls
    /// back to a simple comparison. But .NET's OrderBy/Sort don't just trust a
    /// comparer blindly - they verify the comparisons are internally consistent
    /// (transitive) across the whole sequence, and throw
    /// InvalidOperationException if they ever contradict each other. That
    /// exception is thrown by OrderBy itself, not by ComparePresetNames, so no
    /// try/catch inside ComparePresetNames can ever catch it. This wrapper
    /// catches it at the one place it can actually be caught, and falls back to
    /// a plain ordinal ordering so a pathological/contradictory input can't take
    /// down the whole list.
    /// </summary>
    public static List<T> SafeOrderByName<T>(IEnumerable<T> source, Func<T, string?> nameSelector)
    {
        var list = source as List<T> ?? source.ToList();

        try
        {
            return list.OrderBy(nameSelector, Comparer<string?>.Create(ComparePresetNames)).ToList();
        }
        catch (InvalidOperationException ex)
        {
            Trace.WriteLine($"[BetterRTX] ⚠ SmartPresetSorter ordering failed, falling back to simple alphabetical order: {ex.Message}");
            return list.OrderBy(nameSelector, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Compares two preset names with smart version sorting.
    /// Examples:
    ///   "BetterRTX 1.4.4" comes before "BetterRTX 1.4.0" (and before "BetterRTX 1.4")
    ///   "BetterRTX 1.4.40" comes before "BetterRTX 1.4.4"
    ///   "Pack 10" comes before "Pack 2"
    ///   "Alpha Test" comes before "Beta Test" (normal A-Z)
    ///   "BetterRTX 1.4.4: BetterRTX Default" comes before
    ///   "BetterRTX 1.4.4: BetterRTX — Gilded Graphics" (punctuation doesn't skew ordering)
    ///
    /// COMPARISON STRATEGY: purely positional / segment-by-segment. Names are split
    /// into alternating text and (possibly dotted) numeric segments, and compared
    /// segment by segment left to right - text vs. text alphabetically, number vs.
    /// number by value, dotted version components compared with any missing
    /// trailing part treated as 0.
    ///
    /// This means version numbers are only compared once the surrounding text has
    /// already matched (or once positions line up) - i.e. names are effectively
    /// grouped by their shared leading text first, and ordered by version within
    /// that group. This is a deliberate choice, not an oversight: an earlier
    /// revision of this method tried to be "smarter" by scanning the whole name for
    /// a version number and comparing that first, globally, before anything else.
    /// That broke completely unrelated names - e.g. "Strawberry RT Build 2.0"
    /// jumped above every "BetterRTX 1.4.4: ..." entry purely because 2 > 1,
    /// even though they're different products with no meaningful relationship
    /// between their version numbers. A version number is only meaningfully
    /// comparable between two items that are actually the same thing, and the only
    /// generally reliable signal for "the same thing" in a bare name string is its
    /// shared text - which is exactly what positional comparison already uses.
    /// Trying to bypass that requires guessing at identity in a way that can't be
    /// done safely without hardcoding specific product/brand names.
    ///
    /// Known accepted limitation: if a product fully renames its naming scheme
    /// (e.g. "BetterRTX 1.4.4: Foo" becomes "BetterRTX Preset Pack (v1.5.0): Foo"),
    /// the new-scheme entries won't automatically thread into the old version
    /// timeline - they'll sort based on their own leading text instead. This is
    /// intentional: reliably bridging that case in general isn't possible without
    /// hardcoding names, which would only cover known cases and stay fragile to
    /// anything new.
    ///
    /// Text segments are normalized before comparison: separator punctuation
    /// (":", "-", "–", "—", "_", etc.) is collapsed into plain spaces, so symbols
    /// like an em dash don't get compared as if they were letters (which is what
    /// caused an undecorated name like "...Default" to previously sort below
    /// dash-prefixed variants purely due to punctuation, not wording).
    /// </summary>
    public static int ComparePresetNames(string? name1, string? name2)
    {
        // Handle null/empty cases
        if (name1 == name2) return 0;
        if (string.IsNullOrEmpty(name1)) return 1;
        if (string.IsNullOrEmpty(name2)) return -1;

        try
        {
            // Split both names into segments: each segment is either a text run,
            // or a full dotted numeric run (e.g. "1.4.4") captured as one segment.
            var segments1 = SplitIntoSegments(name1);
            var segments2 = SplitIntoSegments(name2);

            // Compare segment by segment
            int minLength = Math.Min(segments1.Count, segments2.Count);

            for (int i = 0; i < minLength; i++)
            {
                var seg1 = segments1[i];
                var seg2 = segments2[i];

                // If both segments are numeric (version-like), compare component by
                // component (major, minor, patch, ...), missing trailing components
                // are treated as 0. Higher version wins and sorts first (descending).
                if (seg1.IsNumeric && seg2.IsNumeric)
                {
                    int result = CompareVersionComponents(seg1.Components, seg2.Components);
                    if (result != 0) return result;
                    // If every component matched (e.g. "1.4" vs "1.4.0"), fall
                    // through and let the next segment decide.
                }
                // If one is numeric and one is text, text comes first
                else if (seg1.IsNumeric && !seg2.IsNumeric)
                {
                    return 1;
                }
                else if (!seg1.IsNumeric && seg2.IsNumeric)
                {
                    return -1;
                }
                // Both are text - compare normalized text (punctuation collapsed to
                // spaces) with culture-aware comparison for non-ASCII correctness.
                else
                {
                    int result = string.Compare(seg1.NormalizedText, seg2.NormalizedText, StringComparison.CurrentCultureIgnoreCase);
                    if (result != 0) return result;
                }
            }

            // If all shared segments matched, shorter name comes first
            // (e.g. no version number vs. has a version number, or identical prefixes)
            int lengthResult = segments1.Count.CompareTo(segments2.Count);
            if (lengthResult != 0) return lengthResult;

            // Fully tied after everything above (e.g. two names that normalize
            // identically). Fall back to a raw ordinal comparison of the original
            // strings purely for a deterministic, stable result - most sort
            // implementations aren't guaranteed stable, so unresolved ties can
            // otherwise shuffle unpredictably between runs.
            return string.CompareOrdinal(name1, name2);
        }
        catch (Exception ex)
        {
            // If anything goes wrong during parsing/comparison, fall back to simple ordinal comparison
            Trace.WriteLine($"[BetterRTX] ⚠ SmartPresetSorter error, falling back to simple sort: {ex.Message}");
            return string.Compare(name1, name2, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Compares two version component lists (e.g. [1,4,4] vs [1,4,0]) part by
    /// part, treating any missing trailing component as 0. Higher version sorts
    /// first (returns negative), matching the descending version-number ordering.
    /// </summary>
    private static int CompareVersionComponents(List<decimal> components1, List<decimal> components2)
    {
        int maxParts = Math.Max(components1.Count, components2.Count);

        for (int p = 0; p < maxParts; p++)
        {
            decimal v1 = p < components1.Count ? components1[p] : 0m;
            decimal v2 = p < components2.Count ? components2[p] : 0m;

            int cmp = v2.CompareTo(v1); // Reversed! Higher version comes first
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    /// <summary>
    /// Splits a name into alternating text and numeric segments.
    /// Unlike a naive char-by-char split, a numeric run followed by ".digits"
    /// (e.g. "1.4.4") is captured as ONE numeric segment with multiple components,
    /// so version numbers of differing lengths still line up for comparison.
    /// </summary>
    private static List<Segment> SplitIntoSegments(string name)
    {
        var segments = new List<Segment>();
        var text = new System.Text.StringBuilder();
        int i = 0;

        while (i < name.Length)
        {
            if (char.IsDigit(name[i]))
            {
                // Flush any pending text segment before starting a numeric one
                if (text.Length > 0)
                {
                    segments.Add(new Segment { Text = text.ToString(), IsNumeric = false });
                    text.Clear();
                }

                var components = new List<decimal>();

                // Read the first numeric component
                int start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                components.Add(ParseComponent(name.Substring(start, i - start)));

                // Keep consuming ".digits" as additional components of the SAME
                // version segment (e.g. turns "1.4.4" into components [1, 4, 4])
                while (i < name.Length && name[i] == '.' && i + 1 < name.Length && char.IsDigit(name[i + 1]))
                {
                    i++; // skip '.'
                    int partStart = i;
                    while (i < name.Length && char.IsDigit(name[i])) i++;
                    components.Add(ParseComponent(name.Substring(partStart, i - partStart)));
                }

                segments.Add(new Segment { IsNumeric = true, Components = components });
            }
            else
            {
                text.Append(name[i]);
                i++;
            }
        }

        // Add final remaining text segment, if any
        if (text.Length > 0)
        {
            segments.Add(new Segment { Text = text.ToString(), IsNumeric = false });
        }

        return segments;
    }

    /// <summary>
    /// Parses a single numeric component (e.g. the "4" in "1.4.4") as a decimal,
    /// so unusually long numbers don't overflow like they might with long/int.
    /// </summary>
    private static decimal ParseComponent(string numberText)
    {
        if (decimal.TryParse(numberText, out decimal value))
        {
            return value;
        }

        // Number too large to parse - treat as 0 (rare edge case)
        Trace.WriteLine($"[BetterRTX] ⚠ Number too large to parse, treating as 0: {numberText}");
        return 0m;
    }

    /// <summary>
    /// Collapses any run of non-alphanumeric characters (punctuation, symbols,
    /// whitespace - ":", "-", "–", "—", "_", etc.) into a single space, and trims
    /// the ends. This is deliberately generic rather than targeting specific
    /// separator characters, since preset names can use any mix of them
    /// inconsistently. Letters and digits from any script/language are preserved
    /// as-is (char.IsLetterOrDigit is Unicode-aware).
    /// </summary>
    private static string NormalizeForComparison(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private class Segment
    {
        private string _text = string.Empty;
        private string? _normalizedText;

        /// <summary>Raw text for non-numeric segments.</summary>
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                _normalizedText = null; // invalidate cache
            }
        }

        /// <summary>
        /// Normalized (punctuation-collapsed) version of Text, computed lazily and
        /// cached. Used for actual comparisons so separator characters don't
        /// introduce culture-specific ordering artifacts.
        /// </summary>
        public string NormalizedText => _normalizedText ??= NormalizeForComparison(_text);

        /// <summary>Whether this segment represents a (possibly dotted) number.</summary>
        public bool IsNumeric { get; set; }

        /// <summary>
        /// For numeric segments: the dotted components in order, e.g. "1.4.4" -> [1, 4, 4].
        /// Missing trailing components are treated as 0 when compared against a
        /// segment with more components.
        /// </summary>
        public List<decimal> Components { get; set; } = new();
    }
}


/// <summary>
/// Detects Minecraft version changes by hashing <c>MicrosoftGame.Config</c> — the game's install manifest —
/// and comparing it to the hash stored from the previous run. This is a raw content hash, not a parsed version
/// field: the class never reads an actual version number out of the file, it just assumes the manifest's bytes
/// change whenever the game updates, and treats "the hash differs" as a proxy for "the game version changed."
///
/// Elsewhere, a detected change drives that edition's Default backup being dropped (see
/// <see cref="BetterRTXManager.WipeDefaultPresetCache"/>), so it is rebuilt from the post-update game files;
/// for Release it additionally soft-wipes the shared cache
/// (<see cref="BetterRTXManager.WipeNonDefaultPresetsCacheAsync"/>) so possibly update-incompatible downloads
/// are re-fetched rather than reused - see TryAttachAsync for why Preview does not. It also clears the stored
/// BetterRTX disclaimer acknowledgement, so the user is re-prompted after an update.
///
/// <para><b>Release and Preview are tracked separately</b>, under their own stored hash keys. They are two
/// installs on two update cadences - Preview ships roughly weekly - so a shared key would report a change
/// every single time the user switched editions, and every one of those false positives would clear a
/// Default backup that was perfectly current.</para>
///
/// Uncertainty generally resolves in favor of invalidating the cache: an invalid/missing install path, a config
/// file that's gone missing after previously being found, a failed hash computation, or any unexpected exception
/// all report a change occurred. The one exception is the very first run — no stored hash yet means there's
/// nothing to invalidate against, so that case is treated as "unchanged" and simply establishes the baseline
/// hash for future comparisons.
/// </summary>
public static class GameVersionDetector
{
    private const string CONFIG_HASH_KEY = "MinecraftConfigHash";
    private const string PREVIEW_CONFIG_HASH_KEY = "MinecraftPreviewConfigHash";

    /// <summary>
    /// The settings key this edition's last-seen config hash is stored under. Release keeps
    /// the original key so an existing install's baseline survives this becoming per-edition;
    /// Preview starts with none, which is the first-run case and is treated as "unchanged".
    /// </summary>
    private static string ConfigHashKey(bool isPreview) => isPreview ? PREVIEW_CONFIG_HASH_KEY : CONFIG_HASH_KEY;

    /// <summary>
    /// Detects if game version has changed by comparing MicrosoftGame.Config hash.
    /// Returns true if version changed OR unable to determine (safe default).
    /// </summary>
    public static async Task<bool> HasGameVersionChanged(string minecraftInstallPath, bool isPreview)
    {
        var configHashKey = ConfigHashKey(isPreview);

        try
        {
            Trace.WriteLine($"[BetterRTX] === GAME VERSION DETECTION START ({(isPreview ? "Preview" : "Release")}) ===");

            if (string.IsNullOrEmpty(minecraftInstallPath) || !Directory.Exists(minecraftInstallPath))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Invalid Minecraft install path - INVALIDATING CACHE (safe default)");
                Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (invalid path) ===");
                return true; // Invalidate cache when uncertain
            }

            // Find MicrosoftGame.Config file (max 2 levels deep)
            var configPath = FindFileRecursively(minecraftInstallPath, "MicrosoftGame.Config", 2);

            // Get stored hash
            var settings = ApplicationData.Current.LocalSettings;
            var storedConfigHash = settings.Values[configHashKey] as string;

            Trace.WriteLine($"[BetterRTX] 💾 Stored Config hash: {storedConfigHash ?? "NULL (first run or cleared)"}");

            // CASE 1: Config file not found
            if (string.IsNullOrEmpty(configPath))
            {
                Trace.WriteLine("[BetterRTX] ⚠ MicrosoftGame.Config not found in game directory");

                if (!string.IsNullOrEmpty(storedConfigHash))
                {
                    // Had hash before, file now missing - INVALIDATE
                    Trace.WriteLine("[BetterRTX] 🔥 CONFIG FILE DISAPPEARED - CACHE INVALIDATION!");
                    settings.Values.Remove(configHashKey);
                    Trace.WriteLine("[BetterRTX] 💾 Cleared stored config hash");
                    Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (file disappeared) ===");
                    return true;
                }
                else
                {
                    // Never had hash, still can't find file - INVALIDATE (safe default)
                    Trace.WriteLine("[BetterRTX] 🔥 Unable to locate config file - CACHE INVALIDATION (safe default)");
                    Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (unable to determine) ===");
                    return true;
                }
            }

            // CASE 2: Config file exists - compute its hash
            var currentConfigHash = ComputeFileHash(configPath);
            Trace.WriteLine($"[BetterRTX] 📊 Current Config hash: {currentConfigHash ?? "NULL (computation failed)"}");

            if (string.IsNullOrEmpty(currentConfigHash))
            {
                // File exists but can't compute hash - INVALIDATE (safe default)
                Trace.WriteLine("[BetterRTX] 🔥 Failed to compute config hash - CACHE INVALIDATION (safe default)");
                Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (hash computation failed) ===");
                return true;
            }

            // CASE 3: We have a valid current hash
            bool versionChanged = false;

            if (string.IsNullOrEmpty(storedConfigHash))
            {
                // First run - no stored hash yet
                Trace.WriteLine("[BetterRTX] ✓ First run - storing initial config hash (not a version change)");
                versionChanged = false;
            }
            else if (currentConfigHash != storedConfigHash)
            {
                // Hash changed - version updated
                Trace.WriteLine("[BetterRTX] 🔥 CONFIG HASH CHANGED - GAME VERSION UPDATED!");
                Trace.WriteLine($"[BetterRTX]    Old: {storedConfigHash.Substring(0, 16)}...");
                Trace.WriteLine($"[BetterRTX]    New: {currentConfigHash.Substring(0, 16)}...");
                versionChanged = true;

                // Clear disclaimer so user is re-notified after game update. Deliberately the
                // one shared key rather than one per edition: what it warns about is the
                // third-party API and the risk of a game update breaking its files, and either
                // edition updating is a fresh occasion to say so.
                settings.Values.Remove(BetterRTXManager.BETTERRTX_DISCLAIMER_KEY);
                Trace.WriteLine("[BetterRTX] 💾 Cleared BetterRTX disclaimer key — will re-prompt on next open");
            }
            else
            {
                // Hash matches - no change
                Trace.WriteLine("[BetterRTX] ✓ Config hash matches - no version change");
                versionChanged = false;
            }

            // Always update stored hash with current value
            settings.Values[configHashKey] = currentConfigHash;
            Trace.WriteLine("[BetterRTX] 💾 Saved current config hash");

            Trace.WriteLine($"[BetterRTX] === GAME VERSION DETECTION END (changed: {versionChanged}) ===");
            return versionChanged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ EXCEPTION in version detection: {ex.Message}");
            Trace.WriteLine("[BetterRTX] 🔥 Exception occurred - CACHE INVALIDATION (safe default)");
            Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (exception) ===");
            return true; // Invalidate cache on any error (safe default)
        }
    }

    private static string? FindFileRecursively(string startPath, string fileName, int maxDepth)
    {
        try
        {
            return FindFileRecursivelyInternal(startPath, fileName, 0, maxDepth);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error searching for {fileName}: {ex.Message}");
            return null;
        }
    }

    private static string? FindFileRecursivelyInternal(string currentPath, string fileName, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth || !Directory.Exists(currentPath))
            return null;

        var targetPath = Path.Combine(currentPath, fileName);
        if (File.Exists(targetPath))
        {
            Trace.WriteLine($"[BetterRTX] ✓ Found {fileName} at: {targetPath}");
            return targetPath;
        }

        if (currentDepth < maxDepth)
        {
            try
            {
                foreach (var subDir in Directory.GetDirectories(currentPath))
                {
                    var result = FindFileRecursivelyInternal(subDir, fileName, currentDepth + 1, maxDepth);
                    if (result != null)
                        return result;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error accessing subdirectory: {ex.Message}");
            }
        }

        return null;
    }

    private static string? ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error computing hash for {filePath}: {ex.Message}");
            return null;
        }
    }

}
