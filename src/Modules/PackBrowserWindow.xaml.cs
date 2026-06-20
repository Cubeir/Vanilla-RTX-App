using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Newtonsoft.Json.Linq;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.PackBrowser;

public sealed partial class PackBrowserWindow : Window
{
    // ── Window infrastructure ────────────────────────────────────────────────
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    // ── Selection state ──────────────────────────────────────────────────────
    private readonly Dictionary<string, Button> _packButtonMap = new();
    private readonly HashSet<string> _selectedPaths = new();

    // Unique tags seen in the current pack list; rebuilt on each load.
    // Drives the SelectAll dropdown — new tags appear automatically.
    private readonly List<string> _knownTags = new();

    public static string gameTitleText => TunerVariables.Persistent.IsTargetingPreview
        ? "Minecraft Preview" : "Minecraft";

    private const string AlchitexCandidateTag = "Potential Alchitex Candidate";

    /// <summary>
    /// Matches § followed immediately by any non-whitespace character.
    /// Strips Minecraft in-game formatting codes before display.
    /// § followed by a space is left intact (Minecraft renders it literally).
    /// </summary>
    private static readonly Regex MinecraftFormattingCodeRegex =
        new(@"§\S", RegexOptions.Compiled);

    // ════════════════════════════════════════════════════════════════════════
    //  Constructor
    // ════════════════════════════════════════════════════════════════════════
    public PackBrowserWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();
        _mainWindow = mainWindow;

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

        this.Activated += PackBrowserWindow_Activated;
        _mainWindow.Closed += MainWindow_Closed;

        // Wire import status into the title bar.
        ExpImpDel.ImportStatusChanged += OnImportStatusChanged;

        // Wire the overwrite confirmation dialog so ExpImpDel can ask us on the UI thread.
        ExpImpDel.ConfirmOverwrite = ShowOverwriteDialogAsync;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════════════

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        _mainWindow.Closed -= MainWindow_Closed;
        this.Close();
    }

    private async void PackBrowserWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        await Task.Delay(25);
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;

        this.Activated -= PackBrowserWindow_Activated;

        _ = this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            SetTitleBarDragRegion);

        WindowTitle.Text = $"Select from your {gameTitleText} resource packs";

        AddPackDescriptionText.Text =
            $"Select or drag & drop .mcpack or .zip files of your resource packs here to import them to {gameTitleText}";

        PopulatePackBrowserAnnouncements();

        ActionBarShadowHost.Translation = new System.Numerics.Vector3(0, 0, 32);

        // Register drag-and-drop on the window content root.
        if (this.Content is UIElement contentRoot)
        {
            contentRoot.AllowDrop = true;
            contentRoot.DragOver += ContentRoot_DragOver;
            contentRoot.Drop += ContentRoot_Drop;
        }

        await LoadPacksAsync();

        _ = this.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(75);
            try { this.Activate(); } catch { }
        });
    }

    private void SetTitleBarDragRegion()
    {
        if (_appWindow.TitleBar == null || TitleBarArea.XamlRoot == null) return;
        try
        {
            var scale = TitleBarArea.XamlRoot.RasterizationScale;
            _ = (int)(TitleBarArea.ActualHeight * scale);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Error setting drag region: {ex.Message}");
        }
    }

    private void PopulatePackBrowserAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.ResourcePackSelectionAnnouncements);
        if (items is null) return;
        foreach (var item in items)
            PackBrowserAnnouncementsPanel.Children.Add(new PsaCard(item));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Drag-and-drop
    // ════════════════════════════════════════════════════════════════════════

    private void ContentRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Import pack";
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void ContentRoot_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items == null || items.Count == 0) return;

        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder folder)
                paths.Add(folder.Path);
            else if (item is Windows.Storage.StorageFile file)
                paths.Add(file.Path); // filtering by extension happens inside ImportFromPathsAsync
        }

        if (paths.Count == 0) return;
        await RunImportAsync(() => ExpImpDel.ImportFromPathsAsync(paths));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Overwrite confirmation dialog
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by ExpImpDel when a duplicate UUID match is found. Runs on the UI
    /// thread (via DispatcherQueue) so the ContentDialog can be shown safely.
    /// Returns true if the user confirms overwrite, false to skip.
    /// </summary>
    private async Task<bool> ShowOverwriteDialogAsync(string packName, string existingPath)
    {
        bool result = false;

        // ContentDialog must be shown on the UI thread.
        var tcs = new TaskCompletionSource<bool>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var existingFolderName = Path.GetFileName(existingPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var dialog = new ContentDialog
                {
                    Title = "Pack already installed",
                    Content = $"\"{packName}\" is already installed as \"{existingFolderName}\".\n\nReplace it with the incoming version?",
                    PrimaryButtonText = "Replace",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot
                };

                var dialogResult = await dialog.ShowAsync();
                tcs.SetResult(dialogResult == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Overwrite dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        result = await tcs.Task;
        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Minecraft formatting-code stripping
    // ════════════════════════════════════════════════════════════════════════

    private static string StripMinecraftFormatting(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return MinecraftFormattingCodeRegex.Replace(input, string.Empty).Trim();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack list loading
    // ════════════════════════════════════════════════════════════════════════

    private async Task LoadPacksAsync()
    {
        PackListContainer.Children.Clear();
        _packButtonMap.Clear();
        _selectedPaths.Clear();
        _knownTags.Clear();
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        try
        {
            System.Diagnostics.Trace.WriteLine("Starting pack scan...");
            var packs = await ScanForCompatiblePacksAsync();
            System.Diagnostics.Trace.WriteLine($"Found {packs.Count} packs");

            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;

            if (packs.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = TunerVariables.Persistent.IsTargetingPreview
                    ? "No packs found in Minecraft Preview data directory."
                    : "No packs found in Minecraft data directory.";
                RebuildSelectAllDropdown();
                return;
            }

            var sortedPacks = packs
                .OrderBy(p => p switch
                {
                    { PackType: "RTX" } => 0,
                    { PackType: "Vibrant Visuals" } => 1,
                    _ => 2
                })
                .ThenBy(p => p.PackName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Accumulate unique tags in sort order — drives the dropdown.
            foreach (var pack in sortedPacks)
                foreach (var tag in pack.CapabilityTags)
                    if (!_knownTags.Contains(tag))
                        _knownTags.Add(tag);

            foreach (var pack in sortedPacks)
            {
                var btn = CreatePackButton(pack);
                PackListContainer.Children.Add(btn);
                _packButtonMap[pack.PackPath] = btn;
            }

            RebuildSelectAllDropdown();
            System.Diagnostics.Trace.WriteLine("Pack loading complete");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"EXCEPTION in LoadPacksAsync: {ex}");
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SelectAll dropdown  (DropDownButton — flyout rebuilt after every load)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds the MenuFlyout on SelectAll_Button from the current _knownTags.
    /// Called after every LoadPacksAsync so items always reflect visible packs.
    /// Adding new tag types anywhere in the codebase automatically adds them here.
    /// </summary>
    private void RebuildSelectAllDropdown()
    {
        var flyout = new MenuFlyout();

        var selectAll = new MenuFlyoutItem { Text = "Select all" };
        selectAll.Click += (_, _) => SetAllPacksSelected(true);
        flyout.Items.Add(selectAll);

        var deselectAll = new MenuFlyoutItem { Text = "Deselect all" };
        deselectAll.Click += (_, _) => SetAllPacksSelected(false);
        flyout.Items.Add(deselectAll);

        if (_knownTags.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var tag in _knownTags)
            {
                var capturedTag = tag;
                var item = new MenuFlyoutItem { Text = $"Select \"{capturedTag}\"" };
                item.Click += (_, _) => SelectPacksByTag(capturedTag);
                flyout.Items.Add(item);
            }
        }

        // DropDownButton exposes its flyout directly — no Click handler needed.
        SelectAll_Button.Flyout = flyout;
    }

    private void SetAllPacksSelected(bool selected)
    {
        foreach (var (path, button) in _packButtonMap)
        {
            var overlay = FindSelectionOverlay(button);
            if (overlay == null) continue;

            if (selected) { _selectedPaths.Add(path); overlay.Visibility = Visibility.Visible; }
            else { _selectedPaths.Remove(path); overlay.Visibility = Visibility.Collapsed; }
        }
    }

    private void SelectPacksByTag(string tag)
    {
        foreach (var (path, button) in _packButtonMap)
        {
            if (button.Tag is not PackData pack) continue;
            if (!pack.CapabilityTags.Contains(tag)) continue;

            var overlay = FindSelectionOverlay(button);
            if (overlay == null) continue;

            _selectedPaths.Add(path);
            overlay.Visibility = Visibility.Visible;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack button factory
    // ════════════════════════════════════════════════════════════════════════

    private Button CreatePackButton(PackData pack)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 20, 16, 20),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            Tag = pack,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            MinHeight = 115
        };

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

        // ── Icon + selection overlay ─────────────────────────────────────────
        var iconContainer = new Grid { Width = 75, Height = 75 };

        var iconBorder = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Translation = new System.Numerics.Vector3(0, 0, 48)
        };

        var iconShadow = new ThemeShadow();
        iconBorder.Shadow = iconShadow;
        iconBorder.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                iconShadow.Receivers.Add(ShadowReceiverGrid);
        };

        if (pack.Icon != null)
        {
            iconBorder.Child = new Microsoft.UI.Xaml.Controls.Image
            { Source = pack.Icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            try
            {
                iconBorder.Child = new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/missing.png")),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                iconBorder.Child = new FontIcon
                {
                    Glyph = "\uE7B8",
                    FontSize = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        var selectionOverlay = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(ColorHelper.FromArgb(128, 0, 0, 0)),
            Translation = new System.Numerics.Vector3(0, 0, 64),
            Visibility = Visibility.Collapsed,
            Tag = "SelectionOverlay"
        };
        selectionOverlay.Child = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 48,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        iconContainer.Children.Add(iconBorder);
        iconContainer.Children.Add(selectionOverlay);
        Grid.SetColumn(iconContainer, 0);
        grid.Children.Add(iconContainer);

        // ── Pack name + description ──────────────────────────────────────────
        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(new TextBlock
        {
            Text = pack.PackName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        infoPanel.Children.Add(new TextBlock
        {
            Text = pack.PackDescription,
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        // ── Capability tags ──────────────────────────────────────────────────
        var tagsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 6
        };
        foreach (var tag in pack.CapabilityTags)
            tagsPanel.Children.Add(BuildTagBadge(tag));
        Grid.SetColumn(tagsPanel, 4);
        grid.Children.Add(tagsPanel);

        button.Content = grid;
        button.Click += PackButton_Click;
        return button;
    }

    private static Border BuildTagBadge(string tag)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4)
        };
        var text = new TextBlock
        {
            Text = tag,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        switch (tag)
        {
            case "Incompatible":
                text.Text = "Incompatible with Tuner";
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(244, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 192, 33, 0));
                break;
            case "RTX":
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(244, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 111, 177, 0));
                break;
            case "Vibrant Visuals":
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(244, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 200, 132, 0));
                break;
            case AlchitexCandidateTag:
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(244, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 0, 72, 138));
                break;
            default:
                text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                badge.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                break;
        }

        badge.Child = text;
        return badge;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Click handlers
    // ════════════════════════════════════════════════════════════════════════

    private void PackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PackData pack) return;

        var overlay = FindSelectionOverlay(button);
        if (overlay == null) return;

        if (_selectedPaths.Contains(pack.PackPath))
        {
            _selectedPaths.Remove(pack.PackPath);
            overlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selectedPaths.Add(pack.PackPath);
            overlay.Visibility = Visibility.Visible;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPaths.Count > 0)
        {
            TunerVariables.SelectedPacks.Clear();

            foreach (var path in _selectedPaths.Where(p => _packButtonMap.ContainsKey(p)))
            {
                var pack = (PackData)_packButtonMap[path].Tag;
                TunerVariables.SelectedPacks.Add(
                    (pack.PackPath, pack.PackName, pack.PackType, pack.PotentiallySuitableForPBRGen));
            }
        }

        this.Close();
    }

    private async void AddPackButton_Click(object sender, RoutedEventArgs e)
    {
        // Pass THIS window's handle so the picker is owned by PackBrowserWindow,
        // not the main window — prevents the main window from jumping to front.
        var hwnd = WindowNative.GetWindowHandle(this);
        await RunImportAsync(() => ExpImpDel.ImportPackAsync(hwnd));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Import orchestration
    // ════════════════════════════════════════════════════════════════════════

    private async Task RunImportAsync(Func<Task<bool>> importWork)
    {
        AddPackButton.IsEnabled = false;

        try
        {
            await importWork();
        }
        finally
        {
            // Always refresh so newly imported packs appear and the dropdown updates.
            LoadingPanel.Visibility = Visibility.Visible;
            PackSelectionPanel.Visibility = Visibility.Collapsed;
            await LoadPacksAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            AddPackButton.IsEnabled = true;

            // Restore the normal title once the import sequence is fully done.
            WindowTitle.Text = $"Select from your {gameTitleText} resource packs";
        }
    }

    private void OnImportStatusChanged(string message)
    {
        // ImportStatusChanged can fire from background tasks — marshal to UI thread.
        DispatcherQueue.TryEnqueue(() => WindowTitle.Text = message);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack scanning
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<PackData>> ScanForCompatiblePacksAsync()
    {
        var packs = new List<PackData>();

        var dataRoot = MinecraftUserDataLocator.GetDataRoot(TunerVariables.Persistent.IsTargetingPreview);
        if (!dataRoot.Exists)
        {
            System.Diagnostics.Trace.WriteLine($"{dataRoot.VersionDisplayName} data root not found.");
            return packs;
        }

        foreach (var scanPath in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(TunerVariables.Persistent.IsTargetingPreview))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(scanPath, "manifest.json", SearchOption.AllDirectories))
            {
                var packDir = Path.GetDirectoryName(manifestPath);
                if (packDir == null) continue;

                try
                {
                    var packData = await ParsePackAsync(packDir, manifestPath);
                    if (packData != null) packs.Add(packData);
                }
                catch (Newtonsoft.Json.JsonException jsonEx)
                {
                    System.Diagnostics.Trace.WriteLine($"Invalid JSON in {manifestPath}: {jsonEx.Message}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Error parsing pack {packDir}: {ex.Message}");
                }
            }
        }

        return packs;
    }

    private async Task<PackData> ParsePackAsync(string packDir, string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        var root = JObject.Parse(json);

        var capabilityTags = new List<string>();
        var packType = "Incompatible";
        var potentiallySuitableForPbrGen = false;

        var capabilities = root["capabilities"];
        if (capabilities != null && capabilities.Type == JTokenType.Array)
        {
            bool hasRaytraced = false, hasPbr = false;

            foreach (var cap in capabilities)
            {
                var capLower = cap.ToString().ToLowerInvariant();
                if (capLower == "raytraced") hasRaytraced = true;
                else if (capLower == "pbr") hasPbr = true;
            }

            if (hasRaytraced) { capabilityTags.Add("RTX"); packType = "RTX"; }
            if (hasPbr)
            {
                capabilityTags.Add("Vibrant Visuals");
                if (packType == "Incompatible") packType = "Vibrant Visuals";
            }
        }

        if (packType == "Incompatible")
        {
            if (AlchitexSuitabilityScanner.IsPotentiallySuitable(packDir))
            {
                potentiallySuitableForPbrGen = true;
                capabilityTags.Add(AlchitexCandidateTag);
            }
            capabilityTags.Add("Incompatible");
        }

        var header = root["header"];
        string packName = header?["name"]?.ToString() ?? "pack.name";
        string packDesc = header?["description"]?.ToString() ?? "pack.description";

        if (packName == "pack.name" || packDesc == "pack.description")
        {
            var langFolder = Path.Combine(packDir, "texts");
            if (Directory.Exists(langFolder))
            {
                var langData = await TryLoadLangFileAsync(langFolder);
                if (langData != null)
                {
                    if (packName == "pack.name" && langData.ContainsKey("pack.name"))
                        packName = langData["pack.name"];
                    if (packDesc == "pack.description" && langData.ContainsKey("pack.description"))
                        packDesc = langData["pack.description"];
                }
            }
        }

        // Strip §X Minecraft formatting codes before display.
        packName = StripMinecraftFormatting(packName);
        packDesc = StripMinecraftFormatting(packDesc);

        if (string.IsNullOrWhiteSpace(packName)) packName = Path.GetFileName(packDir);
        if (string.IsNullOrWhiteSpace(packDesc)) packDesc = Helpers.SanitizePathForDisplay(packDir);

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = packType,
            PotentiallySuitableForPBRGen = potentiallySuitableForPbrGen
        };
    }

    private async Task<Dictionary<string, string>?> TryLoadLangFileAsync(string langFolder)
    {
        if (!Directory.Exists(langFolder)) return null;

        var langFiles = Directory.GetFiles(langFolder, "*.lang")
            .Where(f => Path.GetFileName(f).StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var langPath in langFiles)
        {
            try
            {
                var langData = new Dictionary<string, string>();
                var lines = await File.ReadAllLinesAsync(langPath);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    langData[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                if (langData.ContainsKey("pack.name") || langData.ContainsKey("pack.description"))
                    return langData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error loading lang file {langPath}: {ex.Message}");
            }
        }

        return null;
    }

    private async Task<BitmapImage?> LoadIconAsync(string packDir)
    {
        var iconFiles = Directory.GetFiles(packDir, "pack_icon.*")
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            .ToArray();

        foreach (var iconPath in iconFiles)
        {
            try
            {
                var bitmap = new BitmapImage();
                using var fs = File.OpenRead(iconPath);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                ms.Position = 0;
                await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Visual-tree helper
    // ════════════════════════════════════════════════════════════════════════

    private static Border? FindSelectionOverlay(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Tag is string s && s == "SelectionOverlay")
                return b;
            var result = FindSelectionOverlay(child);
            if (result != null) return result;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Alchitex suitability scanner
    // ════════════════════════════════════════════════════════════════════════

    private static class AlchitexSuitabilityScanner
    {
        private const int MinimumQualifyingImageCount = 16;

        private static readonly HashSet<string> QualifyingExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tga" };

        public static bool IsPotentiallySuitable(string packDir)
        {
            int matchCount = 0;
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories))
                {
                    if (!QualifyingExtensions.Contains(Path.GetExtension(filePath))) continue;
                    if (!IsUnderTexturesBlocksPath(filePath)) continue;
                    if (++matchCount >= MinimumQualifyingImageCount) return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error scanning {packDir} for Alchitex suitability: {ex.Message}");
            }
            return false;
        }

        private static bool IsUnderTexturesBlocksPath(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return false;

            var segments = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("textures", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 1].Equals("blocks", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Private data model
    // ════════════════════════════════════════════════════════════════════════

    private class PackData
    {
        public required string PackName { get; set; }
        public required string PackDescription { get; set; }
        public required string PackPath { get; set; }
        public BitmapImage? Icon { get; set; }
        public required List<string> CapabilityTags { get; set; }
        public required string PackType { get; set; }
        public bool PotentiallySuitableForPBRGen { get; set; } = false;
    }
}
