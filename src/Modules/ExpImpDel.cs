using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

    /// <summary>When true, packs land in development_resource_packs instead of resource_packs.</summary>
    public static bool InstallToDevelopmentPacks = false;

    /// <summary>Progress/status callback fired during import operations.</summary>
    public static event Action<string>? ImportStatusChanged;

    /// <summary>
    /// Callback invoked when a duplicate UUID match is found. Must return true to
    /// overwrite, false to skip. If null, duplicates are silently skipped.
    /// Parameters: (incomingPackName, existingFolderPath)
    /// </summary>
    public static Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Opens a file picker and imports the chosen packs. Accepts .mcpack, .zip,
    /// and .mcaddon. <paramref name="ownerHwnd"/> must be the PackBrowserWindow
    /// handle — NOT the main window — so the picker stays above the right window.
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
    ///   <item>.mcaddon        — every .mcpack inside is queued.</item>
    ///   <item>folder          — root of folder scanned (non-recursively) for
    ///                           .mcpack and .zip files; each queued.</item>
    /// </list>
    /// Bare folders are never imported as packs themselves — only archives found
    /// inside them are queued.
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
                // Non-recursive scan: queue archives found at the root of the folder.
                var found = Directory
                    .GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsArchiveExtension(Path.GetExtension(f)))
                    .Select(f => new ImportItem(f, ImportItemKind.Archive))
                    .ToList();

                if (found.Count == 0)
                    ReportStatus($"No .mcpack or .zip files found in the root of '{Path.GetFileName(path)}'.");
                else
                    queue.AddRange(found);
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

    /// <summary>Returns true for .mcpack and .zip extensions.</summary>
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

    // ── .mcaddon ─────────────────────────────────────────────────────────────

    private static async Task<bool> ImportFromMcAddonAsync(string addonPath, string destination)
    {
        ReportStatus($"Opening addon '{Path.GetFileName(addonPath)}'…");

        bool anySuccess = false;

        using var addonZip = ZipFile.OpenRead(addonPath);

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

    // ── Archive (.mcpack / .zip) ──────────────────────────────────────────────

    private static async Task<bool> ImportFromArchiveAsync(string archivePath, string destination)
    {
        ReportStatus($"Inspecting '{Path.GetFileName(archivePath)}'…");

        using var zip = ZipFile.OpenRead(archivePath);

        // Find the shallowest manifest.json; fall back to pack_manifest.json.
        var manifestEntry = FindShallowManifestEntry(zip);

        if (manifestEntry == null)
        {
            ReportStatus($"'{Path.GetFileName(archivePath)}' has no manifest.json or pack_manifest.json — not a valid resource pack, skipped.");
            return false;
        }

        bool isLegacy = Path.GetFileName(manifestEntry.FullName)
            .Equals("pack_manifest.json", StringComparison.OrdinalIgnoreCase);

        // Read manifest for dupe detection — soft: any failure falls through to normal import.
        PackIdentity? incomingIdentity = null;
        try
        {
            using var ms = new MemoryStream();
            using (var entryStream = manifestEntry.Open())
                await entryStream.CopyToAsync(ms);
            ms.Position = 0;
            incomingIdentity = ParsePackIdentity(ms, isLegacy);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse incoming manifest for dupe check: {ex.Message}");
        }

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
                    if (await DeletePackAsync(existingMatch) == null)
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

        // Determine the pack root prefix inside the archive.
        var packRootInZip = manifestEntry.FullName.Contains('/')
            ? manifestEntry.FullName.Substring(0, manifestEntry.FullName.LastIndexOf('/') + 1)
            : string.Empty;

        // Derive and sanitize the destination folder name, capped at 10 chars so that
        // deeply nested Minecraft paths don't exceed the engine's file path limits.
        // ResolveUniqueDestination may append _1, _2 … making the final name ≤ ~13 chars.
        var rawFolderName = string.IsNullOrEmpty(packRootInZip)
            ? Path.GetFileNameWithoutExtension(archivePath)
            : packRootInZip.TrimEnd('/').Split('/').Last();

        var folderName = SanitizeFolderName(rawFolderName);
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

    /// <summary>
    /// Returns the shallowest manifest.json entry in the archive, or the shallowest
    /// pack_manifest.json if no manifest.json exists. Modern always wins over legacy.
    /// </summary>
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

    // ── Dupe detection ────────────────────────────────────────────────────────

    /// <summary>
    /// Identity extracted from a manifest for duplicate comparison.
    /// Matched on header UUID + all module UUIDs. Version is intentionally excluded
    /// so that importing an update still triggers the "replace?" prompt.
    /// </summary>
    private record PackIdentity(string Name, string HeaderUuid, IReadOnlyList<string> ModuleUuids);

    /// <summary>
    /// Parses a PackIdentity from a manifest stream.
    /// Handles both modern manifest.json and legacy pack_manifest.json.
    /// Tolerant of // and /* */ comments via JsonLoadSettings.
    /// </summary>
    private static PackIdentity? ParsePackIdentity(Stream manifestStream, bool isLegacy)
    {
        using var sr = new StreamReader(manifestStream, leaveOpen: true);
        var json = sr.ReadToEnd();

        JObject root;
        try
        {
            using var stringReader = new StringReader(json);
            using var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None };
            var loadSettings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
            root = JObject.Load(jsonReader, loadSettings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] JSON parse error in manifest: {ex.Message}");
            return null;
        }

        if (isLegacy)
        {
            // Legacy: header.pack_id is the primary UUID; modules[] are nested inside header.
            var header = root["header"];
            var headerUuid = header?["pack_id"]?.ToString();
            var name = header?["name"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(headerUuid)) return null;

            var moduleUuids = header?["modules"]
                ?.Children<JObject>()
                .Select(m => m["uuid"]?.ToString())
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .ToList() ?? new List<string>();

            return new PackIdentity(name, headerUuid, moduleUuids);
        }
        else
        {
            // Modern: header.uuid at root level; modules[] at root level.
            var headerUuid = root["header"]?["uuid"]?.ToString();
            var name = root["header"]?["name"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(headerUuid)) return null;

            var moduleUuids = root["modules"]
                ?.Children<JObject>()
                .Select(m => m["uuid"]?.ToString())
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .ToList() ?? new List<string>();

            return new PackIdentity(name, headerUuid, moduleUuids);
        }
    }

    private static PackIdentity? ParsePackIdentityFromFile(string manifestPath, bool isLegacy)
    {
        try
        {
            using var fs = File.OpenRead(manifestPath);
            return ParsePackIdentity(fs, isLegacy);
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

        var setA = new HashSet<string>(a.ModuleUuids, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.ModuleUuids, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB);
    }

    /// <summary>
    /// Scans both resource_packs and development_resource_packs (for the current
    /// IsTargetingPreview value) up to 2 folder levels deep — the maximum depth the
    /// game engine reads — and returns the pack folder path of the first existing pack
    /// whose identity matches <paramref name="incoming"/>, or null.
    /// </summary>
    private static string? FindExistingPackMatch(PackIdentity incoming)
    {
        var scanRoots = MinecraftUserDataLocator
            .GetExistingResourcePackScanPaths(TunerVariables.Persistent.IsTargetingPreview)
            .ToList();

        foreach (var root in scanRoots)
        {
            foreach (var dir1 in SafeEnumerateDirectories(root))
            {
                var match = CheckDirForMatch(dir1, incoming);
                if (match != null) return match;

                foreach (var dir2 in SafeEnumerateDirectories(dir1))
                {
                    match = CheckDirForMatch(dir2, incoming);
                    if (match != null) return match;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks one directory for a manifest match. Prefers manifest.json; if found
    /// but no match, does NOT also check pack_manifest.json (they can't both be
    /// authoritative in the same directory).
    /// </summary>
    private static string? CheckDirForMatch(string dir, PackIdentity incoming)
    {
        var modern = Path.Combine(dir, "manifest.json");
        if (File.Exists(modern))
        {
            var identity = ParsePackIdentityFromFile(modern, isLegacy: false);
            if (identity != null && IdentitiesMatch(incoming, identity)) return dir;
            return null;
        }

        var legacy = Path.Combine(dir, "pack_manifest.json");
        if (File.Exists(legacy))
        {
            var identity = ParsePackIdentityFromFile(legacy, isLegacy: true);
            if (identity != null && IdentitiesMatch(incoming, identity)) return dir;
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a unique destination path by appending _1, _2 … to
    /// <paramref name="baseName"/> until a free slot is found.
    /// </summary>
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
    /// Strips characters that are invalid in folder names, trims whitespace, and caps
    /// the result at 10 characters. This keeps installed pack folder names short enough
    /// that Minecraft's internal path-length limits are not at risk even when user data
    /// is nested several levels deep. ResolveUniqueDestination may append a short
    /// numeric suffix (_1 … _N), keeping the final name to ≤ ~13 characters.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "ImportedPack";

        // Cap at 10 characters to limit total path depth impact.
        if (sanitized.Length > 10)
            sanitized = sanitized.Substring(0, 10).TrimEnd('_', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "ImportedPk" : sanitized;
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
    /// Walks upward from <paramref name="packLocation"/> until the parent is a known
    /// scan root (resource_packs or development_resource_packs for either game variant),
    /// then deletes that immediate child folder. Aborts safely if no scan root is found
    /// in the path, so arbitrary folders can never be accidentally removed.
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
