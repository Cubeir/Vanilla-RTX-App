using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

// =====================================================================================
// PIPELINE INSTRUMENTATION
//
// Two pieces of scaffolding that exist so the PBR pipeline can be *inspected and tuned*
// from the Pipeline Preview dev tool (Tools/PipelinePreviewWindow) without the pipeline
// itself having to know that tool exists:
//
//   1. PipelineTuning - every non-artist-facing magic number the generators use, moved
//      out of `private const` and into one place that can be handed a different instance.
//   2. PipelineTrace  - a write-only sink the generators push their intermediate stages
//      into. Compiled out entirely in Release, and inert in Debug unless a sink is
//      explicitly armed on the calling thread.
//
// Both are deliberately in Core/ rather than Tools/: the pipeline *emits* to these, and
// Core must never take a dependency on a dev-only Tools type. The tool only reads them.
//
// -- MAINTENANCE CONTRACT (read this before changing the pipeline) --------------------
//
// The whole point of this design is that keeping the dev tool current costs one line.
//
//   * Adding a tuning constant:  add a public field to PipelineTuning with a [Knob]
//     attribute and read it from the generator. The dev tool builds its control panel by
//     reflecting over these fields, so a control appears with no UI work at all. A field
//     with no [Knob] still shows up, just with inferred bounds.
//
//   * Adding a pipeline step:  add one PipelineTrace.* call where the intermediate value
//     exists. The dev tool renders whatever it was handed, in capture order, so a new
//     tile appears with no UI work. Optionally add a friendly title/blurb for the new
//     stage id to PipelineStageCatalog in Tools/PipelinePreviewSupport.cs - if you don't,
//     the tile just shows the raw stage id, which is a cosmetic loss and nothing more.
//
//   * Removing a step:  delete the trace call. The tool shows whatever it gets; a stage
//     that stops being emitted simply stops appearing. Nothing breaks.
//
//   * Changing materials.json's schema:  nothing to do. The dev tool's material editor
//     is a generic reflective walk over MaterialEntry, so new/renamed/removed fields are
//     picked up automatically.
//
// -- COST -----------------------------------------------------------------------------
//
//   * Release: every PipelineTrace method is [Conditional("DEBUG")], so the C# compiler
//     removes the call sites *including their arguments*. No lambda is allocated, no
//     array is built, nothing is evaluated. The tracing is not "cheap" in Release, it is
//     literally absent.
//
//   * Debug, normal pack generation: the first thing every trace method does is check a
//     [ThreadStatic] sink that is null on every thread the real pipeline ever runs on.
//     Arguments are already-live references and closures over already-live locals, so a
//     traced run costs one predicted branch per capture point per texture.
//
//   * Tuning field reads: every generator hoists the tuning values it needs into locals
//     before entering its loops (see PbrGeneration.cs), so no inner loop ever reads
//     through the object. This is a deliberate convention - keep doing it.
// =====================================================================================

#region Tuning

/// <summary>
/// UI metadata for one PipelineTuning field. Purely descriptive: it drives the dev tool's
/// generated control panel and nothing else - the pipeline never reads it. Bounds are
/// suggestions for the control's range, not clamps; the generators still clamp whatever
/// they're actually sensitive to.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class KnobAttribute : Attribute
{
    /// <summary>Panel section this knob is grouped under in the dev tool.</summary>
    public string Group { get; set; } = "General";

    /// <summary>Human-readable name. Falls back to the field name split on casing.</summary>
    public string Label { get; set; } = "";

    /// <summary>What this number actually does, shown as the control's tooltip.</summary>
    public string About { get; set; } = "";

    public double Min { get; set; }
    public double Max { get; set; } = 1.0;

    /// <summary>Control increment. 0 means "pick something sane from the range".</summary>
    public double Step { get; set; }
}

/// <summary>
/// Every tuning constant the PBR generators use, in one mutable bag.
///
/// These used to be `private const` scattered across PbrGeneration.cs. They were moved
/// here so the Pipeline Preview dev tool can re-run a single texture against a modified
/// set without touching source - which is the only way tuning a dozen interacting numbers
/// by eye is tractable at all.
///
/// USAGE RULES:
///   * Treat an instance as immutable once a generation run has started. Every generator
///     copies what it needs into locals up front; mutating an instance mid-run gives
///     undefined results. The dev tool builds a fresh instance per preview run.
///   * `Default` is what the real pipeline uses, and its values are the shipping values -
///     changing a number here changes what every user's generated pack looks like. These
///     are exactly the numbers the dev tool exists to help settle on.
///   * Generators take `PipelineTuning? tuning = null` as a trailing optional parameter
///     and fall back to Default, so production call sites never mention tuning at all.
/// </summary>
public sealed class PipelineTuning
{
    /// <summary>The shipping values. What AlchitexPipeline runs with.</summary>
    public static readonly PipelineTuning Default = new();

    // -- Secondary mode resolution ----------------------------------------------------

    [Knob(Group = "Secondary mode", Label = "Auto: heightmap at or below", Min = 4, Max = 256, Step = 1,
        About = "Auto mode picks a heightmap for textures at or under this width, a normal map above it. Below ~32px there isn't enough resolution for normal-map detail to read as anything but noise.")]
    public int AutoModeHeightmapMaxWidth = 32;

    [Knob(Group = "Secondary mode", Label = "Explicit heightmap ceiling", Min = 4, Max = 512, Step = 1,
        About = "Above this width an explicitly-requested heightmap is overridden to a normal map. The game derives its own bump effect from a heightmap with a simple sobel pass; past this size the resulting bevels are too thin to see and overflow the texture atlas.")]
    public int ExplicitHeightmapMaxWidth = 64;

    // -- MERS -------------------------------------------------------------------------

    [Knob(Group = "MERS", Label = "White pixel mask opacity", Min = 0, Max = 255, Step = 1,
        About = "How strongly a fully-white pixel contributes to a recursive pass's dominance mask. White can't locally dominate any single channel the way a saturated colour can, so it's capped well below full rather than being 0 or 255. Kept from the legacy heuristic.")]
    public int WhitePixelMaskOpacity = 85;

    // -- Heightmap: mean-shift clustering ---------------------------------------------

    [Knob(Group = "Heightmap - mean-shift", Label = "Iterations", Min = 1, Max = 20, Step = 1,
        About = "How many times each pixel's value is replaced by the weighted average of its neighbourhood. More iterations means flatter, more fully-converged plateaus, at linear cost.")]
    public int MeanShiftIterations = 5;

    [Knob(Group = "Heightmap - mean-shift", Label = "Spatial radius", Min = 1, Max = 8, Step = 1,
        About = "Neighbourhood radius in pixels for the joint spatial+range filter. Cost scales with the square of this. Sampled with wraparound so results stay seamless-tileable.")]
    public int SpatialRadius = 2;

    [Knob(Group = "Heightmap - mean-shift", Label = "Spatial sigma", Min = 0.1, Max = 6.0, Step = 0.1,
        About = "How sharply spatial weight falls off with distance inside the radius. Low means only immediate neighbours matter; high makes the whole window count roughly equally.")]
    public double SpatialSigma = 1.5;

    [Knob(Group = "Heightmap - mean-shift", Label = "Range bandwidth", Min = 1.0, Max = 96.0, Step = 1.0,
        About = "How far apart two grey values can be and still get pulled together. Briefly narrowed to 12 to stop mean-shift bridging a shallow mortar line into the plank above it - it fixed that in isolation, but the extra modes it produced turned real output into a posterized greyscale. 24 keeps clustering coarse enough to produce real plateaus.")]
    public double RangeBandwidth = 24.0;

    [Knob(Group = "Heightmap - mean-shift", Label = "Spatial fallback pixel count", Min = 1024, Max = 4194304, Step = 1024,
        About = "Above this many pixels the spatial neighbour search is skipped in favour of range-only clustering off a 256-bin histogram - bounded cost regardless of texture size, which animation strips need. Automatic, not an art knob.")]
    public int SpatialFallbackPixelCount = 256 * 256;

    [Knob(Group = "Heightmap - clustering", Label = "Cluster merge tolerance", Min = 0.0, Max = 64.0, Step = 1.0,
        About = "Two surviving levels closer together than this get folded into one, even when the cluster count is already under the cap. Without this, levels at 200 and 203 both survive as separate heights - near-identical elevations side by side, which is the definition of a heightmap that reads as a mess in game.")]
    public double ClusterMergeTolerance = 8.0;

    [Knob(Group = "Heightmap - clustering", Label = "Max clusters", Min = 2, Max = 16, Step = 1,
        About = "Hard ceiling on distinct elevations, and the single most direct lever on 'this heightmap looks like a mess'. Rank placement spreads the survivors evenly across 0-255, so 4 means 0/85/170/255. A cutout texture can show up to double this, since the transparency overlay produces a darkened variant of each level.")]
    public int MaxClusters = 4;

    [Knob(Group = "Heightmap - output", Label = "Transparency overlay strength", Min = 0.0, Max = 1.0, Step = 0.05,
        About = "How strongly a fully-transparent colour pixel gets darkened by the overlay blend. Exists for grass_side-style regions that are transparent in the colour texture but should still read as sitting beneath the opaque part.")]
    public double TransparencyOverlayStrength = 0.5;

    // -- Normal map -------------------------------------------------------------------

    [Knob(Group = "Normal - height field", Label = "Heightmap blend ratio", Min = 0.0, Max = 1.0, Step = 0.05,
        About = "How much of the normal map's height field comes from the mean-shift clustered heightmap vs. a contrast-maximized greyscale of the colour texture. Higher favours the clean banded clustered result; lower brings back more of the original texture's own shading.")]
    public double HeightmapBlendRatio = 0.75;

    [Knob(Group = "Normal - response", Label = "Clean texture exponent", Min = 0.1, Max = 6.0, Step = 0.05,
        About = "Response-curve exponent at noise index 0. Above 1 crushes small gradient differences and rewards big ones, so only genuine steps produce strong normals and planks/bricks/tiles read crisply.")]
    public double CleanTextureExponent = 2.2;

    [Knob(Group = "Normal - response", Label = "Noisy texture exponent", Min = 0.1, Max = 6.0, Step = 0.05,
        About = "Response-curve exponent at noise index 100. Below 1 lifts small differences, because subtle variation is all a noisy texture has and its edges were never well-defined anyway. If noisy textures come out too busy, raising this toward 1.0 is the first lever to reach for.")]
    public double NoisyTextureExponent = 0.65;

    [Knob(Group = "Normal - response", Label = "Noise calibration ceiling", Min = 1.0, Max = 128.0, Step = 1.0,
        About = "What average per-pixel brightness delta counts as maximally noisy (noise index 100). Lowering this makes more textures read as 'noisy' and pushes them toward the noisy exponent.")]
    public double NoiseCalibrationCeiling = 40.0;

    [Knob(Group = "Normal - gradients", Label = "Gradient reference percentile", Min = 0.5, Max = 1.0, Step = 0.01,
        About = "Which percentile of this texture's own non-flat gradients counts as its full-strength edge. Everything is normalized against that, which is what lets a soft texture still produce a well-formed normal map and a bold one not saturate into a wall of maxed-out edges.")]
    public double GradientReferencePercentile = 0.95;

    [Knob(Group = "Normal - gradients", Label = "Gradient flat threshold", Min = 0.0, Max = 0.1, Step = 0.001,
        About = "Gradients at or below this are treated as flat and excluded from the percentile population. On a texture that's mostly empty space, counting every flat pixel would drag the percentile to nothing and normalize the faint remainder up to full strength.")]
    public double GradientFlatThreshold = 1.0 / 255.0;

    [Knob(Group = "Normal - gradients", Label = "Min gradient reference", Min = 0.0, Max = 0.5, Step = 0.005,
        About = "Floor under the resolved reference, so a genuinely flat texture's faint noise never gets normalized up into a full-strength normal map.")]
    public double MinGradientReference = 0.02;

    [Knob(Group = "Normal - gradients", Label = "Max slope", Min = 0.1, Max = 16.0, Step = 0.1,
        About = "The slope a fully-shaped gradient reaches at normal.intensity = 1.0. At the 0.25 materials.json default this works out to a 45-degree tilt on a texture's strongest edges.")]
    public double MaxSlope = 4.0;

    [Knob(Group = "Normal - gradients", Label = "Gradient histogram bins", Min = 64, Max = 4096, Step = 64,
        About = "Resolution of the fixed-size histogram used to resolve the percentile without sorting every pixel. Structural, not an art knob - raise it only if the reference looks quantized.")]
    public int GradientHistogramBins = 1024;

    [Knob(Group = "Normal - gradients", Label = "Gradient histogram max", Min = 0.5, Max = 8.0, Step = 0.1,
        About = "Top of the histogram's magnitude range. A full 0-to-1 height step across one pixel boundary reads as exactly 1.0, so 2.0 leaves generous headroom. Structural, not an art knob.")]
    public double GradientHistogramMax = 2.0;

    [Knob(Group = "Normal - POM", Label = "POM contrast reduction", Min = 0.0, Max = 1.0, Step = 0.01,
        About = "How much of each pixel's remaining recession from the surface (255) gets pulled back in after the clustered heightmap is ceiling-maximized. The maximize alone still leaves clusters sitting very deep. Shrinking recession can never overflow past 255, so no separate compression pass is needed.")]
    public double PomContrastReduction = 0.67;

    /// <summary>Deep copy. The dev tool edits a clone, so an in-flight preview run can
    /// never observe a half-applied set of values.</summary>
    public PipelineTuning Clone() => (PipelineTuning)MemberwiseClone();

    /// <summary>Every tunable field, in declaration order, with its (possibly inferred)
    /// [Knob] metadata. Declaration order is deliberate - it's roughly pipeline order,
    /// which is the order the dev tool presents them in.</summary>
    public static IReadOnlyList<(FieldInfo Field, KnobAttribute Knob)> Describe()
    {
        return typeof(PipelineTuning)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => (f, f.GetCustomAttribute<KnobAttribute>() ?? Infer(f)))
            .ToList();
    }

    /// <summary>Fallback metadata for a field someone added without a [Knob]. Deliberately
    /// permissive: a knob with guessed bounds beats a knob that silently isn't there.</summary>
    private static KnobAttribute Infer(FieldInfo f) => new()
    {
        Group = "Unsorted",
        Label = SplitCamelCase(f.Name),
        About = "No [Knob] metadata - add one in PipelineTuning to give this a range and a description.",
        Min = 0,
        Max = f.FieldType == typeof(int) ? 255 : 1.0,
        Step = f.FieldType == typeof(int) ? 1 : 0.01,
    };

    internal static string SplitCamelCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(name[i]) : name[i]);
        }
        return sb.ToString();
    }
}

#endregion

#region Trace model

/// <summary>One rendered view of a captured stage - the composite, or a single isolated
/// channel of it. A stage with more than one view gets a view switcher in the dev
/// tool.</summary>
public sealed class PipelineStageView
{
    public PipelineStageView(string name, Bitmap image)
    {
        Name = name;
        Image = image;
    }

    /// <summary>"RGB", "R", "G", "B", "A", "Value", "Regions", ...</summary>
    public string Name { get; }

    public Bitmap Image { get; }
}

/// <summary>A 1-D function sampled for plotting - for things that aren't per-pixel fields
/// at all, like the normal map's gradient response curve.</summary>
public sealed class PipelineCurve
{
    public required string XLabel { get; init; }
    public required string YLabel { get; init; }
    public required double[] X { get; init; }
    public required double[] Y { get; init; }
}

/// <summary>
/// One captured step of the pipeline. Everything about how this is presented lives in the
/// dev tool - this type is pure data and deliberately knows nothing about WinUI.
/// </summary>
public sealed class PipelineStage
{
    public PipelineStage(string id, int order)
    {
        Id = id;
        Order = order;
        var dot = id.IndexOf('.');
        Chain = dot > 0 ? id[..dot] : id;
    }

    /// <summary>Dotted id, e.g. "normal.gradients". The part before the first dot is the
    /// chain the dev tool groups by ("run", "shared", "mers", "height", "normal").</summary>
    public string Id { get; }

    public string Chain { get; }

    /// <summary>Capture order, which is execution order. The dev tool sorts by this rather
    /// than by name, so no naming discipline is needed to keep steps in sequence.</summary>
    public int Order { get; }

    public List<PipelineStageView> Views { get; } = new();
    public List<(string Key, string Value)> Notes { get; } = new();
    public PipelineCurve? Curve { get; set; }
}

/// <summary>
/// Collects everything one traced generation run emitted. Owns the captured bitmaps and
/// disposes them together, so the dev tool's memory story is simply "keep the last run,
/// drop it when the next one lands".
/// </summary>
public sealed class PipelineTraceSink : IDisposable
{
    private readonly List<PipelineStage> _stages = new();
    private readonly Dictionary<string, PipelineStage> _byId = new(StringComparer.Ordinal);
    private bool _disposed;

    public IReadOnlyList<PipelineStage> Stages => _stages;

    /// <summary>Stages are keyed by id: a step captured more than once in a run (e.g.
    /// ColorField's shared derivations, which every generator asks for) updates in place
    /// and keeps its original position rather than appearing three times.</summary>
    internal PipelineStage GetOrAdd(string id)
    {
        if (_byId.TryGetValue(id, out var existing)) return existing;

        var stage = new PipelineStage(id, _stages.Count);
        _stages.Add(stage);
        _byId[id] = stage;
        return stage;
    }

    internal void ReplaceViews(string id, IEnumerable<PipelineStageView> views)
    {
        var stage = GetOrAdd(id);
        foreach (var v in stage.Views) v.Image.Dispose();
        stage.Views.Clear();
        stage.Views.AddRange(views);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var stage in _stages)
            foreach (var view in stage.Views)
                view.Image.Dispose();

        _stages.Clear();
        _byId.Clear();
    }
}

#endregion

#region Trace sink API

/// <summary>
/// The write-only side of pipeline tracing, called from PbrGeneration.
///
/// EVERY method here is [Conditional("DEBUG")]. That is not decoration - it means the C#
/// compiler deletes the call site *and its arguments* from a Release build, so a capture
/// that builds a bitmap out of a lambda costs exactly nothing in the shipping app. It is
/// also why the capture helpers take samplers (Func&lt;int,int,byte&gt;) rather than
/// pre-built arrays: the generator hands over a closure that reuses the very local
/// function its own loop runs, so no intermediate array is ever allocated for tracing and
/// no formula is ever duplicated between the pipeline and the preview.
///
/// In Debug with no dev tool open, the first line of every method is a null check on a
/// [ThreadStatic] sink that nothing ever armed. A full pack generation pays one predicted
/// branch per capture point per texture.
///
/// The sink is thread-static on purpose: AlchitexPipeline generates textures with
/// Parallel.ForEach, and a process-wide sink would interleave stages from different
/// textures into nonsense. Arming it on one thread captures only that thread's work, and
/// the dev tool runs its single texture synchronously inside one Task.Run so that thread
/// is exactly the one doing the work.
/// </summary>
public static class PipelineTrace
{
    [ThreadStatic] private static PipelineTraceSink? _sink;

    /// <summary>Arms tracing for the calling thread. Not conditional - the dev tool calls
    /// this directly, and it's harmless (and inert) anywhere else. Always pair with End()
    /// in a finally, and keep the whole traced run inside one synchronous block: an await
    /// in between can resume on a different thread, where the sink isn't armed.</summary>
    public static void Begin(PipelineTraceSink sink) => _sink = sink;

    public static void End() => _sink = null;

    /// <summary>True while the calling thread is capturing. Only useful for work that
    /// genuinely cannot be expressed as a sampler; prefer the samplers.</summary>
    public static bool IsCapturing => _sink != null;

    // -- Scalars ----------------------------------------------------------------------

    /// <summary>Attaches a named scalar to a stage. The stage doesn't need an image -
    /// notes-only stages are how run-wide facts (resolved mode, cluster counts, the
    /// gradient reference) get surfaced.</summary>
    [Conditional("DEBUG")]
    public static void Note(string stageId, string key, object? value)
    {
        var sink = _sink;
        if (sink == null) return;

        var text = value switch
        {
            null => "-",
            double d => d.ToString("0.####", CultureInfo.InvariantCulture),
            float f => f.ToString("0.####", CultureInfo.InvariantCulture),
            bool b => b ? "yes" : "no",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-",
        };

        var stage = sink.GetOrAdd(stageId);
        var index = stage.Notes.FindIndex(n => n.Key == key);
        if (index >= 0) stage.Notes[index] = (key, text);
        else stage.Notes.Add((key, text));
    }

    // -- Fields -----------------------------------------------------------------------

    /// <summary>
    /// The general primitive: a greyscale field built by asking the caller for each pixel.
    /// Pass a closure over the generator's own local function so the preview shows exactly
    /// what the loop computed, with nothing duplicated and nothing allocated in Release.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Field(string stageId, int width, int height, Func<int, int, byte> sample, string viewName = "Value")
    {
        var sink = _sink;
        if (sink == null) return;

        var bitmap = NewBitmap(width, height, (x, y) =>
        {
            var v = sample(x, y);
            return Color.FromArgb(255, v, v, v);
        });

        sink.ReplaceViews(stageId, new[] { new PipelineStageView(viewName, bitmap) });
    }

    /// <summary>Convenience for an already-materialized 0..1 field (ColorField's
    /// contrast-maximized output, the normal map's blended height field).</summary>
    [Conditional("DEBUG")]
    public static void Field(string stageId, float[,] normalized01, string viewName = "Value")
        => Field(stageId, normalized01.GetLength(0), normalized01.GetLength(1),
            (x, y) => (byte)Math.Clamp((int)Math.Round(normalized01[x, y] * 255f), 0, 255), viewName);

    /// <summary>Convenience for an already-materialized 0..255 field (mean-shift converged
    /// values, the clustered ladder, recursive-pass masks).</summary>
    [Conditional("DEBUG")]
    public static void Field(string stageId, double[,] values0To255, string viewName = "Value")
        => Field(stageId, values0To255.GetLength(0), values0To255.GetLength(1),
            (x, y) => (byte)Math.Clamp((int)Math.Round(values0To255[x, y]), 0, 255), viewName);

    /// <inheritdoc cref="Field(string, double[,], string)"/>
    [Conditional("DEBUG")]
    public static void Field(string stageId, int[,] values0To255, string viewName = "Value")
        => Field(stageId, values0To255.GetLength(0), values0To255.GetLength(1),
            (x, y) => (byte)Math.Clamp(values0To255[x, y], 0, 255), viewName);

    /// <summary>Gradient pair captured as one stage: magnitude, a hue-encoded direction
    /// view, and each signed component around mid-grey. All four are derived here from the
    /// two arrays the generator already has, so offering them costs the generator
    /// nothing.</summary>
    [Conditional("DEBUG")]
    public static void Gradients(string stageId, float[,] gradX, float[,] gradY, double referenceMagnitude)
    {
        var sink = _sink;
        if (sink == null) return;

        var w = gradX.GetLength(0);
        var h = gradX.GetLength(1);
        var scale = referenceMagnitude > 0 ? referenceMagnitude : 1.0;

        var magnitude = NewBitmap(w, h, (x, y) =>
        {
            double gx = gradX[x, y], gy = gradY[x, y];
            var m = Math.Sqrt(gx * gx + gy * gy);
            var v = (byte)Math.Clamp((int)Math.Round(m / scale * 255.0), 0, 255);
            return Color.FromArgb(255, v, v, v);
        });

        var direction = NewBitmap(w, h, (x, y) =>
        {
            double gx = gradX[x, y], gy = gradY[x, y];
            var m = Math.Sqrt(gx * gx + gy * gy);
            if (m <= 0) return Color.FromArgb(255, 32, 32, 32);
            var hue = (Math.Atan2(gy, gx) + Math.PI) / (2 * Math.PI) * 360.0;
            return FromHsv(hue, 1.0, 0.25 + 0.75 * Math.Clamp(m / scale, 0.0, 1.0));
        });

        var dx = NewBitmap(w, h, (x, y) => SignedGrey(gradX[x, y], scale));
        var dy = NewBitmap(w, h, (x, y) => SignedGrey(gradY[x, y], scale));

        sink.ReplaceViews(stageId, new[]
        {
            new PipelineStageView("Magnitude", magnitude),
            new PipelineStageView("Direction", direction),
            new PipelineStageView("dX", dx),
            new PipelineStageView("dY", dy),
        });
    }

    /// <summary>
    /// A per-pixel transform of an existing bitmap, for views that would otherwise force
    /// the generator to materialize an array it doesn't need (the real-colour-data mask,
    /// the perceptual luminosity field). The source is locked and read here, inside the
    /// conditional call, so nothing about it survives into a Release build.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Derived(string stageId, Bitmap source, Func<Color, Color> map, string viewName = "Value")
    {
        var sink = _sink;
        if (sink == null) return;

        var w = source.Width;
        var h = source.Height;
        var mapped = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var src = new FastBitmap(source, writable: false))
        using (var dst = new FastBitmap(mapped, writable: true))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    dst[x, y] = map(src[x, y]);
        }

        sink.ReplaceViews(stageId, new[] { new PipelineStageView(viewName, mapped) });
    }

    /// <summary>An arbitrary colour field. Same sampler contract as Field.</summary>
    [Conditional("DEBUG")]
    public static void Rgb(string stageId, int width, int height, Func<int, int, Color> sample, string viewName = "RGB")
    {
        var sink = _sink;
        if (sink == null) return;
        sink.ReplaceViews(stageId, new[] { new PipelineStageView(viewName, NewBitmap(width, height, sample)) });
    }

    /// <summary>
    /// A label/region map - each distinct integer gets its own colour. Used for the
    /// heightmap's cluster assignments before and after merging, where what matters is
    /// *which pixels grouped together*, not what value they ended up at.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Labels(string stageId, int width, int height, Func<int, int, int> labelAt)
    {
        var sink = _sink;
        if (sink == null) return;
        sink.ReplaceViews(stageId, new[] { new PipelineStageView("Regions", NewBitmap(width, height, (x, y) => LabelColor(labelAt(x, y)))) });
    }

    // -- Snapshots of live bitmaps ----------------------------------------------------

    /// <summary>
    /// Captures a bitmap the generator is currently building, split into composite plus
    /// isolated channels. Takes a FastBitmap rather than the Bitmap because the interesting
    /// moments (MERS after the base pass, MERS between recursive passes) happen while the
    /// bitmap is locked - reading the Bitmap itself there would be invalid.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Snapshot(string stageId, FastBitmap source, bool includeAlpha = true)
    {
        var sink = _sink;
        if (sink == null) return;
        sink.ReplaceViews(stageId, BuildChannelViews(source.Width, source.Height, (x, y) => source[x, y], includeAlpha));
    }

    /// <inheritdoc cref="Snapshot(string, FastBitmap, bool)"/>
    [Conditional("DEBUG")]
    public static void Snapshot(string stageId, Bitmap source, bool includeAlpha = true)
    {
        var sink = _sink;
        if (sink == null) return;

        var w = source.Width;
        var h = source.Height;
        var pixels = new Color[w, h];

        using (var fb = new FastBitmap(source, writable: false))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    pixels[x, y] = fb[x, y];
        }

        sink.ReplaceViews(stageId, BuildChannelViews(w, h, (x, y) => pixels[x, y], includeAlpha));
    }

    // -- Curves -----------------------------------------------------------------------

    /// <summary>
    /// Samples a 1-D function for plotting. Pass the generator's own local function so the
    /// plot can't drift from what the loop actually applies - that's the whole reason this
    /// takes a delegate instead of a set of coefficients.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Curve(string stageId, string xLabel, string yLabel, double x0, double x1, Func<double, double> f, int samples = 128)
    {
        var sink = _sink;
        if (sink == null) return;
        if (samples < 2) samples = 2;

        var xs = new double[samples];
        var ys = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var x = x0 + (x1 - x0) * i / (samples - 1.0);
            xs[i] = x;
            ys[i] = f(x);
        }

        sink.GetOrAdd(stageId).Curve = new PipelineCurve { XLabel = xLabel, YLabel = yLabel, X = xs, Y = ys };
    }

    // -- Internals --------------------------------------------------------------------

    private static List<PipelineStageView> BuildChannelViews(int w, int h, Func<int, int, Color> at, bool includeAlpha)
    {
        var views = new List<PipelineStageView>
        {
            // The composite is drawn opaque: an RGB payload that merely happens to live in
            // an ARGB bitmap (MERS, normal maps) would otherwise render as a checkerboard
            // of nothing, and the alpha channel gets its own view below anyway.
            new("RGB", NewBitmap(w, h, (x, y) => { var c = at(x, y); return Color.FromArgb(255, c.R, c.G, c.B); })),
            new("R", NewBitmap(w, h, (x, y) => Grey(at(x, y).R))),
            new("G", NewBitmap(w, h, (x, y) => Grey(at(x, y).G))),
            new("B", NewBitmap(w, h, (x, y) => Grey(at(x, y).B))),
        };

        if (includeAlpha)
            views.Add(new PipelineStageView("A", NewBitmap(w, h, (x, y) => Grey(at(x, y).A))));

        return views;
    }

    private static Bitmap NewBitmap(int w, int h, Func<int, int, Color> sample)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var fb = new FastBitmap(bitmap, writable: true))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    fb[x, y] = sample(x, y);
        }
        return bitmap;
    }

    private static Color Grey(byte v) => Color.FromArgb(255, v, v, v);

    private static Color SignedGrey(double value, double scale)
    {
        var t = Math.Clamp(value / scale, -1.0, 1.0);
        var v = (byte)Math.Clamp((int)Math.Round((t + 1.0) * 0.5 * 255.0), 0, 255);
        return Color.FromArgb(255, v, v, v);
    }

    /// <summary>Stable, well-separated colour per label index - golden-ratio hue stepping,
    /// so adjacent cluster ids never come out as adjacent hues, with alternating value so
    /// even a hue collision stays distinguishable.</summary>
    private static Color LabelColor(int label)
    {
        if (label < 0) return Color.FromArgb(255, 24, 24, 24);
        var hue = label * 137.507 % 360.0;
        return FromHsv(hue, 0.62, label % 2 == 0 ? 0.95 : 0.72);
    }

    private static Color FromHsv(double hueDegrees, double saturation, double value)
    {
        var h = (hueDegrees % 360.0 + 360.0) % 360.0 / 60.0;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = value - c;

        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(255,
            (byte)Math.Clamp((int)Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round((b + m) * 255), 0, 255));
    }
}

#endregion
