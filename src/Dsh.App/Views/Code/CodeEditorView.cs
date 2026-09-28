using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Model.Code;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;

namespace Dsh.App.Views.Code;

/// <summary>The code editor: AvalonEdit with the app's colours, line numbers, soft tabs, find/replace
/// (Ctrl+F / Ctrl+H), and a remembered caret and scroll position for every open file.</summary>
public sealed class CodeEditorView : UserControl
{
    private readonly AppConfig _config;
    private readonly TextEditor _editor = new();
    private CodeDocument? _document;
    private SyntaxColorizer? _colorizer;
    private readonly Dictionary<CodeDocument, (int Caret, double X, double Y)> _positions = new();
    private readonly Border _replaceBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _find = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3) };
    private readonly TextBox _replace = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(6, 0, 0, 0) };
    private readonly CheckBox _matchCase = new() { Content = "Aa", ToolTip = "Match case", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _replaceStatus = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };

    /// <summary>Line and column moved (for the status bar).</summary>
    public event Action? CaretMoved;

    public CodeEditorView(AppConfig config)
    {
        _config = config;
        _editor.FontFamily = (FontFamily)FindResource("MonoFont");
        _editor.Padding = new Thickness(4, 4, 0, 0);
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.EnableHyperlinks = true;
        _editor.Options.RequireControlModifierForHyperlinkClick = true;
        _editor.Options.EnableRectangularSelection = true;
        _editor.Options.EnableTextDragDrop = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.AllowScrollBelowDocument = true;
        _editor.Options.CutCopyWholeLine = true;
        _editor.ShowLineNumbers = true;
        TextOptions.SetTextFormattingMode(_editor, TextFormattingMode.Display);
        SearchPanel.Install(_editor);
        _editor.TextArea.Caret.PositionChanged += (_, _) => CaretMoved?.Invoke();
        _editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            _config.EditorFontSize = Math.Clamp(_config.EditorFontSize + (e.Delta > 0 ? 1 : -1), 8, 32);
            e.Handled = true;
        };
        var root = new DockPanel();
        DockPanel.SetDock(_replaceBar, Dock.Top);
        root.Children.Add(_replaceBar);
        root.Children.Add(_editor);
        Content = root;
        BuildReplaceBar();
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowReplace();
                e.Handled = true;
            }
        };

        ApplyOptions();
        ApplyTheme();
        _config.PropertyChanged += OnConfigChanged;
        ThemeService.Instance.Changed += ApplyTheme;
    }

    public TextEditor Editor => _editor;

    public int Line => _editor.TextArea.Caret.Line;
    public int Column => _editor.TextArea.Caret.Column;

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppConfig.EditorFontSize) or nameof(AppConfig.EditorWraps) or nameof(AppConfig.EditorLineNumbers))
            ApplyOptions();
    }

    private void ApplyOptions()
    {
        _editor.FontSize = _config.EditorFontSize;
        _editor.WordWrap = _config.EditorWraps;
        _editor.ShowLineNumbers = _config.EditorLineNumbers;
        _editor.HorizontalScrollBarVisibility = _config.EditorWraps ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void ApplyTheme()
    {
        var dark = ThemeService.Instance.IsDark;
        _editor.Background = ThemeService.Brush("SolidBackgroundFillColorBaseBrush", dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White);
        _editor.Foreground = ThemeService.Brush("TextFillColorPrimaryBrush", dark ? Colors.White : Colors.Black);
        _editor.LineNumbersForeground = ThemeService.Brush("TextFillColorTertiaryBrush", Colors.Gray);
        var line = dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00);
        _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(line);
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        var accent = (ThemeService.Brush("AccentFillColorDefaultBrush", Color.FromRgb(0, 0x78, 0xD4)) as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.TextView.LinkTextForegroundBrush = ThemeService.Brush("AccentTextFillColorPrimaryBrush", Colors.SteelBlue);
        _colorizer?.UpdatePalette();
        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>Show a document, remembering where the previous one was scrolled to.</summary>
    public CodeDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(value, _document)) return;
            if (_document is not null)
            {
                _positions[_document] = (_editor.CaretOffset, _editor.HorizontalOffset, _editor.VerticalOffset);
                _document.Document.Changed -= OnDocumentChanged;
            }
            if (_colorizer is not null) _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
            _document = value;
            if (value is null)
            {
                _editor.Document = new TextDocument();
                _editor.IsReadOnly = true;
                return;
            }
            _editor.IsReadOnly = false;
            _editor.Document = value.Document;
            _editor.Options.IndentationSize = GuessIndent(value.Document);
            if (!value.Language.IsPlain)
            {
                _colorizer = new SyntaxColorizer(value.Language, _editor.TextArea.TextView);
                _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            }
            value.Document.Changed += OnDocumentChanged;
            if (_positions.TryGetValue(value, out var saved))
            {
                _editor.CaretOffset = Math.Min(saved.Caret, value.Document.TextLength);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    _editor.ScrollToHorizontalOffset(saved.X);
                    _editor.ScrollToVerticalOffset(saved.Y);
                });
            }
            else
            {
                _editor.CaretOffset = 0;
                _editor.ScrollToHome();
            }
            CaretMoved?.Invoke();
        }
    }

    public void Forget(CodeDocument document) => _positions.Remove(document);

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_document is not null) _colorizer?.OnDocumentChanged(_document.Document, e);
    }

    public void FocusEditor()
    {
        _editor.Focus();
        _editor.TextArea.Focus();
    }

    public void GoTo(int line, int column = 1)
    {
        if (_document is null) return;
        line = Math.Clamp(line, 1, _document.Document.LineCount);
        _editor.TextArea.Caret.Location = new TextLocation(line, Math.Max(1, column));
        _editor.ScrollTo(line, column);
        FocusEditor();
    }

    // MARK: - Replace (Ctrl+H); Ctrl+F is AvalonEdit's own search panel

    private void BuildReplaceBar()
    {
        _replaceBar.SetResourceReference(Border.BackgroundProperty, "LayerFillColorAltBrush");
        _replaceBar.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        _replaceStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Placeholder.SetText(_find, "Find");
        Placeholder.SetText(_replace, "Replace with");
        var one = new Button { Content = "Replace", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        one.Click += (_, _) => ReplaceNext();
        var all = new Button { Content = "Replace All", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        all.Click += (_, _) => ReplaceAll();
        var close = new Button { Content = Icons.Close, FontSize = 9, ToolTip = "Close (Esc)", Margin = new Thickness(6, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "IconButton");
        close.Click += (_, _) => HideReplace();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var element in new UIElement[] { _find, _replace, _matchCase, one, all, _replaceStatus }) row.Children.Add(element);
        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(close);
        dock.Children.Add(row);
        _replaceBar.Child = dock;
        _replaceBar.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideReplace();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ReplaceAll();
                else ReplaceNext();
                e.Handled = true;
            }
        };
    }

    public void ShowReplace()
    {
        if (_document is null) return;
        var selected = _editor.SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n')) _find.Text = selected;
        _replaceBar.Visibility = Visibility.Visible;
        _replaceStatus.Text = "";
        _find.Focus();
        _find.SelectAll();
    }

    private void HideReplace()
    {
        _replaceBar.Visibility = Visibility.Collapsed;
        FocusEditor();
    }

    private StringComparison Comparison => _matchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Replace the current match (if the selection is one) and move to the next.</summary>
    private void ReplaceNext()
    {
        if (_document is null || _find.Text.Length == 0) return;
        var document = _document.Document;
        if (string.Equals(_editor.SelectedText, _find.Text, Comparison))
        {
            var start = _editor.SelectionStart;
            document.Replace(start, _editor.SelectionLength, _replace.Text);
            _editor.Select(start + _replace.Text.Length, 0);
        }
        var text = document.Text;
        var from = Math.Min(_editor.SelectionStart + _editor.SelectionLength, text.Length);
        var next = text.IndexOf(_find.Text, from, Comparison);
        if (next < 0) next = text.IndexOf(_find.Text, 0, Comparison);
        if (next < 0)
        {
            _replaceStatus.Text = "No more matches";
            return;
        }
        _editor.Select(next, _find.Text.Length);
        var location = document.GetLocation(next);
        _editor.ScrollTo(location.Line, location.Column);
        _replaceStatus.Text = "";
    }

    /// <summary>Replace every match as one undo step.</summary>
    private void ReplaceAll()
    {
        if (_document is null || _find.Text.Length == 0) return;
        var document = _document.Document;
        var text = document.Text;
        var matches = new List<int>();
        for (var index = text.IndexOf(_find.Text, 0, Comparison); index >= 0; index = text.IndexOf(_find.Text, index + _find.Text.Length, Comparison))
            matches.Add(index);
        if (matches.Count == 0)
        {
            _replaceStatus.Text = "No matches";
            return;
        }
        document.BeginUpdate();
        try
        {
            for (var i = matches.Count - 1; i >= 0; i--) document.Replace(matches[i], _find.Text.Length, _replace.Text);
        }
        finally
        {
            document.EndUpdate();
        }
        _replaceStatus.Text = $"Replaced {matches.Count}";
    }

    /// <summary>Two-space files stay two-space: the tab width follows the file.</summary>
    private static int GuessIndent(TextDocument document)
    {
        var twos = 0;
        var fours = 0;
        var lines = Math.Min(document.LineCount, 400);
        for (var n = 1; n <= lines; n++)
        {
            var line = document.GetLineByNumber(n);
            var spaces = 0;
            while (spaces < line.Length && document.GetCharAt(line.Offset + spaces) == ' ') spaces++;
            if (spaces == 0 || spaces == line.Length) continue;
            if (spaces % 4 == 0) fours++;
            else if (spaces % 2 == 0) twos++;
        }
        return twos > fours ? 2 : 4;
    }
}
