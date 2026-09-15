using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.BetterRTX;
using Vanilla_RTX_App.Modules.LUT;
using static Vanilla_RTX_App.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Outcome of a pre-wipe "is a custom preset currently installed?" check.
/// </summary>
public enum RTXDefaultsGuard
{
    /// <summary>No Default backup exists, or the game already matches it - nothing to do.</summary>
    NoActionNeeded,

    /// <summary>A non-default preset was detected and successfully reverted to Default.</summary>
    Restored,

    /// <summary>A non-default preset was detected but reverting failed (elevation declined / IO error).</summary>
    RestoreFailed,

    /// <summary>A Default backup exists but its state relative to the game couldn't be safely verified, so nothing was touched.</summary>
    Skipped
}

/// <summary>
/// QoL safety net for the "hard wipe" button. BetterRTX and RTX LUT both keep a
/// Default preset backup used to restore the game's original files - but that backup
/// lives in local storage, which the reset button wipes. If the game currently has a
/// non-default preset installed when that happens, the user loses their only path back
/// to vanilla files. This guard checks each feature's state right before the wipe and,
/// if needed, silently reverts to Default first (one UAC prompt per dirty backup - each
/// feature keeps one per Minecraft edition).
/// </summary>
public static class DefaultsGuard
{
    /// <summary>
    /// Runs the BetterRTX check for <b>both</b> editions, since each keeps its own Default
    /// backup and the wipe is about to take both with it. The LUT guard is called once per
    /// edition from the call site instead; this one answers for the feature as a whole
    /// because its two backups live in one cache folder and are cleared by one wipe.
    ///
    /// <para>Combining the two results keeps the worst news: a failure anywhere reports
    /// RestoreFailed, then Restored, then Skipped, and only "neither edition needed
    /// anything" reports NoActionNeeded. Each edition still logs under its own label, so the
    /// combined verdict never hides which one it came from. Each dirty edition costs one UAC
    /// prompt, exactly as one dirty feature did before.</para>
    /// </summary>
    public static async Task<RTXDefaultsGuard> RestoreBetterRTXDefaultIfNeededAsync(Action<string>? log = null)
    {
        var release = await RestoreBetterRTXDefaultForEditionAsync(targetPreview: false, log);
        var preview = await RestoreBetterRTXDefaultForEditionAsync(targetPreview: true, log);

        if (release == RTXDefaultsGuard.RestoreFailed || preview == RTXDefaultsGuard.RestoreFailed)
            return RTXDefaultsGuard.RestoreFailed;

        if (release == RTXDefaultsGuard.Restored || preview == RTXDefaultsGuard.Restored)
            return RTXDefaultsGuard.Restored;

        if (release == RTXDefaultsGuard.Skipped || preview == RTXDefaultsGuard.Skipped)
            return RTXDefaultsGuard.Skipped;

        return RTXDefaultsGuard.NoActionNeeded;
    }

    // Folder layout comes from BetterRTXManager rather than being spelled again here - this
    // guard exists to protect that feature's backup, so it has to be looking at the same
    // folder permanently. Re-spelling "RTX_Cache" and "__DEFAULT" here is exactly how this
    // would have quietly kept guarding Release only once Preview got a backup of its own.
    private static async Task<RTXDefaultsGuard> RestoreBetterRTXDefaultForEditionAsync(bool targetPreview, Action<string>? log)
    {
        var tag = $"[BetterRTX Guard{(targetPreview ? " Preview" : "")}]";

        try
        {
            var defaultFolder = BetterRTXManager.GetDefaultFolderPath(targetPreview);
            var folderName = BetterRTXManager.GetDefaultFolderName(targetPreview);

            if (defaultFolder == null || !Directory.Exists(defaultFolder))
            {
                log?.Invoke($"{tag} No {folderName} backup exists - nothing to protect.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var defaultBinFiles = Directory.GetFiles(defaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();
            if (defaultBinFiles.Count == 0)
            {
                log?.Invoke($"{tag} {folderName} folder exists but has no .bin files, nothing to restore.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var cachedPath = targetPreview ? Persistent.MinecraftPreviewInstallPath : Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, targetPreview))
            {
                log?.Invoke($"{tag} Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return RTXDefaultsGuard.Skipped;
            }

            var gameMaterialsPath = Path.Combine(cachedPath!, "data", "renderer", "materials");
            if (!Directory.Exists(gameMaterialsPath))
            {
                log?.Invoke($"{tag} Materials folder not found in game install - can't verify current preset state.");
                return RTXDefaultsGuard.Skipped;
            }

            var defaultHashes = BetterRTXManager.GetPresetHashes(defaultBinFiles);
            var currentHashes = BetterRTXManager.GetCurrentlyInstalledHashes(gameMaterialsPath);

            if (currentHashes.Count == 0)
            {
                log?.Invoke($"{tag} Could not read any Core RTX files from the game - skipping to avoid acting on incomplete info.");
                return RTXDefaultsGuard.Skipped;
            }

            if (BetterRTXManager.AreHashesMatching(currentHashes, defaultHashes))
            {
                log?.Invoke($"{tag} Game already matches Default - nothing to do.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            log?.Invoke($"{tag} Non-default preset detected - restoring Default before wipe...");

            var filesToApply = defaultBinFiles
                .Select(src => (src, Path.Combine(gameMaterialsPath, Path.GetFileName(src))))
                .ToList();

            var success = await Helpers.ReplaceFilesWithElevation(filesToApply, tag, "betterrtx_predelete_restore");

            log?.Invoke(success ? $"{tag} Default restored successfully." : $"{tag} Failed to restore Default.");
            return success ? RTXDefaultsGuard.Restored : RTXDefaultsGuard.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{tag} Exception: {ex.Message}");
            return RTXDefaultsGuard.Skipped;
        }
    }

    // Names, folder and hash check all come from LUTManager rather than being spelled again
    // here - this guard exists to protect that feature's backup, so it has to be looking at
    // the same three files in the same place, permanently.
    private const string LutFile_LookUpTables = LUTManager.FnLut;
    private const string LutFile_Sky = LUTManager.FnSky;
    private const string LutFile_Water = LUTManager.FnWater;
    public static async Task<RTXDefaultsGuard> RestoreLutDefaultIfNeededAsync(bool targetPreview, Action<string>? log = null)
    {
        try
        {
            var defaultsFolder = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, LUTManager.DefaultsFolderName);
            var defaultLut = Path.Combine(defaultsFolder, LutFile_LookUpTables);
            var defaultSky = Path.Combine(defaultsFolder, LutFile_Sky);
            var defaultWater = Path.Combine(defaultsFolder, LutFile_Water);

            if (!File.Exists(defaultLut) || !File.Exists(defaultSky) || !File.Exists(defaultWater))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] No complete Default backup exists - nothing to protect.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var cachedPath = targetPreview ? Persistent.MinecraftPreviewInstallPath : Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, targetPreview))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return RTXDefaultsGuard.Skipped;
            }

            var dstLut = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_LookUpTables);
            var dstSky = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_Sky);
            var dstWater = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_Water);

            if (!File.Exists(dstLut) || !File.Exists(dstSky) || !File.Exists(dstWater))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Game's ray_tracing files are missing/incomplete - can't verify current preset state.");
                return RTXDefaultsGuard.Skipped;
            }

            bool alreadyDefault =
                LUTManager.HashesMatch(dstLut, defaultLut) &&
                LUTManager.HashesMatch(dstSky, defaultSky) &&
                LUTManager.HashesMatch(dstWater, defaultWater);

            if (alreadyDefault)
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Game already matches Default - nothing to do.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Non-default preset detected - restoring Default before wipe...");

            var files = new List<(string, string)>
            {
                (defaultLut, dstLut),
                (defaultSky, dstSky),
                (defaultWater, dstWater)
            };

            var success = await Helpers.ReplaceFilesWithElevation(files, "[LUT Guard]", "rtx_defaults_predelete_restore");

            log?.Invoke(success ? "[LUT Guard] Default restored successfully." : "[LUT Guard] Failed to restore Default.");
            return success ? RTXDefaultsGuard.Restored : RTXDefaultsGuard.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LUT Guard] Exception: {ex.Message}");
            return RTXDefaultsGuard.Skipped;
        }
    }
}
