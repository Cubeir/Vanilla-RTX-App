using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.LUT;

// Maybe the same game version change detection and Default preset re-establishment of BetterRTX manager must be deployed here as well.
// Esepcially if down the line Mojang starts updating the luts after 7 years.
// The only reason it was held back is because you weren't sure if game updates actually revert lut files to default or not...
// In case of BetterRTX, it was certain material.bin files go back to defaults upon game updates, here, we don't know.

/// <summary>
/// One LUT preset: a folder holding some of the five files the game's ray tracing folder
/// reads. Only <see cref="LUTManager.RequiredFiles"/> and a preview image make a preset
/// offerable (<see cref="IsComplete"/>); the other three are installed when a preset ships
/// them and left alone when it doesn't.
///
/// <para><b>Why only two are required.</b> look_up_tables.png and sky.png are what a LUT
/// preset <i>is</i> - the colour grading and the sky. caustics.png, water_n.tga and
/// wibbly.png are separate effects a preset may have no opinion about; requiring all five
/// would mean shipping copies of the game's own files to fill the gaps. Omitting one means
/// "leave whatever is installed alone", which is what lets a preset be partial on
/// purpose.</para>
/// </summary>
internal sealed class LutPreset
{
    public string Name { get; }
    public string FolderPath { get; }

    /// <summary>True only for the backup of the game's own files, which lives outside the Presets folder.</summary>
    public bool IsDefault { get; }

    private readonly string? _imagePathOverride;

    public LutPreset(string name, string folderPath,
                     string? imagePathOverride = null, bool isDefault = false)
    {
        Name = name;
        FolderPath = folderPath;
        IsDefault = isDefault;
        _imagePathOverride = imagePathOverride;
    }

    /// <summary>That file's path if this preset ships it, null if it doesn't.</summary>
    public string? ResolveFile(string fileName)
    {
        var path = Path.Combine(FolderPath, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Every game file this preset actually carries, in <see cref="LUTManager.AllFiles"/>
    /// order. This is exactly what an install writes and what detection compares - a preset
    /// is "installed" when the files it has are the ones in the game, and it has nothing to
    /// say about the ones it doesn't.
    /// </summary>
    public List<string> PresentFiles =>
        LUTManager.AllFiles.Select(ResolveFile).Where(p => p != null).Select(p => p!).ToList();

    public bool HasRequiredFiles => LUTManager.RequiredFiles.All(f => ResolveFile(f) != null);

    /// <summary>
    /// The preview image. Bundled presets keep theirs beside their files under any of
    /// <see cref="LUTManager.ImageExtensions"/>; the Default preset's folder is a backup of
    /// game files and has no art of its own, so it is handed one from the app's assets.
    /// Null when there isn't one.
    /// </summary>
    public string? ImagePath => _imagePathOverride ?? LUTManager.FindImage(FolderPath, "image");

    /// <summary>
    /// Offerable: it has the two files that define a preset, and something to show for it.
    ///
    /// <para><b>Default is exempt from the image half</b> - its picture is a bundled asset
    /// with a placeholder behind it, so a missing thumbnail must never be able to block a
    /// rollback to the user's own game files.</para>
    /// </summary>
    public bool IsComplete => HasRequiredFiles && (IsDefault || ImagePath != null);
}

/// <summary>
/// Everything the RTX LUT manager does to files: where presets come from, where the backup
/// of the game's own files lives, which preset is currently installed, and the elevated
/// write that installs one.
///
/// <para><b>Why it's separate from the window.</b> None of this is UI - it is folder
/// scanning, SHA-256 comparison and one elevated copy. The window renders the list and
/// decides when these run. Split the same way Alchitex keeps its pipeline out of
/// AlchitexWindow, so that reading either half doesn't mean reading both.</para>
///
/// <para>Bound to one edition's install by <see cref="TryAttach"/>; nothing below works
/// until that has succeeded.</para>
/// </summary>
internal sealed class LUTManager
{
    // The game reads exactly these, by these names, from data\ray_tracing. They are public
    // because DefaultsGuard needs the same names for the same folder before a hard wipe -
    // one spelling of them, not two that can drift.
    public const string FnLut = "look_up_tables.png";
    public const string FnSky = "sky.png";
    public const string FnWater = "water_n.tga";
    public const string FnCaustics = "caustics.png";
    public const string FnWibbly = "wibbly.png";

    /// <summary>What a preset must ship to be installable at all - see <see cref="LutPreset"/>.</summary>
    public static readonly string[] RequiredFiles = [FnLut, FnSky];

    /// <summary>Installed when a preset ships them, left alone when it doesn't.</summary>
    public static readonly string[] OptionalFiles = [FnCaustics, FnWater, FnWibbly];

    /// <summary>
    /// Every file the game reads out of data\ray_tracing. <b>All of these are backed up and
    /// all of them are restored</b>, regardless of how few a preset needs: the backup's job
    /// is to undo whatever any preset did, and a preset that ships caustics.png can only be
    /// undone by a backup that has one.
    /// </summary>
    public static readonly string[] AllFiles = [.. RequiredFiles, .. OptionalFiles];

    /// <summary>
    /// Preview-image formats, in priority order. PNG first because that is what everything
    /// bundled today is and lossless is the right default for flat colour art; JPEG accepted
    /// because a screenshot-derived preview is usually one already and re-encoding it to PNG
    /// costs size for nothing.
    /// </summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    private const string FnPlaceholderBase = "placeholder";
    private const string FnDefaultImgBase = "default";

    /// <summary>Where Release's backup of the game's original files lives, under LocalState.</summary>
    public const string DefaultsFolderName = "Lut_Defaults";

    /// <summary>
    /// Preview's own backup folder.
    ///
    /// <para>Kept apart from Release's even though the LUT files themselves rarely change
    /// between builds: one folder for both editions would let whichever installed a preset
    /// first define "default" for the other, and make a rollback restore one install's files
    /// into the other's.</para>
    /// </summary>
    public const string PreviewDefaultsFolderName = "Lut_Defaults_Preview";

    public static string GetDefaultsFolderName(bool isPreview) =>
        isPreview ? PreviewDefaultsFolderName : DefaultsFolderName;

    /// <summary>
    /// Where an edition's backup sits, resolved without attaching to anything - DefaultsGuard
    /// runs before a hard wipe with no manager instance in hand.
    /// </summary>
    public static string? GetDefaultsFolderPath(bool isPreview)
    {
        try
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, GetDefaultsFolderName(isPreview));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Could not resolve defaults folder path: {ex.Message}");
            return null;
        }
    }

    /// <summary>data\ray_tracing inside a game install - where these files live.</summary>
    public static string GameFilePath(string minecraftRoot, string fileName) =>
        Path.Combine(minecraftRoot, "data", "ray_tracing", fileName);

    /// <summary>
    /// The first of <see cref="ImageExtensions"/> that exists for this base name, or null.
    /// Used for a preset's own image.*, and for the bundled placeholder.* and default.*.
    /// </summary>
    public static string? FindImage(string folder, string baseName)
    {
        if (string.IsNullOrEmpty(folder)) return null;

        foreach (var extension in ImageExtensions)
        {
            var candidate = Path.Combine(folder, baseName + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Used to repair a game install that is missing its ray tracing files entirely. Any
    /// preset with the required files would do; this one is picked first only so the outcome
    /// is the same every time rather than depending on folder order.
    ///
    /// <para>It has to name a folder that exists under <see cref="LutRootFolder"/>. A name
    /// that doesn't match makes the preference silently dead and hands the choice to the
    /// alphabetical fallback below - nothing breaks, so the two drifting apart is invisible
    /// unless checked.</para>
    /// </summary>
    private const string PreferredMendPreset = "Gamescom 2019 Demo V2";

    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    private readonly List<LutPreset> _presets = new();

    public bool IsPreview { get; private set; }
    public string MinecraftRoot { get; private set; } = string.Empty;
    public string DefaultsFolder { get; private set; } = string.Empty;
    public string LutRootFolder { get; private set; } = string.Empty;
    public string? PlaceholderImagePath { get; private set; }
    public string? DefaultImagePath { get; private set; }

    public IReadOnlyList<LutPreset> Presets => _presets;

    public string DstPath(string fileName) => GameFilePath(MinecraftRoot, fileName);
    public string DefaultPath(string fileName) => Path.Combine(DefaultsFolder, fileName);

    /// <summary>
    /// Whether the backup holds what a rollback needs. False is the window's cue to disable
    /// installing outright: writing shader files into somebody's game with no way back is the
    /// one thing this feature must never do.
    /// </summary>
    public bool DefaultsComplete => RequiredFiles.All(f => File.Exists(DefaultPath(f)));

    /// <summary>Why <see cref="DefaultsComplete"/> is what it is, for the window's notice.</summary>
    public enum DefaultsState
    {
        /// <summary>The backup holds what a rollback needs.</summary>
        Ready,

        /// <summary>The game is missing required files and couldn't be mended.</summary>
        GameFilesMissing,

        /// <summary>
        /// There is no backup to fall back on and the game is demonstrably not running its
        /// own files, so taking one now would record somebody else's preset as the user's
        /// originals - see <see cref="MatchBundledPreset"/>.
        /// </summary>
        GameRunningAPreset,

        /// <summary>The copy itself failed - disk, permissions, a locked file.</summary>
        BackupFailed
    }

    /// <summary>The result of the last <see cref="EnsureDefaultsBackedUpAsync"/> call.</summary>
    public DefaultsState Defaults { get; private set; } = DefaultsState.GameFilesMissing;

    /// <summary>
    /// Points this instance at one edition's install and makes sure that edition's defaults
    /// folder exists. False means the folder couldn't be created, which is fatal - without it
    /// there is nowhere to keep the user's route back to the game's original files.
    /// </summary>
    public bool TryAttach(string minecraftPath, bool isPreview)
    {
        IsPreview = isPreview;
        MinecraftRoot = minecraftPath;
        LutRootFolder = Path.Combine(AppDir, "Modules", "LUT", "Presets");
        PlaceholderImagePath = FindImage(LutRootFolder, FnPlaceholderBase);
        DefaultImagePath = FindImage(LutRootFolder, FnDefaultImgBase);

        Trace.WriteLine($"[LUTManager] Edition  : {(isPreview ? "Preview" : "Release")}");
        Trace.WriteLine($"[LUTManager] Root     : {MinecraftRoot}");
        Trace.WriteLine($"[LUTManager] AppDir   : {AppDir}");
        Trace.WriteLine($"[LUTManager] LutRoot  : {LutRootFolder}");
        foreach (var fileName in AllFiles)
            Trace.WriteLine($"[LUTManager] Game     : {fileName}  exists={File.Exists(DstPath(fileName))}");

        var defaultsFolder = EstablishDefaultsFolder(isPreview);
        if (defaultsFolder == null)
            return false;

        DefaultsFolder = defaultsFolder;
        return true;
    }

    private static string? EstablishDefaultsFolder(bool isPreview)
    {
        try
        {
            var location = GetDefaultsFolderPath(isPreview);
            if (location == null) return null;

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
    // Backing up (and mending) the game's own files
    // -------------------------------------------------------------------------

    /// <summary>
    /// Makes sure this edition's defaults folder holds a copy of every ray tracing file the
    /// game has - the only way back to stock once a preset has been installed.
    ///
    /// <para>Three cases, and the middle one is the one worth reading:</para>
    /// <list type="bullet">
    /// <item>The <i>game</i> is missing a required file - there is nothing worth backing up,
    /// so this mends the install from a bundled preset instead and then backs that up. A
    /// known-good set beats leaving the renderer with an incomplete one.</item>
    /// <item>The backup is missing a required file - it is replaced wholesale rather than
    /// topped up, because files from two different game versions is not a state anything
    /// could restore from.</item>
    /// <item>The backup has the required files but not every optional one. The gaps are
    /// filled from the game, <b>but only if what is already held still matches it</b>.
    /// Matching establishes that the game is on its own defaults, so what it holds now is
    /// genuinely default; not matching means a preset is installed, and copying caustics.png
    /// out of it would record that preset's file as the user's original permanently.</item>
    /// </list>
    /// </summary>
    public async Task<DefaultsState> EnsureDefaultsBackedUpAsync()
    {
        bool justMended = false;

        if (RequiredFiles.Any(f => !File.Exists(DstPath(f))))
        {
            Trace.WriteLine("[LUTManager] Game is missing required ray tracing files - mending from a bundled preset");
            justMended = await MendGameFilesAsync();
        }

        var gameFiles = AllFiles.Where(f => File.Exists(DstPath(f))).ToList();

        if (RequiredFiles.Any(f => !gameFiles.Contains(f)))
        {
            Trace.WriteLine("[LUTManager] Game still missing required ray tracing files after mending - nothing worth backing up");
            return Defaults = DefaultsState.GameFilesMissing;
        }

        var backedUp = AllFiles.Where(f => File.Exists(DefaultPath(f))).ToList();
        bool backupUsable = RequiredFiles.All(backedUp.Contains);

        if (!backupUsable)
            return Defaults = await TakeFreshBackupAsync(gameFiles, justMended);

        var missingFromBackup = gameFiles.Where(f => !backedUp.Contains(f)).ToList();
        if (missingFromBackup.Count == 0)
        {
            Trace.WriteLine("[LUTManager] Default backup already complete - skipping");
            return Defaults = DefaultsState.Ready;
        }

        // Only the game's own files may be added to a backup of the game's own files.
        return Defaults = await Task.Run(() =>
        {
            try
            {
                foreach (var fileName in backedUp)
                {
                    if (!HashesMatch(DstPath(fileName), DefaultPath(fileName)))
                    {
                        Trace.WriteLine($"[LUTManager] Backup is missing {string.Join(", ", missingFromBackup)}, but the game no longer matches it on {fileName} " +
                                        "- a preset is installed, so those files would not be defaults. Leaving the backup as it is.");
                        return DefaultsState.Ready; // the rollback itself is intact, which is what Ready means
                    }
                }

                foreach (var fileName in missingFromBackup)
                    File.Copy(DstPath(fileName), DefaultPath(fileName), overwrite: true);

                Trace.WriteLine($"[LUTManager] Default backup topped up with {string.Join(", ", missingFromBackup)}");
                return DefaultsState.Ready;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LUTManager] Backup top-up error: {ex.Message}");
                return DefaultsState.Ready; // required files are still backed up - a rollback still works
            }
        });
    }

    /// <summary>
    /// Takes the backup from scratch, all-or-none: whatever partial set is there is cleared
    /// first, so the folder can't end up holding one file from before and four from now.
    ///
    /// <para><b>Unless the game is demonstrably not running its own files.</b> With no backup
    /// to compare against, "whatever the game holds is its default" is normally the only
    /// assumption available - but a bundled preset is a known set of bytes, so a match proves
    /// the install is not stock, and copying it in would make that preset permanent.</para>
    ///
    /// <para>The other edition's backup is the fallback: the same file set a rollback would
    /// have used while the two editions shared one folder. Failing that, nothing is written
    /// and the window blocks installing - offering no rollback is a smaller harm than
    /// offering a broken one.</para>
    ///
    /// <para><paramref name="justMended"/> skips the check, and must: mending puts a bundled
    /// preset into the game deliberately, so the game matches one by construction afterwards,
    /// and that set is the best "original" such an install has.</para>
    /// </summary>
    private async Task<DefaultsState> TakeFreshBackupAsync(List<string> gameFiles, bool justMended)
    {
        List<string> sourceFiles = gameFiles;
        string sourceFolder = Path.Combine(MinecraftRoot, "data", "ray_tracing");
        string origin = "the game";

        if (!justMended)
        {
            var impostor = MatchBundledPreset();
            if (impostor != null)
            {
                var donorFolder = GetDefaultsFolderPath(!IsPreview);
                bool donorUsable = donorFolder != null
                    && RequiredFiles.All(f => File.Exists(Path.Combine(donorFolder, f)));

                if (!donorUsable)
                {
                    Trace.WriteLine($"[LUTManager] ✗ No backup, and the game is running bundled preset [{impostor.Name}] - " +
                                    "backing that up would make it permanent. Nothing written.");
                    return DefaultsState.GameRunningAPreset;
                }

                Trace.WriteLine($"[LUTManager] Game is running bundled preset [{impostor.Name}] and this edition has no backup - " +
                                $"seeding from {GetDefaultsFolderName(!IsPreview)}, which is what the rollback used while the two folders were shared.");

                sourceFolder = donorFolder!;
                sourceFiles = AllFiles.Where(f => File.Exists(Path.Combine(donorFolder!, f))).ToList();
                origin = GetDefaultsFolderName(!IsPreview);
            }
        }

        return await Task.Run(() =>
        {
            try
            {
                foreach (var fileName in AllFiles)
                {
                    var stale = DefaultPath(fileName);
                    if (File.Exists(stale)) File.Delete(stale);
                }

                foreach (var fileName in sourceFiles)
                    File.Copy(Path.Combine(sourceFolder, fileName), DefaultPath(fileName), overwrite: true);

                Trace.WriteLine($"[LUTManager] Default backup created from {sourceFiles.Count} file(s) out of {origin}");
                return DefaultsState.Ready;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LUTManager] Backup error: {ex.Message}");
                return DefaultsState.BackupFailed;
            }
        });
    }

    /// <summary>
    /// The bundled preset the game's files currently match, or null. A match proves the game
    /// is <i>not</i> running its own originals - the only thing establishable about an install
    /// with no backup to compare against, since a bundled preset is a known set of bytes and
    /// none of them is anything Mojang shipped.
    ///
    /// <para>Reads the presets folder directly rather than <see cref="Presets"/>, which runs
    /// later: tying the backup to list-building order would make the safety-critical half
    /// depend on the cosmetic one.</para>
    /// </summary>
    private LutPreset? MatchBundledPreset()
    {
        if (!Directory.Exists(LutRootFolder))
            return null;

        try
        {
            foreach (var dir in Directory.GetDirectories(LutRootFolder))
            {
                var preset = new LutPreset(Path.GetFileName(dir), dir);
                if (!preset.HasRequiredFiles) continue;

                bool allMatch = true;
                foreach (var presetFile in preset.PresentFiles)
                {
                    var gameFile = DstPath(Path.GetFileName(presetFile));
                    if (!File.Exists(gameFile) || !HashesMatch(gameFile, presetFile))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch) return preset;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Could not compare the game against bundled presets: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Puts a known-good set of ray tracing files into a game that is missing them, from
    /// whichever bundled preset has the required ones. One elevated write.
    /// </summary>
    private async Task<bool> MendGameFilesAsync()
    {
        if (!Directory.Exists(LutRootFolder))
        {
            Trace.WriteLine($"[LUTManager] LUT folder not found, cannot mend: {LutRootFolder}");
            return false;
        }

        var candidates = new List<string>();

        var preferred = Path.Combine(LutRootFolder, PreferredMendPreset);
        if (Directory.Exists(preferred)) candidates.Add(preferred);

        candidates.AddRange(Directory.GetDirectories(LutRootFolder)
                                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));

        foreach (var dir in candidates)
        {
            var preset = new LutPreset(Path.GetFileName(dir), dir);
            if (!preset.HasRequiredFiles) continue;

            // Straight to the write rather than through InstallAsync: its Default underlay is
            // what does not exist yet at this point, and a stale backup is not a safe source
            // to fill a broken install from.
            Trace.WriteLine($"[LUTManager] Mending with preset [{preset.Name}]");
            bool mended = await WriteToGameAsync(preset.Name, preset.PresentFiles);
            Trace.WriteLine(mended ? "[LUTManager] Game mended" : "[LUTManager] Mend failed or cancelled");
            return mended;
        }

        Trace.WriteLine("[LUTManager] No bundled preset has the required files - user must install manually");
        return false;
    }

    // -------------------------------------------------------------------------
    // Preset discovery and detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds <see cref="Presets"/>: the Default backup first, then every subfolder of
    /// Modules\LUT\Presets in name order. Incomplete ones are kept in the list deliberately -
    /// the window shows them greyed out, which says more than silently omitting them would.
    /// </summary>
    public void LoadPresets()
    {
        _presets.Clear();

        var defaultPreset = new LutPreset("Default", DefaultsFolder, DefaultImagePath, isDefault: true);
        _presets.Add(defaultPreset);
        Trace.WriteLine($"[LUTManager] Default preset — complete={defaultPreset.IsComplete}  files={defaultPreset.PresentFiles.Count}  folder={DefaultsFolder}");

        if (Directory.Exists(LutRootFolder))
        {
            foreach (var dir in Directory.GetDirectories(LutRootFolder)
                                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var preset = new LutPreset(name, dir);
                _presets.Add(preset);
                Trace.WriteLine($"[LUTManager] Preset [{name}] complete={preset.IsComplete} files={preset.PresentFiles.Count} folder={dir}");
            }
        }
        else
        {
            Trace.WriteLine($"[LUTManager] LUT folder not found: {LutRootFolder}");
        }

        Trace.WriteLine($"[LUTManager] {_presets.Count} preset(s) loaded");
    }

    /// <summary>
    /// Which preset the game is currently running, by hashing the game's files against each
    /// complete preset's.
    ///
    /// <para><b>A preset matches when the game is byte-for-byte what installing it would
    /// produce</b> - its own files where it ships them and the Default backup's everywhere
    /// else, via <see cref="BuildInstallSources"/>. Comparing only the files a preset ships
    /// would call it installed even if a slot it leaves alone had since been changed by
    /// something outside this app, which is a state no install of that preset ever
    /// produces.</para>
    ///
    /// <para>Default is checked first, which settles the one ambiguity this creates: a wholly
    /// stock install reads as Default. Null means nothing matched - a hand-modified install,
    /// or a preset that isn't ours.</para>
    /// </summary>
    public async Task<LutPreset?> DetectCurrentPresetAsync()
    {
        if (RequiredFiles.Any(f => !File.Exists(DstPath(f))))
        {
            Trace.WriteLine("[LUTManager] Required game files missing - cannot detect preset");
            return null;
        }

        return await Task.Run(() =>
        {
            foreach (var preset in _presets.Where(p => p.IsComplete))
            {
                var expected = BuildInstallSources(preset);
                if (expected.Count == 0) continue;

                bool allMatch = true;

                foreach (var sourceFile in expected)
                {
                    var gameFile = DstPath(Path.GetFileName(sourceFile));
                    if (!File.Exists(gameFile) || !HashesMatch(gameFile, sourceFile))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                    return preset;
            }

            return null;
        });
    }

    /// <summary>
    /// Which image the window should show for a preset, falling back to the bundled
    /// placeholder whenever a preset doesn't ship one. Lives here rather than in the window
    /// because it is a question about the preset folder layout, not about how the image is
    /// displayed. Null when even the placeholder is missing.
    /// </summary>
    public string? ResolveImagePath(LutPreset? preset) => preset?.ImagePath ?? PlaceholderImagePath;

    // -------------------------------------------------------------------------
    // Installing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Installs a preset over the Default files rather than over whatever happens to be
    /// there: every slot this preset doesn't fill is written from the backup.
    ///
    /// <para><b>Without the underlay, switching presets accumulates.</b> A preset shipping
    /// caustics.png and water_n.tga followed by one shipping only wibbly.png leaves the
    /// first's two files in place - the second has no opinion about them, so nothing
    /// overwrites them and the game ends up running two presets mixed together. Going through
    /// Default is what makes "installed preset" mean the same thing every time.</para>
    ///
    /// <para>One merged file list rather than two installs: same end state, but one UAC
    /// prompt and no window in which the game is half reverted. Default skips the merge - it
    /// <i>is</i> the underlay.</para>
    ///
    /// <para>A slot the backup doesn't hold either is left alone; that can only happen for a
    /// file the game lacked when the backup was taken.</para>
    /// </summary>
    public Task<bool> InstallAsync(LutPreset preset)
    {
        if (!preset.HasRequiredFiles)
        {
            Trace.WriteLine($"[LUTManager] Aborting - [{preset.Name}] is missing {string.Join(" / ", RequiredFiles.Where(f => preset.ResolveFile(f) == null))}");
            return Task.FromResult(false);
        }

        return WriteToGameAsync(preset.Name, BuildInstallSources(preset));
    }

    /// <summary>
    /// The source file for every slot installing <paramref name="preset"/> would write: the
    /// preset's own where it ships one, the Default backup's everywhere else. A slot neither
    /// holds is absent, because there is nothing to write there.
    ///
    /// <para><b>Install and detection both go through this, and that is the point.</b> The
    /// question "what does the game look like after installing X" has one answer, so
    /// <see cref="DetectCurrentPresetAsync"/> cannot drift from what
    /// <see cref="InstallAsync"/> actually writes. Comparing a preset's own files alone would
    /// report it as installed even when a slot it doesn't ship had been changed by something
    /// else - the game would not be in the state installing it produces, but detection would
    /// say it was.</para>
    ///
    /// <para>The Default preset needs no special case: every slot it has resolves to itself.</para>
    /// </summary>
    private List<string> BuildInstallSources(LutPreset preset)
    {
        var sources = new List<string>();

        foreach (var fileName in AllFiles)
        {
            var fromPreset = preset.ResolveFile(fileName);
            if (fromPreset != null)
            {
                sources.Add(fromPreset);
                continue;
            }

            var fromDefault = DefaultPath(fileName);
            if (File.Exists(fromDefault))
                sources.Add(fromDefault);
        }

        return sources;
    }

    /// <summary>
    /// The write itself: all of them in one elevated batch, so the user sees a single UAC
    /// prompt and the game can never end up half-applied because the second copy was the one
    /// that was declined.
    /// </summary>
    private Task<bool> WriteToGameAsync(string label, List<string> sources)
    {
        Trace.WriteLine($"[LUTManager] Installing [{label}] - {sources.Count} file(s)");

        var files = new List<(string, string)>();
        foreach (var source in sources)
        {
            var fileName = Path.GetFileName(source);
            Trace.WriteLine($"[LUTManager]   {fileName} <- {Path.GetFileName(Path.GetDirectoryName(source))}");
            files.Add((source, DstPath(fileName)));
        }

        return Helpers.ReplaceFilesWithElevation(files, "[LUTManager]", "rtx_defaults");
    }

    // -------------------------------------------------------------------------
    // Hash comparison  (SHA-256)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Byte-identical? Also used by DefaultsGuard to decide whether a hard wipe is about to
    /// strand the user on a non-default preset. False rather than throwing for a file that
    /// can't be read - every caller is asking "are these the same?", and "couldn't tell" has
    /// to answer no there.
    /// </summary>
    public static bool HashesMatch(string pathA, string pathB)
    {
        try
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
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Hash comparison failed ({Path.GetFileName(pathA)}): {ex.Message}");
            return false;
        }
    }
}
