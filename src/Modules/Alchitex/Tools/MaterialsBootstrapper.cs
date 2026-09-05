using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vanilla_RTX_App.Modules; // FastBitmap, TextureSetHelper
using Vanilla_RTX_App.Modules.Alchitex.Core; // MaterialEntry, MerParams, SssParams, HeightmapParams, NormalParams, RecursivePass

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// Debug-only tool: reads every .texture_set.json in an already-fully-PBR'd pack (e.g.
/// the Cubeir's own Vanilla RTX) and reverse-derives materials.json entries from what's
/// actually baked into each MER/MERS texture, so tuning starts from real numbers instead
/// of a blank sheet.
///
/// Lives under Tools/, not Core/, since it isn't part of the generation pipeline itself -
/// it's a one-off dev utility for bootstrapping materials.json, wired to a debug-only
/// button in the Alchitex window.
///
/// Append-only by design: this is meant to be re-run repeatedly as the artist's ongoing
/// workflow for adding new materials over time, not a one-shot "regenerate everything"
/// tool. If the chosen output file already has an entry for a given texture name, that
/// entry is left completely untouched - only genuinely new names get written, so the
/// artist can always go straight to whatever's new after a run instead of re-diffing the
/// whole file. The output is always written back with "default" first, then every other
/// key alphabetical, so new entries land in a predictable, easy-to-locate place.
///
/// What gets derived per new texture, straight from the baked MER/MERS pixels:
///   - metal_min/max, emissive_min/max, roughness_min/max - the observed min/max of the
///     R/G/B channels respectively, across the texture's opaque pixels.
///   - sss_min/max - the observed min/max of the alpha channel, but only if the texture
///     set uses metalness_emissive_roughness_subsurface (MERS). Left at (0, 0) for plain
///     MER sets (Vanilla RTX, being an existing pack, may still have MER-only entries -
///     Alchitex's own generator always produces MERS going forward, but this tool reads
///     whatever's actually there).
///   - Invisible Emission data (has its own algorithm derived from color texture + MER(s))
///
/// What's deliberately left at MaterialEntry's built-in defaults, because none of it is
/// something you can objectively reverse-engineer from a single baked texture:
///   - every invert_* flag, recursive passes, heightmap.intensity, normal.intensity/invert.
/// Every newly-derived entry is a genuine starting point meant to be hand-tuned afterwards,
/// not a finished materials.json.
/// </summary>
public static class MaterialsBootstrapper
{
    public sealed record BootstrapResult(int EntriesWritten, int Skipped, int Failed, string OutputPath);

    // Only used for JsonNode.ToJsonString, which walks an already-built node tree and so
    // needs no reflection over MaterialEntry. Every call that actually (de)serializes a
    // MaterialEntry goes through AlchitexJsonContext instead - see the comment on that
    // class for why bypassing it silently breaks trimmed Release builds.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Reads every texture set under `sourcePackRoot` (root pack + any subpacks - both
    /// TextureSetHelper.ResolveTextureSets and Directory.GetFiles already recurse the
    /// whole pack) and merges newly-derived entries into `outputPath` (a full materials.json
    /// file path, chosen via a save-file dialog). Existing entries are never touched - see
    /// the class doc comment. The file is backed up first regardless (`.bak-<timestamp>`),
    /// as a safety net even though this is a merge rather than an overwrite.
    /// </summary>
    public static BootstrapResult GenerateFromExistingPack(string sourcePackRoot, string outputPath)
    {
        if (!Directory.Exists(sourcePackRoot))
            throw new DirectoryNotFoundException($"Source pack folder not found: '{sourcePackRoot}'");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var merged = LoadExistingEntries(outputPath);
        BackUpExistingFileIfPresent(outputPath);

        var written = 0;
        var skipped = 0;
        var failed = 0;

        if (!merged.ContainsKey("default"))
        {
            // The fallback for anything materials.json doesn't explicitly cover - not
            // derived from the pack, just MaterialDefaults written out property by
            // property so the file is immediately valid, and readable, on its own.
            //
            // This has to be built explicitly. `new MaterialEntry()` used to carry the
            // defaults in its property initializers, but every property is nullable now
            // (that's what makes per-property fallback possible), so an empty entry
            // serializes to nothing at all and the file would ship a `"default": {}`.
            merged["default"] = BuildDefaultEntry();
            written++;
        }

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

                if (merged.ContainsKey(name))
                {
                    // Already present - either pre-existing in the file, or from an
                    // earlier texture set this same run resolved to the same name.
                    // Append-only: never overwrite an existing entry.
                    skipped++;
                    continue;
                }

                var includeSss = resolved.SetNode["metalness_emissive_roughness_subsurface"] != null;
                merged[name] = DeriveEntry(loaded.ColorBmp, loaded.MerBmp, includeSss);
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

        WriteOrdered(merged, outputPath);

        Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: wrote {written} new entries ({skipped} skipped, {failed} failed) to '{outputPath}'.");
        return new BootstrapResult(written, skipped, failed, outputPath);
    }

    /// <summary>Loads `outputPath`'s existing entries to merge into, if it exists.
    /// A parse failure degrades to "treat as empty" rather than throwing - the backup
    /// taken right after this call still protects whatever was actually on disk.</summary>
    private static Dictionary<string, MaterialEntry> LoadExistingEntries(string outputPath)
    {
        if (!File.Exists(outputPath))
            return new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = File.ReadAllText(outputPath);
            var parsed = JsonSerializer.Deserialize(raw, AlchitexJsonContext.Default.DictionaryStringMaterialEntry);
            return parsed != null
                ? new Dictionary<string, MaterialEntry>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] MaterialsBootstrapper: couldn't parse existing '{outputPath}' ({ex.Message}) - treating as empty for this merge; a backup of the original is taken before writing.");
            return new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Writes the merged entry set with "default" first, then every other key
    /// alphabetical (OrdinalIgnoreCase) - so newly-appended entries land in a predictable,
    /// easy-to-locate place rather than wherever Dictionary enumeration happened to put
    /// them.</summary>
    private static void WriteOrdered(Dictionary<string, MaterialEntry> entries, string outputPath)
    {
        var ordered = new JsonObject();

        if (entries.TryGetValue("default", out var defaultEntry))
            ordered["default"] = JsonSerializer.SerializeToNode(defaultEntry, AlchitexJsonContext.Default.MaterialEntry);

        foreach (var key in entries.Keys
                     .Where(k => !string.Equals(k, "default", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            ordered[key] = JsonSerializer.SerializeToNode(entries[key], AlchitexJsonContext.Default.MaterialEntry);
        }

        File.WriteAllText(outputPath, ordered.ToJsonString(WriteOptions));
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

        // Every property is written explicitly, including the ones this tool can't derive
        // and simply fills with the built-in default. materials.json properties are all
        // optional and fall back per-property (see MaterialsConfig), so a sparse entry
        // would work fine - but the artist's job here is to open a new entry and adjust it,
        // and adding a missing property by hand costs far more than editing one that's
        // already sitting there with a sensible value in it.
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

                // Not derivable from a baked texture - a map that renders inverted in game
                // looks identical to a correct one in the pixels. Defaults, for editing.
                InvertMetal = MaterialDefaults.InvertMetal,
                InvertEmissive = MaterialDefaults.InvertEmissive,
                InvertRoughness = MaterialDefaults.InvertRoughness,
            },
            Sss = new SssParams
            {
                Min = includeSss ? minA : MaterialDefaults.SssMin,
                Max = includeSss ? maxA : MaterialDefaults.SssMax,
                Invert = MaterialDefaults.SssInvert,
            },
            Recursive = new List<RecursivePass>(),
            InvisibleEmission = DeriveInvisibleEmission(colorBmp, merBmp),
            Heightmap = new HeightmapParams
            {
                Intensity = MaterialDefaults.HeightmapIntensity,
                Invert = MaterialDefaults.HeightmapInvert,
            },
            Normal = new NormalParams
            {
                Intensity = MaterialDefaults.NormalIntensity,
                Invert = MaterialDefaults.NormalInvert,
            },
        };
    }

    /// <summary>
    /// Detects and measures the "invisible emission" trick (see InvisibleEmissionParams) in
    /// a pack that already uses it, which is the only way these two numbers can be known -
    /// nothing about a texture says "this hole should glow orange" except an artist having
    /// already decided that once.
    ///
    /// Detection is simply: does any pixel that is fully transparent in the colour texture
    /// carry emissive in the MERS? If a pack didn't use the trick, no such pixel exists and
    /// this returns null so the section is omitted entirely.
    ///
    /// Both values are averaged over the alpha-0 pixels **that actually emit**, not over
    /// every alpha-0 pixel. That's a deliberate departure from the plainest reading of
    /// "average the transparent pixels", and it matters on real textures: transparent
    /// regions are usually part lit hole and part ordinary padding, and padding is normally
    /// flat black. Including it would drag the colour toward black (which emits nothing at
    /// all, since emitted light is albedo x emissive) and halve the strength for no reason.
    /// Generation applies one uniform strength to every alpha-0 pixel, so what's wanted here
    /// is the level the artist chose for the lit part, not that level diluted by how much of
    /// the texture happened to be padding.
    ///
    /// Requires matching dimensions - a MERS at a different resolution than its colour
    /// texture can't be compared pixel for pixel, and guessing a correspondence would be
    /// worse than skipping.
    /// </summary>
    private static InvisibleEmissionParams? DeriveInvisibleEmission(
        System.Drawing.Bitmap colorBmp,
        System.Drawing.Bitmap merBmp)
    {
        if (colorBmp.Width != merBmp.Width || colorBmp.Height != merBmp.Height) return null;

        long sumR = 0, sumG = 0, sumB = 0, sumEmissive = 0, count = 0;

        using (var colorFb = new FastBitmap(colorBmp, writable: false))
        using (var merFb = new FastBitmap(merBmp, writable: false))
        {
            for (var y = 0; y < merBmp.Height; y++)
            {
                for (var x = 0; x < merBmp.Width; x++)
                {
                    var c = colorFb[x, y];
                    if (c.A != 0) continue;

                    // Green is emissive in MER/MERS.
                    var emissive = merFb[x, y].G;
                    if (emissive == 0) continue;

                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    sumEmissive += emissive;
                    count++;
                }
            }
        }

        if (count == 0) return null;

        var strength = (int)Math.Round(sumEmissive / (double)count);
        if (strength <= 0) return null; // rounded away to nothing - not really using it

        return new InvisibleEmissionParams
        {
            Color = new List<int>
            {
                (int)Math.Round(sumR / (double)count),
                (int)Math.Round(sumG / (double)count),
                (int)Math.Round(sumB / (double)count),
            },
            Strength = strength,
        };
    }

    /// <summary>
    /// MaterialDefaults, made explicit as a materials.json entry. These are the same values
    /// the code falls back to when the file is missing or a property is absent - the file's
    /// "default" entry and the built-in constants are meant to agree, and this is what keeps
    /// them agreeing without anyone retyping them.
    /// </summary>
    private static MaterialEntry BuildDefaultEntry() => new()
    {
        Mer = new MerParams
        {
            MetalMin = MaterialDefaults.MetalMin,
            MetalMax = MaterialDefaults.MetalMax,
            EmissiveMin = MaterialDefaults.EmissiveMin,
            EmissiveMax = MaterialDefaults.EmissiveMax,
            RoughnessMin = MaterialDefaults.RoughnessMin,
            RoughnessMax = MaterialDefaults.RoughnessMax,
            InvertMetal = MaterialDefaults.InvertMetal,
            InvertEmissive = MaterialDefaults.InvertEmissive,
            InvertRoughness = MaterialDefaults.InvertRoughness,
        },
        Sss = new SssParams
        {
            Min = MaterialDefaults.SssMin,
            Max = MaterialDefaults.SssMax,
            Invert = MaterialDefaults.SssInvert,
        },
        Recursive = new List<RecursivePass>(),
        Heightmap = new HeightmapParams
        {
            Intensity = MaterialDefaults.HeightmapIntensity,
            Invert = MaterialDefaults.HeightmapInvert,
        },
        Normal = new NormalParams
        {
            Intensity = MaterialDefaults.NormalIntensity,
            Invert = MaterialDefaults.NormalInvert,
        },
    };

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
