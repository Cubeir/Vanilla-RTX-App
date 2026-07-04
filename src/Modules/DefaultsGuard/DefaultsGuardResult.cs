using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vanilla_RTX_App.BetterRTXManager;
using Vanilla_RTX_App.LUTManager;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.Modules.DefaultsGuard;

/// <summary>
/// Outcome of a pre-wipe "is a custom preset currently installed?" check.
/// </summary>
public enum DefaultsGuardResult
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
/// QoL safety net for the "full app reset" button. BetterRTX and RTX LUT both keep a
/// Default preset backup used to restore the game's original files - but that backup
/// lives in local storage, which the reset button wipes. If the game currently has a
/// non-default preset installed when that happens, the user loses their only path back
/// to vanilla files. This guard checks each feature's state right before the wipe and,
/// if needed, silently reverts to Default first (one UAC prompt per feature that's dirty).
/// </summary>
public static class DefaultsGuard
{
    private const string LutFile_LookUpTables = "look_up_tables.png";
    private const string LutFile_Sky = "sky.png";
    private const string LutFile_Water = "water_n.tga";

    public static async Task<DefaultsGuardResult> RestoreBetterRTXDefaultIfNeededAsync(Action<string>? log = null)
    {
        try
        {
            var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            var cacheFolder = Path.Combine(localFolder, "RTX_Cache");
            var defaultFolder = Path.Combine(cacheFolder, "__DEFAULT");

            if (!Directory.Exists(defaultFolder))
            {
                log?.Invoke("[BetterRTX Guard] No __DEFAULT backup exists - nothing to protect.");
                return DefaultsGuardResult.NoActionNeeded;
            }

            var defaultBinFiles = Directory.GetFiles(defaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();
            if (defaultBinFiles.Count == 0)
            {
                log?.Invoke("[BetterRTX Guard] __DEFAULT folder exists but has no .bin files, nothing to restore.");
                return DefaultsGuardResult.NoActionNeeded;
            }

            var cachedPath = Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, false))
            {
                log?.Invoke("[BetterRTX Guard] Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return DefaultsGuardResult.Skipped;
            }

            var gameMaterialsPath = Path.Combine(cachedPath!, "data", "renderer", "materials");
            if (!Directory.Exists(gameMaterialsPath))
            {
                log?.Invoke("[BetterRTX Guard] Materials folder not found in game install - can't verify current preset state.");
                return DefaultsGuardResult.Skipped;
            }

            var defaultHashes = BetterRTXManagerWindow.GetPresetHashes(defaultBinFiles);
            var currentHashes = BetterRTXManagerWindow.GetCurrentlyInstalledHashes(gameMaterialsPath);

            if (currentHashes.Count == 0)
            {
                log?.Invoke("[BetterRTX Guard] Could not read any Core RTX files from the game - skipping to avoid acting on incomplete info.");
                return DefaultsGuardResult.Skipped;
            }

            if (BetterRTXManagerWindow.AreHashesMatching(currentHashes, defaultHashes))
            {
                log?.Invoke("[BetterRTX Guard] Game already matches Default - nothing to do.");
                return DefaultsGuardResult.NoActionNeeded;
            }

            log?.Invoke("[BetterRTX Guard] Non-default preset detected - restoring Default before wipe...");

            var filesToApply = defaultBinFiles
                .Select(src => (src, Path.Combine(gameMaterialsPath, Path.GetFileName(src))))
                .ToList();

            var success = await Helpers.ReplaceFilesWithElevation(filesToApply, "[BetterRTX Guard]", "betterrtx_predelete_restore");

            log?.Invoke(success ? "[BetterRTX Guard] Default restored successfully." : "[BetterRTX Guard] Failed to restore Default.");
            return success ? DefaultsGuardResult.Restored : DefaultsGuardResult.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[BetterRTX Guard] Exception: {ex.Message}");
            return DefaultsGuardResult.Skipped;
        }
    }

    public static async Task<DefaultsGuardResult> RestoreLutDefaultIfNeededAsync(bool targetPreview, Action<string>? log = null)
    {
        try
        {
            var defaultsFolder = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Lut_Defaults");
            var defaultLut = Path.Combine(defaultsFolder, LutFile_LookUpTables);
            var defaultSky = Path.Combine(defaultsFolder, LutFile_Sky);
            var defaultWater = Path.Combine(defaultsFolder, LutFile_Water);

            if (!File.Exists(defaultLut) || !File.Exists(defaultSky) || !File.Exists(defaultWater))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] No complete Default backup exists - nothing to protect.");
                return DefaultsGuardResult.NoActionNeeded;
            }

            var cachedPath = targetPreview ? Persistent.MinecraftPreviewInstallPath : Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, targetPreview))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return DefaultsGuardResult.Skipped;
            }

            var dstLut = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_LookUpTables);
            var dstSky = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_Sky);
            var dstWater = Path.Combine(cachedPath!, "data", "ray_tracing", LutFile_Water);

            if (!File.Exists(dstLut) || !File.Exists(dstSky) || !File.Exists(dstWater))
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Game's ray_tracing files are missing/incomplete - can't verify current preset state.");
                return DefaultsGuardResult.Skipped;
            }

            bool alreadyDefault =
                LUTManagerWindow.HashesMatch(dstLut, defaultLut) &&
                LUTManagerWindow.HashesMatch(dstSky, defaultSky) &&
                LUTManagerWindow.HashesMatch(dstWater, defaultWater);

            if (alreadyDefault)
            {
                log?.Invoke($"[LUT Guard{(targetPreview ? " Preview" : "")}] Game already matches Default - nothing to do.");
                return DefaultsGuardResult.NoActionNeeded;
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
            return success ? DefaultsGuardResult.Restored : DefaultsGuardResult.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LUT Guard] Exception: {ex.Message}");
            return DefaultsGuardResult.Skipped;
        }
    }
}
