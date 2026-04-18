using System;
using System;
using System.Diagnostics;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.Modules.Alchitex;

// Potentially rename to Alchemist, PBR Alchemist or RTX Reactor or ARCHITEX or ALCHETEX before release.

// Perfect the licensing windows' appearance

// Review: is it a good idea to limit features lifecycle to their windows? In general... should it all ahve been on the main window?
// well, you see, in your case, navigation view would've been very generic
// and some modules like alchitex might become too heavy, so yes, making the main window act like a nexus hub that spawns child apps is better...
// they have minimal communication/interactions, its like main window is a father responsible for them with all of the logs n things
// navigation view is also nice... think about it, just think, u love the way your buttons look, don't want them to go!

// REDSTONE ELEMENT IMPLEMENTAITON IDEA:
// We got the tile backgrounds
// Beneath there, have PROCEDURALLY GENERATED redstone going Upward from below, that makes 2 layers of bitmaps!
// still do it like u had in mind, tiles exist, images are dynamically selected based on neighbors
// Then, have a toggle, like the lamp, to either trigger random flashes, or continous random power flashes in the redstone
// A nice way to convey something being done in the background!
// This is the way, and is actually imeplementable, unlike earlier versions of the idea. (how were to understand which areas are... to trigger)
// it isn't too convoluted, and is gonna look AMAZING.


public static class AlchitexVariables
{

    public static class Persistent
    {

    }
    public static void SaveSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = field.GetValue(null);
            localSettings.Values[field.Name] = value;
        }
    }
    public static void LoadSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            try
            {
                if (localSettings.Values.ContainsKey(field.Name))
                {
                    var savedValue = localSettings.Values[field.Name];
                    var convertedValue = Convert.ChangeType(savedValue, field.FieldType);
                    field.SetValue(null, convertedValue);
                }
            }
            catch
            {
                Trace.WriteLine($"An issue occured loading settings");
            }
        }
    }
}



public sealed partial class Alchitex : Window
{
    private RedstoneFramework? _redstone;
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    // ── Lamp animators ───────────────────────────────────────────────────────
    // One instance drives the shared titlebar lamp; a second drives the splash.
    private LampAnimator? _titlebarLogoAnimator;
    private LampAnimator? _splashLogoAnimator;

    // ── Version-keyed keys ───────────────────────────────────────────────────
    // In DEBUG builds a fresh GUID is appended so the license + splash sequence
    // always re-runs, letting you test the full flow without clearing app data.
    private static string LicenseAcceptedKey =>
#if DEBUG
        $"Alchitex_LicenseAccepted_{GetAppVersion()}_{Guid.NewGuid()}";
#else
        $"TEMPORARY_LICENSEWINDOW_{GetAppVersion()}_{Guid.NewGuid()}";
        // $"Alchitex_LicenseAccepted_{GetAppVersion()}";
#endif

    private static string GetAppVersion()
    {
        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }

    // ── Constructor ──────────────────────────────────────────────────────────
    public Alchitex(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

        // Theme — identical pattern to DLSSSwitcherWindow
        var mode = TunerVariables.Persistent.AppThemeMode ?? "System";
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        // AppWindow / presenter
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            var dpi = MainWindow.GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(WindowMinSizeY * scaleFactor);
        }

        // Title bar
        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(139, 139, 139, 139);
            _appWindow.TitleBar.InactiveForegroundColor = ColorHelper.FromArgb(128, 139, 139, 139);
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        // Initialise lamp animators (images are already in the visual tree)
        InitializeLampAnimators();

        this.Activated += Alchitex_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    // ── Lamp animator setup ──────────────────────────────────────────────────
    private void InitializeLampAnimators()
    {
        const string assetBase = "ms-appx:///Modules/Alchitex/Assets/";

        _titlebarLogoAnimator = new LampAnimator(
            context: LampAnimator.LampContext.Titlebar,
            baseImage: AlchitexIconOn,
            onPath: assetBase + "splash.on.png",
            offPath: assetBase + "splash.off.png",
            superPath: assetBase + "splash.super.png",
            haloPath: assetBase + "splash.halo.png",
            overlayImage: AlchitexIconOff,
            haloImage: AlchitexIconHalo,
            superImage: AlchitexIconSuper
        );

        _splashLogoAnimator = new LampAnimator(
            context: LampAnimator.LampContext.Splash,
            baseImage: SplashLogo,
            onPath: assetBase + "logo.on.png",
            offPath: assetBase + "logo.off.png",
            superPath: assetBase + "logo.super.png",
            haloPath: assetBase + "logo.halo.png",
            overlayImage: SplashLogoOff,
            haloImage: SplashLogoHalo,
            superImage: SplashLogoSuper
        );
    }

    // ── Public helper — lets callers blink the titlebar lamp ─────────────────
    public async Task BlinkingLamp(bool enable, bool singleFlash = false,
                                   double singleFlashOnChance = 0.75)
    {
        if (_titlebarLogoAnimator is null) return;
        await _titlebarLogoAnimator.Animate(enable, singleFlash, singleFlashOnChance, rotate:true);
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────
    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void Alchitex_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= Alchitex_Activated;

            _ = this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                SetTitleBarDragRegion);

            // Initialise both lamp animators before showing any UI
            if (_titlebarLogoAnimator is not null && _splashLogoAnimator is not null)
            {
                await Task.WhenAll(
                    _titlebarLogoAnimator.InitializeAsync(),
                    _splashLogoAnimator.InitializeAsync()
                );
            }

            await InitializeAsync();

            _ = this.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(75);
                try { this.Activate(); } catch { }
            });
        }
    }

    private void SetTitleBarDragRegion()
    {
        try
        {
            if (_appWindow.TitleBar == null) return;
            // Drag target is swapped between TitleBarDragAreaFull and
            // TitleBarDragAreaNarrow in code. The active one is set via SetTitleBar().
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error setting drag region: {ex.Message}");
        }
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    private async Task InitializeAsync()
    {
        try
        {
            bool accepted = await CheckLicenseAcceptedAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;

            if (!accepted)
            {
                // Show license first (titlebar always visible above it)
                await PopulateLicenseTextAsync();
                InitializeLicenseShadows();
                LicensePanel.Visibility = Visibility.Visible;
                SetTitleBar(TitleBarDragAreaFull);

                // Splash plays on top while the license content loads in behind it —
                // good cover for the 8K background image on slower systems.
                await ShowSplashAsync();
            }
            else
            {
                // License already accepted — splash still plays (always), then main.
                await ShowSplashAsync();
                ShowMainContent();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"EXCEPTION in Alchitex.InitializeAsync: {ex}");
            this.Close();
        }
    }

    // ── Splash sequence ───────────────────────────────────────────────────────
    private async Task ShowSplashAsync()
    {
        SplashOverlay.Visibility = Visibility.Visible;

        // Run the lamp blink animation — singleFlash, 1.0 chance (always flashes On)
        if (_splashLogoAnimator is not null)
        {
            await _splashLogoAnimator.Animate(
                enable: false,
                singleFlash: true,
                singleFlashOnChance: 1.0,
                duration: 375,
                rotate:true
            );
        }

        // Fade out the splash overlay
        await FadeOutElementAsync(SplashOverlay, durationMs: 250);
        SplashOverlay.Visibility = Visibility.Collapsed;
    }

    private Task FadeOutElementAsync(UIElement element, double durationMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(fadeOut, element);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        storyboard.Children.Add(fadeOut);
        storyboard.Completed += (_, _) => tcs.SetResult(true);
        storyboard.Begin();
        return tcs.Task;
    }

    // ── License + splash persistence ─────────────────────────────────────────
    private static Task<bool> CheckLicenseAcceptedAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            var val = settings.Values[LicenseAcceptedKey];
            return Task.FromResult(val is bool b && b);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error reading license key: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private static Task SetLicenseAcceptedAsync()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[LicenseAcceptedKey] = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error writing license key: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // ── Populate license RichTextBlock ────────────────────────────────────────
    private async Task PopulateLicenseTextAsync()
    {
        try
        {
            var uri = new Uri("ms-appx:///Modules/Alchitex/ALCHITEX_LICENSE.txt");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            var body = await FileIO.ReadTextAsync(file);

            LicenseTextBlock.Blocks.Clear();

            // ── "Online version" header ──────────────────────────────────────
            var headerPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 2) };
            headerPara.Inlines.Add(new Run { Text = "Online version:  " });
            var link = new Hyperlink
            {
                NavigateUri = new Uri("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/src/Modules/Alchitex/ALCHITEX_LICENSE.txt")
            };
            link.Inlines.Add(new Run { Text = "View on GitHub" });
            headerPara.Inlines.Add(link);
            LicenseTextBlock.Blocks.Add(headerPara);

            // ── Separator ────────────────────────────────────────────────────
            var sepPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 8) };
            sepPara.Inlines.Add(new Run
            {
                Text = "───────────────────────────────────────",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            LicenseTextBlock.Blocks.Add(sepPara);

            // ── Body ─────────────────────────────────────────────────────────
            var bodyPara = new Paragraph();
            bodyPara.Inlines.Add(new Run { Text = body });
            LicenseTextBlock.Blocks.Add(bodyPara);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading license text: {ex.Message}");
            LicenseTextBlock.Blocks.Clear();
            var err = new Paragraph();
            err.Inlines.Add(new Run { Text = $"Could not load license file: {ex.Message}" });
            LicenseTextBlock.Blocks.Add(err);
        }
    }

    // ── Shadows ───────────────────────────────────────────────────────────────
    private void InitializeTitleBarShadow()
    {
        TitleBarShadow.Receivers.Add(TitleBarShadowReceiver);
    }

    private void InitializeLicenseShadows()
    {
        LicenseTextShadow.Receivers.Add(LicenseShadowReceiver);
        DisagreeShadow.Receivers.Add(LicenseShadowReceiver);
        AgreeShadow.Receivers.Add(LicenseShadowReceiver);
    }

    // ── Button handlers ───────────────────────────────────────────────────────
    private void DisagreeButton_Click(object sender, RoutedEventArgs e)
    {
        // No key written — gate will reappear on next launch
        this.Close();
    }

    private async void AgreeButton_Click(object sender, RoutedEventArgs e)
    {
        await SetLicenseAcceptedAsync();
        LicensePanel.Visibility = Visibility.Collapsed;
        ShowMainContent();

        // Celebrate with a single titlebar lamp flash (always On)
        await BlinkingLamp(enable: false, singleFlash: true, singleFlashOnChance: 1.0);
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.OpenUrl("http://minecraftrtx.net/reactor");
    }

    // ── Reveal main content ───────────────────────────────────────────────────

    // Main content are hidden before license is accepted, i.e. redstone circuits and others
    private async void ShowMainContent()
    {
        this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        TitleBarText.Text = "Alchitex";
        InfoButton.Visibility = Visibility.Visible;
        TitleBarDragAreaNarrow.Visibility = Visibility.Visible;
        SetTitleBar(TitleBarDragAreaNarrow);
        InitializeTitleBarShadow();
        MainGrid.Visibility = Visibility.Visible;

        _redstone = new RedstoneFramework(RedstoneLayer,
                                          Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        await _redstone.InitializeAsync(_appWindow.Size.Width, _appWindow.Size.Height);
    }

    private async void LogoInteractButton_Click(object sender, RoutedEventArgs e)
    {
        _ = BlinkingLamp(true, true, 1.0);
        _redstone.StopContinuousFlashing();
    }

    private async void Window_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (_redstone is null) return;
        await _redstone.RegenerateAsync(args.Size.Width, args.Size.Height);
    }

    private void AnnouncementButton_Click(object sender, RoutedEventArgs e)
    {
        _ = BlinkingLamp(true, true, 1.0);
        _redstone.StartContinuousFlashing();
    }
}
