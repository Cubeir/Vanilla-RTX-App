using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules.Alchitex.Core;
using Vanilla_RTX_App.Modules.Alchitex.Tools;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;

namespace Vanilla_RTX_App.Modules.Alchitex;


public static class AlchitexVariables
{
    public static class Persistent
    {
        // Mirrors SecondaryPbrModeComboBox.SelectedIndex 1:1 (0=None, 1=Auto,
        // 2=Normal, 3=Heightmap) rather than storing the enum directly - ApplicationData
        // LocalSettings needs a WinRT-projectable type, and int is the simplest one that
        // round-trips cleanly through the existing reflection-based Save/LoadSettings.
        public static int SecondaryPbrModeIndex = (int)SecondaryPbrMode.Auto;
        // On by default: the fog configs are what make a generated pack look like it
        // belongs in an RTX world rather than just having PBR textures bolted on.
        public static bool AddFogEnabled = true;
        // Off by default, and deliberately so - it deletes the user's own installed pack.
        public static bool DeleteOriginalPackEnabled = false;
    }
    public static class Defaults
    {

    }
    public static void SaveSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = field.GetValue(null);
            localSettings.Values[field.Name] = value;
        }
    }

    public static void LoadSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            try
            {
                if (localSettings.Values.ContainsKey(field.Name))
                {
                    var savedValue = localSettings.Values[field.Name];
                    var convertedValue = Convert.ChangeType(savedValue, field.FieldType);
                    field.SetValue(null, convertedValue);
                }
            }
            catch
            {
                Trace.WriteLine($"[AlchitexVariables] An issue occured loading settings");
            }
        }
    }
}

public sealed partial class Alchitex : Window
{
    private readonly AppWindow _appWindow;
    private bool _isClosing; // just a secondary guard in case a future code ends up closing a window while already closing
    private CancellationTokenSource? _generateCts;

    /// <summary>
    /// Set by MainWindow right after constructing this window (mirroring how it already
    /// sets size/position in LaunchAlchitexButton_Click) so orphaned-temp-folder cleanup
    /// scans the correct edition's resource-pack folders. Defaults to false (stable).
    /// </summary>
    public bool IsTargetingPreview { get; set; }

    /// <summary>
    /// Mirrors the sibling modules' report-on-close convention (BetterRTX/DLSS/LUT
    /// managers): MainWindow's Closed handler for this window reads these once it closes
    /// and logs accordingly. True only if at least one pack succeeded across the whole
    /// window session (every Generate click, not just the last one) - a mix of some
    /// successes and some failures still reports as successful, since StatusMessage
    /// itself already separates the two lists clearly.
    /// </summary>
    public bool OperationSuccessful { get; private set; }
    public string StatusMessage { get; private set; } = "";

    // Accumulated across every Generate click in this window's lifetime, not just the
    // most recent one - the user can click Generate more than once before closing.
    private readonly List<string> _succeededPackNames = new();
    private readonly List<string> _failedPackNames = new();
    // Originals uninstalled by the "Uninstall the original pack" toggle - reported on
    // close so the user has a record of what was removed on their behalf.
    private readonly List<string> _removedOriginalNames = new();

    private string AlchitexAssetsPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets");

    private static string LicenseAcceptedKey = $"Alchitex_LicenseAccepted_{TunerVariables.appVersion}";

    public Alchitex()
    {
        this.InitializeComponent();

        var manager = WinUIEx.WindowManager.Get(this);
        manager.MinWidth = TunerVariables.WindowMinSizeX;
        manager.MinHeight = TunerVariables.WindowMinSizeY;
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

        this.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets", "logo.large.ico"));

        this.Closed += Alchitex_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += Alchitex_Loaded;
    }
    private async void Alchitex_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is FrameworkElement root)
                root.Loaded -= Alchitex_Loaded;

            if (_isClosing) return;

            SetTitleBar(TitleBarDragArea);

            AlchitexVariables.LoadSettings();
            PopulateAlchitexAnnouncements();

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Alchitex] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    private void Alchitex_Closed(object sender, WindowEventArgs e)
    {
        // SaveSettings() only ever persists whatever's currently sitting in
        // AlchitexVariables.Persistent's static fields - those used to only get updated
        // when Generate was clicked (ReadOptionsFromUI), so changing a toggle/dropdown and
        // closing without generating silently lost the change. Sync from the live
        // controls first. Guarded on MainGrid actually being shown, so closing during the
        // license screen (controls never touched, still at XAML defaults) doesn't stomp
        // whatever was already saved from a previous session.
        if (MainGrid.Visibility == Visibility.Visible)
            SyncPersistentSettingsFromControls();

        AlchitexVariables.SaveSettings();

        if (_isClosing) return;
        _isClosing = true;

        if (Content is FrameworkElement root)
            root.Loaded -= Alchitex_Loaded;

        ThemeService.ThemeChanged -= ApplyTheme;
        this.Closed -= Alchitex_Closed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }
    private void PopulateAlchitexAnnouncements()
    {
        var items = OnlineTexts.GetFiltered(OnlineTextsContent.AlchitexDevProgressUpdates);
        if (items is null) return;
        foreach (var item in items)
            AlchitexAnnouncementsPanel.Children.Add(new PsaCard(item));
    }

    // ── Init ────────────────────────────────────────────
    private async Task InitializeAsync()
    {
        try
        {
            bool accepted = await CheckLicenseAcceptedAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;

            if (!accepted)
            {
                await PopulateLicenseTextAsync();
                LicensePanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowMainContent();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] EXCEPTION in Alchitex.InitializeAsync: {ex}");
            this.Close();
        }
    }
    // ── License ─────────────────────────────────────────
    private static Task<bool> CheckLicenseAcceptedAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            var val = settings.Values[LicenseAcceptedKey];
            return Task.FromResult(val is bool b && b);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error reading license key: {ex.Message}");
            return Task.FromResult(false);
        }
    }
    private async Task PopulateLicenseTextAsync()
    {
        try
        {
            var uri = new Uri("ms-appx:///Modules/Alchitex/Assets/LICENSE.txt");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            var body = await FileIO.ReadTextAsync(file);

            LicenseTextBlock.Blocks.Clear();

            // ── "Online version" header ──────────────────────────────────────
            var headerPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 2) };
            headerPara.Inlines.Add(new Run { Text = "Online version:  " });
            var link = new Hyperlink
            {
                NavigateUri = new Uri("https://github.com/Cubeir/Vanilla-RTX-App/blob/main/src/Modules/Alchitex/LICENSE.txt")
            };
            link.Inlines.Add(new Run { Text = "View on GitHub" });
            headerPara.Inlines.Add(link);
            LicenseTextBlock.Blocks.Add(headerPara);

            // ── Separator ────────────────────────────────────────────────────
            var sepPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 8) };
            sepPara.Inlines.Add(new Run
            {
                Text = "───────────────────────────────────────",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            LicenseTextBlock.Blocks.Add(sepPara);

            // ── Body ─────────────────────────────────────────────────────────
            var bodyPara = new Paragraph();
            bodyPara.Inlines.Add(new Run { Text = body });
            LicenseTextBlock.Blocks.Add(bodyPara);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error loading license text: {ex.Message}");
            LicenseTextBlock.Blocks.Clear();
            var err = new Paragraph();
            err.Inlines.Add(new Run { Text = $"Could not load license file: {ex.Message}" });
            LicenseTextBlock.Blocks.Add(err);
        }
    }
    private void DisagreeButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
    private async void AgreeButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LicenseAcceptedKey] = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Error writing license key: {ex.Message}");
            }
            return Task.CompletedTask;
        });
        LicensePanel.Visibility = Visibility.Collapsed;
        ShowMainContent();
    }


    // ── Main Content ───────────────────────────────────────────────────────

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        _ = MainWindow.OpenUrl("http://minecraftrtx.net/reactor");
    }

    // ── Reveal main content ───────────────────────────────────────────────────

    // Main content are hidden before license is accepted
    /// <summary>
    /// Reveals the app once the license has been accepted.
    ///
    /// The whole post-license UI lives inside exactly two containers - TitleBarActions and
    /// MainGrid - and children inherit a collapsed parent, so revealing it is those two
    /// lines and only those two lines. Adding a new control after the license screen needs
    /// nothing here: put it inside either container and it comes along for free. Please
    /// keep it that way rather than collapsing new controls individually and re-showing
    /// them here, which is what this used to do.
    ///
    /// The only things that still belong in this method are ones that aren't about license
    /// state at all: title text, restoring persisted control values, and the debug-only
    /// group (a different axis entirely - build configuration, not license acceptance).
    /// </summary>
    private void ShowMainContent()
    {
        SetStatus(null);

        TitleBarActions.Visibility = Visibility.Visible;
        MainGrid.Visibility = Visibility.Visible;

        SecondaryPbrModeComboBox.SelectedIndex = AlchitexVariables.Persistent.SecondaryPbrModeIndex;
        AddFogToggle.IsOn = AlchitexVariables.Persistent.AddFogEnabled;
        DeleteOriginalToggle.IsOn = AlchitexVariables.Persistent.DeleteOriginalPackEnabled;

        _ = RebuildPackIconsAsync();

#if DEBUG
        DevOnlyTitleBarActions.Visibility = Visibility.Visible;
#endif
    }

    // ── Titlebar status line ─────────────────────────────────────────────────

    private const string DefaultTitleText = "RTX Reactor";

    // Cancels a pending "revert the title back to RTX Reactor" when something else wants
    // to write to the titlebar first (a new run, mostly).
    private CancellationTokenSource? _titleRevertCts;

    /// <summary>
    /// Writes the generation status into the titlebar, which is where it lives now that
    /// there's no status TextBlock in the content area. Passing null (or empty) restores
    /// "RTX Reactor".
    /// </summary>
    private void SetStatus(string? text)
    {
        _titleRevertCts?.Cancel();
        _titleRevertCts?.Dispose();
        _titleRevertCts = null;

        TitleBarText.Text = string.IsNullOrEmpty(text) ? DefaultTitleText : text;
    }

    /// <summary>
    /// Leaves a run's final line up long enough to actually be read, then puts the title
    /// back. Any SetStatus call in the meantime cancels the revert, so a second Generate
    /// click never gets its status stomped by the previous run's timer.
    /// </summary>
    private void SetStatusThenRevert(string text, int millisecondsBeforeRevert = 8000)
    {
        SetStatus(text);

        var cts = new CancellationTokenSource();
        _titleRevertCts = cts;

        _ = Task.Delay(millisecondsBeforeRevert, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || _isClosing) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!cts.IsCancellationRequested && !_isClosing)
                    TitleBarText.Text = DefaultTitleText;
            });
        }, TaskScheduler.Default);
    }

    // ── Queued pack icons ────────────────────────────────────────────────────

    private const int PackIconSize = 128;
    private const int PackIconSpacing = 12;
    private const int PackIconMaxRows = 2;

    // Locations this window session has already generated for. They drop out of the queue
    // display as they're processed, so the strip empties as a run progresses - which is
    // also the hook the "pack flies into the button" animation will need later.
    private readonly HashSet<string> _processedLocations = new(StringComparer.OrdinalIgnoreCase);

    // Cached one-per-pack so a relayout (window resize) doesn't re-read every icon file.
    private readonly Dictionary<string, BitmapImage?> _packIconCache = new(StringComparer.OrdinalIgnoreCase);

    private List<(string Location, string Name)> QueuedPacks() => TunerVariables.SelectedPacks
        .Where(p => !_processedLocations.Contains(p.Location))
        .Where(p => !string.IsNullOrEmpty(p.Location) && System.IO.Directory.Exists(p.Location))
        .Select(p => (p.Location, p.Name))
        .ToList();

    /// <summary>
    /// Loads any icons not cached yet, then lays the strip out. Call after the queue
    /// changes (window shown, a pack finished, a pack's folder deleted); plain resizes go
    /// through LayoutPackIcons directly since nothing needs re-reading from disk.
    /// </summary>
    private async Task RebuildPackIconsAsync()
    {
        var packs = QueuedPacks();

        foreach (var (location, _) in packs)
        {
            if (_packIconCache.ContainsKey(location)) continue;
            _packIconCache[location] = await PackBrowserWindow.LoadPackIconAsync(location);
        }

        LayoutPackIcons();
    }

    /// <summary>
    /// Fills the icon area with 128px tiles, as many per row as actually fit at the current
    /// window width, up to two rows. If there are more packs than cells, the last cell
    /// becomes a "+N" count instead of an icon - so the strip never silently hides packs
    /// that are still queued.
    /// </summary>
    private void LayoutPackIcons()
    {
        if (PackIconsHost == null) return;

        PackIconsHost.Children.Clear();
        PackIconsHost.RowDefinitions.Clear();
        PackIconsHost.ColumnDefinitions.Clear();

        var packs = QueuedPacks();
        if (packs.Count == 0) return;

        var availableWidth = PackIconsHost.ActualWidth;
        if (availableWidth <= 0) return; // pre-layout; SizeChanged brings us back

        var columns = Math.Max(1, (int)((availableWidth + PackIconSpacing) / (PackIconSize + PackIconSpacing)));
        var capacity = columns * PackIconMaxRows;

        // One cell has to be given up to the "+N" tile when the queue doesn't fit.
        var iconCount = packs.Count <= capacity ? packs.Count : capacity - 1;
        var overflow = packs.Count - iconCount;

        var rows = (int)Math.Ceiling((iconCount + (overflow > 0 ? 1 : 0)) / (double)columns);

        for (var r = 0; r < rows; r++)
            PackIconsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns; c++)
            PackIconsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void Place(FrameworkElement element, int index)
        {
            Grid.SetRow(element, index / columns);
            Grid.SetColumn(element, index % columns);
            element.Margin = new Thickness(0, 0, PackIconSpacing, PackIconSpacing);
            PackIconsHost.Children.Add(element);
        }

        for (var i = 0; i < iconCount; i++)
        {
            var (location, name) = packs[i];
            _packIconCache.TryGetValue(location, out var icon);
            Place(BuildPackIconTile(icon, name), i);
        }

        if (overflow > 0)
            Place(BuildOverflowTile(overflow), iconCount);
    }

    private static Border BuildPackIconTile(BitmapImage? icon, string packName)
    {
        var tile = new Border
        {
            Width = PackIconSize,
            Height = PackIconSize,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 96, 96, 96)),
        };

        ToolTipService.SetToolTip(tile, PackBrowserWindow.StripMinecraftFormatting(packName));

        if (icon != null)
        {
            tile.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            // Same fallback chain the pack browser uses for a pack with no readable icon.
            try
            {
                tile.Child = new Image
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/missing.png")),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                tile.Child = new FontIcon
                {
                    Glyph = "",
                    FontSize = 40,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        return tile;
    }

    private static Grid BuildOverflowTile(int count)
    {
        var tile = new Grid { Width = PackIconSize, Height = PackIconSize };

        tile.Children.Add(new TextBlock
        {
            Text = $"+{count}",
            FontSize = 44,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        });

        ToolTipService.SetToolTip(tile, $"{count} more pack{(count == 1 ? "" : "s")} queued");
        return tile;
    }

    private void PackIconsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
            LayoutPackIcons();
    }

    /// <summary>
    /// Keeps the announcements panel filling whatever's left of the window under the fixed
    /// 512px controls area. Without this the acrylic panel would stop at its last card and
    /// leave the window background showing beneath it.
    /// </summary>
    private void MainScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var progressBarHeight = GenerateProgressBar.Visibility == Visibility.Visible
            ? GenerateProgressBar.ActualHeight
            : 0;

        AnnouncementBackground.MinHeight = Math.Max(0, e.NewSize.Height - 512 - progressBarHeight);
    }

    // ── PBR generation ───────────────────────────────────────────────────────

    /// <summary>Reads the live control values into AlchitexVariables.Persistent. Shared by
    /// ReadOptionsFromUI (on Generate) and Alchitex_Closed (on window close) so a
    /// toggle/dropdown change is captured regardless of which one happens first.</summary>
    private void SyncPersistentSettingsFromControls()
    {
        var modeIndex = SecondaryPbrModeComboBox.SelectedIndex;
        if (modeIndex < 0) modeIndex = (int)SecondaryPbrMode.Auto;

        AlchitexVariables.Persistent.SecondaryPbrModeIndex = modeIndex;
        AlchitexVariables.Persistent.AddFogEnabled = AddFogToggle.IsOn;
        AlchitexVariables.Persistent.DeleteOriginalPackEnabled = DeleteOriginalToggle.IsOn;
    }

    private AlchitexOptions ReadOptionsFromUI()
    {
        SyncPersistentSettingsFromControls();
        AlchitexVariables.SaveSettings();

        return new AlchitexOptions(
            (SecondaryPbrMode)AlchitexVariables.Persistent.SecondaryPbrModeIndex,
            AlchitexVariables.Persistent.AddFogEnabled);
    }

    private void SetGenerationControlsEnabled(bool enabled)
    {
        // The Generate button stays on screen while disabled now that it's the giant logo -
        // hiding the centrepiece of the window mid-run would read as the UI breaking.
        GenerateButton.IsEnabled = enabled;
        AbortButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        AbortButton.IsEnabled = !enabled;
        SecondaryPbrModeComboBox.IsEnabled = enabled;
        AddFogToggle.IsEnabled = enabled;
        DeleteOriginalToggle.IsEnabled = enabled;
        GenerateMaterialsConfigButton.IsEnabled = enabled;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        // Every selected pack is eligible now, not just the ones tagged as candidates -
        // see the confirmation phase below for what that costs the user.
        var selected = TunerVariables.SelectedPacks.ToList();
        if (selected.Count == 0)
        {
            SetStatusThenRevert("No packs selected.");
            return;
        }

        var options = ReadOptionsFromUI();
        var deleteOriginals = AlchitexVariables.Persistent.DeleteOriginalPackEnabled;

        SetGenerationControlsEnabled(false);

        // Created *before* the confirmation dialogs rather than just before the batch:
        // SetGenerationControlsEnabled has already swapped Generate out for Abort, so the
        // button is sitting right there while the user works through the dialogs, and it
        // would do nothing at all against a null token source.
        _generateCts = new CancellationTokenSource();

        var succeeded = 0;
        var failedNames = new List<string>();
        var aborted = false;

        try
        {
            // ── Confirmation phase ───────────────────────────────────────────
            // Asked up front, for every pack, before any work begins - a dialog appearing
            // partway through a running batch (over a live progress bar, after some packs
            // are already written) would be a much worse place to ask.
            var queue = await BuildGenerationQueueAsync(selected, _generateCts.Token);

            if (_generateCts.IsCancellationRequested)
            {
                SetStatusThenRevert("Aborted before generating anything.");
                return;
            }

            if (queue.Count == 0)
            {
                SetStatusThenRevert(selected.Count == 1
                    ? "Skipped - nothing left to generate."
                    : "Skipped every selected pack - nothing left to generate.");
                return;
            }

            GenerateProgressBar.Visibility = Visibility.Visible;
            GenerateProgressBar.IsIndeterminate = true;
            SetStatus("Cleaning up leftovers from any previous run...");

            // Every batch starts by sweeping any alchitex_temp_* folder left behind by a
            // previous run that didn't finish (crash, force-close, a prior Abort). Cheap,
            // and means debris never has a chance to accumulate across sessions.
            await Task.Run(() => AlchitexStaging.CleanupOrphanedTempFolders(IsTargetingPreview));

            SetStatus($"Preparing ({queue.Count} pack{(queue.Count == 1 ? "" : "s")})...");

            for (var i = 0; i < queue.Count; i++)
            {
                if (_generateCts.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var (pack, stripExistingPbr) = queue[i];
                var packIndex = i; // captured for the progress closure below

                // Progress<T> captures this thread's SynchronizationContext at construction
                // time (we're on the UI thread here), so every callback from inside
                // AlchitexPipeline.RunAsync - which runs its heavy work via Task.Run/
                // Parallel.ForEach on background threads - gets automatically marshalled
                // back to the UI thread. Safe to touch controls directly below.
                var progress = new Progress<AlchitexPipeline.AlchitexProgress>(p =>
                {
                    GenerateProgressBar.IsIndeterminate = p.Total == 0;
                    if (p.Total > 0)
                    {
                        GenerateProgressBar.Maximum = p.Total;
                        GenerateProgressBar.Value = p.Completed;
                    }
                    SetStatus($"[{packIndex + 1}/{queue.Count}] {pack.Name}: {p.StatusText}");
                });

                var result = await AlchitexPipeline.RunAsync(
                    pack.Location,
                    pack.Name,
                    stripExistingPbr ? options with { StripExistingPbr = true } : options,
                    AlchitexAssetsPath,
                    TunerVariables.appVersion,
                    progress,
                    _generateCts.Token);

                if (result.Success)
                {
                    succeeded++;
                    _succeededPackNames.Add(result.FinalManifestName ?? pack.Name);

                    // Only ever after a fully successful run for this pack - a failed or
                    // aborted one leaves the user's original exactly where it was.
                    if (deleteOriginals)
                    {
                        SetStatus($"[{packIndex + 1}/{queue.Count}] {pack.Name}: Uninstalling the original pack...");
                        await DeleteOriginalPackAsync(pack.Location, pack.Name);
                    }
                }
                else if (_generateCts.IsCancellationRequested) { aborted = true; break; }
                else
                {
                    failedNames.Add(pack.Name);
                    _failedPackNames.Add(pack.Name);
                }

                // Whether it succeeded or not, this pack is done being queued - drop it
                // from the icon strip so what's left on screen is what's still coming.
                _processedLocations.Add(pack.Location);
                await RebuildPackIconsAsync();
            }

            if (aborted)
            {
                SetStatusThenRevert($"Aborted - {succeeded} pack(s) completed before stopping.");
            }
            else
            {
                SetStatusThenRevert(failedNames.Count == 0
                    ? $"Done - {succeeded}/{queue.Count} pack(s) processed successfully."
                    : $"Done - {succeeded}/{queue.Count} succeeded. Failed: {string.Join(", ", failedNames)}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] GenerateButton_Click failed: {ex}");
            SetStatusThenRevert($"Something went wrong: {ex.Message}");
        }
        finally
        {
            // Whatever just happened - success, a plain failure, or Abort - any temp
            // copy that didn't make it to promotion is still sitting there. Sweep now
            // rather than waiting for the next Generate click, so an aborted run's
            // half-done pack doesn't linger in the resource_packs folder in the meantime.
            await Task.Run(() => AlchitexStaging.CleanupOrphanedTempFolders(IsTargetingPreview));

            GenerateProgressBar.IsIndeterminate = false;
            SetGenerationControlsEnabled(true);
            _generateCts?.Dispose();
            _generateCts = null;

            // Refresh even on an exception mid-batch - whatever succeeded before the
            // exception is still worth reporting when the window closes.
            UpdateSessionSummary();
        }
    }

    // ── "Uninstall the original pack" ────────────────────────────────────────

    /// <summary>
    /// Deletes the pack the RTX version was generated from, via the same
    /// ExpImpDel.DeletePackAsync the main window's Delete button uses - including its
    /// guard against deleting anything that isn't inside a real resource-packs folder.
    /// No confirmation dialog: the toggle IS the confirmation, and it was answered before
    /// the run started.
    ///
    /// Also drops the pack from TunerVariables.SelectedPacks. It's app-wide state and the
    /// folder is gone, so leaving it selected would hand a dead path to Export/Tune/Delete
    /// back in the main window.
    ///
    /// A failure here is reported but never fails the pack: its RTX version generated fine,
    /// which is what the user actually asked for.
    /// </summary>
    private async Task DeleteOriginalPackAsync(string location, string packName)
    {
        try
        {
            if (await ExpImpDel.DeletePackAsync(location) != null)
            {
                _removedOriginalNames.Add(packName);

                for (var i = TunerVariables.SelectedPacks.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(TunerVariables.SelectedPacks[i].Location, location, StringComparison.OrdinalIgnoreCase))
                        TunerVariables.SelectedPacks.RemoveAt(i);
                }
            }
            else
            {
                Trace.WriteLine($"[ALCHITEX] Couldn't uninstall the original pack at '{location}' - its RTX version was still generated.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed uninstalling the original pack at '{location}': {ex.Message}");
        }
    }

    // ── Confirmation phase ───────────────────────────────────────────────────

    private readonly record struct QueuedPack(
        (string Location, string Name, string Type, bool IsAlchitexCandidate) Pack,
        bool StripExistingPbr);

    /// <summary>
    /// Turns the user's selection into the list of packs to actually generate for, asking
    /// about the ones that warrant a question first. Three cases:
    ///
    ///   * Tagged as an RTX Reactor candidate - exactly what this tool is for. No dialog.
    ///   * Already declaring "raytraced"/"pbr" - asks whether to wipe the PBR it ships and
    ///     regenerate from its color textures (AlchitexOptions.StripExistingPbr, applied to
    ///     the staged copy only). Increasingly this is a pack that declares a capability
    ///     while shipping almost no PBR content, which is the whole reason this path exists.
    ///   * Neither - a warning that there may be little here to work with, and a way out.
    ///
    /// Declining just drops that pack from the queue; the rest of the selection still runs.
    /// A pack's own Name is used verbatim from the selection (already resolved from the
    /// manifest or a .lang file upstream, formatting codes stripped) - nothing here re-reads
    /// a manifest to label a dialog.
    /// </summary>
    private async Task<List<QueuedPack>> BuildGenerationQueueAsync(
        List<(string Location, string Name, string Type, bool IsAlchitexCandidate)> selected,
        CancellationToken cancellationToken)
    {
        var queue = new List<QueuedPack>();

        foreach (var pack in selected)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var alreadyDeclaresPbr = pack.Type is "RTX" or "Vibrant Visuals";

            if (!alreadyDeclaresPbr && pack.IsAlchitexCandidate)
            {
                queue.Add(new QueuedPack(pack, StripExistingPbr: false));
                continue;
            }

            SetStatus($"Waiting for your decision on {pack.Name}...");

            var confirmed = alreadyDeclaresPbr
                ? await ConfirmRegenerateExistingPbrAsync(pack.Name, pack.Type)
                : await ConfirmUnsuitablePackAsync(pack.Name);

            if (confirmed)
                queue.Add(new QueuedPack(pack, StripExistingPbr: alreadyDeclaresPbr));
        }

        return queue;
    }

    /// <summary>Asked once per already-PBR pack. Defaults to Skip - the destructive option
    /// shouldn't be the one a stray Enter picks.</summary>
    private async Task<bool> ConfirmRegenerateExistingPbrAsync(string packName, string packType)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"{packName} might already have PBR textures!",
                Content =
                    $"\"{packName}\" declares itself as {packType} compatible, so it may already have its own PBR textures. " +
                    "RTX Reactor can delete all of them and generate a complete new set from the pack's color " +
                    "textures. Your installed copy is never modified - all of this happens on a copy, which becomes " +
                    "a separate RTX pack.\n\n" +
                    "This is worth doing for packs that declare Vibrant Visuals or RTX support but barely ship any " +
                    "PBR content. If this pack's own PBR work is good, the generated copy will not have it.",
                PrimaryButtonText = "Remove and regenerate",
                CloseButtonText = "Skip this pack",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // A dialog that couldn't be shown must not silently green-light a destructive
            // pass on someone's pack.
            Trace.WriteLine($"[ALCHITEX] Couldn't show the regenerate-PBR dialog for '{packName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Asked once per pack that's neither already-PBR nor a tagged candidate -
    /// nothing here is destructive, the pack just may not have much for us to work with.</summary>
    private async Task<bool> ConfirmUnsuitablePackAsync(string packName)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"{packName} may not be suitable for RTX enhancement!",
                Content =
                    $"\"{packName}\" isn't tagged as an \"{PackBrowserWindow.AlchitexCandidateTag}\" - it has few block " +
                    "textures to work with, or uses a pack format too old to build RTX support on.\n\n" +
                    "You can still run it. Your installed copy is left untouched and the result is " +
                    "a separate RTX-compatible pack, so there's nothing to lose, but it may come out with little to " +
                    "nothing added to it, or it may not work at all.",
                PrimaryButtonText = "Generate anyway",
                CloseButtonText = "Skip this pack",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't show the unsuitable-pack dialog for '{packName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds StatusMessage/OperationSuccessful from every pack this window session has
    /// touched so far (see _succeededPackNames/_failedPackNames) - MainWindow's Closed
    /// handler for this window reads these once the window actually closes, mirroring how
    /// BetterRTX/DLSS/LUT manager windows report their own outcome.
    /// </summary>
    private void UpdateSessionSummary()
    {
        if (_succeededPackNames.Count == 0 && _failedPackNames.Count == 0)
        {
            StatusMessage = "";
            OperationSuccessful = false;
            return;
        }

        var sb = new StringBuilder();

        if (_succeededPackNames.Count > 0)
        {
            sb.AppendLine($"Added the following RTX-capable pack{(_succeededPackNames.Count == 1 ? "" : "s")}:");
            foreach (var name in _succeededPackNames)
                sb.AppendLine($"{PackBrowserWindow.StripMinecraftFormatting(name)}");
        }
        if (_succeededPackNames.Count > 0)
        {
            string pronouns = _succeededPackNames.Count == 1 ? "it" : "them";
            sb.AppendLine();
            sb.Append($"ℹ️ You can now activate {pronouns} in-game, you may also select {pronouns} from the Select other packs menu to Export or Tune {pronouns} from the main menu.");
        }

        if (_removedOriginalNames.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"🗑️ Uninstalled the original pack{(_removedOriginalNames.Count == 1 ? "" : "s")} they were generated from:");
            foreach (var name in _removedOriginalNames)
                sb.AppendLine($"* {PackBrowserWindow.StripMinecraftFormatting(name)}");
        }

        if (_failedPackNames.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("⚠️ Partially (or fully) failed to add RTX support to the following:");
            sb.AppendLine();
            foreach (var name in _failedPackNames)
                sb.AppendLine($"* {PackBrowserWindow.StripMinecraftFormatting(name)}");
            sb.Append($"ℹ️ Better luck with another pack!");
        }

        StatusMessage = sb.ToString().TrimEnd();
        OperationSuccessful = _succeededPackNames.Count > 0;
    }

    private void AbortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_generateCts == null || _generateCts.IsCancellationRequested) return;

        SetStatus("Aborting...");
        AbortButton.IsEnabled = false; // avoid double-cancel; re-enabled by SetGenerationControlsEnabled once the run unwinds
        _generateCts.Cancel();
    }

    // ── Debug: materials.json bootstrap ─────────────────────────────────────

    private async void GenerateMaterialsConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var sourceFolder = await PickFolderAsync();
        if (sourceFolder == null) return;

        var outputPath = await PickSaveMaterialsFileAsync();
        if (outputPath == null) return;

        SetGenerationControlsEnabled(false);
        GenerateProgressBar.Visibility = Visibility.Visible;
        GenerateProgressBar.IsIndeterminate = true;
        SetStatus("Reading texture sets and deriving materials.json...");

        try
        {
            var result = await Task.Run(() => MaterialsBootstrapper.GenerateFromExistingPack(sourceFolder, outputPath));
            SetStatusThenRevert($"materials.json updated: {result.EntriesWritten} new entries " +
                                $"({result.Skipped} skipped, {result.Failed} failed) -> {result.OutputPath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] GenerateMaterialsConfigButton_Click failed: {ex}");
            SetStatusThenRevert($"Failed to generate materials.json: {ex.Message}");
        }
        finally
        {
            GenerateProgressBar.IsIndeterminate = false;
            GenerateProgressBar.Visibility = Visibility.Collapsed;
            SetGenerationControlsEnabled(true);
        }
    }

    /// <summary>
    /// Single-folder picker helper, used for the bootstrap button's source pack.
    /// Debug-only tool, so the standard OS folder picker is simplest to build and least
    /// likely to need maintenance later.
    /// </summary>
    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    /// Save-file picker for the bootstrap button's output materials.json - lets the
    /// artist target their existing materials.json directly (for the merge/append
    /// workflow - see MaterialsBootstrapper) instead of picking a destination folder and
    /// always landing on a fixed filename.
    /// </summary>
    private async Task<string?> PickSaveMaterialsFileAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = "materials";
        picker.DefaultFileExtension = ".json";
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}


/* ── The Backlogs and Scattered Ideas ─────────────────────────────────────────────────────
 * 
 * Idea: don't even bother putting Alchitex on a new window, its special, and probably will end up with a codebase the same size as the rest of features combined
 * So, here's what, Hide the MainWindow MainGrid, then display alchitex content...
 * simple! Don't launch it in a separate window
 * You could strip out parts of titlebar content so it remains intact upon clicking Reactor button
 * "Take to RTX Reactor"
 * Could animate the background coming up, cool ideas could be executed here. 
 * 
// Potentially rename to Alchemist, PBR Alchemist or RTX Reactor or ARCHITEX or ALCHETEX before release.

// Perfect the licensing windows' appearance

// Review: is it a good idea to limit features lifecycle to their windows? In general... should it all ahve been on the main window?
// well, you see, in your case, navigation view would've been very generic
// and some modules like alchitex might become too heavy, so yes, making the main window act like a nexus hub that spawns child apps is better...
// they have minimal communication/interactions, its like main window is a father responsible for them with all of the logs n things
// navigation view is also nice... think about it, just think, u love the way your buttons look, don't want them to go!

// REDSTONE ELEMENT IMPLEMENTAITON IDEA:
// We got the tile backgrounds
// Beneath there, have PROCEDURALLY GENERATED redstone going Upward from below, that makes 2 layers of bitmaps!
// still do it like u had in mind, tiles exist, images are dynamically selected based on neighbors
// Then, have a toggle, like the lamp, to either trigger random flashes, or continous random power flashes in the redstone
// A nice way to convey something being done in the background!
// This is the way, and is actually imeplementable, unlike earlier versions of the idea. (how were to understand which areas are... to trigger)
// it isn't too convoluted, and is gonna look AMAZING.
/// 
*/
