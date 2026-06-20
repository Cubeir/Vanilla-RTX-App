using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

public static class ExpImpDel
{
    // ════════════════════════════════════════════════════════════════════════
    //  EXPORT
    // ════════════════════════════════════════════════════════════════════════

    public static async Task<string?> ExportMCPACK(string packFolderPath, string suggestedName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Instance);
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("Minecraft Pack", new List<string>() { ".mcpack" });
        picker.SuggestedFileName = suggestedName;
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var unneededFiles = new[] { "contents.json", "textures_list.json" };
        foreach (var unneededFile in Directory.GetFiles(packFolderPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (unneededFiles.Contains(Path.GetFileName(unneededFile), StringComparer.OrdinalIgnoreCase))
                File.Delete(unneededFile);
        }

        var file = await picker.PickSaveFileAsync();
        if (file == null) return null;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"temp_{Guid.NewGuid()}.mcpack");
        try
        {
            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                foreach (var filePath in Directory.GetFiles(packFolderPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(packFolderPath, filePath);
                    zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                }
            }

            if (!File.Exists(tempZipPath))
            {
                Trace.WriteLine("Temporary .mcpack archive was deleted before writing to output.");
                return null;
            }

            using var destStream = await file.OpenStreamForWriteAsync();
            using var srcStream = File.OpenRead(tempZipPath);
            await srcStream.CopyToAsync(destStream);

            Trace.WriteLine($"{suggestedName}.mcpack exported successfully.");
            return file.Path;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to export {suggestedName}: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); }
            catch (Exception ex) { Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}"); }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  IMPORT — public surface
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When true, packs land in development_resource_packs instead of resource_packs.
    /// </summary>
    public static bool InstallToDevelopmentPacks = false;

    /// <summary>
    /// Progress/status callback fired during import. Callers subscribe to surface
    /// messages in the UI without this class needing a direct UI reference.
    /// </summary>
    public static event Action<string>? ImportStatusChanged;

    /// <summary>
    /// Callback invoked when a duplicate is detected so the caller (PackBrowserWindow)
    /// can show a confirmation dialog on the UI thread. Must return true to overwrite,
    /// false to skip. If null the duplicate is silently skipped.
    /// Parameters: (incomingPackName, existingFolderPath)
    /// </summary>
    public static Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Opens a file picker and imports the chosen packs. Accepts multiple .mcpack,
    /// .zip, and .mcaddon files. Folders must arrive via drag-and-drop because WinUI 3
    /// cannot mix files and folders in a single picker session.
    /// <paramref name="ownerHwnd"/> must be the handle of the window that should own
    /// the picker — pass the PackBrowserWindow handle, not the main window handle.
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

        var paths = files.Select(f => f.Path).ToList();
        return await ImportFromPathsAsync(paths);
    }

    /// <summary>
    /// Imports packs from an arbitrary list of file/folder paths. The bulk-import
    /// rules applied here:
    /// <list type="bullet">
    ///   <item>.mcpack / .zip  — imported individually.</item>
    ///   <item>.mcaddon        — opened as a zip; every .mcpack inside is queued.</item>
    ///   <item>folder          — the root of the folder is scanned (non-recursively)
    ///                           for .mcpack and .zip files; each is queued.</item>
    /// </list>
    /// </summary>
    public static async Task<bool> ImportFromPathsAsync(IEnumerable<string> paths)
    {
        var destination = GetImportDestination();
        if (string.IsNullOrEmpty(destination))
        {
            ReportStatus("Import failed: Minecraft data directory not found.");
            return false;
        }

        // Expand the input list into a flat queue of importable items.
        var queue = new List<ImportItem>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Non-recursive folder scan: queue .mcpack and .zip files at root level.
                var folderItems = Directory
                    .GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsArchiveExtension(Path.GetExtension(f)))
                    .Select(f => new ImportItem(f, ImportItemKind.Archive));

                queue.AddRange(folderItems);

                if (!queue.Any())
                    ReportStatus($"No .mcpack or .zip files found in the root of '{Path.GetFileName(path)}'.");
            }
            else if (File.Exists(path))
            {
                var ext = Path.GetExtension(path);

                if (ext.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase))
                    queue.Add(new ImportItem(path, ImportItemKind.McAddon));
                else if (IsArchiveExtension(ext))
                    queue.Add(new ImportItem(path, ImportItemKind.Archive));
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
                Trace.WriteLine($"Import failed for '{item.Path}': {ex}");
                ReportStatus($"Failed to import '{Path.GetFileName(item.Path)}': {ex.Message}");
            }
        }

        return anySuccess;
    }

    /// <summary>Returns true for file extensions the importer accepts (.mcpack, .zip).</summary>
    public static bool IsImportableExtension(string ext) =>
        ext.Equals(".mcpack", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    // ════════════════════════════════════════════════════════════════════════
    //  IMPORT — internals
    // ════════════════════════════════════════════════════════════════════════

    private enum ImportItemKind { Archive, McAddon }
    private record ImportItem(string Path, ImportItemKind Kind);

    private static string GetImportDestination() =>
        InstallToDevelopmentPacks
            ? MinecraftUserDataLocator.GetDevelopmentResourcePacksPath(
                TunerVariables.Persistent.IsTargetingPreview, createIfMissing: true)
            : MinecraftUserDataLocator.GetResourcePacksPath(
                TunerVariables.Persistent.IsTargetingPreview, createIfMissing: true);

    private static bool IsArchiveExtension(string ext) => IsImportableExtension(ext);

    // ── .mcaddon ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an .mcaddon (which is just a zip) and imports every .mcpack entry
    /// it contains. Non-mcpack content is ignored.
    /// </summary>
    private static async Task<bool> ImportFromMcAddonAsync(string addonPath, string destination)
    {
        ReportStatus($"Opening addon '{Path.GetFileName(addonPath)}'…");

        bool anySuccess = false;

        using var addonZip = ZipFile.OpenRead(addonPath);

        // Find all .mcpack entries at any depth.
        var mcpackEntries = addonZip.Entries
            .Where(e => Path.GetExtension(e.Name).Equals(".mcpack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mcpackEntries.Count == 0)
        {
            ReportStatus($"'{Path.GetFileName(addonPath)}' contains no .mcpack files — skipped.");
            return false;
        }

        ReportStatus($"Found {mcpackEntries.Count} pack{(mcpackEntries.Count == 1 ? "" : "s")} inside '{Path.GetFileName(addonPath)}'.");

        foreach (var entry in mcpackEntries)
        {
            // Extract the inner .mcpack to a temp file, then run the normal archive import.
            var tempMcpack = Path.Combine(Path.GetTempPath(), $"mcaddon_extract_{Guid.NewGuid()}.mcpack");
            try
            {
                await Task.Run(() => entry.ExtractToFile(tempMcpack, overwrite: true));
                bool ok = await ImportFromArchiveAsync(tempMcpack, destination);
                if (ok) anySuccess = true;
            }
            finally
            {
                try { if (File.Exists(tempMcpack)) File.Delete(tempMcpack); }
                catch { /* best-effort cleanup */ }
            }
        }

        return anySuccess;
    }

    // ── Archive (.mcpack / .zip) ─────────────────────────────────────────────

    private static async Task<bool> ImportFromArchiveAsync(string archivePath, string destination)
    {
        ReportStatus($"Inspecting '{Path.GetFileName(archivePath)}'…");

        using var zip = ZipFile.OpenRead(archivePath);

        // Find the shallowest manifest.json — handles messy zips with deep nesting.
        var manifestEntry = zip.Entries
            .Where(e => Path.GetFileName(e.FullName).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/'))
            .FirstOrDefault();

        if (manifestEntry == null)
        {
            ReportStatus($"'{Path.GetFileName(archivePath)}' has no manifest.json — not a valid resource pack, skipped.");
            return false;
        }

        // Read and parse the incoming manifest for dupe detection.
        PackIdentity? incomingIdentity = null;
        try
        {
            using var ms = new MemoryStream();
            using (var entryStream = manifestEntry.Open())
                await entryStream.CopyToAsync(ms);
            ms.Position = 0;
            incomingIdentity = ParsePackIdentity(ms);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse incoming manifest for dupe check: {ex.Message}");
            // Soft fallback — continue without dupe detection.
        }

        // Dupe detection against existing installed packs (both folders).
        if (incomingIdentity != null)
        {
            var existingMatch = FindExistingPackMatch(incomingIdentity);
            if (existingMatch != null)
            {
                bool overwrite = ConfirmOverwrite != null
                    && await ConfirmOverwrite(incomingIdentity.Name, existingMatch);

                if (overwrite)
                {
                    ReportStatus($"Replacing existing pack at '{Path.GetFileName(existingMatch)}'…");
                    var deleted = await DeletePackAsync(existingMatch);
                    if (deleted == null)
                    {
                        ReportStatus($"Could not remove existing pack — import aborted.");
                        return false;
                    }
                }
                else
                {
                    ReportStatus($"Import of '{incomingIdentity.Name}' skipped (duplicate already installed).");
                    return false;
                }
            }
        }

        // Determine the pack root prefix inside the archive.
        var packRootInZip = manifestEntry.FullName.Contains('/')
            ? manifestEntry.FullName.Substring(0, manifestEntry.FullName.LastIndexOf('/') + 1)
            : string.Empty;

        var folderName = string.IsNullOrEmpty(packRootInZip)
            ? Path.GetFileNameWithoutExtension(archivePath)
            : packRootInZip.TrimEnd('/').Split('/').Last();

        folderName = SanitizeFolderName(folderName);
        var finalDestination = ResolveUniqueDestination(destination, folderName);

        ReportStatus($"Extracting '{Path.GetFileName(archivePath)}' → '{Path.GetFileName(finalDestination)}'…");

        Directory.CreateDirectory(finalDestination);

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(packRootInZip, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = entry.FullName
                .Substring(packRootInZip.Length)
                .Replace('/', Path.DirectorySeparatorChar);

            if (string.IsNullOrEmpty(relativePath)) continue;

            var targetPath = Path.Combine(finalDestination, relativePath);

            if (entry.FullName.EndsWith('/'))
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

        ReportStatus($"Imported '{Path.GetFileName(finalDestination)}' successfully.");
        return true;
    }

    // ── Dupe detection ────────────────────────────────────────────────────────

    /// <summary>
    /// Identity extracted from a manifest.json for duplicate comparison.
    /// We match on header UUID + all module UUIDs — version intentionally excluded
    /// so that importing an update still triggers the "replace?" prompt.
    /// </summary>
    private record PackIdentity(string Name, string HeaderUuid, IReadOnlyList<string> ModuleUuids);

    private static PackIdentity? ParsePackIdentity(Stream manifestStream)
    {
        using var reader = new System.IO.StreamReader(manifestStream, leaveOpen: true);
        var json = reader.ReadToEnd();
        var root = Newtonsoft.Json.Linq.JObject.Parse(json);

        var headerUuid = root["header"]?["uuid"]?.ToString();
        var name = root["header"]?["name"]?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(headerUuid)) return null;

        var moduleUuids = root["modules"]
            ?.Children<Newtonsoft.Json.Linq.JObject>()
            .Select(m => m["uuid"]?.ToString())
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList()
            ?? new List<string>();

        return new PackIdentity(name, headerUuid, moduleUuids);
    }

    private static PackIdentity? ParsePackIdentityFromFile(string manifestPath)
    {
        try
        {
            using var fs = File.OpenRead(manifestPath);
            return ParsePackIdentity(fs);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse manifest at '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    private static bool IdentitiesMatch(PackIdentity a, PackIdentity b)
    {
        if (!a.HeaderUuid.Equals(b.HeaderUuid, StringComparison.OrdinalIgnoreCase))
            return false;

        // All module UUIDs must match (order-independent).
        var setA = new HashSet<string>(a.ModuleUuids, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.ModuleUuids, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }

    /// <summary>
    /// Scans both resource_packs and development_resource_packs (for the current
    /// IsTargetingPreview value) up to 2 folder levels deep — the maximum depth
    /// the game engine reads — and returns the manifest.json path of the first
    /// existing pack whose identity matches <paramref name="incoming"/>, or null.
    /// </summary>
    private static string? FindExistingPackMatch(PackIdentity incoming)
    {
        var scanRoots = MinecraftUserDataLocator
            .GetExistingResourcePackScanPaths(TunerVariables.Persistent.IsTargetingPreview)
            .ToList();

        foreach (var root in scanRoots)
        {
            // Depth 1: root/folder/manifest.json
            foreach (var dir1 in SafeEnumerateDirectories(root))
            {
                var m1 = Path.Combine(dir1, "manifest.json");
                if (File.Exists(m1))
                {
                    var identity = ParsePackIdentityFromFile(m1);
                    if (identity != null && IdentitiesMatch(incoming, identity))
                        return dir1; // return the pack folder, not the manifest
                }

                // Depth 2: root/folder/subfolder/manifest.json
                foreach (var dir2 in SafeEnumerateDirectories(dir1))
                {
                    var m2 = Path.Combine(dir2, "manifest.json");
                    if (File.Exists(m2))
                    {
                        var identity = ParsePackIdentityFromFile(m2);
                        if (identity != null && IdentitiesMatch(incoming, identity))
                            return dir2;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ── Folder import ─────────────────────────────────────────────────────────

    /// <summary>
    /// Imports a bare folder by finding its shallowest manifest.json, treating
    /// that manifest's parent as the pack root, zipping to temp, and extracting
    /// to destination. Cross-drive safe; no partial copies.
    /// </summary>
    public static async Task<bool> ImportFromFolderAsync(string sourceFolder, string destination)
    {
        ReportStatus($"Inspecting '{Path.GetFileName(sourceFolder)}'…");

        var allManifests = Directory
            .EnumerateFiles(sourceFolder, "manifest.json", SearchOption.AllDirectories)
            .Select(p => new { Path = p, Depth = p.Split(Path.DirectorySeparatorChar).Length })
            .OrderBy(x => x.Depth)
            .ToList();

        if (allManifests.Count == 0)
        {
            ReportStatus($"'{Path.GetFileName(sourceFolder)}' has no manifest.json — not a valid resource pack, skipped.");
            return false;
        }

        var packRoot = Path.GetDirectoryName(allManifests[0].Path)!;

        // Dupe detection for folder imports.
        PackIdentity? incomingIdentity = null;
        try { incomingIdentity = ParsePackIdentityFromFile(allManifests[0].Path); }
        catch (Exception ex) { Trace.WriteLine($"[Import] Could not parse manifest for dupe check: {ex.Message}"); }

        if (incomingIdentity != null)
        {
            var existingMatch = FindExistingPackMatch(incomingIdentity);
            if (existingMatch != null)
            {
                bool overwrite = ConfirmOverwrite != null
                    && await ConfirmOverwrite(incomingIdentity.Name, existingMatch);

                if (overwrite)
                {
                    ReportStatus($"Replacing existing pack at '{Path.GetFileName(existingMatch)}'…");
                    var deleted = await DeletePackAsync(existingMatch);
                    if (deleted == null)
                    {
                        ReportStatus("Could not remove existing pack — import aborted.");
                        return false;
                    }
                }
                else
                {
                    ReportStatus($"Import of '{incomingIdentity.Name}' skipped (duplicate already installed).");
                    return false;
                }
            }
        }

        var folderName = SanitizeFolderName(Path.GetFileName(packRoot));
        var finalDestination = ResolveUniqueDestination(destination, folderName);

        ReportStatus($"Copying '{Path.GetFileName(packRoot)}' → '{Path.GetFileName(finalDestination)}'…");

        var tempZip = Path.Combine(Path.GetTempPath(), $"pack_import_{Guid.NewGuid()}.zip");
        try
        {
            await Task.Run(() =>
                ZipFile.CreateFromDirectory(packRoot, tempZip, CompressionLevel.Fastest, includeBaseDirectory: false));
            await Task.Run(() =>
                ZipFile.ExtractToDirectory(tempZip, finalDestination, overwriteFiles: false));

            ReportStatus($"Imported '{Path.GetFileName(finalDestination)}' successfully.");
            return true;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); }
            catch (Exception ex) { Trace.WriteLine($"Warning: couldn't delete temp zip: {ex.Message}"); }
        }
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

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "ImportedPack" : sanitized;
    }

    private static void ReportStatus(string message)
    {
        Trace.WriteLine($"[Import] {message}");
        ImportStatusChanged?.Invoke(message);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DELETE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deletes the top-level pack folder for the given pack location.
    /// Walks upward from <paramref name="packLocation"/> until the parent is a
    /// known scan root (resource_packs or development_resource_packs for either
    /// game variant), then deletes that immediate child folder. Aborts safely
    /// if no scan root is found, so arbitrary folders can never be nuked.
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
            var rp = MinecraftUserDataLocator.GetResourcePacksPath(preview);
            var drp = MinecraftUserDataLocator.GetDevelopmentResourcePacksPath(preview);

            if (!string.IsNullOrEmpty(rp))
                scanRoots.Add(Path.GetFullPath(rp).TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(drp))
                scanRoots.Add(Path.GetFullPath(drp).TrimEnd(Path.DirectorySeparatorChar));
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
}
