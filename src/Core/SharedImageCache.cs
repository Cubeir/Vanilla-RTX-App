using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Vanilla_RTX_App.Core;

internal static class SharedImageCache
{
    private static readonly Dictionary<string, BitmapImage> _cache = new();
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static bool TryGet(string path, out BitmapImage? image) => _cache.TryGetValue(path, out image);

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
