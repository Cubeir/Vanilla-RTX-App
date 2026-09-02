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

    /// <summary>
    /// Converts a color water texture into the flat-grey, brightness-as-opacity form
    /// Bedrock RTX expects (water_flow_grey / water_still_grey), and writes it as a
    /// sibling "_grey"-suffixed TGA next to the original. Math ported unchanged from
    /// legacy RTX Reactor's ConvertWater.
    /// </summary>
    public static void ConvertWaterToGrey(string imagePath)
    {
        using var source = Helpers.ReadImage(imagePath, maxOpacity: false);
        using var output = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var srcFb = new FastBitmap(source, writable: false))
        using (var outFb = new FastBitmap(output, writable: true))
        {
            var minAlpha = 255;
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var p = srcFb[x, y];
                    var brightness = (p.R + p.G + p.B) / 3;
                    outFb[x, y] = Color.FromArgb(brightness, 164, 164, 164);
                    if (brightness < minAlpha) minAlpha = brightness;
                }
            }

            // Renormalize opacity so the darkest pixel lands at 129, same floor legacy used.
            var adjust = 129 - minAlpha;
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
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to derive '{baseName}_grey' from '{source}': {ex.Message}");
            return false;
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

        var coefficient = ((128 - 64) * Math.Pow(255, 2)) / 128;

        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                if (c.A > 248) continue;

                var (hue, sat, val) = RgbToHsv(c.R, c.G, c.B);
                val = 1.0f;
                sat = Math.Min(1.0f, sat + 0.1f);
                var boosted = HsvToRgb(hue, sat, val);

                var reduced = Math.Max(c.A - (int)(Math.Pow(c.A / 255.0, 2) * coefficient), 64);

                fb[x, y] = Color.FromArgb(reduced, boosted.R, boosted.G, boosted.B);
            }
        }

        Helpers.WriteImageAsTGA(bitmap, imagePath);
    }

    /// <summary>
    /// Whites-out (fully transparent white) any pixel with opacity &lt;= 64 - required for
    /// regular/tinted glass and copper grate to display correctly under RTX. Ported
    /// unchanged from legacy BasicGlassModifier.
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
                if (c.A <= 64)
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
    private const string NameSuffix = " §r-§2 RTX§r";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

            EnsureFormatVersion(root);
            EnsureMinEngineVersion(root);

            var header = root["header"]!.AsObject();
            var module = root["modules"]!.AsArray()[0]!.AsObject();

            var (resolvedName, resolvedDescription, wasPlaceholder) = ResolvePackName(header, manifestPath);

            header["uuid"] = Guid.NewGuid().ToString();
            module["uuid"] = Guid.NewGuid().ToString();

            var tag = $"Alchitex / Vanilla RTX App {appVersion}";

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

    private static void EnsureFormatVersion(JsonObject root)
    {
        var current = root["format_version"]?.GetValue<int>();
        if (current is null or < 2)
            root["format_version"] = 2;
    }

    private static void EnsureMinEngineVersion(JsonObject root)
    {
        var header = root["header"]!.AsObject();
        var fallback = new JsonArray(1, 21, 50);

        try
        {
            var arr = header["min_engine_version"]?.AsArray();
            if (arr == null || arr.Count < 3)
            {
                header["min_engine_version"] = fallback;
                return;
            }

            var v0 = arr[0]!.GetValue<int>();
            var v1 = arr[1]!.GetValue<int>();
            var v2 = arr[2]!.GetValue<int>();

            var tooLow = v0 < 1 || (v0 == 1 && v1 < 21) || (v0 == 1 && v1 == 21 && v2 < 40);
            if (tooLow)
                header["min_engine_version"] = new JsonArray(1, 21, 50);
        }
        catch
        {
            header["min_engine_version"] = fallback;
        }
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
        var metadata = root["metadata"]?.AsObject();
        if (metadata == null)
        {
            metadata = new JsonObject();
            root["metadata"] = metadata;
        }

        var authors = metadata["authors"]?.AsArray();
        if (authors == null)
        {
            authors = new JsonArray();
            metadata["authors"] = authors;
        }

        var alreadyCredited = authors.Any(a => string.Equals((string?)a, AuthorName, StringComparison.OrdinalIgnoreCase));
        if (!alreadyCredited)
        {
            var wasEmpty = authors.Count == 0;
            authors.Insert(0, AuthorName);
            if (wasEmpty)
                authors.Add("Original Authors of Resource Pack");
        }

        var generatedWith = metadata["generated_with"]?.AsObject();
        if (generatedWith == null)
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
        // overwriting the whole array, and only adds what's missing.
        var existing = root["capabilities"]?.AsArray();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (existing != null)
            foreach (var v in existing)
                if (v?.GetValue<string>() is string s) set.Add(s);

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
    /// </summary>
    public static void UpdateTerrainTexture(string packRoot, bool removeVariations = true, bool randomize = true)
    {
        var path = Path.Combine(packRoot, "textures", "terrain_texture.json");

        JsonObject root;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
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

        if (removeVariations && root["texture_data"]?.AsObject() is JsonObject textureData)
        {
            var random = new Random();

            foreach (var entry in textureData)
            {
                var texturesNode = entry.Value?["textures"];
                if (texturesNode == null) continue;

                if (texturesNode is JsonObject texturesObj)
                {
                    if (texturesObj["variations"] is JsonArray variations)
                    {
                        entry.Value!["textures"] = SelectVariation(variations, randomize, random);
                    }
                    else
                    {
                        // Per-face variants (e.g. "up"/"down"/"side" each carrying their
                        // own "variations" array): collapse each face independently.
                        foreach (var sub in texturesObj.ToList())
                        {
                            if (sub.Value?["variations"] is JsonArray nestedVariations)
                                texturesObj[sub.Key] = SelectVariation(nestedVariations, randomize, random);
                        }
                    }
                }
                else if (texturesNode is JsonArray texturesArray)
                {
                    for (var i = 0; i < texturesArray.Count; i++)
                    {
                        if (texturesArray[i] is JsonObject item && item["variations"] is JsonArray itemVariations)
                        {
                            texturesArray[i] = SelectVariation(itemVariations, randomize, random);
                        }
                    }
                }
            }
        }

        File.WriteAllText(path, root.ToJsonString(WriteOptions));
    }

    private static string SelectVariation(JsonArray variations, bool randomize, Random random)
    {
        if (variations.All(v => v?["weight"] != null))
        {
            var best = variations.OrderByDescending(v => v!["weight"]!.GetValue<int>()).First();
            return (string)best!["path"]!;
        }

        if (randomize)
            return (string)variations[random.Next(variations.Count)]!["path"]!;

        return (string)variations[0]!["path"]!;
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

    /// <summary>
    /// Regenerates pack_icon.png: the original icon centered on a 512x512 canvas with a
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

        const int canvasSize = 512;
        const int contentSize = 428;
        var offset = (canvasSize - contentSize) / 2;

        try
        {
            using var original = new MagickImage(iconPath);

            var averageColor = ComputeAverageColor(original);
            var bottomColor = new MagickColor("#00488A");

            using var canvas = new MagickImage(MagickColors.Transparent, canvasSize, canvasSize);

            using (var gradient = new MagickImage($"gradient:{ToHex(averageColor)}-{bottomColor}",
                       new MagickReadSettings { Width = canvasSize, Height = canvasSize }))
            {
                canvas.Composite(gradient, 0, 0, CompositeOperator.Over);
            }

            using (var content = original.Clone())
            {
                content.FilterType = original.Width < 512 && original.Height < 512 ? FilterType.Point : FilterType.Lanczos;
                content.Resize(new MagickGeometry(contentSize, contentSize) { IgnoreAspectRatio = true });
                canvas.Composite(content, offset, offset, CompositeOperator.Over);
            }

            DrawAccentFrame(canvas, canvasSize);

            var badgePath = Path.Combine(alchitexAssetsPath, IconBadgeFileName);
            if (File.Exists(badgePath))
            {
                using var badge = new MagickImage(badgePath);
                canvas.Composite(badge, 0, canvasSize - 42, CompositeOperator.Over);
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

        const int band = 42;
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
