using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables; // For Public Pack version variables, if null or empty = not installed

namespace Vanilla_RTX_App.PackUpdate;

public sealed partial class PackUpdateWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;

    // Pane animation durations
    private readonly TimeSpan _fadeInDuration = TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _fadeOutDuration = TimeSpan.FromMilliseconds(175);

    private const string REFRESH_COOLDOWN_KEY = "PackUpdater_RefreshCooldown_LastClickTimestamp";
    private const int REFRESH_COOLDOWN_SECONDS = 179;
    private DispatcherTimer? _cooldownTimer;

    // For button text animation
    private DispatcherTimer? _installingAnimationTimer;
    private int _animationDots = 0;

    public PackUpdateWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();

        InitializeHoverEffects();

        _mainWindow = mainWindow;
        _updater = new PackUpdater();

        // Theme
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

        this.Activated += PackUpdateWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void InitializeHoverEffects()
    {
        // Main Panels
        SetupPanelHoverEffect(NormalsPanel, NormalsOverlay);
        SetupPanelHoverEffect(VanillaPanel, VanillaOverlay);
        SetupPanelHoverEffect(OpusPanel, OpusOverlay);

        // Secondary Panels
        SetupPanelHoverEffect(AddOnsPanel, AddOnsOverlay);
        SetupPanelHoverEffect(ChemistryPanel, ChemistryOverlay);
        SetupPanelHoverEffect(CreativePanel, CreativeOverlay);
    }

    private void SetupPanelHoverEffect(Border panel, Border overlay)
    {
        panel.PointerEntered += (s, e) =>
        {
            AnimateOpacity(overlay, 1.0, _fadeInDuration);
        };

        panel.PointerExited += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };
        panel.PointerCaptureLost += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };
        panel.PointerCanceled += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };
    }

    private void AnimateOpacity(UIElement element, double toValue, TimeSpan duration)
    {
        var storyboard = new Storyboard();

        var opacityAnimation = new DoubleAnimation
        {
            To = toValue,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase
            {
                EasingMode = toValue > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        storyboard.Children.Add(opacityAnimation);
        storyboard.Begin();
    }

    private void InitializeRefreshButton()
    {
        UpdateRefreshButtonState();
        _cooldownTimer = new DispatcherTimer();
        _cooldownTimer.Interval = TimeSpan.FromSeconds(1);
        _cooldownTimer.Tick += (s, e) => UpdateRefreshButtonState();
        _cooldownTimer.Start();
    }

    private void UpdateRefreshButtonState()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;

            if (settings.Values.TryGetValue(REFRESH_COOLDOWN_KEY, out var storedValue) && storedValue is long ticks)
            {
                var lastClickTime = new DateTime(ticks, DateTimeKind.Utc);
                var elapsed = DateTime.UtcNow - lastClickTime;
                var remainingSeconds = REFRESH_COOLDOWN_SECONDS - (int)elapsed.TotalSeconds;

                if (remainingSeconds > 0)
                {
                    RefreshButton.IsEnabled = false;
                    RefreshIcon.Visibility = Visibility.Collapsed;
                    RefreshCountdownText.Visibility = Visibility.Visible;
                    RefreshCountdownText.Text = remainingSeconds.ToString();
                    return;
                }
            }

            RefreshButton.IsEnabled = true;
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error updating refresh button state: {ex.Message}");
            RefreshButton.IsEnabled = true;
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Trace.WriteLine("=== REFRESH BUTTON CLICKED (PACK UPDATER) ===");

            _updater.ResetRemoteVersionCache();
            _updater.ResetCacheCheckCooldown();

            await _mainWindow.LocatePacksButton_Click(true);
            UpdateInstalledVersionDisplays();
            await FetchAndDisplayRemoteVersions();

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;

            UpdateRefreshButtonState();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("💣 ERROR AT RefreshButton_Click: " + ex);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _mainWindow.Closed -= MainWindow_Closed;
        _installingAnimationTimer?.Stop();
        _cooldownTimer?.Stop();
        this.Close();
    }

    private async void PackUpdateWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= PackUpdateWindow_Activated;

            _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SetTitleBarDragRegion();
            });

            var text = TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";
            WindowTitle.Text = $"Vanilla RTX resource packs for {text}";

            SetupShadows();

            await InitializePackInformation();
            SetupButtonHandlers();

            InitializeRefreshButton();

            // Check if installation is in progress and update UI accordingly
            CheckAndHandleOngoingInstallation();
        }
    }

    private void SetTitleBarDragRegion()
    {
        if (_appWindow.TitleBar != null && TitleBarArea.XamlRoot != null)
        {
            try
            {
                var scaleAdjustment = TitleBarArea.XamlRoot.RasterizationScale;
                var dragRectHeight = (int)(TitleBarArea.ActualHeight * scaleAdjustment);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error setting drag region: {ex.Message}");
            }
        }
    }

    private void SetupShadows()
    {
        // Shadow setup is handled in XAML
    }

    // ======================= Initialization =======================

    private async Task InitializePackInformation()
    {
        UpdateInstalledVersionDisplays();
        await FetchAndDisplayRemoteVersions();
    }

    private void UpdateInstalledVersionDisplays()
    {
        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        VanillaRTX_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXVersion) ? "Not installed" : vanillaRTXVersion;

        VanillaRTXNormals_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXNormalsVersion) ? "Not installed" : vanillaRTXNormalsVersion;

        VanillaRTXOpus_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXOpusVersion) ? "Not installed" : vanillaRTXOpusVersion;
    }

    private async Task FetchAndDisplayRemoteVersions()
    {
        (string? version, VersionSource source) rtx = (null, VersionSource.Remote);
        (string? version, VersionSource source) normals = (null, VersionSource.Remote);
        (string? version, VersionSource source) opus = (null, VersionSource.Remote);

        try
        {
            var result = await _updater.GetRemoteVersionsAsync();
            rtx = result.rtx;
            normals = result.normals;
            opus = result.opus;
        }
        catch
        {
            // Fetch failed completely
        }

        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        VanillaRTX_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTX_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTX_AvailableVersion.Text = GetAvailabilityText(rtx.version, vanillaRTXVersion, rtx.source);

        VanillaRTXNormals_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXNormals_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXNormals_AvailableVersion.Text = GetAvailabilityText(normals.version, vanillaRTXNormalsVersion, normals.source);

        VanillaRTXOpus_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXOpus_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXOpus_AvailableVersion.Text = GetAvailabilityText(opus.version, vanillaRTXOpusVersion, opus.source);

        await UpdateAllButtonStates(rtx.version, normals.version, opus.version,
            vanillaRTXVersion, vanillaRTXNormalsVersion, vanillaRTXOpusVersion);
    }

    private string GetAvailabilityText(string? availableVersion, string? installedVersion, VersionSource source)
    {
        if (string.IsNullOrEmpty(availableVersion))
        {
            return "Not available";
        }

        bool isUpToDate = !string.IsNullOrEmpty(installedVersion) && availableVersion == installedVersion;

        string suffix = "";
        if (source == VersionSource.ZipballFallback)
        {
            suffix = isUpToDate ? " (Up-to-date, from offline cache)" : " (from offline cache)";
        }
        else if (source == VersionSource.CachedRemote)
        {
            suffix = isUpToDate ? " (Up-to-date)*" : "";
        }
        else
        {
            suffix = isUpToDate ? " (Up-to-date)" : "";
        }

        return $"{availableVersion}{suffix}";
    }

    private async Task UpdateAllButtonStates(
        string? rtxRemote,
        string? normalsRemote,
        string? opusRemote,
        string? rtxInstalled,
        string? normalsInstalled,
        string? opusInstalled)
    {
        bool anyNeedsUpdate = false;

        if (!string.IsNullOrEmpty(rtxRemote) && _updater.IsRemoteVersionNewerThanInstalled(rtxInstalled, rtxRemote))
            anyNeedsUpdate = true;

        if (!string.IsNullOrEmpty(normalsRemote) && _updater.IsRemoteVersionNewerThanInstalled(normalsInstalled, normalsRemote))
            anyNeedsUpdate = true;

        if (!string.IsNullOrEmpty(opusRemote) && _updater.IsRemoteVersionNewerThanInstalled(opusInstalled, opusRemote))
            anyNeedsUpdate = true;

        if (anyNeedsUpdate)
        {
            _updater.InvalidateCache();
            System.Diagnostics.Trace.WriteLine("Cache invalidated: installed version(s) outdated vs remote");
        }

        await UpdateSingleButtonState(VanillaRTX_InstallButton, VanillaRTX_EnhancementsToggle,
            PackType.VanillaRTX, rtxInstalled, rtxRemote);
        await UpdateSingleButtonState(VanillaRTXNormals_InstallButton, VanillaRTXNormals_EnhancementsToggle,
            PackType.VanillaRTXNormals, normalsInstalled, normalsRemote);
        await UpdateSingleButtonState(VanillaRTXOpus_InstallButton, VanillaRTXOpus_EnhancementsToggle,
            PackType.VanillaRTXOpus, opusInstalled, opusRemote);
    }

    private async Task UpdateSingleButtonState(Button button, ToggleSwitch toggle,
        PackType packType, string? installedVersion, string? remoteVersion)
    {
        // If installation is in progress, handle separately
        if (_updater.IsInstallationInProgress())
        {
            var currentlyInstalling = _updater.GetCurrentlyInstallingPack();
            if (currentlyInstalling == packType)
            {
                // This pack is being installed - show Installing... with animation
                button.IsEnabled = false;
                toggle.IsEnabled = false;
                // Animation is handled by timer
                return;
            }
            else
            {
                // Different pack is being installed - disable this button
                button.IsEnabled = false;
                toggle.IsEnabled = false;
                return;
            }
        }

        bool isInstalled = !string.IsNullOrEmpty(installedVersion);
        bool remoteAvailable = !string.IsNullOrEmpty(remoteVersion);
        bool packInCache = await _updater.DoesPackExistInCache(packType);

        button.IsEnabled = true;
        toggle.IsEnabled = true;

        if (!isInstalled)
        {
            button.Content = "Install";
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.IsEnabled = remoteAvailable || packInCache;
        }
        else if (remoteAvailable && _updater.IsRemoteVersionNewerThanInstalled(installedVersion, remoteVersion))
        {
            button.Content = "Update";
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.IsEnabled = true;
        }
        else
        {
            button.Content = "Reinstall";
            button.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            button.IsEnabled = remoteAvailable || packInCache;
        }
    }

    // ======================= Installation State Management =======================

    private void CheckAndHandleOngoingInstallation()
    {
        if (_updater.IsInstallationInProgress())
        {
            var currentPack = _updater.GetCurrentlyInstallingPack();

            // Start animation timer
            StartInstallingAnimation(currentPack);

            // Disable all buttons
            DisableAllInstallButtons();

            // Monitor for completion
            MonitorInstallationCompletion();
        }
    }

    private void StartInstallingAnimation(PackType? packType)
    {
        if (packType == null) return;

        _animationDots = 0;

        _installingAnimationTimer = new DispatcherTimer();
        _installingAnimationTimer.Interval = TimeSpan.FromMilliseconds(500);
        _installingAnimationTimer.Tick += (s, e) =>
        {
            _animationDots = (_animationDots + 1) % 4;
            var dots = new string('.', _animationDots);


            var buttonText = $"Installing{dots}";

            switch (packType.Value)
            {
                case PackType.VanillaRTX:
                    VanillaRTX_InstallButton.Content = buttonText;
                    break;
                case PackType.VanillaRTXNormals:
                    VanillaRTXNormals_InstallButton.Content = buttonText;
                    break;
                case PackType.VanillaRTXOpus:
                    VanillaRTXOpus_InstallButton.Content = buttonText;
                    break;
            }
        };
        _installingAnimationTimer.Start();
    }

    private void StopInstallingAnimation()
    {
        if (_installingAnimationTimer != null)
        {
            _installingAnimationTimer.Stop();
            _installingAnimationTimer = null;
        }
    }

    private async void MonitorInstallationCompletion()
    {
        // Poll for installation completion
        while (_updater.IsInstallationInProgress())
        {
            await Task.Delay(500);
        }

        // Installation completed
        StopInstallingAnimation();

        // Refresh versions and re-enable buttons
        await RefreshInstalledVersions();
        await FetchAndDisplayRemoteVersions();
    }

    private void DisableAllInstallButtons()
    {
        VanillaRTX_InstallButton.IsEnabled = false;
        VanillaRTX_EnhancementsToggle.IsEnabled = false;

        VanillaRTXNormals_InstallButton.IsEnabled = false;
        VanillaRTXNormals_EnhancementsToggle.IsEnabled = false;

        VanillaRTXOpus_InstallButton.IsEnabled = false;
        VanillaRTXOpus_EnhancementsToggle.IsEnabled = false;
    }

    // ======================= Button Handlers =======================

    private void SetupButtonHandlers()
    {
        VanillaRTX_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTX, VanillaRTX_EnhancementsToggle.IsOn);

        VanillaRTXNormals_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTXNormals, VanillaRTXNormals_EnhancementsToggle.IsOn);

        VanillaRTXOpus_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTXOpus, VanillaRTXOpus_EnhancementsToggle.IsOn);
    }

    private async void StartInstallation(PackType packType, bool enableEnhancements)
    {
        // Check if installation is already running
        if (_updater.IsInstallationInProgress())
        {
            System.Diagnostics.Trace.WriteLine("Installation already in progress - ignoring button click");
            return;
        }

        // Disable all buttons immediately
        DisableAllInstallButtons();

        // Start animation for this pack
        StartInstallingAnimation(packType);

        try
        {
            var (success, logs) = await Task.Run(() =>
                _updater.UpdateSinglePackAsync(packType, enableEnhancements));

            if (success)
            {
                System.Diagnostics.Trace.WriteLine($"{GetPackDisplayName(packType)} installed successfully");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"{GetPackDisplayName(packType)} installation failed");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Error installing {GetPackDisplayName(packType)}: {ex.Message}");
        }
        finally
        {
            // Stop animation
            StopInstallingAnimation();

            // Refresh versions and button states
            await RefreshInstalledVersions();
            await FetchAndDisplayRemoteVersions();
        }
    }

    private async Task RefreshInstalledVersions()
    {
        await _mainWindow.LocatePacksButton_Click();

        this.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateInstalledVersionDisplays();
        });
    }

    private string GetPackDisplayName(PackType packType)
    {
        return packType switch
        {
            PackType.VanillaRTX => "Vanilla RTX",
            PackType.VanillaRTXNormals => "Vanilla RTX Normals",
            PackType.VanillaRTXOpus => "Vanilla RTX Opus",
            _ => "Unknown Pack"
        };
    }
}
