using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using ImageMagick.Drawing; // Drawables, DrawableFillColor, DrawableRectangle

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// Everything that happens to a pack after its PBR textures exist: water, glass, manifest,
/// terrain_texture.json, icon, and a bit of housekeeping. Named generically ("PostProcess",
/// not "WaterGlassProcessor") on purpose - this is where any *future* post-generation pass
/// belongs too, not just these five. If a new pass gets added later, it gets a new region
/// in this file and a new call in AlchitexPipeline, not a new file.
/// </summary>
public static class PostProcess
{
    #region Required Assets

    // Every binary asset this file depends on, under the Alchitex module's own Assets
    // folder (passed in as alchitexAssetsPath by every method below - see
    // AlchitexPipeline). None of these can be generated or faked by code; they need to
    // actually be placed here by hand. Every method that needs one checks for it
    // explicitly and logs the exact expected path rather than silently no-op'ing.
    //
    //   Assets/water-fallback.zip
    //     -> four flat files, no folders inside: water_flow_grey.tga + .texture_set.json,
    //        water_still_grey.tga + .texture_set.json.
    //
    //   Assets/badge_42x.png
    //     -> 42x42 watermark composited onto the bottom-left corner of every regenerated pack icon.
    //
    //   Assets/vanilla-rtx-fog.zip
    //     -> top-level "biomes/" and "fogs/" folders, deployed into the pack root and
    //        every subpack root when the (opt-in, off-by-default) fog toggle is enabled.
    private const string WaterFallbackZipFileName = "water-fallback.zip";
    private const string IconBadgeFileName = "badge_42x.png";
    private const string FogZipFileName = "vanilla-rtx-fog.zip";

    #endregion

    #region Water

    // TODO(tuning): visual knobs for water-to-grey conversion.
    // Floor opacity - Bedrock RTX has visible glitches if any water pixel's opacity drops
    // below this after conversion. Ported unchanged from legacy RTX Reactor.
    private const int MinWaterOpacity = 129;
    // How many "votes" the single brightest pixel gets against the plain average when
    // picking ConvertWaterToGrey's flat RGB fill color - higher leans the result more
    // toward the brightest spot in the source texture, 0 would just be a plain average.
    private const double BrightestPixelWeight = 2.0;

    /// <summary>
    /// Converts a color water texture into the flat-grey, brightness-as-opacity form
    /// Bedrock RTX expects (water_flow_grey / water_still_grey), and writes it as a
    /// sibling "_grey"-suffixed TGA next to the original. Opacity math (brightness as
    /// alpha, renormalized to a MinWaterOpacity floor) ported unchanged from legacy RTX
    /// Reactor's ConvertWater; the flat RGB fill is no longer a fixed grey - see below.
    /// </summary>
    public static void ConvertWaterToGrey(string imagePath)
    {
        using var source = Helpers.ReadImage(imagePath, maxOpacity: false);
        using var output = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var brightness = new int[source.Width, source.Height];
        var maxBrightness = 0;
        long brightnessSum = 0;

        using (var srcFb = new FastBitmap(source, writable: false))
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var p = srcFb[x, y];
                    var b = (p.R + p.G + p.B) / 3;
                    brightness[x, y] = b;
                    if (b > maxBrightness) maxBrightness = b;
                    brightnessSum += b;
                }
            }
        }

        var pixelCount = source.Width * source.Height;
        var averageBrightness = pixelCount > 0 ? brightnessSum / (double)pixelCount : 0;
        // Usually-bright result, nudged toward the texture's own brightest pixel rather
        // than a fixed grey.
        var greyChannel = (byte)Math.Clamp(
            (int)Math.Round((maxBrightness * BrightestPixelWeight + averageBrightness) / (BrightestPixelWeight + 1.0)),
            0, 255);

        using (var outFb = new FastBitmap(output, writable: true))
        {
            var minAlpha = 255;
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var b = brightness[x, y];
                    outFb[x, y] = Color.FromArgb(b, greyChannel, greyChannel, greyChannel);
                    if (b < minAlpha) minAlpha = b;
                }
            }

            // Renormalize opacity so the darkest pixel lands at MinWaterOpacity.
            var adjust = MinWaterOpacity - minAlpha;
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var p = outFb[x, y];
                    var newAlpha = Math.Clamp(p.A + adjust, 0, 255);
                    outFb[x, y] = Color.FromArgb(newAlpha, p.R, p.G, p.B);
                }
            }
        }

        var nameNoExt = Path.GetFileNameWithoutExtension(imagePath);
        var directory = Path.GetDirectoryName(imagePath)!;
        // Always .tga regardless of the source's own extension - highest game priority
        // (.tga > .png > .jpg > .jpeg), and avoids ever writing TGA-formatted bytes into a
        // non-.tga-named file when the source was already "_grey"-named but not itself a
        // .tga (e.g. a pack's own water_still_grey.png).
        var greyNameNoExt = nameNoExt.EndsWith("_grey", StringComparison.OrdinalIgnoreCase) ? nameNoExt : nameNoExt + "_grey";
        var greyPath = Path.Combine(directory, greyNameNoExt + ".tga");

        Helpers.WriteImageAsTGA(output, greyPath);
    }

    /// <summary>
    /// Tries to make sure `blocksFolder` ends up with a proper RTX-encoded
    /// water_still_grey.tga and water_flow_grey.tga, per texture independently (not
    /// atomic across the pair - each one can get there from a different source). A pack's
    /// own water_still_grey/water_flow_grey isn't guaranteed to already carry Bedrock
    /// RTX's specific brightness-as-opacity encoding (it might just be the plain in-world
    /// tinting texture from a non-RTX pack) - and even if it does, .tga has to win
    /// priority over whatever extension it shipped as - so it always gets run through
    /// ConvertWaterToGrey, overwriting in place, same as everything else in this pipeline.
    /// Source preference (TextureSetHelper.FindTextureFile - same .tga &gt; .png &gt; .jpg
    /// &gt; .jpeg priority order used everywhere else a texture name gets resolved to a
    /// file): the pack's own "_grey"-named texture if present, otherwise the colored/
    /// inventory variant (packs sometimes ship only that and forget the in-world grey one
    /// the game actually tints per-biome).
    /// Returns true only if this folder ends up with BOTH grey textures present by the
    /// time this returns, regardless of which of the two means produced each one - the
    /// caller (AlchitexPipeline.RunWaterGlassPass) uses this to decide whether the zip
    /// fallback is still needed anywhere in the pack.
    /// </summary>
    public static bool EnsureGreyWaterTextures(string blocksFolder)
    {
        var still = EnsureOneGreyWaterTexture(blocksFolder, "water_still");
        var flow = EnsureOneGreyWaterTexture(blocksFolder, "water_flow");
        return still && flow;
    }

    private static bool EnsureOneGreyWaterTexture(string blocksFolder, string baseName)
    {
        var source = TextureSetHelper.FindTextureFile(blocksFolder, baseName + "_grey")
                  ?? TextureSetHelper.FindTextureFile(blocksFolder, baseName);
        if (source == null) return false;

        try
        {
            ConvertWaterToGrey(source);
            // TextureSetOrchestrator's scan (Phase 2a) already ran and completed before
            // this pass ever creates baseName + "_grey.tga" - if the pack didn't already
            // ship one, nothing would otherwise ever give it a .texture_set.json at all
            // (water_flow_grey/water_still_grey are always PBR-blacklisted anyway, so a
            // color-only one is exactly what TextureSetOrchestrator would have written had
            // it run after this file existed). Deliberately narrow fix scoped to this one
            // gap rather than reordering the pipeline - the zip-fallback path doesn't need
            // this, its .texture_set.json ships inside water-fallback.zip already.
            EnsureColorOnlyTextureSet(blocksFolder, baseName + "_grey");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to derive '{baseName}_grey' from '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes a minimal color-only .texture_set.json ("color": textureName, no
    /// PBR keys - water is always blacklisted) for `textureName` in `blocksFolder`, unless
    /// one already exists (never clobbers a hand-authored/pack-provided file, same
    /// convention as TextureSetOrchestrator).</summary>
    private static void EnsureColorOnlyTextureSet(string blocksFolder, string textureName)
    {
        var jsonPath = Path.Combine(blocksFolder, textureName + ".texture_set.json");
        if (File.Exists(jsonPath)) return;

        var root = new JsonObject
        {
            ["format_version"] = "1.21.30",
            ["minecraft:texture_set"] = new JsonObject { ["color"] = textureName },
        };

        try
        {
            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to write texture set for '{textureName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts Alchitex's packaged water-fallback.zip (four flat files, no folders inside -
    /// both grey TGAs and their .texture_set.json descriptors) directly into `blocksFolder`,
    /// overwriting anything already there. Only called when EnsureGreyWaterTextures couldn't
    /// produce a complete grey water pair for a folder from the pack's own assets, and only
    /// once per pack - see AlchitexPipeline.RunWaterGlassPass. Never invents content - if the
    /// packaged zip isn't present under Assets/, this logs exactly what's missing and leaves
    /// the pack without fallback water rather than pretending to have handled it. Returns
    /// true only on a successful extraction.
    /// </summary>
    public static bool DeployFallbackWaterZip(string blocksFolder, string alchitexAssetsPath)
    {
        var zipPath = Path.Combine(alchitexAssetsPath, WaterFallbackZipFileName);
        if (!File.Exists(zipPath))
        {
            Trace.WriteLine($"[ALCHITEX] Water fallback asset missing - expected '{zipPath}'. Skipping fallback deployment for '{blocksFolder}'.");
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // defensive - zip is flat, no folder entries expected
                entry.ExtractToFile(Path.Combine(blocksFolder, entry.Name), overwrite: true);
            }

            Trace.WriteLine($"[ALCHITEX] Deployed water fallback into '{blocksFolder}'.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to deploy water fallback into '{blocksFolder}': {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Fog

    /// <summary>
    /// Deploys Alchitex's packaged fog asset (biomes_client fog references + fog
    /// definition files) into the pack root and every subpack's own root - not
    /// textures/blocks, since fog files aren't block-texture-scoped the way water
    /// fallback is. Opt-in (AlchitexOptions.AddFog, off by default) and overwrites
    /// whatever's already at each destination path. Never invents content - if the
    /// packaged zip isn't present under Assets/, this logs exactly what's missing and
    /// leaves the pack without fog rather than pretending to have handled it.
    /// </summary>
    public static void DeployFog(string packRoot, string alchitexAssetsPath)
    {
        var zipPath = Path.Combine(alchitexAssetsPath, FogZipFileName);
        if (!File.Exists(zipPath))
        {
            Trace.WriteLine($"[ALCHITEX] Fog asset missing - expected '{zipPath}'. Copy the fog distribution zip (top-level 'biomes/' and 'fogs/' folders) there. Skipping fog deployment for '{packRoot}'.");
            return;
        }

        var targetRoots = new List<string> { packRoot };
        var subpacksDir = Path.Combine(packRoot, "subpacks");
        if (Directory.Exists(subpacksDir))
            targetRoots.AddRange(Directory.GetDirectories(subpacksDir));

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var targetRoot in targetRoots)
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                    var destPath = Path.Combine(targetRoot, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            Trace.WriteLine($"[ALCHITEX] Deployed fog into {targetRoots.Count} location(s) under '{packRoot}'.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to deploy fog into '{packRoot}': {ex.Message}");
        }
    }

    #endregion

    #region Glass

    // TODO(tuning): visual knobs for RTX glass fixups, ported unchanged from legacy RTX
    // Reactor.
    private const byte GlassNearOpaqueThreshold = 248; // pixels this opaque or more are left untouched by ApplyGlassModifier
    private const float GlassSaturationBoost = 0.1f;   // added to saturation so glass reads more vivid under RTX's refraction model
    private const int GlassMidAlpha = 128;              // reference midpoint the opacity-reduction coefficient is derived from
    private const int GlassMinOpacity = 64;             // floor - ApplyGlassModifier's reduced opacity never drops below this
    private const byte BasicGlassOpacityThreshold = 64; // opacity at/below which ApplyBasicGlassModifier whites a pixel out entirely

    /// <summary>
    /// Dispatch for a single color texture: applies the right glass fixup based on its file
    /// name, same rules legacy RTX Reactor applied inline during its main loop. No-op for
    /// anything that isn't glass-like.
    /// </summary>
    public static void ProcessColorTextureIfGlassLike(string imagePath)
    {
        var nameLower = Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();

        if (nameLower.Contains("glass"))
        {
            ApplyGlassModifier(imagePath);
        }

        if (nameLower.Contains("copper_grate") || nameLower.Contains("glass"))
        {
            ApplyBasicGlassModifier(imagePath);
        }
    }

    /// <summary>
    /// Boosts saturation/value and applies a square-falloff opacity reduction curve so
    /// glass reads correctly under RTX's refraction model. Ported unchanged from legacy
    /// GlassModifier.
    /// </summary>
    public static void ApplyGlassModifier(string imagePath)
    {
        using var bitmap = Helpers.ReadImage(imagePath, maxOpacity: false);
        using var fb = new FastBitmap(bitmap, writable: true);

        var coefficient = ((GlassMidAlpha - GlassMinOpacity) * Math.Pow(255, 2)) / GlassMidAlpha;

        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                if (c.A > GlassNearOpaqueThreshold) continue;

                var (hue, sat, val) = RgbToHsv(c.R, c.G, c.B);
                val = 1.0f;
                sat = Math.Min(1.0f, sat + GlassSaturationBoost);
                var boosted = HsvToRgb(hue, sat, val);

                var reduced = Math.Max(c.A - (int)(Math.Pow(c.A / 255.0, 2) * coefficient), GlassMinOpacity);

                fb[x, y] = Color.FromArgb(reduced, boosted.R, boosted.G, boosted.B);
            }
        }

        Helpers.WriteImageAsTGA(bitmap, imagePath);
    }

    /// <summary>
    /// Whites-out (fully transparent white) any pixel with opacity &lt;= BasicGlassOpacityThreshold -
    /// required for regular/tinted glass and copper grate to display correctly under RTX.
    /// Ported unchanged from legacy BasicGlassModifier.
    /// </summary>
    public static void ApplyBasicGlassModifier(string imagePath)
    {
        using var bitmap = Helpers.ReadImage(imagePath, maxOpacity: false);
        using var fb = new FastBitmap(bitmap, writable: true);

        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                if (c.A <= BasicGlassOpacityThreshold)
                    fb[x, y] = Color.FromArgb(0, 255, 255, 255);
            }
        }

        Helpers.WriteImageAsTGA(bitmap, imagePath);
    }

    private static (float h, float s, float v) RgbToHsv(byte r, byte g, byte b)
    {
        float min = Math.Min(r, Math.Min(g, b));
        float max = Math.Max(r, Math.Max(g, b));
        float delta = max - min;

        float h = 0f;
        if (delta > 0)
        {
            if (max == r) h = (g - b) / delta % 6;
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h *= 60;
            if (h < 0) h += 360;
        }

        float s = max > 0 ? delta / max : 0;
        float v = max / 255f;
        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };

        return (
            (byte)Math.Clamp((int)((r + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((g + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((b + m) * 255), 0, 255));
    }

    #endregion

    #region Manifest

    private const string AuthorName = "Cubeir";
    private const string MetadataUrl = "https://github.com/Cubeir/Vanilla-RTX-App";
    private const string NameSuffix = " §r-§a RTX§r";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Real-world manifest.json files - especially hand-edited v1 ones - routinely carry
    // "//" comments or trailing commas even though that's not strictly valid JSON;
    // Bedrock's own reader tolerates both, so ours needs to as well rather than throwing
    // on the first one it meets.
    private static readonly JsonDocumentOptions TolerantReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses `text` into a JsonObject, tolerant of the same non-standard JSON
    /// TolerantReadOptions already covers (comments, trailing commas) AND of duplicate
    /// keys within the same object - a real-world quirk seen in actual pack files despite
    /// being invalid per spec. `JsonNode.Parse` alone can't handle that: its JsonObject
    /// builds a backing dictionary lazily and throws ArgumentException the first time
    /// anything enumerates it (a foreach, e.g.) if a duplicate key turns up, even though
    /// parsing itself "succeeded". This walks a JsonDocument (which tolerates duplicates
    /// natively, no lazy dictionary involved) and rebuilds a fresh JsonObject by indexer
    /// assignment instead, where a later duplicate simply overwrites the earlier one - the
    /// same "last one wins" resolution most JSON consumers, Bedrock's own reader included,
    /// apply in practice. Returns null if the root isn't an object.
    /// </summary>
    private static JsonObject? SafeParseJsonObject(string text)
    {
        using var document = JsonDocument.Parse(text, TolerantReadOptions);
        return RebuildNode(document.RootElement) as JsonObject;
    }

    private static JsonNode? RebuildNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var prop in element.EnumerateObject())
                    obj[prop.Name] = RebuildNode(prop.Value); // later duplicate key overwrites earlier
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    arr.Add(RebuildNode(item));
                return arr;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    /// <summary>
    /// Updates manifest.json in place: new header/module uuids, an RTX tag appended to the
    /// name/description, format_version bumped to at least 2 (capabilities are a v2+
    /// concept - a v3 manifest is left at v3, untouched, same as every other field this
    /// method doesn't specifically care about - subpacks/settings/etc. round-trip as-is),
    /// min_engine_version raised if too low, and the "raytraced" capability + Alchitex
    /// metadata added.
    ///
    /// Every field access below degrades gracefully instead of throwing on a missing or
    /// unexpectedly-shaped value (a quoted "format_version": "2" instead of a number, a
    /// missing/empty "modules" array, a "capabilities" that's some other JSON kind
    /// entirely, etc.) - manifest.json in the wild comes from many different tools across
    /// years of format evolution, so this can't assume any of it is well-formed beyond the
    /// minimum needed to identify header/modules. Anything that can't be salvaged just
    /// skips the update and logs why, same as a missing file - it never leaves a
    /// half-written manifest.json behind (the write only happens once, at the very end,
    /// after every field has already been resolved successfully).
    /// </summary>
    public static void UpdateManifest(string packRoot, string appVersion)
    {
        var manifestPath = Path.Combine(packRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Trace.WriteLine($"[ALCHITEX] No manifest.json found at '{packRoot}' - skipping manifest update.");
            return;
        }

        try
        {
            var root = SafeParseJsonObject(File.ReadAllText(manifestPath));
            if (root == null)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' doesn't parse to a JSON object at its root - skipping manifest update.");
                return;
            }

            if (root["header"] is not JsonObject header)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' has no valid \"header\" object - skipping manifest update.");
                return;
            }

            if (root["modules"] is not JsonArray modulesArray
                || modulesArray.FirstOrDefault(m => m is JsonObject) is not JsonObject module)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' has no valid \"modules\" entry - skipping manifest update.");
                return;
            }

            EnsureFormatVersion(root);
            EnsureMinEngineVersion(header);

            var (resolvedName, resolvedDescription, wasPlaceholder) = ResolvePackName(header, manifestPath);

            header["uuid"] = Guid.NewGuid().ToString();
            module["uuid"] = Guid.NewGuid().ToString();

            var tag = $"RTX Reactor {appVersion}";

            if (wasPlaceholder)
            {
                var description = string.IsNullOrEmpty(resolvedDescription) ? tag : $"{resolvedDescription}\n{tag}";
                header["description"] = description;
                module["description"] = description;
                header["name"] = resolvedName + NameSuffix;
            }
            else
            {
                header["description"] = AppendLine((string?)header["description"], tag);
                module["description"] = AppendLine((string?)module["description"], tag);
                header["name"] = ((string?)header["name"] ?? resolvedName) + NameSuffix;
            }

            EnsureMetadata(root, appVersion);
            EnsureCapability(root, "raytraced");

            File.WriteAllText(manifestPath, root.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to update manifest.json at '{manifestPath}': {ex.Message}");
        }
    }

    /// <summary>Best-effort int coercion for a JSON node that's supposed to be a number
    /// but - in the wild - is sometimes a numeric string instead (e.g.
    /// "format_version": "2"). Returns null if the node is missing, null, or genuinely
    /// not coercible to an int.</summary>
    private static int? TryGetInt(JsonNode? node)
    {
        if (node == null) return null;
        try { return node.GetValue<int>(); }
        catch { return int.TryParse(node.ToString(), out var parsed) ? parsed : null; }
    }

    private static void EnsureFormatVersion(JsonObject root)
    {
        var current = TryGetInt(root["format_version"]);
        if (current is null or < 2)
            root["format_version"] = 2;
    }

    private static void EnsureMinEngineVersion(JsonObject header)
    {
        if (header["min_engine_version"] is not JsonArray arr || arr.Count < 3)
        {
            header["min_engine_version"] = new JsonArray(1, 21, 50);
            return;
        }

        var v0 = TryGetInt(arr[0]);
        var v1 = TryGetInt(arr[1]);
        var v2 = TryGetInt(arr[2]);

        if (v0 is null || v1 is null || v2 is null)
        {
            header["min_engine_version"] = new JsonArray(1, 21, 50);
            return;
        }

        var tooLow = v0 < 1 || (v0 == 1 && v1 < 21) || (v0 == 1 && v1 == 21 && v2 < 40);
        if (tooLow)
            header["min_engine_version"] = new JsonArray(1, 21, 50);
    }

    /// <summary>
    /// If header.name is the literal placeholder "pack.name" (meaning the real name/
    /// description live in a .lang file instead), resolves them from texts/en_US.lang (or
    /// en_GB.lang, or whatever .lang is available) and strips section-sign (§) formatting
    /// codes from the resolved name.
    /// </summary>
    private static (string name, string? description, bool wasPlaceholder) ResolvePackName(JsonObject header, string manifestPath)
    {
        var rawName = (string?)header["name"] ?? string.Empty;

        if (!rawName.Equals("pack.name", StringComparison.OrdinalIgnoreCase))
            return (rawName, null, false);

        var textsFolder = Path.Combine(Path.GetDirectoryName(manifestPath)!, "texts");
        if (!Directory.Exists(textsFolder))
            return (rawName, null, true);

        var langFiles = Directory.GetFiles(textsFolder, "*.lang");
        if (langFiles.Length == 0)
            return (rawName, null, true);

        var englishFile = langFiles.FirstOrDefault(f => f.EndsWith("en_US.lang", StringComparison.OrdinalIgnoreCase))
                        ?? langFiles.FirstOrDefault(f => f.EndsWith("en_GB.lang", StringComparison.OrdinalIgnoreCase))
                        ?? langFiles[0];

        string? name = null;
        string? description = null;

        foreach (var line in File.ReadAllLines(englishFile))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("pack.name=", StringComparison.OrdinalIgnoreCase))
                name = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            else if (trimmed.StartsWith("pack.description=", StringComparison.OrdinalIgnoreCase))
                description = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
        }

        return (StripSectionSigns(name ?? rawName), description, true);
    }

    private static string StripSectionSigns(string input)
    {
        var chars = new List<char>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '§')
            {
                i++; // also skip the formatting character that follows, if any
                continue;
            }
            chars.Add(input[i]);
        }
        return new string(chars.ToArray());
    }

    private static string AppendLine(string? existing, string line)
        => string.IsNullOrEmpty(existing) ? line : $"{existing}\n{line}";

    private static void EnsureMetadata(JsonObject root, string appVersion)
    {
        // `as` rather than `?.AsObject()`/`?.AsArray()` throughout this method - a
        // present-but-wrong-JSON-kind value (e.g. a malformed pack's "metadata": "none")
        // degrades to "treat as missing and replace" instead of throwing.
        if (root["metadata"] is not JsonObject metadata)
        {
            metadata = new JsonObject();
            root["metadata"] = metadata;
        }

        if (metadata["authors"] is not JsonArray authors)
        {
            authors = new JsonArray();
            metadata["authors"] = authors;
        }

        var alreadyCredited = authors.Any(a =>
        {
            try { return string.Equals((string?)a, AuthorName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; } // a non-string entry in a malformed authors array
        });
        if (!alreadyCredited)
        {
            var wasEmpty = authors.Count == 0;
            authors.Insert(0, AuthorName);
            if (wasEmpty)
                authors.Add("Original Authors of Resource Pack");
        }

        if (metadata["generated_with"] is not JsonObject generatedWith)
        {
            generatedWith = new JsonObject();
            metadata["generated_with"] = generatedWith;
        }
        if (!generatedWith.ContainsKey("Alchitex"))
        {
            generatedWith["Alchitex"] = new JsonArray(appVersion);
        }

        metadata["url"] = MetadataUrl;
    }

    private static void EnsureCapability(JsonObject root, string capability)
    {
        // Preserves whatever the pack already declared (e.g. "chemistry") rather than
        // overwriting the whole array, and only adds what's missing. `as`, not `?.AsArray()`
        // - a present-but-wrong-kind "capabilities" degrades to "treat as missing".
        var existing = root["capabilities"] as JsonArray;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (existing != null)
        {
            foreach (var v in existing)
            {
                try { if (v?.GetValue<string>() is string s) set.Add(s); }
                catch { /* a non-string entry in a malformed capabilities array - skip it */ }
            }
        }

        set.Add(capability);

        root["capabilities"] = new JsonArray(set.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
    }

    #endregion

    #region Terrain Texture

    /// <summary>
    /// Forces num_mip_levels/padding to 1, and flattens any "variations" arrays down to a
    /// single texture path (highest-weight if weights are present, otherwise random or
    /// first) - PBR texture sets can't represent per-variation MERS/normal/heightmap, so a
    /// pack with texture variations needs exactly one winner per slot chosen up front.
    ///
    /// Wrapped in its own try/catch and parsed with the same comment/trailing-comma
    /// tolerance as UpdateManifest (a pack's own terrain_texture.json is just as likely to
    /// carry an author's "// note" as its manifest.json is) - this method used to have no
    /// safety net at all, so a malformed file here escaped uncaught all the way to
    /// AlchitexPipeline's outer catch and failed the *entire* pack.
    /// </summary>
    public static void UpdateTerrainTexture(string packRoot, bool removeVariations = true, bool randomize = true)
    {
        var path = Path.Combine(packRoot, "textures", "terrain_texture.json");

        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    root = new JsonObject();
                }
                else if (SafeParseJsonObject(text) is JsonObject parsed)
                {
                    root = parsed;
                }
                else
                {
                    Trace.WriteLine($"[ALCHITEX] '{path}' doesn't parse to a JSON object at its root - treating as empty.");
                    root = new JsonObject();
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                root = new JsonObject();
            }

            var reordered = new JsonObject { ["num_mip_levels"] = 1, ["padding"] = 1 };
            foreach (var kvp in root)
            {
                if (kvp.Key is "num_mip_levels" or "padding") continue;
                reordered[kvp.Key] = kvp.Value?.DeepClone();
            }
            root = reordered;

            if (removeVariations && root["texture_data"] is JsonObject textureData)
            {
                var random = new Random();

                foreach (var entry in textureData)
                {
                    // entry.Value isn't guaranteed to be an object - a malformed entry
                    // (e.g. a bare string instead of {"textures": ...}, or a duplicate
                    // key that collapsed to something unexpected - see SafeParseJsonObject)
                    // is real, and indexing a non-object JsonNode throws. Guard first.
                    var texturesNode = entry.Value is JsonObject entryObj ? entryObj["textures"] : null;
                    if (texturesNode == null) continue;

                    if (texturesNode is JsonObject texturesObj)
                    {
                        if (texturesObj["variations"] is JsonArray variations)
                        {
                            // Couldn't pick anything usable (empty/malformed array) -
                            // leave this entry's textures untouched rather than guessing.
                            if (SelectVariation(variations, randomize, random) is string selected)
                                entry.Value!["textures"] = selected;
                        }
                        else
                        {
                            // Per-face variants (e.g. "up"/"down"/"side" each carrying
                            // their own "variations" array): collapse each face
                            // independently. Real packs mix plain string faces in with
                            // object faces here, so sub.Value being a non-object (and
                            // thus having nothing to flatten) is expected, not an error.
                            foreach (var sub in texturesObj.ToList())
                            {
                                if (sub.Value is JsonObject subObj
                                    && subObj["variations"] is JsonArray nestedVariations
                                    && SelectVariation(nestedVariations, randomize, random) is string nestedSelected)
                                {
                                    texturesObj[sub.Key] = nestedSelected;
                                }
                            }
                        }
                    }
                    else if (texturesNode is JsonArray texturesArray)
                    {
                        for (var i = 0; i < texturesArray.Count; i++)
                        {
                            if (texturesArray[i] is JsonObject item
                                && item["variations"] is JsonArray itemVariations
                                && SelectVariation(itemVariations, randomize, random) is string itemSelected)
                            {
                                texturesArray[i] = itemSelected;
                            }
                        }
                    }
                }
            }

            File.WriteAllText(path, root.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to update terrain_texture.json at '{path}': {ex.Message}");
        }
    }

    /// <summary>Picks one path out of a "variations" array - highest-weight if every
    /// entry carries a (coercible) numeric "weight", otherwise random or first. Returns
    /// null (leaving the caller's entry untouched) rather than throwing if nothing usable
    /// could be picked - a missing/non-string "path", non-numeric "weight", or empty
    /// array are all real things malformed/hand-edited packs do.</summary>
    private static string? SelectVariation(JsonArray variations, bool randomize, Random random)
    {
        if (variations.Count == 0) return null;

        var chosen = variations.All(v => TryGetWeight(v) != null)
            ? variations.OrderByDescending(v => TryGetWeight(v)!.Value).First()
            : randomize ? variations[random.Next(variations.Count)] : variations[0];

        try { return (string?)chosen?["path"]; }
        catch { return null; }
    }

    private static int? TryGetWeight(JsonNode? variation)
    {
        // A "variations" array entry that isn't itself an object (a bare string, say) has
        // no "weight" to speak of - guard before indexing rather than throwing.
        if (variation is not JsonObject obj) return null;
        var node = obj["weight"];
        if (node == null) return null;
        try { return node.GetValue<int>(); }
        catch { return null; }
    }

    #endregion

    #region Housekeeping

    private static readonly HashSet<string> StaleFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "contents.json", "signatures.json", "texture_list.json", "textures_list.json",
    };

    public static void DeleteStaleBookkeepingFiles(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return;

        foreach (var file in Directory.GetFiles(packRoot, "*", SearchOption.AllDirectories))
        {
            if (!StaleFileNames.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); }
            catch (Exception ex) { Trace.WriteLine($"[ALCHITEX] Failed to delete stale file '{file}': {ex.Message}"); }
        }
    }

    #endregion

    #region Pack Icon

    // TODO(tuning): visual knobs for regenerated pack icons.
    private const int IconCanvasSize = 512;
    private const int IconContentSize = 428; // original icon's size once centered on the canvas
    private const string IconGradientBottomColor = "#00488A"; // brand blue - gradient target opposite the icon's own average color
    private const int IconBadgeSize = 42; // must match badge_42x.png's actual pixel dimensions - also drives the accent frame's band width

    /// <summary>
    /// Regenerates pack_icon.png: the original icon centered on a square canvas with a
    /// gradient background (average-icon-color -> brand blue) showing through any
    /// transparent area, a randomized-per-side accent frame, and Alchitex's badge in the
    /// bottom-left corner. Ported from legacy IconDesigner off GDI+ onto Magick.NET's
    /// Composite/Draw API and the `gradient:` pseudo-format.
    /// </summary>
    public static void RegeneratePackIcon(string packRoot, string alchitexAssetsPath)
    {
        var iconPath = Path.Combine(packRoot, "pack_icon.png");
        if (!File.Exists(iconPath))
        {
            Trace.WriteLine($"[ALCHITEX] No pack_icon.png at '{packRoot}' - skipping icon regeneration.");
            return;
        }

        var offset = (IconCanvasSize - IconContentSize) / 2;

        try
        {
            using var original = new MagickImage(iconPath);

            var averageColor = ComputeAverageColor(original);
            var bottomColor = new MagickColor(IconGradientBottomColor);

            using var canvas = new MagickImage(MagickColors.Transparent, IconCanvasSize, IconCanvasSize);

            using (var gradient = new MagickImage($"gradient:{ToHex(averageColor)}-{bottomColor}",
                       new MagickReadSettings { Width = IconCanvasSize, Height = IconCanvasSize }))
            {
                canvas.Composite(gradient, 0, 0, CompositeOperator.Over);
            }

            using (var content = original.Clone())
            {
                content.FilterType = original.Width < IconCanvasSize && original.Height < IconCanvasSize ? FilterType.Point : FilterType.Lanczos;
                content.Resize(new MagickGeometry(IconContentSize, IconContentSize) { IgnoreAspectRatio = true });
                canvas.Composite(content, offset, offset, CompositeOperator.Over);
            }

            DrawAccentFrame(canvas, IconCanvasSize);

            var badgePath = Path.Combine(alchitexAssetsPath, IconBadgeFileName);
            if (File.Exists(badgePath))
            {
                using var badge = new MagickImage(badgePath);
                canvas.Composite(badge, 0, IconCanvasSize - IconBadgeSize, CompositeOperator.Over);
            }
            else
            {
                Trace.WriteLine($"[ALCHITEX] Icon badge asset missing - expected '{badgePath}'. Copy legacy RTX Reactor's src/icons/badge_42x.png (or a new 42x42 asset) there. Regenerated icon will be missing the corner badge until then.");
            }

            canvas.Write(iconPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to regenerate pack icon '{iconPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// This project's Magick.NET package is built for Q16 quantum depth (channel values
    /// are 0-65535, not 0-255 - IMagickColor&lt;ushort&gt; is the actual pixel-color type
    /// here), so raw channel values need the same &gt;&gt; 8 scale-down Helpers.ReadImage
    /// already uses elsewhere before they're usable as standard 0-255 RGB.
    /// new MagickColor(hexString) elsewhere in this region doesn't need this - it already
    /// handles quantum depth internally - only manual pixel-value math like this does.
    /// </summary>
    private static IMagickColor<ushort> ComputeAverageColor(MagickImage source)
    {
        using var tiny = source.Clone();
        tiny.Resize(1, 1);
        using var pixels = tiny.GetPixels();
        var pixel = pixels.GetPixel(0, 0);
        return pixel.ToColor() ?? MagickColors.Gray;
    }

    private static string ToHex(IMagickColor<ushort> c)
    {
        byte r = (byte)(c.R >> 8);
        byte g = (byte)(c.G >> 8);
        byte b = (byte)(c.B >> 8);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static void DrawAccentFrame(MagickImage canvas, int canvasSize)
    {
        var palette = new List<string>
        {
            "#00305B", "#002342", "#1569B2", "#2B9AFF", "#4CABFF",
            "#3BA2FF", "#2081D8", "#00488A", "#00294E", "#003C72",
        };

        var now = DateTime.Now;
        if (now.ToString("dd/MM") == "23/04") palette.AddRange(new[] { "#6900B5", "#000000", "#808080", "#800080", "#FF00FF" });
        if (now.ToString("dd/MM") == "31/10") palette.AddRange(new[] { "#FFA500", "#FF8C00", "#FF4500", "#D2691E" });
        if (now.ToString("MM/dd") is "12/25" or "12/24") palette.AddRange(new[] { "#FF0000", "#008000", "#FFFFFF" });

        var rand = new Random();
        string Pick() => palette[rand.Next(palette.Count)];

        const int band = IconBadgeSize; // frame band width matches the badge's own size, so the badge sits flush in the bottom-left corner
        var edge = canvasSize - band;

        var drawables = new Drawables()
            .FillColor(new MagickColor(Pick())).Rectangle(0, band, band, edge)                 // left
            .FillColor(new MagickColor(Pick())).Rectangle(edge, band, canvasSize, edge)         // right
            .FillColor(new MagickColor(Pick())).Rectangle(band, 0, edge, band)                  // top
            .FillColor(new MagickColor(Pick())).Rectangle(band, edge, edge, canvasSize)          // bottom
            .FillColor(new MagickColor(Pick())).Rectangle(0, edge, band, canvasSize)             // bottom-left
            .FillColor(new MagickColor(Pick())).Rectangle(0, 0, band, band)                      // top-left
            .FillColor(new MagickColor(Pick())).Rectangle(edge, 0, canvasSize, band)             // top-right
            .FillColor(new MagickColor(Pick())).Rectangle(edge, edge, canvasSize, canvasSize);   // bottom-right

        canvas.Draw(drawables);
    }

    #endregion
}
