using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;
using static Vanilla_RTX_App.Modules.Helpers;
using static Vanilla_RTX_App.Modules.ProcessorVariables;
using System.Diagnostics;

namespace Vanilla_RTX_App.Modules;

// ──────────────────────────────────────────────────────────────────────────────
// Module-level constants
// ──────────────────────────────────────────────────────────────────────────────
public static class ProcessorVariables
{
    public const bool FOG_UNIFORM_HEIGHT = false;
    public const double EMISSIVE_EXCESS_INTENSITY_DAMPEN = 0.1;
}

// ══════════════════════════════════════════════════════════════════════════════
//  TextureSetHelper  ──  parsing, resolution, and virtual-bitmap creation
// ══════════════════════════════════════════════════════════════════════════════

public static class TextureSetHelper
{
    // ── Public enums & value types ─────────────────────────────────────────────

    public enum TextureKind { Color, Mer, Normal, Heightmap }

    /// <summary>
    /// Discriminated union: either a real file path or an inline colour value.
    /// </summary>
    public sealed class TextureLayerValue
    {
        // ---- real file ----
        public string? FilePath { get; }

        // ---- inline ----
        public bool IsInline { get; }
        /// <summary>Parsed RGBA components (0-255). Always length 4 even for RGB/hex input.</summary>
        public byte[] InlineRgba { get; } = Array.Empty<byte>();
        /// <summary>Number of components that were originally specified (3 or 4).</summary>
        public int InlineChannels { get; }
        /// <summary>True when the source was a hex string (e.g. "#B48CBE").</summary>
        public bool IsHex { get; }
        /// <summary>The raw token that produced this inline value (kept for write-back).</summary>
        public JToken SourceToken { get; }

        // Constructors ----------------------------------------------------------

        private TextureLayerValue(JToken sourceToken, byte[] rgba, int channels, bool isHex)
        {
            IsInline = true;
            SourceToken = sourceToken;
            InlineRgba = rgba;
            InlineChannels = channels;
            IsHex = isHex;
        }

        private TextureLayerValue(string filePath)
        {
            FilePath = filePath;
            SourceToken = JValue.CreateNull(); // unused
        }

        // Factory helpers -------------------------------------------------------

        public static TextureLayerValue FromFile(string path)
            => new(path);

        public static TextureLayerValue? TryParseInline(JToken token)
        {
            // Hex string
            if (token.Type == JTokenType.String)
            {
                var s = token.Value<string>()!.Trim();
                if (s.StartsWith('#') && TryParseHex(s, out var rgba))
                    return new TextureLayerValue(token, rgba, rgba.Length, isHex: true);
                return null; // plain string → treat as filename elsewhere
            }

            // Array of numbers
            if (token is JArray arr && arr.Count is 3 or 4)
            {
                var comps = new byte[arr.Count];
                for (var i = 0; i < arr.Count; i++)
                {
                    if (!TryGetByte(arr[i], out comps[i]))
                        return null;
                }
                // Pad to 4 channels
                var rgba = comps.Length == 4
                    ? comps
                    : new[] { comps[0], comps[1], comps[2], (byte)255 };
                return new TextureLayerValue(token, rgba, comps.Length, isHex: false);
            }

            return null;
        }

        // ---- helpers ----------------------------------------------------------

        private static bool TryParseHex(string hex, out byte[] rgba)
        {
            rgba = Array.Empty<byte>();
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
                rgba = new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255 };
                return true;
            }
            if (hex.Length == 8)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
                rgba = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                return true;
            }
            return false;
        }

        private static bool TryGetByte(JToken t, out byte b)
        {
            b = 0;
            double d;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                d = t.Value<double>();
            else if (t.Type == JTokenType.String && double.TryParse(t.Value<string>(), out d))
            { /* ok */ }
            else return false;

            b = (byte)Math.Clamp((int)Math.Round(d), 0, 255);
            return true;
        }

        /// <summary>Creates a 1×1 virtual Bitmap from the inline colour value.</summary>
        public Bitmap ToVirtualBitmap()
        {
            var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(InlineRgba[3], InlineRgba[0], InlineRgba[1], InlineRgba[2]));
            return bmp;
        }

        /// <summary>
        /// Serialises the (possibly modified) 1×1 bitmap back to the JToken
        /// format that was originally used (hex / 3-channel array / 4-channel array).
        /// </summary>
        public JToken SerializeVirtual(Bitmap bmp)
        {
            var c = bmp.GetPixel(0, 0);
            byte r = c.R, g = c.G, b = c.B, a = c.A;

            if (IsHex)
            {
                // Reproduce original hex length (6 vs 8 chars)
                return InlineChannels == 4
                    ? new JValue($"#{r:X2}{g:X2}{b:X2}{a:X2}")
                    : new JValue($"#{r:X2}{g:X2}{b:X2}");
            }

            // Array form – always write as ints, 4-channel (RGBA) or 3-channel (RGB)
            if (InlineChannels == 4)
                return new JArray(r, g, b, a);

            return new JArray(r, g, b);
        }
    }

    // ── Resolved / loaded texture set records ────────────────────────────────

    /// <summary>
    /// One validated texture set parsed from a .texture_set.json.
    /// Null secondary means that layer was absent.
    /// </summary>
    public sealed class ResolvedTextureSet
    {
        public string JsonFilePath { get; init; } = "";
        public JObject RootJson { get; init; } = new();
        public JObject SetNode { get; init; } = new();

        public TextureLayerValue Color { get; init; } = null!;
        public TextureLayerValue? Mer { get; init; }
        /// <summary>Either a normal map or a heightmap – check IsHeightmap.</summary>
        public TextureLayerValue? NormalOrHeight { get; init; }
        public bool IsHeightmap { get; init; }
    }

    /// <summary>
    /// In-memory form of a texture set: real Bitmaps ready for processing.
    /// Virtual bitmaps (from inline values) are flagged as such.
    /// </summary>
    public sealed class LoadedTextureSet
    {
        public ResolvedTextureSet Resolved { get; init; } = null!;

        public Bitmap ColorBmp { get; set; } = null!;
        public bool ColorIsVirtual { get; init; }

        public Bitmap? MerBmp { get; set; }
        public bool MerIsVirtual { get; init; }

        public Bitmap? NormalBmp { get; set; }
        public bool NormalIsVirtual { get; init; }

        // Dirty flags set by processors
        public bool ColorDirty { get; set; }
        public bool MerDirty { get; set; }
        public bool NormalDirty { get; set; }
    }

    // ── File-extension priority (matches Minecraft spec) ─────────────────────

    private static readonly string[] SupportedExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans a pack root, parses all .texture_set.json files, validates them
    /// according to the Minecraft spec, and returns the valid resolved sets.
    /// Invalid sets are silently skipped (matching game behaviour).
    /// </summary>
    public static IReadOnlyList<ResolvedTextureSet> ResolveTextureSets(string packRoot)
    {
        if (string.IsNullOrEmpty(packRoot) || !Directory.Exists(packRoot))
            return Array.Empty<ResolvedTextureSet>();

        var results = new List<ResolvedTextureSet>();

        foreach (var jsonFile in Directory.GetFiles(packRoot, "*.texture_set.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(jsonFile);
                var root = JObject.Parse(text);
                if (root.SelectToken("minecraft:texture_set") is not JObject set)
                    continue;

                var folder = Path.GetDirectoryName(jsonFile)!;

                // ---- Color (required) ----------------------------------------
                var colorToken = set["color"];
                if (colorToken == null)
                    continue; // invalid – no color layer

                var colorLayer = ResolveLayer(folder, colorToken);
                if (colorLayer == null)
                    continue; // referenced file missing

                // ---- MER / MERS (mutually exclusive) --------------------------
                var merToken = set["metalness_emissive_roughness"];
                var mersToken = set["metalness_emissive_roughness_subsurface"];

                if (merToken != null && mersToken != null)
                    continue; // invalid – both present

                var merLayer = ResolveLayer(folder, merToken ?? mersToken);

                // ---- Normal / Heightmap (mutually exclusive) ------------------
                var normalToken = set["normal"];
                var heightmapToken = set["heightmap"];

                if (normalToken != null && heightmapToken != null)
                    continue; // invalid – both present

                var normalLayer = ResolveLayer(folder, normalToken);
                var heightmapLayer = ResolveLayer(folder, heightmapToken);
                var isHeightmap = heightmapToken != null;

                var normalOrHeight = normalLayer ?? heightmapLayer;

                results.Add(new ResolvedTextureSet
                {
                    JsonFilePath = jsonFile,
                    RootJson = root,
                    SetNode = set,
                    Color = colorLayer,
                    Mer = merLayer,
                    NormalOrHeight = normalOrHeight,
                    IsHeightmap = isHeightmap,
                });
            }
            catch
            {
                // Malformed JSON or IO error → skip
            }
        }

        return results;
    }

    /// <summary>
    /// Loads all bitmaps for a list of resolved texture sets.
    /// Virtual (inline) colours become 1×1 bitmaps and are flagged accordingly.
    /// Sets whose color bitmap cannot be loaded are skipped.
    /// </summary>
    public static IReadOnlyList<LoadedTextureSet> LoadTextureSets(IReadOnlyList<ResolvedTextureSet> resolved)
    {
        var results = new List<LoadedTextureSet>(resolved.Count);

        foreach (var rs in resolved)
        {
            try
            {
                var colorBmp = LoadLayer(rs.Color);
                if (colorBmp == null) continue;

                var merBmp = rs.Mer != null ? LoadLayer(rs.Mer) : null;
                var normalBmp = rs.NormalOrHeight != null ? LoadLayer(rs.NormalOrHeight!) : null;

                results.Add(new LoadedTextureSet
                {
                    Resolved = rs,
                    ColorBmp = colorBmp,
                    ColorIsVirtual = rs.Color.IsInline,
                    MerBmp = merBmp,
                    MerIsVirtual = rs.Mer?.IsInline ?? false,
                    NormalBmp = normalBmp,
                    NormalIsVirtual = rs.NormalOrHeight?.IsInline ?? false,
                });
            }
            catch
            {
                // Failed to load → skip
            }
        }

        return results;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a JToken from a texture set layer field.
    /// Returns null if the token is absent, or a referenced file doesn't exist.
    /// </summary>
    private static TextureLayerValue? ResolveLayer(string folder, JToken? token)
    {
        if (token == null) return null;

        // Try inline first (array or hex string)
        var inline = TextureLayerValue.TryParseInline(token);
        if (inline != null) return inline;

        // Must be a plain string (filename)
        if (token.Type != JTokenType.String) return null;

        var name = token.Value<string>()!.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var filePath = FindTextureFile(folder, name);
        return filePath != null ? TextureLayerValue.FromFile(filePath) : null;
    }

    private static Bitmap? LoadLayer(TextureLayerValue layer)
    {
        if (layer.IsInline)
            return layer.ToVirtualBitmap();

        if (!File.Exists(layer.FilePath!))
            return null;

        return ReadImage(layer.FilePath!, false);
    }

    /// <summary>
    /// Finds a texture file using Minecraft priority order: .tga > .png > .jpg > .jpeg.
    /// Case-insensitive where the filesystem may be case-sensitive.
    /// </summary>
    public static string? FindTextureFile(string folder, string textureName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var target = Path.Combine(folder, textureName + ext);
            if (File.Exists(target))
                return target;

            // Case-insensitive fallback
            try
            {
                var matches = Directory.GetFiles(folder, textureName + ext, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0) return matches[0];
            }
            catch { /* access denied etc. */ }
        }

        return null;
    }

    // ── Write-back helper ────────────────────────────────────────────────────

    /// <summary>
    /// Persists a loaded texture set's dirty bitmaps back to disk (or inline JSON).
    /// For real files: writes in the source format (TGA → TGA, PNG → PNG, etc.).
    /// For virtual bitmaps: patches the .texture_set.json in place.
    /// </summary>
    public static void SaveDirtyLayers(LoadedTextureSet lts)
    {
        var rs = lts.Resolved;
        var jsonDirty = false;

        // ---- Color -----------------------------------------------------------
        if (lts.ColorDirty && lts.ColorBmp != null)
        {
            if (lts.ColorIsVirtual)
            {
                rs.SetNode["color"] = rs.Color.SerializeVirtual(lts.ColorBmp);
                jsonDirty = true;
            }
            else
            {
                WriteBackBitmap(lts.ColorBmp, rs.Color.FilePath!);
            }
        }

        // ---- MER -------------------------------------------------------------
        if (lts.MerDirty && lts.MerBmp != null && rs.Mer != null)
        {
            if (lts.MerIsVirtual)
            {
                // Preserve which key was used (mer vs mers)
                var merKey = rs.SetNode["metalness_emissive_roughness"] != null
                    ? "metalness_emissive_roughness"
                    : "metalness_emissive_roughness_subsurface";
                rs.SetNode[merKey] = rs.Mer.SerializeVirtual(lts.MerBmp);
                jsonDirty = true;
            }
            else
            {
                WriteBackBitmap(lts.MerBmp, rs.Mer.FilePath!);
            }
        }

        // ---- Normal / Heightmap ----------------------------------------------
        if (lts.NormalDirty && lts.NormalBmp != null && rs.NormalOrHeight != null)
        {
            if (lts.NormalIsVirtual)
            {
                var normalKey = rs.IsHeightmap ? "heightmap" : "normal";
                rs.SetNode[normalKey] = rs.NormalOrHeight.SerializeVirtual(lts.NormalBmp);
                jsonDirty = true;
            }
            else
            {
                WriteBackBitmap(lts.NormalBmp, rs.NormalOrHeight.FilePath!);
            }
        }

        // ---- Flush JSON if any inline layer changed --------------------------
        if (jsonDirty)
        {
            File.WriteAllText(rs.JsonFilePath, rs.RootJson.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }

    /// <summary>
    /// Writes a bitmap back to disk preserving the original file format.
    /// TGA  → TGA via existing WriteImageAsTGA helper.
    /// PNG  → lossless PNG (32-bpp ARGB).
    /// JPG  → high-quality JPEG (the best we can do for a lossy source).
    /// Other formats fall back to TGA.
    /// </summary>
    private static void WriteBackBitmap(Bitmap bmp, string originalPath)
    {
        var ext = Path.GetExtension(originalPath).ToLowerInvariant();

        switch (ext)
        {
            case ".tga":
                WriteImageAsTGA(bmp, originalPath);
                break;

            case ".png":
                // Ensure we save as 32-bpp ARGB PNG (lossless, straight alpha)
                using (var canonical = EnsureArgb32(bmp))
                    canonical.Save(originalPath, ImageFormat.Png);
                break;

            case ".jpg":
            case ".jpeg":
                // JPEG is lossy; use maximum quality to minimise round-trip loss
                var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                if (jpegEncoder == null) goto default;

                var qualityParam = new EncoderParameters(1);
                qualityParam.Param[0] = new EncoderParameter(Encoder.Quality, 100L);

                using (var canonical = EnsureArgb32(bmp))
                    canonical.Save(originalPath, jpegEncoder, qualityParam);
                break;

            default:
                // Unknown format – fall back to TGA (game reads it with highest priority anyway)
                WriteImageAsTGA(bmp, originalPath);
                break;
        }
    }

    /// <summary>
    /// Returns the bitmap as a 32-bpp ARGB image (no-op if already correct).
    /// Caller is responsible for disposing if a new bitmap was created.
    /// </summary>
    private static Bitmap EnsureArgb32(Bitmap src)
    {
        if (src.PixelFormat == PixelFormat.Format32bppArgb)
            return src;

        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        return dst;
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.FormatID == format.Guid)
                return codec;
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Processor  ──  the "big daddy" orchestrator + all sub-processors
// ══════════════════════════════════════════════════════════════════════════════

public class Processor
{
    // ── Pack info (unchanged) ────────────────────────────────────────────────

    private struct PackInfo
    {
        public string Name;
        public string Path;
        public bool Enabled;

        public PackInfo(string name, string path, bool enabled)
        {
            Name = name;
            Path = path;
            Enabled = enabled;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  TuneSelectedPacks  ──  entry point
    // ══════════════════════════════════════════════════════════════════════════

    public static void TuneSelectedPacks()
    {
        // ── Build & deduplicate pack list (same logic as before) ──────────────

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar,
                         System.IO.Path.AltDirectorySeparatorChar);
        }

        var packList = new List<PackInfo>
        {
            new("Vanilla RTX",         VanillaRTXLocation,        IsVanillaRTXEnabled),
            new("Vanilla RTX Normals", VanillaRTXNormalsLocation, IsNormalsEnabled),
            new("Vanilla RTX Opus",    VanillaRTXOpusLocation,    IsOpusEnabled),
        };

        foreach (var (location, name, type) in TunerVariables.SelectedPacks)
        {
            if (type == "Incompatible") continue;
            packList.Add(new PackInfo(name, location, !string.IsNullOrEmpty(location)));
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedupedList = new List<PackInfo>();

        foreach (var pack in packList)
        {
            if (!pack.Enabled) continue;

            var normalised = NormalizePath(pack.Path);
            if (string.IsNullOrEmpty(normalised)) continue;

            if (seenPaths.Contains(normalised))
            {
                MainWindow.Log(
                    $"{pack.Name} was selected twice, but will only be processed once!",
                    MainWindow.LogLevel.Warning);
            }
            else
            {
                seenPaths.Add(normalised);
                dedupedList.Add(pack);
            }
        }

        var packs = dedupedList.ToArray();

        var packNames = string.Join(", ", packs.Select(p => p.Name));
        MainWindow.Log($"Tuning {packNames}...", MainWindow.LogLevel.Lengthy);

        // ── Determine which processors are active ─────────────────────────────

        bool doFog = FogMultiplier != Defaults.FogMultiplier;
        bool doEmissivity = EmissivityMultiplier != Defaults.EmissivityMultiplier
                         || AddEmissivityAmbientLight != Defaults.AddEmissivityAmbientLight;
        bool doLazify = LazifyNormalAlpha != Defaults.LazifyNormalAlpha;
        bool doNormalInt = NormalIntensity != Defaults.NormalIntensity;
        bool doRoughness = RoughnessControlValue != Defaults.RoughnessControlValue;
        bool doGrain = MaterialNoiseOffset != Defaults.MaterialNoiseOffset;

        // ── Per-pack processing ───────────────────────────────────────────────

        foreach (var pack in packs)
        {
            // ---- Fog pass (fully independent, unchanged logic) ---------------
            if (doFog)
            {
                ProcessFog(pack);
                ProcessFog(pack, processWaterOnly: true);
            }

            // ---- Bail early if no texture processing is needed ---------------
            if (!doEmissivity && !doLazify && !doNormalInt && !doRoughness && !doGrain)
                continue;

            if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
                continue;

            // ---- Resolve ALL texture sets for this pack ONCE -----------------
            var resolved = TextureSetHelper.ResolveTextureSets(pack.Path);
            if (resolved.Count == 0) continue;

            var loaded = TextureSetHelper.LoadTextureSets(resolved);
            if (loaded.Count == 0) continue;

            // ---- Feed each loaded set through the active sub-processors ------
            //
            //  Sub-processor needs:
            //    Emissivity   → MER bitmap
            //    NormalInt    → Normal/Heightmap bitmap  (+ IsHeightmap flag)
            //    Lazify       → Color bitmap + Normal/Heightmap bitmap
            //    Roughness    → MER bitmap
            //    MaterialGrain→ MER bitmap  (groups files by base name; handled below)
            //
            // Material grain is the only one that requires cross-set coordination
            // (noise-pattern sharing between variant files).  We pre-group by base
            // filename and pass the shared cache in.

            var grainCache = new Dictionary<string, (int[,] red, int[,] green, int[,] blue, int[,] checker)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var lts in loaded)
            {
                if (doEmissivity && lts.MerBmp != null)
                    lts.MerDirty |= ApplyEmissivity(lts.MerBmp);

                if (doNormalInt && lts.NormalBmp != null)
                    lts.NormalDirty |= ApplyNormalIntensity(lts.NormalBmp, lts.Resolved.IsHeightmap);

                if (doLazify && lts.ColorBmp != null && lts.NormalBmp != null)
                    lts.NormalDirty |= ApplyLazify(lts.ColorBmp, lts.NormalBmp, lts.Resolved.IsHeightmap);

                if (doRoughness && lts.MerBmp != null)
                    lts.MerDirty |= ApplyRoughness(lts.MerBmp);

                if (doGrain && lts.MerBmp != null)
                    lts.MerDirty |= ApplyMaterialGrain(lts.MerBmp, lts.Resolved.Mer?.FilePath, grainCache);
            }

            // ---- Write back every dirty layer --------------------------------
            foreach (var lts in loaded)
            {
                if (lts.ColorDirty || lts.MerDirty || lts.NormalDirty)
                    TextureSetHelper.SaveDirtyLayers(lts);

                // Dispose bitmaps
                lts.ColorBmp?.Dispose();
                lts.MerBmp?.Dispose();
                lts.NormalBmp?.Dispose();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Fog processor  ──  standalone, unchanged logic
    // ══════════════════════════════════════════════════════════════════════════

    private static void ProcessFog(PackInfo pack, bool processWaterOnly = false)
    {
        const double MIN_VALUE_THRESHOLD = 0.00000001;
        const int DECIMAL_PRECISION = 6;

        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        var fogDirectories = Directory
            .GetDirectories(pack.Path, "*", SearchOption.AllDirectories)
            .Where(d => string.Equals(Path.GetFileName(d), "fogs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!fogDirectories.Any() && !processWaterOnly)
        {
            MainWindow.Log($"{pack.Name}: does not contain any fog files.", MainWindow.LogLevel.Informational);
            return;
        }

        var files = fogDirectories
            .SelectMany(dir =>
            {
                try { return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly); }
                catch { return Enumerable.Empty<string>(); }
            })
            .ToList();

        if (!files.Any())
            return;

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var root = JObject.Parse(text);

                var volumetric = root.SelectToken("minecraft:fog_settings.volumetric") as JObject;
                if (volumetric == null) continue;

                var modified = processWaterOnly
                    ? ProcessWaterCoefficients(volumetric)
                    : ProcessAirDensityAndScattering(volumetric);

                if (modified)
                {
                    var jsonString = root.ToString(Newtonsoft.Json.Formatting.Indented);
                    jsonString = RemoveScientificNotation(jsonString);
                    File.WriteAllText(file, jsonString);
                }
            }
            catch { /* per-file errors silently skipped */ }
        }

        // ── Air density & scattering ──────────────────────────────────────────
        bool ProcessAirDensityAndScattering(JObject volumetric)
        {
            var modified = false;
            var density = volumetric.SelectToken("density") as JObject;
            if (density == null) return false;

            var airSection = density.SelectToken("air") as JObject;
            var weatherSection = density.SelectToken("weather") as JObject;

            var densityValues = new List<(string name, JObject section, double original, double multiplied)>();
            var allDensities = new List<double>();

            double airDensityFinal = 0.0;
            double weatherDensityFinal = 0.0;

            if (airSection != null && TryGetNumericValue(airSection.SelectToken("max_density"), out var airDensity))
            {
                allDensities.Add(airDensity);
                if (Math.Abs(airDensity) < 0.0001)
                {
                    airDensityFinal = CalculateNewDensity(airDensity, FogMultiplier);
                    if (Math.Abs(airDensityFinal - airDensity) >= MIN_VALUE_THRESHOLD)
                    {
                        airSection["max_density"] = ClampAndRound(airDensityFinal);
                        modified = true;
                    }
                }
                else
                {
                    var multiplied = airDensity * FogMultiplier;
                    densityValues.Add(("air", airSection, airDensity, multiplied));
                    airDensityFinal = multiplied;
                }
            }

            if (weatherSection != null && TryGetNumericValue(weatherSection.SelectToken("max_density"), out var weatherDensity))
            {
                allDensities.Add(weatherDensity);
                if (Math.Abs(weatherDensity) < 0.0001)
                {
                    weatherDensityFinal = CalculateNewDensity(weatherDensity, FogMultiplier);
                    if (Math.Abs(weatherDensityFinal - weatherDensity) >= MIN_VALUE_THRESHOLD)
                    {
                        weatherSection["max_density"] = ClampAndRound(weatherDensityFinal);
                        modified = true;
                    }
                }
                else
                {
                    var multiplied = weatherDensity * FogMultiplier;
                    densityValues.Add(("weather", weatherSection, weatherDensity, multiplied));
                    weatherDensityFinal = multiplied;
                }
            }

            if (densityValues.Any())
            {
                var maxMultiplied = densityValues.Max(x => x.multiplied);
                var scaleFactor = maxMultiplied > 1.0 ? 1.0 / maxMultiplied : 1.0;

                foreach (var (name, section, _, multiplied) in densityValues)
                {
                    var finalValue = multiplied * scaleFactor;
                    section["max_density"] = ClampAndRound(finalValue);
                    modified = true;

                    if (name == "air") airDensityFinal = finalValue;
                    else if (name == "weather") weatherDensityFinal = finalValue;
                }
            }

            var finalDensities = new List<double>();
            if (airDensityFinal > 0) finalDensities.Add(Math.Min(airDensityFinal, 1.0));
            if (weatherDensityFinal > 0) finalDensities.Add(Math.Min(weatherDensityFinal, 1.0));

            var avgDensity = finalDensities.Any() ? finalDensities.Average() :
                                 (allDensities.Any() ? allDensities.Average() : 0.0);
            var proximityToMax = Math.Min(avgDensity, 1.0);

            if (proximityToMax > 0.0)
            {
                var overage = FogMultiplier - 1.0;
                var dampenedOverage = overage * 0.25 * proximityToMax;
                var scatteringMultiplier = 1.0 + dampenedOverage;

                var airCoefficients = volumetric.SelectToken("media_coefficients.air") as JObject;
                var scatteringArray = airCoefficients?.SelectToken("scattering") as JArray;

                if (scatteringArray != null && scatteringArray.Count >= 3)
                    modified |= ProcessRgbArray(scatteringArray, scatteringMultiplier);
            }

            modified |= MakeDensityUniform(airSection);
            modified |= MakeDensityUniform(weatherSection);

            return modified;
        }

        // ── Water coefficients ────────────────────────────────────────────────
        bool ProcessWaterCoefficients(JObject volumetric)
        {
            var modified = false;

            var airDensity = GetDensityValue(volumetric, "density.air.max_density");
            var weatherDensity = GetDensityValue(volumetric, "density.weather.max_density");

            var densities = new[] { airDensity, weatherDensity }
                .Where(d => d > 0)
                .Select(d => Math.Min(d, 1.0))
                .ToList();

            var avgDensity = densities.Any() ? densities.Average() : 0.5;
            var proximityToMin = 1.0 - avgDensity;
            var overage = FogMultiplier - 1.0;
            var dampenedOverage = overage * 0.1 * Math.Max(proximityToMin, 0.25);
            var waterMultiplier = 1.0 + dampenedOverage;

            var waterCoefficients = volumetric.SelectToken("media_coefficients.water") as JObject;
            if (waterCoefficients == null) return false;

            var scatteringArray = waterCoefficients.SelectToken("scattering") as JArray;
            if (scatteringArray != null && scatteringArray.Count >= 3)
                modified |= ProcessRgbArray(scatteringArray, waterMultiplier);

            var absorptionArray = waterCoefficients.SelectToken("absorption") as JArray;
            if (absorptionArray != null && absorptionArray.Count >= 3)
                modified |= ProcessRgbArray(absorptionArray, waterMultiplier);

            return modified;
        }

        // ── Fog helpers ───────────────────────────────────────────────────────

        bool ProcessRgbArray(JArray rgbArray, double multiplier)
        {
            var rgbValues = new double[3];
            for (var i = 0; i < 3; i++)
            {
                if (!TryGetNumericValue(rgbArray[i], out rgbValues[i])) return false;
                rgbValues[i] *= multiplier;
            }

            var maxRgb = rgbValues.Max();
            if (maxRgb > 1.0)
            {
                var sf = 1.0 / maxRgb;
                for (var i = 0; i < 3; i++) rgbValues[i] *= sf;
            }

            for (var i = 0; i < 3; i++) rgbArray[i] = ClampAndRound(rgbValues[i]);
            return true;
        }

        bool MakeDensityUniform(JObject? section)
        {
            if (section == null || !FOG_UNIFORM_HEIGHT) return false;

            var hasHeightFields = section.SelectToken("max_density_height") != null
                               || section.SelectToken("zero_density_height") != null;
            var isUniform = section.SelectToken("uniform")?.Value<bool>() ?? false;

            if (hasHeightFields && !isUniform)
            {
                section.Remove("max_density_height");
                section.Remove("zero_density_height");
                section["uniform"] = true;
                return true;
            }
            return false;
        }

        bool TryGetNumericValue(JToken? token, out double value)
        {
            value = 0.0;
            if (token == null) return false;
            return token.Type switch
            {
                JTokenType.Float or JTokenType.Integer => (value = token.Value<double>()) >= 0,
                JTokenType.String => double.TryParse(token.Value<string>(), out value),
                _ => false
            };
        }

        double GetDensityValue(JObject volumetric, string path)
        {
            var token = volumetric.SelectToken(path);
            return TryGetNumericValue(token, out var value) ? value : 0.0;
        }

        double ClampAndRound(double value)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            var rounded = Math.Round(clamped, DECIMAL_PRECISION);
            return Math.Abs(rounded) < MIN_VALUE_THRESHOLD ? 0.0 : rounded;
        }

        double CalculateNewDensity(double currentDensity, double fogMultiplier)
        {
            if (Math.Abs(currentDensity) < 0.0001)
                return fogMultiplier <= 1.0
                    ? Math.Clamp(fogMultiplier, 0.0, 1.0)
                    : Math.Clamp(fogMultiplier / 10.0, 0.0, 1.0);

            return Math.Clamp(currentDensity * fogMultiplier, 0.0, 1.0);
        }

        string RemoveScientificNotation(string jsonString)
        {
            const string pattern = @"(?<=:\s*|,\s*|\[\s*)(-?\d+\.?\d*[eE][+-]?\d+)(?=\s*[,\]\}]|\s*$)";
            return Regex.Replace(jsonString, pattern, match =>
            {
                if (!double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                    return match.Value;

                if (Math.Abs(value) < MIN_VALUE_THRESHOLD) return "0.0";
                var rounded = Math.Round(value, DECIMAL_PRECISION);
                if (Math.Abs(rounded) < MIN_VALUE_THRESHOLD) return "0.0";
                return rounded.ToString($"0.{new string('#', DECIMAL_PRECISION)}",
                    System.Globalization.CultureInfo.InvariantCulture);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Sub-processors  ──  receive bitmaps, return true if modified
    //  Virtual (1×1) bitmaps are handled transparently; processors are unaware.
    // ══════════════════════════════════════════════════════════════════════════

    // ── Emissivity ────────────────────────────────────────────────────────────

    /// <summary>Applies emissivity multiplier and optional ambient light to a MER bitmap.</summary>
    private static bool ApplyEmissivity(Bitmap bmp)
    {
        var userMult = EmissivityMultiplier;
        var width = bmp.Width;
        var height = bmp.Height;
        var wroteBack = false;

        // First pass: emissivity multiplier
        if (userMult != 1.0)
        {
            var maxGreen = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    int g = bmp.GetPixel(x, y).G;
                    if (g > maxGreen) maxGreen = g;
                }

            if (maxGreen > 0)
            {
                var ratio = 255.0 / maxGreen;
                var neededMult = ratio;
                var effectiveMult = userMult < neededMult ? userMult : neededMult;
                var excess = Math.Max(0, userMult - effectiveMult);

                var excessOverage = excess - 1.0;
                var dampenedExcessOverage = excessOverage * EMISSIVE_EXCESS_INTENSITY_DAMPEN;
                var dampenedExcess = 1.0 + dampenedExcessOverage;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var origColor = bmp.GetPixel(x, y);
                        int origG = origColor.G;
                        if (origG == 0) continue;

                        var newG = origG * effectiveMult;
                        if (excess > 0)
                            newG += origG * (dampenedExcess - 1.0);

                        int finalG = newG < 127.5
                            ? (int)Math.Ceiling(newG)
                            : (int)Math.Floor(newG);
                        finalG = Math.Clamp(finalG, 0, 255);

                        if (finalG != origG)
                        {
                            wroteBack = true;
                            bmp.SetPixel(x, y, Color.FromArgb(origColor.A, origColor.R, finalG, origColor.B));
                        }
                    }
                }
            }
        }

        // Second pass: ambient light
        if (AddEmissivityAmbientLight)
        {
            var ambientAmount = (int)Math.Ceiling(userMult) + 1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var origColor = bmp.GetPixel(x, y);
                    var newG = Math.Clamp(origColor.G + ambientAmount, 0, 255);
                    if (newG != origColor.G)
                    {
                        wroteBack = true;
                        bmp.SetPixel(x, y, Color.FromArgb(origColor.A, origColor.R, newG, origColor.B));
                    }
                }
            }
        }

        return wroteBack;
    }

    // ── Normal intensity ──────────────────────────────────────────────────────

    /// <summary>Applies normal-intensity scaling to a normal map or heightmap bitmap.</summary>
    private static bool ApplyNormalIntensity(Bitmap bmp, bool isHeightmap)
    {
        return isHeightmap
            ? ApplyHeightmapIntensity(bmp)
            : ApplyNormalMapIntensity(bmp);
    }

    private static bool ApplyNormalMapIntensity(Bitmap bmp)
    {
        var intensityPercent = NormalIntensity / 100.0;
        var width = bmp.Width;
        var height = bmp.Height;
        var wroteBack = false;

        if (intensityPercent <= 1.0)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var orig = bmp.GetPixel(x, y);
                    var newR = (int)Math.Round(128 + (orig.R - 128) * intensityPercent);
                    var newG = (int)Math.Round(128 + (orig.G - 128) * intensityPercent);
                    newR = Math.Clamp(newR, 0, 255);
                    newG = Math.Clamp(newG, 0, 255);

                    if (newR != orig.R || newG != orig.G)
                    {
                        wroteBack = true;
                        bmp.SetPixel(x, y, Color.FromArgb(orig.A, newR, newG, orig.B));
                    }
                }
            }
        }
        else
        {
            double maxIdealDeviation = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = bmp.GetPixel(x, y);
                    var rDev = Math.Abs((pixel.R - 128.0) * intensityPercent);
                    var gDev = Math.Abs((pixel.G - 128.0) * intensityPercent);
                    var maxDev = Math.Max(rDev, gDev);
                    if (maxDev > maxIdealDeviation) maxIdealDeviation = maxDev;
                }

            if (maxIdealDeviation == 0) return false;

            var compressionRatio = maxIdealDeviation > 127.0 ? 127.0 / maxIdealDeviation : 1.0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var orig = bmp.GetPixel(x, y);
                    var idealR = 128.0 + (orig.R - 128.0) * intensityPercent * compressionRatio;
                    var idealG = 128.0 + (orig.G - 128.0) * intensityPercent * compressionRatio;
                    var newR = Math.Clamp((int)Math.Round(idealR), 0, 255);
                    var newG = Math.Clamp((int)Math.Round(idealG), 0, 255);

                    if (newR != orig.R || newG != orig.G)
                    {
                        wroteBack = true;
                        bmp.SetPixel(x, y, Color.FromArgb(orig.A, newR, newG, orig.B));
                    }
                }
            }
        }

        return wroteBack;
    }

    private static bool ApplyHeightmapIntensity(Bitmap bmp)
    {
        var userIntensity = NormalIntensity / 100.0;
        var width = bmp.Width;
        var height = bmp.Height;

        int minGray = 255, maxGray = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var gray = bmp.GetPixel(x, y).R;
                if (gray < minGray) minGray = gray;
                if (gray > maxGray) maxGray = gray;
            }

        double currentSpan = maxGray - minGray;
        if (currentSpan == 0) return false;

        var idealSpan = currentSpan * userIntensity;
        var actualSpan = Math.Min(idealSpan, 255.0);
        var currentCenter = (minGray + maxGray) / 2.0;
        var compressionRatio = actualSpan / Math.Max(idealSpan, actualSpan);
        var wroteBack = false;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var orig = bmp.GetPixel(x, y);
                var deviation = orig.R - currentCenter;
                var newGray = Math.Clamp((int)Math.Round(127.5 + deviation * userIntensity * compressionRatio), 0, 255);

                if (newGray != orig.R)
                {
                    wroteBack = true;
                    bmp.SetPixel(x, y, Color.FromArgb(orig.A, newGray, newGray, newGray));
                }
            }
        }

        return wroteBack;
    }

    // ── Lazify ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lazifies a normal map or heightmap using the colour texture as a luminance guide.
    /// colorBmp and normalBmp must have the same dimensions (virtual 1×1 bitmaps are
    /// handled by the orchestrator—if they differ in size, we skip gracefully).
    /// </summary>
    private static bool ApplyLazify(Bitmap colorBmp, Bitmap normalBmp, bool isHeightmap)
    {
        var alpha = LazifyNormalAlpha;
        var width = normalBmp.Width;
        var height = normalBmp.Height;

        // Dimension guard (virtual bitmaps are 1×1; skip if color is virtual)
        if (colorBmp.Width != width || colorBmp.Height != height)
            return false;

        // Apply edge padding to transparent pixels of the colour map
        var paddedColormap = ApplyEdgePadding(colorBmp);

        // Convert to greyscale and stretch
        var greyscale = new byte[width, height];
        byte minV = 255, maxV = 0;

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = paddedColormap[x, y];
                var gv = (byte)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                greyscale[x, y] = gv;
                if (gv < minV) minV = gv;
                if (gv > maxV) maxV = gv;
            }

        var stretched = new byte[width, height];
        double range = maxV - minV;

        if (range == 0)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    stretched[x, y] = 128;
        }
        else
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    stretched[x, y] = (byte)((greyscale[x, y] - minV) / range * 255);
        }

        return isHeightmap
            ? LazifyHeightmap(normalBmp, stretched, alpha, width, height)
            : LazifyNormalMap(normalBmp, stretched, alpha, width, height);
    }

    private static bool LazifyHeightmap(Bitmap bmp, byte[,] stretched, int alpha, int width, int height)
    {
        var wroteBack = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var orig = bmp.GetPixel(x, y);
                var newH = stretched[x, y];
                var blended = (alpha * newH + (255 - alpha) * orig.R) / 255;
                var finalValue = (byte)Math.Clamp(blended, 0, 255);

                if (finalValue != orig.R)
                {
                    wroteBack = true;
                    bmp.SetPixel(x, y, Color.FromArgb(orig.A, finalValue, finalValue, finalValue));
                }
            }
        }
        return wroteBack;
    }

    private static bool LazifyNormalMap(Bitmap bmp, byte[,] stretched, int alpha, int width, int height)
    {
        // Build 3×3 tiled heightmap for seamless Sobel normal generation
        var expW = width * 3;
        var expH = height * 3;
        var expHmap = new byte[expW, expH];

        for (var ty = 0; ty < 3; ty++)
            for (var tx = 0; tx < 3; tx++)
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        expHmap[tx * width + x, ty * height + y] = stretched[x, y];

        // Sobel normal generation over the tiled image
        var expNormals = new (byte r, byte g)[expW, expH];

        for (var y = 1; y < expH - 1; y++)
        {
            for (var x = 1; x < expW - 1; x++)
            {
                var gx =
                    -1 * expHmap[x - 1, y - 1] + 1 * expHmap[x + 1, y - 1] +
                    -2 * expHmap[x - 1, y] + 2 * expHmap[x + 1, y] +
                    -1 * expHmap[x - 1, y + 1] + 1 * expHmap[x + 1, y + 1];

                var gy =
                    -1 * expHmap[x - 1, y - 1] - 2 * expHmap[x, y - 1] - 1 * expHmap[x + 1, y - 1] +
                     1 * expHmap[x - 1, y + 1] + 2 * expHmap[x, y + 1] + 1 * expHmap[x + 1, y + 1];

                var normalX = gx / (8.0 * 255.0);
                var normalY = -gy / (8.0 * 255.0);

                expNormals[x, y] = (
                    (byte)Math.Clamp((normalX * 0.5 + 0.5) * 255, 0, 255),
                    (byte)Math.Clamp((normalY * 0.5 + 0.5) * 255, 0, 255)
                );
            }
        }

        // Crop centre tile
        var genNormals = new (byte r, byte g)[width, height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                genNormals[x, y] = expNormals[width + x, height + y];

        // Calculate original intensity
        double origIntensitySum = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = bmp.GetPixel(x, y);
                origIntensitySum += (Math.Abs(p.R - 128) + Math.Abs(p.G - 128)) / 2.0;
            }
        double originalIntensity = origIntensitySum / (width * height);

        // Blend generated normals with original (33% linear + 67% overlay)
        var blended = new (byte r, byte g)[width, height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var orig = bmp.GetPixel(x, y);
                var (newR, newG) = genNormals[x, y];

                var detailR = (alpha * newR + (255 - alpha) * 128) / 255.0;
                var detailG = (alpha * newG + (255 - alpha) * 128) / 255.0;

                var linearR = (orig.R + detailR) / 2.0;
                var linearG = (orig.G + detailG) / 2.0;

                double overlayR = orig.R < 128
                    ? (2.0 * orig.R * detailR) / 255.0
                    : 255.0 - (2.0 * (255.0 - orig.R) * (255.0 - detailR)) / 255.0;

                double overlayG = orig.G < 128
                    ? (2.0 * orig.G * detailG) / 255.0
                    : 255.0 - (2.0 * (255.0 - orig.G) * (255.0 - detailG)) / 255.0;

                blended[x, y] = (
                    (byte)Math.Clamp(0.33 * linearR + 0.67 * overlayR, 0, 255),
                    (byte)Math.Clamp(0.33 * linearG + 0.67 * overlayG, 0, 255)
                );
            }
        }

        // Normalise back to original intensity
        double blendedIntensitySum = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (r, g) = blended[x, y];
                blendedIntensitySum += (Math.Abs(r - 128) + Math.Abs(g - 128)) / 2.0;
            }
        double blendedIntensity = blendedIntensitySum / (width * height);
        double intensityRatio = blendedIntensity > 0 ? originalIntensity / blendedIntensity : 1.0;

        var wroteBack = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var orig = bmp.GetPixel(x, y);
                var (bR, bG) = blended[x, y];

                var finalR = (byte)Math.Clamp(128 + (bR - 128) * intensityRatio, 0, 255);
                var finalG = (byte)Math.Clamp(128 + (bG - 128) * intensityRatio, 0, 255);

                if (finalR != orig.R || finalG != orig.G)
                {
                    wroteBack = true;
                    bmp.SetPixel(x, y, Color.FromArgb(orig.A, finalR, finalG, 255));
                }
            }
        }

        return wroteBack;
    }

    // Shared edge-padding helper
    private static Color[,] ApplyEdgePadding(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var result = new Color[width, height];
        var isOpaque = new bool[width, height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                result[x, y] = p;
                isOpaque[x, y] = p.A > 0;
            }

        int maxPasses = width * height;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var anyChanged = false;
            var newOpaque = new bool[width, height];
            Array.Copy(isOpaque, newOpaque, isOpaque.Length);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (isOpaque[x, y]) continue;

                    Color? nearest = null;
                    if (x > 0 && isOpaque[x - 1, y]) nearest = result[x - 1, y];
                    else if (x < width - 1 && isOpaque[x + 1, y]) nearest = result[x + 1, y];
                    else if (y > 0 && isOpaque[x, y - 1]) nearest = result[x, y - 1];
                    else if (y < height - 1 && isOpaque[x, y + 1]) nearest = result[x, y + 1];

                    if (nearest.HasValue)
                    {
                        result[x, y] = Color.FromArgb(255, nearest.Value.R, nearest.Value.G, nearest.Value.B);
                        newOpaque[x, y] = true;
                        anyChanged = true;
                    }
                }
            }

            isOpaque = newOpaque;
            if (!anyChanged) break;
        }

        return result;
    }

    // ── Roughness ─────────────────────────────────────────────────────────────

    private static bool ApplyRoughness(Bitmap bmp)
    {
        const double MetalnessModificationFraction = 0.33;
        const double MetalnessInfluenceOnRoughnessReduction = 0.33;
        const double BasePower = 2.2;
        const double ImpactMultiplier = 2.4;
        const double HighControlScaling = 8.0;

        var controlValue = RoughnessControlValue;
        if (controlValue == 0) return false;

        bool isIncreasing = controlValue > 0;
        int absControl = Math.Abs(controlValue);

        var width = bmp.Width;
        var height = bmp.Height;
        var wroteBack = false;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var orig = bmp.GetPixel(x, y);
                int origRoughness = orig.B;
                int origMetalness = orig.R;

                int newRoughness, newMetalness;

                if (isIncreasing)
                {
                    var normalized = origRoughness / 255.0;
                    var curveAggression = BasePower + (absControl / 25.0) * 1.5;
                    var inverseFactor = 1.0 - Math.Pow(normalized, curveAggression);
                    var maxBoost = absControl * ImpactMultiplier + (absControl / 12.0) * HighControlScaling;
                    var boost = maxBoost * inverseFactor;

                    newRoughness = Math.Clamp((int)Math.Floor(origRoughness + boost), 0, 255);

                    if (origMetalness > 0)
                    {
                        var metalnessReduction = (newRoughness - origRoughness) * MetalnessModificationFraction;
                        newMetalness = Math.Clamp((int)Math.Floor(origMetalness - metalnessReduction), 0, 255);
                    }
                    else newMetalness = origMetalness;
                }
                else
                {
                    var metalnessInfluence = origMetalness > 0 ? origMetalness / 255.0 : 0.0;
                    var roughnessNormalized = origRoughness / 255.0;
                    var curveAggression = BasePower + (absControl / 5.0) * 1.5;
                    var factor = Math.Pow(roughnessNormalized, curveAggression);
                    var maxReduction = absControl * ImpactMultiplier + (absControl / 5.0) * HighControlScaling;
                    var baseReduction = maxReduction * factor;
                    var metalnessBonus = maxReduction * metalnessInfluence * MetalnessInfluenceOnRoughnessReduction;

                    newRoughness = Math.Clamp((int)Math.Ceiling(origRoughness - (baseReduction + metalnessBonus)), 0, 255);

                    if (origMetalness > 0)
                    {
                        var posCurveAggression = BasePower + (absControl / 25.0) * 1.5;
                        var invFactor = 1.0 - Math.Pow(roughnessNormalized, posCurveAggression);
                        var hypoMaxBoost = absControl * ImpactMultiplier + (absControl / 12.0) * HighControlScaling;
                        var metalnessIncrease = hypoMaxBoost * invFactor * MetalnessModificationFraction;
                        newMetalness = Math.Clamp((int)Math.Ceiling(origMetalness + metalnessIncrease), 0, 255);
                    }
                    else newMetalness = origMetalness;
                }

                if (newRoughness != origRoughness || newMetalness != origMetalness)
                {
                    wroteBack = true;
                    bmp.SetPixel(x, y, Color.FromArgb(orig.A, newMetalness, orig.G, newRoughness));
                }
            }
        }

        return wroteBack;
    }

    // ── Material grain ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies per-pixel noise to a MER bitmap.
    /// The noise cache is shared across the call-site so that texture variants
    /// (e.g. _on / _off) receive the same noise pattern as their siblings.
    /// </summary>
    private static bool ApplyMaterialGrain(
        Bitmap bmp,
        string? sourceFilePath,
        Dictionary<string, (int[,] red, int[,] green, int[,] blue, int[,] checker)> noiseCache)
    {
        const double CHECKERBOARD_INTENSITY = 0.2;
        const double CHECKERBOARD_NOISE_AMOUNT = 0.2;

        var materialNoiseOffset = MaterialNoiseOffset;
        if (materialNoiseOffset <= 0) return false;

        var width = bmp.Width;
        var height = bmp.Height;

        // Animated texture (flipbook) detection
        var isAnimated = height >= width * 2 && width > 0 && height % width == 0;
        var frameHeight = width;
        var frameCount = isAnimated ? height / width : 1;
        if (!isAnimated) frameHeight = height;

        // Cache key — uses the base filename (variant-stripped) + dimensions
        var baseFilename = sourceFilePath != null
            ? GetBaseFilename(sourceFilePath)
            : $"virtual_{width}x{frameHeight}";
        var cacheKey = $"{baseFilename}_{width}x{frameHeight}";

        int[,] redOffsets, greenOffsets, blueOffsets, checkerboardOffsets;

        if (noiseCache.TryGetValue(cacheKey, out var cached))
        {
            redOffsets = cached.red;
            greenOffsets = cached.green;
            blueOffsets = cached.blue;
            checkerboardOffsets = cached.checker;
        }
        else
        {
            var rng = new Random();
            redOffsets = new int[width, frameHeight];
            greenOffsets = new int[width, frameHeight];
            blueOffsets = new int[width, frameHeight];
            checkerboardOffsets = new int[width, frameHeight];

            for (var y = 0; y < frameHeight; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    redOffsets[x, y] = rng.Next(-materialNoiseOffset, materialNoiseOffset + 1);
                    greenOffsets[x, y] = rng.Next(-materialNoiseOffset, materialNoiseOffset + 1);
                    blueOffsets[x, y] = rng.Next(-materialNoiseOffset, materialNoiseOffset + 1);

                    var baseChecker = ((x + y) % 2) * 255;
                    var checkerNoise = rng.Next(
                        (int)(-materialNoiseOffset * CHECKERBOARD_NOISE_AMOUNT),
                        (int)(materialNoiseOffset * CHECKERBOARD_NOISE_AMOUNT) + 1);
                    checkerboardOffsets[x, y] = Math.Clamp(baseChecker + checkerNoise, 0, 255);
                }
            }

            noiseCache[cacheKey] = (redOffsets, greenOffsets, blueOffsets, checkerboardOffsets);
        }

        var wroteBack = false;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameStartY = frame * frameHeight;

            for (var y = 0; y < frameHeight; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var actualY = frameStartY + y;
                    var orig = bmp.GetPixel(x, actualY);
                    int r = orig.R, g = orig.G, b = orig.B;

                    var checkerValue = (checkerboardOffsets[x, y] - 127.5) * (materialNoiseOffset / 127.5);

                    var redFN = redOffsets[x, y] * (1.0 - CHECKERBOARD_INTENSITY) + checkerValue * CHECKERBOARD_INTENSITY;
                    var greenFN = greenOffsets[x, y] * (1.0 - CHECKERBOARD_INTENSITY) + checkerValue * CHECKERBOARD_INTENSITY;
                    var blueFN = blueOffsets[x, y] * (1.0 - CHECKERBOARD_INTENSITY) + checkerValue * CHECKERBOARD_INTENSITY;

                    var redEff = CalculateEffectiveness(r);
                    var greenEff = CalculateEffectiveness(g) * 0.2;
                    var blueEff = CalculateEffectiveness(b);

                    var newR = r + (int)Math.Round(redFN * redEff);
                    var newG = g + (int)Math.Round(greenFN * greenEff);
                    var newB = b + (int)Math.Round(blueFN * blueEff);

                    // Anti-clipping: discard if would clip
                    if (newR < 0 || newR > 255) newR = r;
                    if (newG < 0 || newG > 255) newG = g;
                    if (newB < 0 || newB > 255) newB = b;

                    if (newR != r || newG != g || newB != b)
                    {
                        wroteBack = true;
                        bmp.SetPixel(x, actualY, Color.FromArgb(orig.A, newR, newG, newB));
                    }
                }
            }
        }

        return wroteBack;

        static double CalculateEffectiveness(int v) =>
            v == 128 ? 1.0 :
            v < 128 ? v / 128.0 :
                       1.0 - (v - 128) * 0.67 / 127.0;

        static string GetBaseFilename(string filePath)
        {
            var filename = Path.GetFileNameWithoutExtension(filePath);
            var variantSuffixes = new[]
            {
                "on", "off", "active", "inactive", "dormant", "bloom",
                "ejecting", "lit", "unlit", "powered", "crafting"
            };

            var parts = filename.Split('_');
            var baseParts = new List<string>();
            foreach (var part in parts)
            {
                var isVariant = variantSuffixes.Any(s =>
                    part.Equals(s, StringComparison.OrdinalIgnoreCase));
                if (!isVariant) baseParts.Add(part);
            }

            return string.Join("_", baseParts);
        }
    }
}
