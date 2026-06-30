using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using WinRT.Interop;
using WinUIEx;
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
    private readonly List<string> _knownTags = new();

    public static string gameTitleText => TunerVariables.Persistent.IsTargetingPreview
        ? "Minecraft Preview" : "Minecraft";

    private const string AlchitexCandidateTag = "Potential Alchitex Candidate";

    // TODO: re-enable Alchitex candidate eligibility for legacy packs if automatic
    //       manifest format upgrade is implemented downstream in Alchitex.
    private const bool AlchitexLegacyPacksEligible = false;

    /// <summary>
    /// Matches § followed immediately by any non-whitespace character.
    /// Strips Minecraft in-game formatting codes before display.
    /// § followed by a space is left intact (Minecraft renders it literally).
    /// </summary>
    private static readonly Regex MinecraftFormattingCodeRegex =
        new(@"§\S", RegexOptions.Compiled);

    /// <summary>
    /// Matches a strict three-part numeric version: digits.digits.digits only.
    /// Anything else (strings, four-part versions, etc.) is treated as Unknown.
    /// </summary>
    private static readonly Regex StrictSemVerRegex =
        new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

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
            var dpi = this.GetDpiForWindow();
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

        this.SetIcon(System.IO.Path.Combine("Assets", "icons", "vrtx.browse.ico"));

        ExpImpDel.ImportStatusChanged += OnImportStatusChanged;
        ExpImpDel.ConfirmOverwrite = ShowOverwriteDialogAsync;
        ExpImpDel.ConfirmNonResourceImport = ShowNonResourceDialogAsync;
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
            $"Select or drag & drop resource pack files here to import to {gameTitleText} (.mcpack, .zip)";

        PopulatePackBrowserAnnouncements();

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
            Trace.WriteLine($"[PackBrowser] Error setting drag region: {ex.Message}");
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
                paths.Add(file.Path);
        }

        if (paths.Count == 0) return;
        await RunImportAsync(() => ExpImpDel.ImportFromPathsAsync(paths));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Confirmation dialogs
    // ════════════════════════════════════════════════════════════════════════

    private async Task<bool> ShowOverwriteDialogAsync(string packName, string existingPath)
    {
        var tcs = new TaskCompletionSource<bool>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var existingFolderName = Path.GetFileName(
                    existingPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var dialog = new ContentDialog
                {
                    Title = "Pack already installed",
                    Content = $"\"{packName}\" is already installed at \"{existingFolderName}\".\n\nReplace it with the incoming version?",
                    PrimaryButtonText = "Replace",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PackBrowser] Overwrite dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Shown when a pack's manifest has no module of type "resources", or when the
    /// type could not be determined. Defaults to Skip (safe).
    /// </summary>
    private async Task<bool> ShowNonResourceDialogAsync(string packName)
    {
        var tcs = new TaskCompletionSource<bool>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Not a resource pack",
                    Content = $"\"{packName}\" does not appear to be a resource pack, no module of type \"resources\" was found in its manifest.\n\nImport it anyway?",
                    PrimaryButtonText = "Import anyway",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PackBrowser] Non-resource dialog error: {ex.Message}");
                tcs.SetResult(false);
            }
        });

        return await tcs.Task;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  JSON parsing — tolerant of // and /* */ comments in manifests
    // ════════════════════════════════════════════════════════════════════════

    private static JObject ParseManifestJson(string json)
    {
        try
        {
            using var sr = new StringReader(json);
            using var reader = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            var loadSettings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
            return JObject.Load(reader, loadSettings);
        }
        catch
        {
            return JObject.Parse(json);
        }
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
    //  Version string resolution
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a display version string from a manifest header version token.
    /// Accepts a three-element int array [1,26,15] or a strict X.Y.Z string.
    /// Anything else returns "Unknown" — matching the game's own fallback behaviour.
    /// For legacy manifests, pass the raw string via <paramref name="rawString"/>.
    /// </summary>
    private string ResolveVersion(JToken? versionToken, string? rawString = null)
    {
        if (versionToken != null)
        {
            if (versionToken.Type == JTokenType.Array)
            {
                var parts = versionToken.ToArray();
                if (parts.Length == 3 && parts.All(p => int.TryParse(p.ToString(), out int v) && v >= 0))
                    return string.Join(".", parts.Select(p => p.ToString()));
                return "Unknown";
            }

            if (versionToken.Type == JTokenType.String)
            {
                var s = versionToken.ToString();
                return StrictSemVerRegex.IsMatch(s) ? s : "Unknown";
            }

            return "Unknown";
        }

        if (!string.IsNullOrWhiteSpace(rawString))
            return StrictSemVerRegex.IsMatch(rawString) ? rawString : "Unknown";

        return "Unknown";
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
            Trace.WriteLine("[PackBrowser] Starting pack scan...");
            var packs = await ScanForCompatiblePacksAsync();
            Trace.WriteLine($"[PackBrowser] Found {packs.Count} packs");

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
            Trace.WriteLine("[PackBrowser] Pack loading complete");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBrowser] EXCEPTION in LoadPacksAsync: {ex}");
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SelectAll dropdown
    // ════════════════════════════════════════════════════════════════════════

    private void RebuildSelectAllDropdown()
    {
        var flyout = new MenuFlyout();

        var selectAll = new MenuFlyoutItem
        {
            Text = "Select all",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.SemiBold
        };
        selectAll.Click += (_, _) => SetAllPacksSelected(true);
        flyout.Items.Add(selectAll);

        var deselectAll = new MenuFlyoutItem
        {
            Text = "Deselect all",
            HorizontalAlignment =
            HorizontalAlignment.Stretch,
            FontWeight = FontWeights.SemiBold
        };
        deselectAll.Click += (_, _) => SetAllPacksSelected(false);
        flyout.Items.Add(deselectAll);

        if (_knownTags.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var tag in _knownTags)
            {
                var capturedTag = tag;
                var item = new MenuFlyoutItem { Text = $"Add all \"{capturedTag}\" to selections", HorizontalAlignment = HorizontalAlignment.Stretch };
                item.Click += (_, _) => SelectPacksByTag(capturedTag);
                flyout.Items.Add(item);
            }
        }

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

        // Columns: [icon 75] [gap 15] [info *] [gap 15] [right panel Auto]
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

        // Selection overlay
        var selectionOverlay = new Border
        {
            Width = 75,
            Height = 75,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ColorHelper.FromArgb(192, 0, 0, 0)),
            Translation = new System.Numerics.Vector3(0, 0, 128),
            Visibility = Visibility.Collapsed,
            Tag = "SelectionOverlay"
        };
        selectionOverlay.Child = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 72,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        iconContainer.Children.Add(iconBorder);
        iconContainer.Children.Add(selectionOverlay);
        Grid.SetColumn(iconContainer, 0);
        grid.Children.Add(iconContainer);

        // ── Pack name + description ──────────────────────────────────────────
        var nameBlock = new TextBlock
        {
            Text = pack.PackName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var descBlock = new TextBlock
        {
            Text = pack.PackDescription,
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(nameBlock);
        infoPanel.Children.Add(descBlock);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        // ── Right panel: [size | version] top-right, tags bottom-right ───────
        //
        // Row 0 holds a horizontal StackPanel with size badge on the left and
        // version badge on the right. Row 2 holds capability tags.
        var rightPanel = new Grid();
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top row: size badge + version badge side by side, right-aligned
        var topBadgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6
        };
        if (!string.IsNullOrEmpty(pack.PackSizeText))
            topBadgeRow.Children.Add(BuildSizeBadge(pack.PackSizeText)); // Only build size badge if not null or empty, it is, for now, intentionally disabled (returns empty all the time)
        topBadgeRow.Children.Add(BuildVersionBadge(pack.Version));
        Grid.SetRow(topBadgeRow, 0);
        rightPanel.Children.Add(topBadgeRow);

        // Bottom row: capability tags
        var tagsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 6
        };
        foreach (var tag in pack.CapabilityTags)
            tagsPanel.Children.Add(BuildTagBadge(tag));
        Grid.SetRow(tagsPanel, 2);
        rightPanel.Children.Add(tagsPanel);

        Grid.SetColumn(rightPanel, 4);
        grid.Children.Add(rightPanel);

        button.Content = grid;
        button.Click += PackButton_Click;
        return button;
    }

    private static Border BuildSizeBadge(string sizeText)
    {

        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 48, 48))
        };
        badge.Child = new TextBlock
        {
            Text = sizeText,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        return badge;
    }

    private static Border BuildVersionBadge(string version)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(ColorHelper.FromArgb(155, 32, 32, 32))
        };
        badge.Child = new TextBlock
        {
            Text = $"Version: {version}",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        return badge;
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

        // TODO: Could Animate the tags here later... It'd be fun, alchitex having its colors moving around
        // Glowing on and off for red one, RTX glowing constantly, glow effects would be nice, overall, but forget it if it comes to needing Win2D
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

        bool isNowSelected = !_selectedPaths.Contains(pack.PackPath);

        if (isNowSelected)
        {
            _selectedPaths.Add(pack.PackPath);
            overlay.Visibility = Visibility.Visible;
        }
        else
        {
            _selectedPaths.Remove(pack.PackPath);
            overlay.Visibility = Visibility.Collapsed;
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
            LoadingPanel.Visibility = Visibility.Visible;
            PackSelectionPanel.Visibility = Visibility.Collapsed;
            await LoadPacksAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            AddPackButton.IsEnabled = true;
            WindowTitle.Text = $"Select from your {gameTitleText} resource packs";
        }
    }

    private void OnImportStatusChanged(string message)
    {
        DispatcherQueue.TryEnqueue(() => WindowTitle.Text = message);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack scanning
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<PackData>> ScanForCompatiblePacksAsync()
    {
        var packs = new List<PackData>();
        var isTargetingPreview = TunerVariables.Persistent.IsTargetingPreview;

        if (!MinecraftUserDataLocator.IsDataValid(isTargetingPreview))
        {
            Trace.WriteLine($"[PackBrowser] {MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview)} data root not found.");
            return packs;
        }

        // Two passes per scan path: manifest.json first so modern always wins over legacy
        // in the same directory. .Concat() ordering was filesystem-dependent and unsafe.
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scanPath in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(
                     TunerVariables.Persistent.IsTargetingPreview))
        {
            // Pass 1: modern manifest.json
            foreach (var manifestPath in Directory.EnumerateFiles(scanPath, "manifest.json", SearchOption.AllDirectories))
            {
                var packDir = Path.GetDirectoryName(manifestPath);
                if (packDir == null || !seenDirs.Add(packDir)) continue;

                try
                {
                    var packData = await ParsePackAsync(packDir, manifestPath);
                    if (packData != null) packs.Add(packData);
                }
                catch (JsonException jsonEx) { Trace.WriteLine($"[PackBrowser] Invalid JSON in {manifestPath}: {jsonEx.Message}"); }
                catch (Exception ex) { Trace.WriteLine($"[PackBrowser] Error parsing pack {packDir}: {ex.Message}"); }
            }

            // Pass 2: legacy pack_manifest.json (seenDirs skips dirs already handled above)
            foreach (var manifestPath in Directory.EnumerateFiles(scanPath, "pack_manifest.json", SearchOption.AllDirectories))
            {
                var packDir = Path.GetDirectoryName(manifestPath);
                if (packDir == null || !seenDirs.Add(packDir)) continue;

                try
                {
                    var packData = await ParsePackAsync(packDir, manifestPath);
                    if (packData != null) packs.Add(packData);
                }
                catch (JsonException jsonEx) { Trace.WriteLine($"[PackBrowser] Invalid JSON in {manifestPath}: {jsonEx.Message}"); }
                catch (Exception ex) { Trace.WriteLine($"[PackBrowser] Error parsing pack {packDir}: {ex.Message}"); }
            }
        }

        return packs;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Manifest parsing — handles both manifest.json and pack_manifest.json
    // ════════════════════════════════════════════════════════════════════════

    private async Task<PackData?> ParsePackAsync(string packDir, string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        var root = ParseManifestJson(json);

        bool isLegacyFormat = Path.GetFileName(manifestPath)
            .Equals("pack_manifest.json", StringComparison.OrdinalIgnoreCase);

        return isLegacyFormat
            ? await ParseLegacyPackManifestAsync(packDir, root)
            : await ParseModernManifestAsync(packDir, root);
    }

    /// <summary>
    /// Parses the old pack_manifest.json format (pre-1.16 era).
    /// Always Incompatible — no capabilities field exists.
    /// Legacy packs are exempt from the Alchitex candidate tag; see
    /// <see cref="AlchitexLegacyPacksEligible"/>.
    /// </summary>
    private async Task<PackData> ParseLegacyPackManifestAsync(string packDir, JObject root)
    {
        var header = root["header"];

        string packName = StripMinecraftFormatting(header?["name"]?.ToString() ?? string.Empty);
        string packDesc = StripMinecraftFormatting(header?["description"]?.ToString() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(packName)) packName = Path.GetFileName(packDir);
        if (string.IsNullOrWhiteSpace(packDesc)) packDesc = Helpers.SanitizePathForDisplay(packDir);

        string rawVersion = header?["packs_version"]?.ToString()
                         ?? header?["version"]?.ToString()
                         ?? string.Empty;
        string version = ResolveVersion(versionToken: null, rawString: rawVersion);

        var capabilityTags = new List<string>();
        bool potentiallySuitable = false;

        // TODO: re-enable Alchitex candidate check for legacy packs if automatic
        //       manifest format upgrade is implemented downstream in Alchitex.
        if (AlchitexLegacyPacksEligible && AlchitexSuitabilityScanner.IsPotentiallySuitable(packDir))
        {
            potentiallySuitable = true;
            capabilityTags.Add(AlchitexCandidateTag);
        }

        capabilityTags.Add("Incompatible");

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = "Incompatible",
            Version = version,
            PackSizeText = await GetPackSizeTextAsync(packDir),
            IsLegacyFormat = true,
            PotentiallySuitableForPBRGen = potentiallySuitable
        };
    }

    /// <summary>
    /// Parses the modern manifest.json format.
    /// Version must be a three-element int array or a strict X.Y.Z string.
    /// </summary>
    private async Task<PackData> ParseModernManifestAsync(string packDir, JObject root)
    {
        var capabilityTags = new List<string>();
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

            if (hasRaytraced) { capabilityTags.Add("RTX"); packType = "RTX"; }
            if (hasPbr)
            {
                capabilityTags.Add("Vibrant Visuals");
                if (packType == "Incompatible") packType = "Vibrant Visuals";
            }
        }

        bool potentiallySuitable = false;
        if (packType == "Incompatible")
        {
            if (AlchitexSuitabilityScanner.IsPotentiallySuitable(packDir))
            {
                potentiallySuitable = true;
                capabilityTags.Add(AlchitexCandidateTag);
            }
            capabilityTags.Add("Incompatible");
        }

        string version = ResolveVersion(root["header"]?["version"]);

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

        packName = StripMinecraftFormatting(packName);
        packDesc = StripMinecraftFormatting(packDesc);

        if (packName == "pack.name" || string.IsNullOrWhiteSpace(packName))
            packName = Path.GetFileName(packDir);
        if (packDesc == "pack.description" || string.IsNullOrWhiteSpace(packDesc))
            packDesc = Helpers.SanitizePathForDisplay(packDir);

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = packType,
            Version = version,
            PackSizeText = await GetPackSizeTextAsync(packDir),
            IsLegacyFormat = false,
            PotentiallySuitableForPBRGen = potentiallySuitable
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Lang file loading — en_US first, en_GB fallback, then any other en_*
    // ════════════════════════════════════════════════════════════════════════

    private async Task<Dictionary<string, string>?> TryLoadLangFileAsync(string langFolder)
    {
        if (!Directory.Exists(langFolder)) return null;

        var langFiles = Directory.GetFiles(langFolder, "*.lang")
            .Where(f => Path.GetFileName(f).StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                return name switch
                {
                    "en_us" => 0,
                    "en_gb" => 1,
                    _ => 2
                };
            })
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
                Trace.WriteLine($"[PackBrowser] Error loading lang file {langPath}: {ex.Message}");
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Icon loading
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads a pack icon from disk, supports common types, but not Targa.
    /// </summary>
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
                Trace.WriteLine($"[PackBrowser] Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack size calculation
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a formatted size string for the pack directory, e.g. "12.34 MB".
    /// Runs the directory walk on a thread-pool thread to avoid blocking the UI.
    /// Returns "? MB" on any failure.
    /// </summary>
    private static async Task<string> GetPackSizeTextAsync(string packDir)
    {
        try
        {
            return string.Empty;

            var totalBytes = await Task.Run(() =>
                Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories)
                         .Sum(f =>
                         {
                             try { return new FileInfo(f).Length; }
                             catch { return 0L; }
                         }));

            double mb = totalBytes / (1024.0 * 1024.0);
            return mb.ToString("F2") + " MB";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBrowser] Error calculating size for {packDir}: {ex.Message}");
            return "? MB";
        }
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
                Trace.WriteLine($"[PackBrowser] Error scanning {packDir} for Alchitex suitability: {ex.Message}");
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
        public required string Version { get; set; }
        /// <summary>Pre-formatted pack folder size, e.g. "12.34 MB".</summary>
        public required string PackSizeText { get; set; }
        public bool IsLegacyFormat { get; set; } = false;
        public bool PotentiallySuitableForPBRGen { get; set; } = false;
    }
}
