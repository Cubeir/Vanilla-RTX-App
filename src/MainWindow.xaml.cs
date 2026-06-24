using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using static Vanilla_RTX_App.Core.WindowControlsManager;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;

namespace Vanilla_RTX_App;

/* ### BACKLOG // TODO ###

- Fix any remaining compiler warnings

- ExmpImpDel and PackBrowser changes

>> About pack browser, relying on a single UUID and Version field is enough, hopefully we aren't comparing BOTH, RIGHT?!
header one is enough.
modules should only really be used for detecting if its a resource pack or not, good separation of concerns, where to "look"

- Just to be certain
not compatible with tuner, if neither tags are present
rtx, vv, rtx and vv, can co exist, the tags, capbalities
Do the animation ideas?
Or at least a static glow effect of sorts for RTX and Alchitex?

- Check the report on Discord,
Adolf Glitter of the woke reich, his report is incoherent, but try to understand
he says tuning opus a few times and the app freezes
okay, he also said there may be value mismatch between what sliders display, and what actually applies to packs in the backgroumd alright.

- do the userdatalocator expansion idea


- Stress test GDKLocator again

- manifests with comments, do features play well with them?

- Test memory usage when tuning large packs
test for memory leaks

- For any feature that deals with user RP directories:
Ensure it POOLS dev/regular folders, AND across ALL users!
For importing and selecting packs upstream it is ESPECIALLY important
PackUpdater already handles this pretty well iirc, explicitly decide all edge scenarios.


- Safeguard against loss of default RTX files by auto triggering default preset reinstalls for BetterRTX and LUT Manager upon hard reset
in the context where u already gave it all 3 classes, remember

- Fix shadows of selectable panes being cut off in pack browser and similar menus

- Do a review of all cooldowns and retry times. Audit all classes.
How psa cooldowns play with pack update cooldowns, etc..
and
Cached key accumulation 
be more cautious where it can accumulate and varies...
review web calls and GitHub usage patterns too so to speak

- Audit your github call patterns (caching, and cooldowns) -- especially updater, maximize up-to-dateness with as few requests as possible
All settled there? ensure there isn't a way the app can ddos github AND at the same time there are no unintended Blind spots

- Update the docs to be less verbose, more accurate and helpful instead, cut off unneeded details.
Update them to reflect the latest features/changes


==================== ENOUGH FOR 3.1

- Do the redesign.
Offload export and delete to PackBrowser menu, allow deletion and export on the spot

While using bindings for everything else, rip out old checkboxes code paths
Replace with a dynamic dropdown instead, allow selection of AVAILABLE/Installed Vanilla RTX resource packs, decided by PackLocator
Reset and Clear are moved to the top
Tune selection takes spot of Surface normal intensity as surface normal itnensity is moved to the right, cuz there's now enough space.

This way you can shrink the app's min height too. One Row effectively gone -- Possibly move Preview button to the Left side

It sounds a bit redundant having two select packs button, actually, rip out the ENTIRE CODE PATHs for checkboxes
PackUpdater menu prints statuses anyway, there's no use to it
LEAVE THAT AREA EMPTY, there's no harm in it
Make it sit Directly below the VANILLA RTX APP title text and logo roughly, so it draws more attention
that's actually good design! And gives some breathing room/makes it look a lot less overwhelming

And do the idea of making pack tags have Unique effects, that was nice.

For delete and export, keep using the Titlebar updates -> finish the job -> reload window strategy that importer already does

- Begin using Bindings for:
Sliders and their checkboxes (two-way binding), Preview toggle, RTX pack toggles 
The code surrounding it, especially checkboxes, is very messy
it's FUNCTIONALLY CORRECT all throughout at the moment, but it was a lot of hassle, and its messy
LocatePacks task determining whether the 3 checkboxes are togglable (.IsEnabled) or not is something u can't do with binding, and the existing code's perfect for it.
but you may be able to shorten UpdateUI,
its a bit risky touching that part of the code, cuz of its annoying bugs with the previewer, fights over vessels/otheredge cases


- Do the TODOs scattered in the code


- Reduce cache retry timers for PACK UPDATER version retrieval
it hangs too long trying to get from remote
the whole deal is that user quickly gets access to the cached version if no internet is available
this defeats the purpose if they gotta wait 59 or 30 seconds
Github raw should return it within 5-7 seconds at worst, much faster, that's it. if it does not, must resort to cache almost instantly...

- Create a BetterRTX-like lut preset, gets the looks 80% there! 


- Review use of OpenURL/Prcoess.start where you use those
Use the built-in Laucnh URI as necessary
already doing in OpenURL, It's the more correct way there 

- Implement a proper Tuning Progress bar
Update the progressbar manager, allow it to move slowly, to give an indication of the progress of the tuning process
have it update in real time, all files/processed, should be easy enough...
>> Alongside it, add the ability to Abort the operation, WHILE tuning, change tune button to "Abort Tuning Process" with a warning glyph
or &#xE730; would be nicer, &#xECE4; too

- Go over Main Window again some time, especially update ToggleControls usage, its... weird to say the least
Be more CONSISTENT with it, and ensure sidebarlogbox NEVER EVER EVER gets disabled on the main window!
Some overrides now disable it while they should not.

- When targeting preview, a new Dev branch on github
must be used to receieve updates, compare packages, etc...
easier said than done, the code is a clusterfuck
and it all depends on whether you actually need this or not, the decision upstream must help Vanilla RTX's development.
if it doesn't, this is too, is a Useless idea.

- TTService.GetToolTip could be very useful,
users don't read tooltips, hide verbose guides in there, its fine
AND PRINT THEM TO USER when they repeat a mistake a few times and cause errors.
just an idea

- Add a way to add custom presets to BetterRTX Manager (e.g. user made presets)
Give it special treatment same as default preset and avoid changing existing logic
they appear at the bottom
expects zips or rtpacks to be passed in, extracts bins and makes a custom preset, name em custom_preset_[increment]
basically, instead of changing the current pipeline, integerate this/build it on top of it
that way it'll surely work without fucking things up

- Further review PackUpdater and BetterRTX manager codes, ensure no stone is left unturned.
Especially release builds, There COULD BE LATENT TRIMMING BUGS!
Game detection and cache invalidation could be improved for both
PackUpdater may have blindspots still, though HIGHLY unlikely, still, review and test, make changes on the go

- With splash screen here, UpdateUI is useless, getting rid of it is too much work though, just too much...
It is too integerated, previewer class has some funky behavior tied to it, circumvented by it
It's a mess but it works perfectly, so, only fix it once you have an abundance of time...!

In fact, manually calling UpdateUI is NECESSERY, thank GOD you're not using bindings
UpdateUI is VERY NEEDED for Previewer class, it is already implemented everywhere and freezes vessel updates as necessery
You would've had to manually done this anyway

And the smooth transitions are worth it.

- Smoothen the startup sequence, it flashes right now. 🌟 
Use more correct ways to implement splash screen
Scrap the animation
Scrap the animation on alchitex startup sequence (it is annoying have to see it every time, hinders user)
Scrap the code for it in Lamp.cs
Allow rapid flash in Lamp.cs to happen to OFF in addition to SUPERON, decided randomly, no changes downstream

- Is the lamp halo too weak at rest? it seems inconsistent, during runtime reglar flash halos are very bright
watchya doing?

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

- Make holding shift turn the lamp Green to indicate its debugging functionality

- Account for different font scalings, windows accessibility settings, etc...
gonna need lots of painstakingly redoing xamls but if one day you have an abundance of time sure why not
*/

/// <summary>
/// Hosts the Persistent and Default variables where it mattered for it to persist between sessons,
/// or for defaults to remain accessible, as well as the methods to save and load these variables
/// </summary>
public static class TunerVariables
{
    public static string? appVersion = null;

    public static string VanillaRTXLocation = string.Empty;
    public static string VanillaRTXNormalsLocation = string.Empty;
    public static string VanillaRTXOpusLocation = string.Empty;

    public static string VanillaRTXVersion = string.Empty;
    public static string VanillaRTXNormalsVersion = string.Empty;
    public static string VanillaRTXOpusVersion = string.Empty;
    // We already know names of Vanilla RTX packs so we get version instead, for custom pack, name's enough.
    // We invalidate the retrieved name whenever we want to disable processing of the custom pack, so it has multiple purposes
    public static ObservableCollection<(string Location, string Name, string Type, bool IsAlchitexCandidate)> SelectedPacks = new();

    // Tied to checkboxes
    public static bool IsVanillaRTXEnabled = false;
    public static bool IsNormalsEnabled = false;
    public static bool IsOpusEnabled = false;


    public static class Persistent // These are saved and reloaded on app launch
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

    public static class Defaults // These are backed up to be used as a compass by other classes
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
    public const int WindowSizeX = 1150;
    public const int WindowSizeY = 640;
    public const int WindowMinSizeX = 950;
    public const int WindowMinSizeY = 615;

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
                Trace.WriteLine($"[MainWindow] An issue occured loading settings");
            }
        }
    }
}

// For dynamically updating number of other selected packs in the UI (select other packs button)
public class PackSelectionViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher
        = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public PackSelectionViewModel()
    {
        TunerVariables.SelectedPacks.CollectionChanged += OnSelectedPacksChanged;
    }

    private void OnSelectedPacksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowseButtonLabel))));
    }

    public string BrowseButtonLabel
    {
        get
        {
            int count = TunerVariables.SelectedPacks.Count;
            return count switch
            {
                0 => "Select other packs",
                1 => "Selected 1 other pack",
                _ => $"Selected {count} other packs"
            };
        }
    }
}

// --------------------------------------------\                       /-------------------------------------------- \\

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly WindowStateManager _windowStateManager;

    private readonly ProgressBarManager _progressManager;

    public readonly PackUpdater _updater = new();

    private LampAnimator? _titlebarLampAnimator;
    private LampAnimator? _splashLampAnimator;
    public PackSelectionViewModel PackVM { get; } = new PackSelectionViewModel();

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


    /// WindowStateManager
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    /// For buttons hidden under shiftkey
    private readonly Dictionary<FrameworkElement, string> _originalTexts = new();
    private readonly Dictionary<FontIcon, string> _originalGlyphs = new();
    private bool _shiftPressed = false;

    // --------------------------------------------| | | | | | | | | | |-------------------------------------------- \\

    public MainWindow()
    {
        // Properties to set before it is rendered
        SetMainWindowProperties();
        InitializeComponent();
        InitializeLampAnimators();
        SplashOverlay.Visibility = Visibility.Visible;
        SetTitleBar(TitleBarDragArea);

        _windowStateManager = new WindowStateManager(this);
        _progressManager = new ProgressBarManager(ProgressBar);

        Instance = this;

        var defaultSize = new SizeInt32(WindowSizeX, WindowSizeY);
        _windowStateManager.ApplySavedStateOrDefaults();

        // Version, title and initial logs
        var version = Windows.ApplicationModel.Package.Current.Id.Version;
        var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        appVersion = versionString;

        Log($"App Version: {appVersion}" + new string('\n', 2) +
            $"Not affiliated with Mojang or NVIDIA;\nby continuing, you consent to modifications to your Minecraft installations & data.");
        ToolTipService.SetToolTip(TitleBarText, $"Version: {appVersion}");

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
                SetShiftText(ResetButton_TextBlock, "Wipe", ResetButton_FontIcon, "\uE7BA");
                SetShiftText(LaunchButtonText, "Disable Minecraft RTX", LaunchButtonFontIcon, "\uE7A7");
                // Add more as needed...
            }
        };
        Content.KeyUp += (s, e) =>
        {
            if (e.Key == VirtualKey.Shift)
            {
                _shiftPressed = false;
                RestoreShiftText(ResetButton_TextBlock, ResetButton_FontIcon);
                RestoreShiftText(LaunchButtonText, LaunchButtonFontIcon);
                // Mirror every SetShiftText call above...
            }
        };

        // Things to do after mainwindow is initialized
        this.Activated += MainWindow_Activated;
        this.Activated += MainWindow_FocusOpacity;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        // Unsubscribe to avoid running this again
        this.Activated -= MainWindow_Activated;

        // Launch silent update immediately, hopefully by the time the startup sequence is finished, we have new PSAs to show!
        _ = OnlineTexts.TriggerUpdateAsync();

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


        // Set reinstall latest packs button visuals based on cache status (TODO: COULD maybe have a third "Update to latest" stat, but it requires checking remote on startup)
        if (_updater.HasDeployableCache())
        {
            UpdateVanillaRTXGlyph.Glyph = "\uE8F7"; // Syncfolder icon
        }
        else
        {
            UpdateVanillaRTXGlyph.Glyph = "\uEBD3"; // Default cloud icon
        }

        // Slower UI update override for a smoother startup
        UpdateUI(0.001);

        // Locate packs, if Preview is enabled, the toggle itself auto-triggers another pack location, this avoids redundant operation, when it is bound to run anyway
        if (!IsTargetingPreview)
        {
            _ = LocatePacksTask();
        }
        else
        {
            BetterRTXPresetManagerButton.IsEnabled = false;
        }

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
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {buttonName} button.", LogLevel.Warning);
        }

        // Show Leave a Review prompt
        _ = ReviewPromptManager.InitializeAsync(MainGrid);

        // By the time we get here, on good internet the OnlineTexts fetch is already done. On bad internet it may be stale cache, it's ok, we show it anyway
        // The whole idea is, there is separation of concerns, on one side, we only show what's in the cache, the app tries to update the cache sometimes
        // we deal with cache, for showing things, the app deals with updating it later
        var psa = OnlineTexts.GetFiltered(OnlineTextsContent.PSA);
        if (psa is { Length: > 0 })
        {
            for (int i = psa.Length - 1; i >= 0; i--)
                Log(psa[i].Text);
        }


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


    private void MainWindow_FocusOpacity(object sender, WindowActivatedEventArgs e)
    {
        var isFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        var opacity = isFocused ? 1.0 : 0.5;

        ChatButton.Opacity = opacity;
        HelpButton.Opacity = opacity;
        DonateButton.Opacity = opacity;
        CycleThemeButton.Opacity = opacity;
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

        // Force LtR
        if (Content is FrameworkElement root)
            root.FlowDirection = FlowDirection.LeftToRight;

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

            // 🍝 Color of that little border next to the Preview button 🍝
            if (IsTargetingPreview)
            {
                var accentColorKey = theme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3";
                LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources[accentColorKey]);
                var accentColorKeyDark = theme == ElementTheme.Light ? "SystemAccentColorDark2" : "SystemAccentColorDark1";
                RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources[accentColorKeyDark]);
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
                    if (dict.TryGetValue("FakeSplitButtonDarkBorderColor", out var darkColorObj) && darkColorObj is Color darkColor)
                    {
                        RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(darkColor);
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
        Previewer.Instance.InitializeButton(DeleteButton,
            "ms-appx:///Assets/previews/chest.delete.png"
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

        Previewer.Instance.InitializeButton(DefaultRTXModifiersButton,
            "ms-appx:///Assets/previews/lut.png"
        );

        Previewer.Instance.InitializeButton(AlchitexButton,
            "ms-appx:///Assets/previews/reactor.promo.tile.png"
        );

    }


    private void InitializeShadows()
    {
        TitleBarShadow.Receivers.Add(TitleBarShadowReceiver);
        // Top row shadow — spans both columns, add both receivers
        TopRowContentShadow.Receivers.Add(LeftShadowReceiver);
        TopRowContentShadow.Receivers.Add(RightShadowReceiver);
        // Left column shadows
        SidebarLogShadow.Receivers.Add(LeftShadowReceiver);
        CommandBarShadow.Receivers.Add(LeftShadowReceiver);
        // Right column shadows
        ClearResetShadow.Receivers.Add(RightShadowReceiver);
        BottomButtonsShadow.Receivers.Add(RightShadowReceiver);
    }


    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Debug, PSA
    }
    public static void Log(string message, LogLevel? level = null)
    {
        void Prepend()
        {
            if (Instance == null)
                return;

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
                LogLevel.PSA => "📢 ",
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
        if (Instance == null) { return; }
        if (Instance.DispatcherQueue.HasThreadAccess)
            Prepend();
        else
            Instance.DispatcherQueue.TryEnqueue(Prepend);
    }
    public static ScrollViewer? GetScrollViewer(DependencyObject obj)
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


    public static async Task OpenUrl(string url)
    {
#if DEBUG
        Log("OpenUrl is disabled in debug builds.", LogLevel.Informational);
        return;
#else
        try
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                throw new ArgumentException("Malformed URL.");

            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
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
        if (_titlebarLampAnimator == null)
        {
            Trace.WriteLine("[MainWindow] BlinkingLamp called before animators were initialized");
            return;
        }
        await _titlebarLampAnimator.Animate(enable, singleFlash, singleFlashOnChance, rotate: _titlebarLampAnimator.GetSpecialOccasionName(DateTime.Today) != "");
    }
    private async Task AnimateSplash(double splashDurationMs)
    {
        if (_splashLampAnimator == null)
        {
            Trace.WriteLine("[MainWindow] AnimateSplash called before animators were initialized");
            return;
        }
        await _splashLampAnimator.Animate(false, true, 0.9, splashDurationMs, rotate: _splashLampAnimator.GetSpecialOccasionName(DateTime.Today) != "");
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



    private void SetShiftText(FrameworkElement control, string shiftText, FontIcon? icon = null, string? shiftGlyph = null)
    {
        // Save + apply text
        if (!_originalTexts.ContainsKey(control))
        {
            if (control is Button btn) _originalTexts[control] = btn.Content?.ToString() ?? "";
            else if (control is TextBlock tb) _originalTexts[control] = tb.Text;
        }

        if (control is Button button) button.Content = shiftText;
        else if (control is TextBlock textBlock) textBlock.Text = shiftText;

        // Save + apply glyph (optional)
        if (icon != null && shiftGlyph != null)
        {
            if (!_originalGlyphs.ContainsKey(icon))
                _originalGlyphs[icon] = icon.Glyph;

            icon.Glyph = shiftGlyph;
        }
    }
    private void RestoreShiftText(FrameworkElement control, FontIcon? icon = null)
    {
        if (_originalTexts.TryGetValue(control, out var originalText))
        {
            if (control is Button button) button.Content = originalText;
            else if (control is TextBlock textBlock) textBlock.Text = originalText;
        }

        if (icon != null && _originalGlyphs.TryGetValue(icon, out var originalGlyph))
            icon.Glyph = originalGlyph;
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

                        // Special-case SelectedPacks — the tuple list won't print usefully via ToString()
                        if (field.Name == nameof(TunerVariables.SelectedPacks) &&
                            value is List<(string Location, string Name, string Type)> selectedPacks)
                        {
                            if (selectedPacks.Count == 0)
                            {
                                sb.AppendLine("SelectedPacks: (empty)");
                            }
                            else
                            {
                                sb.AppendLine("SelectedPacks:");
                                foreach (var (location, name, type) in selectedPacks)
                                    sb.AppendLine($"  [{type}] {name} → {location}");
                            }
                            continue;
                        }

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
                Trace.WriteLine($"[MainWindow] Error during lamp interaction debug copy: {ex}");
            }
        }
        else
        {
            _ = BlinkingLamp(true, true, 1.0);
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
        _ = OpenUrl("https://discord.gg/A4wv4wwYud");
    }


    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Find helpful resources in the README file, launching in your default browser shortly.", LogLevel.Informational);
        _ = OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md#documentation");
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
        ShowCreditsOnce();
        _ = OpenUrl("https://ko-fi.com/cubeir");
    }
    private void DonateButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        ShowCreditsOnce();
    }
    private void DonateButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB51";
        ShowCreditsOnce();
    }
    private void ShowCreditsOnce()
    {
        var credits = OnlineTextsContent.Credits?[0].Text;
        if (!string.IsNullOrEmpty(credits) && RuntimeFlags.Set("Wrote_Supporter_Shoutout"))
            Log(credits);
    }


    public void CycleThemeButton_Click(object? sender, RoutedEventArgs? e)
    {
        bool invokedByClick = sender is Button;
        string mode = Persistent.AppThemeMode;

        if (invokedByClick)
        {
            mode = mode switch
            {
                "System" => "Light",
                "Light" => "Dark",
                _ => "System"
            };
            Persistent.AppThemeMode = mode;
        }

        var root = Instance!.Content as FrameworkElement;
        root!.RequestedTheme = mode switch
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


    // ------- Top action bar stuff


    private Dictionary<bool, string?> _previousStatusMessages = new();
    public async Task LocatePacksTask(bool ShowLogs = false)
    {
        _ = BlinkingLamp(true, true, 1.0);

        // Reset controls
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

        VanillaRTXVersion = string.Empty;
        VanillaRTXNormalsVersion = string.Empty;
        VanillaRTXOpusVersion = string.Empty;

        // Status message
        var statusMessage = PackLocator.LocatePacks(IsTargetingPreview,
            out VanillaRTXLocation, out VanillaRTXVersion,
            out VanillaRTXNormalsLocation, out VanillaRTXNormalsVersion,
            out VanillaRTXOpusLocation, out VanillaRTXOpusVersion);

        _previousStatusMessages.TryGetValue(IsTargetingPreview, out var previousMessage);
        if (ShowLogs && statusMessage != previousMessage)
        {
            Log(statusMessage);
            _previousStatusMessages[IsTargetingPreview] = statusMessage;
        }

        // Enable checkboxes based on installation statuses
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

            if (TunerVariables.SelectedPacks.Count > 0)
            {
                var names = string.Join(", ", TunerVariables.SelectedPacks.Select(p => p.Name));
                Log($"Selected: {names}", LogLevel.Success);
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

        _ = LocatePacksTask();
        SelectedPacks.Clear();

        Log("Targeting Minecraft Preview.", LogLevel.Informational);
        var theme = LeftEdgeOfTargetPreviewButton.ActualTheme;
        var accentColorKey = theme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3";
        LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources[accentColorKey]);
        var accentColorKeyDark = theme == ElementTheme.Light ? "SystemAccentColorDark2" : "SystemAccentColorDark1";
        RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush((Color)Application.Current.Resources[accentColorKeyDark]);

        BetterRTXPresetManagerButton.IsEnabled = false;
    }
    private void TargetPreviewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = false;
        _ = BlinkingLamp(true, true, 0.0);

        _ = LocatePacksTask();
        SelectedPacks.Clear();

        Log("Targeting Release Minecraft.", LogLevel.Informational);

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
            if (dict.TryGetValue("FakeSplitButtonDarkBorderColor", out var darkColorObj) && darkColorObj is Color darkColor)
            {
                RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(darkColor);
            }
        }
        BetterRTXPresetManagerButton.IsEnabled = true;
    }


    // Vanilla RTX Checkboxes
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
        if (toggle == null) { return; }
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
            Log($"To perform a full reset of app's data if necessery, hold SHIFT key while pressing {ResetButton_TextBlock.Text}.", LogLevel.Informational);
            Log($"Note: this does not restore the packs to their default state!\nTo reset packs back to original you can quickly reinstall the latest versions of Vanilla RTX using the '{UpdateVanillaRTXButtonText.Text}' button. Other packs will require manual reinstallation. Use Export to back them up and quickly reimport them as you need.", LogLevel.Informational);
        }
        Log("Tuning environment reset.", LogLevel.Success);
    }
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        // Capture previous state
        bool hadVanillaRTX = IsVanillaRTXEnabled || IsNormalsEnabled || IsOpusEnabled;
        bool hadCustomPacks = TunerVariables.SelectedPacks.Count > 0;

        // Vanilla RTX
        IsVanillaRTXEnabled = false;
        IsNormalsEnabled = false;
        IsOpusEnabled = false;

        // Custom packs
        TunerVariables.SelectedPacks.Clear();

        // Manually update UI based on new values
        UpdateUI();

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);

        if (hadCustomPacks)
            Log("Cleared all pack selections.", LogLevel.Success);
        else if (hadVanillaRTX)
            Log("Unselected Vanilla RTX packs.", LogLevel.Success);
        else
            Log("You haven't selected any packs to clear.", LogLevel.Informational);
    }

    private async Task WipeAllStorageData()
    {
        try
        {
            Log("Starting hard reset — wiping all app storage...", LogLevel.Warning);
            await Task.Delay(200);

            // ── 1. Local Settings (recursive containers) ─────────────────────────
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var roamingSettings = Windows.Storage.ApplicationData.Current.RoamingSettings;
            int totalKeysWiped = 0;

            var containerStack = new Stack<(Windows.Storage.ApplicationDataContainer container, string path)>();
            containerStack.Push((localSettings, "LocalSettings"));
            containerStack.Push((roamingSettings, "RoamingSettings"));

            while (containerStack.Count > 0)
            {
                var (container, path) = containerStack.Pop();

                // Queue sub-containers for processing, then delete them
                foreach (var subKey in container.Containers.Keys.ToList())
                {
                    containerStack.Push((container.Containers[subKey], $"{path}/{subKey}"));
                }
                foreach (var subKey in container.Containers.Keys.ToList())
                {
                    container.DeleteContainer(subKey);
                    Log($"Deleted container: {path}/{subKey}", LogLevel.Informational);
                }

                // Wipe all values at this level
                foreach (var key in container.Values.Keys.ToList())
                {
                    container.Values.Remove(key);
                    Log($"Deleted key: {path}/{key}", LogLevel.Informational);
                    totalKeysWiped++;
                }
            }

            Log($"Wiped {totalKeysWiped} setting key(s) across all containers.", LogLevel.Success);
            await Task.Delay(100);

            // ── 2. Wipe all storage folders ───────────────────────────────────────
            var foldersToWipe = new[]
            {
            (path: Windows.Storage.ApplicationData.Current.LocalFolder.Path,      label: "LocalFolder (LocalState)"),
            (path: Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, label: "LocalCacheFolder"),
            (path: Windows.Storage.ApplicationData.Current.TemporaryFolder.Path,  label: "TemporaryFolder"),
        };

            int totalItemsDeleted = 0;

            foreach (var (path, label) in foldersToWipe)
            {
                Log($"Wiping {label}: {path}", LogLevel.Informational);
                int deletedInFolder = 0;

                if (!Directory.Exists(path))
                {
                    Log($"{label} not found, skipping.", LogLevel.Informational);
                    continue;
                }

                foreach (var file in Directory.GetFiles(path))
                {
                    try
                    {
                        File.Delete(file);
                        Log($"Deleted file: {Path.GetFileName(file)}", LogLevel.Informational);
                        deletedInFolder++;
                        await Task.Delay(10);
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not delete file {Path.GetFileName(file)}: {ex.Message}", LogLevel.Warning);
                    }
                }

                foreach (var dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        Log($"Deleted folder: {Path.GetFileName(dir)}", LogLevel.Informational);
                        deletedInFolder++;
                        await Task.Delay(15);
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not delete folder {Path.GetFileName(dir)}: {ex.Message}", LogLevel.Warning);
                    }
                }

                Log($"{label} wiped ({deletedInFolder} item(s)).", LogLevel.Success);
                totalItemsDeleted += deletedInFolder;
            }

            Log($"Deleted {totalItemsDeleted} file/folder item(s) total.", LogLevel.Success);
            await Task.Delay(500);
            Log("Hard reset complete! Restarting in a moment...", LogLevel.Success);
            await Task.Delay(3000);

            Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
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

    private void DefaultRTXModifiersButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleControls(this, false, true, []);

        var rtxWindow = new Vanilla_RTX_App.RTXDefaults.DefaultRTXModifiersWindow(this);
        var mainAppWindow = this.AppWindow;

        rtxWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        rtxWindow.AppWindow.Move(mainAppWindow.Position);

        rtxWindow.Closed += (s, args) =>
        {
            ToggleControls(this, true, true, []);

            if (rtxWindow.OperationSuccessful)
            {
                Log(rtxWindow.StatusMessage, LogLevel.Success);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (!string.IsNullOrEmpty(rtxWindow.StatusMessage))
            {
                Log(rtxWindow.StatusMessage, LogLevel.Error);
                _ = BlinkingLamp(true, true, 0.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        rtxWindow.Activate();
    }

    private void AlchitexButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleControls(this, false, true, []);
        var alchitexWindow = new Vanilla_RTX_App.Modules.Alchitex.Alchitex(this);
        var mainAppWindow = this.AppWindow;
        alchitexWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        alchitexWindow.AppWindow.Move(mainAppWindow.Position);
        alchitexWindow.Closed += (s, args) =>
        {
            ToggleControls(this, true, true, []);
        };
        alchitexWindow.Activate();
    }




    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        // Build the full list of pack locations to delete:
        // the three Vanilla RTX packs (if enabled) plus every custom selected pack.
        var toDelete = new List<(string location, string displayName)>();

        if (IsVanillaRTXEnabled && Directory.Exists(VanillaRTXLocation))
            toDelete.Add((VanillaRTXLocation, "Vanilla RTX"));
        if (IsNormalsEnabled && Directory.Exists(VanillaRTXNormalsLocation))
            toDelete.Add((VanillaRTXNormalsLocation, "Vanilla RTX Normals"));
        if (IsOpusEnabled && Directory.Exists(VanillaRTXOpusLocation))
            toDelete.Add((VanillaRTXOpusLocation, "Vanilla RTX Opus"));

        foreach (var (location, name, _, _) in TunerVariables.SelectedPacks)
            if (!string.IsNullOrEmpty(location) && Directory.Exists(location))
                toDelete.Add((location, name));

        if (toDelete.Count == 0)
        {
            Log("Select at least one pack to delete.", LogLevel.Warning);
            return;
        }

        // Confirm with the user before nuking anything from disk.
        var dialog = new ContentDialog
        {
            Title = "Delete selected packs?",
            Content = $"This will permanently delete {toDelete.Count} pack{(toDelete.Count == 1 ? "" : "s")} from disk. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _progressManager.ShowProgress();
        ToggleControls(this, false);

        int deletedCount = 0;

        try
        {
            _ = BlinkingLamp(true, true, 0.0);

            // Deduplicate by normalised path so the same folder isn't deleted twice.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (location, displayName) in toDelete)
            {
                var normalised = Path.GetFullPath(location)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!seenPaths.Add(normalised))
                {
                    Log($"{displayName} was in the list more than once — skipping duplicate selection.", LogLevel.Warning);
                    continue;
                }

                var deleted = await ExpImpDel.DeletePackAsync(location);
                if (deleted != null)
                {
                    deletedCount++;
                    Log($"Deleted {displayName}.", LogLevel.Success);
                }
                else
                {
                    Log($"Could not delete {displayName}.\nsee trace output for details by holding shift while clicking the lamp icon.", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Delete failed: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            if (deletedCount == 0)
                Log("No packs were deleted.", LogLevel.Warning);

            _progressManager.HideProgress();
            ToggleControls(this, true);

            _ = LocatePacksTask(); // Controls get enabled, their state was captured before deletion, so we re-locate AFTER they're restored, so it properly disables packs that aren't there anymore
            SelectedPacks.Clear();
            UpdateUI(); // Just in case, truly don't know why, prolly afraid of checkboxes remaining "on" visually while disabled, while the bool being off
        }
    }


    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        _progressManager.ShowProgress();
        ToggleControls(this, false);

        int exportedCount = 0;

        try
        {
            var exportQueue = new List<(string path, string name)>();
            var suffix = $"_{appVersion}_App_Export";

            // ── Vanilla RTX packs ──────────────────────────────
            if (IsVanillaRTXEnabled && Directory.Exists(VanillaRTXLocation))
                exportQueue.Add((VanillaRTXLocation, "Vanilla_RTX_" + VanillaRTXVersion + suffix));
            if (IsNormalsEnabled && Directory.Exists(VanillaRTXNormalsLocation))
                exportQueue.Add((VanillaRTXNormalsLocation, "Vanilla_RTX_Normals_" + VanillaRTXNormalsVersion + suffix));
            if (IsOpusEnabled && Directory.Exists(VanillaRTXOpusLocation))
                exportQueue.Add((VanillaRTXOpusLocation, "Vanilla_RTX_Opus_" + VanillaRTXOpusVersion + suffix));

            // ── All selected custom packs ─────────────────────────────────────────
            foreach (var (location, name, _, _) in TunerVariables.SelectedPacks)
            {
                if (!string.IsNullOrEmpty(name) && Directory.Exists(location))
                    exportQueue.Add((location, SanitizeFileName(name) + suffix));
            }

            string SanitizeFileName(string name)
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                var sanitized = new string(name
                    .Select(c => char.IsWhiteSpace(c) || invalidChars.Contains(c) ? '_' : c)
                    .ToArray());
                return Regex.Replace(sanitized.Trim('_'), "_{2,}", "_");
            }

            // ── Eradicate dupes w/ normalised path ────────────────────────────────────
            var seenPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dedupedQueue = new List<(string path, string name)>();

            foreach (var (path, name) in exportQueue)
            {
                var normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (seenPaths.ContainsKey(normalizedPath))
                {
                    Log($"{seenPaths[normalizedPath]} was selected twice, but will only be exported once!", LogLevel.Warning);
                }
                else
                {
                    seenPaths.Add(normalizedPath, name.Replace(suffix, ""));
                    dedupedQueue.Add((path, name));
                }
            }

            foreach (var (path, name) in dedupedQueue)
            {
                var exportedPath = await ExpImpDel.ExportMCPACK(path, name);
                if (exportedPath != null)
                {
                    exportedCount++;
                    Log($"Finished exporting {name} to {exportedPath}", LogLevel.Success);
                }

                _ = BlinkingLamp(true, true, 1.0);
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), LogLevel.Warning);
        }
        finally
        {
            bool nothingSelected =
                !IsVanillaRTXEnabled &&
                !IsNormalsEnabled &&
                !IsOpusEnabled &&
                TunerVariables.SelectedPacks.Count == 0;

            if (nothingSelected)
                Log("Select at least one pack to export.", LogLevel.Warning);
            else if (exportedCount == 0)
                Log("All exports failed.", LogLevel.Warning);

            _progressManager.HideProgress();
            ToggleControls(this, true);
        }
    }


    private async void TuneSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);

        try
        {
            bool hasVanillaPacks = IsVanillaRTXEnabled || IsNormalsEnabled || IsOpusEnabled;
            bool hasCompatibleCustom = TunerVariables.SelectedPacks.Any(p => p.Type != "Incompatible");
            bool hasIncompatibleCustom = TunerVariables.SelectedPacks.Any(p => p.Type == "Incompatible");

            // Mixed: at least one compatible AND at least one incompatible in the custom selection
            if ((hasVanillaPacks || hasCompatibleCustom) && hasIncompatibleCustom)
                Log("Some of the selected packs are not RTX compatible & will be excluded from the tuning process.", LogLevel.Warning);

            if (!hasVanillaPacks && !hasCompatibleCustom)
            {
                if (hasIncompatibleCustom)
                    Log("None of the selected packs are RTX or Vibrant Visuals compatible. Select at least one compatible pack to tune.", LogLevel.Warning);
                else
                    Log("Select at least one pack to tune.", LogLevel.Warning);
                return;
            }
            else
            {
                _progressManager.ShowProgress();
                _ = BlinkingLamp(true);
                ToggleControls(this, false);

                var tuningMessage = await Task.Run(Processor.TuneSelectedPacks);
                Log(tuningMessage, LogLevel.Success);
            }
        }
        catch (Exception ex)
        {
            Log($"Something went wrong during the tuning process: {ex.ToString}", LogLevel.Error);
        }
        finally
        {
            _ = BlinkingLamp(false);
            ToggleControls(this, true);
            _progressManager.HideProgress();
        }
    }


    private async void UpdateVanillaRTXButton_Click(object sender, RoutedEventArgs e)
    {
        // The UI display text relies on this, rerun it just in case, few ms overhead worth it
        try
        {
            await LocatePacksTask();
        }
        finally
        {
            if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            {
                Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
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
                }
                else
                {
                    UpdateVanillaRTXGlyph.Glyph = "\uEBD3";
                }
                _ = LocatePacksTask(true); // Trigger an automatic pack location check after update (fail or not) -- only time we log statuses
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

        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var isShiftHeld = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        try
        {
            var logs = isShiftHeld
                ? await Modules.Launcher.LaunchMinecraftStandardAsync(IsTargetingPreview)
                : await Modules.Launcher.LaunchMinecraftRTXAsync(IsTargetingPreview);

            Log(logs, LogLevel.Informational);
        }
        finally
        {
            _ = BlinkingLamp(true, true, 0.0);
        }
    }
}
