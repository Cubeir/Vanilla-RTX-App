using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.PackUpdate;

public sealed partial class PackUpdateWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;
    private readonly Queue<(PackType pack, bool enhancements)> _installQueue = new();
    private bool _isInstalling = false;

    public PackUpdateWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
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

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _mainWindow.Closed -= MainWindow_Closed;
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
            WindowTitle.Text = $"Setup Vanilla RTX resource packs for {text}";

            SetupShadows();

            await InitializePackInformation();
            SetupButtonHandlers();
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
        string rtx = null, normals = null, opus = null;
        VersionSource source = VersionSource.Remote;

        try
        {
            var result = await _updater.GetRemoteVersionsAsync();
            rtx = result.rtx;
            normals = result.normals;
            opus = result.opus;
            source = result.source;
        }
        catch
        {
            // Fetch failed completely, versions remain null
        }

        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        // Update UI
        VanillaRTX_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTX_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTX_AvailableVersion.Text = GetAvailabilityText(rtx, vanillaRTXVersion, source);

        VanillaRTXNormals_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXNormals_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXNormals_AvailableVersion.Text = GetAvailabilityText(normals, vanillaRTXNormalsVersion, source);

        VanillaRTXOpus_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXOpus_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXOpus_AvailableVersion.Text = GetAvailabilityText(opus, vanillaRTXOpusVersion, source);

        await UpdateAllButtonStates(rtx, normals, opus);
    }

    private string GetAvailabilityText(string? availableVersion, string? installedVersion, VersionSource source)
    {
        if (string.IsNullOrEmpty(availableVersion))
        {
            return "Not available";
        }

        if (!string.IsNullOrEmpty(installedVersion) && availableVersion == installedVersion)
        {
            return "Up-to-date";
        }

        // Only show "(cached)" for zipball fallback, not for 5-min cached remote data
        return source == VersionSource.ZipballFallback ? $"{availableVersion} (cached)" : availableVersion;
    }

    private async Task UpdateAllButtonStates(string? rtxRemote, string? normalsRemote, string? opusRemote)
    {
        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        await UpdateSingleButtonState(VanillaRTX_InstallButton, PackType.VanillaRTX, vanillaRTXVersion, rtxRemote);
        await UpdateSingleButtonState(VanillaRTXNormals_InstallButton, PackType.VanillaRTXNormals, vanillaRTXNormalsVersion, normalsRemote);
        await UpdateSingleButtonState(VanillaRTXOpus_InstallButton, PackType.VanillaRTXOpus, vanillaRTXOpusVersion, opusRemote);
    }

    private async Task UpdateSingleButtonState(Button button, PackType packType, string? installedVersion, string? remoteVersion)
    {
        bool isInstalled = !string.IsNullOrEmpty(installedVersion);
        bool remoteAvailable = !string.IsNullOrEmpty(remoteVersion);
        bool packInCache = await _updater.DoesPackExistInCache(packType);

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

    // ======================= Button Handlers =======================

    private void SetupButtonHandlers()
    {
        VanillaRTX_InstallButton.Click += (s, e) =>
            QueueInstallation(PackType.VanillaRTX, VanillaRTX_EnhancementsToggle.IsOn);

        VanillaRTXNormals_InstallButton.Click += (s, e) =>
            QueueInstallation(PackType.VanillaRTXNormals, VanillaRTXNormals_EnhancementsToggle.IsOn);

        VanillaRTXOpus_InstallButton.Click += (s, e) =>
            QueueInstallation(PackType.VanillaRTXOpus, VanillaRTXOpus_EnhancementsToggle.IsOn);
    }

    private async void QueueInstallation(PackType packType, bool enableEnhancements)
    {
        SetPanelQueuedState(packType, true);

        _installQueue.Enqueue((packType, enableEnhancements));

        if (!_isInstalling)
        {
            await ProcessInstallQueue();
        }
    }

    private void SetPanelQueuedState(PackType packType, bool isQueued)
    {
        Button button;
        ToggleSwitch toggle;

        switch (packType)
        {
            case PackType.VanillaRTX:
                button = VanillaRTX_InstallButton;
                toggle = VanillaRTX_EnhancementsToggle;
                break;
            case PackType.VanillaRTXNormals:
                button = VanillaRTXNormals_InstallButton;
                toggle = VanillaRTXNormals_EnhancementsToggle;
                break;
            case PackType.VanillaRTXOpus:
                button = VanillaRTXOpus_InstallButton;
                toggle = VanillaRTXOpus_EnhancementsToggle;
                break;
            default:
                return;
        }

        if (isQueued)
        {
            button.Content = "In queue";
            button.IsEnabled = false;
            toggle.IsEnabled = false;
        }
    }

    private async Task ProcessInstallQueue()
    {
        _isInstalling = true;

        while (_installQueue.Count > 0)
        {
            var (pack, enhancements) = _installQueue.Dequeue();
            await InstallSinglePack(pack, enhancements);
        }

        _isInstalling = false;
    }

    private async Task InstallSinglePack(PackType packType, bool enableEnhancements)
    {
        SetPanelLoadingState(packType, true);

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

            await RefreshInstalledVersions();
            await FetchAndDisplayRemoteVersions();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Error installing {GetPackDisplayName(packType)}: {ex.Message}");
        }
        finally
        {
            SetPanelLoadingState(packType, false);
        }
    }

    private void SetPanelLoadingState(PackType packType, bool isLoading)
    {
        Button button;
        ProgressRing loadingRing;
        ToggleSwitch toggle;

        switch (packType)
        {
            case PackType.VanillaRTX:
                button = VanillaRTX_InstallButton;
                loadingRing = VanillaRTX_LoadingRing;
                toggle = VanillaRTX_EnhancementsToggle;
                break;
            case PackType.VanillaRTXNormals:
                button = VanillaRTXNormals_InstallButton;
                loadingRing = VanillaRTXNormals_LoadingRing;
                toggle = VanillaRTXNormals_EnhancementsToggle;
                break;
            case PackType.VanillaRTXOpus:
                button = VanillaRTXOpus_InstallButton;
                loadingRing = VanillaRTXOpus_LoadingRing;
                toggle = VanillaRTXOpus_EnhancementsToggle;
                break;
            default:
                return;
        }

        if (isLoading)
        {
            button.Visibility = Visibility.Collapsed;
            loadingRing.Visibility = Visibility.Visible;
            loadingRing.IsActive = true;
            toggle.IsEnabled = false;
        }
        else
        {
            loadingRing.IsActive = false;
            loadingRing.Visibility = Visibility.Collapsed;
            button.Visibility = Visibility.Visible;
            toggle.IsEnabled = true;
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
