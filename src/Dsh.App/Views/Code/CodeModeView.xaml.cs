using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Model.Code;

namespace Dsh.App.Views.Code;

/// <summary>A file-filter hit.</summary>
public sealed record FileHit(string Path, string Name, string Folder);

/// <summary>Code mode: the project navigator, a tabbed editor, an integrated terminal, and the agent
/// alongside — the layout a VS Code user expects, with the chat where the side panel would be.</summary>
public partial class CodeModeView : UserControl
{
    private readonly AppModel _model;
    private readonly CodeWorkspace _code;
    private readonly CodeEditorView _editor;
    private CodeDocument? _watchedBuffer;
    private CancellationTokenSource? _filterSearch;
    private GridLength _treeWidth = new(250);
    private GridLength _chatWidth = new(420);
    private GridLength _terminalHeight = new(240);

    /// <summary>What the chat pane shows with no chat selected.</summary>
    public FrameworkElement NoChatPlaceholder { get; }

    public CodeModeView(AppModel model)
    {
        _model = model;
        _code = model.Code;
        InitializeComponent();

        _editor = new CodeEditorView(model.Config);
        _editor.CaretMoved += UpdateStatus;
        EditorHost.Content = _editor;
        NoChatPlaceholder = BuildNoChat();

        Tree.ItemContainerStyle = TreeItemStyle();
        Tabs.ItemsSource = _code.Buffers;
        TerminalTabs.ItemsSource = _code.Terminals;
        Terminal.FontSize = model.Config.TerminalFontSize;
        Terminal.ScrollInfoChanged += UpdateTerminalScroll;
        Terminal.SizeChanged += (_, _) => UpdateTerminalScroll();

        _code.PropertyChanged += OnCodeChanged;
        _code.Buffers.CollectionChanged += (_, _) => UpdateEditor();
        _code.Terminals.CollectionChanged += (_, _) => UpdateTerminal();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.Project)) UpdateTree();
        };
        model.Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppConfig.TerminalFontSize)) Terminal.FontSize = model.Config.TerminalFontSize;
            if (e.PropertyName == nameof(AppConfig.TerminalShell)) Terminal.CommandLine = _code.TerminalCommandLine;
        };
        Terminal.CommandLine = _code.TerminalCommandLine;

        UpdateTree();
        UpdateLayoutPanes();
        UpdateEditor();
        UpdateTerminal();
        UpdateError();
    }

    private void OnCodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CodeWorkspace.Tree):
            case nameof(CodeWorkspace.Root):
                UpdateTree();
                break;
            case nameof(CodeWorkspace.ActiveBuffer):
                UpdateEditor();
                break;
            case nameof(CodeWorkspace.ActiveTerminal):
            case nameof(CodeWorkspace.TerminalVisible):
                UpdateTerminal();
                UpdateLayoutPanes();
                break;
            case nameof(CodeWorkspace.ShowTree):
            case nameof(CodeWorkspace.ShowChat):
                UpdateLayoutPanes();
                break;
            case nameof(CodeWorkspace.ErrorMessage):
                UpdateError();
                break;
        }
    }

    /// <summary>TreeViewItems follow the node's expansion and selection, on top of the theme's style.</summary>
    private Style TreeItemStyle()
    {
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(FileNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(FileNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.PaddingProperty, new Thickness(2, 1, 2, 1)));
        return style;
    }

    private FrameworkElement BuildNoChat()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 300 };
        panel.Children.Add(new TextBlock { Text = "No chat open", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var hint = new TextBlock
        {
            Text = "Start a chat to work on this project with the agent.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 14),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(hint);
        var button = new Button { Content = "New Chat", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Center };
        button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        button.Click += (_, _) => _model.NewChat();
        panel.Children.Add(button);
        return panel;
    }

    // MARK: - Layout

    private void UpdateLayoutPanes()
    {
        var showTree = _code.ShowTree;
        if (!showTree && TreeColumn.ActualWidth > 0) _treeWidth = TreeColumn.Width;
        TreePane.Visibility = TreeSplitter.Visibility = showTree ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.MinWidth = showTree ? 160 : 0;
        TreeColumn.Width = showTree ? _treeWidth : new GridLength(0);
        TreeToggle.IsChecked = showTree;

        var showChat = _code.ShowChat;
        if (!showChat && ChatColumn.ActualWidth > 0) _chatWidth = ChatColumn.Width;
        ChatPane.Visibility = ChatSplitter.Visibility = showChat ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.MinWidth = showChat ? 320 : 0;
        ChatColumn.Width = showChat ? _chatWidth : new GridLength(0);
        ChatToggle.IsChecked = showChat;

        var showTerminal = _code.TerminalVisible && _code.Terminals.Count > 0;
        if (!showTerminal && TerminalRow.ActualHeight > 0) _terminalHeight = TerminalRow.Height;
        TerminalPane.Visibility = TerminalSplitter.Visibility = showTerminal ? Visibility.Visible : Visibility.Collapsed;
        TerminalRow.MinHeight = showTerminal ? 80 : 0;
        TerminalRow.Height = showTerminal ? _terminalHeight : new GridLength(0);
        TerminalToggle.IsChecked = showTerminal;

        foreach (var toggle in new[] { TreeToggle, ChatToggle, TerminalToggle })
            toggle.SetResourceReference(ForegroundProperty, toggle.IsChecked == true ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
    }

    private void TreeToggle_Click(object sender, RoutedEventArgs e) => _code.ShowTree = !_code.ShowTree;
    private void ChatToggle_Click(object sender, RoutedEventArgs e) => _code.ShowChat = !_code.ShowChat;

    private void TerminalToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Project is null)
        {
            TerminalToggle.IsChecked = false;
            return;
        }
        _code.ToggleTerminal();
    }

    // MARK: - File tree

    private void UpdateTree()
    {
        var tree = _code.Tree;
        Tree.ItemsSource = tree?.Children;
        NoProject.Visibility = tree is null ? Visibility.Visible : Visibility.Collapsed;
        Tree.Visibility = tree is null || FilterBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        FilterBox.IsEnabled = tree is not null;
        NoFileHint.Text = _model.Project is null
            ? "Open a project folder, then pick a file from the tree."
            : "Pick a file from the tree to start editing.";
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private async void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        var query = FilterBox.Text.Trim();
        _filterSearch?.Cancel();
        if (query.Length == 0)
        {
            FilterResults.Visibility = NoMatches.Visibility = Visibility.Collapsed;
            Tree.Visibility = _code.Tree is null ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        Tree.Visibility = Visibility.Collapsed;
        var cts = _filterSearch = new CancellationTokenSource();
        try
        {
            var paths = await _code.SearchFilesAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            var hits = paths.Select(p => new FileHit(p, Path.GetFileName(p), Path.GetDirectoryName(_code.RelativePath(p)) ?? "")).ToList();
            FilterResults.ItemsSource = hits;
            FilterResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = hits.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (hits.Count > 0) FilterResults.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                FilterBox.Clear();
                _editor.FocusEditor();
                e.Handled = true;
                break;
            case Key.Down when FilterResults.Items.Count > 0:
                FilterResults.SelectedIndex = Math.Min(FilterResults.SelectedIndex + 1, FilterResults.Items.Count - 1);
                FilterResults.ScrollIntoView(FilterResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when FilterResults.Items.Count > 0:
                FilterResults.SelectedIndex = Math.Max(FilterResults.SelectedIndex - 1, 0);
                FilterResults.ScrollIntoView(FilterResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter when FilterResults.SelectedItem is FileHit hit:
                OpenFromFilter(hit);
                e.Handled = true;
                break;
        }
    }

    private void OpenFromFilter(FileHit hit)
    {
        _code.OpenFile(hit.Path);
        FilterBox.Clear();
        _editor.FocusEditor();
    }

    private void FilterResult_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileHit hit) OpenFromFilter(hit);
    }

    private void FilterResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FilterResults.SelectedItem is FileHit hit)
        {
            OpenFromFilter(hit);
            e.Handled = true;
        }
    }

    private void TreeItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileNode node || node.IsPlaceholder) return;
        if (node.IsDirectory)
        {
            // A click anywhere on a folder row toggles it, like Explorer's navigation pane.
            if (e.OriginalSource is not System.Windows.Shapes.Path) node.IsExpanded = !node.IsExpanded;
        }
        else
        {
            _code.OpenFile(node.Path);
        }
        e.Handled = true;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not FileNode node) return;
        switch (e.Key)
        {
            case Key.Enter when !node.IsDirectory:
                _code.OpenFile(node.Path);
                e.Handled = true;
                break;
            case Key.F2:
                _code.PromptRename(node.Path);
                e.Handled = true;
                break;
            case Key.Delete:
                _code.Delete(node.Path);
                e.Handled = true;
                break;
        }
    }

    private void TreeItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNode { IsPlaceholder: false } node } element || element.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        node.IsSelected = true;
        menu.Items.Clear();
        var folder = node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path)!;
        if (!node.IsDirectory) menu.Items.Add(Menus.Item("Open", () => _code.OpenFile(node.Path), Icons.Page));
        menu.Items.Add(Menus.Item("Reveal in Explorer", () => ShellIntegration.RevealInExplorer(node.Path), Icons.OpenInWindow));
        menu.Items.Add(Menus.Item("Copy Path", () => Copy(node.Path), Icons.Copy));
        menu.Items.Add(Menus.Item("Copy Relative Path", () => Copy(_code.RelativePath(node.Path))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Mention in Chat", () => _code.MentionInChat(node.Path), Icons.Chat));
        if (node.IsDirectory) menu.Items.Add(Menus.Item("Open Terminal Here", () => _code.NewTerminal(node.Path), Icons.Terminal));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("New File…", () => _code.PromptNewFile(folder), Icons.Add));
        menu.Items.Add(Menus.Item("New Folder…", () => _code.PromptNewFolder(folder), Icons.NewFolder));
        menu.Items.Add(Menus.Item("Rename…", () => _code.PromptRename(node.Path), Icons.Rename));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Delete", () => _code.Delete(node.Path), Icons.Delete));
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception) { }
    }

    private string? SelectedFolder()
    {
        if (Tree.SelectedItem is FileNode { IsPlaceholder: false } node) return node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path);
        return _code.Root;
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder() is { } folder) _code.PromptNewFile(folder);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder() is { } folder) _code.PromptNewFolder(folder);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _code.RefreshTree();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _model.ChooseProject();

    // MARK: - Editor

    private void UpdateEditor()
    {
        var buffer = _code.ActiveBuffer;
        if (!ReferenceEquals(Tabs.SelectedItem, buffer)) Tabs.SelectedItem = buffer;
        if (buffer is not null) Tabs.ScrollIntoView(buffer);
        foreach (var gone in _editorDocuments.Except(_code.Buffers).ToList())
        {
            _editor.Forget(gone);
            _editorDocuments.Remove(gone);
        }
        if (buffer is not null) _editorDocuments.Add(buffer);
        _editor.Document = buffer;
        EditorHost.Visibility = buffer is null ? Visibility.Collapsed : Visibility.Visible;
        NoFile.Visibility = buffer is null ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = buffer is null ? Visibility.Collapsed : Visibility.Visible;

        if (_watchedBuffer is not null) _watchedBuffer.PropertyChanged -= OnBufferChanged;
        _watchedBuffer = buffer;
        if (buffer is not null) buffer.PropertyChanged += OnBufferChanged;
        UpdateConflict();
        UpdateStatus();
        if (buffer is not null && IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _editor.FocusEditor);
    }

    private readonly HashSet<CodeDocument> _editorDocuments = [];

    private void OnBufferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.DiskConflict)) UpdateConflict();
        if (e.PropertyName == nameof(CodeDocument.IsDirty)) UpdateStatus();
    }

    private void UpdateConflict()
    {
        var buffer = _code.ActiveBuffer;
        ConflictBar.Visibility = buffer is { DiskConflict: true } ? Visibility.Visible : Visibility.Collapsed;
        ConflictText.Text = buffer is null ? "" : $"{buffer.Name} changed on disk while you had unsaved edits.";
    }

    private void UpdateStatus()
    {
        if (_code.ActiveBuffer is not { } buffer) return;
        PathText.Text = _code.RelativePath(buffer.Path);
        PathText.ToolTip = buffer.Path;
        CaretText.Text = $"Ln {_editor.Line}, Col {_editor.Column}";
        IndentText.Text = $"Spaces: {_editor.Editor.Options.IndentationSize}";
        EncodingText.Text = buffer.Format.HasBom ? "UTF-8 with BOM" : "UTF-8";
        EolText.Text = buffer.Format.UsesCrlf ? "CRLF" : "LF";
        LanguageText.Text = buffer.Language.Name;
        SaveLink.Visibility = buffer.IsDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateError()
    {
        ErrorBar.Visibility = _code.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = _code.ErrorMessage ?? "";
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => _code.ErrorMessage = null;
    private void Save_Click(object sender, RoutedEventArgs e) => _code.SaveActive();

    private void KeepMine_Click(object sender, RoutedEventArgs e) => _code.ActiveBuffer?.KeepMine();
    private void Reload_Click(object sender, RoutedEventArgs e) => _code.ActiveBuffer?.ReloadFromDisk();

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is CodeDocument buffer && !ReferenceEquals(buffer, _code.ActiveBuffer)) _code.ActiveBuffer = buffer;
    }

    /// <summary>Middle-click closes a tab, as in every browser and editor.</summary>
    private void Tabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is CodeDocument buffer)
        {
            _code.CloseBuffer(buffer);
            e.Handled = true;
        }
    }

    private static CodeDocument? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as CodeDocument;

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } buffer) _code.CloseBuffer(buffer);
        e.Handled = true;
    }

    private void CloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } buffer) _code.CloseOtherBuffers(buffer);
    }

    private void CopyTabPath_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } buffer) Copy(buffer.Path);
    }

    private void RevealTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } buffer) ShellIntegration.RevealInExplorer(buffer.Path);
    }

    private void RevealTabInTree_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } buffer) return;
        _code.ShowTree = true;
        if (_code.Tree?.ExpandToward(buffer.Path) is { } node) node.IsSelected = true;
    }

    private void MentionTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } buffer) _code.MentionInChat(buffer.Path);
    }

    // MARK: - Terminal

    private void UpdateTerminal()
    {
        var active = _code.ActiveTerminal;
        if (!ReferenceEquals(TerminalTabs.SelectedItem, active)) TerminalTabs.SelectedItem = active;
        Terminal.CommandLine = _code.TerminalCommandLine;
        Terminal.Session = active;
        UpdateLayoutPanes();
        if (active is not null && _code.TerminalVisible && IsVisible)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Terminal.Focus());
    }

    private void UpdateTerminalScroll()
    {
        var count = Terminal.LineCount;
        var rows = Terminal.VisibleRows;
        TerminalScroll.Maximum = Math.Max(0, count - rows);
        TerminalScroll.ViewportSize = rows;
        TerminalScroll.LargeChange = Math.Max(1, rows - 1);
        TerminalScroll.Value = Terminal.ViewTop;
        TerminalScroll.Visibility = count > rows ? Visibility.Visible : Visibility.Hidden;
    }

    private void TerminalScroll_Scroll(object sender, ScrollEventArgs e) => Terminal.ScrollTo((int)Math.Round(e.NewValue));

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalSession session && !ReferenceEquals(session, _code.ActiveTerminal)) _code.ActiveTerminal = session;
    }

    private void NewTerminal_Click(object sender, RoutedEventArgs e) => _code.NewTerminal();

    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => _code.ActiveTerminal?.Clear();

    private void KillTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_code.ActiveTerminal is { } session) _code.CloseTerminal(session);
    }

    private void HideTerminal_Click(object sender, RoutedEventArgs e) => _code.TerminalVisible = false;

    private void CloseTerminal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TerminalSession session) _code.CloseTerminal(session);
        e.Handled = true;
    }
}
