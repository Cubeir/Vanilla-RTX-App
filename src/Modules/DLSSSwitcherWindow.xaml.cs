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
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

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

        this.Activated += DLSSSwitcherWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
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

            var text = TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft Release";
            WindowTitle.Text = $"Swap DLSS version for {text}";

            ManualSelectionText.Text = $"If this is taking too long, click to manually locate the game folder, confirm in file explorer once you're inside the folder called: {(TunerVariables.Persistent.IsTargetingPreview ? MinecraftGDKLocator.MinecraftPreviewFolderName : MinecraftGDKLocator.MinecraftFolderName)}";

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
                Trace.WriteLine($"Error setting drag region: {ex.Message}");
            }
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = TunerVariables.Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? TunerVariables.Persistent.MinecraftPreviewInstallPath
                : TunerVariables.Persistent.MinecraftInstallPath;

            string minecraftPath = null;

            // SAFETY: Re-validate cache before trusting it
            // Handles edge case where user moved/deleted folder between startup and window opening
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
                    if (isPreview)
                        TunerVariables.Persistent.MinecraftPreviewInstallPath = null;
                    else
                        TunerVariables.Persistent.MinecraftInstallPath = null;
                }

                // Show manual selection button immediately
                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionButton.Visibility = Visibility.Visible;
                });

                // Start Phase 2: System-wide search in background
                Trace.WriteLine("Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    // Search was cancelled or failed - wait for manual selection
                    Trace.WriteLine("System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // At this point we have a valid path - continue initialization
            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        _gameDllPath = Path.Combine(minecraftPath, "Content", "nvngx_dlss.dll");

        // Establish cache folder
        _cacheFolder = EstablishCacheFolder();
        if (_cacheFolder == null)
        {
            StatusMessage = "Could not establish cache folder";
            this.Close();
            return;
        }

        // Check if game DLL exists
        bool gameDllExists = File.Exists(_gameDllPath);

        if (!gameDllExists)
        {
            Trace.WriteLine($"⚠ DLSS file not found at: {_gameDllPath}");

            // Look for cached DLLs to repair the game
            var cachedDlls = Directory.GetFiles(_cacheFolder, "*.dll")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (cachedDlls.Count > 0)
            {
                var repairDll = cachedDlls.First();
                Trace.WriteLine($"🔧 Attempting to repair with: {repairDll}");

                var repairSuccess = await ReplaceDllWithElevation(repairDll);

                if (repairSuccess)
                {
                    Trace.WriteLine("✓ Game repaired successfully");
                    gameDllExists = true;
                    await CopyCurrentDllToCache();
                }
                else
                {
                    Trace.WriteLine("⚠ User cancelled UAC or repair failed - continuing anyway");
                }
            }
            else
            {
                Trace.WriteLine("⚠ No cached DLLs available - user must import one");
            }
        }
        else
        {
            // Game DLL exists, back it up to cache
            await CopyCurrentDllToCache();
        }

        // Load and display all cached DLLs
        await LoadDllsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        DllSelectionPanel.Visibility = Visibility.Visible;
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("Manual selection button clicked - cancelling system search");

        // Cancel any ongoing system search
        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = TunerVariables.Persistent.IsTargetingPreview;

        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

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

    private string EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "DLSS_Cache");

            Trace.WriteLine($"Creating DLSS cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"✓ DLSS cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"✗ Failed to create DLSS cache: {ex.Message}");
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

            Trace.WriteLine($"Copied current DLSS {_currentInstalledVersion} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error copying current DLL to cache: {ex.Message}");
            _currentInstalledVersion = "Unknown";
        }
    }

    private async Task LoadDllsAsync()
    {
        try
        {
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

            Trace.WriteLine("DLL loading complete");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"EXCEPTION in LoadDllsAsync: {ex}");
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

        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var displayVersion = dll.Version.Replace(",", ".");

        if (isCurrentVersion)
        {
            displayVersion += " (Current)";
        }
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
            Text = isCurrentVersion ? Helpers.SanitizePathForDisplay(dll.FilePath) : "Click to swap to this version",
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
                if (dllData.Version == _currentInstalledVersion)
                {
                    Trace.WriteLine("Cannot delete currently installed DLSS version");
                    return;
                }

                if (File.Exists(dllData.FilePath))
                {
                    File.Delete(dllData.FilePath);
                    Trace.WriteLine($"Deleted DLSS version {dllData.Version} from cache");
                    await LoadDllsAsync();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error deleting DLL: {ex.Message}");
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
        if (sender is Button button)
        {
            button.Opacity = 0.7;
        }
    }

    private void AddDllButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.Opacity = 1.0;
        }
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
                            Trace.WriteLine($"Skipped unsupported file type: {extension}");
                        }
                    }
                }

                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error processing dropped files: {ex.Message}");
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

                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error adding file: {ex.Message}");
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
            Trace.WriteLine($"Added DLSS {version} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error processing DLL {dllPath}: {ex.Message}");
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
                        if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
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

                                Trace.WriteLine($"Extracted and added DLSS {version} from ZIP");
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"Error processing {entry.FullName} from ZIP: {ex.Message}");
                            }
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error processing ZIP file: {ex.Message}");
        }
    }

    private async void DllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DllData dllData)
        {
            try
            {
                if (dllData.Version == _currentInstalledVersion)
                {
                    return;
                }

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
                Trace.WriteLine($"Error replacing DLL: {ex.Message}");
            }
        }
    }

    private async Task<bool> ReplaceDllWithElevation(string sourceDllPath)
    {
        try
        {
            return await Task.Run(() =>
            {
                var batchScript = $"@echo off\r\ncopy /Y \"{sourceDllPath}\" \"{_gameDllPath}\" >nul 2>&1\r\nexit %ERRORLEVEL%";
                var tempBatchPath = Path.Combine(Path.GetTempPath(), $"dlss_dll_{Guid.NewGuid():N}.bat");

                File.WriteAllText(tempBatchPath, batchScript);

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = tempBatchPath,
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        Trace.WriteLine($"DLL replacement completed with exit code: {process.ExitCode}");
                        return process.ExitCode == 0;
                    }
                    return false;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempBatchPath))
                            File.Delete(tempBatchPath);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error in ReplaceDllWithElevation: {ex.Message}");
            return false;
        }
    }

    private static async Task<DllData> ParseDllAsync(string dllPath)
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
            Trace.WriteLine($"Error parsing DLL {dllPath}: {ex.Message}");
            return null;
        }
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
