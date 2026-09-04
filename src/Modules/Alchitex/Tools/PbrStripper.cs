using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Vanilla_RTX_App.Modules; // TextureSetHelper
using Vanilla_RTX_App.Modules.Alchitex.Core; // TextureSetOrchestratorOptions.CandidateExtensions

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// Strips a pack back to color-only: deletes every MER/MERS/normal/heightmap texture that a
/// .texture_set.json references, then every .texture_set.json itself. Color textures are
/// never touched.
///
/// Lives under Tools/, not Core/, because it isn't part of the generation pipeline - it's a
/// conditional pass the pipeline may or may not run, only when the user explicitly agreed to
/// it for a given pack (see AlchitexWindow's confirmation dialog).
///
/// Why it exists: packs increasingly declare the "pbr" (Vibrant Visuals) or "raytraced"
/// capability while shipping little or no actual PBR content - and those are exactly the
/// packs people want RTX Reactor for. Alchitex skips any texture already claimed by a
/// texture set, so without stripping first it would no-op on them.
///
/// ONLY ever run against the staged temp copy, never the pack the user selected. The caller
/// is AlchitexPipeline, right after AlchitexStaging.CreateTempCopy. That's what makes a
/// destructive pass like this safe to offer at all: the original is never opened for
/// writing, and a failed or aborted run leaves a temp folder to be swept, not a gutted pack.
///
/// How targets are chosen - this part matters, and the obvious shortcut is wrong:
///
///   * What gets deleted is strictly what TextureSetHelper RESOLVED as a set's MER/MERS or
///     normal/heightmap layer. Never a name-pattern sweep. Plenty of legitimate *color*
///     textures end in "_normal" - sandstone_normal, rail_turned_normal - where "normal"
///     means the ordinary variant of a block, not a normal map. Guessing by suffix would
///     delete those outright, in packs that did nothing wrong.
///
///   * On top of each resolved path, the same name under every other supported extension is
///     deleted too. TextureSetHelper hands back only the one file the game would actually
///     load (.tga > .png > .jpg > .jpeg), so a "foo_mer.png" sitting next to the
///     "foo_mer.tga" it resolved would otherwise survive as an orphan and get picked up as
///     a color texture on the regeneration pass. Those extra three paths are pure guesses
///     that usually don't exist, which is exactly why this is safe: the name came from a
///     real resolved PBR layer either way.
///
///   * Texture sets TextureSetHelper couldn't resolve (malformed JSON, MER and MERS both
///     declared, an unresolvable color layer) still get their .json deleted, but their
///     textures are left alone - there's no trustworthy way to know what they pointed at.
///     A pack in that state may keep some orphans; that's the pack's own doing, and it's
///     strictly better than deleting a file on a guess.
///
/// Every layer path resolves inside the texture set's own folder by construction, so there's
/// no way for any of this to reach outside the pack.
///
/// -- SCOPE --------------------------------------------------------------------------
///
/// The defaults are the pack-regeneration behavior described above: the whole tree, every
/// texture set. The two optional parameters narrow that for PbrTestBench, which regenerates
/// individually chosen textures in a folder of somebody's working files and must not touch
/// the ones sitting next to them. They exist so there is exactly one implementation of
/// "which files is it safe to delete here" - an earlier version of the test bench carried
/// its own copy, including its own copy of ExtensionVariants, which is precisely the kind of
/// duplicate that goes stale the first time this rule changes.
/// </summary>
public static class PbrStripper
{
    public readonly record struct Result(int TextureSetsDeleted, int TexturesDeleted, int Failed);

    /// <param name="root">Folder to strip. For the pipeline this is the staged pack copy;
    /// for the test bench it's one folder of the developer's own textures.</param>
    /// <param name="searchOption">Whether to descend into subfolders. The pack path wants
    /// AllDirectories (subpacks have their own texture sets). TopDirectoryOnly exists for
    /// callers that resolved a specific folder and must not reach below it.</param>
    /// <param name="onlyColorNames">When given, only texture sets whose COLOR layer's base
    /// name (no path, no extension) appears here are stripped; everything else in the folder
    /// is left completely alone. Keyed on the color rather than the descriptor's own file
    /// name so a set that claims one of these textures under a different name is still
    /// caught. Null means "every texture set", which is what a pack regeneration wants.</param>
    public static Result Strip(
        string root,
        SearchOption searchOption = SearchOption.AllDirectories,
        IReadOnlySet<string>? onlyColorNames = null)
    {
        if (!Directory.Exists(root)) return new Result(0, 0, 0);

        var failed = 0;
        var fullRoot = Path.GetFullPath(root);

        // ── Pass 1: resolve everything before deleting anything ──────────────
        // The color list has to be complete before the first delete: a pack could name one
        // texture as another set's normal/MER layer, and art wins over cleanup. Note that
        // colors are protected from EVERY resolved set, in scope or not - narrowing the
        // scope must never widen what's considered safe to delete.
        var pbrPaths = new List<string>();
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colorNameByJsonPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ResolveTextureSets always globs recursively; honour searchOption by dropping the
        // sets that live below the root when the caller asked to stay put.
        var resolved = TextureSetHelper.ResolveTextureSets(root)
            .Where(set => searchOption == SearchOption.AllDirectories
                          || string.Equals(Path.GetFullPath(Path.GetDirectoryName(set.JsonFilePath) ?? ""),
                                           fullRoot, StringComparison.OrdinalIgnoreCase));

        foreach (var set in resolved)
        {
            // Inline layers (a hex string or an RGB array) have no file behind them -
            // nothing to delete, and the texture set is going away regardless.
            string? colorName = null;
            if (set.Color is { IsInline: false, FilePath: not null } color)
            {
                colorName = Path.GetFileNameWithoutExtension(color.FilePath);
                colorNameByJsonPath[set.JsonFilePath] = colorName;

                foreach (var variant in ExtensionVariants(color.FilePath))
                    protectedPaths.Add(variant);
            }

            if (onlyColorNames != null && (colorName == null || !onlyColorNames.Contains(colorName)))
                continue;

            if (set.Mer is { IsInline: false, FilePath: not null } mer)
                pbrPaths.Add(mer.FilePath);

            if (set.NormalOrHeight is { IsInline: false, FilePath: not null } normalOrHeight)
                pbrPaths.Add(normalOrHeight.FilePath);
        }

        // ── Pass 2: delete the PBR textures ──────────────────────────────────
        var texturesDeleted = 0;

        foreach (var pbrPath in pbrPaths)
        {
            foreach (var candidate in ExtensionVariants(pbrPath))
            {
                if (protectedPaths.Contains(candidate)) continue;
                if (!File.Exists(candidate)) continue;

                try
                {
                    File.Delete(candidate);
                    texturesDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ALCHITEX] PbrStripper: couldn't delete '{candidate}': {ex.Message}");
                    failed++;
                }
            }
        }

        // ── Pass 3: delete the descriptors ───────────────────────────────────
        // A plain scan rather than the resolved list, so unresolvable sets go too - leaving
        // one behind would make TextureSetOrchestrator skip its color texture ("a
        // .texture_set.json already exists but wasn't resolvable - leaving it alone"), which
        // is the one outcome this whole pass exists to avoid. When scoped, that same
        // reasoning is why an unresolvable descriptor still counts as in scope if its own
        // file name matches one of the named textures - that's the only clue left about what
        // it was for.
        var setsDeleted = 0;

        try
        {
            foreach (var jsonPath in Directory.GetFiles(root, "*.texture_set.json", searchOption))
            {
                if (!IsDescriptorInScope(jsonPath, onlyColorNames, colorNameByJsonPath)) continue;

                try
                {
                    File.Delete(jsonPath);
                    setsDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ALCHITEX] PbrStripper: couldn't delete '{jsonPath}': {ex.Message}");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] PbrStripper: couldn't scan '{root}' for texture sets: {ex.Message}");
            failed++;
        }

        Trace.WriteLine($"[ALCHITEX] PbrStripper: removed {setsDeleted} texture set(s) and {texturesDeleted} PBR texture(s) from '{root}' ({failed} failure(s)).");
        return new Result(setsDeleted, texturesDeleted, failed);
    }

    private static bool IsDescriptorInScope(
        string jsonPath,
        IReadOnlySet<string>? onlyColorNames,
        Dictionary<string, string> colorNameByJsonPath)
    {
        if (onlyColorNames == null) return true;

        if (colorNameByJsonPath.TryGetValue(jsonPath, out var colorName))
            return onlyColorNames.Contains(colorName);

        // Unresolvable (or below the root, when scoped to one folder) - fall back to the
        // descriptor's own name, "planks.texture_set.json" -> "planks".
        var name = Path.GetFileName(jsonPath);
        const string suffix = ".texture_set.json";
        return name.Length > suffix.Length
               && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && onlyColorNames.Contains(name[..^suffix.Length]);
    }

    /// <summary>The given file path under every extension the game (and this app) supports,
    /// the path's own included - see the class comment for why the ones that don't exist are
    /// worth trying anyway.</summary>
    private static IEnumerable<string> ExtensionVariants(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);

        foreach (var ext in TextureSetOrchestratorOptions.CandidateExtensions)
            yield return Path.Combine(folder, nameNoExt + ext);
    }
}
