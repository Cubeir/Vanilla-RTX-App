using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

// This file holds three small, closely related pieces of "what should this run do" state -
// the artist-authored per-block JSON schema, the PBR blacklist, and the UI-facing per-run
// toggles - rather than splitting them into separate files for subsystems this small.

#region Materials Schema (materials.json)

// ══════════════════════════════════════════════════════════════════════════════════════
// Two layers of types live here, and the split is load-bearing:
//
//   * The DTOs (MerParams, SssParams, ...) are what JSON deserializes into. EVERY property
//     on them is nullable, and that is the whole point: without nullability there is no way
//     to tell "the artist wrote 0" from "the artist wrote nothing", so a per-property
//     fallback to the "default" entry is impossible. A non-nullable int silently reads as 0
//     for both cases.
//
//   * The Resolved* types are what the generators consume. Every value is present, in
//     range, and already merged. Nothing downstream of MaterialsConfig.Resolve ever has to
//     null-check or clamp.
//
// The merge is: this entry's value, else the "default" entry's value, else a built-in
// constant. So an entry can say nothing but {"mer": {"emissive_max": 200}} and inherit the
// rest of the artist's own defaults rather than the code's - which is what makes
// materials.json editable by hand without repeating every field on every block.
// ══════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Min/max/invert triple per output MERS channel. Values are 0-255 target ranges that
/// the greyscaled color texture gets stretched into (see PbrGeneration.MersGenerator).
/// All nullable - see the note above. Defaults live in MaterialDefaults.
/// </summary>
public sealed class MerParams
{
    [JsonPropertyName("metal_min")] public int? MetalMin { get; set; }
    [JsonPropertyName("metal_max")] public int? MetalMax { get; set; }
    [JsonPropertyName("emissive_min")] public int? EmissiveMin { get; set; }
    [JsonPropertyName("emissive_max")] public int? EmissiveMax { get; set; }
    [JsonPropertyName("roughness_min")] public int? RoughnessMin { get; set; }
    [JsonPropertyName("roughness_max")] public int? RoughnessMax { get; set; }

    [JsonPropertyName("invert_metal")] public bool? InvertMetal { get; set; }
    [JsonPropertyName("invert_emissive")] public bool? InvertEmissive { get; set; }
    [JsonPropertyName("invert_roughness")] public bool? InvertRoughness { get; set; }
}

/// <summary>
/// Subsurface-scattering opacity range, written into the MERS alpha channel from the
/// *unaltered* luminosity of the color texture: darkest pixel -> Min, brightest -> Max.
/// Min == Max == 0 means "no SSS", the right default for most blocks.
///
/// `invert` flips which end of the texture's brightness gets which end of the range, so
/// the darkest pixels scatter most instead of least - plenty of materials have their
/// translucency in the dark parts (thin dark leaves, resin in dark wood) rather than the
/// bright ones. An artistic choice, like the three MER inverts; the game-bug workarounds
/// are the heightmap and normal inverts, and only those two.
/// </summary>
public sealed class SssParams
{
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("invert")] public bool? Invert { get; set; }
}

/// <summary>
/// One recursive/advanced MERS pass. `channel` selects which channel of the *color*
/// texture is tested for dominance; the pixels where it dominates get their own
/// independent MER pass blended back over the base MERS, weighted per pixel by how
/// strongly that channel actually dominates there (see MersGenerator.ApplyRecursivePass).
///
/// There is deliberately no `sss` here. Subsurface is the final pass over the finished
/// MERS alpha, computed once from the whole texture's luminosity - a per-region override
/// of it never had a real use, and having it invited the idea that recursion happens
/// after subsurface rather than before.
///
/// Multiple passes are normal - one per channel, or several on the same channel with
/// different ranges. They apply in file order, each blending over the result of the last.
/// </summary>
public sealed class RecursivePass
{
    /// <summary>"R", "G", or "B" - which channel of the color texture must dominate.</summary>
    [JsonPropertyName("channel")] public string? Channel { get; set; }

    [JsonPropertyName("mer")] public MerParams? Mer { get; set; }
}

/// <summary>
/// "Invisible emission" - light emitted from pixels the player can never see.
///
/// Minecraft RTX takes emitted light's COLOUR from the colour texture and its STRENGTH from
/// the MERS green channel, and it does that per pixel regardless of whether the pixel is
/// visible. So a fully transparent pixel (alpha 0) that still carries RGB and emissive data
/// glows without drawing anything. Vanilla RTX uses this heavily: monster spawners, torches,
/// trial spawners - anything whose lit interior is a hole in the artwork rather than a
/// painted surface.
///
/// Both halves are required and neither works alone, which is why they're one section:
/// strength with no colour emits black (albedo x emissive = nothing), and colour with no
/// strength emits nothing at all. Strength is what switches the feature on - 0 means off,
/// which is the default for every texture that doesn't explicitly want it.
///
/// Applied to EVERY alpha-0 pixel in the texture (see PbrGeneration.InvisibleEmission), so
/// it's per-texture art direction, not something to put on the "default" entry.
/// </summary>
public sealed class InvisibleEmissionParams
{
    /// <summary>RGB triplet, 0-255 each. Defaults to white, deliberately: if someone sets a
    /// strength and forgets the colour, neutral light is a far better failure than the
    /// silent nothing that black would emit.</summary>
    [JsonPropertyName("color")] public List<int>? Color { get; set; }

    /// <summary>0-255 emissive written into the MERS green channel of those pixels.
    /// 0 disables the whole feature for this texture.</summary>
    [JsonPropertyName("strength")] public int? Strength { get; set; }
}

public sealed class HeightmapParams
{
    /// <summary>0.0 (fully flattened toward a neutral median - good for very smooth
    /// blocks) .. 1.0 (full quantized contrast, the default). Applied as the last step
    /// of heightmap generation, after quantization.</summary>
    [JsonPropertyName("intensity")] public double? Intensity { get; set; }

    /// <summary>Full color inversion. Workaround for the game-side bug where certain
    /// assets always render their heightmap inverted.</summary>
    [JsonPropertyName("invert")] public bool? Invert { get; set; }
}

public sealed class NormalParams
{
    /// <summary>0.0 (fully flattened toward flat-up) .. 1.0 (full raw detail). Default
    /// 0.25 - the raw Sobel-derived normal is noticeably stronger than most blocks want.
    /// Scales the surface slope before the normal is built, so every value still produces
    /// a true unit normal. See PbrGeneration.NormalMapGenerator.</summary>
    [JsonPropertyName("intensity")] public double? Intensity { get; set; }

    /// <summary>Inverts the red and green channels post-generation. Workaround for the
    /// game-side bug where certain assets always render their normal map inverted. Never
    /// affects the blue channel - that's always parallax-occlusion height data, see
    /// PbrGeneration.NormalMapGenerator.</summary>
    [JsonPropertyName("invert")] public bool? Invert { get; set; }
}

/// <summary>One entry as written in materials.json - either an exact texture-name match or
/// the "default" entry. Every member is optional; see the note at the top of this region.</summary>
public sealed class MaterialEntry
{
    [JsonPropertyName("mer")] public MerParams? Mer { get; set; }
    [JsonPropertyName("sss")] public SssParams? Sss { get; set; }
    [JsonPropertyName("recursive")] public List<RecursivePass>? Recursive { get; set; }
    [JsonPropertyName("invisible_emission")] public InvisibleEmissionParams? InvisibleEmission { get; set; }
    [JsonPropertyName("heightmap")] public HeightmapParams? Heightmap { get; set; }
    [JsonPropertyName("normal")] public NormalParams? Normal { get; set; }
}

// ── Resolved forms: what the generators actually read ─────────────────────────────────

public readonly record struct ResolvedMer(
    int MetalMin, int MetalMax,
    int EmissiveMin, int EmissiveMax,
    int RoughnessMin, int RoughnessMax,
    bool InvertMetal, bool InvertEmissive, bool InvertRoughness);

public readonly record struct ResolvedSss(int Min, int Max, bool Invert);

public readonly record struct ResolvedInvisibleEmission(int R, int G, int B, int Strength)
{
    /// <summary>Strength is the switch - see InvisibleEmissionParams.</summary>
    public bool IsEnabled => Strength > 0;
}

public readonly record struct ResolvedRecursivePass(string Channel, ResolvedMer Mer);

public readonly record struct ResolvedHeightmap(double Intensity, bool Invert);

public readonly record struct ResolvedNormal(double Intensity, bool Invert);

public sealed class ResolvedMaterial
{
    public ResolvedMer Mer { get; init; }
    public ResolvedSss Sss { get; init; }
    public IReadOnlyList<ResolvedRecursivePass> Recursive { get; init; } = Array.Empty<ResolvedRecursivePass>();
    public ResolvedInvisibleEmission InvisibleEmission { get; init; }
    public ResolvedHeightmap Heightmap { get; init; }
    public ResolvedNormal Normal { get; init; }
}

/// <summary>
/// The values used when neither the texture's own entry nor the "default" entry says
/// anything. These are the last line of defence, not the expected source of values - a
/// real materials.json defines "default" and these never come up.
/// </summary>
public static class MaterialDefaults
{
    public const int MetalMin = 0;
    public const int MetalMax = 0;
    public const int EmissiveMin = 0;
    public const int EmissiveMax = 0;

    // Fully rough and non-reflective, so a block nobody configured can never accidentally
    // come out looking wet or metallic.
    public const int RoughnessMin = 192;
    public const int RoughnessMax = 255;

    public const bool InvertMetal = false;
    public const bool InvertEmissive = false;
    public const bool InvertRoughness = true;

    public const int SssMin = 0;
    public const int SssMax = 0;
    public const bool SssInvert = false;

    // White, so a strength set without a colour still emits neutral light instead of the
    // nothing that black would give. Strength 0 = the feature is off, which is the default.
    public const int InvisibleEmissionR = 255;
    public const int InvisibleEmissionG = 255;
    public const int InvisibleEmissionB = 255;
    public const int InvisibleEmissionStrength = 0;

    public const double HeightmapIntensity = 1.0;
    public const bool HeightmapInvert = false;

    public const double NormalIntensity = 0.25;
    public const bool NormalInvert = false;

    public const string RecursiveChannel = "R";
}

// =====================================================================================
// AlchitexJsonContext - source-generated JSON metadata for trim-safe (de)serialization,
// same approach as Core/OnlineTexts.cs's OnlineTextsJsonContext.
//
// This is NOT optional polish. Release builds set PublishTrimmed, and every one of these
// shapes was previously (de)serialized reflectively, so the trimmer had no way to see
// that MaterialEntry's property setters are ever called and stripped them. The failure
// was silent and nasty: MaterialsConfig.Load and PbrBlacklist.Load both swallow their
// exceptions and degrade to "neutral default for everything", so a trimmed Release build
// quietly generated flat default PBR for every block and ignored the blacklist entirely,
// while Debug worked perfectly. Anything new that (de)serializes a materials.json shape
// needs a [JsonSerializable] line here and a Default.<Type> call site, not a bare
// JsonSerializer.Deserialize<T>.
//
// Read options mirror what the hand-rolled JsonSerializerOptions used to set: hand-edited
// pack files carry comments and trailing commas, and property names shouldn't be
// case-sensitive.
// =====================================================================================
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    // Now that every property is nullable, a null means "the artist didn't write this" -
    // so writing it back out as an explicit null would turn every hand-written partial
    // entry into a wall of nulls the first time MaterialsBootstrapper rewrites the file.
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, MaterialEntry>))]
[JsonSerializable(typeof(MaterialEntry))]
[JsonSerializable(typeof(List<string>))]
// InvisibleEmissionParams.Color is a List<int>. Nested types get picked up through
// MaterialEntry, but this shape is listed explicitly for the same reason everything else
// here is: a missing metadata entry fails silently, and only in a trimmed Release build.
[JsonSerializable(typeof(List<int>))]
internal partial class AlchitexJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads materials.json and resolves a texture name to a fully-populated ResolvedMaterial.
///
/// Forgiving on purpose, in three separate ways, because this file is hand-edited across
/// thousands of entries and a single typo must never cost a whole generation run:
///
///   1. A missing FILE, unparseable JSON, or an empty document degrades to the built-in
///      defaults for everything, logged, never thrown.
///   2. A missing PROPERTY falls through to the "default" entry, then to MaterialDefaults.
///      Every level is per-property, so partial entries are normal and expected.
///   3. An OUT-OF-RANGE value is clamped into range and logged, rather than rejected. A
///      "roughness_max": 300 is obviously meant to be 255, and refusing to generate over
///      it would help nobody.
///
/// Resolution is cached per texture name: the merge and its logging happen once per entry
/// no matter how many textures resolve to it.
/// </summary>
public sealed class MaterialsConfig
{
    private const string DefaultKey = "default";

    private readonly Dictionary<string, MaterialEntry> _entries;
    private readonly MaterialEntry _default;
    private readonly Dictionary<string, ResolvedMaterial> _resolvedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResolvedMaterial _resolvedDefault;

    private MaterialsConfig(Dictionary<string, MaterialEntry> entries)
    {
        _entries = entries;

        if (!_entries.TryGetValue(DefaultKey, out var def))
        {
            Trace.WriteLine("[ALCHITEX] materials.json has no \"default\" entry - falling back to built-in defaults. Every block not explicitly listed will get a plain, fully-rough, non-metallic, non-emissive MERS.");
            def = new MaterialEntry();
        }

        _default = def;
        _resolvedDefault = Merge(_default, DefaultKey);
    }

    /// <summary>
    /// Loads and parses materials.json from disk. Never throws - on any failure this
    /// logs and returns a config that only has the built-in defaults, so a malformed or
    /// missing materials.json degrades to "plain MERS for everything" rather than
    /// crashing the whole run.
    /// </summary>
    public static MaterialsConfig Load(string materialsJsonPath)
    {
        try
        {
            var raw = File.ReadAllText(materialsJsonPath);

            // Top-level shape is a flat dictionary: exact-texture-name (or "default") ->
            // entry. Comment-style keys like "// comment" are valid JSON string keys and
            // simply become unused dictionary entries; they're never looked up so they're
            // harmless - this lets materials.json carry human-readable notes without a
            // custom parser, at the cost of "// comment" keys sitting in memory unused.
            var parsed = JsonSerializer.Deserialize(raw, AlchitexJsonContext.Default.DictionaryStringMaterialEntry);

            if (parsed == null || parsed.Count == 0)
            {
                Trace.WriteLine($"[ALCHITEX] materials.json at '{materialsJsonPath}' parsed to empty - using built-in defaults only.");
                return new MaterialsConfig(new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase));
            }

            var caseInsensitive = new Dictionary<string, MaterialEntry>(parsed, StringComparer.OrdinalIgnoreCase);
            return new MaterialsConfig(caseInsensitive);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to load materials.json at '{materialsJsonPath}': {ex.Message}. Falling back to built-in defaults for every texture.");
            return new MaterialsConfig(new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Resolves one texture by its exact file name (no extension, no path,
    /// case-insensitive) into a fully-populated material. Never returns null, never throws.
    /// </summary>
    public ResolvedMaterial Resolve(string textureNameWithoutExtension)
    {
        if (string.IsNullOrEmpty(textureNameWithoutExtension)) return _resolvedDefault;

        if (_resolvedCache.TryGetValue(textureNameWithoutExtension, out var cached))
            return cached;

        if (!_entries.TryGetValue(textureNameWithoutExtension, out var entry))
            return _resolvedDefault;

        var resolved = Merge(entry, textureNameWithoutExtension);
        _resolvedCache[textureNameWithoutExtension] = resolved;
        return resolved;
    }

    // ── Merge: entry value, else "default" entry's value, else MaterialDefaults ────────

    private ResolvedMaterial Merge(MaterialEntry entry, string entryName)
    {
        var mer = entry.Mer;
        var defMer = _default.Mer;

        var sss = entry.Sss;
        var defSss = _default.Sss;

        var heightmap = entry.Heightmap;
        var defHeightmap = _default.Heightmap;

        var normal = entry.Normal;
        var defNormal = _default.Normal;

        return new ResolvedMaterial
        {
            Mer = MergeMer(mer, defMer, entryName, "mer"),
            Sss = new ResolvedSss(
                Channel(sss?.Min ?? defSss?.Min, MaterialDefaults.SssMin, entryName, "sss.min"),
                Channel(sss?.Max ?? defSss?.Max, MaterialDefaults.SssMax, entryName, "sss.max"),
                sss?.Invert ?? defSss?.Invert ?? MaterialDefaults.SssInvert),
            Recursive = MergeRecursive(entry, entryName),
            InvisibleEmission = MergeInvisibleEmission(entry, entryName),
            Heightmap = new ResolvedHeightmap(
                Unit(heightmap?.Intensity ?? defHeightmap?.Intensity, MaterialDefaults.HeightmapIntensity, entryName, "heightmap.intensity"),
                heightmap?.Invert ?? defHeightmap?.Invert ?? MaterialDefaults.HeightmapInvert),
            Normal = new ResolvedNormal(
                Unit(normal?.Intensity ?? defNormal?.Intensity, MaterialDefaults.NormalIntensity, entryName, "normal.intensity"),
                normal?.Invert ?? defNormal?.Invert ?? MaterialDefaults.NormalInvert),
        };
    }

    private ResolvedMer MergeMer(MerParams? mer, MerParams? defMer, string entryName, string path) => new(
        Channel(mer?.MetalMin ?? defMer?.MetalMin, MaterialDefaults.MetalMin, entryName, $"{path}.metal_min"),
        Channel(mer?.MetalMax ?? defMer?.MetalMax, MaterialDefaults.MetalMax, entryName, $"{path}.metal_max"),
        Channel(mer?.EmissiveMin ?? defMer?.EmissiveMin, MaterialDefaults.EmissiveMin, entryName, $"{path}.emissive_min"),
        Channel(mer?.EmissiveMax ?? defMer?.EmissiveMax, MaterialDefaults.EmissiveMax, entryName, $"{path}.emissive_max"),
        Channel(mer?.RoughnessMin ?? defMer?.RoughnessMin, MaterialDefaults.RoughnessMin, entryName, $"{path}.roughness_min"),
        Channel(mer?.RoughnessMax ?? defMer?.RoughnessMax, MaterialDefaults.RoughnessMax, entryName, $"{path}.roughness_max"),
        mer?.InvertMetal ?? defMer?.InvertMetal ?? MaterialDefaults.InvertMetal,
        mer?.InvertEmissive ?? defMer?.InvertEmissive ?? MaterialDefaults.InvertEmissive,
        mer?.InvertRoughness ?? defMer?.InvertRoughness ?? MaterialDefaults.InvertRoughness);

    /// <summary>
    /// Recursive passes are taken whole from whichever entry defines them - this one, else
    /// "default" - rather than merged element by element. A pass list is a description of
    /// specific regions of a specific texture; splicing one entry's second pass into
    /// another's list would produce something nobody wrote. Within a pass, though, the
    /// individual mer values still fall back to "default".mer as usual.
    /// </summary>
    private IReadOnlyList<ResolvedRecursivePass> MergeRecursive(MaterialEntry entry, string entryName)
    {
        var passes = entry.Recursive ?? _default.Recursive;
        if (passes == null || passes.Count == 0) return Array.Empty<ResolvedRecursivePass>();

        var resolved = new List<ResolvedRecursivePass>(passes.Count);

        for (var i = 0; i < passes.Count; i++)
        {
            var pass = passes[i];
            if (pass == null) continue;

            resolved.Add(new ResolvedRecursivePass(
                NormalizeChannel(pass.Channel, entryName, i),
                MergeMer(pass.Mer, _default.Mer, entryName, $"recursive[{i}].mer")));
        }

        return resolved;
    }

    /// <summary>
    /// Falls back per property like everything else, so an entry can set a strength and
    /// inherit a colour. The one thing worth knowing: because it DOES fall back, putting an
    /// enabled invisible_emission on the "default" entry would light the alpha-0 pixels of
    /// every texture in the pack. Strength defaults to 0 precisely so that never happens by
    /// accident - see InvisibleEmissionParams.
    /// </summary>
    private ResolvedInvisibleEmission MergeInvisibleEmission(MaterialEntry entry, string entryName)
    {
        var emission = entry.InvisibleEmission;
        var defEmission = _default.InvisibleEmission;

        var color = emission?.Color ?? defEmission?.Color;

        var r = MaterialDefaults.InvisibleEmissionR;
        var g = MaterialDefaults.InvisibleEmissionG;
        var b = MaterialDefaults.InvisibleEmissionB;

        if (color != null)
        {
            if (color.Count >= 3)
            {
                r = Channel(color[0], r, entryName, "invisible_emission.color[0]");
                g = Channel(color[1], g, entryName, "invisible_emission.color[1]");
                b = Channel(color[2], b, entryName, "invisible_emission.color[2]");
            }
            else
            {
                Trace.WriteLine($"[ALCHITEX] materials.json entry '{entryName}': invisible_emission.color needs 3 values, got {color.Count} - using white.");
            }
        }

        var strength = Channel(
            emission?.Strength ?? defEmission?.Strength,
            MaterialDefaults.InvisibleEmissionStrength,
            entryName,
            "invisible_emission.strength");

        return new ResolvedInvisibleEmission(r, g, b, strength);
    }

    private static string NormalizeChannel(string? channel, string entryName, int index)
    {
        var normalized = channel?.Trim().ToUpperInvariant();

        if (normalized is "R" or "G" or "B") return normalized;

        if (!string.IsNullOrEmpty(channel))
        {
            Trace.WriteLine($"[ALCHITEX] materials.json entry '{entryName}' recursive[{index}] has channel '{channel}', which isn't R, G or B - using {MaterialDefaults.RecursiveChannel}.");
        }

        return MaterialDefaults.RecursiveChannel;
    }

    /// <summary>A 0-255 channel value: absent falls back, out-of-range is clamped and
    /// logged rather than treated as an error.</summary>
    private static int Channel(int? value, int fallback, string entryName, string path)
    {
        if (value is not int v) return fallback;
        if (v is >= 0 and <= 255) return v;

        var clamped = Math.Clamp(v, 0, 255);
        Trace.WriteLine($"[ALCHITEX] materials.json entry '{entryName}': {path} is {v}, outside 0-255 - clamped to {clamped}.");
        return clamped;
    }

    /// <summary>Same, for the 0.0-1.0 intensities.</summary>
    private static double Unit(double? value, double fallback, string entryName, string path)
    {
        if (value is not double v) return fallback;
        if (double.IsNaN(v))
        {
            Trace.WriteLine($"[ALCHITEX] materials.json entry '{entryName}': {path} isn't a number - using {fallback}.");
            return fallback;
        }
        if (v is >= 0.0 and <= 1.0) return v;

        var clamped = Math.Clamp(v, 0.0, 1.0);
        Trace.WriteLine($"[ALCHITEX] materials.json entry '{entryName}': {path} is {v}, outside 0.0-1.0 - clamped to {clamped}.");
        return clamped;
    }
}

#endregion

#region PBR Blacklist (pbr_blacklist.json)

/// <summary>
/// Textures matching a pattern here still get a .texture_set.json written by
/// TextureSetOrchestrator, but a minimal one - color only, no
/// metalness_emissive_roughness_subsurface/normal/heightmap keys at all - rather than
/// getting full PBR generated. Lives next to materials.json (Assets/pbr_blacklist.json) as
/// a flat JSON array of patterns, exact-match unless a pattern contains '*' (matched
/// anywhere in the pattern, standard glob semantics - e.g. "*_carried" matches any name
/// ending in "_carried"). Case-insensitive throughout, same as materials.json's own
/// exact-name matching.
/// </summary>
public sealed class PbrBlacklist
{
    private readonly List<string> _patterns;

    private PbrBlacklist(List<string> patterns) => _patterns = patterns;

    /// <summary>
    /// Loads pbr_blacklist.json from disk. Never throws - on any failure this logs and
    /// returns an empty blacklist, so a malformed or missing file just means nothing gets
    /// blacklisted rather than crashing the whole run.
    /// </summary>
    public static PbrBlacklist Load(string blacklistJsonPath)
    {
        try
        {
            var raw = File.ReadAllText(blacklistJsonPath);
            var parsed = JsonSerializer.Deserialize(raw, AlchitexJsonContext.Default.ListString) ?? new List<string>();
            return new PbrBlacklist(parsed.Select(p => p.ToLowerInvariant()).ToList());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to load pbr_blacklist.json at '{blacklistJsonPath}': {ex.Message}. No textures will be blacklisted for this run.");
            return new PbrBlacklist(new List<string>());
        }
    }

    /// <summary>Exact-name-or-default (no path, no extension, case-insensitive) - same
    /// input shape as MaterialsConfig.Resolve.</summary>
    public bool IsBlacklisted(string textureNameWithoutExtension)
    {
        var name = textureNameWithoutExtension.ToLowerInvariant();
        return _patterns.Any(pattern => MatchesPattern(name, pattern));
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (!pattern.Contains('*'))
            return name == pattern;

        var regexPattern = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(name, regexPattern);
    }
}

#endregion

#region Run Options (UI-facing)

/// <summary>
/// Maps 1:1 to the "Secondary PBR texture" dropdown in the Alchitex window
/// (None / Auto / Normal map / Heightmap).
/// </summary>
public enum SecondaryPbrMode
{
    None,
    Auto,
    Normal,
    Heightmap,
}

/// <summary>
/// Per-run options resolved from the Alchitex window's controls before the pipeline
/// starts. Kept as a plain, immutable record so the pipeline never reaches back into UI
/// state mid-run - once RunAsync is called, the run's behavior is fully pinned down.
///
/// Every texture set Alchitex writes is always MERS (never plain MER) and always gets its
/// materials.json-authored SSS applied - there's no run-time toggle for this. A shader
/// choosing not to read it downstream (same as POM) isn't something to clutter generation
/// with.
///
/// StripExistingPbr is the one option that isn't a window-wide setting: it's decided per
/// pack, from that pack's own confirmation dialog (AlchitexWindow), and the batch loop
/// applies it with `options with { StripExistingPbr = true }` for the packs the user agreed
/// to have regenerated. See PbrStripper.
/// </summary>
public sealed record AlchitexOptions(
    SecondaryPbrMode SecondaryPbr,
    bool AddFog,
    bool StripExistingPbr = false)
{
    /// <summary>
    /// Auto mode's per-texture rule, decided once we know a given color texture's width:
    /// <= 16px -> heightmap (too little resolution for normal-map detail to read as
    /// anything but noise), > 16px -> normal map.
    /// </summary>
    public const int AutoModeHeightmapMaxWidth = 16;

    /// <summary>
    /// Ceiling for an *explicit* Heightmap selection (TextureSetOrchestrator.
    /// ResolveSecondaryMode): above this width a normal map is generated instead - a
    /// heightmap texture set above this size no longer manifests itself correctly in-game.
    /// </summary>
    public const int ExplicitHeightmapMaxWidth = 32;

    public static readonly AlchitexOptions Default = new(SecondaryPbrMode.Auto, AddFog: false);
}

#endregion
