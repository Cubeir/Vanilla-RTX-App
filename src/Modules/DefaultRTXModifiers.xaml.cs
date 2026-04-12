using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.RTXDefaults;


public sealed partial class DefaultRTXModifiersWindow : Window
{
    // -------------------------------------------------------------------------
    // File name constants (same across every preset folder)
    // -------------------------------------------------------------------------

    private const string FnLut = "look_up_tables.png";
    private const string FnSky = "sky.png";
    private const string FnWater = "water_n.tga";
    private const string FnPreviewImg = "image.png";
    private const string FnPlaceholder = "placeholder.png"; // For unknown presets
    private const string FnDefaultImg = "default.png";    // UI image for the Default preset

    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    private string _minecraftRoot;      // e.g. C:\Program Files\Microsoft Games\Minecraft for Windows
    private string _defaultsFolder;     // LocalAppData\<pkg>\Lut_Defaults  — the Default preset's files
    private string _lutRootFolder;      // AppDir\Assets\lut
    private string _placeholderImagePath;
    private string _defaultImagePath;   // AppDir\Assets\lut\Default.png

    private List<LutPreset> _presets = new();
    private LutPreset _selectedPreset;   // currently highlighted in the dropdown
    private LutPreset _installedPreset;  // what is actually on disk right now

    private CancellationTokenSource _scanCancellationTokenSource;

    // Crossfade state
    private bool _crossfadeInProgress = false;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    // -------------------------------------------------------------------------
    // Destination path helpers  (always derived fresh from _minecraftRoot)
    // -------------------------------------------------------------------------

    private string DstLut => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnLut);
    private string DstSky => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnSky);
    private string DstWater => Path.Combine(_minecraftRoot, "Content", "data", "ray_tracing", FnWater);

    // Default preset sources live in LocalAppData\Lut_Defaults
    private string DefaultLut => Path.Combine(_defaultsFolder, FnLut);
    private string DefaultSky => Path.Combine(_defaultsFolder, FnSky);
    private string DefaultWater => Path.Combine(_defaultsFolder, FnWater);

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public DefaultRTXModifiersWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

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

        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(139, 139, 139, 139);
            _appWindow.TitleBar.InactiveForegroundColor = ColorHelper.FromArgb(128, 139, 139, 139);
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        this.Activated += DefaultRTXModifiersWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    // -------------------------------------------------------------------------
    // Shadow setup
    // -------------------------------------------------------------------------

    private void ButtonRowGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var shadow = new ThemeShadow();
        ButtonRowGrid.Shadow = shadow;
        shadow.Receivers.Add(ButtonShadowReceiver);
    }

    // -------------------------------------------------------------------------
    // Window lifecycle
    // -------------------------------------------------------------------------

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void DefaultRTXModifiersWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= DefaultRTXModifiersWindow_Activated;

            _ = this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => SetTitleBarDragRegion());

            var target = TunerVariables.Persistent.IsTargetingPreview
                ? "Minecraft Preview"
                : "Minecraft Release";
            WindowTitle.Text = $"RTX lut manager - {target}";

            ManualSelectionText.Text =
                "If this is taking too long, click to manually locate the game folder. " +
                "Confirm in File Explorer once you're inside the folder called: " +
                (TunerVariables.Persistent.IsTargetingPreview
                    ? MinecraftGDKLocator.MinecraftPreviewFolderName
                    : MinecraftGDKLocator.MinecraftFolderName);

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
        if (_appWindow.TitleBar != null && TitleBarArea.XamlRoot != null)
        {
            try
            {
                var _ = (int)(TitleBarArea.ActualHeight * TitleBarArea.XamlRoot.RasterizationScale);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"LUTM: Error setting drag region: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = TunerVariables.Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? TunerVariables.Persistent.MinecraftPreviewInstallPath
                : TunerVariables.Persistent.MinecraftInstallPath;

            string minecraftPath = null;

            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath))
            {
                Trace.WriteLine($"LUTM: Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine("LUTM: Cache became invalid, clearing");
                    if (isPreview)
                        TunerVariables.Persistent.MinecraftPreviewInstallPath = null;
                    else
                        TunerVariables.Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                    ManualSelectionButton.Visibility = Visibility.Visible);

                _scanCancellationTokenSource = new CancellationTokenSource();
                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview, _scanCancellationTokenSource.Token);

                if (minecraftPath == null)
                {
                    Trace.WriteLine("LUTM: System search cancelled or failed");
                    return;
                }
            }

            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"LUTM EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        _minecraftRoot = minecraftPath;
        _lutRootFolder = Path.Combine(AppDir, "Assets", "lut");
        _placeholderImagePath = Path.Combine(_lutRootFolder, FnPlaceholder);
        _defaultImagePath = Path.Combine(_lutRootFolder, FnDefaultImg);

        Trace.WriteLine($"LUTM: Root     : {_minecraftRoot}");
        Trace.WriteLine($"LUTM: AppDir   : {AppDir}");
        Trace.WriteLine($"LUTM: LutRoot  : {_lutRootFolder}");
        Trace.WriteLine($"LUTM: DstLut   : {DstLut}   exists={File.Exists(DstLut)}");
        Trace.WriteLine($"LUTM: DstSky   : {DstSky}   exists={File.Exists(DstSky)}");
        Trace.WriteLine($"LUTM: DstWater : {DstWater}  exists={File.Exists(DstWater)}");

        // Step 1: Establish the LocalAppData defaults folder (Lut_Defaults)
        _defaultsFolder = EstablishDefaultsFolder();
        if (_defaultsFolder == null)
        {
            StatusMessage = "Could not establish defaults folder";
            this.Close();
            return;
        }

        // Step 2: Back up game defaults into Lut_Defaults — all-or-none
        await EnsureDefaultsBackedUpAsync();

        // Step 3: Discover all presets (Default first, then Assets\lut\ subfolders)
        LoadPresets();

        // Step 4: Detect which preset is currently installed
        _installedPreset = await DetectCurrentPresetAsync();
        Trace.WriteLine($"LUTM: Detected preset: {_installedPreset?.Name ?? "Unknown"}");

        // Step 5: Populate dropdown and settle the UI
        PopulateDropdown(_installedPreset);

        // Step 6: Show main UI
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        });
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = TunerVariables.Persistent.IsTargetingPreview;
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (path != null)
            await ContinueInitializationWithPath(path);
        else
        {
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    // -------------------------------------------------------------------------
    // Defaults folder  (LocalAppData\<pkg>\Lut_Defaults)
    // -------------------------------------------------------------------------

    private string EstablishDefaultsFolder()
    {
        try
        {
            var location = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Lut_Defaults");
            Directory.CreateDirectory(location);
            Trace.WriteLine($"LUTM: Defaults folder: {location}");
            return location;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"LUTM: Failed to create defaults folder: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Ensure game defaults are backed up into Lut_Defaults.
    // Mirrors the old code's logic exactly:
    //   - All three backups present → skip (originals are preserved forever).
    //   - Game files present        → copy them (all-or-none, overwrite partials).
    //   - Game files missing        → mend from best available bundled preset.
    // -------------------------------------------------------------------------

    private async Task EnsureDefaultsBackedUpAsync()
    {
        bool allBackupsPresent =
            File.Exists(DefaultLut) && File.Exists(DefaultSky) && File.Exists(DefaultWater);

        if (allBackupsPresent)
        {
            Trace.WriteLine("LUTM: Default backup already complete - skipping");
            return;
        }

        Trace.WriteLine("LUTM: Default backup incomplete - attempting from game files");

        bool allGameFilesPresent =
            File.Exists(DstLut) && File.Exists(DstSky) && File.Exists(DstWater);

        if (allGameFilesPresent)
        {
            // All-or-none: overwrite any partial backup to keep the set consistent
            await Task.Run(() =>
            {
                try
                {
                    File.Copy(DstLut, DefaultLut, overwrite: true);
                    File.Copy(DstSky, DefaultSky, overwrite: true);
                    File.Copy(DstWater, DefaultWater, overwrite: true);
                    Trace.WriteLine("LUTM: Default backup created from game files");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"LUTM: Backup error: {ex.Message}");
                }
            });
        }
        else
        {
            // Game files missing — mend from a bundled preset so the player isn't left broken.
            // Priority: "Gamescom 2019 Demo" (always bundled) → first complete preset alphabetically.
            Trace.WriteLine("LUTM: Game files missing - mending from bundled preset");

            string mendLut = null, mendSky = null, mendWater = null;

            if (Directory.Exists(_lutRootFolder))
            {
                const string PreferredMendPreset = "Gamescom 2019 Demo";
                var preferred = Path.Combine(_lutRootFolder, PreferredMendPreset);
                var prefLut = Path.Combine(preferred, FnLut);
                var prefSky = Path.Combine(preferred, FnSky);
                var prefWater = Path.Combine(preferred, FnWater);

                if (File.Exists(prefLut) && File.Exists(prefSky) && File.Exists(prefWater))
                {
                    mendLut = prefLut;
                    mendSky = prefSky;
                    mendWater = prefWater;
                    Trace.WriteLine("LUTM: Mending with preferred preset [" + PreferredMendPreset + "]");
                }

                if (mendLut == null)
                {
                    foreach (var dir in Directory.GetDirectories(_lutRootFolder)
                                                 .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    {
                        var lut = Path.Combine(dir, FnLut);
                        var sky = Path.Combine(dir, FnSky);
                        var water = Path.Combine(dir, FnWater);
                        if (File.Exists(lut) && File.Exists(sky) && File.Exists(water))
                        {
                            mendLut = lut;
                            mendSky = sky;
                            mendWater = water;
                            Trace.WriteLine("LUTM: Mending with fallback preset [" + Path.GetFileName(dir) + "]");
                            break;
                        }
                    }
                }
            }

            if (mendLut != null)
            {
                bool mended = await ReplaceRtxFilesWithElevation(mendLut, mendSky, mendWater);
                Trace.WriteLine(mended ? "LUTM: Game mended" : "LUTM: Mend failed or cancelled");
            }
            else
            {
                Trace.WriteLine("LUTM: No complete presets found for mending - user must install manually");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Preset discovery
    // Default preset is always first, sourced from _defaultsFolder.
    // Its UI image comes from Assets\lut\Default.png (optional, falls back to placeholder).
    // All other presets come from Assets\lut\ subfolders, sorted alphabetically.
    // -------------------------------------------------------------------------

    private void LoadPresets()
    {
        _presets.Clear();

        // Default preset — always first, RTX files live in Lut_Defaults
        var defaultPreset = new LutPreset("Default", _defaultsFolder, _defaultImagePath, isDefault: true);
        _presets.Add(defaultPreset);
        Trace.WriteLine($"LUTM: Default preset — complete={defaultPreset.IsComplete}  folder={_defaultsFolder}");

        // Additional presets from Assets\lut\ subfolders
        if (Directory.Exists(_lutRootFolder))
        {
            foreach (var dir in Directory.GetDirectories(_lutRootFolder)
                                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var preset = new LutPreset(name, dir);
                _presets.Add(preset);
                Trace.WriteLine($"LUTM: Preset [{name}] complete={preset.IsComplete} folder={dir}");
            }
        }
        else
        {
            Trace.WriteLine($"LUTM: LUT folder not found: {_lutRootFolder}");
        }

        Trace.WriteLine($"LUTM: {_presets.Count} preset(s) loaded");
    }

    // -------------------------------------------------------------------------
    // Detect currently installed preset by SHA-256 hashing all 3 game files.
    // Returns null if game files are missing or don't match any preset.
    // -------------------------------------------------------------------------

    private async Task<LutPreset> DetectCurrentPresetAsync()
    {
        if (!File.Exists(DstLut) || !File.Exists(DstSky) || !File.Exists(DstWater))
        {
            Trace.WriteLine("LUTM: One or more game files missing - cannot detect preset");
            return null;
        }

        return await Task.Run(() =>
        {
            foreach (var preset in _presets.Where(p => p.IsComplete))
            {
                if (HashesMatch(DstLut, preset.LutPath) &&
                    HashesMatch(DstSky, preset.SkyPath) &&
                    HashesMatch(DstWater, preset.WaterPath))
                {
                    return preset;
                }
            }
            return null;
        });
    }

    // -------------------------------------------------------------------------
    // Dropdown population
    // -------------------------------------------------------------------------

    private void PopulateDropdown(LutPreset installedPreset)
    {
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            var flyout = SelectPresetMenu.Flyout as MenuFlyout;
            if (flyout == null) return;
            flyout.Items.Clear();

            foreach (var preset in _presets)
            {
                var item = new MenuFlyoutItem
                {
                    Text = preset.IsComplete ? preset.Name : $"{preset.Name} (incomplete)",
                    Tag = preset,
                    IsEnabled = preset.IsComplete
                };
                item.Click += PresetMenuItem_Click;
                flyout.Items.Add(item);
            }

            if (installedPreset != null)
            {
                // Auto-select the currently installed preset so dropdown and image match reality
                ApplySelection(installedPreset);
            }
            else
            {
                // Nothing matched — prompt user to pick
                _selectedPreset = null;
                SelectPresetMenu.Content = "Select a preset...";
                InstallButton.IsEnabled = false;
                UpdatePresetImage(null);
            }
        });
    }

    private void PresetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is LutPreset preset)
            ApplySelection(preset);
    }

    /// <summary>
    /// Central method for changing the active selection.
    /// Updates _selectedPreset, the dropdown label, the install button, and the preview image.
    /// The dropdown label reads "Installed Preset: X" when X is currently on disk,
    /// or "Selected Preset: X" otherwise.
    /// </summary>
    private void ApplySelection(LutPreset preset)
    {
        _selectedPreset = preset;

        if (preset == null)
        {
            SelectPresetMenu.Content = "Select a preset...";
            InstallButton.IsEnabled = false;
            UpdatePresetImage(null);
            return;
        }

        // Dropdown label
        bool isInstalled = _installedPreset != null &&
                           string.Equals(preset.Name, _installedPreset.Name, StringComparison.OrdinalIgnoreCase);
        SelectPresetMenu.Content = isInstalled
            ? $"Installed Preset: {preset.Name}"
            : $"Selected Preset: {preset.Name}";


        // Install button content
        InstallButton.IsEnabled = preset.IsComplete;
        if (isInstalled)
        {
            InstallButton.Content = "Reinstall";
            InstallButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];

            // Pane thingy
            var theme = LeftEdgeOfInstallButton.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            var themeDict = Application.Current.Resources.ThemeDictionaries[theme] as ResourceDictionary;
            var color = (Windows.UI.Color)themeDict["FakeSplitButtonBrightBorderColor"];
            LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush(color);
        }
        else
        {
            InstallButton.Content = "Install";
            InstallButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

            // Pane thingy
            var accentColorKey = LeftEdgeOfInstallButton.ActualTheme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3";
            LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources[accentColorKey]);
        }

        UpdatePresetImage(preset);
    }

    // -------------------------------------------------------------------------
    // Preset preview image — crossfade between two layered Image vessels.
    //
    // State: both vessels start transparent/empty.
    //
    // First call (both empty):
    //   Load image into Top, fade both Bottom and Top to 1.
    //   After fade: copy source to Bottom, reset Top opacity to 0.
    //
    // Subsequent calls (Bottom holds the current image):
    //   Load new image into Top, fade Top from 0 → 1.
    //   After fade: copy source to Bottom, reset Top to 0.
    //
    // Result: the viewer always sees a smooth 0.2s crossfade with no flash.
    // -------------------------------------------------------------------------

    private string _currentImagePath = null;   // path settled in Bottom vessel

    private void UpdatePresetImage(LutPreset preset)
    {
        string imagePath = null;

        if (preset != null)
        {
            // For the Default preset, use _defaultImagePath; otherwise use the preset's own image
            imagePath = preset.IsDefault ? _defaultImagePath : preset.ImagePath;

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                imagePath = _placeholderImagePath;
        }
        else
        {
            imagePath = _placeholderImagePath;
        }

        // Avoid redundant crossfades when the image hasn't changed
        if (string.Equals(imagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase))
            return;

        _ = this.DispatcherQueue.TryEnqueue(() => CrossfadeToImage(imagePath));
    }

    private void CrossfadeToImage(string newImagePath)
    {
        if (_crossfadeInProgress)
        {
            // Queue will catch the next call naturally; skip to avoid stacking animations
            return;
        }

        BitmapImage newBitmap = null;

        if (!string.IsNullOrEmpty(newImagePath) && File.Exists(newImagePath))
        {
            try { newBitmap = new BitmapImage(new Uri(newImagePath)); }
            catch (Exception ex) { Trace.WriteLine($"LUTM: Image load error: {ex.Message}"); }
        }

        bool bottomIsEmpty = _currentImagePath == null;

        // Load the incoming image into the Top vessel (currently transparent)
        PresetImageTop.Source = newBitmap;
        PresetImageTop.Opacity = 0;

        _crossfadeInProgress = true;

        var storyboard = new Storyboard();

        if (bottomIsEmpty)
        {
            // First image ever — fade both vessels in together so there is no pop
            var fadeInBottom = MakeOpacityAnimation(PresetImageBottom, from: 0, to: 1, duration: 0.2);
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: 0.2);
            storyboard.Children.Add(fadeInBottom);
            storyboard.Children.Add(fadeInTop);

            // Prime Bottom with the same image so the vessels are in sync
            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 0;
        }
        else
        {
            // Normal transition — just fade the Top vessel in over the settled Bottom
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: 0.2);
            storyboard.Children.Add(fadeInTop);
        }

        storyboard.Completed += (s, e) =>
        {
            // Settle: copy the new image into Bottom, hide Top
            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 1;
            PresetImageTop.Opacity = 0;
            PresetImageTop.Source = null;

            _currentImagePath = newImagePath;
            _crossfadeInProgress = false;
        };

        storyboard.Begin();
    }

    /// <summary>Creates a DoubleAnimation targeting UIElement.Opacity and wires the Storyboard targets.</summary>
    private static DoubleAnimation MakeOpacityAnimation(UIElement target, double from, double to, double duration)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(duration)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    // -------------------------------------------------------------------------
    // Install button
    // -------------------------------------------------------------------------

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || !_selectedPreset.IsComplete)
        {
            Trace.WriteLine("LUTM: InstallButton_Click with no valid preset - ignoring");
            return;
        }

        var preset = _selectedPreset;   // capture before async yields
        InstallButton.IsEnabled = false;

        try
        {
            Trace.WriteLine($"LUTM: Installing preset [{preset.Name}]");

            bool success = await ReplaceRtxFilesWithElevation(
                preset.LutPath, preset.SkyPath, preset.WaterPath);

            if (success)
            {
                OperationSuccessful = true;
                StatusMessage = $"Installed LUT preset: {preset.Name}";
                Trace.WriteLine($"LUTM: Preset [{preset.Name}] installed");

                // Re-detect to confirm what is now on disk, then refresh the dropdown label
                _installedPreset = await DetectCurrentPresetAsync();
                Trace.WriteLine("LUTM: Post-install detection: " + (_installedPreset?.Name ?? "Unknown"));

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    // Refresh the dropdown label without changing the selection
                    if (_selectedPreset != null)
                        ApplySelection(_selectedPreset);
                });
            }
            else
            {
                Trace.WriteLine($"LUTM: Install of [{preset.Name}] failed or was cancelled");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"LUTM: Error in InstallButton_Click: {ex.Message}");
        }
        finally
        {
            _ = this.DispatcherQueue.TryEnqueue(() =>
            {
                // Use _installedPreset — only updated by DetectCurrentPresetAsync on success.
                // If UAC was denied, _installedPreset still reflects what was on disk before.
                bool isInstalled = _installedPreset != null &&
                                   string.Equals(_installedPreset.Name, preset.Name, StringComparison.OrdinalIgnoreCase);

                InstallButton.IsEnabled = _selectedPreset?.IsComplete == true;
                // Install button content
                if (isInstalled)
                {
                    InstallButton.Content = "Reinstall";
                    InstallButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];

                    // Pane thingy
                    var theme = LeftEdgeOfInstallButton.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
                    var themeDict = Application.Current.Resources.ThemeDictionaries[theme] as ResourceDictionary;
                    var color = (Windows.UI.Color)themeDict["FakeSplitButtonBrightBorderColor"];
                    LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush(color);
                }
                else
                {
                    InstallButton.Content = "Install";
                    InstallButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

                    // Pane thingy
                    var accentColorKey = LeftEdgeOfInstallButton.ActualTheme == ElementTheme.Light ? "SystemAccentColorLight1" : "SystemAccentColorLight3";
                    LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources[accentColorKey]);
                }
            }); 
        }
    }

    // -------------------------------------------------------------------------
    // Elevated file replacement — all-or-none, one UAC prompt
    // -------------------------------------------------------------------------

    private Task<bool> ReplaceRtxFilesWithElevation(string srcLut, string srcSky, string srcWater)
    {
        Trace.WriteLine("LUTM: ReplaceRtxFilesWithElevation");
        Trace.WriteLine("  srcLut  =" + srcLut + "  exists=" + File.Exists(srcLut));
        Trace.WriteLine("  srcSky  =" + srcSky + "  exists=" + File.Exists(srcSky));
        Trace.WriteLine("  srcWater=" + srcWater + "  exists=" + File.Exists(srcWater));
        Trace.WriteLine("  dstLut  =" + DstLut);
        Trace.WriteLine("  dstSky  =" + DstSky);
        Trace.WriteLine("  dstWater=" + DstWater);

        if (!File.Exists(srcLut)) { Trace.WriteLine("LUTM: Aborting - srcLut missing"); return Task.FromResult(false); }
        if (!File.Exists(srcSky)) { Trace.WriteLine("LUTM: Aborting - srcSky missing"); return Task.FromResult(false); }
        if (!File.Exists(srcWater)) { Trace.WriteLine("LUTM: Aborting - srcWater missing"); return Task.FromResult(false); }

        var files = new List<(string, string)>
        {
            (srcLut,   DstLut),
            (srcSky,   DstSky),
            (srcWater, DstWater)
        };
        return ReplaceFilesWithElevation(files);
    }

    private async Task<bool> ReplaceFilesWithElevation(List<(string sourcePath, string destPath)> filesToReplace)
    {
        try
        {
            return await Task.Run(() =>
            {
                var scriptLines = new List<string> { "@echo off" };
                foreach (var (sourcePath, destPath) in filesToReplace)
                    scriptLines.Add("copy /Y \"" + sourcePath + "\" \"" + destPath + "\" >nul 2>&1");
                scriptLines.Add("exit %ERRORLEVEL%");

                var batchScript = string.Join("\r\n", scriptLines);
                var tempBatchPath = Path.Combine(
                    Path.GetTempPath(),
                    "rtx_defaults_" + Guid.NewGuid().ToString("N") + ".bat");
                File.WriteAllText(tempBatchPath, batchScript);

                Trace.WriteLine("LUTM: Batch: " + tempBatchPath);
                Trace.WriteLine("LUTM: Contents:\n" + batchScript);

                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + tempBatchPath + "\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };

                    var process = System.Diagnostics.Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        Trace.WriteLine("LUTM: Exit code: " + process.ExitCode);
                        return process.ExitCode == 0;
                    }

                    Trace.WriteLine("LUTM: Process.Start returned null");
                    return false;
                }
                finally
                {
                    try
                    {
                        Thread.Sleep(300);
                        if (File.Exists(tempBatchPath)) File.Delete(tempBatchPath);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine("LUTM: Error in ReplaceFilesWithElevation: " + ex.Message);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Hash comparison  (SHA-256)
    // -------------------------------------------------------------------------

    private static bool HashesMatch(string pathA, string pathB)
    {
        using var sha = SHA256.Create();
        using var streamA = File.OpenRead(pathA);
        var hashA = sha.ComputeHash(streamA);
        sha.Initialize();
        using var streamB = File.OpenRead(pathB);
        var hashB = sha.ComputeHash(streamB);
        return System.MemoryExtensions.SequenceEqual(
            (System.ReadOnlySpan<byte>)hashA,
            (System.ReadOnlySpan<byte>)hashB);
    }

    // -------------------------------------------------------------------------
    // LutPreset  — a folder containing the 3 required RTX files + optional image
    //
    // For the Default preset:
    //   FolderPath = LocalAppData\Lut_Defaults
    //   ImagePath  = overridden to Assets\lut\Default.png  (passed via constructor)
    //
    // For all other presets:
    //   FolderPath = Assets\lut\<PresetName>
    //   ImagePath  = Assets\lut\<PresetName>\image.png  (optional)
    // -------------------------------------------------------------------------

    private sealed class LutPreset
    {
        public string Name { get; }
        public string FolderPath { get; }
        public bool IsDefault { get; }

        // RTX file paths are always inside FolderPath
        public string LutPath => Path.Combine(FolderPath, "look_up_tables.png");
        public string SkyPath => Path.Combine(FolderPath, "sky.png");
        public string WaterPath => Path.Combine(FolderPath, "water_n.tga");

        // UI image: for Default the caller supplies an override path; others use image.png in folder
        private readonly string _imagePathOverride;
        public string ImagePath => _imagePathOverride ?? Path.Combine(FolderPath, "image.png");

        public bool IsComplete =>
            File.Exists(LutPath) && File.Exists(SkyPath) && File.Exists(WaterPath);

        /// <param name="imagePathOverride">
        ///   Optional override for the preview image path (used by the Default preset
        ///   so its UI image can live in Assets\lut\ rather than in Lut_Defaults).
        /// </param>
        public LutPreset(string name, string folderPath,
                         string imagePathOverride = null, bool isDefault = false)
        {
            Name = name;
            FolderPath = folderPath;
            IsDefault = isDefault;
            _imagePathOverride = imagePathOverride;
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/README.md#LUT-Tutorial");
    }
}
