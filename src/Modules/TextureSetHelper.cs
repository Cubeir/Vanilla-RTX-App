using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules;

// ══════════════════════════════════════════════════════════════════════════════
//  TextureSetHelper  ──  parsing, resolution, and virtual-bitmap creation
// ══════════════════════════════════════════════════════════════════════════════

public static class TextureSetHelper
{
    public enum TextureKind { Color, Mer, Normal, Heightmap }

    /// <summary>
    /// Discriminated union: either a real file path or an inline colour value.
    /// </summary>
    public sealed class TextureLayerValue
    {
        public string? FilePath { get; }

        public bool IsInline { get; }
        /// <summary>Parsed RGBA components (0-255). Always length 4 internally.</summary>
        public byte[] InlineRgba { get; } = Array.Empty<byte>();
        /// <summary>Number of components as originally written (3 or 4).</summary>
        public int InlineChannels { get; }
        /// <summary>True when the source was a hex string (e.g. "#B48CBE").</summary>
        public bool IsHex { get; }
        /// <summary>The node this layer was read from, or null for a file-backed layer.</summary>
        public JsonNode? SourceNode { get; }

        private TextureLayerValue(JsonNode sourceNode, byte[] rgba, int originalChannels, bool isHex)
        {
            IsInline = true;
            SourceNode = sourceNode;
            InlineRgba = rgba;
            InlineChannels = originalChannels;   // the count as it appeared in the file
            IsHex = isHex;
        }

        private TextureLayerValue(string filePath)
        {
            FilePath = filePath;
            SourceNode = null;
        }

        /// <summary>A layer backed by a real image file on disk.</summary>
        public static TextureLayerValue FromFile(string path) => new(path);

        /// <summary>
        /// Parses a texture set layer written as a colour rather than a file name, or null if
        /// the node is neither form (in which case it names a texture file).
        ///
        /// <para>Minecraft accepts two spellings and this round-trips whichever it was given:
        /// a <c>"#RRGGBB"</c> / <c>"#RRGGBBAA"</c> hex string, or an array of 3 or 4 numbers.
        /// Both are held internally as RGBA, with the original channel count remembered so a
        /// three-component value is not written back as four - an edit that changes the shape
        /// of a pack author's file is an edit they did not ask for.</para>
        /// </summary>
        public static TextureLayerValue? TryParseInline(JsonNode node)
        {
            // Hex string
            if (node.GetValueKind() == JsonValueKind.String)
            {
                var s = (MinecraftJson.GetString(node) ?? string.Empty).Trim();
                if (s.StartsWith('#') && TryParseHex(s, out var rgba, out var originalChannels))
                    return new TextureLayerValue(node, rgba, originalChannels, isHex: true);
                return null;
            }

            // Array of numbers (RGB triplet or RGBA quadruplet)
            if (node is JsonArray arr && arr.Count is 3 or 4)
            {
                var originalChannels = arr.Count;
                var comps = new byte[originalChannels];
                for (var i = 0; i < originalChannels; i++)
                {
                    if (!TryGetByte(arr[i], out comps[i]))
                        return null;
                }
                // Pad to 4 channels internally, but remember the original count
                var rgba = originalChannels == 4
                    ? comps
                    : new[] { comps[0], comps[1], comps[2], (byte)255 };
                return new TextureLayerValue(arr, rgba, originalChannels, isHex: false);
            }

            return null;
        }

        /// <summary>
        /// Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c> into RGBA, reporting which of the two it
        /// was via <paramref name="originalChannels"/> so it can be written back the same
        /// way. Alpha defaults to 255 for the six-digit form.
        /// </summary>
        private static bool TryParseHex(string hex, out byte[] rgba, out int originalChannels)
        {
            rgba = Array.Empty<byte>();
            originalChannels = 0;
            hex = hex.TrimStart('#');

            if (hex.Length == 6)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255 };
                originalChannels = 3;
                return true;
            }
            if (hex.Length == 8)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                originalChannels = 4;
                return true;
            }
            return false;
        }

        /// <summary>
        /// One colour component from a JSON array element, false for anything out of 0-255 or
        /// not a number. Lenient about how the number is written (packs in the wild quote
        /// them), strict about its value.
        /// </summary>
        private static bool TryGetByte(JsonNode? t, out byte b)
        {
            b = 0;
            // Numbers and quoted numbers both count, exactly as before - a component written
            // as "128" is as legible as one written as 128.
            if (!MinecraftJson.TryGetDouble(t, out var d)) return false;

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
        /// Serialises the (possibly modified) 1×1 bitmap back to exactly the format
        /// it was originally written in: RGB hex stays RGB hex, RGBA array stays RGBA
        /// array, etc. The alpha channel is always preserved from the bitmap as-is.
        /// </summary>
        public JsonNode SerializeVirtual(Bitmap bmp)
        {
            var c = bmp.GetPixel(0, 0);
            byte r = c.R, g = c.G, b = c.B, a = c.A;

            if (IsHex)
            {
                return JsonValue.Create(InlineChannels == 3
                    ? $"#{r:X2}{g:X2}{b:X2}"
                    : $"#{r:X2}{g:X2}{b:X2}{a:X2}")!;
            }

            // Built element by element rather than via the JsonArray(params) constructor so
            // every component goes through the non-generic JsonValue.Create(int) overload -
            // the generic Create<T>/Add<T> path is the one the trimmer flags (IL2026), and
            // this file ships in a PublishTrimmed Release build.
            var array = new JsonArray();
            array.Add((JsonNode?)JsonValue.Create((int)r));
            array.Add((JsonNode?)JsonValue.Create((int)g));
            array.Add((JsonNode?)JsonValue.Create((int)b));
            if (InlineChannels == 4)
                array.Add((JsonNode?)JsonValue.Create((int)a));
            return array;
        }
    }

    public sealed class ResolvedTextureSet
    {
        public string JsonFilePath { get; init; } = "";
        public JsonObject RootJson { get; init; } = new();
        public JsonObject SetNode { get; init; } = new();

        public TextureLayerValue Color { get; init; } = null!;
        public TextureLayerValue? Mer { get; init; }
        public TextureLayerValue? NormalOrHeight { get; init; }
        public bool IsHeightmap { get; init; }
    }

    public sealed class LoadedTextureSet
    {
        public ResolvedTextureSet Resolved { get; init; } = null!;

        public Bitmap ColorBmp { get; set; } = null!;
        public bool ColorIsVirtual { get; init; }

        public Bitmap? MerBmp { get; set; }
        public bool MerIsVirtual { get; init; }

        public Bitmap? NormalBmp { get; set; }
        public bool NormalIsVirtual { get; init; }

        public bool ColorDirty { get; set; }
        public bool MerDirty { get; set; }
        public bool NormalDirty { get; set; }
    }

    private static readonly string[] SupportedExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Scans a pack root, parses all .texture_set.json files, validates them
    /// per the Minecraft spec, and returns the valid resolved sets.
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
                var root = MinecraftJson.ParseObjectFile(jsonFile);
                if (root == null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': not readable as a JSON object.");
                    continue;
                }

                if (root["minecraft:texture_set"] is not JsonObject set)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': missing minecraft:texture_set node.");
                    continue;
                }

                var folder = Path.GetDirectoryName(jsonFile)!;

                var colorNode = set["color"];
                if (colorNode == null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': no color layer defined.");
                    continue;
                }

                var colorLayer = ResolveLayer(folder, colorNode);
                if (colorLayer == null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': color layer could not be resolved.");
                    continue;
                }

                var merNode = set["metalness_emissive_roughness"];
                var mersNode = set["metalness_emissive_roughness_subsurface"];

                if (merNode != null && mersNode != null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': both MER and MERS defined (mutually exclusive).");
                    continue;
                }

                var merLayer = ResolveLayer(folder, merNode ?? mersNode);

                var normalNode = set["normal"];
                var heightmapNode = set["heightmap"];

                if (normalNode != null && heightmapNode != null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': both normal and heightmap defined (mutually exclusive).");
                    continue;
                }

                var normalLayer = ResolveLayer(folder, normalNode);
                var heightmapLayer = ResolveLayer(folder, heightmapNode);
                var isHeightmap = heightmapNode != null;

                results.Add(new ResolvedTextureSet
                {
                    JsonFilePath = jsonFile,
                    RootJson = root,
                    SetNode = set,
                    Color = colorLayer,
                    Mer = merLayer,
                    NormalOrHeight = normalLayer ?? heightmapLayer,
                    IsHeightmap = isHeightmap,
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error resolving '{jsonFile}': {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Loads all bitmaps for a single resolved texture set. Virtual (inline) colours
    /// become 1×1 bitmaps and are flagged accordingly. Returns null (and leaves nothing
    /// allocated) if the color bitmap can't be loaded.
    ///
    /// This is deliberately a single-item operation rather than a batch: decoding an
    /// image from disk is real, sometimes-slow I/O work, and the orchestrator pipelines
    /// load → process → save per texture set (in parallel across texture sets) so that
    /// progress reporting and cancellation are granular to "one texture", not "one pack".
    /// </summary>
    public static LoadedTextureSet? LoadTextureSet(ResolvedTextureSet rs)
    {
        // previously, if the color layer loaded fine but the MER or normal
        // layer then *threw* while loading (rather than just returning null),
        // the already-loaded colorBmp/merBmp were never disposed - a real (if rare)
        // native GDI+ handle + memory leak. Track everything allocated here and
        // dispose it on any failure path via `finally`.
        Bitmap? colorBmp = null;
        Bitmap? merBmp = null;
        Bitmap? normalBmp = null;
        var success = false;

        try
        {
            colorBmp = LoadLayer(rs.Color);
            if (colorBmp == null)
            {
                Trace.WriteLine($"[TUNER] Skipping texture set '{rs.JsonFilePath}': color bitmap could not be loaded.");
                return null;
            }

            if (rs.Mer != null)
            {
                merBmp = LoadLayer(rs.Mer);
                if (merBmp == null)
                    Trace.WriteLine($"[TUNER] Warning for '{rs.JsonFilePath}': MER layer could not be loaded; MER processors will be skipped.");
            }

            if (rs.NormalOrHeight != null)
            {
                normalBmp = LoadLayer(rs.NormalOrHeight);
                if (normalBmp == null)
                    Trace.WriteLine($"[TUNER] Warning for '{rs.JsonFilePath}': normal/heightmap layer could not be loaded; normal processors will be skipped.");
            }

            var result = new LoadedTextureSet
            {
                Resolved = rs,
                ColorBmp = colorBmp,
                ColorIsVirtual = rs.Color.IsInline,
                MerBmp = merBmp,
                MerIsVirtual = rs.Mer?.IsInline ?? false,
                NormalBmp = normalBmp,
                NormalIsVirtual = rs.NormalOrHeight?.IsInline ?? false,
            };
            success = true;
            return result;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TUNER] Error loading texture set '{rs.JsonFilePath}': {ex.Message}");
            return null;
        }
        finally
        {
            if (!success)
            {
                colorBmp?.Dispose();
                merBmp?.Dispose();
                normalBmp?.Dispose();
            }
        }
    }

    /// <summary>Batch convenience wrapper kept for any other callers - loads every
    /// resolved set sequentially. Tuner's own pipeline calls LoadTextureSet directly
    /// per-item instead, so it can parallelize and report progress per texture.</summary>
    public static IReadOnlyList<LoadedTextureSet> LoadTextureSets(IReadOnlyList<ResolvedTextureSet> resolved)
    {
        var results = new List<LoadedTextureSet>(resolved.Count);
        foreach (var rs in resolved)
        {
            var lts = LoadTextureSet(rs);
            if (lts != null) results.Add(lts);
        }
        return results;
    }

    private static TextureLayerValue? ResolveLayer(string folder, JsonNode? node)
    {
        if (node == null) return null;

        var inline = TextureLayerValue.TryParseInline(node);
        if (inline != null) return inline;

        if (node.GetValueKind() != JsonValueKind.String) return null;

        var name = (MinecraftJson.GetString(node) ?? string.Empty).Trim();
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

        return Helpers.ReadImage(layer.FilePath!, false);
    }

    /// <summary>
    /// Resolves a bare texture name (no extension) to the file the <b>game</b> would load,
    /// or null if the folder has none.
    ///
    /// <para><b>Always resolve texture names through this, never by hand.</b> Minecraft takes
    /// the first match in <c>SupportedExtensions</c> order (.tga, .png, .jpg, .jpeg), so a
    /// folder holding both foo.tga and foo.png has exactly one answer and a hand-rolled
    /// existence check that assumes one extension silently picks the wrong file. That is a
    /// real bug this app has shipped before - see CLAUDE.md §4.4.</para>
    ///
    /// <para>The returned path is the real on-disk one, so its casing is usable as-is.</para>
    /// </summary>
    public static string? FindTextureFile(string folder, string textureName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var target = Path.Combine(folder, textureName + ext);
            if (File.Exists(target))
                return target;

            try
            {
                var matches = Directory.GetFiles(folder, textureName + ext, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0) return matches[0];
            }
            catch { /* access denied or directory missing */ }
        }

        return null;
    }

    /// <summary>
    /// Persists a loaded texture set's dirty bitmaps back to disk (or inline JSON).
    /// For real files: writes in the source format (TGA stays TGA, PNG stays PNG, etc.).
    /// For virtual bitmaps: patches the .texture_set.json in place.
    /// </summary>
    public static void SaveDirtyLayers(LoadedTextureSet lts)
    {
        var rs = lts.Resolved;
        var jsonDirty = false;

        if (lts.ColorDirty && lts.ColorBmp != null)
        {
            try
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
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving color layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.MerDirty && lts.MerBmp != null && rs.Mer != null)
        {
            try
            {
                if (lts.MerIsVirtual)
                {
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
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving MER layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.NormalDirty && lts.NormalBmp != null && rs.NormalOrHeight != null)
        {
            try
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
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving normal/heightmap layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (jsonDirty)
        {
            try
            {
                MinecraftJson.WriteIndented(rs.JsonFilePath, rs.RootJson);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error writing JSON for '{rs.JsonFilePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a bitmap back to disk preserving the original file format.
    /// TGA  → TGA   PNG  → lossless 32-bpp ARGB PNG
    /// JPG  → maximum-quality JPEG   Other → TGA fallback
    /// </summary>
    private static void WriteBackBitmap(Bitmap bmp, string originalPath)
    {
        var ext = Path.GetExtension(originalPath).ToLowerInvariant();

        switch (ext)
        {
            case ".tga":
                Helpers.WriteImageAsTGA(bmp, originalPath);
                break;

            case ".png":
                {
                    // EnsureArgb32 returns the *same* instance when bmp is already
                    // Format32bppArgb (the common case). The old code wrapped that in a
                    // `using`, which disposed the caller's bitmap here - and then the
                    // orchestrator disposed it again a moment later. Bitmap.Dispose()
                    // happens to tolerate double-dispose, but it's fragile to rely on
                    // that; only dispose the canonical copy when it's actually a new object.
                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, ImageFormat.Png); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            case ".jpg":
            case ".jpeg":
                {
                    var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    if (jpegEncoder == null) goto default;

                    WarnIfAlphaWillBeLost(bmp, originalPath);

                    var qualityParam = new EncoderParameters(1);
                    qualityParam.Param[0] = new EncoderParameter(Encoder.Quality, 100L);

                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, jpegEncoder, qualityParam); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            default:
                Helpers.WriteImageAsTGA(bmp, originalPath);
                break;
        }
    }

    /// <summary>
    /// JPEG has no alpha channel, so transparency in a layer written back as .jpg comes out
    /// fully opaque. Nothing here changes that - the source file's format is preserved on
    /// purpose, and a pack shipping .jpg usually has its reasons - this only makes the loss
    /// visible instead of silent. Harmless for an opaque color texture; on a MERS layer it
    /// means the subsurface channel is gone. Early-exits on the first non-opaque pixel, so
    /// the common (fully opaque) case costs one pass and the bad case costs almost nothing.
    /// </summary>
    private static void WarnIfAlphaWillBeLost(Bitmap bmp, string originalPath)
    {
        using var fb = new FastBitmap(bmp, writable: false);

        for (var y = 0; y < fb.Height; y++)
            for (var x = 0; x < fb.Width; x++)
                if (fb[x, y].A != 255)
                {
                    Trace.WriteLine($"[TUNER] '{Path.GetFileName(originalPath)}' has transparency but is a JPEG, which cannot store an alpha channel - it will be written back fully opaque. If this is a MERS layer, that is its subsurface data.");
                    return;
                }
    }

    /// <summary>
    /// The bitmap as Format32bppArgb, converting only if it isn't already.
    ///
    /// <para>Everything this app loads goes through <see cref="ReadImage"/>, which always
    /// allocates that format, so in practice this returns its argument and costs nothing. It
    /// exists for the write-back path, where the source could in principle be anything, and
    /// because <see cref="FastBitmap"/> reads raw bytes in that exact layout.</para>
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

    /// <summary>
    /// The GDI+ encoder for a format, needed only where an encoder <i>parameter</i> has to be
    /// passed - JPEG quality. Null if the codec isn't registered, which the caller treats as
    /// "fall back to a plain Save".
    /// </summary>
    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.FormatID == format.Guid)
                return codec;
        return null;
    }
}
