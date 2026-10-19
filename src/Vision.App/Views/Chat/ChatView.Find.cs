using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vision.App.Infrastructure;
using Vision.App.Model;

namespace Vision.App.Views.Chat;

// MARK: - Find in this chat (Ctrl+F)
//
// With the transcript windowed (10-19 #14), most of a long chat isn't on screen to be found by eye. Find searches every row of
// the chat — loaded or not — and each match is a jump: the window moves to it, it comes into view near the top and flashes.
// Newest match first; Enter (or F3) goes to the next older one, Shift+Enter (Shift+F3) back to newer ones; Esc closes.

public partial class ChatView
{
    private Border? _findBar;
    private TextBox? _findBox;
    private TextBlock? _findCount;
    private List<ChatEntryVM> _findMatches = [];
    private int _findAt = -1;
    private string _findFor = "";

    private void InitFind()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _session is { Entries.Count: > 0 })
            {
                OpenFind();
                e.Handled = true;
            }
            else if (e.Key == Key.F3 && _findBar is { Visibility: Visibility.Visible })
            {
                FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };
    }

    /// <summary>Show the find bar (made on first use) with its text selected.</summary>
    public void OpenFind()
    {
        if (_findBar is null) BuildFindBar();
        _findBar!.Visibility = Visibility.Visible;
        _findBox!.Focus();
        _findBox.SelectAll();
    }

    private void CloseFind()
    {
        if (_findBar is null) return;
        _findBar.Visibility = Visibility.Collapsed;
        _findMatches = [];
        _findAt = -1;
        _findFor = "";
        FocusComposer();
    }

    private void BuildFindBar()
    {
        _findBox = new TextBox { Width = 220, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Find in this chat — every message, loaded or not" };
        Placeholder.SetText(_findBox, "Find in this chat");
        _findBox.TextChanged += (_, _) => FindRun();
        _findBox.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseFind();
                    e.Handled = true;
                    break;
            }
        };
        _findCount = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
        _findCount.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Button Icon(string glyph, string tip, Action click)
        {
            var button = new Button { Content = glyph, ToolTip = tip, MinWidth = 28, MinHeight = 28, Padding = new Thickness(5) };
            button.SetResourceReference(StyleProperty, "IconButton");
            button.FontSize = 11;
            button.Click += (_, _) => click();
            return button;
        }
        var search = new TextBlock { Text = Icons.Search, FontSize = 12, Margin = new Thickness(2, 0, 8, 0) };
        search.SetResourceReference(StyleProperty, "Glyph");
        search.FontSize = 12;
        search.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _findBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 4, 4),
            Margin = new Thickness(0, 8, 28, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    search, _findBox, _findCount,
                    Icon(Icons.ChevronUp, "Older match (Enter, F3)", () => FindStep(1)),
                    Icon(Icons.ChevronDown, "Newer match (Shift+Enter, Shift+F3)", () => FindStep(-1)),
                    Icon(Icons.Close, "Close (Esc)", CloseFind),
                },
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.18 },
        };
        _findBar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        _findBar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid.SetRow(_findBar, 1);
        Panel.SetZIndex(_findBar, 10);
        ((Panel)Content).Children.Add(_findBar);
    }

    /// <summary>Every row of the chat that holds the text, oldest first.</summary>
    internal static List<ChatEntryVM> FindIn(IEnumerable<ChatEntryVM> rows, string query)
    {
        if (query.Trim().Length == 0) return [];
        return rows.Where(row => row switch
        {
            MessageEntryVM message => message.Text.Contains(query, StringComparison.OrdinalIgnoreCase),
            ToolEntryVM tool => tool.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || (tool.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false),
            CompactionEntryVM compaction => compaction.Summary.Contains(query, StringComparison.OrdinalIgnoreCase),
            _ => false,
        }).ToList();
    }

    private void FindRun()
    {
        if (_session is not { } session || _findBox is null) return;
        _findFor = _findBox.Text;
        _findMatches = FindIn(session.Entries, _findFor);
        _findAt = -1;
        if (_findMatches.Count > 0) FindStep(1);
        else UpdateFindCount();
    }

    /// <summary>Go to the next older (+1) or newer (-1) match, newest first.</summary>
    private void FindStep(int direction)
    {
        if (_session is null) return;
        if (_findBox is not null && _findBox.Text != _findFor)
        {
            FindRun();
            return;
        }
        if (_findMatches.Count == 0)
        {
            UpdateFindCount();
            return;
        }
        // _findAt counts from the newest (0) to the oldest.
        _findAt = _findAt < 0 ? 0 : (_findAt + direction + _findMatches.Count) % _findMatches.Count;
        var row = _findMatches[_findMatches.Count - 1 - _findAt];
        // Below the find bar, which sits over the top of the transcript.
        ShowEntry(row.Id, below: 64);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        if (_findCount is null) return;
        _findCount.Text = _findFor.Trim().Length == 0 ? "" : _findMatches.Count == 0 ? "No matches" : $"{_findAt + 1} of {_findMatches.Count}";
    }

    internal void SelfTestFind(string query)
    {
        OpenFind();
        _findBox!.Text = query;
    }

    internal string SelfTestFindCount => _findCount?.Text ?? "";
    internal void SelfTestCloseFind() => CloseFind();
}
