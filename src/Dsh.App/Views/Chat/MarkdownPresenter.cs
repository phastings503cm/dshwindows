using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.Core;

namespace Dsh.App.Views.Chat;

/// <summary>Renders markdown into a read-only, selectable document: paragraphs, headings, lists,
/// quotes, tables, rules, and fenced code blocks with a copy button. While a reply streams in, the
/// document is rebuilt at most a few times a second rather than on every token.</summary>
public sealed class MarkdownPresenter : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPresenter), new PropertyMetadata("", OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private readonly DispatcherTimer _throttle;
    private string? _rendered;

    public MarkdownPresenter()
    {
        SetResourceReference(StyleProperty, "SelectableDocument");
        Document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        Document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        _throttle = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) =>
        {
            _throttle!.Stop();
            Render();
        }, Dispatcher);
        _throttle.Stop();
        Loaded += (_, _) =>
        {
            Document.FontFamily = FontFamily;
            Document.FontSize = FontSize;
            if (_rendered != Markdown) Render();
        };
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (MarkdownPresenter)d;
        // First paint immediately (a recycled container must not show the previous message);
        // follow-up changes while streaming are coalesced.
        if (presenter._rendered is null || !presenter.IsLoaded || !((string)e.NewValue).StartsWith(presenter._rendered, StringComparison.Ordinal))
        {
            presenter._throttle.Stop();
            presenter.Render();
        }
        else if (!presenter._throttle.IsEnabled)
        {
            presenter._throttle.Start();
        }
    }

    private void Render()
    {
        var source = Markdown ?? "";
        _rendered = source;
        var blocks = Dsh.Core.Markdown.Parse(source);
        var document = Document;
        document.Blocks.Clear();
        foreach (var block in blocks)
        {
            if (Build(block) is { } built) document.Blocks.Add(built);
        }
        if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    private Block? Build(MarkdownBlock block)
    {
        switch (block)
        {
            case MarkdownBlock.Paragraph p:
                return Inlines(new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = FontSize * 1.45 }, p.Text);

            case MarkdownBlock.Heading h:
            {
                var size = h.Level switch { 1 => FontSize + 7, 2 => FontSize + 4, 3 => FontSize + 2, _ => FontSize + 1 };
                var paragraph = new Paragraph { FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, h.Level <= 2 ? 8 : 4, 0, 6) };
                return Inlines(paragraph, h.Text);
            }

            case MarkdownBlock.Code c:
                return new BlockUIContainer(new CodeBlockView(c.Language, c.Text)) { Margin = new Thickness(0, 2, 0, 10) };

            case MarkdownBlock.ListItem item:
            {
                var indent = 18 + item.Depth * 18;
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(indent, 0, 0, 4),
                    TextIndent = -14,
                    LineHeight = FontSize * 1.45,
                };
                var marker = new Run(item.Marker.Length <= 1 ? "•  " : item.Marker + " ");
                marker.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                paragraph.Inlines.Add(marker);
                return Inlines(paragraph, item.Text);
            }

            case MarkdownBlock.Quote q:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Margin = new Thickness(0, 0, 0, 8) };
                section.SetResourceReference(Block.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                section.Blocks.Add(Inlines(new Paragraph { Margin = new Thickness(0) }, q.Text));
                return section;
            }

            case MarkdownBlock.Table t:
                return BuildTable(t);

            case MarkdownBlock.Rule:
            {
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 };
                rule.SetResourceReference(Block.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                return rule;
            }
        }
        return null;
    }

    private Table BuildTable(MarkdownBlock.Table t)
    {
        var columns = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(1) };
        table.SetResourceReference(Block.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var group = new TableRowGroup();

        TableRow Row(IReadOnlyList<string> cells, bool header)
        {
            var row = new TableRow();
            if (header) row.SetResourceReference(TableRow.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            for (var i = 0; i < columns; i++)
            {
                var paragraph = Inlines(new Paragraph { Margin = new Thickness(0) }, i < cells.Count ? cells[i] : "");
                if (header) paragraph.FontWeight = FontWeights.SemiBold;
                var cell = new TableCell(paragraph) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                row.Cells.Add(cell);
            }
            return row;
        }

        group.Rows.Add(Row(t.Header, header: true));
        foreach (var row in t.Rows) group.Rows.Add(Row(row, header: false));
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>Append inline markdown (bold, italic, code, strike, links) to a paragraph.</summary>
    private Paragraph Inlines(Paragraph paragraph, string text)
    {
        foreach (var run in MarkdownInline.Parse(text)) paragraph.Inlines.Add(BuildInline(run));
        return paragraph;
    }

    private Inline BuildInline(InlineRun run)
    {
        Inline inline;
        if (run.Style.HasFlag(InlineStyle.Link) && run.Url is { } url && IsSafeLink(url))
        {
            var link = new Hyperlink(new Run(run.Text)) { ToolTip = url };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            link.Click += (_, _) => ShellIntegration.Open(url);
            inline = link;
        }
        else
        {
            var r = new Run(run.Text);
            if (run.Style.HasFlag(InlineStyle.Code))
            {
                r.FontFamily = (FontFamily)FindResource("MonoFont");
                r.FontSize = FontSize - 1;
                r.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            }
            inline = r;
        }
        if (run.Style.HasFlag(InlineStyle.Bold)) inline.FontWeight = FontWeights.SemiBold;
        if (run.Style.HasFlag(InlineStyle.Italic)) inline.FontStyle = FontStyles.Italic;
        if (run.Style.HasFlag(InlineStyle.Strike)) inline.TextDecorations = TextDecorations.Strikethrough;
        return inline;
    }

    /// <summary>Only web and mail links open from the transcript; a model-written link must not
    /// launch a local program.</summary>
    private static bool IsSafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
}

/// <summary>A fenced code block: language tag, copy button, and a horizontally scrolling body.</summary>
public sealed class CodeBlockView : Border
{
    public CodeBlockView(string? language, string text)
    {
        CornerRadius = new CornerRadius(6);
        SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        BorderThickness = new Thickness(1);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(10, 4, 4, 0) };
        var copy = new Button
        {
            Content = Icons.Copy,
            ToolTip = "Copy",
            FontSize = 11,
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4),
        };
        copy.SetResourceReference(StyleProperty, "IconButton");
        copy.Click += async (_, _) =>
        {
            try { Clipboard.SetText(text); } catch (Exception) { return; }
            copy.Content = Icons.CheckMark;
            await Task.Delay(1200);
            copy.Content = Icons.Copy;
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var label = new TextBlock { Text = language ?? "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        header.Children.Add(label);
        grid.Children.Add(header);

        var body = new TextBox
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(10, 0, 10, 8),
            FontSize = 12.5,
        };
        body.SetResourceReference(StyleProperty, "SelectableText");
        body.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont");
        body.TextWrapping = TextWrapping.NoWrap;
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        Child = grid;
    }
}
