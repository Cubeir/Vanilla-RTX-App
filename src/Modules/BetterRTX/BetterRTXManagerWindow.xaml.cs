using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Windows.Storage;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.BetterRTX;

internal enum DownloadStatus
{
    NotDownloaded,
    Queued,
    Downloading,
    Downloaded
}

internal class DownloadQueueItem
{
    public DownloadQueueItem() { }

    public string? Uuid { get; set; }
    public string? Name { get; set; }
}


/// <summary>
/// The BetterRTX preset manager's window: chrome, the disclaimer dialog, the preset list it
/// draws, the refresh cooldown, drag/drop, and the download queue that feeds the list.
/// Everything below that - the cache, the bedrock.graphics API, reading presets off disk,
/// downloading, importing, hashing and the elevated install - lives in
/// <see cref="BetterRTXManager"/>. This holds one and renders it.
/// </summary>
public sealed partial class BetterRTXManagerWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _isClosing;

    private readonly BetterRTXManager _manager = new();

    private CancellationTokenSource? _scanCancellationTokenSource;

    // The download queue is the window's, not the manager's: every step of it exists to
    // move a preset's row from "Click to download" to "In queue" to installed, so it is
    // interleaved with redrawing the list all the way through.
    private Dictionary<string, DownloadStatus> _downloadStatuses;
    private readonly Queue<DownloadQueueItem> _downloadQueue;
    private bool _isProcessingQueue;
    private readonly CancellationTokenSource _closingCts = new();
    private readonly object _downloadStatusLock = new object();

    private const string REFRESH_COOLDOWN_KEY = "BetterRTXManager_RefreshCooldown_LastClickTimestamp";
    private const int REFRESH_COOLDOWN_SECONDS = 30;
    private DispatcherTimer? _cooldownTimer;

    /// <summary>
    /// True from the moment a preset install is started until it has finished and the list has
    /// been redrawn. Read and written only on the UI thread, and always cleared in a finally -
    /// see <see cref="PresetButton_Click"/>. Refresh honours it too, because a soft wipe would
    /// delete the folder an install is copying out of.
    /// </summary>
    private bool _applyInProgress;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    public BetterRTXManagerWindow()
    {
        this.InitializeComponent();
        _downloadStatuses = new Dictionary<string, DownloadStatus>();
        _downloadQueue = new Queue<DownloadQueueItem>();
        _isProcessingQueue = false;
        _manager.DownloadTrackingReset = ClearDownloadTracking;

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

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "vrtx.brtx.ico"));

        this.Closed += BetterRTXManagerWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += BetterRTXManagerWindow_Loaded;
    }
    private async void BetterRTXManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= BetterRTXManagerWindow_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarArea);

            if (Persistent.IsTargetingPreview)
            {
                StatusMessage = "BetterRTX Preset Manager does not support Minecraft Preview, the API only provides files intended for stable Minecraft releases that may not work on the latest Preview.";
                this.Close();
                return;
            }

            WindowTitle.Text = "BetterRTX Preset Manager - Minecraft Release";

            await InitializeAsync();
            if (_isClosing) return;

            InitializeRefreshButton();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTXManager] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void BetterRTXManagerWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= BetterRTXManagerWindow_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        _downloadQueue.Clear();
        lock (_downloadStatusLock) { _downloadStatuses.Clear(); }

        _closingCts.Cancel();

        _cooldownTimer?.Stop();
        _cooldownTimer = null;

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= BetterRTXManagerWindow_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }

    private async Task<bool> ShowDisclaimerDialogAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        if (localSettings.Values.ContainsKey(BetterRTXManager.BETTERRTX_DISCLAIMER_KEY))
            return true;

        var tcs = new TaskCompletionSource<bool>();

        var confirmButton = new Button
        {
            Content = "I understand the risks and wish to continue",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            Margin = new Thickness(0, 20, 0, 0),
            IsTextScaleFactorEnabled = false,
            Padding = new Thickness(16, 10, 16, 10),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24)
        };

        var closeButton = new Button
        {
            Content = "Dismiss",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0),
            IsTextScaleFactorEnabled = false,
            Padding = new Thickness(16, 8, 16, 8),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24)
        };

        var contentPanel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
            new TextBlock
                 {
                Text = "BetterRTX is an unofficial mod to Minecraft RTX's shader code. The files for this feature are provided by the following third-party: https://bedrock.graphics/api\n" +
                    "BetterRTX can also potentially break with Minecraft updates. Vanilla RTX App takes extensive measures to mitigate any issues that may arise, such as giving a way to quickly revert to your defaults." +
                    "\n\nPlease pay attention to the info panels to keep updated and help steer yourself away from potential issues.",
                TextWrapping = TextWrapping.Wrap,
                IsTextScaleFactorEnabled = false
                 },
            confirmButton,
            closeButton
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Third-Party API Usage Notice",
            Content = contentPanel,
            XamlRoot = this.Content.XamlRoot,
            IsTextScaleFactorEnabled = false,
            MinWidth = 0,
            MaxWidth = double.PositiveInfinity,
            Width = this.Bounds.Width * 0.55,
            RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
        };

        // Block all closes until one of our buttons sets the tcs
        dialog.Closing += (s, e) =>
        {
            if (!tcs.Task.IsCompleted)
                e.Cancel = true;
        };

        // If the window itself is closed while dialog is open, resolve gracefully
        void onWindowClosed(object s, WindowEventArgs e)
        {
            tcs.TrySetResult(false);
        }
        this.Closed += onWindowClosed;

        confirmButton.Click += (s, e) =>
        {
            localSettings.Values[BetterRTXManager.BETTERRTX_DISCLAIMER_KEY] = true;
            tcs.TrySetResult(true);
            dialog.Hide();
        };

        closeButton.Click += (s, e) =>
        {
            tcs.TrySetResult(false);
            dialog.Hide();
        };

        await dialog.ShowAsync();
        await tcs.Task; // ensure tcs is always resolved before we return

        this.Closed -= onWindowClosed; // clean up listener
        return tcs.Task.Result;
    }

    // ======================= Initialization =======================
    private async Task InitializeAsync()
    {
        try
        {
            var cachedPath = EnvironmentVariables.Persistent.MinecraftInstallPath;
            string? minecraftPath = null;

            // Validate cached path
            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath, Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"[BetterRTX] ✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                // Cache invalid - clear it and search
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine($"[BetterRTX] ⚠ Cache became invalid, clearing");
                    EnvironmentVariables.Persistent.MinecraftInstallPath = null;
                }

                // Show manual selection button
                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionButton.Visibility = Visibility.Visible;
                });

                // Start system-wide search
                Trace.WriteLine("[BetterRTX] Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    false,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[BetterRTX] System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // minecraftPath is guaranteed non-null here — both branches either return early or assign a value
            if (minecraftPath != null)
                await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
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

                    // Hide icon, show countdown text
                    RefreshIcon.Visibility = Visibility.Collapsed;
                    RefreshCountdownText.Visibility = Visibility.Visible;
                    RefreshCountdownText.Text = remainingSeconds.ToString();

                    return;
                }
            }

            // Cooldown expired or never set - enable button
            RefreshButton.IsEnabled = true;

            // Show icon, hide countdown text
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error updating refresh button state: {ex.Message}");

            // On error, default to enabled with icon visible
            RefreshButton.IsEnabled = true;
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // A refresh soft-wipes the cache, which would delete the preset folder a running
        // install is copying its .bin files out of. The 30s cooldown already stops this being
        // spammed; this stops it colliding with an install.
        if (_applyInProgress)
        {
            Trace.WriteLine("[BetterRTX] A preset is being applied - ignoring refresh");
            return;
        }

        try
        {
            Trace.WriteLine("[BetterRTX] === REFRESH BUTTON CLICKED ===");

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;
            UpdateRefreshButtonState();

            LoadingPanel.Visibility = Visibility.Visible;
            PresetSelectionPanel.Visibility = Visibility.Collapsed;
            await Task.Delay(100);

            await _manager.WipeNonDefaultPresetsCacheAsync();
            _manager.ForgetLoadedPresets();

            await _manager.LoadApiDataAsync();
            await _manager.LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;

            Trace.WriteLine($"[BetterRTX] ✓ Refresh complete — {BetterRTXManager.DEFAULT_PRESET_FOLDER_NAME} preserved");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Error during refresh: {ex.Message}");
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;
        }
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("[BetterRTX] Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(false, hWnd);

        if (path != null)
        {
            Trace.WriteLine($"[BetterRTX] ✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Trace.WriteLine("[BetterRTX] ✗ User cancelled or selected invalid path");
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        // Locate the materials folder, establish the cache, and deal with a game update
        // having happened since last time - all of which is the manager's business.
        switch (await _manager.TryAttachAsync(minecraftPath))
        {
            case BetterRTXManager.AttachFailure.MaterialsFolderMissing:
                StatusMessage = "Materials folder not found in Minecraft installation";
                this.Close();
                return;

            case BetterRTXManager.AttachFailure.CacheFolderUnavailable:
                StatusMessage = "Could not establish cache folder";
                this.Close();
                return;
        }

        // Load or fetch API data
        await _manager.LoadApiDataAsync();

        // Load local presets
        await _manager.LoadLocalPresetsAsync();

        // Display
        await DisplayPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;

        // Initialize PSAs
        if (EmptyStatePanel.Visibility != Visibility.Visible) // semantically, it means only alongside actual api preset lists, making sure psas dont clip into fallback background
        {
            PsaCard.Populate(BetterRTXAnnouncementsPanel, OnlineTextsContent.BetterRTXAnnouncements);
        }

        // Show disclaimer -- background work is done, but try to gate the UI
        try
        {
            var agreed = await ShowDisclaimerDialogAsync();
            if (!agreed)
            {
                StatusMessage = "Dismissed third-party API usage notice. You should understand the risks before you can use this feature.\n" +
                    $"If you wish to change up the look of RTX without BetterRTX, try out \"RTX LUT manager\" instead.";
                this.Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[BetterRTX]  Something went wrong while trying to show the BetterRTX disclaimer dialogue:\n" + ex.ToString());
        }
    }


    private async Task DisplayPresetsAsync()
    {
        try
        {
            // Both should be loaded by the time anything draws. A null here means a load was
            // skipped or failed - worth a line in the log, not worth refusing to draw.
            if (_manager.ApiPresets == null)
                Trace.WriteLine("[BetterRTX] ⚠ WARNING: ApiPresets is null in DisplayPresetsAsync!");

            if (_manager.LocalPresets == null)
                Trace.WriteLine("[BetterRTX] ⚠ WARNING: LocalPresets is null in DisplayPresetsAsync!");

            var apiPresets = _manager.ApiPresets ?? new List<ApiPresetData>();
            var localPresets = _manager.LocalPresets ?? new Dictionary<string, LocalPresetData>();

            PresetListContainer.Children.Clear();

            // Get current game hashes (ALL Core RTX files)
            var currentHashes = _manager.GetCurrentlyInstalledHashes();

            // Always add Default preset first
            var defaultPreset = _manager.CreateDefaultPreset();
            if (defaultPreset != null)
            {
                var defaultButton = CreatePresetButton(defaultPreset, currentHashes, true);
                PresetListContainer.Children.Add(defaultButton);
            }

            // Merge API presets with local presets
            var downloadedPresets = new List<DisplayPresetData>();
            var notDownloadedPresets = new List<DisplayPresetData>();
            var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First, process API presets
            foreach (var apiPreset in apiPresets)
            {
                if (string.IsNullOrEmpty(apiPreset.Uuid)) continue;    // skip malformed entries

                seenUuids.Add(apiPreset.Uuid);

                if (localPresets.TryGetValue(apiPreset.Uuid, out var localPreset))
                {
                    // Downloaded - add to downloaded list
                    downloadedPresets.Add(new DisplayPresetData
                    {
                        Uuid = apiPreset.Uuid,
                        Name = localPreset.Name,
                        IsDownloaded = true,
                        IsCustomImport = false,
                        Icon = localPreset.Icon,
                        PresetPath = localPreset.PresetPath,
                        BinFiles = localPreset.BinFiles,
                        FileHashes = localPreset.FileHashes
                    });
                }
                else
                {
                    // Not downloaded - add to not downloaded list
                    notDownloadedPresets.Add(new DisplayPresetData
                    {
                        Uuid = apiPreset.Uuid,
                        Name = apiPreset.Name,
                        IsDownloaded = false,
                        Icon = null,
                        PresetPath = null,
                        BinFiles = null,
                        FileHashes = null
                    });
                }
            }

            // Second, add any local presets that weren't in the API list
            foreach (var localPreset in localPresets.Values)
            {
                if (string.IsNullOrEmpty(localPreset.Uuid)) continue;   // skip malformed entries

                if (!seenUuids.Contains(localPreset.Uuid))
                {
                    downloadedPresets.Add(new DisplayPresetData
                    {
                        Uuid = localPreset.Uuid,
                        Name = localPreset.Name,
                        IsDownloaded = true,
                        IsCustomImport = true,
                        Icon = localPreset.Icon,
                        PresetPath = localPreset.PresetPath,
                        BinFiles = localPreset.BinFiles,
                        FileHashes = localPreset.FileHashes
                    });
                }
            }

            // Sort each list alphabetically
            downloadedPresets = SmartPresetSorter.SafeOrderByName(downloadedPresets, p => p.Name);
            notDownloadedPresets = SmartPresetSorter.SafeOrderByName(notDownloadedPresets, p => p.Name);

            // Display downloaded first, then not downloaded
            foreach (var preset in downloadedPresets)
            {
                var button = CreatePresetButton(preset, currentHashes, false);
                PresetListContainer.Children.Add(button);
            }

            foreach (var preset in notDownloadedPresets)
            {
                var button = CreatePresetButton(preset, currentHashes, false);
                PresetListContainer.Children.Add(button);
            }

            // Handle empty state
            if (downloadedPresets.Count == 0 && notDownloadedPresets.Count == 0 && defaultPreset == null)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;

                int apiCount = apiPresets.Count;
                int localCount = localPresets.Count;

                Trace.WriteLine($"[BetterRTX] 📊 Empty state triggered - API: {apiCount}, Local: {localCount}");

                if (apiCount == 0 && localCount == 0)
                {
                    // Check if we actually tried to load from API
                    bool apiCacheExists = File.Exists(_manager.ApiCachePath);

                    if (apiCacheExists)
                    {
                        // Cache exists but is empty/corrupt
                        EmptyStateText.Text = "No presets available. The preset list may be corrupted, try clicking the Refresh button in the top left corner.";
                        Trace.WriteLine("[BetterRTX] ⚠ Empty state: Cache exists but no presets loaded (possible corruption)");
                    }
                    else
                    {
                        // No cache, probably offline
                        EmptyStateText.Text = "No presets available. An internet connection is required to fetch BetterRTX preset information.\n" +
                            "You may still import and install custom presets (.rtpack) even without an internet connection.";
                        Trace.WriteLine("[BetterRTX] ⚠ Empty state: No cache and no presets (offline?)");
                    }
                }
                else
                {
                    // We have data but nothing to display (edge case)
                    EmptyStateText.Text = "No BetterRTX presets could be displayed. Try clicking the Refresh button in the top left corner.";
                    Trace.WriteLine($"[BetterRTX] ⚠ Empty state: API={apiCount}, Local={localCount} but no displayable presets (parsing issue?)");
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error displaying presets: {ex.Message}");
        }
    }

    private Button CreatePresetButton(object presetData, Dictionary<string, string> currentInstalledHashes, bool isDefault)
    {
        bool isDownloaded = true;
        bool isCurrent = false;
        bool isCustomImport = false;
        string name = "";
        string description = "";
        string uuid = "";
        BitmapImage? icon = null;

        if (presetData is LocalPresetData localPreset)
        {
            isDownloaded = true;
            name = localPreset.Name ?? "";
            icon = localPreset.Icon;
            uuid = localPreset.Uuid ?? "";

            // Compare ALL hashes to see if it is current
            if (localPreset.FileHashes != null && BetterRTXManager.AreHashesMatching(currentInstalledHashes, localPreset.FileHashes))
            {
                isCurrent = true;
            }
            description = isCurrent
                ? Helpers.SanitizePathForDisplay(localPreset.PresetPath ?? "")
                : isDefault
                    ? "Click to rollback" + (Helpers.RuntimeFlags.Set("Already_Informed_About_How_Default_RTX_Preset_Is_Made") ? ": this preset was automatically created from your latest game files upon your first attempt at installing a BetterRTX preset" : "")
                    : "Click to install";
        }
        else if (presetData is DisplayPresetData displayPreset)
        {
            isDownloaded = displayPreset.IsDownloaded;
            isCustomImport = displayPreset.IsCustomImport;
            name = displayPreset.Name ?? "";
            uuid = displayPreset.Uuid ?? "";
            icon = displayPreset.Icon;

            // Dynamic description based on download status
            if (isDownloaded)
            {
                if (displayPreset.FileHashes != null && BetterRTXManager.AreHashesMatching(currentInstalledHashes, displayPreset.FileHashes))
                {
                    isCurrent = true;
                    description = Helpers.SanitizePathForDisplay(displayPreset.PresetPath ?? "");
                }
                else
                {
                    isCurrent = false;
                    description = "Click to install";
                }
            }
            else
            {
                // Check download status with lock
                DownloadStatus status;
                lock (_downloadStatusLock)
                {
                    _downloadStatuses.TryGetValue(uuid, out status);
                }

                description = status switch
                {
                    DownloadStatus.Queued => "In queue",
                    DownloadStatus.Downloading => "Download in progress...",
                    _ => "Click to download"
                };
            }
        }

        if (isCurrent)
        {
            name += " (Currently Installed)";
        }

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 40, 0),
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 96,
            CornerRadius = new CornerRadius(5),
            Tag = presetData,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };
        if (isCurrent)
        {
            button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        }

        var buttonShadow = new ThemeShadow();
        button.Shadow = buttonShadow;
        button.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
            {
                buttonShadow.Receivers.Add(ShadowReceiverGrid);
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon
        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        if (icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = icon,
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            bool showDownloadGlyph = !isDefault && !isDownloaded;
            iconBorder.Child = new FontIcon
            {
                Glyph = showDownloadGlyph ? "\uE896" : "\uEABC",
                FontSize = 44, // scaled up to match larger icon container
                FontWeight = FontWeights.ExtraLight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false
            };
        }

        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // Info panel
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            Text = name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(nameText);
        infoPanel.Children.Add(descText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        // Download button or progress indicator
        if (!isDefault && !isDownloaded)
        {
            DownloadStatus status;
            lock (_downloadStatusLock)
            {
                status = _downloadStatuses.TryGetValue(uuid, out var s) ? s : DownloadStatus.NotDownloaded;
            }

            if (status == DownloadStatus.Downloading || status == DownloadStatus.Queued)
            {
                var progressRing = new ProgressRing
                {
                    Width = 50,
                    Height = 50,
                    IsActive = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                };

                Grid.SetColumn(progressRing, 4);
                grid.Children.Add(progressRing);
            }
        }


        // Delete button: only for custom-imported presets that aren't __DEFAULT
        // and aren't the one currently installed.
        if (!isDefault && !isCurrent && isCustomImport && presetData is DisplayPresetData deletablePreset)
        {
            var deleteButton = new Button
            {
                Width = 40,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(16, 0, 0, 0),
                Translation = new System.Numerics.Vector3(0, 0, 8),
                CornerRadius = new CornerRadius(6),
                IsTextScaleFactorEnabled = false,
                Tag = deletablePreset,
            };

            var deleteButtonShadow = new ThemeShadow();
            deleteButton.Shadow = deleteButtonShadow;
            deleteButton.Loaded += (s, e) =>
            {
                if (ShadowReceiverGrid != null)
                    deleteButtonShadow.Receivers.Add(ShadowReceiverGrid);
            };

            var deleteIcon = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 18,
                IsTextScaleFactorEnabled = false,
            };

            deleteButton.Content = deleteIcon;
            deleteButton.Click += DeletePresetButton_Click;

            Grid.SetColumn(deleteButton, 4);
            grid.Children.Add(deleteButton);
        }

        button.Content = grid;

        // Only attach if it isn't current
        if (!isCurrent)
        {
            button.Click += PresetButton_Click;
        }

        return button;
    }


    #region custom preset handlers
    private async void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hWnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hWnd);

            foreach (var ext in BetterRTXManager.SupportedCustomPresetExtensions)
                picker.FileTypeFilter.Add(ext);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            await ImportCustomPresetsAsync(files.Select(f => f.Path));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] Error browsing for preset file(s): {ex.Message}");
        }
    }
    private async void AddPresetButton_DragOver(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            bool hasSupportedFile = items.OfType<StorageFile>()
                .Any(f => BetterRTXManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

            if (hasSupportedFile)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Add as BetterRTX preset(s)";
                e.DragUIOverride.IsGlyphVisible = true;
            }
            else
            {
                // Something's being dragged, but none of it is a preset file we accept —
                // explicitly refuse so the OS shows the "not allowed" cursor instead of a copy cursor.
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] Error evaluating drag-over content: {ex.Message}");
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }
    // DragEnter dims the button and DragLeave/Drop undo it - but DragEnter has to await the
    // data view before it knows whether to dim at all, and a quick drag across the button
    // delivers DragLeave before that await returns. Left alone, the dim would then land
    // *after* the restore and stick. This token is "is the pointer still over the button?":
    // every enter takes a new one, every leave and drop invalidate it.
    private int _addPresetDragToken;

    private async void AddPresetButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var token = ++_addPresetDragToken;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            bool hasSupportedFile = items.OfType<StorageFile>()
                .Any(f => BetterRTXManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

            if (hasSupportedFile && token == _addPresetDragToken)
            {
                button.Opacity = 0.7;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] Error on drag enter: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
    private void AddPresetButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;

        // Undo exactly what DragEnter did. It used to ClearValue Background/BorderBrush/
        // BorderThickness instead, none of which DragEnter touches - so the dim was never
        // lifted and the button stayed at 0.7 for the rest of the window's life, while
        // clearing BorderThickness actively threw away the BorderThickness="0" set on it
        // in XAML and gave it the theme's 1px border.
        _addPresetDragToken++;
        button.Opacity = 1.0;
    }
    private async void AddPresetButton_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            // Undo DragEnter's dim regardless of what happens next (see DragLeave).
            _addPresetDragToken++;
            if (sender is Button button)
                button.Opacity = 1.0;

            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                Trace.WriteLine("[BetterRTX] [CustomImport] Drop event carried no storage items");
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var filePaths = items.OfType<StorageFile>()
                .Select(f => f.Path)
                .ToList();

            if (filePaths.Count == 0)
            {
                Trace.WriteLine("[BetterRTX] [CustomImport] Drop contained no usable files");
                return;
            }

            // ImportCustomPresetsAsync already filters down to supported extensions,
            // toggles LoadingPanel/PresetSelectionPanel, and refreshes the list when done.
            await ImportCustomPresetsAsync(filePaths);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] Error handling dropped file(s): {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
    private async void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DisplayPresetData preset }) return;

        if (string.IsNullOrEmpty(preset.PresetPath) || !Directory.Exists(preset.PresetPath))
        {
            Trace.WriteLine($"[BetterRTX] [Delete] ✗ Preset path missing or already gone: {preset.PresetPath}");
            return;
        }

        try
        {
            if (!await _manager.DeletePresetAsync(preset.PresetPath!, preset.Name))
                return;

            lock (_downloadStatusLock)
            {
                if (!string.IsNullOrEmpty(preset.Uuid))
                    _downloadStatuses.Remove(preset.Uuid);
            }

            await _manager.LoadLocalPresetsAsync();
            await DisplayPresetsAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [Delete] ✗ Error refreshing after delete of \"{preset.Name}\": {ex.Message}");
        }
    }
    #endregion


    private void EnqueueDownload(string uuid, string name)
    {
        // Check if already in queue or downloading
        lock (_downloadStatusLock)
        {
            if (_downloadStatuses.ContainsKey(uuid))
            {
                Trace.WriteLine($"[BetterRTX] ⚠ Already queued or downloading: {name}");
                return;
            }

            Trace.WriteLine($"[BetterRTX] ➕ Queued download: {name}");

            // Mark as queued
            _downloadStatuses[uuid] = DownloadStatus.Queued;
        }

        // Add to queue
        _downloadQueue.Enqueue(new DownloadQueueItem { Uuid = uuid, Name = name });

        // Refresh UI to show "Queued" status
        _ = this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

        // Start processing queue if not already running
        if (!_isProcessingQueue)
        {
            _ = ProcessDownloadQueueAsync();
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        if (_isProcessingQueue)
            return;

        _isProcessingQueue = true;
        Trace.WriteLine("[BetterRTX] ▶ Starting download queue processor");

        try
        {
            while (_downloadQueue.Count > 0)
            {
                var item = _downloadQueue.Dequeue();
                Trace.WriteLine($"[BetterRTX] 🔽 Processing download: {item.Name} (Queue: {_downloadQueue.Count} remaining)");

                // Update status to downloading
                lock (_downloadStatusLock)
                {
                    if (item.Uuid != null)
                        _downloadStatuses[item.Uuid] = DownloadStatus.Downloading;
                }
                await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

                // Wait a moment before starting (helps with slow API)
                await Task.Delay(2000);

                // Download
                var success = item.Uuid != null && await _manager.DownloadPresetAsync(item.Uuid, _closingCts.Token);

                if (success)
                {
                    Trace.WriteLine($"[BetterRTX] ✓ Download complete: {item.Name}");

                    // Mark as downloaded and remove from status tracking
                    lock (_downloadStatusLock)
                    {
                        if (item.Uuid != null)
                            _downloadStatuses.Remove(item.Uuid);
                    }

                    // Reload local presets and refresh UI
                    await _manager.LoadLocalPresetsAsync();
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }
                else
                {
                    Trace.WriteLine($"[BetterRTX] ✗ Download failed: {item.Name}");

                    // Remove from status (will show download button again)
                    lock (_downloadStatusLock)
                    {
                        if (item.Uuid != null)
                            _downloadStatuses.Remove(item.Uuid);
                    }
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }

                // Wait a bit between downloads to be nice to the api
                if (_downloadQueue.Count > 0)
                {
                    Trace.WriteLine("[BetterRTX] ⏱ Waiting 3 seconds before next download...");
                    await Task.Delay(1500);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Error in download queue processor: {ex.Message}");
        }
        finally
        {
            _isProcessingQueue = false;
            Trace.WriteLine("[BetterRTX] ⏹ Download queue processor stopped");
        }
    }

    /// <summary>
    /// Reloads and redisplays the local presets list - the same pair of calls
    /// ImportCustomPresetsAsync makes after its own import loop. Exposed so MainWindow can
    /// call it on an already-open manager window once a headless .rtpack file-activation
    /// import lands (see MainWindow.ImportBetterRTXPresetFilesAsync,
    /// ImportPresetFilesHeadlessAsync above) - otherwise this window's list would keep
    /// showing what was installed before that import until closed and reopened.
    /// </summary>
    internal async Task RefreshLocalPresetsAsync()
    {
        await _manager.LoadLocalPresetsAsync();
        await DisplayPresetsAsync();
    }

    /// <summary>
    /// Handed to <see cref="BetterRTXManager.DownloadTrackingReset"/>: a soft wipe has just
    /// deleted the folders anything queued was headed for.
    /// </summary>
    private void ClearDownloadTracking()
    {
        lock (_downloadStatusLock)
        {
            _downloadStatuses.Clear();
        }
        _downloadQueue.Clear();
    }

    // Bulk operation wrapper for custom preset imports
    private async Task ImportCustomPresetsAsync(IEnumerable<string> filePaths)
    {
        var candidates = filePaths
            .Where(p => BetterRTXManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] No supported preset files ({string.Join("/", BetterRTXManager.SupportedCustomPresetExtensions)}) in selection");
            return;
        }

        LoadingPanel.Visibility = Visibility.Visible;
        PresetSelectionPanel.Visibility = Visibility.Collapsed;

        int successCount = 0;
        foreach (var path in candidates)
        {
            var (ok, _) = await _manager.ImportCustomPresetAsync(path);
            if (ok) successCount++;
        }

        Trace.WriteLine($"[BetterRTX] [CustomImport] Imported {successCount}/{candidates.Count} preset(s) successfully");

        await _manager.LoadLocalPresetsAsync();
        await DisplayPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;
    }
    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag != null)
        {
            try
            {
                LocalPresetData? presetToApply = null;
                if (button.Tag is LocalPresetData localPreset)
                {
                    presetToApply = localPreset;
                }
                else if (button.Tag is DisplayPresetData displayPreset)
                {
                    if (!displayPreset.IsDownloaded)
                    {
                        // Trigger download by adding to queue
                        EnqueueDownload(displayPreset.Uuid ?? "", displayPreset.Name ?? "");
                        return;
                    }
                    else
                    {
                        presetToApply = new LocalPresetData
                        {
                            Uuid = displayPreset.Uuid,
                            Name = displayPreset.Name,
                            PresetPath = displayPreset.PresetPath,
                            Icon = displayPreset.Icon,
                            BinFiles = displayPreset.BinFiles,
                            FileHashes = displayPreset.FileHashes
                        };
                    }
                }
                if (presetToApply != null)
                {
                    // Applying a preset is an elevated file copy: it writes a batch script,
                    // raises a UAC prompt and waits, with the UI thread free for most of it.
                    // Without this a second click starts a second script and the user gets a
                    // queue of prompts (Helpers.ReplaceFilesWithElevation refuses that outright
                    // as a backstop). Ignoring the click rather than disabling the button is
                    // deliberate - there is no state here that can be left stuck.
                    if (_applyInProgress)
                    {
                        Trace.WriteLine("[BetterRTX] A preset is already being applied - ignoring this click");
                        return;
                    }

                    // Nothing between setting the flag and entering the try, so there is no
                    // statement that could throw its way past the finally and leave the window
                    // permanently refusing installs.
                    _applyInProgress = true;
                    try
                    {
                        SetPresetListBusy(true);

                        var success = await _manager.ApplyPresetAsync(presetToApply);
                        if (success)
                        {
                            OperationSuccessful = true;
                            StatusMessage = $"Installed {presetToApply.Name} successfully";
                            Trace.WriteLine(StatusMessage);
                            await DisplayPresetsAsync();
                        }
                    }
                    finally
                    {
                        SetPresetListBusy(false);
                        _applyInProgress = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error applying preset: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Greys the preset list and stops it taking clicks while an install runs - delete buttons
    /// included, since they live inside it and deleting the folder being copied from is the one
    /// way to break a running install.
    ///
    /// <para>Both properties are set on <c>PresetListContainer</c> itself rather than on the
    /// buttons, which <see cref="DisplayPresetsAsync"/> replaces wholesale. The container is
    /// declared in XAML and outlives every redraw, so the restoring call in the finally always
    /// lands on the same element the disabling call touched. <c>Panel</c> has no
    /// <c>IsEnabled</c> - that lives on <c>Control</c> - so this is the UIElement-level
    /// equivalent.</para>
    /// </summary>
    private void SetPresetListBusy(bool busy)
    {
        PresetListContainer.IsHitTestVisible = !busy;
        PresetListContainer.Opacity = busy ? 0.5 : 1.0;
    }


}


/// <summary>
/// Extension methods for DispatcherQueue to support async operations
/// </summary>
public static class DispatcherQueueExtensions
{
    /// <summary>
    /// Enqueues an async callback on the dispatcher queue and awaits its completion
    /// </summary>
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Func<Task> callback, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource<bool>();

        bool enqueued = dispatcher.TryEnqueue(priority, async () =>
        {
            try
            {
                await callback();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue operation on dispatcher"));
        }

        return tcs.Task;
    }

    /// <summary>
    /// Enqueues a synchronous callback on the dispatcher queue and awaits its completion
    /// </summary>
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Action callback, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource<bool>();

        bool enqueued = dispatcher.TryEnqueue(priority, () =>
        {
            try
            {
                callback();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue operation on dispatcher"));
        }

        return tcs.Task;
    }
}
