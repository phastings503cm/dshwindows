using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App.Infrastructure;

namespace Dsh.App.Model.Code;

/// <summary>One entry in the project tree. Children load on first expansion, so opening a large
/// repository costs one directory read rather than a full walk.</summary>
public sealed partial class FileNode : ObservableObject
{
    public string Path { get; }
    public bool IsDirectory { get; }
    public string Name { get; }
    public bool IsPlaceholder { get; }

    /// <summary>Until a folder is read it holds one placeholder child, which is what gives the tree
    /// its expander arrow.</summary>
    public ObservableCollection<FileNode> Children { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Glyph))] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    /// <summary>An open editor has unsaved changes for this file.</summary>
    [ObservableProperty] private bool _isDirty;

    private bool _loaded;

    public FileNode(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Name.Length == 0) Name = path;
        if (isDirectory) Children.Add(new FileNode());
    }

    private FileNode()
    {
        Path = "";
        Name = "";
        IsPlaceholder = true;
    }

    public string Glyph => Icons.ForFile(Name, IsDirectory, IsExpanded).Glyph;
    public string Tint => Icons.ForFile(Name, IsDirectory, false).Tint;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) Load();
    }

    /// <summary>Read this directory, keeping the expansion state of children that survive.</summary>
    public void Load(bool force = false)
    {
        if (!IsDirectory || (_loaded && !force)) return;
        _loaded = true;

        var previous = Children.Where(c => !c.IsPlaceholder).ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var loaded = new List<FileNode>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", options))
            {
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                // Hidden *and* system is the OS's own clutter (desktop.ini, $RECYCLE.BIN); dotfiles stay.
                if (entry.Attributes.HasFlag(FileAttributes.Hidden) && entry.Attributes.HasFlag(FileAttributes.System)) continue;
                if (FileFilter.IsIgnored(entry.Name, isDirectory)) continue;
                if (previous.TryGetValue(entry.FullName, out var existing) && existing.IsDirectory == isDirectory)
                {
                    if (existing.IsExpanded) existing.Load(force);
                    loaded.Add(existing);
                }
                else
                {
                    loaded.Add(new FileNode(entry.FullName, isDirectory));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable folder shows empty.
        }

        // Folders first, then Explorer's natural order (file2 before file10).
        loaded.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : NaturalCompare(a.Name, b.Name));
        Sync(loaded);
    }

    /// <summary>Update the children in place so the tree keeps its scroll position and selection.</summary>
    private void Sync(List<FileNode> next)
    {
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!next.Contains(Children[i])) Children.RemoveAt(i);
        }
        for (var i = 0; i < next.Count; i++)
        {
            var current = Children.IndexOf(next[i]);
            if (current < 0) Children.Insert(i, next[i]);
            else if (current != i) Children.Move(current, i);
        }
    }

    /// <summary>Re-read every directory that is currently open.</summary>
    public void RefreshExpanded()
    {
        if (!IsDirectory || !IsExpanded) return;
        Load(force: true);
        foreach (var child in Children.ToList()) child.RefreshExpanded();
    }

    /// <summary>Open every directory down to <paramref name="target"/> so it can be revealed.
    /// Returns the node for the target when found.</summary>
    public FileNode? ExpandToward(string target)
    {
        if (string.Equals(Path, target, StringComparison.OrdinalIgnoreCase)) return this;
        if (!IsDirectory) return null;
        var prefix = Path.EndsWith('\\') || Path.EndsWith('/') ? Path : Path + System.IO.Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        Load();
        IsExpanded = true;
        foreach (var child in Children)
        {
            if (child.ExpandToward(target) is { } found) return found;
        }
        return null;
    }

    /// <summary>Nodes that are loaded, depth first (for dirty markers and lookups).</summary>
    public IEnumerable<FileNode> LoadedDescendants()
    {
        foreach (var child in Children)
        {
            if (child.IsPlaceholder) continue;
            yield return child;
            foreach (var nested in child.LoadedDescendants()) yield return nested;
        }
    }

    private static int NaturalCompare(string a, string b)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return StrCmpLogicalW(a, b); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public override string ToString() => Name;
}
