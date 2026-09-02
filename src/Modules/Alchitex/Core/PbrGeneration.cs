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
/// never plain metalness_emissive_roughness. Whether SSS actually does anything is a
/// generation-time decision (AlchitexOptions.SubsurfaceScattering), not a schema decision -
/// see MersGenerator below.
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
        AlchitexOptions options)
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
            if (nameLowerNoExt.Contains("bubble") || nameLowerNoExt.Contains("_placeholder") || nameLowerNoExt.Contains("_carried")) return true;
            // Water is handled entirely by PostProcess (Bedrock RTX reads water_*_grey
            // directly, not through a per-block texture_set.json/materials.json pass).
            if (nameLowerNoExt.Contains("water_flow") || nameLowerNoExt.Contains("water_still")) return true;
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

                var secondaryMode = ResolveSecondaryMode(options.SecondaryPbr, colorPath);

                var set = new JsonObject
                {
                    ["color"] = nameNoExt,
                    ["metalness_emissive_roughness_subsurface"] = nameNoExt + "_mers",
                };

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
    /// otherwise -> normal map.
    /// </summary>
    private static SecondaryPbrMode ResolveSecondaryMode(SecondaryPbrMode requested, string colorPath)
    {
        if (requested != SecondaryPbrMode.Auto)
            return requested;

        try
        {
            var info = new MagickImageInfo(colorPath);
            return info.Width <= AlchitexOptions.AutoModeHeightmapMaxWidth
                ? SecondaryPbrMode.Heightmap
                : SecondaryPbrMode.Normal;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Auto mode: couldn't read dimensions for '{colorPath}' ({ex.Message}), defaulting to normal map.");
            return SecondaryPbrMode.Normal;
        }
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

#region MERS Generation

/// <summary>
/// Generates a MERS (metalness/emissive/roughness/subsurface) bitmap from a color texture
/// and a resolved MaterialEntry. Alchitex only ever produces MERS, never plain MER - see
/// TextureSetOrchestrator above. `sssEnabled` is the run-wide toggle: when false, every
/// block's SSS output is forced to (0, 0) - alpha always 0, i.e. no subsurface scattering
/// anywhere - regardless of what materials.json specifies for that block; when true, each
/// block's own materials.json SSS range is honored.
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
///   5. Safety net: any pixel whose source color was fully transparent gets forced to
///      metal=0, emissive=0, roughness=255 (fully matte), alpha=255 opaque - so an
///      invisible cutout pixel never bleeds a stray metallic/emissive/near-zero-roughness
///      texel into edge mip levels. (This one exception to "alpha = SSS" is deliberate:
///      a cutout pixel isn't real surface, so its alpha here is about mip-edge safety,
///      not scattering.)
/// </summary>
public static class MersGenerator
{
    public static Bitmap Generate(Bitmap colorBitmap, MaterialEntry material, bool sssEnabled)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var grey = new int[w, h];
        var luminosity = new int[w, h];
        var sourceAlpha = new byte[w, h];

        int greyMin = 255, greyMax = 0;
        int lumMin = 255, lumMax = 0;
        var anyOpaquePixel = false;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    sourceAlpha[x, y] = c.A;

                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;
                    if (g < greyMin) greyMin = g;
                    if (g > greyMax) greyMax = g;

                    var l = (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                    luminosity[x, y] = l;

                    if (c.A > 0)
                    {
                        anyOpaquePixel = true;
                        if (l < lumMin) lumMin = l;
                        if (l > lumMax) lumMax = l;
                    }
                }
            }
        }

        if (!anyOpaquePixel) { lumMin = 0; lumMax = 255; }

        // (0,0) when disabled, exactly what Stretch() below needs to always produce 0.
        var effectiveSss = sssEnabled ? material.Sss : new SssParams();

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
                    var alpha = Stretch(luminosity[x, y], lumMin, lumMax, effectiveSss.Min, effectiveSss.Max, invert: false);

                    outFb[x, y] = Color.FromArgb(alpha, r, gr, b);
                }
            }

            foreach (var pass in material.Recursive)
            {
                ApplyRecursivePass(colorBitmap, grey, greyMin, greyMax, luminosity, lumMin, lumMax, outFb, pass, sssEnabled);
            }

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (sourceAlpha[x, y] == 0)
                        outFb[x, y] = Color.FromArgb(255, 0, 0, 255);
                }
            }
        }

        return output;
    }

    private static void ApplyRecursivePass(
        Bitmap colorBitmap,
        int[,] grey, int greyMin, int greyMax,
        int[,] luminosity, int lumMin, int lumMax,
        FastBitmap outFb,
        RecursivePass pass,
        bool sssEnabled)
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
                    var opacity = ComputeDominanceOpacity(colorFb[x, y], pass.Channel);
                    mask[x, y] = opacity;
                    if (opacity <= 0) continue;

                    anyMasked = true;
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
                if (sssEnabled && pass.Sss != null)
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

        if (c.R == 255 && c.G == 255 && c.B == 255) return 85; // 255/3 - white doesn't max out the mask
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
/// Generates a normal map from a color texture using a Sobel operator, sampled across a
/// 3x3-tiled copy of the source so pixels near the texture's edges see their *real*
/// neighbors (the repeated adjacent tile) instead of a hard cutoff - this is what makes
/// the result tile seamlessly in-game.
///
/// Blur/flatten amount is driven continuously by a per-texture noise index (unique-pixel-
/// ratio heuristic), so a busy/noisy texture automatically gets a simpler, calmer normal
/// map and a clean/painterly texture keeps its full detail.
///
/// The blue channel is always overwritten with parallax-occlusion-mapping height data
/// (Bedrock RTX reads POM from the normal map's blue channel) rather than left as the
/// Sobel Z-component - see ApplyPomBlueChannel. This is on for every texture
/// unconditionally; there's no per-block opt-out at generation time. Downstream
/// shader/renderer settings (e.g. BetterRTX) are the right place to let a *player* disable
/// reading POM data - that's not something to gate here.
/// </summary>
public static class NormalMapGenerator
{
    // TODO(tuning): the entire "how aggressively do we simplify noisy textures" knob.
    private const double MinBlurSigma = 0.3;
    private const double MaxBlurSigma = 1.6;
    private const float MinFlatten = 0.05f;
    private const float MaxFlatten = 0.55f;

    public static Bitmap Generate(Bitmap colorBitmap, NormalParams normalParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        var noiseIndex = GetNoiseIndex(colorBitmap);
        var t = Math.Clamp(noiseIndex / 100.0, 0.0, 1.0);
        var blurSigma = Lerp(MinBlurSigma, MaxBlurSigma, t);
        var flatten = (float)Lerp(MinFlatten, MaxFlatten, t);

        using var tiled = BuildTiledCopy(colorBitmap);
        var raw = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var tiledFb = new FastBitmap(tiled, writable: false))
        using (var rawFb = new FastBitmap(raw, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var normal = CalculateNormal(tiledFb, w + x, h + y);
                    normal = Vector3.Normalize(normal);

                    normal.Y = -normal.Y;
                    (normal.X, normal.Y) = (-normal.Y, normal.X);

                    var r = (normal.X + 1f) * 0.5f * 255f;
                    var g = (normal.Y + 1f) * 0.5f * 255f;
                    var b = (normal.Z + 1f) * 0.5f * 255f;

                    r = r * (1 - flatten) + 128f * flatten;
                    g = g * (1 - flatten) + 128f * flatten;
                    b = b * (1 - flatten) + 255f * flatten;

                    rawFb[x, y] = Color.FromArgb(
                        255,
                        (byte)Math.Clamp((int)r, 0, 255),
                        (byte)Math.Clamp((int)g, 0, 255),
                        (byte)Math.Clamp((int)b, 0, 255));
                }
            }
        }

        var blurred = BlurWithMagick(raw, blurSigma);
        raw.Dispose();

        ApplyPomBlueChannel(blurred, colorBitmap);

        if (normalParams.Invert)
            InvertRedGreenInPlace(blurred);

        return blurred;
    }

    /// <summary>
    /// POM height source: flat-average greyscale of the color texture (same convention as
    /// MERS/heightmap generation), scaled up so its brightest pixel hits exactly 255 -
    /// deliberately a pure upward scale, not a full min/max stretch, so the darkest
    /// regions don't get lifted off zero and relative height differences stay
    /// proportional to the source image's own brightness range.
    /// </summary>
    private static void ApplyPomBlueChannel(Bitmap normalBitmap, Bitmap colorBitmap)
    {
        var w = normalBitmap.Width;
        var h = normalBitmap.Height;

        if (colorBitmap.Width != w || colorBitmap.Height != h)
        {
            Trace.WriteLine("[ALCHITEX] POM: color and normal map dimensions differ - skipping POM blue channel override for this texture.");
            return;
        }

        var grey = new int[w, h];
        var maxVal = 0;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;
                    if (g > maxVal) maxVal = g;
                }
            }
        }

        var scale = maxVal > 0 ? 255.0 / maxVal : 1.0;

        using var normalFb = new FastBitmap(normalBitmap, writable: true);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var pom = (byte)Math.Clamp((int)Math.Round(grey[x, y] * scale), 0, 255);
                var c = normalFb[x, y];
                normalFb[x, y] = Color.FromArgb(c.A, c.R, c.G, pom);
            }
        }
    }

    private static Bitmap BuildTiledCopy(Bitmap source)
    {
        var w = source.Width;
        var h = source.Height;
        var tiled = new Bitmap(w * 3, h * 3, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(tiled);
        for (var ty = 0; ty < 3; ty++)
            for (var tx = 0; tx < 3; tx++)
                g.DrawImage(source, tx * w, ty * h);

        return tiled;
    }

    private static Vector3 CalculateNormal(FastBitmap tiled, int x, int y)
    {
        ReadOnlySpan<float> gx = stackalloc float[] { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
        ReadOnlySpan<float> gy = stackalloc float[] { -1, -2, -1, 0, 0, 0, 1, 2, 1 };

        float dx = 0, dy = 0;
        var k = 0;
        for (var j = -1; j <= 1; j++)
        {
            for (var i = -1; i <= 1; i++)
            {
                // x,y is always deep inside the tiled canvas (one full real tile on every
                // side), so a +-1 kernel offset never leaves tiled bounds - no wrap needed.
                var c = tiled[x + i, y + j];
                var intensity = (c.R + c.G + c.B) / 3f;
                dx += intensity * gx[k];
                dy += intensity * gy[k];
                k++;
            }
        }

        return new Vector3(-dx, -dy, 1);
    }

    /// <summary>
    /// Fraction of the texture's pixels that are unique colors, scaled to a 0-100 index.
    /// </summary>
    public static int GetNoiseIndex(Bitmap image)
    {
        var w = image.Width;
        var h = image.Height;
        var total = w * h;
        var unique = new HashSet<int>();

        using (var fb = new FastBitmap(image, writable: false))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    unique.Add(fb[x, y].ToArgb());
        }

        var ratio = unique.Count / (double)total;
        return (int)Math.Min(100.0, ratio * 3 * 100);
    }

    private static Bitmap BlurWithMagick(Bitmap bitmap, double sigma)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var magickImage = new MagickImage(ms);
        magickImage.Blur(0, sigma);
        return magickImage.ToBitmap();
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
///   1. Flat-average greyscale, contrast-stretched across the texture's own min/max.
///   2. Quantized down to exactly three levels (0 / 128 / 255).
///   3. Transparent regions of the color texture get darkened using an overlay blend
///      (not linear) - preserves midtone detail on the darkened side, which matters for
///      cases like grass_side where the dirt portion is transparent in the color texture
///      but should still read as "below" the opaque grass part.
///   4. `intensity` blends the quantized result toward a flat neutral median (128).
///   5. `invert` (workaround for the known game-side inverted-display bug) does a final
///      full color inversion.
/// Output is single-channel-equivalent grayscale (R=G=B), fully opaque.
/// </summary>
public static class HeightmapGenerator
{
    private const byte LevelLow = 0;
    private const byte LevelMid = 128;
    private const byte LevelHigh = 255;

    public static Bitmap Generate(Bitmap colorBitmap, HeightmapParams heightmapParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var grey = new int[w, h];
        var alpha = new byte[w, h];
        int greyMin = 255, greyMax = 0;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    alpha[x, y] = c.A;

                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;
                    if (g < greyMin) greyMin = g;
                    if (g > greyMax) greyMax = g;
                }
            }
        }

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    double t = greyMax > greyMin ? (grey[x, y] - greyMin) / (double)(greyMax - greyMin) : 0.5;
                    var stretched = (byte)Math.Clamp((int)Math.Round(t * 255.0), 0, 255);

                    var quantized = QuantizeToThreeLevels(stretched);

                    var transparencyAmount = 255 - alpha[x, y];
                    var overlayStrength = transparencyAmount * 0.5 / 255.0;

                    var withOverlay = quantized;
                    if (overlayStrength > 0)
                    {
                        var blended = OverlayBlendWithBlack(quantized);
                        withOverlay = Lerp(quantized, blended, overlayStrength);
                    }

                    var withIntensity = Lerp(LevelMid, withOverlay, Math.Clamp(heightmapParams.Intensity, 0.0, 1.0));
                    var final = heightmapParams.Invert ? (byte)(255 - withIntensity) : withIntensity;

                    outFb[x, y] = Color.FromArgb(255, final, final, final);
                }
            }
        }

        return output;
    }

    private static byte QuantizeToThreeLevels(byte value)
    {
        var dLow = Math.Abs(value - LevelLow);
        var dMid = Math.Abs(value - LevelMid);
        var dHigh = Math.Abs(value - LevelHigh);

        if (dLow <= dMid && dLow <= dHigh) return LevelLow;
        return dMid <= dHigh ? LevelMid : LevelHigh;
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
