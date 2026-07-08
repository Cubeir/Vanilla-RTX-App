using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Vanilla_RTX_App.PackBrowser;

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
/// components were detected." (HRESULT 0x800F1000 — a XAML "invalid operation"
/// code that happens to collide with an unrelated SetupAPI error code, hence the
/// misleading text). So every Storyboard here is started lazily via
/// BeginOnLoaded, once its overlay is actually rooted in the tree.
///
/// Kept in its own file so BuildTagBadge's color switch stays a color switch —
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
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[BadgeVFX] Skipping animation for \"{tag}\": {ex.Message}");
            // badge.Background — the flat fallback color — was never touched, so the badge is still fully usable.
        }
    }

    private static double Jitter(double minSeconds, double maxSeconds) =>
        minSeconds + Desync.NextDouble() * (maxSeconds - minSeconds);

    /// <summary>
    /// Starts a Storyboard only once <paramref name="element"/> is actually loaded
    /// into the live visual tree — see the class remarks for why this matters.
    /// </summary>
    private static void BeginOnLoaded(FrameworkElement element, Storyboard storyboard)
    {
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            element.Unloaded += OnUnloaded;
            storyboard.Begin();
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
            foreach (var sb in storyboards) sb.Begin();
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
    /// stretched to cover the full badge including its padding — without ever
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
    //  RTX — Swapped breathing glow effect for a VV-like one cause that one looks cooler
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

        var overlay = new Border { Background = brush, CornerRadius = badge.CornerRadius };

        var drift = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(1.0, 4.0)),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(Jitter(0, 2.5))
        };
        AddLoop(drift, new Point(0.30, 0.35), new Point(0.68, 0.30), new Point(0.65, 0.72), new Point(0.28, 0.68));
        Storyboard.SetTarget(drift, brush);
        Storyboard.SetTargetProperty(drift, "Center");

        var origin = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Jitter(1.0, 4.0)),
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
            Duration = TimeSpan.FromSeconds(Jitter(0.2, 3.2)),
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
            Duration = TimeSpan.FromSeconds(Jitter(0.5, 3.5)),
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


    // ════════════════════════════════════════════════════════════════════
    //  Vibrant Visuals — golden / burnt-orange blobs drifting into one another
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
    //  Incompatible — red drifting toward VV's gold and back, signalling kinship
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
    //  Alchitex candidate — RTX Reactor-style flickering pixel grid
    // ════════════════════════════════════════════════════════════════════
    private static void ApplyReactorRain(Border badge)
    {
        var baseColor = ColorHelper.FromArgb(244, 0, 72, 138);
        var dark = ColorHelper.FromArgb(244, 0, 40, 78);
        var bright = ColorHelper.FromArgb(244, 40, 130, 210);

        const int columns = 24;
        const int rows = 4;

        var pixelGrid = new Grid();
        for (int i = 0; i < columns; i++) pixelGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows; i++) pixelGrid.RowDefinitions.Add(new RowDefinition());

        var cellStoryboards = new List<Storyboard>();

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                var targetColor = Desync.NextDouble() < 0.5 ? dark : bright;
                var cellBrush = new SolidColorBrush(baseColor);

                var cell = new Rectangle { Fill = cellBrush };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                pixelGrid.Children.Add(cell);

                var flicker = new ColorAnimation
                {
                    From = baseColor,
                    To = targetColor,
                    Duration = TimeSpan.FromSeconds(Jitter(0.7, 1.8)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(Jitter(0, 3))
                };
                Storyboard.SetTarget(flicker, cellBrush);
                Storyboard.SetTargetProperty(flicker, "Color");

                var cellStoryboard = new Storyboard();
                cellStoryboard.Children.Add(flicker);
                cellStoryboards.Add(cellStoryboard);
            }
        }

        // Kept at half opacity so the flat fallback blue always reads through as
        // the "base" of the badge, with the pixel flicker as a texture on top.
        var overlay = new Border { Child = pixelGrid, CornerRadius = badge.CornerRadius, Opacity = 0.5 };

        LayerOverlay(badge, overlay);
        BeginOnLoaded(overlay, cellStoryboards);
    }
}
