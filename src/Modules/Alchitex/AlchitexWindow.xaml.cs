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
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
        public static bool AddFogEnabled = false;
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
        TitleBarText.Text = "RTX Reactor";

        TitleBarActions.Visibility = Visibility.Visible;
        MainGrid.Visibility = Visibility.Visible;

        SecondaryPbrModeComboBox.SelectedIndex = AlchitexVariables.Persistent.SecondaryPbrModeIndex;
        AddFogToggle.IsChecked = AlchitexVariables.Persistent.AddFogEnabled;

#if DEBUG
        DevOnlyTitleBarActions.Visibility = Visibility.Visible;
#endif
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
        AlchitexVariables.Persistent.AddFogEnabled = AddFogToggle.IsChecked ?? false;
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
        GenerateButton.IsEnabled = enabled;
        GenerateButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AbortButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        AbortButton.IsEnabled = !enabled;
        SecondaryPbrModeComboBox.IsEnabled = enabled;
        AddFogToggle.IsEnabled = enabled;
        GenerateMaterialsConfigButton.IsEnabled = enabled;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var candidates = TunerVariables.SelectedPacks.Where(p => p.IsAlchitexCandidate).ToList();
        if (candidates.Count == 0)
        {
            GenerateStatusText.Text = "No candidate packs selected.";
            GenerateStatusText.Visibility = Visibility.Visible;
            return;
        }

        var options = ReadOptionsFromUI();

        SetGenerationControlsEnabled(false);
        GenerateProgressBar.Visibility = Visibility.Visible;
        GenerateProgressBar.IsIndeterminate = true;
        GenerateStatusText.Visibility = Visibility.Visible;
        GenerateStatusText.Text = "Cleaning up leftovers from any previous run...";

        // Every batch starts by sweeping any alchitex_temp_* folder left behind by a
        // previous run that didn't finish (crash, force-close, a prior Abort). Cheap,
        // and means debris never has a chance to accumulate across sessions.
        await Task.Run(() => AlchitexStaging.CleanupOrphanedTempFolders(IsTargetingPreview));

        GenerateStatusText.Text = $"Preparing ({candidates.Count} pack{(candidates.Count == 1 ? "" : "s")})...";

        _generateCts = new CancellationTokenSource();
        var succeeded = 0;
        var failedNames = new System.Collections.Generic.List<string>();
        var aborted = false;

        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (_generateCts.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var pack = candidates[i];
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
                    GenerateStatusText.Text = $"[{packIndex + 1}/{candidates.Count}] {pack.Name}: {p.StatusText}";
                });

                var result = await AlchitexPipeline.RunAsync(
                    pack.Location,
                    pack.Name,
                    options,
                    AlchitexAssetsPath,
                    TunerVariables.appVersion,
                    progress,
                    _generateCts.Token);

                if (result.Success)
                {
                    succeeded++;
                    _succeededPackNames.Add(result.FinalManifestName ?? pack.Name);
                }
                else if (_generateCts.IsCancellationRequested) { aborted = true; break; }
                else
                {
                    failedNames.Add(pack.Name);
                    _failedPackNames.Add(pack.Name);
                }
            }

            if (aborted)
            {
                GenerateStatusText.Text = $"Aborted - {succeeded} pack(s) completed before stopping.";
            }
            else
            {
                GenerateStatusText.Text = failedNames.Count == 0
                    ? $"Done - {succeeded}/{candidates.Count} pack(s) processed successfully."
                    : $"Done - {succeeded}/{candidates.Count} succeeded. Failed: {string.Join(", ", failedNames)}";
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] GenerateButton_Click failed: {ex}");
            GenerateStatusText.Text = $"Something went wrong: {ex.Message}";
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

        GenerateStatusText.Text = "Aborting...";
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
        GenerateStatusText.Visibility = Visibility.Visible;
        GenerateStatusText.Text = "Reading texture sets and deriving materials.json...";

        try
        {
            var result = await Task.Run(() => MaterialsBootstrapper.GenerateFromExistingPack(sourceFolder, outputPath));
            GenerateStatusText.Text = $"materials.json updated: {result.EntriesWritten} new entries " +
                                       $"({result.Skipped} skipped, {result.Failed} failed) -> {result.OutputPath}";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] GenerateMaterialsConfigButton_Click failed: {ex}");
            GenerateStatusText.Text = $"Failed to generate materials.json: {ex.Message}";
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
