using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using WinUIEx;
using static Vanilla_RTX_App.EnvironmentVariables; // For Public Pack version variables, if null or empty = not installed

namespace Vanilla_RTX_App.Modules.PackUpdater;

public sealed partial class PackUpdaterWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;
    private bool _isClosing;

    private double animationSpeedMultiplier => Persistent.SuspendUIAnimations ? 0.01 : 1.0;
    private TimeSpan _fadeInDuration => TimeSpan.FromMilliseconds(150 * animationSpeedMultiplier);
    private TimeSpan _fadeOutDuration => TimeSpan.FromMilliseconds(125 * animationSpeedMultiplier);

    // This window's second line of defence against deploying a stale cache now lives in
    // PackUpdater.InvalidateCacheIfStaleAsync, called from UpdateAllButtonStates.
    //
    // It used to live here, and it was wrong in both directions. It triggered on INSTALLED being
    // behind the remote and then invalidated unconditionally, so it would throw away a perfectly
    // current zipball just because the user was running an older pack - an ~11MB re-download and
    // a GitHub hit to replace a file that was already correct. And because the only thing holding
    // that back was a 1-minute cooldown, the genuinely stale case could still slip through it and
    // through PackUpdater's own 55-minute cooldown at the same time, and deploy stale anyway.
    //
    // Asking the right question - is the CACHE behind the remote? - fixes both, and costs nothing:
    // the remote numbers are already in hand from GetRemoteVersionsAsync, and the cache's own
    // numbers are read off the zipball on disk. No request, so no cooldown to reason about.

    private string? _currentInstallActionType;

    private DispatcherTimer? _installingAnimationTimer;
    private int _animationDots = 0;

    public PackUpdaterWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();

        InitializeHoverEffects();

        SpecialOccasionPanel.Visibility = Helpers.GetSpecialOccasionName() == "christmas"
            ? Visibility.Visible
            : Visibility.Collapsed;

        _mainWindow = mainWindow;
        _updater = mainWindow._updater ?? new PackUpdater();

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

        this.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "vrtx.update.ico"));

        this.Closed += PackUpdaterWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += PackUpdaterWindow_Loaded;
    }
    private async void PackUpdaterWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= PackUpdaterWindow_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarDragArea);

            var text = EnvironmentVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";
            WindowTitle.Text = $"Vanilla RTX resource packs for {text}";

            await InitializePackInformation();
            if (_isClosing) return;

            SetupButtonHandlers();
            CheckAndHandleOngoingInstallation();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterWindow] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void PackUpdaterWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= PackUpdaterWindow_Loaded;

        StopInstallingAnimation();

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= PackUpdaterWindow_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }

    // INITIALIZATION =================================
    private void InitializeHoverEffects()
    {
        // Main Panels
        SetupPanelHoverEffect(NormalsPanel, NormalsOverlay);
        SetupPanelHoverEffect(VanillaPanel, VanillaOverlay);
        SetupPanelHoverEffect(OpusPanel, OpusOverlay);

        // SpecialOccasionPanel
        SetupPanelHoverEffect(SpecialOccasionPanel, SpecialOccasionOverlay);

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


    // ======================= Initialization =======================

    private async Task InitializePackInformation()
    {
        PsaCard.Populate(PackUpdateAnnouncementsPanel, OnlineTextsContent.PackUpdateAnnouncements, cardFontSize: 13);
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
            return "Failed to check";
        }

        bool isUpToDate = !string.IsNullOrEmpty(installedVersion) && availableVersion == installedVersion;

        string suffix = "";
        if (source == VersionSource.ZipballFallback)
        {
            // does the case where installed version is older than an offline cache really ever happen? NAH! offline cache would only be there if user has updated recently
            // But we're ready! lovely overengineered bullshit
            suffix = isUpToDate ? "(Up-to-date, from offline cache)" : "(From offline cache)";
        }
        else if (source == VersionSource.CachedRemote)
        {
            suffix = isUpToDate ? "(You seem up-to-date)" : "";
        }
        else
        {
            suffix = isUpToDate ? "(You're up-to-date!)" : "";
        }

        return $"{availableVersion} {suffix}";
    }

    private async Task UpdateAllButtonStates(
        string? rtxRemote,
        string? normalsRemote,
        string? opusRemote,
        string? rtxInstalled,
        string? normalsInstalled,
        string? opusInstalled)
    {
        // Drop the cached zipball if, and only if, it is actually behind the remote. Runs on every
        // refresh with no cooldown because it makes no requests at all - the remote numbers are
        // the ones just fetched above, and the cache's are read off the zipball on disk.
        await _updater.InvalidateCacheIfStaleAsync(rtxRemote, normalsRemote, opusRemote);

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


        // If installation is in progress, handle differently
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


            var actionWord = _currentInstallActionType ?? "Installing";
            var actionIng = actionWord switch
            {
                "Update" => "Updating",
                "Install" => "Installing",
                "Reinstall" => "Reinstalling",
                _ => "Installing"
            };

            var buttonText = $"{actionIng}{dots}";

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
            Trace.WriteLine("Installation already in progress - ignoring button click");
            return;
        }

        _currentInstallActionType = GetButtonForPackType(packType)?.Content?.ToString();
        Button? GetButtonForPackType(PackType packType)
        {
            return packType switch
            {
                PackType.VanillaRTX => VanillaRTX_InstallButton,
                PackType.VanillaRTXNormals => VanillaRTXNormals_InstallButton,
                PackType.VanillaRTXOpus => VanillaRTXOpus_InstallButton,
                _ => null
            };
        }


        // Disable all buttons immediately
        DisableAllInstallButtons();

        // Start animation for this pack
        StartInstallingAnimation(packType);

        try
        {
            var success = await Task.Run(() =>
                _updater.UpdateSinglePackAsync(packType, enableEnhancements));

            if (success)
            {
                Trace.WriteLine($"{GetPackDisplayName(packType)} installed successfully");
            }
            else
            {
                Trace.WriteLine($"{GetPackDisplayName(packType)} installation failed");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error installing {GetPackDisplayName(packType)}: {ex.Message}");
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
        await _mainWindow.LocatePacksTask();

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
