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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    // Drives the Generate button's three layers. Built in ShowMainContent, since it needs
    // the XAML to exist, and shut down with the window so no loop outlives it.
    private ReactorAnimator? _reactor;

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

        _reactor?.Shutdown();

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

        ResolveReactorArt();

        _reactor = new ReactorAnimator(ReactorTileGrid, ReactorBloom);
        _reactor.Initialize();

        // Press-and-hold is a pointer state, not a click - the button's own Click event
        // fires too late and only once, so the wind-up is driven from these three.
        GenerateButton.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((s, e) => _reactor?.BeginPressHold()), handledEventsToo: true);
        GenerateButton.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((s, e) => _reactor?.EndPressHold()), handledEventsToo: true);
        GenerateButton.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler((s, e) => _reactor?.EndPressHold()), handledEventsToo: true);

        _ = RebuildPackIconsAsync();

#if DEBUG
        DevOnlyTitleBarActions.Visibility = Visibility.Visible;
#endif
    }

    /// <summary>
    /// Points the reactor's logo and bloom layers at whichever art is actually installed.
    ///
    /// The XAML asks for reactor.logo.png / reactor.bloom.png - the dedicated 1024px pair.
    /// Until those are in Assets/, the logo layer falls back to logo.large.png (the same
    /// mark, just without a matching bloom) so the button is never a bare grid of tiles,
    /// and the bloom layer is hidden rather than left as a broken image. The tile
    /// background is generated in code and never depends on a file at all.
    /// </summary>
    private void ResolveReactorArt()
    {
        var logoPath = System.IO.Path.Combine(AlchitexAssetsPath, "reactor.logo.png");
        var bloomPath = System.IO.Path.Combine(AlchitexAssetsPath, "reactor.bloom.png");

        if (!System.IO.File.Exists(logoPath))
        {
            Trace.WriteLine($"[ALCHITEX] '{logoPath}' not found - falling back to logo.large.png for the reactor's logo layer.");
            ReactorLogo.Source = new BitmapImage(new Uri("ms-appx:///Modules/Alchitex/Assets/logo.large.png"));
        }

        if (!System.IO.File.Exists(bloomPath))
        {
            Trace.WriteLine($"[ALCHITEX] '{bloomPath}' not found - the reactor's bloom layer stays hidden until it's added.");
            ReactorBloom.Source = null;
        }
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

    // ── Pack queue ───────────────────────────────────────────────────────────
    //
    // Two rows: what's still waiting to go into the reactor on top, what has come out of
    // it underneath. Both scroll horizontally rather than collapsing into a "+N", because
    // every tile now carries its own discard button and something you can't reach is
    // something you can't discard.

    private const double PackTileMinSize = 64;
    private const double PackTileMaxSize = 128;
    private double _packTileSize = 112;

    /// <summary>
    /// Packs this window is ignoring for the rest of its lifetime: discarded by the user,
    /// skipped at a confirmation dialog, or already run (successfully or not).
    ///
    /// Deliberately NOT a change to TunerVariables.SelectedPacks - that selection belongs
    /// to the main window and the pack browser. Closing and reopening this window brings
    /// everything back, which is exactly what "temporarily ignore" should mean. (The one
    /// case where SelectedPacks does change is the "Uninstall the original pack" toggle,
    /// and only because the folder genuinely stopped existing.)
    /// </summary>
    private readonly HashSet<string> _dismissedLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Packs that made it through, in the order they came out.</summary>
    private readonly List<(string Location, string Name)> _outputPacks = new();

    // Cached per pack so a resize or a queue change doesn't re-read icon files.
    private readonly Dictionary<string, BitmapImage?> _packIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool AnimationsSuspended => TunerVariables.Persistent.SuspendUIAnimations;

    private List<(string Location, string Name)> InputQueue() => TunerVariables.SelectedPacks
        .Where(p => !string.IsNullOrEmpty(p.Location))
        .Where(p => !_dismissedLocations.Contains(p.Location))
        .Where(p => System.IO.Directory.Exists(p.Location))
        .Select(p => (p.Location, p.Name))
        .ToList();

    /// <summary>
    /// Loads any icons that aren't cached yet, then redraws both rows. Call whenever the
    /// queue's contents change; a plain resize goes straight to RenderQueues.
    /// </summary>
    private async Task RebuildPackIconsAsync()
    {
        foreach (var (location, _) in InputQueue().Concat(_outputPacks))
        {
            if (_packIconCache.ContainsKey(location)) continue;
            _packIconCache[location] = await PackBrowserWindow.LoadPackIconAsync(location);
        }

        RenderQueues();
    }

    private void RenderQueues()
    {
        if (InputQueuePanel == null || OutputQueuePanel == null) return;

        InputQueuePanel.Children.Clear();
        OutputQueuePanel.Children.Clear();

        foreach (var (location, name) in InputQueue())
            InputQueuePanel.Children.Add(BuildPackTile(location, name, allowDiscard: true));

        foreach (var (location, name) in _outputPacks)
            OutputQueuePanel.Children.Add(BuildPackTile(location, name, allowDiscard: false));
    }

    /// <summary>
    /// One pack tile: the icon with the pack browser's rounded corners and drop shadow,
    /// plus - for the input row - a discard button that fades in on hover, mirroring the
    /// selection overlay in the pack browser but with the RemoveFrom glyph.
    /// </summary>
    private Grid BuildPackTile(string location, string packName, bool allowDiscard)
    {
        var size = _packTileSize;

        var tile = new Grid
        {
            Width = size,
            Height = size,
            Tag = location, // how the generation loop finds this pack's tile again
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };

        var iconBorder = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 96, 96, 96)),
            Translation = new System.Numerics.Vector3(0, 0, 12),
            Shadow = new ThemeShadow(),
        };

        _packIconCache.TryGetValue(location, out var icon);

        if (icon != null)
        {
            iconBorder.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            // Same fallback chain the pack browser uses for a pack with no readable icon.
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
                    Glyph = "",
                    FontSize = size * 0.32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        ToolTipService.SetToolTip(tile, PackBrowserWindow.StripMinecraftFormatting(packName));
        tile.Children.Add(iconBorder);

        if (allowDiscard)
        {
            // A Border with a Tapped handler rather than a Button, mirroring the pack
            // browser's selection overlay. A Button would be wrong here for a concrete
            // reason: this overlay only exists while the pointer is over it, so it would
            // spend its whole visible life in the "pointer over" visual state and paint
            // that theme brush instead of the dark scrim it is supposed to be.
            var discard = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(200, 0, 0, 0)),
                Opacity = 0,
                Child = new FontIcon
                {
                    Glyph = "", // RemoveFrom
                    FontSize = size * 0.45,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            ToolTipService.SetToolTip(discard, "Remove from this queue (stays selected in the main window)");

            // Revealed by hovering the tile as a whole, so the target is the icon itself
            // rather than a control you have to find before you can use it.
            tile.PointerEntered += (s, e) => FadeTo(discard, 1.0, 120);
            tile.PointerExited += (s, e) => FadeTo(discard, 0.0, 120);

            discard.Tapped += async (s, e) =>
            {
                e.Handled = true;
                await DiscardPackAsync(location, tile);
            };

            tile.Children.Add(discard);
        }

        return tile;
    }

    /// <summary>Drops a pack from the queue for this window's lifetime, with the same
    /// send-off a skipped pack gets.</summary>
    private async Task DiscardPackAsync(string location, FrameworkElement tile)
    {
        if (!_dismissedLocations.Add(location)) return;

        await AnimateDismissAsync(tile);
        RenderQueues();
    }

    private void PackQueueHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Two rows split the host's height; a tile is a square that fits one of them with
        // a little air. Recomputed rather than fixed so the reactor area's height stays
        // the single thing that decides how big this all is.
        var rowHeight = e.NewSize.Height / 2;
        var size = Math.Clamp(Math.Floor(rowHeight - 10), PackTileMinSize, PackTileMaxSize);

        if (Math.Abs(size - _packTileSize) < 1) return;

        _packTileSize = size;
        RenderQueues();
    }

    /// <summary>Keeps the reactor square and equal to the panel's height, so changing the
    /// controls area's height is the only edit needed to resize it.</summary>
    private void AlchitexControlsArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = Math.Max(0, e.NewSize.Height - 40); // the inner Grid's 20px top/bottom margins

        GenerateButton.Width = side;
        GenerateButton.Height = side;
    }

    // ── Queue animations ─────────────────────────────────────────────────────
    //
    // All of them no-op into their end state when SuspendUIAnimations is on - the queue
    // still updates, it just stops moving.

    private const double DismissAnimationMs = 180;
    private const double IntoReactorAnimationMs = 320;
    private const double ArrivalAnimationMs = 260;

    /// <summary>Discarded or skipped: straight up and out.</summary>
    private async Task AnimateDismissAsync(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;

        await RunStoryboardAsync(BuildTileStoryboard(tile, DismissAnimationMs,
            translateY: -40, opacity: 0, easing: new QuadraticEase { EasingMode = EasingMode.EaseIn }));
    }

    /// <summary>Accepted for generation: flies right, into the reactor, shrinking as it
    /// goes. The tiles behind it then slide up into the gap (RenderQueues redraws them at
    /// their new positions, and AnimateReflow covers the jump).</summary>
    private async Task AnimateIntoReactorAsync(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;

        var distance = Math.Max(120, PackQueueHost.ActualWidth - tile.ActualOffset.X);

        await RunStoryboardAsync(BuildTileStoryboard(tile, IntoReactorAnimationMs,
            translateX: distance, opacity: 0, scale: 0.35,
            easing: new QuadraticEase { EasingMode = EasingMode.EaseIn }));
    }

    /// <summary>The tiles left in the input row closing the gap the departed one left.</summary>
    private void AnimateReflow()
    {
        if (AnimationsSuspended) return;

        foreach (var child in InputQueuePanel.Children.OfType<FrameworkElement>())
        {
            if (child.RenderTransform is not CompositeTransform transform) continue;

            transform.TranslateX = _packTileSize + 12; // where it was before the gap closed
            _ = RunStoryboardAsync(BuildTileStoryboard(child, 220, translateX: 0,
                easing: new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        }
    }

    /// <summary>A finished pack arriving in the output row, coming from the reactor's
    /// side rather than just appearing.</summary>
    private void AnimateArrival(FrameworkElement tile)
    {
        if (AnimationsSuspended) return;

        if (tile.RenderTransform is not CompositeTransform transform) return;

        transform.TranslateX = 60;
        transform.ScaleX = transform.ScaleY = 0.6;
        tile.Opacity = 0;

        _ = RunStoryboardAsync(BuildTileStoryboard(tile, ArrivalAnimationMs,
            translateX: 0, opacity: 1, scale: 1.0,
            easing: new QuadraticEase { EasingMode = EasingMode.EaseOut }));
    }

    /// <summary>
    /// One storyboard covering any combination of translate/scale/opacity on a tile.
    /// CompositeTransform properties are dependent animations (they run on the UI thread),
    /// which is fine for the handful of tiles ever moving at once - and keeps this to one
    /// small helper instead of a composition-animation layer.
    /// </summary>
    private static Storyboard BuildTileStoryboard(
        FrameworkElement element,
        double durationMs,
        double? translateX = null,
        double? translateY = null,
        double? opacity = null,
        double? scale = null,
        EasingFunctionBase? easing = null)
    {
        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(durationMs);

        void Add(double to, string property, DependencyObject target, bool dependent)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EnableDependentAnimation = dependent,
                EasingFunction = easing,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        if (element.RenderTransform is CompositeTransform transform)
        {
            if (translateX.HasValue) Add(translateX.Value, "TranslateX", transform, true);
            if (translateY.HasValue) Add(translateY.Value, "TranslateY", transform, true);
            if (scale.HasValue)
            {
                Add(scale.Value, "ScaleX", transform, true);
                Add(scale.Value, "ScaleY", transform, true);
            }
        }

        if (opacity.HasValue) Add(opacity.Value, "Opacity", element, false);

        return storyboard;
    }

    private static Task RunStoryboardAsync(Storyboard storyboard)
    {
        var tcs = new TaskCompletionSource<bool>();
        storyboard.Completed += (s, e) => tcs.TrySetResult(true);
        storyboard.Begin();
        return tcs.Task;
    }

    private static void FadeTo(UIElement element, double opacity, double durationMs)
    {
        if (AnimationsSuspended)
        {
            element.Opacity = opacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>
    /// Keeps the announcements panel filling whatever's left of the window under the
    /// controls area. Without this the acrylic panel would stop at its last card and leave
    /// the window background showing beneath it.
    /// </summary>
    private void MainScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var progressBarHeight = GenerateProgressBar.Visibility == Visibility.Visible
            ? GenerateProgressBar.ActualHeight
            : 0;

        AnnouncementBackground.MinHeight = Math.Max(0, e.NewSize.Height - AlchitexControlsArea.Height - progressBarHeight);
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
            _reactor?.BeginGeneration();

            for (var i = 0; i < queue.Count; i++)
            {
                if (_generateCts.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var (pack, stripExistingPbr) = queue[i];

                // The pack visibly goes into the reactor before its run starts, and the
                // rest of the queue closes the gap behind it.
                await SendTileIntoReactorAsync(pack.Location);
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

                    // The reactor reacts to the phase, not to the text - see AlchitexPhase.
                    _reactor?.Pulse(p.Phase);
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
                        _reactor?.Pulse(AlchitexPhase.RemovingPack);
                        await DeleteOriginalPackAsync(pack.Location, pack.Name);
                    }

                    // Out the bottom of the reactor and into the output row. Keyed on the
                    // generated pack's own folder, so its icon is the NEW pack's icon -
                    // and it still resolves when the original has just been uninstalled.
                    AddToOutputRow(result.OutputPackPath ?? pack.Location, result.FinalManifestName ?? pack.Name);
                }
                else if (_generateCts.IsCancellationRequested) { aborted = true; break; }
                else
                {
                    failedNames.Add(pack.Name);
                    _failedPackNames.Add(pack.Name);
                }

                // Whether it succeeded or not, this pack is done: it stays out of the queue
                // for the rest of this window's lifetime (but stays selected upstream).
                _dismissedLocations.Add(pack.Location);
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
            _reactor?.EndGeneration();
            _generateCts?.Dispose();
            _generateCts = null;

            // Skipped, failed and aborted packs all leave the queue here, so what's left
            // on screen is only ever what a further click would actually act on.
            await RebuildPackIconsAsync();

            // Refresh even on an exception mid-batch - whatever succeeded before the
            // exception is still worth reporting when the window closes.
            UpdateSessionSummary();
        }
    }

    // ── Queue hand-off ───────────────────────────────────────────────────────

    /// <summary>
    /// Plays a pack's tile flying into the reactor, then redraws the input row without it
    /// and slides the remaining tiles into the gap. Awaited by the batch loop so the
    /// hand-off finishes before that pack's run starts - it's ~320ms against a run of
    /// seconds to minutes, and it's what makes the reactor look like it's being fed.
    /// </summary>
    private async Task SendTileIntoReactorAsync(string location)
    {
        var tile = InputQueuePanel.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(t => t.Tag is string tagged &&
                                 string.Equals(tagged, location, StringComparison.OrdinalIgnoreCase));

        if (tile == null) return;

        await AnimateIntoReactorAsync(tile);

        InputQueuePanel.Children.Remove(tile);
        AnimateReflow();
    }

    /// <summary>Adds a finished pack to the output row and plays its arrival.</summary>
    private void AddToOutputRow(string location, string packName)
    {
        _outputPacks.Add((location, packName));

        // The generated pack has its own regenerated icon; load it in the background and
        // redraw once it's there, rather than holding the batch up for a file read.
        _ = LoadOutputIconAsync(location);

        var tile = BuildPackTile(location, packName, allowDiscard: false);
        OutputQueuePanel.Children.Add(tile);
        AnimateArrival(tile);
    }

    private async Task LoadOutputIconAsync(string location)
    {
        if (_packIconCache.ContainsKey(location)) return;

        _packIconCache[location] = await PackBrowserWindow.LoadPackIconAsync(location);
        RenderQueues();
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
            {
                queue.Add(new QueuedPack(pack, StripExistingPbr: alreadyDeclaresPbr));
            }
            else
            {
                // Declined: out of the queue for this window's lifetime, with the same
                // send-off the discard button gives.
                _dismissedLocations.Add(pack.Location);

                var tile = InputQueuePanel.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(t => t.Tag is string tagged &&
                                         string.Equals(tagged, pack.Location, StringComparison.OrdinalIgnoreCase));

                if (tile != null)
                {
                    await AnimateDismissAsync(tile);
                    InputQueuePanel.Children.Remove(tile);
                }
            }
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
