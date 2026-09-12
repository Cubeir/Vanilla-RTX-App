using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.DLSS;

/// <summary>
/// One DLSS runtime sitting in the local cache. <see cref="Version"/> is whatever the file
/// itself reports, verbatim - some builds report it comma-separated ("3,7,0,0"), which is
/// why <see cref="DisplayVersion"/> exists and why nothing compares raw version strings
/// without going through <see cref="DLSSSwapper.IsSupportedVersion"/> first.
/// </summary>
internal sealed class DllData
{
    public string Version { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    public string DisplayVersion => Version.Replace(",", ".");
}

/// <summary>
/// Everything the DLSS swapper does to files: where the cache lives, what's in it, how a
/// .dll or .zip gets imported into it, and how one of them ends up in the game folder.
///
/// <para><b>Why it's separate from the window.</b> None of this needs a UI - it is
/// FileVersionInfo, File.Copy and one elevated replace. The window's job is to render the
/// list this produces and to decide *when* these run; this class is the only thing that
/// knows the cache layout. Split out so that reading either half doesn't mean reading
/// both, exactly as Alchitex separates its pipeline from AlchitexWindow.</para>
///
/// <para>The instance is bound to one Minecraft install by <see cref="TryAttach"/>; every
/// method below is inert until that has succeeded.</para>
/// </summary>
internal sealed class DLSSSwapper
{
    /// <summary>The game's own DLSS runtime - the file every swap overwrites.</summary>
    public const string GameDllFileName = "nvngx_dlss.dll";

    private const string CacheFolderName = "DLSS_Cache";

    /// <summary>
    /// Anything below this is a DLSS 1.x runtime, which Minecraft's renderer can't use.
    /// Cached copies that fail this are deleted on load rather than offered and refused.
    /// </summary>
    private static readonly Version MinimumSupportedVersion = new(2, 0, 0, 0);

    public string GameDllPath { get; private set; } = string.Empty;
    public string CacheFolder { get; private set; } = string.Empty;

    /// <summary>
    /// Version string of the DLL currently sitting in the game folder, as last read by
    /// <see cref="CacheInstalledDllAsync"/>. Null until that has run at least once, and
    /// "Unknown" if the file was there but unreadable.
    /// </summary>
    public string? InstalledVersion { get; private set; }

    public bool GameDllExists => !string.IsNullOrEmpty(GameDllPath) && File.Exists(GameDllPath);

    /// <summary>
    /// Points this instance at a Minecraft install and makes sure the cache folder exists.
    /// False means the cache couldn't be created, which is fatal for the whole feature -
    /// there is nowhere to put anything.
    /// </summary>
    public bool TryAttach(string minecraftPath)
    {
        GameDllPath = Path.Combine(minecraftPath, GameDllFileName);

        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
            return false;

        CacheFolder = cacheFolder;
        return true;
    }

    private static string? EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, CacheFolderName);

            Trace.WriteLine($"[DLSS] Creating DLSS cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"[DLSS] ✓ DLSS cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] ✗ Failed to create DLSS cache: {ex.Message}");
            return null;
        }
    }

    // ======================= Cache contents =======================

    /// <summary>
    /// Every cached .dll, newest file first. Used for the repair path, which just takes
    /// the most recent one it can find.
    /// </summary>
    public List<string> CachedDllPathsByNewest() =>
        Directory.GetFiles(CacheFolder, "*.dll")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();

    /// <summary>
    /// The cache as the window lists it: newest file first, one entry per distinct version
    /// string. Files that can't be read at all are dropped.
    /// </summary>
    public List<DllData> ListCachedVersions()
    {
        var results = new List<DllData>();
        var seenVersions = new HashSet<string>();

        foreach (var dllPath in CachedDllPathsByNewest())
        {
            var dllData = ParseDll(dllPath);
            if (dllData != null && seenVersions.Add(dllData.Version))
                results.Add(dllData);
        }

        return results;
    }

    private static DllData? ParseDll(string dllPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            return new DllData { Version = version, FilePath = dllPath };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error parsing DLL {dllPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The single answer to "can Minecraft actually use this one?", shared by the cleanup
    /// pass and by the list (which greys out anything this rejects). Covers unreadable,
    /// unparseable and DLSS 1.x alike - a version string that isn't a real version is no
    /// more usable than one that's simply too old.
    /// </summary>
    public static bool IsSupportedVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return false;

        return Version.TryParse(rawVersion.Replace(",", "."), out var parsed)
            && parsed >= MinimumSupportedVersion;
    }

    /// <summary>
    /// Drops every cached runtime the game couldn't use anyway. The currently installed
    /// version is spared even when it fails the check - it's already in the game folder,
    /// so deleting our copy of it would only remove the way back.
    /// </summary>
    public async Task RemoveUnsupportedFromCacheAsync()
    {
        await Task.Run(() =>
        {
            foreach (var dllPath in Directory.GetFiles(CacheFolder, "*.dll"))
            {
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                    var raw = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "";

                    if (IsSupportedVersion(raw))
                        continue;

                    if (!string.IsNullOrEmpty(InstalledVersion) &&
                        Path.GetFileNameWithoutExtension(dllPath) == InstalledVersion)
                    {
                        Trace.WriteLine($"[DLSS] Skipping cleanup of current installed version: {dllPath}");
                        continue;
                    }

                    File.Delete(dllPath);
                    Trace.WriteLine($"[DLSS] Cleaned up incompatible/unversioned DLSS from cache: {dllPath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DLSS] Error during cleanup of {dllPath}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Removes one cached runtime. Refuses the installed one for the same reason the
    /// cleanup pass spares it.
    /// </summary>
    public bool DeleteCachedVersion(DllData dll)
    {
        try
        {
            if (dll.Version == InstalledVersion)
            {
                Trace.WriteLine("[DLSS] Cannot delete currently installed DLSS version");
                return false;
            }

            if (!File.Exists(dll.FilePath))
                return false;

            File.Delete(dll.FilePath);
            Trace.WriteLine($"[DLSS] Deleted DLSS version {dll.Version} from cache");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error deleting DLL: {ex.Message}");
            return false;
        }
    }

    // ======================= Import =======================

    /// <summary>
    /// Copies the game's current runtime into the cache under its own version number, and
    /// records that version as installed. Runs on every open so the user always has a way
    /// back to whatever they started with.
    /// </summary>
    public async Task CacheInstalledDllAsync()
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(GameDllPath);

            InstalledVersion = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";

            var cachePath = Path.Combine(CacheFolder, $"{InstalledVersion}.dll");

            await Task.Run(() => File.Copy(GameDllPath, cachePath, true));

            Trace.WriteLine($"[DLSS] Copied current DLSS {InstalledVersion} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error copying current DLL to cache: {ex.Message}");
            InstalledVersion = "Unknown";
        }
    }

    /// <summary>
    /// Cache name is the version, not the source file name, so importing the same runtime
    /// twice from two different downloads lands on one entry rather than two.
    /// </summary>
    public async Task ImportDllAsync(string dllPath)
    {
        try
        {
            if (!Path.GetFileName(dllPath).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[DLSS] Skipped non-DLL file: {dllPath}");
                return;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            var cachePath = Path.Combine(CacheFolder, $"{version}.dll");

            await Task.Run(() => File.Copy(dllPath, cachePath, true));
            Trace.WriteLine($"[DLSS] Added DLSS {version} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing DLL {dllPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Most places hand out DLSS runtimes as a zip. Every .dll inside is taken on its own
    /// terms - a bad entry costs that entry, not the archive.
    /// </summary>
    public async Task ImportZipAsync(string zipPath)
    {
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.WriteLine($"[DLSS] Skipped non-DLL file from ZIP");
                        continue;
                    }

                    try
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), entry.Name);
                        entry.ExtractToFile(tempPath, true);

                        var versionInfo = FileVersionInfo.GetVersionInfo(tempPath);
                        var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
                        var cachePath = Path.Combine(CacheFolder, $"{version}.dll");

                        File.Copy(tempPath, cachePath, true);
                        File.Delete(tempPath);

                        Trace.WriteLine($"[DLSS] Extracted and added DLSS {version} from ZIP");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[DLSS] Error processing {entry.FullName} from ZIP: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing ZIP file: {ex.Message}");
        }
    }

    // ======================= Install =======================

    /// <summary>
    /// The swap itself. The game folder is under Program Files, so this is the one step
    /// that needs elevation - one UAC prompt, one file.
    /// </summary>
    public Task<bool> InstallAsync(string sourceDllPath) =>
        Helpers.ReplaceFilesWithElevation(
            new List<(string, string)> { (sourceDllPath, GameDllPath) },
            "[DLSS]",
            "dlss_dll");
}
