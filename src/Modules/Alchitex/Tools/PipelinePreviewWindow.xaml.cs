using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules.Alchitex.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// DEVELOPER TOOL. Runs one colour texture through all three PBR generators and shows
/// every intermediate stage each of them goes through, against a live-editable copy of
/// both the material entry and every pipeline tuning constant.
///
/// It exists because tuning a dozen interacting numbers by generating a whole pack and
/// squinting at the result in-game is not a workable loop. Nothing here writes to
/// materials.json, to the tuning constants, or to any pack - the outputs are what you
/// see, plus two clipboard exports.
///
/// -- HOW THIS STAYS CURRENT AS THE PIPELINE CHANGES -----------------------------------
///
/// Almost nothing in this file knows anything specific about the pipeline. It renders
/// whatever stages PipelineTrace was handed, in capture order, grouped by the chain prefix
/// of their ids, and it builds its two control panels by reflecting over MaterialEntry and
/// PipelineTuning. So:
///
///   * a new pipeline step needs one PipelineTrace call in PbrGeneration.cs and nothing
///     here (optionally a prose entry in PipelineStageCatalog, which is not required);
///   * a new tuning constant needs one field in PipelineTuning and nothing here;
///   * a materials.json schema change needs nothing anywhere.
///
/// The one place that does name specific stages is FeaturedStageIds below, and it fails
/// softly: rename a final stage and the pinned strip goes empty while every stage still
/// renders in the filmstrips underneath.
/// </summary>
public sealed partial class PipelinePreviewWindow : Window
{
    /// <summary>
    /// The three finals, pinned at the top because they're what you're actually judging.
    /// The only hardcoded stage ids in the whole tool - see the class comment for what
    /// happens if they ever stop being emitted (nothing bad).
    /// </summary>
    private static readonly string[] FeaturedStageIds = { "mers.final", "height.final", "normal.final" };

    private readonly AppWindow _appWindow;
    private bool _isClosing;

    // Live state the previews are a function of.
    private string? _colorPath;
    private MaterialEntry _material = new();
    private PipelineTuning _tuning = PipelineTuning.Default.Clone();

    // Loaded once - materials.json is large, and the exact-match check runs every preview.
    private Dictionary<string, MaterialEntry> _materialsByName = new(StringComparer.OrdinalIgnoreCase);
    private PbrBlacklist? _blacklist;

    private PipelinePreviewResult? _result;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool _running;
    private bool _rerunRequested;

    // Which view (RGB / R / G / B / A / ...) each tile is showing, kept across re-runs so
    // that sweeping a knob while looking at one channel doesn't snap you back to RGB.
    private readonly Dictionary<string, string> _selectedView = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StageTile> _tiles = new(StringComparer.Ordinal);
    private string _renderedLayoutKey = "";
    private bool _detailOpen;

    private static string AlchitexAssetsPath =>
        Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets");

    public PipelinePreviewWindow()
    {
        InitializeComponent();

        var manager = WindowManager.Get(this);
        manager.MinWidth = 1100;
        manager.MinHeight = 700;
        manager.IsResizable = true;
        manager.IsMaximizable = true;

        _appWindow = AppWindow;
        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        ThemeService.ThemeChanged += ApplyTheme;
        ApplyTheme(ThemeService.ResolveInitialTheme());

        try
        {
            this.SetIcon(Path.Combine(AlchitexAssetsPath, "logo.large.ico"));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't set its window icon: {ex.Message}");
        }

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunPreviewAsync();
        };

        Closed += OnClosed;

        if (Content is FrameworkElement root)
            root.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Content is FrameworkElement root)
            root.Loaded -= OnLoaded;
        if (_isClosing) return;

        SetTitleBar(TitleBarDragArea);

        _materialsByName = MaterialEntryJson.LoadAll(Path.Combine(AlchitexAssetsPath, "materials.json"));
        _blacklist = PbrBlacklist.Load(Path.Combine(AlchitexAssetsPath, "pbr_blacklist.json"));

        // Start from the shipping "default" entry, which is what an unlisted texture
        // actually gets - a more honest starting point than a zeroed MaterialEntry.
        _material = _materialsByName.TryGetValue("default", out var fallback)
            ? MaterialEntryJson.Clone(fallback)
            : new MaterialEntry();

        RebuildMaterialEditor();
        RebuildKnobEditor();

        TextureNameBox.TextChanged += (_, _) => MarkDirty();

        SetStatus($"materials.json: {_materialsByName.Count} entries loaded.");
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        _debounce.Stop();
        _result?.Dispose();
        _result = null;

        ThemeService.ThemeChanged -= ApplyTheme;
        Closed -= OnClosed;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root) root.RequestedTheme = theme;
        ThemeService.ApplyTitleBarColors(_appWindow, theme);
    }

    // ── Source selection ─────────────────────────────────────────────────────────────

    private async void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        foreach (var ext in new[] { ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file != null) SetSource(file.Path);
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;

        // Null for drags that didn't originate outside the app.
        if (e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = "Preview this texture";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is StorageFile file) SetSource(file.Path);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't read the dropped file: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Points the tool at a new texture. The texture name box follows the file
    /// name, since that's what the real pipeline looks materials.json up by - but it stays
    /// editable, so a scratch file can still be previewed as any block you like.</summary>
    private void SetSource(string path)
    {
        _colorPath = path;
        SourcePathText.Text = path;
        TextureNameBox.Text = Path.GetFileNameWithoutExtension(path);
        EmptyHint.Visibility = Visibility.Collapsed;

        // A different texture means different stages (a material with recursive passes on
        // one block and none on the next), so let the next render rebuild from scratch.
        _renderedLayoutKey = "";
        MarkDirty(immediate: true);
    }

    // ── Editors ──────────────────────────────────────────────────────────────────────

    private void RebuildMaterialEditor()
    {
        MaterialEditorPanel.Children.Clear();
        ReflectiveEditor.BuildObjectEditor(MaterialEditorPanel, _material, () =>
        {
            // Adding or removing a recursive pass changes which stages exist.
            _renderedLayoutKey = "";
            MarkDirty();
        });
    }

    private void RebuildKnobEditor()
    {
        KnobEditorPanel.Children.Clear();
        ReflectiveEditor.BuildKnobEditor(KnobEditorPanel, _tuning, () => MarkDirty());
    }

    private void LoadMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        var name = TextureNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetStatus("Type a texture name first.");
            return;
        }

        if (_materialsByName.TryGetValue(name, out var entry))
        {
            _material = MaterialEntryJson.Clone(entry);
            SetStatus($"Loaded the exact materials.json entry for '{name}'.");
        }
        else
        {
            _material = _materialsByName.TryGetValue("default", out var fallback)
                ? MaterialEntryJson.Clone(fallback)
                : new MaterialEntry();
            SetStatus($"'{name}' has no entry in materials.json - loaded the \"default\" entry, which is what it would actually get.");
        }

        RebuildMaterialEditor();
        _renderedLayoutKey = "";
        MarkDirty(immediate: true);
    }

    private void CopyMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(TextureNameBox.Text) ? "texture_name" : TextureNameBox.Text.Trim();
        CopyToClipboard(MaterialEntryJson.ToFragment(name, _material));
        SetStatus($"Copied the materials.json fragment for '{name}'.");
    }

    private async void PasteMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                SetStatus("There's no text on the clipboard.");
                return;
            }

            var parsed = MaterialEntryJson.FromClipboardText(await view.GetTextAsync());
            if (parsed == null)
            {
                SetStatus("Couldn't read a material entry out of the clipboard text.");
                return;
            }

            _material = parsed;
            RebuildMaterialEditor();
            _renderedLayoutKey = "";
            MarkDirty(immediate: true);
            SetStatus("Loaded a material entry from the clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard read failed: {ex.Message}");
        }
    }

    private void ResetMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        _material = _materialsByName.TryGetValue("default", out var fallback)
            ? MaterialEntryJson.Clone(fallback)
            : new MaterialEntry();
        RebuildMaterialEditor();
        _renderedLayoutKey = "";
        MarkDirty(immediate: true);
        SetStatus("Material reset to the \"default\" entry.");
    }

    private void CopyTuningButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(MaterialEntryJson.ToCSharp(_tuning));
        SetStatus("Copied the changed tuning values as C#.");
    }

    private void ResetTuningButton_Click(object sender, RoutedEventArgs e)
    {
        _tuning = PipelineTuning.Default.Clone();
        RebuildKnobEditor();
        MarkDirty(immediate: true);
        SetStatus("Tuning reset to the shipping defaults.");
    }

    private void SecondaryModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    private void RerunButton_Click(object sender, RoutedEventArgs e) => MarkDirty(immediate: true);

    private void TileSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Purely a redraw - no need to put the pipeline through its paces again.
        if (_result != null) RenderResult(_result);
    }

    // ── Running ──────────────────────────────────────────────────────────────────────

    private void MarkDirty(bool immediate = false)
    {
        if (_colorPath == null) return;

        if (immediate)
        {
            _debounce.Stop();
            _ = RunPreviewAsync();
            return;
        }

        if (LiveUpdateToggle == null || !LiveUpdateToggle.IsOn) return;

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Snapshots the live-edited state, runs all three chains off the UI thread, then
    /// renders. Overlapping requests collapse: a change arriving mid-run just sets a flag,
    /// and the loop runs once more with the newest values rather than queueing up a run
    /// per slider tick.
    /// </summary>
    private async Task RunPreviewAsync()
    {
        if (_colorPath == null) return;

        if (_running)
        {
            _rerunRequested = true;
            return;
        }

        _running = true;
        try
        {
            do
            {
                _rerunRequested = false;

                var path = _colorPath;
                var mode = (SecondaryPbrMode)Math.Clamp(SecondaryModeCombo.SelectedIndex, 0, 3);
                var name = TextureNameBox.Text.Trim();
                var blacklisted = _blacklist?.IsBlacklisted(name) ?? false;
                var exact = !string.IsNullOrEmpty(name) && _materialsByName.ContainsKey(name);

                // Snapshot both: the reflective editors mutate these objects live, and a
                // background run reading a half-applied edit would produce a preview of a
                // state that never existed.
                var material = MaterialEntryJson.Clone(_material);
                var tuning = _tuning.Clone();

                var result = await Task.Run(() =>
                    PipelinePreviewRunner.Run(path, material, tuning, mode, blacklisted, exact));

                // Render before disposing the previous run: the tiles hold references to
                // the sink's bitmaps, and the view switcher can fire at any moment.
                var previous = _result;
                _result = result;
                RenderResult(result);
                previous?.Dispose();

                SetStatus(result.Error != null
                    ? $"Run failed: {result.Error}"
                    : $"{result.Sink.Stages.Count} stages · {result.Elapsed.TotalMilliseconds:0} ms · resolved secondary: {result.ResolvedMode}");
            }
            while (_rerunRequested);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline preview run loop failed: {ex}");
            SetStatus($"Preview failed: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────

    private int TileSize => (int)Math.Round(TileSizeSlider?.Value ?? 176);

    /// <summary>
    /// Draws a completed run. The stage set is usually identical between runs (only the
    /// pixels changed), so the control tree is rebuilt only when the set of stage ids
    /// actually differs - otherwise images and notes are swapped in place, which is what
    /// keeps dragging a slider smooth instead of rebuilding a few hundred controls per
    /// tick.
    /// </summary>
    private void RenderResult(PipelinePreviewResult result)
    {
        var stages = result.Sink.Stages.OrderBy(s => PipelineStageCatalog.ChainOrder(s.Chain))
                                       .ThenBy(s => s.Order)
                                       .ToList();

        var layoutKey = string.Join("|", stages.Select(s => s.Id)) + $"@{TileSize}";
        if (layoutKey != _renderedLayoutKey)
        {
            RebuildStageLayout(stages);
            _renderedLayoutKey = layoutKey;
        }

        foreach (var stage in stages)
            if (_tiles.TryGetValue(stage.Id, out var tile))
                tile.Update(stage, TileSize, _selectedView);

        RenderOutputsStrip(stages);

        // A run that threw produces few or no stages, and a results area that just goes
        // blank reads as a hang. Say what happened where the eye already is.
        if (result.Error != null)
        {
            EmptyHint.Text = $"This texture didn't make it through the pipeline:\n\n{result.Error}";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (stages.Count == 0)
        {
            EmptyHint.Text = "The run produced no stages. Is this a Debug build? Stage capture is compiled out of Release.";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildStageLayout(List<PipelineStage> stages)
    {
        ChainsPanel.Children.Clear();
        _tiles.Clear();

        foreach (var group in stages.GroupBy(s => s.Chain)
                                    .OrderBy(g => PipelineStageCatalog.ChainOrder(g.Key)))
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 6) };

            var first = true;
            foreach (var stage in group.OrderBy(s => s.Order))
            {
                // Arrows between tiles: the filmstrip is in execution order, and making
                // that visually explicit is most of why it reads as a pipeline at all.
                if (!first)
                {
                    strip.Children.Add(new TextBlock
                    {
                        Text = "→",
                        FontSize = 18,
                        Opacity = 0.35,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                first = false;

                var tile = new StageTile(stage.Id, ShowStageDetail, _selectedView, RootGrid.Resources);
                _tiles[stage.Id] = tile;
                strip.Children.Add(tile.Root);
            }

            ChainsPanel.Children.Add(new Expander
            {
                Header = $"{PipelineStageCatalog.ChainTitle(group.Key)}  ·  {group.Count()} steps",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = true,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = strip,
                },
            });
        }
    }

    private void RenderOutputsStrip(List<PipelineStage> stages)
    {
        OutputsStrip.Children.Clear();

        var featuredSize = Math.Max(TileSize, 192);
        foreach (var id in FeaturedStageIds)
        {
            var stage = stages.FirstOrDefault(s => s.Id == id);
            if (stage == null) continue;

            var tile = new StageTile(stage.Id, ShowStageDetail, _selectedView, RootGrid.Resources, featured: true);
            tile.Update(stage, featuredSize, _selectedView);
            OutputsStrip.Children.Add(tile.Root);
        }
    }

    /// <summary>Full-size look at one stage, with everything the catalog and the trace have
    /// to say about it. This is where the close eyeballing actually happens.</summary>
    private async void ShowStageDetail(string stageId)
    {
        // ContentDialog throws if a second one is opened while the first is up, and these
        // are opened by pointer events that can easily double-fire.
        if (_detailOpen) return;

        var stage = _result?.Sink.Stages.FirstOrDefault(s => s.Id == stageId);
        if (stage == null) return;

        var (title, blurb) = PipelineStageCatalog.Describe(stageId);

        var body = new StackPanel { Spacing = 10, Width = 640 };

        if (!string.IsNullOrEmpty(blurb))
            body.Children.Add(new TextBlock { Text = blurb, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });

        body.Children.Add(new TextBlock
        {
            Text = stageId,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Opacity = 0.55,
        });

        if (stage.Views.Count > 0)
        {
            var images = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var view in stage.Views)
            {
                var column = new StackPanel { Spacing = 4 };
                column.Children.Add(new Image
                {
                    Source = PreviewImaging.ToPreviewSource(view.Image, 256),
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                column.Children.Add(new TextBlock
                {
                    Text = view.Name,
                    FontSize = 11,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                images.Children.Add(column);
            }

            body.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = images,
            });
        }

        if (stage.Curve != null)
            body.Children.Add(BuildCurve(stage.Curve, 600, 220));

        foreach (var (key, value) in stage.Notes)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{key}:  {value}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = body, MaxHeight = 620 },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
        };

        _detailOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't open the stage detail dialog: {ex.Message}");
        }
        finally
        {
            _detailOpen = false;
        }
    }

    /// <summary>Plots a sampled 1-D function. Used for the normal map's response curve,
    /// which isn't a per-pixel field and would be meaningless drawn as one.</summary>
    internal static FrameworkElement BuildCurve(PipelineCurve curve, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

        var maxY = curve.Y.DefaultIfEmpty(0).Max();
        var minY = curve.Y.DefaultIfEmpty(0).Min();
        var spanY = Math.Max(maxY - minY, 1e-9);
        var maxX = curve.X.DefaultIfEmpty(0).Max();
        var minX = curve.X.DefaultIfEmpty(0).Min();
        var spanX = Math.Max(maxX - minX, 1e-9);

        var frame = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            StrokeThickness = 1,
            Opacity = 0.4,
        };
        frame.Points.Add(new Windows.Foundation.Point(0, 0));
        frame.Points.Add(new Windows.Foundation.Point(0, height));
        frame.Points.Add(new Windows.Foundation.Point(width, height));
        canvas.Children.Add(frame);

        var line = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = 2,
        };
        for (var i = 0; i < curve.X.Length; i++)
        {
            var px = (curve.X[i] - minX) / spanX * width;
            var py = height - (curve.Y[i] - minY) / spanY * height;
            line.Points.Add(new Windows.Foundation.Point(px, py));
        }
        canvas.Children.Add(line);

        var caption = new TextBlock
        {
            Text = $"{curve.XLabel}  {minX:0.###} → {maxX:0.###}        {curve.YLabel}  {minY:0.###} → {maxY:0.###}",
            FontSize = 11,
            Opacity = 0.7,
        };

        return new StackPanel { Spacing = 4, Children = { canvas, caption } };
    }

    // ── Export ───────────────────────────────────────────────────────────────────────

    private async void ExportStagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            SetStatus("Nothing to export yet.");
            return;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var written = 0;
        var failed = 0;

        foreach (var stage in _result.Sink.Stages)
        {
            foreach (var view in stage.Views)
            {
                try
                {
                    var safeId = string.Join("_", stage.Id.Split(Path.GetInvalidFileNameChars()));
                    var safeView = string.Join("_", view.Name.Split(Path.GetInvalidFileNameChars()));
                    var target = Path.Combine(folder.Path, $"{stage.Order:00}_{safeId}_{safeView}.png");
                    view.Image.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                    written++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't export '{stage.Id}/{view.Name}': {ex.Message}");
                }
            }
        }

        SetStatus($"Exported {written} stage images to {folder.Path}{(failed > 0 ? $" ({failed} failed)" : "")}.");
    }

    // ── Small helpers ────────────────────────────────────────────────────────────────

    private void SetStatus(string text) => StatusText.Text = text;

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't write to the clipboard: {ex.Message}");
        }
    }

    /// <summary>
    /// One stage's tile. Built once per layout and then updated in place, so that the
    /// image, the notes and - importantly - the selected channel survive a re-run.
    /// </summary>
    private sealed class StageTile
    {
        private readonly string _stageId;
        private readonly Image _image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly ComboBox _viewPicker = new() { FontSize = 11, MinWidth = 84, Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _notes = new() { Spacing = 1, Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _body;
        private readonly TextBlock _title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly Dictionary<string, string> _selectedView;

        private List<PipelineStageView> _views = new();
        private int _size = 176;
        private bool _suppressSelection;

        public Border Root { get; }

        public StageTile(string stageId, Action<string> onOpenDetail, Dictionary<string, string> selectedView, ResourceDictionary styles, bool featured = false)
        {
            _stageId = stageId;
            _selectedView = selectedView;

            var (title, blurb) = PipelineStageCatalog.Describe(stageId);
            _title.Text = title;

            var imageFrame = new Border { Child = _image };
            if (styles.TryGetValue("StageImageFrameStyle", out var frameStyle) && frameStyle is Style fs)
                imageFrame.Style = fs;

            _body = new StackPanel { Spacing = 2 };
            _body.Children.Add(_title);
            _body.Children.Add(imageFrame);
            _body.Children.Add(_viewPicker);
            _body.Children.Add(_notes);

            Root = new Border { Child = _body };
            var wantedStyle = featured ? "FeaturedStageTileStyle" : "StageTileStyle";
            if (styles.TryGetValue(wantedStyle, out var tileStyle) && tileStyle is Style ts)
                Root.Style = ts;

            if (!string.IsNullOrEmpty(blurb))
                ToolTipService.SetToolTip(Root, new ToolTip { Content = new TextBlock { Text = blurb, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } });

            // Only the image opens the detail view. Hanging this off the whole tile would
            // mean every click on the view picker also popped a dialog.
            imageFrame.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                onOpenDetail(stageId);
            };
            imageFrame.PointerEntered += (_, _) => imageFrame.Opacity = 0.8;
            imageFrame.PointerExited += (_, _) => imageFrame.Opacity = 1.0;

            _viewPicker.SelectionChanged += (_, _) =>
            {
                if (_suppressSelection || _viewPicker.SelectedItem is not string name) return;
                _selectedView[_stageId] = name;
                ApplySelectedView();
            };
        }

        public void Update(PipelineStage stage, int size, Dictionary<string, string> selectedView)
        {
            _views = stage.Views;
            _size = size;

            var names = _views.Select(v => v.Name).ToList();
            var currentItems = _viewPicker.Items.OfType<string>().ToList();

            if (!currentItems.SequenceEqual(names))
            {
                _suppressSelection = true;
                _viewPicker.Items.Clear();
                foreach (var n in names) _viewPicker.Items.Add(n);
                _suppressSelection = false;
            }

            // A single-view stage doesn't need a picker at all - the label is already in
            // the title and the row would just be noise on two dozen tiles.
            _viewPicker.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (names.Count > 0)
            {
                var wanted = selectedView.TryGetValue(_stageId, out var remembered) && names.Contains(remembered)
                    ? remembered
                    : names[0];

                _suppressSelection = true;
                _viewPicker.SelectedItem = wanted;
                _suppressSelection = false;
            }

            ApplySelectedView();
            ApplyNotes(stage);
        }

        private void ApplySelectedView()
        {
            if (_views.Count == 0)
            {
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
                return;
            }

            var name = _viewPicker.SelectedItem as string ?? _views[0].Name;
            var view = _views.FirstOrDefault(v => v.Name == name) ?? _views[0];

            _image.Visibility = Visibility.Visible;
            try
            {
                _image.Source = PreviewImaging.ToPreviewSource(view.Image, _size);
            }
            catch (Exception ex)
            {
                // A disposed bitmap from a superseded run is the only realistic cause, and
                // the next render replaces it anyway - not worth failing the whole redraw.
                Trace.WriteLine($"[ALCHITEX] Pipeline preview couldn't render '{_stageId}/{name}': {ex.Message}");
            }
        }

        private void ApplyNotes(PipelineStage stage)
        {
            _notes.Children.Clear();

            // Notes-only stages (the run summary) show everything; image stages show a few
            // and keep the rest for the detail dialog, so tiles stay a consistent height.
            var limit = stage.Views.Count == 0 ? int.MaxValue : 3;

            foreach (var (key, value) in stage.Notes.Take(limit))
            {
                _notes.Children.Add(new TextBlock
                {
                    Text = $"{key}: {value}",
                    FontSize = 10,
                    Opacity = 0.7,
                    MaxWidth = Math.Max(_size, 160),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            if (stage.Notes.Count > limit)
            {
                _notes.Children.Add(new TextBlock
                {
                    Text = $"+{stage.Notes.Count - limit} more...",
                    FontSize = 10,
                    Opacity = 0.5,
                });
            }

            if (stage.Curve != null && stage.Views.Count == 0)
                _notes.Children.Add(BuildCurve(stage.Curve, Math.Max(_size, 160), Math.Max(_size, 160) * 0.55));
        }
    }
}
