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
using Microsoft.UI.Xaml.Media;
using Vanilla_RTX_App.Core;
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
            WindowTitle.Text = $"Switch DLSS version for {text}";

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
            if (minecraftPath == null)
            {
                StatusMessage = "Minecraft installation not found";
                this.Close();
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
            @"C:\Program Files\Microsoft Games\" + targetFolderName,
            @"C:\XboxGames\" + targetFolderName
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

        // Scan all drives for Minecraft installation
        System.Diagnostics.Debug.WriteLine($"=== FULL DRIVE SCAN ===");

        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
        System.Diagnostics.Debug.WriteLine($"Found {drives.Count} fixed drives");

        foreach (var drive in drives)
        {
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
                        if (isPreview)
                            TunerVariables.Persistent.MinecraftPreviewInstallPath = dirInfo;
                        else
                            TunerVariables.Persistent.MinecraftInstallPath = dirInfo;

                        return programFilesPath;
                    }
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
                        if (isPreview)
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

        // If scan failed, prompt user to locate manually
        System.Diagnostics.Debug.WriteLine("=== MANUAL LOCATION REQUIRED ===");
        var manualPath = await LocateMinecraftManually();

        System.Diagnostics.Debug.WriteLine($"Manual path result: {manualPath ?? "NULL"}");
        return manualPath;
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
            var exePath = Path.Combine(folder.Path, "Content", "Minecraft.Windows.exe");
            System.Diagnostics.Debug.WriteLine($"Checking for exe at: {exePath}");
            System.Diagnostics.Debug.WriteLine($"Exe exists: {File.Exists(exePath)}");

            if (File.Exists(exePath))
            {
                System.Diagnostics.Debug.WriteLine($"✓ Valid Minecraft folder selected: {folder.Path}");

                // Cache the path
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
        var fallbackLocations = new Func<string>[]
        {
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TunerVariables.CacheFolderName, "DLSS"),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), TunerVariables.CacheFolderName, "DLSS"),
            () => Path.Combine(Path.GetTempPath(), TunerVariables.CacheFolderName, "DLSS"),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), TunerVariables.CacheFolderName, "DLSS"),
        };

        foreach (var locationFunc in fallbackLocations)
        {
            try
            {
                var location = locationFunc();
                Directory.CreateDirectory(location);
                return location;
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    private async Task CopyCurrentDllToCache()
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(_gameDllPath);
            _currentInstalledVersion = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";

            // Sanitize version for filename
            var sanitizedVersion = SanitizeVersion(_currentInstalledVersion);
            var cacheFileName = $"nvngx_dlss_{sanitizedVersion}.dll";
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

    private string SanitizeVersion(string version)
    {
        // Normalize version string: replace common separators with dots, then remove invalid chars
        var normalized = version.Replace(",", ".").Replace(" ", ".").Replace("_", ".");

        // Remove invalid filename characters
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("", normalized.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

        return cleaned.Trim('.');
    }

    private string FormatVersionForDisplay(string version)
    {
        // Display version with dots (should already be normalized from SanitizeVersion)
        return version.Replace("_", ".").Replace(",", ".").Trim('.');
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
                EmptyStateText.Text = "No DLSS versions cached yet. Click the button below to add one.";
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                foreach (var dllPath in dllFiles)
                {
                    var dllData = await ParseDllAsync(dllPath);
                    if (dllData != null)
                    {
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

    private Button CreateAddDllButton()
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 16, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

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
            Text = "Browse for a DLSS DLL file to add",
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

        var badge = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(120, 60, 60, 60)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

        badge.Child = hyperlinkButton;
        Grid.SetColumn(badge, 4);
        grid.Children.Add(badge);

        button.Content = grid;
        button.Click += AddDllButton_Click;

        return button;
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

            var hWnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // Get version from selected DLL
                var versionInfo = FileVersionInfo.GetVersionInfo(file.Path);
                var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
                var sanitizedVersion = SanitizeVersion(version);
                var cacheFileName = $"nvngx_dlss_{sanitizedVersion}.dll";
                var cachePath = Path.Combine(_cacheFolder, cacheFileName);

                // Copy to cache (overwrite if exists)
                await Task.Run(() => File.Copy(file.Path, cachePath, true));

                System.Diagnostics.Debug.WriteLine($"Added DLSS {version} to cache");

                // Refresh list
                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error adding DLL: {ex.Message}");
        }
    }

    private Button CreateDllButton(DllData dll)
    {
        bool isCurrentVersion = dll.Version == _currentInstalledVersion;

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 16, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            Tag = dll,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

        // Apply accent color style if this is the current version
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

        var versionText = new TextBlock
        {
            Text = $"DLSS {FormatVersionForDisplay(dll.Version)}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var pathText = new TextBlock
        {
            Text = dll.FilePath,
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

        button.Content = grid;
        button.Click += DllButton_Click;

        return button;
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
                    StatusMessage = $"Switched to DLSS {FormatVersionForDisplay(dllData.Version)}";

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
            // Create a temporary PowerShell script
            var scriptPath = Path.Combine(Path.GetTempPath(), $"dlss_replace_{Guid.NewGuid()}.ps1");

            var scriptContent = $@"
try {{
    Copy-Item -Path '{sourceDllPath}' -Destination '{_gameDllPath}' -Force
    exit 0
}} catch {{
    exit 1
}}
";
            await File.WriteAllTextAsync(scriptPath, scriptContent);

            // Execute with elevation
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();

                // Clean up script
                try { File.Delete(scriptPath); } catch { }

                System.Diagnostics.Debug.WriteLine($"DLL replacement completed with exit code: {process.ExitCode}");
                return process.ExitCode == 0;
            }

            return false;
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