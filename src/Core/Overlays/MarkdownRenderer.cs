using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Markdig;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MI = Markdig.Syntax.Inlines;
using MS = Markdig.Syntax;
using MT = Markdig.Extensions.Tables;
using TL = Markdig.Extensions.TaskLists;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>Result of <see cref="MarkdownRenderer.Render"/> - the built visual tree, plus every heading's GitHub-style anchor slug mapped to the element it belongs to, so the host can scroll to a URL fragment or an in-document TOC link.</summary>
public sealed class MarkdownRenderResult
{
    public required UIElement Content { get; init; }
    public required IReadOnlyDictionary<string, FrameworkElement> Anchors { get; init; }
    public required IReadOnlyList<MarkdownSearchEntry> SearchEntries { get; init; }
}

/// <summary>One searchable unit of text (a heading, a paragraph, or a code block) and the element it renders as - see <see cref="MarkdownRenderer.SetSearchHighlight"/> for what the host can do with that element.</summary>
public sealed class MarkdownSearchEntry
{
    public required string Text { get; init; }
    public required FrameworkElement Element { get; init; }
}

/// <summary>
/// Turns a Markdig <see cref="MS.MarkdownDocument"/> into a plain WinUI visual tree - no HTML,
/// no WebView2, just <see cref="TextBlock"/>/<see cref="RichTextBlock"/>/<see cref="Grid"/> the
/// same way any other part of this app builds UI in code-behind (compare
/// <c>WebImportOverlay.AddDownloadRow</c>). Deliberately doesn't use Markdig's own HTML
/// renderer - there is no HTML surface to render into here, and hand-walking the AST is what
/// lets images, relative links and GitHub-style heading anchors resolve against the *source*
/// repository rather than against nothing.
///
/// <para><b>Only what shows up in a real GitHub README is covered</b>: headings, paragraphs,
/// bold/italic/strikethrough/code spans, links (including "badge" images that are themselves a
/// link), autolinks, lists (ordered/unordered/nested/task lists), fenced and indented code
/// blocks, block quotes, thematic breaks, and pipe tables. Raw HTML blocks/inlines are skipped
/// outright rather than guessed at - most README HTML is layout-only (centering, `&lt;br&gt;`
/// spacing) that has no honest WinUI equivalent, and guessing wrong reads worse than
/// omitting.</para>
///
/// <para><b>The InlineUIContainer trap.</b> WinUI only allows an <see cref="InlineUIContainer"/>
/// (needed for a real, decoded <see cref="Image"/>) as a *direct* child of a
/// <see cref="Paragraph"/>'s <see cref="InlineCollection"/> - nested one level inside a
/// <see cref="Span"/>/<see cref="Bold"/>/<see cref="Italic"/>/<see cref="Hyperlink"/> and it
/// throws at the point the child is added. Every inline-building method therefore threads an
/// <c>allowInlineUI</c> flag that goes false the moment recursion enters a nested span, so an
/// image inside emphasis degrades to its alt text instead of crashing the whole render. The one
/// case worth doing properly rather than degrading is a *linked* image - `[![badge](img)](url)`,
/// extremely common in READMEs - which is detected before generic dispatch and rendered as one
/// top-level, tappable <see cref="InlineUIContainer"/> instead of a Hyperlink wrapping one.</para>
///
/// <para><b>Anchor slugs are best-effort, not a reimplementation of GitHub's slugger.</b>
/// <see cref="ToGithubAnchor"/> lowercases, keeps letters/digits/hyphen/underscore/space, drops
/// everything else (punctuation, emoji), and turns spaces into hyphens. That alone matches
/// GitHub exactly for every heading observed in this app's own two linked documents (plain
/// "Documentation" -> "documentation", emoji-prefixed "❌ Unresolved" -> "-unresolved" via the
/// dropped emoji leaving a lone leading space). It will drift from GitHub's real algorithm on
/// headings with repeated punctuation runs or non-Latin scripts; the fallback for a miss is
/// simply "the initial fragment doesn't auto-scroll", never a crash.</para>
///
/// <para><b>Search entries exist for in-document find, and every heading/paragraph is wrapped in
/// a bare <see cref="Border"/> only because of it.</b> <see cref="RichTextBlock"/> has no
/// <c>Background</c> property, so there is nothing <see cref="SetSearchHighlight"/> could paint
/// to mark "this is the current match" without that wrapper. The wrapper carries no border,
/// padding or background of its own - it is invisible until highlighted, and its <c>Tag</c> is
/// what <see cref="SetSearchHighlight"/> restores to afterward (<see langword="null"/> there, the
/// real card brush on a code block's own <see cref="Border"/>). Only headings, paragraphs and
/// code blocks are indexed; a match inside a list item or table cell is still found because those
/// containers render their content through the same two methods recursively.</para>
///
/// <para><b>Never throws.</b> <see cref="Render"/> wraps the whole structured pass in one
/// try/catch; a parse or layout surprise anywhere degrades to a monospace dump of the raw
/// markdown text rather than an error the caller has to handle.</para>
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly string _blobBaseUrl;
    private readonly string _rawBaseUrl;
    private readonly Action<string> _openLink;
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedAnchorSlugs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarkdownSearchEntry> _searchEntries = new();

    private MarkdownRenderer(string blobBaseUrl, string rawBaseUrl, Action<string> openLink)
    {
        _blobBaseUrl = blobBaseUrl;
        _rawBaseUrl = rawBaseUrl;
        _openLink = openLink;
    }

    /// <summary>
    /// Renders <paramref name="markdown"/> into a visual tree. <paramref name="blobBaseUrl"/> and
    /// <paramref name="rawBaseUrl"/> are the directory-level URLs (trailing slash) that relative
    /// links and relative image sources resolve against, respectively - see
    /// <c>MarkdownOverlay.ResolveGithubUrls</c> for how those are derived from the page URL a
    /// caller passes to <c>MarkdownOverlay.Show</c>. <paramref name="openLink"/> is called with
    /// either an absolute URL (open externally) or a bare <c>#slug</c> (scroll within this
    /// document) whenever the user activates a link or a tappable image.
    /// </summary>
    public static MarkdownRenderResult Render(string markdown, string blobBaseUrl, string rawBaseUrl, Action<string> openLink)
    {
        var renderer = new MarkdownRenderer(blobBaseUrl, rawBaseUrl, openLink);
        try
        {
            var document = Markdig.Markdown.Parse(markdown, Pipeline);
            var panel = renderer.RenderDocument(document);
            return new MarkdownRenderResult { Content = panel, Anchors = renderer._anchors, SearchEntries = renderer._searchEntries };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownRenderer] Structured render failed, falling back to plain text: {ex}");
            var fallback = new TextBlock
            {
                Text = markdown,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };
            return new MarkdownRenderResult
            {
                Content = fallback,
                Anchors = new Dictionary<string, FrameworkElement>(),
                // The structured pass never ran, so there's nothing to index - search just finds
                // nothing against a raw-text fallback rather than crashing.
                SearchEntries = Array.Empty<MarkdownSearchEntry>()
            };
        }
    }

    /// <summary>
    /// Toggles the "this is the current search match" highlight on an element from a
    /// <see cref="MarkdownSearchEntry"/>. Only <see cref="RichTextBlock"/> (headings/paragraphs)
    /// and <see cref="Border"/> (code blocks) are ever handed out as search entries, so those are
    /// the only two cases - restoring a code block's background means putting back the same card
    /// brush <see cref="RenderCodeBlock"/> gave it, not clearing to nothing.
    /// </summary>
    public static void SetSearchHighlight(FrameworkElement element, bool highlighted)
    {
        if (element is not Border border) return;

        // A heading/paragraph's Border has no background of its own (see RenderHeading /
        // RenderParagraph - it exists purely so this method has something to paint), so
        // "restore" there means null; a code block's Border is a real card and restoring means
        // putting the same brush RenderCodeBlock gave it back, not clearing it to nothing.
        border.Background = highlighted
            ? HighlightBrush()
            : border.Tag as Brush;
    }

    private static Brush HighlightBrush() => new SolidColorBrush
    {
        Color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"],
        Opacity = 0.35
    };

    // =========================================================================
    // Blocks
    // =========================================================================

    private Panel RenderDocument(MS.MarkdownDocument document)
    {
        var root = new StackPanel { Spacing = 14 };
        foreach (var block in document)
            RenderBlockInto(root, block);
        return root;
    }

    private void RenderBlockInto(Panel parent, MS.Block block)
    {
        switch (block)
        {
            case MS.HeadingBlock heading:
                parent.Children.Add(RenderHeading(heading));
                break;

            case MT.Table table:
                parent.Children.Add(RenderTable(table));
                break;

            case MS.QuoteBlock quote:
                parent.Children.Add(RenderQuote(quote));
                break;

            case MS.ListBlock list:
                parent.Children.Add(RenderList(list));
                break;

            case MS.FencedCodeBlock fenced:
                parent.Children.Add(RenderCodeBlock(fenced));
                break;

            case MS.CodeBlock code:
                parent.Children.Add(RenderCodeBlock(code));
                break;

            case MS.ThematicBreakBlock:
                parent.Children.Add(RenderThematicBreak());
                break;

            case MS.ParagraphBlock para:
                parent.Children.Add(RenderParagraph(para));
                break;

            case MS.HtmlBlock:
                // Raw HTML - see the class doc. Skipped rather than guessed at.
                break;

            // Any other container (including one from an extension this renderer doesn't know
            // about) - render its children rather than dropping the whole thing on the floor.
            case MS.ContainerBlock container:
                foreach (var child in container)
                    RenderBlockInto(parent, child);
                break;
        }
    }

    private FrameworkElement RenderHeading(MS.HeadingBlock heading)
    {
        var (fontSize, weight) = heading.Level switch
        {
            1 => (26.0, FontWeights.Bold),
            2 => (22.0, FontWeights.Bold),
            3 => (18.0, FontWeights.SemiBold),
            4 => (16.0, FontWeights.SemiBold),
            5 => (14.0, FontWeights.SemiBold),
            _ => (13.0, FontWeights.SemiBold),
        };

        var rtb = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        var paragraph = new Paragraph { FontSize = fontSize, FontWeight = weight };
        AppendInlines(paragraph.Inlines, heading.Inline);
        rtb.Blocks.Add(paragraph);

        // RichTextBlock has no Background property to toggle for a search highlight, so every
        // heading/paragraph is wrapped in a bare Border purely to give SetSearchHighlight
        // something to paint - Tag=null is what it restores to when the highlight clears.
        var searchTarget = new Border { Child = rtb, Tag = null };

        FrameworkElement result = searchTarget;
        if (heading.Level <= 2)
        {
            // GitHub underlines H1/H2 - a thin divider is the closest honest equivalent without
            // a border-bottom concept on a RichTextBlock.
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(searchTarget);
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
            });
            result = stack;
        }
        result.Margin = new Thickness(0, heading.Level == 1 ? 6 : 2, 0, 0);

        var plainText = GetPlainText(heading.Inline);
        var slug = ToGithubAnchor(plainText);
        if (!string.IsNullOrEmpty(slug))
            _anchors[MakeUniqueSlug(slug)] = result;

        if (!string.IsNullOrWhiteSpace(plainText))
            _searchEntries.Add(new MarkdownSearchEntry { Text = plainText, Element = searchTarget });

        return result;
    }

    private FrameworkElement RenderParagraph(MS.ParagraphBlock para, bool skipLeadingTaskInline = false)
    {
        var rtb = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var p = new Paragraph();

        if (skipLeadingTaskInline && para.Inline?.FirstChild is TL.TaskList)
        {
            foreach (var inline in para.Inline.Skip(1))
                AppendInline(p.Inlines, inline, allowInlineUI: true);
        }
        else
        {
            AppendInlines(p.Inlines, para.Inline);
        }

        rtb.Blocks.Add(p);

        // See RenderHeading - the Border exists only so a search highlight has a Background to set.
        var searchTarget = new Border { Child = rtb, Tag = null };

        var plainText = GetPlainText(para.Inline);
        if (!string.IsNullOrWhiteSpace(plainText))
            _searchEntries.Add(new MarkdownSearchEntry { Text = plainText, Element = searchTarget });

        return searchTarget;
    }

    private FrameworkElement RenderList(MS.ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list)
        {
            if (item is not MS.ListItemBlock listItem) continue;
            panel.Children.Add(RenderListItem(listItem, list.IsOrdered, index));
            index++;
        }

        return panel;
    }

    private FrameworkElement RenderListItem(MS.ListItemBlock item, bool ordered, int index)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Markdig's task-list extension turns a leading "[ ] "/"[x] " in the item's first
        // paragraph into a TaskList inline rather than a separate block - the only way to find
        // it is to look at that paragraph's first inline.
        var firstBlock = item.FirstOrDefault();
        var isTask = firstBlock is MS.ParagraphBlock { Inline.FirstChild: TL.TaskList } ;
        var isChecked = isTask && ((TL.TaskList)((MS.ParagraphBlock)firstBlock!).Inline!.FirstChild!).Checked;

        FrameworkElement marker = isTask
            ? new CheckBox { IsChecked = isChecked, IsEnabled = false, Padding = new Thickness(0), Margin = new Thickness(0, 1, 0, 0) }
            : new TextBlock { Text = ordered ? $"{index}." : "•", FontSize = 14, Margin = new Thickness(0, 1, 0, 0) };
        Grid.SetColumn(marker, 0);
        row.Children.Add(marker);

        var content = new StackPanel { Spacing = 6 };
        Grid.SetColumn(content, 1);
        foreach (var block in item)
        {
            if (isTask && ReferenceEquals(block, firstBlock))
            {
                content.Children.Add(RenderParagraph((MS.ParagraphBlock)block, skipLeadingTaskInline: true));
                continue;
            }
            RenderBlockInto(content, block);
        }
        row.Children.Add(content);

        return row;
    }

    private FrameworkElement RenderQuote(MS.QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var block in quote)
            RenderBlockInto(content, block);

        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 4, 10, 4),
            Opacity = 0.85,
            Child = content
        };
    }

    private FrameworkElement RenderCodeBlock(MS.CodeBlock code)
    {
        var text = code.Lines.ToString();
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };

        var codeBackground = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        var border = new Border
        {
            Background = codeBackground,
            // SetSearchHighlight restores to this on Tag - see the class doc on that method.
            Tag = codeBackground,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = textBlock
            }
        };

        if (!string.IsNullOrWhiteSpace(text))
            _searchEntries.Add(new MarkdownSearchEntry { Text = text, Element = border });

        return border;
    }

    private static FrameworkElement RenderThematicBreak() => new Border
    {
        Height = 1,
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        Margin = new Thickness(0, 4, 0, 4)
    };

    private FrameworkElement RenderTable(MT.Table table)
    {
        var columnCount = table.ColumnDefinitions.Count;
        if (columnCount == 0 && table.FirstOrDefault() is MT.TableRow firstRow)
            columnCount = firstRow.Count;
        columnCount = Math.Max(columnCount, 1);

        var grid = new Grid();
        for (var c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rowIndex = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is not MT.TableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var colIndex = 0;
            foreach (var cellBlock in row)
            {
                if (cellBlock is not MT.TableCell cell)
                {
                    colIndex++;
                    continue;
                }

                var cellContent = new StackPanel { Spacing = 4 };
                foreach (var block in cell)
                    RenderBlockInto(cellContent, block);

                if (row.IsHeader)
                {
                    foreach (var rtb in cellContent.Children.OfType<RichTextBlock>())
                        foreach (var p in rtb.Blocks.OfType<Paragraph>())
                            p.FontWeight = FontWeights.SemiBold;
                }

                if (colIndex < table.ColumnDefinitions.Count)
                {
                    cellContent.HorizontalAlignment = table.ColumnDefinitions[colIndex].Alignment switch
                    {
                        MT.TableColumnAlign.Center => HorizontalAlignment.Center,
                        MT.TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Left
                    };
                }

                var border = new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = row.IsHeader ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] : null,
                    Child = cellContent
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, colIndex);
                var span = Math.Max(1, cell.ColumnSpan);
                if (span > 1) Grid.SetColumnSpan(border, span);
                grid.Children.Add(border);

                colIndex += span;
            }
            rowIndex++;
        }

        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid
            }
        };
    }

    // =========================================================================
    // Inlines
    // =========================================================================

    private void AppendInlines(InlineCollection target, MI.ContainerInline? container, bool allowInlineUI = true)
    {
        if (container is null) return;
        foreach (var inline in container)
            AppendInline(target, inline, allowInlineUI);
    }

    private void AppendInline(InlineCollection target, MI.Inline inline, bool allowInlineUI)
    {
        switch (inline)
        {
            case MI.LiteralInline lit:
                target.Add(new Run { Text = lit.Content.ToString() });
                break;

            case MI.CodeInline code:
                // A styled Run rather than an InlineUIContainer chip - inline code shows up
                // inside emphasis and table cells constantly, and only a Run is legal there.
                target.Add(new Run
                {
                    Text = code.Content,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                });
                break;

            case TL.TaskList task:
                // Reached only if a task marker shows up outside a list item's first paragraph
                // (RenderListItem intercepts the normal case and draws a real CheckBox instead).
                target.Add(new Run { Text = task.Checked ? "☑ " : "☐ " });
                break;

            case MI.EmphasisInline emphasis:
                AppendEmphasis(target, emphasis);
                break;

            case MI.LinkInline { IsImage: true } image:
                target.Add(allowInlineUI ? BuildInlineImage(image) : BuildImageFallbackRun(image));
                break;

            case MI.LinkInline link when !link.IsImage && allowInlineUI && TryGetSoleImageChild(link, out var soleImage):
                target.Add(BuildClickableImage(soleImage, ResolveLink(link.Url ?? string.Empty)));
                break;

            case MI.LinkInline link:
                target.Add(BuildHyperlink(link));
                break;

            case MI.AutolinkInline autolink:
                target.Add(BuildAutolink(autolink));
                break;

            case MI.LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run { Text = " " });
                break;

            case MI.HtmlEntityInline entity:
                target.Add(new Run { Text = entity.Transcoded.ToString() });
                break;

            case MI.HtmlInline:
                // Raw inline HTML - same call as HtmlBlock above, skipped rather than guessed at.
                break;

            case MI.ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(target, child, allowInlineUI);
                break;
        }
    }

    private void AppendEmphasis(InlineCollection target, MI.EmphasisInline emphasis)
    {
        Span span = emphasis.DelimiterChar == '~'
            ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
            : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();

        // Nested content can never itself hold an InlineUIContainer - see the class doc.
        AppendInlines(span.Inlines, emphasis, allowInlineUI: false);
        target.Add(span);
    }

    private Inline BuildHyperlink(MI.LinkInline link)
    {
        var resolvedUrl = ResolveLink(link.Url ?? string.Empty);
        var hyperlink = new Hyperlink();
        if (!string.IsNullOrEmpty(link.Title))
            ToolTipService.SetToolTip(hyperlink, link.Title);

        AppendInlines(hyperlink.Inlines, link, allowInlineUI: false);
        if (hyperlink.Inlines.Count == 0)
            hyperlink.Inlines.Add(new Run { Text = resolvedUrl });

        hyperlink.Click += (_, _) => _openLink(resolvedUrl);
        return hyperlink;
    }

    private Inline BuildAutolink(MI.AutolinkInline autolink)
    {
        var url = autolink.IsEmail ? $"mailto:{autolink.Url}" : ResolveLink(autolink.Url);
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(new Run { Text = autolink.Url });
        hyperlink.Click += (_, _) => _openLink(url);
        return hyperlink;
    }

    private static bool TryGetSoleImageChild(MI.LinkInline link, out MI.LinkInline image)
    {
        if (link.FirstChild is MI.LinkInline { IsImage: true } img && ReferenceEquals(link.FirstChild, link.LastChild))
        {
            image = img;
            return true;
        }
        image = null!;
        return false;
    }

    /// <summary>A badge-style "linked image" - `[![alt](img)](url)`. Rendered as one top-level, tappable InlineUIContainer rather than a Hyperlink wrapping an image, which WinUI doesn't allow (see the class doc).</summary>
    private Inline BuildClickableImage(MI.LinkInline image, string linkUrl)
    {
        var built = BuildInlineImage(image);
        if (built is InlineUIContainer { Child: UIElement el })
        {
            el.PointerPressed += (_, _) => _openLink(linkUrl);
            ToolTipService.SetToolTip(el, linkUrl);
        }
        return built;
    }

    private const int ImageDecodeHeight = 800;

    private Inline BuildInlineImage(MI.LinkInline image)
    {
        var altText = GetPlainText(image);
        var src = ResolveImage(image.Url ?? string.Empty);

        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri))
            return BuildImageFallbackRun(image);

        var host = new Grid();
        var img = new Image
        {
            MaxHeight = 400,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTipService.SetToolTip(img, altText);

        img.ImageFailed += (_, _) =>
        {
            host.Children.Clear();
            host.Children.Add(BuildBrokenImageVisual(altText));
        };

        host.Children.Add(img);

        // Height is what bounds these on screen (MaxHeight above, Stretch.Uniform), so it is
        // what bounds the decode: 2x that for 200% scale. The source is a remote URL of
        // unknown size and a decoded image costs width x height x 4 bytes of graphics memory
        // whatever it weighs on the wire. DecodePixelHeight is ignored unless it is set before
        // UriSource.
        var bitmap = new BitmapImage { DecodePixelHeight = ImageDecodeHeight };
        bitmap.UriSource = uri;
        img.Source = bitmap;

        return new InlineUIContainer { Child = host };
    }

    private static FrameworkElement BuildBrokenImageVisual(string altText) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Opacity = 0.6,
        Children =
        {
            new FontIcon { Glyph = "", FontSize = 14 },
            new TextBlock
            {
                Text = string.IsNullOrEmpty(altText) ? "Image failed to load" : altText,
                FontSize = 12,
                FontStyle = Windows.UI.Text.FontStyle.Italic
            }
        }
    };

    private Run BuildImageFallbackRun(MI.LinkInline image)
    {
        var alt = GetPlainText(image);
        return new Run { Text = string.IsNullOrEmpty(alt) ? "[image]" : $"[{alt}]" };
    }

    // =========================================================================
    // URL resolution
    // =========================================================================

    private string ResolveLink(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith('#')) return href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(_blobBaseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, href, out var resolved))
            return resolved.ToString();
        return href;
    }

    private string ResolveImage(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return string.Empty;
        if (Uri.TryCreate(src, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(_rawBaseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, src, out var resolved))
            return resolved.ToString();
        return src;
    }

    // =========================================================================
    // Plain-text extraction (alt text, heading anchors)
    // =========================================================================

    private static string GetPlainText(MI.ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new StringBuilder();
        AppendPlainText(sb, container);
        return sb.ToString();
    }

    private static void AppendPlainText(StringBuilder sb, MI.ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case MI.LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case MI.CodeInline code: sb.Append(code.Content); break;
                case MI.HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                case MI.LineBreakInline: sb.Append(' '); break;
                case MI.AutolinkInline autolink: sb.Append(autolink.Url); break;
                case MI.ContainerInline nested: AppendPlainText(sb, nested); break;
            }
        }
    }

    /// <summary>Best-effort GitHub heading-anchor slug - see the class doc for exactly how close this gets.</summary>
    private static string ToGithubAnchor(string headingText)
    {
        var kept = new StringBuilder(headingText.Length);
        foreach (var ch in headingText)
        {
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
                kept.Append(char.ToLowerInvariant(ch));
        }
        return kept.ToString().Replace(' ', '-');
    }

    /// <summary>GitHub disambiguates repeated identical headings with a -1, -2… suffix; matched here so two "Notes" sections don't silently collide onto one anchor.</summary>
    private string MakeUniqueSlug(string baseSlug)
    {
        if (_usedAnchorSlugs.Add(baseSlug)) return baseSlug;
        var i = 1;
        while (!_usedAnchorSlugs.Add($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }
}
