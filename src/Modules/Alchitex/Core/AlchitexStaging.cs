using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// Alchitex never generates onto the pack the user selected - it always works on a copy,
/// staged under a name that unambiguously marks it as Alchitex's own in-progress work,
/// sitting right next to the original pack (has to - that's the only place Minecraft will
/// actually discover it as a resource pack).
///
/// The temp folder only ever gets renamed to its real, final "&lt;name&gt;_RTX" form if the
/// entire pipeline completes successfully. Anything short of full success - a thrown
/// exception, a cancelled run, the app getting force-closed, a crash - leaves the temp
/// folder exactly where it is, still wearing its "alchitex_temp_" prefix. That's
/// deliberate: it means a failed run is trivially identifiable and safe to nuke, and
/// CleanupOrphanedTempFolders (run before every new batch, and again after every batch
/// ends) is the single place that ever has to reason about "is this thing safe to delete" -
/// the answer is always yes, because nothing that still has the prefix ever became a real
/// pack.
///
/// Both temp and final names are kept short on purpose: some packs get buried several
/// subpacks/folders deep, and long folder names stacked on top of that has been known to
/// trip the game on Windows' path-length limits. See SanitizeFolderName.
/// </summary>
public static class AlchitexStaging
{
    public const string TempFolderPrefix = "alchitex_temp_";
    private const string FinalSuffix = "_RTX";

    #region Pack Copy & Promotion

    public static string CreateTempCopy(string sourcePackPath)
    {
        var trimmedSource = sourcePackPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmedSource)
            ?? throw new InvalidOperationException($"Couldn't determine the parent directory of '{sourcePackPath}'.");

        var originalName = Path.GetFileName(trimmedSource);
        // Short guid (8 hex chars) rather than a full Guid("N") - this only needs to avoid
        // collisions within a single run/session, not cryptographic uniqueness, and every
        // character here counts toward the path-length concern above.
        var shortGuid = Guid.NewGuid().ToString("N")[..8];
        var tempName = $"{TempFolderPrefix}{shortGuid}_{SanitizeFolderName(originalName)}";
        var tempPath = Path.Combine(parent, tempName);

        Directory.CreateDirectory(tempPath);
        CopyDirectoryRecursive(trimmedSource, tempPath);

        Trace.WriteLine($"[ALCHITEX] Staged working copy: '{sourcePackPath}' -> '{tempPath}'.");
        return tempPath;
    }

    /// <summary>
    /// Only ever called once a run has fully succeeded. Renames the temp folder to
    /// "&lt;sanitized original name&gt;_RTX", appending "_2", "_3", etc. via
    /// ResolveUniqueDestination if that name is already taken (e.g. re-running Alchitex on
    /// the same source pack a second time).
    /// </summary>
    public static string PromoteToFinalName(string tempPath, string originalFolderName)
    {
        var trimmedTemp = tempPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmedTemp)
            ?? throw new InvalidOperationException($"Couldn't determine the parent directory of '{tempPath}'.");

        var baseName = SanitizeFolderName(originalFolderName) + FinalSuffix;
        var candidate = ResolveUniqueDestination(parent, baseName);

        Directory.Move(trimmedTemp, candidate);
        Trace.WriteLine($"[ALCHITEX] Promoted '{tempPath}' -> '{candidate}'.");
        return candidate;
    }

    /// <summary>
    /// Returns parent/baseName, or parent/baseName_2, _3, etc. - whichever doesn't already
    /// exist on disk.
    /// </summary>
    public static string ResolveUniqueDestination(string parentDir, string baseName)
    {
        var candidate = Path.Combine(parentDir, baseName);
        var n = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(parentDir, $"{baseName}_{n}");
            n++;
        }
        return candidate;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    #endregion

    #region Temp Folder Cleanup

    /// <summary>
    /// Scans every resource-pack root the app knows about for the given edition (regular +
    /// dev, via MinecraftUserDataLocator.GetExistingResourcePackScanPaths) for top-level
    /// folders matching the alchitex_temp_ prefix, and deletes them. Safe to call
    /// unconditionally and often: every one of these folders is, by construction, either
    /// mid-generation or abandoned - nothing depends on one once it exists.
    /// </summary>
    public static int CleanupOrphanedTempFolders(bool isPreview)
    {
        var removed = 0;

        foreach (var scanRoot in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(isPreview))
        {
            IEnumerable<string> orphans;
            try
            {
                orphans = Directory.GetDirectories(scanRoot, TempFolderPrefix + "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Couldn't scan '{scanRoot}' for orphaned temp folders: {ex.Message}");
                continue;
            }

            foreach (var dir in orphans)
            {
                DeleteFolderSafely(dir);
                removed++;
            }
        }

        if (removed > 0)
            Trace.WriteLine($"[ALCHITEX] Cleaned up {removed} orphaned '{TempFolderPrefix}*' folder(s).");

        return removed;
    }

    /// <summary>
    /// A handful of short retries: a file that was just written can occasionally still be
    /// held briefly right after the write completes (AV scan, search indexing), and this
    /// is called right after a pipeline run ends - immediately after its own last write.
    /// </summary>
    private static void DeleteFolderSafely(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                Trace.WriteLine($"[ALCHITEX] Delete attempt {attempt} failed for '{path}': {ex.Message} - retrying...");
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Giving up deleting '{path}' after 3 attempts: {ex.Message}");
            }
        }
    }

    #endregion

    #region Texture Folder Discovery

    /// <summary>
    /// Bedrock resource packs can define per-memory-tier subpacks (a "subpacks/&lt;name&gt;/"
    /// folder next to the root pack content), each with their own textures/blocks that
    /// overrides/extends the root pack's. Which subpack is active is a runtime decision the
    /// game makes based on device memory - Alchitex can't know that ahead of time, so every
    /// textures/blocks folder found anywhere under the pack root is treated as its own
    /// independent scan root and processed the same way.
    /// </summary>
    public static IReadOnlyList<string> DiscoverBlocksFolders(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return Array.Empty<string>();

        var results = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(packRoot, "blocks", SearchOption.AllDirectories))
            {
                var parentName = Path.GetFileName(Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty);
                if (string.Equals(parentName, "textures", StringComparison.OrdinalIgnoreCase))
                    results.Add(dir);
            }
        }
        catch (Exception)
        {
            // Directory.GetDirectories throws on access-denied subfolders - a partial
            // result is still useful, so this intentionally doesn't rethrow.
        }

        return results;
    }

    /// <summary>
    /// Every "textures" folder anywhere under the pack root - the root pack's own plus one
    /// per subpack (see DiscoverBlocksFolders). terrain_texture.json lives directly inside
    /// each of these, as a sibling of "blocks", so post-process steps that need to reach it
    /// per-subpack use this instead of hardcoding the root pack's "textures" path.
    /// </summary>
    public static IReadOnlyList<string> DiscoverTexturesFolders(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return Array.Empty<string>();

        try
        {
            return Directory.GetDirectories(packRoot, "textures", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            // Directory.GetDirectories throws on access-denied subfolders - a partial
            // result is still useful, so this intentionally doesn't rethrow.
            return Array.Empty<string>();
        }
    }

    #endregion

    #region Name Sanitization

    /// <summary>
    /// Strips invalid filename characters, trims whitespace, and caps at 10 characters to
    /// limit path depth impact. ResolveUniqueDestination may append _N, keeping final
    /// names to roughly 13 characters maximum.
    /// </summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "ImportedPack";

        if (sanitized.Length > 10)
            sanitized = sanitized.Substring(0, 10).TrimEnd('_', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "ImportedPk" : sanitized;
    }

    #endregion
}
