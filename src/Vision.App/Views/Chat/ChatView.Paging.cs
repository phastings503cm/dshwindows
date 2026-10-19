using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Chat;

// MARK: - The windowed transcript (10-19 #14)
//
// The transcript renders a window of the chat's rows (TranscriptWindow): the newest page when the chat opens, a "Load
// earlier messages" row above it that scrolling to the top also triggers, and — when the window stops short of the newest
// row (after reading far back, or a jump to an old prompt) — "Load newer messages" and "Jump to latest" below it. Pages
// come in with the row being read held where it was on screen. A long session followed at its bottom lets go of its oldest
// rows.

public partial class ChatView
{
    private TranscriptWindow<ChatEntryVM>? _window;
    private readonly TranscriptPagerRow _earlierRow = new(top: true);
    private readonly TranscriptPagerRow _newerRow = new(top: false);
    /// <summary>A chat attached before its rows were replayed builds its window once they are in, not row by row.</summary>
    private bool _windowPending;
    private bool _trimQueued;
    /// <summary>Set while a page is coming in, so the scroll it causes doesn't trigger another.</summary>
    private bool _paging;
    /// <summary>Scrolling to the top loads a page only once per arrival there: the reader scrolls away and back for the next.</summary>
    private bool _edgeArmed = true;

    /// <summary>How near the top (or bottom) of the transcript, in pixels, loads the next page.</summary>
    private const double EdgeDistance = 48;

    private void InitPaging()
    {
        Transcript.PreviewMouseWheel += OnTranscriptWheel;
        // Show more / Show less on a long message: the reader is reading it, so the transcript stops following its end.
        Transcript.AddHandler(MessageBody.ToggledEvent, new RoutedEventHandler((_, _) => _pinned = false));
        InitCopy();
        InitFind();
    }

    /// <summary>What the transcript shows for <paramref name="session"/>: the pager rows around its window, then the footers.</summary>
    private CompositeCollection TranscriptSource(SessionVM session)
    {
        DetachWindow();
        _rows = new CollectionContainer();
        // Opened before its rows were read (a chat from an earlier launch): the rows arrive right after this, one by one —
        // let them land, then show the newest page, rather than carrying every one of them through the view.
        _windowPending = session.Entries.Count == 0;
        if (!_windowPending) BuildWindow(session);
        UpdatePagerRows();
        return [_earlierRow, _rows, _newerRow, _phase, _thinking, _files];
    }

    private CollectionContainer _rows = new();

    private void BuildWindow(SessionVM session)
    {
        DetachWindow();
        _window = new TranscriptWindow<ChatEntryVM>(session.Entries);
        _window.Changed += UpdatePagerRows;
        _rows.Collection = _window.Items;
        UpdatePagerRows();
    }

    private void DetachPaging()
    {
        DetachWindow();
        _windowPending = false;
        _rows.Collection = null;
    }

    private void DetachWindow()
    {
        if (_window is null) return;
        _window.Changed -= UpdatePagerRows;
        _window.Dispose();
        _window = null;
    }

    private void UpdatePagerRows()
    {
        _earlierRow.Update(_window?.Earlier ?? 0);
        _newerRow.Update(_window?.Later ?? 0);
    }

    /// <summary>After the chat's rows changed: build the window of a chat opened before its rows were read, once they are in;
    /// let go of the oldest rows of one followed at its bottom for a long while.</summary>
    private void PageOnEntriesChanged()
    {
        if (_trimQueued) return;
        if (!_windowPending && (_window is null || _window.Items.Count <= _window.MaxRows + _window.PageSize)) return;
        _trimQueued = true;
        // Before the next layout pass (Render priority), so a chat whose rows were just replayed lays out one page.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _trimQueued = false;
            if (_windowPending)
            {
                _windowPending = false;
                if (_session is { } session) BuildWindow(session);
                return;
            }
            if (_window is not null && _pinned && _window.IsLive) _window.TrimTop(_window.MaxRows);
        });
    }

    // MARK: Pages

    private void PagerRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TranscriptPagerRow row) return;
        if (row.IsTop) LoadEarlier();
        else LoadNewer();
    }

    private void JumpToLatest_Click(object sender, RoutedEventArgs e) => JumpToLatest();

    /// <summary>Bring the page before the window in, keeping the row being read where it is on screen; returns how many came.</summary>
    public int LoadEarlier()
    {
        if (_window is not { Earlier: > 0 } window) return 0;
        return Paged(() => window.LoadEarlier());
    }

    /// <summary>Bring the page after the window in (at the bottom), keeping the row being read where it is.</summary>
    public int LoadNewer()
    {
        if (_window is not { Later: > 0 } window) return 0;
        return Paged(() => window.LoadLater());
    }

    /// <summary>The newest page again, followed at its end.</summary>
    public void JumpToLatest()
    {
        if (_window is null) return;
        if (!_window.IsLive || _window.Items.Count > _window.MaxRows) _window.ShowLatest();
        _pinned = true;
        _scroller?.ScrollToEnd();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scroller?.ScrollToEnd());
    }

    private int Paged(Func<int> page)
    {
        _paging = true;
        try
        {
            _pinned = false;
            var anchor = Anchor();
            var count = page();
            if (anchor is { } held) Hold(held.Row, held.Top);
            return count;
        }
        finally
        {
            _paging = false;
            ArmEdges();
        }
    }

    /// <summary>A page that came in leaves the reader where they were — mid-transcript, so the next arrival at an edge loads the
    /// next page; still at the edge (nothing could be held), it waits for them to scroll away and back.</summary>
    private void ArmEdges() =>
        _edgeArmed = _scroller is { } scroller && scroller.VerticalOffset > EdgeDistance && scroller.VerticalOffset < scroller.ScrollableHeight - EdgeDistance;

    /// <summary>The first row whose bottom is in view, and how far its top is from the top of the transcript's viewport.</summary>
    private (ChatEntryVM Row, double Top)? Anchor()
    {
        if (_scroller is null || TranscriptPanel() is not { } panel) return null;
        (ChatEntryVM Row, double Top)? best = null;
        // In recycling mode the panel's children are not in item order: take the topmost visible one.
        foreach (var child in panel.Children.OfType<ContentPresenter>())
        {
            if (child.Content is not ChatEntryVM row || !child.IsVisible) continue;
            var top = child.TranslatePoint(new Point(0, 0), _scroller).Y;
            if (top + child.ActualHeight <= 0) continue;
            if (best is null || top < best.Value.Top) best = (row, top);
        }
        return best;
    }

    /// <summary>Scroll so <paramref name="row"/>'s top is <paramref name="top"/> pixels below the viewport's top again. The
    /// panel estimates rows it hasn't laid out, so this corrects until the row lands (a few passes at most).</summary>
    private void Hold(ChatEntryVM row, double top)
    {
        if (_scroller is null) return;
        for (var pass = 0; pass < 4; pass++)
        {
            Transcript.UpdateLayout();
            if (Container(row) is not { } container) return;
            var now = container.TranslatePoint(new Point(0, 0), _scroller).Y;
            var drift = now - top;
            if (Math.Abs(drift) < 0.5) return;
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + drift);
        }
        Transcript.UpdateLayout();
    }

    /// <summary>The row's container, laid out first if it is outside what the panel has realized.</summary>
    private FrameworkElement? Container(object row)
    {
        if (Transcript.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement ready) return ready;
        var index = Transcript.Items.IndexOf(row);
        if (index < 0 || TranscriptPanel() is not { } panel) return null;
        panel.BringIndexIntoViewPublic(index);
        Transcript.UpdateLayout();
        return Transcript.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
    }

    private VirtualizingStackPanel? TranscriptPanel() => FindChild<VirtualizingStackPanel>(Transcript);

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>When the reader last turned the wheel, pressed a scrolling key or the scrollbar; and whether they are dragging its thumb.</summary>
    private long _readerScrolledAt = long.MinValue / 2;
    private bool _readerDragging;

    /// <summary>The transcript is moving because the reader moved it (just now), not because rows were added or laid out.</summary>
    private bool ReaderScrolling => _readerDragging || Environment.TickCount64 - _readerScrolledAt < 600;

    /// <summary>The view moved up and now sits away from the end: something scrolled it there (content growing or shrinking
    /// keeps the distance to the end, or moves the view down to it).</summary>
    private bool MovedAwayFromEnd(ScrollChangedEventArgs e) =>
        e.VerticalChange < 0 && _scroller is { } scroller && scroller.ScrollableHeight - scroller.VerticalOffset > 32;

    private void WatchReaderScrolling(ScrollViewer scroller)
    {
        void Now() => _readerScrolledAt = Environment.TickCount64;
        scroller.PreviewMouseWheel += (_, _) => Now();
        scroller.AddHandler(System.Windows.Controls.Primitives.ScrollBar.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null) Now();
            }), handledEventsToo: true);
        scroller.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _readerDragging = true), handledEventsToo: true);
        scroller.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _readerDragging = false;
                Now();
            }), handledEventsToo: true);
        scroller.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down or Key.Space) Now();
        };
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null && element is not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    /// <summary>The reader scrolled: at the top, the page before comes in; at the bottom of a window short of the newest row,
    /// the page after.</summary>
    private void PageOnScroll()
    {
        if (_paging || _scroller is null || _window is null) return;
        var nearTop = _scroller.VerticalOffset <= EdgeDistance;
        var nearBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - EdgeDistance;
        if (!nearTop && !nearBottom)
        {
            _edgeArmed = true;
            return;
        }
        if (!_edgeArmed) return;
        if (nearTop && _window.Earlier > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LoadEarlier());
        else if (nearBottom && _window.Later > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LoadNewer());
    }

    /// <summary>The wheel turned up at the very top (or down at the very bottom) moves no scrollbar, so it raises no scroll
    /// event: it loads the next page itself.</summary>
    private void OnTranscriptWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroller is null || _window is null || _paging) return;
        if (e.Delta > 0 && _scroller.VerticalOffset <= 0.5 && _window.Earlier > 0)
        {
            _edgeArmed = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LoadEarlier());
        }
        else if (e.Delta < 0 && _scroller.VerticalOffset >= _scroller.ScrollableHeight - 0.5 && _window.Later > 0)
        {
            _edgeArmed = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LoadNewer());
        }
    }

    // MARK: Jumping to a row

    /// <summary>Show the row <paramref name="entryId"/> of this chat — moving the window to it when it is outside — with its
    /// top <paramref name="below"/> pixels into the transcript, and flash it. False when the chat no longer has it (compacted
    /// away).</summary>
    public bool ShowEntry(string entryId, double below = 40)
    {
        if (_session is not { } session || _scroller is null) return false;
        if (_window is null)
        {
            // Asked before the replayed rows' window was built: build it now.
            _windowPending = false;
            BuildWindow(session);
        }
        if (_window is null) return false;
        var index = -1;
        for (var i = session.Entries.Count - 1; i >= 0; i--)
        {
            if (session.Entries[i].Id != entryId) continue;
            index = i;
            break;
        }
        if (index < 0) return false;
        var row = session.Entries[index];
        _paging = true;
        try
        {
            _pinned = false;
            _window.ShowAround(index);
            Transcript.UpdateLayout();
            if (Container(row) is not { } container) return false;
            var top = container.TranslatePoint(new Point(0, 0), _scroller).Y;
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + top - below);
            Hold(row, below);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (Transcript.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement shown) FlashRow(shown);
            });
            return true;
        }
        finally
        {
            _paging = false;
            ArmEdges();
        }
    }

    /// <summary>A brief accent wash over a row that was jumped to.</summary>
    private static void FlashRow(FrameworkElement container)
    {
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(container);
        if (layer is null) return;
        var accent = ThemeService.Brush("AccentFillColorDefaultBrush", Colors.SteelBlue) is SolidColorBrush solid ? solid.Color : Colors.SteelBlue;
        var brush = new SolidColorBrush(accent) { Opacity = 0 };
        var wash = new RowWash(container, brush);
        layer.Add(wash);
        // A recycled container shows another row: the wash goes with the row it was for.
        void Recycled(object? sender, DependencyPropertyChangedEventArgs e) => layer.Remove(wash);
        container.DataContextChanged += Recycled;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.3, 0, TimeSpan.FromSeconds(1.8)) { BeginTime = TimeSpan.FromMilliseconds(150) };
        fade.Completed += (_, _) =>
        {
            container.DataContextChanged -= Recycled;
            layer.Remove(wash);
        };
        brush.BeginAnimation(Brush.OpacityProperty, fade);
    }

    private sealed class RowWash(UIElement row, Brush brush) : System.Windows.Documents.Adorner(row)
    {
        protected override void OnRender(DrawingContext drawing) =>
            drawing.DrawRoundedRectangle(brush, null, new Rect(new Point(-8, -4), new Size(AdornedElement.RenderSize.Width + 16, AdornedElement.RenderSize.Height + 8)), 8, 8);
    }

    // MARK: Self-test

    internal TranscriptWindow<ChatEntryVM>? SelfTestPagingWindow => _window;
    internal ScrollViewer? SelfTestPagingScroller
    {
        get
        {
            HookScroller();
            return _scroller;
        }
    }
    internal (ChatEntryVM Row, double Top)? SelfTestPagingAnchor() => Anchor();
    internal FrameworkElement? SelfTestPagingContainer(object row) => Container(row);
    internal bool SelfTestPagingPinned => _pinned;
    internal bool SelfTestPagingReader => ReaderScrolling;
    /// <summary>Scroll as the reader would (the wheel, the scrollbar): what <paramref name="scroll"/> does counts as theirs.</summary>
    internal void SelfTestPagingAsReader(Action scroll)
    {
        _readerScrolledAt = Environment.TickCount64;
        scroll();
    }
}
