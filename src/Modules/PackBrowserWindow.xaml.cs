using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Newtonsoft.Json.Linq;
using Vanilla_RTX_App.Core;
using WinRT.Interop;
using static Vanilla_RTX_App.TunerVariables;

namespace Vanilla_RTX_App.PackBrowser;
/*
// Implement the "potentially suitable for alchitex" tag, there are more consierations to this, incomaptible packs have to be able to accept more than 1 tag...!
// and you have to keep it nice where it is already used, dont mess it up
// and required or null? the last fieldS!

New ideas to implement:

Currently there are 3 "RTX", "VV", and "Incompatible" tags, need a FOURTH, BLUE rgb(17, 66, 112) tag added called "Potentially suitable for Alchitex"

this tag is bestowed by a special class, this special class's job is determining if the pack is potentially suitable for alchitext or not.

how so?

looks into the directory of the resource pack, if there is a textures/blocks folder that is non - empty and contains at least 10 .png, .jpg, .jpeg, or.tga in total, its a a simple rule, will expand on later, for now this is enough
this tag can appear only next to Incompatible

CAN NOT appear next to RTX and VV, a pack is either RTX, or VV.

*/




public sealed partial class PackBrowserWindow : Window
{
    // ── Window infrastructure ────────────────────────────────────────────────
    private readonly AppWindow _appWindow;
    private readonly Window _mainWindow;

    // ── Selection state ──────────────────────────────────────────────────────
    // Maps pack directory path → its toggle button.
    private readonly Dictionary<string, Button> _packButtonMap = new();

    // Tracks which packs are currently toggled ON.
    private readonly HashSet<string> _selectedPaths = new();

    public static string gameTitleText => TunerVariables.Persistent.IsTargetingPreview ? "Minecraft Preview" : "Minecraft";

    // ════════════════════════════════════════════════════════════════════════
    //  Constructor
    // ════════════════════════════════════════════════════════════════════════
    public PackBrowserWindow(MainWindow mainWindow)
    {
        this.InitializeComponent();

        _mainWindow = mainWindow;

        // ── Theme ────────────────────────────────────────────────────────────
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

        // ── AppWindow / title bar ────────────────────────────────────────────
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

        // Populate the import button description now that gameTitleText is known
        AddPackDescriptionText.Text =
            $"Select or drag & drop .mcpack or .zip files of your resource packs here to import them to {gameTitleText}";

        PopulatePackBrowserAnnouncements();

        ActionBarShadowHost.Translation = new System.Numerics.Vector3(0, 0, 32);

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
    //  Pack list loading
    // ════════════════════════════════════════════════════════════════════════

    private async Task LoadPacksAsync()
    {
        PackListContainer.Children.Clear();
        _packButtonMap.Clear();
        _selectedPaths.Clear();
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
                return;
            }

            // Sort: RTX → Vibrant Visuals → Incompatible; alphabetical within each group
            var sortedPacks = packs
                .OrderBy(p => p.PackType switch
                {
                    "RTX" => 0,
                    "Vibrant Visuals" => 1,
                    _ => 2   // Incompatible
                })
                .ThenBy(p => p.PackName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pack in sortedPacks)
            {
                var btn = CreatePackButton(pack);
                PackListContainer.Children.Add(btn);
                _packButtonMap[pack.PackPath] = btn;
            }

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

        // Columns: [icon 75] [gap 15] [info *] [gap 15] [tags Auto]
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
            iconBorder.Child = new Image { Source = pack.Icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            try
            {
                iconBorder.Child = new Image
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

        // Selection overlay: semi-transparent black tint + checkmark, hidden by default.
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
            Glyph = "\uE73E",   // Checkmark
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
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(230, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(230, 110, 7, 0));
                break;
            case "RTX":
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 110, 175, 0));
                break;
            case "Vibrant Visuals":
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(230, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(230, 110, 75, 0));
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

    /// <summary>
    /// Toggles the selected state of a pack. ALL pack types are selectable,
    /// including Incompatible — callers decide what to do with the type field.
    /// </summary>
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

    /// <summary>
    /// Writes the toggled packs to <see cref="TunerVariables.SelectedPacks"/> and closes.
    /// If nothing is toggled the window closes without modifying SelectedPacks.
    /// Each entry is a (Location, Name, Type) tuple where Type is "RTX",
    /// "Vibrant Visuals", or "Incompatible".
    /// </summary>
    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPaths.Count > 0)
        {
            TunerVariables.SelectedPacks.Clear();

            foreach (var path in _selectedPaths.Where(p => _packButtonMap.ContainsKey(p)))
            {
                var pack = (PackData)_packButtonMap[path].Tag;
                TunerVariables.SelectedPacks.Add((pack.PackPath, pack.PackName, pack.PackType));
            }
        }

        this.Close();
    }

    /// <summary>
    /// Placeholder for future import functionality.
    /// The pack list is always refreshed in the finally block.
    /// </summary>
    private async void AddPackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // TODO: implement ImportPackAsync() and call it here
            // await ImportPackAsync();
        }
        finally
        {
            // Refresh regardless of success or failure
            LoadingPanel.Visibility = Visibility.Visible;
            PackSelectionPanel.Visibility = Visibility.Collapsed;
            await LoadPacksAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack scanning
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<PackData>> ScanForCompatiblePacksAsync()
    {
        var packs = new List<PackData>();

        var basePath = TunerVariables.Persistent.IsTargetingPreview
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock Preview")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock");

        var scanPaths = new[]
        {
            Path.Combine(basePath, "Users", "Shared", "games", "com.mojang", "resource_packs"),
            Path.Combine(basePath, "Users", "Shared", "games", "com.mojang", "development_resource_packs")
        };

        foreach (var scanPath in scanPaths)
        {
            if (!Directory.Exists(scanPath))
            {
                System.Diagnostics.Trace.WriteLine($"Path doesn't exist: {scanPath}");
                continue;
            }

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
        // PackType is the primary/most-significant tag: RTX > Vibrant Visuals > Incompatible
        var packType = "Incompatible";

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

            if (hasRaytraced)
            {
                capabilityTags.Add("RTX");
                packType = "RTX";
            }
            if (hasPbr)
            {
                capabilityTags.Add("Vibrant Visuals");
                // Only promote to VV if not already RTX
                if (packType == "Incompatible") packType = "Vibrant Visuals";
            }
        }

        if (packType == "Incompatible")
            capabilityTags.Add("Incompatible");

        // ── Name / description ───────────────────────────────────────────────
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

        if (packName == "pack.name") packName = Path.GetFileName(packDir);
        if (packDesc == "pack.description") packDesc = string.Empty;

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = packType   // "RTX", "Vibrant Visuals", or "Incompatible"
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

    /// <summary>
    /// Recursively finds the selection-overlay Border (Tag = "SelectionOverlay")
    /// within a button's visual tree.
    /// </summary>
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
    //  Private data model
    // ════════════════════════════════════════════════════════════════════════

    private class PackData
    {
        public required string PackName { get; set; }
        public required string PackDescription { get; set; }
        public required string PackPath { get; set; }
        public BitmapImage? Icon { get; set; }
        public required List<string> CapabilityTags { get; set; }
        /// <summary>
        /// Primary type string: "RTX", "Vibrant Visuals", or "Incompatible".
        /// Drives sort order and the Type field written to TunerVariables.SelectedPacks.
        /// </summary>
        public required string PackType { get; set; }
    }
}
