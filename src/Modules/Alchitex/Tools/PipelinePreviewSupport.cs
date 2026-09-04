using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Modules.Alchitex.Core;

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

// =====================================================================================
// Support types for the Pipeline Preview dev tool (PipelinePreviewWindow).
//
// Everything here is presentation and plumbing. The tool's *value* comes from
// PipelineTuning and PipelineTrace over in Core/PipelineInstrumentation.cs - this file
// only decides how what they produce gets drawn and edited.
//
// The design goal throughout is that changing the PBR pipeline should not require
// changing this file:
//
//   * ReflectiveEditor walks whatever properties MaterialEntry happens to have, so a
//     materials.json schema change needs no work here at all.
//   * KnobEditor walks whatever fields PipelineTuning happens to have, so a new tuning
//     constant needs no work here either.
//   * PipelineStageCatalog is *optional* prose keyed by stage id. A stage with no entry
//     still renders, titled by its own id. Adding a trace call and never touching this
//     file is a supported, working outcome - the catalog is a nicety, not a registry.
// =====================================================================================

#region Stage catalog

/// <summary>
/// Friendly titles and explanations for captured stage ids, and nothing more. This is a
/// lookup with a graceful fallback, NOT a list of known stages: an id that isn't in here
/// renders perfectly well with its own id as the title. That's deliberate - it means a
/// new PipelineTrace call in PbrGeneration.cs shows up in the tool immediately, and
/// writing prose for it is a separate, optional, whenever-you-feel-like-it edit.
/// </summary>
public static class PipelineStageCatalog
{
    /// <summary>Display order and labels for the chain groups (the part of a stage id
    /// before its first dot). An unrecognized chain sorts last under its own name.</summary>
    public static readonly (string Chain, string Title)[] Chains =
    {
        ("run", "Run"),
        ("shared", "Shared analysis"),
        ("mers", "MERS"),
        ("height", "Heightmap"),
        ("normal", "Normal map"),
    };

    private static readonly Dictionary<string, (string Title, string Blurb)> Entries = new(StringComparer.Ordinal)
    {
        ["run"] = ("Run summary",
            "What this preview run resolved to before any pixels were touched."),

        // -- Shared ------------------------------------------------------------------
        ["shared.color"] = ("Source colour texture",
            "The texture exactly as read off disk. Alpha is preserved - generation reads with maxOpacity:false, because the real-colour-data rule below needs the real alpha channel."),
        ["shared.realmask"] = ("Real colour data mask",
            "White means this pixel gets a vote in the texture's value domain. Black means excluded: fully transparent AND filled with pure black or pure white, the two conventional padding fills authors use. grass_side's transparent dirt holds real colour and stays white. Excluded pixels still receive an output value - they just don't widen the domain and flatten the visible content."),
        ["shared.grey"] = ("Flat-average greyscale",
            "(R+G+B)/3, deliberately not luminosity-weighted, so 'value' means exactly one thing across MERS, heightmap and normal generation. MERS computes this same field inline for itself."),
        ["shared.contrastmax"] = ("Contrast-maximized greyscale",
            "The grey stretched so the darkest real pixel lands at 0 and the brightest at 1. This is a full min-to-max stretch - not the same operation as the ceiling-maximize used for POM, which only ever scales up."),

        // -- MERS --------------------------------------------------------------------
        ["mers.luminosity"] = ("Perceptual luminosity (SSS source)",
            "The one place true 0.2126/0.7152/0.0722 perceptual luminosity is used instead of the flat average. Stretched into the material's SSS range and written as the MERS alpha channel."),
        ["mers.base"] = ("Base MERS",
            "The grey field stretched independently into each channel's own target range, each with its own invert flag. R = metalness, G = emissive, B = roughness, A = subsurface."),
        ["mers.pass#.mask"] = ("Recursive pass - dominance mask",
            "Per-pixel opacity taken from how strongly the chosen channel locally dominates the other two. On a furnace front, red flame pixels have red far above green and blue so they mask in strongly; grey stone pixels where red only just leads mask in faintly and are barely affected."),
        ["mers.pass#.result"] = ("Recursive pass - after blending",
            "The pass's own fully independent MERS, alpha-blended over all three output channels weighted by that mask. Legacy only blended green; this blends all three."),
        ["mers.final"] = ("Final MERS",
            "What gets written as <name>_mers.tga. Always MERS, never plain MER, always carrying the block's real subsurface data - whether a shader reads it is downstream of us."),

        // -- Heightmap ---------------------------------------------------------------
        ["height.converged"] = ("Mean-shift converged values",
            "Iterated joint spatial+range weighted averaging, which converges to the same piecewise-flat result as literal mode-seeking mean-shift without tracking particles. Neighbours are sampled with wraparound so the result stays seamless-tileable. Above the fallback pixel count this switches to range-only clustering off a 256-bin histogram."),
        ["height.clusters.raw"] = ("Clusters found",
            "Which pixels converged together, before any capping or merging. Shown as arbitrary region colours rather than heights, because at this point what matters is the grouping, not what value each group will land on."),
        ["height.clusters.merged"] = ("Clusters after capping and merging",
            "Merging continues while either the count is over the cap OR the two closest levels are nearer than the merge tolerance. That second condition is load-bearing: without it, levels at 200 and 203 both survive as separate heights, which is what a heightmap that reads as a mess in game is made of."),
        ["height.ladder"] = ("Placed height ladder",
            "Each surviving cluster placed at an evenly spaced slot by brightness RANK, not at its own mean brightness. Value placement is measurably more faithful and was tried - but brightness isn't height, so faithfulness reproduces wood grain and mottling as real elevation. Rank's inaccuracy collapses each cluster onto a discrete step, which is what reads as distinct flat surfaces."),
        ["height.alpha"] = ("Colour texture alpha",
            "The source alpha, which drives how strongly the transparency overlay darkens each pixel in the next step."),
        ["height.overlay"] = ("After transparency overlay",
            "Transparent regions darkened with a Photoshop Overlay blend rather than a linear one - overlay preserves midtone detail on the darkened side, which is what grass_side's transparent dirt portion needs to still read as sitting beneath the grass."),
        ["height.intensity"] = ("After intensity blend",
            "The material's heightmap.intensity blending the result toward flat neutral 128. 0 gives a completely flat heightmap."),
        ["height.final"] = ("Final heightmap",
            "What gets written as <name>_heightmap.tga, after the optional full inversion (a workaround for the game-side bug where certain assets always render inverted, not a stylistic knob)."),

        // -- Normal map --------------------------------------------------------------
        ["normal.clustered"] = ("Clustered heightmap (shared basis)",
            "The same mean-shift clustered heightmap the heightmap generator produces. Both texture types share one height basis - this is that basis, raw and untouched."),
        ["normal.heightfield"] = ("Blended height field",
            "The clustered heightmap linearly blended with the contrast-maximized colour greyscale. Kept as floats rather than round-tripped through an 8-bit bitmap, so the gradient pass sees the real blend instead of a re-quantized copy."),
        ["normal.gradients"] = ("Sobel gradients",
            "Sampled with wraparound indexing, so an edge pixel sees its real neighbour from the opposite edge and the result tiles seamlessly - which matters because Bedrock randomly rotates isometric block textures and any two edges can end up meeting. (Scharr was measured here and came out marginally worse; don't re-try it without new evidence.)"),
        ["normal.response"] = ("Response curve",
            "The exponent applied to each normalized gradient magnitude, interpolated by this texture's noise index. Above 1 crushes small differences so only genuine steps produce strong normals; below 1 lifts them, because a noisy texture's subtle variation is all it has."),
        ["normal.shaped"] = ("Shaped slope",
            "Each pixel's gradient magnitude after the response curve, normalized against the full-strength slope. This is the surface steepness the normal is actually built from."),
        ["normal.encoded"] = ("Encoded normal (pre-POM)",
            "(-slopeX, -slopeY, 1) normalized and mapped straight to RGB, which is already DirectX convention - the format Bedrock expects. No axis swap belongs here; an earlier version transposed X and Y, which was invisible along a symmetric bump's main diagonal but swapped the other two corners."),
        ["normal.pom.maximized"] = ("POM - ceiling-maximized",
            "The clustered heightmap scaled up so its brightest pixel hits exactly 255. Deliberately a pure upward scale, not a min/max stretch, so dark regions stay off zero and relative height stays proportional."),
        ["normal.pom"] = ("POM - after contrast reduction",
            "Each pixel's remaining recession from the surface pulled in by the reduction factor, because the clustered heightmap still sinks very deep even after ceiling-maximizing. Shrinking recession can never overflow past 255, so no separate compression pass is needed."),
        ["normal.withpom"] = ("Normal with POM blue channel",
            "Bedrock RTX reads parallax-occlusion height from the normal map's blue channel, so the encoded Z is overwritten with the POM data above. Unconditional for every texture - a player disabling POM is a downstream shader setting, not something to gate here."),
        ["normal.final"] = ("Final normal map",
            "What gets written as <name>_normal.tga, after the optional red/green inversion. Blue is never inverted - it's POM height, not a normal component."),
    };

    /// <summary>Title and explanation for a stage id, or a readable fallback derived from
    /// the id itself when the catalog has nothing to say about it.</summary>
    public static (string Title, string Blurb) Describe(string stageId)
    {
        if (Entries.TryGetValue(stageId, out var exact)) return exact;

        // Indexed stages (mers.pass0.mask, mers.pass1.mask, ...) share one catalog entry
        // under a '#' placeholder, so adding a second recursive pass doesn't need a second
        // block of prose.
        var generalized = GeneralizeIndices(stageId);
        if (generalized != stageId && Entries.TryGetValue(generalized, out var indexed))
        {
            var index = ExtractFirstIndex(stageId);
            return index == null ? indexed : ($"{indexed.Title} ({index})", indexed.Blurb);
        }

        // Unknown stage: still perfectly usable, just unannotated. This is the expected
        // state for anything just added to the pipeline.
        var tail = stageId.Contains('.') ? stageId[(stageId.IndexOf('.') + 1)..] : stageId;
        return (tail.Replace('.', ' '), "");
    }

    public static string ChainTitle(string chain)
        => Chains.FirstOrDefault(c => c.Chain == chain).Title ?? PipelineTuning.SplitCamelCase(chain);

    /// <summary>Chains sort by their listed order; anything unlisted goes to the end,
    /// which is exactly where a brand-new chain should appear rather than nowhere.</summary>
    public static int ChainOrder(string chain)
    {
        for (var i = 0; i < Chains.Length; i++)
            if (Chains[i].Chain == chain) return i;
        return int.MaxValue;
    }

    private static string GeneralizeIndices(string id)
    {
        var sb = new StringBuilder(id.Length);
        var lastWasDigit = false;
        foreach (var ch in id)
        {
            if (char.IsDigit(ch))
            {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true;
            }
            else
            {
                sb.Append(ch);
                lastWasDigit = false;
            }
        }
        return sb.ToString();
    }

    private static string? ExtractFirstIndex(string id)
    {
        var digits = new string(id.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : $"#{digits}";
    }
}

#endregion

#region Imaging

/// <summary>
/// Turns the System.Drawing bitmaps the pipeline works in into something XAML can show.
///
/// Scaling is always nearest-neighbour and done here rather than by the Image element:
/// WinUI's Image has no nearest-neighbour option, and everything this tool exists to let
/// you judge - a mortar line one pixel wide, a single cluster boundary, whether a normal's
/// corner pixel is right - is destroyed by bilinear smoothing at the 16x zoom these
/// textures need.
/// </summary>
public static class PreviewImaging
{
    /// <summary>
    /// Nearest-neighbour resample to fit inside a maxEdge box, then upload to a
    /// WriteableBitmap. Small textures are scaled by whole integers only, so a 16px
    /// texture becomes an exact 16x blowup with no resampling artifacts at all; large
    /// ones are point-sampled down, which keeps single-pixel features visible instead of
    /// averaging them away.
    /// </summary>
    public static WriteableBitmap ToPreviewSource(Bitmap source, int maxEdge)
    {
        var sw = source.Width;
        var sh = source.Height;
        var longest = Math.Max(sw, sh);

        int tw, th;
        if (longest <= maxEdge)
        {
            var factor = Math.Max(1, maxEdge / Math.Max(1, longest));
            tw = sw * factor;
            th = sh * factor;
        }
        else
        {
            var ratio = maxEdge / (double)longest;
            tw = Math.Max(1, (int)Math.Round(sw * ratio));
            th = Math.Max(1, (int)Math.Round(sh * ratio));
        }

        var pixels = new byte[tw * th * 4];

        using (var fb = new FastBitmap(source, writable: false))
        {
            for (var y = 0; y < th; y++)
            {
                var sy = Math.Min(sh - 1, (int)((long)y * sh / th));
                for (var x = 0; x < tw; x++)
                {
                    var sx = Math.Min(sw - 1, (int)((long)x * sw / tw));
                    var c = fb[sx, sy];
                    var i = (y * tw + x) * 4;
                    // WriteableBitmap's buffer is BGRA8, premultiplied-alpha ignored here
                    // because every view the trace produces is already fully opaque.
                    pixels[i] = c.B;
                    pixels[i + 1] = c.G;
                    pixels[i + 2] = c.R;
                    pixels[i + 3] = c.A;
                }
            }
        }

        var writeable = new WriteableBitmap(tw, th);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        // Required after writing through PixelBuffer - without it the bitmap can present
        // whatever its backing store happened to contain rather than what was just
        // written.
        writeable.Invalidate();

        return writeable;
    }
}

#endregion

#region Preview run

/// <summary>One preview run's outputs. Owns its sink, and therefore every captured
/// bitmap - disposing this is what frees the previous run's images.</summary>
public sealed class PipelinePreviewResult : IDisposable
{
    public required PipelineTraceSink Sink { get; init; }
    public required SecondaryPbrMode ResolvedMode { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }

    public void Dispose() => Sink.Dispose();
}

/// <summary>
/// Runs one colour texture through all three generators with a given material entry and
/// tuning set, capturing everything.
///
/// All three chains always run, regardless of which secondary mode is selected - the whole
/// point is seeing MERS, heightmap and normal side by side. The selected mode only decides
/// what the ResolvedMode readout reports the real pipeline would have picked.
/// </summary>
public static class PipelinePreviewRunner
{
    public static PipelinePreviewResult Run(
        string colorPath,
        MaterialEntry material,
        PipelineTuning tuning,
        SecondaryPbrMode requestedMode,
        bool blacklisted,
        bool materialMatchedExactly)
    {
        var sink = new PipelineTraceSink();
        var stopwatch = Stopwatch.StartNew();
        string? error = null;
        var resolved = requestedMode;

        // Begin/End must bracket a single synchronous block on one thread: the sink is
        // [ThreadStatic], so an await in the middle could resume somewhere it isn't armed.
        PipelineTrace.Begin(sink);
        try
        {
            resolved = TextureSetOrchestrator.ResolveSecondaryMode(requestedMode, colorPath, tuning);

            using var color = Helpers.ReadImage(colorPath, maxOpacity: false);

            PipelineTrace.Note("run", "texture", Path.GetFileNameWithoutExtension(colorPath));
            PipelineTrace.Note("run", "size", $"{color.Width} x {color.Height}");
            PipelineTrace.Note("run", "materials.json entry", materialMatchedExactly ? "exact name match" : "\"default\" fallback");
            PipelineTrace.Note("run", "pbr_blacklist.json", blacklisted ? "MATCHED - real pipeline writes a colour-only texture set" : "no match");
            PipelineTrace.Note("run", "secondary requested", requestedMode.ToString());
            PipelineTrace.Note("run", "secondary resolved", resolved.ToString());
            PipelineTrace.Note("run", "recursive passes", material.Recursive.Count);

            var mersWatch = Stopwatch.StartNew();
            using (var mers = MersGenerator.Generate(color, material, tuning)) { }
            mersWatch.Stop();

            var heightWatch = Stopwatch.StartNew();
            using (var heightmap = HeightmapGenerator.Generate(color, material.Heightmap, tuning)) { }
            heightWatch.Stop();

            var normalWatch = Stopwatch.StartNew();
            using (var normal = NormalMapGenerator.Generate(color, material.Normal, tuning)) { }
            normalWatch.Stop();

            PipelineTrace.Note("run", "MERS time", $"{mersWatch.ElapsedMilliseconds} ms");
            PipelineTrace.Note("run", "heightmap time", $"{heightWatch.ElapsedMilliseconds} ms");
            PipelineTrace.Note("run", "normal time", $"{normalWatch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Trace.WriteLine($"[ALCHITEX] Pipeline preview run failed for '{colorPath}': {ex}");
        }
        finally
        {
            PipelineTrace.End();
            stopwatch.Stop();
        }

        return new PipelinePreviewResult
        {
            Sink = sink,
            ResolvedMode = resolved,
            Elapsed = stopwatch.Elapsed,
            Error = error,
        };
    }
}

#endregion

#region Reflective editors

/// <summary>
/// Builds editing controls for an arbitrary object by walking its properties, and for
/// PipelineTuning by walking its fields.
///
/// This is reflective rather than hand-written XAML for one specific reason: the developer
/// brief for this tool was that it must not become a maintenance tax on the PBR pipeline.
/// A hand-built material editor would have to be revisited every time materials.json's
/// schema moved, and a hand-built knob panel every time a tuning constant was added -
/// which is exactly the kind of drift that ends with a dev tool nobody trusts. Walking the
/// types means a schema change shows up here for free, and a field nobody wrote metadata
/// for still gets a working control.
/// </summary>
public static class ReflectiveEditor
{
    private const string TrimJustification =
        "Every type walked here (MaterialEntry and its nested parameter types) is already " +
        "rooted for trimming by AlchitexJsonContext's [JsonSerializable] declarations, " +
        "which force the source generator to read and write all of their properties. " +
        "PipelineTuning is walked through a literal typeof, which ILLink resolves " +
        "statically. This code is also unreachable in Release, where the dev-tool entry " +
        "point is hidden.";

    // -- Material entry (materials.json shape) ---------------------------------------

    /// <summary>
    /// Builds a full editor for a MaterialEntry into `host`, calling `onChanged` whenever
    /// anything is edited. Handles nested objects, nullable nested objects and lists
    /// (recursive passes) generically, so it keeps working across schema changes.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = TrimJustification)]
    public static void BuildObjectEditor(Panel host, object target, Action onChanged, int depth = 0)
    {
        var properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            var type = property.PropertyType;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            var label = FriendlyName(property);

            if (underlying == typeof(bool))
            {
                host.Children.Add(BuildToggle(label, () => (bool)(property.GetValue(target) ?? false),
                    v => { property.SetValue(target, v); onChanged(); }));
            }
            else if (underlying == typeof(int))
            {
                // 0-255 covers every integer in the current schema (MERS channel targets,
                // SSS opacity). The numeric box is not clamped to it, so a schema that
                // later needs something else stays fully reachable - only the slider's
                // convenient range would be wrong, and it widens to fit the live value.
                host.Children.Add(BuildNumeric(label, "",
                    () => Convert.ToDouble(property.GetValue(target) ?? 0),
                    v => { property.SetValue(target, (int)Math.Round(v)); onChanged(); },
                    0, 255, 1, isInteger: true));
            }
            else if (underlying == typeof(double) || underlying == typeof(float))
            {
                // Likewise: every double in the current schema is a 0-1 intensity.
                host.Children.Add(BuildNumeric(label, "",
                    () => Convert.ToDouble(property.GetValue(target) ?? 0.0),
                    v =>
                    {
                        property.SetValue(target, underlying == typeof(float) ? (float)v : v);
                        onChanged();
                    },
                    0, 1, 0.01, isInteger: false));
            }
            else if (underlying == typeof(string))
            {
                host.Children.Add(BuildStringRow(label, property.Name,
                    () => (string?)property.GetValue(target) ?? "",
                    v => { property.SetValue(target, v); onChanged(); }));
            }
            else if (typeof(IList).IsAssignableFrom(type) && type.IsGenericType)
            {
                host.Children.Add(BuildListEditor(label, target, property, onChanged, depth));
            }
            else if (underlying.IsClass)
            {
                host.Children.Add(BuildNestedEditor(label, target, property, onChanged, depth));
            }
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = TrimJustification)]
    private static FrameworkElement BuildNestedEditor(string label, object owner, PropertyInfo property, Action onChanged, int depth)
    {
        var inner = new StackPanel { Spacing = 6, Margin = new Thickness(10, 6, 0, 6) };
        var value = property.GetValue(owner);

        var expander = new Expander
        {
            Header = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = depth == 0,
            Margin = new Thickness(0, 2, 0, 2),
            Content = inner,
        };

        // A nullable nested object (RecursivePass.Sss) gets an explicit opt-in, because
        // "absent" and "present but zeroed" mean genuinely different things here: absent
        // leaves the base entry's SSS untouched inside the masked region.
        var isOptional = property.CanWrite && !property.PropertyType.IsValueType && IsOptionalReference(property);
        if (isOptional)
        {
            var toggle = new CheckBox { Content = "override", MinWidth = 0, IsChecked = value != null };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            headerPanel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            headerPanel.Children.Add(toggle);
            expander.Header = headerPanel;

            toggle.Checked += (_, _) =>
            {
                if (property.GetValue(owner) == null)
                    property.SetValue(owner, NewInstance(property.PropertyType));
                Rebuild();
                onChanged();
            };
            toggle.Unchecked += (_, _) =>
            {
                property.SetValue(owner, null);
                Rebuild();
                onChanged();
            };
        }

        Rebuild();
        return expander;

        void Rebuild()
        {
            inner.Children.Clear();
            var current = property.GetValue(owner);
            if (current == null)
            {
                inner.Children.Add(new TextBlock
                {
                    Text = "Not set - the base entry's value applies here.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            BuildObjectEditor(inner, current, onChanged, depth + 1);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = TrimJustification)]
    private static FrameworkElement BuildListEditor(string label, object owner, PropertyInfo property, Action onChanged, int depth)
    {
        var itemType = property.PropertyType.GetGenericArguments()[0];
        var items = new StackPanel { Spacing = 6 };
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(10, 6, 0, 6) };

        var add = new Button { Content = $"Add {itemType.Name}", HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(items);
        body.Children.Add(add);

        var expander = new Expander
        {
            Header = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true,
            Margin = new Thickness(0, 2, 0, 2),
            Content = body,
        };

        add.Click += (_, _) =>
        {
            var list = (IList?)property.GetValue(owner);
            if (list == null)
            {
                list = (IList?)NewInstance(property.PropertyType);
                property.SetValue(owner, list);
            }
            list?.Add(NewInstance(itemType));
            Rebuild();
            onChanged();
        };

        Rebuild();
        return expander;

        void Rebuild()
        {
            items.Children.Clear();
            var list = (IList?)property.GetValue(owner);
            if (list == null || list.Count == 0)
            {
                items.Children.Add(new TextBlock { Text = "None.", Opacity = 0.7 });
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var index = i;
                var element = list[index];
                if (element == null) continue;

                var itemBody = new StackPanel { Spacing = 6, Margin = new Thickness(10, 6, 0, 0) };
                BuildObjectEditor(itemBody, element, onChanged, depth + 1);

                var remove = new Button { Content = "Remove", Margin = new Thickness(0, 6, 0, 0) };
                remove.Click += (_, _) =>
                {
                    ((IList?)property.GetValue(owner))?.RemoveAt(index);
                    Rebuild();
                    onChanged();
                };
                itemBody.Children.Add(remove);

                items.Children.Add(new Expander
                {
                    Header = $"{itemType.Name} {index}",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsExpanded = true,
                    Content = itemBody,
                });
            }
        }
    }

    // -- Pipeline tuning (PipelineTuning fields) -------------------------------------

    /// <summary>
    /// Builds the tuning panel from PipelineTuning.Describe(), grouped by each [Knob]'s
    /// Group. Adding a field to PipelineTuning is the entire cost of adding a control
    /// here - there is nothing to register and nothing to lay out.
    /// </summary>
    public static void BuildKnobEditor(Panel host, PipelineTuning tuning, Action onChanged)
    {
        foreach (var group in PipelineTuning.Describe().GroupBy(d => d.Knob.Group))
        {
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(10, 6, 0, 6) };

            foreach (var (field, knob) in group)
            {
                var isInteger = field.FieldType == typeof(int);
                var label = string.IsNullOrEmpty(knob.Label) ? PipelineTuning.SplitCamelCase(field.Name) : knob.Label;
                var step = knob.Step > 0 ? knob.Step : (isInteger ? 1 : (knob.Max - knob.Min) / 100.0);

                body.Children.Add(BuildNumeric(label, knob.About,
                    () => Convert.ToDouble(field.GetValue(tuning) ?? 0),
                    v =>
                    {
                        field.SetValue(tuning, isInteger ? (int)Math.Round(v) : v);
                        onChanged();
                    },
                    knob.Min, knob.Max, step, isInteger));
            }

            host.Children.Add(new Expander
            {
                Header = group.Key,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = false,
                Margin = new Thickness(0, 2, 0, 2),
                Content = body,
            });
        }
    }

    // -- Control factories ------------------------------------------------------------

    /// <summary>
    /// A slider and a numeric box over one value. Both exist on purpose: the slider is
    /// what makes sweeping a knob and watching every preview redraw feel like tuning at
    /// all, and the box is what lets you type an exact value or one outside the slider's
    /// suggested range.
    /// </summary>
    private static FrameworkElement BuildNumeric(
        string label, string about,
        Func<double> get, Action<double> set,
        double min, double max, double step, bool isInteger)
    {
        var current = get();
        // Widen the slider to whatever the live value actually is, so a value outside the
        // suggested range is still reachable by dragging rather than silently clamped.
        var lo = Math.Min(min, current);
        var hi = Math.Max(max, current);

        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(caption);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slider = new Slider
        {
            Minimum = lo,
            Maximum = hi,
            StepFrequency = step,
            Value = current,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsThumbToolTipEnabled = false,
        };

        var box = new NumberBox
        {
            Value = current,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
            // Without an explicit formatter, values like the gradient flat threshold
            // (1/255) display every digit they have. Six significant figures is enough to
            // see what a knob is actually set to without the box overflowing.
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                IsGrouped = false,
                FractionDigits = 0,
                NumberRounder = new Windows.Globalization.NumberFormatting.SignificantDigitsNumberRounder
                {
                    SignificantDigits = 6,
                },
            },
        };

        if (!string.IsNullOrEmpty(about))
        {
            ToolTipService.SetToolTip(caption, new ToolTip { Content = about });
            ToolTipService.SetToolTip(slider, new ToolTip { Content = about });
        }

        var suppress = false;

        slider.ValueChanged += (_, e) =>
        {
            if (suppress) return;
            suppress = true;
            var v = isInteger ? Math.Round(e.NewValue) : e.NewValue;
            box.Value = v;
            set(v);
            suppress = false;
        };

        box.ValueChanged += (_, _) =>
        {
            if (suppress) return;
            if (double.IsNaN(box.Value)) return;
            suppress = true;
            var v = isInteger ? Math.Round(box.Value) : box.Value;
            if (v >= slider.Minimum && v <= slider.Maximum) slider.Value = v;
            set(v);
            suppress = false;
        };

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(box, 1);
        row.Children.Add(slider);
        row.Children.Add(box);
        panel.Children.Add(row);

        return panel;
    }

    private static FrameworkElement BuildToggle(string label, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 2, 0, 2) };
        check.Checked += (_, _) => set(true);
        check.Unchecked += (_, _) => set(false);
        return check;
    }

    private static FrameworkElement BuildStringRow(string label, string propertyName, Func<string> get, Action<string> set)
    {
        // Purely a convenience: the MERS recursive pass's channel is one of exactly three
        // letters, and picking from a list beats typing one. If that property is ever
        // renamed this quietly falls back to a text box, which still works.
        if (string.Equals(propertyName, "Channel", StringComparison.OrdinalIgnoreCase))
        {
            var combo = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in new[] { "R", "G", "B" }) combo.Items.Add(option);
            combo.SelectedItem = new[] { "R", "G", "B" }.FirstOrDefault(o => string.Equals(o, get(), StringComparison.OrdinalIgnoreCase)) ?? "R";
            combo.SelectionChanged += (_, _) => set((string)combo.SelectedItem);
            return combo;
        }

        var textBox = new TextBox { Header = label, Text = get(), HorizontalAlignment = HorizontalAlignment.Stretch };
        textBox.TextChanged += (_, _) => set(textBox.Text);
        return textBox;
    }

    /// <summary>
    /// Instantiates a schema type discovered by reflection (a nested parameter object, or
    /// a new recursive pass). Deliberately its own named method rather than an inline
    /// Activator call inside a lambda: the suppression below has to sit on a real member
    /// for ILLink to honour it, and a closure's compiler-generated method isn't reliably
    /// one. Adding a `default:` parameterless-constructor requirement instead would only
    /// push the same unanalyzable Type one frame up the call chain.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = TrimJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = TrimJustification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    private static object? NewInstance(Type type) => Activator.CreateInstance(type);

    /// <summary>Prefers the [JsonPropertyName] the schema actually uses, so the label in
    /// the tool matches what you'd type into materials.json.</summary>
    private static string FriendlyName(PropertyInfo property)
    {
        var json = property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        return json != null ? json.Name : PipelineTuning.SplitCamelCase(property.Name);
    }

    /// <summary>A reference-typed property is treated as optional (gets an "override"
    /// checkbox) when it's declared nullable. Detected from the nullable annotation
    /// metadata rather than a hardcoded property name, so it tracks the schema.</summary>
    private static bool IsOptionalReference(PropertyInfo property)
    {
        var context = new NullabilityInfoContext();
        return context.Create(property).WriteState == NullabilityState.Nullable;
    }
}

#endregion

#region materials.json clipboard interop

/// <summary>
/// Reading one entry out of the real materials.json and writing an edited one back to the
/// clipboard. This is the workflow the tool exists to serve: pull a block's current entry,
/// tune it against a live preview, copy the result, paste it into materials.json by hand.
/// The tool deliberately never writes to materials.json itself.
/// </summary>
public static class MaterialEntryJson
{
    /// <summary>Serializes one entry as a materials.json fragment - the quoted texture
    /// name plus its object, ready to paste straight into the file.</summary>
    public static string ToFragment(string textureName, MaterialEntry entry)
    {
        var body = JsonSerializer.Serialize(entry, AlchitexJsonContext.Default.MaterialEntry);
        var indented = string.Join("\n  ", body.Split('\n'));
        return $"\"{textureName}\": {indented}";
    }

    /// <summary>Parses either a bare entry object or a one-key fragment of the shape
    /// ToFragment produces, so round-tripping through the clipboard works.</summary>
    public static MaterialEntry? FromClipboardText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim().TrimEnd(',');

        try
        {
            // A pasted fragment isn't a JSON document on its own - wrap it and unwrap the
            // single entry back out.
            if (!trimmed.StartsWith('{'))
            {
                var wrapped = JsonNode.Parse("{" + trimmed + "}")?.AsObject();
                var first = wrapped?.FirstOrDefault().Value;
                if (first == null) return null;
                return JsonSerializer.Deserialize(first.ToJsonString(), AlchitexJsonContext.Default.MaterialEntry);
            }

            // A bare object could be the entry itself, or a single-key wrapper around it.
            var node = JsonNode.Parse(trimmed)?.AsObject();
            if (node == null) return null;

            var looksLikeEntry = node.ContainsKey("mer") || node.ContainsKey("sss")
                || node.ContainsKey("normal") || node.ContainsKey("heightmap") || node.ContainsKey("recursive");

            if (!looksLikeEntry && node.Count == 1)
            {
                var inner = node.First().Value;
                if (inner != null)
                    return JsonSerializer.Deserialize(inner.ToJsonString(), AlchitexJsonContext.Default.MaterialEntry);
            }

            return JsonSerializer.Deserialize(trimmed, AlchitexJsonContext.Default.MaterialEntry);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't parse a material entry from the clipboard: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads the whole of materials.json once, case-insensitively keyed. The tool holds on
    /// to this rather than re-reading per lookup: the real file runs to tens of thousands
    /// of lines, and the exact-match check runs on every preview to report whether a
    /// texture is genuinely covered or silently falling through to "default" - which is
    /// itself one of the more useful things this tool surfaces.
    /// </summary>
    public static Dictionary<string, MaterialEntry> LoadAll(string materialsJsonPath)
    {
        try
        {
            var raw = File.ReadAllText(materialsJsonPath);
            var parsed = JsonSerializer.Deserialize(raw, AlchitexJsonContext.Default.DictionaryStringMaterialEntry);
            return parsed == null
                ? new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MaterialEntry>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't read materials.json at '{materialsJsonPath}': {ex.Message}");
            return new Dictionary<string, MaterialEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Deep copy through the same source-generated serializer materials.json
    /// itself uses, so it stays correct across schema changes for free. Used to snapshot
    /// the live-edited entry before handing it to a background preview run.</summary>
    public static MaterialEntry Clone(MaterialEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, AlchitexJsonContext.Default.MaterialEntry);
        return JsonSerializer.Deserialize(json, AlchitexJsonContext.Default.MaterialEntry) ?? new MaterialEntry();
    }

    /// <summary>The tuning set as C# field initializers, for pasting back into
    /// PipelineTuning once a value has been settled on. Only fields that actually differ
    /// from the shipping defaults are emitted, so what comes out is a diff rather than a
    /// wall of unchanged numbers.</summary>
    public static string ToCSharp(PipelineTuning tuning)
    {
        var defaults = PipelineTuning.Default;
        var sb = new StringBuilder();
        var changed = 0;

        foreach (var (field, _) in PipelineTuning.Describe())
        {
            var current = field.GetValue(tuning);
            var original = field.GetValue(defaults);
            if (Equals(current, original)) continue;

            changed++;
            var literal = current switch
            {
                double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
                float f => f.ToString("0.#####", CultureInfo.InvariantCulture) + "f",
                _ => Convert.ToString(current, CultureInfo.InvariantCulture) ?? "?",
            };
            var originalLiteral = original switch
            {
                double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
                float f => f.ToString("0.#####", CultureInfo.InvariantCulture) + "f",
                _ => Convert.ToString(original, CultureInfo.InvariantCulture) ?? "?",
            };

            sb.AppendLine($"public {FriendlyTypeName(field.FieldType)} {field.Name} = {literal}; // was {originalLiteral}");
        }

        return changed == 0
            ? "// Every tuning value still matches PipelineTuning's shipping defaults."
            : $"// Changed tuning values ({changed}) - paste into PipelineTuning in Core/PipelineInstrumentation.cs\n{sb}";
    }

    private static string FriendlyTypeName(Type type)
        => type == typeof(int) ? "int"
         : type == typeof(double) ? "double"
         : type == typeof(float) ? "float"
         : type == typeof(bool) ? "bool"
         : type.Name;
}

#endregion
