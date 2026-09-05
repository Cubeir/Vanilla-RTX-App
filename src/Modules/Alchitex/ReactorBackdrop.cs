using System;
using System.Collections.Generic;
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
/// The window's background field of blue tiles, generated instead of shipped as a
/// 10000x1000 PNG.
///
/// The constants come off the art it replaces: a 40px grid, red always zero, six blues -
/// five of which are exactly ReactorAnimator's palette - and adjacent tiles matching about
/// half as often as chance, so its generator avoided repeats without forbidding them. The
/// one thing not reproduced is its vertical alpha ramp: the field disperses toward the top
/// instead, tiles simply stopping rather than fading out.
///
/// Everything about a cell is a hash of (seed, column, row), so the field is a function of
/// position and a resize re-derives what it already had. Rebuilds are incremental and the
/// field is anchored to the host's bottom edge by the compositor, so no tile ever moves and
/// growing the window only appends.
///
/// Two rules keep it cheap, both learned the hard way:
///
///   - No composition animations. A running animation makes the window recomposite every
///     frame, and this window is acrylic over most of its area, so every frame means
///     re-blurring. That cost is set by whether anything is animating at all, not by how
///     much - cutting the animated tile count sevenfold changed nothing. Motion is a slow
///     timer repainting a few tiles per tick instead, so the window composites about four
///     times a second rather than at refresh rate.
///   - Every tile is one opaque quad filled from one of six shared brushes. Gradient fills
///     and separate shadow strips have both been tried; both cost more than they were worth.
///
/// Motion stops entirely under SuspendUIAnimations, energy saver, or on a machine with few
/// cores. If any of this throws the host is left empty - a background is never worth taking
/// the window down for.
/// </summary>
internal sealed class ReactorBackdrop
{
    // -- The field, measured off huge_background.png ---------------------------

    private const float TileSize = 40f;

    // Darkest first. Five of these are ReactorAnimator's palette exactly; #00305B is the
    // extra step between its two darkest.
    private static readonly Color[] Palette =
    {
        ColorHelper.FromArgb(255, 0, 35, 66),
        ColorHelper.FromArgb(255, 0, 41, 78),
        ColorHelper.FromArgb(255, 0, 48, 91),
        ColorHelper.FromArgb(255, 0, 53, 102),
        ColorHelper.FromArgb(255, 0, 59, 114),
        ColorHelper.FromArgb(255, 0, 72, 138),

        // Duplicate the above to double their chance overall
        ColorHelper.FromArgb(255, 0, 35, 66),
        ColorHelper.FromArgb(255, 0, 41, 78),
        ColorHelper.FromArgb(255, 0, 48, 91),
        ColorHelper.FromArgb(255, 0, 53, 102),
        ColorHelper.FromArgb(255, 0, 59, 114),
        ColorHelper.FromArgb(255, 0, 72, 138),

        // A unique, rarer bright color
        ColorHelper.FromArgb(255, 44, 154, 255),
    };

    // The field spans the window in whole rows - dispersion has to finish on screen, or it is
    // just a field that got cut off, which is what the fixed-height bitmap did.
    private const int MinFieldRows = 10;
    private const int MaxFieldRows = 26;

    // The dispersion span is the only thing that reads off the window's height, so it is the
    // only thing a vertical resize can disturb. Quantised, so most drags disturb nothing.
    private const int FieldRowQuantum = 3;

    private const float SolidFraction = 0.22f;

    // Convex: a linear falloff spends too long at "half the tiles are missing", which reads
    // as damage rather than dispersion.
    private const double DispersionCurve = 1.6;

    // How far either side of the present/absent line a tile counts as dispersing, and so
    // fades in and out rather than simply being there or not.
    private const double DispersionBand = 0.18;

    // Static tiles are close to free, so this is about scene size rather than frame cost.
    // Rows are dropped from the top, the sparsest part of the field.
    private const int MaxTiles = 5000;

    // -- Motion ---------------------------------------------------------------

    // The tick rate is the frame rate. See the class remarks for why it is this low.
    private const double TickMs = 200;

    // A tick costs one composition frame whatever it touches, so this is about how fast the
    // field turns over, not about cost. It scales with the field so a large window doesn't
    // look frozen.
    private const int MinTilesPerTick = 8;
    private const int MaxTilesPerTick = 32;
    private const int TilesPerTickDivisor = 100;

    // Dispersing tiles step through these rather than jumping, so the edge dissolves.
    private static readonly float[] FadeRungs = { 0f, 0.5f, 1f };

    // -- State ----------------------------------------------------------------

    private sealed class Tile
    {
        public required SpriteVisual Visual;
        public required int BaseShade;
        public required bool Dispersing;
        public int Shade;
        public int Rung = FadeRungs.Length - 1;
        public bool Rising;
    }

    private readonly Border _host;
    private readonly int _seed = Environment.TickCount;
    private readonly Random _random = new();

    private readonly Dictionary<int, Tile> _tiles = new();
    private readonly List<Tile> _live = new();
    private readonly HashSet<int> _wanted = new();

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private ContainerVisual? _field;
    private CompositionColorBrush[]? _brushes;

    private DispatcherTimer? _resizeTimer;
    private DispatcherTimer? _tickTimer;

    private int _builtColumns = -1;
    private int _builtRows = -1;
    private int _fieldRows = MinFieldRows;
    private bool _isShutDown;

    /// <summary>
    /// Whether this window gets a moving background at all. Energy saver is an explicit
    /// request not to burn power on decoration, and a low core count stands in for a machine
    /// with better uses for its compositor - the same two checks as MainWindow's UI logger
    /// tick rate. Read per window, so toggling energy saver and reopening changes it.
    /// </summary>
    private readonly bool _motionAffordable = ResolveMotionBudget();

    private bool MotionAllowed => _motionAffordable && !TunerVariables.Persistent.SuspendUIAnimations;

    public ReactorBackdrop(Border host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    private static bool ResolveMotionBudget()
    {
        try
        {
            if (Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On)
                return false;

            return Environment.ProcessorCount >= 4;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Attaches to the host and builds the field. The host's own SizeChanged keeps
    /// it in step from then on.</summary>
    public void Start()
    {
        try
        {
            _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;

            // Sized and clipped to the host by the compositor rather than by hand.
            _root = _compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _root.Clip = _compositor.CreateInsetClip();

            // Origin on the host's bottom edge, tiles hanging above it at negative Y. This is
            // what makes a resize move nothing: offsets are relative to the bottom, and the
            // compositor keeps the bottom where the host's bottom is.
            _field = _compositor.CreateContainerVisual();
            _field.RelativeOffsetAdjustment = new Vector3(0, 1, 0);
            _root.Children.InsertAtTop(_field);

            ElementCompositionPreview.SetElementChildVisual(_host, _root);

            _brushes = new CompositionColorBrush[Palette.Length];
            for (var i = 0; i < Palette.Length; i++)
                _brushes[i] = _compositor.CreateColorBrush(Palette[i]);

            _host.SizeChanged += Host_SizeChanged;

            Rebuild();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Backdrop unavailable, carrying on without it: {ex.Message}");
            Shutdown();
        }
    }

    /// <summary>Drops the field. Call on window close so nothing outlives the window.</summary>
    public void Shutdown()
    {
        if (_isShutDown) return;
        _isShutDown = true;

        try
        {
            _host.SizeChanged -= Host_SizeChanged;

            StopResizeTimer();
            StopTicking();

            // Unparent before closing: a composition object is only safe to dispose once
            // nothing in the tree still references it.
            _field?.Children.RemoveAll();
            _root?.Children.RemoveAll();

            ElementCompositionPreview.SetElementChildVisual(_host, null);

            foreach (var tile in _tiles.Values)
                tile.Visual.Dispose();

            _tiles.Clear();
            _live.Clear();

            if (_brushes != null)
                foreach (var brush in _brushes)
                    brush.Dispose();

            _brushes = null;
            _field = null;
            _root = null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Backdrop shutdown: {ex.Message}");
        }
    }

    // -- Resize ---------------------------------------------------------------

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isShutDown || _field == null) return;

        // Debounced: dragging a window edge raises this every frame. The rebuild itself is a
        // no-op unless the grid actually changed size.
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
        if (_isShutDown || _field == null || _compositor == null) return;

        var width = _host.ActualWidth;
        var height = _host.ActualHeight;

        if (width < 1 || height < 1) return;

        var columns = (int)Math.Ceiling(width / TileSize);
        var visibleRows = (int)Math.Ceiling(height / TileSize);

        var fieldRows = Math.Clamp(
            (int)Math.Ceiling(visibleRows / (double)FieldRowQuantum) * FieldRowQuantum,
            MinFieldRows,
            MaxFieldRows);

        var rows = Math.Min(Math.Min(visibleRows, fieldRows), Math.Max(1, MaxTiles / Math.Max(columns, 1)));

        if (columns <= 0 || rows <= 0) return;
        if (columns == _builtColumns && rows == _builtRows && fieldRows == _fieldRows) return;

        _builtColumns = columns;
        _builtRows = rows;
        _fieldRows = fieldRows;

        ApplyPlan(columns, rows);
    }

    /// <summary>
    /// Works out what the field should be at this size and moves the live tiles onto it,
    /// adding and removing only where they differ. Anything already correct is left alone,
    /// which is what stops a resize disturbing the field it already had.
    /// </summary>
    private void ApplyPlan(int columns, int rows)
    {
        _wanted.Clear();

        for (var row = 0; row < rows; row++)
        {
            var density = Density(row);

            for (var col = 0; col < columns; col++)
            {
                var roll = Roll(col, row, 0);
                bool dispersing;

                if (roll < density - DispersionBand) dispersing = false;
                else if (roll < density + DispersionBand && density > 0) dispersing = true;
                else continue;

                var key = CellKey(col, row);
                _wanted.Add(key);

                if (_tiles.TryGetValue(key, out var existing))
                {
                    if (existing.Dispersing == dispersing) continue;

                    RemoveTile(key, existing);
                }

                AddTile(col, row, dispersing);
            }
        }

        if (_tiles.Count != _wanted.Count)
        {
            var stale = new List<int>();

            foreach (var key in _tiles.Keys)
                if (!_wanted.Contains(key)) stale.Add(key);

            foreach (var key in stale)
                RemoveTile(key, _tiles[key]);
        }

        _live.Clear();
        _live.AddRange(_tiles.Values);

        if (MotionAllowed && _live.Count > 0) StartTicking();
        else StopTicking();
    }

    private void AddTile(int col, int row, bool dispersing)
    {
        var shade = PickShade(col, row);

        var visual = _compositor!.CreateSpriteVisual();
        visual.Size = new Vector2(TileSize, TileSize);

        // Row 0 is the bottom row, and the field's origin is the host's bottom edge, so this
        // offset is correct at every window size.
        visual.Offset = new Vector3(col * TileSize, -(row + 1) * TileSize, 0);
        visual.Brush = _brushes![shade];

        var tile = new Tile { Visual = visual, BaseShade = shade, Shade = shade, Dispersing = dispersing };

        // A dispersing tile that will never be stepped has to settle somewhere; its own roll
        // decides, so a static field is the moving one caught at a plausible moment.
        if (dispersing && !MotionAllowed && Roll(col, row, 0) >= Density(row))
        {
            tile.Rung = 0;
            visual.Opacity = 0f;
        }

        _field!.Children.InsertAtTop(visual);
        _tiles[CellKey(col, row)] = tile;
    }

    private void RemoveTile(int key, Tile tile)
    {
        _field?.Children.Remove(tile.Visual);
        _tiles.Remove(key);
        tile.Visual.Dispose();
    }

    // -- Motion ---------------------------------------------------------------

    private void StartTicking()
    {
        if (_tickTimer != null) return;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _tickTimer.Tick += (s, e) => Tick();
        _tickTimer.Start();
    }

    private void StopTicking()
    {
        if (_tickTimer == null) return;

        _tickTimer.Stop();
        _tickTimer = null;
    }

    /// <summary>
    /// One frame of the field's life: a handful of tiles each take a single step. Solid tiles
    /// step one shade off their own colour and back, dispersing ones step along the fade
    /// rungs, so nothing ever jumps and the field drifts as a whole.
    /// </summary>
    private void Tick()
    {
        if (_isShutDown || _live.Count == 0) return;

        var count = Math.Clamp(_live.Count / TilesPerTickDivisor, MinTilesPerTick, MaxTilesPerTick);

        for (var i = 0; i < count; i++)
        {
            var tile = _live[_random.Next(_live.Count)];

            if (tile.Dispersing) StepFade(tile);
            else StepShade(tile);
        }
    }

    private void StepShade(Tile tile)
    {
        // Back to its own colour if it has wandered, otherwise one step off it - never
        // further, so the field keeps the arrangement it was generated with.
        var target = tile.Shade != tile.BaseShade
            ? tile.BaseShade
            : Math.Clamp(tile.BaseShade + (_random.NextDouble() < 0.5 ? -1 : 1), 0, Palette.Length - 1);

        if (target == tile.Shade) return;

        tile.Shade = target;
        tile.Visual.Brush = _brushes![target];
    }

    private void StepFade(Tile tile)
    {
        if (tile.Rung == 0) tile.Rising = true;
        else if (tile.Rung == FadeRungs.Length - 1) tile.Rising = false;

        tile.Rung += tile.Rising ? 1 : -1;
        tile.Visual.Opacity = FadeRungs[tile.Rung];
    }

    // -- The field's own arithmetic -------------------------------------------

    /// <summary>How much of a row survives: all of it in the solid band at the bottom, none
    /// at the top of the field, convex in between.</summary>
    private double Density(int row)
    {
        var solidRows = _fieldRows * SolidFraction;

        if (row <= solidRows) return 1.0;
        if (row >= _fieldRows) return 0.0;

        return 1.0 - Math.Pow((row - solidRows) / (_fieldRows - solidRows), DispersionCurve);
    }

    /// <summary>A tile's colour, re-rolled once if it matches the neighbour to its left or
    /// below - which is what the original art's generator did, measured.</summary>
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

    private static int CellKey(int col, int row) => (row << 12) | col;
}
