using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Adds subtle, out-of-phase looping animations on top of a tag badge's existing
/// flat-color Background. Every effect here is purely additive: the flat color set
/// by PackBrowserWindow.BuildTagBadge is never modified, only layered over, so if
/// anything below throws (older WinAppSDK, theming quirk, whatever), the badge
/// simply stays exactly as it looks today. That flat color is the fallback, by
/// construction, not by convention.
///
/// IMPORTANT: Apply() runs while the badge is still being constructed in
/// CreatePackButton, i.e. BEFORE it's added to PackListContainer.Children. A
/// Storyboard.Begin() called on targets that aren't yet part of a live visual
/// tree throws a COMException whose message is the infamous "No installed
/// components were detected." (HRESULT 0x800F1000 – a XAML "invalid operation"
/// code that happens to collide with an unrelated SetupAPI error code, hence the
/// misleading text). So every Storyboard here is started lazily via
/// BeginOnLoaded, once its overlay is actually rooted in the tree.
///
/// Not everything here is a Storyboard. The reactor field is driven by one shared
/// DispatcherTimer that steps a few cells per tick, mirroring the real ReactorBackdrop
/// in the Alchitex module – see the block comment above ApplyReactorRain for why. It
/// registers and unregisters on the same Loaded/Unloaded pair, so the same rule holds:
/// nothing animates until it is in the tree, and nothing survives leaving it.
///
/// Kept in its own file so BuildTagBadge's color switch stays a color switch –
/// call BadgeVFX.Apply(badge, tag) once at the end and move on.
/// </summary>
internal static class PackBrowserBadgeVFX
{
    // Small pseudo-random desync so identical badges across different packs never
    // breathe/pulse/drift in lockstep with one another. Only ever touched on the
    // UI thread (badges are built synchronously in a foreach), so no locking needed.
    private static readonly Random Desync = new();

    public static void Apply(Border badge, string tag)
    {
        try
        {
            switch (tag)
            {
                case "RTX":
                    ApplyRtxGlow(badge);
                    break;
                case "Vibrant Visuals":
                    ApplyVibrantVisualsBlobs(badge);
                    break;
                case "Incompatible":
                    ApplyIncompatiblePulse(badge);
                    break;
                case PackBrowserWindow.AlchitexCandidateTag:
                    ApplyReactorRain(badge);
                    break;
                case PackBrowserWindow.ChemistryTag:
                    ApplyChemistryBlobs(badge);
                    break;
                case PackBrowserWindow.UnknownCapabilityTag:
                    ApplyUnknownGlitch(badge);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[BadgeVFX] Skipping animation for \"{tag}\": {ex.Message}");
            // badge.Background – the flat fallback color – was never touched, so the badge is still fully usable.
        }
    }

    private static double Jitter(double minSeconds, double maxSeconds) =>
        minSeconds + Desync.NextDouble() * (maxSeconds - minSeconds);

    /// <summary>
    /// Starts a Storyboard only once <paramref name="element"/> is actually loaded
    /// into the live visual tree – see the class remarks for why this matters.
    /// </summary>
    private static void BeginOnLoaded(FrameworkElement element, Storyboard storyboard)
    {
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            element.Unloaded += OnUnloaded;

            // Apply()'s try/catch is long gone by the time this runs, and the flat badge
            // colour underneath is still a perfectly good badge – so a Begin() that throws
            // here costs the animation, never the window.
            try { storyboard.Begin(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[BadgeVFX] Begin failed: {ex.Message}"); }
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            element.Unloaded -= OnUnloaded;
            storyboard.Stop(); // break animation reference cycle
        }

        element.Loaded += OnLoaded;
    }

    private static void BeginOnLoaded(FrameworkElement element, IReadOnlyList<Storyboard> storyboards)
    {
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            element.Unloaded += OnUnloaded;

            try { foreach (var sb in storyboards) sb.Begin(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[BadgeVFX] Begin failed: {ex.Message}"); }
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            element.Unloaded -= OnUnloaded;
            foreach (var sb in storyboards) sb.Stop(); // stop all cell storyboards
        }

        element.Loaded += OnLoaded;
    }


    /// <summary>
    /// Lays an animated visual over the badge's existing content, behind the text,
    /// stretched to cover the full badge including its padding – without ever
    /// touching badge.Background itself.
    /// </summary>
    private static void LayerOverlay(Border badge, FrameworkElement overlay)
    {
        overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        overlay.VerticalAlignment = VerticalAlignment.Stretch;
        overlay.Margin = new Thickness(
            -badge.Padding.Left, -badge.Padding.Top,
            -badge.Padding.Right, -badge.Padding.Bottom);

        var existingContent = badge.Child;
        badge.Child = null;

        var host = new Grid();
        host.Children.Add(overlay);
        if (existingContent is UIElement contentElement)
            host.Children.Add(contentElement);

        badge.Child = host;
    }

    // ════════════════════════════════════════════════════════════════════
    //  RTX – Breathing glow effect plus a shine passing over the top of it
    // ════════════════════════════════════════════════════════════════════
    private static void ApplyRtxGlow(Border badge)
    {
        var current = ColorHelper.FromArgb(255, 177, 255, 44);
        var nvidia = ColorHelper.FromArgb(244, 111, 177, 0);

        var brush = new RadialGradientBrush
        {
            RadiusX = 0.6,
            RadiusY = 0.6,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.35, 0.4)
        };
        var stopA = new GradientStop { Offset = 0.0, Color = current };
        var stopB = new GradientStop { Offset = 1.0, Color = nvidia };
        brush.GradientStops.Add(stopA);
        brush.GradientStops.Add(stopB);

        var glow = new Border { Background = brush, CornerRadius = badge.CornerRadius };

        var drift = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(2.0, 4.0)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.5))
        };
        AddLoop(drift, new Point(0.30, 0.35), new Point(0.68, 0.30), new Point(0.65, 0.72), new Point(0.28, 0.68));
        Storyboard.SetTarget(drift, brush);
        Storyboard.SetTargetProperty(drift, "Center");

        var origin = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(2.0, 4.0)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 1.8))
        };
        AddLoop(origin, new Point(0.40, 0.55), new Point(0.62, 0.62), new Point(0.35, 0.30));
        Storyboard.SetTarget(origin, brush);
        Storyboard.SetTargetProperty(origin, "GradientOrigin");

        var hueShift = new ColorAnimation
        {
            From = current,
            To = ColorHelper.FromArgb(127, 0, 255, 0),
            Duration = TimeSpan.FromSeconds(Jitter(0.6, 3.2)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 0.5))
        };
        Storyboard.SetTarget(hueShift, stopA);
        Storyboard.SetTargetProperty(hueShift, "Color");

        var opacityPulse = new DoubleAnimation
        {
            From = 0.2,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(Jitter(0.8, 3.5)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(opacityPulse, glow);
        Storyboard.SetTargetProperty(opacityPulse, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(drift); sb.Children.Add(origin); sb.Children.Add(hueShift); sb.Children.Add(opacityPulse);

        var storyboards = new List<Storyboard> { sb };

        var host = new Grid();
        host.Children.Add(glow);

        // Over the top of the breathing glow rather than instead of it
        host.Children.Add(BuildShineSweep(badge, storyboards,
            band: ColorHelper.FromArgb(105, 225, 255, 190),
            fastestPass: 0.7, slowestPass: 1.4,
            shortestRest: 4.0, longestRest: 12.0));

        var overlay = new Border { Child = host, CornerRadius = badge.CornerRadius };

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, storyboards);
    }


    // ════════════════════════════════════════════════════════════════════
    //  Vibrant Visuals – burnt-orange/brown blobs drifting into one another
    // ════════════════════════════════════════════════════════════════════
    private static void ApplyVibrantVisualsBlobs(Border badge)
    {
        var golden = ColorHelper.FromArgb(225, 155, 120, 80);
        var burnt = ColorHelper.FromArgb(225, 92, 62, 28);

        var brush = new RadialGradientBrush
        {
            RadiusX = 0.8,
            RadiusY = 0.8,
            Center = new Point(0.35, 0.4),
            GradientOrigin = new Point(0.35, 0.4)
        };
        var stopA = new GradientStop { Offset = 0.0, Color = golden };
        var stopB = new GradientStop { Offset = 1.0, Color = burnt };
        brush.GradientStops.Add(stopA);
        brush.GradientStops.Add(stopB);

        var overlay = new Border { Background = brush, CornerRadius = badge.CornerRadius };

        var drift = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(1.0, 3.5)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.0))
        };
        AddLoop(drift, new Point(0.30, 0.35), new Point(0.68, 0.30), new Point(0.65, 0.72), new Point(0.28, 0.68));
        Storyboard.SetTarget(drift, brush);
        Storyboard.SetTargetProperty(drift, "Center");

        var origin = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(2.0, 4.0)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 1.8))
        };
        AddLoop(origin, new Point(0.40, 0.55), new Point(0.62, 0.62), new Point(0.35, 0.30));
        Storyboard.SetTarget(origin, brush);
        Storyboard.SetTargetProperty(origin, "GradientOrigin");

        var hueShift = new ColorAnimation
        {
            From = golden,
            To = ColorHelper.FromArgb(244, 255, 168, 40),
            Duration = TimeSpan.FromSeconds(Jitter(1.0, 2.0)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 1.5))
        };
        Storyboard.SetTarget(hueShift, stopA);
        Storyboard.SetTargetProperty(hueShift, "Color");

        var opacityPulse = new DoubleAnimation
        {
            From = 0.5,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(Jitter(1.8, 3.0)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(opacityPulse, overlay);
        Storyboard.SetTargetProperty(opacityPulse, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(drift); sb.Children.Add(origin); sb.Children.Add(hueShift); sb.Children.Add(opacityPulse);

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, sb);
    }


    /// <summary>
    /// Traces the given points evenly across the animation's Duration (which must
    /// already be set), then loops back to the first point so the repeat has no
    /// visible seam.
    /// </summary>
    private static void AddLoop(PointAnimationUsingKeyFrames anim, params Point[] loopPoints)
    {
        var totalSeconds = anim.Duration.TimeSpan.TotalSeconds;
        for (int i = 0; i < loopPoints.Length; i++)
        {
            var progress = (double)i / loopPoints.Length;
            anim.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(totalSeconds * progress)),
                Value = loopPoints[i],
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        anim.KeyFrames.Add(new EasingPointKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(totalSeconds)),
            Value = loopPoints[0],
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  Incompatible – red drifting toward VV's brown color, signaling their somewhat kinship in how
    //  Tuning isn't really gonna work well for VV, and not at all for Incompatible packs.
    // ════════════════════════════════════════════════════════════════════
    private static void ApplyIncompatiblePulse(Border badge)
    {
        var red = ColorHelper.FromArgb(244, 192, 33, 0);
        var gold = ColorHelper.FromArgb(225, 99, 66, 9);

        var pulseBrush = new SolidColorBrush(red);
        var overlay = new Border { Background = pulseBrush, CornerRadius = badge.CornerRadius };

        var pulse = new ColorAnimation
        {
            From = red,
            To = gold,
            Duration = TimeSpan.FromSeconds(Jitter(5, 14)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 5))
        };
        Storyboard.SetTarget(pulse, pulseBrush);
        Storyboard.SetTargetProperty(pulse, "Color");

        var sb = new Storyboard();
        sb.Children.Add(pulse);

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, sb);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Alchitex candidate – a miniature ReactorBackdrop field
    //
    //  Built to the same two rules as the real one in Alchitex/AlchitexWindow.ReactorBackdrop.cs,
    //  for the same reasons:
    //
    //    - Nothing is animated. Colour never eases between two values; a cell is on
    //      one rung of the ramp or the next, and it gets there in one step. That is
    //      the visual language the reactor window speaks, and matching it is most of
    //      the point of this effect.
    //    - Every cell is one quad filled from one of seven shared brushes, and a
    //      single slow timer steps a handful of cells per tick across every live
    //      badge at once. The storyboard version this replaced ran 96 ColorAnimations
    //      per badge, each evaluated every frame for as long as the window was open.
    //
    //  The cadence is unchanged – a cell still moves about as often as it used to.
    //  Only the smoothness is gone, deliberately.
    //
    //  What it does NOT take from the backdrop is dispersion. Out there the field has to
    //  stop somewhere and cells thinning out toward the top is how it ends without a cut
    //  edge. Here the field fills the badge exactly, corner to corner, over a flat blue
    //  that shows through at 50% - there is no edge to dissolve, so cells winking on and
    //  off just read as a fault in the badge. The rows, the falloff curve, the fade ladder
    //  and the per-cell rung state all existed to serve it and went with it.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ReactorBackdrop's palette: usual RTX Reactor-brand blues
    /// </summary>
    private static readonly Color[] ReactorPalette =
    {
        ColorHelper.FromArgb(255, 0, 35, 66),
        ColorHelper.FromArgb(255, 0, 41, 78),
        ColorHelper.FromArgb(255, 0, 48, 91),
        ColorHelper.FromArgb(255, 0, 53, 102),
        ColorHelper.FromArgb(255, 0, 59, 114),
        ColorHelper.FromArgb(255, 0, 72, 138),
        ColorHelper.FromArgb(255, 44, 154, 255),
    };

    /// <summary>Palette entries below this form the ordered ramp a cell steps along; anything
    /// from here up is an accent with no neighbours on it (see StepReactorShade).</summary>
    private const int ReactorRampLength = 6;

    private const int ReactorColumns = 24;
    private const int ReactorRows = 4;

    /// <summary>The tick rate is the frame rate – see the block comment above.</summary>
    private const double ReactorTickMs = 100;

    /// <summary>How often a given cell takes a step, which is what sets how many of them each
    /// tick touches. Matched to the half-cycle of the ColorAnimation this replaced, so the
    /// effect turns over at the speed it always did.</summary>
    private const double ReactorCellStepSeconds = 1.2;

    /// <summary>
    /// Brushes are DependencyObjects and so have thread affinity. Built on first use rather
    /// than in a static initialiser, so they are created on whichever UI thread is actually
    /// building badges – the same thread the ticker below binds itself to.
    /// </summary>
    private static SolidColorBrush[]? _reactorBrushes;

    private static SolidColorBrush[] ReactorBrushes =>
        _reactorBrushes ??= ReactorPalette.Select(c => new SolidColorBrush(c)).ToArray();

    private sealed class ReactorCell
    {
        public required Rectangle Shape;
        public required int BaseShade;
        public int Shade;
    }

    private sealed class ReactorField
    {
        public required ReactorCell[] Cells;
        public required int CellsPerTick;
    }

    // Every live reactor badge in the app, ticked together. Only ever touched on the UI
    // thread – from Loaded/Unloaded and from the timer – so no locking is needed.
    private static readonly List<ReactorField> ReactorFields = new();
    private static DispatcherTimer? _reactorTimer;

    private static void ApplyReactorRain(Border badge)
    {
        var brushes = ReactorBrushes;

        var pixelGrid = new Grid();
        for (int i = 0; i < ReactorColumns; i++) pixelGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < ReactorRows; i++) pixelGrid.RowDefinitions.Add(new RowDefinition());

        var shades = new int[ReactorColumns, ReactorRows];
        var cells = new ReactorCell[ReactorColumns * ReactorRows];
        var next = 0;

        for (int r = 0; r < ReactorRows; r++)
        {
            for (int c = 0; c < ReactorColumns; c++)
            {
                // One re-roll away from the neighbours already placed, exactly as the
                // backdrop's PickShade does: repeats allowed, just not favoured.
                var shade = PickReactorShade();
                var left = c > 0 ? shades[c - 1, r] : -1;
                var above = r > 0 ? shades[c, r - 1] : -1;
                if (shade == left || shade == above) shade = PickReactorShade();
                shades[c, r] = shade;

                var cell = new ReactorCell
                {
                    Shape = new Rectangle { Fill = brushes[shade] },
                    BaseShade = shade,
                    Shade = shade,
                };

                Grid.SetRow(cell.Shape, r);
                Grid.SetColumn(cell.Shape, c);
                pixelGrid.Children.Add(cell.Shape);

                cells[next++] = cell;
            }
        }

        var field = new ReactorField
        {
            Cells = cells,
            CellsPerTick = Math.Max(1,
                (int)Math.Round(cells.Length * (ReactorTickMs / 1000.0) / ReactorCellStepSeconds))
        };

        // Kept at half opacity so the flat fallback blue always reads through as the
        // "base" of the badge, with the pixel field as a texture on top.
        var overlay = new Border { Child = pixelGrid, CornerRadius = badge.CornerRadius, Opacity = 0.5 };

        LayerOverlay(badge, overlay);
        TickWhileLoaded(overlay, field);
    }

    /// <summary>Uniform across the palette, accent included. The backdrop weights its own
    /// pick so the accent stays rare over a field the size of a window; a badge is 96 cells
    /// behind a flat blue, and wants the accent often enough to be seen at all.</summary>
    private static int PickReactorShade() => Desync.Next(ReactorPalette.Length);

    /// <summary>
    /// Registers a field with the shared ticker for as long as its overlay is in the tree.
    /// The same lazily-on-Loaded discipline as BeginOnLoaded, for a related reason: Apply
    /// runs before the badge is rooted, and a field whose badge was never shown would
    /// otherwise be ticked forever.
    /// </summary>
    private static void TickWhileLoaded(FrameworkElement element, ReactorField field)
    {
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            element.Unloaded += OnUnloaded;

            ReactorFields.Add(field);
            StartReactorTicker();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            element.Unloaded -= OnUnloaded;

            ReactorFields.Remove(field);
            if (ReactorFields.Count == 0) StopReactorTicker();
        }

        element.Loaded += OnLoaded;
    }

    private static void StartReactorTicker()
    {
        if (_reactorTimer != null) return;

        // Created on first use so it binds to the dispatcher of the thread the badges live
        // on, and dropped again when the last badge goes, so nothing outlives the window.
        _reactorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReactorTickMs) };
        _reactorTimer.Tick += (s, e) => ReactorTick();
        _reactorTimer.Start();
    }

    private static void StopReactorTicker()
    {
        if (_reactorTimer == null) return;

        _reactorTimer.Stop();
        _reactorTimer = null;
    }

    /// <summary>
    /// One frame of every live field's life: a few cells each move one rung along the blue
    /// ramp and back. Nothing interpolates – every cell is always on a rung.
    /// </summary>
    private static void ReactorTick()
    {
        try
        {
            // Indexed rather than foreach'd: an Unloaded handler is free to mutate the list
            // between ticks, and this way one running during a tick wouldn't invalidate an
            // enumerator either.
            for (var f = 0; f < ReactorFields.Count; f++)
            {
                var field = ReactorFields[f];

                for (var i = 0; i < field.CellsPerTick; i++)
                    StepReactorShade(field.Cells[Desync.Next(field.Cells.Length)]);
            }
        }
        catch (Exception ex)
        {
            // Same bargain as everywhere else here: the badges keep their flat colours and
            // whatever shade they last landed on. Stop, rather than throw once per tick.
            System.Diagnostics.Trace.WriteLine($"[BadgeVFX] Reactor ticker stopped: {ex.Message}");
            StopReactorTicker();
        }
    }

    private static void StepReactorShade(ReactorCell cell)
    {
        // Back to its own colour if it has wandered, otherwise one step off it – never
        // further, so the field keeps the arrangement it was built with. An accent cell has
        // no neighbours on the ramp, so it steps against the ramp's bright end instead;
        // stepping it by index would land on an unrelated colour.
        var target = cell.Shade != cell.BaseShade
            ? cell.BaseShade
            : cell.BaseShade >= ReactorRampLength
                ? ReactorRampLength - 1
                : Math.Clamp(cell.BaseShade + (Desync.NextDouble() < 0.5 ? -1 : 1), 0, ReactorRampLength - 1);

        if (target == cell.Shade) return;

        cell.Shade = target;
        cell.Shape.Fill = ReactorBrushes[target];
    }


    // ════════════════════════════════════════════════════════════════════
    //  Chemistry – three reagents diffusing through the teal base
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The badge's own flat teal is the solvent they are being dropped into, which is why every blob fades to
    /// fully transparent at its rim rather than to a colour of its own.
    /// The purple is also what the other two turn into: it sits between them on the wheel, so
    /// using it as the colour each of them drifts toward is what makes the drift read as the
    /// two of them reacting rather than as two lights independently changing hue. It then
    /// gets a blob of its own as well, which is how the mixture ends up somewhere the two
    /// source colours alone could not put it.
    /// </summary>
    private static readonly Color ChemistryCyan = ColorHelper.FromArgb(255, 57, 171, 191);
    private static readonly Color ChemistryMagenta = ColorHelper.FromArgb(255, 204, 57, 174);
    private static readonly Color ChemistryPurple = ColorHelper.FromArgb(255, 119, 65, 191);

    private static void ApplyChemistryBlobs(Border badge)
    {
        var host = new Grid();
        var storyboards = new List<Storyboard>();

        // Three blobs on deliberately unrelated periods: large and slow, middling, small and
        // quick. Where they cross, their colours stack; where they part, the teal comes back
        // through. That is the whole "fluids not yet mixed" read – one blob on its own just
        // looks like a moving light, and three beat two because with three the overlaps stop
        // repeating on any period short enough to notice.
        host.Children.Add(BuildChemistryBlob(badge, storyboards,
            reagent: ChemistryCyan, reacted: ChemistryPurple,
            radius: 0.85, radiusSwing: 0.18, lowOpacity: 0.50,
            centerPath: new[] { new Point(0.22, 0.62), new Point(0.55, 0.28), new Point(0.82, 0.66), new Point(0.48, 0.80) },
            originPath: new[] { new Point(0.30, 0.45), new Point(0.62, 0.60), new Point(0.40, 0.30) },
            slowest: 6.0, fastest: 9.0));

        host.Children.Add(BuildChemistryBlob(badge, storyboards,
            reagent: ChemistryMagenta, reacted: ChemistryPurple,
            radius: 0.62, radiusSwing: 0.16, lowOpacity: 0.38,
            centerPath: new[] { new Point(0.78, 0.30), new Point(0.40, 0.72), new Point(0.15, 0.35), new Point(0.60, 0.20) },
            originPath: new[] { new Point(0.60, 0.55), new Point(0.35, 0.40), new Point(0.65, 0.35) },
            slowest: 3.0, fastest: 6.0));

        // The purple one drifts back toward magenta rather than onward to anything new, so
        // the three of them stay a closed loop instead of wandering off the palette.
        host.Children.Add(BuildChemistryBlob(badge, storyboards,
            reagent: ChemistryPurple, reacted: ChemistryMagenta,
            radius: 0.45, radiusSwing: 0.12, lowOpacity: 0.28,
            centerPath: new[] { new Point(0.45, 0.20), new Point(0.18, 0.58), new Point(0.68, 0.78), new Point(0.88, 0.42) },
            originPath: new[] { new Point(0.42, 0.38), new Point(0.55, 0.66), new Point(0.30, 0.52) },
            slowest: 1.0, fastest: 2.0));

        var overlay = new Border { Child = host, CornerRadius = badge.CornerRadius };

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, storyboards);
    }

    private static Border BuildChemistryBlob(
        Border badge, List<Storyboard> storyboards,
        Color reagent, Color reacted,
        double radius, double radiusSwing, double lowOpacity,
        Point[] centerPath, Point[] originPath,
        double slowest, double fastest)
    {
        var core = ColorHelper.FromArgb(235, reagent.R, reagent.G, reagent.B);
        var halo = ColorHelper.FromArgb(120, reagent.R, reagent.G, reagent.B);
        var rim = ColorHelper.FromArgb(0, reagent.R, reagent.G, reagent.B);
        var mixed = ColorHelper.FromArgb(235, reacted.R, reacted.G, reacted.B);

        var brush = new RadialGradientBrush
        {
            RadiusX = radius,
            RadiusY = radius,
            Center = centerPath[0],
            GradientOrigin = centerPath[0]
        };
        var coreStop = new GradientStop { Offset = 0.0, Color = core };
        brush.GradientStops.Add(coreStop);
        brush.GradientStops.Add(new GradientStop { Offset = 0.45, Color = halo });
        brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = rim });

        var blob = new Border { Background = brush, CornerRadius = badge.CornerRadius };

        var drift = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(slowest, fastest)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 6.0))
        };
        AddLoop(drift, centerPath);
        Storyboard.SetTarget(drift, brush);
        Storyboard.SetTargetProperty(drift, "Center");

        var origin = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(slowest, fastest)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.5))
        };
        AddLoop(origin, originPath);
        Storyboard.SetTarget(origin, brush);
        Storyboard.SetTargetProperty(origin, "GradientOrigin");

        // Swelling out of step on the two axes is what turns a drifting disc into something
        // being stirred; on their own, Center and GradientOrigin only slide it around.
        var swellX = BuildChemistrySwell(brush, "RadiusX", radius, radiusSwing, slowest, fastest);
        var swellY = BuildChemistrySwell(brush, "RadiusY", radius, radiusSwing, slowest, fastest);

        var react = new ColorAnimation
        {
            From = core,
            To = mixed,
            Duration = TimeSpan.FromSeconds(Jitter(slowest, fastest)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.0))
        };
        Storyboard.SetTarget(react, coreStop);
        Storyboard.SetTargetProperty(react, "Color");

        var diffuse = new DoubleAnimation
        {
            From = lowOpacity,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(Jitter(slowest, fastest)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(diffuse, blob);
        Storyboard.SetTargetProperty(diffuse, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(drift); sb.Children.Add(origin);
        sb.Children.Add(swellX); sb.Children.Add(swellY);
        sb.Children.Add(react); sb.Children.Add(diffuse);
        storyboards.Add(sb);

        return blob;
    }

    private static DoubleAnimation BuildChemistrySwell(
        RadialGradientBrush brush, string property,
        double radius, double swing, double slowest, double fastest)
    {
        var swell = new DoubleAnimation
        {
            From = radius - swing,
            To = radius + swing,
            Duration = TimeSpan.FromSeconds(Jitter(slowest, fastest)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.0)),

            // A brush radius is not one of the properties the compositor can drive on its
            // own, so it has to be opted in explicitly or Begin() refuses it outright.
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(swell, brush);
        Storyboard.SetTargetProperty(swell, property);
        return swell;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Shared – the shine sweep
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A band of light crossing the badge once, then waiting a good while before doing it
    /// again. Layer it over whatever the badge already does; it contributes nothing of its
    /// own between passes.
    ///
    /// It is a narrow gradient window – transparent, bright, transparent – whose whole span
    /// is slid across the badge, rather than stops animated within a fixed span. Both ends of
    /// the window are transparent and the default pad extend repeats those ends outward, so
    /// the band is genuinely absent everywhere the window is not, and it starts and finishes
    /// each pass entirely off the badge. That means the loop needs no seam handling, no
    /// SpreadMethod, and no stop offsets outside 0..1 – none of which a gradient brush is
    /// obliged to render the way you would hope.
    ///
    /// Pass length, rest length and start offset are all rolled per badge, so a menu full of
    /// the same tag doesn't flash in unison.
    /// </summary>
    private static Border BuildShineSweep(
        Border badge, List<Storyboard> storyboards, Color band,
        double fastestPass, double slowestPass, double shortestRest, double longestRest)
    {
        var clear = ColorHelper.FromArgb(0, band.R, band.G, band.B);

        // The window's width, in badge widths. The travel below is padded by this on each
        // side, so the band is fully off the badge at both ends of a pass.
        const double windowWidth = 0.7;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(-windowWidth, 0),
            EndPoint = new Point(0, 0)
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = clear });
        brush.GradientStops.Add(new GradientStop { Offset = 0.5, Color = band });
        brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = clear });

        var sweep = new Border { Background = brush, CornerRadius = badge.CornerRadius };

        // Rolled once and shared by both halves of the slide – roll them separately and the
        // window stretches and shears instead of travelling.
        var pass = Jitter(fastestPass, slowestPass);
        var cycle = pass + Jitter(shortestRest, longestRest);
        var begin = TimeSpan.FromSeconds(Jitter(0, longestRest));

        var sb = new Storyboard();
        sb.Children.Add(BuildSweepSlide(brush, "StartPoint",
            new Point(-windowWidth, 0), new Point(1.0, 0), pass, cycle, begin));
        sb.Children.Add(BuildSweepSlide(brush, "EndPoint",
            new Point(0, 0), new Point(1.0 + windowWidth, 0), pass, cycle, begin));
        storyboards.Add(sb);

        return sweep;
    }

    private static PointAnimationUsingKeyFrames BuildSweepSlide(
        LinearGradientBrush brush, string property,
        Point from, Point to, double pass, double cycle, TimeSpan begin)
    {
        var slide = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(cycle),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin,
            EnableDependentAnimation = true
        };

        // Linear across the pass – a shine that eased in and out would look like it was
        // considering something – then parked off the far edge for the rest of the cycle.
        // The snap back at the loop point is invisible because both ends of the travel are
        // entirely off the badge, which is the same property that lets the band arrive and
        // leave cleanly in the first place.
        slide.KeyFrames.Add(new LinearPointKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from
        });
        slide.KeyFrames.Add(new LinearPointKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pass)),
            Value = to
        });
        slide.KeyFrames.Add(new LinearPointKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle)),
            Value = to
        });

        Storyboard.SetTarget(slide, brush);
        Storyboard.SetTargetProperty(slide, property);
        return slide;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Unknown – a badge that will not hold still
    //
    //  The one tag here that says nothing about the pack, so its effect says as
    //  little as it can get away with: no colour, no travel, no shine – just the
    //  badge intermittently failing. It is the least important thing in the row and
    //  is meant to look it. The sweep it used to carry now belongs to RTX, where
    //  looking special is the entire point.
    // ════════════════════════════════════════════════════════════════════
    private static void ApplyUnknownGlitch(Border badge)
    {
        var storyboards = new List<Storyboard>();
        var overlay = BuildUnknownGlitch(badge, storyboards);

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, storyboards);
    }

    /// <summary>
    /// The badge occasionally failing to hold still: two or three flashes of wash at odd
    /// intervals. Discrete key frames, so nothing here interpolates, making it cheaper to run.
    /// </summary>
    private static Border BuildUnknownGlitch(Border badge, List<Storyboard> storyboards)
    {
        var glitch = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 210, 210, 210)),
            CornerRadius = badge.CornerRadius,
            Opacity = 0
        };

        var cycle = Jitter(4.0, 8.0);
        var blink = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(cycle),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(1.0, 6.0))
        };

        // Placed as fractions of the cycle rather than in seconds, so the jittered period
        // stretches the stutter instead of detaching its parts from one another.
        AddBlink(blink, cycle, 0.00, 0.00);
        AddBlink(blink, cycle, 0.62, 0.16);
        AddBlink(blink, cycle, 0.65, 0.00);
        AddBlink(blink, cycle, 0.71, 0.09);
        AddBlink(blink, cycle, 0.73, 0.00);
        AddBlink(blink, cycle, 0.76, 0.20);
        AddBlink(blink, cycle, 0.80, 0.00);
        AddBlink(blink, cycle, 1.00, 0.00);

        Storyboard.SetTarget(blink, glitch);
        Storyboard.SetTargetProperty(blink, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(blink);
        storyboards.Add(sb);

        return glitch;
    }

    private static void AddBlink(DoubleAnimationUsingKeyFrames anim, double cycle, double atFraction, double opacity) =>
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(cycle * atFraction)),
            Value = opacity
        });
}
