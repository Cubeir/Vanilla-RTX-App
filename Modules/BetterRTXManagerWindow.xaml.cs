using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management.Automation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Vanilla_RTX_App.BetterRTXBrowser;


public sealed partial class BetterRTXManagerWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;
    private string _gameMaterialsPath;
    private string _cacheFolder;
    private string _defaultFolder;
    private CancellationTokenSource _scanCancellationTokenSource;

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

        this.Activated += BetterRTXManagerWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void BetterRTXManagerWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Activated -= BetterRTXManagerWindow_Activated;

            _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SetTitleBarDragRegion();
            });

            var text = TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";
            WindowTitle.Text = $"BetterRTX Preset Manager (Targeting {text})";

            ManualSelectionText.Text = $"If this is taking too long, click to manually locate the game folder, confirm in file explorer once you're inside the folder called: {(TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview for Windows" : "Minecraft for Windows")}";

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
            var isPreview = TunerVariables.Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? TunerVariables.Persistent.MinecraftPreviewInstallPath
                : TunerVariables.Persistent.MinecraftInstallPath;

            string minecraftPath = null;

            // SAFETY: Re-validate cache before trusting it
            // Handles edge case where user moved/deleted folder between startup and window opening
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
                Debug.WriteLine("Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    // Search was cancelled or failed - wait for manual selection
                    Debug.WriteLine("System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // At this point we have a valid path - continue initialization
            await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private string EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, "RTX_Cache");

            System.Diagnostics.Debug.WriteLine($"Creating BetterRTX cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            System.Diagnostics.Debug.WriteLine($"✓ BetterRTX cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ Failed to create BetterRTX cache: {ex.Message}");
            return null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
        this.Close();
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

        // Establish default folder
        _defaultFolder = Path.Combine(_cacheFolder, "__DEFAULT");
        Directory.CreateDirectory(_defaultFolder);

        // Load and display all cached presets
        await LoadPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;
    }
    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("Manual selection button clicked - cancelling system search");

        // Cancel any ongoing system search
        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowNative.GetWindowHandle(this);
        var isPreview = TunerVariables.Persistent.IsTargetingPreview;

        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

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

    private async Task LoadPresetsAsync()
    {
        try
        {
            PresetListContainer.Children.Clear();

            // Get hash of currently installed RTXStub.material.bin
            string currentHash = GetCurrentlyInstalledPresetHash();
            System.Diagnostics.Debug.WriteLine($"Current installed hash: {currentHash ?? "NULL"}");

            // Always add Default preset first
            var defaultPreset = CreateDefaultPreset();
            if (defaultPreset != null)
            {
                // Check if default matches current
                if (!string.IsNullOrEmpty(currentHash) && defaultPreset.StubHash == currentHash)
                {
                    System.Diagnostics.Debug.WriteLine("Current installation matches Default preset");
                }
                var defaultButton = CreatePresetButton(defaultPreset, currentHash);
                PresetListContainer.Children.Add(defaultButton);
            }

            // Get all preset folders from cache (excluding __DEFAULT)
            var presetFolders = Directory.GetDirectories(_cacheFolder)
                .Where(d => !Path.GetFileName(d).Equals("__DEFAULT", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Path.GetFileName(d))
                .ToList();

            if (presetFolders.Count == 0 && defaultPreset == null)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = "No BetterRTX presets have been imported yet. Click download presets, download .rtpacks from BetterRTX's website and import them here.";
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                foreach (var presetFolder in presetFolders)
                {
                    var presetData = await ParsePresetAsync(presetFolder);
                    if (presetData != null)
                    {
                        if (!string.IsNullOrEmpty(currentHash) && presetData.StubHash == currentHash)
                        {
                            System.Diagnostics.Debug.WriteLine($"Current installation matches preset: {presetData.PresetName}");
                        }
                        var presetButton = CreatePresetButton(presetData, currentHash);
                        PresetListContainer.Children.Add(presetButton);
                    }
                }
            }

            // Add "Add Preset" button at the end
            var addButton = CreateAddPresetButton();
            PresetListContainer.Children.Add(addButton);

            System.Diagnostics.Debug.WriteLine("Preset loading complete");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EXCEPTION in LoadPresetsAsync: {ex}");
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error loading presets: {ex.Message}";
        }
    }

    private PresetData CreateDefaultPreset()
    {
        if (!Directory.Exists(_defaultFolder))
            return null;

        var binFiles = Directory.GetFiles(_defaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();

        if (binFiles.Count == 0)
            return null;

        // Compute hash of RTXStub.material.bin if it exists
        string stubHash = null;
        var stubPath = binFiles.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("RTXStub.material.bin", StringComparison.OrdinalIgnoreCase));
        if (stubPath != null)
        {
            stubHash = ComputeFileHash(stubPath);
        }

        return new PresetData
        {
            PresetName = "Default RTX",
            PresetDescription = _defaultFolder,
            PresetPath = _defaultFolder,
            Icon = null,
            BinFiles = binFiles,
            IsDefault = true,
            StubHash = stubHash
        };
    }

    private async Task<PresetData> ParsePresetAsync(string presetFolder)
    {
        try
        {
            // Search recursively for manifest.json
            var manifestFiles = Directory.GetFiles(presetFolder, "manifest.json", SearchOption.AllDirectories);

            string presetName = Path.GetFileName(presetFolder);
            BitmapImage icon = null;

            // Try to parse manifest if found (only for name)
            if (manifestFiles.Length > 0)
            {
                var manifestPath = manifestFiles[0];
                var manifestDir = Path.GetDirectoryName(manifestPath);

                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath);
                    using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                    var root = doc.RootElement;

                    if (root.TryGetProperty("header", out var header))
                    {
                        if (header.TryGetProperty("name", out var nameElement))
                        {
                            var name = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                presetName = name;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing manifest in {presetFolder}: {ex.Message}");
                }

                // Try to load icon from manifest directory
                icon = await LoadIconAsync(manifestDir);
            }

            // If no icon found in manifest directory, try preset root
            if (icon == null)
            {
                icon = await LoadIconAsync(presetFolder);
            }

            // Search recursively for bin files
            var binFiles = Directory.GetFiles(presetFolder, "*.bin", SearchOption.AllDirectories).ToList();

            if (binFiles.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"No bin files found in preset: {presetFolder}");
                return null;
            }

            // Compute hash of RTXStub.material.bin if it exists
            string stubHash = null;
            var stubPath = binFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("RTXStub.material.bin", StringComparison.OrdinalIgnoreCase));
            if (stubPath != null)
            {
                stubHash = ComputeFileHash(stubPath);
                System.Diagnostics.Debug.WriteLine($"Preset {presetName} stub hash: {stubHash}");
            }

            // Description is always the file path
            return new PresetData
            {
                PresetName = presetName,
                PresetDescription = presetFolder,
                PresetPath = presetFolder,
                Icon = icon,
                BinFiles = binFiles,
                IsDefault = false,
                StubHash = stubHash
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing preset {presetFolder}: {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage> LoadIconAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        // Search recursively for pack_icon
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
                System.Diagnostics.Debug.WriteLine($"Loading icon: {iconPath}");
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
                System.Diagnostics.Debug.WriteLine($"Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }







    // PART 3: UI Creation Methods - Add these to the BetterRTXManagerWindow class

    private Button CreatePresetButton(PresetData preset, string currentInstalledHash)
    {
        bool isCurrent = !string.IsNullOrEmpty(currentInstalledHash) &&
                         !string.IsNullOrEmpty(preset.StubHash) &&
                         preset.StubHash == currentInstalledHash;

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 38, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            Tag = preset,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

        // Highlight ONLY the currently installed preset
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

        if (preset.Icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = preset.Icon,
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

        // Preset info
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            Text = preset.PresetName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = Helpers.SanitizePathForDisplay(preset.PresetDescription),
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

        // Delete button - only show if not current preset
        if (!isCurrent)
        {
            var deleteButton = new Button
            {
                Width = 36,
                Height = 36,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(16, 0, 0, 0),
                IsTextScaleFactorEnabled = false,
                Tag = preset
            };

            var deleteIcon = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 16,
                IsTextScaleFactorEnabled = false
            };

            deleteButton.Content = deleteIcon;
            deleteButton.Click += DeletePresetButton_Click;

            Grid.SetColumn(deleteButton, 4);
            grid.Children.Add(deleteButton);
        }

        button.Content = grid;
        button.Click += PresetButton_Click;

        return button;
    }

    private Button CreateAddPresetButton()
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

        button.DragOver += AddPresetButton_DragOver;
        button.Drop += AddPresetButton_Drop;
        button.DragEnter += AddPresetButton_DragEnter;
        button.DragLeave += AddPresetButton_DragLeave;

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
            Text = "Add BetterRTX Preset",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = "Drag and drop or browse for a BetterRTX preset (.rtpack or .zip)",
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
            Content = "Download Presets",
            NavigateUri = new Uri("https://bedrock.graphics/presets"),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16, 6, 16, 6),
            IsTextScaleFactorEnabled = false
        };

        Grid.SetColumn(hyperlinkButton, 4);
        grid.Children.Add(hyperlinkButton);

        button.Content = grid;
        button.Click += AddPresetButton_Click;

        return button;
    }

    private void AddPresetButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.Opacity = 0.7;
        }
    }

    private void AddPresetButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.Opacity = 1.0;
        }
    }

    private void AddPresetButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to add BetterRTX preset";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void AddPresetButton_Drop(object sender, DragEventArgs e)
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

                        if (extension == ".rtpack" || extension == ".zip")
                        {
                            await ProcessArchiveFileAsync(file.Path);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipped unsupported file type: {extension}");
                        }
                    }
                }

                await LoadPresetsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing dropped files: {ex.Message}");
        }
    }

    private async void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".rtpack");
            picker.FileTypeFilter.Add(".zip");

            var hWnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await ProcessArchiveFileAsync(file.Path);
                await LoadPresetsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error adding file: {ex.Message}");
        }
    }

    private async void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PresetData presetData)
        {
            try
            {
                /* Prevents deletion of default preset
                if (presetData.IsDefault)
                {
                    System.Diagnostics.Debug.WriteLine("Cannot delete default preset");
                    return;
                }
                */

                if (Directory.Exists(presetData.PresetPath))
                {
                    Directory.Delete(presetData.PresetPath, true);
                    System.Diagnostics.Debug.WriteLine($"Deleted preset: {presetData.PresetName}");
                    await LoadPresetsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting preset: {ex.Message}");
            }
        }
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PresetData presetData)
        {
            try
            {
                var success = await ApplyPresetAsync(presetData);

                if (success)
                {
                    OperationSuccessful = true;
                    StatusMessage = $"Installed {presetData.PresetName} successfully";
                    System.Diagnostics.Debug.WriteLine(StatusMessage);
                    // refresh the preset list
                    await LoadPresetsAsync();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to apply preset: {presetData.PresetName}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying preset: {ex.Message}");
            }
        }
    }


    private string ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error computing hash for {filePath}: {ex.Message}");
            return null;
        }
    }
    private string GetCurrentlyInstalledPresetHash()
    {
        var stubPath = Path.Combine(_gameMaterialsPath, "RTXStub.material.bin");
        return ComputeFileHash(stubPath);
    }





    // PART 4: File Processing & Application - Add these to the BetterRTXManagerWindow class

    private async Task ProcessArchiveFileAsync(string archivePath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(archivePath);
            var sanitizedName = SanitizePresetName(fileName);
            var destinationFolder = Path.Combine(_cacheFolder, sanitizedName);

            // If folder already exists, delete it to allow overwrite
            if (Directory.Exists(destinationFolder))
            {
                System.Diagnostics.Debug.WriteLine($"Overwriting existing preset: {sanitizedName}");
                Directory.Delete(destinationFolder, true);
            }

            Directory.CreateDirectory(destinationFolder);

            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            var destinationPath = Path.Combine(destinationFolder, entry.FullName);
                            var destinationDir = Path.GetDirectoryName(destinationPath);

                            if (!string.IsNullOrEmpty(destinationDir))
                            {
                                Directory.CreateDirectory(destinationDir);
                            }

                            entry.ExtractToFile(destinationPath, true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            System.Diagnostics.Debug.WriteLine($"Extracted preset to: {destinationFolder}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing archive {archivePath}: {ex.Message}");
        }
    }

    private async Task<bool> ApplyPresetAsync(PresetData preset)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"=== APPLYING PRESET: {preset.PresetName} ===");

            var filesToApply = new List<(string sourcePath, string destPath)>();
            var filesToCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Use HashSet to avoid duplicates

            // PHASE 1: Always try to cache the 4 core RTX files if they exist
            System.Diagnostics.Debug.WriteLine("PHASE 1: Checking core RTX files for Default preset...");
            foreach (var coreFileName in CoreRTXFiles)
            {
                var coreFilePath = Path.Combine(_gameMaterialsPath, coreFileName);
                if (File.Exists(coreFilePath))
                {
                    System.Diagnostics.Debug.WriteLine($"  Core file exists: {coreFileName}");
                    filesToCache.Add(coreFilePath);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"  Core file missing: {coreFileName}");
                }
            }

            // PHASE 2: Check which files the preset will replace and add them to cache list
            System.Diagnostics.Debug.WriteLine("PHASE 2: Checking preset files...");
            foreach (var binFilePath in preset.BinFiles)
            {
                var binFileName = Path.GetFileName(binFilePath);
                var destBinPath = Path.Combine(_gameMaterialsPath, binFileName);

                if (File.Exists(destBinPath))
                {
                    System.Diagnostics.Debug.WriteLine($"  Preset file exists in game: {binFileName}");
                    filesToCache.Add(destBinPath); // HashSet prevents duplicates
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"  Preset file missing in game: {binFileName}");
                }

                // Add to operation list regardless of existence
                filesToApply.Add((binFilePath, destBinPath));
            }

            if (filesToApply.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("⚠ No files to apply (preset might be empty)");
                return false;
            }

            // PHASE 3: Cache all collected files to Default preset
            if (filesToCache.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"PHASE 3: Caching {filesToCache.Count} files to Default preset...");

                foreach (var existingFilePath in filesToCache)
                {
                    var fileName = Path.GetFileName(existingFilePath);
                    var defaultBinPath = Path.Combine(_defaultFolder, fileName);

                    if (!File.Exists(defaultBinPath))
                    {
                        try
                        {
                            File.Copy(existingFilePath, defaultBinPath, false);
                            System.Diagnostics.Debug.WriteLine($"  ✓ Cached to Default: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ✗ Error caching {fileName}: {ex.Message}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  Already in Default: {fileName}");
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("⚠ No files to cache - Default preset will remain empty");
            }

            // PHASE 4: Apply the preset (replace OR create files)
            System.Diagnostics.Debug.WriteLine($"PHASE 4: Applying {filesToApply.Count} files with elevation...");
            var success = await ReplaceFilesWithElevation(filesToApply);

            if (success)
            {
                System.Diagnostics.Debug.WriteLine($"✓ Successfully applied preset: {preset.PresetName}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"✗ Failed to apply preset: {preset.PresetName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in ApplyPresetAsync: {ex.Message}");
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
                    // Escape single quotes
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
                    System.Diagnostics.Debug.WriteLine($"File operations completed with exit code: {process.ExitCode}");
                    return process.ExitCode == 0;
                }

                return false;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in ReplaceFilesWithElevation: {ex.Message}");
            return false;
        }
    }

    // REUSABLE
    public static string SanitizePresetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unnamed_Preset";

        // original name
        var sanitized = name;

        // These are all problematic characters (filesystem + PowerShell or command line)
        var badChars = new HashSet<char>(Path.GetInvalidFileNameChars())
    {
        '\'', '`', '$', ';', '&', '|', '<', '>', '(', ')', '{', '}', '[', ']',
        '"', '~', '!', '@', '#', '%', '^'
    };

        // Replace bad characters and control characters with underscores
        var chars = sanitized.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (badChars.Contains(chars[i]) || char.IsControl(chars[i]))
            {
                chars[i] = '_';
            }
        }
        sanitized = new string(chars);

        // Collapse multiple consecutive underscores into one
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }

        // Collapse multiple consecutive spaces into one
        while (sanitized.Contains("  "))
        {
            sanitized = sanitized.Replace("  ", " ");
        }

        // Remove leading/trailing underscores, spaces, and dots
        sanitized = sanitized.Trim('_', ' ', '.');

        // reserved Windows names
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                       "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                       "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var upperName = sanitized.ToUpperInvariant();
        if (reserved.Contains(upperName) || reserved.Any(r => upperName.StartsWith(r + ".")))
        {
            sanitized = "_" + sanitized;
        }

        // Ensure not empty after sanitization
        if (string.IsNullOrWhiteSpace(sanitized))
            return "Unnamed_Preset";

        // Limit length, leave room for full path (recommend 150-200 max)
        if (sanitized.Length > 200)
            sanitized = sanitized.Substring(0, 150).TrimEnd('_', ' ', '.');

        return sanitized;
    }




    private class PresetData
    {
        public string PresetName
        {
            get; set;
        }
        public string PresetDescription
        {
            get; set;
        }
        public string PresetPath
        {
            get; set;
        }
        public BitmapImage Icon
        {
            get; set;
        }
        public List<string> BinFiles
        {
            get; set;
        }
        public bool IsDefault
        {
            get; set;
        }
        public string StubHash // Hash of RTXStub.material.bin
        {
            get; set;
        } 
    }
}