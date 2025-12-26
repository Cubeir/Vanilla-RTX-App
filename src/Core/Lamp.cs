using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Resolved once at the start
    private readonly string _onPath;
    private readonly string _offPath;
    private readonly string _superPath;
    private readonly string _haloPath;

    private CancellationTokenSource _blinkCts;
    private bool _isAnimating;

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

        // Resolve image paths once at construction based on date
        var paths = ResolveImagePaths();
        _onPath = paths.on;
        _offPath = paths.off;
        _superPath = paths.super;
        _haloPath = paths.halo;
    }

    /// <summary>
    /// Main animation method - handles blinking, single flashes, and steady states.
    /// Cannot interrupt existing animations.
    /// </summary>
    public async Task Animate(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75)
    {
        const double initialDelayMs = 900;
        const double minDelayMs = 150;
        const double minRampSec = 1;
        const double maxRampSec = 8;
        const double fadeAnimationMs = 75;

        // CRITICAL: Prevent any interruptions if animation is running
        if (_isAnimating)
        {
            return;
        }

        var random = new Random();

        // Preload all images
        await PreloadImages();

        // Single flash
        if (singleFlash)
        {
            await ExecuteSingleFlash(random, singleFlashOnChance, fadeAnimationMs);
            return;
        }

        // Continuous blinking
        if (enable)
        {
            _blinkCts?.Cancel();
            _blinkCts = new CancellationTokenSource();
            _isAnimating = true;

            // Start background animation - keep _isAnimating true until it stops
            _ = BlinkLoop(_blinkCts.Token, fadeAnimationMs, initialDelayMs, minDelayMs, minRampSec, maxRampSec, random);
        }
        else
        {
            // Stop any running animation
            _blinkCts?.Cancel();
            _blinkCts = null;
            _isAnimating = false;

            // Steady on state
            await SetupSteadyOnState(fadeAnimationMs);
        }
    }

    /// <summary>
    /// Animate splash screen (simplified for startup).
    /// </summary>
    public async Task AnimateSplash(double splashDurationMs)
    {
        if (_context != LampContext.Splash)
            throw new InvalidOperationException("AnimateSplash can only be called on Splash context");

        const double fadeAnimationMs = 100;
        const double minFlashDuration = 300;
        const double maxFlashDuration = 700;

        var random = new Random();
        await PreloadImages();

        bool isOffFlash = random.NextDouble() < 0.25;
        double targetSuperOpacity = 1.0;
        double targetHaloOpacity = isOffFlash ? 0.01 : 0.75;

        if (isOffFlash)
        {
            await SetImageAsync(_superImage, _offPath);
        }
        else
        {
            await SetImageAsync(_superImage, _superPath);
        }

        double availableTime = splashDurationMs - 400;
        double flashStart = Math.Max(200, availableTime * 0.3);
        double flashDuration = Math.Clamp(availableTime * 0.4, minFlashDuration, maxFlashDuration);

        await Task.Delay((int)flashStart);

        var superFadeIn = AnimateOpacity(_superImage, targetSuperOpacity, fadeAnimationMs);
        var haloChange = _haloImage != null ? AnimateOpacity(_haloImage, targetHaloOpacity, fadeAnimationMs) : Task.CompletedTask;
        await Task.WhenAll(superFadeIn, haloChange);

        await Task.Delay((int)flashDuration);

        var superFadeOut = AnimateOpacity(_superImage, 0.0, fadeAnimationMs);
        var haloNormal = _haloImage != null ? AnimateOpacity(_haloImage, 0.175, fadeAnimationMs) : Task.CompletedTask;
        await Task.WhenAll(superFadeOut, haloNormal);
    }

    private (string on, string off, string super, string halo) ResolveImagePaths()
    {
        var today = DateTime.Today;
        string specialName = GetSpecialOccasionName(today);

        if (specialName != null)
        {
            // Special occasion - use themed texture set
            string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "special");
            return (
                Path.Combine(baseDir, $"{specialName}.on.png"),
                Path.Combine(baseDir, $"{specialName}.off.png"),
                Path.Combine(baseDir, $"{specialName}.super.png"),
                Path.Combine(baseDir, $"{specialName}.halo.png")
            );
        }

        // Default texture set
        if (_context == LampContext.Splash)
        {
            return (
                "ms-appx:///Assets/icons/SplashScreen.Regular.png",
                "ms-appx:///Assets/icons/SplashScreen.Off.png",
                "ms-appx:///Assets/icons/SplashScreen.Super.png",
                "ms-appx:///Assets/icons/SplashScreen.Halo.png"
            );
        }
        else // Titlebar
        {
            return (
                Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.small.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.off.small.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.super.small.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.halo.png")
            );
        }
    }

    private string GetSpecialOccasionName(DateTime date)
    {
        // Birthdays
        if (date.Month == 4 && date.Day >= 21 && date.Day <= 23)
            return "birthday";

        // Christmas
        if ((date.Month == 12 && date.Day >= 23) || (date.Month == 1 && date.Day <= 7))
            return "christmas";

        // October Weekends
        if (date.Month == 10 && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
            return "pumpkin";

        return null;
    }

    private async Task PreloadImages()
    {
        await Task.WhenAll(
            GetCachedImageAsync(_onPath),
            GetCachedImageAsync(_offPath),
            GetCachedImageAsync(_superPath),
            GetCachedImageAsync(_haloPath)
        );
    }

    private async Task SetupSteadyOnState(double fadeMs)
    {
        await SetImageAsync(_baseImage, _onPath);
        _baseImage.Opacity = 1.0;

        if (_overlayImage != null) _overlayImage.Opacity = 0;
        if (_superImage != null) _superImage.Opacity = 0;
        if (_haloImage != null) await AnimateOpacity(_haloImage, 0.25, fadeMs);
    }

    private async Task ExecuteSingleFlash(Random random, double flashChance, double fadeMs)
    {
        if (_overlayImage == null)
            return;

        await SetImageAsync(_baseImage, _onPath);
        await SetImageAsync(_overlayImage, _offPath);

        bool doSuperFlash = random.NextDouble() < flashChance;

        if (doSuperFlash)
        {
            await SetImageAsync(_baseImage, _superPath);
            _overlayImage.Opacity = 0;

            var baseTask = AnimateOpacity(_baseImage, 1.0, fadeMs);
            var haloTask = _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask;
            await Task.WhenAll(baseTask, haloTask);

            await Task.Delay(random.Next(300, 800));
        }
        else
        {
            _baseImage.Opacity = 1.0;
            _overlayImage.Opacity = 0.0;

            var overlayTask = AnimateOpacity(_overlayImage, 1.0, fadeMs);
            var haloTask = _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs) : Task.CompletedTask;
            await Task.WhenAll(overlayTask, haloTask);

            await Task.Delay(random.Next(300, 800));
        }

        await SetImageAsync(_baseImage, _onPath);
        _overlayImage.Opacity = 0.0;
        _baseImage.Opacity = 1.0;
        if (_haloImage != null) await AnimateOpacity(_haloImage, 0.25, fadeMs);
    }

    private async Task BlinkLoop(
        CancellationToken token,
        double fadeMs,
        double initialDelayMs,
        double minDelayMs,
        double minRampSec,
        double maxRampSec,
        Random random)
    {
        if (_overlayImage == null)
            return;

        try
        {
            await SetImageAsync(_baseImage, _onPath);
            await SetImageAsync(_overlayImage, _offPath);

            _baseImage.Opacity = 1.0;
            _overlayImage.Opacity = 0.0;

            bool state = true;
            double phaseTime = 0;
            bool rampingUp = true;
            double currentRampDuration = GetRandomRampDuration(random, minRampSec, maxRampSec);
            var rampStartTime = DateTime.UtcNow;
            var nextSuperFlash = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // Super flash logic
                if (now >= nextSuperFlash)
                {
                    bool isRapidFlash = random.NextDouble() < 0.20;

                    if (isRapidFlash)
                    {
                        var flashCount = random.Next(1, 6);
                        var flashSpeed = random.Next(50, 100);

                        for (int i = 0; i < flashCount; i++)
                        {
                            await SetImageAsync(_baseImage, _superPath);
                            _overlayImage.Opacity = 0;
                            if (_haloImage != null) _ = AnimateOpacity(_haloImage, 0.6, fadeMs);
                            await Task.Delay(75, token);

                            await SetImageAsync(_baseImage, _onPath);
                            if (_haloImage != null) _ = AnimateOpacity(_haloImage, 0.5, fadeMs);
                            await Task.Delay(flashSpeed, token);
                        }
                    }
                    else
                    {
                        var superFlashDuration = random.Next(300, 1500);

                        await SetImageAsync(_baseImage, _superPath);
                        _overlayImage.Opacity = 0;

                        var superBaseTask = AnimateOpacity(_baseImage, 1.0, fadeMs);
                        var superHaloTask = _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask;
                        await Task.WhenAll(superBaseTask, superHaloTask);

                        await Task.Delay(superFlashDuration, token);
                    }

                    await SetImageAsync(_baseImage, _onPath);
                    await SetImageAsync(_overlayImage, _offPath);

                    var resetHaloTask = _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs) : Task.CompletedTask;
                    var resetOverlayTask = AnimateOpacity(_overlayImage, 1.0, fadeMs);
                    await Task.WhenAll(resetOverlayTask, resetHaloTask);

                    state = false;
                    rampingUp = true;
                    currentRampDuration = GetRandomRampDuration(random, minRampSec, maxRampSec);
                    rampStartTime = DateTime.UtcNow;
                    nextSuperFlash = DateTime.UtcNow.AddSeconds(random.NextDouble() * 5 + 4);
                    continue;
                }

                phaseTime = (now - rampStartTime).TotalSeconds;
                double progress = Math.Clamp(phaseTime / currentRampDuration, 0, 1);
                double eased = EaseInOut(progress);

                double delay = rampingUp
                    ? initialDelayMs - (initialDelayMs - minDelayMs) * eased
                    : minDelayMs + (initialDelayMs - minDelayMs) * eased;

                double overlayOpacity = state ? 0.0 : 1.0;
                double normalHaloOpacity = state ? 0.5 : 0.025;

                var overlayTask = AnimateOpacity(_overlayImage, overlayOpacity, fadeMs);
                var normalHaloTask = _haloImage != null ? AnimateOpacity(_haloImage, normalHaloOpacity, fadeMs) : Task.CompletedTask;
                await Task.WhenAll(overlayTask, normalHaloTask);

                state = !state;

                if (progress >= 1.0)
                {
                    rampingUp = !rampingUp;
                    rampStartTime = DateTime.UtcNow;
                    currentRampDuration = GetRandomRampDuration(random, minRampSec, maxRampSec);
                }

                await Task.Delay((int)delay, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            try
            {
                if (!token.IsCancellationRequested)
                {
                    await PerformFinalSuperFlash(random.Next(450, 900), fadeMs);
                }
                else
                {
                    await PerformFinalSuperFlash(400, fadeMs);
                }
            }
            catch { }

            await SetupSteadyOnState(fadeMs);
            _isAnimating = false; // CRITICAL: Only clear flag when truly done
        }
    }

    private async Task PerformFinalSuperFlash(int duration, double fadeMs)
    {
        await SetImageAsync(_baseImage, _superPath);
        if (_overlayImage != null) _overlayImage.Opacity = 0;

        var superBaseTask = AnimateOpacity(_baseImage, 1.0, fadeMs);
        var superHaloTask = _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask;
        await Task.WhenAll(superBaseTask, superHaloTask);

        await Task.Delay(duration, CancellationToken.None);
    }

    private double GetRandomRampDuration(Random random, double min, double max)
        => random.NextDouble() * (max - min) + min;

    private double EaseInOut(double t)
        => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

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
        imageControl.Source = await GetCachedImageAsync(path);
    }

    private async Task AnimateOpacity(
        FrameworkElement element,
        double targetOpacity,
        double durationMs,
        CancellationToken ct = default)
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

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, ct));

        if (ct.IsCancellationRequested)
        {
            storyboard.Stop();
            element.Opacity = targetOpacity;
        }
    }
}
