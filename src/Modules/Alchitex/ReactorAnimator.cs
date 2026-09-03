using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Vanilla_RTX_App.Modules.Alchitex;

/// <summary>
/// Drives the Generate button's three layers so the reactor reads as a live instrument
/// rather than a picture of one - the same idea as the main window's lamp
/// (Core/LampAnimator.cs), tied here to what generation is actually doing:
///
///   1. Background - a 3x3 grid of tiles built here, not an image. Each tile holds one of
///      the logo's five blues, and the whole point of this class is deciding when and how
///      those change.
///   2. Logo - the static mark. Never animated; it's the still point everything else moves
///      around.
///   3. Bloom - the same mark with its glow, animated by opacity alone.
///
/// Vocabulary, by state:
///   - Rest: the tiles' default diagonal arrangement (brightest top-right, darkest
///     bottom-left, straight off the reference art) and the bloom sitting at a random
///     25-50%, re-rolled every time it comes back to rest so it never looks pinned.
///   - Press-and-hold: tiles fire erratically, a couple at a time, while the bloom drops
///     to near zero - the reactor visibly winding up under the finger.
///   - Generating: one behavior per phase (see Pulse). Underneath all of them the bloom
///     breathes on a loop, which is what separates "running" from "idle" at a glance.
///
/// Everything routes through AnimateTile/SetBloom, which honor
/// TunerVariables.Persistent.SuspendUIAnimations: with it on, every transition is applied
/// instantly and the two looping behaviors (bloom breathing, press-hold flicker) never
/// start. The reactor still tracks state, it just stops moving.
///
/// Cost control matters here: PulseGeneratingTextures is called once per texture, which on
/// a real pack is thousands of calls a second. Every pulse goes through a throttle
/// (MinPulseIntervalMs) so what reaches the compositor is bounded no matter how fast the
/// pipeline runs. Nine brushes' worth of color animation is cheap; nine thousand is not.
/// </summary>
public sealed class ReactorAnimator
{
    // The RTX Reactor logo's five blues, brightest first. Every tile is always one of
    // these - the reactor never shows a color that isn't part of the mark.
    private static readonly Color[] Palette =
    {
        ColorHelper.FromArgb(255, 0x00, 0x48, 0x8A), // brightest
        ColorHelper.FromArgb(255, 0x00, 0x3B, 0x72),
        ColorHelper.FromArgb(255, 0x00, 0x35, 0x66),
        ColorHelper.FromArgb(255, 0x00, 0x29, 0x4E),
        ColorHelper.FromArgb(255, 0x00, 0x23, 0x42), // darkest
    };

    // Resting arrangement, as palette indices - a diagonal gradient with the brightest
    // cell top-right and the darkest bottom-left, matching the reference art.
    private static readonly int[,] RestLayout =
    {
        { 1, 1, 0 },
        { 3, 2, 1 },
        { 4, 3, 2 },
    };

    // The abort stance. Four corners plus the middle: on a 3x3 grid that reads as an X,
    // which is the whole point - the button has to say "this stops it" without a label.
    private static readonly (int Row, int Col)[] AbortCross =
    {
        (0, 0), (0, 2), (1, 1), (2, 0), (2, 2),
    };

    private static readonly Color AbortRedBright = ColorHelper.FromArgb(255, 0xFF, 0x2E, 0x2E);
    private static readonly Color AbortRedDeep = ColorHelper.FromArgb(255, 0x7A, 0x00, 0x00);

    private const int GridSize = 3;

    // Nothing reaches the compositor more often than this, however hard the pipeline
    // hammers Pulse. 70ms still reads as "flickering fast" to the eye.
    private const double MinPulseIntervalMs = 70;

    private const double RestBloomMin = 0.25;
    private const double RestBloomMax = 0.50;

    private readonly Grid _tileGrid;
    private readonly Image? _bloom;

    private readonly Border[,] _tiles = new Border[GridSize, GridSize];
    private readonly SolidColorBrush[,] _brushes = new SolidColorBrush[GridSize, GridSize];

    private readonly Random _random = new();

    private Storyboard? _bloomLoop;
    private DispatcherTimer? _pressHoldTimer;
    private DispatcherTimer? _abortHintTimer;
    private DateTime _lastPulseUtc = DateTime.MinValue;
    private bool _isGenerating;
    private bool _isAbortHintActive;
    private bool _isInitialized;

    private static bool AnimationsSuspended => TunerVariables.Persistent.SuspendUIAnimations;

    public ReactorAnimator(Grid tileGrid, Image? bloomLayer)
    {
        _tileGrid = tileGrid ?? throw new ArgumentNullException(nameof(tileGrid));
        _bloom = bloomLayer;
    }

    /// <summary>Builds the 3x3 tiles and puts the reactor in its resting state. Safe to
    /// call more than once; only the first call does anything.</summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        _tileGrid.RowDefinitions.Clear();
        _tileGrid.ColumnDefinitions.Clear();
        _tileGrid.Children.Clear();

        for (var i = 0; i < GridSize; i++)
        {
            _tileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var brush = new SolidColorBrush(Palette[RestLayout[row, col]]);
                var tile = new Border { Background = brush };

                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, col);

                _brushes[row, col] = brush;
                _tiles[row, col] = tile;
                _tileGrid.Children.Add(tile);
            }
        }

        _isInitialized = true;
        EnterRest();
    }

    // ── States ───────────────────────────────────────────────────────────────

    /// <summary>Back to the default arrangement, with a freshly rolled bloom level.</summary>
    public void EnterRest()
    {
        if (!_isInitialized) return;

        StopPressHold();
        StopBloomLoop();
        _isGenerating = false;

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, RestLayout[row, col], 420);

        SetBloom(RestBloomMin + _random.NextDouble() * (RestBloomMax - RestBloomMin), 420);
    }

    /// <summary>Pointer down on the reactor: tiles start firing erratically and the bloom
    /// collapses, as though the charge is being drawn out of it.</summary>
    public void BeginPressHold()
    {
        if (!_isInitialized) return;

        SetBloom(_random.NextDouble() * 0.10, 140);

        // One erratic burst either way, so a quick click still registers visually with
        // animations suspended or a timer that never gets to tick.
        FlickerRandomTiles(2, 90);

        if (AnimationsSuspended) return;

        StopPressHold();
        _pressHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _pressHoldTimer.Tick += (s, e) => FlickerRandomTiles(_random.Next(1, 4), 90);
        _pressHoldTimer.Start();
    }

    /// <summary>Pointer released or capture lost. Hands back to whatever the reactor should
    /// be doing - a run in progress keeps its behavior, otherwise it settles.</summary>
    public void EndPressHold()
    {
        if (!_isInitialized) return;

        StopPressHold();

        if (_isGenerating)
        {
            StartBloomLoop();
            return;
        }

        EnterRest();
    }

    /// <summary>A run has started: the bloom begins breathing and keeps at it until
    /// EndGeneration, underneath whatever the per-phase pulses are doing.</summary>
    public void BeginGeneration()
    {
        if (!_isInitialized) return;

        _isGenerating = true;
        StartBloomLoop();
    }

    public void EndGeneration()
    {
        if (!_isInitialized) return;

        _isGenerating = false;

        // The pointer may still be sitting on the button when the run ends. Drop the abort
        // stance explicitly, or its flag would keep swallowing every later pulse.
        StopAbortHint();
        EnterRest();
    }

    // ── Abort stance ─────────────────────────────────────────────────────────

    /// <summary>
    /// The reactor's "click me and this stops" face, shown while the pointer is over the
    /// button during a run: the cross lights up in an agitated red while every other tile
    /// drops to the darkest blue so the X reads cleanly.
    ///
    /// It deliberately claims the whole grid - Pulse is ignored for as long as this is up,
    /// which is why the phase animations visibly stop dead underneath it. That interruption
    /// is the message: the thing you're about to do is abrupt.
    ///
    /// The bloom is left completely alone. It belongs to the run, and the run is still
    /// going until the user actually commits.
    /// </summary>
    public void BeginAbortHint()
    {
        if (!_isInitialized || _isAbortHintActive) return;

        _isAbortHintActive = true;

        foreach (var (row, col) in AbortCross)
            SetTileColor(row, col, AbortRedBright, 90);

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                if (!IsOnCross(row, col))
                    AnimateTile(row, col, Palette.Length - 1, 140);

        if (AnimationsSuspended) return;

        // Every cross tile pulses on its own schedule between deep and bright, which is
        // what makes it read as live and unstable rather than a static red X.
        StopAbortHintTimer();
        _abortHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _abortHintTimer.Tick += (s, e) =>
        {
            foreach (var (row, col) in AbortCross)
            {
                if (_random.NextDouble() < 0.45) continue; // stagger, so they don't blink in unison

                var hot = _random.NextDouble() < 0.5;
                SetTileColor(row, col, hot ? AbortRedBright : AbortRedDeep, _random.Next(70, 150));
            }
        };
        _abortHintTimer.Start();
    }

    /// <summary>Pointer left, or the run ended. Hands the grid back to whatever owns it
    /// now - a run in progress repaints itself on its next pulse, so this only has to undo
    /// the red.</summary>
    public void EndAbortHint()
    {
        if (!_isInitialized || !_isAbortHintActive) return;

        StopAbortHint();

        if (_isGenerating)
        {
            for (var row = 0; row < GridSize; row++)
                for (var col = 0; col < GridSize; col++)
                    AnimateTile(row, col, RestLayout[row, col], 220);
            return;
        }

        EnterRest();
    }

    private void StopAbortHint()
    {
        StopAbortHintTimer();
        _isAbortHintActive = false;
    }

    private void StopAbortHintTimer()
    {
        if (_abortHintTimer == null) return;

        _abortHintTimer.Stop();
        _abortHintTimer = null;
    }

    private static bool IsOnCross(int row, int col)
    {
        foreach (var (r, c) in AbortCross)
            if (r == row && c == col) return true;

        return false;
    }

    // ── Per-phase behavior ───────────────────────────────────────────────────

    /// <summary>
    /// The reactor's reaction to one step of the pipeline. Called from the window's
    /// progress handler, which gets the phase straight from AlchitexPipeline rather than
    /// from parsing status strings - if a phase is added there, it gets a behavior here.
    /// </summary>
    public void Pulse(Core.AlchitexPhase phase)
    {
        if (!_isInitialized) return;

        // The abort stance owns the whole grid while it's up - see BeginAbortHint.
        if (_isAbortHintActive) return;

        // Throttle everything. GeneratingTextures alone can arrive thousands of times a
        // second; the rest are rare but there's no reason to special-case them.
        var now = DateTime.UtcNow;
        if ((now - _lastPulseUtc).TotalMilliseconds < MinPulseIntervalMs) return;
        _lastPulseUtc = now;

        switch (phase)
        {
            // Every tile jumps at once, fast: the busiest thing the pipeline does, and it
            // should look like it.
            case Core.AlchitexPhase.GeneratingTextures:
                for (var row = 0; row < GridSize; row++)
                    for (var col = 0; col < GridSize; col++)
                        AnimateTile(row, col, _random.Next(Palette.Length), 90);
                break;

            case Core.AlchitexPhase.StrippingPbr:
                StepErasure();
                break;

            case Core.AlchitexPhase.RemovingPack:
                PlayImplosion();
                break;

            // Staging and scanning: nothing is being written yet. A single tile lifts
            // toward the bright end, like a needle twitching before the machine spins up.
            case Core.AlchitexPhase.Staging:
            case Core.AlchitexPhase.ScanningTextures:
                AnimateTile(_random.Next(GridSize), _random.Next(GridSize), _random.Next(0, 2), 260);
                break;

            // The finishing passes each walk a bright cell one step along a random path,
            // so the last stretch of a run reads as something tracing its way out.
            case Core.AlchitexPhase.WaterAndGlass:
            case Core.AlchitexPhase.Fog:
            case Core.AlchitexPhase.Finalizing:
            case Core.AlchitexPhase.Bookkeeping:
                StepTrail();
                break;

            // A pack just landed. One bright wash across the grid, corner to corner.
            case Core.AlchitexPhase.Done:
                PlayCompletionWash();
                break;
        }
    }

    // ── Behaviors ────────────────────────────────────────────────────────────

    private void FlickerRandomTiles(int count, double durationMs)
    {
        for (var i = 0; i < count; i++)
            AnimateTile(_random.Next(GridSize), _random.Next(GridSize), _random.Next(Palette.Length), durationMs);
    }

    // Where the erasure head currently is, as a flat 0..8 index in row-major order.
    private int _erasureHead = -1;

    /// <summary>
    /// Stripping a pack's existing PBR: a wipe head travels the grid in reading order,
    /// snapping the tile under it to black-dark instantly while the tiles behind it bleed
    /// back up toward their resting values. What it should look like is content being
    /// cleared off a surface, one cell at a time, with the cleared trail slowly recovering
    /// - not a random flicker, because this phase isn't random work, it's a sweep.
    /// </summary>
    private void StepErasure()
    {
        _erasureHead = (_erasureHead + 1) % (GridSize * GridSize);

        var head = _erasureHead;
        var headRow = head / GridSize;
        var headCol = head % GridSize;

        // The head itself: hard cut to the darkest value, no easing in - erasure is not
        // a fade, it's a removal.
        AnimateTile(headRow, headCol, Palette.Length - 1, 0);

        // Two cells behind it, recovering at different rates so the trail has depth.
        var behind1 = (head - 1 + GridSize * GridSize) % (GridSize * GridSize);
        var behind2 = (head - 2 + GridSize * GridSize) % (GridSize * GridSize);

        AnimateTile(behind1 / GridSize, behind1 % GridSize, Palette.Length - 2, 260);
        AnimateTile(behind2 / GridSize, behind2 % GridSize, RestLayout[behind2 / GridSize, behind2 % GridSize], 520);
    }

    /// <summary>
    /// Uninstalling the original pack: everything flashes bright for an instant, then
    /// collapses to black from the outside in - the corners first, the middle last, each
    /// ring delayed behind the one outside it. An implosion, because that's what deleting
    /// the thing you started from feels like it should look like.
    /// </summary>
    private void PlayImplosion()
    {
        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                // Chebyshev distance from the centre: 1 for the ring, 0 for the middle.
                var ring = Math.Max(Math.Abs(row - 1), Math.Abs(col - 1));

                // The flash.
                AnimateTile(row, col, 0, 90);

                // The collapse, outer ring first, centre last and slowest.
                AnimateTile(row, col, Palette.Length - 1, 260, delayMs: 120 + (1 - ring) * 220);
            }
        }
    }

    /// <summary>
    /// A pack finished: one bright wash sweeping corner to corner, each tile lit on a
    /// delay proportional to how far along the diagonal it sits, then released back to
    /// rest behind the wash.
    /// </summary>
    private void PlayCompletionWash()
    {
        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var diagonal = row + col; // 0..4, top-left to bottom-right
                var delay = diagonal * 70.0;

                AnimateTile(row, col, 0, 120, delayMs: delay);
                AnimateTile(row, col, RestLayout[row, col], 320, delayMs: delay + 160);
            }
        }
    }

    // Where the bright cell currently sits, so consecutive trail steps move rather than
    // teleport. -1 means "no trail yet, start anywhere".
    private int _trailRow = -1;
    private int _trailCol = -1;

    /// <summary>
    /// Moves a single bright cell one step to a neighbouring tile, dimming the one it
    /// leaves back toward that tile's resting value. Repeated calls draw a wandering
    /// bright trail across the grid.
    /// </summary>
    private void StepTrail()
    {
        if (_trailRow < 0)
        {
            _trailRow = _random.Next(GridSize);
            _trailCol = _random.Next(GridSize);
        }
        else
        {
            AnimateTile(_trailRow, _trailCol, RestLayout[_trailRow, _trailCol], 320);

            // A step in one axis, staying on the grid.
            if (_random.NextDouble() < 0.5)
                _trailRow = Math.Clamp(_trailRow + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
            else
                _trailCol = Math.Clamp(_trailCol + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
        }

        AnimateTile(_trailRow, _trailCol, 0, 200);
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    private void AnimateTile(int row, int col, int paletteIndex, double durationMs, double delayMs = 0)
        => SetTileColor(row, col, Palette[Math.Clamp(paletteIndex, 0, Palette.Length - 1)], durationMs, delayMs);

    /// <summary>
    /// The one place a tile's color ever changes. Takes a Color rather than a palette index
    /// so the abort stance can paint its reds through the same path as everything else.
    /// </summary>
    private void SetTileColor(int row, int col, Color target, double durationMs, double delayMs = 0)
    {
        var brush = _brushes[row, col];

        if (AnimationsSuspended || (durationMs <= 0 && delayMs <= 0))
        {
            brush.Color = target;
            return;
        }

        // A brush's Color is a dependent animation - it runs on the UI thread rather than
        // the compositor. Fine at this scale (nine brushes, throttled), and the honest
        // alternative (five stacked opacity layers per tile) buys nothing here.
        var animation = new ColorAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(Math.Max(durationMs, 1)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void SetBloom(double opacity, double durationMs)
    {
        if (_bloom == null) return;

        if (AnimationsSuspended || durationMs <= 0)
        {
            _bloom.Opacity = opacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        Storyboard.SetTarget(animation, _bloom);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>The bloom breathing under everything else for as long as a run lasts.</summary>
    private void StartBloomLoop()
    {
        if (_bloom == null || AnimationsSuspended) return;

        StopBloomLoop();

        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.15 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(900),
            Value = 0.70,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(1800),
            Value = 0.15,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });

        Storyboard.SetTarget(animation, _bloom);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _bloomLoop = new Storyboard();
        _bloomLoop.Children.Add(animation);
        _bloomLoop.Begin();
    }

    private void StopBloomLoop()
    {
        if (_bloomLoop == null) return;

        _bloomLoop.Stop();
        _bloomLoop = null;
    }

    private void StopPressHold()
    {
        if (_pressHoldTimer == null) return;

        _pressHoldTimer.Stop();
        _pressHoldTimer = null;
    }

    /// <summary>Stops every loop this animator owns. Call on window close so a timer or a
    /// forever-storyboard can't outlive the window it was animating.</summary>
    public void Shutdown()
    {
        StopPressHold();
        StopAbortHint();
        StopBloomLoop();
    }
}
