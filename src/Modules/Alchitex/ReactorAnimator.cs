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
///   - Waiting: the reactor is powered but blocked on something outside itself - a
///     confirmation dialog, a pack being uninstalled, a folder sweep. A single bright cell
///     orbits the eight perimeter tiles with a fading tail behind it, which is the one
///     stance here that reads as "this is not finished, and it is not your turn yet".
///
/// Running behaviors are built from two ingredients: tiles firing at random, and a
/// travelling gradient (StepGradientWave) - the resting arrangement's own diagonal ramp
/// rolled across the grid. Phases that mean a direction are pure gradient and keep theirs;
/// GeneratingTextures mixes both (StepBusyWork).
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
        { 2, 1, 0 },
        { 3, 2, 1 },
        { 4, 3, 2 },
    };

    // The abort stance. Four corners plus the middle: on a 3x3 grid that reads as an X,
    // which is the whole point - the button has to say "this stops it" without a label.
    private static readonly (int Row, int Col)[] AbortCross =
    {
        (0, 0), (0, 2), (1, 1), (2, 0), (2, 2),
    };

    // The cross only ever moves between these three, and they're all unambiguously red -
    // the X has to hold its shape while it flickers. Anything that dropped toward the blues
    // (or toward black) would break the shape apart every time it pulsed, which is the
    // opposite of "this is dangerous, and it is definitely still here".
    private static readonly Color[] AbortReds =
    {
        ColorHelper.FromArgb(255, 255, 0, 0), // hot
        ColorHelper.FromArgb(255, 205, 0, 0), // mid
        ColorHelper.FromArgb(255, 155, 0, 0), // deep, still clearly red
    };

    // The backdrop the cross is read against - dark enough to disappear, and the darkest
    // thing the reactor's own palette contains.
    private static readonly Color AbortBackdrop = Palette[Palette.Length - 1];

    // The eight perimeter tiles in clockwise order, starting top-left. The waiting stance
    // walks a bright head around this ring; the centre tile is deliberately not part of it,
    // so the ring reads as a ring rather than as nine tiles taking turns.
    private static readonly (int Row, int Col)[] OrbitRing =
    {
        (0, 0), (0, 1), (0, 2), (1, 2), (2, 2), (2, 1), (2, 0), (1, 0),
    };

    private const int GridSize = 3;

    // Nothing reaches the compositor more often than this, however hard the pipeline
    // hammers Pulse. 70ms still reads as "flickering fast" to the eye.
    private const double MinPulseIntervalMs = 70;

    // How fast the waiting stance's head moves around the ring. Slow enough to read as one
    // travelling cell rather than a flicker, fast enough to look impatient.
    private const double OrbitStepMs = 110;

    // A travelling gradient's cycle length: the palette walked out and back again, so the
    // brightest and darkest ends each come round once per cycle with no seam between them.
    private static readonly int WavePeriod = (Palette.Length - 1) * 2;

    // Slightly longer than the pulse throttle, so consecutive steps overlap and the band
    // slides instead of stepping.
    private const double WaveStepMs = 150;

    private const double RestBloomMin = 0.45;
    private const double RestBloomMax = 0.85;

    private readonly Grid _tileGrid;
    private readonly Image? _bloom;

    private readonly SolidColorBrush[,] _brushes = new SolidColorBrush[GridSize, GridSize];

    private readonly Random _random = new();

    private Storyboard? _bloomLoop;
    private DispatcherTimer? _pressHoldTimer;
    private DispatcherTimer? _orbitTimer;
    private DispatcherTimer? _abortHintTimer;
    private DispatcherTimer? _abortHintEndTimer;
    private DateTime _lastPulseUtc = DateTime.MinValue;
    private bool _isGenerating;
    private bool _isAbortHintActive;
    private bool _isWaiting;
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
        StopOrbit();
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

        // The pointer may still be sitting on the button when the run ends, and a wait can
        // still be up if the run ended out from under one. Drop both explicitly, or their
        // flags would keep swallowing every later pulse.
        EndAbortHintImmediate();
        StopOrbit();
        EnterRest();
    }

    // ── Waiting stance ───────────────────────────────────────────────────────

    /// <summary>
    /// The reactor is powered and blocked on something it doesn't control: a confirmation
    /// dialog the user hasn't answered, a pack being uninstalled, a folder sweep. A single
    /// bright cell orbits the eight perimeter tiles with a fading tail behind it - the one
    /// unmistakably "still going, nothing to show yet" shape a 3x3 grid can make.
    ///
    /// Like the abort stance it claims the whole grid, so Pulse is ignored while it's up.
    /// Unlike the abort stance it is not a warning, so it keeps the bloom alive underneath -
    /// during a run that's the loop already breathing, and outside one it starts it, because
    /// a wait outside a run is still the machine doing something.
    ///
    /// Safe to call twice; the second call is a no-op rather than a restart, so nesting
    /// waits (a dialog inside a batch, say) can't reset the orbit halfway round.
    /// </summary>
    public void BeginWaiting()
    {
        if (!_isInitialized || _isWaiting) return;

        _isWaiting = true;
        StopPressHold();

        _orbitHead = 0;
        PaintOrbit(220);

        // Outside a run there's no loop yet; inside one it's already going and this is a
        // no-op that just re-arms it after a suspended-animations toggle.
        StartBloomLoop();

        if (AnimationsSuspended) return;

        StopOrbitTimer();
        _orbitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OrbitStepMs) };
        _orbitTimer.Tick += (s, e) =>
        {
            _orbitHead = (_orbitHead + 1) % OrbitRing.Length;
            PaintOrbit(OrbitStepMs * 2);
        };
        _orbitTimer.Start();
    }

    /// <summary>Whatever the reactor was waiting on has happened. A run in progress goes
    /// back to being repainted by its pulses; otherwise the reactor settles.</summary>
    public void EndWaiting()
    {
        if (!_isInitialized || !_isWaiting) return;

        StopOrbit();

        if (_isGenerating)
        {
            // The next pulse repaints the grid properly; this just gets the orbit off it in
            // the meantime, the same way EndAbortHintImmediate does.
            for (var row = 0; row < GridSize; row++)
                for (var col = 0; col < GridSize; col++)
                    AnimateTile(row, col, RestLayout[row, col], 220);

            return;
        }

        EnterRest();
    }

    // Which ring tile the bright head is currently on, as an index into OrbitRing.
    private int _orbitHead;

    /// <summary>
    /// Draws the orbit as it stands: the head at its brightest, the three tiles behind it
    /// stepping down through the palette, the rest of the ring dark. The centre holds its
    /// resting value so there's something for the ring to be read against.
    /// </summary>
    private void PaintOrbit(double durationMs)
    {
        // The abort stance outranks this - the user is hovering a button that stops the run,
        // and that has to win. The orbit repaints itself on its next tick once the red drops.
        if (_isAbortHintActive) return;

        for (var i = 0; i < OrbitRing.Length; i++)
        {
            var (row, col) = OrbitRing[i];

            // How far behind the head this tile sits, walking backwards around the ring.
            var trail = (_orbitHead - i + OrbitRing.Length) % OrbitRing.Length;

            AnimateTile(row, col, Math.Min(trail, Palette.Length - 1), durationMs);
        }

        AnimateTile(1, 1, RestLayout[1, 1], durationMs);
    }

    private void StopOrbit()
    {
        StopOrbitTimer();
        _isWaiting = false;
    }

    private void StopOrbitTimer()
    {
        if (_orbitTimer == null) return;

        _orbitTimer.Stop();
        _orbitTimer = null;
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
        if (!_isInitialized) return;

        // A pending teardown means the pointer only "left" for a moment - see EndAbortHint.
        StopAbortHintEndTimer();

        if (_isAbortHintActive) return;
        _isAbortHintActive = true;

        foreach (var (row, col) in AbortCross)
            SetTileColor(row, col, AbortReds[0], 90);

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                if (!IsOnCross(row, col))
                    SetTileColor(row, col, AbortBackdrop, 140);

        if (AnimationsSuspended) return;

        // Each cross tile drifts between the three reds on its own schedule - alive and
        // agitated, but never leaving red, so the X never stops being an X.
        StopAbortHintTimer();
        _abortHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _abortHintTimer.Tick += (s, e) =>
        {
            foreach (var (row, col) in AbortCross)
            {
                if (_random.NextDouble() < 0.5) continue; // stagger, so they don't blink in unison

                // Weighted toward the hot end: mostly bright, occasionally banked down.
                var roll = _random.NextDouble();
                var color = roll < 0.5 ? AbortReds[0] : roll < 0.85 ? AbortReds[1] : AbortReds[2];

                SetTileColor(row, col, color, _random.Next(90, 180));
            }
        };
        _abortHintTimer.Start();
    }

    /// <summary>
    /// Pointer left. Deliberately debounced rather than immediate: a tooltip opening over
    /// the button counts as leaving it, and the pointer re-enters a frame later, so acting
    /// straight away made the grid strobe between the red X and the blue phase colors. A
    /// re-entry inside the grace period cancels the teardown entirely.
    /// </summary>
    public void EndAbortHint()
    {
        if (!_isInitialized || !_isAbortHintActive) return;
        if (_abortHintEndTimer != null) return; // already winding down

        if (AnimationsSuspended)
        {
            EndAbortHintImmediate();
            return;
        }

        _abortHintEndTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _abortHintEndTimer.Tick += (s, e) => EndAbortHintImmediate();
        _abortHintEndTimer.Start();
    }

    /// <summary>Drops the abort stance now, no grace period - for the click actually
    /// landing, the run ending, or the window closing.</summary>
    public void EndAbortHintImmediate()
    {
        if (!_isInitialized) return;

        var wasActive = _isAbortHintActive;
        StopAbortHint();

        if (!wasActive) return;

        // A run in progress repaints itself on its next pulse; this just has to get the
        // red off the grid in the meantime.
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
        StopAbortHintEndTimer();
        _isAbortHintActive = false;
    }

    private void StopAbortHintTimer()
    {
        if (_abortHintTimer == null) return;

        _abortHintTimer.Stop();
        _abortHintTimer = null;
    }

    private void StopAbortHintEndTimer()
    {
        if (_abortHintEndTimer == null) return;

        _abortHintEndTimer.Stop();
        _abortHintEndTimer = null;
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

        // The abort stance and the waiting orbit each own the whole grid while they're up -
        // see BeginAbortHint / BeginWaiting.
        if (_isAbortHintActive || _isWaiting) return;

        // Throttle everything. GeneratingTextures alone can arrive thousands of times a
        // second; the rest are rare but there's no reason to special-case them.
        var now = DateTime.UtcNow;
        if ((now - _lastPulseUtc).TotalMilliseconds < MinPulseIntervalMs) return;
        _lastPulseUtc = now;

        switch (phase)
        {
            case Core.AlchitexPhase.GeneratingTextures:
                StepBusyWork();
                break;

            case Core.AlchitexPhase.StrippingPbr:
                StepErasure();
                break;

            case Core.AlchitexPhase.RemovingPack:
                PlayImplosion();
                break;

            // Staging: nothing is being written yet. A single tile lifts toward the bright
            // end, like a needle twitching before the machine spins up.
            case Core.AlchitexPhase.Staging:
                AnimateTile(_random.Next(GridSize), _random.Next(GridSize), _random.Next(0, 2), 260);
                break;

            // Reading the pack's folders top to bottom - so does the gradient. Locked
            // downward, because a scan that reversed direction would be a lie about what
            // the pass is doing.
            case Core.AlchitexPhase.ScanningTextures:
                StepGradientWave(WaveAxis.Vertical, forward: true);
                break;

            // Water and glass sweep sideways, the way a pass over a surface does.
            case Core.AlchitexPhase.WaterAndGlass:
                StepGradientWave(WaveAxis.Horizontal, forward: true);
                break;

            // Fog rises.
            case Core.AlchitexPhase.Fog:
                StepGradientWave(WaveAxis.Vertical, forward: false);
                break;

            // The last two passes each walk a bright cell one step along a random path, so
            // the end of a run reads as something tracing its way out rather than another
            // sweep - these are bookkeeping over a finished pack, not work across it.
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

    /// <summary>The four axes a gradient can travel along. Ranks are scaled so every axis
    /// spans the whole palette across the grid, whether it crosses three tiles or five.</summary>
    private enum WaveAxis
    {
        /// <summary>Left to right.</summary>
        Horizontal,
        /// <summary>Top to bottom.</summary>
        Vertical,
        /// <summary>Top-left to bottom-right.</summary>
        Diagonal,
        /// <summary>Top-right to bottom-left - the axis the resting arrangement itself sits
        /// on, so a wave along it is literally the logo's own gradient set moving.</summary>
        AntiDiagonal,
    }

    private WaveAxis _waveAxis = WaveAxis.AntiDiagonal;
    private bool _waveForward = true;
    private int _wavePhase;

    // Pulses left in the current sweep. Zero means back to firing at random.
    private int _waveBurstRemaining;

    // In pulses rather than seconds, since the pulse rate is the pipeline's own throughput.
    // Works out to a sweep every couple of seconds, lasting about one.
    private const double WaveBurstChance = 0.04;
    private const int MinWaveBurstSteps = 8;
    private const int MaxWaveBurstSteps = 15;

    /// <summary>
    /// Textures being written: mostly every tile firing at once, with a sweep cutting
    /// through every couple of seconds.
    ///
    /// The mix is deliberate. Pure noise reads as static and stops meaning anything after a
    /// second of watching, and a pure gradient is far too orderly for a phase that means
    /// "flat out, in no particular order".
    /// </summary>
    private void StepBusyWork()
    {
        if (_waveBurstRemaining > 0)
        {
            _waveBurstRemaining--;
            StepGradientWave();
            return;
        }

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, _random.Next(Palette.Length), 90);

        if (_random.NextDouble() >= WaveBurstChance) return;

        // Phase 0 so the sweep leaves the resting arrangement rather than cutting in
        // halfway through a ramp.
        RerollWave();
        _wavePhase = 0;
        _waveBurstRemaining = _random.Next(MinWaveBurstSteps, MaxWaveBurstSteps + 1);
    }

    /// <summary>
    /// Advances a gradient one step along its axis. Every tile is repainted every step, but
    /// from a single ramp rather than independently, so what moves is the arrangement -
    /// which is why this reads as the mark itself flowing instead of nine tiles flickering.
    ///
    /// Driven by Pulse, not by a timer: the wave's speed is the pipeline's actual
    /// throughput, so a pack full of tiny textures visibly races and a slow one crawls.
    ///
    /// A caller that names an axis owns it for as long as its phase lasts (see Pulse's phase
    /// map). Called with no axis, the wave re-rolls its own direction at the end of each
    /// cycle - that's the "keeps finding new ways to move" behavior, and it belongs only to
    /// the phase that runs long enough to need it.
    /// </summary>
    private void StepGradientWave(WaveAxis? axis = null, bool? forward = null)
    {
        if (axis.HasValue) _waveAxis = axis.Value;
        if (forward.HasValue) _waveForward = forward.Value;

        _wavePhase++;

        if (_wavePhase >= WavePeriod)
        {
            _wavePhase = 0;

            // Only a wave that wasn't given a direction gets to pick a new one.
            if (!axis.HasValue) RerollWave();
        }

        var offset = _waveForward ? _wavePhase : -_wavePhase;

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, RampIndex(WaveRank(_waveAxis, row, col) + offset), WaveStepMs);
    }

    private void RerollWave()
    {
        _waveAxis = (WaveAxis)_random.Next(4);
        _waveForward = _random.NextDouble() < 0.5;
    }

    /// <summary>Where a tile sits along an axis, on the palette's own 0..4 scale. The
    /// anti-diagonal case is exactly RestLayout, which is the whole trick: at phase 0 an
    /// anti-diagonal wave IS the resting arrangement.</summary>
    private static int WaveRank(WaveAxis axis, int row, int col) => axis switch
    {
        WaveAxis.Horizontal => col * 2,
        WaveAxis.Vertical => row * 2,
        WaveAxis.Diagonal => row + col,
        _ => row + (GridSize - 1 - col),
    };

    /// <summary>Walks the palette out and back again rather than wrapping it, so the cycle
    /// has no seam where the darkest tile snaps back to the brightest.</summary>
    private static int RampIndex(int position)
    {
        var wrapped = ((position % WavePeriod) + WavePeriod) % WavePeriod;

        return wrapped <= Palette.Length - 1 ? wrapped : WavePeriod - wrapped;
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

    // The breath, as (millisecond, opacity) pairs. Uneven on purpose: two of the three
    // peaks go all the way to 1.0 and the troughs never fully collapse, so the reactor reads
    // as something with a lot of light in it being modulated - not something being switched
    // on and off. An even sine would loop invisibly; this one you notice.
    private static readonly (double AtMs, double Opacity)[] BloomBreath =
    {
        (0, 0.35),
        (760, 1.00),
        (1500, 0.50),
        (2200, 0.88),
        (2900, 0.40),
        (3500, 1.00),
        (4200, 0.35),
    };

    /// <summary>The bloom breathing under everything else for as long as a run - or a wait -
    /// lasts. Calling it while it's already running restarts it, which is why every caller
    /// checks that it isn't.</summary>
    private void StartBloomLoop()
    {
        if (_bloom == null || AnimationsSuspended) return;
        if (_bloomLoop != null) return;

        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };

        foreach (var (atMs, opacity) in BloomBreath)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(atMs),
                Value = opacity,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
        }

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
        StopOrbit();
        StopAbortHint();
        StopBloomLoop();
    }
}
