using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;
using static Vanilla_RTX_App.Modules.PackLocator; // For static UUIDs, they are stored there for locating packs

namespace Vanilla_RTX_App.Modules.PackUpdater;

/// =====================================================================================================================
/// Only deals with cache, we don't care if user has Vanilla RTX installed or not, we compare versions of cache to remote
/// No cache? download latest, cache outdated? download latest, if there's a cache and the rest fails for whatever the reason, fallback to cache
/// Deployment deletes any pack that matches UUIDs as defined at the begenning of PackLocator class
/// =====================================================================================================================

public enum PackType { VanillaRTX, VanillaRTXNormals, VanillaRTXOpus }

public enum VersionSource
{
    Remote,           // Fresh from GitHub
    CachedRemote,     // From few-min cache of remote versions
    ZipballFallback   // Read from cached zipball when remote unavailable
}

/// <summary>
/// The verdict of <see cref="PackUpdater.GetUpdateNoticeAsync"/> - what, if anything, is worth
/// telling the user about Vanilla RTX from outside the updater window.
///
/// Deliberately a verdict and not a sentence: this class decides the facts, the caller owns the
/// wording, because the wording names a button that lives in the caller's own XAML.
/// </summary>
public enum PackUpdateNotice
{
    /// <summary>Everything installed is current, or nothing could be verified against the remote.
    /// Both mean the same thing to a caller: say nothing.</summary>
    None,

    /// <summary>The remote answered, and the user has none of the three packs installed.</summary>
    NothingInstalled,

    /// <summary>At least one installed pack is behind the remote. See the accompanying count.</summary>
    UpdatesAvailable
}

public class PackUpdater
{
    private const string VANILLA_RTX_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX/manifest.json";
    private const string VANILLA_RTX_NORMALS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Normals/manifest.json";
    private const string VANILLA_RTX_OPUS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Opus/manifest.json";
    private const string VANILLA_RTX_REPO_ZIPBALL_URL = "https://github.com/Cubeir/Vanilla-RTX/archive/refs/heads/master.zip";

    // Remote version cache // how frequently to check the remote again for manifest's versions
    //
    // Not split by edition, and that is the point: everything this class fetches - the three
    // manifests above, the zipball - is the same file on the same branch whichever edition the
    // app is targeting. Only where a pack gets INSTALLED differs, and PackLocator handles that
    // separately. These keys used to be per-edition, which bought nothing and meant a user who
    // toggles Preview re-asked GitHub for bytes it had already cached, doubling the request rate
    // for identical data.
    private const string RemoteVersionsCacheKey = "RemoteVersionsCache";
    private const string RemoteVersionsCacheTimeKey = "RemoteVersionsCacheTime";
    private static readonly TimeSpan RemoteVersionCacheDuration = TimeSpan.FromMinutes(10);

    // Cache validation check cooldown (Zip re-check versus remote before trying to install from it)
    private const string LastCacheCheckKey = "LastCacheValidationCheck";
    private static readonly TimeSpan CacheCheckCooldown = TimeSpan.FromMinutes(55);

    private bool _installationInProgress = false;
    private PackType? _currentInstallingPack = null;

    public string EnhancementFolderName { get; set; } = "__enhancements";
    public bool InstallToDevelopmentFolder { get; set; } = false;
    public bool CleanUpTheOtherFolder { get; set; } = true;

    // ======================= Installation State Management =======================

    public bool IsInstallationInProgress()
    {
        return _installationInProgress;
    }

    public PackType? GetCurrentlyInstallingPack()
    {
        return _currentInstallingPack;
    }

    private void SetInstallationState(bool isInstalling, PackType? pack = null)
    {
        _installationInProgress = isInstalling;
        _currentInstallingPack = pack;
    }

    private void ClearInstallationState()
    {
        SetInstallationState(false, null);
    }

    // ======================= Cache Invalidation (Core) =======================

    public void InvalidateCache()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var cachedPath = localSettings.Values["CachedZipballPath"] as string;

        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
        {
            try
            {
                File.Delete(cachedPath);
                Trace.WriteLine("🗑️ Deleted outdated cache file");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to delete cache file: {ex.Message}");
            }
        }

        localSettings.Values["CachedZipballPath"] = null;
        Trace.WriteLine("❌ Cache invalidated - will download fresh on next install");

        RefreshDeployableCacheState();
    }

    // ======================= Deployable Cache State (broadcast) =======================

    /// <summary>
    /// Raised when the answer to "is there a deployable cache?" changes, so anything drawing that
    /// state (MainWindow's Get-latest-packs glyph) can follow along instead of re-asking at every
    /// place that might have changed it - which is how it was done before, and meant every new
    /// cache-touching code path silently owed a refresh call it was easy to forget.
    ///
    /// Static because the cache is one process-wide thing - a single LocalSettings key plus one
    /// file on disk - not a per-instance one. That matters in practice: PackUpdaterWindow falls
    /// back to `new PackUpdater()` when it can't borrow MainWindow's, and an invalidation from
    /// that second instance still has to reach whoever is drawing the glyph.
    ///
    /// Raised on whatever thread made the change - installs run under the caller's Task.Run - so
    /// a subscriber touching UI has to marshal. Nothing here does that for you on purpose: this
    /// class has no business knowing what a DispatcherQueue is.
    /// </summary>
    public static event Action<bool>? DeployableCacheChanged;

    private static bool? _lastBroadcastCacheState;

    /// <summary>
    /// Re-probes the cache and raises <see cref="DeployableCacheChanged"/> only when the answer
    /// actually moved since the last broadcast - so this is safe to call as often as a caller
    /// likes, and a subscriber never sees a redundant event.
    ///
    /// Callers only need this for changes made behind the class's back (a Wipe, a temp-folder
    /// sweep, a file deleted by hand); every change this class makes itself already broadcasts.
    /// Returns the freshly probed state for callers that want it inline.
    /// </summary>
    public bool RefreshDeployableCacheState()
    {
        var state = HasDeployableCache();

        if (_lastBroadcastCacheState == state)
            return state;

        _lastBroadcastCacheState = state;

        try
        {
            DeployableCacheChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            // A subscriber blowing up is a UI problem, never a reason to fail a cache operation.
            Trace.WriteLine($"[PackUpdater] DeployableCacheChanged subscriber threw: {ex.Message}");
        }

        return state;
    }

    // ======================= Cache Validation Check =======================

    public async Task<bool> ValidateCacheAgainstRemote()
    {
        var cacheInfo = GetCacheInfo();

        if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
        {
            Trace.WriteLine("📦 No cache exists - will download on first pack installation");
            return false;
        }

        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            Trace.WriteLine("🛜 No network available - will use existing cache");
            return false;
        }

        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var checkKey = LastCacheCheckKey;

        if (localSettings.Values[checkKey] is string lastCheckStr &&
            DateTimeOffset.TryParse(lastCheckStr, out var lastCheck))
        {
            if (now < lastCheck + CacheCheckCooldown)
            {
                var minutesLeft = (int)Math.Ceiling((lastCheck + CacheCheckCooldown - now).TotalMinutes);
                Trace.WriteLine($"⏳ Cache check on cooldown - {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} left");
                return false;
            }
        }

        (PackManifest? rtx, PackManifest? normals, PackManifest? opus)? remote = null;

        try
        {
            remote = await FetchRemoteManifests();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"⚠️ Failed to contact GitHub: {ex.Message}");
        }

        if (remote != null)
        {
            localSettings.Values[checkKey] = now.ToString("o");
        }
        else
        {
            Trace.WriteLine("⚠️ Could not validate cache - will use existing cache");
            return false;
        }

        bool needsInvalidation = await DoesCacheNeedUpdate(cacheInfo.path!, remote.Value);

        if (needsInvalidation)
        {
            Trace.WriteLine("📦 Cache is outdated - invalidating now");
            InvalidateCache();
            return true;
        }

        Trace.WriteLine("✅ Cache is up-to-date");
        return false;
    }

    private async Task<bool> DoesCacheNeedUpdate(string cachedPath, (PackManifest? rtx, PackManifest? normals, PackManifest? opus) remoteManifests)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachedPath);
            var cachedPacks = await FindPacksInZip(archive);

            var rtxManifest = cachedPacks.TryGetValue(PackType.VanillaRTX, out var rtxFound) ? rtxFound.Manifest : null;
            var normalsManifest = cachedPacks.TryGetValue(PackType.VanillaRTXNormals, out var normalsFound) ? normalsFound.Manifest : null;
            var opusManifest = cachedPacks.TryGetValue(PackType.VanillaRTXOpus, out var opusFound) ? opusFound.Manifest : null;

            bool anyOutdated = false;

            if (remoteManifests.rtx != null)
            {
                if (rtxManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(rtxManifest, remoteManifests.rtx))
                {
                    var cacheVer = ExtractVersionFromManifest(rtxManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.rtx);
                    Trace.WriteLine($"📦 Vanilla RTX: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (rtxManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (remoteManifests.normals != null)
            {
                if (normalsManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX Normals is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(normalsManifest, remoteManifests.normals))
                {
                    var cacheVer = ExtractVersionFromManifest(normalsManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.normals);
                    Trace.WriteLine($"📦 Vanilla RTX Normals: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (normalsManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX Normals exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (remoteManifests.opus != null)
            {
                if (opusManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX Opus is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(opusManifest, remoteManifests.opus))
                {
                    var cacheVer = ExtractVersionFromManifest(opusManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.opus);
                    Trace.WriteLine($"📦 Vanilla RTX Opus: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (opusManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX Opus exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (!anyOutdated)
            {
                Trace.WriteLine("✅ All packs in cache are up-to-date");
            }

            return anyOutdated;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"⚠️ Error reading cached zipball: {ex.Message} - invalidating cache");
            return true;
        }
    }

    // ======================= Individual Pack Installation =======================

    public async Task<bool> UpdateSinglePackAsync(PackType packType, bool enableEnhancements)
    {
        // Check if another installation is already running
        if (IsInstallationInProgress())
        {
            Trace.WriteLine("⚠️ Another installation is already in progress");
            return false;
        }

        try
        {
            // Mark installation as in progress
            SetInstallationState(true, packType);

            var packName = GetPackDisplayName(packType);
            Trace.WriteLine($"🔄 Starting installation for {packName}...");

            await ValidateCacheAgainstRemote();

            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
            {
                Trace.WriteLine("📦 No cache available - downloading now...");

                var (downloadSuccess, downloadPath) = await DownloadLatestPackage();
                if (!downloadSuccess || string.IsNullOrEmpty(downloadPath))
                {
                    Trace.WriteLine("❌ Download failed");
                    return false;
                }

                SaveCachedZipballPath(downloadPath);
                cacheInfo = (true, downloadPath);
            }

            Trace.WriteLine("✅ Using cached zipball for deployment");
            return await DeployPackage(cacheInfo.path!, packType, enableEnhancements);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Unexpected error: {ex.Message}");
            return false;
        }
        finally
        {
            // Always clear installation state when done
            ClearInstallationState();
        }
    }

    // ======================= Remote Version Fetching (For UI Display) =======================

    public async Task<(
        (string? version, VersionSource source) rtx,
        (string? version, VersionSource source) normals,
        (string? version, VersionSource source) opus
    )> GetRemoteVersionsAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var cacheKey = RemoteVersionsCacheKey;
        var timeKey = RemoteVersionsCacheTimeKey;

        if (localSettings.Values[timeKey] is string cacheTimeStr &&
            DateTimeOffset.TryParse(cacheTimeStr, out var cacheTime) &&
            now < cacheTime + RemoteVersionCacheDuration)
        {
            if (localSettings.Values[cacheKey] is string cachedJson)
            {
                try
                {
                    var cached = ParseJsonObject(cachedJson)
                        ?? throw new JsonException("Cached remote versions payload was not a JSON object.");

                    var rtxCached = cached["rtx"];
                    var normalsCached = cached["normals"];
                    var opusCached = cached["opus"];

                    return (
                        (MinecraftJson.GetString(rtxCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(rtxCached?["source"])))),
                        (MinecraftJson.GetString(normalsCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(normalsCached?["source"])))),
                        (MinecraftJson.GetString(opusCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(opusCached?["source"]))))
                    );
                }
                catch { /* Fall through */ }
            }
        }

        string? rtxVersion = null, normalsVersion = null, opusVersion = null;
        VersionSource rtxSource = VersionSource.Remote;
        VersionSource normalsSource = VersionSource.Remote;
        VersionSource opusSource = VersionSource.Remote;
        bool anyRemoteSuccess = false;

        if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            try
            {
                var remoteManifests = await FetchRemoteManifests();
                if (remoteManifests.HasValue)
                {
                    var (rtxManifest, normalsManifest, opusManifest) = remoteManifests.Value;

                    if (rtxManifest != null)
                    {
                        rtxVersion = ExtractVersionFromManifest(rtxManifest);
                        rtxSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }

                    if (normalsManifest != null)
                    {
                        normalsVersion = ExtractVersionFromManifest(normalsManifest);
                        normalsSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }

                    if (opusManifest != null)
                    {
                        opusVersion = ExtractVersionFromManifest(opusManifest);
                        opusSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to fetch remote versions: {ex.Message}");
            }
        }

        var cacheInfo = GetCacheInfo();
        if (cacheInfo.exists && File.Exists(cacheInfo.path))
        {
            try
            {
                var zipballVersions = await GetVersionsFromCachedZipball(cacheInfo.path!);
                if (zipballVersions.HasValue)
                {
                    if (rtxVersion == null && zipballVersions.Value.rtx != null)
                    {
                        rtxVersion = zipballVersions.Value.rtx;
                        rtxSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX version");
                    }

                    if (normalsVersion == null && zipballVersions.Value.normals != null)
                    {
                        normalsVersion = zipballVersions.Value.normals;
                        normalsSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Normals version");
                    }

                    if (opusVersion == null && zipballVersions.Value.opus != null)
                    {
                        opusVersion = zipballVersions.Value.opus;
                        opusSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Opus version");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to read zipball versions: {ex.Message}");
            }
        }

        if (anyRemoteSuccess)
        {
            var cacheObj = new JsonObject();

            if (rtxVersion != null)
            {
                cacheObj["rtx"] = new JsonObject
                {
                    ["version"] = rtxVersion,
                    ["source"] = rtxSource.ToString()
                };
            }

            if (normalsVersion != null)
            {
                cacheObj["normals"] = new JsonObject
                {
                    ["version"] = normalsVersion,
                    ["source"] = normalsSource.ToString()
                };
            }

            if (opusVersion != null)
            {
                cacheObj["opus"] = new JsonObject
                {
                    ["version"] = opusVersion,
                    ["source"] = opusSource.ToString()
                };
            }

            localSettings.Values[cacheKey] = cacheObj.ToJsonString();
            localSettings.Values[timeKey] = now.ToString("o");
        }

        return (
            (rtxVersion, rtxSource),
            (normalsVersion, normalsSource),
            (opusVersion, opusSource)
        );
    }

    private VersionSource ParseVersionSource(string? sourceString)
    {
        if (string.IsNullOrEmpty(sourceString))
            return VersionSource.Remote;

        return Enum.TryParse<VersionSource>(sourceString, out var source)
            ? source
            : VersionSource.Remote;
    }

    /// <summary>
    /// Restates a stored reading's provenance for the fact that it is now being served from the
    /// few-minute LocalSettings cache rather than freshly fetched.
    ///
    /// Without this the cache branch echoed back whatever it stored - always <c>Remote</c> - so
    /// <see cref="VersionSource.CachedRemote"/> was never produced anywhere in the app and the
    /// "(You seem up-to-date)" wording that hangs off it in PackUpdaterWindow was unreachable.
    ///
    /// <see cref="VersionSource.ZipballFallback"/> deliberately keeps its own provenance: that a
    /// reading came off the offline zipball is the more useful thing to tell the user, and it
    /// stays true no matter how many times it is re-served from cache.
    /// </summary>
    private static VersionSource AsCached(VersionSource stored) =>
        stored == VersionSource.Remote ? VersionSource.CachedRemote : stored;

    /// <summary>
    /// Drops the cached zipball, but only when it is genuinely behind the remote versions handed
    /// in. Returns whether it dropped anything.
    ///
    /// This closes a gap that <see cref="ValidateCacheAgainstRemote"/> alone cannot: that one is
    /// behind a 55-minute cooldown, so a cache that goes stale inside that window would deploy
    /// stale. This runs whenever the updater window refreshes, with no cooldown of its own,
    /// because it costs nothing to run - the remote numbers are the caller's already-fetched
    /// ones (so no request), and the cache's own numbers are read off the zipball already on
    /// disk. Nothing here touches the network.
    ///
    /// The thing to keep straight - and what the previous version of this got wrong - is that
    /// the question is cache-versus-remote and nothing else. The caller used to trigger on
    /// installed-versus-remote and then invalidate unconditionally, which threw away a perfectly
    /// current zipball whenever the user merely happened to be running an older pack, costing an
    /// ~11MB re-download and a GitHub hit to replace a file that was already correct.
    /// </summary>
    public async Task<bool> InvalidateCacheIfStaleAsync(string? remoteVanillaRTX, string? remoteNormals, string? remoteOpus)
    {
        try
        {
            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || string.IsNullOrEmpty(cacheInfo.path))
                return false; // Nothing cached, nothing to drop.

            var cached = await GetVersionsFromCachedZipball(cacheInfo.path!);

            if (cached == null)
            {
                // A cache we can't read versions out of can't be trusted to install from either.
                Trace.WriteLine("📦 Cached zipball unreadable - invalidating");
                InvalidateCache();
                return true;
            }

            var packs = new[]
            {
                (cached: cached.Value.rtx,     remote: remoteVanillaRTX),
                (cached: cached.Value.normals, remote: remoteNormals),
                (cached: cached.Value.opus,    remote: remoteOpus)
            };

            // A pack the remote has and the cache doesn't counts as stale, same as one the cache
            // has an older copy of. A remote we couldn't read says nothing either way, so skip it.
            var stale = packs.Any(p =>
                !string.IsNullOrEmpty(p.remote) &&
                (string.IsNullOrEmpty(p.cached) || IsRemoteVersionNewerThanInstalled(p.cached, p.remote)));

            if (!stale)
                return false;

            Trace.WriteLine("📦 Cached zipball is behind the remote - invalidating");
            InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            // Failing to verify is not a reason to throw away a cache that may well be fine.
            Trace.WriteLine($"[PackUpdater] Staleness check failed, keeping cache: {ex.Message}");
            return false;
        }
    }

    // ======================= Update Availability (for callers outside the updater window) =======================

    /// <summary>
    /// Compares what is installed against the remote versions and returns whether that is worth
    /// telling the user about. Built for MainWindow, which has no updater UI of its own to show
    /// this in and just wants to know whether to mention it once.
    ///
    /// Reads remote versions through <see cref="GetRemoteVersionsAsync"/>, so it shares the same
    /// few-minute cache the updater window already fills - calling this on every pack re-locate
    /// costs a GitHub request at most once per cache window, not once per call.
    ///
    /// Silence is the default: a remote that can't be reached, a version that can't be read, or
    /// an install that is simply current all return <see cref="PackUpdateNotice.None"/>. Nothing
    /// about failing to check is worth interrupting someone over.
    ///
    /// Installed versions are passed in rather than located here - the caller has just done that
    /// work, and repeating a filesystem sweep to re-learn what it already knows would be waste.
    /// </summary>
    /// <returns>
    /// The verdict, plus how many of the three packs are behind - which the caller needs in order
    /// to get "update" versus "updates" right.
    /// </returns>
    public async Task<(PackUpdateNotice Notice, int OutdatedCount)> GetUpdateNoticeAsync(
        string? installedVanillaRTX,
        string? installedNormals,
        string? installedOpus)
    {
        try
        {
            var remote = await GetRemoteVersionsAsync();

            var packs = new[]
            {
                (installed: installedVanillaRTX, available: remote.rtx.version),
                (installed: installedNormals,    available: remote.normals.version),
                (installed: installedOpus,       available: remote.opus.version)
            };

            // Nothing readable came back for any of the three - offline, rate-limited, malformed
            // manifest, doesn't matter. We know nothing, so we say nothing.
            if (packs.All(p => string.IsNullOrEmpty(p.available)))
                return (PackUpdateNotice.None, 0);

            if (packs.All(p => string.IsNullOrEmpty(p.installed)))
                return (PackUpdateNotice.NothingInstalled, 0);

            var outdated = packs.Count(p =>
                !string.IsNullOrEmpty(p.installed) &&
                !string.IsNullOrEmpty(p.available) &&
                IsRemoteVersionNewerThanInstalled(p.installed, p.available));

            return outdated > 0
                ? (PackUpdateNotice.UpdatesAvailable, outdated)
                : (PackUpdateNotice.None, 0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdater] Update-notice check failed: {ex.Message}");
            return (PackUpdateNotice.None, 0);
        }
    }

    private async Task<(string? rtx, string? normals, string? opus)?> GetVersionsFromCachedZipball(string cachePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachePath);
            var cachedPacks = await FindPacksInZip(archive);

            string? VersionOf(PackType packType) =>
                cachedPacks.TryGetValue(packType, out var found) ? found.Manifest.VersionDisplay : null;

            return (VersionOf(PackType.VanillaRTX), VersionOf(PackType.VanillaRTXNormals), VersionOf(PackType.VanillaRTXOpus));
        }
        catch
        {
            return null;
        }
    }

    // ======================= Cooldown Management =======================

    public void ResetCacheCheckCooldown()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[LastCacheCheckKey] = null;
    }

    public void ResetRemoteVersionCache()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[RemoteVersionsCacheKey] = null;
        localSettings.Values[RemoteVersionsCacheTimeKey] = null;
    }

    // ======================= Helper Methods =======================

    // Every manifest this class reads now goes through PackManifest.cs - one tolerant parser
    // (comments, trailing commas, duplicate keys, raw control characters in strings) and one
    // definition of "header UUID", "module UUID" and "version", shared with PackLocator,
    // PackBrowser, ExpImpDel, BetterRTXManager and Alchitex. What this class vets is unchanged
    // and is now stated in one place: header.uuid + modules[0].uuid for identity,
    // header.version for freshness.
    //
    // ParseJsonObject survives only for the remote-versions cache below, which is a payload
    // this class writes to LocalSettings itself - not a manifest.
    private static JsonObject? ParseJsonObject(string json) => MinecraftJson.ParseObject(json);

    /// <summary>
    /// Cache-vs-remote comparison, and the only thing this class compares: header.version.
    /// A version either side can't be read as an integer array counts as "newer", so an
    /// unreadable manifest re-downloads rather than pinning the user to a stale cache.
    /// </summary>
    private bool IsRemoteVersionNewer(PackManifest cachedManifest, PackManifest remoteManifest)
    {
        var cachedVersion = cachedManifest.VersionArray;
        var remoteVersion = remoteManifest.VersionArray;

        if (cachedVersion == null || remoteVersion == null) return true;

        return CompareVersionArrays(remoteVersion, cachedVersion) > 0;
    }

    /// <summary>
    /// Scans a cached zipball for pack manifests, identifying each by its header + module UUID
    /// pair - never by folder name. A GitHub codeload zipball wraps the whole repo in a
    /// branch/commit-named folder, so manifests sit one level inside that (depth 2 from the
    /// archive root); depth 1 is included too in case a future zipball ever drops the wrapper.
    /// This is the single place that turns "what's in the zip" into "which of our three packs
    /// is this", and every other method in this class that needs that answer goes through it.
    /// </summary>
    private async Task<Dictionary<PackType, (ZipArchiveEntry Entry, PackManifest Manifest)>> FindPacksInZip(ZipArchive archive)
    {
        var found = new Dictionary<PackType, (ZipArchiveEntry, PackManifest)>();

        foreach (var entry in Helpers.FindZipEntriesAtDepth(archive, PackManifest.ModernFileName, minDepth: 1, maxDepth: 2))
        {
            var manifest = await ReadManifestFromZipEntry(entry);
            if (manifest?.HeaderUuid == null || manifest.FirstModuleUuid == null) continue;

            var packType = IdentifyPackType(manifest.HeaderUuid, manifest.FirstModuleUuid);
            if (packType.HasValue)
            {
                found[packType.Value] = (entry, manifest);
            }
        }

        return found;
    }

    private static PackType? IdentifyPackType(string headerUUID, string moduleUUID)
    {
        if (headerUUID == VANILLA_RTX_HEADER_UUID && moduleUUID == VANILLA_RTX_MODULE_UUID)
            return PackType.VanillaRTX;
        if (headerUUID == VANILLA_RTX_NORMALS_HEADER_UUID && moduleUUID == VANILLA_RTX_NORMALS_MODULE_UUID)
            return PackType.VanillaRTXNormals;
        if (headerUUID == VANILLA_RTX_OPUS_HEADER_UUID && moduleUUID == VANILLA_RTX_OPUS_MODULE_UUID)
            return PackType.VanillaRTXOpus;
        return null;
    }

    private static async Task<PackManifest?> ReadManifestFromZipEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return PackManifest.Parse(json, sourcePath: entry.FullName);
        }
        catch
        {
            return null;
        }
    }

    private string GetPackFolderShortName(PackType packType) => packType switch
    {
        PackType.VanillaRTX => "vrtx",
        PackType.VanillaRTXNormals => "vrtxn",
        PackType.VanillaRTXOpus => "vrtxo",
        _ => "pack"
    };

    private async Task<(PackManifest? rtx, PackManifest? normals, PackManifest? opus)?> FetchRemoteManifests()
    {
        async Task<PackManifest?> TryFetchManifest(string url)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = await Helpers.UpdaterHttpClient.GetStringAsync(url, cts.Token);
                return PackManifest.Parse(response, sourcePath: url);
            }
            catch
            {
                return null;
            }
        }

        var rtxTask = TryFetchManifest(VANILLA_RTX_MANIFEST_URL);
        var normalsTask = TryFetchManifest(VANILLA_RTX_NORMALS_MANIFEST_URL);
        var opusTask = TryFetchManifest(VANILLA_RTX_OPUS_MANIFEST_URL);

        await Task.WhenAll(rtxTask, normalsTask, opusTask);

        var rtx = await rtxTask;
        var normals = await normalsTask;
        var opus = await opusTask;

        if (rtx == null && normals == null && opus == null)
        {
            return null;
        }

        return (rtx, normals, opus);
    }

    private async Task<(bool Success, string? Path)> DownloadLatestPackage()
    {
        try
        {
            Trace.WriteLine("📦 Downloading latest zipball from GitHub...");
            return await Helpers.Download(VANILLA_RTX_REPO_ZIPBALL_URL);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Download error: {ex.Message}");
            return (false, null);
        }
    }

    // ======================= Deploy Package =======================

    private async Task<bool> DeployPackage(string packagePath, PackType? targetPack = null, bool enableEnhancements = true)
    {
        if (Helpers.IsMinecraftRunning() && Helpers.RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Trace.WriteLine("⚠️ Minecraft is running. Please close the game while using the app.");
        }

        bool anyPackDeployed = false;
        string? tempExtractionDir = null;
        string? resourcePackPath = null;

        try
        {
            var versionName = MinecraftUserDataLocator.GetVersionDisplayName(EnvironmentVariables.Persistent.IsTargetingPreview);

            if (!MinecraftUserDataLocator.IsDataValid(EnvironmentVariables.Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"❌ {versionName} data root not found. Please make sure the game is installed or has been launched at least once.");
                return false;
            }

            resourcePackPath = MinecraftUserDataLocator.GetResourcePacksPath(
                EnvironmentVariables.Persistent.IsTargetingPreview,
                development: InstallToDevelopmentFolder,
                createIfMissing: true);

            if (string.IsNullOrEmpty(resourcePackPath))
            {
                Trace.WriteLine("❌ Could not access or create the resource packs directory.");
                return false;
            }

            Trace.WriteLine("📁 Resource pack directory ready.");

            tempExtractionDir = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractionDir);

            var packsToProcess = new List<(string uuid, string moduleUuid, string finalName, string displayName, PackType packType)>();

            // Targeted extraction: identify pack folders inside the zip by manifest UUID (never
            // by name, see FindPacksInZip), then pull only the matched folder(s) out to disk -
            // the other pack(s) and the rest of the repo (README, .github, etc.) are never
            // extracted at all, rather than extracting the whole zipball just to move one folder.
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var foundPacks = await FindPacksInZip(archive);

                foreach (var (packType, found) in foundPacks)
                {
                    if (targetPack.HasValue && packType != targetPack.Value) continue;
                    packsToProcess.Add((found.Manifest.HeaderUuid!, found.Manifest.FirstModuleUuid!, GetPackFolderShortName(packType), GetPackDisplayName(packType), packType));
                }

                if (packsToProcess.Count == 0)
                {
                    Trace.WriteLine(targetPack.HasValue
                        ? $"❌ {GetPackDisplayName(targetPack.Value)} not found in the cached package."
                        : "❌ No recognized Vanilla RTX packs found in the cached package.");
                    return false;
                }

                Trace.WriteLine($"📦 Found {packsToProcess.Count} pack(s) to install: {string.Join(", ", packsToProcess.Select(p => p.displayName))}");

                foreach (var pack in packsToProcess)
                {
                    var found = foundPacks[pack.packType];
                    var zipFolderPrefix = found.Entry.FullName.Substring(0, found.Entry.FullName.Length - found.Entry.Name.Length);
                    var destFolder = Path.Combine(tempExtractionDir, pack.finalName);
                    Directory.CreateDirectory(destFolder);

                    // Same entry-to-path resolution ExpImpDel's import uses (Helpers.EnumerateZipFolderExtraction) -
                    // only the write strategy differs, since this method already runs off the UI
                    // thread via its caller's own Task.Run and doesn't need ExpImpDel's per-file one.
                    foreach (var (entry, targetPath, isDirectory) in Helpers.EnumerateZipFolderExtraction(archive, zipFolderPrefix, destFolder))
                    {
                        if (isDirectory)
                        {
                            Directory.CreateDirectory(targetPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        entry.ExtractToFile(targetPath, overwrite: true);
                    }
                }
            }

            Trace.WriteLine("📦 Extracted targeted pack folder(s) from cached zipball");

            foreach (var pack in packsToProcess)
            {
                try
                {
                    Trace.WriteLine($"🔄 Processing {pack.displayName}...");

                    await DeleteExistingPackByUUID(resourcePackPath, pack.uuid, pack.moduleUuid, pack.displayName);

                    var finalDestination = GetSafeDirectoryName(resourcePackPath, pack.finalName);
                    var extractedPackPath = Path.Combine(tempExtractionDir, pack.finalName);
                    Directory.Move(extractedPackPath, finalDestination);

                    if (enableEnhancements)
                    {
                        ProcessEnhancementFolders(finalDestination);
                    }
                    else
                    {
                        RemoveEnhancementsFolder(finalDestination);
                    }

                    Helpers.GenerateBookkeepingFiles(finalDestination);

                    Trace.WriteLine($"✅ {pack.displayName} deployed successfully");
                    anyPackDeployed = true;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"❌ Failed to deploy {pack.displayName}: {ex.Message}");
                }
            }

            return anyPackDeployed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Deployment error: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempExtractionDir != null && Directory.Exists(tempExtractionDir))
            {
                try
                {
                    ForceWritable(tempExtractionDir);
                    Directory.Delete(tempExtractionDir, true);
                    Trace.WriteLine(anyPackDeployed ? "🧹 Cleaned up" : "🧹 Cleaned up after fail");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠️ Failed to clean up temp directory: {ex.Message}");
                }
            }

            if (resourcePackPath != null)
            {
                CleanupOrphanedDirectories(resourcePackPath);
            }
        }
    }

    private void CleanupOrphanedDirectories(string resourcePackPath)
    {
        var pathsToCleanOrphans = new List<string> { resourcePackPath };

        if (CleanUpTheOtherFolder)
        {
            var dirInfo = new DirectoryInfo(resourcePackPath);
            string opposingPath = InstallToDevelopmentFolder
                ? dirInfo.Name.Equals("development_resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "development_resource_packs")
                    : resourcePackPath;

            if (Directory.Exists(opposingPath))
            {
                pathsToCleanOrphans.Add(opposingPath);
            }
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-1);

        foreach (var pathToClean in pathsToCleanOrphans)
        {
            try
            {
                var orphanedDirs = Directory.GetDirectories(pathToClean, "__rtxapp_*", SearchOption.TopDirectoryOnly)
                    .Where(d => Directory.GetCreationTimeUtc(d) < cutoff);

                foreach (var dir in orphanedDirs)
                {
                    try
                    {
                        ForceWritable(dir);
                        Directory.Delete(dir, true);
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    // ======================= Enhancement Methods =======================

    private void RemoveEnhancementsFolder(string rootDirectory)
    {
        if (string.IsNullOrEmpty(EnhancementFolderName)) return;

        try
        {
            var enhancementFolders = Directory.GetDirectories(rootDirectory, EnhancementFolderName, SearchOption.AllDirectories);

            foreach (var enhancementPath in enhancementFolders)
            {
                try
                {
                    ForceWritable(enhancementPath);
                    Directory.Delete(enhancementPath, true);
                    Trace.WriteLine("🗑️ Removed enhancements folder (toggle was OFF)");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to remove enhancement folder ({enhancementPath}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error during enhancement folder removal: {ex.Message}");
        }
    }

    private void ProcessEnhancementFolders(string rootDirectory)
    {
        if (string.IsNullOrEmpty(EnhancementFolderName)) return;

        var enhancementFolders = Directory.GetDirectories(rootDirectory, EnhancementFolderName, SearchOption.AllDirectories);
        int processed = 0, failed = 0, deleteIssues = 0;

        foreach (var enhancementPath in enhancementFolders)
        {
            try
            {
                var parentDirectory = Directory.GetParent(enhancementPath)!.FullName;

                deleteIssues += MoveDirectoryContents(enhancementPath, parentDirectory);

                try
                {
                    Directory.Delete(enhancementPath, false);
                }
                catch
                {
                    deleteIssues++;
                }

                processed++;
            }
            catch (Exception ex)
            {
                failed++;
                Trace.WriteLine($"Enhancement folder error ({enhancementPath}): {ex.Message}");
            }
        }

        if (processed + failed > 0)
        {
            var msg = failed == 0
                ? $"✨ Enabled Enhancements"
                : $"⚠️ Processing {processed} failed {failed}. Delete failures: {deleteIssues}";

            Trace.WriteLine(msg);
        }
    }

    private int MoveDirectoryContents(string sourceDir, string targetDir)
    {
        int deleteFailures = 0;

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));

            if (File.Exists(destFile))
            {
                try { File.Delete(destFile); }
                catch { deleteFailures++; continue; }
            }

            try { File.Move(file, destFile); }
            catch { deleteFailures++; }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));

            if (Directory.Exists(destSubDir))
            {
                deleteFailures += MoveDirectoryContents(subDir, destSubDir);

                try { Directory.Delete(subDir, false); }
                catch { deleteFailures++; }
            }
            else
            {
                try { Directory.Move(subDir, destSubDir); }
                catch { deleteFailures++; }
            }
        }

        return deleteFailures;
    }

    // ======================= Cache & Utility Methods =======================

    public async Task<bool> DoesPackExistInCache(PackType packType)
    {
        var cacheInfo = GetCacheInfo();
        if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(cacheInfo.path!);
            var cachedPacks = await FindPacksInZip(archive);
            return cachedPacks.ContainsKey(packType);
        }
        catch
        {
            return false;
        }
    }

    private string GetPackDisplayName(PackType packType)
    {
        return packType switch
        {
            PackType.VanillaRTX => "Vanilla RTX",
            PackType.VanillaRTXNormals => "Vanilla RTX Normals",
            PackType.VanillaRTXOpus => "Vanilla RTX Opus",
            _ => "Unknown Pack"
        };
    }

    private string? ExtractVersionFromManifest(PackManifest? manifest) => manifest?.VersionDisplay;

    public bool IsRemoteVersionNewerThanInstalled(string? installedVersionString, string? remoteVersionString)
    {
        try
        {
            if (string.IsNullOrEmpty(installedVersionString) || string.IsNullOrEmpty(remoteVersionString))
                return false;

            var installedVersion = ParseVersionString(installedVersionString);
            var remoteVersion = ParseVersionString(remoteVersionString);

            if (installedVersion == null || remoteVersion == null)
                return false;

            return CompareVersionArrays(remoteVersion, installedVersion) > 0;
        }
        catch
        {
            return false;
        }
    }

    private int[]? ParseVersionString(string versionString)
    {
        try
        {
            if (string.IsNullOrEmpty(versionString))
                return null;

            versionString = versionString.TrimStart('v', 'V');

            var parts = versionString.Split('.');
            return parts.Select(int.Parse).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static int CompareVersionArrays(int[] versionA, int[] versionB)
    {
        for (int i = 0; i < Math.Max(versionA.Length, versionB.Length); i++)
        {
            int a = i < versionA.Length ? versionA[i] : 0;
            int b = i < versionB.Length ? versionB[i] : 0;

            if (a > b) return 1;
            if (a < b) return -1;
        }
        return 0;
    }

    private string GetSafeDirectoryName(string parentPath, string desiredName)
    {
        var fullPath = Path.Combine(parentPath, desiredName);

        if (!Directory.Exists(fullPath))
            return fullPath;

        if (Directory.GetFileSystemEntries(fullPath).Length == 0)
        {
            Directory.Delete(fullPath);
            return fullPath;
        }

        int suffix = 1;
        string safeName;
        do
        {
            safeName = Path.Combine(parentPath, $"{desiredName}{suffix}");
            suffix++;
        } while (Directory.Exists(safeName) && Directory.GetFileSystemEntries(safeName).Length > 0);

        if (Directory.Exists(safeName))
            Directory.Delete(safeName);

        return safeName;
    }

    private async Task DeleteExistingPackByUUID(string resourcePackPath, string targetHeaderUUID, string targetModuleUUID, string packName)
    {
        var pathsToClean = new List<string> { resourcePackPath };

        if (CleanUpTheOtherFolder)
        {
            var dirInfo = new DirectoryInfo(resourcePackPath);

            string opposingPath = InstallToDevelopmentFolder
                ? dirInfo.Name.Equals("development_resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "development_resource_packs")
                    : resourcePackPath;

            if (Directory.Exists(opposingPath))
            {
                pathsToClean.Add(opposingPath);
            }
        }

        foreach (var pathToClean in pathsToClean)
        {
            var currentManifests = Directory.GetFiles(pathToClean, PackManifest.ModernFileName, SearchOption.AllDirectories)
                .Where(m => !Path.GetDirectoryName(m)!.Contains("__rtxapp_"));

            foreach (var manifestPath in currentManifests)
            {
                var uuids = await ReadManifestUUIDs(manifestPath);
                if (uuids == null) continue;

                var (headerUUID, moduleUUID) = uuids.Value;
                if (headerUUID.Equals(targetHeaderUUID, StringComparison.OrdinalIgnoreCase) &&
                    moduleUUID.Equals(targetModuleUUID, StringComparison.OrdinalIgnoreCase))
                {
                    var topLevelFolder = GetTopLevelFolderForManifest(manifestPath, pathToClean);
                    if (topLevelFolder != null && Directory.Exists(topLevelFolder))
                    {
                        ForceWritable(topLevelFolder);
                        Directory.Delete(topLevelFolder, true);
                        Trace.WriteLine($"🗑️ Removed previous installation of: {packName}");
                    }
                }
            }
        }
    }

    private void ForceWritable(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;

        if ((di.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
            di.Attributes &= ~System.IO.FileAttributes.ReadOnly;

        foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
        {
            if ((file.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                file.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }

        foreach (var dir in di.GetDirectories("*", SearchOption.AllDirectories))
        {
            if ((dir.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                dir.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }
    }

    private async Task<(string headerUUID, string moduleUUID)?> ReadManifestUUIDs(string manifestPath)
    {
        try
        {
            var manifest = await PackManifest.FromFileAsync(manifestPath);
            if (manifest == null) return null;

            string? headerUUID = manifest.HeaderUuid;
            string? moduleUUID = manifest.FirstModuleUuid;

            if (headerUUID == null || moduleUUID == null)
                return null;

            return (headerUUID, moduleUUID);
        }
        catch
        {
            return null;
        }
    }

    private (bool exists, string? path) GetCacheInfo()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var cachedPath = localSettings.Values["CachedZipballPath"] as string;
        bool exists = !string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath);
        if (exists)
        {
            try
            {
                using (ZipFile.OpenRead(cachedPath!)) { }
            }
            catch
            {
                Trace.WriteLine("⚠️ Cached package is corrupted, proceeding as if no cache was available.");
                exists = false;
                cachedPath = null;
            }
        }
        return (exists, cachedPath);
    }

    private void SaveCachedZipballPath(string path)
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values["CachedZipballPath"] = path;

        RefreshDeployableCacheState();
    }

    public bool HasDeployableCache()
    {
        var (exists, _) = GetCacheInfo();
        return exists;
    }

    private string? GetTopLevelFolderForManifest(string manifestPath, string resourcePackPath)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath);
        var resourcePackDir = new DirectoryInfo(resourcePackPath);
        var currentDir = manifestDir != null ? new DirectoryInfo(manifestDir) : null;

        while (currentDir != null && currentDir.Parent != null)
        {
            if (currentDir.Parent.FullName.Equals(resourcePackDir.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return currentDir.FullName;
            }
            currentDir = currentDir.Parent;
        }

        return null;
    }
}
