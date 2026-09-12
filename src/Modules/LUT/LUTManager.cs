using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.LUT;

/// <summary>
/// One LUT preset: a folder holding the three files the game's ray tracing folder wants.
/// A preset is only ever offered if it has all three (<see cref="IsComplete"/>) - installing
/// a partial set would leave the game mixing one preset's colour grading with another's sky.
/// </summary>
internal sealed class LutPreset
{
    public string Name { get; }
    public string FolderPath { get; }

    /// <summary>True only for the backup of the game's own files, which lives outside the Presets folder.</summary>
    public bool IsDefault { get; }

    public string LutPath => Path.Combine(FolderPath, LUTManager.FnLut);
    public string SkyPath => Path.Combine(FolderPath, LUTManager.FnSky);
    public string WaterPath => Path.Combine(FolderPath, LUTManager.FnWater);

    private readonly string? _imagePathOverride;

    /// <summary>
    /// The preview image. Bundled presets keep theirs beside their files; the Default
    /// preset's folder is a backup of game files and has no art of its own, so it is handed
    /// one from the app's assets instead.
    /// </summary>
    public string ImagePath => _imagePathOverride ?? Path.Combine(FolderPath, "image.png");

    public bool IsComplete =>
        File.Exists(LutPath) && File.Exists(SkyPath) && File.Exists(WaterPath);

    public LutPreset(string name, string folderPath,
                     string? imagePathOverride = null, bool isDefault = false)
    {
        Name = name;
        FolderPath = folderPath;
        IsDefault = isDefault;
        _imagePathOverride = imagePathOverride;
    }
}

/// <summary>
/// Everything the RTX LUT manager does to files: where presets come from, where the backup
/// of the game's own three files lives, which preset is currently installed, and the
/// elevated write that installs one.
///
/// <para><b>Why it's separate from the window.</b> None of this is UI - it is folder
/// scanning, SHA-256 comparison and one elevated copy. The window renders the list and
/// decides when these run. Split the same way Alchitex keeps its pipeline out of
/// AlchitexWindow, so that reading either half doesn't mean reading both.</para>
///
/// <para>Bound to one Minecraft install by <see cref="TryAttach"/>; nothing below works
/// until that has succeeded.</para>
/// </summary>
internal sealed class LUTManager
{
    // The game reads exactly these three, by these names, from data\ray_tracing. They are
    // public because DefaultsGuard needs the same three names to check the same folder
    // before a hard wipe - one spelling of them, not two that can drift.
    public const string FnLut = "look_up_tables.png";
    public const string FnSky = "sky.png";
    public const string FnWater = "water_n.tga";

    private const string FnPlaceholder = "placeholder.png";
    private const string FnDefaultImg = "default.png";

    /// <summary>Where the backup of the game's original three files lives, under LocalState.</summary>
    public const string DefaultsFolderName = "Lut_Defaults";

    /// <summary>
    /// Used to repair a game install that is missing its ray tracing files entirely. Any
    /// complete preset would do; this one is picked first only so the outcome is the same
    /// every time rather than depending on folder order.
    ///
    /// <para>It has to be a folder name that actually exists under <see cref="LutRootFolder"/>
    /// or the preference is silently dead and the alphabetical fallback below picks instead -
    /// which is what it had been doing, this having read "Gamescom 2019 Demo" while the folder
    /// on disk is "Gamescom 2019 Demo V2". Nothing breaks when they disagree, which is exactly
    /// why it went unnoticed.</para>
    /// </summary>
    private const string PreferredMendPreset = "Gamescom 2019 Demo V2";

    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    private readonly List<LutPreset> _presets = new();

    public string MinecraftRoot { get; private set; } = string.Empty;
    public string DefaultsFolder { get; private set; } = string.Empty;
    public string LutRootFolder { get; private set; } = string.Empty;
    public string PlaceholderImagePath { get; private set; } = string.Empty;
    public string DefaultImagePath { get; private set; } = string.Empty;

    public IReadOnlyList<LutPreset> Presets => _presets;

    public string DstLut => Path.Combine(MinecraftRoot, "data", "ray_tracing", FnLut);
    public string DstSky => Path.Combine(MinecraftRoot, "data", "ray_tracing", FnSky);
    public string DstWater => Path.Combine(MinecraftRoot, "data", "ray_tracing", FnWater);

    public string DefaultLut => Path.Combine(DefaultsFolder, FnLut);
    public string DefaultSky => Path.Combine(DefaultsFolder, FnSky);
    public string DefaultWater => Path.Combine(DefaultsFolder, FnWater);

    /// <summary>
    /// Points this instance at a Minecraft install and makes sure the defaults folder
    /// exists. False means that folder couldn't be created, which is fatal - without it
    /// there is nowhere to keep the user's route back to the game's original files.
    /// </summary>
    public bool TryAttach(string minecraftPath)
    {
        MinecraftRoot = minecraftPath;
        LutRootFolder = Path.Combine(AppDir, "Modules", "LUT", "Presets");
        PlaceholderImagePath = Path.Combine(LutRootFolder, FnPlaceholder);
        DefaultImagePath = Path.Combine(LutRootFolder, FnDefaultImg);

        Trace.WriteLine($"[LUTManager] Root     : {MinecraftRoot}");
        Trace.WriteLine($"[LUTManager] AppDir   : {AppDir}");
        Trace.WriteLine($"[LUTManager] LutRoot  : {LutRootFolder}");
        Trace.WriteLine($"[LUTManager] DstLut   : {DstLut}   exists={File.Exists(DstLut)}");
        Trace.WriteLine($"[LUTManager] DstSky   : {DstSky}   exists={File.Exists(DstSky)}");
        Trace.WriteLine($"[LUTManager] DstWater : {DstWater}  exists={File.Exists(DstWater)}");

        var defaultsFolder = EstablishDefaultsFolder();
        if (defaultsFolder == null)
            return false;

        DefaultsFolder = defaultsFolder;
        return true;
    }

    private static string? EstablishDefaultsFolder()
    {
        try
        {
            var location = Path.Combine(ApplicationData.Current.LocalFolder.Path, DefaultsFolderName);
            Directory.CreateDirectory(location);
            Trace.WriteLine($"[LUTManager] Defaults folder: {location}");
            return location;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Failed to create defaults folder: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Backing up (and mending) the game's own three files
    // -------------------------------------------------------------------------

    /// <summary>
    /// Makes sure Lut_Defaults holds a complete copy of the game's original three files -
    /// the only way back to stock once a preset has been installed.
    ///
    /// <para>All-or-none in both directions: a partial backup is overwritten wholesale
    /// rather than topped up, because three files from two different game versions is not a
    /// state anything could restore from. If the *game* is the one missing files, there is
    /// nothing worth backing up, so this mends the install from a bundled preset instead -
    /// a known-good set of three beats leaving the renderer with an incomplete one.</para>
    /// </summary>
    public async Task EnsureDefaultsBackedUpAsync()
    {
        bool allBackupsPresent =
            File.Exists(DefaultLut) && File.Exists(DefaultSky) && File.Exists(DefaultWater);

        if (allBackupsPresent)
        {
            Trace.WriteLine("[LUTManager] Default backup already complete - skipping");
            return;
        }

        Trace.WriteLine("[LUTManager] Default backup incomplete - attempting from game files");

        bool allGameFilesPresent =
            File.Exists(DstLut) && File.Exists(DstSky) && File.Exists(DstWater);

        if (allGameFilesPresent)
        {
            await Task.Run(() =>
            {
                try
                {
                    File.Copy(DstLut, DefaultLut, overwrite: true);
                    File.Copy(DstSky, DefaultSky, overwrite: true);
                    File.Copy(DstWater, DefaultWater, overwrite: true);
                    Trace.WriteLine("[LUTManager] Default backup created from game files");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[LUTManager] Backup error: {ex.Message}");
                }
            });
        }
        else
        {
            Trace.WriteLine("[LUTManager] Game files missing - mending from bundled preset");

            string? mendLut = null, mendSky = null, mendWater = null;

            if (Directory.Exists(LutRootFolder))
            {
                var preferred = Path.Combine(LutRootFolder, PreferredMendPreset);
                var prefLut = Path.Combine(preferred, FnLut);
                var prefSky = Path.Combine(preferred, FnSky);
                var prefWater = Path.Combine(preferred, FnWater);

                if (File.Exists(prefLut) && File.Exists(prefSky) && File.Exists(prefWater))
                {
                    mendLut = prefLut;
                    mendSky = prefSky;
                    mendWater = prefWater;
                    Trace.WriteLine("[LUTManager] Mending with preferred preset [" + PreferredMendPreset + "]");
                }

                if (mendLut == null)
                {
                    foreach (var dir in Directory.GetDirectories(LutRootFolder)
                                                 .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    {
                        var lut = Path.Combine(dir, FnLut);
                        var sky = Path.Combine(dir, FnSky);
                        var water = Path.Combine(dir, FnWater);
                        if (File.Exists(lut) && File.Exists(sky) && File.Exists(water))
                        {
                            mendLut = lut;
                            mendSky = sky;
                            mendWater = water;
                            Trace.WriteLine("[LUTManager] Mending with fallback preset [" + Path.GetFileName(dir) + "]");
                            break;
                        }
                    }
                }
            }

            if (mendLut != null && mendSky != null && mendWater != null)
            {
                bool mended = await ReplaceRtxFilesWithElevation(mendLut, mendSky, mendWater);
                Trace.WriteLine(mended ? "LUTM: Game mended" : "LUTM: Mend failed or cancelled");
            }
            else
            {
                Trace.WriteLine("[LUTManager] No complete presets found for mending - user must install manually");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Preset discovery and detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds <see cref="Presets"/>: the Default backup first, then every subfolder of
    /// Modules\LUT\Presets in name order. Incomplete ones are kept in the list deliberately - the
    /// window shows them greyed out, which says more than silently omitting them would.
    /// </summary>
    public void LoadPresets()
    {
        _presets.Clear();

        var defaultPreset = new LutPreset("Default", DefaultsFolder, DefaultImagePath, isDefault: true);
        _presets.Add(defaultPreset);
        Trace.WriteLine($"[LUTManager] Default preset — complete={defaultPreset.IsComplete}  folder={DefaultsFolder}");

        if (Directory.Exists(LutRootFolder))
        {
            foreach (var dir in Directory.GetDirectories(LutRootFolder)
                                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var preset = new LutPreset(name, dir);
                _presets.Add(preset);
                Trace.WriteLine($"[LUTManager] Preset [{name}] complete={preset.IsComplete} folder={dir}");
            }
        }
        else
        {
            Trace.WriteLine($"[LUTManager] LUT folder not found: {LutRootFolder}");
        }

        Trace.WriteLine($"[LUTManager] {_presets.Count} preset(s) loaded");
    }

    /// <summary>
    /// Which preset the game is currently running, by hashing its three files against each
    /// complete preset's. Null means none of them matched - a hand-modified install, or a
    /// preset that isn't ours.
    /// </summary>
    public async Task<LutPreset?> DetectCurrentPresetAsync()
    {
        if (!File.Exists(DstLut) || !File.Exists(DstSky) || !File.Exists(DstWater))
        {
            Trace.WriteLine("[LUTManager] One or more game files missing - cannot detect preset");
            return null;
        }

        return await Task.Run(() =>
        {
            foreach (var preset in _presets.Where(p => p.IsComplete))
            {
                if (HashesMatch(DstLut, preset.LutPath) &&
                    HashesMatch(DstSky, preset.SkyPath) &&
                    HashesMatch(DstWater, preset.WaterPath))
                {
                    return preset;
                }
            }
            return null;
        });
    }

    /// <summary>
    /// Which image the window should show for a preset, falling back to the placeholder
    /// whenever a preset doesn't ship one. Lives here rather than in the window because it
    /// is a question about the preset folder layout, not about how the image is displayed.
    /// </summary>
    public string ResolveImagePath(LutPreset? preset)
    {
        if (preset == null)
            return PlaceholderImagePath;

        var imagePath = preset.IsDefault ? DefaultImagePath : preset.ImagePath;

        return string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)
            ? PlaceholderImagePath
            : imagePath;
    }

    // -------------------------------------------------------------------------
    // Installing
    // -------------------------------------------------------------------------

    public Task<bool> InstallAsync(LutPreset preset) =>
        ReplaceRtxFilesWithElevation(preset.LutPath, preset.SkyPath, preset.WaterPath);

    /// <summary>
    /// All three files in one elevated batch, so the user sees a single UAC prompt and the
    /// game can never end up with a half-applied preset because the second write was the
    /// one that was declined.
    /// </summary>
    private Task<bool> ReplaceRtxFilesWithElevation(string srcLut, string srcSky, string srcWater)
    {
        Trace.WriteLine("[LUTManager] ReplaceRtxFilesWithElevation");
        Trace.WriteLine("  srcLut  =" + srcLut + "  exists=" + File.Exists(srcLut));
        Trace.WriteLine("  srcSky  =" + srcSky + "  exists=" + File.Exists(srcSky));
        Trace.WriteLine("  srcWater=" + srcWater + "  exists=" + File.Exists(srcWater));
        Trace.WriteLine("  dstLut  =" + DstLut);
        Trace.WriteLine("  dstSky  =" + DstSky);
        Trace.WriteLine("  dstWater=" + DstWater);

        if (!File.Exists(srcLut)) { Trace.WriteLine("[LUTManager] Aborting - srcLut missing"); return Task.FromResult(false); }
        if (!File.Exists(srcSky)) { Trace.WriteLine("[LUTManager] Aborting - srcSky missing"); return Task.FromResult(false); }
        if (!File.Exists(srcWater)) { Trace.WriteLine("[LUTManager] Aborting - srcWater missing"); return Task.FromResult(false); }

        var files = new List<(string, string)>
        {
            (srcLut,   DstLut),
            (srcSky,   DstSky),
            (srcWater, DstWater)
        };
        return Helpers.ReplaceFilesWithElevation(files, "[LUTManager]", "rtx_defaults");
    }

    // -------------------------------------------------------------------------
    // Hash comparison  (SHA-256)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Byte-identical? Also used by DefaultsGuard to decide whether a hard wipe is about to
    /// strand the user on a non-default preset.
    /// </summary>
    public static bool HashesMatch(string pathA, string pathB)
    {
        using var sha = SHA256.Create();
        using var streamA = File.OpenRead(pathA);
        var hashA = sha.ComputeHash(streamA);
        sha.Initialize();
        using var streamB = File.OpenRead(pathB);
        var hashB = sha.ComputeHash(streamB);
        return System.MemoryExtensions.SequenceEqual(
            (System.ReadOnlySpan<byte>)hashA,
            (System.ReadOnlySpan<byte>)hashB);
    }
}
