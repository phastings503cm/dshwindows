using System.Windows;
using System.Windows.Media;
using Dsh.App.Infrastructure;
using Dsh.Core;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Dsh.App.Views.Code;

/// <summary>Colours comments, strings, numbers, keywords and tags with the app's palette (VS Code's
/// Light+ / Dark+ colours, which Windows developers read without thinking). Lines are lexed as they
/// are drawn; the only cross-line state — "starts inside a block comment" — is cached per line and
/// recomputed from the first edited line, so a 20 000-line file costs the same as a short one.</summary>
public sealed class SyntaxColorizer : DocumentColorizingTransformer
{
    private readonly Language _language;
    private readonly TextView _view;
    private readonly List<bool> _startsInComment = [false];
    private int _validLines = 1;
    private readonly List<SyntaxToken> _tokens = [];
    private Brush _comment = null!, _string = null!, _number = null!, _keyword = null!, _tag = null!;

    public SyntaxColorizer(Language language, TextView view)
    {
        _language = language;
        _view = view;
        UpdatePalette();
    }

    public void UpdatePalette()
    {
        var dark = ThemeService.Instance.IsDark;
        _comment = Frozen(dark ? Color.FromRgb(0x6A, 0x99, 0x55) : Color.FromRgb(0x00, 0x80, 0x00));
        _string = Frozen(dark ? Color.FromRgb(0xCE, 0x91, 0x78) : Color.FromRgb(0xA3, 0x15, 0x15));
        _number = Frozen(dark ? Color.FromRgb(0xB5, 0xCE, 0xA8) : Color.FromRgb(0x09, 0x86, 0x58));
        _keyword = Frozen(dark ? Color.FromRgb(0x56, 0x9C, 0xD6) : Color.FromRgb(0x00, 0x00, 0xFF));
        _tag = Frozen(dark ? Color.FromRgb(0x56, 0x9C, 0xD6) : Color.FromRgb(0x80, 0x00, 0x00));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Forget cached state from the line containing <paramref name="offset"/> on. When the
    /// edit could open or close a block comment, every visible line is repainted.</summary>
    public void OnDocumentChanged(TextDocument document, DocumentChangeEventArgs e)
    {
        var line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
        _validLines = Math.Min(_validLines, line);
        if (_language.BlockComment is { } block && (Touches(e.InsertedText, block) || Touches(e.RemovedText, block)))
            _view.Redraw();
    }

    private static bool Touches(ITextSource? text, (string Open, string Close) block)
    {
        if (text is null || text.TextLength == 0) return false;
        var s = text.Text;
        return s.Contains(block.Open[0]) || s.Contains(block.Close[^1]);
    }

    /// <summary>Whether line <paramref name="lineNumber"/> starts inside a block comment.</summary>
    private bool StartsInComment(TextDocument document, int lineNumber)
    {
        if (_language.BlockComment is null) return false;
        while (_startsInComment.Count <= lineNumber) _startsInComment.Add(false);
        for (var n = _validLines; n < lineNumber; n++)
        {
            var line = document.GetLineByNumber(n);
            _tokens.Clear();
            _startsInComment[n + 1] = SyntaxScanner.ScanLine(document.GetText(line), _language, _startsInComment[n], _tokens);
        }
        _validLines = Math.Max(_validLines, lineNumber);
        return _startsInComment[lineNumber];
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_language.IsPlain || line.Length == 0 && _language.BlockComment is null) return;
        var document = CurrentContext.Document;
        // Lines 1-based; index n holds "line n starts inside a comment".
        while (_startsInComment.Count <= line.LineNumber + 1) _startsInComment.Add(false);
        var inside = StartsInComment(document, line.LineNumber);
        _tokens.Clear();
        var next = SyntaxScanner.ScanLine(document.GetText(line), _language, inside, _tokens);
        if (_validLines == line.LineNumber)
        {
            _startsInComment[line.LineNumber + 1] = next;
            _validLines = line.LineNumber + 1;
        }
        foreach (var token in _tokens)
        {
            var brush = token.Kind switch
            {
                SyntaxKind.Comment => _comment,
                SyntaxKind.String => _string,
                SyntaxKind.Number => _number,
                SyntaxKind.Tag => _tag,
                _ => _keyword,
            };
            var italic = token.Kind == SyntaxKind.Comment;
            ChangeLinePart(line.Offset + token.Start, line.Offset + token.Start + token.Length, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                {
                    var face = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(face.FontFamily, FontStyles.Italic, face.Weight, face.Stretch));
                }
            });
        }
    }
}
