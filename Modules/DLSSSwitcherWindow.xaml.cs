using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vanilla_RTX_App.BetterRTXBrowser;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Vanilla_RTX_App.DLSSBrowser;

public sealed partial class DLSSSwitcherWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;
    private string _gameDllPath;
    private string _cacheFolder;
    private string _currentInstalledVersion;
    private CancellationTokenSource _scanCancellationTokenSource;

    public bool OperationSuccessful { get; private set; } = false;
    public string StatusMessage { get; private set; } = "";

    public DLSSSwitcherWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

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
            presenter.IsAlwaysOnTop = true;
            var dpi = MainWindow.GetDpiForWindow(hWnd);
            var scaleFactor = dpi / 96.0;
            presenter.PreferredMinimumWidth = (int)(925 * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(525 * scaleFactor);
        }

        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }

        this.Activated += DLSSSwitcherWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void DLSSSwitcherWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= DLSSSwitcherWindow_Activated;

            _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SetTitleBarDragRegion();
            });

            var text = TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";
            WindowTitle.Text = $"Swap DLSS version for {text}";

            await InitializeAsync();
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
                System.Diagnostics.Debug.WriteLine($"Error setting drag region: {ex.Message}");
            }
        }
    }


    private async Task InitializeAsync()
    {
        try
        {
            // Find Minecraft installation
            var minecraftPath = await FindMinecraftInstallationAsync();

            // If null is returned, it means the delay completed and we're waiting for manual selection
            // The manual selection button will handle initialization when clicked
            if (minecraftPath == null)
            {
                System.Diagnostics.Debug.WriteLine("Waiting for manual Minecraft location selection");
                return;
            }

            _gameDllPath = Path.Combine(minecraftPath, "Content", "nvngx_dlss.dll");

            // Verify DLL exists
            if (!File.Exists(_gameDllPath))
            {
                StatusMessage = "DLSS file not found in Minecraft installation";
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

            // Copy current game DLL to cache
            await CopyCurrentDllToCache();

            // Load and display all cached DLLs
            await LoadDllsAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;
            DllSelectionPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task<string> FindMinecraftInstallationAsync()
    {
        bool isPreview = TunerVariables.Persistent.IsTargetingPreview;
        string targetFolderName = isPreview ? "Minecraft Preview for Windows" : "Minecraft for Windows";

        System.Diagnostics.Debug.WriteLine($"=== STARTING MINECRAFT SEARCH ===");
        System.Diagnostics.Debug.WriteLine($"IsPreview: {isPreview}");
        System.Diagnostics.Debug.WriteLine($"Target folder: {targetFolderName}");

        // Check if we have a cached path
        var cachedPath = isPreview ? TunerVariables.Persistent.MinecraftPreviewInstallPath : TunerVariables.Persistent.MinecraftInstallPath;

        System.Diagnostics.Debug.WriteLine($"Cached path: {cachedPath?.FullName ?? "NULL"}");

        if (cachedPath != null)
        {
            System.Diagnostics.Debug.WriteLine($"Cached directory exists: {cachedPath.Exists}");

            if (cachedPath.Exists)
            {
                var cachedExePath = Path.Combine(cachedPath.FullName, "Content", "Minecraft.Windows.exe");
                System.Diagnostics.Debug.WriteLine($"Checking for exe at: {cachedExePath}");
                System.Diagnostics.Debug.WriteLine($"Exe exists: {File.Exists(cachedExePath)}");

                if (File.Exists(cachedExePath))
                {
                    System.Diagnostics.Debug.WriteLine($"✓ Using cached Minecraft path: {cachedPath.FullName}");
                    return cachedPath.FullName;
                }
            }

            System.Diagnostics.Debug.WriteLine("✗ Cached path invalid, clearing");
            if (isPreview)
                TunerVariables.Persistent.MinecraftPreviewInstallPath = null;
            else
                TunerVariables.Persistent.MinecraftInstallPath = null;
        }

        // Try common locations first
        System.Diagnostics.Debug.WriteLine($"=== CHECKING COMMON LOCATIONS ===");

        var commonLocations = new[]
        {
     Path.Combine(@"C:\XboxGames", targetFolderName),
     Path.Combine(@"C:\Program Files\Microsoft Games", targetFolderName)
    };

        foreach (var location in commonLocations)
        {
            System.Diagnostics.Debug.WriteLine($"Checking: {location}");
            System.Diagnostics.Debug.WriteLine($"  Directory exists: {Directory.Exists(location)}");

            if (Directory.Exists(location))
            {
                var exePath = Path.Combine(location, "Content", "Minecraft.Windows.exe");
                System.Diagnostics.Debug.WriteLine($"  Checking exe: {exePath}");
                System.Diagnostics.Debug.WriteLine($"  Exe exists: {File.Exists(exePath)}");

                if (File.Exists(exePath))
                {
                    System.Diagnostics.Debug.WriteLine($"✓ Found Minecraft at: {location}");

                    // Cache the path
                    var dirInfo = new DirectoryInfo(location);
                    if (isPreview)
                        TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                    else
                        TunerVariables.Persistent.MinecraftInstallPath = dirInfo;

                    System.Diagnostics.Debug.WriteLine($"✓ Cached path set to: {dirInfo.FullName}");
                    return location;
                }
            }
        }

        // System-wide scan with cancellation support
        _scanCancellationTokenSource = new CancellationTokenSource();
        var scanTask = ScanAllDrivesAsync(targetFolderName, _scanCancellationTokenSource.Token);
        var delayTask = ShowManualSelectionButtonAfterDelay();

        var completedTask = await Task.WhenAny(scanTask, delayTask);

        if (completedTask == scanTask)
        {
            var result = await scanTask;
            _scanCancellationTokenSource?.Cancel();
            return result;
        }

        // return null and let manual selection handle it
        // DON'T await the scan unconditionally
        return null;
    }


    private async Task ShowManualSelectionButtonAfterDelay()
    {
        await Task.Delay(5000);

        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            ManualSelectionButton.Visibility = Visibility.Visible;
        });
    }

    private async Task<string> ScanAllDrivesAsync(string targetFolderName, CancellationToken cancellationToken)
    {
        System.Diagnostics.Debug.WriteLine($"=== FULL DRIVE SCAN ===");

        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
        System.Diagnostics.Debug.WriteLine($"Found {drives.Count} fixed drives");

        foreach (var drive in drives)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine("Drive scan cancelled by user");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"Scanning drive: {drive.Name}");

            try
            {
                // Check Program Files\Microsoft Games
                var programFilesPath = Path.Combine(drive.Name, "Program Files", "Microsoft Games", targetFolderName);
                System.Diagnostics.Debug.WriteLine($"  Checking: {programFilesPath}");

                if (Directory.Exists(programFilesPath))
                {
                    var exePath = Path.Combine(programFilesPath, "Content", "Minecraft.Windows.exe");
                    System.Diagnostics.Debug.WriteLine($"  Exe path: {exePath}");
                    System.Diagnostics.Debug.WriteLine($"  Exe exists: {File.Exists(exePath)}");

                    if (File.Exists(exePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"✓ Found Minecraft at: {programFilesPath}");

                        // Cache the path
                        var dirInfo = new DirectoryInfo(programFilesPath);
                        if (TunerVariables.Persistent.IsTargetingPreview)
                            TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                        else
                            TunerVariables.Persistent.MinecraftInstallPath = dirInfo;

                        return programFilesPath;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine("Drive scan cancelled by user");
                    return null;
                }

                // Check XboxGames
                var xboxGamesPath = Path.Combine(drive.Name, "XboxGames", targetFolderName);
                System.Diagnostics.Debug.WriteLine($"  Checking: {xboxGamesPath}");

                if (Directory.Exists(xboxGamesPath))
                {
                    var exePath = Path.Combine(xboxGamesPath, "Content", "Minecraft.Windows.exe");
                    System.Diagnostics.Debug.WriteLine($"  Exe path: {exePath}");
                    System.Diagnostics.Debug.WriteLine($"  Exe exists: {File.Exists(exePath)}");

                    if (File.Exists(exePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"✓ Found Minecraft at: {xboxGamesPath}");

                        // Cache the path
                        var dirInfo = new DirectoryInfo(xboxGamesPath);
                        if (TunerVariables.Persistent.IsTargetingPreview)
                            TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                        else
                            TunerVariables.Persistent.MinecraftInstallPath = dirInfo;

                        return xboxGamesPath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗ Error scanning drive {drive.Name}: {ex.Message}");
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine("✗ Drive scan cancelled before manual selection");
            return null;
        }

        // If scan failed, prompt user to locate manually
        System.Diagnostics.Debug.WriteLine("=== MANUAL LOCATION REQUIRED ===");
        var manualPath = await LocateMinecraftManually();

        System.Diagnostics.Debug.WriteLine($"Manual path result: {manualPath ?? "NULL"}");
        return manualPath;
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Manual selection button clicked - cancelling scan");
        _scanCancellationTokenSource?.Cancel();

        var path = await LocateMinecraftManually();

        if (path != null)
        {
            // Close loading and reinitialize with found path
            _gameDllPath = Path.Combine(path, "Content", "nvngx_dlss.dll");

            if (!File.Exists(_gameDllPath))
            {
                StatusMessage = "DLSS file not found in selected Minecraft installation";
                this.Close();
                return;
            }

            _cacheFolder = EstablishCacheFolder();
            if (_cacheFolder == null)
            {
                StatusMessage = "Could not establish cache folder";
                this.Close();
                return;
            }

            await CopyCurrentDllToCache();
            await LoadDllsAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;
            DllSelectionPanel.Visibility = Visibility.Visible;
        }
        else
        {
            StatusMessage = "No valid Minecraft installation selected";
            this.Close();
        }
    }

    private async Task<string> LocateMinecraftManually()
    {
        System.Diagnostics.Debug.WriteLine("Opening folder picker...");

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");

        var hWnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hWnd);

        var folder = await picker.PickSingleFolderAsync();

        System.Diagnostics.Debug.WriteLine($"User selected folder: {folder?.Path ?? "NULL"}");

        if (folder != null)
        {
            // Check if they selected the root folder or Content folder
            var exePath = Path.Combine(folder.Path, "Content", "Minecraft.Windows.exe");
            var isContentFolder = folder.Path.EndsWith("Content", StringComparison.OrdinalIgnoreCase);

            if (isContentFolder)
            {
                // User selected Content folder, go up one level
                var parentPath = Directory.GetParent(folder.Path)?.FullName;
                if (parentPath != null)
                {
                    exePath = Path.Combine(parentPath, "Content", "Minecraft.Windows.exe");
                    if (File.Exists(exePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"✓ Valid Minecraft folder selected (from Content): {parentPath}");

                        var dirInfo = new DirectoryInfo(parentPath);
                        if (TunerVariables.Persistent.IsTargetingPreview)
                            TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                        else
                            TunerVariables.Persistent.MinecraftInstallPath = dirInfo;

                        return parentPath;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"Checking for exe at: {exePath}");
            System.Diagnostics.Debug.WriteLine($"Exe exists: {File.Exists(exePath)}");

            if (File.Exists(exePath))
            {
                System.Diagnostics.Debug.WriteLine($"✓ Valid Minecraft folder selected: {folder.Path}");

                var dirInfo = new DirectoryInfo(folder.Path);
                if (TunerVariables.Persistent.IsTargetingPreview)
                {
                    TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                    System.Diagnostics.Debug.WriteLine($"✓ Cached to MinecraftPreviewInstallPath: {dirInfo.FullName}");
                }
                else
                {
                    TunerVariables.Persistent.MinecraftInstallPath = dirInfo;
                    System.Diagnostics.Debug.WriteLine($"✓ Cached to MinecraftInstallPath: {dirInfo.FullName}");
                }

                return folder.Path;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"✗ Selected folder does not contain Content\\Minecraft.Windows.exe: {folder.Path}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("✗ User cancelled folder picker");
        }

        return null;
    }

    private string EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "DLSS_Cache");

            System.Diagnostics.Debug.WriteLine($"Creating DLSS cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            System.Diagnostics.Debug.WriteLine($"✓ DLSS cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ Failed to create DLSS cache: {ex.Message}");
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

            // Always overwrite to ensure cache is up-to-date
            await Task.Run(() => File.Copy(_gameDllPath, cachePath, true));

            System.Diagnostics.Debug.WriteLine($"Copied current DLSS {_currentInstalledVersion} to cache");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error copying current DLL to cache: {ex.Message}");
            _currentInstalledVersion = "Unknown";
        }
    }

    private async Task LoadDllsAsync()
    {
        try
        {
            DllListContainer.Children.Clear();

            // Get all DLL files from cache
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

                // Use HashSet to track versions we've already added
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

            // Add "Add DLL" button at the end
            var addButton = CreateAddDllButton();
            DllListContainer.Children.Add(addButton);

            System.Diagnostics.Debug.WriteLine("DLL loading complete");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EXCEPTION in LoadDllsAsync: {ex}");
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error loading DLLs: {ex.Message}";
        }
    }

    private Button CreateDllButton(DllData dll)
    {
        bool isCurrentVersion = dll.Version == _currentInstalledVersion;

        var button = new Button
        {
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

        // DLL info
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        // Sanitize version for display: replace commas with dots
        var displayVersion = dll.Version.Replace(",", ".");

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
            Text = Helpers.SanitizePathForDisplay(dll.FilePath),
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

        // Delete button - only show if NOT current version
        if (!isCurrentVersion)
        {
            var deleteButton = new Button
            {
                Width = 36,
                Height = 36,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(16, 0, 0, 0),
                IsTextScaleFactorEnabled = false,
                Tag = dll
            };

            var deleteIcon = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 16,
                IsTextScaleFactorEnabled = false
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
                // Don't allow deleting the currently installed version
                if (dllData.Version == _currentInstalledVersion)
                {
                    System.Diagnostics.Debug.WriteLine("Cannot delete currently installed DLSS version");
                    return;
                }

                // Delete the file
                if (File.Exists(dllData.FilePath))
                {
                    File.Delete(dllData.FilePath);
                    System.Diagnostics.Debug.WriteLine($"Deleted DLSS version {dllData.Version} from cache");

                    // Refresh list
                    await LoadDllsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting DLL: {ex.Message}");
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
            AllowDrop = true  // Enable drop
        };

        // Add drag-drop event handlers
        button.DragOver += AddDllButton_DragOver;
        button.Drop += AddDllButton_Drop;
        button.DragEnter += AddDllButton_DragEnter;
        button.DragLeave += AddDllButton_DragLeave;

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

        // Info panel
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleText = new TextBlock
        {
            Text = "Add DLSS Version",
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

        // Download link badge
        var hyperlinkButton = new HyperlinkButton
        {
            Content = "Download More DLLs",
            NavigateUri = new Uri("https://www.techpowerup.com/download/nvidia-dlss-dll/"),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16, 6, 16, 6),
            IsTextScaleFactorEnabled = false
        };

        Grid.SetColumn(hyperlinkButton, 4);
        grid.Children.Add(hyperlinkButton);

        button.Content = grid;
        button.Click += AddDllButton_Click;

        return button;
    }
    private void AddDllButton_DragEnter(object sender, DragEventArgs e)
    {
        // Visual feedback when dragging over
        if (sender is Button button)
        {
            button.Opacity = 0.7;
        }
    }

    private void AddDllButton_DragLeave(object sender, DragEventArgs e)
    {
        // Restore opacity when leaving
        if (sender is Button button)
        {
            button.Opacity = 1.0;
        }
    }

    private void AddDllButton_DragOver(object sender, DragEventArgs e)
    {
        // Check if the dragged items contain files
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
        // Restore opacity
        if (sender is Button button)
        {
            button.Opacity = 1.0;
        }

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
                        {
                            await ProcessDllFileAsync(file.Path);
                        }
                        else if (extension == ".zip")
                        {
                            await ProcessZipFileAsync(file.Path);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipped unsupported file type: {extension}");
                        }
                    }
                }

                // Refresh list after processing all dropped files
                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing dropped files: {ex.Message}");
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
                {
                    await ProcessZipFileAsync(file.Path);
                }
                else if (file.FileType.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessDllFileAsync(file.Path);
                }

                // Refresh list after processing
                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error adding file: {ex.Message}");
        }
    }

    private async Task ProcessDllFileAsync(string dllPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            var cacheFileName = $"{version}.dll";
            var cachePath = Path.Combine(_cacheFolder, cacheFileName);

            await Task.Run(() => File.Copy(dllPath, cachePath, true));
            System.Diagnostics.Debug.WriteLine($"Added DLSS {version} to cache");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing DLL {dllPath}: {ex.Message}");
        }
    }

    private async Task ProcessZipFileAsync(string zipPath)
    {
        try
        {
            await Task.Run(() =>
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Check if it's a DLL file (at any depth)
                        if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                // Extract to temp location first
                                var tempPath = Path.Combine(Path.GetTempPath(), entry.Name);
                                entry.ExtractToFile(tempPath, true);

                                // Get version info
                                var versionInfo = FileVersionInfo.GetVersionInfo(tempPath);
                                var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
                                var cacheFileName = $"{version}.dll";
                                var cachePath = Path.Combine(_cacheFolder, cacheFileName);

                                // Move to cache
                                File.Copy(tempPath, cachePath, true);
                                File.Delete(tempPath);

                                System.Diagnostics.Debug.WriteLine($"Extracted and added DLSS {version} from ZIP");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error processing {entry.FullName} from ZIP: {ex.Message}");
                            }
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing ZIP file: {ex.Message}");
        }
    }

    private async void DllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DllData dllData)
        {
            try
            {
                // Don't do anything if clicking the current version
                if (dllData.Version == _currentInstalledVersion)
                {
                    return;
                }

                // Replace DLL using elevated PowerShell
                var success = await ReplaceDllWithElevation(dllData.FilePath);

                if (success)
                {
                    OperationSuccessful = true;
                    var displayVersion = dllData.Version.Replace(",", ".");
                    StatusMessage = $"Swapped to DLSS {displayVersion}";

                    // Refresh to update UI and cache
                    await CopyCurrentDllToCache();
                    await LoadDllsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error replacing DLL: {ex.Message}");
            }
        }
    }

    private async Task<bool> ReplaceDllWithElevation(string sourceDllPath)
    {
        try
        {
            return await Task.Run(() =>
            {
                using (var powerShell = PowerShell.Create())
                {
                    // Add the copy command
                    var script = $"Copy-Item -Path '{sourceDllPath}' -Destination '{_gameDllPath}' -Force";
                    powerShell.AddScript(script);

                    // Configure to run elevated
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
                        System.Diagnostics.Debug.WriteLine($"DLL replacement completed with exit code: {process.ExitCode}");
                        return process.ExitCode == 0;
                    }

                    return false;
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in ReplaceDllWithElevation: {ex.Message}");
            return false;
        }
    }

    private async Task<DllData> ParseDllAsync(string dllPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";

            return new DllData
            {
                Version = version,
                FilePath = dllPath
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing DLL {dllPath}: {ex.Message}");
            return null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        this.Close();
    }

    private class DllData
    {
        public string Version
        {
            get; set;
        }
        public string FilePath
        {
            get; set;
        }
    }
}
