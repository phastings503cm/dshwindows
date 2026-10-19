using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Vision.Core;

// MARK: - The windowed transcript
//
// Patrick (10-19, #14): "we need to have a 'load more' button if the chat gets too long … an eloquent way to paginate the
// chat as it expands." A chat's view renders a window of its rows — the newest page when it opens — not all of them. A
// "Load earlier messages" row above the window (also reached by scrolling to the top) brings the page before it; a row
// below a window that stops short of the newest ("Load newer messages", "Jump to latest") brings the next. Jumping to an
// old row (the Prompts timeline, search) moves the window to it. The window follows its chat: rows added at its end
// appear while the window reaches the end; a row added or removed above it shifts it; one inside it is shown or taken out.

/// <summary>The numbers the windowed transcript is tuned with.</summary>
public static class TranscriptPaging
{
    /// <summary>Rows a chat shows when it opens, and how many each "Load earlier" / "Load newer" brings.</summary>
    public const int PageSize = 50;
    /// <summary>The most rows the window holds after loading pages: loading past it lets go of the far end (the newest when
    /// loading earlier, the oldest when loading newer), so a long read back through a chat stays as light as its first page.</summary>
    public const int MaxRows = 250;
    /// <summary>Rows kept above a row jumped to, so it is seen in its context.</summary>
    public const int JumpContext = 12;

    /// <summary>"Load earlier messages" row's count: "1,950 earlier".</summary>
    public static string EarlierLabel(int count) => $"{count:N0} earlier";

    /// <summary>"Load newer messages" row's count: "120 newer".</summary>
    public static string NewerLabel(int count) => $"{count:N0} newer";
}

/// <summary>A contiguous slice [<see cref="Start"/>, <see cref="End"/>) of an observable list, kept in step with it: what a
/// view renders instead of the whole list. UI-thread only, like the list it watches.</summary>
public sealed class TranscriptWindow<T> : IDisposable where T : class
{
    private readonly ObservableCollection<T> _source;
    private readonly int _pageSize;
    private readonly int _maxRows;
    private bool _disposed;

    public TranscriptWindow(ObservableCollection<T> source, int pageSize = TranscriptPaging.PageSize, int maxRows = TranscriptPaging.MaxRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _source = source;
        _pageSize = pageSize;
        _maxRows = Math.Max(maxRows, pageSize);
        _source.CollectionChanged += OnSourceChanged;
        ShowLatest();
    }

    /// <summary>The rows in the window, in order — bind the view to this.</summary>
    public ObservableCollection<T> Items { get; } = [];

    /// <summary>Index in the source of the window's first row.</summary>
    public int Start { get; private set; }

    /// <summary>Index in the source just past the window's last row.</summary>
    public int End => Start + Items.Count;

    /// <summary>Rows before the window ("Load earlier messages").</summary>
    public int Earlier => Start;

    /// <summary>Rows after the window ("Load newer messages"); 0 while the window reaches the newest row.</summary>
    public int Later => _source.Count - End;

    /// <summary>The window reaches the newest row, so rows added at the end appear in it (a streaming reply, a new prompt).</summary>
    public bool IsLive => End == _source.Count;

    public int PageSize => _pageSize;
    public int MaxRows => _maxRows;

    /// <summary>Raised after <see cref="Earlier"/>, <see cref="Later"/> or <see cref="IsLive"/> may have changed.</summary>
    public event Action? Changed;

    /// <summary>The newest page: what a chat shows when it opens.</summary>
    public void ShowLatest() => Show(Math.Max(0, _source.Count - _pageSize), _source.Count);

    /// <summary>Bring up to a page of the rows before the window into it, at its top; returns how many came. Past
    /// <see cref="MaxRows"/>, the newest rows leave the window (it no longer reaches the end).</summary>
    public int LoadEarlier(int? count = null)
    {
        var take = Math.Min(count ?? _pageSize, Start);
        if (take <= 0) return 0;
        for (var i = 0; i < take; i++) Items.Insert(0, _source[Start - 1 - i]);
        Start -= take;
        while (Items.Count > _maxRows) Items.RemoveAt(Items.Count - 1);
        Changed?.Invoke();
        return take;
    }

    /// <summary>Bring up to a page of the rows after the window into it, at its bottom; returns how many came. Past
    /// <see cref="MaxRows"/>, the oldest rows leave the window.</summary>
    public int LoadLater(int? count = null)
    {
        var take = Math.Min(count ?? _pageSize, Later);
        if (take <= 0) return 0;
        var from = End;
        for (var i = 0; i < take; i++) Items.Add(_source[from + i]);
        var drop = Items.Count - _maxRows;
        for (var i = 0; i < drop; i++) Items.RemoveAt(0);
        if (drop > 0) Start += drop;
        Changed?.Invoke();
        return take;
    }

    /// <summary>Let go of the oldest rows of a window that has grown past <paramref name="keep"/> (a long session followed at
    /// its bottom); returns how many left. The view calls this only while it is at the bottom, so nothing it shows moves.</summary>
    public int TrimTop(int keep)
    {
        keep = Math.Max(keep, 1);
        var drop = Items.Count - keep;
        if (drop <= 0) return 0;
        if (drop > keep)
        {
            // Mostly going (a whole chat replayed into an open view): one reset is far cheaper than thousands of removals.
            Show(End - keep, End);
            return drop;
        }
        for (var i = 0; i < drop; i++) Items.RemoveAt(0);
        Start += drop;
        Changed?.Invoke();
        return drop;
    }

    /// <summary>Move the window to the source row at <paramref name="index"/>: <see cref="TranscriptPaging.JumpContext"/>
    /// rows above it and the rest of a page below. Returns false for an index outside the source.</summary>
    public bool ShowAround(int index)
    {
        if (index < 0 || index >= _source.Count) return false;
        if (index >= Start && index < End) return true;
        var start = Math.Max(0, index - TranscriptPaging.JumpContext);
        var end = Math.Min(_source.Count, Math.Max(start + _pageSize, index + 1));
        // Near the end, take a whole page that reaches it, so the window is live again.
        if (_source.Count - end < _pageSize / 2) end = _source.Count;
        Show(start, end);
        return true;
    }

    /// <summary>Whether <paramref name="item"/> is in the window.</summary>
    public bool Contains(T item) => Items.Contains(item);

    private void Show(int start, int end)
    {
        Items.Clear();
        Start = Math.Clamp(start, 0, _source.Count);
        for (var i = Start; i < Math.Min(end, _source.Count); i++) Items.Add(_source[i]);
        Changed?.Invoke();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added && e.NewStartingIndex >= 0:
                Inserted(e.NewStartingIndex, added);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && e.OldStartingIndex >= 0:
                Removed(e.OldStartingIndex, removed.Count);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is { } replaced && e.NewStartingIndex >= 0:
                for (var i = 0; i < replaced.Count; i++)
                {
                    var at = e.NewStartingIndex + i - Start;
                    if (at >= 0 && at < Items.Count) Items[at] = (T)replaced[i]!;
                }
                break;
            case NotifyCollectionChangedAction.Move when e.OldItems is { } moved && e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0:
                Removed(e.OldStartingIndex, moved.Count);
                Inserted(e.NewStartingIndex, moved);
                break;
            default:
                ShowLatest();
                return;
        }
        // A window emptied from under it (its rows removed) shows the newest page again rather than nothing.
        if (Items.Count == 0 && _source.Count > 0) ShowLatest();
        else Changed?.Invoke();
    }

    private void Inserted(int index, System.Collections.IList added)
    {
        if (index < Start)
        {
            Start += added.Count;
            return;
        }
        // At the window's first row, inside it, or right at its end (a window that reaches the end of the chat takes the rows
        // added there, the newest): shown. Further on — after a window that stops short of the end — counted in Later.
        if (index > End) return;
        for (var i = 0; i < added.Count; i++) Items.Insert(index - Start + i, (T)added[i]!);
    }

    private void Removed(int index, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (index < Start) Start--;
            else if (index - Start < Items.Count) Items.RemoveAt(index - Start);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.CollectionChanged -= OnSourceChanged;
    }
}
