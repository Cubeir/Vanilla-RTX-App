using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

// This file holds two small, closely related pieces of "what should this run do" state -
// the artist-authored per-block JSON schema, and the UI-facing per-run toggles - rather
// than splitting them into separate files for subsystems this small.

#region Materials Schema (materials.json)

/// <summary>
/// Min/max/invert triple per output MERS channel. Values are 0-255 target ranges that
/// the greyscaled color texture gets stretched into (see PbrGeneration.MersGenerator).
/// Roughness min/max default to a fully-rough, non-reflective fallback so that
/// forgetting to define a block never accidentally makes it look wet/metallic.
/// </summary>
public sealed class MerParams
{
    [JsonPropertyName("metal_min")] public int MetalMin { get; set; }
    [JsonPropertyName("metal_max")] public int MetalMax { get; set; }
    [JsonPropertyName("emissive_min")] public int EmissiveMin { get; set; }
    [JsonPropertyName("emissive_max")] public int EmissiveMax { get; set; }
    [JsonPropertyName("roughness_min")] public int RoughnessMin { get; set; } = 201;
    [JsonPropertyName("roughness_max")] public int RoughnessMax { get; set; } = 249;

    [JsonPropertyName("invert_metal")] public bool InvertMetal { get; set; }
    [JsonPropertyName("invert_emissive")] public bool InvertEmissive { get; set; }
    [JsonPropertyName("invert_roughness")] public bool InvertRoughness { get; set; } = true;
}

/// <summary>
/// Subsurface-scattering opacity range, written into the MERS alpha channel based on the
/// *unaltered* luminosity of the color texture: darkest pixel of the block -> Min,
/// brightest -> Max, everything else interpolated. Min == Max == 0 means "no SSS" - the
/// correct default for the overwhelming majority of blocks. Always applied - there's no
/// run-time toggle for this; whether a shader chooses to read it is downstream of us.
/// </summary>
public sealed class SssParams
{
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
}

/// <summary>
/// One recursive/advanced MERS pass. `Channel` selects which channel of the *color*
/// texture is used to extract a dominance mask (legacy RTX Reactor's AdjustColorChannels
/// advanced-gen branch, generalized off of always-red/furnace-only). The masked region
/// then gets its own independent MERS pass, alpha-blended back over the base MERS using
/// the mask's per-pixel opacity.
/// </summary>
public sealed class RecursivePass
{
    /// <summary>"R", "G", or "B" - which channel of the color texture to extract a
    /// local-dominance mask from.</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "R";

    [JsonPropertyName("mer")] public MerParams Mer { get; set; } = new();

    /// <summary>Optional. If omitted, the base entry's SSS (if any) is left alone inside
    /// the masked region as well - this only needs to be set when the recursive region
    /// should scatter light differently than the rest of the block.</summary>
    [JsonPropertyName("sss")] public SssParams? Sss { get; set; }
}

public sealed class HeightmapParams
{
    /// <summary>0.0 (fully flattened toward a neutral median - good for very smooth
    /// blocks) .. 1.0 (full quantized contrast, the default). Applied as the last step
    /// of heightmap generation, after quantization.</summary>
    [JsonPropertyName("intensity")] public double Intensity { get; set; } = 1.0;

    /// <summary>Full color inversion. Workaround for the game-side bug where certain
    /// assets always render their heightmap inverted.</summary>
    [JsonPropertyName("invert")] public bool Invert { get; set; }
}

public sealed class NormalParams
{
    /// <summary>0.0 (fully flattened toward flat-up) .. 1.0 (full raw detail). Default
    /// 0.5 - the raw Sobel-derived normal is noticeably stronger than most blocks want.
    /// Applied after blur, blending the computed normal toward flat-up (128,128,255) by
    /// (1 - intensity). See PbrGeneration.NormalMapGenerator.</summary>
    [JsonPropertyName("intensity")] public double Intensity { get; set; } = 0.5;

    /// <summary>Inverts the red and green channels post-generation. Workaround for the
    /// game-side bug where certain assets always render their normal map inverted. Never
    /// affects the blue channel - that's always parallax-occlusion height data, see
    /// PbrGeneration.NormalMapGenerator.</summary>
    [JsonPropertyName("invert")] public bool Invert { get; set; }
}

/// <summary>One resolved entry - either an exact texture-name match, or the "default"
/// fallback entry that's used for anything materials.json doesn't explicitly cover.</summary>
public sealed class MaterialEntry
{
    [JsonPropertyName("mer")] public MerParams Mer { get; set; } = new();
    [JsonPropertyName("sss")] public SssParams Sss { get; set; } = new();
    [JsonPropertyName("recursive")] public List<RecursivePass> Recursive { get; set; } = new();
    [JsonPropertyName("heightmap")] public HeightmapParams Heightmap { get; set; } = new();
    [JsonPropertyName("normal")] public NormalParams Normal { get; set; } = new();
}

/// <summary>Loads materials.json and resolves exact-name-or-default.</summary>
public sealed class MaterialsConfig
{
    private const string DefaultKey = "default";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, MaterialEntry> _entries;
    private readonly MaterialEntry _default;

    private MaterialsConfig(Dictionary<string, MaterialEntry> entries)
    {
        _entries = entries;

        if (!_entries.TryGetValue(DefaultKey, out var def))
        {
            Trace.WriteLine("[ALCHITEX] materials.json has no \"default\" entry - falling back to a built-in neutral default. Every block not explicitly listed will get a plain, fully-rough, non-metallic, non-emissive MERS.");
            def = new MaterialEntry();
        }
        _default = def;
    }

    /// <summary>
    /// Loads and parses materials.json from disk. Never throws - on any failure this
    /// logs and returns a config that only has the built-in neutral default, so a
    /// malformed or missing materials.json degrades to "plain MERS for everything"
    /// rather than crashing the whole run.
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
            var parsed = JsonSerializer.Deserialize<Dictionary<string, MaterialEntry>>(raw, ReadOptions);

            if (parsed == null || parsed.Count == 0)
            {
                Trace.WriteLine($"[ALCHITEX] materials.json at '{materialsJsonPath}' parsed to empty - using built-in neutral default only.");
                return new MaterialsConfig(new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase));
            }

            var caseInsensitive = new Dictionary<string, MaterialEntry>(parsed, StringComparer.OrdinalIgnoreCase);
            return new MaterialsConfig(caseInsensitive);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to load materials.json at '{materialsJsonPath}': {ex.Message}. Falling back to built-in neutral default for every texture.");
            return new MaterialsConfig(new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Resolves the entry for one texture by its exact file name (no extension, no path,
    /// case-insensitive). Falls back to the "default" entry - and if that's also missing,
    /// to a hardcoded neutral entry - so this never returns null.
    /// </summary>
    public MaterialEntry Resolve(string textureNameWithoutExtension)
    {
        if (_entries.TryGetValue(textureNameWithoutExtension, out var entry))
            return entry;

        return _default;
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
/// </summary>
public sealed record AlchitexOptions(
    SecondaryPbrMode SecondaryPbr,
    bool AddFog)
{
    /// <summary>
    /// Auto mode's per-texture rule, decided once we know a given color texture's width:
    /// <= 32px -> heightmap (too little resolution for normal-map detail to read as
    /// anything but noise), > 32px -> normal map.
    /// </summary>
    public const int AutoModeHeightmapMaxWidth = 32;

    /// <summary>
    /// Ceiling for an *explicit* Heightmap selection (TextureSetOrchestrator.
    /// ResolveSecondaryMode): above this width a normal map is generated instead - a
    /// heightmap texture set above this size no longer manifests correctly in-game.
    /// </summary>
    public const int ExplicitHeightmapMaxWidth = 64;

    public static readonly AlchitexOptions Default = new(SecondaryPbrMode.Auto, AddFog: false);
}

#endregion
