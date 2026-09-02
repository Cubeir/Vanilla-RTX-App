using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

#region Texture Set Orchestration (Phase 2a - write .texture_set.json, then discover what needs generating)

public sealed class TextureSetOrchestratorOptions
{
    public static readonly string[] CandidateExtensions = { ".tga", ".png", ".jpg", ".jpeg" };
}

/// <summary>
/// One texture set that needs (or might need) generated PBR files, resolved directly from
/// its .texture_set.json's own text content rather than through TextureSetHelper.
/// ResolveTextureSets. That distinction matters: TextureSetHelper's Mer/NormalOrHeight
/// resolution is file-existence-gated (it was built for Tuner, which only ever touches
/// texture sets whose files already fully exist) - it returns null for a layer whose
/// referenced file doesn't exist on disk *yet*, which is the normal state of every
/// texture set the moment TextureSetOrchestrator just wrote it. Relying on that here
/// silently produced zero generation targets for every fresh texture set. This type and
/// DiscoverGenerationTargets below read the JSON directly and never gate on file
/// existence for anything except the color texture, which must already be real.
/// </summary>
public sealed record GenerationTarget(
    string ColorPath,
    string MersPath,
    string? SecondaryPath,
    bool IsHeightmap,
    string TextureName);

/// <summary>
/// Phase 2a of the Alchitex pipeline. Walks every textures/blocks folder in the pack
/// (root + subpacks - see AlchitexStaging.DiscoverBlocksFolders), figures out which color
/// textures are NOT already claimed by an existing .texture_set.json (via
/// TextureSetHelper - safe to use for this exclusion check specifically, since a null
/// Mer/NormalOrHeight here only means "don't count this as already covered", never a
/// false exclusion), and writes a fresh .texture_set.json for each remaining one.
///
/// Every texture set Alchitex writes always uses metalness_emissive_roughness_subsurface -
/// never plain metalness_emissive_roughness - and always gets real SSS data, see
/// MersGenerator below - except for textures matching pbr_blacklist.json (Configuration.
/// PbrBlacklist), which still get a texture set, just a color-only one with no PBR keys.
///
/// Running this twice on an already-processed pack is a no-op for every texture that
/// already got a texture set on the first run - which is exactly the "safe to re-run
/// Alchitex on a pack" behavior we want.
/// </summary>
public static class TextureSetOrchestrator
{
    public readonly record struct Result(int Created, int SkippedAlreadyCovered, int SkippedJunk, int Failed);

    public static Result GenerateMissingTextureSets(
        string packRoot,
        AlchitexOptions options,
        PbrBlacklist blacklist)
    {
        var created = 0;
        var skippedJunk = 0;
        var failed = 0;

        var blocksFolders = AlchitexStaging.DiscoverBlocksFolders(packRoot);
        if (blocksFolders.Count == 0)
        {
            Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: no textures/blocks folder found anywhere under '{packRoot}'.");
            return new Result(0, 0, 0, 0);
        }

        var candidates = new List<string>();
        foreach (var blocksFolder in blocksFolders)
        {
            foreach (var ext in TextureSetOrchestratorOptions.CandidateExtensions)
            {
                candidates.AddRange(Directory.GetFiles(blocksFolder, "*" + ext, SearchOption.AllDirectories));
            }
        }

        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: no candidate textures found under '{packRoot}'.");
            return new Result(0, 0, 0, 0);
        }

        // Exclude anything already claimed by an existing texture set.
        var alreadyCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolved in TextureSetHelper.ResolveTextureSets(packRoot))
        {
            if (resolved.Color.FilePath != null) alreadyCovered.Add(resolved.Color.FilePath);
            if (resolved.Mer?.FilePath != null) alreadyCovered.Add(resolved.Mer.FilePath);
            if (resolved.NormalOrHeight?.FilePath != null) alreadyCovered.Add(resolved.NormalOrHeight.FilePath);
        }

        static bool LooksLikeGeneratedOrJunk(string nameLowerNoExt)
        {
            if (nameLowerNoExt.EndsWith("_mer") || nameLowerNoExt.EndsWith("_mers")) return true;
            if (nameLowerNoExt.EndsWith("_normal") || nameLowerNoExt.EndsWith("_heightmap")) return true;
            if (nameLowerNoExt.Contains("bubble") || nameLowerNoExt.Contains("_placeholder")) return true;
            // Colored/inventory water icons - consumed as source material by the
            // water-fallback pass (PostProcess.EnsureGreyWaterTextures), never need a
            // texture set of their own. The grey in-world variants (water_still_grey/
            // water_flow_grey) DO get one now - see the PBR blacklist below.
            if (nameLowerNoExt == "water_flow" || nameLowerNoExt == "water_still") return true;
            return false;
        }

        var colorTextures = candidates
            .Where(path => !alreadyCovered.Contains(path))
            .Where(path => !LooksLikeGeneratedOrJunk(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skippedCovered = candidates.Count - colorTextures.Count;

        foreach (var colorPath in colorTextures)
        {
            try
            {
                var directory = Path.GetDirectoryName(colorPath)!;
                var nameNoExt = Path.GetFileNameWithoutExtension(colorPath);
                var jsonPath = Path.Combine(directory, nameNoExt + ".texture_set.json");

                if (File.Exists(jsonPath))
                {
                    // Exists but TextureSetHelper couldn't resolve it (malformed, or
                    // references a color layer under a different name) - don't clobber a
                    // hand-authored/broken file blindly.
                    Trace.WriteLine($"[ALCHITEX] Skipping '{colorPath}': a .texture_set.json already exists at '{jsonPath}' but wasn't resolvable - leaving it alone.");
                    skippedJunk++;
                    continue;
                }

                var set = new JsonObject { ["color"] = nameNoExt };

                if (blacklist.IsBlacklisted(nameNoExt))
                {
                    // pbr_blacklist.json match - color-only texture set, no PBR keys at all.
                    Trace.WriteLine($"[ALCHITEX] '{nameNoExt}' matches pbr_blacklist.json - writing a color-only texture set, no PBR.");
                }
                else
                {
                    set["metalness_emissive_roughness_subsurface"] = nameNoExt + "_mers";

                    var secondaryMode = ResolveSecondaryMode(options.SecondaryPbr, colorPath);
                    switch (secondaryMode)
                    {
                        case SecondaryPbrMode.Normal:
                            set["normal"] = nameNoExt + "_normal";
                            break;
                        case SecondaryPbrMode.Heightmap:
                            set["heightmap"] = nameNoExt + "_heightmap";
                            break;
                        case SecondaryPbrMode.None:
                        case SecondaryPbrMode.Auto: // Auto never reaches here unresolved - see ResolveSecondaryMode
                            break;
                    }
                }

                var root = new JsonObject
                {
                    ["format_version"] = "1.21.30",
                    ["minecraft:texture_set"] = set,
                };

                File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                created++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Failed to create texture set for '{colorPath}': {ex.Message}");
                failed++;
            }
        }

        Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: created {created}, already-covered {skippedCovered}, skipped-junk {skippedJunk}, failed {failed}.");
        return new Result(created, skippedCovered, skippedJunk, failed);
    }

    /// <summary>
    /// Resolves Auto mode per-texture by probing just the image header (MagickImageInfo -
    /// no full decode) for width, per the agreed rule: width &lt;= 32 -> heightmap,
    /// otherwise -> normal map. Also overrides an *explicit* Heightmap request above
    /// AlchitexOptions.ExplicitHeightmapMaxWidth - the game manifests a heightmap texture
    /// set in-game via its own internal Sobel-derived bump effect (unrelated to our own
    /// generated normal maps), which thins out and stops reading as height at higher
    /// resolutions, so a normal map is generated instead.
    /// </summary>
    private static SecondaryPbrMode ResolveSecondaryMode(SecondaryPbrMode requested, string colorPath)
    {
        if (requested == SecondaryPbrMode.None || requested == SecondaryPbrMode.Normal)
            return requested;

        int width;
        try
        {
            width = (int)new MagickImageInfo(colorPath).Width;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't read dimensions for '{colorPath}' ({ex.Message}), defaulting to normal map.");
            return SecondaryPbrMode.Normal;
        }

        if (requested == SecondaryPbrMode.Auto)
        {
            return width <= AlchitexOptions.AutoModeHeightmapMaxWidth
                ? SecondaryPbrMode.Heightmap
                : SecondaryPbrMode.Normal;
        }

        // requested == Heightmap (explicit).
        if (width > AlchitexOptions.ExplicitHeightmapMaxWidth)
        {
            Trace.WriteLine($"[ALCHITEX] '{colorPath}' is {width}px wide - too large for a heightmap to render correctly in Minecraft RTX (>{AlchitexOptions.ExplicitHeightmapMaxWidth}px). Generating a normal map instead of the explicitly-requested heightmap.");
            return SecondaryPbrMode.Normal;
        }

        return SecondaryPbrMode.Heightmap;
    }

    /// <summary>
    /// Phase 2b's starting point. Scans every .texture_set.json under the pack directly
    /// (System.Text.Json, not TextureSetHelper) and resolves each one's *intended*
    /// MERS/normal/heightmap file paths regardless of whether those files exist yet -
    /// that's the whole point of this method existing separately from TextureSetHelper's
    /// resolution. PBR outputs are always .tga (the app's standing convention), so target
    /// paths are built directly rather than probed by extension.
    /// </summary>
    public static IReadOnlyList<GenerationTarget> DiscoverGenerationTargets(string packRoot)
    {
        var targets = new List<GenerationTarget>();

        foreach (var jsonPath in Directory.GetFiles(packRoot, "*.texture_set.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(jsonPath);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var set = JsonNode.Parse(text)?.AsObject()?["minecraft:texture_set"]?.AsObject();
                if (set == null) continue;

                var colorName = (string?)set["color"];
                if (string.IsNullOrEmpty(colorName)) continue;

                var folder = Path.GetDirectoryName(jsonPath)!;
                var colorPath = TextureSetHelper.FindTextureFile(folder, colorName);
                if (colorPath == null) continue; // color texture itself missing - nothing to do

                var mersName = (string?)set["metalness_emissive_roughness_subsurface"];
                if (string.IsNullOrEmpty(mersName))
                {
                    // Not one of ours (or hand-authored as plain MER) - Alchitex only
                    // ever generates MERS, so a set without that key is out of scope.
                    continue;
                }

                var mersPath = Path.Combine(folder, mersName + ".tga");

                string? secondaryPath = null;
                var isHeightmap = false;

                var normalName = (string?)set["normal"];
                var heightmapName = (string?)set["heightmap"];

                if (!string.IsNullOrEmpty(normalName))
                {
                    secondaryPath = Path.Combine(folder, normalName + ".tga");
                }
                else if (!string.IsNullOrEmpty(heightmapName))
                {
                    secondaryPath = Path.Combine(folder, heightmapName + ".tga");
                    isHeightmap = true;
                }

                targets.Add(new GenerationTarget(colorPath, mersPath, secondaryPath, isHeightmap, Path.GetFileNameWithoutExtension(colorPath)));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Couldn't parse '{jsonPath}' while discovering generation targets: {ex.Message}");
            }
        }

        return targets;
    }
}

#endregion

#region Shared Colour Analysis

/// <summary>
/// The single place the "what counts as real colour data" rule lives, and the
/// contrast-maximized greyscale every generator derives from it. MERS, heightmap and
/// normal generation all reduce a colour texture to the same flat-average greyscale and
/// all need the same answer to the same question - which pixels are allowed a *vote* in
/// what that texture's value range actually is - so it lives here rather than being
/// re-derived (and drifting) in three places.
/// </summary>
public static class ColorField
{
    /// <summary>
    /// A pixel counts toward a texture's value domain if it's not fully transparent
    /// (opacity &gt;= 1), or - for the fully-transparent case - if its underlying colour
    /// isn't one of the two conventional "background padding" fills (pure black or pure
    /// white at alpha 0). Texture authors routinely leave real colour data under a
    /// collapsed alpha channel too (e.g. grass_side's dirt portion, transparent so the
    /// game skips tinting it, but still real colour) - that data should still count.
    ///
    /// Excluded pixels are only denied a vote in the domain; they still get a real output
    /// value from whatever stretch that domain feeds, clamped to the nearest extreme.
    /// </summary>
    public static bool IsRealColorData(Color c)
    {
        if (c.A >= 1) return true;

        var isPureBlack = c.R == 0 && c.G == 0 && c.B == 0;
        var isPureWhite = c.R == 255 && c.G == 255 && c.B == 255;
        return !(isPureBlack || isPureWhite);
    }

    /// <summary>
    /// Flat-average greyscale ((R+G+B)/3, deliberately not luminosity-weighted - the same
    /// "value" convention across every generator) for every pixel, plus the min/max of
    /// that greyscale taken over real-colour pixels only. Callers that stretch into their
    /// own arbitrary target ranges (MersGenerator) want this raw form; callers that just
    /// want the texture normalized to full range want BuildContrastMaximized below.
    /// </summary>
    public static (int[,] Grey, int Min, int Max) ComputeGreyField(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var grey = new int[w, h];

        int min = 255, max = 0;
        var sawRealPixel = false;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;

                    if (!ColorField.IsRealColorData(c)) continue;

                    sawRealPixel = true;
                    if (g < min) min = g;
                    if (g > max) max = g;
                }
            }
        }

        // Nothing real anywhere (a fully blank/padding-only texture) - fall back to the
        // full range so the stretch degenerates to identity instead of dividing by zero.
        if (!sawRealPixel) { min = 0; max = 255; }

        return (grey, min, max);
    }

    /// <summary>
    /// The contrast-maximized greyscale: flat-average grey stretched so the darkest real
    /// pixel lands at 0.0 and the brightest at 1.0, everything between interpolated.
    /// Pixels outside the real-colour domain still get a value, clamped to whichever end
    /// they fall past.
    ///
    /// Note this is a *full* min-to-max stretch, distinct from the ceiling-maximize used
    /// for POM data (ApplyPomBlueChannel), which deliberately only scales up so the
    /// brightest pixel hits the top while leaving the dark end where it sits.
    /// </summary>
    public static float[,] BuildContrastMaximized(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var (grey, min, max) = ComputeGreyField(colorBitmap);

        var result = new float[w, h];
        var range = max - min;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var t = range > 0 ? (grey[x, y] - min) / (double)range : 0.5;
                result[x, y] = (float)Math.Clamp(t, 0.0, 1.0);
            }
        }

        return result;
    }
}

#endregion

#region MERS Generation

/// <summary>
/// Generates a MERS (metalness/emissive/roughness/subsurface) bitmap from a color texture
/// and a resolved MaterialEntry. Alchitex only ever produces MERS, never plain MER - see
/// TextureSetOrchestrator above - and always writes each block's own materials.json SSS
/// range; there's no run-time toggle to suppress it (same reasoning as POM: a shader
/// choosing not to read the data is downstream of us, not something to gate here).
///
/// Base algorithm:
///   1. Greyscale the color texture with a flat channel average (NOT luminosity - a
///      literal (R+G+B)/3 per pixel). This single "value" shape is the common source for
///      all three MER channels.
///   2. Stretch that grey value's own min/max across the image into each output channel's
///      configured target range independently (metal, emissive, roughness), each with its
///      own invert flag.
///   3. Layer on any recursive/advanced passes.
///   4. Compute the color texture's real luminosity (unaltered - standard perceptual
///      weighting, not the flat average from step 1), stretch it into the effective SSS
///      min/max range, and write that as the output alpha.
///
/// The min/max *domain* used for every stretch above (steps 2 and 4, and the recursive
/// pass's own sub-stretch) only ever considers "real" pixels - see ColorField.IsRealColorData -
/// so background junk (typically a fully-black or fully-white fill left under a fully
/// collapsed alpha channel, the common way texture pack authors pad cutout regions) can't
/// artificially widen the domain and flatten the contrast of the actual visible content.
/// Excluded pixels still get a real output value (via the same Stretch call, using the
/// domain computed without them - Stretch already clamps out-of-range input to the
/// nearest extreme), they just don't get a *vote* in what that domain is.
/// </summary>
public static class MersGenerator
{
    // TODO(tuning): how strongly a fully-white pixel contributes to a recursive pass's
    // dominance mask - white can't locally "dominate" any single channel the way a
    // saturated color can, so it's capped well below full (255/3) rather than 0 or 255.
    // Kept from the legacy heuristic this was ported from.
    private const int WhitePixelMaskOpacity = 85;

    public static Bitmap Generate(Bitmap colorBitmap, MaterialEntry material)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var grey = new int[w, h];
        var luminosity = new int[w, h];

        int greyMin = 255, greyMax = 0;
        int lumMin = 255, lumMax = 0;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];

                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;

                    var l = (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                    luminosity[x, y] = l;

                    if (!ColorField.IsRealColorData(c)) continue;

                    if (g < greyMin) greyMin = g;
                    if (g > greyMax) greyMax = g;
                    if (l < lumMin) lumMin = l;
                    if (l > lumMax) lumMax = l;
                }
            }
        }

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var g = grey[x, y];
                    var r = Stretch(g, greyMin, greyMax, material.Mer.MetalMin, material.Mer.MetalMax, material.Mer.InvertMetal);
                    var gr = Stretch(g, greyMin, greyMax, material.Mer.EmissiveMin, material.Mer.EmissiveMax, material.Mer.InvertEmissive);
                    var b = Stretch(g, greyMin, greyMax, material.Mer.RoughnessMin, material.Mer.RoughnessMax, material.Mer.InvertRoughness);
                    var alpha = Stretch(luminosity[x, y], lumMin, lumMax, material.Sss.Min, material.Sss.Max, invert: false);

                    outFb[x, y] = Color.FromArgb(alpha, r, gr, b);
                }
            }

            foreach (var pass in material.Recursive)
            {
                ApplyRecursivePass(colorBitmap, grey, greyMin, greyMax, luminosity, lumMin, lumMax, outFb, pass);
            }
        }

        return output;
    }

    private static void ApplyRecursivePass(
        Bitmap colorBitmap,
        int[,] grey, int greyMin, int greyMax,
        int[,] luminosity, int lumMin, int lumMax,
        FastBitmap outFb,
        RecursivePass pass)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var mask = new int[w, h];

        int subGreyMin = 255, subGreyMax = 0;
        var anyMasked = false;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    var opacity = ComputeDominanceOpacity(c, pass.Channel);
                    mask[x, y] = opacity;
                    if (opacity <= 0) continue;

                    anyMasked = true;
                    if (!ColorField.IsRealColorData(c)) continue;

                    var g = grey[x, y];
                    if (g < subGreyMin) subGreyMin = g;
                    if (g > subGreyMax) subGreyMax = g;
                }
            }
        }

        if (!anyMasked) return;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var weight = mask[x, y];
                if (weight <= 0) continue;

                var t = weight / 255.0;
                var g = grey[x, y];

                var passR = Stretch(g, subGreyMin, subGreyMax, pass.Mer.MetalMin, pass.Mer.MetalMax, pass.Mer.InvertMetal);
                var passG = Stretch(g, subGreyMin, subGreyMax, pass.Mer.EmissiveMin, pass.Mer.EmissiveMax, pass.Mer.InvertEmissive);
                var passB = Stretch(g, subGreyMin, subGreyMax, pass.Mer.RoughnessMin, pass.Mer.RoughnessMax, pass.Mer.InvertRoughness);

                var baseColor = outFb[x, y];
                var newR = Lerp(baseColor.R, passR, t);
                var newG = Lerp(baseColor.G, passG, t);
                var newB = Lerp(baseColor.B, passB, t);

                var newAlpha = baseColor.A;
                if (pass.Sss != null)
                {
                    var passAlpha = Stretch(luminosity[x, y], lumMin, lumMax, pass.Sss.Min, pass.Sss.Max, invert: false);
                    newAlpha = Lerp(baseColor.A, passAlpha, t);
                }

                outFb[x, y] = Color.FromArgb(newAlpha, newR, newG, newB);
            }
        }
    }

    /// <summary>
    /// Legacy AdjustColorChannels's advanced-gen "is this channel locally dominant" test,
    /// kept as-is - it's a solid heuristic. Returns 0-255 opacity, 0 meaning "this pixel
    /// isn't part of the mask at all".
    /// </summary>
    private static int ComputeDominanceOpacity(Color c, string channel)
    {
        int target, secondHighest, thirdValue;
        bool accepted;

        switch (channel.ToUpperInvariant())
        {
            case "G":
                target = c.G;
                secondHighest = Math.Max(c.R, c.B);
                thirdValue = Math.Min(c.R, c.B);
                accepted = (c.G > c.R && c.G > c.B)
                           || (c.R == 255 && c.G == 255 && c.B == 255)
                           || (c.G == c.R && c.G > c.B)
                           || (c.G == c.B && c.G > c.R);
                break;
            case "B":
                target = c.B;
                secondHighest = Math.Max(c.R, c.G);
                thirdValue = Math.Min(c.R, c.G);
                accepted = (c.B > c.R && c.B > c.G)
                           || (c.R == 255 && c.G == 255 && c.B == 255)
                           || (c.B == c.R && c.B > c.G)
                           || (c.B == c.G && c.B > c.R);
                break;
            default: // "R"
                target = c.R;
                secondHighest = Math.Max(c.G, c.B);
                thirdValue = Math.Min(c.G, c.B);
                accepted = (c.R > c.G && c.R > c.B)
                           || (c.R == 255 && c.G == 255 && c.B == 255)
                           || (c.R == c.G && c.R > c.B)
                           || (c.R == c.B && c.R > c.G);
                break;
        }

        if (!accepted) return 0;

        if (c.R == 255 && c.G == 255 && c.B == 255) return WhitePixelMaskOpacity;
        if (target == secondHighest) return (target - thirdValue) / 2;
        return target - secondHighest;
    }

    private static byte Lerp(byte a, int b, double t)
        => (byte)Math.Clamp((int)Math.Round(a * (1.0 - t) + b * t), 0, 255);

    /// <summary>
    /// Stretches `value` from the [oldMin, oldMax] range found in the source data into
    /// [targetMin, targetMax], optionally reversed.
    /// </summary>
    private static byte Stretch(int value, int oldMin, int oldMax, int targetMin, int targetMax, bool invert)
    {
        double t = oldMax > oldMin ? (value - oldMin) / (double)(oldMax - oldMin) : 0.5;
        t = Math.Clamp(t, 0.0, 1.0);
        if (invert) t = 1.0 - t;

        var result = targetMin + t * (targetMax - targetMin);
        return (byte)Math.Clamp((int)Math.Round(result), 0, 255);
    }
}

#endregion

#region Normal Map Generation

/// <summary>
/// Generates a normal map from a color texture, built on top of
/// HeightmapGenerator.ComputeClusteredHeights rather than raw color brightness - the
/// mean-shift clustered heightmap is the shared height-field basis for both texture
/// types now:
///   1. The clustered heightmap (raw, untouched) is blended with a ceiling-maximized flat
///      greyscale of the color texture, weighted by HeightmapBlendRatio (default 75%
///      clustered / 25% color), giving a clean height field that still carries some of
///      the original texture's own shading. The ceiling-maximize here only ever applies
///      to that color-texture greyscale, never to the clustered heightmap itself - a
///      *second*, independent ceiling-maximize of the clustered heightmap happens later,
///      purely for the POM blue channel (step 5) - the two are unrelated uses of the same
///      source array, not the same operation.
///   2. Gradients come from a Sobel operator sampled with wraparound indexing, so a pixel
///      on any edge sees its real neighbor from the opposite edge and the result tiles
///      seamlessly - which is what Bedrock's random rotation of isometric block textures
///      needs, since any two edges can end up meeting each other.
///   3. Those gradient magnitudes are normalized against this texture's *own* strong-edge
///      reference (a high percentile of its non-flat gradients - see
///      ResolveGradientReference), so a low-contrast texture still gets a well-formed
///      normal map and a bold one doesn't saturate into a wall of maxed-out edges. This is
///      what the old version lacked: with an unscaled gradient against a fixed Z of 1, a
///      one-level difference and a full black-to-white edge both landed within a degree
///      of each other, so nothing was ever weighted.
///   4. The normalized magnitude then goes through a response curve whose exponent is
///      driven by the per-texture noise index (GetNoiseIndex). A clean texture with
///      well-defined edges gets an exponent above 1: small differences are suppressed and
///      only genuinely big steps produce strong normals, so planks/bricks/tiles read
///      crisply. A noisy texture gets an exponent below 1, lifting its small differences
///      instead - subtle variation is all such a texture has to work with, and its edges
///      were never well-defined to begin with.
///   5. `intensity` (materials.json, default 0.25) scales the resulting slope *before* the
///      normal is built and normalized, so it controls real surface steepness rather than
///      fading an already-encoded normal toward flat. Every output stays a true unit
///      normal at any intensity, and 0 gives a perfectly flat map.
///   6. The blue channel is always overwritten with parallax-occlusion-mapping height
///      data sourced from the *raw* clustered heightmap, separately ceiling-maximized and
///      contrast-reduced (Bedrock RTX reads POM from the normal map's blue channel) - see
///      ApplyPomBlueChannel. This is on for every texture unconditionally; there's no
///      per-block opt-out at generation time. Downstream shader/renderer settings (e.g.
///      BetterRTX) are the right place to let a *player* disable reading POM data - that's
///      not something to gate here.
///
/// Deliberately does no blurring at any stage. Blurring the finished map can't respect the
/// wraparound its gradients were built with - the blur's own edge handling doesn't wrap -
/// so the outermost pixels stop matching the opposite edge. That's invisible on a statically
/// placed tile, but Bedrock randomly rotates isometric block textures, and those mismatched
/// edges then meet each other and show up as seams. The noise index shapes the response
/// curve (step 4) instead, which calms a busy texture without ever touching edge continuity.
/// </summary>
public static class NormalMapGenerator
{
    // TODO(tuning): how much of the height field comes from the mean-shift clustered
    // heightmap vs. a ceiling-maximized flat greyscale of the color texture itself.
    // Higher favors the clean, banded clustered result; lower brings back more of the
    // original texture's own shading detail.
    private const double HeightmapBlendRatio = 0.75;

    // TODO(tuning): the response curve. Exponent applied to each pixel's normalized
    // gradient magnitude, interpolated by noise index: above 1 crushes small differences
    // and rewards big ones, below 1 lifts small ones. If noisy textures come out too busy,
    // raising NoisyTextureExponent toward 1.0 is the first lever to reach for.
    private const double CleanTextureExponent = 2.2;  // noise index 0
    private const double NoisyTextureExponent = 0.65; // noise index 100

    // TODO(tuning): what counts as this texture's "full strength" edge - a percentile
    // taken over its non-flat gradients only. Restricting the population that way matters:
    // on a texture that's mostly empty space, including every flat pixel would drag the
    // percentile down to nothing and then normalize the faint remainder up to full.
    private const double GradientReferencePercentile = 0.95;
    private const double GradientFlatThreshold = 1.0 / 255.0;
    // Floor for that reference, so a genuinely flat texture's faint noise never gets
    // normalized up into a full-strength normal map.
    private const double MinGradientReference = 0.02;

    // TODO(tuning): slope the shaped gradient reaches at normal.intensity = 1.0. At the
    // 0.25 default that works out to a 45-degree tilt on a texture's strongest edges.
    private const double MaxSlope = 4.0;

    // TODO(tuning): calibration ceiling for GetNoiseIndex - what average per-pixel
    // brightness delta counts as "maximally noisy" (index 100).
    private const double NoiseCalibrationCeiling = 40.0;

    // Magnitude histogram used to resolve the percentile above without sorting every
    // pixel - fixed cost regardless of texture size (animation strips get enormous).
    private const int GradientHistogramBins = 1024;
    private const double GradientHistogramMax = 2.0;

    public static Bitmap Generate(Bitmap colorBitmap, NormalParams normalParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        var clustered = HeightmapGenerator.ComputeClusteredHeights(colorBitmap);
        var height = BuildHeightField(colorBitmap, clustered);

        var gradX = new float[w, h];
        var gradY = new float[w, h];
        ComputeSobelGradients(height, w, h, gradX, gradY);

        var reference = ResolveGradientReference(gradX, gradY, w, h);

        var noise = Math.Clamp(GetNoiseIndex(colorBitmap) / 100.0, 0.0, 1.0);
        var exponent = Lerp(CleanTextureExponent, NoisyTextureExponent, noise);
        var strength = MaxSlope * Math.Clamp(normalParams.Intensity, 0.0, 1.0);

        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    double gx = gradX[x, y];
                    double gy = gradY[x, y];
                    var magnitude = Math.Sqrt(gx * gx + gy * gy);

                    double slopeX = 0, slopeY = 0;
                    if (magnitude > 0)
                    {
                        // Shape the magnitude, keep the direction.
                        var shaped = Math.Pow(Math.Clamp(magnitude / reference, 0.0, 1.0), exponent) * strength;
                        slopeX = gx / magnitude * shaped;
                        slopeY = gy / magnitude * shaped;
                    }

                    // (-slopeX, -slopeY, 1) is already DirectX convention (the format
                    // Bedrock RTX expects) mapped straight to (R, G, B) - no axis swap or
                    // extra sign flip belongs here. An earlier version transposed X and Y
                    // trying to "fix" this, which was invisible on a symmetric bump's main
                    // diagonal but swapped the other two corners - don't reintroduce it.
                    var normal = Vector3.Normalize(new Vector3((float)-slopeX, (float)-slopeY, 1f));

                    outFb[x, y] = Color.FromArgb(
                        255,
                        EncodeChannel(normal.X),
                        EncodeChannel(normal.Y),
                        EncodeChannel(normal.Z));
                }
            }
        }

        ApplyPomBlueChannel(output, clustered);

        if (normalParams.Invert)
            InvertRedGreenInPlace(output);

        return output;
    }

    private static byte EncodeChannel(float component)
        => (byte)Math.Clamp((int)Math.Round((component + 1f) * 0.5f * 255f), 0, 255);

    /// <summary>
    /// Blends the mean-shift clustered heightmap (raw, untouched here) with a
    /// ceiling-maximized flat greyscale of the color texture, weighted by
    /// HeightmapBlendRatio (regular linear blend, not overlay), into the single 0-1 height
    /// field the gradients are measured against. Kept as floats rather than round-tripped
    /// through an 8-bit bitmap, so the gradient pass sees the real blend instead of a
    /// re-quantized copy of it. The ceiling-maximize step here only ever touches the
    /// color-texture greyscale computed in this method - the clustered heightmap's own
    /// separate ceiling-maximize, for the POM blue channel, happens later in
    /// ApplyPomBlueChannel and has nothing to do with this blend.
    /// </summary>
    private static float[,] BuildHeightField(Bitmap colorBitmap, int[,] clustered)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        // Shared, alpha-aware contrast maximization (ColorField) rather than a local
        // greyscale pass - background padding under a collapsed alpha channel gets no vote
        // in the range here for exactly the same reason it gets none in MERS generation,
        // and both now go through one implementation instead of two that could drift.
        var maximizedGrey = ColorField.BuildContrastMaximized(colorBitmap);
        var height = new float[w, h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var clusteredNormalized = clustered[x, y] / 255.0;
                var blended = clusteredNormalized * HeightmapBlendRatio + maximizedGrey[x, y] * (1.0 - HeightmapBlendRatio);
                height[x, y] = (float)Math.Clamp(blended, 0.0, 1.0);
            }
        }

        return height;
    }

    /// <summary>
    /// Sobel gradients over the height field, sampled with wraparound indexing so an edge
    /// pixel sees its real neighbor from the opposite edge - that's what makes the result
    /// tile seamlessly, and it replaces the old approach of building a 3x3-tiled copy of
    /// the whole bitmap just to read nine neighbors per pixel.
    ///
    /// Output is in height-per-pixel units - a full 0-to-1 step across one pixel boundary
    /// reads as exactly 1.0 - which is what lets MaxSlope and MinGradientReference be
    /// expressed as real slopes rather than arbitrary kernel-sum numbers.
    ///
    /// (Scharr was measured here as an alternative, on the theory that its rotational
    /// symmetry would matter for the isometric textures Bedrock randomly rotates. It
    /// didn't: the two are identical on smooth gradients - both are exact for linear
    /// signals - and on hard pixel-art edges Scharr came out marginally worse, with both
    /// dominated by the ~32% anisotropy inherent to staircasing a diagonal onto a pixel
    /// grid. Not worth the change.)
    /// </summary>
    private static void ComputeSobelGradients(float[,] height, int w, int h, float[,] gradX, float[,] gradY)
    {
        ReadOnlySpan<float> kx = stackalloc float[] { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
        ReadOnlySpan<float> ky = stackalloc float[] { -1, -2, -1, 0, 0, 0, 1, 2, 1 };
        const float weightSum = 4f; // 1 + 2 + 1 down each signed side of the kernel

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float dx = 0, dy = 0;
                var k = 0;

                for (var j = -1; j <= 1; j++)
                {
                    for (var i = -1; i <= 1; i++)
                    {
                        var nx = ((x + i) % w + w) % w;
                        var ny = ((y + j) % h + h) % h;
                        var v = height[nx, ny];
                        dx += v * kx[k];
                        dy += v * ky[k];
                        k++;
                    }
                }

                gradX[x, y] = dx / weightSum;
                gradY[x, y] = dy / weightSum;
            }
        }
    }

    /// <summary>
    /// The gradient magnitude this texture's "full strength" edges sit at - a high
    /// percentile over its non-flat pixels only. Using the texture's own edges as the
    /// reference is what lets a soft, low-contrast texture still produce a well-formed
    /// normal map while a very bold one doesn't collapse into uniformly maxed-out edges,
    /// and it's what gives the response curve a meaningful 0-1 range to shape in the first
    /// place. Excluding flat pixels matters: on a texture that's mostly empty space,
    /// counting every flat pixel would drag any percentile to nothing, and its faint
    /// remainder would then normalize up to full strength.
    ///
    /// Uses a fixed-size histogram rather than sorting every magnitude, so cost doesn't
    /// scale with the (occasionally enormous - animation strips run to hundreds of
    /// thousands of pixels) texture size.
    /// </summary>
    private static double ResolveGradientReference(float[,] gradX, float[,] gradY, int w, int h)
    {
        var histogram = new int[GradientHistogramBins];
        var counted = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double gx = gradX[x, y];
                double gy = gradY[x, y];
                var magnitude = Math.Sqrt(gx * gx + gy * gy);
                if (magnitude <= GradientFlatThreshold) continue;

                var bin = (int)(magnitude / GradientHistogramMax * (GradientHistogramBins - 1));
                histogram[Math.Clamp(bin, 0, GradientHistogramBins - 1)]++;
                counted++;
            }
        }

        if (counted == 0) return MinGradientReference;

        var target = (long)Math.Ceiling(counted * GradientReferencePercentile);
        long running = 0;

        for (var i = 0; i < GradientHistogramBins; i++)
        {
            running += histogram[i];
            if (running < target) continue;

            var magnitude = (i + 0.5) / (GradientHistogramBins - 1) * GradientHistogramMax;
            return Math.Max(magnitude, MinGradientReference);
        }

        return Math.Max(GradientHistogramMax, MinGradientReference);
    }

    // Built-in (not a materials.json knob) default POM contrast reduction - the mean-shift
    // heightmap can still sink quite deep even after ceiling-maximizing it, so every
    // pixel's remaining distance from the surface (255) gets pulled in by this fraction.
    // Same mechanic as Tuner.ApplyNormalMapIntensity's own blue-channel handling:
    // recession = 255 - value; shrinking recession pulls pixels toward the surface and can
    // never overflow past 255, so no separate compression pass is needed. TODO(tuning).
    private const double PomContrastReduction = 0.67;

    /// <summary>
    /// POM height source: the mean-shift clustered heightmap (HeightmapGenerator.
    /// ComputeClusteredHeights), scaled up so its brightest pixel hits exactly 255 -
    /// deliberately a pure upward scale, not a full min/max stretch, so the darkest
    /// regions don't get lifted off zero and relative height differences stay
    /// proportional to the clustered heightmap's own range - then has its remaining
    /// recession from 255 reduced by PomContrastReduction, since the ceiling-maximize
    /// alone still leaves some clusters sitting quite deep.
    /// </summary>
    private static void ApplyPomBlueChannel(Bitmap normalBitmap, int[,] clustered)
    {
        var w = normalBitmap.Width;
        var h = normalBitmap.Height;

        var maxVal = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (clustered[x, y] > maxVal) maxVal = clustered[x, y];

        var scale = maxVal > 0 ? 255.0 / maxVal : 1.0;

        using var normalFb = new FastBitmap(normalBitmap, writable: true);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var maximized = clustered[x, y] * scale;
                var recession = 255.0 - maximized;
                var pom = (byte)Math.Clamp((int)Math.Round(255.0 - recession * (1.0 - PomContrastReduction)), 0, 255);
                var c = normalFb[x, y];
                normalFb[x, y] = Color.FromArgb(c.A, c.R, c.G, pom);
            }
        }
    }

    /// <summary>
    /// Average local gradient magnitude between each pixel and its immediate right/down
    /// neighbor (flat-average grey values), normalized to a 0-100 index. Unlike a raw
    /// unique-color ratio, this actually tracks visual noisiness: soft painterly
    /// gradients (many unique colors, small per-pixel jumps) score low, while genuinely
    /// noisy/high-frequency textures (large abrupt jumps) score high.
    /// </summary>
    public static int GetNoiseIndex(Bitmap image)
    {
        var w = image.Width;
        var h = image.Height;
        var grey = new int[w, h];

        using (var fb = new FastBitmap(image, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = fb[x, y];
                    grey[x, y] = (c.R + c.G + c.B) / 3;
                }
            }
        }

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x + 1 < w)
                {
                    totalDelta += Math.Abs(grey[x, y] - grey[x + 1, y]);
                    sampleCount++;
                }
                if (y + 1 < h)
                {
                    totalDelta += Math.Abs(grey[x, y] - grey[x, y + 1]);
                    sampleCount++;
                }
            }
        }

        var averageDelta = sampleCount > 0 ? totalDelta / sampleCount : 0;
        return (int)Math.Clamp(averageDelta / NoiseCalibrationCeiling * 100.0, 0, 100);
    }

    private static void InvertRedGreenInPlace(Bitmap bitmap)
    {
        using var fb = new FastBitmap(bitmap, writable: true);
        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                fb[x, y] = Color.FromArgb(c.A, 255 - c.R, 255 - c.G, c.B); // blue (POM) untouched
            }
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

#endregion

#region Heightmap Generation

/// <summary>
/// Generates a heightmap from a color texture:
///   1. ComputeClusteredHeights groups the texture's grey values into however many
///      distinct height bands it actually has via mean-shift filtering (iterated joint
///      spatial+range weighted averaging, which converges to the same piecewise-flat
///      result as literal mode-seeking mean-shift), then assigns each band an
///      evenly-spaced output level by brightness rank - this replaces the old flat
///      contrast-stretch + fixed-3-level quantization. Shared with NormalMapGenerator,
///      which uses this same clustering as its own height-field basis.
///   2. Transparent regions of the color texture get darkened using an overlay blend
///      (not linear) - preserves midtone detail on the darkened side, which matters for
///      cases like grass_side where the dirt portion is transparent in the color texture
///      but should still read as "below" the opaque grass part.
///   3. `intensity` blends the clustered result toward a flat neutral median (128).
///   4. `invert` (workaround for the known game-side inverted-display bug) does a final
///      full color inversion.
/// Output is single-channel-equivalent grayscale (R=G=B), fully opaque.
/// </summary>
public static class HeightmapGenerator
{
    private const byte LevelMid = 128;

    // TODO(tuning): how strongly a fully-transparent color-texture pixel's darkening
    // overlay applies (0 = no darkening, 1 = full overlay strength) - see class doc
    // comment above for why this exists (grass_side-style "read as beneath" regions).
    private const double TransparencyOverlayStrength = 0.5;

    // TODO(tuning): mean-shift filtering knobs - iteration count, spatial window radius,
    // range (value) bandwidth, and the spatial sigma that shapes how much nearby pixels
    // outweigh distant ones. Untested against a broad enough set of textures yet; wants
    // an artist's eye once there's enough generated output to compare against.
    private const int MeanShiftIterations = 5;
    private const int SpatialRadius = 2;
    private const double SpatialSigma = 1.5;

    // TODO(tuning): how far apart two values can be and still get pulled together.
    //
    // This was briefly narrowed to 12 to stop mean-shift bridging a shallow mortar line
    // into the plank above it. It did fix that in isolation, but on real packs the extra
    // modes it produced - combined with the value-based placement tried alongside it -
    // turned output into little more than a posterized greyscale: far too many distinct
    // elevations, which is the defining trait of a heightmap that reads as a mess in game.
    // Back at 24 the clustering stays coarse enough to produce real plateaus.
    private const double RangeBandwidth = 24.0;

    // Above this pixel count, ComputeClusteredHeights skips the spatial neighbor search
    // (which scales with W*H*R^2*iterations) and falls back to range-only clustering off
    // a 256-bin histogram instead - still mean-shift filtering, just position-independent
    // and bounded regardless of texture size. Automatic, not a user-facing setting.
    private const int SpatialFallbackPixelCount = 256 * 256;

    // Converged values within this distance of each other are folded into the same
    // cluster during ranking.
    private const double ClusterMergeTolerance = 8.0;

    // Hard cap on the final number of elevation levels a texture can produce, regardless
    // of how many distinct clusters mean-shift converged to. A busy/high-color-count
    // texture that would otherwise land on many clusters gets merged down harder to reach
    // this; a calm texture that already converged to fewer is untouched. TODO(tuning).
    private const int MaxClusters = 6;

    // Pairing mean-shift with a fixed-step quantization of the same values, and keeping
    // only regions the two agree on, was tried here as a guard against mean-shift bridging
    // features it shouldn't. It didn't survive measurement: fixed band boundaries sit at
    // arbitrary values, so on a plank texture whose mortar line straddled one, the line
    // came out as two different heights - worse than the problem it was meant to fix - and
    // once the bandwidth below was narrowed the guard had nothing left to catch anyway.
    // Narrowing the bandwidth is the real cure; the merge pass makes it safe.

    public static Bitmap Generate(Bitmap colorBitmap, HeightmapParams heightmapParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var clustered = ComputeClusteredHeights(colorBitmap);
        var alpha = new byte[w, h];

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    alpha[x, y] = colorFb[x, y].A;
        }

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var clusteredValue = (byte)clustered[x, y];

                    var transparencyAmount = 255 - alpha[x, y];
                    var overlayStrength = transparencyAmount * TransparencyOverlayStrength / 255.0;

                    var withOverlay = clusteredValue;
                    if (overlayStrength > 0)
                    {
                        var blended = OverlayBlendWithBlack(clusteredValue);
                        withOverlay = Lerp(clusteredValue, blended, overlayStrength);
                    }

                    var withIntensity = Lerp(LevelMid, withOverlay, Math.Clamp(heightmapParams.Intensity, 0.0, 1.0));
                    var final = heightmapParams.Invert ? (byte)(255 - withIntensity) : withIntensity;

                    outFb[x, y] = Color.FromArgb(255, final, final, final);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Mean-shift filtering core, shared with NormalMapGenerator (which uses this as its
    /// own height-field input, and separately as its POM blue-channel source). Returns
    /// raw clustered grey values (0-255) with no artist-facing post-processing applied.
    /// </summary>
    public static int[,] ComputeClusteredHeights(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        // Contrast-maximized up front (and alpha-aware - background padding under a
        // collapsed alpha channel gets no vote in the range, see ColorField), so the
        // bandwidth/tolerance constants below mean the same thing on a washed-out texture
        // as on a punchy one instead of drifting with whatever range the source happened
        // to occupy.
        var normalized = ColorField.BuildContrastMaximized(colorBitmap);

        var grey = new double[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grey[x, y] = normalized[x, y] * 255.0;

        var converged = (long)w * h <= SpatialFallbackPixelCount
            ? ConvergeSpatial(grey, w, h)
            : ConvergeRangeOnly(grey, w, h);

        return ClusterAndPlace(converged, grey, w, h);
    }

    /// <summary>Full joint spatial+range mean-shift filtering: each pixel's value is
    /// repeatedly replaced by a weighted average of its small spatial neighborhood,
    /// weighted by both spatial closeness and value closeness. Neighbors are sampled with
    /// wraparound/modulo indexing so results stay seamless-tileable without needing a full
    /// 3x tiled copy at this small radius.</summary>
    private static double[,] ConvergeSpatial(double[,] grey, int w, int h)
    {
        var current = grey;

        for (var iter = 0; iter < MeanShiftIterations; iter++)
        {
            var next = new double[w, h];

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var centerValue = current[x, y];
                    double weightSum = 0, valueSum = 0;

                    for (var dy = -SpatialRadius; dy <= SpatialRadius; dy++)
                    {
                        for (var dx = -SpatialRadius; dx <= SpatialRadius; dx++)
                        {
                            var nx = ((x + dx) % w + w) % w;
                            var ny = ((y + dy) % h + h) % h;
                            var neighborValue = grey[nx, ny];

                            var spatialDist2 = dx * dx + dy * dy;
                            var rangeDist = neighborValue - centerValue;
                            var weight = Math.Exp(-spatialDist2 / (2 * SpatialSigma * SpatialSigma))
                                       * Math.Exp(-(rangeDist * rangeDist) / (2 * RangeBandwidth * RangeBandwidth));

                            weightSum += weight;
                            valueSum += weight * neighborValue;
                        }
                    }

                    next[x, y] = weightSum > 0 ? valueSum / weightSum : centerValue;
                }
            }

            current = next;
        }

        return current;
    }

    /// <summary>Value-only mean-shift for very large textures, where a per-pixel spatial
    /// neighbor search would scale too aggressively: clusters purely on a 256-bin
    /// grey-value histogram (position-independent), which is O(256^2*iterations)
    /// regardless of texture size, then looks each pixel's converged value up by its
    /// rounded grey value.</summary>
    private static double[,] ConvergeRangeOnly(double[,] grey, int w, int h)
    {
        var histogram = new int[256];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                histogram[Math.Clamp((int)Math.Round(grey[x, y]), 0, 255)]++;

        var representative = new double[256];
        for (var i = 0; i < 256; i++) representative[i] = i;

        for (var iter = 0; iter < MeanShiftIterations; iter++)
        {
            var next = new double[256];

            for (var i = 0; i < 256; i++)
            {
                double weightSum = 0, valueSum = 0;

                for (var j = 0; j < 256; j++)
                {
                    if (histogram[j] == 0) continue;
                    var rangeDist = j - representative[i];
                    var weight = histogram[j] * Math.Exp(-(rangeDist * rangeDist) / (2 * RangeBandwidth * RangeBandwidth));
                    weightSum += weight;
                    valueSum += weight * j;
                }

                next[i] = weightSum > 0 ? valueSum / weightSum : representative[i];
            }

            representative = next;
        }

        var converged = new double[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                converged[x, y] = representative[Math.Clamp((int)Math.Round(grey[x, y]), 0, 255)];

        return converged;
    }

    /// <summary>Merges per-pixel converged values into clusters (values within
    /// ClusterMergeTolerance of their neighbor in sorted order are folded together), caps
    /// the result at MaxClusters by repeatedly merging whichever two brightness-adjacent
    /// clusters are closest together (weighted by pixel count) until at or under the cap,
    /// then places each surviving cluster at its own mean brightness - see the comment at
    /// the placement step for why placing by value rather than by rank is what makes
    /// plank/mortar-style textures come out right.</summary>
    private static int[,] ClusterAndPlace(double[,] converged, double[,] original, int w, int h)
    {
        var distinctValues = new SortedSet<double>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                distinctValues.Add(Math.Round(converged[x, y], 1));

        var clusterOf = new Dictionary<double, int>();
        var clusterId = 0;
        var clusterAnchor = 0.0;
        var first = true;

        foreach (var v in distinctValues)
        {
            if (first || v - clusterAnchor > ClusterMergeTolerance)
            {
                if (!first) clusterId++;
                clusterAnchor = v;
                first = false;
            }
            clusterOf[v] = clusterId;
        }

        var clusterCount = clusterId + 1;
        var brightnessSum = new double[clusterCount];
        var brightnessCount = new int[clusterCount];
        var pixelCluster = new int[w, h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var id = clusterOf[Math.Round(converged[x, y], 1)];
                pixelCluster[x, y] = id;
                brightnessSum[id] += original[x, y];
                brightnessCount[id]++;
            }
        }

        var clusterMeanBrightness = new double[clusterCount];
        for (var i = 0; i < clusterCount; i++)
            clusterMeanBrightness[i] = brightnessCount[i] > 0 ? brightnessSum[i] / brightnessCount[i] : 0;

        // Brightness-sorted list of surviving buckets, each carrying which original region
        // ids it absorbed. Starts as one bucket per region; merging two adjacent buckets
        // (always adjacent in this sorted order, since nothing ever reorders) is just a
        // local list splice - cheap even repeated all the way down.
        var order = Enumerable.Range(0, clusterCount).OrderBy(i => clusterMeanBrightness[i]).ToList();
        var bucketMeans = order.Select(i => clusterMeanBrightness[i]).ToList();
        var bucketCounts = order.Select(i => brightnessCount[i]).ToList();
        var bucketMembers = order.Select(i => new List<int> { i }).ToList();

        while (bucketMeans.Count > 1)
        {
            var mergeAt = 0;
            var smallestGap = double.MaxValue;
            for (var i = 0; i < bucketMeans.Count - 1; i++)
            {
                var gap = bucketMeans[i + 1] - bucketMeans[i];
                if (gap < smallestGap) { smallestGap = gap; mergeAt = i; }
            }

            // Two reasons to keep merging: still over the cap, or the two closest levels
            // are near enough that keeping them apart would just be two nearly-identical
            // heights sitting next to each other - which is the definition of a heightmap
            // that reads as a mess in game. Under the cap AND well separated means done.
            if (bucketMeans.Count <= MaxClusters && smallestGap > ClusterMergeTolerance) break;

            var combinedCount = bucketCounts[mergeAt] + bucketCounts[mergeAt + 1];
            bucketMeans[mergeAt] = combinedCount > 0
                ? (bucketMeans[mergeAt] * bucketCounts[mergeAt] + bucketMeans[mergeAt + 1] * bucketCounts[mergeAt + 1]) / combinedCount
                : (bucketMeans[mergeAt] + bucketMeans[mergeAt + 1]) / 2.0;
            bucketCounts[mergeAt] = combinedCount;
            bucketMembers[mergeAt].AddRange(bucketMembers[mergeAt + 1]);

            bucketMeans.RemoveAt(mergeAt + 1);
            bucketCounts.RemoveAt(mergeAt + 1);
            bucketMembers.RemoveAt(mergeAt + 1);
        }

        var bucketOf = new int[clusterCount];
        for (var bucket = 0; bucket < bucketMembers.Count; bucket++)
            foreach (var originalId in bucketMembers[bucket])
                bucketOf[originalId] = bucket;

        // Buckets are placed at evenly spaced slots by brightness RANK, not at their own
        // mean brightness.
        //
        // Placing by value is the more faithful of the two, and measurably so - on a plank
        // texture it reproduced the real mortar-gap-to-grain ratio almost exactly where
        // ranking inverted it. But faithful to brightness turns out to be the wrong target,
        // because brightness isn't height: preserving true brightness relationships also
        // preserves every bit of wood grain and surface mottling as real elevation, and the
        // output stops being a heightmap and becomes a posterized greyscale of the colour
        // texture. Ranking's "inaccuracy" is doing useful work - it collapses each cluster
        // onto a discrete step and pushes the steps apart, which is what reads as distinct
        // flat surfaces in game.
        //
        // The honest limitation underneath: brightness is only a proxy for height, so no
        // placement rule can be right in general. Ranking just fails more gracefully.
        var placed = new int[bucketMeans.Count];
        for (var i = 0; i < bucketMeans.Count; i++)
            placed[i] = bucketMeans.Count > 1
                ? (int)Math.Clamp(Math.Round(i / (double)(bucketMeans.Count - 1) * 255.0), 0, 255)
                : LevelMid;

        var result = new int[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                result[x, y] = placed[bucketOf[pixelCluster[x, y]]];
            }
        }

        return result;
    }

    /// <summary>Photoshop-style "Overlay" blend against a black overlay, at full strength
    /// (caller lerps by overlayStrength afterwards).</summary>
    private static byte OverlayBlendWithBlack(byte baseValue)
    {
        var baseNorm = baseValue / 255.0;
        var result = baseNorm < 0.5 ? 0.0 : 2 * baseNorm - 1;
        return (byte)Math.Clamp((int)Math.Round(result * 255.0), 0, 255);
    }

    private static byte Lerp(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a * (1.0 - t) + b * t), 0, 255);
}

#endregion
