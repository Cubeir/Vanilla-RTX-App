using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Xaml.Input;
using static Vanilla_RTX_App.Core.WindowControlsManager;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;

namespace Vanilla_RTX_App;

/*
### GENERAL TODO & IDEAS ###

- Add a proper, non-intrusive leave a review prompt

- Get copilot to examine the entire codebase for raw string path manipulation where better methods could've been used
(note: it crashes, feed it file by file)

- Somehow fix window maximizing when clicking titlebar buttons, they should absorb it but they dont.. window gets it too

- Unify the 4 places hardcoded paths are used into a class
pack updater, pack locator, pack browser, launcher, they deal with hardcoded paths, what else? (Ask copilot to scry the code)

- Expose as many params as you can to a json in app's root
the URLs the app sends requests to + the hardcoded Minecraft paths
* Resource packs only end up in shared
* Options file is in both shared and non-shared, but non-shared is presumably the one that takes priority, still, we take care of both
* PackLocator, PackUpdater (deployer), Browse Packs, and LaunchMinecraftRTX's options.txt updater are the only things that rely on hardcoded paths on the system

Additionally, while going through params, 
Check your github usage patterns (caching, and cooldowns) -- especially updater, maximize up-to-dateness with as few requests as possible
All settled there? ensure there isn't a way the app can ddos github


- Do the TODO and ISSUES scattered in the code
Finish all that you had postponed

- What if, you expanded Butcher heightmaps into Butcher Normals
Then had it generate simple seamless normal maps as well and blend them?
That way all sliders can affect all packs in some shape or form, which is good.

- With splash screen here, UpdateUI is useless, getting rid of it is too much work though, just too much...
It is too integerated, previewer class has some funky behavior tied to it, circumvented by it
It's a mess but it works perfectly, so, only fix it once you have an abundance of time...!

In fact, manually calling UpdateUI is NECESSERY, thank GOD you're not using bindings
UpdateUI is VERY NEEDED for Previewer class, it is already implemented everywhere and freezes vessel updates as necessery
You would've had to manually done this anyway

And the smooth transitions are worth it.



- A cool "Gradual logger" -- log texts gradually but very quickly! It helps make it less overwhelming when dumping huge logs
Besides that you're gonna need something to unify the logging
A public variable that gets all text dumped to perhaps, and gradually writes out its contents to sidebarlog whenever it is changed, async
This way direct interaction with non-UI threads will be zero
Long running tasks dump their text, UI thread gradually writes it out on its own.
only concern is performance with large logs
This idea can be a public static method and it won't ever ever block Ui thread
A variable is getting constantly updated with new logs, a worker in main UI thread's only job is to write out its content as it comes along

^ yeah lets dedicate more code clutter to visual things

- Set random preview arts on startup, featuring locations from Vanilla RTX's history
Or simple pixel arts you'd like to make in the same style
Have 5-10 made

- Tuner could, in theory, use the MANIFEST.JSON's metadata (i.e. TOOLS USED) to MARK packs
e.g. you can preserve their tuning histories there, embed it into the manifest, like for ambient lighting toggle

- Account for different font scalings, windows accessibility settings, etc...
gonna need lots of painstakingly redoing xamls but if one day you have an abundance of time sure why not


*/

public static class TunerVariables
{
    public static string? appVersion = null;

    public static string VanillaRTXLocation = string.Empty;
    public static string VanillaRTXNormalsLocation = string.Empty;
    public static string VanillaRTXOpusLocation = string.Empty;
    public static string CustomPackLocation = string.Empty;

    public static string VanillaRTXVersion = string.Empty;
    public static string VanillaRTXNormalsVersion = string.Empty;
    public static string VanillaRTXOpusVersion = string.Empty;
    public static string CustomPackDisplayName = string.Empty;
    // We already know names of Vanilla RTX packs so we get version instead, for custom pack, name's enough.
    // We invalidate the retrieved name whenever we want to disable processing of the custom pack, so it has multiple purposes

    // Tied to checkboxes
    public static bool IsVanillaRTXEnabled = false;
    public static bool IsNormalsEnabled = false;
    public static bool IsOpusEnabled = false;

    public static string HaveDeployableCache = "";

    // These variables are saved and loaded, they persist
    public static class Persistent
    {
        public static bool IsTargetingPreview = Defaults.IsTargetingPreview;

        public static string? MinecraftInstallPath = null;
        public static string? MinecraftPreviewInstallPath = null;

        public static double FogMultiplier = Defaults.FogMultiplier;
        public static double EmissivityMultiplier = Defaults.EmissivityMultiplier;
        public static int NormalIntensity = Defaults.NormalIntensity;
        public static int MaterialNoiseOffset = Defaults.MaterialNoiseOffset;
        public static int RoughenUpIntensity = Defaults.RoughenUpIntensity;
        public static int ButcheredHeightmapAlpha = Defaults.ButcheredHeightmapAlpha;
        public static bool AddEmissivityAmbientLight = Defaults.AddEmissivityAmbientLight;

        public static string AppThemeMode = "Dark";
    }

    // Defaults are backed up to be used as a compass by other classes
    public static class Defaults
    {
        public const bool IsTargetingPreview = false;
        public const double FogMultiplier = 1.0;
        public const double EmissivityMultiplier = 1.0;
        public const int NormalIntensity = 100;
        public const int MaterialNoiseOffset = 0;
        public const int RoughenUpIntensity = 0;
        public const int ButcheredHeightmapAlpha = 0;
        public const bool AddEmissivityAmbientLight = false;
    }

    // Saves persistent variables
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

    // Loads persitent variables
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
                Debug.WriteLine($"An issue occured loading settings");
            }
        }
    }
}

// ---------------------------------------\                /-------------------------------------------- \\

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly WindowStateManager _windowStateManager;

    private readonly ProgressBarManager _progressManager;

    private readonly PackUpdater _updater = new();

    private static CancellationTokenSource? _lampBlinkCts;
    private static readonly Dictionary<string, BitmapImage> _imageCache = new();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    private int _mojankClickCount = 0;
    private DateTime _mojankLastClick = DateTime.MinValue;


    public MainWindow()
    {
        // Properties to set before it is rendered
        SetMainWindowProperties();
        InitializeComponent();

        // Show splash screen immedietly
        if (SplashOverlay != null)
        {
            SplashOverlay.Visibility = Visibility.Visible;
        }

        _windowStateManager = new WindowStateManager(this, false, msg => Log(msg));
        _progressManager = new ProgressBarManager(ProgressBar);

        Instance = this;

        var defaultSize = new SizeInt32(1055, 555);
        _windowStateManager.ApplySavedStateOrDefaults();

        // Version, title and initial logs
        var version = Windows.ApplicationModel.Package.Current.Id.Version;
        var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        appVersion = versionString;
        Log($"App Version: {versionString}" + new string('\n', 2) +
             "Not affiliated with Mojang Studios or NVIDIA;\nby continuing, you consent to modifications to your Minecraft data folders.");

        // Do upon app closure
        this.Closed += (s, e) =>
        {
            SaveSettings();
            App.CleanupMutex();
        };

        // Things to do after mainwindow is initialized
        this.Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        // Unsubscribe to avoid running this again
        this.Activated -= MainWindow_Activated;

        // Give the window time to render for the first time
        // If one day something goes on the background that needs waiting, increase this, it delays the flash
        await Task.Delay(50);

        // RTX shaders omg
        InitializeShadows();

        // Splash Blinking Animation
        _ = AnimateSplash(450);

        Previewer.Initialize(PreviewVesselTop, PreviewVesselBottom, PreviewVesselBackground);
        LoadSettings();

        // APPLY THEME if it isn't a button click they won't cycle and apply the loaded setting instead
        CycleThemeButton_Click(null, null);


        // Set reinstall latest packs button visuals based on cache status
        if (_updater.HasDeployableCache())
        {
            UpdateVanillaRTXGlyph.Glyph = "\uE8F7"; // Syncfolder icon
            UpdateVanillaRTXButtonText.Text = "Reinstall latest RTX packs";
            HaveDeployableCache = "Reinstallation";
        }
        else
        {
            UpdateVanillaRTXGlyph.Glyph = "\uEBD3"; // Default cloud icon
            UpdateVanillaRTXButtonText.Text = "Install latest RTX packages";
            HaveDeployableCache = "Installation";
        }

        // Brief delay to ensure everything is fully rendered, then fade out splash screen
        await Task.Delay(1000);
        // ================ Do all UI updates you DON'T want to be seen BEFORE here, and what you want seen AFTER ======================= 
        await FadeOutSplash();


        // Slower UI update override for a smoother startup
        UpdateUI(0.5);

        // Locate packs, if Preview is enabled, TargetPreview triggers another pack location, avoid redundant operation
        if (!IsTargetingPreview)
        {
            _ = LocatePacksButton_Click();
        }

        // lazy credits and PSA retriever, credits are saved for donate hover event, PSA is shown when ready
        _ = CreditsUpdater.GetCredits(false);
        _ = Task.Run(async () =>
        {
            var psa = await PSAUpdater.GetPSAAsync();
            if (!string.IsNullOrWhiteSpace(psa))
            {
                Log(psa, LogLevel.Informational);
            }
        });
        // Warning if MC is running
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            var buttonName = LaunchButtonText.Text;
            Log($"Please close Minecraft while using the app, when finished, launch the game using {buttonName} button.", LogLevel.Warning);
        }

        // Calling it last since it might add a bit of delay
        MinecraftGDKLocator.ValidateAndUpdateCachedLocations();

        async Task FadeOutSplash()
        {
            if (SplashOverlay == null) return;

            var fadeOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(fadeOut, SplashOverlay);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            storyboard.Children.Add(fadeOut);

            var tcs = new TaskCompletionSource<bool>();
            storyboard.Completed += (s, e) =>
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
                tcs.SetResult(true);
            };

            storyboard.Begin();
            await tcs.Task;
        }
    }


    #region Main Window properties and essential components used throughout the app
    private void SetMainWindowProperties()
    {
        ExtendsContentIntoTitleBar = true;
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;

            var dpi = GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(970 * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(555 * scaleFactor);
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.ico");
        appWindow.SetTaskbarIcon(iconPath);
        appWindow.SetTitleBarIcon(iconPath);

        // Watches theme changes and adjusts based on theme
        // use only for stuff that can be altered before mainwindow initlization
        ThemeWatcher(this, theme =>
        {
            var titleBar = appWindow.TitleBar;
            if (titleBar == null) return;

            bool isLight = theme == ElementTheme.Light;

            titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonPressedForegroundColor = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonInactiveForegroundColor = isLight
                ? Color.FromArgb(255, 100, 100, 100)
                : Color.FromArgb(255, 160, 160, 160);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = isLight
                ? Color.FromArgb(20, 0, 0, 0)
                : Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = isLight
                ? Color.FromArgb(40, 0, 0, 0)
                : Color.FromArgb(60, 255, 255, 255);

            // Color of that little border next to the button 🍝
            if (IsTargetingPreview)
            {
                LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColorLight3"]);
            }
            else
            {
                var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
                var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
                if (themeDictionaries.TryGetValue(themeKey, out var themeDict) && themeDict is ResourceDictionary dict)
                {
                    if (dict.TryGetValue("FakeSplitButtonBrightBorderColor", out var colorObj) && colorObj is Color color)
                    {
                        LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(color);
                    }
                }
            }
        });


    }
    public static void ThemeWatcher(Window window, Action<ElementTheme> onThemeChanged)
    {
        void HookThemeChangeListener()
        {
            if (window.Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, __) =>
                {
                    onThemeChanged(root.ActualTheme);
                };

                // also call once now
                onThemeChanged(root.ActualTheme);
            }
        }

        // Safe way to defer until content is ready
        window.Activated += (_, __) =>
        {
            HookThemeChangeListener();
        };
    }


    public void CycleThemeButton_Click(object? sender, RoutedEventArgs? e)
    {
        bool invokedByClick = sender is Button;
        string mode = TunerVariables.Persistent.AppThemeMode;

        if (invokedByClick)
        {
            mode = mode switch
            {
                "System" => "Light",
                "Light" => "Dark",
                _ => "System"
            };
            TunerVariables.Persistent.AppThemeMode = mode;
        }

        var root = MainWindow.Instance.Content as FrameworkElement;
        root.RequestedTheme = mode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        Button btn = (sender as Button) ?? CycleThemeButton;

        // Visual Feedback
        if (mode == "System")
        {
            btn.Content = new TextBlock
            {
                Text = "A",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 15
            };
        }
        else
        {
            btn.Content = mode switch
            {
                "Light" => "\uE793",
                "Dark" => "\uE706",
                _ => "A",
            };
        }

        ToolTipService.SetToolTip(btn, "Theme: " + mode);
    }


    private void SetPreviews()
    {
        Previewer.Instance.InitializeSlider(FogMultiplierSlider,
            "ms-appx:///Assets/previews/fog.default.png",
            "ms-appx:///Assets/previews/fog.min.png",
            "ms-appx:///Assets/previews/fog.max.png",
            Defaults.FogMultiplier
        );

        Previewer.Instance.InitializeSlider(EmissivityMultiplierSlider,
            "ms-appx:///Assets/previews/emissivity.default.png",
            "ms-appx:///Assets/previews/emissivity.min.png",
            "ms-appx:///Assets/previews/emissivity.max.png",
            Defaults.EmissivityMultiplier
        );

        Previewer.Instance.InitializeSlider(NormalIntensitySlider,
            "ms-appx:///Assets/previews/normals.default.png",
            "ms-appx:///Assets/previews/normals.flat.png",
            "ms-appx:///Assets/previews/normals.intense.png",
            Defaults.NormalIntensity
        );

        Previewer.Instance.InitializeSlider(RoughenUpSlider,
            "ms-appx:///Assets/previews/roughenup.default.png",
            "ms-appx:///Assets/previews/roughenup.default.png",
            "ms-appx:///Assets/previews/roughenup.rough.png",
            Defaults.RoughenUpIntensity
        );

        Previewer.Instance.InitializeSlider(MaterialNoiseSlider,
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/materials.grainy.png",
             Defaults.MaterialNoiseOffset
        );

        Previewer.Instance.InitializeSlider(ButcherHeightmapsSlider,
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.butchered.png",
            Defaults.ButcheredHeightmapAlpha
        );

        Previewer.Instance.InitializeToggleSwitch(EmissivityAmbientLightToggle,
            "ms-appx:///Assets/previews/emissivity.ambient.on.png",
            "ms-appx:///Assets/previews/emissivity.ambient.off.png"
        );

        Previewer.Instance.InitializeToggleButton(TargetPreviewToggle,
            "ms-appx:///Assets/previews/preview.overlay.png",
            "ms-appx:///Assets/previews/preview.png"
        );

        Previewer.Instance.InitializeCheckBox(VanillaRTXCheckBox,
            "ms-appx:///Assets/previews/checkbox.regular.ticked.png",
            "ms-appx:///Assets/previews/checkbox.regular.unticked.png"
        ); 
        Previewer.Instance.InitializeCheckBox(NormalsCheckBox,
            "ms-appx:///Assets/previews/checkbox.normals.ticked.png",
            "ms-appx:///Assets/previews/checkbox.normals.unticked.png"
        );
        Previewer.Instance.InitializeCheckBox(OpusCheckBox,
            "ms-appx:///Assets/previews/checkbox.opus.ticked.png",
            "ms-appx:///Assets/previews/checkbox.opus.unticked.png"
        );

        Previewer.Instance.InitializeButton(BrowsePacksButton,
            "ms-appx:///Assets/previews/locate.png"
        );

        Previewer.Instance.InitializeButton(ExportButton,
            "ms-appx:///Assets/previews/chest.export.png"
        );

        Previewer.Instance.InitializeButton(UpdateVanillaRTXButton,
            "ms-appx:///Assets/previews/repository.reinstall.png"
        );

        Previewer.Instance.InitializeButton(TuneSelectionButton,
            "ms-appx:///Assets/previews/table.tune.png"
        );

        Previewer.Instance.InitializeButton(LaunchButton,
            "ms-appx:///Assets/previews/minecart.launch.png"
        );

        Previewer.Instance.InitializeButton(CycleThemeButton,
            "ms-appx:///Assets/previews/theme.png"
        );

        Previewer.Instance.InitializeButton(DonateButton,
            "ms-appx:///Assets/previews/cubeir.thankyou.png"
        );

        Previewer.Instance.InitializeButton(HelpButton,
            "ms-appx:///Assets/previews/cubeir.help.png"
        );

        Previewer.Instance.InitializeButton(ResetButton,
            "ms-appx:///Assets/previews/table.reset.variables.png"
        );

        Previewer.Instance.InitializeButton(ClearButton,
            "ms-appx:///Assets/previews/table.reset.png"
        );

        Previewer.Instance.InitializeButton(BetterRTXPresetManagerButton,
            "ms-appx:///Assets/previews/brtx.png"
        );

        Previewer.Instance.InitializeButton(DLSSVersionSwitcherButton,
            "ms-appx:///Assets/previews/dlss.png"
        );

    }


    private void InitializeShadows()
    {
        TitleBarShadow.Receivers.Add(TitleBarShadowReceiver);

        // Left column shadows
        BrowsePacksShadow.Receivers.Add(LeftShadowReceiver);
        SidebarLogShadow.Receivers.Add(LeftShadowReceiver);
        CommandBarShadow.Receivers.Add(LeftShadowReceiver);

        // Right column shadows
        PackOptionsShadow.Receivers.Add(RightShadowReceiver);
        SlidersGridShadow.Receivers.Add(RightShadowReceiver);
        ClearResetShadow.Receivers.Add(RightShadowReceiver);
        BottomButtonsShadow.Receivers.Add(RightShadowReceiver);

        // Individual textbox shadows
        FogMultiplierBoxShadow.Receivers.Add(RightShadowReceiver);
        EmissivityMultiplierBoxShadow.Receivers.Add(RightShadowReceiver);
        NormalIntensityBoxShadow.Receivers.Add(RightShadowReceiver);
        MaterialNoiseBoxShadow.Receivers.Add(RightShadowReceiver);
        RoughenUpBoxShadow.Receivers.Add(RightShadowReceiver);
        ButcherHeightmapsBoxShadow.Receivers.Add(RightShadowReceiver);
    }


    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Debug
    }
    public static void Log(string message, LogLevel? level = null)
    {
        void Prepend()
        {
            var textBox = Instance.SidebarLog;

            string prefix = level switch
            {
                LogLevel.Success => "✅ ",
                LogLevel.Informational => "ℹ️ ",
                LogLevel.Warning => "⚠️ ",
                LogLevel.Error => "❌ ",
                LogLevel.Network => "🛜 ",
                LogLevel.Lengthy => "⏳ ",
                LogLevel.Debug => "🔍 ",
                _ => ""
            };

            string prefixedMessage = $"{prefix}{message}";
            string separator = "";

            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                textBox.Text = prefixedMessage + "\n";
            }
            else
            {
                var sb = new StringBuilder(prefixedMessage.Length + textBox.Text.Length + separator.Length + 2);
                sb.Append(prefixedMessage)
                  .Append('\n')
                  .Append(separator)
                  .Append('\n')
                  .Append(textBox.Text);
                textBox.Text = sb.ToString();
            }

            // Scroll to top
            textBox.UpdateLayout();
            var sv = GetScrollViewer(textBox);
            sv?.ChangeView(null, 0, null);
        }

        if (Instance.DispatcherQueue.HasThreadAccess)
            Prepend();
        else
            Instance.DispatcherQueue.TryEnqueue(Prepend);
    }
    public static ScrollViewer GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv)
                return sv;

            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }



    public static void OpenUrl(string url)
    {
#if DEBUG
        Log("OpenUrl is disabled in debug builds.", LogLevel.Informational);
        return;
#else
    try
    {
        if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            throw new ArgumentException("Malformed URL.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"Details: {ex.Message}", LogLevel.Informational);
        Log("Failed to open URL. Make sure you have a browser installed and associated with web links.", LogLevel.Warning); 
    }
#endif
    }



    public async Task BlinkingLamp(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75)
    {
        const double initialDelayMs = 900;
        const double minDelayMs = 150;
        const double minRampSec = 1;
        const double maxRampSec = 8;
        const double fadeAnimationMs = 75;

        var onPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.small.png");
        var superOnPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.super.small.png");
        var offPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.off.small.png");

        var today = DateTime.Today;
        string specialPath = null;
        bool isSpecial = false;

        if (today.Month == 4 && today.Day >= 21 && today.Day <= 23)
        {
            specialPath = Path.Combine(AppContext.BaseDirectory, "Assets", "special", "happybirthdayme.png");
            isSpecial = true;
        }
        else if (today.Month == 10 && (today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday))
        {
            onPath = Path.Combine(AppContext.BaseDirectory, "Assets", "special", "pumpkin.on.png");
            offPath = Path.Combine(AppContext.BaseDirectory, "Assets", "special", "pumpkin.off.png");
            superOnPath = Path.Combine(AppContext.BaseDirectory, "Assets", "special", "pumpkin.super.png");
            isSpecial = false;
        }
        else if (today.Month == 12 && today.Day >= 25)
        {
            specialPath = Path.Combine(AppContext.BaseDirectory, "Assets", "special", "gingerman.png");
            isSpecial = true;
        }

        var imagesToPreload = new List<string> { onPath, superOnPath, offPath };
        if (specialPath != null) imagesToPreload.Add(specialPath);

        await Task.WhenAll(imagesToPreload.Select(GetCachedImageAsync));

        var random = new Random();
        _lampBlinkCts?.Cancel();
        _lampBlinkCts = null;

        if (singleFlash)
        {
            await ExecuteSingleFlash(random, onPath, superOnPath, offPath);
            return;
        }

        if (enable)
        {
            _lampBlinkCts = new CancellationTokenSource();

            if (isSpecial && !string.IsNullOrEmpty(specialPath))
            {
                await SetImageAsync(iconImageBox, specialPath);
                iconOverlayImageBox.Opacity = 0;
                await AnimateOpacity(iconHaloImageBox, 0, fadeAnimationMs);
                return;
            }

            _ = BlinkLoop(_lampBlinkCts.Token, onPath, superOnPath, offPath);
        }
        else
        {
            await SetImageAsync(iconImageBox, onPath);
            iconOverlayImageBox.Opacity = 0;
            await AnimateOpacity(iconHaloImageBox, 0.25, fadeAnimationMs);
        }

        async Task ExecuteSingleFlash(Random rng, string onPath, string superOnPath, string offPath)
        {
            await SetImageAsync(iconImageBox, onPath);
            await SetImageAsync(iconOverlayImageBox, offPath);

            bool doSuperFlash = rng.NextDouble() < singleFlashOnChance;

            if (doSuperFlash)
            {
                await SetImageAsync(iconImageBox, superOnPath);
                iconOverlayImageBox.Opacity = 0;

                var superBaseTask = AnimateOpacity(iconImageBox, 1.0, fadeAnimationMs);
                var superHaloTask = AnimateOpacity(iconHaloImageBox, 0.6, fadeAnimationMs);
                await Task.WhenAll(superBaseTask, superHaloTask);

                await Task.Delay(rng.Next(300, 800));
            }
            else
            {
                iconImageBox.Opacity = 1.0;
                iconOverlayImageBox.Opacity = 0.0;

                var overlayTask = AnimateOpacity(iconOverlayImageBox, 1.0, fadeAnimationMs);
                var haloTask = AnimateOpacity(iconHaloImageBox, 0.025, fadeAnimationMs);
                await Task.WhenAll(overlayTask, haloTask);

                await Task.Delay(rng.Next(300, 800));
            }

            await SetImageAsync(iconImageBox, onPath);
            iconOverlayImageBox.Opacity = 0.0;
            iconImageBox.Opacity = 1.0;
            await AnimateOpacity(iconHaloImageBox, 0.25, fadeAnimationMs);
        }

        async Task BlinkLoop(CancellationToken token, string onPath, string superOnPath, string offPath)
        {
            try
            {
                await SetImageAsync(iconImageBox, onPath);
                await SetImageAsync(iconOverlayImageBox, offPath);

                iconImageBox.Opacity = 1.0;
                iconOverlayImageBox.Opacity = 0.0;

                bool state = true;
                double phaseTime = 0;
                bool rampingUp = true;
                double currentRampDuration = GetRandomRampDuration();
                var rampStartTime = DateTime.UtcNow;
                var nextSuperFlash = DateTime.UtcNow;

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

                            for (int i = 0; i < flashCount; i++)
                            {
                                await SetImageAsync(iconImageBox, superOnPath);
                                iconOverlayImageBox.Opacity = 0;
                                var superTask = AnimateOpacity(iconHaloImageBox, 0.6, fadeAnimationMs);
                                await Task.Delay(75, token);

                                await SetImageAsync(iconImageBox, onPath);
                                var normalTask = AnimateOpacity(iconHaloImageBox, 0.5, fadeAnimationMs);
                                await Task.Delay(flashSpeed, token);
                            }
                        }
                        else
                        {
                            var superFlashDuration = random.Next(300, 1500);

                            await SetImageAsync(iconImageBox, superOnPath);
                            iconOverlayImageBox.Opacity = 0;

                            var superBaseTask = AnimateOpacity(iconImageBox, 1.0, fadeAnimationMs);
                            var superHaloTask = AnimateOpacity(iconHaloImageBox, 0.6, fadeAnimationMs);
                            await Task.WhenAll(superBaseTask, superHaloTask);

                            await Task.Delay(superFlashDuration, token);
                        }

                        await SetImageAsync(iconImageBox, onPath);
                        await SetImageAsync(iconOverlayImageBox, offPath);

                        var resetHaloTask = AnimateOpacity(iconHaloImageBox, 0.025, fadeAnimationMs);
                        var resetOverlayTask = AnimateOpacity(iconOverlayImageBox, 1.0, fadeAnimationMs);

                        await Task.WhenAll(resetOverlayTask, resetHaloTask);

                        state = false;
                        rampingUp = true;
                        currentRampDuration = GetRandomRampDuration();
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

                    var overlayTask = AnimateOpacity(iconOverlayImageBox, overlayOpacity, fadeAnimationMs);
                    var normalHaloTask = AnimateOpacity(iconHaloImageBox, normalHaloOpacity, fadeAnimationMs);

                    await Task.WhenAll(overlayTask, normalHaloTask);

                    state = !state;

                    if (progress >= 1.0)
                    {
                        rampingUp = !rampingUp;
                        rampStartTime = DateTime.UtcNow;
                        currentRampDuration = GetRandomRampDuration();
                    }

                    await Task.Delay((int)delay, token);
                }
            }
            finally
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        await PerformFinalSuperFlash(onPath, superOnPath, random.Next(450, 900));
                    }
                    else
                    {
                        await PerformFinalSuperFlash(onPath, superOnPath, 400);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }

                await SetImageAsync(iconImageBox, onPath);
                iconOverlayImageBox.Opacity = 0.0;
                iconImageBox.Opacity = 1.0;
                await AnimateOpacity(iconHaloImageBox, 0.25, fadeAnimationMs);
            }

            async Task PerformFinalSuperFlash(string onPath, string superOnPath, int duration)
            {
                await SetImageAsync(iconImageBox, superOnPath);
                iconOverlayImageBox.Opacity = 0;

                var superBaseTask = AnimateOpacity(iconImageBox, 1.0, fadeAnimationMs);
                var superHaloTask = AnimateOpacity(iconHaloImageBox, 0.6, fadeAnimationMs);
                await Task.WhenAll(superBaseTask, superHaloTask);

                await Task.Delay(duration, CancellationToken.None);
            }
        }

        double GetRandomRampDuration()
            => random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;

        double EaseInOut(double t)
            => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

        async Task<BitmapImage> GetCachedImageAsync(string path)
        {
            if (_imageCache.TryGetValue(path, out var cachedImage))
            {
                return cachedImage;
            }

            using var stream = File.OpenRead(path);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            _imageCache[path] = bmp;
            return bmp;
        }

        async Task SetImageAsync(Image imageControl, string path)
        {
            imageControl.Source = await GetCachedImageAsync(path);
        }

        async Task AnimateOpacity(FrameworkElement element, double targetOpacity, double durationMs, CancellationToken ct = default)
        {
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
    private async Task AnimateSplash(double splashDurationMs)
    {
        if (SplashLamp == null || SplashLampSuper == null || SplashLampHalo == null)
            return;

        const double fadeAnimationMs = 100;
        const double minFlashDuration = 300;
        const double maxFlashDuration = 700;

        var random = new Random();
        var today = DateTime.Today;

        // Easter egg detection
        string specialImagePath = null;
        bool isSpecialStatic = false;

        if (today.Month == 4 && today.Day >= 21 && today.Day <= 23)
        {
            // Birthday - static special image
            specialImagePath = "ms-appx:///Assets/special/happybirthdayme.png";
            isSpecialStatic = true;
        }
        else if (today.Month == 10 && (today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday))
        {
            // Halloween pumpkins - use themed flash animation
            SplashLampSuper.Source = new BitmapImage(new Uri(random.NextDouble() < 0.25
                ? "ms-appx:///Assets/special/pumpkin.off.png"
                : "ms-appx:///Assets/special/pumpkin.super.png"));
        }
        else if (today.Month == 12 && today.Day >= 25)
        {
            // Christmas/Holiday - static special image
            specialImagePath = "ms-appx:///Assets/special/gingerman.png";
            isSpecialStatic = true;
        }

        // If it's a static special occasion, just show the image and skip animation
        if (isSpecialStatic && !string.IsNullOrEmpty(specialImagePath))
        {
            SplashLampSuper.Source = new BitmapImage(new Uri(specialImagePath));
            SplashLampSuper.Opacity = 1.0;
            SplashLampHalo.Opacity = 0.0;
            return;
        }

        // Normal animation logic (or themed animation for Halloween)
        bool isOffFlash = random.NextDouble() < 0.25;

        if (specialImagePath == null) // Only set default images if not using special themed ones
        {
            if (isOffFlash)
            {
                SplashLampSuper.Source = new BitmapImage(new Uri("ms-appx:///Assets/icons/SplashScreen.Off.png"));
            }
            // else: Keep default Super image
        }

        double targetSuperOpacity = 1.0;
        double targetHaloOpacity = isOffFlash ? 0.01 : 0.75;

        // Calculate flash timing
        double availableTime = splashDurationMs - 400;
        double flashStart = Math.Max(200, availableTime * 0.3);
        double flashDuration = Math.Clamp(availableTime * 0.4, minFlashDuration, maxFlashDuration);

        // Wait for flash start
        await Task.Delay((int)flashStart);

        // Flash in
        var superFadeIn = AnimateOpacitySplash(SplashLampSuper, targetSuperOpacity, fadeAnimationMs);
        var haloChange = AnimateOpacitySplash(SplashLampHalo, targetHaloOpacity, fadeAnimationMs);
        await Task.WhenAll(superFadeIn, haloChange);

        // Hold the flash
        await Task.Delay((int)flashDuration);

        // Fade back to normal
        var superFadeOut = AnimateOpacitySplash(SplashLampSuper, 0.0, fadeAnimationMs);
        var haloNormal = AnimateOpacitySplash(SplashLampHalo, 0.175, fadeAnimationMs);
        await Task.WhenAll(superFadeOut, haloNormal);

        async Task AnimateOpacitySplash(FrameworkElement element, double targetOpacity, double durationMs)
        {
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
            await tcs.Task;
        }
    }



    public async void UpdateUI(double animationDurationSeconds = 0.15)
    {
        // Suppress Previewer Updates
        Previewer.Instance.Freeze();

        // Sliders
        var sliderConfigs = new[]
        {
        (FogMultiplierSlider, FogMultiplierBox, Persistent.FogMultiplier, false),
        (EmissivityMultiplierSlider, EmissivityMultiplierBox, Persistent.EmissivityMultiplier, false),
        (NormalIntensitySlider, NormalIntensityBox, (double)Persistent.NormalIntensity, true),
        (MaterialNoiseSlider, MaterialNoiseBox, (double)Persistent.MaterialNoiseOffset, true),
        (RoughenUpSlider, RoughenUpBox, (double)Persistent.RoughenUpIntensity, true),
        (ButcherHeightmapsSlider, ButcherHeightmapsBox, (double)Persistent.ButcheredHeightmapAlpha, true)
        };

        // Match bool-based UI elements to their current bools
        VanillaRTXCheckBox.IsChecked = TunerVariables.IsVanillaRTXEnabled;
        NormalsCheckBox.IsChecked = TunerVariables.IsNormalsEnabled;
        OpusCheckBox.IsChecked = TunerVariables.IsOpusEnabled;
        EmissivityAmbientLightToggle.IsOn = Persistent.AddEmissivityAmbientLight;
        TargetPreviewToggle.IsChecked = Persistent.IsTargetingPreview;

        // Animate sliders (intentionally put here, don't move up or down)
        await AnimateSliders(sliderConfigs, animationDurationSeconds);

        if (RuntimeFlags.Set("Initialize_UI_Previews_Only_With_The_First_Call"))
        {
            // UpdateUI is called once at the start. we want previews to initialize only once. Thus this flag, which allows this code block
            // To run once and then never again.
            SetPreviews();
        }


        // Resume Previewer Updates
        Previewer.Instance.Unfreeze();



        async Task AnimateSliders(
            (Slider slider, TextBox textBox, double targetValue, bool isInteger)[] configs,
            double durationSeconds)
        {
            var startValues = configs.Select(c => c.slider.Value).ToArray();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var totalMs = durationSeconds * 1000;

            while (stopwatch.ElapsedMilliseconds < totalMs)
            {
                var progress = stopwatch.ElapsedMilliseconds / totalMs;
                var easedProgress = 1 - Math.Pow(1 - progress, 3);

                for (int i = 0; i < configs.Length; i++)
                {
                    var (slider, textBox, targetValue, isInteger) = configs[i];
                    var currentValue = Lerp(startValues[i], targetValue, easedProgress);
                    SetSliderValue(slider, textBox, currentValue, isInteger);
                }

                await Task.Delay(4);
            }

            for (int i = 0; i < configs.Length; i++)
            {
                var (slider, textBox, targetValue, isInteger) = configs[i];
                SetSliderValue(slider, textBox, targetValue, isInteger);
            }
        }

        void SetSliderValue(Slider slider, TextBox textBox, double value, bool isInteger)
        {
            var rounded = isInteger ? Math.Round(value) : Math.Round(value, 2);
            slider.Value = rounded;
            textBox.Text = isInteger ? rounded.ToString() : rounded.ToString("0.00");
        }

        double Lerp(double start, double end, double t) => start + (end - start) * t;
    }

    #endregion -------------------------------



    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Find helpful resources in the README file, launching in your browser shortly.", LogLevel.Informational);
        OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App/blob/master/README.md");
    }



    private async void MojankEasterEggButton_Click(object sender, RoutedEventArgs e)
    {
        _ = BlinkingLamp(true, true);

        var now = DateTime.UtcNow;
        if ((now - _mojankLastClick).TotalSeconds > 8)
        {
            _mojankClickCount = 0;
        }
        _mojankLastClick = now;
        _mojankClickCount++;

        if (_mojankClickCount == 3)
        {
            var message = MojankMessages.JokeMessages[Random.Shared.Next(MojankMessages.JokeMessages.Length)];
            Log(message + " Continue and you might see a UAC prompt...", LogLevel.Warning);
        }

        if (_mojankClickCount >= 4)
        {
            _mojankClickCount = 0;
            await MojankEasterEgg.TriggerAsync();
            Log("Your Minecraft startup splash texts may have been slightly updated to Mojank.", LogLevel.Informational);
        }
    }



    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }

        OpenUrl("https://ko-fi.com/cubeir");
    }
    private void DonateButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }
    }
    private void DonateButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB51";
        var credits = CreditsUpdater.GetCredits(true);
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
        {
            Log(credits);
        }
    }






    public async Task LocatePacksButton_Click(bool ShowLogs = false)
    {
        _ = BlinkingLamp(true, true, 1.0);

        // Reset these variables and controls
        VanillaRTXCheckBox.IsEnabled = false;
        VanillaRTXCheckBox.IsChecked = false;
        IsVanillaRTXEnabled = false;
        NormalsCheckBox.IsEnabled = false;
        NormalsCheckBox.IsChecked = false;
        IsNormalsEnabled = false;
        OpusCheckBox.IsEnabled = false;
        OpusCheckBox.IsChecked = false;
        IsOpusEnabled = false;

        VanillaRTXLocation = string.Empty;
        VanillaRTXNormalsLocation = string.Empty;
        VanillaRTXOpusLocation = string.Empty;
        CustomPackLocation = string.Empty;

        VanillaRTXVersion = string.Empty;
        VanillaRTXNormalsVersion = string.Empty;
        VanillaRTXOpusVersion = string.Empty;
        CustomPackDisplayName = string.Empty;

        var statusMessage = PackLocator.LocatePacks(IsTargetingPreview,
            out VanillaRTXLocation, out VanillaRTXVersion,
            out VanillaRTXNormalsLocation, out VanillaRTXNormalsVersion,
            out VanillaRTXOpusLocation, out VanillaRTXOpusVersion);
        if (ShowLogs)
        {
            Log(statusMessage);
        }

        if (!string.IsNullOrEmpty(VanillaRTXLocation) && Directory.Exists(VanillaRTXLocation))
        {
            VanillaRTXCheckBox.IsEnabled = true;
        }

        if (!string.IsNullOrEmpty(VanillaRTXNormalsLocation) && Directory.Exists(VanillaRTXNormalsLocation))
        {
            NormalsCheckBox.IsEnabled = true;
        }

        if (!string.IsNullOrEmpty(VanillaRTXOpusLocation) && Directory.Exists(VanillaRTXOpusLocation))
        {
            OpusCheckBox.IsEnabled = true;
        }
    }
    private void BrowsePacksButton_Click(object sender, RoutedEventArgs e)
    {
        var shiftPressed = Microsoft.UI.Input.InputKeyboardSource
                                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftPressed)
        {
            _ = LocatePacksButton_Click(shiftPressed);
            return;
        }

        ToggleControls(this, false, true, []);

        var packBrowserWindow = new Vanilla_RTX_App.PackBrowser.PackBrowserWindow(this);
        var mainAppWindow = this.AppWindow;

        packBrowserWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        packBrowserWindow.AppWindow.Move(mainAppWindow.Position);

        packBrowserWindow.Closed += (s, args) =>
        {
            ToggleControls(this, true, true, []);

            if (!string.IsNullOrEmpty(TunerVariables.CustomPackLocation))
            {
                Log($"Selected: {TunerVariables.CustomPackDisplayName}", LogLevel.Success);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        packBrowserWindow.Activate();
    }



    private void TargetPreviewToggle_Checked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = true;
        _ = LocatePacksButton_Click();
        Log("Targeting Minecraft Preview.", LogLevel.Informational);

        LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColorLight3"]);
    }
    private void TargetPreviewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = false;
        _ = LocatePacksButton_Click();
        Log("Targeting Minecraft Release.", LogLevel.Informational);

        // Color of that little border next to the button
        var theme = LeftEdgeOfTargetPreviewButton.ActualTheme;
        var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
        var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
        if (themeDictionaries.TryGetValue(themeKey, out var themeDict) && themeDict is ResourceDictionary dict)
        {
            if (dict.TryGetValue("FakeSplitButtonBrightBorderColor", out var colorObj) && colorObj is Color color)
            {
                LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(color);
            }
        }
    }


    // TODO: make it return status and values some specific controls as well for easier debugging where you should've used bindings
    private void LogCopyButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SidebarLog.Text))
        {
            var sb = new StringBuilder();
            sb.AppendLine(SidebarLog.Text);
            sb.AppendLine();
            sb.AppendLine("===== Tuner Variables");

            var fields = typeof(TunerVariables).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                var value = field.GetValue(null);
                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }

            sb.AppendLine();
            sb.AppendLine("===== Persistent Variables");
            var persistentFields = typeof(TunerVariables.Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in persistentFields)
            {
                var value = field.GetValue(null);
                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);
            Log("Copied logs to clipboard.", LogLevel.Success);

            // Lamp off single flash
            _ = BlinkingLamp(true, true, 0.0);
        }
    }



    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            switch (checkbox.Name)
            {
                case "VanillaRTXCheckBox":
                    IsVanillaRTXEnabled = true;
                    break;
                case "NormalsCheckBox":
                    IsNormalsEnabled = true;
                    break;
                case "OpusCheckBox":
                    IsOpusEnabled = true;
                    break;
            }
        }
    }
    private void Option_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            switch (checkbox.Name)
            {
                case "VanillaRTXCheckBox":
                    IsVanillaRTXEnabled = false;
                    break;
                case "NormalsCheckBox":
                    IsNormalsEnabled = false;
                    break;
                case "OpusCheckBox":
                    IsOpusEnabled = false;
                    break;
            }
        }
    }



    private void FogMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue, 2);
        FogMultiplier = roundedValue;
        FogMultiplierSlider.Value = roundedValue; // force slider to show rounded value
        if (FogMultiplierBox != null && FogMultiplierBox.FocusState == FocusState.Unfocused)
            FogMultiplierBox.Text = roundedValue.ToString("0.00");
    }

    private void FogMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(FogMultiplierBox.Text, out double val))
        {
            val = Math.Clamp(val, 0.0, 10.0);
            double roundedVal = Math.Round(val, 2);
            FogMultiplier = roundedVal;
            FogMultiplierSlider.Value = roundedVal;
            FogMultiplierBox.Text = roundedVal.ToString("0.00");
        }
        else
        {
            FogMultiplierBox.Text = FogMultiplier.ToString("0.00");
        }
    }

    private void EmissivityMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        EmissivityMultiplier = Math.Round(e.NewValue, 1);
        if (EmissivityMultiplierBox != null && EmissivityMultiplierBox.FocusState == FocusState.Unfocused)
            EmissivityMultiplierBox.Text = EmissivityMultiplier.ToString("F1");
    }

    private void EmissivityMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(EmissivityMultiplierBox.Text, out double val))
        {
            val = Math.Clamp(val, 0.5, 10.0);
            EmissivityMultiplier = val;
            EmissivityMultiplierSlider.Value = val;
            EmissivityMultiplierBox.Text = val.ToString("F1");
        }
        else
        {
            EmissivityMultiplierBox.Text = EmissivityMultiplier.ToString("F1");
        }
    }

    private void NormalIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        NormalIntensity = (int)Math.Round(e.NewValue);
        if (NormalIntensityBox != null && NormalIntensityBox.FocusState == FocusState.Unfocused)
            NormalIntensityBox.Text = NormalIntensity.ToString();
    }

    private void NormalIntensity_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(NormalIntensityBox.Text, out int val))
        {
            val = Math.Clamp(val, 0, 900);
            NormalIntensity = val;
            NormalIntensitySlider.Value = val;
            NormalIntensityBox.Text = val.ToString();
        }
        else
        {
            NormalIntensityBox.Text = NormalIntensity.ToString();
        }
    }

    private void MaterialNoise_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        MaterialNoiseOffset = (int)Math.Round(e.NewValue);
        if (MaterialNoiseBox != null && MaterialNoiseBox.FocusState == FocusState.Unfocused)
            MaterialNoiseBox.Text = MaterialNoiseOffset.ToString();
    }

    private void MaterialNoise_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MaterialNoiseBox.Text, out int val))
        {
            val = Math.Clamp(val, 0, 25);
            MaterialNoiseOffset = val;
            MaterialNoiseSlider.Value = val;
            MaterialNoiseBox.Text = val.ToString();
        }
        else
        {
            MaterialNoiseBox.Text = MaterialNoiseOffset.ToString();
        }
    }

    private void RoughenUp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        RoughenUpIntensity = (int)Math.Round(e.NewValue);
        if (RoughenUpBox != null && RoughenUpBox.FocusState == FocusState.Unfocused)
            RoughenUpBox.Text = RoughenUpIntensity.ToString();
    }

    private void RoughenUp_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(RoughenUpBox.Text, out int val))
        {
            val = Math.Clamp(val, 0, 25);
            RoughenUpIntensity = val;
            RoughenUpSlider.Value = val;
            RoughenUpBox.Text = val.ToString();
        }
        else
        {
            RoughenUpBox.Text = RoughenUpIntensity.ToString();
        }
    }

    private void ButcherHeightmaps_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ButcheredHeightmapAlpha = (int)Math.Round(e.NewValue);
        if (ButcherHeightmapsBox != null && ButcherHeightmapsBox.FocusState == FocusState.Unfocused)
            ButcherHeightmapsBox.Text = ButcheredHeightmapAlpha.ToString();
    }

    private void ButcherHeightmaps_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ButcherHeightmapsBox.Text, out int val))
        {
            val = Math.Clamp(val, 0, 255);
            ButcheredHeightmapAlpha = val;
            ButcherHeightmapsSlider.Value = val;
            ButcherHeightmapsBox.Text = val.ToString();
        }
        else
        {
            ButcherHeightmapsBox.Text = ButcheredHeightmapAlpha.ToString();
        }
    }

    // Input validation helpers to prevent invalid characters
    private void IntegerTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Allow only digits
        args.Cancel = !System.Text.RegularExpressions.Regex.IsMatch(args.NewText, @"^[0-9]*$");
    }
    private void DoubleTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Allow digits, one decimal point, and leading negative sign
        args.Cancel = !System.Text.RegularExpressions.Regex.IsMatch(args.NewText, @"^-?[0-9]*\.?[0-9]*$");
    }


    private void EmissivityAmbientLightToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = sender as ToggleSwitch;
        AddEmissivityAmbientLight = toggle.IsOn;

        // Show/hide the warning icon
        EmissivityWarningIcon.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }
    


    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        // ----- HARD RESET 
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {   
            ToggleControls(this, false, true, ["LogCopyButton"]);
            _progressManager.ShowProgress();
            _ = BlinkingLamp(true);

            _ = WipeAllStorageData();
            return;
        }
        // ----- HARD RESET 

        // Defaults
        FogMultiplier = Defaults.FogMultiplier;
        EmissivityMultiplier = Defaults.EmissivityMultiplier;
        NormalIntensity = Defaults.NormalIntensity;
        MaterialNoiseOffset = Defaults.MaterialNoiseOffset;
        RoughenUpIntensity = Defaults.RoughenUpIntensity;
        ButcheredHeightmapAlpha = Defaults.ButcheredHeightmapAlpha;
        AddEmissivityAmbientLight = Defaults.AddEmissivityAmbientLight;

        // Manually updates UI based on new values
        UpdateUI();

        // Empty the sidebarlog
        SidebarLog.Text = "";

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);

        RuntimeFlags.Unset("Wrote_Supporter_Shoutout");

        if (RuntimeFlags.Set("Said_Extra_Resetting_Information"))
        {
            Log($"To perform a full reset of app's data if necessery, hold SHIFT key while pressing {ResetButton.Content}.", LogLevel.Informational);
            Log($"Note: this does not restore the packs to their default state!\nTo reset packs back to original you can quickly reinstall the latest versions of Vanilla RTX using the '{UpdateVanillaRTXButtonText.Text}' button. Other packs will require manual reinstallation.\nUse Export button to back them up!", LogLevel.Informational);
        }
        Log("Tuning environment reset.", LogLevel.Success);
    }
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        // Vanilla RTX
        IsVanillaRTXEnabled = false;
        IsNormalsEnabled = false;
        IsOpusEnabled = false;

        // The custom one
        CustomPackDisplayName = string.Empty;
        CustomPackLocation = string.Empty;

        // Manually updates UI based on new values
        UpdateUI();

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);
        Log("Cleared all pack selections.", LogLevel.Success);

    }
    private async Task WipeAllStorageData()
    {
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var roamingSettings = Windows.Storage.ApplicationData.Current.RoamingSettings;
            Log("Wiping all of app's storage data...", LogLevel.Warning);
            await Task.Delay(200);

            // Wipe local settings
            var localKeys = localSettings.Values.Keys.ToList();
            foreach (var key in localKeys)
            {
                localSettings.Values.Remove(key);
                Log($"Deleted: {key}", LogLevel.Informational);
                await Task.Delay(20);
            }

            // Wipe roaming settings (even though you don't use it, because of its limits and that you don't need it)
            var roamingKeys = roamingSettings.Values.Keys.ToList();
            foreach (var key in roamingKeys)
            {
                roamingSettings.Values.Remove(key);
                Log($"Deleted: {key}", LogLevel.Informational);
                await Task.Delay(20);
            }

            Log($"Wiped {localKeys.Count + roamingKeys.Count} keys.", LogLevel.Success);

            // Delete LocalState folders (Downloads and DLSS_Cache)
            Log("Checking for app data folders...", LogLevel.Informational);

            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                var foldersToDelete = new[]
                {
                Path.Combine(localFolder, "Downloads"),
                Path.Combine(localFolder, "DLSS_Cache"),
                Path.Combine(localFolder, "RTX_Cache")
                };

                int deletedFolders = 0;
                foreach (var folder in foldersToDelete)
                {
                    try
                    {
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, true);
                            Log($"Deleted folder: {folder}", LogLevel.Informational);
                            deletedFolders++;
                            await Task.Delay(15);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not delete {folder}: {ex.Message}", LogLevel.Warning);
                        await Task.Delay(15);
                    }
                }

                if (deletedFolders > 0)
                {
                    Log($"Deleted {deletedFolders} folder(s).", LogLevel.Success);
                }
                else
                {
                    Log("No data folders found.", LogLevel.Informational);
                }
            }
            catch (Exception ex)
            {
                Log($"Error deleting app data folders: {ex.Message}", LogLevel.Warning);
            }

            await Task.Delay(500);
            Log("Hard reset complete! Restarting in a moment...", LogLevel.Success);
            await Task.Delay(3000);
            var restartResult = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
        }
        catch (Exception ex)
        {
            Log($"Error during hard reset: {ex.Message}", LogLevel.Error);
        }
    }







    private void BetterRTXPresetManagerButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleControls(this, false, true, []);

        var betterRTXWindow = new Vanilla_RTX_App.BetterRTXBrowser.BetterRTXManagerWindow(this);

        var mainAppWindow = this.AppWindow;
        betterRTXWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        betterRTXWindow.AppWindow.Move(mainAppWindow.Position);

        betterRTXWindow.Closed += (s, args) =>
        {
            ToggleControls(this, true, true, []);

            // Log status after window closes
            if (betterRTXWindow.OperationSuccessful)
            {
                Log(betterRTXWindow.StatusMessage, LogLevel.Success);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (!string.IsNullOrEmpty(betterRTXWindow.StatusMessage))
            {
                Log(betterRTXWindow.StatusMessage, LogLevel.Error);
                _ = BlinkingLamp(true, true, 0.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        betterRTXWindow.Activate();
    }

    private void DLSSVersionSwitcherButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleControls(this, false, true, []);

        var dlssSwitcherWindow = new Vanilla_RTX_App.DLSSBrowser.DLSSSwitcherWindow(this);
        var mainAppWindow = this.AppWindow;

        dlssSwitcherWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        dlssSwitcherWindow.AppWindow.Move(mainAppWindow.Position);

        dlssSwitcherWindow.Closed += (s, args) =>
        {
            ToggleControls(this, true, true, []);

            // Log status after window closes
            if (dlssSwitcherWindow.OperationSuccessful)
            {
                Log(dlssSwitcherWindow.StatusMessage, LogLevel.Success);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (!string.IsNullOrEmpty(dlssSwitcherWindow.StatusMessage))
            {
                Log(dlssSwitcherWindow.StatusMessage, LogLevel.Error);
                _ = BlinkingLamp(true, true, 0.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        dlssSwitcherWindow.Activate();
    }










    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        _progressManager.ShowProgress();
        ToggleControls(this, false);
        try
        {
            var exportQueue = new List<(string path, string name)>();
            var suffix = $"_export_{appVersion}";

            if (IsVanillaRTXEnabled && Directory.Exists(VanillaRTXLocation))
                exportQueue.Add((VanillaRTXLocation, "Vanilla_RTX_" + VanillaRTXVersion + suffix));
            if (IsNormalsEnabled && Directory.Exists(VanillaRTXNormalsLocation))
                exportQueue.Add((VanillaRTXNormalsLocation, "Vanilla_RTX_Normals_" + VanillaRTXNormalsVersion + suffix));
            if (IsOpusEnabled && Directory.Exists(VanillaRTXOpusLocation))
                exportQueue.Add((VanillaRTXOpusLocation, "Vanilla_RTX_Opus_" + VanillaRTXOpusVersion + suffix));
            if (!string.IsNullOrEmpty(CustomPackDisplayName) && Directory.Exists(CustomPackLocation))
                exportQueue.Add((CustomPackLocation, SanitizeFileName(CustomPackDisplayName) + suffix));

            string SanitizeFileName(string name)
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                var sanitized = new string(name
                    .Select(c => char.IsWhiteSpace(c) || invalidChars.Contains(c) ? '_' : c)
                    .ToArray());
                return Regex.Replace(sanitized.Trim('_'), "_{2,}", "_");
            }

            // Deduplicate by normalized paths
            var seenPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dedupedQueue = new List<(string path, string name)>();

            foreach (var (path, name) in exportQueue)
            {
                var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (seenPaths.ContainsKey(normalizedPath))
                {
                    Log($"{seenPaths[normalizedPath]} was selected twice, but will only be exported once!", LogLevel.Warning);
                }
                else
                {
                    seenPaths.Add(normalizedPath, name.Replace(suffix, "")); // Store display name without suffix
                    dedupedQueue.Add((path, name));
                }
            }

            foreach (var (path, name) in dedupedQueue)
            {
                await Exporter.ExportMCPACK(path, name);

                // Blinks once for each exported pack!
                _ = BlinkingLamp(true, true, 1.0);
            }   
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), LogLevel.Warning);
        }
        finally
        {
            if (!IsVanillaRTXEnabled && !IsNormalsEnabled && !IsOpusEnabled &&
                (string.IsNullOrEmpty(CustomPackLocation) || string.IsNullOrEmpty(CustomPackDisplayName))
                )
            {
                Log("Select at least one package to export.", LogLevel.Warning);
            }
            else
            {
                Log("Export Queue Finished.", LogLevel.Success);
            }
            _progressManager.HideProgress();
            ToggleControls(this, true);
        }
    }


    private async void TuneSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            Log($"Please close Minecraft while using the app, when finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);

        try
        {
            if (!IsVanillaRTXEnabled && !IsNormalsEnabled && !IsOpusEnabled &&
                (string.IsNullOrEmpty(CustomPackLocation) || string.IsNullOrEmpty(CustomPackDisplayName))
                )
            {
                Log("Select at least one package to tune.", LogLevel.Warning);
                return;
            }
            else
            {
                _progressManager.ShowProgress();
                _ = BlinkingLamp(true);
                ToggleControls(this, false);

                await Task.Run(Processor.TuneSelectedPacks);
                Log("Completed tuning.", LogLevel.Success);

                // Reset emissive multiplier if ambient light was enabled during current tuning attempt
                if (AddEmissivityAmbientLight)
                {
                    EmissivityMultiplier = Defaults.EmissivityMultiplier;
                }
            }
        }
        finally
        {
            _ = BlinkingLamp(false);
            ToggleControls(this, true);
            _progressManager.HideProgress();

            // Always update the UI, mainly because of EmissivityMultiplier = Defaults.EmissivityMultiplier; line above
            UpdateUI();
        }
    }


    private async void UpdateVanillaRTXButton_Click(object sender, RoutedEventArgs e)
    {
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Log($"Please close Minecraft while using the app, when finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
        }
        try
        {
            ToggleControls(this, false);
            _progressManager.ShowProgress();
            _ = BlinkingLamp(true);

            var updater = new PackUpdater();

            updater.ProgressUpdate += (message) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    Log($"{message}");
                });
            };

            // Run the update operation
            var (success, logs) = await Task.Run(() => updater.UpdatePacksAsync());
            // foreach (var log in logs) Log(log);

            if (success)
            {
                Log($"{HaveDeployableCache} completed.", LogLevel.Success);
            }
            else
            {
                Log($"{HaveDeployableCache} failed.", LogLevel.Error);
            }
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _ = BlinkingLamp(false);
            ToggleControls(this, true);
            _progressManager.HideProgress();

            // Set reinstall latest packs button visuals based on cache status
            if (_updater.HasDeployableCache())
            {
                UpdateVanillaRTXGlyph.Glyph = "\uE8F7";
                UpdateVanillaRTXButtonText.Text = "Reinstall latest RTX packages";
            }
            else
            {
                UpdateVanillaRTXGlyph.Glyph = "\uEBD3";
                UpdateVanillaRTXButtonText.Text = "Install latest RTX packages";
            }

            // Trigger an automatic pack location check after update (fail or not)
            _ = LocatePacksButton_Click();
        }
    }


    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {

        if (Helpers.IsMinecraftRunning())
        {
            Log("The game was already open, please restart the game for options.txt changes to take effect.", LogLevel.Warning);
        }

        try
        {
            var logs = await Modules.Launcher.LaunchMinecraftRTXAsync(IsTargetingPreview);
            Log(logs, LogLevel.Informational);
        }
        finally
        {
            _ = BlinkingLamp(true, true, 0.0);
        }
    }
}