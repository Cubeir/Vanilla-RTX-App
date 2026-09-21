using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImageMagick;

namespace Vanilla_RTX_App.Modules;

public static partial class Helpers
{
    /// <summary>
    /// Reads images of any given format, with an option to return opacity at maximum (retaining rgb data under 0 opacity pixels)
    /// </summary>
    public static Bitmap ReadImage(string imagePath, bool maxOpacity = false)
    {
        try
        {
            using var sourceImage = new MagickImage(imagePath);
            var width = (int)sourceImage.Width;
            var height = (int)sourceImage.Height;

            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var sourcePixels = sourceImage.GetPixels())
            using (var fb = new FastBitmap(bitmap, writable: true))
            {
                var channelCount = (int)sourceImage.ChannelCount;
                var values = sourcePixels.GetValues()
                    ?? throw new InvalidOperationException($"Failed to read pixel data for '{imagePath}'.");

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixelIndex = (y * width + x) * channelCount;

                        byte r, g, b, a;

                        var hasAlpha = sourceImage.HasAlpha || sourceImage.ColorType == ColorType.GrayscaleAlpha || sourceImage.ColorType == ColorType.TrueColorAlpha;

                        if (sourceImage.ColorType == ColorType.Grayscale)
                        {
                            var gray = (byte)(values[pixelIndex + 0] >> 8);
                            r = g = b = gray;
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.GrayscaleAlpha)
                        {
                            var gray = (byte)(values[pixelIndex + 0] >> 8);
                            r = g = b = gray;
                            var originalAlpha = (byte)(values[pixelIndex + 1] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColor)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColorAlpha)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);
                            var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.Palette)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);

                            if (hasAlpha && sourceImage.ChannelCount > 3)
                            {
                                var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }
                        else
                        {
                            var channels = (int)sourceImage.ChannelCount;

                            r = channels > 0 ? (byte)(values[pixelIndex + 0] >> 8) : (byte)0;
                            g = channels > 1 ? (byte)(values[pixelIndex + 1] >> 8) : r;
                            b = channels > 2 ? (byte)(values[pixelIndex + 2] >> 8) : r;

                            if (hasAlpha && channels > 3)
                            {
                                var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }

                        fb[x, y] = Color.FromArgb(a, r, g, b);
                    }
                }
            }

            return bitmap;
        }
        catch (Exception)
        {
            var errorBitmap = new Bitmap(512, 512, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(errorBitmap))
            {
                g.Clear(Color.Transparent);
                var squareSize = 256;
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), 0, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), squareSize, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), 0, squareSize, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), squareSize, squareSize, squareSize, squareSize);
            }
            return errorBitmap;
        }
    }

    /// <summary>
    /// Write a bitmap to a path as raw, pure targa with 4 channels, 8 bit per channel
    /// </summary>
    public static void WriteImageAsTGA(Bitmap bitmap, string outputPath)
    {
        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);
            // TGA
            writer.Write((byte)0);    // ID Length
            writer.Write((byte)0);    // Color Map Type (0 = no color map)
            writer.Write((byte)2);    // Image Type (2 = uncompressed RGB)
            writer.Write((ushort)0);  // Color Map First Entry Index
            writer.Write((ushort)0);  // Color Map Length
            writer.Write((byte)0);    // Color Map Entry Size
            writer.Write((ushort)0);  // X-origin
            writer.Write((ushort)0);  // Y-origin
            writer.Write((ushort)width);  // Width
            writer.Write((ushort)height); // Height
            writer.Write((byte)32);       // Pixel Depth (32-bit RGBA)
            writer.Write((byte)8);        // Image Descriptor (default origin, 8-bit alpha)

            // FastBitmap bulk-copies the whole buffer once via LockBits/Marshal.Copy,
            // then reads are plain array indexing instead of bitmap.GetPixel(x, y)
            using var fb = new FastBitmap(bitmap, writable: false);

            for (var y = height - 1; y >= 0; y--) // TGA is bottom-up by default
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = fb[x, y];

                    writer.Write(pixel.B);
                    writer.Write(pixel.G);
                    writer.Write(pixel.R);
                    writer.Write(pixel.A);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error writing direct TGA to {outputPath}: {ex.Message}");
            throw;
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  FastBitmap  ──  LockBits-based pixel accessor, drop-in for GetPixel/SetPixel
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Replaces Bitmap.GetPixel/SetPixel for bulk pixel work. Each GetPixel/SetPixel
/// call on a System.Drawing.Bitmap round-trips through native GDI+ with format
/// checks and marshalling on every single pixel; for a 512x512 image that's a
/// quarter million native calls per full pass. FastBitmap instead locks the
/// bitmap once, bulk-copies its raw bytes into a managed buffer with a single
/// Marshal.Copy, and does all reads/writes against that plain byte[] (fast,
/// bounds-checked, no native calls). On Dispose it copies the buffer back
/// (only if opened writable) and unlocks.
///
/// Always requests Format32bppArgb regardless of the bitmap's real pixel
/// format - this exactly mirrors what GetPixel/SetPixel already did (they always
/// hand back/accept a plain ARGB Color regardless of underlying storage), so
/// output is unaffected: GDI+ performs the same implicit conversion on lock/unlock
/// that GetPixel/SetPixel performed internally per call.
///
/// No `unsafe` blocks are required, so no project/csproj changes are needed.
/// </summary>
public sealed class FastBitmap : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly BitmapData _data;
    private readonly byte[] _buffer;
    private readonly int _stride;
    private readonly bool _writable;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public FastBitmap(Bitmap bitmap, bool writable)
    {
        _bitmap = bitmap;
        _writable = writable;
        Width = bitmap.Width;
        Height = bitmap.Height;

        _data = bitmap.LockBits(
            new Rectangle(0, 0, Width, Height),
            writable ? ImageLockMode.ReadWrite : ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        _stride = _data.Stride;
        _buffer = new byte[_stride * Height];
        Marshal.Copy(_data.Scan0, _buffer, 0, _buffer.Length);
    }

    /// <summary>
    /// Reads one pixel out of the local buffer. Bounds are not checked - this is the hot path
    /// for whole-image loops, and the indexer is the public way in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Color Get(int x, int y)
    {
        var i = y * _stride + x * 4;
        // Format32bppArgb byte order in memory is B, G, R, A.
        return Color.FromArgb(_buffer[i + 3], _buffer[i + 2], _buffer[i + 1], _buffer[i]);
    }

    /// <summary>
    /// Writes one pixel into the local buffer. <b>Nothing reaches the Bitmap until
    /// <see cref="Dispose"/></b>, and only if this instance was constructed writable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(int x, int y, Color c)
    {
        var i = y * _stride + x * 4;
        _buffer[i] = c.B;
        _buffer[i + 1] = c.G;
        _buffer[i + 2] = c.R;
        _buffer[i + 3] = c.A;
    }

    /// <summary>
    /// Deliberately named as an indexer rather than GetPixel/SetPixel: those names
    /// read exactly like the slow Bitmap API this class replaces, which caused real
    /// confusion during review even though the implementation underneath is entirely
    /// different (plain array access, no GDI+ calls). fb[x, y] makes it visually
    /// obvious it's not that.
    /// </summary>
    public Color this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Get(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Set(x, y, value);
    }

    /// <summary>
    /// <b>Where writes actually happen.</b> Copies the buffer back into the Bitmap (writable
    /// instances only) and unlocks it. Skipping this - or constructing read-only and then
    /// assigning through the indexer - loses every pixel written, silently, because the
    /// buffer is a copy rather than a view. Always <c>using</c>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_writable)
                Marshal.Copy(_buffer, 0, _data.Scan0, _buffer.Length);
        }
        finally
        {
            _bitmap.UnlockBits(_data);
        }
    }
}
