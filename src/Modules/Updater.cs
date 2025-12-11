using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Storage;
using static Vanilla_RTX_App.Modules.PackLocator; // For static UUIDs, they are stored there for locating packs

namespace Vanilla_RTX_App.Modules;

// Updating logging of classes here to allow proper use of the actual Logging method with logLevels

/// =====================================================================================================================
/// Only deals with cache, we don't care if user has Vanilla RTX installed or not, we compare versions of cache to remote
/// No cache? download latest, cache outdated? download latest, if there's a cache and the rest fails for whatever the reason, fallback to cache
/// Deployment deletes any pack that matches UUIDs as defined at the begenning of PackLocator class
/// =====================================================================================================================

public class PackUpdater
{
    private const string VANILLA_RTX_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX/manifest.json";
    private const string VANILLA_RTX_NORMALS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Normals/manifest.json";
    private const string VANILLA_RTX_OPUS_MANIFEST_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/Vanilla-RTX-Opus/manifest.json";

    private const string VANILLA_RTX_REPO_ZIPBALL_URL = "https://github.com/Cubeir/Vanilla-RTX/archive/refs/heads/master.zip";

    // Event for progress updates to avoid UI thread blocking
    public event Action<string>? ProgressUpdate;
    private readonly List<string> _logMessages = new();

    // For cooldown of checking for update to avoid spamming github
    // These are now target-aware (Release vs Preview have independent cooldowns)
    private const string LastUpdateCheckKey_Release = "LastPackUpdateCheckTime_Release";
    private const string LastUpdateCheckKey_Preview = "LastPackUpdateCheckTime_Preview";
    private static readonly TimeSpan UpdateCooldown = TimeSpan.FromMinutes(60);

    // Cooldowns for manual version checks (5 minutes, independent per target)
    private const string LastUpdateCheckNotificationKey_Release = "LastPackUpdateNotificationCheckTime_Release";
    private const string LastUpdateCheckNotificationKey_Preview = "LastPackUpdateNotificationCheckTime_Preview";
    private static readonly TimeSpan UpdateCheckNotificationCooldown = TimeSpan.FromMinutes(15);

    // TODO: EXPOSE THESE TO AN EXTERNAL FILE. CONFIGURE AT STARTUP -- SAME WITH THE OTHER STUFF
    public string EnhancementFolderName { get; set; } = "__enhancements";

    // If enabled, installs to development folder
    public bool InstallToDevelopmentFolder { get; set; } = false;

    // If enabled, tries to find the opposite folder of where we're deploying to, and cleans the folders there too before and after installation
    public bool CleanUpTheOtherFolder { get; set; } = true;


    private string GetUpdateCheckKey() => TunerVariables.Persistent.IsTargetingPreview
        ? LastUpdateCheckKey_Preview
        : LastUpdateCheckKey_Release;

    private string GetNotificationCheckKey() => TunerVariables.Persistent.IsTargetingPreview
        ? LastUpdateCheckNotificationKey_Preview
        : LastUpdateCheckNotificationKey_Release;

    // -------------------------------\           /------------------------------------ \\
    public async Task<(bool Success, List<string> Logs)> UpdatePacksAsync()
    {
        _logMessages.Clear();

        try
        {
            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
            {
                return await HandleNoCacheScenario();
            }
            // Either cache location or the file itself were unavailable:
            else
            {
                return await HandleCacheExistsScenario(cacheInfo.path);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Unexpected error: {ex.Message}");
            return (false, new List<string>(_logMessages));
        }
    }

    // Updating packs splits into two major possible outcomes, the rest is here:
    private async Task<(bool Success, List<string> Logs)> HandleNoCacheScenario()
    {
        LogMessage("📦 Downloading latest version");

        var (downloadSuccess, downloadPath) = await DownloadLatestPackage();
        if (!downloadSuccess || string.IsNullOrEmpty(downloadPath))
        {
            LogMessage("❌ Failed: Download failed and no pre-existing cache is available.");
            return (false, new List<string>(_logMessages));
        }

        SaveCachedZipballPath(downloadPath);
        LogMessage("✅ Download cached for quicker future redeployment");

        var deploySuccess = await DeployPackage(downloadPath);
        return (deploySuccess, new List<string>(_logMessages));
    }
    private async Task<(bool Success, List<string> Logs)> HandleCacheExistsScenario(string cachePath)
    {
        LogMessage("✅ Cache found");

        var needsUpdate = await CheckIfUpdateNeeded(cachePath);

        if (needsUpdate)
        {
            LogMessage("📦 Package Update available!");
            var (downloadSuccess, downloadPath) = await DownloadLatestPackage();

            if (downloadSuccess && !string.IsNullOrEmpty(downloadPath))
            {
                SaveCachedZipballPath(downloadPath);
                LogMessage("✅ New version downloaded and cached successfully for future redeployment.");

                var deploySuccess = await DeployPackage(downloadPath);
                return (deploySuccess, new List<string>(_logMessages));
            }
            else
            {
                LogMessage("⚠️ Download failed: fell back to older cached version.");
                var deploySuccess = await DeployPackage(cachePath);
                return (deploySuccess, new List<string>(_logMessages));
            }
        }
        else
        {
            // Either update failed, or is on cooldown, or no update is available, whatever the case got no choice, this is the latest we got
            // TODO: Update so it returns actual reasons
            LogMessage("ℹ️ Current cached package is the latest available at the moment.");
            var deploySuccess = await DeployPackage(cachePath);
            return (deploySuccess, new List<string>(_logMessages));
        }
    }



    private async Task<bool> CheckIfUpdateNeeded(string cachePath)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            LogMessage("🛜 No network available – reusing cache.");
            return false;
        }

        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var cooldownKey = GetUpdateCheckKey();

        // Only honor cooldown if we have a VALID, usable cache
        if (localSettings.Values[cooldownKey] is string lastCheckStr &&
            DateTimeOffset.TryParse(lastCheckStr, out var lastSuccessfulCheck))
        {
            if (now < lastSuccessfulCheck + UpdateCooldown)
            {
                var minutesLeft = (int)Math.Ceiling((lastSuccessfulCheck + UpdateCooldown - now).TotalMinutes);
                LogMessage($"⏳ Update check on cooldown – {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} left");
                return false;
            }
        }

        (JObject? rtx, JObject? normals, JObject? opus)? remote = null;
        bool fetchSuccess = false;

        try
        {
            remote = await FetchRemoteManifests();
            fetchSuccess = true;
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to contact GitHub – forcing redownload ({ex.Message})");
        }

        // ONLY update cooldown timestamp on SUCCESSFUL remote contact + valid cache comparison
        if (fetchSuccess && remote.HasValue)
        {
            localSettings.Values[cooldownKey] = now.ToString("o");
        }

        if (remote == null)
            return true; // network/manifest fail means force download

        return await DoesCacheNeedUpdate(cachePath, remote.Value);
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

            // Check each pack independently
            bool needsUpdate = false;

            // RTX Pack
            if (remoteManifests.rtx != null)
            {
                if (rtxManifest == null)
                {
                    // Pack exists remotely but not in cache - definitely need update
                    LogMessage("📦 Vanilla RTX is available remotely but missing from cache");
                    needsUpdate = true;
                }
                else if (IsRemoteVersionNewer(rtxManifest, remoteManifests.rtx))
                {
                    LogMessage("📦 Vanilla RTX has a newer version available!");
                    needsUpdate = true;
                }
            }

            // Normals Pack
            if (remoteManifests.normals != null)
            {
                if (normalsManifest == null)
                {
                    LogMessage("📦 Vanilla RTX Normals is available remotely but missing from cache");
                    needsUpdate = true;
                }
                else if (IsRemoteVersionNewer(normalsManifest, remoteManifests.normals))
                {
                    LogMessage("📦 Vanilla RTX Normals has a newer version available!");
                    needsUpdate = true;
                }
            }

            // Opus Pack
            if (remoteManifests.opus != null)
            {
                if (opusManifest == null)
                {
                    LogMessage("📦 Vanilla RTX Opus is available remotely but missing from cache");
                    needsUpdate = true;
                }
                else if (IsRemoteVersionNewer(opusManifest, remoteManifests.opus))
                {
                    LogMessage("📦 Vanilla RTX Opus has a newer version available!");
                    needsUpdate = true;
                }
            }

            return needsUpdate;
        }
        catch (Exception ex)
        {
            LogMessage($"Error reading cached package: {ex.Message}");
            return true; // Force update on read errors
        }
    }

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
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", $"vanilla_rtx_app_updater/{TunerVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)");

            // Fetch each manifest independently, allowing some to fail
            async Task<JObject?> TryFetchManifest(string url)
            {
                try
                {
                    var response = await client.GetStringAsync(url);
                    return JObject.Parse(response);
                }
                catch
                {
                    return null; // Pack doesn't exist remotely or network error
                }
            }

            var rtxTask = TryFetchManifest(VANILLA_RTX_MANIFEST_URL);
            var normalsTask = TryFetchManifest(VANILLA_RTX_NORMALS_MANIFEST_URL);
            var opusTask = TryFetchManifest(VANILLA_RTX_OPUS_MANIFEST_URL);

            await Task.WhenAll(rtxTask, normalsTask, opusTask);

            var rtx = await rtxTask;
            var normals = await normalsTask;
            var opus = await opusTask;

            // Return null only if ALL manifests failed to fetch
            if (rtx == null && normals == null && opus == null)
            {
                return null;
            }

            return (rtx, normals, opus);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, string? Path)> DownloadLatestPackage()
    {
        try
        {
            return await Helpers.Download(VANILLA_RTX_REPO_ZIPBALL_URL);
        }
        catch (Exception ex)
        {
            LogMessage($"Download error: {ex.Message}");
            return (false, null);
        }
    }


    // ---------- Deployment methods


    private async Task<bool> DeployPackage(string packagePath)
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
            // Find the resource pack path, where we wanna deploy
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

            // Step 1: Extract zipball to temp directory
            tempExtractionDir = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractionDir);

            ZipFile.ExtractToDirectory(packagePath, tempExtractionDir, overwriteFiles: true);
            LogMessage("📦 Extracted package to temporary directory");

            // Step 2: Find which packs exist in the zipball extraction
            var extractedManifests = Directory.GetFiles(tempExtractionDir, "manifest.json", SearchOption.AllDirectories);

            var packsToProcess = new List<(string uuid, string moduleUuid, string sourcePath, string finalName, string displayName)>();

            foreach (var manifestPath in extractedManifests)
            {
                var uuids = await ReadManifestUUIDs(manifestPath);
                if (uuids == null) continue;

                var (headerUUID, moduleUUID) = uuids.Value;
                var packSourcePath = Path.GetDirectoryName(manifestPath);

                if (headerUUID == VANILLA_RTX_HEADER_UUID && moduleUUID == VANILLA_RTX_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtx", "Vanilla RTX"));
                }
                else if (headerUUID == VANILLA_RTX_NORMALS_HEADER_UUID && moduleUUID == VANILLA_RTX_NORMALS_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtxn", "Vanilla RTX Normals"));
                }
                else if (headerUUID == VANILLA_RTX_OPUS_HEADER_UUID && moduleUUID == VANILLA_RTX_OPUS_MODULE_UUID)
                {
                    packsToProcess.Add((headerUUID, moduleUUID, packSourcePath, "vrtxo", "Vanilla RTX Opus"));
                }
            }

            if (packsToProcess.Count == 0)
            {
                LogMessage("❌ No recognized Vanilla RTX packs found in the downloaded package.");
                return false;
            }

            LogMessage($"📦 Found {packsToProcess.Count} pack(s) in cache: {string.Join(", ", packsToProcess.Select(p => p.displayName))}");


            // Step 4: Atomic deployment for each pack
            foreach (var pack in packsToProcess)
            {
                try
                {
                    LogMessage($"🔄 Processing {pack.displayName}...");

                    // Atomic operation: Delete old, then immediately move new
                    await DeleteExistingPackByUUID(resourcePackPath,
                        pack.uuid, pack.moduleUuid, pack.displayName);

                    var finalDestination = GetSafeDirectoryName(resourcePackPath, pack.finalName);
                    Directory.Move(pack.sourcePath, finalDestination);

                    ProcessEnhancementFolders(finalDestination);

                    // Ensure empty contents.json and make TL.json
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
                    catch { Debug.WriteLine("Contents json or textures list creation failed."); }

                    LogMessage($"✅ {pack.displayName} deployed successfully");
                    anyPackDeployed = true;
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Failed to deploy {pack.displayName}: {ex.Message}");
                    // Continue with other packs - don't let one failure stop everything
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
            // Step 5: Clean up temp extraction directory
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

            // Step 6: Clean up orphaned temp directories (like from crashes)
            if (resourcePackPath != null)
            {
                var pathsToCleanOrphans = new List<string> { resourcePackPath };

                // Same logic as deployment-time existing pack clean up thingy
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

                var cutoff = DateTime.UtcNow.AddMinutes(-1); // 1 minute grace period for temp folders

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
                                LogMessage($"🧹 Cleaned up previous orphaned folder(s).");
                            }
                            catch { /* ignore */ }
                        }
                    }
                    catch { /* ignore */ }
                }
            }
        }
    }
    private string GetSafeDirectoryName(string parentPath, string desiredName)
    {
        var fullPath = Path.Combine(parentPath, desiredName);

        // If directory doesn't exist, we can use it
        if (!Directory.Exists(fullPath))
            return fullPath;

        // If directory exists but is empty, we can safely overwrite
        if (Directory.GetFileSystemEntries(fullPath).Length == 0)
        {
            Directory.Delete(fullPath);
            return fullPath;
        }

        // Directory exists and has files, find a numbered suffix
        int suffix = 1;
        string safeName;
        do
        {
            safeName = Path.Combine(parentPath, $"{desiredName}{suffix}");
            suffix++;
        } while (Directory.Exists(safeName) && Directory.GetFileSystemEntries(safeName).Length > 0);

        // If we found an existing empty directory with this numbered name, delete it
        if (Directory.Exists(safeName))
            Directory.Delete(safeName);

        return safeName;
    }
    private async Task DeleteExistingPackByUUID(string resourcePackPath,
        string targetHeaderUUID, string targetModuleUUID, string packName)
    {
        // Build list of paths to clean
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

        // Clean from all paths
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
                    // Don't early return anymore - we might find it in both folders
                }
            }
        }
    }
    private void ForceWritable(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;

        // Only modify if actually read-only
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



    // ---------- Enhanced option enabler
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
                Debug.WriteLine($"Enhancement folder error ({enhancementPath}): {ex.Message}");
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

    // ---------- Caching Helpers
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


    // ---------- Concerning manual user-triggered update check
    /// <summary>
    /// Performs a detailed version check with detailed output all packs (installed or not).
    /// Returns cooldown message if on cooldown, detailed status for all packs otherwise.
    /// This is designed for manual invocation (e.g., Shift+button press).
    /// </summary>
    public async Task<string?> CheckForPackUpdates(
        string vanillaRTXVersion,
        string vanillaRTXNormalsVersion,
        string vanillaRTXOpusVersion)
    {
        // FIRST: Check cooldown - no network activity if on cooldown
        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var cooldownKey = GetNotificationCheckKey();

        if (localSettings.Values[cooldownKey] is string lastCheckStr &&
            DateTimeOffset.TryParse(lastCheckStr, out var lastCheck))
        {
            if (now < lastCheck + UpdateCheckNotificationCooldown)
            {
                var minutesLeft = (int)Math.Ceiling((lastCheck + UpdateCheckNotificationCooldown - now).TotalMinutes);
                return $"⏳ You just performed an update check, wait {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} before trying again.";
            }
        }

        // Check network availability
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            return "❌ Failed to check for pack updates: No network connection available.";
        }

        try
        {
            // Fetch remote manifests (always fetch all three)
            var remoteManifests = await FetchRemoteManifests();
            if (remoteManifests == null)
            {
                return "❌ Failed to check for pack updates: Unable to reach GitHub.";
            }

            var (rtxRemote, normalsRemote, opusRemote) = remoteManifests.Value;

            // Build detailed status for each pack
            var statusLines = new List<string>();

            // Vanilla RTX
            statusLines.Add(GeneratePackStatus("Vanilla RTX", vanillaRTXVersion, rtxRemote));

            // Vanilla RTX Normals
            statusLines.Add(GeneratePackStatus("Vanilla RTX Normals", vanillaRTXNormalsVersion, normalsRemote));

            // Vanilla RTX Opus
            statusLines.Add(GeneratePackStatus("Vanilla RTX Opus", vanillaRTXOpusVersion, opusRemote));

            // Update cooldown timestamp only after successful check
            localSettings.Values[cooldownKey] = now.ToString("o");

            return string.Join(Environment.NewLine, statusLines);
        }
        catch (Exception ex)
        {
            return $"❌ Failed to check for pack updates: {ex.Message}";
        }
    }

    /// <summary>
    /// Generates the detailed status line for a single pack.
    /// Covers all scenarios: installed/not installed × available/unavailable × up-to-date/update available
    /// </summary>
    private string GeneratePackStatus(string packName, string installedVersion, JObject? remoteManifest)
    {
        bool isInstalled = !string.IsNullOrEmpty(installedVersion);
        string? remoteVersionStr = ExtractVersionFromManifest(remoteManifest);
        bool remoteAvailable = !string.IsNullOrEmpty(remoteVersionStr);

        if (!isInstalled)
        {
            // Pack is not installed
            if (remoteAvailable)
            {
                return $"📦 {packName} - Not installed - Available: {remoteVersionStr}";
            }
            else
            {
                return $"❌ {packName} - Not installed - Unavailable";
            }
        }
        else // Pack is installed
        {
            if (!remoteAvailable)
            {
                // Can't determine if update exists, just show installed version
                return $"⚠️ {packName} - Installed: {installedVersion} - Unavailable";
            }
            else
            {
                // Compare versions
                bool updateAvailable = IsRemoteVersionNewerThanInstalled(installedVersion, remoteManifest);

                if (updateAvailable)
                {
                    return $"📦 {packName} - Installed: {installedVersion} - Available: {remoteVersionStr}";
                }
                else
                {
                    return $"✅ {packName} - Installed: {installedVersion} - Up-to-date";
                }
            }
        }
    }

    /// <summary>
    /// Extracts version string from a remote manifest JObject.
    /// Returns formatted version string like "v1.2.3" or null if unavailable.
    /// </summary>
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

    private bool IsRemoteVersionNewerThanInstalled(string installedVersionString, JObject remoteManifest)
    {
        try
        {
            // Parse installed version string (format: "v1.2.3")
            var installedVersion = ParseVersionString(installedVersionString);
            if (installedVersion == null) return true; // Can't parse installed, assume update needed

            // Parse remote version from manifest
            var remoteVersion = remoteManifest["header"]?["version"]?.ToObject<int[]>();
            if (remoteVersion == null) return false; // Can't get remote version, assume no update

            return CompareVersionArrays(remoteVersion, installedVersion) > 0;
        }
        catch
        {
            return false; // If comparison fails, assume no update to avoid false positives
        }
    }

    private int[]? ParseVersionString(string versionString)
    {
        try
        {
            if (string.IsNullOrEmpty(versionString))
                return null;

            // Remove 'v' prefix if present (handles "v1.1232.323" format from LocatePacks)
            versionString = versionString.TrimStart('v', 'V');

            var parts = versionString.Split('.');
            return parts.Select(int.Parse).ToArray();
        }
        catch
        {
            return null;
        }
    }



    // ---------- Other Helpers

    /// <summary>
    /// Unified version comparison logic. Returns: 1 if a > b, -1 if a < b, 0 if equal.
    /// </summary>
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

    private string GetTopLevelFolderForManifest(string manifestPath, string resourcePackPath)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath);
        var resourcePackDir = new DirectoryInfo(resourcePackPath);
        var currentDir = new DirectoryInfo(manifestDir);

        // Walk up the directory tree until we find the direct child of resourcePackPath
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
