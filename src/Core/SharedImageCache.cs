using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// One decoded <see cref="BitmapImage"/> per path, held for the life of the process.
///
/// <para><b>What it is for:</b> art the UI needs unpredictably and instantly - the previewer
/// swaps images on hover with no chance to load one first. Decoding on demand there shows a
/// blank frame; decoding once and keeping it does not.</para>
///
/// <para><b>Nothing is ever evicted</b>, so everything put in here is resident for the
/// session and its total is a fixed cost. A decoded image costs width x height x 4 bytes of
/// graphics memory regardless of its file size, so the number that matters is total decoded
/// bytes, not file count or bytes on disk - see CLAUDE.md §6. Do not add images whose
/// dimensions are not under this repo's control.</para>
/// </summary>
internal static class SharedImageCache
{
    private static readonly Dictionary<string, BitmapImage> _cache = new();
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// The cached image for <paramref name="path"/>, without loading one if it is absent.
    /// For callers on the UI thread that need an image <i>now</i> and have a fallback for
    /// "not yet" - <see cref="GetOrLoadAsync"/> is async and cannot answer synchronously.
    /// </summary>
    public static bool TryGet(string path, out BitmapImage? image) => _cache.TryGetValue(path, out image);

    /// <summary>
    /// The cached image for <paramref name="path"/>, loading and caching it on first ask.
    /// Null for a null/empty path or a file that isn't there; a decode failure throws, as
    /// <see cref="BitmapImage"/> does.
    ///
    /// <para>Accepts both <c>ms-appx:///</c> URIs (packaged assets, loaded by URI) and
    /// absolute file paths (loaded through a stream, since BitmapImage cannot take a plain
    /// path). The double-checked lock means concurrent callers for the same path decode it
    /// once rather than racing to overwrite each other's entry.</para>
    /// </summary>
    public static async Task<BitmapImage?> GetOrLoadAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(path, out cached)) return cached;

            BitmapImage bmp;
            if (path.StartsWith("ms-appx:///"))
            {
                bmp = new BitmapImage(new Uri(path));
            }
            else
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            }

            _cache[path] = bmp;
            return bmp;
        }
        finally
        {
            _lock.Release();
        }
    }
}
