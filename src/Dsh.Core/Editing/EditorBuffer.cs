using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Dsh.Core;

/// <summary>One open file. The buffer keeps the text the user is editing and the text last seen on
/// disk, which is what lets the editor tell "the agent changed this underneath me" apart from "I have
/// unsaved edits".</summary>
public class EditorBuffer : INotifyPropertyChanged
{
    /// <summary>A guard against loading a 200 MB log into a text view.</summary>
    public const int MaxEditableBytes = 8 * 1024 * 1024;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public Language Language { get; }
    /// <summary>How the file was stored (BOM, CRLF) so saving writes it back the same way.</summary>
    public TextFileFormat Format { get; private set; }

    private string _text;
    private string _savedText;
    private bool _diskConflict;
    private int _reloadToken;

    public virtual string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise();
            Raise(nameof(IsDirty));
        }
    }

    /// <summary>The contents as of the last load or save.</summary>
    public string SavedText
    {
        get => _savedText;
        private set
        {
            if (_savedText == value) return;
            _savedText = value;
            Raise();
            Raise(nameof(IsDirty));
        }
    }

    /// <summary>Set when the file changed on disk while the buffer had unsaved edits.</summary>
    public bool DiskConflict
    {
        get => _diskConflict;
        set
        {
            if (_diskConflict == value) return;
            _diskConflict = value;
            Raise();
        }
    }

    /// <summary>Bumped to force the text view to take <see cref="Text"/> even when it thinks it is
    /// already current (after a reload).</summary>
    public int ReloadToken
    {
        get => _reloadToken;
        private set
        {
            _reloadToken = value;
            Raise();
        }
    }

    public string Id => Path;
    public string Name => System.IO.Path.GetFileName(Path);
    public virtual bool IsDirty => Text != SavedText;

    protected EditorBuffer(string path, string contents, TextFileFormat format)
    {
        Path = path;
        _text = contents;
        _savedText = contents;
        Format = format;
        Language = Language.Detect(path);
    }

    /// <summary>Open a file, or null when it is missing, too large, or not UTF-8 text. Line endings
    /// are normalized to LF in memory and restored on save.</summary>
    public static EditorBuffer? Open(string path)
    {
        if (ReadFile(path) is not { } read) return null;
        return new EditorBuffer(path, read.Text, read.Format);
    }

    protected static (string Text, TextFileFormat Format)? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEditableBytes) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (FileText.Read(path) is not { } read) return null;
        return (TextUtil.NormalizeNewlines(read.Text), read.Format);
    }

    public void Save()
    {
        FileText.Write(Path, Text, Format);
        SavedText = Text;
        DiskConflict = false;
    }

    /// <summary>Take the on-disk contents, discarding unsaved edits.</summary>
    public void ReloadFromDisk()
    {
        if (ReadFile(Path) is not { } read) return;
        Format = read.Format;
        Text = read.Text;
        SavedText = read.Text;
        DiskConflict = false;
        ReloadToken++;
    }

    /// <summary>Called when the watcher reports this file changed. A clean buffer silently follows the
    /// file; a dirty one raises a conflict so unsaved work is never thrown away without asking.</summary>
    public void ExternalChangeDetected()
    {
        if (ReadFile(Path) is not { } read) return;
        if (read.Text == Text)
        {
            SavedText = read.Text;
            return;
        }
        if (IsDirty)
        {
            DiskConflict = true;
        }
        else
        {
            Format = read.Format;
            Text = read.Text;
            SavedText = read.Text;
            ReloadToken++;
        }
    }

    public void KeepMine()
    {
        DiskConflict = false;
        // Re-baseline against disk so the buffer stays "dirty" against it.
        if (ReadFile(Path) is { } read) SavedText = read.Text;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
