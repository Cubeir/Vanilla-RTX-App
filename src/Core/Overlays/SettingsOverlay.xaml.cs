using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Modules;
using WinRT.Interop;
using static Vanilla_RTX_App.EnvironmentVariables;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The app's one settings surface - everything that configures the app rather than a pack.
/// A sibling of <see cref="MarkdownOverlay"/> in placement and chrome (36px down, acrylic,
/// shadowed, fades in and out), but it claims only half the width and leaves the tuning surface
/// visible behind a dismiss scrim.
///
/// <para><b>It drives MainWindow directly through <see cref="MainWindow.Instance"/> rather than
/// raising events.</b> Almost everything in here is a change to the window - the theme applies
/// to its root, suspending animations hides its preview vessels, setting a data path refreshes
/// its pack list, and both maintenance buttons are the window's own long-running work. Wiring
/// six events for a control that only ever lives inside that one window would be ceremony
/// around the same coupling.</para>
///
/// <para><b>Nothing here is applied optimistically.</b> A path is shown only once the locator
/// that owns it has validated and cached it - the same check that runs at every startup. A URL
/// is written only once <see cref="ProviderLinks.IsValid"/> accepts it. A setting accepted here
/// that startup would reject is a setting that appears to silently revert itself, which reads as
/// a bug rather than as a rejection.</para>
///
/// <para><b>The panel is never reachable while the window is busy.</b> MainWindow's
/// <c>LockControls</c> disables the titlebar's Settings button for the duration of every
/// operation, because half of what is in here - the four Minecraft locations - is what those
/// operations are reading from while they run.</para>
/// </summary>
public sealed partial class SettingsOverlay : UserControl
{
    /// <summary>
    /// Matches <see cref="MarkdownOverlay"/>'s fade so the two overlays feel like one surface
    /// being swapped rather than two different panels.
    /// </summary>
    private const double FADE_MS = 100;

    private static bool AnimationsSuspended => Persistent.SuspendUIAnimations;

    private MainWindow? _host;
    private bool _isOpen;
    private Storyboard? _fadeStoryboard;

    /// <summary>
    /// Guards the handlers that write a setting while <see cref="Refresh"/> is painting the
    /// controls from that same setting. Without it, assigning <c>IsOn</c> or a TextBox's
    /// <c>Text</c> in code raises the very handler that would then write it back - harmless for
    /// the theme, but it would make the animations toggle flip itself.
    /// </summary>
    private bool _suppressCallbacks;

    /// <summary>
    /// The live launch-option rows. The UI is the model while the panel is open; it is
    /// serialized into <see cref="Persistent.LaunchOptions"/> on every edit, so there is no
    /// "unsaved" state to lose if the window closes or crashes.
    /// </summary>
    private readonly List<LaunchOptionRow> _launchRows = new();

    /// <summary>The four editable addresses, built once in the constructor - see <see cref="UrlField"/>.</summary>
    private readonly List<UrlField> _urlFields = new();

    /// <summary>Every path row, so refreshing and bevel repainting can walk them rather than naming eight controls each time.</summary>
    private PathRow[] _pathRows = Array.Empty<PathRow>();

    public SettingsOverlay()
    {
        InitializeComponent();

        BuildPathRows();
        BuildUrlFields();

        // The path selectors' 3px seams are a ThemeService color choice, not a ThemeResource
        // binding that re-resolves itself, so they have to be repainted by hand on every theme
        // change - the same deal as OverlayHeaderBar's Close button.
        ApplyBevelColors(ThemeService.ResolveInitialTheme());
        ThemeService.ThemeChanged += ApplyBevelColors;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= ApplyBevelColors;
    }

    /// <summary>True while the panel is showing. MainWindow reads it to toggle and to decide which overlay has to close first.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Binds the panel to its window and paints it for the first time. Called from
    /// <c>MainWindow_Loaded</c> once settings are loaded and both locators have run, so the
    /// first paint shows resolved paths rather than empty ones.
    /// </summary>
    public void Initialize(MainWindow host)
    {
        _host = host;
        BuildLaunchOptionRows(MinecraftLauncher.ParseOptions(Persistent.LaunchOptions));
        Refresh();
    }

    // =========================================================================
    //  Open / close
    // =========================================================================

    public void Show()
    {
        if (_isOpen) return;
        _isOpen = true;

        // Paths, the theme and the animation flag can all have moved since the panel was last
        // shown (the pack browser locates user data too, and a hard reset clears everything),
        // so every open repaints rather than trusting what is on screen.
        Refresh();

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);
    }

    /// <summary>
    /// Closes the panel and flushes every setting to disk.
    ///
    /// <para><b>The save is here rather than only in MainWindow's Closed handler</b> because
    /// this panel is the one place a user changes several settings in a row and then expects
    /// them kept. Everything in here writes its value into <see cref="Persistent"/> the moment
    /// it changes, but that is memory - a crash, a hard kill, or the app being restarted by
    /// something else between now and window close would take the lot. Saving on close costs
    /// one pass over a dozen fields at the one moment the user has finished.</para>
    /// </summary>
    public void Hide()
    {
        if (!_isOpen) return;
        _isOpen = false;

        SaveSettings();

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () => Visibility = Visibility.Collapsed);
    }

    private void ModalBlocker_Tapped(object sender, TappedRoutedEventArgs e) => Hide();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void AnimateOpacity(double to, Action? onCompleted)
    {
        _fadeStoryboard?.Stop();
        _fadeStoryboard = null;

        if (AnimationsSuspended)
        {
            Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FADE_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => onCompleted?.Invoke();

        _fadeStoryboard = storyboard;
        storyboard.Begin();
    }

    // =========================================================================
    //  Painting the panel from current state
    // =========================================================================

    /// <summary>
    /// Pulls every control back in line with what the app currently believes. Safe to call at
    /// any time; it writes nothing.
    /// </summary>
    public void Refresh()
    {
        _suppressCallbacks = true;
        try
        {
            var mode = Persistent.AppThemeMode ?? "System";
            ThemeSystemItem.IsChecked = mode == "System";
            ThemeLightItem.IsChecked = mode == "Light";
            ThemeDarkItem.IsChecked = mode == "Dark";
            ThemeModeLabel.Text = mode switch
            {
                "Light" => "Light",
                "Dark" => "Dark",
                _ => "Auto"
            };
            ThemeModeGlyph.Glyph = mode switch
            {
                "Light" => "",  // Brightness
                "Dark" => "",   // QuietHours - the moon the old titlebar button used
                _ => ""         // DevUpdate - "whatever Windows is set to"
            };

            SuspendAnimationsSwitch.IsOn = Persistent.SuspendUIAnimations;

            RefreshPaths();
            RefreshUrlFields();
            RefreshCredits();
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    private void RefreshCredits()
    {
        var credits = OnlineTextsContent.Credits?.FirstOrDefault()?.Text;
        CreditsText.Text = credits ?? string.Empty;
        CreditsText.Visibility = string.IsNullOrWhiteSpace(credits) ? Visibility.Collapsed : Visibility.Visible;
    }

    // =========================================================================
    //  Appearance
    // =========================================================================

    private void ThemeModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string mode) return;

        Persistent.AppThemeMode = mode;
        _host?.ApplyThemeMode();
        Refresh();

        // The lamp answers with the thing the mode means: off for dark, lit for light, and a
        // rapid flicker for Auto, which is neither and follows whatever Windows decides.
        if (_host is null) return;
        _ = mode switch
        {
            "Light" => _host.BlinkingLamp(true, true, 1.0, 0.0),
            "Dark" => _host.BlinkingLamp(true, true, 0.0, 0.0),
            _ => _host.BlinkingLamp(true, true, 0.5, 1.0)
        };
    }

    private void SuspendAnimationsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;

        Persistent.SuspendUIAnimations = SuspendAnimationsSwitch.IsOn;
        _host?.ApplySuspendUIAnimations(invokedByUser: true);
    }

    // =========================================================================
    //  Minecraft locations
    // =========================================================================

    /// <summary>
    /// One (edition x kind) location: the clickable path, the Select/Change button, the seam
    /// between them, and the two operations that differ per kind - how to read the current
    /// path, and how to ask the user for a new one.
    /// </summary>
    private sealed class PathRow
    {
        public required TextBlock Text { get; init; }
        public required Button PathButton { get; init; }
        public required Button ChangeButton { get; init; }
        public required Border Bevel { get; init; }
        public required Func<string?> Current { get; init; }
        public required Func<Task> Pick { get; init; }
    }

    private void BuildPathRows()
    {
        _pathRows =
        [
            new PathRow
            {
                Text = ReleaseInstallPathText, PathButton = ReleaseInstallPathButton,
                ChangeButton = ReleaseInstallButton, Bevel = ReleaseInstallBevel,
                // Install paths are read straight out of the cache: MinecraftGDKLocator only
                // ever writes a path it has verified, and re-verifying here would mean a
                // filesystem walk every time the panel opens.
                Current = () => Persistent.MinecraftInstallPath,
                Pick = () => PickInstallPathAsync(isPreview: false)
            },
            new PathRow
            {
                Text = PreviewInstallPathText, PathButton = PreviewInstallPathButton,
                ChangeButton = PreviewInstallButton, Bevel = PreviewInstallBevel,
                Current = () => Persistent.MinecraftPreviewInstallPath,
                Pick = () => PickInstallPathAsync(isPreview: true)
            },
            new PathRow
            {
                Text = ReleaseDataPathText, PathButton = ReleaseDataPathButton,
                ChangeButton = ReleaseDataButton, Bevel = ReleaseDataBevel,
                // Data roots go through GetDataRoot rather than the raw field, so a folder that
                // has gone missing since startup reads as "not set" instead of as a path that
                // still works.
                Current = () => MinecraftUserDataLocator.GetDataRoot(isPreview: false),
                Pick = () => PickDataPathAsync(isPreview: false)
            },
            new PathRow
            {
                Text = PreviewDataPathText, PathButton = PreviewDataPathButton,
                ChangeButton = PreviewDataButton, Bevel = PreviewDataBevel,
                Current = () => MinecraftUserDataLocator.GetDataRoot(isPreview: true),
                Pick = () => PickDataPathAsync(isPreview: true)
            },
        ];

        foreach (var row in _pathRows)
        {
            // The seam is drawn from the button's accent, so it has to follow that button's
            // enabled state the way MainWindow's Preview toggle bevels follow theirs - an
            // accent stripe glued to a greyed-out button reads as a rendering bug. Subscribing
            // here covers both ways it gets disabled: a picker being open, and
            // WindowControlsManager locking the window down.
            var captured = row;
            row.ChangeButton.IsEnabledChanged += (_, _) => ApplyBevelColor(captured);
        }
    }

    private void RefreshPaths()
    {
        foreach (var row in _pathRows)
        {
            var path = row.Current();
            var known = !string.IsNullOrWhiteSpace(path);

            row.Text.Text = known ? path! : "Not found";
            row.Text.Opacity = known ? 1.0 : 0.55;

            // "Select" when there is nothing set and "Change" when there is - which is also the
            // only cue that the path above it is real rather than a placeholder.
            row.ChangeButton.Content = known ? "Change" : "Select";

            row.PathButton.IsEnabled = known;
            ToolTipService.SetToolTip(row.PathButton, known
                ? "Open this folder in File Explorer."
                : "Nothing to open yet - the app hasn't found this location.");
        }
    }

    /// <summary>Repaints the four path-selector seams for a theme. See <see cref="BuildPathRows"/> for why by hand.</summary>
    private void ApplyBevelColors(ElementTheme theme)
    {
        foreach (var row in _pathRows)
            ApplyBevelColor(row, theme);
    }

    private void ApplyBevelColor(PathRow row, ElementTheme? theme = null)
        => row.Bevel.BorderBrush = new SolidColorBrush(ThemeService.GetBevelColor(
            theme ?? ActualTheme,
            ThemeService.BevelEdge.Left,
            accented: true,
            isEnabled: row.ChangeButton.IsEnabled));

    private void ReleaseInstallPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(Persistent.MinecraftInstallPath);
    private void PreviewInstallPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(Persistent.MinecraftPreviewInstallPath);
    private void ReleaseDataPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(MinecraftUserDataLocator.GetDataRoot(isPreview: false));
    private void PreviewDataPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(MinecraftUserDataLocator.GetDataRoot(isPreview: true));

    /// <summary>
    /// Opens a folder in File Explorer. A path the app is showing can still be gone by the time
    /// it is clicked, and <c>explorer.exe</c> answers a missing folder by opening Documents
    /// instead of failing - so the existence check is what keeps a stale path from looking like
    /// the app navigated somewhere random on purpose.
    /// </summary>
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!Directory.Exists(path))
        {
            MainWindow.Log($"That folder isn't there any more: {path}", MainWindow.LogLevel.Warning);
            RefreshPaths();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Couldn't open '{path}' in Explorer: {ex.Message}");
            MainWindow.Log($"Couldn't open that folder: {ex.Message}", MainWindow.LogLevel.Error);
        }
    }

    private void ReleaseInstallButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[0]);
    private void PreviewInstallButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[1]);
    private void ReleaseDataButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[2]);
    private void PreviewDataButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[3]);

    /// <summary>
    /// Runs one row's picker with its button disabled for the duration. The disable is not
    /// cosmetic: a second picker opened on top of the first resolves against the same cached
    /// field, and whichever finishes last silently wins.
    /// </summary>
    private async Task RunPickerAsync(PathRow row)
    {
        if (_host is null) return;

        row.ChangeButton.IsEnabled = false;
        try
        {
            await row.Pick();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Path selection failed: {ex}");
        }
        finally
        {
            row.ChangeButton.IsEnabled = true;
            RefreshPaths();
        }
    }

    private async Task PickInstallPathAsync(bool isPreview)
    {
        _ = _host!.BlinkingLamp(false, true, 0.5, 1.0);

        var edition = isPreview ? "Minecraft Preview" : "Minecraft";
        var hWnd = WindowNative.GetWindowHandle(_host);

        // LocateMinecraftManuallyAsync does the picking (starting at the current path), the
        // one-level-deep tolerance, the MicrosoftGame.Config edition check and the caching.
        // Nothing is written here.
        var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (path is null)
        {
            MainWindow.Log($"No {edition} installation was set. Pick the folder that holds " +
                           $"{MinecraftGDKLocator.MinecraftExecutableName}, or the one directly above it.",
                           MainWindow.LogLevel.Warning);
            return;
        }

        MainWindow.Log($"{edition} installation set: {path}", MainWindow.LogLevel.Success);
    }

    private Task PickDataPathAsync(bool isPreview) => _host!.HandleManualDataLocationAsync(isPreview);

    // =========================================================================
    //  Launch options
    // =========================================================================

    /// <summary>One editable options.txt entry: its name box, its value box, and the row they live in.</summary>
    private sealed class LaunchOptionRow
    {
        public required Grid Container { get; init; }
        public required TextBox NameBox { get; init; }
        public required NumberBox ValueBox { get; init; }
    }

    private void BuildLaunchOptionRows(IEnumerable<LaunchOption> options)
    {
        _suppressCallbacks = true;
        try
        {
            _launchRows.Clear();
            LaunchOptionRows.Children.Clear();

            foreach (var option in options)
                AddRow(option.Name, option.Value);
        }
        finally
        {
            _suppressCallbacks = false;
        }

        UpdateLaunchOptionsEmptyState();
    }

    /// <summary>
    /// Builds one row in code because the number of them is the user's choice. Everything here
    /// writes through <see cref="CommitLaunchOptions"/> on change, so what the Launch button
    /// does is always exactly what is on screen.
    /// </summary>
    private void AddRow(string name, int value)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = name,
            PlaceholderText = "option name",
            FontSize = 12,
            IsTextScaleFactorEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(nameBox, "The options.txt parameter name, exactly as the game spells it (e.g. graphics_mode).");
        Grid.SetColumn(nameBox, 0);

        // NumberBox rather than a TextBox: every options.txt value the app writes is an
        // integer, and letting a non-number be typed only to be silently dropped at launch is
        // the kind of thing nobody ever finds out about.
        var valueBox = new NumberBox
        {
            Value = value,
            SmallChange = 1,
            LargeChange = 1,
            Width = 96,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            IsTextScaleFactorEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(valueBox, "The whole number to write for this parameter.");
        Grid.SetColumn(valueBox, 1);

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center
        };

        // TryGetValue rather than an indexer: the key comes from XamlControlsResources and is
        // certainly there, but a missing style should cost this button its subtlety rather than
        // throw out of a row build and leave the list half-constructed.
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle) && subtleStyle is Style style)
            removeButton.Style = style;

        ToolTipService.SetToolTip(removeButton, "Remove this option. The game keeps whatever value it already has.");
        Grid.SetColumn(removeButton, 2);

        var row = new LaunchOptionRow { Container = grid, NameBox = nameBox, ValueBox = valueBox };

        nameBox.TextChanged += (_, _) => CommitLaunchOptions();
        valueBox.ValueChanged += (_, _) => CommitLaunchOptions();
        removeButton.Click += (_, _) =>
        {
            _launchRows.Remove(row);
            LaunchOptionRows.Children.Remove(grid);
            CommitLaunchOptions();
            UpdateLaunchOptionsEmptyState();
        };

        grid.Children.Add(nameBox);
        grid.Children.Add(valueBox);
        grid.Children.Add(removeButton);

        _launchRows.Add(row);
        LaunchOptionRows.Children.Add(grid);
    }

    private void AddLaunchOptionButton_Click(object sender, RoutedEventArgs e)
    {
        AddRow(string.Empty, 0);
        UpdateLaunchOptionsEmptyState();

        // Straight into the empty name box - an added row with nothing in it and no cursor
        // reads as though the button did nothing.
        _launchRows[^1].NameBox.Focus(FocusState.Programmatic);
    }

    private void ResetLaunchOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        BuildLaunchOptionRows(MinecraftLauncher.DefaultOptions);
        CommitLaunchOptions();
        MainWindow.Log("Launch options restored to defaults: ray tracing on, in-game graphics mode switching on, VSync off.", MainWindow.LogLevel.Reset);
        _ = _host?.BlinkingLamp(true, true, 0.0);
    }

    /// <summary>
    /// Serializes the rows into the persisted setting. A row whose name is still blank is left
    /// out by <see cref="MinecraftLauncher.SerializeOptions"/> rather than rejected, so a row
    /// can sit half-typed without the setting flickering between valid states.
    /// </summary>
    private void CommitLaunchOptions()
    {
        if (_suppressCallbacks) return;

        var options = _launchRows.Select(r => new LaunchOption(
            r.NameBox.Text,
            // NaN is what a NumberBox holds while its text is empty or mid-edit; 0 is the
            // value the row was created with and the only sane reading of "nothing typed".
            double.IsNaN(r.ValueBox.Value) ? 0 : (int)Math.Round(r.ValueBox.Value)));

        Persistent.LaunchOptions = MinecraftLauncher.SerializeOptions(options);
    }

    private void UpdateLaunchOptionsEmptyState() =>
        LaunchOptionsEmptyText.Visibility = _launchRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    //  Content sources
    // =========================================================================

    /// <summary>
    /// One editable address: its box, its revert button, the line under it, and the things that
    /// differ per field - what it must look like, what the built-in value is, and how to read
    /// and write the stored one.
    /// </summary>
    private sealed class UrlField
    {
        public required TextBox Box { get; init; }
        public required Button ResetButton { get; init; }
        public required TextBlock Hint { get; init; }
        public required string Description { get; init; }
        public required string Fallback { get; init; }
        public required ProviderLinks.LinkKind Kind { get; init; }
        public required Func<string?> Read { get; init; }
        public required Action<string> Write { get; init; }
    }

    private void BuildUrlFields()
    {
        _urlFields.AddRange(
        [
            new UrlField
            {
                Box = DlssProviderBox, ResetButton = DlssProviderResetButton, Hint = DlssProviderHint,
                Description = "The page the DLSS swapper's \"Download DLLs\" button browses to.",
                Fallback = ProviderLinks.DefaultDlssProvider, Kind = ProviderLinks.LinkKind.WebPage,
                Read = () => Persistent.DlssProviderUrl, Write = v => Persistent.DlssProviderUrl = v
            },
            new UrlField
            {
                Box = BetterRtxProviderBox, ResetButton = BetterRtxProviderResetButton, Hint = BetterRtxProviderHint,
                Description = "The page the BetterRTX manager's \"Create preset\" button browses to.",
                Fallback = ProviderLinks.DefaultBetterRtxProvider, Kind = ProviderLinks.LinkKind.WebPage,
                Read = () => Persistent.BetterRtxProviderUrl, Write = v => Persistent.BetterRtxProviderUrl = v
            },
            new UrlField
            {
                Box = DocumentationBox, ResetButton = DocumentationResetButton, Hint = DocumentationHint,
                Description = "The markdown the titlebar's Help button renders. A #heading at the end is honoured.",
                Fallback = ProviderLinks.DefaultDocumentation, Kind = ProviderLinks.LinkKind.Markdown,
                Read = () => Persistent.DocumentationUrl, Write = v => Persistent.DocumentationUrl = v
            },
            new UrlField
            {
                Box = BugTrackerBox, ResetButton = BugTrackerResetButton, Hint = BugTrackerHint,
                Description = "The markdown the titlebar's Bugs button renders. A #heading at the end is honoured.",
                Fallback = ProviderLinks.DefaultBugTracker, Kind = ProviderLinks.LinkKind.Markdown,
                Read = () => Persistent.BugTrackerUrl, Write = v => Persistent.BugTrackerUrl = v
            },
        ]);

        foreach (var field in _urlFields)
        {
            var captured = field;

            // Committed on focus loss and on Enter, never per keystroke: a half-typed address
            // is invalid for most of the time it is being typed, and rejecting it letter by
            // letter turns the hint into a flicker nobody can read.
            captured.Box.LostFocus += (_, _) => CommitUrlField(captured);
            captured.Box.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                CommitUrlField(captured);
                e.Handled = true;
            };

            captured.ResetButton.Click += (_, _) =>
            {
                captured.Write(captured.Fallback);
                SetBoxText(captured, captured.Fallback);
                ShowUrlFieldHint(captured, accepted: true);
            };
        }
    }

    private void RefreshUrlFields()
    {
        foreach (var field in _urlFields)
        {
            field.Box.Text = ProviderLinks.Resolve(field.Read(), field.Fallback, field.Kind);
            ShowUrlFieldHint(field, accepted: true);
        }
    }

    /// <summary>Writes into the box without the write coming back around as an edit to commit.</summary>
    private void SetBoxText(UrlField field, string text)
    {
        _suppressCallbacks = true;
        try { field.Box.Text = text; }
        finally { _suppressCallbacks = false; }
    }

    /// <summary>
    /// Validates what's in the box and stores it if it passes. A rejected value is left on
    /// screen with the reason under it rather than being snapped back: the user is looking at
    /// what they typed and needs to see what is wrong with it, and nothing downstream has
    /// changed because the stored value was never touched.
    /// </summary>
    private void CommitUrlField(UrlField field)
    {
        if (_suppressCallbacks) return;

        var typed = field.Box.Text?.Trim() ?? string.Empty;

        // An emptied box means "go back to the built-in one" - the same thing the revert button
        // does, and a more discoverable way to ask for it than finding that button.
        if (typed.Length == 0)
        {
            field.Write(field.Fallback);
            SetBoxText(field, field.Fallback);
            ShowUrlFieldHint(field, accepted: true);
            return;
        }

        var accepted = ProviderLinks.IsValid(typed, field.Kind);
        if (accepted) field.Write(typed);

        ShowUrlFieldHint(field, accepted);
    }

    private void ShowUrlFieldHint(UrlField field, bool accepted)
    {
        field.Hint.Text = accepted ? field.Description : ProviderLinks.RejectionReason(field.Kind);
        field.Hint.Opacity = accepted ? 0.55 : 1.0;
        field.Hint.Foreground = accepted
            ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    }

    // =========================================================================
    //  Maintenance
    // =========================================================================

    private void HardResetButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _host?.BlinkingLamp(true, true, 0.0, 1.0);

        // The panel gets out of the way first: the confirmation dialog and the progress bar it
        // leads to both belong to the window behind this, and a modal scrim over them would
        // leave the user looking at a dimmed app they can't see the progress of.
        Hide();
        _ = _host?.RequestHardResetAsync();
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e) => _host?.CopyDebugReportToClipboard();

    // =========================================================================
    //  Links
    // =========================================================================

    private void GitHubLink_Click(object sender, RoutedEventArgs e)
        => _ = MainWindow.OpenUrl("https://github.com/Cubeir/Vanilla-RTX-App");

    private void KoFiLink_Click(object sender, RoutedEventArgs e)
    {
        // Same gesture the titlebar's Donate button used to make - the credits go into the log
        // on the way out, so they're still there after the browser takes focus.
        _host?.RollCredits();
        _ = MainWindow.OpenUrl("https://ko-fi.com/cubeir");
    }

    private void DiscordLink_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Log("Here is the invitation!\nDiscord.gg/A4wv4wwYud", MainWindow.LogLevel.VanillaRTX);
        _ = MainWindow.OpenUrl("https://discord.gg/A4wv4wwYud");
    }
}
