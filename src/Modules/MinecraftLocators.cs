using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Vanilla_RTX_App.Core;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Provides tools for locating Minecraft (Bedrock) and Minecraft Preview installations.
/// Handles caching, validation, system-wide searching, and manual selection.
///
/// Contract: every path returned or cached by this class is the PHYSICAL directory
/// containing Minecraft.Windows.exe - i.e. the Content subfolder of the install root.
/// Callers reference files as Path.Combine(installPath, "filename") directly.
/// No symlinks or junctions are ever stored - all paths are resolved to physical targets.
///
/// Edition detection (Preview vs Stable) is authoritative, not name-based: every
/// GDK Minecraft install ships a MicrosoftGame.Config next to the exe whose
/// <Identity Name="..."/> attribute is "Microsoft.MinecraftUWP" (stable) or
/// "Microsoft.MinecraftWindowsBeta" (preview). This value is baked in by Mojang/Microsoft
/// and is independent of folder names, GUIDs, drive letters, or which launcher installed it.
/// Folder names and known package GUIDs are used only as fast-path optimizations to try
/// first - they are never required for correctness.
///
/// Location flow:
///   Phase 1 (startup, fast):
///     Cache check → Stage 0: PackageManager → Stage 1: Common locations
///   Phase 2 (async, slow):
///     System-wide recursive search across all fixed drives - matches on the
///     presence of Minecraft.Windows.exe + a MicrosoftGame.Config with the
///     correct Identity, never on folder name.
///   Phase 3 (manual):
///     User picks Minecraft.Windows.exe - directory is validated and cached
/// </summary>
public static class MinecraftGDKLocator
{
    public const string MinecraftFolderName = "Minecraft for Windows";
    public const string MinecraftPreviewFolderName = "Minecraft Preview for Windows";
    public const string MinecraftExecutableName = "Minecraft.Windows.exe";
    private const string GameConfigFileName = "MicrosoftGame.Config";
    private const int MaxSearchDepth = 9;

    // Package family names
    private const string MinecraftStablePackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string MinecraftPreviewPackageFamilyName = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";

    // MicrosoftGame.Config <Identity Name="..."/> values - the authoritative,
    // folder-name-independent way to tell stable and preview apart. These are
    // the same identity strings the package family names above are built from,
    // and they have remained unchanged even through the "Beta" → "Preview" rebrand.
    private const string MinecraftStableIdentityName = "Microsoft.MinecraftUWP";
    private const string MinecraftPreviewIdentityName = "Microsoft.MinecraftWindowsBeta";

    // Known Microsoft Store install GUIDs used in place of friendly folder names
    // by some install paths. Treated as fully interchangeable with the friendly
    // names below - both are just fast-path hints, never a requirement.
    private const string MinecraftStableStoreGuid = "7792D9CE-355A-493C-AFBD-768F4A77C3B0";
    private const string MinecraftPreviewStoreGuid = "98BD2335-9B01-4E4C-BD05-CCC01614078B";

    private static readonly HashSet<string> FoldersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "WinSxS", "$Recycle.Bin", "ProgramData",
        "AppData", "Recovery", "System Volume Information", "Config.Msi",
        "Windows.old", "PerfLogs", "Temp", "tmp", "Program Files (x86)",
        "MSOCache", "OneDriveTemp"
    };

    // -------------------------------------------------------------------------
    // PUBLIC API
    // -------------------------------------------------------------------------

    /// <summary>
    /// PHASE 1: Quick validation of cached paths and common locations.
    /// Called on app startup. Self-contained and fast.
    /// Validates both Minecraft stable and Preview installations.
    /// </summary>
    public static void ValidateAndUpdateCachedLocations()
    {
        Trace.WriteLine("=== PHASE 1: Quick Validation Starting ===");

        ValidateAndUpdateSingleInstallation(
            isPreview: false,
            cachedPath: EnvironmentVariables.Persistent.MinecraftInstallPath,
            updateCache: (path) => EnvironmentVariables.Persistent.MinecraftInstallPath = path
        );

        ValidateAndUpdateSingleInstallation(
            isPreview: true,
            cachedPath: EnvironmentVariables.Persistent.MinecraftPreviewInstallPath,
            updateCache: (path) => EnvironmentVariables.Persistent.MinecraftPreviewInstallPath = path
        );

        Trace.WriteLine("=== PHASE 1 Complete ===");
    }

    /// <summary>
    /// Quick re-validation of a cached path before use.
    /// Called by feature windows before trusting the cache.
    /// Also detects and evicts stale symlink paths, and evicts paths whose
    /// edition no longer matches what's expected (e.g. after a manual swap).
    /// </summary>
    public static bool RevalidateCachedPath(string? cachedPath, bool expectedPreview)
    {
        if (string.IsNullOrEmpty(cachedPath))
            return false;

        if (!Directory.Exists(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path no longer exists: {cachedPath}");
            return false;
        }

        if (!IsValidExecutableDirectory(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path no longer valid: {cachedPath}");
            return false;
        }

        // Evict if the cached path is still a symlink - force re-discovery
        // so the physical path gets written to cache instead.
        var resolved = ResolveToPhysicalPath(cachedPath);
        if (!resolved.Equals(cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path is a symlink - evicting so physical path gets cached: {resolved}");
            return false;
        }

        // Authoritative edition check via MicrosoftGame.Config. If the config is
        // missing or unreadable we don't evict on that basis alone (degrade gracefully -
        // see TryGetEditionFromGameConfig), but a confirmed mismatch is disqualifying.
        var detectedEdition = TryGetEditionFromGameConfig(resolved);
        if (detectedEdition.HasValue && detectedEdition.Value != expectedPreview)
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path edition mismatch (expected Preview={expectedPreview}, found Preview={detectedEdition.Value}) - evicting");
            return false;
        }

        return true;
    }

    /// <summary>
    /// PHASE 2: Deep system-wide search for Minecraft installation.
    /// Only searches for the version the user is targeting.
    /// Can be cancelled when the user initiates manual selection.
    /// Returns the physical directory containing Minecraft.Windows.exe.
    ///
    /// Matching is based entirely on file contents (exe + MicrosoftGame.Config
    /// Identity), never on folder name - friendly names and known GUIDs are only
    /// used as a priority pass to find common cases fast.
    /// </summary>
    public static async Task<string?> SearchForMinecraftAsync(bool searchForPreview, CancellationToken cancellationToken)
    {
        Trace.WriteLine($"=== PHASE 2: Deep System Search Starting (Preview={searchForPreview}) ===");

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();

            Trace.WriteLine($"[GDKLocator] Found {drives.Count} fixed drives to search");

            foreach (var drive in drives)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Trace.WriteLine("[GDKLocator] Search cancelled by user");
                    return null;
                }

                Trace.WriteLine($"[GDKLocator] Scanning drive: {drive.Name}");

                // Priority pass: check high-probability locations (friendly names + known GUIDs)
                foreach (var priorityPath in GetCommonLocations(searchForPreview, drive))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    if (IsValidExecutableDirectoryForEdition(priorityPath, searchForPreview))
                    {
                        Trace.WriteLine($"[GDKLocator] Found at priority location: {priorityPath}");
                        CacheInstallation(searchForPreview, priorityPath);
                        return priorityPath;
                    }
                }

                // Deep recursive search of this drive - matches on exe + config identity only,
                // completely independent of folder naming.
                var foundPath = await RecursiveSearchAsync(
                    drive.Name,
                    searchForPreview,
                    currentDepth: 0,
                    cancellationToken
                );

                if (foundPath != null)
                {
                    Trace.WriteLine($"[GDKLocator] Found via deep search: {foundPath}");
                    CacheInstallation(searchForPreview, foundPath);
                    return foundPath;
                }
            }

            Trace.WriteLine("[GDKLocator] Target not found on any drive");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error during system search: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// PHASE 3: Manual selection - user picks a folder near the installation
    /// (folder picker, not file picker: the exe itself may sit in a
    /// permission-protected directory that the OS won't allow the app to "open,"
    /// even though only the path is needed - folders don't carry that restriction).
    ///
    /// Tolerant of imprecision: accepts the exact folder containing Minecraft.Windows.exe,
    /// or a folder one level shallower (the install root, whose child folder - named
    /// anything, including a GUID - holds the exe). This mirrors the leniency
    /// MinecraftUserDataLocator gives when accepting Shared/Users subfolders.
    /// Edition is verified via MicrosoftGame.Config, not folder name.
    /// </summary>
    public static async Task<string?> LocateMinecraftManuallyAsync(bool isPreview, IntPtr windowHandle)
    {
        Trace.WriteLine($"=== PHASE 3: Manual Selection Starting (Preview={isPreview}) ===");

        try
        {
            // Opens on this edition's currently cached install, when there is one - a re-pick is
            // usually a correction to a path that is nearly right, so starting anywhere else
            // makes the user navigate back to where the app already was.
            var cached = isPreview
                ? EnvironmentVariables.Persistent.MinecraftPreviewInstallPath
                : EnvironmentVariables.Persistent.MinecraftInstallPath;

            var selectedPath = await Helpers.PickFolderAsync(windowHandle, cached);
            if (selectedPath == null)
            {
                Trace.WriteLine("[GDKLocator] User cancelled folder selection");
                return null;
            }

            Trace.WriteLine($"[GDKLocator] User selected: {selectedPath}");

            var resolvedSelection = ResolveToPhysicalPath(selectedPath);
            if (!resolvedSelection.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                Trace.WriteLine($"[GDKLocator] Resolved selection: {selectedPath} → {resolvedSelection}");

            var exeDirectory = FindExecutableDirectoryNearby(resolvedSelection);
            if (exeDirectory == null)
            {
                Trace.WriteLine($"[GDKLocator] Could not find {MinecraftExecutableName} in or one level under the selected folder");
                return null;
            }

            // Authoritative edition check via MicrosoftGame.Config.
            var detectedEdition = TryGetEditionFromGameConfig(exeDirectory);
            if (detectedEdition.HasValue)
            {
                if (detectedEdition.Value != isPreview)
                {
                    var foundName = detectedEdition.Value ? "Preview" : "Stable";
                    var expectedName = isPreview ? "Preview" : "Stable";
                    Trace.WriteLine($"[GDKLocator] Selected wrong version - MicrosoftGame.Config identifies this as {foundName}, expected {expectedName}");
                    return null;
                }
            }
            else
            {
                // No usable config - soft folder-name guard as last resort, same as before.
                var unexpectedFolderName = isPreview ? MinecraftFolderName : MinecraftPreviewFolderName;
                var installRoot = Directory.GetParent(exeDirectory)?.Name ?? string.Empty;
                if (installRoot.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"[GDKLocator] Selected wrong version - install root is: {installRoot}");
                    return null;
                }
                Trace.WriteLine("[GDKLocator] MicrosoftGame.Config unavailable - proceeding on unverified edition (folder name didn't indicate a mismatch)");
            }

            Trace.WriteLine($"[GDKLocator] Valid installation selected: {exeDirectory}");
            CacheInstallation(isPreview, exeDirectory);
            return exeDirectory;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error during manual selection: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Looks for Minecraft.Windows.exe directly inside the selected folder, or one
    /// level deeper - tolerating the user having selected the install root instead
    /// of the exe's own folder. No name assumption on that child folder: it could be
    /// "Content", a GUID, or anything a third-party launcher decided to call it.
    /// Each candidate is symlink-resolved before being checked, since a subfolder
    /// can itself turn out to be a junction. This is a bounded, one-hop convenience -
    /// not a search; Phase 2 already owns unbounded discovery.
    /// </summary>
    private static string? FindExecutableDirectoryNearby(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
            return null;

        // Direct hit - selected folder already contains the exe
        if (IsValidExecutableDirectory(selectedPath))
            return selectedPath;

        // One level deeper - selected folder was probably the install root
        try
        {
            foreach (var subdir in Directory.GetDirectories(selectedPath))
            {
                var resolvedSubdir = ResolveToPhysicalPath(subdir);
                if (IsValidExecutableDirectory(resolvedSubdir))
                    return resolvedSubdir;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error scanning subfolders of {selectedPath}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Returns common installation locations for a given drive (or all fixed drives).
    /// Includes both friendly folder names and known Microsoft Store GUIDs - the two
    /// are fully interchangeable as far as this locator is concerned, since some
    /// installers use one and some use the other. Returns the Content subdirectory
    /// directly - the directory where the exe lives.
    /// </summary>
    public static IEnumerable<string> GetCommonLocations(bool isPreview, DriveInfo? onlyDrive = null)
    {
        var friendlyFolder = isPreview ? MinecraftPreviewFolderName : MinecraftFolderName;
        var storeGuid = isPreview ? MinecraftPreviewStoreGuid : MinecraftStableStoreGuid;

        var drives = onlyDrive != null
            ? new[] { onlyDrive }
            : DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;

            // Xbox App install, friendly name
            yield return Path.Combine(root, "XboxGames", friendlyFolder, "Content");
            // Direct Microsoft Store install, GUID-named - fully equivalent to the friendly name
            yield return Path.Combine(root, "XboxGames", storeGuid, "Content");
            // Some installs land directly under Program Files
            yield return Path.Combine(root, "Program Files", "Microsoft Games", friendlyFolder, "Content");
        }
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Core Phase 1 logic for a single edition.
    /// Order: cache check → Stage 0 PackageManager → Stage 1 common locations.
    /// Symlink resolution and edition verification are applied at every point a path is accepted.
    /// </summary>
    private static void ValidateAndUpdateSingleInstallation(
        bool isPreview,
        string? cachedPath,
        Action<string?> updateCache)
    {
        var versionName = isPreview ? "Preview" : "Stable";
        Trace.WriteLine($"[GDKLocator] Validating {versionName} Minecraft...");

        // Cache check
        if (!string.IsNullOrEmpty(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] Cached path: {cachedPath}");

            if (RevalidateCachedPath(cachedPath, isPreview))
            {
                // RevalidateCachedPath already confirmed exe + edition; only the
                // symlink-resolution re-cache case needs writing back here, and
                // RevalidateCachedPath would have returned false for that case
                // (forcing this branch to fall through to rediscovery), so a true
                // result here means the cache is genuinely already correct as-is.
                Trace.WriteLine($"[GDKLocator] Cache valid for {versionName}");
                return;
            }

            Trace.WriteLine($"[GDKLocator] Cache invalid for {versionName}, clearing");
            updateCache(null);

            // The cache might have been invalid purely because it was a symlink
            // pointing at an otherwise-correct physical path - try that quick
            // resolve-and-recache before falling all the way through to Stage 0/1.
            if (Directory.Exists(cachedPath) && IsValidExecutableDirectory(cachedPath))
            {
                var resolved = ResolveToPhysicalPath(cachedPath);
                var resolvedEdition = TryGetEditionFromGameConfig(resolved);
                if (!resolvedEdition.HasValue || resolvedEdition.Value == isPreview)
                {
                    Trace.WriteLine($"[GDKLocator] Cache was a symlink - re-caching physical path: {resolved}");
                    updateCache(resolved);
                    return;
                }
            }
        }
        else
        {
            Trace.WriteLine($"[GDKLocator] No cached path for {versionName}");
        }

        // STAGE 0: PackageManager - authoritative OS query, instant
        var packagePath = TryGetInstallPathFromPackageManager(isPreview);
        if (packagePath != null)
        {
            Trace.WriteLine($"[GDKLocator] Found {versionName} via PackageManager: {packagePath}");
            updateCache(packagePath);
            return;
        }
        // STAGE 0.5 (0's FALLBACK): Try to look up the junction has a hardcoded path, a hail mary in case previous step fails, before moving on
        var junctionPath = TryGetInstallPathFromWindowsAppsJunction(isPreview);
        if (junctionPath != null)
        {
            Trace.WriteLine($"[GDKLocator] Found {versionName} via a blind try at hardcoded Junction/Symlink resolution: {junctionPath}");
            updateCache(junctionPath);
            return;
        }

        // STAGE 1: Common locations across all drives (friendly names + known GUIDs)
        foreach (var location in GetCommonLocations(isPreview))
        {
            Trace.WriteLine($"[GDKLocator] Checking common location: {location}");
            if (IsValidExecutableDirectoryForEdition(location, isPreview))
            {
                Trace.WriteLine($"[GDKLocator] Found {versionName} at common location: {location}");
                updateCache(location);
                return;
            }
        }

        Trace.WriteLine($"[GDKLocator] {versionName} not found in Phase 1");
    }

    /// <summary>
    /// STAGE 0: Query PackageManager for the game's registered install location.
    /// PackageManager returns the WindowsApps junction - resolved to the physical
    /// Content directory (where Minecraft.Windows.exe lives) before returning.
    /// </summary>
    private static string? TryGetInstallPathFromPackageManager(bool isPreview)
    {
        try
        {
            var familyName = isPreview ? MinecraftPreviewPackageFamilyName : MinecraftStablePackageFamilyName;
            Trace.WriteLine($"[GDKLocator] Querying PackageManager for: {familyName}");

            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty, familyName);

            foreach (var package in packages)
            {
                var installLocation = package.InstalledLocation?.Path;
                if (string.IsNullOrEmpty(installLocation))
                    continue;

                Trace.WriteLine($"[GDKLocator] PackageManager returned: {installLocation}");

                var resolvedLocation = ResolveToPhysicalPath(installLocation);
                Trace.WriteLine($"[GDKLocator] Resolved to physical path: {resolvedLocation}");

                if (IsValidExecutableDirectory(resolvedLocation))
                {
                    Trace.WriteLine("[GDKLocator] Executable found at resolved path");
                    return resolvedLocation;
                }

                var contentSubdir = Path.Combine(resolvedLocation, "Content");
                if (IsValidExecutableDirectory(contentSubdir))
                {
                    Trace.WriteLine($"[GDKLocator] Executable found in Content subdir: {contentSubdir}");
                    return contentSubdir;
                }
            }

            Trace.WriteLine($"[GDKLocator] PackageManager: no valid install found for {familyName}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.WriteLine($"[GDKLocator] PackageManager access denied: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] PackageManager query failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// STAGE 0.5: Direct WindowsApps junction lookup — a cheap, single-directory
    /// fallback for the rare case PackageManager itself fails (policy restrictions,
    /// odd app contexts) despite the package actually being installed. The junction's
    /// naming convention (family name + version + architecture) is stable and
    /// well-documented, unlike a full-drive search.
    /// </summary>
    private static string? TryGetInstallPathFromWindowsAppsJunction(bool isPreview)
    {
        var familyName = isPreview ? MinecraftPreviewPackageFamilyName : MinecraftStablePackageFamilyName;
        var windowsAppsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

        if (!Directory.Exists(windowsAppsPath))
            return null;

        try
        {
            // Junction folders are named "{PackageFamilyName-prefix}_{version}_{arch}__{publisherId}"
            // e.g. Microsoft.MinecraftUWP_1.26.3005.0_x64__8wekyb3d8bbwe
            var familyPrefix = familyName.Split('_')[0]; // "Microsoft.MinecraftUWP"

            foreach (var dir in Directory.GetDirectories(windowsAppsPath, $"{familyPrefix}_*"))
            {
                var resolved = ResolveToPhysicalPath(dir);

                if (IsValidExecutableDirectoryForEdition(resolved, isPreview))
                    return resolved;

                var contentSubdir = Path.Combine(resolved, "Content");
                if (IsValidExecutableDirectoryForEdition(contentSubdir, isPreview))
                    return contentSubdir;
            }
        }
        catch (UnauthorizedAccessException)
        {
            Trace.WriteLine("[GDKLocator] Access denied to WindowsApps folder");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error scanning WindowsApps: {ex.Message}");
        }

        return null;
    }
    /// <summary>
    /// Resolves a path to its physical target, following symlinks and junctions anywhere
    /// along it. Returns the original path unchanged if there is nothing to resolve or
    /// resolution fails. See <see cref="Helpers.ResolveToPhysicalPath"/>, shared with
    /// <see cref="MinecraftUserDataLocator"/>.
    /// </summary>
    private static string ResolveToPhysicalPath(string path) => Helpers.ResolveToPhysicalPath(path);

    /// <summary>
    /// Returns true if the directory exists and directly contains Minecraft.Windows.exe.
    /// This is the canonical validity check - the contract path always satisfies this.
    /// Does NOT verify edition; use IsValidExecutableDirectoryForEdition when the
    /// caller cares which edition it is.
    /// </summary>
    private static bool IsValidExecutableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, MinecraftExecutableName));
    }

    /// <summary>
    /// Returns true if the directory contains Minecraft.Windows.exe AND its
    /// MicrosoftGame.Config identifies it as the requested edition. If the config
    /// is missing or unparseable, this degrades to exe-presence only - we never
    /// want a missing/corrupt config to make an otherwise-good install invisible.
    /// </summary>
    private static bool IsValidExecutableDirectoryForEdition(string path, bool expectedPreview)
    {
        if (!IsValidExecutableDirectory(path))
            return false;

        var detected = TryGetEditionFromGameConfig(path);
        return !detected.HasValue || detected.Value == expectedPreview;
    }

    /// <summary>
    /// Reads MicrosoftGame.Config next to the exe and parses its
    /// &lt;Identity Name="..."/&gt; attribute to authoritatively determine whether
    /// this install is Preview or Stable. This identity string is the same one the
    /// package family name is built from, is independent of folder naming or which
    /// installer placed it there, and has survived the "Beta" → "Preview" rebrand
    /// unchanged.
    ///
    /// Returns true for Preview, false for Stable, or null if the config is missing,
    /// unreadable, or doesn't contain a recognized identity (callers should treat
    /// null as "unknown" and fall back to other signals rather than rejecting outright).
    /// </summary>
    private static bool? TryGetEditionFromGameConfig(string executableDirectory)
    {
        try
        {
            var configPath = Path.Combine(executableDirectory, GameConfigFileName);
            if (!File.Exists(configPath))
                return null;

            var doc = XDocument.Load(configPath);
            var identityName = doc.Root?
                .Element("Identity")?
                .Attribute("Name")?
                .Value;

            if (string.IsNullOrEmpty(identityName))
                return null;

            if (identityName.Equals(MinecraftPreviewIdentityName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (identityName.Equals(MinecraftStableIdentityName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Recognized config, but an identity we don't know - don't guess.
            Trace.WriteLine($"[GDKLocator] Unrecognized MicrosoftGame.Config Identity: {identityName}");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Failed to read/parse {GameConfigFileName} at {executableDirectory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recursively searches a directory tree for a Minecraft install of the requested
    /// edition. Matching is based entirely on directory contents - Minecraft.Windows.exe
    /// plus a MicrosoftGame.Config confirming the edition - never on folder name. This
    /// makes the fallback genuinely unconditional: GUID folders, third-party launcher
    /// naming, anything goes, as long as the files are really there.
    /// Used in Phase 2 only. Respects FoldersToSkip and CancellationToken.
    /// </summary>
    private static async Task<string?> RecursiveSearchAsync(
        string searchPath,
        bool searchForPreview,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        if (currentDepth >= MaxSearchDepth || cancellationToken.IsCancellationRequested)
            return null;

        try
        {
            // Test this directory directly - exe presence + confirmed edition.
            // Unlike the old folder-name-gated approach, every directory visited
            // is tested, not just ones matching a known name.
            if (IsValidExecutableDirectoryForEdition(searchPath, searchForPreview))
                return searchPath;

            var subdirectories = await Task.Run(() =>
            {
                try { return Directory.GetDirectories(searchPath); }
                catch { return Array.Empty<string>(); }
            }, cancellationToken);

            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                if (FoldersToSkip.Contains(Path.GetFileName(subdir)))
                    continue;

                var result = await RecursiveSearchAsync(subdir, searchForPreview, currentDepth + 1, cancellationToken);
                if (result != null)
                    return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error searching {searchPath}: {ex.Message}");
        }

        return null;
    }

    private static void CacheInstallation(bool isPreview, string path)
    {
        if (isPreview)
        {
            EnvironmentVariables.Persistent.MinecraftPreviewInstallPath = path;
            Trace.WriteLine($"[GDKLocator] Cached Preview installation: {path}");
        }
        else
        {
            EnvironmentVariables.Persistent.MinecraftInstallPath = path;
            Trace.WriteLine($"[GDKLocator] Cached Stable installation: {path}");
        }
    }
}

/// <summary>
/// Centralizes discovery and validation of Minecraft's GDK user data root -
/// the folder that contains worlds, options, resource packs, and the Shared tree.
///
/// Contract: the path stored in EnvironmentVariables.Persistent.MinecraftDataPath (and
/// MinecraftPreviewDataPath) is always the "Minecraft Bedrock" or "Minecraft Bedrock
/// Preview" root folder - the one that directly contains a "Users" subfolder.
/// All deeper paths (com.mojang, resource_packs, options.txt) are derived from this
/// root on demand via the helper methods below.
///
/// Like GDKLocator's, that stored path is always PHYSICAL: symlinks and junctions - on the
/// folder itself or anywhere above it - are resolved before a path is validated or cached,
/// via <see cref="Helpers.ResolveToPhysicalPath"/>. Third-party launchers relocate user data
/// with links, and every user-data path in the app is built from this root, so resolving it
/// here is what makes all of them physical without any of them doing it themselves.
///
/// Unlike GDKLocator, there is no exe or config file to serve as an absolute gospel
/// here - validation is based on folder structure (presence of the "Users" subfolder).
/// If the default AppData location is absent, we cannot reliably auto-discover an
/// alternative (third-party launchers like LeviLauncher can put this anywhere), so
/// we surface that as a user-actionable warning rather than attempting a blind search.
///
/// The result of the last validation is exposed as a simple bool per edition so that
/// the main window and any other caller can gate features without re-checking the path
/// themselves.
/// </summary>
public static class MinecraftUserDataLocator
{
    // ── Folder names ──────────────────────────────────────────────────────────
    public const string StableRootFolderName = "Minecraft Bedrock";
    public const string PreviewRootFolderName = "Minecraft Bedrock Preview";

    // ── Internal sub-paths ────────────────────────────────────────────────────
    private const string UsersFolderName = "Users";
    private static readonly string SharedComMojangSubPath = Path.Combine("Shared", "games", "com.mojang");
    private const string ResourcePacksFolderName = "resource_packs";
    private const string DevResourcePacksFolderName = "development_resource_packs";
    private const string BehaviorPacksFolderName = "behavior_packs";
    private const string DevBehaviorPacksFolderName = "development_behavior_packs";
    private const string OptionsFileName = "options.txt";

    // ── Last-known validation state (set by ValidateAndUpdateCachedLocations) ─
    public static bool IsStableDataValid { get; private set; }
    public static bool IsPreviewDataValid { get; private set; }

    // =========================================================================
    //  PUBLIC API - startup + path resolution
    // =========================================================================

    /// <summary>
    /// Called on app startup (and on Preview/Release toggle) to verify cached
    /// user data paths and attempt to fill them from the default AppData location
    /// if missing. Updates <see cref="IsStableDataValid"/> and
    /// <see cref="IsPreviewDataValid"/> so callers can gate features without
    /// re-checking themselves.
    /// Call this after LoadSettings() so the cached paths are already loaded.
    /// </summary>
    public static void ValidateAndUpdateCachedLocations()
    {
        Trace.WriteLine("=== [UserDataLocator] Validation Starting ===");

        IsStableDataValid = ValidateSingleEdition(isPreview: false);
        IsPreviewDataValid = ValidateSingleEdition(isPreview: true);

        Trace.WriteLine($"=== [UserDataLocator] Complete - Stable={IsStableDataValid}, Preview={IsPreviewDataValid} ===");
    }

    /// <summary>
    /// Returns the validated data root for the given edition, or null if it isn't
    /// known/valid. Callers that only care about one edition at a time (most of them)
    /// use this rather than reading the Persistent fields directly.
    /// </summary>
    public static string? GetDataRoot(bool isPreview)
    {
        var path = isPreview
            ? EnvironmentVariables.Persistent.MinecraftPreviewDataPath
            : EnvironmentVariables.Persistent.MinecraftDataPath;

        return IsValidDataRoot(path, isPreview) ? path : null;
    }

    /// <summary>
    /// True if the data root for the given edition is currently valid.
    /// Mirrors <see cref="IsStableDataValid"/>/<see cref="IsPreviewDataValid"/>
    /// but addressable by bool rather than two separate properties.
    /// </summary>
    public static bool IsDataValid(bool isPreview)
        => isPreview ? IsPreviewDataValid : IsStableDataValid;

    /// <summary>
    /// Attempts to accept a user-supplied path as the data root for the given edition.
    /// Validates structure, caches on success, updates the validity flag.
    /// Returns true if the path was accepted.
    /// </summary>
    public static bool TrySetCustomDataRoot(bool isPreview, string path)
    {
        path = Helpers.ResolveToPhysicalPath(path);

        if (!IsValidDataRoot(path, isPreview))
        {
            Trace.WriteLine($"[UserDataLocator] Rejected custom path (no Users subfolder): {path}");
            return false;
        }

        Trace.WriteLine($"[UserDataLocator] Accepted custom path for {(isPreview ? "Preview" : "Stable")}: {path}");
        SetCachedPath(isPreview, path);

        if (isPreview) IsPreviewDataValid = true;
        else IsStableDataValid = true;

        return true;
    }

    // ── Derived paths ---------------------------------------------------------
    // All return empty string (never null, never throw) when the root isn't valid,
    // so callers can pass the result to Directory.Exists / File.Exists without a
    // null-check dance.

    public static string GetUsersPath(bool isPreview)
    {
        var root = GetDataRoot(isPreview);
        return root is null ? string.Empty : Path.Combine(root, UsersFolderName);
    }

    public static string GetSharedComMojangPath(bool isPreview)
    {
        var users = GetUsersPath(isPreview);
        return string.IsNullOrEmpty(users) ? string.Empty
            : Path.Combine(users, SharedComMojangSubPath);
    }

    /// <summary>
    /// resource_packs or development_resource_packs under Shared\games\com.mojang.
    /// Pass createIfMissing=true for write-path callers (e.g. DeployPackage).
    /// </summary>
    public static string GetResourcePacksPath(bool isPreview, bool development = false, bool createIfMissing = false) =>
        GetPacksFolderPath(isPreview, development ? DevResourcePacksFolderName : ResourcePacksFolderName, createIfMissing);

    /// <summary>
    /// behavior_packs or development_behavior_packs under Shared\games\com.mojang. Only the
    /// importer writes here, for a pack the user chose to import as a behaviour pack - nothing
    /// else in the app reads behaviour packs.
    /// </summary>
    public static string GetBehaviorPacksPath(bool isPreview, bool development = false, bool createIfMissing = false) =>
        GetPacksFolderPath(isPreview, development ? DevBehaviorPacksFolderName : BehaviorPacksFolderName, createIfMissing);

    private static string GetPacksFolderPath(bool isPreview, string folder, bool createIfMissing)
    {
        var comMojang = GetSharedComMojangPath(isPreview);
        if (string.IsNullOrEmpty(comMojang)) return string.Empty;

        var fullPath = Path.Combine(comMojang, folder);

        if (!Directory.Exists(fullPath) && createIfMissing)
        {
            try { Directory.CreateDirectory(fullPath); }
            catch { return string.Empty; }
        }

        return fullPath;
    }

    /// <summary>
    /// Both resource_packs and development_resource_packs paths that actually
    /// exist on disk. Convenient for scan-all operations (PackLocator, PackBrowser).
    /// </summary>
    public static IEnumerable<string> GetExistingResourcePackScanPaths(bool isPreview)
    {
        var rp = GetResourcePacksPath(isPreview, development: false);
        var dev = GetResourcePacksPath(isPreview, development: true);

        if (!string.IsNullOrEmpty(rp) && Directory.Exists(rp)) yield return rp;
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) yield return dev;
    }

    /// <summary>The behaviour-pack counterpart of <see cref="GetExistingResourcePackScanPaths"/>.</summary>
    public static IEnumerable<string> GetExistingBehaviorPackScanPaths(bool isPreview)
    {
        var bp = GetBehaviorPacksPath(isPreview, development: false);
        var dev = GetBehaviorPacksPath(isPreview, development: true);

        if (!string.IsNullOrEmpty(bp) && Directory.Exists(bp)) yield return bp;
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) yield return dev;
    }

    /// <summary>
    /// All options.txt files under the Users tree (one per XUID + Shared).
    /// Returns empty array if the data root is unknown or the Users folder is absent.
    /// </summary>
    public static string[] FindAllOptionsFiles(bool isPreview)
    {
        var usersPath = GetUsersPath(isPreview);
        if (string.IsNullOrEmpty(usersPath) || !Directory.Exists(usersPath))
            return Array.Empty<string>();

        try { return Directory.GetFiles(usersPath, OptionsFileName, SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Human-readable label for the XUID or "Shared" folder that owns a given
    /// path (first segment under Users\). Used for per-file log messages.
    /// </summary>
    public static string GetOwningFolderLabel(bool isPreview, string fullPath)
    {
        var usersPath = GetUsersPath(isPreview);
        if (string.IsNullOrEmpty(usersPath))
            return Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? fullPath;

        try
        {
            var relative = Path.GetRelativePath(usersPath, fullPath);
            return relative.Split(Path.DirectorySeparatorChar)[0];
        }
        catch
        {
            return Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? fullPath;
        }
    }

    /// <summary>
    /// Display name for the targeted edition - "Minecraft" or "Minecraft Preview".
    /// </summary>
    public static string GetVersionDisplayName(bool isPreview)
        => isPreview ? "Minecraft Preview" : "Minecraft";

    // =========================================================================
    //  PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Validates the cached path for one edition and attempts to fill it from
    /// AppData if missing. Returns true if a valid path is now in cache.
    /// </summary>
    private static bool ValidateSingleEdition(bool isPreview)
    {
        var versionName = isPreview ? "Preview" : "Stable";
        var cachedPath = isPreview
            ? EnvironmentVariables.Persistent.MinecraftPreviewDataPath
            : EnvironmentVariables.Persistent.MinecraftDataPath;

        // 1. Cached path - still there and valid? Resolved again every time: a path cached
        // before resolution existed, or a link that has since been repointed, is corrected here
        // rather than trusted.
        if (!string.IsNullOrEmpty(cachedPath))
        {
            var physicalPath = Helpers.ResolveToPhysicalPath(cachedPath);
            if (IsValidDataRoot(physicalPath, isPreview))
            {
                if (!string.Equals(physicalPath, cachedPath, StringComparison.OrdinalIgnoreCase))
                    SetCachedPath(isPreview, physicalPath);
                Trace.WriteLine($"[UserDataLocator] {versionName} cache valid: {physicalPath}");
                return true;
            }

            Trace.WriteLine($"[UserDataLocator] {versionName} cache invalid, clearing: {cachedPath}");
            SetCachedPath(isPreview, null);
        }

        // 2. Default AppData location
        var folderName = isPreview ? PreviewRootFolderName : StableRootFolderName;
        var defaultPath = Helpers.ResolveToPhysicalPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            folderName));

        if (IsValidDataRoot(defaultPath, isPreview))
        {
            Trace.WriteLine($"[UserDataLocator] {versionName} found at default location: {defaultPath}");
            SetCachedPath(isPreview, defaultPath);
            return true;
        }

        // 3. Not found? tell the user exactly what to look for
        Trace.WriteLine($"[UserDataLocator] {versionName} data root not found");
        return false;
    }

    /// <summary>
    /// A data root is valid if it exists on disk and contains a "Users" subfolder.
    /// This is the closest equivalent to GDKLocator's exe-presence check - the
    /// "Users" folder is created by the game on first launch and is required for
    /// all per-user data to exist under it.
    /// </summary>
    private static bool IsValidDataRoot(string? path, bool isPreview)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path)) return false;
        if (!Directory.Exists(Path.Combine(path, UsersFolderName, SharedComMojangSubPath))) return false;

        // Reject if the folder name is explicitly the wrong edition.
        // Unknown/custom names (third-party launchers) pass through unchecked.
        var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var wrongEditionName = isPreview ? StableRootFolderName : PreviewRootFolderName;
        if (folderName.Equals(wrongEditionName, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[UserDataLocator] Rejected path - folder name indicates wrong edition: {folderName}");
            return false;
        }

        return true;
    }

    private static void SetCachedPath(bool isPreview, string? path)
    {
        if (isPreview) EnvironmentVariables.Persistent.MinecraftPreviewDataPath = path;
        else EnvironmentVariables.Persistent.MinecraftDataPath = path;
    }


    /// <summary>
    /// Helper. Call at the top of any feature that depends on the current edition's user
    /// data folder. Returns true if the caller should proceed; false means the
    /// feature was short-circuited and the user has already been told what to do.
    /// Uses a live filesystem check rather than the cached validity flag, so it
    /// still catches the folder having gone missing mid-session.
    /// </summary>
    public static bool RequireValidUserData(bool isTargetingPreview)
    {
        if (GetDataRoot(isTargetingPreview) != null)
            return true;

        var versionName = GetVersionDisplayName(isTargetingPreview);
        var editionLabel = isTargetingPreview ? "Preview" : "Stable";
        var expectedFolderName = isTargetingPreview
                                 ? MinecraftUserDataLocator.PreviewRootFolderName
                                 : MinecraftUserDataLocator.StableRootFolderName;

        MainWindow.Log($"You can't use this feature without first telling the app where your {versionName} user data folder is located. " +
                       $"Click \"Locate {editionLabel} user data\" above, find and select the folder named \"{expectedFolderName}\" " +
                       $"- It's the one with a \"Users\" subfolder inside it.\n" +
                       $"The same folder can be set from the Settings menu, under Game user data, which is also where to change it later.", LogLevel.Warning);

        return false;
    }
}
