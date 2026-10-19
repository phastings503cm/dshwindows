using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App.Views.Settings;

// MARK: - Settings › MCP Servers (10-19 #20)
//
// The servers that give the agent more tools: each one's state (connected with its tools, failed with why, off), a switch,
// "ask before use", Edit, Restart and Remove — and Add, and Import from Claude Code, Claude Desktop and Cursor. Values of
// environment variables and headers live in Windows Credential Manager; the page shows only their names.

public sealed class McpServersPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();

    public McpServersPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 780 };
        page.Children.Add(Ui.Title("MCP Servers"));
        var intro = Ui.Secondary(
            "MCP servers give the agent more tools — GitHub, a database, a browser, your own services. Vision starts each server that is on " +
            "(a program, spoken to over stdio) or reaches it (a Streamable HTTP address), and offers its tools to the chats it applies to, named " +
            "mcp__server__tool. Each tool asks before its first use in a chat unless you switch that off. Environment variables and headers " +
            "are kept in Windows Credential Manager, never in a file.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        page.Children.Add(intro);
        page.Children.Add(Ui.Buttons(
            Ui.Button("Add Server…", () => Edit(null), accent: true),
            Ui.Button("Import from Claude Code, Claude Desktop & Cursor…", Import),
            Ui.Button("Reconnect All", () => _model.Host.StartMcp())));
        page.Children.Add(_list);
        Content = Ui.Scroll(page);
        Rebuild();
        _model.Host.McpChanged += Rebuild;
        Unloaded += (_, _) => _model.Host.McpChanged -= Rebuild;
        Loaded += (_, _) =>
        {
            _model.Host.McpChanged -= Rebuild;
            _model.Host.McpChanged += Rebuild;
        };
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var statuses = _model.Host.Mcp.Statuses;
        _list.Children.Add(Ui.Section(statuses.Count == 0 ? "No servers yet" : $"Servers ({statuses.Count})"));
        if (statuses.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Secondary("Add one, or import the servers Claude Code, Claude Desktop and Cursor already have — " +
                                                    "their settings come along, with their secrets moved into Credential Manager.")));
            return;
        }
        foreach (var status in statuses) _list.Children.Add(Card(status));
    }

    private Border Card(McpServerStatus status)
    {
        var server = status.Config;
        var body = new StackPanel();
        var brush = status.State switch
        {
            McpState.Connected => "SystemFillColorSuccessBrush",
            McpState.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        var enabled = Ui.Toggle(server.Enabled, on => _model.Host.McpServers.SetEnabled(server.Id, on));
        enabled.ToolTip = "Off: the server isn't started and its tools aren't offered.";
        var top = new DockPanel();
        DockPanel.SetDock(enabled, Dock.Right);
        top.Children.Add(enabled);
        var title = Ui.Stack(Orientation.Horizontal, Ui.Glyph(Icons.Connect, 14, "AccentTextFillColorPrimaryBrush"),
            Ui.Text(server.Name, 14, FontWeights.SemiBold, wrap: false),
            Ui.Secondary(status.ServerName is { } n && n != server.Name ? $"  · {n}{(status.ServerVersion is { } v ? " " + v : "")}" : "", 11.5));
        ((FrameworkElement)title.Children[0]).Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(title);
        body.Children.Add(top);
        var state = Ui.Status(status.Headline, brush);
        state.Margin = new Thickness(22, 2, 0, 0);
        state.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(state);
        var target = Ui.Text((server.Transport switch { McpTransportKind.Stdio => "stdio · ", McpTransportKind.Sse => "SSE · ", _ => "HTTP · " }) + server.Target, 11.5, wrap: false);
        target.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        target.Margin = new Thickness(22, 4, 0, 0);
        target.ToolTip = server.Target;
        body.Children.Add(target);
        var scope = server.Project is { } project ? $"Only in {project}" : "Every chat";
        var secrets = server.EnvKeys.Count + server.HeaderKeys.Count > 0
            ? $" · secrets in Credential Manager: {string.Join(", ", server.EnvKeys.Concat(server.HeaderKeys))}"
            : "";
        var where = Ui.Secondary(scope + (server.Source is { } source ? $" · imported from {source}" : "") + secrets, 11.5);
        where.Margin = new Thickness(22, 2, 0, 0);
        body.Children.Add(where);
        var ask = Ui.Check("Ask before each tool's first use in a chat", server.AskBeforeUse, on => _model.Host.McpServers.SetAskBeforeUse(server.Id, on));
        ask.Margin = new Thickness(22, 8, 0, 0);
        body.Children.Add(ask);
        var buttons = Ui.Buttons(
            Ui.LinkButton("Edit…", () => Edit(server)),
            Ui.LinkButton("Restart", () => _ = Task.Run(() => _model.Host.Mcp.RestartAsync(server.Id))),
            Ui.LinkButton("Remove", () =>
            {
                if (Dialog.Confirm("Remove this server?", $"“{server.Name}” and its secrets in Credential Manager are removed from Vision. " +
                                                          "Claude Code, Claude Desktop and Cursor keep their own copies.", "Remove", destructive: true))
                    _model.Host.McpServers.Remove(server.Id);
            }));
        buttons.Margin = new Thickness(16, 6, 0, 0);
        body.Children.Add(buttons);
        if (status.Tools.Count > 0)
        {
            var tools = new StackPanel { Margin = new Thickness(6, 4, 0, 0) };
            foreach (var tool in status.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var name = Ui.Text(McpTool.NameFor(server.Id, tool.Name), 11.5, wrap: false);
                name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var description = Ui.Secondary("  " + TextUtil.Prefix(tool.Description.Replace('\n', ' '), 140) + (tool.ReadOnly ? "  (read-only)" : ""), 11.5);
                description.TextTrimming = TextTrimming.CharacterEllipsis;
                description.TextWrapping = TextWrapping.NoWrap;
                tools.Children.Add(Ui.Stack(Orientation.Horizontal, name, description));
            }
            var expander = new Expander { Header = $"Tools ({status.Tools.Count})", Content = tools, Margin = new Thickness(20, 6, 0, 0) };
            body.Children.Add(expander);
        }
        return Ui.Card(body);
    }

    private void Edit(McpServerConfig? server)
    {
        var dialog = new McpServerDialog(_model, server) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void Import()
    {
        var window = new McpImportWindow(_model) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}

/// <summary>Add or edit one MCP server.</summary>
public sealed class McpServerDialog : Window
{
    /// <summary>Shown in place of a value already in Credential Manager; leaving it keeps the value.</summary>
    public const string Kept = "•••••• (kept)";

    private readonly AppModel _model;
    private readonly McpServerConfig? _existing;
    private readonly TextBox _name = Ui.Field(placeholder: "github");
    private readonly ComboBox _transport = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _command = Ui.Field(placeholder: "npx", mono: true);
    private readonly TextBox _args = Ui.Field(placeholder: "-y\n@modelcontextprotocol/server-github", mono: true);
    private readonly TextBox _cwd = Ui.Field(placeholder: "(the project folder, or your user folder)", mono: true);
    private readonly TextBox _url = Ui.Field(placeholder: "https://mcp.example.com/mcp", mono: true);
    private readonly TextBox _env = Ui.Field(placeholder: "GITHUB_TOKEN=ghp_…", mono: true);
    private readonly TextBox _headers = Ui.Field(placeholder: "Authorization: Bearer …", mono: true);
    private readonly CheckBox _projectOnly = new();
    private readonly TextBlock _error = Ui.Status("", "SystemFillColorCriticalBrush");
    private readonly StackPanel _stdio = new();
    private readonly StackPanel _http = new();

    public McpServerDialog(AppModel model, McpServerConfig? existing)
    {
        _model = model;
        _existing = existing;
        Title = existing is null ? "Add an MCP server" : "Edit " + existing.Name;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        foreach (var (label, kind) in new[] { ("A program (stdio)", McpTransportKind.Stdio), ("An address (Streamable HTTP)", McpTransportKind.Http), ("An address (older SSE)", McpTransportKind.Sse) })
            _transport.Items.Add(new ComboBoxItem { Content = label, Tag = kind });
        _transport.SelectionChanged += (_, _) => ShowTransport();
        foreach (var box in new[] { _args, _env, _headers })
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.NoWrap;
            box.MinHeight = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        var server = existing ?? new McpServerConfig();
        _name.Text = server.Name;
        _transport.SelectedIndex = server.Transport switch { McpTransportKind.Http => 1, McpTransportKind.Sse => 2, _ => 0 };
        _command.Text = server.Command ?? "";
        _args.Text = string.Join("\n", server.Args);
        _cwd.Text = server.Cwd ?? "";
        _url.Text = server.Url ?? "";
        _env.Text = string.Join("\n", server.EnvKeys.Select(k => $"{k}={Kept}"));
        _headers.Text = string.Join("\n", server.HeaderKeys.Select(k => $"{k}: {Kept}"));
        var project = existing?.Project ?? model.Project;
        _projectOnly.Content = project is null ? "Only in one project (open a project first)" : $"Only in the project {project}";
        _projectOnly.IsEnabled = project is not null;
        _projectOnly.IsChecked = existing?.Project is not null;
        _projectOnly.Tag = project;

        Field(_stdio, "Command", "The program to start (npx, uvx, python, a full path…). Windows scripts like npx.cmd run through cmd.exe.", _command);
        Field(_stdio, "Arguments", "One per line. ${NAME} takes the value of an environment variable.", _args);
        Field(_stdio, "Working folder", null, _cwd);
        Field(_stdio, "Environment variables", "NAME=value, one per line. Kept in Credential Manager.", _env);
        Field(_http, "Address", null, _url);
        Field(_http, "Headers", "Name: value, one per line (an Authorization header, say). Kept in Credential Manager.", _headers);

        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Ui.Subtitle(Title));
        var fields = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Field(fields, "Name", "Its tools are named mcp__<name>__<tool>.", _name);
        Field(fields, "Kind", null, _transport);
        fields.Children.Add(_stdio);
        fields.Children.Add(_http);
        _projectOnly.Margin = new Thickness(0, 8, 0, 0);
        fields.Children.Add(_projectOnly);
        page.Children.Add(fields);
        _error.Margin = new Thickness(0, 10, 0, 0);
        _error.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(_error);
        var buttons = Ui.Buttons(Ui.Button("Cancel", Close), Ui.Button(existing is null ? "Add" : "Save", Save, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(buttons);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ShowTransport();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    private static void Field(Panel panel, string label, string? note, FrameworkElement control)
    {
        var heading = Ui.Text(label, 12.5, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 10, 0, 3);
        panel.Children.Add(heading);
        if (note is not null)
        {
            var hint = Ui.Secondary(note, 11.5);
            hint.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(hint);
        }
        panel.Children.Add(control);
    }

    private McpTransportKind Transport => (_transport.SelectedItem as ComboBoxItem)?.Tag is McpTransportKind kind ? kind : McpTransportKind.Stdio;

    private void ShowTransport()
    {
        _stdio.Visibility = Transport == McpTransportKind.Stdio ? Visibility.Visible : Visibility.Collapsed;
        _http.Visibility = Transport == McpTransportKind.Stdio ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"NAME=value" (or "Name: value") lines; a value left as <see cref="Kept"/> keeps the stored one.</summary>
    private static Dictionary<string, string>? Pairs(string text, char separator, Func<string, string?> stored, out string? error)
    {
        error = null;
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var at = line.IndexOf(separator);
            if (at <= 0)
            {
                error = $"“{TextUtil.Prefix(line, 40)}” needs a name, then {(separator == '=' ? "=" : ":")} and its value.";
                return null;
            }
            var name = line[..at].Trim();
            var value = line[(at + 1)..].Trim();
            output[name] = value == Kept ? stored(name) ?? "" : value;
        }
        return output;
    }

    private void Save()
    {
        var store = _model.Host.McpServers;
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _error.Text = "Give the server a name.";
            return;
        }
        var server = (_existing ?? new McpServerConfig()).Copy();
        server.Name = name;
        server.Id = _existing?.Id ?? store.UniqueId(name);
        server.Transport = Transport;
        var stored = _existing is null ? new Dictionary<string, string>() : McpServerStore.EnvOf(_existing);
        var storedHeaders = _existing is null ? new Dictionary<string, string>() : McpServerStore.HeadersOf(_existing);
        Dictionary<string, string>? env = new(), headers = new();
        if (Transport == McpTransportKind.Stdio)
        {
            if (_command.Text.Trim().Length == 0)
            {
                _error.Text = "Say which program to start.";
                return;
            }
            server.Command = _command.Text.Trim();
            server.Args = _args.Text.Replace("\r", "").Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            server.Cwd = _cwd.Text.Trim() is { Length: > 0 } cwd ? cwd : null;
            server.Url = null;
            env = Pairs(_env.Text, '=', k => stored.GetValueOrDefault(k), out var problem);
            if (env is null)
            {
                _error.Text = problem;
                return;
            }
        }
        else
        {
            if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                _error.Text = "The address must start with https:// (or http://).";
                return;
            }
            server.Url = url.ToString();
            server.Command = null;
            server.Args = [];
            headers = Pairs(_headers.Text, ':', k => storedHeaders.GetValueOrDefault(k), out var problem);
            if (headers is null)
            {
                _error.Text = problem;
                return;
            }
        }
        server.Project = _projectOnly.IsChecked == true ? _projectOnly.Tag as string : null;
        store.Save(server, env, headers);
        Close();
    }
}

/// <summary>Import the MCP servers Claude Code, Claude Desktop and Cursor have.</summary>
public sealed class McpImportWindow : Window
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = Ui.Secondary("");
    private readonly List<(CheckBox Box, McpCandidate Candidate)> _rows = [];

    /// <summary>Tests and the self-test: where to look instead of this PC's profile.</summary>
    public static ExternalLocations? LocationsOverride { get; set; }

    public McpImportWindow(AppModel model)
    {
        _model = model;
        Title = "Import MCP servers";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        var page = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = Ui.Stack(Ui.Subtitle("Import MCP servers"),
            Ui.Secondary("The servers Claude Code (~/.claude.json and projects' .mcp.json), Claude Desktop and Cursor (~/.cursor/mcp.json) have. " +
                         "Their environment variables and headers go into Windows Credential Manager; only their names are shown here."));
        ((FrameworkElement)header.Children[1]).Margin = new Thickness(0, 4, 0, 10);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        var buttons = Ui.Buttons(Ui.Button("Cancel", Close), Ui.Button("Import Selected", Import, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        page.Children.Add(buttons);
        _summary.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_summary, Dock.Bottom);
        page.Children.Add(_summary);
        page.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = page;
        Scan();
    }

    private void Scan()
    {
        var projects = _model.Config.RecentProjects.Concat(_model.Project is { } p ? [p] : []).ToList();
        var candidates = McpImport.Scan(LocationsOverride ?? ExternalLocations.Standard, projects);
        var existing = _model.Host.McpServers.Servers;
        _list.Children.Clear();
        _rows.Clear();
        if (candidates.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Secondary("No MCP servers found in Claude Code, Claude Desktop or Cursor on this PC.")));
            return;
        }
        foreach (var group in candidates.GroupBy(c => c.Server.Source))
        {
            _list.Children.Add(Ui.Section(group.Key ?? "Other"));
            foreach (var candidate in group)
            {
                var server = candidate.Server;
                var already = existing.Any(s => s.Name == server.Name && s.SourceFile == server.SourceFile
                                                && string.Equals(s.Project, server.Project, StringComparison.OrdinalIgnoreCase));
                var box = new CheckBox { IsChecked = !already, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
                var name = Ui.Text(server.Name + (already ? "  (already imported — importing updates it)" : ""), 13, FontWeights.SemiBold);
                var target = Ui.Text(server.Target, 11.5, wrap: false);
                target.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var keys = candidate.Env.Keys.Concat(candidate.Headers.Keys).ToList();
                var detail = Ui.Secondary((server.Project is { } project ? $"Only in {project}" : "Every chat") +
                                          (keys.Count > 0 ? $" · secrets: {string.Join(", ", keys)}" : "") + (server.Enabled ? "" : " · switched off there"), 11.5);
                var row = new DockPanel();
                DockPanel.SetDock(box, Dock.Left);
                row.Children.Add(box);
                row.Children.Add(Ui.Stack(name, target, detail));
                _list.Children.Add(Ui.Card(row, new Thickness(12, 8, 12, 8)));
                _rows.Add((box, candidate));
            }
        }
        _summary.Text = $"Found {candidates.Count} {(candidates.Count == 1 ? "server" : "servers")}.";
    }

    private void Import()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Candidate).ToList();
        if (chosen.Count == 0)
        {
            Close();
            return;
        }
        var (added, replaced) = McpImport.Import(_model.Host.McpServers, chosen);
        Dialog.Info("MCP servers imported",
            $"Added {added} and updated {replaced}. Their environment values and headers are in Windows Credential Manager now. " +
            "Each one connects in a moment; Settings › MCP Servers shows how it went.");
        Close();
    }
}
