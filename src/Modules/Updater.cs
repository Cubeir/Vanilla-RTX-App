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

    // Remote version cache
    private const string RemoteVersionsCacheKey_Release = "RemoteVersionsCache_Release";
    private const string RemoteVersionsCacheKey_Preview = "RemoteVersionsCache_Preview";
    private const string RemoteVersionsCacheTimeKey_Release = "RemoteVersionsCacheTime_Release";
    private const string RemoteVersionsCacheTimeKey_Preview = "RemoteVersionsCacheTime_Preview";
    private static readonly TimeSpan RemoteVersionCacheDuration = TimeSpan.FromMinutes(1);

    // Cache validation check cooldown
    private const string LastCacheCheckKey_Release = "LastCacheValidationCheck_Release";
    private const string LastCacheCheckKey_Preview = "LastCacheValidationCheck_Preview";
    private static readonly TimeSpan CacheCheckCooldown = TimeSpan.FromMinutes(90);

    public string EnhancementFolderName { get; set; } = "__enhancements";
    public bool InstallToDevelopmentFolder { get; set; } = false;
    public bool CleanUpTheOtherFolder { get; set; } = true;

    private string GetRemoteVersionsCacheKey() => TunerVariables.Persistent.IsTargetingPreview
        ? RemoteVersionsCacheKey_Preview : RemoteVersionsCacheKey_Release;

    private string GetRemoteVersionsCacheTimeKey() => TunerVariables.Persistent.IsTargetingPreview
        ? RemoteVersionsCacheTimeKey_Preview : RemoteVersionsCacheTimeKey_Release;

    private string GetLastCacheCheckKey() => TunerVariables.Persistent.IsTargetingPreview
        ? LastCacheCheckKey_Preview : LastCacheCheckKey_Release;

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
                LogMessage("🗑️ Deleted outdated cache file");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to delete cache file: {ex.Message}");
            }
        }

        localSettings.Values["CachedZipballPath"] = null;
        LogMessage("❌ Cache invalidated - will download fresh on next install");
    }

    // ======================= Cache Validation Check =======================

    public async Task<bool> ValidateCacheAgainstRemote()
    {
        var cacheInfo = GetCacheInfo();

        if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
        {
            LogMessage("📦 No cache exists - will download on first pack installation");
            return false;
        }

        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            LogMessage("🛜 No network available - will use existing cache");
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
                LogMessage($"⏳ Cache check on cooldown - {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} left");
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
            LogMessage($"⚠️ Failed to contact GitHub: {ex.Message}");
        }

        if (remote != null)
        {
            localSettings.Values[checkKey] = now.ToString("o");
        }
        else
        {
            LogMessage("⚠️ Could not validate cache - will use existing cache");
            return false;
        }

        bool needsInvalidation = await DoesCacheNeedUpdate(cacheInfo.path, remote.Value);

        if (needsInvalidation)
        {
            LogMessage("📦 Cache is outdated - invalidating now");
            InvalidateCache();
            return true;
        }

        LogMessage("✅ Cache is up-to-date");
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

            // Check Vanilla RTX
            if (remoteManifests.rtx != null)
            {
                if (rtxManifest == null)
                {
                    LogMessage("📦 Vanilla RTX is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(rtxManifest, remoteManifests.rtx))
                {
                    var cacheVer = ExtractVersionFromManifest(rtxManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.rtx);
                    LogMessage($"📦 Vanilla RTX: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (rtxManifest != null)
            {
                LogMessage("📦 Vanilla RTX exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            // Check Vanilla RTX Normals
            if (remoteManifests.normals != null)
            {
                if (normalsManifest == null)
                {
                    LogMessage("📦 Vanilla RTX Normals is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(normalsManifest, remoteManifests.normals))
                {
                    var cacheVer = ExtractVersionFromManifest(normalsManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.normals);
                    LogMessage($"📦 Vanilla RTX Normals: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (normalsManifest != null)
            {
                LogMessage("📦 Vanilla RTX Normals exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            // Check Vanilla RTX Opus
            if (remoteManifests.opus != null)
            {
                if (opusManifest == null)
                {
                    LogMessage("📦 Vanilla RTX Opus is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(opusManifest, remoteManifests.opus))
                {
                    var cacheVer = ExtractVersionFromManifest(opusManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.opus);
                    LogMessage($"📦 Vanilla RTX Opus: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (opusManifest != null)
            {
                LogMessage("📦 Vanilla RTX Opus exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (!anyOutdated)
            {
                LogMessage("✅ All packs in cache are up-to-date");
            }

            return anyOutdated;
        }
        catch (Exception ex)
        {
            LogMessage($"⚠️ Error reading cached zipball: {ex.Message} - invalidating cache");
            return true;
        }
    }

    // ======================= Individual Pack Installation =======================

    public async Task<(bool Success, List<string> Logs)> UpdateSinglePackAsync(PackType packType, bool enableEnhancements)
    {
        _logMessages.Clear();

        try
        {
            var packName = GetPackDisplayName(packType);
            LogMessage($"🔄 Starting installation for {packName}...");

            await ValidateCacheAgainstRemote();

            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
            {
                LogMessage("📦 No cache available - downloading now...");

                var (downloadSuccess, downloadPath) = await DownloadLatestPackage();
                if (!downloadSuccess || string.IsNullOrEmpty(downloadPath))
                {
                    LogMessage("❌ Download failed");
                    return (false, new List<string>(_logMessages));
                }

                SaveCachedZipballPath(downloadPath);
                cacheInfo = (true, downloadPath);
            }

            LogMessage("✅ Using cached zipball for deployment");
            var deploySuccess = await DeployPackage(cacheInfo.path, packType, enableEnhancements);
            return (deploySuccess, new List<string>(_logMessages));
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Unexpected error: {ex.Message}");
            return (false, new List<string>(_logMessages));
        }
    }

    // ======================= Remote Version Fetching (For UI Display) =======================

    public async Task<(string? rtx, string? normals, string? opus, VersionSource source)> GetRemoteVersionsAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var cacheKey = GetRemoteVersionsCacheKey();
        var timeKey = GetRemoteVersionsCacheTimeKey();

        // Step 1: Try cached remote versions (5-min cache)
        if (localSettings.Values[timeKey] is string cacheTimeStr &&
            DateTimeOffset.TryParse(cacheTimeStr, out var cacheTime) &&
            now < cacheTime + RemoteVersionCacheDuration)
        {
            if (localSettings.Values[cacheKey] is string cachedJson)
            {
                try
                {
                    var cached = JObject.Parse(cachedJson);
                    return (
                        cached["rtx"]?.ToString(),
                        cached["normals"]?.ToString(),
                        cached["opus"]?.ToString(),
                        VersionSource.CachedRemote
                    );
                }
                catch { /* Fall through */ }
            }
        }

        // Step 2: Try fetching fresh versions from remote
        string rtxVersion = null, normalsVersion = null, opusVersion = null;
        bool anyRemoteSuccess = false;

        if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            try
            {
                var remoteManifests = await FetchRemoteManifests();
                if (remoteManifests.HasValue)
                {
                    var (rtxManifest, normalsManifest, opusManifest) = remoteManifests.Value;

                    rtxVersion = ExtractVersionFromManifest(rtxManifest);
                    normalsVersion = ExtractVersionFromManifest(normalsManifest);
                    opusVersion = ExtractVersionFromManifest(opusManifest);

                    anyRemoteSuccess = true;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to fetch remote versions: {ex.Message}");
            }
        }

        // FIX #3: Per-pack zipball fallback for missing packs
        var cacheInfo = GetCacheInfo();
        if (cacheInfo.exists && File.Exists(cacheInfo.path))
        {
            try
            {
                var zipballVersions = await GetVersionsFromCachedZipball(cacheInfo.path);
                if (zipballVersions.HasValue)
                {
                    // Use zipball version for any pack that's null from remote
                    if (rtxVersion == null && zipballVersions.Value.rtx != null)
                    {
                        rtxVersion = zipballVersions.Value.rtx;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX version");
                    }

                    if (normalsVersion == null && zipballVersions.Value.normals != null)
                    {
                        normalsVersion = zipballVersions.Value.normals;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Normals version");
                    }

                    if (opusVersion == null && zipballVersions.Value.opus != null)
                    {
                        opusVersion = zipballVersions.Value.opus;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Opus version");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to read zipball versions: {ex.Message}");
            }
        }

        // Cache results if we got any from remote
        if (anyRemoteSuccess)
        {
            var cacheObj = new JObject
            {
                ["rtx"] = rtxVersion,
                ["normals"] = normalsVersion,
                ["opus"] = opusVersion
            };
            localSettings.Values[cacheKey] = cacheObj.ToString();
            localSettings.Values[timeKey] = now.ToString("o");
        }

        // Determine source: Remote if we got any from remote, ZipballFallback if all came from zipball
        var source = anyRemoteSuccess ? VersionSource.Remote : VersionSource.ZipballFallback;

        return (rtxVersion, normalsVersion, opusVersion, source);
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
            LogMessage("📦 Downloading latest zipball from GitHub...");
            return await Helpers.Download(VANILLA_RTX_REPO_ZIPBALL_URL);
        }
        catch (Exception ex)
        {
            LogMessage($"Download error: {ex.Message}");
            return (false, null);
        }
    }

    // ======================= Deploy Package =======================

    private async Task<bool> DeployPackage(string packagePath, PackType? targetPack = null, bool enableEnhancements = true)
    {
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            LogMessage("⚠️ Minecraft is running. Please close the game while using the app.");
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
                LogMessage("❌ Minecraft data root not found. Please make sure the game is installed or has been launched at least once.");
                return false;
            }

            resourcePackPath = Path.Combine(basePath, "Users", "Shared", "games", "com.mojang", InstallToDevelopmentFolder ? "development_resource_packs" : "resource_packs");

            if (!Directory.Exists(resourcePackPath))
            {
                Directory.CreateDirectory(resourcePackPath);
                LogMessage("📁 Shared resources directory was missing and has been created.");
            }

            tempExtractionDir = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractionDir);

            ZipFile.ExtractToDirectory(packagePath, tempExtractionDir, overwriteFiles: true);
            LogMessage("📦 Extracted package to temporary directory");

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
                LogMessage(targetPack.HasValue
                    ? $"❌ {GetPackDisplayName(targetPack.Value)} not found in the downloaded package."
                    : "❌ No recognized Vanilla RTX packs found in the downloaded package.");
                return false;
            }

            LogMessage($"📦 Found {packsToProcess.Count} pack(s) to install: {string.Join(", ", packsToProcess.Select(p => p.displayName))}");

            foreach (var pack in packsToProcess)
            {
                try
                {
                    LogMessage($"🔄 Processing {pack.displayName}...");

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

                    LogMessage($"✅ {pack.displayName} deployed successfully");
                    anyPackDeployed = true;
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Failed to deploy {pack.displayName}: {ex.Message}");
                }
            }

            return anyPackDeployed;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Deployment error: {ex.Message}");
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
                    LogMessage(anyPackDeployed ? "🧹 Cleaned up" : "🧹 Cleaned up after fail");
                }
                catch (Exception ex)
                {
                    LogMessage($"⚠️ Failed to clean up temp directory: {ex.Message}");
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
                    LogMessage("🗑️ Removed enhancements folder (toggle was OFF)");
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

            LogMessage(msg);
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
                        LogMessage($"🗑️ Removed previous installation of: {packName}");
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
                LogMessage("⚠️ Cached package is corrupted, proceeding as if no cache was available.");
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

    private void LogMessage(string message)
    {
        _logMessages.Add($"{message}");
        ProgressUpdate?.Invoke(message);
    }
}

/// =====================================================================================================================
/// Silently tries to update the credits from Vanilla RTX's readme -- any failure will result in null.
/// Cooldowns also result in null, check for null and don't show credits whereever this class is used.
/// =====================================================================================================================

public class CreditsUpdater
{
    private const string CREDITS_CACHE_KEY = "CreditsCache";
    private const string CREDITS_TIMESTAMP_KEY = "CreditsTimestamp";
    private const string CREDITS_LAST_SHOWN_KEY = "CreditsLastShown";
    private const string README_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/README.md";
    private const int CACHE_UPDATE_COOLDOWN_DAYS = 1;
    private const int DISPLAY_COOLDOWN_DAYS = 0;

    public static string Credits { get; private set; } = string.Empty;
    private static readonly object _updateLock = new();
    private static bool _isUpdating = false;

    public static string GetCredits(bool returnString = false)
    {
        try
        {
            var updater = new CreditsUpdater();
            var cachedCredits = updater.GetCachedCredits();

            // If no cache or update cooldown expired, trigger background update (only one at a time)
            if ((string.IsNullOrEmpty(cachedCredits) || updater.ShouldUpdateCache()) && !_isUpdating)
            {
                lock (_updateLock)
                {
                    if (!_isUpdating) // Double-check inside lock
                    {
                        _isUpdating = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // MainWindow.Instance?.BlinkingLamp(true);
                                var freshCredits = await updater.FetchAndExtractCreditsAsync();
                                if (!string.IsNullOrEmpty(freshCredits))
                                {
                                    Credits = freshCredits;
                                    updater.CacheCredits(freshCredits);
                                }
                            }
                            catch
                            {
                                // Silently fail background update
                            }
                            finally
                            {
                                // MainWindow.Instance?.BlinkingLamp(false);
                                lock (_updateLock)
                                {
                                    _isUpdating = false;
                                }
                            }
                        });
                    }
                }
            }

            // Check display cooldown - return null if still in cooldown period
            if (!updater.ShouldShowCredits())
            {
                return null;
            }

            // Update last shown timestamp when credits are about to be displayed
            if (!string.IsNullOrEmpty(cachedCredits))
            {
                updater.UpdateLastShownTimestamp();
            }

            // Only return credits if display is allowed AND cache exists
            return returnString && !string.IsNullOrEmpty(cachedCredits) ? cachedCredits : null;
        }
        catch
        {
            return null;
        }
    }

    private string? GetCachedCredits()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            return localSettings.Values.TryGetValue(CREDITS_CACHE_KEY, out var value)
                ? value.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldUpdateCache()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Values.TryGetValue(CREDITS_TIMESTAMP_KEY, out var value))
                return true;

            if (DateTime.TryParse(value.ToString(), out var cachedTime))
            {
                return DateTime.Now - cachedTime >= TimeSpan.FromDays(CACHE_UPDATE_COOLDOWN_DAYS);
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private bool ShouldShowCredits()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Values.TryGetValue(CREDITS_LAST_SHOWN_KEY, out var value))
                return true; // Never shown before, allow showing

            if (DateTime.TryParse(value.ToString(), out var lastShownTime))
            {
                return DateTime.Now - lastShownTime >= TimeSpan.FromDays(DISPLAY_COOLDOWN_DAYS);
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private void UpdateLastShownTimestamp()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_LAST_SHOWN_KEY] = DateTime.Now.ToString();
        }
        catch
        {
            // Silently ignore timestamp update failures
        }
    }

    private async Task<string> FetchAndExtractCreditsAsync()
    {
        try
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                string userAgent = $"vanilla_rtx_app_updater/{TunerVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);

                var response = await client.GetAsync(README_URL);
                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync();

                // Extract credits between "### Credits" and "——"
                int creditsIndex = content.IndexOf("### Credits", StringComparison.OrdinalIgnoreCase);
                if (creditsIndex == -1)
                    return null;

                string afterCredits = content.Substring(creditsIndex + "### Credits".Length).Trim();
                int delimiterIndex = afterCredits.IndexOf("——");
                if (delimiterIndex == -1)
                    return null;

                return afterCredits.Substring(0, delimiterIndex).Trim() +
                       "\n\nConsider supporting development of Vanilla RTX, maybe you'll find your name here next time!? ❤️";
            }
        }
        catch
        {
            return null;
        }
    }

    private void CacheCredits(string credits)
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_CACHE_KEY] = credits;
            localSettings.Values[CREDITS_TIMESTAMP_KEY] = DateTime.Now.ToString();
        }
        catch
        {
            // Silent fails
        }
    }

    public static void ForceUpdateCache()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_TIMESTAMP_KEY] = DateTime.Now.AddDays(-10).ToString();
            localSettings.Values[CREDITS_LAST_SHOWN_KEY] = DateTime.Now.AddDays(-10).ToString();
        }
        catch
        {
        }
    }
}

/// =====================================================================================================================
/// Show PSA from github readme, simply add a ### PSA tag followed by the announcement at the end of the readme file linked below
/// =====================================================================================================================

public class PSAUpdater
{
    private const string README_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX-App/main/README.md";
    private const string CACHE_KEY = "PSAContentCache";
    private const string TIMESTAMP_KEY = "PSALastCheckedTimestamp";
    private const string LAST_SHOWN_KEY = "PSALastShownTimestamp";
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromHours(6);
    private static readonly TimeSpan SHOW_COOLDOWN = TimeSpan.FromMinutes(0.5);

    public static async Task<string?> GetPSAAsync()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            // Check if we need to fetch new data from GitHub
            bool shouldFetch = true;
            if (localSettings.Values.ContainsKey(TIMESTAMP_KEY))
            {
                var lastChecked = DateTime.Parse(localSettings.Values[TIMESTAMP_KEY] as string);
                if (DateTime.UtcNow - lastChecked < COOLDOWN)
                {
                    shouldFetch = false;
                }
            }

            // Fetch new data if cooldown expired
            if (shouldFetch)
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var userAgent = $"vanilla_rtx_app_updater/{TunerVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
                    client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                    var response = await client.GetAsync(README_URL);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        int psaIndex = content.IndexOf("### PSA", StringComparison.OrdinalIgnoreCase);

                        if (psaIndex != -1)
                        {
                            var afterPSA = content.Substring(psaIndex + "### PSA".Length).Trim();
                            var result = string.IsNullOrWhiteSpace(afterPSA) ? null : afterPSA;

                            // Cache the result and update fetch timestamp
                            localSettings.Values[CACHE_KEY] = result;
                        }

                        localSettings.Values[TIMESTAMP_KEY] = DateTime.UtcNow.ToString("O");
                    }
                }
            }

            // Now check if we should show the PSA based on last shown time
            if (localSettings.Values.ContainsKey(LAST_SHOWN_KEY))
            {
                var lastShown = DateTime.Parse(localSettings.Values[LAST_SHOWN_KEY] as string);
                if (DateTime.UtcNow - lastShown < SHOW_COOLDOWN)
                {
                    // Too soon to show again
                    return null;
                }
            }

            // Get cached content to show
            var cachedContent = localSettings.Values.ContainsKey(CACHE_KEY)
                ? localSettings.Values[CACHE_KEY] as string
                : null;

            // Update last shown timestamp if we have content to show
            if (!string.IsNullOrWhiteSpace(cachedContent))
            {
                localSettings.Values[LAST_SHOWN_KEY] = DateTime.UtcNow.ToString("O");
            }

            return cachedContent;
        }
        catch
        {
            // On error, check if we can still show cached content
            var localSettings = ApplicationData.Current.LocalSettings;

            // Check show cooldown
            if (localSettings.Values.ContainsKey(LAST_SHOWN_KEY))
            {
                var lastShown = DateTime.Parse(localSettings.Values[LAST_SHOWN_KEY] as string);
                if (DateTime.UtcNow - lastShown < SHOW_COOLDOWN)
                {
                    return null;
                }
            }

            var cachedContent = localSettings.Values.ContainsKey(CACHE_KEY)
                ? localSettings.Values[CACHE_KEY] as string
                : null;

            if (!string.IsNullOrWhiteSpace(cachedContent))
            {
                localSettings.Values[LAST_SHOWN_KEY] = DateTime.UtcNow.ToString("O");
            }

            return cachedContent;
        }
    }
}
