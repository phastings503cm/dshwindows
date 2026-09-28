using System.ComponentModel;
using System.Windows.Threading;
using Dsh.Core;
using ICSharpCode.AvalonEdit.Document;

namespace Dsh.App.Model.Code;

/// <summary>An open file whose text lives in an AvalonEdit document. The Core buffer logic (disk
/// baseline, conflicts, BOM/CRLF-preserving saves) is inherited; the text itself is the editor's, so
/// a keystroke never copies the file.</summary>
public sealed class CodeDocument : EditorBuffer
{
    public TextDocument Document { get; } = new();

    private bool _dirty;
    private bool _dirtyStale = true;
    private bool _notifyScheduled;
    private bool _applying;

    private CodeDocument(string path, string contents, TextFileFormat format) : base(path, contents, format)
    {
        Document.Text = contents;
        Document.UndoStack.ClearAll();
        Document.FileName = path;
        Document.Changed += (_, _) =>
        {
            _dirtyStale = true;
            ScheduleDirtyNotification();
        };
        PropertyChanged += OnSelfChanged;
    }

    /// <summary>Open a file, or null when it is missing, too large, or not UTF-8 text.</summary>
    public static new CodeDocument? Open(string path) =>
        ReadFile(path) is { } read ? new CodeDocument(path, read.Text, read.Format) : null;

    public override string Text
    {
        get => Document.Text;
        set
        {
            if (_applying || Document.Text == value) return;
            ReplaceKeepingPosition(value);
            _dirtyStale = true;
            Raise();
            Raise(nameof(IsDirty));
        }
    }

    public override bool IsDirty
    {
        get
        {
            if (_dirtyStale)
            {
                _dirty = Document.TextLength != SavedText.Length || Document.Text != SavedText;
                _dirtyStale = false;
            }
            return _dirty;
        }
    }

    private void OnSelfChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Saving or reloading moves the baseline; the next read recomputes.
        if (e.PropertyName == nameof(SavedText)) _dirtyStale = true;
    }

    /// <summary>Coalesce "maybe dirty" notifications to one per idle moment so typing in a large
    /// file does not compare it against disk on every keystroke.</summary>
    private void ScheduleDirtyNotification()
    {
        if (_notifyScheduled) return;
        _notifyScheduled = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _notifyScheduled = false;
            var before = _dirty;
            var now = IsDirty;
            if (before != now) Raise(nameof(IsDirty));
        });
    }

    /// <summary>Replace only the part that changed, so a reload after the agent edits a file keeps
    /// the caret, the scroll position, and a small undo step.</summary>
    private void ReplaceKeepingPosition(string next)
    {
        var current = Document.Text;
        var prefix = 0;
        var max = Math.Min(current.Length, next.Length);
        while (prefix < max && current[prefix] == next[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && current[current.Length - 1 - suffix] == next[next.Length - 1 - suffix]) suffix++;
        _applying = true;
        try
        {
            Document.Replace(prefix, current.Length - prefix - suffix, next.Substring(prefix, next.Length - prefix - suffix));
        }
        finally
        {
            _applying = false;
        }
    }
}
