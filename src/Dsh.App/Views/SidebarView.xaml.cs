using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views;

/// <summary>Projects and chats. Anything running sorts to the top and stays reachable without
/// opening its chat.</summary>
public partial class SidebarView : UserControl
{
    private AppModel _model = null!;
    private ListCollectionView _chats = null!;
    private bool _syncingSelection;
    private readonly DispatcherTimer _clock;

    public SidebarView()
    {
        InitializeComponent();
        // "5m ago" labels move on their own.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var session in _model.Host.Sessions) session.RefreshRelativeTime();
        }, Dispatcher);
    }

    public void Attach(AppModel model)
    {
        _model = model;
        DataContext = model;
        _chats = new ListCollectionView(model.Host.Sessions);
        _chats.SortDescriptions.Add(new SortDescription(nameof(SessionVM.Running), ListSortDirection.Descending));
        _chats.SortDescriptions.Add(new SortDescription(nameof(SessionVM.UpdatedAt), ListSortDirection.Descending));
        _chats.IsLiveSorting = true;
        _chats.LiveSortingProperties.Add(nameof(SessionVM.Running));
        _chats.LiveSortingProperties.Add(nameof(SessionVM.UpdatedAt));
        _chats.IsLiveFiltering = true;
        _chats.LiveFilteringProperties.Add(nameof(SessionVM.Title));
        ChatList.ItemsSource = _chats;

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.Project)) UpdateProject();
        };
        model.Host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentHost.SelectedId)) SyncSelection();
            if (e.PropertyName is nameof(AgentHost.AnyRunning) or nameof(AgentHost.RunningCount)) UpdateRunning();
        };
        model.Host.Sessions.CollectionChanged += OnSessionsChanged;
        model.Config.RecentProjects.CollectionChanged += (_, _) => UpdateProject();

        UpdateProject();
        UpdateRunning();
        OnSessionsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        SyncSelection();
        _clock.Start();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var count = _model.Host.Sessions.Count;
        EmptyChats.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Search only earns its space once there is something to search.
        SearchBox.Visibility = count > 8 || SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProject()
    {
        var project = _model.Project;
        ProjectCard.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectName.Text = _model.ProjectName ?? "";
        var branch = project is null ? null : ProjectContext.GitBranch(project);
        ProjectBranch.Text = branch is null ? project ?? "" : $"⎇ {branch}";
        var recents = _model.Config.LiveRecentProjects
            .Where(p => !string.Equals(p, project, StringComparison.OrdinalIgnoreCase)).ToList();
        RecentList.ItemsSource = recents;
        RecentExpander.Visibility = recents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRunning()
    {
        var count = _model.Host.RunningCount;
        RunningBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunningText.Text = count == 1 ? "1 agent running" : $"{count} agents running";
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        try
        {
            ChatList.SelectedItem = _model.Host.Selected;
            if (ChatList.SelectedItem is not null) ChatList.ScrollIntoView(ChatList.SelectedItem);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ChatList.SelectedItem is not SessionVM session) return;
        if (session.Id != _model.Host.SelectedId) _model.Select(session.Id);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        _chats.Filter = query.Length == 0
            ? null
            : item => item is SessionVM s && s.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ChatList_KeyDown(object sender, KeyEventArgs e)
    {
        if (ChatList.SelectedItem is not SessionVM session) return;
        if (e.Key == Key.F2)
        {
            Rename(session);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Delete(session);
            e.Handled = true;
        }
    }

    private static SessionVM? SessionOf(object sender) => (sender as FrameworkElement)?.DataContext as SessionVM;

    private void Rename(SessionVM session)
    {
        if (Dialog.Prompt("Rename Chat", "A title for this chat:", session.Title, "Rename") is { } title)
            _model.Host.RenameSession(session.Id, title);
    }

    private void Delete(SessionVM session)
    {
        if (Dialog.Confirm($"Delete “{session.Title}”?", "The conversation is removed from this computer. This can't be undone.",
                "Delete", destructive: true))
            _model.Host.DeleteSession(session.Id);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } session) Rename(session);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } session) Delete(session);
    }

    private void StopSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } session) _model.Host.StopSession(session.Id);
        e.Handled = true;
    }

    private void RevealSessionProject_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender)?.WorkspacePath is { } path) ShellIntegration.OpenFolder(path);
    }

    private void CopyTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is not { } session) return;
        _model.Host.Hydrate(session);
        var text = string.Join("\n\n", session.Entries.Select(entry => entry switch
        {
            MessageEntryVM { IsUser: true } m => "## You\n\n" + m.Text,
            MessageEntryVM { IsAssistant: true } m => "## Assistant\n\n" + m.Text,
            MessageEntryVM m => "> " + m.Text.Replace("\n", "\n> "),
            ToolEntryVM t => $"- {t.Label} `{t.Preview}` → {t.Summary ?? (t.IsOk == false ? "failed" : "ok")}",
            CompactionEntryVM c => $"--- compacted {c.Removed} earlier messages ---",
            _ => null,
        }).Where(s => s is not null));
        try { Clipboard.SetText(text); } catch (Exception) { }
    }

    private void RunningBar_Click(object sender, MouseButtonEventArgs e)
    {
        if (_model.Host.RunningSessions.FirstOrDefault() is { } first) _model.Select(first.Id);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _model.StopAll();
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _model.ChooseProject();

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _model.OpenProject(path);
    }

    private void ForgetRecent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _model.ForgetProject(path);
    }

    private void RevealProject_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Project is { } project) ShellIntegration.OpenFolder(project);
    }

    private void CopyProjectPath_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Project is { } project)
            try { Clipboard.SetText(project); } catch (Exception) { }
    }

    private void ProjectTerminal_Click(object sender, RoutedEventArgs e)
    {
        _model.Mode = WorkspaceMode.Code;
        _model.Code.NewTerminal();
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e) => _model.CloseProject();

    private void NewChat_Click(object sender, RoutedEventArgs e) => _model.NewChat();

    private void Settings_Click(object sender, RoutedEventArgs e) => _model.ShowSettings();

    private void Skills_Click(object sender, RoutedEventArgs e) => _model.ShowSettings(SettingsTab.Skills);
}
