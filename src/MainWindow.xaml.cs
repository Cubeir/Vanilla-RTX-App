using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
using static System.Runtime.InteropServices.JavaScript.JSType;
using static Vanilla_RTX_App.Core.WindowControlsManager;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;

namespace Vanilla_RTX_App;

/*
### GENERAL TODO & IDEAS ###

- https://discord.com/channels/721377277480402985/1455281964138369054/1455548708123840604
Does the app stop working if Minecraft, for whatever the reason, is named weirdly?
Should the GDKLocator's behavior be updated to: just find the game's exe?
But then, how do we differentiate preview and release?

Investigate, and after all changes, TEST the whole thing again
locator, manual locator, all steps, on diff drives, deep in subfolders up to 9, on a busy last drive/worst case
And lastly the CACHE invalidator, will it continue to work well with it (betterrtx cache invalidator)

- Update the docs to be less verbose, more accurate and helpful instead, cut off unneeded details.

- Further review PackUpdater and BetterRTX manager codes, ensure no stone is left unturned.
Especially release builds
Game detection and cache invalidation could be improved for both
PackUpdater may have blindspots still, though HIGHLY unlikely, still, review and test, make changes on the go

- Go over Main Window again some time, especially update ToggleControls usage, its... weird to say the least
Be more CONSISTENT with it, and ensure sidebarlogbox NEVER EVER EVER gets disabled on the main window!
Some overrides now disable it while they should not.

- Unify the 4 places hardcoded paths are used into a class
pack updater, pack locator, pack browser, launcher, they deal with hardcoded paths, what else? (Ask copilot to scry the code)

For finding the game, GDKLocator kit handles it system-wide, all good
**For Minecraft's USER DATA however, you better expose those, apparently some third party launchers use different paths!!!**

For GDKLocator, and wherever it is used, you could still expose the SPECIFIC file and folder names it looks for
Actually don't expose anything, the overhead and the risk, instead, make them globally-available constants that can easily be changed
so in the event of Minecraft files restructuing, you can quickly release an update without having to do much testing, make the code clear, basically
This Applies to this older todo below as well:

- Expose as many params as you can to a json in app's root
the URLs the app sends requests to + the hardcoded Minecraft paths
* Resource packs only end up in shared
* Options file is in both shared and non-shared, but non-shared is presumably the one that takes priority, still, we take care of both
* PackLocator, PackUpdater (deployer), Browse Packs, and LaunchMinecraftRTX's options.txt updater are the only things that rely on hardcoded paths on the system
* EXPOSE ALL hardcoded URLs and Tuning parameters

Additionally, while going through params, 
Examine your github usage patterns (caching, and cooldowns) -- especially updater, maximize up-to-dateness with as few requests as possible
All settled there? ensure there isn't a way the app can ddos github AND at the same time there are no unintended Blind spots

- Do the TODOs scattered in the code

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

- Set random preview arts on startup, featuring locations from Vanilla RTX's history (Autumn's End, Pale Horizons, Bevy of Bugs, etc...)
Or simple pixel arts you'd like to make in the same style
Have 5-10 made

- Tuner could, in theory, use the MANIFEST.JSON's metadata (i.e. TOOLS USED param) to MARK packs
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
        public static int RoughnessControlValue = Defaults.RoughnessControlValue;
        public static int LazifyNormalAlpha = Defaults.LazifyNormalAlpha;
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
        public const int RoughnessControlValue = 0;
        public const int LazifyNormalAlpha = 0;
        public const bool AddEmissivityAmbientLight = false;
    }

    // Set Window size default for all windows
    public const int WindowSizeX = 1105;
    public const int WindowSizeY = 555;
    public const int WindowMinSizeX = 970;
    public const int WindowMinSizeY = 555;

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
                Trace.WriteLine($"An issue occured loading settings");
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

    public readonly PackUpdater _updater = new();

    private LampAnimator _titlebarLampAnimator;
    private LampAnimator _splashLampAnimator;
    private async void InitializeLampAnimators()
    {
        // Titlebar lamp
        _titlebarLampAnimator = new LampAnimator(
            LampAnimator.LampContext.Titlebar,
            baseImage: iconImageBox,
            overlayImage: iconOverlayImageBox,
            haloImage: iconHaloImageBox
        );

        // Splash lamp
        _splashLampAnimator = new LampAnimator(
            LampAnimator.LampContext.Splash,
            baseImage: SplashLamp,
            overlayImage: null,
            haloImage: SplashLampHalo,
            superImage: SplashLampSuper
        );

        // Initialize both immediately to preload and set special occasion images
        await Task.WhenAll(
            _titlebarLampAnimator.InitializeAsync(),
            _splashLampAnimator.InitializeAsync()
        );
    }


    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    private Dictionary<FrameworkElement, string> _originalTexts = new();
    private bool _shiftPressed = false;

    // ---------------------------------------| | | | | | | | | | |-------------------------------------------- \\

    public MainWindow()
    {
        // Properties to set before it is rendered
        SetMainWindowProperties();
        InitializeComponent();
        InitializeLampAnimators();

        // Titlebar drag region
        SetTitleBar(TitleBarDragArea);

        // Show splash screen immedietly
        if (SplashOverlay != null)
        {
            SplashOverlay.Visibility = Visibility.Visible;
        }

        _windowStateManager = new WindowStateManager(this, false, msg => Log(msg));
        _progressManager = new ProgressBarManager(ProgressBar);

        Instance = this;

        var defaultSize = new SizeInt32(WindowSizeX, WindowSizeY);
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

        // For dynamiclly changing text with Shift key
        Content.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Shift && !_shiftPressed)
            {
                _shiftPressed = true;
                // Just set controls and shift texts here
                SetShiftText(ResetButton, "⚠️ Wipe");
                // Add more as needed...
            }
        };
        Content.KeyUp += (s, e) =>
        {
            if (e.Key == VirtualKey.Shift && _shiftPressed)
            {
                _shiftPressed = false;
                foreach (var kvp in _originalTexts)
                {
                    if (kvp.Key is Button btn) btn.Content = kvp.Value;
                    else if (kvp.Key is TextBlock tb) tb.Text = kvp.Value;
                }
            }
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
        _ = AnimateSplash(375);

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

        // Slower UI update override for a smoother startup
        UpdateUI(0.001);

        // Locate packs, if Preview is enabled, TargetPreview triggers another pack location, avoid redundant operation
        if (!IsTargetingPreview)
        {
            _ = LocatePacksButton_Click();
        }
        else
        {
            BetterRTXPresetManagerButton.IsEnabled = false;
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


        // Calling it last since it might add a bit of delay as it searches a few dirs and files
        MinecraftGDKLocator.ValidateAndUpdateCachedLocations();

        // Brief delay to ensure everything is fully rendered, then fade out splash screen
        await Task.Delay(750);
        // ================ Do all UI updates you DON'T want to be seen BEFORE here, and what you want seen AFTER ======================= 
        await FadeOutSplashScreen();

        // Warning if MC is running
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            var buttonName = LaunchButtonText.Text;
            Log($"Please close Minecraft while using the app, when finished, launch the game using {buttonName} button.", LogLevel.Warning);
        }

        // Show Leave a Review prompt, has a 10 sec cd built in
        _ = ReviewPromptManager.InitializeAsync(MainGrid);

        async Task FadeOutSplashScreen()
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
            presenter.PreferredMinimumWidth = (int)(WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(WindowMinSizeY * scaleFactor);
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


    private void SetPreviews()
    {
        var date = DateTime.Today;

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
            "ms-appx:///Assets/previews/roughenup.unrough.png",
            "ms-appx:///Assets/previews/roughenup.rough.png",
            Defaults.RoughnessControlValue
        );

        Previewer.Instance.InitializeSlider(MaterialNoiseSlider,
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/materials.grainy.png",
             Defaults.MaterialNoiseOffset
        );

        Previewer.Instance.InitializeSlider(LazifyNormalsSlider,
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.butchered.png",
            Defaults.LazifyNormalAlpha
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
        if (date.Month == 4 && date.Day >= 21 && date.Day <= 23)
        {
            Previewer.Instance.InitializeCheckBox(NormalsCheckBox, "ms-appx:///Assets/previews/checkbox.normals.ticked.birthday.png", "ms-appx:///Assets/previews/checkbox.normals.unticked.birthday.png");
        }
        else
        {
            Previewer.Instance.InitializeCheckBox(NormalsCheckBox,
               "ms-appx:///Assets/previews/checkbox.normals.ticked.png",
               "ms-appx:///Assets/previews/checkbox.normals.unticked.png"
            );
        }
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

        if ((date.Month == 12 && date.Day >= 23) || (date.Month == 1 && date.Day <= 7))
        {
            Previewer.Instance.InitializeButton(UpdateVanillaRTXButton,
                "ms-appx:///Assets/previews/version.checker.christmas.png"
            );
        }
        else
        {
            Previewer.Instance.InitializeButton(UpdateVanillaRTXButton,
                "ms-appx:///Assets/previews/version.checker.png"
            );
        }

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

        Previewer.Instance.InitializeButton(ChatButton,
            "ms-appx:///Assets/previews/bonfire.png"
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
        LazifyNormalsBoxShadow.Receivers.Add(RightShadowReceiver);
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
        await _titlebarLampAnimator.Animate(enable, singleFlash, singleFlashOnChance);
    }
    private async Task AnimateSplash(double splashDurationMs)
    {
        await _splashLampAnimator.Animate(false, true, 0.9, splashDurationMs);
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
        (RoughenUpSlider, RoughenUpBox, (double)Persistent.RoughnessControlValue, true),
        (LazifyNormalsSlider, LazifyNormalsBox, (double)Persistent.LazifyNormalAlpha, true)
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



    private void SetShiftText(FrameworkElement control, string shiftText)
    {
        // Save original text if not already saved
        if (!_originalTexts.ContainsKey(control))
        {
            if (control is Button btn)
                _originalTexts[control] = btn.Content?.ToString() ?? "";
            else if (control is TextBlock tb)
                _originalTexts[control] = tb.Text;
        }

        // Apply shift text
        if (control is Button button)
            button.Content = shiftText;
        else if (control is TextBlock textBlock)
            textBlock.Text = shiftText;
    }
    #endregion -------------------------------


    // ------- Titlebar stuff
    private void LampInteraction_Click(object sender, RoutedEventArgs e)
    {
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            try
            {
                if (!string.IsNullOrEmpty(SidebarLog.Text))
                {
                    var sb = new StringBuilder();
                    // Original sidebar log (important status messages)
                    sb.AppendLine("===== Sidebar Log (UI-shown Messages)");
                    sb.AppendLine(SidebarLog.Text);
                    sb.AppendLine();
                    // Tuner variables
                    sb.AppendLine("===== Tuner Variables");
                    var fields = typeof(TunerVariables).GetFields(BindingFlags.Public | BindingFlags.Static);
                    foreach (var field in fields)
                    {
                        var value = field.GetValue(null);
                        sb.AppendLine($"{field.Name}: {value ?? "null"}");
                    }
                    sb.AppendLine();
                    // Persistent variables
                    sb.AppendLine("===== Persistent Variables");
                    var persistentFields = typeof(TunerVariables.Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
                    foreach (var field in persistentFields)
                    {
                        var value = field.GetValue(null);
                        sb.AppendLine($"{field.Name}: {value ?? "null"}");
                    }
                    sb.AppendLine();
                    // Trace logs
                    sb.AppendLine(TraceManager.GetAllTraceLogs());

                    // UI Controls State
                    sb.AppendLine();
                    sb.AppendLine("===== UI Controls State");
                    CollectUIControlsState(sb);

                    var dataPackage = new DataPackage();
                    dataPackage.SetText(sb.ToString());
                    Clipboard.SetContent(dataPackage);
                    Log("Copied debug logs to clipboard.", LogLevel.Success);
                    _ = BlinkingLamp(true, true, 0.0);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error during lamp interaction debug copy: {ex}");
            }
        }
        else
        {
            _ = BlinkingLamp(true, true);
        }

        void CollectUIControlsState(StringBuilder sb)
        {
            var fields = this.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var value = field.GetValue(this);
                if (value == null) continue;

                var type = value.GetType();
                var name = field.Name;

                // Toggle-type controls
                if (value is ToggleButton toggleBtn)
                {
                    sb.AppendLine($"{name} (ToggleButton): {toggleBtn.IsChecked?.ToString() ?? "null"}");
                }
                else if (value is CheckBox checkBox)
                {
                    sb.AppendLine($"{name} (CheckBox): {checkBox.IsChecked?.ToString() ?? "null"}");
                }
                else if (value is ToggleSwitch toggleSwitch)
                {
                    sb.AppendLine($"{name} (ToggleSwitch): {toggleSwitch.IsOn}");
                }
                else if (value is RadioButton radioBtn)
                {
                    sb.AppendLine($"{name} (RadioButton): {radioBtn.IsChecked?.ToString() ?? "null"}");
                }
                // Value controls
                else if (value is Slider slider)
                {
                    sb.AppendLine($"{name} (Slider): {slider.Value}");
                }
                else if (value is NumberBox numberBox)
                {
                    sb.AppendLine($"{name} (NumberBox): {numberBox.Value}");
                }
                else if (value is ComboBox comboBox)
                {
                    sb.AppendLine($"{name} (ComboBox): SelectedIndex={comboBox.SelectedIndex}, SelectedItem={comboBox.SelectedItem?.ToString() ?? "null"}");
                }
                else if (value is TextBox textBox)
                {
                    var text = textBox.Text;
                    if (!string.IsNullOrEmpty(text) && text.Length > 50)
                        text = text.Substring(0, 50) + "...";
                    sb.AppendLine($"{name} (TextBox): \"{text}\"");
                }
                else if (value is RatingControl rating)
                {
                    sb.AppendLine($"{name} (RatingControl): {rating.Value}");
                }
                else if (value is ColorPicker colorPicker)
                {
                    sb.AppendLine($"{name} (ColorPicker): {colorPicker.Color}");
                }
                else if (value is DatePicker datePicker)
                {
                    sb.AppendLine($"{name} (DatePicker): {datePicker.Date}");
                }
                else if (value is TimePicker timePicker)
                {
                    sb.AppendLine($"{name} (TimePicker): {timePicker.Time}");
                }
            }
        }
    }




    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Here is the invitation!\nDiscord.gg/A4wv4wwYud", LogLevel.Informational);
        OpenUrl("https://discord.gg/A4wv4wwYud");
    }


    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Find helpful resources in the README file, launching in your default browser shortly.", LogLevel.Informational);
        OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md");
    }
    private void HelpButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HelpButton.Content = "\uF167";
        if (RuntimeFlags.Set("Wrote_Info_Thingy"))
        {
            Log("Open a page with full documentation of the app and a how-to guide.", LogLevel.Informational);
        }
    }
    private void HelpButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HelpButton.Content = "\uE946";
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
                "Light" => "\uE706",
                "Dark" => "\uEC46",
                _ => "A",
            };
        }

        ToolTipService.SetToolTip(btn, "Theme: " + mode);
    }


    // -------


    private string _previousStatusMessage;
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
        if (ShowLogs && statusMessage != _previousStatusMessage)
        {
            Log(statusMessage);
            _previousStatusMessage = statusMessage;
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
        BetterRTXPresetManagerButton.IsEnabled = false;
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
        BetterRTXPresetManagerButton.IsEnabled = true;
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



    #region =============== SLIDER HANDLERS ===============


    private void HandleDoubleSliderValueChanged(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
    {
        double roundedValue = Math.Round(slider.Value, decimalPlaces);
        property = roundedValue;
        slider.Value = roundedValue;

        string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
        if (textBox != null && textBox.FocusState == FocusState.Unfocused)
        {
            // Use CurrentCulture so weirdos see "1,50" not "1.50"
            textBox.Text = roundedValue.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    private void HandleDoubleTextBoxLostFocus(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
    {
        // Try parsing with user's culture first (respects comma vs period)
        bool parsed = double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                       CultureInfo.CurrentCulture, out double val);

        // Fallback to invariant culture if that fails (for copy-paste scenarios)
        if (!parsed)
        {
            parsed = double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                    CultureInfo.InvariantCulture, out val);
        }

        if (parsed)
        {
            val = Math.Clamp(val, slider.Minimum, slider.Maximum);
            double roundedVal = Math.Round(val, decimalPlaces);
            property = roundedVal;
            slider.Value = roundedVal;

            string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
            // Display with user's culture
            textBox.Text = roundedVal.ToString(format, CultureInfo.CurrentCulture);
        }
        else
        {
            // Restore the last valid value with user's culture
            string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
            textBox.Text = property.ToString(format, CultureInfo.CurrentCulture);
        }
    }


    private void HandleIntSliderValueChanged(Slider slider, TextBox textBox, ref int property)
    {
        property = (int)Math.Round(slider.Value);
        if (textBox != null && textBox.FocusState == FocusState.Unfocused)
            textBox.Text = property.ToString(CultureInfo.InvariantCulture);
    }

    private void HandleIntTextBoxLostFocus(Slider slider, TextBox textBox, ref int property)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            val = Math.Clamp(val, (int)slider.Minimum, (int)slider.Maximum);
            property = val;
            slider.Value = val;
            textBox.Text = val.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            textBox.Text = property.ToString(CultureInfo.InvariantCulture);
        }
    }

    // =============== SLIDER EVENT HANDLERS ===============
    private void FogMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleDoubleSliderValueChanged(FogMultiplierSlider, FogMultiplierBox, ref FogMultiplier, 2);

    private void FogMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
        => HandleDoubleTextBoxLostFocus(FogMultiplierSlider, FogMultiplierBox, ref FogMultiplier, 2);


    private void EmissivityMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleDoubleSliderValueChanged(EmissivityMultiplierSlider, EmissivityMultiplierBox, ref EmissivityMultiplier, 1);

    private void EmissivityMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
        => HandleDoubleTextBoxLostFocus(EmissivityMultiplierSlider, EmissivityMultiplierBox, ref EmissivityMultiplier, 1);


    private void NormalIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(NormalIntensitySlider, NormalIntensityBox, ref NormalIntensity);

    private void NormalIntensity_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(NormalIntensitySlider, NormalIntensityBox, ref NormalIntensity);


    private void MaterialNoise_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(MaterialNoiseSlider, MaterialNoiseBox, ref MaterialNoiseOffset);

    private void MaterialNoise_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(MaterialNoiseSlider, MaterialNoiseBox, ref MaterialNoiseOffset);


    private void RoughenUp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(RoughenUpSlider, RoughenUpBox, ref RoughnessControlValue);

    private void RoughenUp_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(RoughenUpSlider, RoughenUpBox, ref RoughnessControlValue);


    private void LazifyNormals_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(LazifyNormalsSlider, LazifyNormalsBox, ref LazifyNormalAlpha);

    private void LazifyNormals_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(LazifyNormalsSlider, LazifyNormalsBox, ref LazifyNormalAlpha);


    private void EmissivityAmbientLightToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = sender as ToggleSwitch;
        AddEmissivityAmbientLight = toggle.IsOn;

        // Show/hide the warning icon
        EmissivityWarningIcon.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }
    #endregion


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
        RoughnessControlValue = Defaults.RoughnessControlValue;
        LazifyNormalAlpha = Defaults.LazifyNormalAlpha;
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
        // The UI display text relies on this, rerun it just in case, few ms overhead worth it
        try
        {
            await LocatePacksButton_Click();
        }
        finally
        {
            if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            {
                Log($"Please close Minecraft while using the app, when finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
            }

            ToggleControls(this, false, true, []);

            var packUpdaterWindow = new Vanilla_RTX_App.PackUpdate.PackUpdateWindow(this);
            var mainAppWindow = this.AppWindow;

            packUpdaterWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                mainAppWindow.Size.Width,
                mainAppWindow.Size.Height));
            packUpdaterWindow.AppWindow.Move(mainAppWindow.Position);

            // Do on window closure
            packUpdaterWindow.Closed += (s, args) =>
            {
                // Enable main UI buttons again
                ToggleControls(this, true, true, []);

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
                _ = LocatePacksButton_Click(true);
            };

            packUpdaterWindow.Activate();
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
