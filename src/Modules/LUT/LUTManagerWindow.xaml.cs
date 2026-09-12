using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.LUT;

/// <summary>
/// The RTX LUT manager's window: chrome, the preset dropdown, the crossfading preview image
/// and the install button. Everything it does to actual files - preset discovery, the backup
/// of the game's own three files, detecting what is installed and the elevated write that
/// installs a preset - lives in <see cref="LUTManager"/>. This holds one and renders it.
/// </summary>
public sealed partial class LUTManagerWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _isClosing;

    private readonly LUTManager _manager = new();

    private LutPreset? _selectedPreset;
    private LutPreset? _installedPreset;

    private CancellationTokenSource? _scanCancellationTokenSource;
    private bool _crossfadeInProgress = false;

    /// <summary>
    /// True from the moment an install is started until it has finished and the button has
    /// been restored. Read and written only on the UI thread, and always cleared in a finally -
    /// see <see cref="InstallButton_Click"/>.
    /// </summary>
    private bool _installInProgress;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    public LUTManagerWindow()
    {
        this.InitializeComponent();

        var manager = WinUIEx.WindowManager.Get(this);
        manager.MinWidth = WindowMinSizeX;
        manager.MinHeight = WindowMinSizeY;
        manager.IsResizable = true;
        manager.IsMaximizable = true;

        _appWindow = this.AppWindow;

        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        ThemeService.ThemeChanged += ApplyTheme;
        ApplyTheme(ThemeService.ResolveInitialTheme());

        // The centered title dims with the window, same as the system's caption buttons
        // beside it - this window has no titlebar controls of its own to include.
        TitleBarFocus.Attach(this, WindowTitle);

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "vrtx.lut.ico"));

        InstallButton.IsEnabledChanged += (s, e) => ApplyInstallButtonBevel(_isPresetInstalled);

        this.Closed += LUTManagerWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += LUTManagerWindow_Loaded;
    }

    private async void LUTManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= LUTManagerWindow_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarArea);

            var target = Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft Release";
            WindowTitle.Text = $"RTX LUT manager - {target}";

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void LUTManagerWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= LUTManagerWindow_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= LUTManagerWindow_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
        ApplyInstallButtonBevel(_isPresetInstalled);
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? Persistent.MinecraftPreviewInstallPath
                : Persistent.MinecraftInstallPath;

            string? minecraftPath = null;

            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath, Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"[LUTManager] Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine("[LUTManager] Cache became invalid, clearing");
                    if (isPreview)
                        Persistent.MinecraftPreviewInstallPath = null;
                    else
                        Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                    ManualSelectionButton.Visibility = Visibility.Visible);

                _scanCancellationTokenSource = new CancellationTokenSource();
                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview, _scanCancellationTokenSource.Token);

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[LUTManager] System search cancelled or failed");
                    return;
                }
            }

            if (minecraftPath != null)
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
        // Step 1: Bind to this install and establish the LocalAppData defaults folder
        if (!_manager.TryAttach(minecraftPath))
        {
            StatusMessage = "Could not establish defaults folder";
            this.Close();
            return;
        }

        // Step 2: Back up game defaults into Lut_Defaults — all-or-none
        await _manager.EnsureDefaultsBackedUpAsync();

        // Step 3: Discover all presets (Default first, then Assets\lut\ subfolders)
        _manager.LoadPresets();

        // Step 4: Detect which preset is currently installed
        _installedPreset = await _manager.DetectCurrentPresetAsync();
        Trace.WriteLine($"[LUTManager] Detected preset: {_installedPreset?.Name ?? "Unknown"}");

        // Step 5: Populate dropdown and settle the UI
        PopulateDropdown(_installedPreset);

        // Step 6: Show main UI
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
            PsaCard.Populate(LutAnnouncementsPanel, OnlineTextsContent.LutManagerAnnouncements);
        });
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = EnvironmentVariables.Persistent.IsTargetingPreview;
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
    // Dropdown population
    // -------------------------------------------------------------------------

    private void PopulateDropdown(LutPreset? installedPreset)
    {
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            var flyout = SelectPresetMenu.Flyout as MenuFlyout;
            if (flyout == null) return;
            flyout.Items.Clear();

            foreach (var preset in _manager.Presets)
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
                ApplySelection(installedPreset);
            }
            else
            {
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

    private void ApplySelection(LutPreset? preset)
    {
        _selectedPreset = preset;

        if (preset == null)
        {
            SelectPresetMenu.Content = "Select a preset...";
            InstallButton.IsEnabled = false;
            UpdatePresetImage(null);
            return;
        }

        bool isInstalled = _installedPreset != null &&
                           string.Equals(preset.Name, _installedPreset.Name, StringComparison.OrdinalIgnoreCase);

        SelectPresetMenu.Content = isInstalled
            ? $"Installed Preset: {preset.Name}"
            : $"Selected Preset: {preset.Name}";

        // An install in flight keeps the button down even if the user picks a different preset
        // from the dropdown while it runs - without this, selecting one would hand the button
        // straight back and a second click would start a second elevated copy.
        InstallButton.IsEnabled = preset.IsComplete && !_installInProgress;

        if (isInstalled)
        {
            InstallButton.Content = "Reinstall";
            InstallButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ApplyInstallButtonBevel(true);
        }
        else
        {
            InstallButton.Content = "Install";
            InstallButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ApplyInstallButtonBevel(false);
        }

        UpdatePresetImage(preset);
    }

    private bool _isPresetInstalled; // mirrors whatever local `isInstalled` your install-state method already computes
    private void ApplyInstallButtonBevel(bool isInstalled)
    {
        _isPresetInstalled = isInstalled;

        LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(LeftEdgeOfInstallButton.ActualTheme, ThemeService.BevelEdge.Left,
                accented: !isInstalled, isEnabled: InstallButton.IsEnabled));
    }

    // -------------------------------------------------------------------------
    // Preset preview image — crossfade between two layered Image vessels
    // -------------------------------------------------------------------------

    private string? _currentImagePath = null;

    private void UpdatePresetImage(LutPreset? preset)
    {
        var imagePath = _manager.ResolveImagePath(preset);

        if (string.Equals(imagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase))
            return;

        _ = this.DispatcherQueue.TryEnqueue(() => CrossfadeToImage(imagePath));
    }

    private void CrossfadeToImage(string? newImagePath)
    {
        if (_crossfadeInProgress)
            return;

        BitmapImage? newBitmap = null;

        if (!string.IsNullOrEmpty(newImagePath) && File.Exists(newImagePath))
        {
            try { newBitmap = new BitmapImage(new Uri(newImagePath)); }
            catch (Exception ex) { Trace.WriteLine($"[LUTManager] Image load error: {ex.Message}"); }
        }

        bool bottomIsEmpty = _currentImagePath == null;

        PresetImageTop.Source = newBitmap;
        PresetImageTop.Opacity = 0;

        _crossfadeInProgress = true;

        var storyboard = new Storyboard();

        double duration = Persistent.SuspendUIAnimations ? 0.02 : 0.2;

        if (bottomIsEmpty)
        {
            var fadeInBottom = MakeOpacityAnimation(PresetImageBottom, from: 0, to: 1, duration: duration);
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: duration);
            storyboard.Children.Add(fadeInBottom);
            storyboard.Children.Add(fadeInTop);

            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 0;
        }
        else
        {
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: duration);
            storyboard.Children.Add(fadeInTop);
        }

        storyboard.Completed += (s, e) =>
        {
            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 1;
            PresetImageTop.Opacity = 0;
            PresetImageTop.Source = null;

            _currentImagePath = newImagePath;
            _crossfadeInProgress = false;
        };

        storyboard.Begin();
    }

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
            Trace.WriteLine("[LUTManager] InstallButton_Click with no valid preset - ignoring");
            return;
        }

        // Installing is an elevated file copy: it writes a batch script, raises a UAC prompt
        // and waits, with the UI thread free for most of it. The disabled button below is the
        // visible half of stopping a second one from starting; this flag is the half that
        // doesn't depend on every path that touches IsEnabled getting it right.
        // (Helpers.ReplaceFilesWithElevation refuses overlapping calls outright as a backstop.)
        if (_installInProgress)
        {
            Trace.WriteLine("[LUTManager] An install is already in progress - ignoring this click");
            return;
        }

        var preset = _selectedPreset;

        // Nothing between setting the flag and entering the try, so there is no statement that
        // could throw its way past the finally and leave the button permanently down.
        _installInProgress = true;
        try
        {
            InstallButton.IsEnabled = false; // fires IsEnabledChanged -> dims bevel using current _isPresetInstalled, correct mid-install look

            Trace.WriteLine($"[LUTManager] Installing preset [{preset.Name}]");

            bool success = await _manager.InstallAsync(preset);

            if (success)
            {
                OperationSuccessful = true;
                StatusMessage = $"Installed LUT preset: {preset.Name}";
                Trace.WriteLine($"[LUTManager] Preset [{preset.Name}] installed");

                _installedPreset = await _manager.DetectCurrentPresetAsync();
                Trace.WriteLine("[LUTManager] Post-install detection: " + (_installedPreset?.Name ?? "Unknown"));

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_selectedPreset != null)
                        ApplySelection(_selectedPreset);
                });
            }
            else
            {
                Trace.WriteLine($"[LUTManager] Install of [{preset.Name}] failed or was cancelled");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Error in InstallButton_Click: {ex.Message}");
        }
        finally
        {
            // Cleared before the callback below re-enables the button, so ApplySelection - which
            // consults this flag - agrees with what that callback is about to draw.
            _installInProgress = false;

            _ = this.DispatcherQueue.TryEnqueue(() =>
            {
                bool isInstalled = _installedPreset != null &&
                                   string.Equals(_installedPreset.Name, preset.Name, StringComparison.OrdinalIgnoreCase);

                InstallButton.Content = isInstalled ? "Reinstall" : "Install";
                InstallButton.Style = (Style)Application.Current.Resources[
                    isInstalled ? "DefaultButtonStyle" : "AccentButtonStyle"];

                // Order matters: set IsEnabled first (fires IsEnabledChanged with the
                // still-stale _isPresetInstalled), then ApplyInstallButtonBevel runs
                // explicitly with the fresh isInstalled value and wins — final bevel
                // state is always correct regardless of what the auto-handler drew first.
                InstallButton.IsEnabled = _selectedPreset?.IsComplete == true;
                ApplyInstallButtonBevel(isInstalled);
            });
        }
    }
}
