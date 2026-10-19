using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Chat;

/// <summary>A transcript row's text — a prompt (plain), a reply or notice (markdown) or a tool's output (monospace, in its own
/// box) — that collapses when it is very long (10-19 #14): the first lines and "Show more · 10,174 more lines". Show more lays
/// the rest out inline while that stays cheap; past <see cref="LongText.InlineLines"/> it goes in a box of its own height,
/// virtualized line by line, so one message can never freeze the window or break its scrolling. Its menu and Ctrl+C (with
/// nothing selected) copy the whole message (10-19 #13).</summary>
public sealed class MessageBody : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MessageBody), new PropertyMetadata("", (d, _) => ((MessageBody)d).Update()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(LongTextKind), typeof(MessageBody), new PropertyMetadata(LongTextKind.Prompt, (d, _) => ((MessageBody)d).Rebuild()));

    /// <summary>Plain prompt, markdown reply, or monospace output.</summary>
    public LongTextKind Kind
    {
        get => (LongTextKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Raised (bubbling) when the person shows more or less of a message, so the transcript stops following its end.</summary>
    public static readonly RoutedEvent ToggledEvent = EventManager.RegisterRoutedEvent(
        "Toggled", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MessageBody));

    /// <summary>Height of the box an expanded very long message is read in.</summary>
    public const double BoxHeight = 560;

    /// <summary>Which messages are open, by their row (not the recycled control showing them).</summary>
    private static readonly ConditionalWeakTable<object, StrongBox<bool>> Opened = new();

    private readonly StackPanel _root = new();
    private readonly Button _toggle;
    private readonly TextBlock _toggleGlyph;
    private readonly TextBlock _toggleText;
    private FrameworkElement? _shown;
    private TextBox? _plain;
    private MarkdownPresenter? _rich;
    private LinesBox? _lines;
    private string[]? _linesFor;
    private string? _linesText;
    private LongTextCut? _cut;
    private string? _cutFor;
    private bool _localOpen;

    public MessageBody()
    {
        Focusable = false;
        IsTabStop = false;
        Content = _root;
        _toggleGlyph = new TextBlock { FontSize = 10, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _toggleGlyph.SetResourceReference(StyleProperty, "Glyph");
        _toggleGlyph.FontSize = 10;
        _toggleText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _toggle = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _toggleGlyph, _toggleText } },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(-6, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _toggle.SetResourceReference(StyleProperty, "SubtleButton");
        _toggle.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        _toggle.Click += (_, _) => Toggle();
        DataContextChanged += (_, _) => Update();
    }

    /// <summary>The message is very long and shows only its first lines.</summary>
    public bool IsCollapsed => _cut is not null && !IsOpen;

    /// <summary>The message is very long (whether it is open or not).</summary>
    public bool IsLong => _cut is not null;

    /// <summary>Open (Show more) or collapsed, remembered per row across recycling.</summary>
    public bool IsOpen
    {
        get => DataContext is { } row ? Opened.TryGetValue(row, out var box) && box.Value : _localOpen;
        set
        {
            if (DataContext is { } row) Opened.AddOrUpdate(row, new StrongBox<bool>(value));
            else _localOpen = value;
        }
    }

    /// <summary>The expanded text is in the virtualized box (it is past the inline bar).</summary>
    public bool InBox => ReferenceEquals(_shown, _lines) && _lines is not null;

    /// <summary>The element showing the text now (for the self-test's measurements).</summary>
    internal FrameworkElement? Shown => _shown;

    /// <summary>"Show more · 10,174 more lines" / "Show less"; empty for a message short enough to show whole.</summary>
    internal string ToggleLabel => _toggle.Visibility == Visibility.Visible ? _toggleText.Text : "";

    /// <summary>Show more / Show less.</summary>
    public void Toggle()
    {
        if (_cut is null) return;
        IsOpen = !IsOpen;
        RaiseEvent(new RoutedEventArgs(ToggledEvent, this));
        Update();
        if (!IsOpen)
        {
            // Back to the first lines: keep the message's top in sight rather than leaving the reader far below it.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => BringIntoView(new Rect(0, 0, ActualWidth, Math.Min(ActualHeight, 120))));
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // A recycled row (a notice's 13 px, then a reply's 14 px) restyles the document the presenter built.
        if (_rich is not null && (e.Property == FontSizeProperty || e.Property == FontFamilyProperty))
        {
            _rich.Document.FontSize = FontSize;
            _rich.Document.FontFamily = FontFamily;
        }
    }

    /// <summary>The text controls take this control's font, whatever their theme style says.</summary>
    private T Font<T>(T element) where T : Control
    {
        element.SetBinding(FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        element.SetBinding(FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        return element;
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        _plain = null;
        _rich = null;
        _lines = null;
        _shown = null;
        _cutFor = null;
        Update();
    }

    private void Update()
    {
        var text = Text ?? "";
        if (!ReferenceEquals(_cutFor, text))
        {
            _cutFor = text;
            _cut = LongText.Collapse(text, Kind);
        }
        if (_cut is not { } cut)
        {
            ShowText(text);
            _toggle.Visibility = Visibility.Collapsed;
            return;
        }
        if (!IsOpen) ShowText(cut.Preview);
        else if (LongText.FitsInline(text, cut.Lines)) ShowText(text);
        else ShowLines(text);
        _toggleGlyph.Text = IsOpen ? Icons.ChevronUp : Icons.ChevronDown;
        _toggleText.Text = IsOpen ? LongText.LessLabel : LongText.MoreLabel(cut);
        _toggle.ToolTip = IsOpen ? null : $"{cut.Lines:N0} lines in all — Copy takes the whole message";
        _toggle.Visibility = Visibility.Visible;
    }

    private void ShowText(string shown)
    {
        if (Kind == LongTextKind.Reply)
        {
            _rich ??= Menu(Font(new MarkdownPresenter()));
            _rich.Markdown = shown;
            Present(_rich);
        }
        else
        {
            _plain ??= Menu(Font(Kind == LongTextKind.Output ? OutputBox() : PromptBox()));
            if (!string.Equals(_plain.Text, shown, StringComparison.Ordinal)) _plain.Text = shown;
            Present(_plain);
        }
    }

    private void ShowLines(string text)
    {
        _lines ??= Menu(Font(new LinesBox(wrap: Kind != LongTextKind.Output) { MaxHeight = Kind == LongTextKind.Output ? 280 : BoxHeight }));
        if (!ReferenceEquals(_linesText, text))
        {
            _linesText = text;
            _linesFor = LongText.Lines(text);
            _lines.ItemsSource = _linesFor.Select((line, i) => new LinesBox.Line(i + 1, line)).ToList();
        }
        Present(_lines);
    }

    private void Present(FrameworkElement element)
    {
        if (ReferenceEquals(_shown, element)) return;
        _root.Children.Clear();
        _root.Children.Add(element);
        _root.Children.Add(_toggle);
        _shown = element;
    }

    private static TextBox PromptBox()
    {
        var box = new TextBox();
        box.SetResourceReference(StyleProperty, "SelectableText");
        return box;
    }

    private static TextBox OutputBox()
    {
        var box = new TextBox
        {
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        box.SetResourceReference(StyleProperty, "SelectableText");
        WheelBubbling.SetIsEnabled(box, false);
        WheelChaining.SetIsEnabled(box, true);
        return box;
    }

    // MARK: - Copy

    /// <summary>"Copy prompt" / "Copy reply" / "Copy output" — the whole message, whatever is selected or shown.</summary>
    private string WholeLabel => DataContext switch
    {
        MessageEntryVM { IsUser: true } => "Copy prompt",
        MessageEntryVM { IsAssistant: true } => "Copy reply",
        ToolEntryVM => "Copy output",
        _ => "Copy message",
    };

    private T Menu<T>(T element) where T : Control
    {
        element.ContextMenuOpening += (_, _) => element.ContextMenu = BuildMenu(element);
        element.ContextMenu = new ContextMenu();
        return element;
    }

    private ContextMenu BuildMenu(Control target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = target, InputGestureText = "Ctrl+C" });
        menu.Items.Add(new MenuItem
        {
            Header = WholeLabel,
            Command = TranscriptCommands.CopyMessage,
            CommandParameter = DataContext,
            CommandTarget = this,
            InputGestureText = "Ctrl+C, nothing selected",
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Select All", Command = ApplicationCommands.SelectAll, CommandTarget = target, InputGestureText = "Ctrl+A" });
        if (_cut is { } cut)
        {
            var toggle = new MenuItem { Header = IsOpen ? LongText.LessLabel : LongText.MoreLabel(cut) };
            toggle.Click += (_, _) => Toggle();
            menu.Items.Add(toggle);
        }
        return menu;
    }

    /// <summary>Ctrl+C with nothing selected — or, in a collapsed message, with all of what it shows selected — copies the
    /// whole message, not just its first lines.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && CopyKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>Copies the whole message when <paramref name="key"/> with <paramref name="modifiers"/> is Ctrl+C and nothing
    /// (or, collapsed, all it shows) is selected; true when it did.</summary>
    internal bool CopyKey(Key key, ModifierKeys modifiers)
    {
        if (key != Key.C || modifiers != ModifierKeys.Control || !WantsWholeCopy()) return false;
        TranscriptCommands.CopyMessage.Execute(DataContext, this);
        return true;
    }

    private bool WantsWholeCopy()
    {
        var collapsed = IsCollapsed;
        return _shown switch
        {
            TextBox box => box.SelectionLength == 0 || (collapsed && box.SelectionLength >= box.Text.Length),
            RichTextBox rich => rich.Selection.IsEmpty
                                || (collapsed && rich.Selection.Text.Trim().Length >= new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim().Length),
            LinesBox lines => lines.SelectedItems.Count == 0 || lines.SelectedItems.Count == lines.Items.Count,
            _ => true,
        };
    }
}

/// <summary>A very long text, one row per line, virtualized: only the lines in view exist, so its cost is a screenful whatever
/// its length. Lines can be selected (click, Shift/Ctrl+click, Ctrl+A) and copied (Ctrl+C).</summary>
internal sealed class LinesBox : ListBox
{
    public sealed record Line(int Number, string Text);

    public LinesBox(bool wrap)
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        SelectionMode = SelectionMode.Extended;
        FocusVisualStyle = null;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        WheelChaining.SetIsEnabled(this, true);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Line.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        ItemTemplate = new DataTemplate { VisualTree = text };

        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        chrome.SetValue(Border.PaddingProperty, new Thickness(0, 0, 4, 0));
        chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SubtleFillColorTertiaryBrush"), "Chrome"));
        template.Triggers.Add(selected);
        var item = new Style(typeof(ListBoxItem));
        item.Setters.Add(new Setter(TemplateProperty, template));
        item.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        item.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        item.Setters.Add(new Setter(MinHeightProperty, 0.0));
        item.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        item.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        ItemContainerStyle = item;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            var lines = SelectedItems.OfType<Line>().OrderBy(l => l.Number).Select(l => l.Text);
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch (Exception) { }
            e.Handled = true;
        }, (_, e) => e.CanExecute = SelectedItems.Count > 0));
    }
}
