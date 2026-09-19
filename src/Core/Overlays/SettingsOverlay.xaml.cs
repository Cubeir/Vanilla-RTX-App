using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
/// shadowed, fades in and out), but it claims only the left 40% and leaves the tuning surface
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
/// that owns it has validated and cached it - the same check that runs at every startup. A
/// setting accepted here that startup would reject is a setting that appears to silently
/// revert itself, which reads as a bug rather than as a rejection.</para>
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
    /// controls from that same setting. Without it, assigning <c>IsOn</c> or clicking a radio
    /// item in code raises the very handler that would then write it back - harmless for the
    /// theme, but it would make the animations toggle flip itself.
    /// </summary>
    private bool _suppressCallbacks;

    /// <summary>
    /// The live launch-option rows. The UI is the model while the panel is open; it is
    /// serialized into <see cref="Persistent.LaunchOptions"/> on every edit, so there is no
    /// "unsaved" state to lose if the window closes or crashes.
    /// </summary>
    private readonly List<LaunchOptionRow> _launchRows = new();

    public SettingsOverlay()
    {
        InitializeComponent();

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

    public void Hide()
    {
        if (!_isOpen) return;
        _isOpen = false;

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () => Visibility = Visibility.Collapsed);
    }

    private void ModalBlocker_Tapped(object sender, TappedRoutedEventArgs e) => Hide();

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
                "Dark" => "",   // QuietHours (moon)
                _ => ""         // DevUpdate - "whatever Windows says"
            };

            SuspendAnimationsSwitch.IsOn = Persistent.SuspendUIAnimations;

            RefreshPaths();
            RefreshCredits();
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    private void RefreshPaths()
    {
        // Install paths are read straight out of the cache: MinecraftGDKLocator only ever
        // writes a path it has verified, and re-verifying here would mean a filesystem walk
        // every time the panel opens.
        SetPathRow(ReleaseInstallPathText, ReleaseInstallButton, Persistent.MinecraftInstallPath);
        SetPathRow(PreviewInstallPathText, PreviewInstallButton, Persistent.MinecraftPreviewInstallPath);

        // Data roots go through GetDataRoot rather than the raw field, so a folder that has
        // gone missing since startup reads as "not set" instead of as a path that works.
        SetPathRow(ReleaseDataPathText, ReleaseDataButton, MinecraftUserDataLocator.GetDataRoot(isPreview: false));
        SetPathRow(PreviewDataPathText, PreviewDataButton, MinecraftUserDataLocator.GetDataRoot(isPreview: true));
    }

    /// <summary>
    /// The button says "Select" when there is nothing to change and "Change" when there is -
    /// which is also the only cue that the path above it is real rather than a placeholder.
    /// </summary>
    private static void SetPathRow(TextBlock text, Button button, string? path)
    {
        var known = !string.IsNullOrWhiteSpace(path);

        text.Text = known ? path! : "Not found";
        text.Opacity = known ? 1.0 : 0.55;
        button.Content = known ? "Change" : "Select";
    }

    private void RefreshCredits()
    {
        var credits = OnlineTextsContent.Credits?.FirstOrDefault()?.Text;
        CreditsText.Text = credits ?? string.Empty;
        CreditsText.Visibility = string.IsNullOrWhiteSpace(credits) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Repaints the four path-selector seams for a theme. See the constructor for why by hand.</summary>
    private void ApplyBevelColors(ElementTheme theme)
    {
        var brush = new SolidColorBrush(ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: true));

        ReleaseInstallBevel.BorderBrush = brush;
        PreviewInstallBevel.BorderBrush = brush;
        ReleaseDataBevel.BorderBrush = brush;
        PreviewDataBevel.BorderBrush = brush;
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

    private void ReleaseInstallButton_Click(object sender, RoutedEventArgs e) => _ = PickInstallPathAsync(isPreview: false, (Button)sender);
    private void PreviewInstallButton_Click(object sender, RoutedEventArgs e) => _ = PickInstallPathAsync(isPreview: true, (Button)sender);
    private void ReleaseDataButton_Click(object sender, RoutedEventArgs e) => _ = PickDataPathAsync(isPreview: false, (Button)sender);
    private void PreviewDataButton_Click(object sender, RoutedEventArgs e) => _ = PickDataPathAsync(isPreview: true, (Button)sender);

    /// <summary>
    /// Runs one picker, with the button disabled for its whole duration. The disable is not
    /// cosmetic: a second picker opened on top of the first resolves against the same cached
    /// field, and whichever finishes last silently wins.
    /// </summary>
    private async System.Threading.Tasks.Task PickInstallPathAsync(bool isPreview, Button button)
    {
        if (_host is null) return;

        button.IsEnabled = false;
        try
        {
            var edition = isPreview ? "Minecraft Preview" : "Minecraft";
            var hWnd = WindowNative.GetWindowHandle(_host);

            // LocateMinecraftManuallyAsync does the picking, the one-level-deep tolerance, the
            // MicrosoftGame.Config edition check and the caching. Nothing is written here.
            var path = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

            if (path is null)
            {
                MainWindow.Log($"That folder wasn't accepted as a {edition} installation. " +
                               $"Pick the folder that holds {MinecraftGDKLocator.MinecraftExecutableName}, or the one directly above it.",
                               MainWindow.LogLevel.Error);
                return;
            }

            MainWindow.Log($"{edition} installation set: {path}", MainWindow.LogLevel.Success);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Install path selection failed: {ex}");
        }
        finally
        {
            button.IsEnabled = true;
            RefreshPaths();
        }
    }

    private async System.Threading.Tasks.Task PickDataPathAsync(bool isPreview, Button button)
    {
        if (_host is null) return;

        button.IsEnabled = false;
        try
        {
            await _host.HandleManualDataLocationAsync(isPreview);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Data path selection failed: {ex}");
        }
        finally
        {
            button.IsEnabled = true;
            RefreshPaths();
        }
    }

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
    //  Maintenance
    // =========================================================================

    private void HardResetButton_Click(object sender, RoutedEventArgs e)
    {
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
