using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// Single entry point for turning one selected candidate pack into its RTX-enabled
/// version. Call once per pack.
///
/// Alchitex never touches the pack the user selected directly - every run works on a
/// disposable "alchitex_temp_*" copy (AlchitexStaging) that only ever gets promoted to its
/// final "&lt;name&gt;_RTX" name if the whole pipeline succeeds. Anything else - failure,
/// cancellation, the app closing mid-run - just leaves that temp copy behind for
/// AlchitexStaging.CleanupOrphanedTempFolders to sweep up.
///
/// Order:
///   1. Stage a working copy under a temp name next to the source pack.
///   2. Generate texture sets: write missing .texture_set.json descriptors
///      (PbrGeneration.TextureSetOrchestrator), then discover what actually needs
///      generating and generate MERS + normal-or-heightmap pixels for it.
///   3. Water & glass passes (PostProcess).
///   4. Manifest, terrain_texture.json, and icon (PostProcess).
///   5. Promote the temp copy to its final name - only reached on full success.
/// </summary>
public static class AlchitexPipeline
{
    public readonly record struct AlchitexProgress(int Completed, int Total, string StatusText);

    public sealed record AlchitexResult(bool Success, string? OutputPackPath, string? ErrorMessage, string? FinalManifestName = null);

    public static async Task<AlchitexResult> RunAsync(
        string sourcePackPath,
        string packDisplayName,
        AlchitexOptions options,
        string alchitexAssetsPath,
        string appVersion,
        IProgress<AlchitexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? workingPackPath = null;

        try
        {
            progress?.Report(new AlchitexProgress(0, 0, "Staging working copy..."));
            workingPackPath = await Task.Run(() => AlchitexStaging.CreateTempCopy(sourcePackPath), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var materials = MaterialsConfig.Load(Path.Combine(alchitexAssetsPath, "materials.json"));
            var blacklist = PbrBlacklist.Load(Path.Combine(alchitexAssetsPath, "pbr_blacklist.json"));

            // ── Phase 2: texture sets ────────────────────────────────────────
            progress?.Report(new AlchitexProgress(0, 0, "Scanning textures..."));
            TextureSetOrchestrator.GenerateMissingTextureSets(workingPackPath, options, blacklist);

            cancellationToken.ThrowIfCancellationRequested();

            await Task.Run(
                () => GenerateTexturePixels(workingPackPath, materials, options, progress, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // ── Phase 3: water & glass ───────────────────────────────────────
            progress?.Report(new AlchitexProgress(0, 0, "Processing water & glass..."));
            await Task.Run(() => RunWaterGlassPass(workingPackPath, alchitexAssetsPath), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (options.AddFog)
            {
                progress?.Report(new AlchitexProgress(0, 0, "Adding fog..."));
                await Task.Run(() => PostProcess.DeployFog(workingPackPath, alchitexAssetsPath), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ── Phase 4: manifest, terrain data, icon ────────────────────────
            progress?.Report(new AlchitexProgress(0, 0, "Finalizing manifest..."));
            var finalManifestName = PostProcess.UpdateManifest(workingPackPath, appVersion);
            PostProcess.UpdateTerrainTexture(workingPackPath);
            PostProcess.RegeneratePackIcon(workingPackPath, alchitexAssetsPath);
            PostProcess.DeleteStaleBookkeepingFiles(workingPackPath);

            // Last chance to catch a token signaled during that last phase before the
            // folder becomes "real" - cooperative cancellation means it might not have
            // been observed yet.
            cancellationToken.ThrowIfCancellationRequested();

            var finalPath = AlchitexStaging.PromoteToFinalName(workingPackPath, packDisplayName);

            progress?.Report(new AlchitexProgress(1, 1, "Done."));
            return new AlchitexResult(true, finalPath, null, finalManifestName);
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline run for '{sourcePackPath}' was cancelled. Working copy '{workingPackPath}' left in place - a cleanup pass will remove it.");
            return new AlchitexResult(false, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline run for '{sourcePackPath}' failed: {ex}. Working copy '{workingPackPath}' left in place - a cleanup pass will remove it.");
            return new AlchitexResult(false, null, ex.Message);
        }
    }

    // ── Step 2b: pixel generation ────────────────────────────────────────────

    private static void GenerateTexturePixels(
        string packRoot,
        MaterialsConfig materials,
        AlchitexOptions options,
        IProgress<AlchitexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allTargets = TextureSetOrchestrator.DiscoverGenerationTargets(packRoot);
        var toProcess = allTargets
            .Where(t => !File.Exists(t.MersPath) || (t.SecondaryPath != null && !File.Exists(t.SecondaryPath)))
            .ToList();

        var total = toProcess.Count;
        var completed = 0;
        progress?.Report(new AlchitexProgress(0, total, "Generating PBR textures..."));

        if (total == 0) return;

        // Two texture sets can point at the same physical MERS or normal/heightmap file
        // (shared textures between blocks). Whichever gets there first claims and
        // generates it; every other referencer is skipped for that file.
        var claimedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        bool TryClaim(string? path) => path == null || claimedFiles.TryAdd(Path.GetFullPath(path), 0);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.ForEach(toProcess, parallelOptions, target =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (TryClaim(target.MersPath) && TryClaim(target.SecondaryPath))
                {
                    ProcessOneTarget(target, materials, options);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Failed to generate PBR textures for '{target.TextureName}': {ex.Message}");
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new AlchitexProgress(done, total, target.TextureName));
        });
    }

    private static void ProcessOneTarget(GenerationTarget target, MaterialsConfig materials, AlchitexOptions options)
    {
        var material = materials.Resolve(target.TextureName);
        using var colorBitmap = Helpers.ReadImage(target.ColorPath, maxOpacity: false);

        if (!File.Exists(target.MersPath))
        {
            using var mers = MersGenerator.Generate(colorBitmap, material);
            Helpers.WriteImageAsTGA(mers, target.MersPath);
        }

        if (target.SecondaryPath != null && !File.Exists(target.SecondaryPath))
        {
            if (target.IsHeightmap)
            {
                using var heightmap = HeightmapGenerator.Generate(colorBitmap, material.Heightmap);
                Helpers.WriteImageAsTGA(heightmap, target.SecondaryPath);
            }
            else
            {
                using var normal = NormalMapGenerator.Generate(colorBitmap, material.Normal);
                Helpers.WriteImageAsTGA(normal, target.SecondaryPath);
            }
        }
    }

    // ── Step 3: water & glass ────────────────────────────────────────────────

    private static void RunWaterGlassPass(string packRoot, string alchitexAssetsPath)
    {
        // Root-pack blocks folder(s) first: if the zip fallback ends up needed anywhere,
        // it only ever gets deployed once (see the loop below) - a subpack that doesn't
        // define its own water inherits the root pack's at runtime, so it needs to land on
        // root, not on whichever subpack happens to be enumerated first.
        var blocksFolders = AlchitexStaging.DiscoverBlocksFolders(packRoot)
            .OrderBy(f => f.Replace('\\', '/').Contains("/subpacks/", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        var zipFallbackDeployed = false;

        foreach (var blocksFolder in blocksFolders)
        {
            bool hasCompleteGreyWater;
            try
            {
                hasCompleteGreyWater = PostProcess.EnsureGreyWaterTextures(blocksFolder);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Failed processing water textures in '{blocksFolder}': {ex.Message}");
                hasCompleteGreyWater = false;
            }

            if (!hasCompleteGreyWater && !zipFallbackDeployed)
            {
                zipFallbackDeployed = PostProcess.DeployFallbackWaterZip(blocksFolder, alchitexAssetsPath);
            }
        }

        // Glass fixups apply to every resolved color texture in the pack (both
        // pre-existing and freshly generated), since they only ever touch the color
        // layer, never MERS/normal/heightmap. TextureSetHelper already recurses the
        // whole pack root, subpacks included - fine to use here since we only need
        // Color.FilePath, which isn't file-existence-gated the way Mer/NormalOrHeight are.
        foreach (var rs in TextureSetHelper.ResolveTextureSets(packRoot))
        {
            if (rs.Color.FilePath == null) continue;

            try { PostProcess.ProcessColorTextureIfGlassLike(rs.Color.FilePath); }
            catch (Exception ex) { Trace.WriteLine($"[ALCHITEX] Failed glass pass on '{rs.Color.FilePath}': {ex.Message}"); }
        }
    }
}
