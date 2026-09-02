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
/// MersGenerator below.
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
/// The color texture is read upstream (AlchitexPipeline.ProcessOneTarget) with
/// maxOpacity: true, so originally-transparent pixels arrive here with their real
/// (usually near-black, per Helpers.ReadImage) RGB recoverable and alpha forced to 255 -
/// there's no separate "cutout" case to special-case here: those pixels just flow through
/// the same stretch math as everything else, which naturally lands on safe near-matte
/// values for the default material entry (roughness stretches toward its max/inverted
/// range at low grey values).
/// </summary>
public static class MersGenerator
{
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
                    if (g < greyMin) greyMin = g;
                    if (g > greyMax) greyMax = g;

                    var l = (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                    luminosity[x, y] = l;
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
/// Generates a normal map from a color texture, built on top of
/// HeightmapGenerator.ComputeClusteredHeights rather than raw color brightness - the
/// mean-shift clustered heightmap is the shared height-field basis for both texture
/// types now:
///   1. The clustered heightmap is blended 50/50 (regular linear blend, not overlay) with
///      a ceiling-maximized flat greyscale of the color texture, giving a clean height
///      field that still carries some of the original texture's own shading.
///   2. That blended greyscale is sampled across a 3x3-tiled copy of itself (so pixels
///      near the texture's edges see their *real* neighbors instead of a hard cutoff -
///      this is what makes the result tile seamlessly in-game) and run through a Sobel
///      operator to produce the normal's X/Y/Z.
///   3. Blur is driven continuously by a per-texture noise index (local gradient/contrast
///      heuristic - see GetNoiseIndex), so a busy/noisy texture automatically gets a
///      calmer normal map.
///   4. `intensity` (materials.json, default 0.5 - deliberately toned down from the old
///      always-near-maximum result) blends the computed normal toward flat-up
///      (128,128,255); this is now a direct artist knob, independent of the noise index.
///   5. The blue channel is always overwritten with parallax-occlusion-mapping height
///      data sourced from the *raw* clustered heightmap (Bedrock RTX reads POM from the
///      normal map's blue channel) - see ApplyPomBlueChannel. This is on for every
///      texture unconditionally; there's no per-block opt-out at generation time.
///      Downstream shader/renderer settings (e.g. BetterRTX) are the right place to let a
///      *player* disable reading POM data - that's not something to gate here.
/// </summary>
public static class NormalMapGenerator
{
    // TODO(tuning): how aggressively noisy textures get blurred - untested against a
    // broad enough set of textures yet.
    private const double MinBlurSigma = 0.3;
    private const double MaxBlurSigma = 1.6;

    public static Bitmap Generate(Bitmap colorBitmap, NormalParams normalParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        var clustered = HeightmapGenerator.ComputeClusteredHeights(colorBitmap);

        var noiseIndex = GetNoiseIndex(colorBitmap);
        var t = Math.Clamp(noiseIndex / 100.0, 0.0, 1.0);
        var blurSigma = Lerp(MinBlurSigma, MaxBlurSigma, t);
        var flatten = (float)(1.0 - Math.Clamp(normalParams.Intensity, 0.0, 1.0));

        using var blended = BuildBlendedGreyBitmap(colorBitmap, clustered);
        using var tiled = BuildTiledCopy(blended);
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

        ApplyPomBlueChannel(blurred, clustered);

        if (normalParams.Invert)
            InvertRedGreenInPlace(blurred);

        return blurred;
    }

    /// <summary>
    /// Combines the mean-shift clustered heightmap with a ceiling-maximized flat
    /// greyscale of the color texture via a regular 50% linear blend (not overlay) - this
    /// is the "somewhat clean greyscale image" that becomes the Sobel input, giving
    /// normal generation a proper height-field basis instead of raw color brightness.
    /// </summary>
    private static Bitmap BuildBlendedGreyBitmap(Bitmap colorBitmap, int[,] clustered)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

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

        using var outFb = new FastBitmap(output, writable: true);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var maximizedGrey = Math.Clamp((int)Math.Round(grey[x, y] * scale), 0, 255);
                var blendedValue = (byte)Math.Clamp((int)Math.Round((maximizedGrey + clustered[x, y]) / 2.0), 0, 255);
                outFb[x, y] = Color.FromArgb(255, blendedValue, blendedValue, blendedValue);
            }
        }

        return output;
    }

    /// <summary>
    /// POM height source: the mean-shift clustered heightmap (HeightmapGenerator.
    /// ComputeClusteredHeights), scaled up so its brightest pixel hits exactly 255 -
    /// deliberately a pure upward scale, not a full min/max stretch, so the darkest
    /// regions don't get lifted off zero and relative height differences stay
    /// proportional to the clustered heightmap's own range.
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
                var pom = (byte)Math.Clamp((int)Math.Round(clustered[x, y] * scale), 0, 255);
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
    /// Average local gradient magnitude between each pixel and its immediate right/down
    /// neighbor (flat-average grey values), normalized to a 0-100 index. Unlike a raw
    /// unique-color ratio, this actually tracks visual noisiness: soft painterly
    /// gradients (many unique colors, small per-pixel jumps) score low, while genuinely
    /// noisy/high-frequency textures (large abrupt jumps) score high.
    /// </summary>
    public static int GetNoiseIndex(Bitmap image)
    {
        // TODO(tuning): calibration ceiling for what counts as "maximally noisy" -
        // untested against a broad enough set of textures yet.
        const double calibrationCeiling = 40.0;

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
        return (int)Math.Clamp(averageDelta / calibrationCeiling * 100.0, 0, 100);
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

    // TODO(tuning): mean-shift filtering knobs - iteration count, spatial window radius,
    // range (value) bandwidth, and the spatial sigma that shapes how much nearby pixels
    // outweigh distant ones. Untested against a broad enough set of textures yet; wants
    // an artist's eye once there's enough generated output to compare against.
    private const int MeanShiftIterations = 5;
    private const int SpatialRadius = 2;
    private const double SpatialSigma = 1.5;
    private const double RangeBandwidth = 24.0;

    // Above this pixel count, ComputeClusteredHeights skips the spatial neighbor search
    // (which scales with W*H*R^2*iterations) and falls back to range-only clustering off
    // a 256-bin histogram instead - still mean-shift filtering, just position-independent
    // and bounded regardless of texture size. Automatic, not a user-facing setting.
    private const int SpatialFallbackPixelCount = 256 * 256;

    // Converged values within this distance of each other are folded into the same
    // cluster during ranking.
    private const double ClusterMergeTolerance = 8.0;

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
                    var overlayStrength = transparencyAmount * 0.5 / 255.0;

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

        var grey = new double[w, h];
        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    grey[x, y] = (c.R + c.G + c.B) / 3.0;
                }
            }
        }

        var converged = (long)w * h <= SpatialFallbackPixelCount
            ? ConvergeSpatial(grey, w, h)
            : ConvergeRangeOnly(grey, w, h);

        return ClusterAndRank(converged, grey, w, h);
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
    /// ClusterMergeTolerance of their neighbor in sorted order are folded together),
    /// ranks clusters by their mean *original* grey value, and assigns each an
    /// evenly-spaced output level across 0-255 by rank.</summary>
    private static int[,] ClusterAndRank(double[,] converged, double[,] original, int w, int h)
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

        var rank = Enumerable.Range(0, clusterCount)
            .OrderBy(i => clusterMeanBrightness[i])
            .Select((originalIndex, rankIndex) => (originalIndex, rankIndex))
            .ToDictionary(p => p.originalIndex, p => p.rankIndex);

        var result = new int[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var r = rank[pixelCluster[x, y]];
                var level = clusterCount > 1 ? (int)Math.Round(r / (double)(clusterCount - 1) * 255.0) : 128;
                result[x, y] = Math.Clamp(level, 0, 255);
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
