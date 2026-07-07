using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.DLSSBrowser;

// TODO: Upgrade DLSS Swapper to pull dlls from a third party API like BetterRTX Manager.
// Keep the current manual import pipeline, just add a new potential Source, list dlls, etc...
public sealed partial class DLSSSwapperWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _isClosing;

    private string _gameDllPath = string.Empty;
    private string _cacheFolder = string.Empty;
    private string? _currentInstalledVersion;
    private CancellationTokenSource? _scanCancellationTokenSource;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    public DLSSSwapperWindow()
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

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "vrtx.dlss.ico"));

        this.Closed += DLSSSwapperWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += DLSSSwapperWindow_Loaded;
    }
    private async void DLSSSwapperWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= DLSSSwapperWindow_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarArea);

            var text = Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft Release";
            WindowTitle.Text = $"Swap DLSS version for {text}";

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSSSwapper] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void DLSSSwapperWindow_Closed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= DLSSSwapperWindow_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= DLSSSwapperWindow_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }
    private void PopulateDLSSAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.DLSSAnnouncements);
        if (items is null) return;
        foreach (var item in items)
            DLSSAnnouncementsPanel.Children.Add(new PsaCard(item));
    }
    // ======================= Initialization =======================
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
                Trace.WriteLine($"[DLSS] ✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine($"[DLSS] ⚠ Cache became invalid, clearing");
                    if (isPreview)
                        Persistent.MinecraftPreviewInstallPath = null;
                    else
                        Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionButton.Visibility = Visibility.Visible;
                });

                Trace.WriteLine("[DLSS] Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[DLSS] System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            if (minecraftPath != null)
                await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        _gameDllPath = Path.Combine(minecraftPath, "nvngx_dlss.dll");

        // Establish cache folder
        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
        {
            StatusMessage = "Could not establish cache folder";
            this.Close();
            return;
        }
        _cacheFolder = cacheFolder;

        bool gameDllExists = File.Exists(_gameDllPath);

        if (!gameDllExists)
        {
            Trace.WriteLine($"[DLSS] ⚠ DLSS file not found at: {_gameDllPath}");

            var cachedDlls = Directory.GetFiles(_cacheFolder, "*.dll")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (cachedDlls.Count > 0)
            {
                var repairDll = cachedDlls.First();
                Trace.WriteLine($"[DLSS] 🔧 Attempting to repair with: {repairDll}");

                var repairSuccess = await ReplaceDllWithElevation(repairDll);

                if (repairSuccess)
                {
                    Trace.WriteLine("[DLSS] ✓ Game repaired successfully");
                    gameDllExists = true;
                    await CopyCurrentDllToCache();
                }
                else
                {
                    Trace.WriteLine("[DLSS] ⚠ User cancelled UAC or repair failed - continuing anyway");
                }
            }
            else
            {
                Trace.WriteLine("[DLSS] ⚠ No cached DLLs available - user must import one");
            }
        }
        else
        {
            await CopyCurrentDllToCache();
        }

        await LoadDllsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        DllSelectionPanel.Visibility = Visibility.Visible;
        PopulateDLSSAnnouncements();
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("[DLSS] Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = TunerVariables.Persistent.IsTargetingPreview;
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (path != null)
        {
            Trace.WriteLine($"[DLSS] ✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Trace.WriteLine("[DLSS] ✗ User cancelled or selected invalid path");
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    private string? EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "DLSS_Cache");

            Trace.WriteLine($"[DLSS] Creating DLSS cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"[DLSS] ✓ DLSS cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] ✗ Failed to create DLSS cache: {ex.Message}");
            return null;
        }
    }

    private async Task CopyCurrentDllToCache()
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(_gameDllPath);

            _currentInstalledVersion = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";

            var cacheFileName = $"{_currentInstalledVersion}.dll";
            var cachePath = Path.Combine(_cacheFolder, cacheFileName);

            await Task.Run(() => File.Copy(_gameDllPath, cachePath, true));

            Trace.WriteLine($"[DLSS] Copied current DLSS {_currentInstalledVersion} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error copying current DLL to cache: {ex.Message}");
            _currentInstalledVersion = "Unknown";
        }
    }

    private async Task LoadDllsAsync(bool IsCalledByAddDLSSVersion = false)
    {
        try
        {
            if (!IsCalledByAddDLSSVersion)
            {
                await CleanupOldDllsAsync();
            }

            DllListContainer.Children.Clear();

            var dllFiles = Directory.GetFiles(_cacheFolder, "*.dll")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (dllFiles.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = "No DLSS versions imported yet. Download nvngx_dlss.dll files and import them here.";
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                var addedVersions = new HashSet<string>();

                foreach (var dllPath in dllFiles)
                {
                    var dllData = await ParseDllAsync(dllPath);
                    if (dllData != null && !addedVersions.Contains(dllData.Version))
                    {
                        addedVersions.Add(dllData.Version);
                        var dllButton = CreateDllButton(dllData);
                        DllListContainer.Children.Add(dllButton);
                    }
                }
            }

            var addButton = CreateAddDllButton();
            DllListContainer.Children.Add(addButton);

            Trace.WriteLine("[DLSS] DLL loading complete");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] EXCEPTION in LoadDllsAsync: {ex}");
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error loading DLLs: {ex.Message}";
        }
    }

    private Button CreateDllButton(DllData dll)
    {
        bool isCurrentVersion = dll.Version == _currentInstalledVersion;

        var normalizedVersion = dll.Version.Replace(",", ".");
        bool isValidVersion = Version.TryParse(normalizedVersion, out var parsedVersion);
        bool isTooOld = dll.Version == "Unknown" || !isValidVersion || parsedVersion < new Version(2, 0, 0, 0);

        var button = new Button
        {
            IsEnabled = !isTooOld,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 38, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            Tag = dll,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

        if (isCurrentVersion)
        {
            button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        }

        var buttonShadow = new ThemeShadow();
        button.Shadow = buttonShadow;
        button.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                buttonShadow.Receivers.Add(ShadowReceiverGrid);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var icon = new FontIcon
        {
            Glyph = "\uF156",
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        iconBorder.Child = icon;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var displayVersion = dll.Version.Replace(",", ".");
        if (isCurrentVersion)
            displayVersion += " (Currently Installed)";

        var versionText = new TextBlock
        {
            Text = $"DLSS {displayVersion}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var pathText = new TextBlock
        {
            Text = isCurrentVersion
                ? Helpers.SanitizePathForDisplay(dll.FilePath)
                : isTooOld
                    ? "Incompatible DLSS version. Only import 2.0.0.0 or higher!\nThis version will be auto removed."
                    : "Click to swap to this version",
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(versionText);
        infoPanel.Children.Add(pathText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        if (!isCurrentVersion)
        {
            var deleteButton = new Button
            {
                Width = 40,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(16, 0, 0, 0),
                IsTextScaleFactorEnabled = false,
                Tag = dll,
            };

            var deleteIcon = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 18,
                IsTextScaleFactorEnabled = false,
            };

            deleteButton.Content = deleteIcon;
            deleteButton.Click += DeleteDllButton_Click;

            Grid.SetColumn(deleteButton, 4);
            grid.Children.Add(deleteButton);
        }

        button.Content = grid;
        button.Click += DllButton_Click;

        return button;
    }

    private async void DeleteDllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DllData dllData)
        {
            try
            {
                if (dllData.Version == _currentInstalledVersion)
                {
                    Trace.WriteLine("[DLSS] Cannot delete currently installed DLSS version");
                    return;
                }

                if (File.Exists(dllData.FilePath))
                {
                    File.Delete(dllData.FilePath);
                    Trace.WriteLine($"[DLSS] Deleted DLSS version {dllData.Version} from cache");
                    await LoadDllsAsync();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DLSS] Error deleting DLL: {ex.Message}");
            }
        }
    }

    private Button CreateAddDllButton()
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 38, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            AllowDrop = true
        };

        button.DragOver += AddDllButton_DragOver;
        button.Drop += AddDllButton_Drop;
        button.DragEnter += AddDllButton_DragEnter;
        button.DragLeave += AddDllButton_DragLeave;

        var buttonShadow = new ThemeShadow();
        button.Shadow = buttonShadow;
        button.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                buttonShadow.Receivers.Add(ShadowReceiverGrid);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var icon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        iconBorder.Child = icon;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleText = new TextBlock
        {
            Text = "Add DLSS files",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = "Drag and drop or browse for a DLSS DLL file to add (.dll or .zip with the dlls)",
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(titleText);
        infoPanel.Children.Add(descText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        var hyperlinkButton = new HyperlinkButton
        {
            Content = "Download More DLLs",
            NavigateUri = new Uri("https://www.techpowerup.com/download/nvidia-dlss-dll/"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
            FontSize = 14,
            Padding = new Thickness(18, 8, 16, 8),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            IsTextScaleFactorEnabled = false
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
        button.Click += AddDllButton_Click;

        return button;
    }

    private void AddDllButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 0.7;
    }

    private void AddDllButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 1.0;
    }

    private void AddDllButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to add DLSS files";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void AddDllButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 1.0;

        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();

                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file)
                    {
                        var extension = file.FileType.ToLower();

                        if (extension == ".dll")
                            await ProcessDllFileAsync(file.Path);
                        else if (extension == ".zip")
                            await ProcessZipFileAsync(file.Path);
                        else
                            Trace.WriteLine($"[DLSS] Skipped unsupported file type: {extension}");
                    }
                }

                await LoadDllsAsync(true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing dropped files: {ex.Message}");
        }
    }

    private async void AddDllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".dll");
            picker.FileTypeFilter.Add(".zip");

            var hWnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                if (file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    await ProcessZipFileAsync(file.Path);
                else if (file.FileType.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    await ProcessDllFileAsync(file.Path);

                await LoadDllsAsync(true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error adding file: {ex.Message}");
        }
    }

    private async Task ProcessDllFileAsync(string dllPath)
    {
        try
        {
            if (!Path.GetFileName(dllPath).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[DLSS] Skipped non-DLL file: {dllPath}");
                return;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            var cacheFileName = $"{version}.dll";
            var cachePath = Path.Combine(_cacheFolder, cacheFileName);

            await Task.Run(() => File.Copy(dllPath, cachePath, true));
            Trace.WriteLine($"[DLSS] Added DLSS {version} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing DLL {dllPath}: {ex.Message}");
        }
    }

    private async Task ProcessZipFileAsync(string zipPath)
    {
        try
        {
            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[DLSS] Skipped non-DLL file from ZIP");
                            continue;
                        }

                        try
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), entry.Name);
                            entry.ExtractToFile(tempPath, true);

                            var versionInfo = FileVersionInfo.GetVersionInfo(tempPath);
                            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
                            var cacheFileName = $"{version}.dll";
                            var cachePath = Path.Combine(_cacheFolder, cacheFileName);

                            File.Copy(tempPath, cachePath, true);
                            File.Delete(tempPath);

                            Trace.WriteLine($"[DLSS] Extracted and added DLSS {version} from ZIP");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[DLSS] Error processing {entry.FullName} from ZIP: {ex.Message}");
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing ZIP file: {ex.Message}");
        }
    }

    private async void DllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DllData dllData)
        {
            try
            {
                if (dllData.Version == _currentInstalledVersion)
                    return;

                var success = await ReplaceDllWithElevation(dllData.FilePath);

                if (success)
                {
                    OperationSuccessful = true;
                    var displayVersion = dllData.Version.Replace(",", ".");
                    StatusMessage = $"Swapped to DLSS {displayVersion}";

                    await CopyCurrentDllToCache();
                    await LoadDllsAsync();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DLSS] Error replacing DLL: {ex.Message}");
            }
        }
    }
    private Task<bool> ReplaceDllWithElevation(string sourceDllPath)
    {
        return Helpers.ReplaceFilesWithElevation(
            new List<(string, string)> { (sourceDllPath, _gameDllPath) },
            "[DLSS]",
            "dlss_dll");
    }

    private static Task<DllData?> ParseDllAsync(string dllPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            return Task.FromResult<DllData?>(new DllData { Version = version, FilePath = dllPath });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error parsing DLL {dllPath}: {ex.Message}");
            return Task.FromResult<DllData?>(null);
        }
    }

    private async Task CleanupOldDllsAsync()
    {
        await Task.Run(() =>
        {
            foreach (var dllPath in Directory.GetFiles(_cacheFolder, "*.dll"))
            {
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                    var raw = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "";
                    var normalized = raw.Replace(",", ".");

                    if (string.IsNullOrWhiteSpace(raw) ||
                        !Version.TryParse(normalized, out var parsedVersion) ||
                        parsedVersion < new Version(2, 0, 0, 0))
                    {
                        // Don't delete the currently installed version even if it's weird
                        if (!string.IsNullOrEmpty(_currentInstalledVersion) &&
                            Path.GetFileNameWithoutExtension(dllPath) == _currentInstalledVersion)
                        {
                            Trace.WriteLine($"[DLSS] Skipping cleanup of current installed version: {dllPath}");
                            continue;
                        }

                        File.Delete(dllPath);
                        Trace.WriteLine($"[DLSS] Cleaned up incompatible/unversioned DLSS from cache: {dllPath}");
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DLSS] Error during cleanup of {dllPath}: {ex.Message}");
                }
            }
        });
    }

    private class DllData
    {
        public string Version { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }
}
