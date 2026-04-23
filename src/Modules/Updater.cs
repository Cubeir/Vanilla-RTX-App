using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Storage;
using static Vanilla_RTX_App.Modules.PackLocator; // For static UUIDs, they are stored there for locating packs

namespace Vanilla_RTX_App.Modules;

/// =====================================================================================================================
/// Only deals with cache, we don't care if user has Vanilla RTX installed or not, we compare versions of cache to remote
/// No cache? download latest, cache outdated? download latest, if there's a cache and the rest fails for whatever the reason, fallback to cache
/// Deployment deletes any pack that matches UUIDs as defined at the begenning of PackLocator class
/// =====================================================================================================================

public enum PackType { VanillaRTX, VanillaRTXNormals, VanillaRTXOpus }

public enum VersionSource
{
    Remote,           // Fresh from GitHub
    CachedRemote,     // From 5-min cache of remote versions
    ZipballFallback   // Read from cached zipball when remote unavailable
}

public class PackUpdater
{
    private const string VANILLA_RTX_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX/manifest.json";
    private const string VANILLA_RTX_NORMALS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Normals/manifest.json";
    private const string VANILLA_RTX_OPUS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Opus/manifest.json";
    private const string VANILLA_RTX_REPO_ZIPBALL_URL = "https://github.com/Cubeir/Vanilla-RTX/archive/refs/heads/master.zip";

    public event Action<string>? ProgressUpdate;
    private readonly List<string> _logMessages = new();

    private bool _installationInProgress = false;
    private PackType? _currentInstallingPack = null;

    // Remote version cache
    private const string RemoteVersionsCacheKey_Release = "RemoteVersionsCache_Release";
    private const string RemoteVersionsCacheKey_Preview = "RemoteVersionsCache_Preview";
    private const string RemoteVersionsCacheTimeKey_Release = "RemoteVersionsCacheTime_Release";
    private const string RemoteVersionsCacheTimeKey_Preview = "RemoteVersionsCacheTime_Preview";
    private static readonly TimeSpan RemoteVersionCacheDuration = TimeSpan.FromMinutes(10);

    // Cache validation check cooldown
    private const string LastCacheCheckKey_Release = "LastCacheValidationCheck_Release";
    private const string LastCacheCheckKey_Preview = "LastCacheValidationCheck_Preview";
    private static readonly TimeSpan CacheCheckCooldown = TimeSpan.FromMinutes(60);

    public string EnhancementFolderName { get; set; } = "__enhancements";
    public bool InstallToDevelopmentFolder { get; set; } = false;
    public bool CleanUpTheOtherFolder { get; set; } = true;

    private string GetRemoteVersionsCacheKey() => TunerVariables.Persistent.IsTargetingPreview
        ? RemoteVersionsCacheKey_Preview : RemoteVersionsCacheKey_Release;

    private string GetRemoteVersionsCacheTimeKey() => TunerVariables.Persistent.IsTargetingPreview
        ? RemoteVersionsCacheTimeKey_Preview : RemoteVersionsCacheTimeKey_Release;

    private string GetLastCacheCheckKey() => TunerVariables.Persistent.IsTargetingPreview
        ? LastCacheCheckKey_Preview : LastCacheCheckKey_Release;

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
        var checkKey = GetLastCacheCheckKey();

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

        (JObject? rtx, JObject? normals, JObject? opus)? remote = null;

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

        bool needsInvalidation = await DoesCacheNeedUpdate(cacheInfo.path, remote.Value);

        if (needsInvalidation)
        {
            Trace.WriteLine("📦 Cache is outdated - invalidating now");
            InvalidateCache();
            return true;
        }

        Trace.WriteLine("✅ Cache is up-to-date");
        return false;
    }

    private async Task<bool> DoesCacheNeedUpdate(string cachedPath, (JObject? rtx, JObject? normals, JObject? opus) remoteManifests)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachedPath);

            async Task<JObject?> TryReadManifest(string partialPath)
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith(partialPath, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                return JObject.Parse(json);
            }

            var rtxManifest = await TryReadManifest("Vanilla-RTX/manifest.json");
            var normalsManifest = await TryReadManifest("Vanilla-RTX-Normals/manifest.json");
            var opusManifest = await TryReadManifest("Vanilla-RTX-Opus/manifest.json");

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

    public async Task<(bool Success, List<string> Logs)> UpdateSinglePackAsync(PackType packType, bool enableEnhancements)
    {
        _logMessages.Clear();

        // Check if another installation is already running
        if (IsInstallationInProgress())
        {
            Trace.WriteLine("⚠️ Another installation is already in progress");
            return (false, new List<string>(_logMessages));
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
                    return (false, new List<string>(_logMessages));
                }

                SaveCachedZipballPath(downloadPath);
                cacheInfo = (true, downloadPath);
            }

            Trace.WriteLine("✅ Using cached zipball for deployment");
            var deploySuccess = await DeployPackage(cacheInfo.path, packType, enableEnhancements);
            return (deploySuccess, new List<string>(_logMessages));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Unexpected error: {ex.Message}");
            return (false, new List<string>(_logMessages));
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
        var cacheKey = GetRemoteVersionsCacheKey();
        var timeKey = GetRemoteVersionsCacheTimeKey();

        if (localSettings.Values[timeKey] is string cacheTimeStr &&
            DateTimeOffset.TryParse(cacheTimeStr, out var cacheTime) &&
            now < cacheTime + RemoteVersionCacheDuration)
        {
            if (localSettings.Values[cacheKey] is string cachedJson)
            {
                try
                {
                    var cached = JObject.Parse(cachedJson);
                    var rtxCached = cached["rtx"];
                    var normalsCached = cached["normals"];
                    var opusCached = cached["opus"];

                    return (
                        (rtxCached?["version"]?.ToString(),
                         ParseVersionSource(rtxCached?["source"]?.ToString())),
                        (normalsCached?["version"]?.ToString(),
                         ParseVersionSource(normalsCached?["source"]?.ToString())),
                        (opusCached?["version"]?.ToString(),
                         ParseVersionSource(opusCached?["source"]?.ToString()))
                    );
                }
                catch { /* Fall through */ }
            }
        }

        string rtxVersion = null, normalsVersion = null, opusVersion = null;
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
                var zipballVersions = await GetVersionsFromCachedZipball(cacheInfo.path);
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
            var cacheObj = new JObject();

            if (rtxVersion != null)
            {
                cacheObj["rtx"] = new JObject
                {
                    ["version"] = rtxVersion,
                    ["source"] = rtxSource.ToString()
                };
            }

            if (normalsVersion != null)
            {
                cacheObj["normals"] = new JObject
                {
                    ["version"] = normalsVersion,
                    ["source"] = normalsSource.ToString()
                };
            }

            if (opusVersion != null)
            {
                cacheObj["opus"] = new JObject
                {
                    ["version"] = opusVersion,
                    ["source"] = opusSource.ToString()
                };
            }

            localSettings.Values[cacheKey] = cacheObj.ToString();
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

    private async Task<(string? rtx, string? normals, string? opus)?> GetVersionsFromCachedZipball(string cachePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachePath);

            async Task<string?> TryReadVersion(string partialPath)
            {
                try
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith(partialPath, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var json = await reader.ReadToEndAsync();
                    var manifest = JObject.Parse(json);
                    return ExtractVersionFromManifest(manifest);
                }
                catch
                {
                    return null;
                }
            }

            var rtxVersion = await TryReadVersion("Vanilla-RTX/manifest.json");
            var normalsVersion = await TryReadVersion("Vanilla-RTX-Normals/manifest.json");
            var opusVersion = await TryReadVersion("Vanilla-RTX-Opus/manifest.json");

            return (rtxVersion, normalsVersion, opusVersion);
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
        localSettings.Values[GetLastCacheCheckKey()] = null;
    }

    public void ResetRemoteVersionCache()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[GetRemoteVersionsCacheKey()] = null;
        localSettings.Values[GetRemoteVersionsCacheTimeKey()] = null;
    }

    // ======================= Helper Methods =======================

    private bool IsRemoteVersionNewer(JObject cachedManifest, JObject remoteManifest)
    {
        try
        {
            var cachedVersion = cachedManifest["header"]?["version"]?.ToObject<int[]>();
            var remoteVersion = remoteManifest["header"]?["version"]?.ToObject<int[]>();

            if (cachedVersion == null || remoteVersion == null) return true;

            return CompareVersionArrays(remoteVersion, cachedVersion) > 0;
        }
        catch
        {
            return true;
        }
    }

    private async Task<(JObject? rtx, JObject? normals, JObject? opus)?> FetchRemoteManifests()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", $"vanilla_rtx_app_updater/{TunerVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)");

        async Task<JObject?> TryFetchManifest(string url)
        {
            try
            {
                var response = await client.GetStringAsync(url);
                return JObject.Parse(response);
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
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Trace.WriteLine("⚠️ Minecraft is running. Please close the game while using the app.");
        }

        bool anyPackDeployed = false;
        string tempExtractionDir = null;
        string resourcePackPath = null;

        try
        {
            string basePath = TunerVariables.Persistent.IsTargetingPreview
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock Preview")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock");

            if (!Directory.Exists(basePath))
            {
                Trace.WriteLine("❌ Minecraft data root not found. Please make sure the game is installed or has been launched at least once.");
                return false;
            }

            resourcePackPath = Path.Combine(basePath, "Users", "Shared", "games", "com.mojang", InstallToDevelopmentFolder ? "development_resource_packs" : "resource_packs");

            if (!Directory.Exists(resourcePackPath))
            {
                Directory.CreateDirectory(resourcePackPath);
                Trace.WriteLine("📁 Shared resources directory was missing and has been created.");
            }

            tempExtractionDir = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractionDir);

            ZipFile.ExtractToDirectory(packagePath, tempExtractionDir, overwriteFiles: true);
            Trace.WriteLine("📦 Extracted package to temporary directory");

            var extractedManifests = Directory.GetFiles(tempExtractionDir, "manifest.json", SearchOption.AllDirectories);

            var packsToProcess = new List<(string uuid, string moduleUuid, string sourcePath, string finalName, string displayName, PackType packType)>();

            foreach (var manifestPath in extractedManifests)
            {
                var uuids = await ReadManifestUUIDs(manifestPath);
                if (uuids == null) continue;

                var (headerUUID, moduleUUID) = uuids.Value;
                var packSourcePath = Path.GetDirectoryName(manifestPath);

                if (headerUUID == VANILLA_RTX_HEADER_UUID && moduleUUID == VANILLA_RTX_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtx", "Vanilla RTX", PackType.VanillaRTX));
                }
                else if (headerUUID == VANILLA_RTX_NORMALS_HEADER_UUID && moduleUUID == VANILLA_RTX_NORMALS_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtxn", "Vanilla RTX Normals", PackType.VanillaRTXNormals));
                }
                else if (headerUUID == VANILLA_RTX_OPUS_HEADER_UUID && moduleUUID == VANILLA_RTX_OPUS_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtxo", "Vanilla RTX Opus", PackType.VanillaRTXOpus));
                }
            }

            if (targetPack.HasValue)
            {
                packsToProcess = packsToProcess.Where(p => p.packType == targetPack.Value).ToList();
            }

            if (packsToProcess.Count == 0)
            {
                Trace.WriteLine(targetPack.HasValue
                    ? $"❌ {GetPackDisplayName(targetPack.Value)} not found in the downloaded package."
                    : "❌ No recognized Vanilla RTX packs found in the downloaded package.");
                return false;
            }

            Trace.WriteLine($"📦 Found {packsToProcess.Count} pack(s) to install: {string.Join(", ", packsToProcess.Select(p => p.displayName))}");

            foreach (var pack in packsToProcess)
            {
                try
                {
                    Trace.WriteLine($"🔄 Processing {pack.displayName}...");

                    await DeleteExistingPackByUUID(resourcePackPath, pack.uuid, pack.moduleUuid, pack.displayName);

                    var finalDestination = GetSafeDirectoryName(resourcePackPath, pack.finalName);
                    Directory.Move(pack.sourcePath, finalDestination);

                    if (enableEnhancements)
                    {
                        ProcessEnhancementFolders(finalDestination);
                    }
                    else
                    {
                        RemoveEnhancementsFolder(finalDestination);
                    }

                    try
                    {
                        Helpers.GenerateTexturesLists(finalDestination);
                        var contentsPath = Path.Combine(finalDestination, "contents.json");
                        if (File.Exists(contentsPath))
                        {
                            var attr = File.GetAttributes(contentsPath);
                            if ((attr & System.IO.FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(contentsPath, attr & ~System.IO.FileAttributes.ReadOnly);

                            File.Delete(contentsPath);
                        }
                        File.WriteAllText(contentsPath, "{}");
                    }
                    catch { Trace.WriteLine("Contents json or textures list creation failed."); }

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
                    ? Path.Combine(dirInfo.Parent.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent.FullName, "development_resource_packs")
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
            using var archive = ZipFile.OpenRead(cacheInfo.path);

            string manifestPath = packType switch
            {
                PackType.VanillaRTX => "Vanilla-RTX/manifest.json",
                PackType.VanillaRTXNormals => "Vanilla-RTX-Normals/manifest.json",
                PackType.VanillaRTXOpus => "Vanilla-RTX-Opus/manifest.json",
                _ => null
            };

            if (manifestPath == null) return false;

            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(manifestPath, StringComparison.OrdinalIgnoreCase));

            return entry != null;
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

    private string? ExtractVersionFromManifest(JObject? manifest)
    {
        try
        {
            if (manifest == null) return null;

            var versionArray = manifest["header"]?["version"]?.ToObject<int[]>();
            if (versionArray == null || versionArray.Length == 0) return null;

            return $"{string.Join(".", versionArray)}";
        }
        catch
        {
            return null;
        }
    }

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
                    ? Path.Combine(dirInfo.Parent.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent.FullName, "development_resource_packs")
                    : resourcePackPath;

            if (Directory.Exists(opposingPath))
            {
                pathsToClean.Add(opposingPath);
            }
        }

        foreach (var pathToClean in pathsToClean)
        {
            var currentManifests = Directory.GetFiles(pathToClean, "manifest.json", SearchOption.AllDirectories)
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
            var json = await File.ReadAllTextAsync(manifestPath);
            var data = JObject.Parse(json);

            string headerUUID = data["header"]?["uuid"]?.ToString();
            string moduleUUID = data["modules"]?[0]?["uuid"]?.ToString();

            return (headerUUID, moduleUUID);
        }
        catch
        {
            return null;
        }
    }

    private (bool exists, string path) GetCacheInfo()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var cachedPath = localSettings.Values["CachedZipballPath"] as string;
        bool exists = !string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath);
        if (exists)
        {
            try
            {
                using (ZipFile.OpenRead(cachedPath)) { }
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
    }

    public bool HasDeployableCache()
    {
        var (exists, _) = GetCacheInfo();
        return exists;
    }

    private string GetTopLevelFolderForManifest(string manifestPath, string resourcePackPath)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath);
        var resourcePackDir = new DirectoryInfo(resourcePackPath);
        var currentDir = new DirectoryInfo(manifestDir);

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
