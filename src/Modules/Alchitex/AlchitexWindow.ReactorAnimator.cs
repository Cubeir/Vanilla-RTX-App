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
/// (Core/MainWindow.LampAnimator.cs), tied here to what generation is actually doing:
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
/// How often a phase reports decides what kind of behavior it can have, and getting this
/// wrong is invisible in code and obvious on screen:
///   - Reports continuously (GeneratingTextures) -> a per-pulse behavior. It is also the
///     only phase long enough to show off a one-shot flourish.
///   - Reports once, then works for a long time in silence (Staging, copying thousands of
///     files) -> a timer. BeginPhaseOrbit exists for exactly this: a per-pulse behavior has
///     a single pulse to work with and leaves the reactor looking switched off.
///   - Reports once and is immediately followed by more work (ScanningTextures) -> a
///     one-shot flourish, which gets to finish on screen under whatever comes next.
///
/// Running behaviors are built from two ingredients: tiles firing at random, and a
/// travelling gradient (StepGradientWave) - the resting arrangement's own diagonal ramp
/// rolled across the grid. Phases that mean a direction are pure gradient and keep theirs;
/// GeneratingTextures mixes both (StepBusyWork).
///
/// On top of those sit the one-shot flourishes - PlayQueueWash, PlayRipple, PlayImplosion,
/// PlayCompletionWash. They aren't driven by a pulse or a timer; something happened and the
/// reactor answers once, across staggered tiles over several hundred milliseconds. Because
/// of that they take the grid for their duration (ClaimGrid), the same way the abort stance
/// and the waiting orbit do - otherwise the next pulse repaints all nine tiles and the
/// flourish never finishes.
///
/// Everything routes through AnimateTile/SetBloom, which honor
/// EnvironmentVariables.Persistent.SuspendUIAnimations: with it on, every transition is applied
/// instantly and the two looping behaviors (bloom breathing, press-hold flicker) never
/// start. The reactor still tracks state, it just stops moving.
///
/// Cost control matters here, and it has two halves that are easy to confuse:
///
///   * The frame rate is MinPulseIntervalMs. PulseGeneratingTextures is called once per
///     texture - thousands of calls a second on a real pack - and the throttle is what
///     bounds that to a fixed cost. Nine brushes' worth of colour animation is cheap; nine
///     thousand is not. This is the knob for a slow machine.
///   * How fast it *feels* is the transition durations, which are a separate thing
///     entirely. They are all shorter than the pulse interval on purpose: a transition
///     still easing when the next one starts on top of it is what made the reactor look
///     sluggish while changing stances rapidly.
///
/// A tile runs exactly one storyboard at a time, and starting a new sequence stops the one
/// before it - see PlayTileSequence for why that is load-bearing rather than tidy, and what
/// it means for anything that wants two things happening on one tile at once.
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

    // The abort stance. Four corners plus the middle: on a 3x3 grid that reads as an X for example.
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
        ColorHelper.FromArgb(255, 255, 0, 0),
        ColorHelper.FromArgb(255, 192, 0, 0), 
        ColorHelper.FromArgb(255, 128, 0, 0),
        ColorHelper.FromArgb(255, 96, 0, 0), 
        ColorHelper.FromArgb(255, 64, 0, 0),
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
    // hammers Pulse. ~14 repaints a second of a nine-tile grid, which is the whole reason
    // a run of thousands of textures costs the same as a run of ten.
    //
    // This is the frame rate, and it is deliberately NOT what makes the reactor feel quick.
    // Raising it costs real work on an iGPU for very little; the transition durations below
    // are the lever, and they are all shorter than the interval so a change lands inside its
    // own slot instead of still easing when the next one starts on top of it.
    private const double MinPulseIntervalMs = 70;

    // How fast the waiting stance's head moves around the ring. Slow enough to read as one
    // travelling cell rather than a flicker, fast enough to look impatient.
    private const double OrbitStepMs = 90;

    // A travelling gradient's cycle length: the palette walked out and back again, so the
    // brightest and darkest ends each come round once per cycle with no seam between them.
    private static readonly int WavePeriod = (Palette.Length - 1) * 2;

    // Just under the pulse throttle: consecutive steps still overlap enough for the band to
    // slide rather than step, without a step being half-finished when the next one lands.
    private const double WaveStepMs = 75;

    // What a tile costs to settle when nothing is driving it - coming back to rest, or
    // getting the abort red off the grid.
    private const double SettleMs = 190;

    private const double RestBloomMin = 0.45;
    private const double RestBloomMax = 0.85;

    private readonly Grid _tileGrid;
    private readonly Image? _bloom;

    private readonly SolidColorBrush[,] _brushes = new SolidColorBrush[GridSize, GridSize];

    // One storyboard per tile, built once and reused forever, and this matters far more
    // than it looks. The obvious version news up a Storyboard per colour change and
    // Begin()s it, and nothing ever stops the one before: Pulse repaints nine tiles up to
    // fourteen times a second for the length of a run, the press-hold, orbit and abort
    // timers add their own, and the one-shot flourishes fire several per tile at once. Each
    // of those is a dependent animation ticking on the UI thread, and they accumulated on
    // every brush - which is what made the button lock the window up under a fast pack or
    // an impatient click. Nine live storyboards is now the hard ceiling, whatever the
    // pipeline does.
    private readonly Storyboard?[,] _tileStoryboards = new Storyboard?[GridSize, GridSize];

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

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;

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
        ReleaseGrid();
        _isGenerating = false;

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, RestLayout[row, col], SettleMs);

        SetBloom(RestBloomMin + _random.NextDouble() * (RestBloomMax - RestBloomMin), SettleMs);
    }

    /// <summary>Pointer down on the reactor: tiles start firing erratically and the bloom
    /// collapses, as though the charge is being drawn out of it.</summary>
    public void BeginPressHold()
    {
        if (!_isInitialized) return;

        ReleaseGrid();
        SetBloom(_random.NextDouble() * 0.10, 110);

        // One erratic burst either way, so a quick click still registers visually with
        // animations suspended or a timer that never gets to tick.
        FlickerRandomTiles(2, 55);

        if (AnimationsSuspended) return;

        StopPressHold();
        _pressHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _pressHoldTimer.Tick += (s, e) => FlickerRandomTiles(_random.Next(1, 4), 55);
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
        _orbitPhase = null; // the wait stance takes the orbit over from any phase using it
        StopPressHold();
        ReleaseGrid();

        _orbitHead = 0;
        PaintOrbit(100);

        // Outside a run there's no loop yet; inside one it's already going and this is a
        // no-op that just re-arms it after a suspended-animations toggle.
        StartBloomLoop();

        if (AnimationsSuspended) return;

        StopOrbitTimer();
        _orbitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OrbitStepMs) };
        _orbitTimer.Tick += (s, e) =>
        {
            _orbitHead = (_orbitHead + 1) % OrbitRing.Length;
            PaintOrbit(OrbitStepMs * 1.25);
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
                    AnimateTile(row, col, RestLayout[row, col], 110);

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
        _orbitPhase = null;
    }

    // ── The orbit as a phase's own behaviour ─────────────────────────────────

    /// <summary>
    /// Which phase a phase-driven orbit belongs to, if any. The external wait stance and a
    /// long phase share one mechanism but not one meaning: a wait owns the whole grid and
    /// swallows pulses, while this is simply what one phase looks like and has to step
    /// aside the moment the next phase reports.
    /// </summary>
    private Core.AlchitexPhase? _orbitPhase;

    /// <summary>
    /// Runs the orbit for a phase that reports once and then works for a long time without
    /// saying anything else.
    ///
    /// Staging is the case that needs it, and it is worth being explicit about why, because
    /// it is the mirror image of the ripple problem (§ the class comment on flourishes):
    /// copying a heavy pack is thousands of files between two progress reports, so there is
    /// exactly one pulse for the whole of it. A per-pulse behaviour has nothing to work
    /// with there - the reactor sat on one twitched tile and looked switched off. A phase
    /// that reports continuously wants a pulse behaviour; a phase that reports once and
    /// then disappears wants a timer.
    ///
    /// Unlike BeginWaiting this does NOT claim the grid and does not block pulses.
    /// </summary>
    private void BeginPhaseOrbit(Core.AlchitexPhase phase)
    {
        if (!_isInitialized || _isWaiting) return;  // the real wait stance outranks this
        if (_orbitPhase == phase) return;           // already running for this phase

        StopOrbitTimer();
        _orbitPhase = phase;
        _orbitHead = 0;

        if (AnimationsSuspended) return;

        _orbitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OrbitStepMs) };
        _orbitTimer.Tick += (s, e) =>
        {
            // Defer to a flourish that is still playing rather than cutting it short - the
            // intake wash lands right about here, since a pack's run starts the moment its
            // tile has finished flying in.
            if (IsGridClaimed) return;

            _orbitHead = (_orbitHead + 1) % OrbitRing.Length;
            PaintOrbit(OrbitStepMs * 1.25);
        };
        _orbitTimer.Start();
    }

    private void StopPhaseOrbit()
    {
        if (_orbitPhase == null) return;

        _orbitPhase = null;
        StopOrbitTimer();
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
        ReleaseGrid();

        foreach (var (row, col) in AbortCross)
            SetTileColor(row, col, AbortReds[0], 55);

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                if (!IsOnCross(row, col))
                    SetTileColor(row, col, AbortBackdrop, 85);

        if (AnimationsSuspended) return;

        // Each cross tile drifts between the three reds on its own schedule - alive and
        // agitated, but never leaving red, so the X never stops being an X.
        StopAbortHintTimer();
        _abortHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _abortHintTimer.Tick += (s, e) =>
        {
            foreach (var (row, col) in AbortCross)
            {
                if (_random.NextDouble() < 0.5) continue; // stagger, so they don't blink in unison

                // Weighted toward the hot end: mostly bright, occasionally banked down.
                var roll = _random.NextDouble();
                var color = roll < 0.5 ? AbortReds[0] : roll < 0.85 ? AbortReds[1] : AbortReds[2];

                SetTileColor(row, col, color, _random.Next(55, 100));
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
                    AnimateTile(row, col, RestLayout[row, col], 110);
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

        // A phase orbit belongs to exactly one phase, so anything else reporting ends it.
        // Ahead of every gate below deliberately: a pulse that gets swallowed still means
        // that phase is over, and leaving the orbit spinning under the next one would
        // outlast the gate that swallowed it. Safe above the wait check because the wait
        // stance takes _orbitPhase to null when it claims the orbit, so this can never stop
        // a wait's own timer.
        if (_orbitPhase.HasValue && _orbitPhase.Value != phase) StopPhaseOrbit();

        // The abort stance and the waiting orbit own the whole grid while they're up - see
        // BeginAbortHint / BeginWaiting. Neither is a flourish; both mean the user is being
        // told something more important than progress.
        if (_isAbortHintActive || _isWaiting) return;

        // A pack finishing is the one thing the reactor must never fail to say, so it is
        // exempt from both gates below. It arrives once per pack, hard on the heels of
        // Finalizing - which is itself a flourish holding the grid - and it would otherwise
        // be swallowed by that hold on any machine quick enough to close the gap.
        if (phase != Core.AlchitexPhase.Done)
        {
            // Any one-shot flourish owns the grid for its duration, or the next pulse
            // repaints all nine tiles out from under it - see ClaimGrid.
            if (IsGridClaimed) return;

            // Throttle everything else. GeneratingTextures alone can arrive thousands of
            // times a second; the rest are rare but there's no reason to special-case them.
            var now = DateTime.UtcNow;
            if ((now - _lastPulseUtc).TotalMilliseconds < MinPulseIntervalMs) return;
            _lastPulseUtc = now;
        }

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

            // Copying the pack. Reports once and then goes quiet for as long as the copy
            // takes, so this is a timer rather than a per-pulse behaviour - see
            // BeginPhaseOrbit. The orbit is the shape that means "still going, nothing to
            // show yet", which is exactly what staging is.
            case Core.AlchitexPhase.Staging:
                BeginPhaseOrbit(phase);
                break;

            // The orchestrator taking the measure of the whole pack in one pass: a single
            // drop out from the centre. Also reports once, but unlike staging it is followed
            // immediately by texture work, so a half-second flourish finishes on screen.
            case Core.AlchitexPhase.ScanningTextures:
                PlayRipple(brighten: true);
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
            //
            // The ripple was tried here and does not belong: post-processing is over in a
            // blink against a texture pass measured in seconds, so a half-second flourish
            // hung on it was never actually seen. It lives in StepBusyWork instead, which
            // is the only phase that runs long enough to show anything off. Anything else
            // wanting a flourish should ask the same question first - is this phase on
            // screen long enough to finish one?
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

    // ── One-shot flourishes ──────────────────────────────────────────────────
    //
    // These are the only behaviors that aren't driven by a pulse or a timer: something
    // happened, and the reactor reacts once. They all span several hundred milliseconds
    // across staggered tiles, which means they need the grid to themselves - a pulse
    // landing mid-flourish repaints all nine tiles and it never finishes. Hence the claim.

    /// <summary>
    /// How long a one-shot owns the grid. A timestamp rather than a flag, deliberately: a
    /// flag that failed to clear (an exception, a run ending mid-flourish, a window closing)
    /// would wedge the reactor into never repainting again, and this is cosmetic code
    /// sitting on top of a pipeline. The worst a stale timestamp can do is expire.
    /// </summary>
    private DateTime _gridClaimedUntilUtc = DateTime.MinValue;

    private bool IsGridClaimed => DateTime.UtcNow < _gridClaimedUntilUtc;

    private void ClaimGrid(double durationMs)
        => _gridClaimedUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);

    private void ReleaseGrid() => _gridClaimedUntilUtc = DateTime.MinValue;

    // A wash's leading edge crossing one column, and how long a column stays lit behind it.
    private const double WashColumnDelayMs = 80;
    private const double WashRiseMs = 100;
    private const double WashFallMs = 220;

    /// <summary>
    /// A pack being taken in or handed back: a bright band washing across the grid, left to
    /// right on the way in and right to left on the way out - the same direction the pack's
    /// own tile travels, so the two read as one event rather than two things happening at
    /// once.
    ///
    /// Three columns is not much to say "something landed in water" with, so the band is
    /// built from the palette rather than a single bright frame: each column rises to the
    /// brightest blue, falls back through the middle of the ramp, and settles at its
    /// resting value, one column-delay behind the column before it. What sells it is that
    /// the trailing columns are still falling while the leading one has already settled.
    /// </summary>
    public void PlayQueueWash(bool leftToRight)
    {
        if (!_isInitialized || _isAbortHintActive) return;

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var lead = (leftToRight ? col : GridSize - 1 - col) * WashColumnDelayMs;
                var rest = Palette[RestLayout[row, col]];

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (Palette[0], lead + WashRiseMs),
                    (Palette[2], lead + WashRiseMs + WashFallMs * 0.45),
                    (rest, lead + WashRiseMs + WashFallMs));
            }
        }

        ClaimGrid((GridSize - 1) * WashColumnDelayMs + WashRiseMs + WashFallMs);
    }

    private const double RippleRingDelayMs = 75;
    private const double RippleRiseMs = 110;
    private const double RippleFallMs = 230;

    /// <summary>
    /// A drop landing in the middle: the centre moves first, then the four edge tiles, then
    /// the corners, each ring one delay behind the last.
    ///
    /// Rings are Manhattan distance from the centre, not Chebyshev, and that is the whole
    /// reason this reads as a ripple - Chebyshev gives a 3x3 grid only two rings, which is
    /// a blink. Manhattan gives three, which is a wave. (PlayImplosion uses Chebyshev on
    /// purpose, because a collapse inward wants to arrive all at once.)
    ///
    /// Goes up toward the brightest blue or down toward the darkest; both are the same
    /// motion, and having both means the reactor can answer "something arrived" and
    /// "something was consumed" with the same gesture.
    /// </summary>
    public void PlayRipple(bool brighten)
    {
        if (!_isInitialized || _isAbortHintActive) return;

        var peak = brighten ? Palette[0] : Palette[Palette.Length - 1];

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var ring = Math.Abs(row - 1) + Math.Abs(col - 1); // 0, 1 or 2
                var lead = ring * RippleRingDelayMs;

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (peak, lead + RippleRiseMs),
                    (Palette[RestLayout[row, col]], lead + RippleRiseMs + RippleFallMs));
            }
        }

        ClaimGrid((GridSize - 1) * RippleRingDelayMs + RippleRiseMs + RippleFallMs);
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

    // Rarer than a sweep, but not by much - this is the reactor's only home for the ripple
    // now, and at a pulse every 70ms a chance this size works out to roughly one every few
    // seconds of texture work. Seldom enough to still read as an event, often enough that a
    // run actually shows it off.
    private const double RippleChance = 0.02;

    /// <summary>
    /// Textures being written: mostly every tile firing at once, with a sweep cutting
    /// through every couple of seconds and, now and then, a drop rippling out of the centre.
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
                AnimateTile(row, col, _random.Next(Palette.Length), 55);

        // Checked before the sweep so the two can't fire on the same pulse - the ripple
        // claims the grid and the sweep's first step would be thrown away.
        if (_random.NextDouble() < RippleChance)
        {
            PlayRipple(brighten: _random.NextDouble() < 0.5);
            return;
        }

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

        AnimateTile(behind1 / GridSize, behind1 % GridSize, Palette.Length - 2, 130);
        AnimateTile(behind2 / GridSize, behind2 % GridSize, RestLayout[behind2 / GridSize, behind2 % GridSize], 250);
    }

    /// <summary>
    /// Uninstalling the original pack: everything flashes bright for an instant, then
    /// collapses to black from the outside in - the corners first, the middle last, each
    /// ring delayed behind the one outside it. An implosion, because that's what deleting
    /// the thing you started from feels like it should look like.
    /// </summary>
    private void PlayImplosion()
    {
        const double flashMs = 80;
        const double holdMs = 100;
        const double ringDelayMs = 180;
        const double collapseMs = 220;

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                // Chebyshev distance from the centre: 1 for the ring, 0 for the middle.
                // Chebyshev rather than the ripple's Manhattan on purpose - a collapse
                // inward wants the whole outer ring to go together, not corner by corner.
                var ring = Math.Max(Math.Abs(row - 1), Math.Abs(col - 1));
                var collapseAt = flashMs + holdMs + (1 - ring) * ringDelayMs;

                // Flash, hold, then collapse - outer ring first, centre last. One sequence
                // rather than two overlapping animations: the second would now cancel the
                // first, since a tile only ever runs one storyboard (see PlayTileSequence).
                PlayTileSequence(row, col,
                    (Palette[0], flashMs),
                    (Palette[0], collapseAt),
                    (Palette[Palette.Length - 1], collapseAt + collapseMs));
            }
        }

        ClaimGrid(flashMs + holdMs + ringDelayMs + collapseMs);
    }

    /// <summary>
    /// A pack finished: one bright wash sweeping corner to corner, each tile lit on a
    /// delay proportional to how far along the diagonal it sits, then released back to
    /// rest behind the wash.
    /// </summary>
    private void PlayCompletionWash()
    {
        const double diagonalDelayMs = 55;
        const double riseMs = 100;
        const double fallMs = 260;

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var lead = (row + col) * diagonalDelayMs; // 0..4 steps, corner to corner

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (Palette[0], lead + riseMs),
                    (Palette[RestLayout[row, col]], lead + riseMs + fallMs));
            }
        }

        ClaimGrid((GridSize - 1) * 2 * diagonalDelayMs + riseMs + fallMs);
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
            AnimateTile(_trailRow, _trailCol, RestLayout[_trailRow, _trailCol], 160);

            // A step in one axis, staying on the grid.
            if (_random.NextDouble() < 0.5)
                _trailRow = Math.Clamp(_trailRow + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
            else
                _trailCol = Math.Clamp(_trailCol + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
        }

        AnimateTile(_trailRow, _trailCol, 0, 100);
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    private void AnimateTile(int row, int col, int paletteIndex, double durationMs)
        => SetTileColor(row, col, Palette[Math.Clamp(paletteIndex, 0, Palette.Length - 1)], durationMs);

    /// <summary>
    /// The one place a tile's color ever changes. Takes a Color rather than a palette index
    /// so the abort stance can paint its reds through the same path as everything else.
    /// </summary>
    private void SetTileColor(int row, int col, Color target, double durationMs)
    {
        if (AnimationsSuspended || durationMs <= 0)
        {
            StopTile(row, col);
            _brushes[row, col].Color = target;
            return;
        }

        PlayTileSequence(row, col, (target, durationMs));
    }

    // Staggering is expressed as key frames on one sequence rather than as a delay on a
    // transition - a tile runs exactly one storyboard, so two overlapping calls would cancel
    // rather than compose. PlayImplosion is the worked example.

    /// <summary>
    /// Runs one tile through a series of colours at absolute times, on that tile's own
    /// reused storyboard. Every colour change in the class ends up here, including the
    /// single-step case - so a flourish that needs three stages costs exactly what a plain
    /// transition costs, and a later call always replaces an earlier one rather than
    /// stacking on top of it.
    ///
    /// A brush's Color is a dependent animation, so this runs on the UI thread rather than
    /// the compositor. Fine at this scale (nine brushes, throttled); the honest alternative
    /// of five stacked opacity layers per tile buys nothing here.
    /// </summary>
    private void PlayTileSequence(int row, int col, params (Color Color, double AtMs)[] frames)
    {
        if (frames.Length == 0) return;

        var brush = _brushes[row, col];

        if (AnimationsSuspended)
        {
            StopTile(row, col);
            brush.Color = frames[^1].Color;
            return;
        }

        // Retire the tile's previous storyboard before starting another. This is the whole
        // fix: a fresh Storyboard per call is fine and always was - what wasn't fine is
        // never stopping the last one, so every brush accumulated live animations for the
        // length of a run. Building a new one rather than re-arming a kept one is
        // deliberate: re-arming means mutating a Storyboard's key frames after it has been
        // begun, which is not something WinUI promises to allow, and this is cosmetic code
        // that must never be the reason a run falls over.
        StopTile(row, col);

        var animation = new ColorAnimationUsingKeyFrames { EnableDependentAnimation = true };

        foreach (var (color, atMs) in frames)
        {
            animation.KeyFrames.Add(new EasingColorKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(Math.Max(atMs, 1)),
                Value = color,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        _tileStoryboards[row, col] = storyboard;
        storyboard.Begin();
    }

    /// <summary>Takes a tile off its storyboard without letting go of the colour it is
    /// currently showing - see PlayTileSequence for why that isn't automatic.</summary>
    private void StopTile(int row, int col)
    {
        var storyboard = _tileStoryboards[row, col];
        if (storyboard == null) return;

        // Stop() reverts the brush to the value it held before that storyboard started, so
        // whatever it is showing right now has to be written back as its base first -
        // otherwise every restart snaps to where the previous sequence began, which on a
        // grid repainted fourteen times a second is a permanent flicker.
        var current = _brushes[row, col].Color;
        storyboard.Stop();
        _brushes[row, col].Color = current;

        _tileStoryboards[row, col] = null;
    }

    // The bloom's one-shot storyboard, reused for the same reason the tiles' are - press
    // and release the button a few times and the naive version leaves one running per
    // press. Separate from _bloomLoop, which is the long-lived breathing.
    private Storyboard? _bloomShot;

    private void SetBloom(double opacity, double durationMs)
    {
        if (_bloom == null) return;

        if (AnimationsSuspended || durationMs <= 0)
        {
            _bloomShot?.Stop();
            _bloom.Opacity = opacity;
            return;
        }

        if (_bloomShot != null)
        {
            var current = _bloom.Opacity;
            _bloomShot.Stop();
            _bloom.Opacity = current;
        }

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        Storyboard.SetTarget(animation, _bloom);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _bloomShot = new Storyboard();
        _bloomShot.Children.Add(animation);
        _bloomShot.Begin();
    }

    // The breath, as (millisecond, opacity) pairs. Uneven on purpose: two of the three
    // peaks go all the way to 1.0 and the troughs never fully collapse, so the reactor reads
    // as something with a lot of light in it being modulated - not something being switched
    // on and off. An even sine would loop invisibly; this one you notice.
    private static readonly (double AtMs, double Opacity)[] BloomBreath =
    {
        (0, 0.35),
        (520, 1.00),
        (1040, 0.50),
        (1520, 0.88),
        (2000, 0.40),
        (2420, 1.00),
        (2900, 0.35),
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

        _bloomShot?.Stop();

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                _tileStoryboards[row, col]?.Stop();
    }
}
