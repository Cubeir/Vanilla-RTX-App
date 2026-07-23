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
using Newtonsoft.Json.Linq;
using Vanilla_RTX_App.Core;
using Windows.Storage;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.Modules;

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

internal class ApiPresetData
{
    public ApiPresetData() { }

    public string? Uuid { get; set; }
    public string? Slug { get; set; }
    public string? Name { get; set; }
    public string? Stub { get; set; }
    public string? Tonemapping { get; set; }
    public string? Bloom { get; set; }
}

internal class LocalPresetData
{
    public LocalPresetData() { }

    public string? Uuid { get; set; }
    public string? Name { get; set; }
    public string? PresetPath { get; set; }
    public BitmapImage? Icon { get; set; }
    public List<string>? BinFiles { get; set; }
    public Dictionary<string, string>? FileHashes { get; set; }
}

internal class DisplayPresetData
{
    public DisplayPresetData() { }

    public string? Uuid { get; set; }
    public string? Name { get; set; }
    public bool IsDownloaded { get; set; }
    public bool IsCustomImport { get; set; }
    public BitmapImage? Icon { get; set; }
    public string? PresetPath { get; set; }
    public List<string>? BinFiles { get; set; }
    public Dictionary<string, string>? FileHashes { get; set; }
}



public sealed partial class BetterRTXManagerWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _isClosing;

    private string _gameMaterialsPath = string.Empty;
    private string _cacheFolder = string.Empty;
    private string _defaultFolder = string.Empty;
    private string _apiCachePath = string.Empty;
    private CancellationTokenSource? _scanCancellationTokenSource;
    private List<ApiPresetData>? _apiPresets;
    private Dictionary<string, LocalPresetData>? _localPresets;
    private Dictionary<string, DownloadStatus> _downloadStatuses;
    private readonly Queue<DownloadQueueItem> _downloadQueue;
    private bool _isProcessingQueue;
    private readonly CancellationTokenSource _closingCts = new();
    private readonly object _downloadStatusLock = new object();

    private const string REFRESH_COOLDOWN_KEY = "BetterRTXManager_RefreshCooldown_LastClickTimestamp";
    private const int REFRESH_COOLDOWN_SECONDS = 30;
    private DispatcherTimer? _cooldownTimer;

    private const string API_LAST_FETCH_KEY = "BetterRTXManager_ApiLastFetchTimestamp";
    private const int API_REFETCH_INTERVAL_HOURS = 1;
    private string? _cachedApiHash = null;

    public const string BETTERRTX_DISCLAIMER_KEY = $"BetterRTXDisclaimerAgreed_Key";

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    internal static readonly string[] CoreRTXFiles =
    [
       "RTXPostFX.Bloom.material.bin",
       "RTXPostFX.material.bin",
       "RTXPostFX.Tonemapping.material.bin",
       "RTXStub.material.bin"
    ];
    internal static readonly string[] SupportedCustomPresetExtensions = [".rtpack"];

    internal const string DEFAULT_PRESET_FOLDER_NAME = "__DEFAULT";

    public BetterRTXManagerWindow()
    {
        this.InitializeComponent();
        _downloadStatuses = new Dictionary<string, DownloadStatus>();
        _downloadQueue = new Queue<DownloadQueueItem>();
        _isProcessingQueue = false;

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
    private void PopulateBetterRTXAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.BetterRTXAnnouncements);
        if (items is null) return;
        foreach (var item in items)
            BetterRTXAnnouncementsPanel.Children.Add(new PsaCard(item));
    }
    private async Task<bool> ShowDisclaimerDialogAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        if (localSettings.Values.ContainsKey(BETTERRTX_DISCLAIMER_KEY))
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
            localSettings.Values[BETTERRTX_DISCLAIMER_KEY] = true;
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
            var cachedPath = TunerVariables.Persistent.MinecraftInstallPath;
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
                    TunerVariables.Persistent.MinecraftInstallPath = null;
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
        try
        {
            Trace.WriteLine("[BetterRTX] === REFRESH BUTTON CLICKED ===");

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;
            UpdateRefreshButtonState();

            LoadingPanel.Visibility = Visibility.Visible;
            PresetSelectionPanel.Visibility = Visibility.Collapsed;
            await Task.Delay(100);

            await WipeNonDefaultPresetsCacheAsync();

            _apiPresets = null;
            _localPresets = null;

            await LoadApiDataAsync();
            await LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;

            Trace.WriteLine($"[BetterRTX] ✓ Refresh complete — {DEFAULT_PRESET_FOLDER_NAME} preserved");
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
        _gameMaterialsPath = Path.Combine(minecraftPath, "data", "renderer", "materials");

        // Verify materials folder exists
        if (!Directory.Exists(_gameMaterialsPath))
        {
            StatusMessage = "Materials folder not found in Minecraft installation";
            this.Close();
            return;
        }

        // Establish cache folder
        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
        {
            StatusMessage = "Could not establish cache folder";
            this.Close();
            return;
        }
        _cacheFolder = cacheFolder;

        _defaultFolder = Path.Combine(_cacheFolder, DEFAULT_PRESET_FOLDER_NAME);
        _apiCachePath = Path.Combine(_cacheFolder, "betterrtx_api_cache.json");

        // CRITICAL: Check if game version changed
        bool versionChanged = await GameVersionDetector.HasGameVersionChanged(minecraftPath);

        if (versionChanged)
        {
            Trace.WriteLine("[BetterRTX] ⚠🔥 GAME VERSION CHANGED - WIPING CACHE 🔥⚠");
            WipeEntireCache();
            // Recreate cache folder structure
            Directory.CreateDirectory(_cacheFolder);
            Directory.CreateDirectory(_defaultFolder);
        }
        else
        {
            Directory.CreateDirectory(_defaultFolder);
            // Only check API staleness when the game itself hasn't changed, cuz it has already nuked everything including the API cache.
            await CheckApiStalenessOnStartupAsync();
        }

        // Load or fetch API data
        await LoadApiDataAsync();

        // Load local presets
        await LoadLocalPresetsAsync();

        // Display
        await DisplayPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;

        // Initialize PSAs
        PopulateBetterRTXAnnouncements();

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

    private string? EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "RTX_Cache");

            Trace.WriteLine($"[BetterRTX] Cache location: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"[BetterRTX] ✓ Cache established");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Failed to create cache: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Called once per window open. If an hour has elapsed since the last API fetch,
    /// fetches fresh JSON and compares its hash to what we last cached.
    ///  Same hash? nothing, LoadApiDataAsync will load the cache normally
    ///  Different hash? soft wipe so stale downloaded presets are cleared
    ///  Fetch failed? defer silently to next launch (do not wipe)
    ///  Undetermined? soft wipe (safe default)
    /// </summary>
    private async Task CheckApiStalenessOnStartupAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;

            // Check if an hour has elapsed since last fetch
            if (settings.Values.TryGetValue(API_LAST_FETCH_KEY, out var raw) && raw is long ticks)
            {
                var lastFetch = new DateTime(ticks, DateTimeKind.Utc);
                if ((DateTime.UtcNow - lastFetch).TotalHours < API_REFETCH_INTERVAL_HOURS)
                {
                    Trace.WriteLine("[BetterRTX] [StalenessCheck] Within hour window — skipping API check");
                    return;
                }
            }

            Trace.WriteLine("[BetterRTX] [StalenessCheck] Hour elapsed — fetching latest API JSON for comparison...");

            var freshJson = await FetchApiDataAsync();

            // Fetch failed entirely — defer, do NOT wipe
            if (string.IsNullOrWhiteSpace(freshJson))
            {
                Trace.WriteLine("[BetterRTX] [StalenessCheck] API unreachable — deferring to next launch");
                return;
            }

            // Stamp the successful fetch time now
            settings.Values[API_LAST_FETCH_KEY] = DateTime.UtcNow.Ticks;

            // Compute hash of fresh response
            string freshHash;
            using (var sha256 = SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(freshJson);
                freshHash = BitConverter.ToString(sha256.ComputeHash(bytes))
                                        .Replace("-", "")
                                        .ToLowerInvariant();
            }

            Trace.WriteLine($"[BetterRTX] [StalenessCheck] Fresh hash : {freshHash[..16]}...");
            Trace.WriteLine($"[BetterRTX] [StalenessCheck] Cached hash: {(_cachedApiHash != null ? _cachedApiHash[..16] + "..." : "none yet")}");

            // Load existing cache hash if we don't have it in memory yet
            // (e.g. first run of this method before LoadApiDataAsync has set _cachedApiHash)
            if (_cachedApiHash == null && File.Exists(_apiCachePath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(_apiCachePath);
                    using var sha256 = SHA256.Create();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(existingJson);
                    _cachedApiHash = BitConverter.ToString(sha256.ComputeHash(bytes))
                                                 .Replace("-", "")
                                                 .ToLowerInvariant();
                    Trace.WriteLine($"[BetterRTX] [StalenessCheck] Loaded cache hash from disk: {_cachedApiHash[..16]}...");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] [StalenessCheck] Couldn't read existing cache for comparison: {ex.Message}");
                    // _cachedApiHash stays null — falls through to undetermined → wipe
                }
            }

            if (_cachedApiHash == null)
            {
                // No prior cache to compare against — undetermined, wipe to be safe
                Trace.WriteLine("[BetterRTX] [StalenessCheck] No prior cache hash — undetermined, soft wipe (safe default)");
                await WipeNonDefaultPresetsCacheAsync();
                return;
            }

            if (freshHash == _cachedApiHash)
            {
                Trace.WriteLine("[BetterRTX] [StalenessCheck] ✓ API unchanged — no wipe needed");
                return;
            }

            // Content changed — wipe downloaded presets so stale files don't linger
            Trace.WriteLine("[BetterRTX] [StalenessCheck] ⚠ API changed — soft wiping downloaded presets");
            await WipeNonDefaultPresetsCacheAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [StalenessCheck] ✗ Unexpected error: {ex.Message} — soft wipe (safe default)");
            try { await WipeNonDefaultPresetsCacheAsync(); } catch { }
        }
    }

    /// <summary>
    /// Hard wipe, like soft wipe, but deletes Default preset too, the nuclear option
    /// </summary>
    private void WipeEntireCache()
    {
        try
        {
            if (Directory.Exists(_cacheFolder))
            {
                Trace.WriteLine($"[BetterRTX] Deleting entirety of cache folder: {_cacheFolder}");
                Directory.Delete(_cacheFolder, true);
                Trace.WriteLine("[BetterRTX] ✓ Cache wiped successfully");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error wiping cache: {ex.Message}");
        }
    }
    /// <summary>
    /// Soft wipe: deletes all downloaded and custom imported preset folders and the API cache JSON.
    /// __DEFAULT is intentionally preserved — only a game version change warrants clearing that.
    /// </summary>
    private async Task WipeNonDefaultPresetsCacheAsync()
    {
        Trace.WriteLine("[BetterRTX] [SoftWipe] Starting soft cache wipe...");

        int deletedCount = 0;

        if (Directory.Exists(_cacheFolder))
        {
            var allFolders = Directory.GetDirectories(_cacheFolder)
                .Where(d => !Path.GetFileName(d).Equals(DEFAULT_PRESET_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in allFolders)
            {
                try
                {
                    Directory.Delete(folder, true);
                    deletedCount++;
                    Trace.WriteLine($"[BetterRTX] [SoftWipe] Deleted: {Path.GetFileName(folder)}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] [SoftWipe] Error deleting {Path.GetFileName(folder)}: {ex.Message}");
                }
            }
        }

        Trace.WriteLine($"[BetterRTX] [SoftWipe] Deleted {deletedCount} preset folder(s) (downloaded + custom-imported alike)");

        // Delete API cache JSON itself
        if (File.Exists(_apiCachePath))
        {
            File.Delete(_apiCachePath);
            Trace.WriteLine("[BetterRTX] [SoftWipe] API cache deleted");
        }

        // Clear in-memory tracking
        lock (_downloadStatusLock)
        {
            _downloadStatuses.Clear();
        }
        _downloadQueue.Clear();
        _cachedApiHash = null;

        Trace.WriteLine($"[BetterRTX] [SoftWipe] ✓ Done — {DEFAULT_PRESET_FOLDER_NAME} preserved");
    }

    private async Task LoadApiDataAsync()
    {
        try
        {
            string? jsonData = null;
            bool loadedFromCache = false;

            // Check if cache exists and is valid
            if (File.Exists(_apiCachePath))
            {
                Trace.WriteLine("[BetterRTX] ✓ Loading API data from cache...");
                try
                {
                    jsonData = await File.ReadAllTextAsync(_apiCachePath);

                    if (!string.IsNullOrWhiteSpace(jsonData))
                    {
                        // Parse ONCE and validate
                        var parsedPresets = ParseApiData(jsonData);
                        if (parsedPresets != null && parsedPresets.Count > 0)
                        {
                            _apiPresets = parsedPresets;
                            loadedFromCache = true;
                            Trace.WriteLine($"[BetterRTX] ✓ Cache is valid with {_apiPresets.Count} presets");
                        }
                        else
                        {
                            Trace.WriteLine("[BetterRTX] ⚠ Cache exists but is empty or invalid - will fetch fresh data");
                            jsonData = null;
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[BetterRTX] ⚠ Cache file is empty - will fetch fresh data");
                        jsonData = null;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BetterRTX] ⚠ Error reading/parsing cache: {ex.Message} - will fetch fresh data");
                    jsonData = null;
                    loadedFromCache = false;
                }
            }

            // If no valid cache, fetch from API
            if (!loadedFromCache)
            {
                Trace.WriteLine("[BetterRTX] Fetching API data from server...");
                jsonData = await FetchApiDataAsync();

                if (jsonData != null && !string.IsNullOrWhiteSpace(jsonData))
                {
                    // Parse ONCE and validate
                    var parsedPresets = ParseApiData(jsonData);
                    if (parsedPresets != null && parsedPresets.Count > 0)
                    {
                        _apiPresets = parsedPresets;

                        // Save to cache
                        try
                        {
                            await File.WriteAllTextAsync(_apiCachePath, jsonData);
                            Trace.WriteLine("[BetterRTX] ✓ API data cached successfully");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[BetterRTX] ⚠ Failed to save cache: {ex.Message}");
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[BetterRTX] ⚠ Fetched data is empty or invalid - not caching");
                        _apiPresets = new List<ApiPresetData>();
                    }
                }
                else
                {
                    Trace.WriteLine("[BetterRTX] ⚠ Failed to fetch API data and no valid cache available");
                    _apiPresets = new List<ApiPresetData>();
                }
            }

            Trace.WriteLine($"[BetterRTX] ✓ Loaded {_apiPresets?.Count ?? 0} presets total");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error in LoadApiDataAsync: {ex.Message}");
            _apiPresets = new List<ApiPresetData>();
        }
    }

    private async Task<string?> FetchApiDataAsync()
    {
        try
        {
            var client = Helpers.SharedHttpClient;
            var response = await client.GetAsync("https://bedrock.graphics/api");

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[BetterRTX] ⚠ API returned status code: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                Trace.WriteLine("[BetterRTX] ⚠ API returned empty response");
                return null;
            }

            return content;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Trace.WriteLine("[BetterRTX] ⚠ API request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ⚠ Error fetching API data: {ex.Message}");
            return null;
        }
    }

    private List<ApiPresetData> ParseApiData(string jsonData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Cannot parse null or empty JSON data");
                return new List<ApiPresetData>();
            }

            var presets = new List<ApiPresetData>();
            var jsonArray = JArray.Parse(jsonData);

            foreach (var item in jsonArray)
            {
                var preset = new ApiPresetData
                {
                    Uuid = item["uuid"]?.Value<string>(),
                    Slug = item["slug"]?.Value<string>(),
                    Name = item["name"]?.Value<string>(),
                    Stub = item["stub"]?.Value<string>(),
                    Tonemapping = item["tonemapping"]?.Value<string>(),
                    Bloom = item["bloom"]?.Value<string>()
                };
                presets.Add(preset);
            }

            return presets;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error parsing API data: {ex.Message}");
            return new List<ApiPresetData>();
        }
    }

    private async Task LoadLocalPresetsAsync()
    {
        _localPresets = new Dictionary<string, LocalPresetData>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(_cacheFolder))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Cache folder doesn't exist - no local presets");
                return;
            }

            // Get all folders except __DEFAULT
            var presetFolders = Directory.GetDirectories(_cacheFolder)
                .Where(d => !Path.GetFileName(d).Equals(DEFAULT_PRESET_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in presetFolders)
            {
                var localPreset = await ParseLocalPresetAsync(folder);
                if (localPreset != null && !string.IsNullOrEmpty(localPreset.Uuid))
                {
                    _localPresets[localPreset.Uuid] = localPreset;
                    Trace.WriteLine($"[BetterRTX] ✓ Loaded local preset: {localPreset.Name} (UUID: {localPreset.Uuid})");
                }
            }

            Trace.WriteLine($"[BetterRTX] ✓ Loaded {_localPresets.Count} local presets");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error loading local presets: {ex.Message}");
        }
    }

    private async Task<LocalPresetData?> ParseLocalPresetAsync(string presetFolder)
    {
        try
        {
            var manifestFiles = Directory.GetFiles(presetFolder, "manifest.json", SearchOption.AllDirectories);

            if (manifestFiles.Length == 0)
            {
                Trace.WriteLine($"[BetterRTX] No manifest found in: {presetFolder}");
                return null;
            }

            var manifestPath = manifestFiles[0];
            var manifestDir = Path.GetDirectoryName(manifestPath);

            var json = await File.ReadAllTextAsync(manifestPath);
            var root = JObject.Parse(json);

            string? uuid = null;
            string name = Path.GetFileName(presetFolder);

            // Try to get header.uuid
            var header = root["header"];
            if (header != null)
            {
                var uuidToken = header["uuid"];
                if (uuidToken != null)
                {
                    uuid = uuidToken.Value<string>();
                }

                var nameToken = header["name"];
                if (nameToken != null)
                {
                    var parsedName = nameToken.Value<string>();
                    if (!string.IsNullOrWhiteSpace(parsedName))
                        name = parsedName;
                }
            }

            if (string.IsNullOrEmpty(uuid))
            {
                Trace.WriteLine($"[BetterRTX] ⚠ No UUID in manifest: {presetFolder}");
                return null;
            }

            // manifestDir may be null if manifestPath has no directory component (extremely unlikely for a file found via GetFiles,
            // but we guard anyway by falling back to presetFolder)
            var icon = await LoadIconAsync(manifestDir ?? presetFolder) ?? await LoadIconAsync(presetFolder);
            var binFiles = Directory.GetFiles(presetFolder, "*.bin", SearchOption.AllDirectories).ToList();

            // Compute hashes for ALL Core RTX files
            var presetHashes = GetPresetHashes(binFiles);

            return new LocalPresetData
            {
                Uuid = uuid,
                Name = name,
                PresetPath = presetFolder,
                Icon = icon,
                BinFiles = binFiles,
                FileHashes = presetHashes
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error parsing local preset {presetFolder}: {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage?> LoadIconAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        var iconFiles = Directory.GetFiles(directory, "pack_icon.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga";
            })
            .ToArray();

        foreach (var iconPath in iconFiles)
        {
            try
            {
                var bitmap = new BitmapImage();

                using (var fileStream = File.OpenRead(iconPath))
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        await fileStream.CopyToAsync(memoryStream);
                        memoryStream.Position = 0;

                        var randomAccessStream = memoryStream.AsRandomAccessStream();
                        await bitmap.SetSourceAsync(randomAccessStream);
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }

    private async Task DisplayPresetsAsync()
    {
        try
        {
            if (_apiPresets == null)
            {
                Trace.WriteLine("[BetterRTX] ⚠ WARNING: _apiPresets is null in DisplayPresetsAsync!");
                _apiPresets = new List<ApiPresetData>();
            }

            if (_localPresets == null)
            {
                Trace.WriteLine("[BetterRTX] ⚠ WARNING: _localPresets is null in DisplayPresetsAsync!");
                _localPresets = new Dictionary<string, LocalPresetData>();
            }

            PresetListContainer.Children.Clear();

            // Get current game hashes (ALL Core RTX files)
            var currentHashes = GetCurrentlyInstalledHashes();

            // Always add Default preset first
            var defaultPreset = CreateDefaultPreset();
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
            if (_apiPresets != null)
            {
                foreach (var apiPreset in _apiPresets)
                {
                    if (string.IsNullOrEmpty(apiPreset.Uuid)) continue;    // skip malformed entries

                    seenUuids.Add(apiPreset.Uuid);

                    if (_localPresets != null && _localPresets.TryGetValue(apiPreset.Uuid, out var localPreset))
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
            }

            // Second, add any local presets that weren't in the API list
            if (_localPresets != null)
            {
                foreach (var localPreset in _localPresets.Values)
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

            // Always last in the list, regardless of what else is (or isn't) displayed
            var addPresetButton = CreateAddPresetButton();
            PresetListContainer.Children.Add(addPresetButton);

            // Handle empty state
            if (downloadedPresets.Count == 0 && notDownloadedPresets.Count == 0 && defaultPreset == null)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;

                int apiCount = _apiPresets?.Count ?? 0;
                int localCount = _localPresets?.Count ?? 0;

                Trace.WriteLine($"[BetterRTX] 📊 Empty state triggered - API: {apiCount}, Local: {localCount}");

                if (apiCount == 0 && localCount == 0)
                {
                    // Check if we actually tried to load from API
                    bool apiCacheExists = File.Exists(_apiCachePath);

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

    private LocalPresetData? CreateDefaultPreset()
    {
        if (!Directory.Exists(_defaultFolder))
            return null;

        var binFiles = Directory.GetFiles(_defaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();

        if (binFiles.Count == 0)
            return null;

        // Compute hashes for ALL Core RTX files
        var presetHashes = GetPresetHashes(binFiles);

        return new LocalPresetData
        {
            Uuid = DEFAULT_PRESET_FOLDER_NAME,
            Name = "Default RTX",
            PresetPath = _defaultFolder,
            Icon = null,
            BinFiles = binFiles,
            FileHashes = presetHashes
        };
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
            if (localPreset.FileHashes != null && AreHashesMatching(currentInstalledHashes, localPreset.FileHashes))
            {
                isCurrent = true;
            }
            description = isCurrent
                ? Helpers.SanitizePathForDisplay(localPreset.PresetPath ?? "")
                : isDefault
                    ? "Click to rollback" + (Helpers.RuntimeFlags.Set("Already_Informed_About_How_Default_RTX_Preset_Is_Made") ? ": this preset was automatically created from your latest game files upon your first attempt at installing a preset" : "")
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
                if (displayPreset.FileHashes != null && AreHashesMatching(currentInstalledHashes, displayPreset.FileHashes))
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
    private Button CreateAddPresetButton()
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 40, 0),
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 96,
            CornerRadius = new CornerRadius(5),
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            AllowDrop = true
        };

        button.DragOver += AddPresetButton_DragOver;
        button.Drop += AddPresetButton_Drop;
        button.DragEnter += AddPresetButton_DragEnter;
        button.DragLeave += AddPresetButton_DragLeave;

        var buttonShadow = new ThemeShadow();
        button.Shadow = buttonShadow;
        button.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                buttonShadow.Receivers.Add(ShadowReceiverGrid);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        iconBorder.Child = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 44,
            FontWeight = FontWeights.ExtraLight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleText = new TextBlock
        {
            Text = "Add customized preset (.rtpack)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = "Drag and drop or browse for the .rtpack preset files exported from the BetterRTX website",
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(titleText);
        infoPanel.Children.Add(descText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        var hyperlinkButton = new HyperlinkButton
        {
            Content = "Create your own BetterRTX preset",
            NavigateUri = new Uri("https://bedrock.graphics/creator"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
            FontSize = 14,
            Padding = new Thickness(16, 8, 16, 8),
            Translation = new System.Numerics.Vector3(0, 0, 8),
            IsTextScaleFactorEnabled = false,
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            CornerRadius = new CornerRadius(6)
        };

        var hyperLinkShadow = new ThemeShadow();
        hyperlinkButton.Shadow = hyperLinkShadow;
        hyperlinkButton.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                hyperLinkShadow.Receivers.Add(ShadowReceiverGrid);
        };

        Grid.SetColumn(hyperlinkButton, 4);
        grid.Children.Add(hyperlinkButton);

        button.Content = grid;
        button.Click += AddPresetButton_Click;

        return button;
    }
    private async void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hWnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hWnd);

            foreach (var ext in SupportedCustomPresetExtensions)
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
                .Any(f => SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

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
    private async void AddPresetButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            bool hasSupportedFile = items.OfType<StorageFile>()
                .Any(f => SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

            if (hasSupportedFile)
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

        // Revert to whatever the button's style would normally give it
        button.ClearValue(Button.BackgroundProperty);
        button.ClearValue(Button.BorderBrushProperty);
        button.ClearValue(Button.BorderThicknessProperty);
    }
    private async void AddPresetButton_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            // Reset the highlight regardless of what happens next
            if (sender is Button button)
            {
                button.ClearValue(Button.BackgroundProperty);
                button.ClearValue(Button.BorderBrushProperty);
                button.ClearValue(Button.BorderThicknessProperty);
            }

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
            Trace.WriteLine($"[BetterRTX] [Delete] Deleting custom preset \"{preset.Name}\" at: {preset.PresetPath}");
            await Task.Run(() => Directory.Delete(preset.PresetPath!, true));

            lock (_downloadStatusLock)
            {
                if (!string.IsNullOrEmpty(preset.Uuid))
                    _downloadStatuses.Remove(preset.Uuid);
            }

            await LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            Trace.WriteLine($"[BetterRTX] [Delete] ✓ Deleted \"{preset.Name}\"");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [Delete] ✗ Error deleting \"{preset.Name}\": {ex.Message}");
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
                var success = item.Uuid != null && await DownloadPresetAsync(item.Uuid);

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
                    await LoadLocalPresetsAsync();
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

    private async Task<bool> DownloadPresetAsync(string uuid)
    {
        try
        {
            var url = $"https://bedrock.graphics/pack/{uuid}/release";
            Trace.WriteLine($"[BetterRTX] Downloading from: {url}");

            var (success, downloadedPath) = await Helpers.Download(url, cancellationToken: _closingCts.Token, timeout: TimeSpan.FromMinutes(3));

            if (!success || string.IsNullOrEmpty(downloadedPath))
            {
                Trace.WriteLine($"[BetterRTX] ✗ Download failed");
                return false;
            }

            Trace.WriteLine($"[BetterRTX] ✓ Downloaded to: {downloadedPath}");

            // Extract to RTX_Cache
            var sanitizedName = SanitizePresetName(uuid);
            var destinationFolder = Path.Combine(_cacheFolder, sanitizedName);

            // Delete existing folder if present
            if (Directory.Exists(destinationFolder))
            {
                Directory.Delete(destinationFolder, true);
            }

            Directory.CreateDirectory(destinationFolder);

            // Extract the archive
            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(downloadedPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            var destPath = Path.Combine(destinationFolder, entry.FullName);
                            var destDir = Path.GetDirectoryName(destPath);

                            if (!string.IsNullOrEmpty(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }

                            entry.ExtractToFile(destPath, true);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[BetterRTX] Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            Trace.WriteLine($"[BetterRTX] ✓ Extracted to: {destinationFolder}");

            // Clean up downloaded file
            try
            {
                File.Delete(downloadedPath);
            }
            catch { }

            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ Error downloading {uuid}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ImportCustomPresetAsync(string archivePath)
    {
        string? stagingFolder = null;
        try
        {
            if (!File.Exists(archivePath))
            {
                Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ File not found: {archivePath}");
                return false;
            }

            Trace.WriteLine($"[BetterRTX] [CustomImport] Importing: {archivePath}");

            stagingFolder = Path.Combine(_cacheFolder, $"__staging_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingFolder);

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        var destPath = Path.Combine(stagingFolder, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);

                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        entry.ExtractToFile(destPath, true);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[BetterRTX] [CustomImport] Error extracting {entry.FullName}: {ex.Message}");
                    }
                }
            });

            var parsed = await ParseLocalPresetAsync(stagingFolder);
            if (parsed == null || string.IsNullOrEmpty(parsed.Uuid))
            {
                Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ Not a valid BetterRTX preset: {archivePath}");
                return false;
            }

            // Same convention downloaded presets use: folder named after the pack's
            // own UUID, so re-importing the same file just replaces it in place.
            var destinationFolder = Path.Combine(_cacheFolder, SanitizePresetName(parsed.Uuid));

            if (Directory.Exists(destinationFolder))
                Directory.Delete(destinationFolder, true);

            Directory.Move(stagingFolder, destinationFolder);
            stagingFolder = null; // moved successfully, nothing left to clean up

            Trace.WriteLine($"[BetterRTX] [CustomImport] ✓ Imported \"{parsed.Name}\" to: {destinationFolder}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] ✗ Error importing {archivePath}: {ex.Message}");
            return false;
        }
        finally
        {
            if (stagingFolder != null && Directory.Exists(stagingFolder))
            {
                try { Directory.Delete(stagingFolder, true); } catch { }
            }
        }
    }


    // Bulk operation wrapper for custom preset imports
    private async Task ImportCustomPresetsAsync(IEnumerable<string> filePaths)
    {
        var candidates = filePaths
            .Where(p => SupportedCustomPresetExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[BetterRTX] [CustomImport] No supported preset files ({string.Join("/", SupportedCustomPresetExtensions)}) in selection");
            return;
        }

        LoadingPanel.Visibility = Visibility.Visible;
        PresetSelectionPanel.Visibility = Visibility.Collapsed;

        int successCount = 0;
        foreach (var path in candidates)
        {
            if (await ImportCustomPresetAsync(path))
                successCount++;
        }

        Trace.WriteLine($"[BetterRTX] [CustomImport] Imported {successCount}/{candidates.Count} preset(s) successfully");

        await LoadLocalPresetsAsync();
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
                    var success = await ApplyPresetAsync(presetToApply);
                    if (success)
                    {
                        OperationSuccessful = true;
                        StatusMessage = $"Installed {presetToApply.Name} successfully";
                        Trace.WriteLine(StatusMessage);
                        await DisplayPresetsAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error applying preset: {ex.Message}");
            }
        }
    }


    private async Task<bool> ApplyPresetAsync(LocalPresetData preset)
    {
        try
        {
            Trace.WriteLine($"[BetterRTX] === APPLYING PRESET: {preset.Name} ===");

            var filesToApply = new List<(string sourcePath, string destPath)>();
            var filesToCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingDefaultFiles = Directory.GetFiles(_defaultFolder, "*.bin", SearchOption.TopDirectoryOnly);
            bool isDefaultEmpty = existingDefaultFiles.Length == 0;

            if (isDefaultEmpty)
            {
                foreach (var coreFileName in CoreRTXFiles)
                {
                    var coreFilePath = Path.Combine(_gameMaterialsPath, coreFileName);
                    if (File.Exists(coreFilePath))
                    {
                        filesToCache.Add(coreFilePath);
                    }
                }
            }

            if (preset.BinFiles != null)
            {
                foreach (var binFilePath in preset.BinFiles)
                {
                    var binFileName = Path.GetFileName(binFilePath);
                    var destBinPath = Path.Combine(_gameMaterialsPath, binFileName);

                    if (File.Exists(destBinPath) && isDefaultEmpty)
                    {
                        filesToCache.Add(destBinPath);
                    }

                    filesToApply.Add((binFilePath, destBinPath));
                }
            }

            if (isDefaultEmpty && filesToCache.Count > 0)
            {
                foreach (var filePath in filesToCache)
                {
                    var fileName = Path.GetFileName(filePath);
                    var defaultPath = Path.Combine(_defaultFolder, fileName);
                    try
                    {
                        File.Copy(filePath, defaultPath, false);
                        Trace.WriteLine($"[BetterRTX]   ✓ Cached: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[BetterRTX]   ✗ Error caching {fileName}: {ex.Message}");
                    }
                }
            }

            var success = await Helpers.ReplaceFilesWithElevation(filesToApply, "[BetterRTX]", "betterrtx_install");
            return success;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error in ApplyPresetAsync: {ex.Message}");
            return false;
        }
    }

    internal static string? ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error computing hash: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, string> GetCurrentlyInstalledHashes() => GetCurrentlyInstalledHashes(_gameMaterialsPath);

    internal static Dictionary<string, string> GetCurrentlyInstalledHashes(string gameMaterialsPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CoreRTXFiles)
        {
            var filePath = Path.Combine(gameMaterialsPath, fileName);
            if (File.Exists(filePath))
            {
                var hash = ComputeFileHash(filePath);
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes[fileName] = hash;
                    Trace.WriteLine($"[BetterRTX]   📊 {fileName}: {hash.Substring(0, 8)}...");
                }
            }
        }

        Trace.WriteLine($"[BetterRTX] 📊 Current game has {hashes.Count}/{CoreRTXFiles.Length} Core RTX files");
        return hashes;
    }

    internal static Dictionary<string, string> GetPresetHashes(List<string> binFiles)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CoreRTXFiles)
        {
            var matchingFile = binFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (matchingFile != null && File.Exists(matchingFile))
            {
                var hash = ComputeFileHash(matchingFile);
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes[fileName] = hash;
                }
            }
        }

        return hashes;
    }

    internal static bool AreHashesMatching(Dictionary<string, string> currentHashes, Dictionary<string, string> presetHashes)
    {
        if (currentHashes == null || presetHashes == null)
        {
            Trace.WriteLine("[BetterRTX] ⚠ Cannot compare - one or both hash sets are null");
            return false;
        }

        if (currentHashes.Count == 0 || presetHashes.Count == 0)
        {
            Trace.WriteLine("[BetterRTX] ⚠ Cannot compare - one or both hash sets are empty");
            return false;
        }

        // Find files present in BOTH
        var commonFiles = currentHashes.Keys.Intersect(presetHashes.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        if (commonFiles.Count == 0)
        {
            Trace.WriteLine("[BetterRTX] ⚠ No common files to compare");
            return false;
        }

        Trace.WriteLine($"[BetterRTX] 🔍 Comparing {commonFiles.Count} common files:");

        // ALL common files must match
        foreach (var fileName in commonFiles)
        {
            var currentHash = currentHashes[fileName];
            var presetHash = presetHashes[fileName];

            if (currentHash != presetHash)
            {
                Trace.WriteLine($"[BetterRTX]   ✗ {fileName}: MISMATCH");
                return false;
            }
            else
            {
                Trace.WriteLine($"[BetterRTX]   ✓ {fileName}: Match");
            }
        }

        Trace.WriteLine("[BetterRTX]   ✓✓✓ ALL common files match!");
        return true;
    }

    public static string SanitizePresetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unnamed_Preset";

        var sanitized = name;

        var badChars = new HashSet<char>(Path.GetInvalidFileNameChars())
        {
            '\'', '`', '$', ';', '&', '|', '<', '>', '(', ')', '{', '}', '[', ']',
            '"', '~', '!', '@', '#', '%', '^'
        };

        var chars = sanitized.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (badChars.Contains(chars[i]) || char.IsControl(chars[i]))
            {
                chars[i] = '_';
            }
        }
        sanitized = new string(chars);

        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        while (sanitized.Contains("  "))
            sanitized = sanitized.Replace("  ", " ");

        sanitized = sanitized.Trim('_', ' ', '.');

        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                       "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                       "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = sanitized.ToUpperInvariant();
        if (reserved.Contains(upperName) || reserved.Any(r => upperName.StartsWith(r + ".")))
        {
            sanitized = "_" + sanitized;
        }

        if (string.IsNullOrWhiteSpace(sanitized))
            return "Unnamed_Preset";

        if (sanitized.Length > 200)
            sanitized = sanitized.Substring(0, 150).TrimEnd('_', ' ', '.');

        return sanitized;
    }
}







/// <summary>
/// Smart preset sorter: A-Z alphabetically, but version numbers in descending order (9-1)
/// </summary>
public static class SmartPresetSorter
{
    /// <summary>
    /// Orders a sequence by name using <see cref="ComparePresetNames"/>, with a
    /// safety net around the ordering call itself.
    ///
    /// ComparePresetNames already catches its own internal exceptions and falls
    /// back to a simple comparison. But .NET's OrderBy/Sort don't just trust a
    /// comparer blindly - they verify the comparisons are internally consistent
    /// (transitive) across the whole sequence, and throw
    /// InvalidOperationException if they ever contradict each other. That
    /// exception is thrown by OrderBy itself, not by ComparePresetNames, so no
    /// try/catch inside ComparePresetNames can ever catch it. This wrapper
    /// catches it at the one place it can actually be caught, and falls back to
    /// a plain ordinal ordering so a pathological/contradictory input can't take
    /// down the whole list.
    /// </summary>
    public static List<T> SafeOrderByName<T>(IEnumerable<T> source, Func<T, string?> nameSelector)
    {
        var list = source as List<T> ?? source.ToList();

        try
        {
            return list.OrderBy(nameSelector, Comparer<string?>.Create(ComparePresetNames)).ToList();
        }
        catch (InvalidOperationException ex)
        {
            Trace.WriteLine($"[BetterRTX] ⚠ SmartPresetSorter ordering failed, falling back to simple alphabetical order: {ex.Message}");
            return list.OrderBy(nameSelector, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Compares two preset names with smart version sorting.
    /// Examples:
    ///   "BetterRTX 1.4.4" comes before "BetterRTX 1.4.0" (and before "BetterRTX 1.4")
    ///   "BetterRTX 1.4.40" comes before "BetterRTX 1.4.4"
    ///   "Pack 10" comes before "Pack 2"
    ///   "Alpha Test" comes before "Beta Test" (normal A-Z)
    ///   "BetterRTX 1.4.4: BetterRTX Default" comes before
    ///   "BetterRTX 1.4.4: BetterRTX — Gilded Graphics" (punctuation doesn't skew ordering)
    ///
    /// COMPARISON STRATEGY: purely positional / segment-by-segment. Names are split
    /// into alternating text and (possibly dotted) numeric segments, and compared
    /// segment by segment left to right - text vs. text alphabetically, number vs.
    /// number by value, dotted version components compared with any missing
    /// trailing part treated as 0.
    ///
    /// This means version numbers are only compared once the surrounding text has
    /// already matched (or once positions line up) - i.e. names are effectively
    /// grouped by their shared leading text first, and ordered by version within
    /// that group. This is a deliberate choice, not an oversight: an earlier
    /// revision of this method tried to be "smarter" by scanning the whole name for
    /// a version number and comparing that first, globally, before anything else.
    /// That broke completely unrelated names - e.g. "Strawberry RT Build 2.0"
    /// jumped above every "BetterRTX 1.4.4: ..." entry purely because 2 > 1,
    /// even though they're different products with no meaningful relationship
    /// between their version numbers. A version number is only meaningfully
    /// comparable between two items that are actually the same thing, and the only
    /// generally reliable signal for "the same thing" in a bare name string is its
    /// shared text - which is exactly what positional comparison already uses.
    /// Trying to bypass that requires guessing at identity in a way that can't be
    /// done safely without hardcoding specific product/brand names.
    ///
    /// Known accepted limitation: if a product fully renames its naming scheme
    /// (e.g. "BetterRTX 1.4.4: Foo" becomes "BetterRTX Preset Pack (v1.5.0): Foo"),
    /// the new-scheme entries won't automatically thread into the old version
    /// timeline - they'll sort based on their own leading text instead. This is
    /// intentional: reliably bridging that case in general isn't possible without
    /// hardcoding names, which would only cover known cases and stay fragile to
    /// anything new.
    ///
    /// Text segments are normalized before comparison: separator punctuation
    /// (":", "-", "–", "—", "_", etc.) is collapsed into plain spaces, so symbols
    /// like an em dash don't get compared as if they were letters (which is what
    /// caused an undecorated name like "...Default" to previously sort below
    /// dash-prefixed variants purely due to punctuation, not wording).
    /// </summary>
    public static int ComparePresetNames(string? name1, string? name2)
    {
        // Handle null/empty cases
        if (name1 == name2) return 0;
        if (string.IsNullOrEmpty(name1)) return 1;
        if (string.IsNullOrEmpty(name2)) return -1;

        try
        {
            // Split both names into segments: each segment is either a text run,
            // or a full dotted numeric run (e.g. "1.4.4") captured as one segment.
            var segments1 = SplitIntoSegments(name1);
            var segments2 = SplitIntoSegments(name2);

            // Compare segment by segment
            int minLength = Math.Min(segments1.Count, segments2.Count);

            for (int i = 0; i < minLength; i++)
            {
                var seg1 = segments1[i];
                var seg2 = segments2[i];

                // If both segments are numeric (version-like), compare component by
                // component (major, minor, patch, ...), missing trailing components
                // are treated as 0. Higher version wins and sorts first (descending).
                if (seg1.IsNumeric && seg2.IsNumeric)
                {
                    int result = CompareVersionComponents(seg1.Components, seg2.Components);
                    if (result != 0) return result;
                    // If every component matched (e.g. "1.4" vs "1.4.0"), fall
                    // through and let the next segment decide.
                }
                // If one is numeric and one is text, text comes first
                else if (seg1.IsNumeric && !seg2.IsNumeric)
                {
                    return 1;
                }
                else if (!seg1.IsNumeric && seg2.IsNumeric)
                {
                    return -1;
                }
                // Both are text - compare normalized text (punctuation collapsed to
                // spaces) with culture-aware comparison for non-ASCII correctness.
                else
                {
                    int result = string.Compare(seg1.NormalizedText, seg2.NormalizedText, StringComparison.CurrentCultureIgnoreCase);
                    if (result != 0) return result;
                }
            }

            // If all shared segments matched, shorter name comes first
            // (e.g. no version number vs. has a version number, or identical prefixes)
            int lengthResult = segments1.Count.CompareTo(segments2.Count);
            if (lengthResult != 0) return lengthResult;

            // Fully tied after everything above (e.g. two names that normalize
            // identically). Fall back to a raw ordinal comparison of the original
            // strings purely for a deterministic, stable result - most sort
            // implementations aren't guaranteed stable, so unresolved ties can
            // otherwise shuffle unpredictably between runs.
            return string.CompareOrdinal(name1, name2);
        }
        catch (Exception ex)
        {
            // If anything goes wrong during parsing/comparison, fall back to simple ordinal comparison
            Trace.WriteLine($"[BetterRTX] ⚠ SmartPresetSorter error, falling back to simple sort: {ex.Message}");
            return string.Compare(name1, name2, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Compares two version component lists (e.g. [1,4,4] vs [1,4,0]) part by
    /// part, treating any missing trailing component as 0. Higher version sorts
    /// first (returns negative), matching the descending version-number ordering.
    /// </summary>
    private static int CompareVersionComponents(List<decimal> components1, List<decimal> components2)
    {
        int maxParts = Math.Max(components1.Count, components2.Count);

        for (int p = 0; p < maxParts; p++)
        {
            decimal v1 = p < components1.Count ? components1[p] : 0m;
            decimal v2 = p < components2.Count ? components2[p] : 0m;

            int cmp = v2.CompareTo(v1); // Reversed! Higher version comes first
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    /// <summary>
    /// Splits a name into alternating text and numeric segments.
    /// Unlike a naive char-by-char split, a numeric run followed by ".digits"
    /// (e.g. "1.4.4") is captured as ONE numeric segment with multiple components,
    /// so version numbers of differing lengths still line up for comparison.
    /// </summary>
    private static List<Segment> SplitIntoSegments(string name)
    {
        var segments = new List<Segment>();
        var text = new System.Text.StringBuilder();
        int i = 0;

        while (i < name.Length)
        {
            if (char.IsDigit(name[i]))
            {
                // Flush any pending text segment before starting a numeric one
                if (text.Length > 0)
                {
                    segments.Add(new Segment { Text = text.ToString(), IsNumeric = false });
                    text.Clear();
                }

                var components = new List<decimal>();

                // Read the first numeric component
                int start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                components.Add(ParseComponent(name.Substring(start, i - start)));

                // Keep consuming ".digits" as additional components of the SAME
                // version segment (e.g. turns "1.4.4" into components [1, 4, 4])
                while (i < name.Length && name[i] == '.' && i + 1 < name.Length && char.IsDigit(name[i + 1]))
                {
                    i++; // skip '.'
                    int partStart = i;
                    while (i < name.Length && char.IsDigit(name[i])) i++;
                    components.Add(ParseComponent(name.Substring(partStart, i - partStart)));
                }

                segments.Add(new Segment { IsNumeric = true, Components = components });
            }
            else
            {
                text.Append(name[i]);
                i++;
            }
        }

        // Add final remaining text segment, if any
        if (text.Length > 0)
        {
            segments.Add(new Segment { Text = text.ToString(), IsNumeric = false });
        }

        return segments;
    }

    /// <summary>
    /// Parses a single numeric component (e.g. the "4" in "1.4.4") as a decimal,
    /// so unusually long numbers don't overflow like they might with long/int.
    /// </summary>
    private static decimal ParseComponent(string numberText)
    {
        if (decimal.TryParse(numberText, out decimal value))
        {
            return value;
        }

        // Number too large to parse - treat as 0 (rare edge case)
        Trace.WriteLine($"[BetterRTX] ⚠ Number too large to parse, treating as 0: {numberText}");
        return 0m;
    }

    /// <summary>
    /// Collapses any run of non-alphanumeric characters (punctuation, symbols,
    /// whitespace - ":", "-", "–", "—", "_", etc.) into a single space, and trims
    /// the ends. This is deliberately generic rather than targeting specific
    /// separator characters, since preset names can use any mix of them
    /// inconsistently. Letters and digits from any script/language are preserved
    /// as-is (char.IsLetterOrDigit is Unicode-aware).
    /// </summary>
    private static string NormalizeForComparison(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private class Segment
    {
        private string _text = string.Empty;
        private string? _normalizedText;

        /// <summary>Raw text for non-numeric segments.</summary>
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                _normalizedText = null; // invalidate cache
            }
        }

        /// <summary>
        /// Normalized (punctuation-collapsed) version of Text, computed lazily and
        /// cached. Used for actual comparisons so separator characters don't
        /// introduce culture-specific ordering artifacts.
        /// </summary>
        public string NormalizedText => _normalizedText ??= NormalizeForComparison(_text);

        /// <summary>Whether this segment represents a (possibly dotted) number.</summary>
        public bool IsNumeric { get; set; }

        /// <summary>
        /// For numeric segments: the dotted components in order, e.g. "1.4.4" -> [1, 4, 4].
        /// Missing trailing components are treated as 0 when compared against a
        /// segment with more components.
        /// </summary>
        public List<decimal> Components { get; set; } = new();
    }
}

/// <summary>
/// Detects Minecraft version changes by hashing <c>MicrosoftGame.Config</c> — the game's install manifest —
/// and comparing it to the hash stored from the previous run. This is a raw content hash, not a parsed version
/// field: the class never reads an actual version number out of the file, it just assumes the manifest's bytes
/// change whenever the game updates, and treats "the hash differs" as a proxy for "the game version changed."
///
/// Elsewhere, a detected change drives a full cache wipe (see <c>WipeEntireCache</c>, as opposed to the soft/non-default
/// wipe used elsewhere), which both forces __DEFAULT to be freshly reconstructed from the post-update game files
/// next time a preset is applied, and forces every BetterRTX preset to be treated as not-downloaded so stale,
/// possibly update-incompatible files get re-fetched rather than reused. It also clears the stored BetterRTX
/// disclaimer acknowledgement, so the user is re-prompted after an update.
///
/// Uncertainty generally resolves in favor of invalidating the cache: an invalid/missing install path, a config
/// file that's gone missing after previously being found, a failed hash computation, or any unexpected exception
/// all report a change occurred. The one exception is the very first run — no stored hash yet means there's
/// nothing to invalidate against, so that case is treated as "unchanged" and simply establishes the baseline
/// hash for future comparisons.
/// </summary>
public static class GameVersionDetector
{
    // Stable release only
    private const string CONFIG_HASH_KEY = "MinecraftConfigHash";

    /// <summary>
    /// Detects if game version has changed by comparing MicrosoftGame.Config hash.
    /// Returns true if version changed OR unable to determine (safe default).
    /// </summary>
    public static async Task<bool> HasGameVersionChanged(string minecraftInstallPath)
    {
        try
        {
            Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION START ===");

            if (string.IsNullOrEmpty(minecraftInstallPath) || !Directory.Exists(minecraftInstallPath))
            {
                Trace.WriteLine("[BetterRTX] ⚠ Invalid Minecraft install path - INVALIDATING CACHE (safe default)");
                Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (invalid path) ===");
                return true; // Invalidate cache when uncertain
            }

            // Find MicrosoftGame.Config file (max 2 levels deep)
            var configPath = FindFileRecursively(minecraftInstallPath, "MicrosoftGame.Config", 2);

            // Get stored hash
            var settings = ApplicationData.Current.LocalSettings;
            var storedConfigHash = settings.Values[CONFIG_HASH_KEY] as string;

            Trace.WriteLine($"[BetterRTX] 💾 Stored Config hash: {storedConfigHash ?? "NULL (first run or cleared)"}");

            // CASE 1: Config file not found
            if (string.IsNullOrEmpty(configPath))
            {
                Trace.WriteLine("[BetterRTX] ⚠ MicrosoftGame.Config not found in game directory");

                if (!string.IsNullOrEmpty(storedConfigHash))
                {
                    // Had hash before, file now missing - INVALIDATE
                    Trace.WriteLine("[BetterRTX] 🔥 CONFIG FILE DISAPPEARED - CACHE INVALIDATION!");
                    settings.Values.Remove(CONFIG_HASH_KEY);
                    Trace.WriteLine("[BetterRTX] 💾 Cleared stored config hash");
                    Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (file disappeared) ===");
                    return true;
                }
                else
                {
                    // Never had hash, still can't find file - INVALIDATE (safe default)
                    Trace.WriteLine("[BetterRTX] 🔥 Unable to locate config file - CACHE INVALIDATION (safe default)");
                    Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (unable to determine) ===");
                    return true;
                }
            }

            // CASE 2: Config file exists - compute its hash
            var currentConfigHash = ComputeFileHash(configPath);
            Trace.WriteLine($"[BetterRTX] 📊 Current Config hash: {currentConfigHash ?? "NULL (computation failed)"}");

            if (string.IsNullOrEmpty(currentConfigHash))
            {
                // File exists but can't compute hash - INVALIDATE (safe default)
                Trace.WriteLine("[BetterRTX] 🔥 Failed to compute config hash - CACHE INVALIDATION (safe default)");
                Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (hash computation failed) ===");
                return true;
            }

            // CASE 3: We have a valid current hash
            bool versionChanged = false;

            if (string.IsNullOrEmpty(storedConfigHash))
            {
                // First run - no stored hash yet
                Trace.WriteLine("[BetterRTX] ✓ First run - storing initial config hash (not a version change)");
                versionChanged = false;
            }
            else if (currentConfigHash != storedConfigHash)
            {
                // Hash changed - version updated
                Trace.WriteLine("[BetterRTX] 🔥 CONFIG HASH CHANGED - GAME VERSION UPDATED!");
                Trace.WriteLine($"[BetterRTX]    Old: {storedConfigHash.Substring(0, 16)}...");
                Trace.WriteLine($"[BetterRTX]    New: {currentConfigHash.Substring(0, 16)}...");
                versionChanged = true;

                // Clear disclaimer so user is re-notified after game update
                settings.Values.Remove(BetterRTXManagerWindow.BETTERRTX_DISCLAIMER_KEY);
                Trace.WriteLine("[BetterRTX] 💾 Cleared BetterRTX disclaimer key — will re-prompt on next open");
            }
            else
            {
                // Hash matches - no change
                Trace.WriteLine("[BetterRTX] ✓ Config hash matches - no version change");
                versionChanged = false;
            }

            // Always update stored hash with current value
            settings.Values[CONFIG_HASH_KEY] = currentConfigHash;
            Trace.WriteLine("[BetterRTX] 💾 Saved current config hash");

            Trace.WriteLine($"[BetterRTX] === GAME VERSION DETECTION END (changed: {versionChanged}) ===");
            return versionChanged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] ✗ EXCEPTION in version detection: {ex.Message}");
            Trace.WriteLine("[BetterRTX] 🔥 Exception occurred - CACHE INVALIDATION (safe default)");
            Trace.WriteLine("[BetterRTX] === GAME VERSION DETECTION END (exception) ===");
            return true; // Invalidate cache on any error (safe default)
        }
    }

    private static string? FindFileRecursively(string startPath, string fileName, int maxDepth)
    {
        try
        {
            return FindFileRecursivelyInternal(startPath, fileName, 0, maxDepth);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error searching for {fileName}: {ex.Message}");
            return null;
        }
    }

    private static string? FindFileRecursivelyInternal(string currentPath, string fileName, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth || !Directory.Exists(currentPath))
            return null;

        var targetPath = Path.Combine(currentPath, fileName);
        if (File.Exists(targetPath))
        {
            Trace.WriteLine($"[BetterRTX] ✓ Found {fileName} at: {targetPath}");
            return targetPath;
        }

        if (currentDepth < maxDepth)
        {
            try
            {
                foreach (var subDir in Directory.GetDirectories(currentPath))
                {
                    var result = FindFileRecursivelyInternal(subDir, fileName, currentDepth + 1, maxDepth);
                    if (result != null)
                        return result;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BetterRTX] Error accessing subdirectory: {ex.Message}");
            }
        }

        return null;
    }

    private static string? ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error computing hash for {filePath}: {ex.Message}");
            return null;
        }
    }

    public static void ClearStoredVersionHashes()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(CONFIG_HASH_KEY);
            Trace.WriteLine("[BetterRTX] ✓ Cleared stored version hash");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BetterRTX] Error clearing version hash: {ex.Message}");
        }
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
