// ── XAML (add inside root <Grid>) ────────────────────────────────────────────
//   <Image x:Name="RedstoneLayer"
//          HorizontalAlignment="Left"  VerticalAlignment="Top"
//          IsHitTestVisible="False"    Canvas.ZIndex="-3"
//          CacheMode="BitmapCache" />
//
// ── CODE-BEHIND ───────────────────────────────────────────────────────────────
//   private RedstoneFramework? _redstone;
//
//   // at the end of ShowMainContent() — method must be async void:
//   _redstone = new RedstoneFramework(RedstoneLayer,
//                   Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
//   await _redstone.InitializeAsync(_appWindow.Size.Width, _appWindow.Size.Height);
//
//   // in Window_SizeChanged:
//   if (_redstone is not null)
//       await _redstone.RegenerateAsync(args.Size.Width, args.Size.Height);
//
//   // single stroke (e.g. logo click):
//   await _redstone.TriggerStrokeAsync();
//
//   // ambient loop:
//   _redstone.StartContinuousFlashing();
//   _redstone.StopContinuousFlashing();
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.Alchitex;

// This thing is amazing
// Offload it to a background thread, improve its performance
// Then, do this, remake the reactor tile background image, this time with MISSING TILES!
// And keep em flat!? flat as a starting test, you can make it 3d later but yeah
// take the darkest ones perhaps, and second darkest, or randomly, via NOISE, and delete most tiles
// that way redstone shows through the GAPS! this will look a lot nicer! but there gotta be many big gaps!


/// <summary>
/// Generates and animates a procedural Minecraft-style redstone background layer.
/// </summary>
public sealed class RedstoneFramework
{
    // =========================================================================
    //  ▸ TUNABLE CONSTANTS
    // =========================================================================

    // ── Tile ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Physical pixel side-length of each redstone tile.
    /// Should match the source PNG size; arm fractions below are relative so
    /// any TexelSize (16, 32, 48 …) renders correctly without further changes.
    /// </summary>
    private const int TexelSize = 48;

    /// <summary>First column/row of the "arm channel" as a fraction of TexelSize.</summary>
    private const float ArmFracStart = 5f / 16f;

    /// <summary>One past the last column/row of the "arm channel" as a fraction of TexelSize.</summary>
    private const float ArmFracEnd = 11f / 16f;

    // ── Walker / path generation ──────────────────────────────────────────────

    /// <summary>Number of primary walkers seeded from the window edges.</summary>
    private const int WalkerCount = 50;

    /// <summary>Probability [0–1] of the walker turning 90° at each step.</summary>
    private const double TurnChance = 0.1;

    /// <summary>Probability [0–1] of the walker stopping voluntarily (after MinWalkerSteps).</summary>
    private const double StopChance = 0.05;

    /// <summary>Probability [0–1] of spawning a branch walker at each step.</summary>
    private const double BranchChance = 0.2;

    /// <summary>Minimum steps before a walker may stop voluntarily.</summary>
    private const int MinWalkerSteps = 4;

    /// <summary>Step budget granted to branch walkers.</summary>
    private const int BranchBudget = 25;

    /// <summary>Hard cap on total walkers (seeds + branches).</summary>
    private const int MaxTotalWalkers = WalkerCount * 6;

    // ── Power / tint ──────────────────────────────────────────────────────────

    /// <summary>Maximum power level </summary>
    private const int MaxPower = 30;

    /// <summary>R-channel ceiling when a wire carries no stroke power (dim, always visible).</summary>
    private const byte RedAtIdle = 22;

    /// <summary>R-channel ceiling at stroke power = 1 (per spec).</summary>
    private const byte RedAtMinPower = 32;

    /// <summary>R-channel ceiling at stroke power = MaxPower — full bright (per spec).</summary>
    private const byte RedAtMaxPower = 255;

    /// <summary>Power lost per BFS hop from the stroke origin tile.</summary>
    private const int StrokePowerDecay = 1;

    // ── Bloom / glow ─────────────────────────────────────────────────────────

    /// <summary>Pixel radius of the soft glow halo around lit wires.</summary>
    private const int BloomRadius = 4;

    /// <summary>Additive gain applied to the blurred signal before compositing.</summary>
    private const float BloomGain = 1.8f;

    /// <summary>
    /// Gamma applied to the R channel before blurring.
    /// Values &lt; 1 brighten midrange pixels before the blur,
    /// giving a more pronounced glow on partially-powered tiles.
    /// </summary>
    private const float BloomGamma = 0.75f;

    // ── Animation timing ─────────────────────────────────────────────────────

    /// <summary>Milliseconds over which a stroke fades from full intensity to zero.</summary>
    private const int StrokeDurationMs = 500;

    /// <summary>Tick interval for the background fade loop.</summary>
    private const int FadeTickMs = 50;

    /// <summary>Pause between automatic strokes in continuous mode.</summary>
    private const int ContinuousGapMs = 250;

    // ── XAML layer ────────────────────────────────────────────────────────────

    /// <summary>Opacity of the output XAML Image element.</summary>
    public const double LayerOpacity = 1.0;

    // ── Super-grid caching ────────────────────────────────────────────────────

    /// <summary>
    /// The redstone path grid is generated at this multiple of the current
    /// window tile count in each axis.  Resizes that stay within the cached
    /// bounds reuse existing paths and only re-render the visible slice.
    /// A full regen only triggers when the window grows beyond the super-grid.
    /// </summary>
    private const int SuperMultiple = 2;

    // =========================================================================
    //  ▸ SESSION-STABLE SEED
    // =========================================================================

    /// <summary>
    /// Constant for the entire process lifetime.  The same wire layout is
    /// produced for every resize that stays within the cached super-grid,
    /// and a different (but still stable) layout is produced if the super-grid
    /// must grow.  Derived from the process start-time so each new app launch
    /// gets a fresh arrangement.
    /// </summary>
    private static readonly int _sessionSeed =
        Environment.ProcessId ^
        unchecked((int)(Process.GetCurrentProcess().StartTime.Ticks & 0xFFFF_FFFF));

    // =========================================================================
    //  ▸ DIRECTION CONSTANTS & HELPERS
    // =========================================================================

    private const int N = 1, S = 2, E = 4, W = 8;
    private static readonly int[] AllDirs = { N, S, E, W };

    private static (int dx, int dy) Vec(int d) => d switch
    { N => (0, -1), S => (0, 1), E => (1, 0), W => (-1, 0), _ => (0, 0) };

    private static int Opp(int d) => d switch { N => S, S => N, E => W, W => E, _ => 0 };
    private static int Left(int d) => d switch { N => W, W => S, S => E, E => N, _ => 0 };
    private static int Right(int d) => d switch { N => E, E => S, S => W, W => N, _ => 0 };

    // =========================================================================
    //  ▸ CELL DATA
    // =========================================================================

    private struct Cell
    {
        /// <summary>NSEW bitmask of wire connections to adjacent tiles.</summary>
        public int Connections;

        /// <summary>Is there a wire on this tile?</summary>
        public bool Exists;

        /// <summary>
        /// Current animation power (0–MaxPower). Set each fade tick from active
        /// strokes; never stored persistently — always 0 at idle.
        /// </summary>
        public int FlashPower;
    }

    // =========================================================================
    //  ▸ STROKE (animation payload)
    // =========================================================================

    private sealed class Stroke
    {
        /// <summary>Base power (0–MaxPower) per tile at peak intensity.</summary>
        public readonly Dictionary<(int x, int y), int> TilePower = new();

        /// <summary>Current intensity [0..1]; decremented each fade tick.</summary>
        public float Intensity = 1.0f;
    }

    // =========================================================================
    //  ▸ FIELDS
    // =========================================================================

    // ── Super-grid ────────────────────────────────────────────────────────────
    private int _superGw, _superGh;
    private Cell[,] _grid = new Cell[0, 0];

    // ── Visible viewport (subset of the super-grid) ───────────────────────────
    private int _gw, _gh;   // tile count
    private int _pw, _ph;   // pixel count = _g* × TexelSize

    // ── Texture variants [mask 0–15] ──────────────────────────────────────────
    private readonly byte[]?[] _variants = new byte[16][];

    // ── Scene buffers (BGRA8 premultiplied) ───────────────────────────────────
    private byte[] _base = Array.Empty<byte>();   // rendered tiles, no bloom
    private float[] _blur = Array.Empty<float>();  // single-channel blur scratch
    private byte[] _scene = Array.Empty<byte>();   // base + bloom → bitmap
    private byte[] _idle = Array.Empty<byte>();   // baked idle scene (all FlashPower=0)

    // ── Bloom scratch (allocated once, reused every tick) ────────────────────
    private float[] _rowBuf = Array.Empty<float>();
    private float[] _colBuf = Array.Empty<float>();

    // ── Output ────────────────────────────────────────────────────────────────
    private WriteableBitmap? _bitmap;
    private readonly Image _output;
    private readonly DispatcherQueue _dispatcher;

    // ── Animation ─────────────────────────────────────────────────────────────
    private readonly List<Stroke> _strokes = new();
    private readonly object _strokeLock = new();
    private CancellationTokenSource? _fadeCts;
    private CancellationTokenSource? _contCts;
    private volatile bool _ready;

    // =========================================================================
    //  ▸ ARM PIXEL HELPERS (fraction-based, any TexelSize)
    // =========================================================================

    private static int ArmA => (int)(ArmFracStart * TexelSize);           // inclusive start
    private static int ArmB => (int)(ArmFracEnd * TexelSize) - 1;       // inclusive end

    // =========================================================================
    //  ▸ CONSTRUCTOR
    // =========================================================================

    /// <param name="output">
    ///   The XAML Image element that will display the redstone layer.
    /// </param>
    /// <param name="dispatcher">
    ///   Pass <c>Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()</c>
    ///   from the window thread.
    /// </param>
    public RedstoneFramework(Image output, DispatcherQueue dispatcher)
    {
        _output = output;
        _dispatcher = dispatcher;
        _output.Opacity = LayerOpacity;
    }

    // =========================================================================
    //  ▸ PUBLIC API
    // =========================================================================

    /// <summary>
    /// Load textures, generate the super-grid, bake the idle scene, and display it.
    /// Call once after the window has a stable pixel size.
    /// </summary>
    public async Task InitializeAsync(double pixW, double pixH)
    {
        _ready = false;
        StopContinuousFlashing();
        StopFadeLoop();

        int gw = Math.Max(2, (int)pixW / TexelSize);
        int gh = Math.Max(2, (int)pixH / TexelSize);

        try
        {
            await LoadAndBuildVariantsAsync();

            await Task.Run(() =>
            {
                ResizeGrid(gw, gh, forceRegen: true);
                AllocBuffers();
                BakeIdle();
            });

            await DispatchAsync(() =>
            {
                _bitmap = new WriteableBitmap(_pw, _ph);
                _output.Source = _bitmap;
                Array.Copy(_idle, _scene, _scene.Length);
                CommitBitmap();
            });

            _ready = true;
            StartFadeLoop();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Redstone] InitializeAsync: {ex}");
        }
    }

    /// <summary>
    /// Call from <c>Window_SizeChanged</c>.
    /// Reuses the cached super-grid if the new visible size still fits;
    /// otherwise regenerates with 2× headroom before returning.
    /// </summary>
    public async Task RegenerateAsync(double pixW, double pixH)
    {
        if (!_ready) return;

        StopFadeLoop();

        int gw = Math.Max(2, (int)pixW / TexelSize);
        int gh = Math.Max(2, (int)pixH / TexelSize);
        bool needsFullRegen = (gw > _superGw || gh > _superGh);

        try
        {
            await Task.Run(() =>
            {
                ResizeGrid(gw, gh, forceRegen: needsFullRegen);
                AllocBuffers();
                BakeIdle();
            });

            await DispatchAsync(() =>
            {
                _bitmap = new WriteableBitmap(_pw, _ph);
                _output.Source = _bitmap;
                Array.Copy(_idle, _scene, _scene.Length);
                CommitBitmap();
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Redstone] RegenerateAsync: {ex}");
        }
        finally
        {
            StartFadeLoop();
        }
    }

    /// <summary>
    /// Trigger a single power stroke: a BFS-based gradient radiates from a
    /// random visible-edge tile along all connected wires, then fades out over
    /// <see cref="StrokeDurationMs"/> milliseconds.
    /// </summary>
    public Task TriggerStrokeAsync(CancellationToken ct = default)
    {
        if (!_ready) return Task.CompletedTask;

        var origins = EdgeCells();
        if (origins.Count == 0) return Task.CompletedTask;

        var (ox, oy) = origins[new Random().Next(origins.Count)];
        var stroke = BuildStroke(ox, oy);

        lock (_strokeLock)
            _strokes.Add(stroke);

        return Task.CompletedTask;
    }

    /// <summary>Begin triggering strokes automatically at a regular interval.</summary>
    public void StartContinuousFlashing()
    {
        if (!_ready) return;
        _contCts?.Cancel();
        _contCts = new CancellationTokenSource();
        var token = _contCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(ContinuousGapMs, token); }
                catch (OperationCanceledException) { return; }
                await TriggerStrokeAsync(token);
            }
        }, token);
    }

    /// <summary>Stop automatic stroke triggering.</summary>
    public void StopContinuousFlashing()
    {
        _contCts?.Cancel();
        _contCts = null;
    }

    // =========================================================================
    //  ▸ GRID MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Update the visible tile count and, when necessary, regenerate the
    /// underlying super-grid with a deterministic session-stable seed.
    /// </summary>
    private void ResizeGrid(int gw, int gh, bool forceRegen)
    {
        _gw = gw;
        _gh = gh;

        if (forceRegen || gw > _superGw || gh > _superGh)
        {
            _superGw = gw * SuperMultiple;
            _superGh = gh * SuperMultiple;
            _grid = new Cell[_superGw, _superGh];
            GeneratePaths();
        }
        // else: viewport shrank or stayed the same — reuse existing _grid paths.

        lock (_strokeLock) _strokes.Clear(); // old stroke coordinates may be invalid
    }

    private void AllocBuffers()
    {
        _pw = _gw * TexelSize;
        _ph = _gh * TexelSize;
        int n = _pw * _ph;

        // Scene buffers: always sized to the current visible pixel area.
        if (_base.Length != n * 4)
        {
            _base = new byte[n * 4];
            _scene = new byte[n * 4];
            _idle = new byte[n * 4];
        }

        if (_blur.Length != n) _blur = new float[n];
        if (_rowBuf.Length < _pw) _rowBuf = new float[_pw];
        if (_colBuf.Length < _ph) _colBuf = new float[_ph];
    }

    // =========================================================================
    //  ▸ PATH GENERATION
    // =========================================================================

    private void GeneratePaths()
    {
        // Seed: session constant XOR'd with super-grid dimensions.
        // Same session + same super-grid dimensions → identical layout.
        var rng = new Random(_sessionSeed ^ (_superGw * 3571 + _superGh * 1009));
        var queue = new Queue<(int x, int y, int dir, int budget)>();
        int total = 0;

        // ── Seed walkers uniformly from all four super-grid edges ─────────────
        for (int i = 0; i < WalkerCount; i++)
        {
            int edge = rng.Next(4);
            int sx, sy, sdir;
            switch (edge)
            {
                case 0: sx = rng.Next(_superGw); sy = 0; sdir = S; break;
                case 1: sx = rng.Next(_superGw); sy = _superGh - 1; sdir = N; break;
                case 2: sx = 0; sy = rng.Next(_superGh); sdir = E; break;
                default: sx = _superGw - 1; sy = rng.Next(_superGh); sdir = W; break;
            }

            // Add an outward connection so the tile appears to have wire arriving
            // from off-screen rather than starting at a dead end.
            ref Cell sc = ref _grid[sx, sy];
            sc.Exists = true;
            sc.Connections |= Opp(sdir);   // direction toward screen edge

            queue.Enqueue((sx, sy, sdir, _superGw + _superGh + 8));
            total++;
        }

        // ── Process walkers ───────────────────────────────────────────────────
        while (queue.Count > 0)
        {
            var (cx, cy, cd, budget) = queue.Dequeue();
            int steps = 0;

            for (; budget > 0; budget--)
            {
                if (!InBounds(cx, cy)) break;

                _grid[cx, cy].Exists = true;
                steps++;

                // Voluntary stop
                if (steps > MinWalkerSteps && rng.NextDouble() < StopChance) break;

                // Branch: peel off a sub-walker in a perpendicular direction
                if (steps > 1 && total < MaxTotalWalkers && rng.NextDouble() < BranchChance)
                {
                    int bd = rng.Next(2) == 0 ? Left(cd) : Right(cd);
                    var (bdx, bdy) = Vec(bd);
                    int bx = cx + bdx, by = cy + bdy;
                    if (InBounds(bx, by) && !_grid[bx, by].Exists)
                    {
                        Wire(cx, cy, bx, by, bd);
                        queue.Enqueue((bx, by, bd, BranchBudget));
                        total++;
                    }
                }

                // Choose next direction (possibly turn)
                int nd = rng.NextDouble() < TurnChance
                    ? (rng.Next(2) == 0 ? Left(cd) : Right(cd))
                    : cd;

                // Advance, retrying up to 2 alternative directions on collision
                bool moved = false;
                for (int retry = 0; retry <= 2; retry++)
                {
                    var (ndx, ndy) = Vec(nd);
                    int nx = cx + ndx, ny = cy + ndy;

                    if (!InBounds(nx, ny)) goto WalkerDone;

                    if (_grid[nx, ny].Exists)
                    {
                        if (retry < 2) { nd = rng.Next(2) == 0 ? Left(cd) : Right(cd); continue; }
                        goto WalkerDone;
                    }

                    Wire(cx, cy, nx, ny, nd);
                    cx = nx; cy = ny; cd = nd;
                    moved = true;
                    break;
                }

                if (!moved) break;
            }
        WalkerDone:;
        }
    }

    /// <summary>Mark a bidirectional wire connection between two adjacent tiles.</summary>
    private void Wire(int ax, int ay, int bx, int by, int d)
    {
        _grid[ax, ay].Connections |= d;
        _grid[bx, by].Exists = true;
        _grid[bx, by].Connections |= Opp(d);
    }

    private bool InBounds(int x, int y) =>
        (uint)x < (uint)_superGw && (uint)y < (uint)_superGh;

    // =========================================================================
    //  ▸ RENDERING
    // =========================================================================

    /// <summary>
    /// Render the idle scene (all FlashPower = 0) into <c>_base</c>, apply bloom,
    /// and cache the result in <c>_idle</c> for instant reuse between strokes.
    /// </summary>
    private void BakeIdle()
    {
        // Reset any leftover flash power from a previous session
        for (int ty = 0; ty < _superGh; ty++)
            for (int tx = 0; tx < _superGw; tx++)
                _grid[tx, ty].FlashPower = 0;

        RenderAll();
        ApplyBloom();
        Array.Copy(_scene, _idle, _scene.Length);
    }

    private void RenderAll()
    {
        Array.Clear(_base, 0, _base.Length);
        for (int ty = 0; ty < _gh; ty++)
            for (int tx = 0; tx < _gw; tx++)
                if (_grid[tx, ty].Exists)
                    PaintCell(tx, ty);
    }

    private void PaintCell(int tx, int ty)
    {
        ref Cell cell = ref _grid[tx, ty];
        if (!cell.Exists) return;

        int mask = VisualMask(tx, ty) & 0xF;
        byte[]? src = _variants[mask];

        int bx = tx * TexelSize;
        int by = ty * TexelSize;

        // Clear the tile region in _base
        for (int py = 0; py < TexelSize; py++)
            Array.Clear(_base, ((by + py) * _pw + bx) * 4, TexelSize * 4);

        if (src is null) return;

        // Map FlashPower → red ceiling
        int power = cell.FlashPower;
        byte rCeil;
        if (power <= 0)
            rCeil = RedAtIdle;
        else if (power >= MaxPower)
            rCeil = RedAtMaxPower;
        else
        {
            float t = (power - 1f) / (MaxPower - 1f);   // 0 at power 1, 1 at MaxPower
            rCeil = (byte)(RedAtMinPower + t * (RedAtMaxPower - RedAtMinPower));
        }

        for (int py = 0; py < TexelSize; py++)
        {
            for (int px = 0; px < TexelSize; px++)
            {
                int sOff = (py * TexelSize + px) * 4;
                byte srcA = src[sOff + 3];
                if (srcA == 0) continue;

                // Source texture is near-greyscale; brightness drives the red value.
                float bright = (src[sOff] + src[sOff + 1] + src[sOff + 2]) / (3f * 255f);
                // Premultiply R: straightR × alpha
                byte r = (byte)(bright * rCeil * srcA / 255f);

                int dOff = ((by + py) * _pw + (bx + px)) * 4;
                _base[dOff] = 0;     // B (premultiplied = 0)
                _base[dOff + 1] = 0;     // G (premultiplied = 0)
                _base[dOff + 2] = r;     // R (premultiplied)
                _base[dOff + 3] = srcA;  // A
            }
        }
    }

    /// <summary>
    /// Returns the visual connection mask for a tile.
    /// For tiles on the visible window border, the outward-facing bit is added
    /// (if the wire already has the matching inward bit) so the wire appears to
    /// enter/exit from off-screen rather than dead-ending at the border.
    /// </summary>
    private int VisualMask(int tx, int ty)
    {
        int m = _grid[tx, ty].Connections;
        if (tx == 0 && (m & E) != 0) m |= W;
        if (tx == _gw - 1 && (m & W) != 0) m |= E;
        if (ty == 0 && (m & S) != 0) m |= N;
        if (ty == _gh - 1 && (m & N) != 0) m |= S;
        return m;
    }

    // =========================================================================
    //  ▸ BLOOM  (separable box blur → additive composite)
    // =========================================================================

    /// <summary>
    /// Reads the R channel from <c>_base</c>, runs a separable box blur,
    /// and additively composites the result into <c>_scene</c> for a soft glow.
    /// All scratch arrays (_blur, _rowBuf, _colBuf) are pre-allocated fields.
    /// </summary>
    private void ApplyBloom()
    {
        int W = _pw, H = _ph, R = BloomRadius;

        // ── Step 1: gamma-encode R channel from _base into _blur ─────────────
        for (int i = 0; i < W * H; i++)
            _blur[i] = MathF.Pow(_base[i * 4 + 2] / 255f, BloomGamma);

        // ── Step 2: horizontal box blur (sliding window, O(W) per row) ────────
        for (int y = 0; y < H; y++)
        {
            int rowOff = y * W;
            float sum = 0f;
            int cnt = 0;

            for (int x = 0; x < W; x++)
            {
                // Expand right side of window
                sum += _blur[rowOff + x];
                cnt++;

                // Shrink left side once window is full
                int drop = x - (2 * R + 1);
                if (drop >= 0) { sum -= _blur[rowOff + drop]; cnt--; }

                // Write to the centre of the current window
                int c = x - R;
                if (c >= 0) _rowBuf[c] = cnt > 0 ? sum / cnt : 0f;
            }

            // Pad the right tail (last R columns)
            float edgeVal = _rowBuf[Math.Max(0, W - R - 1)];
            for (int x = W - R; x < W; x++) _rowBuf[x] = edgeVal;

            Array.Copy(_rowBuf, 0, _blur, rowOff, W);
        }

        // ── Step 3: vertical box blur (same sliding-window pattern) ──────────
        for (int x = 0; x < W; x++)
        {
            float sum = 0f;
            int cnt = 0;

            for (int y = 0; y < H; y++)
            {
                sum += _blur[y * W + x];
                cnt++;

                int drop = y - (2 * R + 1);
                if (drop >= 0) { sum -= _blur[drop * W + x]; cnt--; }

                int c = y - R;
                if (c >= 0) _colBuf[c] = cnt > 0 ? sum / cnt : 0f;
            }

            float edgeVal = _colBuf[Math.Max(0, H - R - 1)];
            for (int y = H - R; y < H; y++) _colBuf[y] = edgeVal;

            for (int y = 0; y < H; y++) _blur[y * W + x] = _colBuf[y];
        }

        // ── Step 4: additive composite — _scene = _base + glow ───────────────
        for (int i = 0; i < W * H; i++)
        {
            int b = i * 4;
            float glow = _blur[i] * BloomGain;

            // Glow contributes only to the red channel and a soft alpha halo.
            byte gR = (byte)Math.Min(255, (int)(glow * 255f));
            byte gA = (byte)Math.Min(255, (int)(glow * 130f));   // softer alpha so it doesn't wash out

            _scene[b] = _base[b];                                        // B unchanged
            _scene[b + 1] = _base[b + 1];                                    // G unchanged
            _scene[b + 2] = (byte)Math.Min(255, _base[b + 2] + gR);          // R + glow
            _scene[b + 3] = (byte)Math.Min(255, _base[b + 3] + gA);          // A + glow alpha
        }
    }

    // =========================================================================
    //  ▸ BITMAP COMMIT
    // =========================================================================

    private void CommitBitmap()
    {
        if (_bitmap is null) return;
        using var s = _bitmap.PixelBuffer.AsStream();
        s.Position = 0;
        s.Write(_scene, 0, _scene.Length);
        _bitmap.Invalidate();
    }

    private Task CommitBitmapAsync() => DispatchAsync(CommitBitmap);

    // =========================================================================
    //  ▸ FADE LOOP (background animation tick)
    // =========================================================================

    private void StartFadeLoop()
    {
        StopFadeLoop();
        _fadeCts = new CancellationTokenSource();
        var token = _fadeCts.Token;
        float fadeStep = (float)FadeTickMs / StrokeDurationMs;

        _ = Task.Run(async () =>
        {
            bool wasActive = false;

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(FadeTickMs, token); }
                catch (OperationCanceledException) { return; }

                bool isActive;

                lock (_strokeLock)
                {
                    // ── Decrement intensities, reap dead strokes ──────────────
                    for (int i = _strokes.Count - 1; i >= 0; i--)
                    {
                        _strokes[i].Intensity -= fadeStep;
                        if (_strokes[i].Intensity <= 0f) _strokes.RemoveAt(i);
                    }

                    // ── Reset flash power, then apply live strokes ────────────
                    for (int ty = 0; ty < _gh; ty++)
                        for (int tx = 0; tx < _gw; tx++)
                            _grid[tx, ty].FlashPower = 0;

                    foreach (var s in _strokes)
                    {
                        foreach (var kv in s.TilePower)
                        {
                            int tx = kv.Key.x, ty = kv.Key.y;
                            // Bounds-guard: stroke may have been built on an older viewport
                            if ((uint)tx >= (uint)_gw || (uint)ty >= (uint)_gh) continue;

                            int fp = (int)(kv.Value * s.Intensity);
                            if (fp > _grid[tx, ty].FlashPower)
                                _grid[tx, ty].FlashPower = fp;
                        }
                    }

                    isActive = _strokes.Count > 0;
                }

                // Draw if strokes are active — or once more right after they die
                // (to restore the clean idle scene).
                bool needsDraw = isActive || wasActive;
                wasActive = isActive;

                if (needsDraw)
                {
                    if (isActive)
                    {
                        // Full render + bloom each tick while strokes are alive.
                        await Task.Run(() => { RenderAll(); ApplyBloom(); });
                    }
                    else
                    {
                        // Strokes just died — restore the pre-baked idle scene
                        // without recomputing anything.
                        Array.Copy(_idle, _scene, _scene.Length);
                    }

                    await CommitBitmapAsync();
                }
            }
        }, token);
    }

    private void StopFadeLoop()
    {
        _fadeCts?.Cancel();
        _fadeCts = null;
    }

    // =========================================================================
    //  ▸ STROKE BUILDING
    // =========================================================================

    /// <summary>
    /// BFS from <paramref name="ox"/>, <paramref name="oy"/> through connected
    /// wire tiles, assigning power = max(0, MaxPower − hops × StrokePowerDecay).
    /// The resulting gradient looks like electricity flowing from the window edge.
    /// </summary>
    private Stroke BuildStroke(int ox, int oy)
    {
        var stroke = new Stroke();
        var visited = new HashSet<(int, int)>();
        var queue = new Queue<(int x, int y, int power)>();
        queue.Enqueue((ox, oy, MaxPower));

        while (queue.Count > 0)
        {
            var (cx, cy, cp) = queue.Dequeue();
            if (!visited.Add((cx, cy))) continue;
            if (!InBounds(cx, cy) || !_grid[cx, cy].Exists) continue;

            stroke.TilePower[(cx, cy)] = cp;
            if (cp <= 0) continue;

            int nextPow = Math.Max(0, cp - StrokePowerDecay);
            int conn = _grid[cx, cy].Connections;

            foreach (int dir in AllDirs)
            {
                if ((conn & dir) == 0) continue;
                var (dx, dy) = Vec(dir);
                queue.Enqueue((cx + dx, cy + dy, nextPow));
            }
        }

        return stroke;
    }

    /// <summary>
    /// Returns all existing tiles that lie on the VISIBLE window border
    /// (not the super-grid border), so strokes always originate at the screen edge.
    /// </summary>
    private List<(int x, int y)> EdgeCells()
    {
        var list = new List<(int x, int y)>();

        for (int tx = 0; tx < _gw; tx++)
        {
            if (_grid[tx, 0].Exists) list.Add((tx, 0));
            if (_grid[tx, _gh - 1].Exists) list.Add((tx, _gh - 1));
        }
        for (int ty = 1; ty < _gh - 1; ty++)
        {
            if (_grid[0, ty].Exists) list.Add((0, ty));
            if (_grid[_gw - 1, ty].Exists) list.Add((_gw - 1, ty));
        }

        // Fallback: seed from any tile within the first 3 rows/columns
        if (list.Count == 0)
        {
            int margin = Math.Min(3, _gh);
            for (int ty = 0; ty < margin; ty++)
                for (int tx = 0; tx < _gw; tx++)
                    if (_grid[tx, ty].Exists) list.Add((tx, ty));
        }

        return list;
    }

    // =========================================================================
    //  ▸ TEXTURE LOADING & VARIANT BUILDING
    // =========================================================================

    private async Task LoadAndBuildVariantsAsync()
    {
        const string root = "ms-appx:///Modules/Alchitex/Assets/";
        byte[] cross = await LoadTexAsync(root + "cross_junction.png");
        byte[] hline = await LoadTexAsync(root + "horizontal_line.png");

        for (int mask = 0; mask < 16; mask++)
            _variants[mask] = BuildVariant(cross, hline, mask);
    }

    private static async Task<byte[]> LoadTexAsync(string msAppxUri)
    {
        var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(msAppxUri));
        using var stream = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(stream);

        var pd = await dec.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = (uint)TexelSize, ScaledHeight = (uint)TexelSize },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return pd.DetachPixelData();
    }

    /// <summary>
    /// Derive one BGRA8 variant tile for the given NSEW connection mask.
    ///
    /// Mask bit layout:  N=1  S=2  E=4  W=8
    ///
    /// Pure EW  (mask 12) → horizontal_line.png as-is.
    /// Pure NS  (mask  3) → horizontal_line.png rotated 90° CW in memory (no extra file needed).
    /// All 14 others      → cross_junction.png with pixel columns/rows zeroed for missing arms.
    ///
    /// The "arm" on each axis spans pixels [ArmA .. ArmB] (inclusive).
    /// Pixels inside BOTH the vertical and horizontal arm bands form the centre
    /// intersection and are always kept when any connection is present.
    /// </summary>
    private static byte[] BuildVariant(byte[] cross, byte[] hline, int mask)
    {
        var dst = new byte[TexelSize * TexelSize * 4];
        bool hasN = (mask & N) != 0, hasS = (mask & S) != 0;
        bool hasE = (mask & E) != 0, hasW = (mask & W) != 0;

        bool pureH = hasE && hasW && !hasN && !hasS;
        bool pureV = hasN && hasS && !hasE && !hasW;

        int half = TexelSize / 2;
        int a = ArmA, b = ArmB;

        for (int iy = 0; iy < TexelSize; iy++)
        {
            for (int ix = 0; ix < TexelSize; ix++)
            {
                int dOff = (iy * TexelSize + ix) * 4;

                if (pureH || pureV)
                {
                    // For pureV: rotate hline 90° CW — source (sx, sy) = (T-1-iy, ix)
                    int sx = pureV ? (TexelSize - 1 - iy) : ix;
                    int sy = pureV ? ix : iy;
                    int sOff = (sy * TexelSize + sx) * 4;
                    dst[dOff] = hline[sOff];
                    dst[dOff + 1] = hline[sOff + 1];
                    dst[dOff + 2] = hline[sOff + 2];
                    dst[dOff + 3] = hline[sOff + 3];
                }
                else
                {
                    bool inV = ix >= a && ix <= b;  // inside vertical arm band
                    bool inH = iy >= a && iy <= b;  // inside horizontal arm band
                    bool isCenter = inV && inH;

                    bool keep;
                    if (mask == 0)
                    {
                        keep = isCenter;   // isolated node: centre dot only
                    }
                    else
                    {
                        keep = isCenter                                        // centre intersection
                            || (hasN && inV && iy < half && !inH)           // north arm
                            || (hasS && inV && iy >= half && !inH)           // south arm
                            || (hasE && inH && ix >= half && !inV)           // east arm
                            || (hasW && inH && ix < half && !inV);          // west arm
                    }

                    if (keep)
                    {
                        // Source and destination are identical layouts (TexelSize × TexelSize).
                        dst[dOff] = cross[dOff];
                        dst[dOff + 1] = cross[dOff + 1];
                        dst[dOff + 2] = cross[dOff + 2];
                        dst[dOff + 3] = cross[dOff + 3];
                    }
                    // else: transparent (already 0)
                }
            }
        }

        return dst;
    }

    // =========================================================================
    //  ▸ DISPATCHER HELPER
    // =========================================================================

    private Task DispatchAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        bool ok = _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try { action(); }
            catch (Exception ex) { Trace.WriteLine($"[Redstone] dispatch: {ex.Message}"); }
            finally { tcs.TrySetResult(); }
        });
        if (!ok) tcs.TrySetResult();
        return tcs.Task;
    }
}
