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
        Content = _editor;

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
