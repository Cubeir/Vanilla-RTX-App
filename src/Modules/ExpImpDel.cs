using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage.Pickers;
using static Vanilla_RTX_App.MainWindow;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules;

public static class ExpImpDel
{
    #region Export

    public static async Task<string?> ExportMCPACK(string packFolderPath, string suggestedName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Instance);
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("Minecraft Pack", new List<string>() { ".mcpack" });
        picker.SuggestedFileName = suggestedName;
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        // A pack on its way out of the app ships without the game's own caches/signatures -
        // see Helpers.RemoveBookkeepingFiles for why a stale one is worse than none.
        await Task.Run(() => Helpers.RemoveBookkeepingFiles(packFolderPath));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return null;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"temp_{Guid.NewGuid()}.mcpack");
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create);
                foreach (var filePath in Directory.GetFiles(packFolderPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(packFolderPath, filePath);
                    zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                }
            });

            if (!File.Exists(tempZipPath))
            {
                Trace.WriteLine("[Export] Temporary .mcpack archive was deleted before writing to output.");
                return null;
            }

            using var destStream = await file.OpenStreamForWriteAsync();
            using var srcStream = File.OpenRead(tempZipPath);
            await srcStream.CopyToAsync(destStream);

            Trace.WriteLine($"[Export] {suggestedName}.mcpack exported successfully.");
            return file.Path;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Export] Failed to export {suggestedName}: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); }
            catch (Exception ex) { Trace.WriteLine($"[Export] Warning: Couldn't delete temp file: {ex.Message}"); }
        }
    }

    #endregion

    #region Import — public surface

    /// <summary>When true, packs land in development_resource_packs instead of resource_packs.</summary>
    public static bool InstallToDevelopmentPacks = false;

    /// <summary>Progress/status callback fired during import operations.</summary>
    public static event Action<string>? ImportStatusChanged;

    /// <summary>
    /// Invoked when a duplicate header UUID match is found. Return true to overwrite,
    /// false to skip. If null, duplicates are silently skipped.
    /// Parameters: (incomingPackName, existingFolderPath)
    /// </summary>
    public static Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Invoked when a pack's manifest has no module of type "resources" and no behaviour-pack
    /// module either, or when the type cannot be determined. Return true to import it into the
    /// resource folder anyway, false to skip. If null, such packs are silently skipped.
    /// Parameter: pack display name from the manifest, or filename if unreadable.
    /// </summary>
    public static Func<string, Task<bool>>? ConfirmNonResourceImport { get; set; }

    /// <summary>
    /// Invoked instead of <see cref="ConfirmNonResourceImport"/> when the manifest declares a
    /// behaviour-pack module (<see cref="PackManifest.HasBehaviorModule"/>). Return true to
    /// import it into behavior_packs (development_behavior_packs under
    /// <see cref="InstallToDevelopmentPacks"/>), false to skip. There is no "into the resource
    /// folder anyway" answer: the game ignores a behaviour pack there. If null, skipped.
    /// </summary>
    public static Func<string, Task<bool>>? ConfirmBehaviorImport { get; set; }

    /// <summary>
    /// Opens a file picker and imports the chosen packs. Accepts .mcpack, .zip,
    /// and .mcaddon. <paramref name="ownerHwnd"/> must be the PackBrowserOverlay
    /// handle so the picker stays above the right window.
    /// </summary>
    public static async Task<bool> ImportPackAsync(IntPtr ownerHwnd)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add(".mcpack");
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".mcaddon");
        picker.ViewMode = PickerViewMode.List;

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return false;

        return await ImportFromPathsAsync(files.Select(f => f.Path));
    }

    /// <summary>
    /// Imports packs from an arbitrary list of file/folder paths (drag-and-drop entry
    /// point). Bulk-import rules:
    /// <list type="bullet">
    ///   <item>.mcpack / .zip  — imported individually.</item>
    ///   <item>.mcaddon        — every pack inside is imported, as .mcpack files or folders.</item>
    ///   <item>folder          — root scanned non-recursively for .mcpack, .zip,
    ///                           and .mcaddon files; each queued by its own rule.</item>
    /// </list>
    /// All dialogs (overwrite, non-resource) block the queue until answered.
    /// </summary>
    public static async Task<bool> ImportFromPathsAsync(IEnumerable<string> paths)
    {
        var destination = GetImportDestination();
        if (string.IsNullOrEmpty(destination))
        {
            ReportStatus("Import failed: Minecraft data directory not found.");
            return false;
        }

        var queue = new List<ImportItem>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var found = Directory
                    .GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsQueueableExtension(Path.GetExtension(f)))
                    .Select(f => ExtToImportItem(f))
                    .ToList();

                if (found.Count == 0)
                    ReportStatus($"No .mcpack, .zip, or .mcaddon files found in the root of '{Path.GetFileName(path)}'.");
                else
                    queue.AddRange(found);
            }
            else if (File.Exists(path))
            {
                var ext = Path.GetExtension(path);
                if (IsQueueableExtension(ext))
                    queue.Add(ExtToImportItem(path));
                else
                    ReportStatus($"Skipped '{Path.GetFileName(path)}': only .mcpack, .zip, .mcaddon, or folders are accepted.");
            }
            else
            {
                ReportStatus($"Skipped '{path}': path not found.");
            }
        }

        if (queue.Count == 0)
        {
            ReportStatus("Nothing to import.");
            return false;
        }

        bool anySuccess = false;

        foreach (var item in queue)
        {
            try
            {
                bool ok = item.Kind == ImportItemKind.McAddon
                    ? await ImportFromMcAddonAsync(item.Path, destination)
                    : await ImportFromArchiveAsync(item.Path, destination);

                if (ok) anySuccess = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Import] Import failed for '{item.Path}': {ex}");
                ReportStatus($"Failed to import '{Path.GetFileName(item.Path)}': {ex.Message}");
            }
        }

        return anySuccess;
    }

    /// <summary>Returns true for .mcpack and .zip extensions.</summary>
    public static bool IsImportableExtension(string ext) =>
        ext.Equals(".mcpack", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Import — internals

    private enum ImportItemKind { Archive, McAddon }
    private record ImportItem(string Path, ImportItemKind Kind);

    /// <summary>True for .mcpack, .zip, and .mcaddon — all types that can be queued.</summary>
    private static bool IsQueueableExtension(string ext) =>
        IsImportableExtension(ext) ||
        ext.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a file path to the correct ImportItem kind based on extension.</summary>
    private static ImportItem ExtToImportItem(string path) =>
        Path.GetExtension(path).Equals(".mcaddon", StringComparison.OrdinalIgnoreCase)
            ? new ImportItem(path, ImportItemKind.McAddon)
            : new ImportItem(path, ImportItemKind.Archive);

    private static string GetImportDestination() =>
        MinecraftUserDataLocator.GetResourcePacksPath(
            EnvironmentVariables.Persistent.IsTargetingPreview,
            development: InstallToDevelopmentPacks,
            createIfMissing: true);

    private static string GetBehaviorImportDestination() =>
        MinecraftUserDataLocator.GetBehaviorPacksPath(
            EnvironmentVariables.Persistent.IsTargetingPreview,
            development: InstallToDevelopmentPacks,
            createIfMissing: true);

    private static bool IsArchiveExtension(string ext) => IsImportableExtension(ext);

    // ── .mcaddon ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an .mcaddon and imports every pack inside it, through the same resource-type
    /// check, duplicate detection and extraction as any other import - so a behaviour pack
    /// gets the same "not a resource pack" question it would get on its own.
    ///
    /// <para><b>An .mcaddon holds its packs in one of two shapes, and often both.</b> As
    /// .mcpack files, each a zip of its own, or as plain folders each holding a manifest.
    /// Both are imported. A folder pack is exactly what an .mcpack is once unzipped, so it
    /// goes through <see cref="ImportPackFromZipAsync"/> with its manifest's folder as the
    /// pack root - no temp copy, no second code path. See <see cref="FindFolderPackManifests"/>
    /// for which folders count.</para>
    ///
    /// <para>An .mcpack is extracted to a temp file first, since a zip inside a zip can't be
    /// opened in place, and is reported and named by its own file name - not the temp file's,
    /// which is what a pack with its manifest at its root would otherwise be installed as.</para>
    /// </summary>
    private static async Task<bool> ImportFromMcAddonAsync(string addonPath, string destination)
    {
        var addonName = Path.GetFileName(addonPath);
        ReportStatus($"Opening addon '{addonName}'…");

        bool anySuccess = false;

        using var addonZip = ZipFile.OpenRead(addonPath);

        var mcpackEntries = addonZip.Entries
            .Where(e => Path.GetExtension(e.Name).Equals(".mcpack", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var folderManifests = FindFolderPackManifests(addonZip);

        var packCount = mcpackEntries.Count + folderManifests.Count;
        if (packCount == 0)
        {
            ReportStatus($"'{addonName}' contains no packs, skipped.");
            return false;
        }

        ReportStatus($"Found {packCount} pack{(packCount == 1 ? "" : "s")} inside '{addonName}'.");

        foreach (var entry in mcpackEntries)
        {
            var tempMcpack = Path.Combine(Path.GetTempPath(), $"mcaddon_extract_{Guid.NewGuid()}.mcpack");
            try
            {
                await Task.Run(() => entry.ExtractToFile(tempMcpack, overwrite: true));
                bool ok = await ImportFromArchiveAsync(tempMcpack, destination, displayName: entry.Name);
                if (ok) anySuccess = true;
            }
            finally
            {
                try { if (File.Exists(tempMcpack)) File.Delete(tempMcpack); }
                catch { /* best-effort cleanup */ }
            }
        }

        foreach (var manifest in folderManifests)
        {
            var folder = PackRootOf(manifest).TrimEnd('/');
            var label = folder.Length == 0 ? addonName : folder.Split('/').Last();
            if (await ImportPackFromZipAsync(addonZip, manifest, label, folder.Length == 0 ? Path.GetFileNameWithoutExtension(addonName) : label, destination))
                anySuccess = true;
        }

        return anySuccess;
    }

    /// <summary>
    /// One manifest per folder pack inside an .mcaddon, outermost first.
    ///
    /// <para>A folder is a pack when it directly holds a manifest.json or pack_manifest.json;
    /// with both, manifest.json wins, the same preference <see cref="FindShallowManifestEntry"/>
    /// has. <b>A manifest inside a folder that is already a pack is part of that pack</b>, not a
    /// pack of its own, so it is skipped - the same outermost-wins rule an .mcpack gets by
    /// taking its shallowest manifest. A manifest at the addon's root makes the whole addon one
    /// pack, which is simply that rule with an empty root.</para>
    /// </summary>
    private static List<ZipArchiveEntry> FindFolderPackManifests(ZipArchive zip)
    {
        var perFolder = zip.Entries
            .Where(e => e.Name.Equals(PackManifest.ModernFileName, StringComparison.OrdinalIgnoreCase)
                     || e.Name.Equals(PackManifest.LegacyFileName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(PackRootOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(e => e.Name.Equals(PackManifest.ModernFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .OrderBy(e => e.FullName.Count(c => c == '/'))
            .ToList();

        var packs = new List<ZipArchiveEntry>();
        foreach (var manifest in perFolder)
        {
            var root = PackRootOf(manifest);
            if (!packs.Any(p => root.StartsWith(PackRootOf(p), StringComparison.OrdinalIgnoreCase)))
                packs.Add(manifest);
        }
        return packs;
    }

    /// <summary>The folder a manifest entry sits in, as a zip path prefix ("Folder/Sub/"), or "" at the root.</summary>
    private static string PackRootOf(ZipArchiveEntry manifest) =>
        manifest.FullName.Contains('/')
            ? manifest.FullName.Substring(0, manifest.FullName.LastIndexOf('/') + 1)
            : string.Empty;

    // ── Archive (.mcpack / .zip) ──────────────────────────────────────────────

    /// <summary>
    /// Imports the one pack an archive holds: the shallowest manifest in it decides where the
    /// pack's root is. <paramref name="displayName"/> is the file name to report and fall back
    /// on; it defaults to the archive's own, and is passed when the archive is a temp file
    /// standing in for something else (an .mcpack taken out of an .mcaddon).
    /// </summary>
    private static async Task<bool> ImportFromArchiveAsync(string archivePath, string destination, string? displayName = null)
    {
        var fileName = displayName ?? Path.GetFileName(archivePath);
        ReportStatus($"Inspecting '{fileName}'…");

        using var zip = ZipFile.OpenRead(archivePath);

        var manifestEntry = FindShallowManifestEntry(zip);

        if (manifestEntry == null)
        {
            ReportStatus($"'{fileName}' has no manifest — not a valid resource pack, skipped.");
            return false;
        }

        return await ImportPackFromZipAsync(zip, manifestEntry, fileName, Path.GetFileNameWithoutExtension(fileName), destination);
    }

    /// <summary>
    /// The shared core of every import: given an open zip and the manifest that defines a pack
    /// inside it, checks the pack is a resource pack, checks for an installed duplicate, and
    /// extracts the manifest's folder - and only that folder - as the pack.
    ///
    /// <para><paramref name="destination"/> is the resource folder. A pack whose manifest
    /// declares a behaviour module, and which the user agrees to import as one, is redirected
    /// to the behaviour folder instead, and its duplicate is looked for there - never among
    /// the resource packs, which is the only thing that differs for it.</para>
    ///
    /// <para>Callers decide which manifest that is, and that is the only thing that differs
    /// between them: an .mcpack/.zip uses its shallowest manifest (<see cref="ImportFromArchiveAsync"/>),
    /// an .mcaddon one per pack folder inside it (<see cref="ImportFromMcAddonAsync"/>).
    /// <paramref name="sourceFileName"/> is what messages call the source; <paramref name="sourceBaseName"/>
    /// is the pack's fallback name, and its folder name when the manifest sits at the zip's
    /// root. They differ for a file (extension off) and are the same for a folder.</para>
    /// </summary>
    private static async Task<bool> ImportPackFromZipAsync(ZipArchive zip, ZipArchiveEntry manifestEntry, string sourceFileName, string sourceBaseName, string destination)
    {

        bool isLegacy = Path.GetFileName(manifestEntry.FullName)
            .Equals(PackManifest.LegacyFileName, StringComparison.OrdinalIgnoreCase);

        // Parse manifest once — extracts resource-type flag (from modules) and
        // header UUID (for dupe detection). Single stream read, no redundancy.
        PackManifest? parsed = null;
        try
        {
            using var ms = new MemoryStream();
            using (var entryStream = manifestEntry.Open())
                await entryStream.CopyToAsync(ms);
            ms.Position = 0;
            parsed = PackManifest.FromStream(ms, isLegacy, manifestEntry.FullName);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse incoming manifest: {ex.Message}");
        }

        // ── Resource-type check (modules section) ─────────────────────────────
        // Runs BEFORE dupe detection, because a behaviour pack changes both where it is
        // installed and which folders its duplicate could be in.
        bool isResourcePack = parsed?.HasResourceModule ?? false;
        bool asBehaviorPack = false;

        if (!isResourcePack)
        {
            string packDisplayName = parsed?.HeaderName is { Length: > 0 } n
                ? n
                : sourceBaseName;

            if (parsed?.HasBehaviorModule == true)
            {
                bool importAsBehavior = ConfirmBehaviorImport != null
                    && await ConfirmBehaviorImport(packDisplayName);

                if (!importAsBehavior)
                {
                    ReportStatus($"Skipped '{packDisplayName}': it is a behavior pack.");
                    return false;
                }

                destination = GetBehaviorImportDestination();
                if (string.IsNullOrEmpty(destination))
                {
                    ReportStatus($"Could not import '{packDisplayName}': behavior packs folder not found.");
                    return false;
                }

                asBehaviorPack = true;
                ReportStatus($"Importing '{packDisplayName}' as a behavior pack.");
            }
            else
            {
                bool importAnyway = ConfirmNonResourceImport != null
                    && await ConfirmNonResourceImport(packDisplayName);

                if (!importAnyway)
                {
                    ReportStatus($"Skipped '{packDisplayName}': not identified as a resource pack.");
                    return false;
                }

                ReportStatus($"Importing '{packDisplayName}' as requested (not confirmed resource pack).");
            }
        }

        // ── Dupe detection (header UUID only) ────────────────────────────────
        // The game uses header UUID to identify packs. Module UUID is separate
        // and optional; checking it would over-restrict matching.
        if (parsed?.HeaderUuid is { Length: > 0 } headerUuid)
        {
            var existingMatch = FindExistingPackMatch(headerUuid, asBehaviorPack);
            if (existingMatch != null)
            {
                string displayName = parsed.HeaderName ?? sourceBaseName;

                bool overwrite = ConfirmOverwrite != null
                    && await ConfirmOverwrite(displayName, existingMatch);

                if (overwrite)
                {
                    ReportStatus($"Replacing existing pack at '{Path.GetFileName(existingMatch)}'…");
                    if (await DeletePackAsync(existingMatch) == null)
                    {
                        ReportStatus("Could not remove existing pack — import aborted.");
                        return false;
                    }
                }
                else
                {
                    ReportStatus($"Import of '{displayName}' skipped (duplicate already installed).");
                    return false;
                }
            }
        }

        // ── Extract ───────────────────────────────────────────────────────────
        var packRootInZip = PackRootOf(manifestEntry);

        var rawFolderName = string.IsNullOrEmpty(packRootInZip)
            ? sourceBaseName
            : packRootInZip.TrimEnd('/').Split('/').Last();

        var folderName = SanitizeFolderName(rawFolderName);
        var finalDestination = ResolveUniqueDestination(destination, folderName);

        ReportStatus($"Extracting '{sourceFileName}' -> '{Path.GetFileName(finalDestination)}'…");

        Directory.CreateDirectory(finalDestination);

        foreach (var (entry, targetPath, isDirectory) in Helpers.EnumerateZipFolderExtraction(zip, packRootInZip, finalDestination))
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await Task.Run(() =>
            {
                using var src = entry.Open();
                using var dest = File.Create(targetPath);
                src.CopyTo(dest);
            });
        }

        ReportStatus(asBehaviorPack
            ? $"Imported '{Path.GetFileName(finalDestination)}' into behavior packs successfully."
            : $"Imported '{Path.GetFileName(finalDestination)}' successfully.");
        return true;
    }

    private static ZipArchiveEntry? FindShallowManifestEntry(ZipArchive zip)
    {
        ZipArchiveEntry? Shallowest(IEnumerable<ZipArchiveEntry> entries) =>
            entries
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .FirstOrDefault();

        var modern = Shallowest(zip.Entries.Where(e =>
            Path.GetFileName(e.FullName).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)));

        if (modern != null) return modern;

        return Shallowest(zip.Entries.Where(e =>
            Path.GetFileName(e.FullName).Equals("pack_manifest.json", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Manifest parsing ──────────────────────────────────────────────────────
    //
    // Three things are needed from one parse, and PackManifest exposes all three:
    //   HeaderUuid        — dupe detection (header section only; legacy reads header.pack_id)
    //   HeaderName        — display name for the confirmation dialogs
    //   HasResourceModule — resource-vs-behaviour check (modules section only; legacy nests
    //                       them inside the header)
    // Separation of concerns is preserved: identity lives in the header, the resource/behaviour
    // distinction lives in the modules array, and neither bleeds into the other.

    // ── Dupe detection — header UUID only ────────────────────────────────────

    /// <summary>
    /// Scans resource_packs and development_resource_packs - or their behaviour-pack
    /// counterparts when <paramref name="behaviorPacks"/> - up to 2 folder levels
    /// deep (the game's own read limit) and returns the folder path of the first
    /// existing pack whose header UUID matches <paramref name="headerUuid"/>, or null.
    /// </summary>
    private static string? FindExistingPackMatch(string headerUuid, bool behaviorPacks)
    {
        var isPreview = EnvironmentVariables.Persistent.IsTargetingPreview;
        var scanRoots = (behaviorPacks
                ? MinecraftUserDataLocator.GetExistingBehaviorPackScanPaths(isPreview)
                : MinecraftUserDataLocator.GetExistingResourcePackScanPaths(isPreview))
            .ToList();

        foreach (var root in scanRoots)
        {
            foreach (var dir1 in SafeEnumerateDirectories(root))
            {
                var match = CheckDirForMatch(dir1, headerUuid);
                if (match != null) return match;

                foreach (var dir2 in SafeEnumerateDirectories(dir1))
                {
                    match = CheckDirForMatch(dir2, headerUuid);
                    if (match != null) return match;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks one directory for a header UUID match. Prefers manifest.json; if a
    /// modern manifest is found but doesn't match, does not also check
    /// pack_manifest.json — they can't both be authoritative in the same directory.
    /// </summary>
    private static string? CheckDirForMatch(string dir, string headerUuid)
    {
        var modern = Path.Combine(dir, PackManifest.ModernFileName);
        if (File.Exists(modern))
        {
            var parsed = PackManifest.FromFile(modern);
            if (parsed?.HeaderUuid?.Equals(headerUuid, StringComparison.OrdinalIgnoreCase) == true)
                return dir;
            return null; // modern manifest found, no match — don't also check legacy
        }

        var legacy = Path.Combine(dir, PackManifest.LegacyFileName);
        if (File.Exists(legacy))
        {
            var parsed = PackManifest.FromFile(legacy);
            if (parsed?.HeaderUuid?.Equals(headerUuid, StringComparison.OrdinalIgnoreCase) == true)
                return dir;
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static string ResolveUniqueDestination(string destination, string baseName)
    {
        var candidate = Path.Combine(destination, baseName);
        if (!Directory.Exists(candidate)) return candidate;

        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(destination, $"{baseName}_{i}");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Strips invalid filename characters, trims whitespace, and caps at 10 characters
    /// to limit path depth impact. ResolveUniqueDestination may append _N, keeping
    /// final names to roughly 13 characters maximum.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "ImportedPack";

        if (sanitized.Length > 10)
            sanitized = sanitized.Substring(0, 10).TrimEnd('_', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "ImportedPk" : sanitized;
    }

    private static void ReportStatus(string message)
    {
        Trace.WriteLine($"[Import] {message}");
        ImportStatusChanged?.Invoke(message);
    }

    #endregion

    #region Delete

    /// <summary>
    /// Walks upward from <paramref name="packLocation"/> until the parent is a known
    /// scan root (resource_packs, development_resource_packs, or their behaviour-pack
    /// counterparts, for either game variant), then deletes that immediate child folder.
    /// Aborts safely if no scan root is found in the path, so arbitrary folders can never
    /// be accidentally removed. The behaviour folders are roots only so that replacing an
    /// installed behaviour pack on import can remove the old copy; nothing else in the app
    /// hands this a behaviour pack.
    /// </summary>
    public static async Task<string?> DeletePackAsync(string packLocation)
    {
        if (string.IsNullOrEmpty(packLocation) || !Directory.Exists(packLocation))
        {
            Trace.WriteLine($"[Delete] Location doesn't exist or is empty: '{packLocation}'");
            return null;
        }

        var scanRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (bool preview in new[] { false, true })
        {
            foreach (var root in new[]
            {
                MinecraftUserDataLocator.GetResourcePacksPath(preview, development: false),
                MinecraftUserDataLocator.GetResourcePacksPath(preview, development: true),
                MinecraftUserDataLocator.GetBehaviorPacksPath(preview, development: false),
                MinecraftUserDataLocator.GetBehaviorPacksPath(preview, development: true),
            })
            {
                if (!string.IsNullOrEmpty(root))
                    scanRoots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
            }
        }

        var current = Path.GetFullPath(packLocation).TrimEnd(Path.DirectorySeparatorChar);

        while (true)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent == null) break;

            parent = parent.TrimEnd(Path.DirectorySeparatorChar);

            if (scanRoots.Contains(parent))
            {
                try
                {
                    await Task.Run(() => Directory.Delete(current, recursive: true));
                    Trace.WriteLine($"[Delete] Deleted '{current}'.");
                    return current;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Delete] Failed to delete '{current}': {ex.Message}");
                    return null;
                }
            }

            current = parent;
        }

        Trace.WriteLine($"[Delete] Could not resolve pack root for '{packLocation}' — no known scan root in path. Aborted.");
        return null;
    }

    #endregion
}


/// <summary>
/// Ready-made ContentDialog implementations for ExpImpDel.ConfirmOverwrite,
/// ConfirmNonResourceImport and ConfirmBehaviorImport, parameterized on whichever window is doing the importing.
/// PackBrowserOverlay's Add-pack button/drag-and-drop and MainWindow's .mcpack
/// file-activation path both wire these in as-is.
///
/// <para>Each takes the element the dialog should belong to rather than a window: the pack
/// browser is an overlay inside MainWindow now, and what a ContentDialog actually needs from
/// its host is a XamlRoot, a dispatcher and a theme - all three of which any FrameworkElement
/// in the tree has.</para>
/// </summary>
public static class ImportDialogs
{
    public static Task<bool> ShowOverwriteDialogAsync(FrameworkElement host, string packName, string existingPath)
    {
        var tcs = new TaskCompletionSource<bool>();

        host.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var existingFolderName = Path.GetFileName(
                    existingPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var dialog = new ContentDialog
                {
                    Title = "Pack already installed",
                    Content = $"\"{packName}\" is already installed at \"{existingFolderName}\".\n\nReplace it with the incoming version?",
                    PrimaryButtonText = "Replace",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = host.XamlRoot,
                    RequestedTheme = host.ActualTheme
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ImportDialogs] Overwrite dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Shown when a pack's manifest has no module of type "resources", or when the type
    /// could not be determined. Defaults to Skip (safe).
    /// </summary>
    public static Task<bool> ShowNonResourceDialogAsync(FrameworkElement host, string packName)
    {
        var tcs = new TaskCompletionSource<bool>();

        host.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Not a resource pack",
                    Content = $"\"{packName}\" does not appear to be a resource pack, no module of type \"resources\" was found in its manifest.\n\nImport it anyway?",
                    PrimaryButtonText = "Import anyway",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = host.XamlRoot,
                    RequestedTheme = host.ActualTheme
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ImportDialogs] Non-resource dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Shown when a pack's manifest declares a behaviour module. The two answers are skip and
    /// import into the behaviour folder - importing it as a resource pack would install
    /// something the game never loads. Defaults to Skip, like every import dialog.
    /// </summary>
    public static Task<bool> ShowBehaviorDialogAsync(FrameworkElement host, string packName)
    {
        var tcs = new TaskCompletionSource<bool>();

        host.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "This is a behavior pack",
                    Content = $"\"{packName}\" is a behavior pack, not a resource pack.\n\nIt can still be imported into the game's behavior packs folder, where it will be available in-game. It will not appear among the packs listed in this app.",
                    PrimaryButtonText = "Import as behavior pack",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = host.XamlRoot,
                    RequestedTheme = host.ActualTheme
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ImportDialogs] Behavior dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        return tcs.Task;
    }
}
