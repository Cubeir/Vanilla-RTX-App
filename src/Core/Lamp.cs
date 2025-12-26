using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Unified lamp animation system for both titlebar and splash screen contexts.
/// Handles special occasion theming as alternative texture sets.
/// </summary>
public class LampAnimator
{
    private static readonly Dictionary<string, BitmapImage> _globalImageCache = new();
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    private readonly LampContext _context;
    private readonly Image _baseImage;
    private readonly Image _overlayImage;
    private readonly Image _haloImage;
    private readonly Image _superImage;

    private readonly string _onPath;
    private readonly string _offPath;
    private readonly string _superPath;
    private readonly string _haloPath;

    // Default opacity values matching XAML
    private readonly double _defaultHaloOpacity;
    private readonly double _defaultBaseOpacity = 1.0;
    private readonly double _defaultOverlayOpacity = 0.0;
    private readonly double _defaultSuperOpacity = 0.0;

    private CancellationTokenSource _blinkCts;
    private Task _animationTask;
    private readonly SemaphoreSlim _animationLock = new(1, 1);
    private bool _isInitialized;
    private bool _isBusy;

    public enum LampContext
    {
        Titlebar,
        Splash
    }

    public LampAnimator(
        LampContext context,
        Image baseImage,
        Image overlayImage = null,
        Image haloImage = null,
        Image superImage = null)
    {
        _context = context;
        _baseImage = baseImage ?? throw new ArgumentNullException(nameof(baseImage));
        _overlayImage = overlayImage;
        _haloImage = haloImage;
        _superImage = superImage;

        _defaultHaloOpacity = _context == LampContext.Splash ? 0.175 : 0.25;

        var paths = ResolveImagePaths();
        _onPath = paths.on;
        _offPath = paths.off;
        _superPath = paths.super;
        _haloPath = paths.halo;
    }

    /// <summary>
    /// Main animation method - handles blinking, single flashes, steady states, and timed animations.
    /// </summary>
    public async Task Animate(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75, double? duration = null)
    {
        const double initialDelayMs = 900;
        const double minDelayMs = 150;
        const double minRampSec = 1;
        const double maxRampSec = 8;

        double fadeAnimationMs = _context == LampContext.Splash ? 100 : 75;

        await _animationLock.WaitAsync();
        try
        {
            var random = new Random();
            await EnsureInitialized();

            // Single flashes are IGNORED if lamp is busy
            if (singleFlash && _isBusy)
                return;

            // If animation is running, only allow disable command
            if (_animationTask != null && !_animationTask.IsCompleted)
            {
                if (!enable)
                {
                    _blinkCts?.Cancel();
                    try { await _animationTask; }
                    catch (OperationCanceledException) { }

                    _animationTask = null;
                    _blinkCts = null;
                }
                else
                {
                    return; // Already animating
                }
                return;
            }

            // Timed animation (e.g., splash screen)
            if (duration.HasValue)
            {
                _isBusy = true;
                try
                {
                    await ExecuteTimedAnimation(duration.Value, random, singleFlashOnChance, fadeAnimationMs);
                }
                finally
                {
                    _isBusy = false;
                }
                return;
            }

            // Single flash
            if (singleFlash)
            {
                _isBusy = true;
                try
                {
                    await ExecuteSingleFlash(random, singleFlashOnChance, fadeAnimationMs);
                }
                finally
                {
                    _isBusy = false;
                }
                return;
            }

            // Continuous blinking
            if (enable)
            {
                _blinkCts = new CancellationTokenSource();
                _isBusy = true;
                _animationTask = BlinkLoop(_blinkCts.Token, fadeAnimationMs, initialDelayMs, minDelayMs, minRampSec, maxRampSec, random);
            }
            else
            {
                if (!_isBusy)
                    return; // Already stopped

                await SetupSteadyOnState(fadeAnimationMs);
            }
        }
        finally
        {
            _animationLock.Release();
        }
    }

    private (string on, string off, string super, string halo) ResolveImagePaths()
    {
        var today = DateTime.Today;
        string specialName = GetSpecialOccasionName(today);

        if (specialName != null)
        {
            string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "special");
            return (
                Path.Combine(baseDir, $"{specialName}.on.png"),
                Path.Combine(baseDir, $"{specialName}.off.png"),
                Path.Combine(baseDir, $"{specialName}.super.png"),
                Path.Combine(baseDir, $"{specialName}.halo.png")
            );
        }

        if (_context == LampContext.Splash)
        {
            return (
                "ms-appx:///Assets/icons/SplashScreen.Regular.png",
                "ms-appx:///Assets/icons/SplashScreen.Off.png",
                "ms-appx:///Assets/icons/SplashScreen.Super.png",
                "ms-appx:///Assets/icons/SplashScreen.Halo.png"
            );
        }

        return (
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.off.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.super.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.halo.png")
        );
    }

    private string GetSpecialOccasionName(DateTime date)
    {
        if (date.Month == 4 && date.Day >= 21 && date.Day <= 23)
            return "birthday";

        if ((date.Month == 12 && date.Day >= 23) || (date.Month == 1 && date.Day <= 7))
            return "christmas";

        if (date.Month == 10 && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
            return "pumpkin";

        return null;
    }

    /// <summary>
    /// Ensures all images are preloaded and ready for instant swapping.
    /// XAML defaults are preserved - only overridden for special occasions.
    /// </summary>
    private async Task EnsureInitialized()
    {
        if (_isInitialized)
            return;

        string specialName = GetSpecialOccasionName(DateTime.Today);

        // Always preload images for instant access during animations
        var loadTasks = new List<Task>();

        if (specialName != null)
        {
            // Special occasion - preload AND set images
            loadTasks.Add(GetCachedImageAsync(_onPath));
            loadTasks.Add(GetCachedImageAsync(_offPath));
            loadTasks.Add(GetCachedImageAsync(_superPath));
            loadTasks.Add(GetCachedImageAsync(_haloPath));

            await Task.WhenAll(loadTasks);

            // Override XAML with special textures
            await SetImageAsync(_baseImage, _onPath);
            if (_haloImage != null)
                await SetImageAsync(_haloImage, _haloPath);
            if (_overlayImage != null)
                await SetImageAsync(_overlayImage, _offPath);
            if (_superImage != null)
                await SetImageAsync(_superImage, _context == LampContext.Splash ? _offPath : _superPath);
        }
        else if (_context == LampContext.Titlebar)
        {
            // Titlebar on normal days - preload for instant texture swapping
            // (Splash uses ms-appx which loads instantly, no preload needed)
            loadTasks.Add(GetCachedImageAsync(_onPath));
            loadTasks.Add(GetCachedImageAsync(_offPath));
            loadTasks.Add(GetCachedImageAsync(_superPath));
            loadTasks.Add(GetCachedImageAsync(_haloPath));

            await Task.WhenAll(loadTasks);
        }

        // Ensure opacity defaults match XAML
        _baseImage.Opacity = _defaultBaseOpacity;
        if (_overlayImage != null)
            _overlayImage.Opacity = _defaultOverlayOpacity;
        if (_superImage != null)
            _superImage.Opacity = _defaultSuperOpacity;
        if (_haloImage != null)
            _haloImage.Opacity = _defaultHaloOpacity;

        _isInitialized = true;
    }

    private async Task SetupSteadyOnState(double fadeMs)
    {
        await SetImageAsync(_baseImage, _onPath);
        _baseImage.Opacity = _defaultBaseOpacity;

        if (_overlayImage != null)
            _overlayImage.Opacity = _defaultOverlayOpacity;

        if (_superImage != null)
            _superImage.Opacity = _defaultSuperOpacity;

        if (_haloImage != null)
            await AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs);
    }

    private async Task<BitmapImage> GetCachedImageAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        await _cacheLock.WaitAsync();
        try
        {
            if (_globalImageCache.TryGetValue(path, out var cached))
                return cached;

            BitmapImage bmp;
            if (path.StartsWith("ms-appx:///"))
            {
                bmp = new BitmapImage(new Uri(path));
            }
            else
            {
                if (!File.Exists(path))
                    return null;

                using var stream = File.OpenRead(path);
                bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            }

            _globalImageCache[path] = bmp;
            return bmp;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task SetImageAsync(Image imageControl, string path)
    {
        if (imageControl == null || string.IsNullOrEmpty(path)) return;

        var image = await GetCachedImageAsync(path);
        if (image != null)
            imageControl.Source = image;
    }

    private async Task AnimateOpacity(FrameworkElement element, double targetOpacity, double durationMs, CancellationToken ct = default)
    {
        if (element == null) return;

        if (ct.IsCancellationRequested)
        {
            element.Opacity = targetOpacity;
            return;
        }

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);

        var tcs = new TaskCompletionSource<bool>();
        storyboard.Completed += (s, e) => tcs.SetResult(true);
        storyboard.Begin();

        await Task.WhenAny(tcs.Task, Task.Delay(-1, ct));

        if (ct.IsCancellationRequested)
        {
            storyboard.Stop();
            element.Opacity = targetOpacity;
        }
    }

    private async Task ExecuteTimedAnimation(double durationMs, Random random, double superFlashChance, double fadeMs)
    {
        const double minFlashDuration = 300;
        const double maxFlashDuration = 700;

        double availableTime = durationMs - 400;
        double flashStart = Math.Max(200, availableTime * 0.3);
        double flashDuration = Math.Clamp(availableTime * 0.4, minFlashDuration, maxFlashDuration);

        await Task.Delay((int)flashStart);
        await ExecuteSingleFlash(random, superFlashChance, fadeMs, (int)flashDuration);
        await SetupSteadyOnState(fadeMs);
    }

    private async Task ExecuteSingleFlash(Random random, double superFlashChance, double fadeMs, int? customDuration = null)
    {
        bool doSuperFlash = random.NextDouble() < superFlashChance;
        int flashDuration = customDuration ?? random.Next(300, 800);

        if (doSuperFlash)
        {
            if (_context == LampContext.Splash && _superImage != null)
            {
                await SetImageAsync(_superImage, _superPath);
                var tasks = new[] {
                    AnimateOpacity(_superImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                tasks = new[] {
                    AnimateOpacity(_superImage, _defaultSuperOpacity, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
            }
            else if (_context == LampContext.Titlebar)
            {
                await SetImageAsync(_baseImage, _superPath);
                if (_overlayImage != null)
                    _overlayImage.Opacity = 0;

                var tasks = new[] {
                    AnimateOpacity(_baseImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                await SetImageAsync(_baseImage, _onPath);
                _baseImage.Opacity = _defaultBaseOpacity;
                if (_overlayImage != null)
                    _overlayImage.Opacity = _defaultOverlayOpacity;
                if (_haloImage != null)
                    await AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs);
            }
        }
        else
        {
            if (_context == LampContext.Splash && _superImage != null)
            {
                await SetImageAsync(_superImage, _offPath);
                var tasks = new[] {
                    AnimateOpacity(_superImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                tasks = new[] {
                    AnimateOpacity(_superImage, _defaultSuperOpacity, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
            }
            else if (_context == LampContext.Titlebar && _overlayImage != null)
            {
                _baseImage.Opacity = _defaultBaseOpacity;
                _overlayImage.Opacity = 0.0;

                var tasks = new[] {
                    AnimateOpacity(_overlayImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                _overlayImage.Opacity = _defaultOverlayOpacity;
                _baseImage.Opacity = _defaultBaseOpacity;
                if (_haloImage != null)
                    await AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs);
            }
        }
    }

    private async Task BlinkLoop(CancellationToken token, double fadeMs, double initialDelayMs, double minDelayMs,
        double minRampSec, double maxRampSec, Random random)
    {
        if (_overlayImage == null)
            return;

        try
        {
            await SetImageAsync(_baseImage, _onPath);
            await SetImageAsync(_overlayImage, _offPath);

            _baseImage.Opacity = _defaultBaseOpacity;
            _overlayImage.Opacity = _defaultOverlayOpacity;

            bool state = true;
            bool rampingUp = true;
            double currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
            var rampStartTime = DateTime.UtcNow;
            var nextSuperFlash = DateTime.UtcNow.AddSeconds(random.NextDouble() * 5 + 4);

            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                if (now >= nextSuperFlash)
                {
                    bool isRapidFlash = random.NextDouble() < 0.20;

                    if (isRapidFlash)
                    {
                        var flashCount = random.Next(1, 6);
                        var flashSpeed = random.Next(50, 100);

                        for (int i = 0; i < flashCount && !token.IsCancellationRequested; i++)
                        {
                            await SetImageAsync(_baseImage, _superPath);
                            _overlayImage.Opacity = 0;
                            if (_haloImage != null) _ = AnimateOpacity(_haloImage, 0.6, fadeMs, token);
                            await Task.Delay(75, token);

                            await SetImageAsync(_baseImage, _onPath);
                            if (_haloImage != null) _ = AnimateOpacity(_haloImage, 0.5, fadeMs, token);
                            await Task.Delay(flashSpeed, token);
                        }
                    }
                    else
                    {
                        var superFlashDuration = random.Next(300, 1500);
                        await SetImageAsync(_baseImage, _superPath);
                        _overlayImage.Opacity = 0;

                        await Task.WhenAll(
                            AnimateOpacity(_baseImage, 1.0, fadeMs, token),
                            _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs, token) : Task.CompletedTask
                        );

                        await Task.Delay(superFlashDuration, token);
                    }

                    if (token.IsCancellationRequested)
                        break;

                    await SetImageAsync(_baseImage, _onPath);
                    await SetImageAsync(_overlayImage, _offPath);

                    await Task.WhenAll(
                        AnimateOpacity(_overlayImage, 1.0, fadeMs, token),
                        _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs, token) : Task.CompletedTask
                    );

                    state = false;
                    rampingUp = true;
                    currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
                    rampStartTime = DateTime.UtcNow;
                    nextSuperFlash = DateTime.UtcNow.AddSeconds(random.NextDouble() * 5 + 4);
                    continue;
                }

                double phaseTime = (now - rampStartTime).TotalSeconds;
                double progress = Math.Clamp(phaseTime / currentRampDuration, 0, 1);
                double eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;

                double delay = rampingUp
                    ? initialDelayMs - (initialDelayMs - minDelayMs) * eased
                    : minDelayMs + (initialDelayMs - minDelayMs) * eased;

                await Task.WhenAll(
                    AnimateOpacity(_overlayImage, state ? 0.0 : 1.0, fadeMs, token),
                    _haloImage != null ? AnimateOpacity(_haloImage, state ? 0.5 : 0.025, fadeMs, token) : Task.CompletedTask
                );

                state = !state;

                if (progress >= 1.0)
                {
                    rampingUp = !rampingUp;
                    rampStartTime = DateTime.UtcNow;
                    currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
                }

                await Task.Delay((int)delay, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try
            {
                await SetImageAsync(_baseImage, _superPath);
                if (_overlayImage != null) _overlayImage.Opacity = 0;

                await Task.WhenAll(
                    AnimateOpacity(_baseImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask
                );

                await Task.Delay(token.IsCancellationRequested ? 400 : random.Next(450, 900));
            }
            catch { }

            await SetupSteadyOnState(fadeMs);
            _isBusy = false;
        }
    }
}
