using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Import;

/// <summary>"Import chats": the conversations other tools keep — Claude Code, Codex and OpenClaw on this PC, a Claude.ai or
/// ChatGPT export someone adds, and the person's Guardian chats when signed in — listed with their source, title, date and
/// size, the new ones ticked; the chosen ones come in as real chats any model can carry on. Chats already imported are shown
/// but can't be ticked, so importing again brings only what is new. Nothing is changed where the chats are read from, and
/// nothing leaves the PC except the Guardian calls.</summary>
public sealed class ChatImportWindow : Window
{
    private readonly AppModel _model;
    private readonly ChatImportLocations _locations;
    private readonly bool _useGuardian;
    private readonly List<Row> _rows = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    private readonly TextBlock _status = Ui.Secondary("");
    private readonly Spinner _spinner = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _chips = new();
    private readonly TextBox _filter = Ui.Field(placeholder: "Filter by title, folder or source", width: 280);
    private readonly TextBlock _counts = Ui.Secondary("");
    private readonly CheckBox _automated = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly ItemsControl _list = new();
    private readonly TextBlock _empty = Ui.Secondary("");
    private readonly Button _import;
    private readonly StackPanel _chooseButtons;
    private readonly StackPanel _doneButtons;
    private readonly DockPanel _choose = new();
    private readonly ScrollViewer _done = new() { Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private ChatSource? _source;
    private CancellationTokenSource _work = new();
    private bool _busy;
    private bool _importing;
    private ChatImportResult? _result;

    /// <summary>One chat in the list: what was found, and its line.</summary>
    private sealed class Row
    {
        public required ChatCandidate Chat { get; set; }
        public required CheckBox Box { get; init; }
        public required TextBlock Meta { get; init; }
        public required FrameworkElement Element { get; init; }
        public bool Ticked => Box.IsChecked == true && Chat.Status == ChatImportStatus.New;
    }

    /// <param name="locations">Where to look (the real profile unless the self-test says otherwise).</param>
    /// <param name="useGuardian">Ask Guardian for the account's chats (off in the self-test).</param>
    public ChatImportWindow(AppModel model, ChatImportLocations? locations = null, bool useGuardian = true)
    {
        _model = model;
        _locations = locations ?? ChatImportLocations.Standard;
        _useGuardian = useGuardian;
        Title = "Import Chats";
        Width = 940;
        Height = 760;
        MinWidth = 680;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.MaxWidth = 460;

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 6) };
        header.Children.Add(Ui.Title("Import Chats"));
        // The Mac's sheet says the same, with "this Mac".
        var intro = Ui.Secondary("Bring conversations from other AI tools into Vision as chats you can carry on with any model. " +
                                 "Reading them changes nothing, and nothing leaves this PC except calls to your signed-in Guardian.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        header.Children.Add(intro);
        var add = Ui.Button("Add Export File…", AddFiles);
        add.ToolTip = "A Claude.ai or ChatGPT data export (.zip or conversations.json), or a Vision chat (.json) — or one Claude Code, Codex or OpenClaw session (.jsonl).";
        var chipsRow = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        add.VerticalAlignment = VerticalAlignment.Top;
        chipsRow.Children.Add(add);
        chipsRow.Children.Add(_chips);
        header.Children.Add(chipsRow);

        var links = Ui.Stack(Orientation.Horizontal,
            Ui.LinkButton("Select New", SelectNew), Ui.LinkButton("Select All Shown", () => SelectShown(true)), Ui.LinkButton("Select None", () => SelectShown(false)),
            Ui.LinkButton("Look Again", () => _ = ScanAsync()));
        var tools = new DockPanel { Margin = new Thickness(24, 4, 24, 6) };
        DockPanel.SetDock(links, Dock.Right);
        tools.Children.Add(links);
        _counts.VerticalAlignment = VerticalAlignment.Center;
        _counts.Margin = new Thickness(14, 0, 0, 0);
        tools.Children.Add(Ui.Stack(Orientation.Horizontal, _filter, _automated, _counts));
        _filter.TextChanged += (_, _) => Render();
        _automated.Click += (_, _) => Render();
        DockPanel.SetDock(tools, Dock.Top);
        _choose.Children.Add(tools);

        // A long list stays quick: only the lines on screen are laid out.
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var presenter = new FrameworkElementFactory(typeof(ScrollViewer));
        presenter.SetValue(ScrollViewer.CanContentScrollProperty, true);
        presenter.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        presenter.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        presenter.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        _list.Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = presenter };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.Margin = new Thickness(24, 0, 12, 0);
        _empty.Margin = new Thickness(24, 12, 24, 0);
        _empty.TextWrapping = TextWrapping.Wrap;
        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        _choose.Children.Add(listArea);

        var stages = new Grid();
        stages.Children.Add(_choose);
        stages.Children.Add(_done);

        _import = Ui.Button("Import", () => _ = ImportAsync(), accent: true);
        _chooseButtons = Ui.Buttons(Ui.Button("Close", Close), _import);
        _doneButtons = Ui.Buttons(Ui.Button("Search them…", SearchImported), Ui.Button("Open the newest", OpenNewest), Ui.Button("Done", Close, accent: true));
        _doneButtons.Visibility = Visibility.Collapsed;
        var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 16) };
        var right = Ui.Stack(Orientation.Horizontal, _chooseButtons, _doneButtons);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(Ui.Stack(Orientation.Horizontal, _spinner, _status));

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(stages);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_importing)
            {
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            if (SelfTest.Current is null) _ = ScanAsync();
        };
        Closed += (_, _) => _work.Cancel();
        RenderChips();
        RefreshChrome();
    }

    // MARK: - Looking

    /// <summary>Look everywhere again: this PC's tools, then Guardian.</summary>
    internal async Task ScanAsync()
    {
        if (_importing) return;
        _work.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        _rows.Clear();
        _keys.Clear();
        _keys.UnionWith(ChatImporter.ImportedKeys(_model.Host.Log));
        Render();
        Busy(true, "Looking for chats on this PC…");
        var problems = new List<string>();
        var progress = new Progress<string>(text =>
        {
            if (!token.IsCancellationRequested) _status.Text = text;
        });
        try
        {
            var stateDirs = await OpenClawFolders();
            var locations = _locations with { OpenClawStateDirs = stateDirs };
            var cache = SelfTest.Current is null ? ChatScanCache.Load(ChatScanCache.DefaultPath) : null;
            var local = await Task.Run(() =>
            {
                var found = ChatImportScanner.Local(locations, cache, progress, token);
                cache?.Save();
                return found;
            }, token);
            Add(local);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problems.Add("Couldn't read everything on this PC: " + error.Message);
        }
        if (_useGuardian && _model.Guardian.Client is { } client)
        {
            _status.Text = "Asking Guardian for your chats…";
            await ScanGuardianAsync(client, problems, token);
        }
        if (token.IsCancellationRequested) return;
        Busy(false, problems.Count > 0 ? string.Join(" ", problems) : Found());
    }

    /// <summary>OpenClaw's state folders: the ones in the profile, plus any the Import &amp; Export page's search finds (other
    /// profiles, WSL).</summary>
    private async Task<IReadOnlyList<string>> OpenClawFolders()
    {
        var folders = _locations.OpenClaw.ToList();
        if (SelfTest.Current is not null || _locations.OpenClawStateDirs is not null) return folders;
        try
        {
            foreach (var install in await Task.Run(() => OpenClawScanner.FindAsync(new LocalFileSource())))
                if (Directory.Exists(install.StateDir) && !folders.Contains(install.StateDir, StringComparer.OrdinalIgnoreCase)) folders.Add(install.StateDir);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The usual folders were still looked in.
        }
        return folders;
    }

    private async Task ScanGuardianAsync(GuardianClient client, List<string> problems, CancellationToken token)
    {
        try
        {
            var web = await client.WebChatsAsync(token);
            Add(web.Select(chat => new ChatCandidate
            {
                Source = ChatSource.Guardian,
                SourceId = chat.Id,
                Title = chat.Title,
                StartedAt = chat.CreatedAt,
                UpdatedAt = chat.UpdatedAt,
                Location = client.Address,
                Shared = chat.SharedWithMe,
                Note = chat.Remark,
                Load = t => GuardianChatImport.ReadAsync(client, chat, t),
            }));
            var saved = await client.ChatsAsync(token);
            Add(saved.Select(chat => new ChatCandidate
            {
                Source = ChatSource.Vision,
                SourceId = chat.Id,
                Title = chat.Title,
                UpdatedAt = chat.UpdatedAt,
                Location = client.Address,
                Note = "Saved in Guardian from Vision",
                Load = t => GuardianChatImport.ReadSavedAsync(client, chat, t),
            }));
            _ = CountGuardianAsync(client, token);
        }
        catch (OperationCanceledException)
        {
            // Closed or looked again.
        }
        catch (GuardianError error)
        {
            if (error.IsRefusal) _model.Guardian.Report(error);
            problems.Add("Guardian: " + error.Message);
        }
    }

    /// <summary>Guardian's list has no sizes: each chat's count is asked for (three at a time) and filled in as it comes.</summary>
    private async Task CountGuardianAsync(GuardianClient client, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(3);
        var waiting = _rows.Where(r => r.Chat.Source == ChatSource.Guardian && r.Chat.Messages is null).ToList();
        await Task.WhenAll(waiting.Select(async row =>
        {
            await gate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                var count = await GuardianChatImport.CountAsync(client, row.Chat.SourceId, token).ConfigureAwait(true);
                row.Chat = row.Chat with { Messages = count };
                row.Meta.Text = Meta(row.Chat);
                if (count == 0) row.Box.IsChecked = false;
                RefreshChrome();
            }
            catch (Exception error) when (error is GuardianError or OperationCanceledException)
            {
                // Stays "counted when imported".
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);
    }

    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a chat export or session file",
            Filter = "Chat exports and sessions (*.zip;*.json;*.jsonl)|*.zip;*.json;*.jsonl|All files (*.*)|*.*",
            Multiselect = true,
        };
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) dialog.InitialDirectory = downloads;
        if (dialog.ShowDialog(this) != true) return;
        _ = AddFilesAsync(dialog.FileNames);
    }

    internal async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        Busy(true, "Reading " + string.Join(", ", paths.Select(Path.GetFileName)) + "…");
        var problems = new List<string>();
        var added = 0;
        ChatSource? first = null;
        foreach (var path in paths)
        {
            var (chats, problem) = await Task.Run(() => ChatExportFiles.Read(path));
            if (problem is not null) problems.Add(problem);
            added += Add(chats.Select(c => c.ToCandidate(path)));
            first ??= chats.FirstOrDefault()?.Source;
        }
        if (first is { } source && added > 0) _source = source;
        Render();
        Busy(false, problems.Count > 0 ? string.Join(" ", problems)
            : added == 0 ? "Those chats are already in the list." : $"Found {Words(added, "chat", "chats")} in {string.Join(", ", paths.Select(Path.GetFileName))}.");
    }

    /// <summary>Add found chats (one already listed is left as it is). Returns how many were new to the list.</summary>
    private int Add(IEnumerable<ChatCandidate> found)
    {
        var listed = _rows.Select(r => r.Chat.Key).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var candidate in ChatImporter.WithStatus(found, _keys))
        {
            if (!listed.Add(candidate.Key)) continue;
            _rows.Add(MakeRow(candidate));
            added++;
        }
        _rows.Sort((a, b) => b.Chat.UpdatedAt.CompareTo(a.Chat.UpdatedAt));
        Render();
        return added;
    }

    private Row MakeRow(ChatCandidate chat)
    {
        var title = Ui.Text(chat.Title, 13.5, wrap: false);
        var meta = Ui.Secondary(Meta(chat));
        meta.TextWrapping = TextWrapping.NoWrap;
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        var box = new CheckBox
        {
            Content = Ui.Stack(title, meta),
            IsChecked = chat.SelectedByDefault,
            IsEnabled = chat.Status == ChatImportStatus.New,
            Padding = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = chat.Location,
        };
        if (chat.Status != ChatImportStatus.New) box.ToolTip = "Imported already — importing again brings only new chats.";
        box.Click += (_, _) => RefreshChrome();
        var element = new Border { Child = box, Padding = new Thickness(0, 5, 0, 5) };
        return new Row { Chat = chat, Box = box, Meta = meta, Element = element };
    }

    /// <summary>"Claude Code · 19 Oct 2026, 14:02 · 473 messages · C:\code\shop · 12 subagent transcripts left out".</summary>
    internal static string Meta(ChatCandidate chat)
    {
        var parts = new List<string>
        {
            chat.Source == ChatSource.Vision && chat.Location.StartsWith("http", StringComparison.Ordinal) ? "Vision (saved in Guardian)" : chat.Source.Label(),
            chat.UpdatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture),
            chat.Messages is { } n ? Words(n, "message", "messages") : "counting messages…",
        };
        if (chat.Project is { Length: > 0 } project) parts.Add(project);
        if (chat.Note is { Length: > 0 } note && note is not ("Saved in Guardian from Vision" or "Cron run" or "Scripted run")) parts.Add(note);
        if (chat.Automated) parts.Add("Scheduled");
        if (chat.Status == ChatImportStatus.Imported) parts.Add("Imported");
        return string.Join(" · ", parts);
    }

    private static string Words(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    // MARK: - The list

    private bool Visible(Row row)
    {
        if (_source is { } source && row.Chat.Source != source) return false;
        if (row.Chat.Automated && _automated.IsChecked != true) return false;
        var text = _filter.Text.Trim();
        return text.Length == 0 || row.Chat.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || row.Chat.Project?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true
               || row.Chat.Source.Label().Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Render()
    {
        var shown = _rows.Where(Visible).Select(r => r.Element).ToList();
        _list.ItemsSource = shown;
        var automated = _rows.Count(r => r.Chat.Automated && (_source is null || r.Chat.Source == _source));
        _automated.Content = $"Show scheduled and scripted runs ({automated.ToString("N0", CultureInfo.InvariantCulture)})";
        _counts.Text = _rows.Count == 0 ? "" : $"{shown.Count.ToString("N0", CultureInfo.InvariantCulture)} shown · {_rows.Count(r => r.Ticked).ToString("N0", CultureInfo.InvariantCulture)} ticked";
        _automated.Visibility = automated > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _busy && _rows.Count == 0 ? ""
            : _rows.Count == 0 ? "No chats found yet. Add an export file, or sign in to Guardian."
            : shown.Count == 0 ? "No chats match." : "";
        _empty.Visibility = _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderChips();
        RefreshChrome();
    }

    private void RenderChips()
    {
        _chips.Children.Clear();
        void Chip(string label, ChatSource? source, int count)
        {
            var chip = Ui.Button($"{label} · {count.ToString("N0", CultureInfo.InvariantCulture)}", () =>
            {
                _source = source;
                Render();
            }, accent: _source == source);
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.MinWidth = 0;
            chip.Padding = new Thickness(10, 3, 10, 3);
            _chips.Children.Add(chip);
        }
        Chip("All", null, _rows.Count);
        foreach (var source in ChatSources.All)
        {
            var count = _rows.Count(r => r.Chat.Source == source);
            if (count > 0 || source is ChatSource.ClaudeCode or ChatSource.Codex or ChatSource.OpenClaw || source == ChatSource.Guardian && _model.Guardian.IsSignedIn)
                Chip(source == ChatSource.Vision ? "Saved in Guardian" : source.Label(), source, count);
        }
    }

    private void SelectNew()
    {
        foreach (var row in _rows) row.Box.IsChecked = row.Chat.SelectedByDefault;
        RefreshChrome();
    }

    private void SelectShown(bool on)
    {
        foreach (var row in _rows.Where(Visible).Where(r => r.Chat.Status == ChatImportStatus.New)) row.Box.IsChecked = on;
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        var ticked = _rows.Count(r => r.Ticked);
        if (_rows.Count > 0) _counts.Text = $"{_rows.Count(Visible).ToString("N0", CultureInfo.InvariantCulture)} shown · {ticked.ToString("N0", CultureInfo.InvariantCulture)} ticked";
        _import.Content = ticked == 0 ? "Import" : $"Import {Words(ticked, "Chat", "Chats")}";
        _import.IsEnabled = ticked > 0 && !_busy;
    }

    private string Found()
    {
        var fresh = _rows.Count(r => r.Chat.Status == ChatImportStatus.New);
        var already = _rows.Count - fresh;
        return _rows.Count == 0 ? "" : $"Found {Words(_rows.Count, "chat", "chats")}" + (already > 0 ? $"; {already.ToString("N0", CultureInfo.InvariantCulture)} already in Vision." : ".");
    }

    private void Busy(bool busy, string status)
    {
        _busy = busy;
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = status;
        Render();
    }

    // MARK: - Importing

    internal async Task ImportAsync()
    {
        var chosen = _rows.Where(r => r.Ticked).Select(r => r.Chat).ToList();
        if (chosen.Count == 0 || _importing) return;
        _importing = true;
        Busy(true, "Importing…");
        var progress = new Progress<string>(text => _status.Text = text);
        try
        {
            _result = await ChatImporter.ImportAsync(_model.Host.Log, chosen, progress, _work.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _importing = false;
            // Whatever came in before a stop is a real chat now: show it in the list.
            _model.ChatsImported();
        }
        Busy(false, "");
        ShowDone(_result);
    }

    private void ShowDone(ChatImportResult result)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 8) };
        var summary = Ui.Text(result.Summary(), 15, FontWeights.SemiBold);
        panel.Children.Add(summary);
        var hint = Ui.Secondary(result.Imported.Count > 0
            ? "They're in the chat list by the dates they happened. Open one and keep going with whichever model you choose — what the tools did is kept as readable steps, so any model can follow it."
            : "Everything you chose was already in Vision.");
        hint.Margin = new Thickness(0, 6, 0, 10);
        panel.Children.Add(hint);
        foreach (var (chat, reason) in result.Failed.Take(30))
            panel.Children.Add(Ui.Status($"{chat.Title} ({chat.Source.Label()}): {reason}", "SystemFillColorCautionBrush"));
        _done.Content = panel;
        _choose.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Visible;
        _chooseButtons.Visibility = Visibility.Collapsed;
        _doneButtons.Visibility = Visibility.Visible;
        ((FrameworkElement)_doneButtons.Children[1]).IsEnabled = result.Imported.Count > 0;
        ((FrameworkElement)_doneButtons.Children[0]).IsEnabled = result.Imported.Count > 0;
    }

    private void OpenNewest()
    {
        var log = _model.Host.Log;
        var newest = _result?.Imported.Select(log.Session).OfType<LogSession>().MaxBy(s => s.UpdatedAt);
        Close();
        if (newest is not null) _model.OpenSession(newest.Id);
    }

    private void SearchImported()
    {
        Close();
        _model.ShowChatSearch();
    }

    // MARK: - Self-test

    /// <summary>For the self-test: Guardian's chats as if the account had answered (nothing is contacted).</summary>
    internal void SelfTestAddGuardian(IEnumerable<ChatCandidate> chats) => Add(chats);

    /// <summary>For the self-test: show one source, and the scheduled runs.</summary>
    internal void SelfTestShow(ChatSource? source, bool automated)
    {
        _source = source;
        _automated.IsChecked = automated;
        Render();
    }
}
