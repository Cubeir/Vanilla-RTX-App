using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Vanilla_RTX_App.Modules.Alchitex;

/// <summary>
/// Draws the window's background field of blue tiles, in code, instead of stretching a
/// 10000x1000 PNG behind everything.
///
/// The art it replaces was measured rather than eyeballed, and the numbers below come
/// straight out of it: a 40px grid, six blues (five of which are exactly ReactorAnimator's
/// palette - the background and the mark were always made of the same colours), red always
/// zero, and neighbouring tiles matching about half as often as chance, i.e. the original
/// generator avoided repeats without forbidding them. The one thing deliberately NOT
/// reproduced is its vertical alpha ramp: instead of the field fading out toward the top, it
/// disperses - tiles simply stop being there, thinning to nothing. Same silhouette, but it
/// reads as something coming apart rather than something being faded down.
///
/// Why composition visuals and not XAML elements: a full window is on the order of a
/// thousand cells. A thousand Rectangles would be a thousand UIElements with layout,
/// hit-testing and UI-thread animations - the exact cost model that makes ReactorAnimator's
/// NINE tiles a deliberate decision. SpriteVisuals have no layout pass, and everything
/// animated here runs on the compositor thread, so a busy generation run never competes with
/// the background for the UI thread.
///
/// Three things keep it cheap beyond that: the six palette colours are shared brush objects
/// (only a tile that animates its colour gets one of its own), both shadow gradients are a
/// single shared brush each, and only a bounded fraction of tiles animate at all.
///
/// Everything is derived from a hash of (seed, column, row) rather than sequential random
/// draws, which is what makes a resize cheap to reason about: the field is anchored to the
/// bottom-left, so growing the window reveals more of the same field instead of reshuffling
/// the part that was already on screen.
///
/// With TunerVariables.Persistent.SuspendUIAnimations on, the field is built exactly as it
/// would be otherwise and nothing moves - dispersing tiles resolve to present-or-absent on
/// their own roll. If any of this throws, the host is simply left empty (the window still
/// has its own acrylic backdrop underneath); a background is never worth taking the window
/// down for.
/// </summary>
internal sealed class ReactorBackdrop
{
    // -- Measured off huge_background.png -------------------------------------

    // Its grid pitch, at 1:1. Everything else is expressed in these units.
    private const float TileSize = 40f;

    // The six blues it was built from, darkest first. Five are ReactorAnimator's palette
    // exactly; #00305B is the extra step between its two darkest.
    private static readonly Color[] Palette =
    {
        ColorHelper.FromArgb(255, 0x00, 0x22, 0x42),
        ColorHelper.FromArgb(255, 0x00, 0x29, 0x4E),
        ColorHelper.FromArgb(255, 0x00, 0x30, 0x5B),
        ColorHelper.FromArgb(255, 0x00, 0x35, 0x66),
        ColorHelper.FromArgb(255, 0x00, 0x3B, 0x72),
        ColorHelper.FromArgb(255, 0x00, 0x48, 0x8A),
    };

    // The field spans the window's own height, clamped: dispersion has to *finish* on screen
    // or it isn't dispersion, it's just a field that got cut off - which is what the bitmap
    // did, since it faded over a fixed 1000px that most windows are shorter than. Clamped at
    // both ends so a very short window still gets a recognisable field and a very tall one
    // doesn't stretch the effect into a smear.
    private const float MinFieldHeight = 460f;
    private const float MaxFieldHeight = 1200f;

    // How much of the field is packed solid before any of it starts going missing.
    private const float SolidFraction = 0.22f;

    // Dispersion falls off convexly, so the field stays rich well past halfway and then
    // thins out quickly - a straight line spends too long at "half the tiles are missing",
    // which reads as damage rather than as dispersion.
    private const double DispersionCurve = 1.6;

    // How far either side of the present/absent line a tile counts as marginal, and so
    // fades in and out instead of just being there or not. This is what stops the top of
    // the field being a static scatter of survivors.
    private const double ShimmerReach = 0.18;

    // -- Look -----------------------------------------------------------------

    // Depth of the cast shadow, and how dark it gets at its darkest.
    //
    // Light comes from the upper right - which is where the mark's own brightest tile sits,
    // and, measured, where the original art's light came from too. So a tile darker than the
    // one above it or the one to its right is being shadowed by it, and the shadow's
    // strength is the brightness difference between them.
    private const float ShadowDepth = 7f;
    private const float MaxShadowOpacity = 0.5f;

    // -- Motion ---------------------------------------------------------------

    // Fraction of solid tiles that slowly drift one step along the palette and back.
    private const double ColorDriftShare = 0.16;

    private const double ColorDriftMinSeconds = 9;
    private const double ColorDriftMaxSeconds = 22;

    private const double ShimmerMinSeconds = 6;
    private const double ShimmerMaxSeconds = 16;

    // A ceiling on the whole thing. A maximised window on a large display works out around
    // 1500 cells; this only bites on something far larger, and losing the top of the field -
    // the sparsest part - is the least visible way to stop.
    private const int MaxTiles = 3000;

    // -- State ----------------------------------------------------------------

    private readonly Border _host;
    private readonly int _seed = Environment.TickCount;

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CompositionColorBrush[]? _sharedBrushes;
    private CompositionLinearGradientBrush? _topShadowBrush;
    private CompositionLinearGradientBrush? _rightShadowBrush;
    private CompositionEasingFunction? _ease;

    private DispatcherTimer? _resizeTimer;
    private double _builtWidth = -1;
    private double _builtHeight = -1;

    // The current field's vertical extent, and the solid band at the bottom of it. Both are
    // derived from the host's height in Rebuild, so Density can stay a pure function of row.
    private float _fieldHeight = MinFieldHeight;
    private float _solidHeight = MinFieldHeight * SolidFraction;
    private bool _isShutDown;

    private static bool AnimationsSuspended => TunerVariables.Persistent.SuspendUIAnimations;

    public ReactorBackdrop(Border host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>Attaches to the host and builds the field. Safe to call once; the host's own
    /// SizeChanged keeps it in step from then on.</summary>
    public void Start()
    {
        try
        {
            _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            _root = _compositor.CreateContainerVisual();

            // The field is sized to the host, but a rebuild that lands a frame after a
            // shrink would otherwise paint outside it for that frame.
            _root.Clip = _compositor.CreateInsetClip();

            ElementCompositionPreview.SetElementChildVisual(_host, _root);

            _sharedBrushes = new CompositionColorBrush[Palette.Length];
            for (var i = 0; i < Palette.Length; i++)
                _sharedBrushes[i] = _compositor.CreateColorBrush(Palette[i]);

            _topShadowBrush = CreateShadowBrush(_compositor, vertical: true);
            _rightShadowBrush = CreateShadowBrush(_compositor, vertical: false);
            _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));

            _host.SizeChanged += Host_SizeChanged;

            Rebuild();
        }
        catch (Exception ex)
        {
            // The window keeps its acrylic backdrop; it just doesn't get a tile field.
            Trace.WriteLine($"[ALCHITEX] Backdrop unavailable, carrying on without it: {ex.Message}");
            Shutdown();
        }
    }

    /// <summary>Drops the field and every animation in it. Call on window close so nothing
    /// the compositor is still running outlives the window.</summary>
    public void Shutdown()
    {
        if (_isShutDown) return;
        _isShutDown = true;

        try
        {
            _host.SizeChanged -= Host_SizeChanged;

            StopResizeTimer();

            _root?.Children.RemoveAll();
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _root = null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Backdrop shutdown: {ex.Message}");
        }
    }

    // -- Resize ---------------------------------------------------------------

    /// <summary>
    /// Rebuilds on a size change, debounced - dragging a window edge raises this every frame,
    /// and rebuilding a thousand visuals per frame is the one way a background this cheap
    /// could become expensive.
    /// </summary>
    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isShutDown || _root == null) return;

        // Too small a change to be worth a rebuild. Width has to move by a whole column;
        // height gets a tighter test because it also stretches the dispersion curve, which is
        // visible well before it costs a row.
        var widthMoved = Math.Abs(e.NewSize.Width - _builtWidth) >= TileSize;
        var heightMoved = Math.Abs(e.NewSize.Height - _builtHeight) >= TileSize / 4;

        if (!widthMoved && !heightMoved) return;

        StopResizeTimer();

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeTimer.Tick += (s, args) =>
        {
            StopResizeTimer();
            Rebuild();
        };
        _resizeTimer.Start();
    }

    private void StopResizeTimer()
    {
        if (_resizeTimer == null) return;

        _resizeTimer.Stop();
        _resizeTimer = null;
    }

    // -- Building the field ---------------------------------------------------

    private void Rebuild()
    {
        if (_isShutDown || _root == null || _compositor == null) return;

        var width = _host.ActualWidth;
        var height = _host.ActualHeight;

        if (width < 1 || height < 1) return;

        _builtWidth = width;
        _builtHeight = height;

        _root.Size = new Vector2((float)width, (float)height);
        _root.Children.RemoveAll();

        _fieldHeight = Math.Clamp((float)height, MinFieldHeight, MaxFieldHeight);
        _solidHeight = _fieldHeight * SolidFraction;

        var columns = (int)Math.Ceiling(width / TileSize);
        var rows = (int)Math.Ceiling(Math.Min(height, _fieldHeight) / TileSize);

        if (columns <= 0 || rows <= 0) return;

        // Presence and colour for every cell first, because a tile's shadows are decided by
        // its neighbours and a cell can't answer for itself.
        var present = new bool[columns, rows];
        var shimmering = new bool[columns, rows];
        var shade = new int[columns, rows];

        for (var row = 0; row < rows; row++)
        {
            var density = Density(row);

            for (var col = 0; col < columns; col++)
            {
                var roll = Roll(col, row, 0);

                // Three bands: solidly there, marginal (fades in and out), or simply absent.
                if (roll < density - ShimmerReach)
                {
                    present[col, row] = true;
                }
                else if (roll < density + ShimmerReach && density > 0)
                {
                    present[col, row] = true;
                    shimmering[col, row] = true;
                }
                else
                {
                    continue;
                }

                shade[col, row] = PickShade(col, row);
            }
        }

        var placed = 0;

        for (var row = 0; row < rows && placed < MaxTiles; row++)
        {
            for (var col = 0; col < columns && placed < MaxTiles; col++)
            {
                if (!present[col, row]) continue;

                AddTile(col, row, rows, columns, height, present, shimmering, shade);
                placed++;
            }
        }
    }

    private void AddTile(
        int col, int row, int rows, int columns, double hostHeight,
        bool[,] present, bool[,] shimmering, int[,] shade)
    {
        var index = shade[col, row];
        var isShimmering = shimmering[col, row];

        // Only a tile that animates its colour needs a brush of its own; everything else
        // shares one of six.
        var drifts = !isShimmering && !AnimationsSuspended && Roll(col, row, 3) < ColorDriftShare;

        var tile = _compositor!.CreateSpriteVisual();
        tile.Size = new Vector2(TileSize, TileSize);

        // Row 0 is the bottom row: the field is anchored to the bottom edge, the way the art
        // it replaces was.
        tile.Offset = new Vector3(col * TileSize, (float)hostHeight - (row + 1) * TileSize, 0);
        tile.Brush = drifts ? _compositor.CreateColorBrush(Palette[index]) : _sharedBrushes![index];

        // A neighbour brighter than this tile is standing above it and casting onto it. The
        // shadows are children of the tile, so a dispersing tile takes them with it.
        var above = row + 1 < rows && present[col, row + 1] ? shade[col, row + 1] : index;
        var right = col + 1 < columns && present[col + 1, row] ? shade[col + 1, row] : index;

        AddShadow(tile, above - index, vertical: true);
        AddShadow(tile, right - index, vertical: false);

        _root!.Children.InsertAtTop(tile);

        if (AnimationsSuspended)
        {
            // A marginal tile has to settle somewhere; its own roll decides, so the static
            // field is the animated one caught at a plausible moment rather than a denser
            // version of it.
            if (isShimmering && Roll(col, row, 0) >= Density(row)) tile.Opacity = 0f;

            return;
        }

        if (isShimmering) StartShimmer(tile, col, row);
        else if (drifts) StartColorDrift((CompositionColorBrush)tile.Brush, col, row, index);
    }

    private void AddShadow(SpriteVisual tile, int shadeDelta, bool vertical)
    {
        if (shadeDelta <= 0) return;

        var shadow = _compositor!.CreateSpriteVisual();

        shadow.Size = vertical
            ? new Vector2(TileSize, ShadowDepth)
            : new Vector2(ShadowDepth, TileSize);

        shadow.Offset = vertical
            ? Vector3.Zero
            : new Vector3(TileSize - ShadowDepth, 0, 0);

        shadow.Brush = vertical ? _topShadowBrush : _rightShadowBrush;

        // One palette step is a shallow lip, five is the full drop.
        shadow.Opacity = MaxShadowOpacity * shadeDelta / (Palette.Length - 1);

        tile.Children.InsertAtTop(shadow);
    }

    // -- Motion ---------------------------------------------------------------

    /// <summary>A marginal tile fading out and back, on its own long cycle - the dispersion
    /// edge breathing instead of sitting still.</summary>
    private void StartShimmer(SpriteVisual tile, int col, int row)
    {
        var animation = _compositor!.CreateScalarKeyFrameAnimation();
        var period = Lerp(ShimmerMinSeconds, ShimmerMaxSeconds, Roll(col, row, 4));

        animation.InsertKeyFrame(0f, 1f, _ease);
        animation.InsertKeyFrame(0.5f, 0f, _ease);
        animation.InsertKeyFrame(1f, 1f, _ease);
        animation.Duration = TimeSpan.FromSeconds(period);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;

        // Phase, not a pause: without it every marginal tile would vanish in unison.
        animation.DelayTime = TimeSpan.FromSeconds(Roll(col, row, 5) * period);
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        tile.StartAnimation("Opacity", animation);
    }

    /// <summary>A solid tile drifting one palette step and back. One step, never more: the
    /// field should look like it is alive, not like it is cycling.</summary>
    private void StartColorDrift(CompositionColorBrush brush, int col, int row, int index)
    {
        // Toward the middle of the palette from either end, so the drift can't run out of it.
        var toward = index == 0 ? 1
            : index == Palette.Length - 1 ? Palette.Length - 2
            : Roll(col, row, 6) < 0.5 ? index - 1 : index + 1;

        var animation = _compositor!.CreateColorKeyFrameAnimation();
        var period = Lerp(ColorDriftMinSeconds, ColorDriftMaxSeconds, Roll(col, row, 7));

        animation.InsertKeyFrame(0f, Palette[index], _ease);
        animation.InsertKeyFrame(0.5f, Palette[toward], _ease);
        animation.InsertKeyFrame(1f, Palette[index], _ease);
        animation.Duration = TimeSpan.FromSeconds(period);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.DelayTime = TimeSpan.FromSeconds(Roll(col, row, 8) * period);
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        brush.StartAnimation("Color", animation);
    }

    // -- The field's own arithmetic -------------------------------------------

    /// <summary>How much of a row survives: all of it inside the solid band at the bottom,
    /// none of it at the top of the field, convex in between.</summary>
    private double Density(int row)
    {
        var y = row * TileSize;

        if (y <= _solidHeight) return 1.0;
        if (y >= _fieldHeight) return 0.0;

        return 1.0 - Math.Pow((y - _solidHeight) / (_fieldHeight - _solidHeight), DispersionCurve);
    }

    /// <summary>
    /// A tile's colour, re-rolled once if it lands on the same shade as the neighbour to its
    /// left or below. Measured on the original art, adjacent tiles match about half as often
    /// as six colours picked freely would - so its generator avoided repeats without
    /// forbidding them, and one re-roll is exactly that.
    /// </summary>
    private int PickShade(int col, int row)
    {
        var index = ShadeRoll(col, row, 1);

        var left = col > 0 ? ShadeRoll(col - 1, row, 1) : -1;
        var below = row > 0 ? ShadeRoll(col, row - 1, 1) : -1;

        if (index != left && index != below) return index;

        return ShadeRoll(col, row, 2);
    }

    private int ShadeRoll(int col, int row, int salt)
        => Math.Min((int)(Roll(col, row, salt) * Palette.Length), Palette.Length - 1);

    // Deterministic per cell rather than a sequential random draw, so the field is a
    // function of position: a resize re-derives the same tiles it already had.
    private double Roll(int col, int row, int salt) => Hash(col, row, salt) / (double)uint.MaxValue;

    private uint Hash(int col, int row, int salt)
    {
        unchecked
        {
            var h = (uint)_seed;

            h ^= (uint)col * 0x9E3779B1u;
            h = (h ^ (h >> 15)) * 0x85EBCA6Bu;
            h ^= (uint)row * 0xC2B2AE35u;
            h = (h ^ (h >> 13)) * 0x27D4EB2Fu;
            h ^= (uint)salt * 0x165667B1u;

            return h ^ (h >> 16);
        }
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static CompositionLinearGradientBrush CreateShadowBrush(Compositor compositor, bool vertical)
    {
        var brush = compositor.CreateLinearGradientBrush();

        // Relative, so one brush fits every tile regardless of the strip's pixel size.
        brush.MappingMode = CompositionMappingMode.Relative;

        // Dark at the edge the shadow is cast from, gone by the far side of the strip.
        brush.StartPoint = vertical ? new Vector2(0, 0) : new Vector2(1, 0);
        brush.EndPoint = vertical ? new Vector2(0, 1) : new Vector2(0, 0);

        brush.ColorStops.Insert(0, compositor.CreateColorGradientStop(0f, ColorHelper.FromArgb(255, 0, 0, 0)));
        brush.ColorStops.Insert(1, compositor.CreateColorGradientStop(1f, ColorHelper.FromArgb(0, 0, 0, 0)));

        return brush;
    }
}
