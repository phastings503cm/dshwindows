using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model.Code;
using Dsh.Core;

namespace Dsh.App.Views.Code;

/// <summary>Draws a terminal session's screen and history and turns key presses into the bytes a
/// terminal sends. Output is parsed off the UI thread; this control repaints at most once per frame.</summary>
public sealed class TerminalControl : FrameworkElement
{
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(TerminalControl),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TerminalControl)d).OnFontChanged()));

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private const double Pad = 6;

    private TerminalSession? _session;
    private Typeface _regular = null!, _bold = null!, _italic = null!, _boldItalic = null!;
    private double _cellWidth = 8, _cellHeight = 16;
    private int _viewTop;
    private bool _pinned = true;
    private (int Line, int Col)? _anchor;
    private (int Line, int Col)? _head;
    private bool _selecting;
    private int _redrawQueued;
    private readonly DispatcherTimer _resizeTimer;
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private (int Cols, int Rows) _lastSize;

    /// <summary>The command line new sessions start with.</summary>
    public string CommandLine { get; set; } = "powershell.exe -NoLogo";

    /// <summary>History length, viewport, or position changed (for an external scroll bar).</summary>
    public event Action? ScrollInfoChanged;

    public TerminalControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);
        InputMethod.SetIsInputMethodEnabled(this, true);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        OnFontChanged();
        _resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) =>
        {
            _resizeTimer!.Stop();
            ApplySize();
        }, Dispatcher);
        _resizeTimer.Stop();
        Loaded += (_, _) =>
        {
            ThemeService.Instance.Changed += OnThemeChanged;
            ApplySize();
        };
        Unloaded += (_, _) => ThemeService.Instance.Changed -= OnThemeChanged;
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    private void OnThemeChanged()
    {
        _brushes.Clear();
        InvalidateVisual();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(value, _session)) return;
            if (_session is not null) _session.Updated -= OnUpdated;
            _session = value;
            _anchor = _head = null;
            _pinned = true;
            _lastSize = default;
            if (_session is not null) _session.Updated += OnUpdated;
            if (IsLoaded) ApplySize();
            InvalidateVisual();
            ScrollInfoChanged?.Invoke();
        }
    }

    private void OnFontChanged()
    {
        var family = TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Cascadia Mono, Consolas");
        _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
        var sample = Format("MMMMMMMMMM", _regular, Brushes.Black);
        _cellWidth = Math.Max(1, sample.WidthIncludingTrailingWhitespace / 10.0);
        _cellHeight = Math.Ceiling(sample.Height) + 1;
        if (IsLoaded) ScheduleResize();
    }

    private FormattedText Format(string text, Typeface typeface, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>New output: repaint once the UI thread is free, never more than once a frame.</summary>
    private void OnUpdated()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _redrawQueued, 0);
            InvalidateVisual();
            ScrollInfoChanged?.Invoke();
        });
    }

    // MARK: - Geometry

    public int VisibleRows => Math.Max(1, (int)((ActualHeight - Pad) / _cellHeight));
    public int VisibleCols => Math.Max(20, (int)((ActualWidth - Pad * 2) / _cellWidth));

    public int LineCount
    {
        get
        {
            if (_session is null) return 0;
            lock (_session.Emulator) return _session.Emulator.LineCount;
        }
    }

    /// <summary>First visible line (into history + screen).</summary>
    public int ViewTop
    {
        get
        {
            var max = Math.Max(0, LineCount - VisibleRows);
            return _pinned ? max : Math.Clamp(_viewTop, 0, max);
        }
    }

    public void ScrollTo(int top)
    {
        var max = Math.Max(0, LineCount - VisibleRows);
        _viewTop = Math.Clamp(top, 0, max);
        _pinned = _viewTop >= max;
        InvalidateVisual();
        ScrollInfoChanged?.Invoke();
    }

    public void ScrollBy(int lines) => ScrollTo(ViewTop + lines);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ScheduleResize();
    }

    private void ScheduleResize()
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>Match the shell's grid to the view (starting it the first time there is room).</summary>
    private void ApplySize()
    {
        if (_session is null || ActualWidth < 40 || ActualHeight < 20) return;
        var size = (VisibleCols, VisibleRows);
        if (size == _lastSize) return;
        _lastSize = size;
        _session.Start(size.Item1, size.Item2, CommandLine);
        _session.Resize(size.Item1, size.Item2);
        InvalidateVisual();
        ScrollInfoChanged?.Invoke();
    }

    // MARK: - Drawing

    private SolidColorBrush Brush(Color color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[color] = brush;
        return brush;
    }

    private Color ThemeColor(string key, Color fallback) =>
        (TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var dark = ThemeService.Instance.IsDark;
        var background = ThemeColor("SolidBackgroundFillColorBaseBrush", dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White);
        var foreground = ThemeColor("TextFillColorPrimaryBrush", dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        foreground.A = 255;
        var selectionBack = ThemeColor("AccentFillColorSelectedTextBackgroundBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        var selectionFore = Colors.White;
        dc.DrawRectangle(Brush(background), null, new Rect(RenderSize));
        if (_session is not { } session) return;

        lock (session.Emulator)
        {
            var emulator = session.Emulator;
            var rows = VisibleRows;
            var count = emulator.LineCount;
            var top = _pinned ? Math.Max(0, count - rows) : Math.Clamp(_viewTop, 0, Math.Max(0, count - rows));
            var selection = NormalizedSelection();

            for (var r = 0; r < rows; r++)
            {
                var index = top + r;
                if (index >= count) break;
                DrawLine(dc, emulator.Line(index), index, Pad / 2 + r * _cellHeight, foreground, background, dark, selection,
                    selectionBack, selectionFore);
            }

            // Cursor: a block when focused, an outline otherwise.
            if (session.IsRunning && emulator.CursorVisible)
            {
                var line = emulator.ScrollbackCount + emulator.CursorRow;
                if (line >= top && line < top + rows)
                {
                    var rect = new Rect(Pad + emulator.CursorCol * _cellWidth, Pad / 2 + (line - top) * _cellHeight, _cellWidth, _cellHeight);
                    var cursorColor = foreground;
                    if (IsKeyboardFocused)
                    {
                        cursorColor.A = 170;
                        dc.DrawRectangle(Brush(cursorColor), null, rect);
                    }
                    else
                    {
                        cursorColor.A = 140;
                        var pen = new Pen(Brush(cursorColor), 1);
                        pen.Freeze();
                        rect.Inflate(-0.5, -0.5);
                        dc.DrawRectangle(null, pen, rect);
                    }
                }
            }
        }
    }

    private void DrawLine(DrawingContext dc, TerminalCell[] line, int index, double y, Color foreground, Color background, bool dark,
                          ((int Line, int Col) Start, (int Line, int Col) End)? selection, Color selectionBack, Color selectionFore)
    {
        var col = 0;
        while (col < line.Length)
        {
            var cell = line[col];
            if (cell.IsContinuation)
            {
                col++;
                continue;
            }
            var selected = IsSelected(index, col, selection);
            var end = col + 1;
            var ascii = cell.CodePoint < 0x80;
            if (ascii)
            {
                while (end < line.Length && !line[end].IsContinuation && line[end].CodePoint < 0x80
                       && line[end].Style == cell.Style && IsSelected(index, end, selection) == selected)
                    end++;
            }
            var cells = ascii ? end - col : (end < line.Length && line[end].IsContinuation ? 2 : 1);

            var style = cell.Style;
            var fore = TerminalPalette.Resolve(style.Foreground, foreground, dark);
            var back = TerminalPalette.Resolve(style.Background, background, dark);
            if (style.Inverse) (fore, back) = (back, fore);
            if (style.Dim) fore.A = 150;
            if (selected)
            {
                back = selectionBack;
                fore = selectionFore;
            }

            var x = Pad + col * _cellWidth;
            var width = cells * _cellWidth;
            if (back != background) dc.DrawRectangle(Brush(back), null, new Rect(x, y, width, _cellHeight));

            var text = ascii ? Text(line, col, end) : cell.Text;
            var decorated = style.Underline || style.Strikethrough;
            if (!string.IsNullOrWhiteSpace(text) || decorated)
            {
                var typeface = style.Bold ? (style.Italic ? _boldItalic : _bold) : (style.Italic ? _italic : _regular);
                var brush = Brush(fore);
                if (!string.IsNullOrWhiteSpace(text)) dc.DrawText(Format(text, typeface, brush), new Point(x, y));
                if (decorated)
                {
                    var pen = new Pen(brush, 1);
                    pen.Freeze();
                    if (style.Underline) dc.DrawLine(pen, new Point(x, y + _cellHeight - 2.5), new Point(x + width, y + _cellHeight - 2.5));
                    if (style.Strikethrough) dc.DrawLine(pen, new Point(x, y + _cellHeight / 2), new Point(x + width, y + _cellHeight / 2));
                }
            }
            col = ascii ? end : col + cells;
        }
    }

    private static string Text(TerminalCell[] line, int from, int to)
    {
        var chars = new char[to - from];
        for (var i = from; i < to; i++) chars[i - from] = line[i].CodePoint is 0 ? ' ' : (char)line[i].CodePoint;
        return new string(chars);
    }

    // MARK: - Selection

    private (int Line, int Col) PositionAt(Point point)
    {
        var line = ViewTop + (int)Math.Floor((point.Y - Pad / 2) / _cellHeight);
        var col = (int)Math.Round((point.X - Pad) / _cellWidth);
        return (Math.Clamp(line, 0, Math.Max(0, LineCount - 1)), Math.Clamp(col, 0, VisibleCols));
    }

    private ((int Line, int Col) Start, (int Line, int Col) End)? NormalizedSelection()
    {
        if (_anchor is not { } anchor || _head is not { } head || anchor == head) return null;
        return anchor.Line < head.Line || (anchor.Line == head.Line && anchor.Col <= head.Col) ? (anchor, head) : (head, anchor);
    }

    private static bool IsSelected(int line, int col, ((int Line, int Col) Start, (int Line, int Col) End)? selection)
    {
        if (selection is not { } s) return false;
        if (line < s.Start.Line || line > s.End.Line) return false;
        if (line == s.Start.Line && col < s.Start.Col) return false;
        if (line == s.End.Line && col >= s.End.Col) return false;
        return true;
    }

    public bool HasSelection => NormalizedSelection() is not null;

    public string? SelectedText
    {
        get
        {
            if (_session is null || NormalizedSelection() is not { } s) return null;
            lock (_session.Emulator) return _session.Emulator.TextBetween(s.Start, s.End);
        }
    }

    public void SelectAll()
    {
        if (_session is null) return;
        lock (_session.Emulator)
        {
            var count = _session.Emulator.LineCount;
            _anchor = (0, 0);
            _head = (Math.Max(0, count - 1), _session.Emulator.Cols);
        }
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _anchor = _head = null;
        InvalidateVisual();
    }

    public void Copy()
    {
        if (SelectedText is not { Length: > 0 } text) return;
        try { Clipboard.SetText(text); } catch (Exception) { }
    }

    public void Paste()
    {
        if (_session is null) return;
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (Exception)
        {
            return;
        }
        if (text.Length == 0) return;
        _pinned = true;
        _session.Send(TerminalInput.Paste(text, _session.BracketedPaste));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var position = PositionAt(e.GetPosition(this));
        if (e.ClickCount == 2 && _session is not null)
        {
            (int Start, int End)? word;
            lock (_session.Emulator) word = _session.Emulator.WordAt(position.Line, position.Col);
            if (word is { } w)
            {
                _anchor = (position.Line, w.Start);
                _head = (position.Line, w.End);
            }
        }
        else if (e.ClickCount >= 3)
        {
            _anchor = (position.Line, 0);
            _head = (position.Line + 1, 0);
        }
        else
        {
            _anchor = _head = position;
            _selecting = true;
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || !IsMouseCaptured) return;
        var point = e.GetPosition(this);
        if (point.Y < 0) ScrollBy(-1);
        else if (point.Y > ActualHeight) ScrollBy(1);
        _head = PositionAt(point);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        if (NormalizedSelection() is null) _anchor = _head = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        var menu = new ContextMenu();
        menu.Items.Add(Views.Menus.Item("Copy", Copy, Icons.Copy, enabled: HasSelection));
        menu.Items.Add(Views.Menus.Item("Paste", Paste));
        menu.Items.Add(Views.Menus.Item("Select All", SelectAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Views.Menus.Item("Clear", () => _session?.Clear(), Icons.Erase));
        menu.Items.Add(Views.Menus.Item("Copy All Output", () =>
        {
            if (_session?.Transcript is { Length: > 0 } all)
                try { Clipboard.SetText(all); } catch (Exception) { }
        }));
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            FontSize = Math.Clamp(FontSize + (e.Delta > 0 ? 1 : -1), 8, 32);
        }
        else
        {
            ScrollBy(-e.Delta / 40);
        }
        e.Handled = true;
    }

    // MARK: - Keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_session is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers & ~ModifierKeys.Windows;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var shift = mods.HasFlag(ModifierKeys.Shift);

        // Clipboard, the Windows Terminal way.
        if ((ctrl && shift && key == Key.C) || (ctrl && !shift && key == Key.Insert))
        {
            Copy();
            e.Handled = true;
            return;
        }
        if ((ctrl && shift && key == Key.V) || (ctrl && !shift && !alt && key == Key.V) || (shift && !ctrl && key == Key.Insert))
        {
            Paste();
            e.Handled = true;
            return;
        }
        if (ctrl && !shift && !alt && key == Key.C && HasSelection)
        {
            Copy();
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && key == Key.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (shift && !ctrl && key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(key == Key.PageUp ? -VisibleRows + 1 : VisibleRows - 1);
            e.Handled = true;
            return;
        }

        byte[]? bytes = null;
        if (MapKey(key) is { } special)
        {
            var modifiers = (shift ? TerminalModifiers.Shift : 0) | (alt ? TerminalModifiers.Alt : 0) | (ctrl ? TerminalModifiers.Control : 0);
            bytes = TerminalInput.Encode(special, modifiers, _session.ApplicationCursorKeys);
        }
        else if (ctrl && !alt && ControlCharacter(key, shift) is { } c && TerminalInput.ControlCode(c) is { } code)
        {
            bytes = [code];
        }
        else if (alt && !ctrl && LetterOrDigit(key, shift) is { } typed)
        {
            bytes = TerminalInput.Text(typed.ToString(), alt: true);
        }

        if (bytes is null) return;
        Send(bytes);
        e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        // Control characters were already sent from the key handler.
        var text = new string(e.Text.Where(c => c >= 0x20 && c != 0x7F).ToArray());
        if (text.Length == 0) return;
        Send(TerminalInput.Text(text));
        e.Handled = true;
    }

    private void Send(byte[] bytes)
    {
        if (_session is null) return;
        if (HasSelection) ClearSelection();
        if (!_pinned)
        {
            _pinned = true;
            ScrollInfoChanged?.Invoke();
        }
        _session.Send(bytes);
    }

    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.Back => TerminalKey.Backspace,
        Key.Enter => TerminalKey.Enter,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    /// <summary>The character a Ctrl chord stands for (^C, ^[, ^_ …). Digits are left alone so
    /// Ctrl+1 / Ctrl+2 keep switching modes.</summary>
    private static char? ControlCharacter(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.Oem5 => '\\',
        Key.OemCloseBrackets => ']',
        Key.OemMinus when shift => '_',
        Key.Oem2 => '/',
        _ => null,
    };

    private static char? LetterOrDigit(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 when !shift => (char)('0' + (key - Key.D0)),
        Key.OemPeriod when !shift => '.',
        Key.OemComma when !shift => ',',
        Key.OemMinus when !shift => '-',
        _ => null,
    };
}
