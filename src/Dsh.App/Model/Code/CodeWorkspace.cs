using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App.Infrastructure;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Model.Code;

/// <summary>Everything code mode owns: the file tree, the open editors, the terminal tabs, and the
/// watcher that keeps all three honest about what is on disk.</summary>
public sealed partial class CodeWorkspace : ObservableObject
{
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private FsWatcher? _watcher;
    private readonly DispatcherTimer _treeRefresh;
    /// <summary>Set while restoring, so reopening tabs does not write the state back half-built.</summary>
    private bool _restoring;

    [ObservableProperty] private string? _root;
    [ObservableProperty] private FileNode? _tree;

    public ObservableCollection<CodeDocument> Buffers { get; } = [];
    [ObservableProperty] private CodeDocument? _activeBuffer;
    [ObservableProperty] private string? _selection;

    public ObservableCollection<TerminalSession> Terminals { get; } = [];
    [ObservableProperty] private TerminalSession? _activeTerminal;
    [ObservableProperty] private bool _terminalVisible;

    [ObservableProperty] private bool _showTree = true;
    /// <summary>Whether code mode shows the chat pane beside the editor.</summary>
    [ObservableProperty] private bool _showChat = true;
    /// <summary>Raised when a file could not be opened, renamed, or deleted.</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>The user asked to mention a file in chat; the composer inserts it.</summary>
    public event Action<string>? MentionRequested;
    /// <summary>An image file was opened from the tree.</summary>
    public event Action<string>? ImageRequested;

    public CodeWorkspace(AppConfig config, Dispatcher dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        // A build touches thousands of paths; refresh the tree once things settle.
        _treeRefresh = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _treeRefresh!.Stop();
            RefreshTree();
        }, dispatcher);
        _treeRefresh.Stop();
        Buffers.CollectionChanged += (_, e) =>
        {
            foreach (CodeDocument doc in e.NewItems ?? Array.Empty<CodeDocument>()) doc.PropertyChanged += OnBufferChanged;
            foreach (CodeDocument doc in e.OldItems ?? Array.Empty<CodeDocument>()) doc.PropertyChanged -= OnBufferChanged;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(HasBuffers));
        };
        Terminals.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTerminals));
    }

    public bool HasUnsavedChanges => Buffers.Any(b => b.IsDirty);
    public bool HasBuffers => Buffers.Count > 0;
    public bool HasTerminals => Terminals.Count > 0;
    public string? RootName => Root is null ? null : Path.GetFileName(Root.TrimEnd('\\', '/'));

    partial void OnRootChanged(string? value) => OnPropertyChanged(nameof(RootName));
    partial void OnShowTreeChanged(bool value) => PersistState();
    partial void OnShowChatChanged(bool value) => PersistState();
    partial void OnTerminalVisibleChanged(bool value) => PersistState();
    partial void OnActiveBufferChanged(CodeDocument? value)
    {
        if (value is not null) Selection = value.Path;
        PersistState();
    }

    private void OnBufferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorBuffer.IsDirty) || sender is not CodeDocument doc) return;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (FindNode(doc.Path) is { } node) node.IsDirty = doc.IsDirty;
    }

    // MARK: - Project lifecycle

    public void Open(string root)
    {
        if (string.Equals(root, Root, StringComparison.OrdinalIgnoreCase)) return;
        Close();
        Root = root;
        var node = new FileNode(root, isDirectory: true);
        node.Load();
        node.IsExpanded = true;
        Tree = node;
        _watcher = new FsWatcher(root, _dispatcher, FilesChangedOnDisk);
        RestoreState(root);
    }

    public void Close()
    {
        foreach (var terminal in Terminals) terminal.Dispose();
        Terminals.Clear();
        ActiveTerminal = null;
        _watcher?.Dispose();
        _watcher = null;
        _restoring = true;
        Buffers.Clear();
        ActiveBuffer = null;
        _restoring = false;
        Tree = null;
        Root = null;
        Selection = null;
    }

    public string RelativePath(string path)
    {
        if (Root is null) return path;
        var relative = Path.GetRelativePath(Root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    // MARK: - Editors

    public void OpenFile(string path)
    {
        Selection = path;
        if (Buffers.FirstOrDefault(b => SamePath(b.Path, path)) is { } existing)
        {
            ActiveBuffer = existing;
            return;
        }
        if (Directory.Exists(path)) return;
        if (FileFilter.IsImage(path))
        {
            ImageRequested?.Invoke(path);
            return;
        }
        if (!FileFilter.IsTextFile(path))
        {
            // Never launch an executable from a double-click in the tree.
            if (FileFilter.IsLaunchable(path)) ShellIntegration.RevealInExplorer(path);
            else ShellIntegration.Open(path);
            return;
        }
        if (CodeDocument.Open(path) is not { } buffer)
        {
            ErrorMessage = $"{Path.GetFileName(path)} could not be opened as text (it may be too large, or not UTF-8).";
            return;
        }
        Buffers.Add(buffer);
        ActiveBuffer = buffer;
        if (FindNode(path) is { } node) node.IsDirty = false;
        PersistState();
    }

    public void CloseBuffer(CodeDocument buffer)
    {
        var index = Buffers.IndexOf(buffer);
        if (index < 0) return;
        if (buffer.IsDirty && !ConfirmDiscard(buffer)) return;
        Buffers.RemoveAt(index);
        if (FindNode(buffer.Path) is { } node) node.IsDirty = false;
        if (ReferenceEquals(ActiveBuffer, buffer))
            ActiveBuffer = Buffers.Count == 0 ? null : Buffers[Math.Min(index, Buffers.Count - 1)];
        PersistState();
    }

    public void CloseActiveBuffer()
    {
        if (ActiveBuffer is { } buffer) CloseBuffer(buffer);
    }

    public void CloseOtherBuffers(CodeDocument keep)
    {
        foreach (var buffer in Buffers.Where(b => !ReferenceEquals(b, keep)).ToList()) CloseBuffer(buffer);
    }

    private bool ConfirmDiscard(CodeDocument buffer)
    {
        var choice = Dialog.Ask($"Save changes to {buffer.Name}?", "Your changes will be lost if you don't save them.",
            new Dialog.Choice("Save", IsDefault: true), new Dialog.Choice("Don't Save", IsDestructive: true),
            new Dialog.Choice("Cancel", IsCancel: true));
        switch (choice)
        {
            case 0: return Save(buffer);
            case 1: return true;
            default: return false;
        }
    }

    /// <summary>Ask about every unsaved buffer before quitting. False means "stay open".</summary>
    public bool ConfirmCloseAll()
    {
        var dirty = Buffers.Where(b => b.IsDirty).ToList();
        if (dirty.Count == 0) return true;
        var names = string.Join(", ", dirty.Take(5).Select(b => b.Name)) + (dirty.Count > 5 ? ", …" : "");
        var choice = Dialog.Ask(dirty.Count == 1 ? $"Save changes to {dirty[0].Name}?" : $"Save changes to {dirty.Count} files?",
            $"{names}\n\nYour changes will be lost if you don't save them.",
            new Dialog.Choice(dirty.Count == 1 ? "Save" : "Save All", IsDefault: true),
            new Dialog.Choice("Don't Save", IsDestructive: true), new Dialog.Choice("Cancel", IsCancel: true));
        return choice switch
        {
            0 => dirty.All(Save),
            1 => true,
            _ => false,
        };
    }

    public bool Save(CodeDocument buffer)
    {
        try
        {
            buffer.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not save {buffer.Name}: {ex.Message}";
            return false;
        }
    }

    public void SaveActive()
    {
        if (ActiveBuffer is { } buffer) Save(buffer);
    }

    public void SaveAll()
    {
        foreach (var buffer in Buffers.Where(b => b.IsDirty).ToList()) Save(buffer);
    }

    public bool IsDirty(string path) => Buffers.FirstOrDefault(b => SamePath(b.Path, path))?.IsDirty ?? false;

    /// <summary>Reveal a file in the tree and open it — "jump to file" from the chat's file chips.</summary>
    public void Reveal(string path)
    {
        var node = Tree?.ExpandToward(path);
        if (node is not null) node.IsSelected = true;
        Selection = path;
        if (File.Exists(path)) OpenFile(path);
    }

    public void MentionInChat(string path) => MentionRequested?.Invoke(RelativePath(path));

    private FileNode? FindNode(string path) =>
        Tree?.LoadedDescendants().FirstOrDefault(n => SamePath(n.Path, path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // MARK: - Disk changes

    /// <summary>The agent's tool results say precisely which files changed; the watcher covers
    /// everything else (a build, a git checkout, another app).</summary>
    public void ApplyExternalChanges(IReadOnlyList<FileChange> changes) =>
        FilesChangedOnDisk(changes.Select(c => c.Path).ToList());

    private void FilesChangedOnDisk(IReadOnlyList<string> paths)
    {
        if (Root is null) return;
        var everything = paths.Any(p => SamePath(p.TrimEnd('\\', '/'), Root.TrimEnd('\\', '/')));
        var interesting = paths.Where(p => !FileFilter.IsNoise(p)).ToList();
        if (!everything && interesting.Count == 0) return;
        foreach (var buffer in Buffers)
        {
            if (everything || interesting.Any(p => SamePath(p, buffer.Path))) buffer.ExternalChangeDetected();
        }
        _treeRefresh.Stop();
        _treeRefresh.Start();
    }

    public void RefreshTree()
    {
        if (Tree is not { } tree) return;
        tree.Load(force: true);
        foreach (var child in tree.Children.ToList()) child.RefreshExpanded();
        foreach (var buffer in Buffers)
            if (FindNode(buffer.Path) is { } node) node.IsDirty = buffer.IsDirty;
    }

    /// <summary>File names under the project that contain <paramref name="query"/>, for the tree's
    /// filter box. Walks off the UI thread; at most 200 results.</summary>
    public Task<List<string>> SearchFilesAsync(string query, CancellationToken cancellationToken)
    {
        var root = Root;
        if (root is null || query.Trim().Length == 0) return Task.FromResult(new List<string>());
        var needle = query.Trim();
        return Task.Run(() =>
        {
            var results = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            while (pending.Count > 0 && results.Count < 200)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(directory, "*", options))
                        if (!FileFilter.IsIgnored(Path.GetFileName(sub), true)) pending.Push(sub);
                    foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                    {
                        var name = Path.GetFileName(file);
                        if (FileFilter.IsIgnored(name, false)) continue;
                        if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) results.Add(file);
                        if (results.Count >= 200) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            results.Sort((a, b) => Path.GetFileName(a).Length.CompareTo(Path.GetFileName(b).Length));
            return results;
        }, cancellationToken);
    }

    // MARK: - Terminals

    public TerminalSession? NewTerminal(string? cwd = null)
    {
        var directory = cwd ?? Root;
        if (directory is null) return null;
        var session = new TerminalSession(directory, _dispatcher);
        Terminals.Add(session);
        ActiveTerminal = session;
        TerminalVisible = true;
        return session;
    }

    public void CloseTerminal(TerminalSession session)
    {
        var index = Terminals.IndexOf(session);
        if (index < 0) return;
        session.Dispose();
        Terminals.RemoveAt(index);
        if (ReferenceEquals(ActiveTerminal, session))
            ActiveTerminal = Terminals.Count == 0 ? null : Terminals[Math.Min(index, Terminals.Count - 1)];
        if (Terminals.Count == 0) TerminalVisible = false;
    }

    /// <summary>Toggle the panel, creating the first terminal on demand.</summary>
    public void ToggleTerminal()
    {
        if (Terminals.Count == 0) NewTerminal();
        else TerminalVisible = !TerminalVisible;
    }

    /// <summary>The command line the terminal panel starts.</summary>
    public string TerminalCommandLine => AgentShell.DefaultTerminalCommandLine(_config.TerminalShell);

    // MARK: - Restore

    /// <summary>What code mode reopens with. Terminals are not restored as processes — only whether
    /// the panel was showing — because a shell's state is not ours to fake.</summary>
    private void PersistState()
    {
        if (_restoring || Root is null) return;
        _config.SetCodeState(Root, new CodeState(Buffers.Select(b => b.Path).ToList(), ActiveBuffer?.Path,
                                                 TerminalVisible, ShowTree, ShowChat));
    }

    private void RestoreState(string root)
    {
        if (!_config.CodeStates.TryGetValue(root, out var state)) return;
        _restoring = true;
        try
        {
            ShowTree = state.Tree;
            ShowChat = state.Chat;
            foreach (var path in state.Files.Where(File.Exists)) OpenFile(path);
            if (state.Active is { } active && Buffers.FirstOrDefault(b => SamePath(b.Path, active)) is { } buffer)
            {
                ActiveBuffer = buffer;
                Tree?.ExpandToward(buffer.Path);
            }
        }
        finally
        {
            _restoring = false;
        }
        if (state.Terminal) NewTerminal();
    }

    // MARK: - File operations

    public void PromptNewFile(string directory)
    {
        if (Dialog.Prompt("New File", $"Name for the new file in {Path.GetFileName(directory.TrimEnd('\\'))}:", confirm: "Create") is not { } name)
            return;
        var path = Path.Combine(directory, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            ErrorMessage = $"{name} already exists.";
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            RefreshTree();
            Tree?.ExpandToward(path);
            OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorMessage = $"Could not create {name}: {ex.Message}";
        }
    }

    public void PromptNewFolder(string directory)
    {
        if (Dialog.Prompt("New Folder", $"Name for the new folder in {Path.GetFileName(directory.TrimEnd('\\'))}:", confirm: "Create") is not { } name)
            return;
        try
        {
            var path = Path.Combine(directory, name);
            if (Directory.Exists(path) || File.Exists(path))
            {
                ErrorMessage = $"{name} already exists.";
                return;
            }
            Directory.CreateDirectory(path);
            RefreshTree();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorMessage = $"Could not create {name}: {ex.Message}";
        }
    }

    public void PromptRename(string path)
    {
        var current = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Dialog.Prompt("Rename", $"New name for {current}:", current, "Rename") is not { } name || name == current) return;
        var destination = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\', '/'))!, name);
        try
        {
            var caseOnly = string.Equals(path, destination, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
            {
                ErrorMessage = $"{name} already exists.";
                return;
            }
            if (Directory.Exists(path)) Directory.Move(path, destination);
            else File.Move(path, destination);
            if (Buffers.FirstOrDefault(b => SamePath(b.Path, path)) is { } buffer)
            {
                // A buffer's path is fixed; reopen at the new one.
                var wasActive = ReferenceEquals(ActiveBuffer, buffer);
                Buffers.Remove(buffer);
                OpenFile(destination);
                if (!wasActive) ActiveBuffer = Buffers.LastOrDefault();
            }
            RefreshTree();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorMessage = $"Could not rename: {ex.Message}";
        }
    }

    public void Delete(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (!Dialog.Confirm($"Delete {name}?", "It will be moved to the Recycle Bin.", "Delete", destructive: true)) return;
        if (!ShellIntegration.MoveToRecycleBin(path))
        {
            ErrorMessage = $"Could not delete {name}.";
            return;
        }
        foreach (var buffer in Buffers.Where(b => SamePath(b.Path, path) || b.Path.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            Buffers.Remove(buffer);
        if (ActiveBuffer is not null && !Buffers.Contains(ActiveBuffer)) ActiveBuffer = Buffers.LastOrDefault();
        RefreshTree();
    }
}
