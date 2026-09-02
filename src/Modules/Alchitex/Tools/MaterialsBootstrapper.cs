using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Vanilla_RTX_App.Modules; // FastBitmap, TextureSetHelper
using Vanilla_RTX_App.Modules.Alchitex.Core; // MaterialEntry, MerParams, SssParams, HeightmapParams, NormalParams, RecursivePass

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// Debug-only tool: reads every .texture_set.json in an already-fully-PBR'd pack (e.g.
/// the developer's own Vanilla RTX) and reverse-derives a starting materials.json from
/// what's actually baked into each MER/MERS texture, so tuning starts from real numbers
/// instead of a blank sheet.
///
/// Lives under Tools/, not Core/, since it isn't part of the generation pipeline itself -
/// it's a one-off dev utility for bootstrapping materials.json, wired to a debug-only
/// button in the Alchitex window.
///
/// What gets derived per texture, straight from the baked MER/MERS pixels:
///   - metal_min/max, emissive_min/max, roughness_min/max - the observed min/max of the
///     R/G/B channels respectively, across the texture's opaque pixels.
///   - sss_min/max - the observed min/max of the alpha channel, but only if the texture
///     set uses metalness_emissive_roughness_subsurface (MERS). Left at (0, 0) for plain
///     MER sets (Vanilla RTX, being an existing pack, may still have MER-only entries -
///     Alchitex's own generator always produces MERS going forward, but this tool reads
///     whatever's actually there).
///
/// What's deliberately left at MaterialEntry's built-in defaults, because none of it is
/// something you can objectively reverse-engineer from a single baked texture:
///   - every invert_* flag, recursive passes, heightmap.intensity, normal.invert.
/// Every derived entry is a genuine starting point meant to be hand-tuned afterwards, not
/// a finished materials.json.
/// </summary>
public static class MaterialsBootstrapper
{
    public sealed record BootstrapResult(int EntriesWritten, int Skipped, int Failed, string OutputPath);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Reads every texture set under `sourcePackRoot` (root pack + any subpacks - both
    /// TextureSetHelper.ResolveTextureSets and Directory.GetFiles already recurse the
    /// whole pack) and writes a derived materials.json into `destinationDirectory`. If a
    /// materials.json already exists there, it's backed up first (this is a debug tool
    /// meant to be re-run repeatedly while iterating - it should never silently eat a
    /// hand-tuned file).
    /// </summary>
    public static BootstrapResult GenerateFromExistingPack(string sourcePackRoot, string destinationDirectory)
    {
        if (!Directory.Exists(sourcePackRoot))
            throw new DirectoryNotFoundException($"Source pack folder not found: '{sourcePackRoot}'");

        Directory.CreateDirectory(destinationDirectory);
        var outputPath = Path.Combine(destinationDirectory, "materials.json");
        BackUpExistingFileIfPresent(outputPath);

        var entries = new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // A sane, safe neutral fallback for anything a future materials.json doesn't
            // explicitly cover - not derived from the pack, just MaterialEntry's own
            // built-in defaults made explicit so the file is immediately valid on its own.
            ["default"] = new MaterialEntry(),
        };

        var written = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var resolved in TextureSetHelper.ResolveTextureSets(sourcePackRoot))
        {
            TextureSetHelper.LoadedTextureSet? loaded = null;
            try
            {
                loaded = TextureSetHelper.LoadTextureSet(resolved);
                if (loaded?.MerBmp == null)
                {
                    skipped++;
                    continue;
                }

                var name = ResolveTextureName(resolved);
                if (name == null)
                {
                    skipped++;
                    continue;
                }

                var includeSss = resolved.SetNode["metalness_emissive_roughness_subsurface"] != null;
                var entry = DeriveEntry(loaded.ColorBmp, loaded.MerBmp, includeSss);

                if (!entries.TryAdd(name, entry))
                {
                    Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: two texture sets both resolved to the name '{name}' - keeping the first one seen and skipping the duplicate ('{resolved.JsonFilePath}').");
                    skipped++;
                    continue;
                }

                written++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: failed on '{resolved.JsonFilePath}': {ex.Message}");
                failed++;
            }
            finally
            {
                loaded?.ColorBmp?.Dispose();
                loaded?.MerBmp?.Dispose();
                loaded?.NormalBmp?.Dispose();
            }
        }

        var json = JsonSerializer.Serialize(entries, WriteOptions);
        File.WriteAllText(outputPath, json);

        Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: wrote {written} entries ({skipped} skipped, {failed} failed) to '{outputPath}'.");
        return new BootstrapResult(written, skipped, failed, outputPath);
    }

    private static string? ResolveTextureName(TextureSetHelper.ResolvedTextureSet resolved)
    {
        if (resolved.Color.FilePath != null)
            return Path.GetFileNameWithoutExtension(resolved.Color.FilePath);

        var jsonName = Path.GetFileName(resolved.JsonFilePath);
        const string suffix = ".texture_set.json";
        return jsonName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? jsonName[..^suffix.Length]
            : null;
    }

    private static MaterialEntry DeriveEntry(System.Drawing.Bitmap colorBmp, System.Drawing.Bitmap merBmp, bool includeSss)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        int minA = 255, maxA = 0;
        var anyOpaqueSample = false;

        var canFilterByColorAlpha = colorBmp.Width == merBmp.Width && colorBmp.Height == merBmp.Height;

        using (var merFb = new FastBitmap(merBmp, writable: false))
        {
            var colorForFilter = canFilterByColorAlpha ? colorBmp : null;
            FastBitmap? colorFb = null;
            try
            {
                if (colorForFilter != null) colorFb = new FastBitmap(colorForFilter, writable: false);

                for (var y = 0; y < merBmp.Height; y++)
                {
                    for (var x = 0; x < merBmp.Width; x++)
                    {
                        if (colorFb != null && colorFb[x, y].A == 0) continue;

                        var p = merFb[x, y];
                        anyOpaqueSample = true;

                        if (p.R < minR) minR = p.R;
                        if (p.R > maxR) maxR = p.R;
                        if (p.G < minG) minG = p.G;
                        if (p.G > maxG) maxG = p.G;
                        if (p.B < minB) minB = p.B;
                        if (p.B > maxB) maxB = p.B;

                        if (includeSss)
                        {
                            if (p.A < minA) minA = p.A;
                            if (p.A > maxA) maxA = p.A;
                        }
                    }
                }
            }
            finally
            {
                colorFb?.Dispose();
            }
        }

        // Fallback: every pixel got filtered out (fully transparent color texture) -
        // re-scan without the filter rather than emitting a degenerate all-zero entry.
        if (!anyOpaqueSample)
        {
            using var merFb = new FastBitmap(merBmp, writable: false);
            for (var y = 0; y < merBmp.Height; y++)
            {
                for (var x = 0; x < merBmp.Width; x++)
                {
                    var p = merFb[x, y];
                    if (p.R < minR) minR = p.R;
                    if (p.R > maxR) maxR = p.R;
                    if (p.G < minG) minG = p.G;
                    if (p.G > maxG) maxG = p.G;
                    if (p.B < minB) minB = p.B;
                    if (p.B > maxB) maxB = p.B;
                    if (includeSss)
                    {
                        if (p.A < minA) minA = p.A;
                        if (p.A > maxA) maxA = p.A;
                    }
                }
            }
        }

        return new MaterialEntry
        {
            Mer = new MerParams
            {
                MetalMin = minR,
                MetalMax = maxR,
                EmissiveMin = minG,
                EmissiveMax = maxG,
                RoughnessMin = minB,
                RoughnessMax = maxB,
                // invert_* intentionally untouched - stays at MerParams' own defaults.
            },
            Sss = includeSss ? new SssParams { Min = minA, Max = maxA } : new SssParams(),
            Recursive = new List<RecursivePass>(),
            Heightmap = new HeightmapParams(),
            Normal = new NormalParams(),
        };
    }

    private static void BackUpExistingFileIfPresent(string outputPath)
    {
        if (!File.Exists(outputPath)) return;

        var backupPath = $"{outputPath}.bak-{DateTime.Now:yyyyMMdd_HHmmss}";
        try
        {
            File.Copy(outputPath, backupPath, overwrite: false);
            Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: backed up existing materials.json to '{backupPath}' before overwriting.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: couldn't back up existing materials.json ({ex.Message}) - proceeding anyway.");
        }
    }
}
