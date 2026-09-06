using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vanilla_RTX_App.Modules.Alchitex;

/// <summary>
/// The pack tiles moving between the two queue rows and the reactor.
///
/// A partial of the window rather than a class of its own, deliberately. Every method here
/// reads the window's named XAML elements (InputQueuePanel, GenerateButton,
/// QueueEjectionHost) and measures against its live layout; handing all of that to a
/// separate type would be plumbing in exchange for nothing. What it isn't is part of the
/// window's *logic*, which is why it doesn't belong in the same file as the batch loop.
///
/// Four departures, four directions, and the distinction is the whole point of having them:
/// up and out for a discarded pack, right into the reactor for an accepted one, left back
/// down the row for a result, and straight out the far side for a failure. A pack should
/// never have to be read to know what happened to it.
///
/// Each one is paired with a matching stance on the reactor itself (ReactorAnimator.
/// PlayQueueWash), washing the same direction the tile travels, so a hand-off reads as one
/// event rather than two things happening near each other.
///
/// All of them no-op into their end state when SuspendUIAnimations is on - the queue still
/// updates, it just stops moving.
/// </summary>
public sealed partial class Alchitex
{
    private const double DismissAnimationMs = 150;
    private const double IntoReactorAnimationMs = 260;
    private const double ArrivalAnimationMs = 220;
    private const double EjectAnimationMs = 380;

    /// <summary>
    /// How small a tile gets on its way into the reactor, and how it fades.
    ///
    /// Both were more aggressive (0.35 and a plain quadratic fade) and the tile read as
    /// evaporating on the way rather than being taken in. Shrinking is what conveys
    /// distance, so it stays, but only enough to read as perspective; and the fade is
    /// cubic-in, which holds the tile near full opacity for most of the trip and drops it
    /// over the last stretch, where the reactor is about to cover it anyway.
    /// </summary>
    private const double ReactorTravelScale = 0.75;

    private static EasingFunctionBase TravelEase(EasingMode mode)
        => new QuadraticEase { EasingMode = mode };

    private static EasingFunctionBase FadeEase(EasingMode mode)
        => new CubicEase { EasingMode = mode };

    /// <summary>Discarded or skipped: straight up and out.</summary>
    private async Task AnimateDismissAsync(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;

        await RunStoryboardAsync(
            BuildTileStoryboard(tile, DismissAnimationMs, translateY: -40, opacity: 0,
                easing: TravelEase(EasingMode.EaseIn), opacityEasing: FadeEase(EasingMode.EaseIn)),
            DismissAnimationMs);
    }

    /// <summary>Accepted for generation: flies right, into the reactor, shrinking as it
    /// goes. The tiles behind it then slide up into the gap (RenderQueues redraws them at
    /// their new positions, and AnimateReflow covers the jump).</summary>
    private async Task AnimateIntoReactorAsync(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;

        await RunStoryboardAsync(
            BuildTileStoryboard(tile, IntoReactorAnimationMs,
                translateX: ReactorTravelDistance(tile), opacity: 0, scale: ReactorTravelScale,
                easing: TravelEase(EasingMode.EaseIn), opacityEasing: FadeEase(EasingMode.EaseIn)),
            IntoReactorAnimationMs);
    }

    /// <summary>The tiles left in the input row closing the gap the departed one left.</summary>
    private void AnimateReflow()
    {
        if (AnimationsSuspended) return;

        foreach (var child in InputQueuePanel.Children.OfType<FrameworkElement>())
        {
            if (child.RenderTransform is not CompositeTransform transform) continue;

            transform.TranslateX = _packTileSize + 12; // where it was before the gap closed
            _ = RunStoryboardAsync(
                BuildTileStoryboard(child, 180, translateX: 0, easing: TravelEase(EasingMode.EaseOut)),
                180);
        }
    }

    /// <summary>
    /// A finished pack coming out of the reactor - AnimateIntoReactorAsync played
    /// backwards: it starts small and transparent, off at the reactor's side, and travels
    /// back down the row into place. Same distance basis, scale and easing (mirrored) as the
    /// intake, so the two read as one motion in opposite directions.
    /// </summary>
    private async Task AnimateArrivalAsync(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;
        if (tile.RenderTransform is not CompositeTransform transform) return;

        transform.TranslateX = ReactorTravelDistance(tile);
        transform.ScaleX = transform.ScaleY = ReactorTravelScale;
        tile.Opacity = 0;

        await RunStoryboardAsync(
            BuildTileStoryboard(tile, ArrivalAnimationMs,
                translateX: 0, opacity: 1, scale: 1.0,
                easing: TravelEase(EasingMode.EaseOut), opacityEasing: FadeEase(EasingMode.EaseOut)),
            ArrivalAnimationMs);
    }

    /// <summary>How far a tile has to travel to reach the reactor from where it sits.
    /// Shared by the intake and the arrival so the two mirror each other exactly.</summary>
    private double ReactorTravelDistance(FrameworkElement tile)
        => Math.Max(120, PackQueueHost.ActualWidth - tile.ActualOffset.X);

    /// <summary>
    /// A pack that errored out: thrown clear through the reactor and off the far side,
    /// rather than coming back down the output row like a finished one.
    ///
    /// The tile is built fresh here: the original left the input row when the pack was
    /// handed over, so there's nothing left to animate by the time the failure is known.
    /// It's parented to QueueEjectionHost (see the XAML) because the queue rows clip.
    /// </summary>
    private async Task EjectFailedPackAsync(string location, string packName)
    {
        if (AnimationsSuspended || QueueEjectionHost == null || _isClosing) return;

        try
        {
            var tile = BuildPackTile(location, packName, allowDiscard: false);

            // Start where the pack was last seen - the reactor's leading edge, on the
            // input row's line - measured rather than assumed, so it stays right through
            // any layout change.
            var reactorEdge = GenerateButton.TransformToVisual(QueueEjectionHost)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var rowLine = InputQueuePanel.TransformToVisual(QueueEjectionHost)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.VerticalAlignment = VerticalAlignment.Top;
            tile.Margin = new Thickness(reactorEdge.X, rowLine.Y, 0, 0);

            QueueEjectionHost.Children.Add(tile);

            // Left to right, matching its travel: a failure still leaves the way an intake
            // arrived, which is what makes it read as "thrown through" rather than "handed
            // back". A returned or finished pack washes the other way.
            _reactor?.PlayQueueWash(leftToRight: true);

            using (BeginQueueTransition())
            {
                await RunStoryboardAsync(
                    BuildTileStoryboard(tile, EjectAnimationMs,
                        translateX: GenerateButton.ActualWidth + _packTileSize,
                        opacity: 0,
                        scale: 0.85,
                        easing: TravelEase(EasingMode.EaseIn), opacityEasing: FadeEase(EasingMode.EaseIn)),
                    EjectAnimationMs);
            }

            QueueEjectionHost.Children.Remove(tile);
        }
        catch (Exception ex)
        {
            // Cosmetic to the last: a failed pack is already being reported properly, and
            // a broken flourish must not turn into a second failure.
            Trace.WriteLine($"[ALCHITEX] Couldn't play the ejection animation for '{packName}': {ex.Message}");
        }
    }

    /// <summary>
    /// One storyboard covering any combination of translate/scale/opacity on a tile.
    /// CompositeTransform properties are dependent animations (they run on the UI thread),
    /// which is fine for the handful of tiles ever moving at once - and keeps this to one
    /// small helper instead of a composition-animation layer.
    ///
    /// Opacity takes its own easing because it wants a different curve from the motion: the
    /// travel should accelerate, while the fade should stay out of the way until the tile
    /// is nearly there. One shared curve is what made a tile look like it was dissolving
    /// halfway across.
    /// </summary>
    private static Storyboard BuildTileStoryboard(
        FrameworkElement element,
        double durationMs,
        double? translateX = null,
        double? translateY = null,
        double? opacity = null,
        double? scale = null,
        EasingFunctionBase? easing = null,
        EasingFunctionBase? opacityEasing = null)
    {
        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(durationMs);

        void Add(double to, string property, DependencyObject target, bool dependent, EasingFunctionBase? curve)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EnableDependentAnimation = dependent,
                EasingFunction = curve,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        if (element.RenderTransform is CompositeTransform transform)
        {
            if (translateX.HasValue) Add(translateX.Value, "TranslateX", transform, true, easing);
            if (translateY.HasValue) Add(translateY.Value, "TranslateY", transform, true, easing);
            if (scale.HasValue)
            {
                Add(scale.Value, "ScaleX", transform, true, easing);
                Add(scale.Value, "ScaleY", transform, true, easing);
            }
        }

        if (opacity.HasValue) Add(opacity.Value, "Opacity", element, false, opacityEasing ?? easing);

        return storyboard;
    }

    /// <summary>
    /// Awaits a storyboard - but never forever, which is the entire reason this takes a
    /// duration.
    ///
    /// Completed does not fire if a storyboard is stopped, or if its target leaves the
    /// visual tree mid-flight, and the queue rows are cleared and rebuilt by RenderQueues
    /// on any change. The batch loop awaits these hand-offs before starting a pack's run,
    /// so a single missed event would not skip a flourish - it would hang the run, with the
    /// window still responsive and nothing visibly wrong. The timeout is the belt: whatever
    /// happens to the storyboard, the await comes back.
    /// </summary>
    private static async Task RunStoryboardAsync(Storyboard storyboard, double durationMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        storyboard.Completed += (s, e) => tcs.TrySetResult(true);
        storyboard.Begin();

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMilliseconds(durationMs + 250)));
    }
}
