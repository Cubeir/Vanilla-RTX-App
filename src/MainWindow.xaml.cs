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
using System.Threading;
using System.Threading.Tasks;
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
using Windows.Storage;
using Windows.System;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.Modules.Helpers;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;

namespace Vanilla_RTX_App;

/// <summary>
/// Hosts the Persistent and Default variables where it mattered for it to persist between sessons,
/// or for defaults to remain accessible, as well as the methods to save and load these variables
/// </summary>
public static class TunerVariables
{
    public static string? appVersion = App.GetAppVersion();

    public static string VanillaRTXLocation = string.Empty;
    public static string VanillaRTXNormalsLocation = string.Empty;
    public static string VanillaRTXOpusLocation = string.Empty;

    public static string VanillaRTXVersion = string.Empty;
    public static string VanillaRTXNormalsVersion = string.Empty;
    public static string VanillaRTXOpusVersion = string.Empty;

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

        public static string? MinecraftDataPath = null;
        public static string? MinecraftPreviewDataPath = null;

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

    // Window size defaults for all windows
    public const int WindowSizeX = 1150;
    public const int WindowSizeY = 630;
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

    // When set, overrides the count-based label entirely.
    // Set to null to restore normal behavior.
    private string? _labelOverride;

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
            if (_labelOverride != null)
                return _labelOverride;

            int count = TunerVariables.SelectedPacks.Count;
            return count switch
            {
                0 => "Select other packs",
                1 => "Selected 1 other pack",
                _ => $"Selected {count} other packs"
            };
        }
    }

    public void SetLabelOverride(string? label)
    {
        _labelOverride = label;
        _dispatcher.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowseButtonLabel))));
    }
}

// --------------------------------------------\                       /-------------------------------------------- \\

public sealed partial class MainWindow : Window
{
    #region MainWindow Boilerplate
    public static MainWindow? Instance { get; private set; }

    private readonly List<Window> _childWindows = new();
    private bool _isClosing = false;
    private bool _isInitializing = true;

    private readonly ProgressBarManager _progressManager;

    public readonly PackUpdater _updater = new();

    private LampAnimator? _titlebarLampAnimator;
    private LampAnimator? _splashLampAnimator;
    public PackSelectionViewModel PackVM { get; } = new();

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
            superImage: SplashLampOverlay
        );

        // Initialize both immediately to preload and set special occasion images
        await Task.WhenAll(
            _titlebarLampAnimator.InitializeAsync(),
            _splashLampAnimator.InitializeAsync()
        );
    }
    private void InitializePreviewerImages()
    {
        var occasion = GetSpecialOccasionName();
        var (prefix, count) = occasion switch
        {
            "birthday" => ("vrtx.birthday", 3),
            "pumpkin" => ("vrtx.pumpkin", 3),
            "christmas" => ("vrtx.christmas", 5),
            _ => ("vrtx.app", 50)
        };
        var PreviewArt = Enumerable.Range(1, count)
            .Select(i => $"ms-appx:///Assets/previews/{prefix}.{i}.png").ToArray();
        Previewer.Instance.InitializeButton(LampInteractionButton, PreviewArt);

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
        if (GetSpecialOccasionName() == "birthday")
        {
            Previewer.Instance.InitializeCheckBox(NormalsCheckBox,
                "ms-appx:///Assets/previews/checkbox.normals.ticked.birthday.png",
                "ms-appx:///Assets/previews/checkbox.normals.unticked.birthday.png");
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

        Previewer.Instance.InitializeButton(LaunchPackUpdateButton,
            GetSpecialOccasionName() == "christmas"
                ? "ms-appx:///Assets/previews/version.checker.christmas.png"
                : "ms-appx:///Assets/previews/version.checker.png"
        );

        Previewer.Instance.InitializeButton(TuneSelectionButton,
            "ms-appx:///Assets/previews/table.tune.png"
        );

        Previewer.Instance.InitializeButton(LaunchMinecraftButton,
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

        Previewer.Instance.InitializeButton(LaunchBetterRTXManagerButton,
            "ms-appx:///Assets/previews/brtx.png"
        );

        Previewer.Instance.InitializeButton(LaunchDLSSSwapperButton,
            "ms-appx:///Assets/previews/dlss.png"
        );

        Previewer.Instance.InitializeButton(LaunchLUTManagerButton,
            "ms-appx:///Assets/previews/lut.png"
        );

        Previewer.Instance.InitializeButton(LaunchAlchitexButton,
            "ms-appx:///Assets/previews/reactor.promo.tile.png"
        );

        _ = Previewer.Instance.PreloadAllRegisteredImagesAsync();
    }

    /// For buttons hidden under shiftkey
    private readonly Dictionary<FrameworkElement, string> _originalTexts = new();
    private readonly Dictionary<FontIcon, string> _originalGlyphs = new();
    private bool _shiftPressed = false;

    #endregion

    public MainWindow()
    {
        // Set Window-level properties before initializing
        SetMainWindowProperties();
        InitializeComponent();

        InitializeLogTypewriter();
        InitializeLampAnimators();
        SetTitleBar(TitleBarDragArea);
        Instance = this;
        _progressManager = new ProgressBarManager(ProgressBar);

        // Do upon app closure
        this.Closed += (s, e) =>
        {
            _isClosing = true;

            SaveSettings();

            _typewriterTimer?.Stop();

            // Cascade closure of all windows
            foreach (var child in _childWindows.ToList())
            {
                try { child.Close(); } catch (COMException) { /* already gone */ }
            }
        };

        // For dynamiclly changing text with Shift key
        Content.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Shift && !_shiftPressed)
            {
                _shiftPressed = true;
                SetShiftText(ResetButton_TextBlock, "Wipe", ResetButton_FontIcon, "\uE7BA");
                SetShiftText(LaunchButtonText, "Launch Minecraft RTX (w/ VSync)", LaunchButtonFontIcon, "\uEC74");
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
        // Tie in colors of these special fake titlebar buttons
        this.Activated += (s, e) =>
        {
            var isFocused = e.WindowActivationState != WindowActivationState.Deactivated;
            var opacity = isFocused ? 1.0 : 0.5;

            ChatButton.Opacity = opacity;
            HelpButton.Opacity = opacity;
            DonateButton.Opacity = opacity;
            CycleThemeButton.Opacity = opacity;
        };
        // Things to do after mainwindow is initialized...
        if (Content is FrameworkElement root)
            root.Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FrameworkElement? root = Content as FrameworkElement;

            if (root != null)
                root.Loaded -= MainWindow_Loaded;

            // Load variables back in from previous session
            LoadSettings();

            // APPLY THEME, passing nulls means it isn't a button, instead of cycling, it applies the loaded setting
            CycleThemeButton_Click(null, null);

            // Give the window time to render
            await Task.Delay(100);

            // Apply some colors, then continue to watch theme changes and adjust based on theme
            if (root != null)
            {
                ThemeService.ApplyTitleBarColors(this.AppWindow, root.ActualTheme);
                root.FlowDirection = FlowDirection.LeftToRight;

                ApplyTargetPreviewBevelColors(root.ActualTheme);
                ApplyLocateUserDataColors(root.ActualTheme);
                TargetPreviewToggle.IsEnabledChanged += (s, e) =>
                {
                    if (_isClosing) return; // It crashes the app if we try to set it while window is closed, duh!
                    ApplyTargetPreviewBevelColors(((FrameworkElement)Content).ActualTheme);
                };
                BrowsePacksButton.IsEnabledChanged += (s, e) =>
                {
                    if (_isClosing) return;
                    ApplyLocateUserDataColors(((FrameworkElement)Content).ActualTheme);
                };

                root.ActualThemeChanged += (_, __) =>
                {
                    if (_isClosing) return;
                    ThemeService.ApplyTitleBarColors(this.AppWindow, root.ActualTheme);
                    ApplyTargetPreviewBevelColors(root.ActualTheme);
                    ApplyLocateUserDataColors(root.ActualTheme);

                    ThemeService.Broadcast(root.ActualTheme);
                };
            }

            // Check for crash logs, might summon a ContentDialogue
            await CheckForCrashLog();

            // Splash Blinking Animation
            _ = AnimateSplash(100);

            // Attach previewer/art vessels
            Previewer.Initialize(PreviewVesselTop, PreviewVesselBottom, PreviewVesselBackground);

            // Set reinstall latest packs button visuals based on cache status
            // It is also set after closing pack update window, don't forget to update it there if done here
            // TODO: COULD maybe have a third "Update to latest?" stat to return, but it requires checking remote on startup)
            // a way to let user know of Vanilla RTX updates inside the main window.
            if (_updater.HasDeployableCache())
            {
                UpdateVanillaRTXGlyph.Glyph = "\uE8F7"; // Syncfolder icon
            }
            else
            {
                UpdateVanillaRTXGlyph.Glyph = "\uEBD3"; // Default cloud icon
            }

            // Update UI to reflect loaded settings
            UpdateUI(0.01);

            // Calling it last since it might add a bit of delay as it searches a few dirs and files
            MinecraftGDKLocator.ValidateAndUpdateCachedLocations();

            // Assign Previewer images a bit after attaching vessels, just a safety gap
            InitializePreviewerImages();

            // Brief delay to ensure everything is fully locked and loaded, then fade out splash screen
            await Task.Delay(700);
            // ================ Do all UI updates you DON'T want to be seen BEFORE here, and for what you want seen, AFTER here =======================

            Log($"App Version: {appVersion}" + new string('\n', 2) +
                $"Not affiliated with Mojang or NVIDIA;\nby continuing, you consent to modifications to your Minecraft installations & data.");
            ToolTipService.SetToolTip(TitleBarText, $"Version: {appVersion}");

            // Warning if MC is running
            if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            {
                Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
            }


            _isInitializing = false; // This makes sure ONLY the earlier call from UpdateUI -> TogglePreview_checked is blocked from running similar operations as below, aka, Unblocks these operations from running in regular Preview button toggles
            MinecraftUserDataLocator.ValidateAndUpdateCachedLocations(); // Similar to GDKLocator but faster since it deals with fewer passes, and we want its warning messages
            UpdateUserDataDependentUI(IsTargetingPreview); // Updates UI based on location cache status
            Bindings.Update(); // Update bindings cause of a x:Bind gotcha where values come alive after some unrelated property change

            _ = LocatePacksTask(); // Trigger finding packs

            // By the time we get here, on good internet the OnlineTexts fetch is already done (called from App.xaml.cs). On bad internet it may be stale cache, it's ok, we show it anyway
            // The whole idea is, there is separation of concerns, on this side, we only show what's in the cache, the app tries to update the cache sometimes
            // we deal with cache, for showing things, another task deals with updating sometimes it at App start
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                var psa = OnlineTexts.GetFiltered(OnlineTextsContent.PSA);
                if (psa is { Length: > 0 })
                {
                    for (int i = psa.Length - 1; i >= 0; i--)
                    {
                        Log(psa[i].Text);
                        await Task.Delay(700);
                    }
                }
            });

            // Random previewer image
            var occasion = GetSpecialOccasionName();
            var (prefix, count) = occasion switch
            {
                "birthday" => ("vrtx.birthday", 3),
                "pumpkin" => ("vrtx.pumpkin", 3),
                "christmas" => ("vrtx.christmas", 5),
                _ => ("vrtx.app", 50)
            };
            int rng = Random.Shared.Next(1, count + 1);
            Previewer.Instance.SetStartupImages($"ms-appx:///Assets/previews/{prefix}.{rng}.png");

            await FadeOutSplashScreen();

            // Show Leave a Review prompt
            _ = ReviewPromptManager.InitializeAsync(MainGrid);

            // ============= End
            async Task FadeOutSplashScreen()
            {
                if (SplashOverlay == null) return;

                var fadeOut = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(100)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
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
        catch (Exception ex)
        {
            App.WriteCrashLog("Mainwindow_Loaded ", ex?.Message ?? "Unknown error", ex!.ToString());
        }
    }

    #region Main Window properties and essential components used throughout the app
    private void SetMainWindowProperties()
    {
        ExtendsContentIntoTitleBar = true;
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var manager = WinUIEx.WindowManager.Get(this);
        manager.PersistenceId = "MainWindow";
        manager.Width = WindowSizeX; // WinUIEx scales internally
        manager.Height = WindowSizeY;
        manager.MinWidth = WindowMinSizeX;
        manager.MinHeight = WindowMinSizeY;
        manager.IsResizable = true;
        manager.IsMaximizable = true;

        // Center window if we no saved pos, by quering if "WinUIEx" key container exists or not, which keep saved positions)
        var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (!settings.Containers.ContainsKey("WinUIEx"))
            this.CenterOnScreen();

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.ico"));
    }

    private async Task CheckForCrashLog()
    {
        try
        {
            var logPath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "last_session_crash_log.txt");

            if (!File.Exists(logPath)) return;

            var content = File.ReadAllText(logPath);
            File.Delete(logPath);

            var copyButton = new Button
            {
                Content = "Copy Crash Logs",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var githubLink = new HyperlinkButton
            {
                Content = "Create an issue on GitHub",
                NavigateUri = new Uri("https://github.com/Cubeir/Vanilla-RTX-App/issues"),
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(4, 4, 4, 4)
            };

            var discordLink = new HyperlinkButton
            {
                Content = "Create a post on the Vanilla RTX Discord Server",
                NavigateUri = new Uri("https://discord.gg/A4wv4wwYud"),
                Padding = new Thickness(4, 4, 4, 4)
            };

            var logBox = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = content,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 200,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dismissButton = new Button
            {
                Content = "Continue Using the App (dismisses the report)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var panel = new StackPanel { Spacing = 12 };

            panel.Children.Add(new TextBlock
            {
                Text = "Oh no! Looks like a crash occurred during the previous session, you may continue to use the app, but it would be better if you report it to the developer to see it patched up soon!",
                TextWrapping = TextWrapping.Wrap
            });

            var linksPanel = new StackPanel { Spacing = 2 };
            linksPanel.Children.Add(new TextBlock { Text = "Report using one of the following methods:" });
            linksPanel.Children.Add(githubLink);
            linksPanel.Children.Add(discordLink);
            panel.Children.Add(linksPanel);

            panel.Children.Add(new TextBlock
            {
                Text = "Crash details:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(logBox);
            panel.Children.Add(copyButton);
            panel.Children.Add(dismissButton);

            var dialog = new ContentDialog
            {
                Title = "Previous Session Crash Report",
                Content = panel,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
            };

            copyButton.Click += async (s, e) =>
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                copyButton.Content = "Copied!";
                await Task.Delay(1500);
                copyButton.Content = "Copy Crash Log";
            };

            dismissButton.Click += (s, e) => dialog.Hide();

            await dialog.ShowAsync();
        }
        catch { }
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

    public async Task BlinkingLamp(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75, double rapidFlashChance = 0.05)
    {
        await _titlebarLampAnimator!.Animate(enable, singleFlash, singleFlashOnChance, rotate: GetSpecialOccasionName() != null, rapidFlashChance: rapidFlashChance);
    }
    private async Task AnimateSplash(double splashDurationMs)
    {
        await _splashLampAnimator!.Animate(false, true, 0.9, duration: splashDurationMs, rotate: GetSpecialOccasionName() != null, rapidFlashChance: 0.01);
    }


    public async void UpdateUI(double animationDurationSeconds = 0.15)
    {
        // Suppress Previewer Updates
        Previewer.Instance.Freeze();

        // 1. Match bool-based UI elements to their current bools
        TargetPreviewToggle.IsChecked = Persistent.IsTargetingPreview;
        EmissivityAmbientLightToggle.IsOn = Persistent.AddEmissivityAmbientLight;
        VanillaRTXCheckBox.IsChecked = TunerVariables.IsVanillaRTXEnabled;
        NormalsCheckBox.IsChecked = TunerVariables.IsNormalsEnabled;
        OpusCheckBox.IsChecked = TunerVariables.IsOpusEnabled;

        // Sliders/texbox pairs
        var sliderConfigs = new[]
        {
        (FogMultiplierSlider, FogMultiplierBox, Persistent.FogMultiplier, false),
        (EmissivityMultiplierSlider, EmissivityMultiplierBox, Persistent.EmissivityMultiplier, false),
        (NormalIntensitySlider, NormalIntensityBox, (double)Persistent.NormalIntensity, true),
        (MaterialNoiseSlider, MaterialNoiseBox, (double)Persistent.MaterialNoiseOffset, true),
        (RoughenUpSlider, RoughenUpBox, (double)Persistent.RoughnessControlValue, true),
        (LazifyNormalsSlider, LazifyNormalsBox, (double)Persistent.LazifyNormalAlpha, true)
        };

        // 2. Animate sliders (intentionally put here, don't move up or down)
        await AnimateSliders(sliderConfigs, animationDurationSeconds);

        // Resume Previewer Updates
        Previewer.Instance.Unfreeze();

        async Task AnimateSliders((Slider slider, TextBox textBox, double targetValue, bool isInteger)[] configs, double durationSeconds)
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


    #region Titlebar Features -------------------------------
    private static int lampSecretMessageCounter = 0;
    private void LampInteraction_Click(object sender, RoutedEventArgs e)
    {
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            try
            {
                var sb = new StringBuilder();
                // Original sidebar log (important status messages)
                sb.AppendLine($"===== Sidebar Log (Last {MaxLogChars.ToString()} Chars)");
                string logSnapshot;
                lock (_logGate) logSnapshot = LogText;
                sb.AppendLine(logSnapshot.Replace(EntrySentinel, Environment.NewLine));
                sb.AppendLine();
                // Tuner variables
                sb.AppendLine("===== Tuner Variables");
                var fields = typeof(TunerVariables).GetFields(BindingFlags.Public | BindingFlags.Static);

                foreach (var field in fields)
                {
                    var value = field.GetValue(null);

                    // Special-case SelectedPacks - the tuple list won't print usefully via ToString()
                    if (field.Name == nameof(TunerVariables.SelectedPacks) &&
                        value is ObservableCollection<(string Location, string Name, string Type, bool IsAlchitexCandidate)> selectedPacks)
                    {
                        if (selectedPacks.Count == 0)
                        {
                            sb.AppendLine("SelectedPacks: (empty)");
                        }
                        else
                        {
                            sb.AppendLine("SelectedPacks:");
                            foreach (var (location, name, type, isAlchitexCandidate) in selectedPacks)
                                sb.AppendLine($"  [{type}] {name} → {location}{(isAlchitexCandidate ? " (Alchitex candidate)" : "")}");
                        }
                        continue;
                    }
                    else if (value is System.Collections.IEnumerable enumerable && value is not string)
                    {
                        var items = enumerable.Cast<object>().ToList();
                        sb.AppendLine(items.Count == 0 ? $"{field.Name}: (empty)" : $"{field.Name}:");
                        foreach (var item in items)
                            sb.AppendLine($"  {FormatValue(item)}");
                        continue;
                    }

                    sb.AppendLine($"{field.Name}: {value ?? "null"}");
                }

                static string FormatValue(object? value)
                {
                    if (value is null) return "null";
                    var type = value.GetType();
                    if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple"))
                        return string.Join(", ", type.GetFields().Select(f => f.GetValue(value)));
                    return value.ToString() ?? "null";
                }

                sb.AppendLine();
                // Persistent variables
                sb.AppendLine("===== Persistent Tuner Variables");
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
                Log("Copied stack trace to clipboard.", LogLevel.Success);
                _ = BlinkingLamp(true, true, 0.0, 1.0);

            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MainWindow] Error during lamp interaction debug copy: {ex}");
            }
        }
        else // regular non-shift clicks
        {
            _ = BlinkingLamp(true, true, 1.0, 0.1);
            if (RuntimeFlags.Set("Has_said_the_Thing_about_Debug_Logs_something"))
            {
                Log("Holding shift while clicking the lamp will copy app's stack trace. Attach these if reporting issues or debugging.", LogLevel.Debug);
            }
            else
            {
                lampSecretMessageCounter++;
                if (lampSecretMessageCounter > (DateTime.Now.Year - 2005)) // it amounts to having to click 1 more time every year, starting in 2026, 21 times
                {
                    if (RuntimeFlags.Set("Has_said_the_Thing_about_Debug_Logs_something_2"))
                    {
                        Log("What? you're expecting some kind of hidden message?? Believe me I've crammed enough of those throughout the app already.", LogLevel.VanillaRTX);
                        Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            Log("But now that you've found this one in particular, I won't leave you empty-handed. Wait a couple of seconds...", LogLevel.Lengthy);
                            await Task.Delay(4000);
                            _ = OpenUrl("https://youtu.be/1MhB8mF10H4?si=UragVyvGtqUgm4Oi&t=450");
                            await Task.Delay(3014);
                            Log("I just love this piece! That's it. Hope you like it too.", LogLevel.Debug);
                            await Task.Delay(delay: TimeSpan.FromMinutes(10));
                            Log("The secret message you triggered ten minutes ago wasn't done yet... It might do something in: 5 hours.", LogLevel.Lengthy);
                            await Task.Delay(delay: TimeSpan.FromHours(7));
                            Log("This was Cubeir, creator of Vanilla RTX, this app, and everything else around it...", LogLevel.VanillaRTX);
                            await Task.Delay(2718);
                            Log("If people knew the amount of love, effort, and difficulty I had to go through to keep this up, maybe they'd appreciate it.. just a tiny bit more?", LogLevel.Error);
                            await Task.Delay(2718);
                            Log("Despite everything, I continued; Out of necessity. Never wavered. That is how good things are made after all!", LogLevel.Warning);

                            int iteration = 0;
                            var rng = Random.Shared;
                            string[] baseMsgs = { "If people knew the amount of love, effort, and difficulty I had to go through to keep this up, maybe they'd appreciate it.. just a tiny bit more?",
                                                 "Despite everything, I continued; Out of necessity. Never wavered. That is how good things are made after all!" };
                            LogLevel[] levels = { LogLevel.Warning, LogLevel.Error, LogLevel.PSA, LogLevel.Lengthy };
                            string[] spookyEmojis = { "👁️" };

                            // Deteriorate the message over time, then make it seem like It's lagging to creep out the user
                            while (true)
                            {
                                iteration++;

                                string baseMsg = baseMsgs[rng.Next(baseMsgs.Length)];
                                char[] chars = baseMsg.ToCharArray();
                                double c = iteration / 50.0;
                                int corruptCount = (int)(chars.Length * c);
                                double t = Math.Max(0, (iteration - 15) / 35.0);
                                int delay = (int)(500 + 9500 * (t * t * t));

                                for (int i = 0; i < corruptCount; i++)
                                {
                                    int pos = rng.Next(chars.Length);
                                    chars[pos] = (char)rng.Next(33, 126);
                                }

                                // Sprinkle creepy emojis at random positions
                                string msg = new string(chars);
                                int emojiCount = rng.Next(1, 4);
                                for (int i = 0; i < emojiCount; i++)
                                {
                                    if (rng.NextDouble() < 0.1)
                                    {
                                        int pos = rng.Next(msg.Length);
                                        msg = msg.Insert(pos, spookyEmojis[rng.Next(spookyEmojis.Length)]);
                                    }
                                }

                                await Task.Delay(delay);
                                Log(msg, levels[rng.Next(levels.Length)]);
                            }
                        });
                    }
                }
            }
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
        RollCredits();
        _ = OpenUrl("https://ko-fi.com/cubeir");
    }
    private void DonateButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB52";
        RollCredits();
    }
    private void DonateButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        DonateButton.Content = "\uEB51";
        RollCredits();
    }
    private void RollCredits()
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

        ElementTheme targetTheme = mode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (root!.RequestedTheme != targetTheme)
            root.RequestedTheme = targetTheme;

        Button btn = (sender as Button) ?? CycleThemeButton;

        // Visual Feedback
        btn.Content = mode == "System"
            ? new TextBlock
            {
                Text = "A",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 15
            }
            : mode switch
            {
                "Light" => "\uE706",
                "Dark" => "\uEC46",
                _ => "A",
            };

        ToolTipService.SetToolTip(btn, "Theme: " + mode);
    }


    #endregion ------------------------------- Titlebar Features


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



    private async void BrowsePacksButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
             "LaunchAlchitexButton", "LaunchPackUpdateButton",
              "TuneSelectionButton", "ExportButton", "DeleteButton", "BrowsePacksButton", "ClearButton", "ResetButton"
        ];
        // If user data isn't valid for the current edition, repurpose this click
        // to let the user locate the data folder manually instead.
        if (!MinecraftUserDataLocator.IsDataValid(IsTargetingPreview))
        {
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);
            await HandleManualDataLocationAsync();
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);
            return;
        }

        // The Usual Pack browser flow ============ Above is repurposed functionality of the button in case user data is missing

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

        var packBrowserWindow = new PackBrowser.PackBrowserWindow();
        var mainAppWindow = this.AppWindow;

        packBrowserWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        packBrowserWindow.AppWindow.Move(mainAppWindow.Position);

        packBrowserWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(packBrowserWindow);

            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            if (TunerVariables.SelectedPacks.Count > 0)
            {
                var names = string.Join(Environment.NewLine, TunerVariables.SelectedPacks.Select(p => p.Name));
                Log($"Selected the following:\n{names}", LogLevel.Selected);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (SelectedPacks.Count == 0)
            {
                Log($"Selected:\nNothing{(Random.Shared.Next(1,3) > 1 ? ", literally!" : ".")}", LogLevel.Selected);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        WindowControlsManager.Activate(packBrowserWindow);
    }

    public async Task HandleManualDataLocationAsync()
    {
        _ = BlinkingLamp(false, true, 0.5, 1.0);

        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(IsTargetingPreview);
        var expectedName = IsTargetingPreview
            ? MinecraftUserDataLocator.PreviewRootFolderName
            : MinecraftUserDataLocator.StableRootFolderName;

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        // Accept the selected folder directly, or its parent if the user navigated
        // one level too deep (e.g. selected "Users" instead of "Minecraft Bedrock")
        string? acceptedPath = null;

        if (MinecraftUserDataLocator.TrySetCustomDataRoot(IsTargetingPreview, folder.Path))
        {
            acceptedPath = folder.Path;
        }
        else
        {
            var parent = Directory.GetParent(folder.Path)?.FullName;
            if (parent != null && MinecraftUserDataLocator.TrySetCustomDataRoot(IsTargetingPreview, parent))
                acceptedPath = parent;
        }

        if (acceptedPath == null)
        {
            Log($"That doesn't look like a valid {versionName} data folder. " +
                $"Please select the folder named \"{expectedName}\", it should be the one that contains a \"Users\" subfolder.",
                LogLevel.Error);
            return;
        }

        Log($"{versionName} data folder set: {acceptedPath}\n\n" +
            $"ℹ️ The app is going to remember this location, you can now continue to use features that relied on user data. Enjoy!", LogLevel.Success);

        // Update button state and kick off pack detection now that the path is known
        UpdateUserDataDependentUI(IsTargetingPreview);
        _ = LocatePacksTask();
    }
    private void UpdateUserDataDependentUI(bool isTargetingPreview)
    {
        var isValid = MinecraftUserDataLocator.IsDataValid(isTargetingPreview);
        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);

        if (isValid)
        {
            PackVM.SetLabelOverride(null);
            ToolTipService.SetToolTip(BrowsePacksButton,
                "Select resource packs that you'd want to tune, export, or delete, you can also import more packs into Minecraft from this menu.");

            BrowsePacksButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ApplyLocateUserDataColors(RightEdgeOfLocateButton.ActualTheme);

            _ = LocatePacksTask();
        }
        else
        {
            var editionLabel = isTargetingPreview ? "Preview" : "Stable";
            var expectedFolderName = isTargetingPreview
                ? MinecraftUserDataLocator.PreviewRootFolderName
                : MinecraftUserDataLocator.StableRootFolderName;

            PackVM.SetLabelOverride($"Locate {editionLabel} user data");
            ToolTipService.SetToolTip(BrowsePacksButton,
                $"The app couldn't find {versionName} data folder automatically - click to locate it manually.");

            BrowsePacksButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ApplyLocateUserDataColors(RightEdgeOfLocateButton.ActualTheme);

            Log($"Couldn't find {versionName} user data folder automatically. Here's what to do:\n" +
                $"Click \"Locate {editionLabel} user data\" button above, find and select the folder named \"{expectedFolderName}\" " +
                $"- It's the one with a \"Users\" subfolder inside it.\n" +
                $"If you don't have {versionName} installed, you can ignore this warning. Also make sure you've played the game at least once if you've installed or reinstalled recently.",
                LogLevel.Error);
        }
    }
    private void ApplyLocateUserDataColors(ElementTheme theme)
    {
        bool needsAttention = !MinecraftUserDataLocator.IsDataValid(IsTargetingPreview);
        RightEdgeOfLocateButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right, accented: needsAttention, isEnabled: BrowsePacksButton.IsEnabled));
    }


    private void TargetPreviewToggle_Checked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = true;

        ApplyTargetPreviewBevelColors(LeftEdgeOfTargetPreviewButton.ActualTheme);
        LaunchBetterRTXManagerButton.IsEnabled = false;

        // _Checked runs up until here IF the persistent IsTargetingPreview is True, UpdateUI makes sure this happens...

        if (_isInitializing) return; // return early, so the part below this line doesn't run on Window init-triggered TogglePreview (by UpdateUI method...)
        // Locating user data, updating ui based on it, and locating packs, runs regardless, we're avoiding the dupe operation here basically. so we safely run all there instead where its appropriate

        SelectedPacks.Clear();
        Log("Targeting Minecraft Preview.", LogLevel.MCPreview);

        MinecraftUserDataLocator.ValidateAndUpdateCachedLocations();
        UpdateUserDataDependentUI(IsTargetingPreview);
        _ = LocatePacksTask();
    }
    private void TargetPreviewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = false;
        _ = BlinkingLamp(true, true, 0.0);

        ApplyTargetPreviewBevelColors(LeftEdgeOfTargetPreviewButton.ActualTheme);
        LaunchBetterRTXManagerButton.IsEnabled = true;

        if (_isInitializing) return; // same as Checked

        SelectedPacks.Clear();
        Log("Targeting stable Minecraft release.", LogLevel.MCRelease);

        MinecraftUserDataLocator.ValidateAndUpdateCachedLocations();
        UpdateUserDataDependentUI(IsTargetingPreview);
        _ = LocatePacksTask();
    }
    private void ApplyTargetPreviewBevelColors(ElementTheme theme)
    {
        LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: IsTargetingPreview, isEnabled: TargetPreviewToggle.IsEnabled));
        RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right, accented: IsTargetingPreview, isEnabled: TargetPreviewToggle.IsEnabled));
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



    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        // ----- HARD RESET 
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        try
        {
            if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                WindowControlsManagerExtensions.DisableAllControls(this);
                _progressManager.ShowProgress();
                _ = BlinkingLamp(true);

                _ = WipeAllStorageData();

                return;
            }
        }
        catch (Exception ex)
        {
            Log($"Hard Reset Error: {ex.ToString}", LogLevel.Error);
            WindowControlsManagerExtensions.RestoreAllControls(this);
            _ = BlinkingLamp(false);
            _progressManager.HideProgress();

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

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);

        if (RuntimeFlags.Set("Said_Extra_Resetting_Information"))
        {
            Log($"Note:\nThis does not restore the packs to their default state!\nTo reset packs back to original you can quickly reinstall the latest versions of Vanilla RTX using the '{UpdateVanillaRTXButtonText.Text}' button. Other packs will require manual reinstallation. Use Export to back them up and quickly reimport them as you need.", LogLevel.Informational);
        }
        Log("Tuning environment reset.", LogLevel.Reset);
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
            Log("Cleared all pack selections.", LogLevel.Cleaning);
        else if (hadVanillaRTX)
            Log("Deselected all Vanilla RTX packs.", LogLevel.Cleaning);
        else
            Log("You haven't selected any packs to clear.", LogLevel.Informational);
    }

    private async Task WipeAllStorageData()
    {
        try
        {
            Log("Starting hard reset, this will wipe all of app's storage and temporary files...", LogLevel.Warning);
            await Task.Delay(250);

            await GuardActivePresetsBeforeWipeAsync();

            // ── 1. Local Settings (recursive containers) ─────────────────────────
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var roamingSettings = Windows.Storage.ApplicationData.Current.RoamingSettings;
            int totalKeysWiped = 0;

            foreach (var (root, rootName) in new[] { (localSettings, "LocalSettings"), (roamingSettings, "RoamingSettings") })
            {
                foreach (var key in root.Values.Keys.ToList())
                {
                    root.Values.Remove(key);
                    Log($"Deleted key: {rootName}/{key}", LogLevel.Informational);
                    totalKeysWiped++;
                }

                foreach (var containerKey in root.Containers.Keys.ToList())
                {
                    root.DeleteContainer(containerKey);
                    Log($"Deleted container: {rootName}/{containerKey}", LogLevel.Informational);
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

        // ── local helpers, only meaningful when trying to wipe and user doesn't have their default presets installed if any ───────────

        async Task GuardActivePresetsBeforeWipeAsync()
        {
            Log("Checking for active custom presets that need to be reverted first...", LogLevel.Informational);

            await RunGuard("BetterRTX",
                DefaultsGuard.RestoreBetterRTXDefaultIfNeededAsync(
                    msg => Log(msg, LogLevel.Informational)));

            await RunGuard("RTX LUT (Release)",
                DefaultsGuard.RestoreLutDefaultIfNeededAsync(
                    targetPreview: false, log: msg => Log(msg, LogLevel.Informational)));

            await RunGuard("RTX LUT (Preview)",
                DefaultsGuard.RestoreLutDefaultIfNeededAsync(
                    targetPreview: true, log: msg => Log(msg, LogLevel.Informational)));

            await Task.Delay(150);
        }

        async Task RunGuard(string featureName, Task<RTXDefaultsGuard> guardTask)
        {
            var result = await guardTask;
            switch (result)
            {
                case RTXDefaultsGuard.Restored:
                    Log($"{featureName}: reverted to Default before wipe.", LogLevel.Success);
                    break;
                case RTXDefaultsGuard.RestoreFailed:
                    Log($"{featureName}: tried to revert to Default but it failed - the game may still be on a modified preset.", LogLevel.Warning);
                    break;
                case RTXDefaultsGuard.Skipped:
                    Log($"{featureName}: couldn't safely verify preset state - left untouched.", LogLevel.Warning);
                    break;
                case RTXDefaultsGuard.NoActionNeeded:
                    Log($"{featureName}: already on Default or nothing to protect.", LogLevel.Informational);
                    break;
            }
        }
    }


    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
         "LaunchMinecraftButton", "TargetPreviewToggle",
         "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
         "TuneSelectionButton", "ExportButton", "DeleteButton",
         "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton"
        ];

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
            Content = $"This will delete {toDelete.Count} pack{(toDelete.Count == 1 ? "" : "s")} forever! (A very long time!)",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _progressManager.ShowProgress();
        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

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
                    Log($"{displayName} was found in the list more than once - skipped duplicate selection.", LogLevel.Warning);
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

            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            _ = LocatePacksTask(); // Controls get enabled, their state was captured before deletion, so we re-locate AFTER they're restored, so it properly disables packs that aren't there anymore
            SelectedPacks.Clear();
            UpdateUI(); // Just in case, truly don't know why, prolly afraid of checkboxes remaining "on" visually while disabled, while the bool being off
        }
    }
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
    "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
    "TuneSelectionButton", "DeleteButton", "ExportButton",
             "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton"
        ];

        _progressManager.ShowProgress();
        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

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
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);
        }
    }

    private CancellationTokenSource? _tuningCts;
    private async void TuneSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tuningCts != null)
        {
            TuneSelectionButton.IsEnabled = false;
            TuneSelectionButtonText.Text = "Stopping...";
            _tuningCts.Cancel();
            return;
        }

        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
        "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
        "ExportButton", "DeleteButton",
        "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton",
        "FogMultiplierSlider", "FogMultiplierBox",
        "EmissivityMultiplierSlider", "EmissivityMultiplierBox", "NormalIntensitySlider", "NormalIntensityBox",
        "MaterialNoiseSlider", "MaterialNoiseBox", "RoughenUpSlider", "RoughenUpBox",
        "LazifyNormalsSlider", "LazifyNormalsBox", "EmissivityAmbientLightToggle"
        ];

        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);

        try
        {
            bool hasVanillaPacks = IsVanillaRTXEnabled || IsNormalsEnabled || IsOpusEnabled;
            bool hasCompatibleCustom = TunerVariables.SelectedPacks.Any(p => p.Type != "Incompatible");
            bool hasIncompatibleCustom = TunerVariables.SelectedPacks.Any(p => p.Type == "Incompatible");

            if ((hasVanillaPacks || hasCompatibleCustom) && hasIncompatibleCustom)
                Log("Some of the selected packs are not RTX compatible & will be excluded from the tuning process.", LogLevel.Warning);

            if (!hasVanillaPacks && !hasCompatibleCustom)
            {
                if (hasIncompatibleCustom)
                    Log("None of the selected packs are RTX or Vibrant Visuals compatible. Select at least one compatible pack to tune.", LogLevel.Warning);
                else
                    Log("Select at least one compatible pack to tune.", LogLevel.Warning);
                return;
            }

            _tuningCts = new CancellationTokenSource();
            var progress = new Progress<Tuner.TuningProgress>(p => _progressManager.ReportTuningProgress(p));

            _ = BlinkingLamp(true);
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

            TuneSelectionButtonIcon.Glyph = "\uE733";
            TuneSelectionButtonText.Text = "Abort tuning operation";
            ToolTipService.SetToolTip(TuneSelectionButton,
                "Stops the tuning operation. Textures already finished keep their changes; anything not yet reached is left untouched.");

            var tuningMessage = await Task.Run(() => Tuner.TuneSelectedPacks(progress, _tuningCts.Token));
            Log(tuningMessage, LogLevel.Success);
            _progressManager.Complete();
        }
        catch (OperationCanceledException)
        {
            Log("Tuning was cancelled.", LogLevel.Warning);
            _progressManager.ReportCancelled();
        }
        catch (Exception ex)
        {
            Log($"Something went wrong during the tuning process: {ex}", LogLevel.Error);
            _progressManager.ReportError();
        }
        finally
        {
            _ = BlinkingLamp(false);
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            TuneSelectionButtonIcon.Glyph = "\uE9F5";
            TuneSelectionButtonText.Text = "Tune selection";
            ToolTipService.SetToolTip(TuneSelectionButton,
                "Begins permanently modifying the select packs using the current set parameters.\n\nMake sure Minecraft is closed while packs are being tuned.");
            TuneSelectionButton.IsEnabled = true;

            _tuningCts?.Dispose();
            _tuningCts = null;
        }
    }




    private async void LaunchPackUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
    "LaunchAlchitexButton", "BrowsePacksButton",
    "TuneSelectionButton", "ExportButton", "DeleteButton", "LaunchPackUpdateButton",
             "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox","ClearButton", "ResetButton"
        ];

        // The UI display text relies on this, rerun it just in case, few ms overhead worth it
        await LocatePacksTask();

        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
        }

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

        var packUpdaterWindow = new PackUpdate.PackUpdateWindow(this);
        var mainAppWindow = this.AppWindow;

        packUpdaterWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        packUpdaterWindow.AppWindow.Move(mainAppWindow.Position);

        // Do on window closure
        packUpdaterWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(packUpdaterWindow);

            // Enable main UI buttons again
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            // Set reinstall latest packs button visuals based on cache status
            if (_updater.HasDeployableCache())
            {
                UpdateVanillaRTXGlyph.Glyph = "\uE8F7";
            }
            else
            {
                UpdateVanillaRTXGlyph.Glyph = "\uEBD3";
            }
            _ = LocatePacksTask(true); // Trigger an auto pack location check after, only time we log statuses for user to see what's installed
        };

        _childWindows.Add(packUpdaterWindow);
        WindowControlsManager.Activate(packUpdaterWindow);
    }
    private void LaunchBetterRTXManagerButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchBetterRTXManagerButton", "ResetButton"];

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

        var betterRTXWindow = new BetterRTXManager.BetterRTXManagerWindow();

        var mainAppWindow = this.AppWindow;
        betterRTXWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        betterRTXWindow.AppWindow.Move(mainAppWindow.Position);

        betterRTXWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(betterRTXWindow);

            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            // Log status after window closes
            if (betterRTXWindow.OperationSuccessful)
            {
                Log(betterRTXWindow.StatusMessage, LogLevel.BetterRTX);
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

        _childWindows.Add(betterRTXWindow);
        WindowControlsManager.Activate(betterRTXWindow);
    }
    private void LaunchDLSSSwapperButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchDLSSSwapperButton", "ResetButton"];

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

        var DLSSSwapperWindow = new DLSSBrowser.DLSSSwapperWindow();
        var mainAppWindow = this.AppWindow;

        DLSSSwapperWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        DLSSSwapperWindow.AppWindow.Move(mainAppWindow.Position);

        DLSSSwapperWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(DLSSSwapperWindow);

            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            // Log status after window closes
            if (DLSSSwapperWindow.OperationSuccessful)
            {
                Log(DLSSSwapperWindow.StatusMessage, LogLevel.DLSS);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (!string.IsNullOrEmpty(DLSSSwapperWindow.StatusMessage))
            {
                Log(DLSSSwapperWindow.StatusMessage, LogLevel.Error);
                _ = BlinkingLamp(true, true, 0.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        _childWindows.Add(DLSSSwapperWindow);
        WindowControlsManager.Activate(DLSSSwapperWindow);
    }
    private void LaunchLUTManagerButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchLUTManagerButton", "ResetButton"];

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

        var LutManagerWindow = new LUTManager.LUTManagerWindow();
        var mainAppWindow = this.AppWindow;

        LutManagerWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        LutManagerWindow.AppWindow.Move(mainAppWindow.Position);

        LutManagerWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(LutManagerWindow);

            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

            if (LutManagerWindow.OperationSuccessful)
            {
                Log(LutManagerWindow.StatusMessage, LogLevel.LUT);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (!string.IsNullOrEmpty(LutManagerWindow.StatusMessage))
            {
                Log(LutManagerWindow.StatusMessage, LogLevel.Error);
                _ = BlinkingLamp(true, true, 0.0);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        };

        _childWindows.Add(LutManagerWindow);
        WindowControlsManager.Activate(LutManagerWindow);
    }
    private void LaunchAlchitexButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        if (!SelectedPacks.Any(p => p.IsAlchitexCandidate))
        {
            if (RuntimeFlags.Set("Has Already Said the thing about what RTX Reactor does to packs in the button click menu"))
            {
                Log("RTX Reactor generates proper RTX support only for non-PBR texture packs that it may consider suitable.", LogLevel.Alchitex);
            }
            Log($"You must select at least one '{PackBrowser.PackBrowserWindow.AlchitexCandidateTag}' from your resource packs to use this feature on.", LogLevel.Warning);
            return; // TODO: Definition of what makes a pack truly a good "Alchitex Candidate" could evolve over time into something more concrete
            // Might wanna let ALL non-RTX AND non-VV packs in, but leave a warning for user once inside the window, that texture packs not marked as candidates
            // have a higher chance of breaking, not working, or not seeing any benefit from this feature.
            // It's true! (legacy packs, few to no block textures are decent starting indicators for now.
        }


        string[] ToDisable =
        [
         "LaunchMinecraftButton", "TargetPreviewToggle",
        "BrowsePacksButton", "TuneSelectionButton", "ExportButton", "DeleteButton", "LaunchAlchitexButton"
        ];

        WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);
        var alchitexWindow = new Modules.Alchitex.Alchitex();
        var mainAppWindow = this.AppWindow;
        alchitexWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            mainAppWindow.Size.Width,
            mainAppWindow.Size.Height));
        alchitexWindow.AppWindow.Move(mainAppWindow.Position);
        alchitexWindow.Closed += (s, args) =>
        {
            _childWindows.Remove(alchitexWindow);
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);
            _ = BlinkingLamp(true, true, 0.0, 1.0);
        };

        _childWindows.Add(alchitexWindow);
        WindowControlsManager.Activate(alchitexWindow);
    }


    private async void LaunchMinecraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        if (Helpers.IsMinecraftRunning())
        {
            Log("Minecraft already seems to be open. Please restart the game for options.txt changes to take effect.", LogLevel.Warning);
        }

        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var isShiftHeld = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        try
        {
            var logs = isShiftHeld
                ? await MinecraftLauncher.LaunchVSyncMinecraftRTXAsync(IsTargetingPreview)
                : await MinecraftLauncher.LaunchMinecraftRTXAsync(IsTargetingPreview);

            Log(logs, (IsTargetingPreview ? LogLevel.MCPreview : LogLevel.MCRelease));
        }
        finally
        {
            _ = BlinkingLamp(true, true, 0.0);
        }
    }


    #region =============== SLIDER HANDLERS ===============

    private static void HandleDoubleSliderValueChanged(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
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

    private static void HandleDoubleTextBoxLostFocus(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
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


    private static void HandleIntSliderValueChanged(Slider slider, TextBox textBox, ref int property)
    {
        property = (int)Math.Round(slider.Value);
        if (textBox != null && textBox.FocusState == FocusState.Unfocused)
            textBox.Text = property.ToString(CultureInfo.InvariantCulture);
    }

    private static void HandleIntTextBoxLostFocus(Slider slider, TextBox textBox, ref int property)
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


    #region Logger

    // add more types, specifically, let feature windows use their own unique emojis!
    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Debug, PSA, Alchitex,
        DLSS, BetterRTX, LUT, VanillaRTX, Selected, MCPreview, MCRelease, Cleaning, Reset
    }

    // The single source of truth, Log() only ever writes here
    public static string LogText = "";
    private static readonly object _logGate = new();

    // Typewriter state, only ever touched on the UI thread, inside TypewriterTick()
    // Logger writes fast; typewriter reveals it to the UI on its own schedule — always the
    // oldest not-yet-shown entry first, left-to-right within it — so chronology holds up
    // AND each message types start-to-finish instead of finish-to-start.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _typewriterTimer;
    private ScrollViewer? _logScrollViewer;
    private int _settledLength = 0;   // trailing chars of LogText already fully shown & final
    private int _activeRevealed = 0;  // chars revealed so far of the current (oldest-pending) entry
    private string? _lastRenderedText;

    private const int MaxLogChars = 4000;

    private const double BaselineCharsPerTick = 2.0; // relaxed pace for small/no backlog
    private const double CatchUpFraction = 0.10;      // reveal % of the backlog each tick
    private static readonly int TickIntervalMs = ((Func<int>)(() => // speed based on corecount, since this really does affect cpu usage! it's the main lever
    {
        try
        {
            if (Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On)
                return 64;

            return Environment.ProcessorCount switch
            {
                >= 24 => 4,
                >= 16 => 8,
                >= 8 => 16,
                >= 5 => 32,
                _ => 64,
            };
        }
        catch { return 16; }
    }))();


    // Structural marker ONLY — never rendered, never typed character-by-character, never
    private const string EntrySentinel = "\uE000\uE001";

    // Idle/typing cursor — sits at the current write-head
    private const bool ShowTypingCursor = true;
    private const int CursorBlinkMs = 750;
    private const string CursorOnGlyph = " |";
    private const string CursorOffGlyph = "  ";

    public static void Log(string message, LogLevel? level = null)
    {
        string prefix = level switch
        {
            LogLevel.Success => "✅ ",
            LogLevel.Informational => "ℹ️ ",
            LogLevel.Warning => "⚠️ ",
            LogLevel.Error => "❌ ",
            LogLevel.Selected => "📍 ",
            LogLevel.MCPreview => "🚧 ",
            LogLevel.MCRelease => "🟩 ",
            LogLevel.Cleaning => "🧹 ",
            LogLevel.Reset => "🔄️ ",
            LogLevel.Lengthy => "⏳ ",
            LogLevel.PSA => "📢 ",
            LogLevel.Network => "🛜 ",
            LogLevel.Debug => "🛸 ",
            LogLevel.Alchitex => "🟦 ",
            LogLevel.DLSS => "🫧 ",
            LogLevel.BetterRTX => "🧈 ",
            LogLevel.LUT => "🎨 ",
            LogLevel.VanillaRTX => "⛏️ ",
            null => "",
            _ => "💩 "
        };

        string entry = $"{prefix}{message}";

        lock (_logGate)
            LogText = string.IsNullOrEmpty(LogText) ? entry : $"{entry}{EntrySentinel}{LogText}";
    }

    private void InitializeLogTypewriter()
    {
        SidebarLog.Loaded += (_, _) => _logScrollViewer ??= GetScrollViewer(SidebarLog);
        if (SidebarLog.IsLoaded) _logScrollViewer ??= GetScrollViewer(SidebarLog);

        _typewriterTimer = DispatcherQueue.CreateTimer();
        _typewriterTimer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _typewriterTimer.Tick += (_, _) => TypewriterTick();
        _typewriterTimer.Start();
    }

    private void TypewriterTick()
    {
        string current;
        lock (_logGate)
        {
            current = LogText;

            if (current.Length > MaxLogChars)
            {
                // Cut on a sentinel boundary so we drop whole oldest entries, never mid-message.
                int cut = current.LastIndexOf(EntrySentinel, MaxLogChars - 1, MaxLogChars, StringComparison.Ordinal);
                if (cut > 0)
                {
                    int trimmedAmount = current.Length - cut;
                    current = current[..cut];
                    LogText = current;

                    // Trimmed content came off the tail — exactly where _settledLength measures
                    // from — so shrink it by the same amount. If the cut reached into content that
                    // wasn't fully settled yet (only possible under an extreme backlog like a stress
                    // test), just reset both — the next tick starts clean against the trimmed text.
                    if (trimmedAmount > _settledLength)
                    {
                        _settledLength = 0;
                        _activeRevealed = 0;
                    }
                    else
                    {
                        _settledLength -= trimmedAmount;
                    }
                }
            }
        }

        int unshownLength = current.Length - _settledLength;

        if (unshownLength > 0)
        {
            // The oldest not-yet-shown entry sits adjacent to the settled region. Its own
            // trailing sentinel (connecting it to whatever follows) isn't a real boundary
            // between two DIFFERENT pending entries, so exclude it before searching.
            int trailingConnector = _settledLength > 0 ? EntrySentinel.Length : 0;
            int searchLength = Math.Max(0, unshownLength - trailingConnector);

            int sepIndex = searchLength > 0
                ? current.LastIndexOf(EntrySentinel, searchLength - 1, searchLength, StringComparison.Ordinal)
                : -1;

            int activeStart = sepIndex >= 0 ? sepIndex + EntrySentinel.Length : 0;
            int activeTextLength = searchLength - activeStart; // entry's OWN text only, sentinel excluded

            int remaining = unshownLength - _activeRevealed; // whole backlog left — drives speed-up
            int charsThisTick = (int)Math.Max(BaselineCharsPerTick, Math.Ceiling(remaining * CatchUpFraction));

            _activeRevealed = Math.Min(activeTextLength, _activeRevealed + charsThisTick);
            _activeRevealed = SnapForward(current, activeStart, _activeRevealed);

            if (_activeRevealed >= activeTextLength)
            {
                // Entry fully typed — fold it (and its sentinel, converted to a real blank
                // line) into settled INSTANTLY. The separator is never itself "typed."
                _settledLength = current.Length - activeStart;
                _activeRevealed = 0;

                SidebarLog.UpdateLayout();
                _logScrollViewer?.ChangeView(null, 0, null, true); // once, per finished entry
            }
            else
            {
                RenderFrame(current, activeStart, _activeRevealed);
                return;
            }
        }

        RenderFrame(current, 0, 0);
    }

    private void RenderFrame(string current, int activeStart, int activeRevealed)
    {
        string revealedPrefix = activeRevealed > 0 ? current.Substring(activeStart, activeRevealed) : "";
        string settledDisplay = _settledLength > 0
            ? current.Substring(current.Length - _settledLength).Replace(EntrySentinel, "\n\n")
            : "";

        string headText, tailText;
        if (revealedPrefix.Length > 0)
        {
            headText = revealedPrefix;
            tailText = settledDisplay.Length > 0 ? "\n\n" + settledDisplay : "";
        }
        else
        {
            int firstBoundary = settledDisplay.IndexOf("\n\n", StringComparison.Ordinal);
            headText = firstBoundary >= 0 ? settledDisplay[..firstBoundary] : settledDisplay;
            tailText = firstBoundary >= 0 ? settledDisplay[firstBoundary..] : "";
        }

        string cursor = ShowTypingCursor
            ? ((Environment.TickCount64 / CursorBlinkMs) % 2 == 0 ? CursorOnGlyph : CursorOffGlyph)
            : "";

        string newText = headText + cursor + tailText;
        if (newText == _lastRenderedText) return; // nothing visually changed, skip the relayout entirely

        _lastRenderedText = newText;
        SidebarLog.Text = newText;
    }

    // Never reveal a cut that splits a surrogate pair or strands an emoji's
    // variation-selector/combining mark — grows past them instead of stopping mid-glyph.
    private static int SnapForward(string s, int rangeStart, int localIndex)
    {
        int i = rangeStart + localIndex;
        if (i <= rangeStart || i >= s.Length) return localIndex;

        if (char.IsHighSurrogate(s[i - 1]) && char.IsLowSurrogate(s[i]))
            i++;

        while (i < s.Length && IsJoiningMark(s[i]))
            i++;

        return i - rangeStart;
    }
    private static bool IsJoiningMark(char c) =>
        c is '\uFE0F' or '\uFE0E' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    public static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
    #endregion
}

/* ### BACKLOG/TODO OF HIGHCORTISOL SOFTWARE LTD (STRICTLY CONFIDENTIAL)

- BEFORE RELEASE:
Test thoroughly, ensure no latent trimming bugs, on a FRESH release build
// fresh as in, ensure all assets that should be copied, are copied, not just on your system from older builds.

Release it asap, then begin updating images and readmes and such while the update rolls out, efficient.

- Update the readme to be less verbose, more accurate and helpful instead, cut off unneeded details.
Update them to reflect the latest features/changes

>> Be more explicit about the right channels to give feedback, report issues, etc...
like the new content dialogue for crashes, its literally the only place people are easily directed to the right place
maybe you should do it more often, in more places

- Create a BetterRTX-like lut preset, gets the looks 80% there!

- Do the TODOs scattered in the code

- PackBrowser pack badge Effects, possibly with win2d, for tags/badges:
RTX Reactor, pixelated rain like its tiles
RTX glow
VV... blobl colors moving around maybe
Incompatible, switch between VV-like orange, and red, to indicate vv packs are in-between being compatible and not being compatible with tuner, its true.

- Mayhaps, switch to JSdelivr or a similar cdn to lift some weight off of github

- More previewer asset ideas:
random block renders thrown in there
iconns/logos of features of app thrown in there too, one for each would be enough
Idea, of a render of a Tuner block, but each side features one of the feature-unique icons you've made!
Also leave a reference to the original icon: Netherite, and the slightly uglier one after that.
Leave references to iconic Vanilla RTX worlds as well, from its previous updates/history

*/
// ============================================================================================================
/* THE GULAG 

- Do the DLSS swapper expansion, have it load from SOMEWHERE, as an option perhaps...
Options: Parse TechPowerUP HTMLs and resolve to destination (flaky) but maybe there are
publicly maintained apis to do this too.
WHATEVER YOU DO: make it secondary to the primary manner of its workings, y'know? be clever with the design

- Add a way to add custom presets to BetterRTX Manager (e.g. user made presets)
Give it special treatment same as default preset and avoid changing existing logic
they appear at the bottom
expects zips, rtpacks, or .tar.gz, in any of the 3
look for the .bin files, call the presets custom1, 2, etc.. don't convolute it
since their structure can vary, have something robust for all kinds of custom presets.
to be passed in, extracts bins and makes a custom preset, name em custom_preset_[increment]
basically, instead of changing the current pipeline, integerate this/build it on top of it
that way it'll surely work without fucking things up
> This is probably not so useful
And it goes against your design philosphies
The flow of going to a site, twiddling ALL those knobs, coming back, and having to do it again with mc updates
is just... NOPE!
That said, it's a cool feature for those who might want it.
>> DO IT ONLY IF you actually end up separating the presenter and service logic for BetterRTX manager... it'd be a LOT easier then!

// json says he might unify the output of /creator with what the /api gives.
good news!

// IDEA: RTX Creator can become a reality, powerd by bedrock.graphics if it lasts
use webview, direct, while building aclhitex, route a pipeline through there:
manually creating each block by twiddling knobs, pretty cool, manual edits possible
talk to json about it some time, It's a cool idea for the long run

It's what RTX Reactor was initially supposed to be, before the idea mutated.



- Make holding shift turn the lamp Green to indicate its debugging functionality

- Begin embedding most visual assets into the .resx, fewer IO operations, good optimization
very low prio though, not too many assets, things are good

- Account for different font scalings, windows accessibility settings, etc...
gonna need lots of painstakingly redoing xamls but if one day you have an abundance of time sure why not

- add the ability to TOTALLY DISABLE entire features on startup?
PARTICULARY BETTERRTX
Not all features need this, so a full system may not be needed, in-app announcements could serve as the host, online texts could
be used as the parser
DISABEL the button when betterrtx is broken, manually enable it again when not.

> Prolly not a good idea warnings are enough.


- Make a  secondary image fade in and out briefly over lampinteraction when clicked
same as bottom vessel
so you can create this feeling of lamp shining brighter while its just the translucent parts being overlayed
two arrays passed in
both arrays must select the same image/same rng etc..
- Slowly rework and improve art vessels, introduce 1-2 variants for some static buttons, maybe fire could burn brighter when delete button
gets clicked, if the above is implemented, things can look really nice

> This whole thing would've worked a lot easier if you weren't trying to be a smartass and minimize the number of vessels used for lampanimator/previewer


- Idea: when other windows launch, recieve clicks on the main window, and just log something that tells user, finish your work in the current
open module before returning to main window.
just.. it'd be nice QoL.
Btw random thought
indeed, button functionalities hidden under shift have Debug/Development related purposes, but they're exposed to user nontheless, useful

- Turn the textbox of sidebarlog into a rich textbox, and add the ability to show clickable links
useful down the line

>> while at it, MOVE Art Previewer vessel thingy to a new container beteween the 3x2 button grid, and tune/export/delete grid
Taller default app height, but, here's the cool part
Use can Collapse the window/reduce height, and all it'll do is Swallow the container, so effectively it gives user to the ability to
"Hide or Disable" preview art section in a totally indirect way, which is pretty cool.
Better yet, it should have a Collapse/Expand button, that Updates the Minimum height of the window itself, that'd be better! no ugly half visible previewer vessels
Might end up redoing a whole bunch of art for this, since there's now more space, a wide area to work with.

- Do the redesign?
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

But there are more considerations to this:
Remove all code paths related to the checkboxes
Do the redesign. TODAY. Delete PackLocator
perfect user data locator's reimplementation, it should've concerned itself with filling the variables and validating it
so other classes could use it
Not manually constructing every little thing for callers.
>> Just make sure packs that match Your UUID instead appear at the very very top in PackBrowser, to make things nice and easy!
Move preview button to leftmost part, make the browse packs button larger. y'know! see the concept!
Pack locator is busted right now with your new centralized userdata locator rework
But its ok, no need to fix it, you're doing a redesign that retires it anyway.
But you can't just retire it?!
It's needed for PackUpdater, that's how it knows what Vanilla RTX packs are installed.
Maybe postpone this redesign for now. indeed. don't go too far, sleep on the idea for now.
so yeah, you can't actually just scrap all this and call it a day? there's more involved

- Make shift-clicking the LOCATE PACKS button or something allow user to manually select another path
To be honest:
this is a fucking mess
you should've had a settings panel
inside it, left the options to configure what the launch button does exactly
configure user data and game data paths manually
configure theme
etc..
could collapse all of the titlebar buttons into a gear that opens it instead
that's pretty cool now isn't it?
clean, clear, instead of trying to get the user to do the right thing via logs
leave a place they can instinctively go to and configure everyting IF needed, totally optional
you're still taking the extreme measures needed to keep as many users away from having to touch settings as possible
but that's just better, think about it

its not too late


*/
