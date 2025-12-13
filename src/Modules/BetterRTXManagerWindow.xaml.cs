using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.BetterRTXBrowser;

public sealed partial class BetterRTXManagerWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;
    private string _gameMaterialsPath;
    private string _cacheFolder;
    private string _defaultFolder;
    private string _apiCachePath;
    private CancellationTokenSource _scanCancellationTokenSource;
    private List<ApiPresetData> _apiPresets;
    private Dictionary<string, LocalPresetData> _localPresets;
    private Dictionary<string, DownloadStatus> _downloadStatuses;
    private readonly Queue<DownloadQueueItem> _downloadQueue;
    private bool _isProcessingQueue;
    private readonly HttpClient _betterRtxHttpClient;

    private const string REFRESH_COOLDOWN_KEY = "BetterRTXManager_RefreshCooldown_LastClickTimestamp_v1";
    private const int REFRESH_COOLDOWN_SECONDS = 3;
    private DispatcherTimer _cooldownTimer;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    private static readonly string[] CoreRTXFiles = new[]
    {
       "RTXPostFX.Bloom.material.bin",
       "RTXPostFX.material.bin",
       "RTXPostFX.Tonemapping.material.bin",
       "RTXStub.material.bin"
    };

    public BetterRTXManagerWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;
        _downloadStatuses = new Dictionary<string, DownloadStatus>();
        _downloadQueue = new Queue<DownloadQueueItem>();
        _isProcessingQueue = false;

        // Create dedicated HttpClient for BetterRTX downloads
        // This is configured ONCE and never modified
        _betterRtxHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        _betterRtxHttpClient.DefaultRequestHeaders.Add("User-Agent", $"vanilla_rtx_app/{TunerVariables.appVersion}");

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

        // Window setup
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsAlwaysOnTop = false;
            var dpi = MainWindow.GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(Defaults.WindowMinSizeX * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(Defaults.WindowMinSizeY * scaleFactor);
        }

        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        this.Activated += BetterRTXManagerWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        _downloadQueue.Clear();
        _downloadStatuses.Clear();

        _betterRtxHttpClient?.Dispose();

        _mainWindow.Closed -= MainWindow_Closed;

        _cooldownTimer?.Stop();
        _cooldownTimer = null;

        this.Close();
    }

    private async void BetterRTXManagerWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= BetterRTXManagerWindow_Activated;

            if (TunerVariables.Persistent.IsTargetingPreview)
            {
                StatusMessage = "BetterRTX Preset Manager does not support Minecraft Preview at this time.";
                this.Close();
                return;
            }

            WindowTitle.Text = "BetterRTX Preset Manager - Minecraft Release";
            ManualSelectionText.Text = $"If this is taking too long, click to manually locate the game folder, confirm in file explorer once you're inside the folder called: {MinecraftGDKLocator.MinecraftFolderName}";

            await InitializeAsync();

            // Bring to top again
            _ = this.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(75);
                try
                {
                    this.Activate();
                }
                catch { }
            });

            InitializeRefreshButton();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var cachedPath = TunerVariables.Persistent.MinecraftInstallPath;
            string minecraftPath = null;

            // Validate cached path
            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath))
            {
                Debug.WriteLine($"✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                // Cache invalid - clear it and search
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Debug.WriteLine($"⚠ Cache became invalid, clearing");
                    TunerVariables.Persistent.MinecraftInstallPath = null;
                }

                // Show manual selection button
                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionButton.Visibility = Visibility.Visible;
                });

                // Start system-wide search
                Debug.WriteLine("Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    false, // Never preview
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Debug.WriteLine("System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // Continue with valid path
            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }


    // Refresh button things
    private void InitializeRefreshButton()
    {
        UpdateRefreshButtonState();

        // Set up timer to update button state every second
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
                var remainingMinutes = REFRESH_COOLDOWN_SECONDS - (int)elapsed.TotalSeconds;

                if (remainingMinutes > 0)
                {
                    RefreshButton.IsEnabled = false;
                    ToolTipService.SetToolTip(RefreshButton, $"On cooldown, check back in {remainingMinutes} minute{(remainingMinutes != 1 ? "s" : "")}");
                    return;
                }
            }

            RefreshButton.IsEnabled = true;
            ToolTipService.SetToolTip(RefreshButton, "Refresh preset list");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating refresh button state: {ex.Message}");
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Debug.WriteLine("=== REFRESH BUTTON CLICKED ===");

            // Set cooldown timestamp
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;

            // Update button state immediately to show cooldown
            UpdateRefreshButtonState();

            // Delete API cache to force re-fetch
            if (File.Exists(_apiCachePath))
            {
                File.Delete(_apiCachePath);
                Debug.WriteLine("✓ Deleted API cache - will fetch fresh data");
            }
            else
            {
                Debug.WriteLine("⚠ API cache didn't exist");
            }

            // Show loading panel
            LoadingPanel.Visibility = Visibility.Visible;
            PresetSelectionPanel.Visibility = Visibility.Collapsed;

            await Task.Delay(100); // Brief delay for UI update

            // Reload everything (this will fetch fresh API data since cache was deleted)
            _apiPresets = null;
            await LoadApiDataAsync();
            await LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            // Hide loading panel
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;

            Debug.WriteLine("✓ Preset list refreshed successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error refreshing preset list: {ex.Message}");

            // Ensure UI is restored even on error
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;
        }
    }



    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(false, hWnd);

        if (path != null)
        {
            Debug.WriteLine($"✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Debug.WriteLine("✗ User cancelled or selected invalid path");
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        _gameMaterialsPath = Path.Combine(minecraftPath, "Content", "data", "renderer", "materials");

        // Verify materials folder exists
        if (!Directory.Exists(_gameMaterialsPath))
        {
            StatusMessage = "Materials folder not found in Minecraft installation";
            this.Close();
            return;
        }

        // Establish cache folder
        _cacheFolder = EstablishCacheFolder();
        if (_cacheFolder == null)
        {
            StatusMessage = "Could not establish cache folder";
            this.Close();
            return;
        }

        _defaultFolder = Path.Combine(_cacheFolder, "__DEFAULT");
        _apiCachePath = Path.Combine(_cacheFolder, "betterrtx_api_cache.json");

        // CRITICAL: Check if game version changed
        bool versionChanged = await GameVersionDetector.HasGameVersionChanged(minecraftPath);

        if (versionChanged)
        {
            Debug.WriteLine("⚠⚠⚠ GAME VERSION CHANGED - WIPING CACHE ⚠⚠⚠");
            WipeEntireCache();
            // Recreate cache folder structure
            Directory.CreateDirectory(_cacheFolder);
            Directory.CreateDirectory(_defaultFolder);
        }
        else
        {
            Directory.CreateDirectory(_defaultFolder);
        }

        // Load or fetch API data
        await LoadApiDataAsync();

        // Load local presets
        await LoadLocalPresetsAsync();

        // Display everything
        await DisplayPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;
    }

    private string EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "RTX_Cache");

            Debug.WriteLine($"Cache location: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Debug.WriteLine($"✓ Cache established");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Failed to create cache: {ex.Message}");
            return null;
        }
    }

    private void WipeEntireCache()
    {
        try
        {
            if (Directory.Exists(_cacheFolder))
            {
                Debug.WriteLine($"Deleting entire cache folder: {_cacheFolder}");
                Directory.Delete(_cacheFolder, true);
                Debug.WriteLine("✓ Cache wiped successfully");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error wiping cache: {ex.Message}");
        }
    }

    private async Task LoadApiDataAsync()
    {
        try
        {
            string jsonData = null;

            // Check if cache exists
            if (File.Exists(_apiCachePath))
            {
                Debug.WriteLine("✓ Loading API data from cache (no fetch needed)");
                jsonData = await File.ReadAllTextAsync(_apiCachePath);
            }
            else
            {
                // No cache - fetch from API
                Debug.WriteLine("Fetching API data from server (no cache found)...");
                jsonData = await FetchApiDataAsync();

                if (jsonData != null)
                {
                    // Save to cache
                    await File.WriteAllTextAsync(_apiCachePath, jsonData);
                    Debug.WriteLine("✓ API data cached");
                }
                else
                {
                    Debug.WriteLine("⚠ Failed to fetch API data and no cache available");
                    _apiPresets = new List<ApiPresetData>();
                    return;
                }
            }

            if (jsonData != null)
            {
                _apiPresets = ParseApiData(jsonData);
                Debug.WriteLine($"✓ Loaded {_apiPresets?.Count ?? 0} presets from API");
            }
            else
            {
                _apiPresets = new List<ApiPresetData>();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading API data: {ex.Message}");
            _apiPresets = new List<ApiPresetData>();
        }
    }

    private async Task<string> FetchApiDataAsync()
    {
        try
        {
            // Use the shared client WITHOUT modifying it
            // Assume it's already configured with timeout and user-agent
            var client = Helpers.SharedHttpClient;

            var response = await client.GetAsync("https://bedrock.graphics/api");

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"⚠ API returned status code: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            return content;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Debug.WriteLine("⚠ API request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ Error fetching API data: {ex.Message}");
            return null;
        }
    }

    private List<ApiPresetData> ParseApiData(string jsonData)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var presets = JsonSerializer.Deserialize<List<ApiPresetData>>(jsonData, options);
            return presets ?? new List<ApiPresetData>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing API data: {ex.Message}");
            return new List<ApiPresetData>();
        }
    }

    private async Task LoadLocalPresetsAsync()
    {
        _localPresets = new Dictionary<string, LocalPresetData>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Get all folders except __DEFAULT
            var presetFolders = Directory.GetDirectories(_cacheFolder)
                .Where(d => !Path.GetFileName(d).Equals("__DEFAULT", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in presetFolders)
            {
                var localPreset = await ParseLocalPresetAsync(folder);
                if (localPreset != null && !string.IsNullOrEmpty(localPreset.Uuid))
                {
                    _localPresets[localPreset.Uuid] = localPreset;
                    Debug.WriteLine($"✓ Loaded local preset: {localPreset.Name} (UUID: {localPreset.Uuid})");
                }
            }

            Debug.WriteLine($"✓ Loaded {_localPresets.Count} local presets");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading local presets: {ex.Message}");
        }
    }

    private async Task<LocalPresetData> ParseLocalPresetAsync(string presetFolder)
    {
        try
        {
            var manifestFiles = Directory.GetFiles(presetFolder, "manifest.json", SearchOption.AllDirectories);

            if (manifestFiles.Length == 0)
            {
                Debug.WriteLine($"No manifest found in: {presetFolder}");
                return null;
            }

            var manifestPath = manifestFiles[0];
            var manifestDir = Path.GetDirectoryName(manifestPath);

            var json = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = doc.RootElement;

            string uuid = null;
            string name = Path.GetFileName(presetFolder);

            if (root.TryGetProperty("header", out var header))
            {
                if (header.TryGetProperty("uuid", out var uuidElement))
                {
                    uuid = uuidElement.GetString();
                }

                if (header.TryGetProperty("name", out var nameElement))
                {
                    var parsedName = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(parsedName))
                        name = parsedName;
                }
            }

            if (string.IsNullOrEmpty(uuid))
            {
                Debug.WriteLine($"⚠ No UUID in manifest: {presetFolder}");
                return null;
            }

            var icon = await LoadIconAsync(manifestDir) ?? await LoadIconAsync(presetFolder);
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
                FileHashes = presetHashes // Changed from StubHash
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing local preset {presetFolder}: {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage> LoadIconAsync(string directory)
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
                Debug.WriteLine($"Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }

    private async Task DisplayPresetsAsync()
    {
        try
        {
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
            foreach (var apiPreset in _apiPresets)
            {
                seenUuids.Add(apiPreset.Uuid);

                if (_localPresets.TryGetValue(apiPreset.Uuid, out var localPreset))
                {
                    // Downloaded - add to downloaded list
                    downloadedPresets.Add(new DisplayPresetData
                    {
                        Uuid = apiPreset.Uuid,
                        Name = localPreset.Name,
                        IsDownloaded = true,
                        Icon = localPreset.Icon,
                        PresetPath = localPreset.PresetPath,
                        BinFiles = localPreset.BinFiles,
                        FileHashes = localPreset.FileHashes // Changed from StubHash
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
                        FileHashes = null // Changed from StubHash
                    });
                }
            }

            // Second, add any local presets that weren't in the API list
            foreach (var localPreset in _localPresets.Values)
            {
                if (!seenUuids.Contains(localPreset.Uuid))
                {
                    downloadedPresets.Add(new DisplayPresetData
                    {
                        Uuid = localPreset.Uuid,
                        Name = localPreset.Name,
                        IsDownloaded = true,
                        Icon = localPreset.Icon,
                        PresetPath = localPreset.PresetPath,
                        BinFiles = localPreset.BinFiles,
                        FileHashes = localPreset.FileHashes // Changed from StubHash
                    });
                }
            }

            // Sort each list alphabetically
            downloadedPresets = downloadedPresets.OrderBy(p => p.Name).ToList();
            notDownloadedPresets = notDownloadedPresets.OrderBy(p => p.Name).ToList();

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
                if (_apiPresets.Count == 0 && _localPresets.Count == 0)
                {
                    EmptyStateText.Text = "No presets available. An internet connection is required to load BetterRTX presets.";
                }
                else
                {
                    EmptyStateText.Text = "No BetterRTX presets available.";
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error displaying presets: {ex.Message}");
        }
    }

    private LocalPresetData CreateDefaultPreset()
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
            Uuid = "__DEFAULT",
            Name = "Default RTX",
            PresetPath = _defaultFolder,
            Icon = null,
            BinFiles = binFiles,
            FileHashes = presetHashes // Changed from StubHash
        };
    }

    private Button CreatePresetButton(object presetData, Dictionary<string, string> currentInstalledHashes, bool isDefault)
    {
        bool isDownloaded = true;
        bool isCurrent = false;
        string name = "";
        string description = "";
        string uuid = "";
        BitmapImage icon = null;

        if (presetData is LocalPresetData localPreset)
        {
            isDownloaded = true;
            name = localPreset.Name;
            description = Helpers.SanitizePathForDisplay(localPreset.PresetPath);
            icon = localPreset.Icon;
            uuid = localPreset.Uuid;

            // Compare ALL hashes
            if (localPreset.FileHashes != null && AreHashesMatching(currentInstalledHashes, localPreset.FileHashes))
            {
                isCurrent = true;
            }
        }
        else if (presetData is DisplayPresetData displayPreset)
        {
            isDownloaded = displayPreset.IsDownloaded;
            name = displayPreset.Name;
            uuid = displayPreset.Uuid;
            icon = displayPreset.Icon;

            // Dynamic description based on download status
            if (displayPreset.IsDownloaded)
            {
                description = Helpers.SanitizePathForDisplay(displayPreset.PresetPath);
            }
            else
            {
                // Check download status
                if (_downloadStatuses.TryGetValue(uuid, out var status))
                {
                    description = status switch
                    {
                        DownloadStatus.Queued => "In queue",
                        DownloadStatus.Downloading => "Download in progress...",
                        _ => "Click to download"
                    };
                }
                else
                {
                    description = "Click to download";
                }
            }

            // Compare ALL hashes
            if (isDownloaded && displayPreset.FileHashes != null &&
                AreHashesMatching(currentInstalledHashes, displayPreset.FileHashes))
            {
                isCurrent = true;
            }
        }

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 38, 20),
            Margin = new Thickness(0, 5, 0, 5),
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon
        var iconBorder = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent),
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

        var iconShadow = new ThemeShadow();
        iconBorder.Shadow = iconShadow;
        iconBorder.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
            {
                iconShadow.Receivers.Add(ShadowReceiverGrid);
            }
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
            iconBorder.Child = new FontIcon
            {
                Glyph = "\uE794",
                FontSize = 48,
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
            // Check download status
            var status = _downloadStatuses.TryGetValue(uuid, out var s) ? s : DownloadStatus.NotDownloaded;

            if (status == DownloadStatus.Downloading || status == DownloadStatus.Queued)
            {
                // Show progress ring
                var progressRing = new ProgressRing
                {
                    Width = 40,
                    Height = 40,
                    IsActive = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                };

                Grid.SetColumn(progressRing, 4);
                grid.Children.Add(progressRing);
            }
            else
            {
                // Show download button
                var downloadButton = new Button
                {
                    Width = 36,
                    Height = 36,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    Margin = new Thickness(16, 0, 0, 0),
                    IsTextScaleFactorEnabled = false,
                    Tag = presetData
                };

                var downloadIcon = new FontIcon
                {
                    Glyph = "\uE896",
                    FontSize = 16,
                    IsTextScaleFactorEnabled = false
                };

                downloadButton.Content = downloadIcon;
                downloadButton.Click += DownloadButton_Click;

                Grid.SetColumn(downloadButton, 4);
                grid.Children.Add(downloadButton);
            }
        }

        button.Content = grid;
        button.Click += PresetButton_Click;

        return button;
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DisplayPresetData preset)
        {
            // Add to queue
            EnqueueDownload(preset.Uuid, preset.Name);
        }
    }

    private void EnqueueDownload(string uuid, string name)
    {
        // Check if already in queue or downloading
        if (_downloadStatuses.ContainsKey(uuid))
        {
            Debug.WriteLine($"⚠ Already queued or downloading: {name}");
            return;
        }

        Debug.WriteLine($"➕ Queued download: {name}");

        // Mark as queued
        _downloadStatuses[uuid] = DownloadStatus.Queued;

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
        Debug.WriteLine("▶ Starting download queue processor");

        try
        {
            while (_downloadQueue.Count > 0)
            {
                var item = _downloadQueue.Dequeue();
                Debug.WriteLine($"🔽 Processing download: {item.Name} (Queue: {_downloadQueue.Count} remaining)");

                // Update status to downloading
                _downloadStatuses[item.Uuid] = DownloadStatus.Downloading;
                await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

                // Wait a moment before starting (helps with slow API)
                await Task.Delay(2000);

                // Download
                var success = await DownloadPresetAsync(item.Uuid);

                if (success)
                {
                    Debug.WriteLine($"✓ Download complete: {item.Name}");

                    // Mark as downloaded and remove from status tracking
                    _downloadStatuses.Remove(item.Uuid);

                    // Reload local presets and refresh UI
                    await LoadLocalPresetsAsync();
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }
                else
                {
                    Debug.WriteLine($"✗ Download failed: {item.Name}");

                    // Remove from status (will show download button again)
                    _downloadStatuses.Remove(item.Uuid);
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }

                // Wait between downloads (be nice to the API)
                if (_downloadQueue.Count > 0)
                {
                    Debug.WriteLine("⏱ Waiting 3 seconds before next download...");
                    await Task.Delay(3000);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error in download queue processor: {ex.Message}");
        }
        finally
        {
            _isProcessingQueue = false;
            Debug.WriteLine("⏹ Download queue processor stopped");
        }
    }

    private async Task<bool> DownloadPresetAsync(string uuid)
    {
        try
        {
            var url = $"https://bedrock.graphics/pack/{uuid}/release";
            Debug.WriteLine($"Downloading from: {url}");

            var (success, downloadedPath) = await Helpers.Download(
                url,
                cancellationToken: CancellationToken.None,
                httpClient: _betterRtxHttpClient
            );

            if (!success || string.IsNullOrEmpty(downloadedPath))
            {
                Debug.WriteLine($"✗ Download failed");
                return false;
            }

            Debug.WriteLine($"✓ Downloaded to: {downloadedPath}");

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
                            Debug.WriteLine($"Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            Debug.WriteLine($"✓ Extracted to: {destinationFolder}");

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
            Debug.WriteLine($"✗ Error downloading {uuid}: {ex.Message}");
            return false;
        }
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag != null)
        {
            try
            {
                LocalPresetData presetToApply = null;
                if (button.Tag is LocalPresetData localPreset)
                {
                    presetToApply = localPreset;
                }
                else if (button.Tag is DisplayPresetData displayPreset)
                {
                    if (!displayPreset.IsDownloaded)
                    {
                        // Trigger download by adding to queue
                        EnqueueDownload(displayPreset.Uuid, displayPreset.Name);
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
                            FileHashes = displayPreset.FileHashes // Changed from StubHash
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
                        Debug.WriteLine(StatusMessage);
                        await DisplayPresetsAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying preset: {ex.Message}");
            }
        }
    }

    private async Task<bool> ApplyPresetAsync(LocalPresetData preset)
    {
        try
        {
            Debug.WriteLine($"=== APPLYING PRESET: {preset.Name} ===");

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

            if (isDefaultEmpty && filesToCache.Count > 0)
            {
                foreach (var filePath in filesToCache)
                {
                    var fileName = Path.GetFileName(filePath);
                    var defaultPath = Path.Combine(_defaultFolder, fileName);
                    try
                    {
                        File.Copy(filePath, defaultPath, false);
                        Debug.WriteLine($"  ✓ Cached: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"  ✗ Error caching {fileName}: {ex.Message}");
                    }
                }
            }

            var success = await ReplaceFilesWithElevation(filesToApply);
            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in ApplyPresetAsync: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReplaceFilesWithElevation(List<(string sourcePath, string destPath)> filesToReplace)
    {
        try
        {
            return await Task.Run(() =>
            {
                var scriptLines = new List<string>();

                foreach (var (sourcePath, destPath) in filesToReplace)
                {
                    var escapedSource = sourcePath.Replace("'", "''");
                    var escapedDest = destPath.Replace("'", "''");
                    scriptLines.Add($"Copy-Item -Path '{escapedSource}' -Destination '{escapedDest}' -Force");
                }

                var script = string.Join("; ", scriptLines);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }

                return false;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in ReplaceFilesWithElevation: {ex.Message}");
            return false;
        }
    }

    private string ComputeFileHash(string filePath)
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
            Debug.WriteLine($"Error computing hash: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes hashes for all Core RTX files present in the game's materials folder.
    /// Returns a dictionary of filename -> hash.
    /// </summary>
    private Dictionary<string, string> GetCurrentlyInstalledHashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CoreRTXFiles)
        {
            var filePath = Path.Combine(_gameMaterialsPath, fileName);
            if (File.Exists(filePath))
            {
                var hash = ComputeFileHash(filePath);
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes[fileName] = hash;
                    Debug.WriteLine($"  📊 {fileName}: {hash.Substring(0, 8)}...");
                }
            }
        }

        Debug.WriteLine($"📊 Current game has {hashes.Count}/{CoreRTXFiles.Length} Core RTX files");
        return hashes;
    }

    /// <summary>
    /// Computes hashes for all Core RTX files in a preset's bin files.
    /// Returns a dictionary of filename -> hash.
    /// </summary>
    private Dictionary<string, string> GetPresetHashes(List<string> binFiles)
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

    /// <summary>
    /// Compares two hash dictionaries.
    /// Returns true if ALL files present in BOTH dictionaries have matching hashes.
    /// Ignores files that are missing from either dictionary.
    /// </summary>
    private bool AreHashesMatching(Dictionary<string, string> currentHashes, Dictionary<string, string> presetHashes)
    {
        if (currentHashes == null || presetHashes == null)
        {
            Debug.WriteLine("⚠ Cannot compare - one or both hash sets are null");
            return false;
        }

        if (currentHashes.Count == 0 || presetHashes.Count == 0)
        {
            Debug.WriteLine("⚠ Cannot compare - one or both hash sets are empty");
            return false;
        }

        // Find files present in BOTH
        var commonFiles = currentHashes.Keys.Intersect(presetHashes.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        if (commonFiles.Count == 0)
        {
            Debug.WriteLine("⚠ No common files to compare");
            return false;
        }

        Debug.WriteLine($"🔍 Comparing {commonFiles.Count} common files:");

        // ALL common files must match
        foreach (var fileName in commonFiles)
        {
            var currentHash = currentHashes[fileName];
            var presetHash = presetHashes[fileName];

            if (currentHash != presetHash)
            {
                Debug.WriteLine($"  ✗ {fileName}: MISMATCH");
                return false;
            }
            else
            {
                Debug.WriteLine($"  ✓ {fileName}: Match");
            }
        }

        Debug.WriteLine("  ✓✓✓ ALL common files match!");
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


    // Data classes
    private enum DownloadStatus
    {
        NotDownloaded,
        Queued,
        Downloading,
        Downloaded
    }

    private class DownloadQueueItem
    {
        public string Uuid { get; set; }
        public string Name { get; set; }
    }

    private class ApiPresetData
    {
        public string Uuid { get; set; }
        public string Slug { get; set; }
        public string Name { get; set; }
        public string Stub { get; set; }
        public string Tonemapping { get; set; }
        public string Bloom { get; set; }
    }

    private class LocalPresetData
    {
        public string Uuid { get; set; }
        public string Name { get; set; }
        public string PresetPath { get; set; }
        public BitmapImage Icon { get; set; }
        public List<string> BinFiles { get; set; }
        public Dictionary<string, string> FileHashes { get; set; }
    }

    private class DisplayPresetData
    {
        public string Uuid { get; set; }
        public string Name { get; set; }
        public bool IsDownloaded { get; set; }
        public BitmapImage Icon { get; set; }
        public string PresetPath { get; set; }
        public List<string> BinFiles { get; set; }
        public Dictionary<string, string> FileHashes { get; set; }
    }
}








public static class GameVersionDetector
{
    private const string VERSION_HASH_KEY = "MinecraftVersionHash";
    private const string CONFIG_HASH_KEY = "MinecraftConfigHash";

    /// <summary>
    /// Detects if game version has changed by comparing file hashes.
    /// Returns true if version changed, false if same or unable to determine.
    /// </summary>
    public static async Task<bool> HasGameVersionChanged(string minecraftInstallPath)
    {
        try
        {
            Debug.WriteLine("=== GAME VERSION DETECTION START ===");

            if (string.IsNullOrEmpty(minecraftInstallPath) || !Directory.Exists(minecraftInstallPath))
            {
                Debug.WriteLine("⚠ Invalid Minecraft install path provided to version detector");
                return false;
            }

            // Find the two key files
            var exePath = FindFileRecursively(minecraftInstallPath, "Minecraft.Windows.exe", 2);
            var configPath = FindFileRecursively(minecraftInstallPath, "MicrosoftGame.Config", 2);

            if (string.IsNullOrEmpty(exePath) && string.IsNullOrEmpty(configPath))
            {
                Debug.WriteLine("⚠ Could not find version files - unable to detect version");
                Debug.WriteLine("=== GAME VERSION DETECTION END (no files) ===");
                return false;
            }

            // Compute current hashes
            string currentExeHash = null;
            string currentConfigHash = null;

            if (!string.IsNullOrEmpty(exePath))
            {
                currentExeHash = ComputeFileHash(exePath);
                Debug.WriteLine($"📊 Current EXE hash: {currentExeHash}");
            }
            else
            {
                Debug.WriteLine("⚠ EXE file not found (unusual but handled)");
            }

            if (!string.IsNullOrEmpty(configPath))
            {
                currentConfigHash = ComputeFileHash(configPath);
                Debug.WriteLine($"📊 Current Config hash: {currentConfigHash}");
            }
            else
            {
                Debug.WriteLine("⚠ Config file not found (unusual but handled)");
            }

            // Get stored hashes
            var settings = ApplicationData.Current.LocalSettings;
            var storedExeHash = settings.Values[VERSION_HASH_KEY] as string;
            var storedConfigHash = settings.Values[CONFIG_HASH_KEY] as string;

            Debug.WriteLine($"💾 Stored EXE hash: {storedExeHash ?? "NULL"}");
            Debug.WriteLine($"💾 Stored Config hash: {storedConfigHash ?? "NULL"}");

            // Determine if version changed
            bool versionChanged = false;

            // FIRST RUN: No stored hashes at all
            if (string.IsNullOrEmpty(storedExeHash) && string.IsNullOrEmpty(storedConfigHash))
            {
                Debug.WriteLine("✓ First run - no stored version hashes (not a change)");
                versionChanged = false;
            }
            else
            {
                // NOT first run - we have at least one stored hash

                // Check EXE file
                if (!string.IsNullOrEmpty(currentExeHash))
                {
                    if (string.IsNullOrEmpty(storedExeHash))
                    {
                        // NEW FILE APPEARED - cache invalidation
                        Debug.WriteLine("🔥 EXE FILE NEWLY APPEARED - CACHE INVALIDATION!");
                        versionChanged = true;
                    }
                    else if (currentExeHash != storedExeHash)
                    {
                        // HASH CHANGED - version update
                        Debug.WriteLine("🔥 EXE HASH CHANGED - GAME VERSION UPDATED!");
                        versionChanged = true;
                    }
                }
                else if (!string.IsNullOrEmpty(storedExeHash))
                {
                    // FILE DISAPPEARED - cache invalidation
                    Debug.WriteLine("🔥 EXE FILE DISAPPEARED - CACHE INVALIDATION!");
                    versionChanged = true;
                }

                // Check Config file
                if (!string.IsNullOrEmpty(currentConfigHash))
                {
                    if (string.IsNullOrEmpty(storedConfigHash))
                    {
                        // NEW FILE APPEARED - cache invalidation
                        Debug.WriteLine("🔥 CONFIG FILE NEWLY APPEARED - CACHE INVALIDATION!");
                        versionChanged = true;
                    }
                    else if (currentConfigHash != storedConfigHash)
                    {
                        // HASH CHANGED - version update
                        Debug.WriteLine("🔥 CONFIG HASH CHANGED - GAME VERSION UPDATED!");
                        versionChanged = true;
                    }
                }
                else if (!string.IsNullOrEmpty(storedConfigHash))
                {
                    // FILE DISAPPEARED - cache invalidation
                    Debug.WriteLine("🔥 CONFIG FILE DISAPPEARED - CACHE INVALIDATION!");
                    versionChanged = true;
                }

                if (!versionChanged)
                {
                    Debug.WriteLine("✓ All hashes match - no version change");
                }
            }

            // Update stored hashes with current values (even if null)
            if (!string.IsNullOrEmpty(currentExeHash))
            {
                settings.Values[VERSION_HASH_KEY] = currentExeHash;
                Debug.WriteLine("💾 Saved current EXE hash");
            }
            else if (storedExeHash != null)
            {
                settings.Values.Remove(VERSION_HASH_KEY);
                Debug.WriteLine("💾 Removed EXE hash (file no longer exists)");
            }

            if (!string.IsNullOrEmpty(currentConfigHash))
            {
                settings.Values[CONFIG_HASH_KEY] = currentConfigHash;
                Debug.WriteLine("💾 Saved current Config hash");
            }
            else if (storedConfigHash != null)
            {
                settings.Values.Remove(CONFIG_HASH_KEY);
                Debug.WriteLine("💾 Removed Config hash (file no longer exists)");
            }

            Debug.WriteLine($"=== GAME VERSION DETECTION END (changed: {versionChanged}) ===");
            return versionChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error detecting game version: {ex.Message}");
            Debug.WriteLine("=== GAME VERSION DETECTION END (error) ===");
            return false;
        }
    }

    private static string FindFileRecursively(string startPath, string fileName, int maxDepth)
    {
        try
        {
            return FindFileRecursivelyInternal(startPath, fileName, 0, maxDepth);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error searching for {fileName}: {ex.Message}");
            return null;
        }
    }

    private static string FindFileRecursivelyInternal(string currentPath, string fileName, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth || !Directory.Exists(currentPath))
            return null;

        var targetPath = Path.Combine(currentPath, fileName);
        if (File.Exists(targetPath))
        {
            Debug.WriteLine($"✓ Found {fileName} at: {targetPath}");
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
        }

        return null;
    }

    private static string ComputeFileHash(string filePath)
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
            Debug.WriteLine($"Error computing hash for {filePath}: {ex.Message}");
            return null;
        }
    }

    public static void ClearStoredVersionHashes()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(VERSION_HASH_KEY);
            settings.Values.Remove(CONFIG_HASH_KEY);
            Debug.WriteLine("✓ Cleared stored version hashes");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error clearing version hashes: {ex.Message}");
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
