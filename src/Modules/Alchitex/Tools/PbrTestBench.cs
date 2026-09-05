using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Vanilla_RTX_App.Modules.Alchitex.Core;

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// DEVELOPER TOOL. Runs loose texture files - or a whole folder of them - through the real
/// PBR generation path, exactly as if they had been discovered inside a resource pack, and
/// writes the results next to the originals.
///
/// Why it exists: testing a PBR change otherwise means assembling a throwaway pack,
/// generating it, looking at the output, deleting the pack, and starting over. That loop is
/// the single biggest cost of iterating on PbrGeneration. This collapses it to "point at a
/// folder of textures, look at what comes out, change something, do it again".
///
/// -- WHAT THIS IS NOT ------------------------------------------------------------------
///
/// It is not a second pipeline. It has no generation logic of its own and never will: every
/// decision - which files count as color textures, which names are junk, blacklist handling,
/// normal-vs-heightmap resolution, and every pixel - comes from Core. This file only moves
/// files around so that Core sees the shape it expects.
///
/// It also runs *only* the texture-set + pixel-generation phases (pipeline steps 2a/2b).
/// No staging, no promotion, no water/glass, no fog, no manifest, no icon. Those operate on
/// a pack, and there is no pack here.
///
/// -- WHY IT STAGES INTO A TEMP FOLDER --------------------------------------------------
///
/// TextureSetOrchestrator finds its work through AlchitexStaging.DiscoverBlocksFolders,
/// which looks for a folder literally named "blocks" whose parent is named "textures". A
/// scratch folder full of PNGs is not that shape. Rather than teach Core about a second
/// discovery mode (or, worse, reimplement the orchestrator's filtering here where it would
/// quietly drift), the selected textures are copied into a throwaway
/// &lt;temp&gt;/textures/blocks/ tree, run through the untouched production path, and the
/// files that generation *added* are copied back out.
///
/// Copying back only what generation added - a before/after diff of the temp tree rather
/// than a list of expected suffixes - is deliberate: if the pipeline ever starts emitting a
/// fourth output, it lands in the results with no change here.
///
/// -- ORDER OF OPERATIONS, AND WHY IT'S THIS ORDER --------------------------------------
///
/// Generation happens first, in temp; the destination folders are stripped and written only
/// once generation has actually succeeded. So a crash, a cancel, or a bad texture leaves the
/// user's folders exactly as they were, rather than stripped bare with nothing to show for
/// it. The strip is what makes re-running idempotent - without it, TextureSetOrchestrator
/// skips every texture that the *previous* run already claimed with a .texture_set.json, and
/// changing the Secondary PBR option would leave last run's orphaned _normal.tga sitting
/// next to this run's _heightmap.tga.
///
/// DESTRUCTIVE, and outside the temp-copy safety net the real pipeline enjoys (§4.2) - by
/// necessity, since the whole point is regenerating in place. The caller must confirm with
/// the user first; Survey() exists to give that dialog real numbers rather than a vague
/// warning.
///
/// -- WHAT GETS REMOVED ------------------------------------------------------------------
///
/// PbrStripper, scoped: this folder only (not below it), and only the texture sets whose
/// *color* layer is one of the textures just regenerated. Everything else beside them is
/// left alone.
///
/// That scoping lives in PbrStripper rather than here on purpose. This file briefly carried
/// its own removal pass - including its own copy of the extension-variants rule - because
/// the stripper only did whole trees, which would wipe a neighbour's PBR when one file is
/// picked. Two implementations of "which files is it safe to delete" is a far worse problem
/// than two optional parameters: the rule is subtle (a set's PBR layers come from
/// TextureSetHelper's resolution, never from name suffixes, because sandstone_normal.png is
/// a *color* texture) and the copy would go stale the first time it changed.
/// </summary>
public static class PbrTestBench
{
    /// <summary>
    /// Whether picking a folder also picks up everything in its subfolders.
    ///
    /// Off by default: the working habit this tool serves is "a folder of textures I'm
    /// looking at right now", and quietly hauling in a whole tree underneath it is both
    /// slower and more destructive than what was asked for. Flip it here if a session
    /// genuinely needs the recursive behavior - it's a development-time choice, not
    /// something worth a UI control.
    ///
    /// Explicitly picked files are unaffected either way: those are always taken exactly as
    /// given, never expanded.
    /// </summary>
    private const bool ScanFoldersRecursively = false;

    /// <summary>What a selection resolves to, for the confirmation dialog. Nothing has been
    /// touched at this point - this is a read-only look at the disk. Folders is derived from
    /// Images (the distinct directories they live in), not from what the user picked, so it
    /// is always exactly the set of folders that will be written to.</summary>
    public sealed record Plan(IReadOnlyList<string> Folders, IReadOnlyList<string> Images)
    {
        public bool IsEmpty => Images.Count == 0;
    }

    public readonly record struct Result(
        int ImagesStaged,
        int TextureSetsCreated,
        int SkippedJunk,
        int OrchestratorFailures,
        int FilesWritten,
        int StaleTextureSetsRemoved,
        int StalePbrTexturesRemoved,
        string? Error)
    {
        public bool Success => Error == null;
    }

    /// <summary>
    /// Resolves a raw selection (files, folders, or a mix - from the picker or from a drop)
    /// into the exact images that will be fed in and the folders that will be written to.
    ///
    /// A picked FILE means that file and nothing else. This is deliberate and was once
    /// wrong: an earlier version resolved a file to its containing folder and then scanned
    /// that, so picking one texture silently pulled in every other texture beside it - and,
    /// worse, put them all in scope for removal. A picked FOLDER means the images in it,
    /// subfolders included only if ScanFoldersRecursively says so.
    ///
    /// The extension list is TextureSetOrchestratorOptions.CandidateExtensions, not a local
    /// copy, so "what counts as a texture" can never disagree with what the pipeline itself
    /// accepts.
    ///
    /// No attempt is made here to exclude names that look like generated PBR output
    /// (_mer/_mers/_normal/_heightmap). That rule lives inside TextureSetOrchestrator and is
    /// private to it; duplicating it here to make a count look tidier is exactly the kind of
    /// second copy that goes stale. Everything is staged, the orchestrator throws out what
    /// it doesn't want, and Result reports what it actually did with them.
    /// </summary>
    public static Plan Survey(IEnumerable<string> selectedPaths)
    {
        var images = new List<string>();
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!IsCandidateExtension(path)) return;

            var full = Path.GetFullPath(path);
            if (seenImages.Add(full)) images.Add(full);
        }

        foreach (var path in selectedPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in EnumerateCandidateImages(path)) Add(file);
                }
                else if (File.Exists(path))
                {
                    Add(path);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't inspect '{path}': {ex.Message}");
            }
        }

        // Derived from the images rather than from the selection, so this is always exactly
        // "where results will be written", whether the user picked folders, files, or both.
        var folders = images
            .Select(i => Path.GetDirectoryName(i)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Plan(folders, images);
    }

    /// <summary>
    /// Generates PBR for everything in <paramref name="plan"/>, then strips and rewrites the
    /// destination folders. See the class comment for why it happens in that order.
    ///
    /// <paramref name="options"/> should come straight from the window's own controls, so
    /// the Secondary PBR dropdown means the same thing here as it does for a real run.
    /// AddFog / StripExistingPbr are ignored - neither has anything to act on without a pack.
    /// </summary>
    public static Result Run(
        Plan plan,
        AlchitexOptions options,
        IProgress<AlchitexPipeline.AlchitexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.IsEmpty)
            return new Result(0, 0, 0, 0, 0, 0, 0, "No supported image files were found in the selection.");

        var benchRoot = Path.Combine(
            Path.GetTempPath(),
            "alchitex_bench_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            progress?.Report(new AlchitexPipeline.AlchitexProgress(0, 0, "Staging textures...", AlchitexPhase.Staging));

            // stagedDirectory -> the real folder its results belong back in. Directory-level
            // because everything generation produces lands beside the color texture it came
            // from, so a directory mapping is all the copy-back ever needs.
            var destinationOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var blocksRoot = Path.Combine(benchRoot, "textures", "blocks");
            var staged = 0;

            // One staging directory per source directory. Two folders that both contain a
            // "stone.png" therefore can't collide, and no relative-path bookkeeping is
            // needed. Subfolders under blocks/ are ordinary in real packs, so this changes
            // nothing about how discovery behaves.
            var sourceGroups = plan.Images
                .GroupBy(i => Path.GetDirectoryName(i)!, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < sourceGroups.Count; i++)
            {
                var group = sourceGroups[i];
                var stagedDir = Path.Combine(blocksRoot, "d" + i);
                Directory.CreateDirectory(stagedDir);
                destinationOf[Path.GetFullPath(stagedDir)] = group.Key;

                foreach (var image in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(image, Path.Combine(stagedDir, Path.GetFileName(image)), overwrite: true);
                    staged++;
                }
            }

            if (staged == 0)
                return new Result(0, 0, 0, 0, 0, 0, 0, "Nothing could be staged from the selection.");

            var before = SnapshotFiles(benchRoot);

            cancellationToken.ThrowIfCancellationRequested();

            // ── The real pipeline, phases 2a and 2b, unmodified ──────────────
            var materials = MaterialsConfig.Load(AssetUpdater.Resolve(AssetUpdater.MaterialsJson));
            var blacklist = PbrBlacklist.Load(AssetUpdater.Resolve(AssetUpdater.PbrBlacklistJson));

            progress?.Report(new AlchitexPipeline.AlchitexProgress(0, 0, "Scanning textures...", AlchitexPhase.ScanningTextures));
            var orchestrated = TextureSetOrchestrator.GenerateMissingTextureSets(benchRoot, options, blacklist);

            cancellationToken.ThrowIfCancellationRequested();

            // ── Force regeneration ───────────────────────────────────────────
            // The one place the bench deliberately differs from a pack run, and it has to be
            // here rather than left to the destination strip at the end.
            //
            // Selection is by extension, so a folder that has been run before hands us the
            // last run's _mers.tga / _normal.tga alongside the color textures. Those get
            // staged too (harmlessly - the orchestrator's own junk filter refuses to treat
            // them as color textures). But GenerateTexturePixels skips any target whose
            // output file already exists, which for a real pack correctly means "don't redo
            // finished work", and here would mean "silently keep the previous run's result"
            // - so re-running after changing an option produced a fresh .texture_set.json
            // pointing at PBR files that were then stripped from the destination and never
            // rewritten. Deleting the outputs inside the staged copy first is what makes a
            // re-run actually regenerate.
            //
            // The paths come from the production discovery pass, so this can only ever
            // target files a texture set genuinely claims as a PBR layer - never a color
            // texture that happens to be named like one (sandstone_normal.png).
            foreach (var target in TextureSetOrchestrator.DiscoverGenerationTargets(benchRoot))
            {
                ClearStagedOutput(target.MersPath, before);
                ClearStagedOutput(target.SecondaryPath, before);
            }

            AlchitexPipeline.GenerateTexturePixels(benchRoot, materials, options, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // ── Everything generation added, whatever it turned out to be ────
            var produced = SnapshotFiles(benchRoot).Where(f => !before.Contains(f)).ToList();

            // ── Only now is anything of the user's touched ───────────────────
            progress?.Report(new AlchitexPipeline.AlchitexProgress(0, 0, "Removing previous PBR...", AlchitexPhase.StrippingPbr));

            var setsRemoved = 0;
            var texturesRemoved = 0;
            foreach (var group in sourceGroups)
            {
                // The production stripper, scoped: this folder only, and only the texture
                // sets whose color layer is one of the textures we just regenerated. Its
                // defaults are still the pack behaviour, so the real pipeline is unaffected.
                var names = new HashSet<string>(
                    group.Select(image => Path.GetFileNameWithoutExtension(image)),
                    StringComparer.OrdinalIgnoreCase);

                var removed = PbrStripper.Strip(group.Key, SearchOption.TopDirectoryOnly, names);
                setsRemoved += removed.TextureSetsDeleted;
                texturesRemoved += removed.TexturesDeleted;
            }

            progress?.Report(new AlchitexPipeline.AlchitexProgress(0, produced.Count, "Writing results...", AlchitexPhase.Finalizing));

            var written = 0;
            foreach (var file in produced)
            {
                var stagedDir = Path.GetFullPath(Path.GetDirectoryName(file)!);
                if (!destinationOf.TryGetValue(stagedDir, out var destinationDir))
                {
                    // Generation only ever writes beside a color texture, so a produced file
                    // in an unmapped directory means an assumption above has stopped holding.
                    Trace.WriteLine($"[ALCHITEX] PbrTestBench: no destination mapped for '{file}' - not copying it back.");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(destinationDir);
                    File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
                    written++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't write '{Path.GetFileName(file)}' to '{destinationDir}': {ex.Message}");
                }
            }

            Trace.WriteLine($"[ALCHITEX] PbrTestBench: staged {staged}, created {orchestrated.Created} texture set(s), wrote {written} file(s) to {plan.Folders.Count} folder(s).");

            return new Result(
                ImagesStaged: staged,
                TextureSetsCreated: orchestrated.Created,
                SkippedJunk: orchestrated.SkippedJunk,
                OrchestratorFailures: orchestrated.Failed,
                FilesWritten: written,
                StaleTextureSetsRemoved: setsRemoved,
                StalePbrTexturesRemoved: texturesRemoved,
                Error: null);
        }
        catch (OperationCanceledException)
        {
            // Cancellation can only land before the strip/copy-back block finishes its first
            // write, so the user's folders are either untouched or fully written.
            return new Result(0, 0, 0, 0, 0, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] PbrTestBench failed: {ex}");
            return new Result(0, 0, 0, 0, 0, 0, 0, ex.Message);
        }
        finally
        {
            TryDeleteDirectory(benchRoot);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateCandidateImages(string root)
    {
        var scope = ScanFoldersRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var extension in TextureSetOrchestratorOptions.CandidateExtensions)
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(root, "*" + extension, scope);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't scan '{root}' for '{extension}': {ex.Message}");
                continue;
            }

            // GetFiles("*.tga") also matches things like "foo.tga.bak" on Windows' 8.3
            // shortname semantics, so the extension is verified rather than trusted.
            foreach (var match in matches)
                if (IsCandidateExtension(match))
                    yield return match;
        }
    }

    private static bool IsCandidateExtension(string path)
        => TextureSetOrchestratorOptions.CandidateExtensions
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> SnapshotFiles(string root)
    {
        try
        {
            return new HashSet<string>(
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't snapshot '{root}': {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Removes a stale PBR output from the staged copy so generation rewrites it, and drops
    /// it from the "what was here before" set at the same time - otherwise the regenerated
    /// file would still look pre-existing to the diff and never get copied back out.
    /// </summary>
    private static void ClearStagedOutput(string? path, HashSet<string> before)
    {
        if (string.IsNullOrEmpty(path)) return;

        var full = Path.GetFullPath(path);
        before.Remove(full);

        try
        {
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex)
        {
            // Left in place, generation will skip it, and the diff simply won't see it -
            // the run reports fewer files written rather than producing a broken folder.
            Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't clear staged '{full}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // Under the OS temp folder, so a leftover is the OS's problem rather than ours.
            Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't remove its temp folder '{path}': {ex.Message}");
        }
    }
}
