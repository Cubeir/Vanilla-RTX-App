using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Storage.Pickers;
using static Vanilla_RTX_App.MainWindow;

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

        var unneededFiles = new[] { "contents.json", "textures_list.json", "signatures.json", "texture_list.json", "signature.json" };
        await Task.Run(() =>
        {
            foreach (var name in unneededFiles)
                foreach (var file in Directory.GetFiles(packFolderPath, name, SearchOption.AllDirectories))
                    File.Delete(file);
        });

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
    /// Invoked when a pack's manifest has no module of type "resources", or when the
    /// type cannot be determined. Return true to import anyway, false to skip.
    /// If null, non-resource packs are silently skipped.
    /// Parameter: pack display name from the manifest, or filename if unreadable.
    /// </summary>
    public static Func<string, Task<bool>>? ConfirmNonResourceImport { get; set; }

    /// <summary>
    /// Opens a file picker and imports the chosen packs. Accepts .mcpack, .zip,
    /// and .mcaddon. <paramref name="ownerHwnd"/> must be the PackBrowserWindow
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
    ///   <item>.mcaddon        — every resource-pack .mcpack inside is queued.</item>
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
            TunerVariables.Persistent.IsTargetingPreview,
            development: InstallToDevelopmentPacks,
            createIfMissing: true);

    private static bool IsArchiveExtension(string ext) => IsImportableExtension(ext);

    // ── .mcaddon ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an .mcaddon and imports each .mcpack it contains through the normal
    /// archive path, which handles resource-type checking and dupe detection.
    /// Behaviour packs are filtered out automatically at that stage.
    /// </summary>
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
            ReportStatus($"'{Path.GetFileName(addonPath)}' contains no .mcpack files, skipped.");
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

        var manifestEntry = FindShallowManifestEntry(zip);

        if (manifestEntry == null)
        {
            ReportStatus($"'{Path.GetFileName(archivePath)}' has no manifest — not a valid resource pack, skipped.");
            return false;
        }

        bool isLegacy = Path.GetFileName(manifestEntry.FullName)
            .Equals("pack_manifest.json", StringComparison.OrdinalIgnoreCase);

        // Parse manifest once — extracts resource-type flag (from modules) and
        // header UUID (for dupe detection). Single stream read, no redundancy.
        ParsedManifest? parsed = null;
        try
        {
            using var ms = new MemoryStream();
            using (var entryStream = manifestEntry.Open())
                await entryStream.CopyToAsync(ms);
            ms.Position = 0;
            parsed = ParseManifestFull(ms, isLegacy);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse incoming manifest: {ex.Message}");
        }

        // ── Resource-type check (modules section) ─────────────────────────────
        // Runs BEFORE dupe detection. Behaviour packs are caught here.
        bool isResourcePack = parsed?.HasResourceModule ?? false;

        if (!isResourcePack)
        {
            string packDisplayName = parsed?.HeaderName is { Length: > 0 } n
                ? n
                : Path.GetFileNameWithoutExtension(archivePath);

            bool importAnyway = ConfirmNonResourceImport != null
                && await ConfirmNonResourceImport(packDisplayName);

            if (!importAnyway)
            {
                ReportStatus($"Skipped '{packDisplayName}': not identified as a resource pack.");
                return false;
            }

            ReportStatus($"Importing '{packDisplayName}' as requested (not confirmed resource pack).");
        }

        // ── Dupe detection (header UUID only) ────────────────────────────────
        // The game uses header UUID to identify packs. Module UUID is separate
        // and optional; checking it would over-restrict matching.
        if (parsed?.HeaderUuid is { Length: > 0 } headerUuid)
        {
            var existingMatch = FindExistingPackMatch(headerUuid);
            if (existingMatch != null)
            {
                string displayName = parsed.HeaderName ?? Path.GetFileNameWithoutExtension(archivePath);

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
        var packRootInZip = manifestEntry.FullName.Contains('/')
            ? manifestEntry.FullName.Substring(0, manifestEntry.FullName.LastIndexOf('/') + 1)
            : string.Empty;

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

    /// <summary>
    /// Everything needed from one manifest parse:
    /// <list type="bullet">
    ///   <item><see cref="HeaderUuid"/> — for dupe detection (header section only).</item>
    ///   <item><see cref="HeaderName"/> — display name for dialog messages.</item>
    ///   <item><see cref="HasResourceModule"/> — type check (modules section only).</item>
    /// </list>
    /// Separation of concerns: UUID/identity lives in the header; resource/behaviour
    /// distinction lives in the modules array. Neither bleeds into the other.
    /// </summary>
    private record ParsedManifest(
        string? HeaderUuid,
        string? HeaderName,
        bool HasResourceModule);

    /// <summary>
    /// Parses a manifest stream into a <see cref="ParsedManifest"/>.
    /// Handles both modern manifest.json and legacy pack_manifest.json.
    /// Tolerant of // and /* */ comments via JsonLoadSettings.
    /// </summary>
    private static ParsedManifest? ParseManifestFull(Stream manifestStream, bool isLegacy)
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
            // Legacy: UUID lives at header.pack_id; modules[] nested inside header.
            var header = root["header"];
            return new ParsedManifest(
                HeaderUuid: header?["pack_id"]?.ToString(),
                HeaderName: header?["name"]?.ToString(),
                HasResourceModule: HasModuleOfTypeResources(header?["modules"]));
        }
        else
        {
            // Modern: UUID lives at header.uuid; modules[] at root level.
            return new ParsedManifest(
                HeaderUuid: root["header"]?["uuid"]?.ToString(),
                HeaderName: root["header"]?["name"]?.ToString(),
                HasResourceModule: HasModuleOfTypeResources(root["modules"]));
        }
    }

    /// <summary>
    /// Returns true if the modules token contains at least one entry whose "type"
    /// equals "resources" (case-insensitive). Returns false for null/empty/missing.
    /// </summary>
    private static bool HasModuleOfTypeResources(JToken? modulesToken)
    {
        if (modulesToken?.Type != JTokenType.Array) return false;

        return modulesToken
            .Children<JObject>()
            .Any(m => m["type"]?.ToString()
                          .Equals("resources", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static ParsedManifest? ParseManifestFullFromFile(string manifestPath, bool isLegacy)
    {
        try
        {
            using var fs = File.OpenRead(manifestPath);
            return ParseManifestFull(fs, isLegacy);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Import] Could not parse manifest at '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    // ── Dupe detection — header UUID only ────────────────────────────────────

    /// <summary>
    /// Scans resource_packs and development_resource_packs up to 2 folder levels
    /// deep (the game's own read limit) and returns the folder path of the first
    /// existing pack whose header UUID matches <paramref name="headerUuid"/>, or null.
    /// </summary>
    private static string? FindExistingPackMatch(string headerUuid)
    {
        var scanRoots = MinecraftUserDataLocator
            .GetExistingResourcePackScanPaths(TunerVariables.Persistent.IsTargetingPreview)
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
        var modern = Path.Combine(dir, "manifest.json");
        if (File.Exists(modern))
        {
            var parsed = ParseManifestFullFromFile(modern, isLegacy: false);
            if (parsed?.HeaderUuid?.Equals(headerUuid, StringComparison.OrdinalIgnoreCase) == true)
                return dir;
            return null; // modern manifest found, no match — don't also check legacy
        }

        var legacy = Path.Combine(dir, "pack_manifest.json");
        if (File.Exists(legacy))
        {
            var parsed = ParseManifestFullFromFile(legacy, isLegacy: true);
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
            var rp = MinecraftUserDataLocator.GetResourcePacksPath(preview, development: false);
            var drp = MinecraftUserDataLocator.GetResourcePacksPath(preview, development: true);

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

    #endregion
}
