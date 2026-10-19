using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Import;

/// <summary>"Search all chats": one search over every chat on this PC — the ones typed here and the ones imported from Claude
/// Code, Codex, OpenClaw, Claude.ai, ChatGPT and Guardian — by title and by what was said, run and answered, with a source
/// filter. Each hit opens its chat at that message.</summary>
public sealed class ChatSearchWindow : Window
{
    private static ChatSearchWindow? _open;

    private readonly AppModel _model;
    private readonly TextBox _query = Ui.Field(placeholder: "Words to find — \"a phrase\" in quotes");
    private readonly ComboBox _source = new() { MinWidth = 170, Margin = new Thickness(10, 0, 0, 0) };
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = Ui.Secondary("");
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _search;

    public static void Show(Window? owner, AppModel model, string? query = null)
    {
        if (_open is { IsLoaded: true })
        {
            if (query is not null) _open._query.Text = query;
            _open.Activate();
            return;
        }
        _open = new ChatSearchWindow(model, query) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public ChatSearchWindow(AppModel model, string? query = null)
    {
        _model = model;
        Title = "Search all chats";
        Width = 880;
        Height = 720;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        header.Children.Add(Ui.Title("Search all chats"));
        var intro = Ui.Secondary("Every chat on this PC — started here or imported — by its title and by what was said, run and answered. Every word must appear in the chat.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        header.Children.Add(intro);
        var bar = new DockPanel();
        DockPanel.SetDock(_source, Dock.Right);
        bar.Children.Add(_source);
        bar.Children.Add(_query);
        header.Children.Add(bar);

        _source.Items.Add(new ComboBoxItem { Content = "All sources", Tag = null, IsSelected = true });
        foreach (var source in ChatSources.All)
            _source.Items.Add(new ComboBoxItem { Content = source == ChatSource.Vision ? "Started in Vision" : source.Label(), Tag = source });
        _source.SelectionChanged += (_, _) => Schedule();

        _results.Margin = new Thickness(24, 0, 24, 12);
        var scroll = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _status.Margin = new Thickness(24, 8, 24, 14);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        Content = root;

        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RunAsync();
        }, Dispatcher);
        _debounce.Stop();
        _query.TextChanged += (_, _) => Schedule();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            _query.Focus();
            if (query is not null) _query.Text = query;
            else Render([], "");
        };
        Closed += (_, _) => _search?.Cancel();
    }

    private ChatSource? Source => (_source.SelectedItem as ComboBoxItem)?.Tag as ChatSource?;

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Search what the chats hold now (taken here, searched off the UI thread).</summary>
    internal async Task RunAsync()
    {
        _search?.Cancel();
        var cancel = _search = new CancellationTokenSource();
        var query = _query.Text;
        if (ChatSearch.Terms(query).Count == 0)
        {
            Render([], query);
            return;
        }
        var snapshot = ChatSearch.Snapshot(_model.Host.Log);
        var source = Source;
        _status.Text = "Searching…";
        IReadOnlyList<ChatSearch.Result> results;
        try
        {
            results = await Task.Run(() => ChatSearch.Run(snapshot, query, source, cancellationToken: cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancel.IsCancellationRequested) return;
        Render(results, query);
    }

    private void Render(IReadOnlyList<ChatSearch.Result> results, string query)
    {
        _results.Children.Clear();
        if (ChatSearch.Terms(query).Count == 0)
        {
            var chats = _model.Host.Log.List();
            var imported = chats.Count(c => c.Imported is not null);
            _status.Text = $"{Words(chats.Count, "chat", "chats")} on this PC" + (imported > 0 ? $", {imported.ToString("N0", CultureInfo.InvariantCulture)} of them imported." : ".");
            return;
        }
        foreach (var result in results) _results.Children.Add(Card(result));
        var matches = results.Sum(r => r.MatchCount);
        _status.Text = results.Count == 0 ? "No chat has all of those words." : $"{Words(results.Count, "chat", "chats")}, {Words(matches, "matching message", "matching messages")}" +
                                                                              (results.Count >= 200 ? " (the first 200 chats)." : ".");
    }

    private Border Card(ChatSearch.Result result)
    {
        var panel = new StackPanel();
        var title = Ui.LinkButton(result.Session.Title, () => Open(result.Session.Id, null));
        title.HorizontalAlignment = HorizontalAlignment.Left;
        ((TextBlock)title.Content).FontSize = 14;
        ((TextBlock)title.Content).FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        var facts = new List<string>
        {
            result.Source == ChatSource.Vision ? "Vision" : "From " + result.Source.Label(),
            result.Session.UpdatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture),
            result.MatchCount == 0 ? "title" : Words(result.MatchCount, "match", "matches"),
        };
        if (result.Session.Cwd is { Length: > 0 } cwd) facts.Add(cwd);
        var meta = Ui.Secondary(string.Join(" · ", facts));
        meta.Margin = new Thickness(4, 0, 0, 4);
        panel.Children.Add(meta);
        foreach (var match in result.Matches.Take(3))
        {
            var who = match.Kind switch
            {
                "user" => "You",
                "assistant" => "Assistant",
                "tool" => "Tool",
                "compaction" => "Summary",
                _ => "Note",
            };
            var line = Ui.Text($"{who}: {match.Snippet}", 12.5, wrap: false);
            var hit = new Button { Content = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2, 4, 2), Cursor = Cursors.Hand };
            hit.SetResourceReference(StyleProperty, "SubtleButton");
            hit.ToolTip = "Open the chat at this message";
            var seq = match.Seq;
            hit.Click += (_, _) => Open(result.Session.Id, seq);
            panel.Children.Add(hit);
        }
        var card = Ui.Card(panel, new Thickness(12, 8, 12, 8));
        card.Margin = new Thickness(0, 0, 0, 6);
        return card;
    }

    private void Open(string chatId, int? seq)
    {
        if (seq is { } row) _model.ShowChatRow(chatId, row);
        else _model.OpenSession(chatId);
        Owner?.Activate();
    }

    private static string Words(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    /// <summary>For the self-test: search for <paramref name="query"/> now.</summary>
    internal Task SelfTestSearchAsync(string query)
    {
        _query.Text = query;
        _debounce.Stop();
        return RunAsync();
    }
}
