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
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
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
    private readonly object _downloadStatusLock = new object();

    private const string REFRESH_COOLDOWN_KEY = "BetterRTXManager_RefreshCooldown_LastClickTimestamp";
    private const int REFRESH_COOLDOWN_SECONDS = 59;
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

        this.Activated += BetterRTXManagerWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        _downloadQueue.Clear();
        lock (_downloadStatusLock)
        {
            _downloadStatuses.Clear();
        }

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
                Trace.WriteLine($"✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                // Cache invalid - clear it and search
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine($"⚠ Cache became invalid, clearing");
                    TunerVariables.Persistent.MinecraftInstallPath = null;
                }

                // Show manual selection button
                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionButton.Visibility = Visibility.Visible;
                });

                // Start system-wide search
                Trace.WriteLine("Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    false,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Trace.WriteLine("System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // Continue with valid path
            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"EXCEPTION in InitializeAsync: {ex}");
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
            Trace.WriteLine($"Error updating refresh button state: {ex.Message}");

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
            Trace.WriteLine("=== RESET BUTTON CLICKED ===");

            // Set cooldown timestamp
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;

            // Update button state immediately to show cooldown
            UpdateRefreshButtonState();

            // Show loading panel
            LoadingPanel.Visibility = Visibility.Visible;
            PresetSelectionPanel.Visibility = Visibility.Collapsed;

            await Task.Delay(100); // Brief delay for UI update

            // STEP 1: Read API cache to get list of preset UUIDs to delete
            List<string> uuidsToDelete = new List<string>();

            if (File.Exists(_apiCachePath))
            {
                try
                {
                    Trace.WriteLine("📖 Reading API cache to identify presets to delete...");
                    var jsonData = await File.ReadAllTextAsync(_apiCachePath);
                    var apiPresets = ParseApiData(jsonData);

                    if (apiPresets != null && apiPresets.Count > 0)
                    {
                        uuidsToDelete = apiPresets.Select(p => p.Uuid).ToList();
                        Trace.WriteLine($"✓ Found {uuidsToDelete.Count} presets to delete from API cache");
                    }
                    else
                    {
                        Trace.WriteLine("⚠ API cache was empty or invalid");
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠ Error reading API cache for deletion: {ex.Message}");
                }
            }
            else
            {
                Trace.WriteLine("⚠ API cache didn't exist - no presets to delete");
            }

            // STEP 2: Delete preset folders (but NOT __DEFAULT)
            if (uuidsToDelete.Count > 0)
            {
                Trace.WriteLine("🗑️ Deleting preset folders...");
                int deletedCount = 0;

                foreach (var uuid in uuidsToDelete)
                {
                    try
                    {
                        // Find folder matching this UUID
                        // Presets are stored with sanitized names, so we need to check all folders
                        var allFolders = Directory.GetDirectories(_cacheFolder)
                            .Where(d => !Path.GetFileName(d).Equals("__DEFAULT", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var folder in allFolders)
                        {
                            // Check if this folder contains a manifest with matching UUID
                            var manifestFiles = Directory.GetFiles(folder, "manifest.json", SearchOption.AllDirectories);

                            if (manifestFiles.Length > 0)
                            {
                                try
                                {
                                    var manifestJson = await File.ReadAllTextAsync(manifestFiles[0]);
                                    using var doc = JsonDocument.Parse(manifestJson, new JsonDocumentOptions
                                    {
                                        AllowTrailingCommas = true,
                                        CommentHandling = JsonCommentHandling.Skip
                                    });

                                    if (doc.RootElement.TryGetProperty("header", out var header) &&
                                        header.TryGetProperty("uuid", out var uuidElement))
                                    {
                                        var folderUuid = uuidElement.GetString();

                                        if (string.Equals(folderUuid, uuid, StringComparison.OrdinalIgnoreCase))
                                        {
                                            // This is the folder we want to delete
                                            Directory.Delete(folder, true);
                                            deletedCount++;
                                            Trace.WriteLine($"  ✓ Deleted: {Path.GetFileName(folder)}");
                                            break; // Found and deleted, move to next UUID
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Trace.WriteLine($"  ⚠ Error checking folder {Path.GetFileName(folder)}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"  ✗ Error deleting preset {uuid}: {ex.Message}");
                    }
                }

                Trace.WriteLine($"✓ Deleted {deletedCount}/{uuidsToDelete.Count} preset folders");
            }

            // STEP 3: Delete API cache
            if (File.Exists(_apiCachePath))
            {
                File.Delete(_apiCachePath);
                Trace.WriteLine("✓ Deleted API cache - will fetch fresh data");
            }

            // STEP 4: Clear download statuses and queue
            lock (_downloadStatusLock)
            {
                _downloadStatuses.Clear();
            }
            _downloadQueue.Clear();

            // STEP 5: Reload everything (this will fetch fresh API data since cache was deleted)
            _apiPresets = null;
            _localPresets = null;

            await LoadApiDataAsync();
            await LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            // Hide loading panel
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;

            Trace.WriteLine("✓ Reset completed successfully");
            Trace.WriteLine("✓ __DEFAULT preset preserved (not deleted)");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"✗ Error during reset: {ex.Message}");

            // Ensure UI is restored even on error
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;
        }
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(false, hWnd);

        if (path != null)
        {
            Trace.WriteLine($"✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Trace.WriteLine("✗ User cancelled or selected invalid path");
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
            Trace.WriteLine("⚠⚠⚠ GAME VERSION CHANGED - WIPING CACHE ⚠⚠⚠");
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

        // Display
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

            Trace.WriteLine($"Cache location: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"✓ Cache established");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"✗ Failed to create cache: {ex.Message}");
            return null;
        }
    }

    private void WipeEntireCache()
    {
        try
        {
            if (Directory.Exists(_cacheFolder))
            {
                Trace.WriteLine($"Deleting entire cache folder: {_cacheFolder}");
                Directory.Delete(_cacheFolder, true);
                Trace.WriteLine("✓ Cache wiped successfully");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error wiping cache: {ex.Message}");
        }
    }

    private async Task LoadApiDataAsync()
    {
        try
        {
            string jsonData = null;
            bool loadedFromCache = false;

            // Check if cache exists and is valid
            if (File.Exists(_apiCachePath))
            {
                Trace.WriteLine("✓ Loading API data from cache...");
                try
                {
                    jsonData = await File.ReadAllTextAsync(_apiCachePath);

                    if (!string.IsNullOrWhiteSpace(jsonData))
                    {
                        // Parse ONCE and validate
                        var parsedPresets = ParseApiData(jsonData);
                        if (parsedPresets != null && parsedPresets.Count > 0)
                        {
                            _apiPresets = parsedPresets;  // ✅ Use the already-parsed result
                            loadedFromCache = true;
                            Trace.WriteLine($"✓ Cache is valid with {_apiPresets.Count} presets");
                        }
                        else
                        {
                            Trace.WriteLine("⚠ Cache exists but is empty or invalid - will fetch fresh data");
                            jsonData = null;
                        }
                    }
                    else
                    {
                        Trace.WriteLine("⚠ Cache file is empty - will fetch fresh data");
                        jsonData = null;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠ Error reading/parsing cache: {ex.Message} - will fetch fresh data");
                    jsonData = null;
                    loadedFromCache = false;
                }
            }

            // If no valid cache, fetch from API
            if (!loadedFromCache)
            {
                Trace.WriteLine("Fetching API data from server...");
                jsonData = await FetchApiDataAsync();

                if (jsonData != null && !string.IsNullOrWhiteSpace(jsonData))
                {
                    // Parse ONCE and validate
                    var parsedPresets = ParseApiData(jsonData);
                    if (parsedPresets != null && parsedPresets.Count > 0)
                    {
                        _apiPresets = parsedPresets;  // ✅ Use the already-parsed result

                        // Save to cache
                        try
                        {
                            await File.WriteAllTextAsync(_apiCachePath, jsonData);
                            Trace.WriteLine("✓ API data cached successfully");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"⚠ Failed to save cache: {ex.Message}");
                        }
                    }
                    else
                    {
                        Trace.WriteLine("⚠ Fetched data is empty or invalid - not caching");
                        _apiPresets = new List<ApiPresetData>();
                    }
                }
                else
                {
                    Trace.WriteLine("⚠ Failed to fetch API data and no valid cache available");
                    _apiPresets = new List<ApiPresetData>();
                }
            }

            Trace.WriteLine($"✓ Loaded {_apiPresets?.Count ?? 0} presets total");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in LoadApiDataAsync: {ex.Message}");
            _apiPresets = new List<ApiPresetData>();
        }
    }

    private async Task<string> FetchApiDataAsync()
    {
        try
        {
            var client = Helpers.SharedHttpClient;
            var response = await client.GetAsync("https://bedrock.graphics/api");

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"⚠ API returned status code: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                Trace.WriteLine("⚠ API returned empty response");
                return null;
            }

            return content;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Trace.WriteLine("⚠ API request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"⚠ Error fetching API data: {ex.Message}");
            return null;
        }
    }

    private List<ApiPresetData> ParseApiData(string jsonData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                Trace.WriteLine("⚠ Cannot parse null or empty JSON data");
                return new List<ApiPresetData>();
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var presets = JsonSerializer.Deserialize<List<ApiPresetData>>(jsonData, options);
            return presets ?? new List<ApiPresetData>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error parsing API data: {ex.Message}");
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
                Trace.WriteLine("⚠ Cache folder doesn't exist - no local presets");
                return;
            }

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
                    Trace.WriteLine($"✓ Loaded local preset: {localPreset.Name} (UUID: {localPreset.Uuid})");
                }
            }

            Trace.WriteLine($"✓ Loaded {_localPresets.Count} local presets");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading local presets: {ex.Message}");
        }
    }

    private async Task<LocalPresetData> ParseLocalPresetAsync(string presetFolder)
    {
        try
        {
            var manifestFiles = Directory.GetFiles(presetFolder, "manifest.json", SearchOption.AllDirectories);

            if (manifestFiles.Length == 0)
            {
                Trace.WriteLine($"No manifest found in: {presetFolder}");
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
                Trace.WriteLine($"⚠ No UUID in manifest: {presetFolder}");
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
                FileHashes = presetHashes
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error parsing local preset {presetFolder}: {ex.Message}");
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
                Trace.WriteLine($"Error loading icon {iconPath}: {ex.Message}");
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
                Trace.WriteLine("⚠ WARNING: _apiPresets is null in DisplayPresetsAsync!");
                _apiPresets = new List<ApiPresetData>();
            }

            if (_localPresets == null)
            {
                Trace.WriteLine("⚠ WARNING: _localPresets is null in DisplayPresetsAsync!");
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
                    seenUuids.Add(apiPreset.Uuid);

                    if (_localPresets != null && _localPresets.TryGetValue(apiPreset.Uuid, out var localPreset))
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
                            FileHashes = localPreset.FileHashes
                        });
                    }
                }
            }

            // Sort each list alphabetically
            downloadedPresets = downloadedPresets
                .OrderBy(p => p.Name, Comparer<string>.Create(SmartPresetSorter.ComparePresetNames))
                .ToList();

            notDownloadedPresets = notDownloadedPresets
                .OrderBy(p => p.Name, Comparer<string>.Create(SmartPresetSorter.ComparePresetNames))
                .ToList();

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

                int apiCount = _apiPresets?.Count ?? 0;
                int localCount = _localPresets?.Count ?? 0;

                Trace.WriteLine($"📊 Empty state triggered - API: {apiCount}, Local: {localCount}");

                if (apiCount == 0 && localCount == 0)
                {
                    // Check if we actually tried to load from API
                    bool apiCacheExists = File.Exists(_apiCachePath);

                    if (apiCacheExists)
                    {
                        // Cache exists but is empty/corrupt
                        EmptyStateText.Text = "No presets available. The preset list may be corrupted - try clicking the Refresh button.";
                        Trace.WriteLine("⚠ Empty state: Cache exists but no presets loaded (possible corruption)");
                    }
                    else
                    {
                        // No cache, probably offline
                        EmptyStateText.Text = "No presets available. An internet connection is required to load BetterRTX presets.";
                        Trace.WriteLine("⚠ Empty state: No cache and no presets (offline?)");
                    }
                }
                else
                {
                    // We have data but nothing to display (edge case)
                    EmptyStateText.Text = "No BetterRTX presets could be displayed. Try clicking the Refresh button.";
                    Trace.WriteLine($"⚠ Empty state: API={apiCount}, Local={localCount} but no displayable presets (parsing issue?)");
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error displaying presets: {ex.Message}");
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
            FileHashes = presetHashes
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

            // Compare ALL hashes
            if (isDownloaded && displayPreset.FileHashes != null &&
                AreHashesMatching(currentInstalledHashes, displayPreset.FileHashes))
            {
                isCurrent = true;
            }
        }


        if (isCurrent)
        {
            name += " (Current)";
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
                FontSize = 36,
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
            // Check download status with lock
            DownloadStatus status;
            lock (_downloadStatusLock)
            {
                status = _downloadStatuses.TryGetValue(uuid, out var s) ? s : DownloadStatus.NotDownloaded;
            }

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
        lock (_downloadStatusLock)
        {
            if (_downloadStatuses.ContainsKey(uuid))
            {
                Trace.WriteLine($"⚠ Already queued or downloading: {name}");
                return;
            }

            Trace.WriteLine($"➕ Queued download: {name}");

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
        Trace.WriteLine("▶ Starting download queue processor");

        try
        {
            while (_downloadQueue.Count > 0)
            {
                var item = _downloadQueue.Dequeue();
                Trace.WriteLine($"🔽 Processing download: {item.Name} (Queue: {_downloadQueue.Count} remaining)");

                // Update status to downloading
                lock (_downloadStatusLock)
                {
                    _downloadStatuses[item.Uuid] = DownloadStatus.Downloading;
                }
                await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

                // Wait a moment before starting (helps with slow API)
                await Task.Delay(2000);

                // Download
                var success = await DownloadPresetAsync(item.Uuid);

                if (success)
                {
                    Trace.WriteLine($"✓ Download complete: {item.Name}");

                    // Mark as downloaded and remove from status tracking
                    lock (_downloadStatusLock)
                    {
                        _downloadStatuses.Remove(item.Uuid);
                    }

                    // Reload local presets and refresh UI
                    await LoadLocalPresetsAsync();
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }
                else
                {
                    Trace.WriteLine($"✗ Download failed: {item.Name}");

                    // Remove from status (will show download button again)
                    lock (_downloadStatusLock)
                    {
                        _downloadStatuses.Remove(item.Uuid);
                    }
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }

                // Wait between downloads (be nice to the API)
                if (_downloadQueue.Count > 0)
                {
                    Trace.WriteLine("⏱ Waiting 3 seconds before next download...");
                    await Task.Delay(3000);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"✗ Error in download queue processor: {ex.Message}");
        }
        finally
        {
            _isProcessingQueue = false;
            Trace.WriteLine("⏹ Download queue processor stopped");
        }
    }

    private async Task<bool> DownloadPresetAsync(string uuid)
    {
        try
        {
            var url = $"https://bedrock.graphics/pack/{uuid}/release";
            Trace.WriteLine($"Downloading from: {url}");

            var (success, downloadedPath) = await Helpers.Download(
                url,
                cancellationToken: CancellationToken.None,
                httpClient: _betterRtxHttpClient
            );

            if (!success || string.IsNullOrEmpty(downloadedPath))
            {
                Trace.WriteLine($"✗ Download failed");
                return false;
            }

            Trace.WriteLine($"✓ Downloaded to: {downloadedPath}");

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
                            Trace.WriteLine($"Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            Trace.WriteLine($"✓ Extracted to: {destinationFolder}");

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
            Trace.WriteLine($"✗ Error downloading {uuid}: {ex.Message}");
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
                Trace.WriteLine($"Error applying preset: {ex.Message}");
            }
        }
    }

    private async Task<bool> ApplyPresetAsync(LocalPresetData preset)
    {
        try
        {
            Trace.WriteLine($"=== APPLYING PRESET: {preset.Name} ===");

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
                        Trace.WriteLine($"  ✓ Cached: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"  ✗ Error caching {fileName}: {ex.Message}");
                    }
                }
            }

            var success = await ReplaceFilesWithElevation(filesToApply);
            return success;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in ApplyPresetAsync: {ex.Message}");
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
            Trace.WriteLine($"Error in ReplaceFilesWithElevation: {ex.Message}");
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
            Trace.WriteLine($"Error computing hash: {ex.Message}");
            return null;
        }
    }

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
                    Trace.WriteLine($"  📊 {fileName}: {hash.Substring(0, 8)}...");
                }
            }
        }

        Trace.WriteLine($"📊 Current game has {hashes.Count}/{CoreRTXFiles.Length} Core RTX files");
        return hashes;
    }

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

    private bool AreHashesMatching(Dictionary<string, string> currentHashes, Dictionary<string, string> presetHashes)
    {
        if (currentHashes == null || presetHashes == null)
        {
            Trace.WriteLine("⚠ Cannot compare - one or both hash sets are null");
            return false;
        }

        if (currentHashes.Count == 0 || presetHashes.Count == 0)
        {
            Trace.WriteLine("⚠ Cannot compare - one or both hash sets are empty");
            return false;
        }

        // Find files present in BOTH
        var commonFiles = currentHashes.Keys.Intersect(presetHashes.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        if (commonFiles.Count == 0)
        {
            Trace.WriteLine("⚠ No common files to compare");
            return false;
        }

        Trace.WriteLine($"🔍 Comparing {commonFiles.Count} common files:");

        // ALL common files must match
        foreach (var fileName in commonFiles)
        {
            var currentHash = currentHashes[fileName];
            var presetHash = presetHashes[fileName];

            if (currentHash != presetHash)
            {
                Trace.WriteLine($"  ✗ {fileName}: MISMATCH");
                return false;
            }
            else
            {
                Trace.WriteLine($"  ✓ {fileName}: Match");
            }
        }

        Trace.WriteLine("  ✓✓✓ ALL common files match!");
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




/// <summary>
/// Smart preset sorter: A-Z alphabetically, but version numbers in descending order (9-1)
/// </summary>
public static class SmartPresetSorter
{
    /// <summary>
    /// Compares two preset names with smart version sorting.
    /// Examples:
    ///   "BetterRTX 1.4.4" comes before "BetterRTX 1.2.0"
    ///   "Pack 10" comes before "Pack 2"
    ///   "Alpha Test" comes before "Beta Test" (normal A-Z)
    /// </summary>
    public static int ComparePresetNames(string name1, string name2)
    {
        // Handle null/empty cases
        if (name1 == name2) return 0;
        if (string.IsNullOrEmpty(name1)) return 1;
        if (string.IsNullOrEmpty(name2)) return -1;

        try
        {
            // Split both names into segments (alternating text and numbers)
            var segments1 = SplitIntoSegments(name1);
            var segments2 = SplitIntoSegments(name2);

            // Compare segment by segment
            int minLength = Math.Min(segments1.Count, segments2.Count);

            for (int i = 0; i < minLength; i++)
            {
                var seg1 = segments1[i];
                var seg2 = segments2[i];

                // If both segments are numeric, compare numerically IN REVERSE (9→1)
                if (seg1.IsNumeric && seg2.IsNumeric)
                {
                    int result = seg2.NumericValue.CompareTo(seg1.NumericValue); // Reversed!
                    if (result != 0) return result;
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
                // Both are text - compare with culture-aware comparison for non-ASCII
                else
                {
                    int result = string.Compare(seg1.Text, seg2.Text, StringComparison.CurrentCultureIgnoreCase);
                    if (result != 0) return result;
                }
            }

            // If all segments matched, shorter name comes first
            return segments1.Count.CompareTo(segments2.Count);
        }
        catch (Exception ex)
        {
            // If anything goes wrong during parsing/comparison, fall back to simple ordinal comparison
            Trace.WriteLine($"⚠ SmartPresetSorter error, falling back to simple sort: {ex.Message}");
            return string.Compare(name1, name2, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<Segment> SplitIntoSegments(string name)
    {
        var segments = new List<Segment>();
        var currentText = new System.Text.StringBuilder();
        var currentNumber = new System.Text.StringBuilder();
        bool inNumber = false;

        foreach (char c in name)
        {
            if (char.IsDigit(c))
            {
                if (!inNumber && currentText.Length > 0)
                {
                    // Switching from text to number - save text segment
                    segments.Add(new Segment { Text = currentText.ToString(), IsNumeric = false });
                    currentText.Clear();
                }
                currentNumber.Append(c);
                inNumber = true;
            }
            else
            {
                if (inNumber && currentNumber.Length > 0)
                {
                    // Switching from number to text - save number segment
                    segments.Add(CreateNumericSegment(currentNumber.ToString()));
                    currentNumber.Clear();
                }
                currentText.Append(c);
                inNumber = false;
            }
        }

        // Add final remaining segment
        if (currentNumber.Length > 0)
        {
            segments.Add(CreateNumericSegment(currentNumber.ToString()));
        }
        else if (currentText.Length > 0)
        {
            segments.Add(new Segment { Text = currentText.ToString(), IsNumeric = false });
        }

        return segments;
    }

    private static Segment CreateNumericSegment(string numberText)
    {
        // Try parsing as decimal (handles very long numbers better than long)
        // If it overflows decimal (unlikely for version numbers), treat as text
        if (decimal.TryParse(numberText, out decimal value))
        {
            return new Segment
            {
                Text = numberText,
                IsNumeric = true,
                NumericValue = value
            };
        }
        else
        {
            // Number too large to parse - treat as text (rare edge case)
            Trace.WriteLine($"⚠ Number too large to parse, treating as text: {numberText}");
            return new Segment
            {
                Text = numberText,
                IsNumeric = false
            };
        }
    }

    private class Segment
    {
        public string Text { get; set; }
        public bool IsNumeric { get; set; }
        public decimal NumericValue { get; set; }
    }
}





public static class GameVersionDetector
{
    private const string CONFIG_HASH_KEY = "MinecraftConfigHash";

    /// <summary>
    /// Detects if game version has changed by comparing MicrosoftGame.Config hash.
    /// Returns true if version changed OR unable to determine (safe default).
    /// </summary>
    public static async Task<bool> HasGameVersionChanged(string minecraftInstallPath)
    {
        try
        {
            Trace.WriteLine("=== GAME VERSION DETECTION START ===");

            if (string.IsNullOrEmpty(minecraftInstallPath) || !Directory.Exists(minecraftInstallPath))
            {
                Trace.WriteLine("⚠ Invalid Minecraft install path - INVALIDATING CACHE (safe default)");
                Trace.WriteLine("=== GAME VERSION DETECTION END (invalid path) ===");
                return true; // Invalidate cache when uncertain
            }

            // Find MicrosoftGame.Config file (max 2 levels deep)
            var configPath = FindFileRecursively(minecraftInstallPath, "MicrosoftGame.Config", 2);

            // Get stored hash
            var settings = ApplicationData.Current.LocalSettings;
            var storedConfigHash = settings.Values[CONFIG_HASH_KEY] as string;

            Trace.WriteLine($"💾 Stored Config hash: {storedConfigHash ?? "NULL (first run or cleared)"}");

            // CASE 1: Config file not found
            if (string.IsNullOrEmpty(configPath))
            {
                Trace.WriteLine("⚠ MicrosoftGame.Config not found in game directory");

                if (!string.IsNullOrEmpty(storedConfigHash))
                {
                    // Had hash before, file now missing - INVALIDATE
                    Trace.WriteLine("🔥 CONFIG FILE DISAPPEARED - CACHE INVALIDATION!");
                    settings.Values.Remove(CONFIG_HASH_KEY);
                    Trace.WriteLine("💾 Cleared stored config hash");
                    Trace.WriteLine("=== GAME VERSION DETECTION END (file disappeared) ===");
                    return true;
                }
                else
                {
                    // Never had hash, still can't find file - INVALIDATE (safe default)
                    Trace.WriteLine("🔥 Unable to locate config file - CACHE INVALIDATION (safe default)");
                    Trace.WriteLine("=== GAME VERSION DETECTION END (unable to determine) ===");
                    return true;
                }
            }

            // CASE 2: Config file exists - compute its hash
            var currentConfigHash = ComputeFileHash(configPath);
            Trace.WriteLine($"📊 Current Config hash: {currentConfigHash ?? "NULL (computation failed)"}");

            if (string.IsNullOrEmpty(currentConfigHash))
            {
                // File exists but can't compute hash - INVALIDATE (safe default)
                Trace.WriteLine("🔥 Failed to compute config hash - CACHE INVALIDATION (safe default)");
                Trace.WriteLine("=== GAME VERSION DETECTION END (hash computation failed) ===");
                return true;
            }

            // CASE 3: We have a valid current hash
            bool versionChanged = false;

            if (string.IsNullOrEmpty(storedConfigHash))
            {
                // First run - no stored hash yet
                Trace.WriteLine("✓ First run - storing initial config hash (not a version change)");
                versionChanged = false;
            }
            else if (currentConfigHash != storedConfigHash)
            {
                // Hash changed - version updated
                Trace.WriteLine("🔥 CONFIG HASH CHANGED - GAME VERSION UPDATED!");
                Trace.WriteLine($"   Old: {storedConfigHash.Substring(0, 16)}...");
                Trace.WriteLine($"   New: {currentConfigHash.Substring(0, 16)}...");
                versionChanged = true;
            }
            else
            {
                // Hash matches - no change
                Trace.WriteLine("✓ Config hash matches - no version change");
                versionChanged = false;
            }

            // Always update stored hash with current value
            settings.Values[CONFIG_HASH_KEY] = currentConfigHash;
            Trace.WriteLine("💾 Saved current config hash");

            Trace.WriteLine($"=== GAME VERSION DETECTION END (changed: {versionChanged}) ===");
            return versionChanged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"✗ EXCEPTION in version detection: {ex.Message}");
            Trace.WriteLine("🔥 Exception occurred - CACHE INVALIDATION (safe default)");
            Trace.WriteLine("=== GAME VERSION DETECTION END (exception) ===");
            return true; // Invalidate cache on any error (safe default)
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
            Trace.WriteLine($"Error searching for {fileName}: {ex.Message}");
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
            Trace.WriteLine($"✓ Found {fileName} at: {targetPath}");
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
                Trace.WriteLine($"Error accessing subdirectory: {ex.Message}");
            }
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
            Trace.WriteLine($"Error computing hash for {filePath}: {ex.Message}");
            return null;
        }
    }

    public static void ClearStoredVersionHashes()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values.Remove(CONFIG_HASH_KEY);
            Trace.WriteLine("✓ Cleared stored version hash");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error clearing version hash: {ex.Message}");
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
