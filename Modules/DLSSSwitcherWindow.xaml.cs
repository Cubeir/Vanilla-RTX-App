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
            // Determine game DLL path
            _gameDllPath = TunerVariables.Persistent.IsTargetingPreview
                ? @"C:\Program Files\Microsoft Games\Minecraft Preview for Windows\Content\nvngx_dlss.dll"
                : @"C:\Program Files\Microsoft Games\Minecraft for Windows\Content\nvngx_dlss.dll";

            // Check if game DLL exists
            if (!File.Exists(_gameDllPath))
            {
                // Prompt user to locate manually
                var located = await LocateGameDllManually();
                if (!located)
                {
                    StatusMessage = "Game DLSS file not found";
                    this.Close();
                    return;
                }
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

    private async Task<bool> LocateGameDllManually()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".dll");

        var hWnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hWnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null && file.Name.ToLowerInvariant() == "nvngx_dlss.dll")
        {
            _gameDllPath = file.Path;
            return true;
        }

        return false;
    }

    private string EstablishCacheFolder()
    {
        var fallbackLocations = new Func<string>[]
        {
            () => Path.Combine(Path.GetTempPath(), TunerVariables.CacheFolderName, "DLSS"),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TunerVariables.CacheFolderName, "DLSS"),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), TunerVariables.CacheFolderName, "DLSS"),
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
        // Remove invalid filename characters
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", version.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private async Task LoadDllsAsync()
    {
        try
        {
            DllListContainer.Children.Clear();

            // Add "Add New DLL" button at top
            var addButton = CreateAddDllButton();
            DllListContainer.Children.Add(addButton);

            // Get all DLL files from cache
            var dllFiles = Directory.GetFiles(_cacheFolder, "*.dll")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (dllFiles.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = "No DLSS versions cached. Add one using the button above.";
                return;
            }

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
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 30, 16, 30),
            Margin = new Thickness(0, 5, 0, 15),
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

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8
        };

        var icon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        var text = new TextBlock
        {
            Text = "Add DLSS Version",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        panel.Children.Add(icon);
        panel.Children.Add(text);
        button.Content = panel;
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
            Background = new SolidColorBrush(Colors.Transparent),
            Translation = new System.Numerics.Vector3(0, 0, 48)
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

        var icon = new FontIcon
        {
            Glyph = "\uF158",
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
            Text = $"DLSS {dll.Version}",
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
                    StatusMessage = $"Switched to DLSS {dllData.Version}";

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