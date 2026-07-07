using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using WinUIEx;
using static Vanilla_RTX_App.TunerVariables; // For Public Pack version variables, if null or empty = not installed

namespace Vanilla_RTX_App.PackUpdate;

public sealed partial class PackUpdateWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;
    private bool _isClosing;

    private readonly TimeSpan _fadeInDuration = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _fadeOutDuration = TimeSpan.FromMilliseconds(125);

    // How frequently differences of Installed version versus Cached version (versus offline or online) can invalidate the cache
    // Only once every few mins, so user can't get a way to spam github by changing pack versions constantly. while also allowing INSTALLED versions to invalidate
    private const string CACHE_INVALIDATION_COOLDOWN_KEY = "PackUpdater_CacheInvalidation_LastTimestamp";
    private const int CACHE_INVALIDATION_COOLDOWN_MINUTES = 5;
    // Source could either be the online zipball's manifests, or the version of files inside the offline/cached zipball
    // UI-displayed versions are from the INSTALLED version of the pack, this determines how frequently it gets to invalidate based solely on that.
    // The service code (PackUpdater.cs) only concerns itself with REMOTE VS CACHE and grapples to keep it updated there.
    // what we have here is just one extra check that closes all the gaps. In case that's on cooldown, this ends up
    // being another layer than can invalidate user's cache and lets them receive the latest version of the pack.

    private string? _currentInstallActionType;

    private DispatcherTimer? _installingAnimationTimer;
    private int _animationDots = 0;

    public PackUpdateWindow(MainWindow mainWindow)
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

        this.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "vrtx.update.ico"));

        this.Closed += PackUpdateWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += PackUpdateWindow_Loaded;
    }
    private async void PackUpdateWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= PackUpdateWindow_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarArea);

            var text = TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";
            WindowTitle.Text = $"Vanilla RTX resource packs for {text}";

            await InitializePackInformation();
            if (_isClosing) return;

            SetupButtonHandlers();
            CheckAndHandleOngoingInstallation();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdateWindow] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void PackUpdateWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= PackUpdateWindow_Loaded;

        StopInstallingAnimation();

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= PackUpdateWindow_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }
    private void PopulatePackUpdateAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.PackUpdateAnnouncements);
        if (items is null) return;
        foreach (var item in items)
            PackUpdateAnnouncementsPanel.Children.Add(new PsaCard(item) { CardFontSize = 13 });
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
        UpdateInstalledVersionDisplays();
        await FetchAndDisplayRemoteVersions();
        PopulatePackUpdateAnnouncements();
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
            // does the case where installed version is older than an offline cache really ever happen? NAH! offline cache would only be there if user has updated recently
            // But we're ready! lovely overengineered bullshit
            suffix = isUpToDate ? "(Up-to-date, from offline cache)" : "(from offline cache)";
        }
        else if (source == VersionSource.CachedRemote)
        {
            suffix = isUpToDate ? "(You're up-to-date)*" : "";
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
        bool anyNeedsUpdate = false;

        if (!string.IsNullOrEmpty(rtxRemote) && _updater.IsRemoteVersionNewerThanInstalled(rtxInstalled, rtxRemote))
            anyNeedsUpdate = true;

        if (!string.IsNullOrEmpty(normalsRemote) && _updater.IsRemoteVersionNewerThanInstalled(normalsInstalled, normalsRemote))
            anyNeedsUpdate = true;

        if (!string.IsNullOrEmpty(opusRemote) && _updater.IsRemoteVersionNewerThanInstalled(opusInstalled, opusRemote))
            anyNeedsUpdate = true;

        if (anyNeedsUpdate)
        {
            var settings = ApplicationData.Current.LocalSettings;
            bool canInvalidate = true;

            if (settings.Values.TryGetValue(CACHE_INVALIDATION_COOLDOWN_KEY, out var storedValue) && storedValue is long ticks)
            {
                var lastInvalidation = new DateTime(ticks, DateTimeKind.Utc);
                var elapsed = DateTime.UtcNow - lastInvalidation;
                var remainingMinutes = CACHE_INVALIDATION_COOLDOWN_MINUTES - (int)elapsed.TotalMinutes;

                if (remainingMinutes > 0)
                {
                    canInvalidate = false;
                    Trace.WriteLine($"Cache invalidation on cooldown - {remainingMinutes} minute(s) remaining");
                }
            }

            if (canInvalidate)
            {
                _updater.InvalidateCache();
                settings.Values[CACHE_INVALIDATION_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;
                Trace.WriteLine("Cache invalidated: installed version(s) outdated vs remote");
            }
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
            var (success, logs) = await Task.Run(() =>
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
