using System.IO;
using System.Windows.Threading;

namespace Dsh.App.Model.Code;

/// <summary>Recursive filesystem watcher over a project folder. The agent writes files from a
/// background task and the user edits the same files in the editor; both need to see the other's
/// changes without a manual refresh. Events are coalesced over a short window and delivered on the
/// UI thread.</summary>
public sealed class FsWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Action<IReadOnlyList<string>> _handler;
    private readonly Dispatcher _dispatcher;
    private readonly string _root;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary><paramref name="handler"/> receives the changed paths; the root alone means "the
    /// watcher lost track, re-read everything".</summary>
    public FsWatcher(string root, Dispatcher dispatcher, Action<IReadOnlyList<string>> handler)
    {
        _root = root;
        _dispatcher = dispatcher;
        _handler = handler;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Note(e.FullPath);
            _watcher.Created += (_, e) => Note(e.FullPath);
            _watcher.Deleted += (_, e) => Note(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Note(e.OldFullPath);
                Note(e.FullPath);
            };
            // A burst larger than the buffer overflows it: rescan rather than miss changes.
            _watcher.Error += (_, _) => Note(_root);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Network shares and odd filesystems may refuse to be watched; the tree still refreshes
            // after the agent's own writes and on demand.
            _watcher = null;
        }
    }

    private void Note(string path)
    {
        lock (_pending)
        {
            if (_disposed) return;
            _pending.Add(path);
        }
        _timer.Change(250, Timeout.Infinite);
    }

    private void Flush()
    {
        List<string> changed;
        lock (_pending)
        {
            if (_disposed || _pending.Count == 0) return;
            changed = _pending.ToList();
            _pending.Clear();
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => _handler(changed));
    }

    public void Dispose()
    {
        lock (_pending)
        {
            _disposed = true;
            _pending.Clear();
        }
        _timer.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
