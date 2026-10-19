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
// The servers that give the agent more tools: each one's state (connected, failed with why, off), a switch, when its calls
// may run without asking, its tools (each can be kept from the model), Edit…, Reconnect and Remove… — and Add Server… and
// Import… from Claude Code, Claude Desktop and Cursor. Values of environment variables and headers live in Windows Credential
// Manager; the page shows only their names. The words are Vision for macOS's (MCPSettings.swift).

public sealed class McpServersPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();
    private readonly HashSet<string> _showTools = [];

    public McpServersPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 780 };
        page.Children.Add(Ui.Title("MCP Servers"));
        var intro = Ui.Secondary(
            "MCP servers give the agent more tools — GitHub, a database, your docs, a browser. Each server's tools are offered as " +
            "mcp__<server>__<tool>, and every call asks you first unless you trust that server. Keys and tokens go to Windows Credential Manager.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        page.Children.Add(intro);
        var import = Ui.Button("Import…", Import, tooltip: "Bring in the servers Claude Code, Claude Desktop or Cursor already have");
        page.Children.Add(Ui.Buttons(Ui.Button("Add Server…", () => Edit(null), accent: true), import));
        page.Children.Add(_list);
        Content = Ui.Scroll(page);
        Rebuild();
        Loaded += (_, _) =>
        {
            _model.Host.McpChanged -= Rebuild;
            _model.Host.McpChanged += Rebuild;
        };
        Unloaded += (_, _) => _model.Host.McpChanged -= Rebuild;
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var statuses = _model.Host.Mcp.Statuses;
        if (statuses.Count == 0)
        {
            _list.Children.Add(Ui.Section("No MCP servers yet"));
            _list.Children.Add(Ui.Card(Ui.Stack(
                Ui.Secondary("Add one, or import the servers you already set up for Claude Code, Claude Desktop or Cursor."),
                Ui.Buttons(Ui.LinkButton("Import…", Import)))));
            return;
        }
        _list.Children.Add(Ui.Section($"Servers ({statuses.Count})"));
        foreach (var status in statuses) _list.Children.Add(Card(status));
    }

    private Border Card(McpServerStatus status)
    {
        var server = status.Config;
        var store = _model.Host.McpServers;
        var body = new StackPanel();
        var enabled = Ui.Toggle(server.Enabled, on => store.SetEnabled(server.Id, on));
        enabled.ToolTip = "Off: the server isn't started and its tools aren't offered.";
        var top = new DockPanel();
        DockPanel.SetDock(enabled, Dock.Right);
        top.Children.Add(enabled);
        var brush = status.State switch
        {
            McpState.Connected => "SystemFillColorSuccessBrush",
            McpState.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        // A failure's reason is on its own line below; beside the name it is just "Failed".
        var state = Ui.Status("  " + (status.State == McpState.Failed ? "Failed" : status.Headline), brush);
        state.TextWrapping = TextWrapping.NoWrap;
        state.TextTrimming = TextTrimming.CharacterEllipsis;
        state.ToolTip = status.Error;
        var title = Ui.Stack(Orientation.Horizontal, Ui.Glyph(Icons.Connect, 14, "AccentTextFillColorPrimaryBrush"),
            Ui.Text(server.Name, 14, FontWeights.SemiBold, wrap: false), state);
        ((FrameworkElement)title.Children[0]).Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(title);
        body.Children.Add(top);
        var target = Ui.Text((server.Transport == McpTransportKind.Stdio ? "Program · " : "URL · ") + server.Summary, 11.5, wrap: false);
        target.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        target.Margin = new Thickness(22, 4, 0, 0);
        target.ToolTip = server.Summary;
        body.Children.Add(target);
        var secrets = server.EnvNames.Count + server.HeaderNames.Count;
        var where = Ui.Secondary((server.Project is { } project ? $"Only in {project}" : "Every chat") +
                                 (server.Source is { } source ? $" · from {source}" : "") +
                                 (secrets > 0 ? $" · {string.Join(", ", server.EnvNames.Concat(server.HeaderNames))} in Credential Manager" : ""), 11.5);
        where.Margin = new Thickness(22, 2, 0, 0);
        body.Children.Add(where);
        if (status.State == McpState.Failed && status.Error is { } error)
        {
            var why = Ui.Status(error, "SystemFillColorCriticalBrush");
            why.Margin = new Thickness(22, 4, 0, 0);
            body.Children.Add(why);
        }
        if (server.Note is { } note)
        {
            var noteText = Ui.Status(note, "SystemFillColorCautionBrush");
            noteText.Margin = new Thickness(22, 4, 0, 0);
            body.Children.Add(noteText);
        }
        var trust = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var level in Enum.GetValues<McpTrust>()) trust.Items.Add(new ComboBoxItem { Content = level.Label(), Tag = level });
        trust.SelectedIndex = (int)server.Trust;
        trust.SelectionChanged += (_, _) =>
        {
            if ((trust.SelectedItem as ComboBoxItem)?.Tag is McpTrust chosen && chosen != server.Trust) store.SetTrust(server.Id, chosen);
        };
        var controls = new WrapPanel { Margin = new Thickness(22, 8, 0, 0) };
        controls.Children.Add(Ui.Stack(Orientation.Horizontal, WithMargin(Ui.Secondary("Calls"), new Thickness(0, 0, 8, 0)), trust));
        if (status.Tools.Count > 0)
        {
            var shown = _showTools.Contains(server.Id);
            controls.Children.Add(Ui.LinkButton(shown ? "Hide tools" : $"{status.Tools.Count} tool{(status.Tools.Count == 1 ? "" : "s")}", () =>
            {
                if (!_showTools.Remove(server.Id)) _showTools.Add(server.Id);
                Rebuild();
            }));
        }
        controls.Children.Add(Ui.LinkButton("Edit…", () => Edit(server)));
        var reconnect = Ui.LinkButton("Reconnect", () => _ = Task.Run(() => _model.Host.Mcp.RestartAsync(server.Id)));
        reconnect.IsEnabled = server.Enabled;
        controls.Children.Add(reconnect);
        controls.Children.Add(Ui.LinkButton("Remove…", () =>
        {
            if (Dialog.Confirm($"Remove “{server.Name}”?", "Its tools leave every chat, and its keys are removed from Windows Credential Manager.", "Remove", destructive: true))
                store.Remove(server.Id);
        }));
        foreach (var child in controls.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(controls);
        if (_showTools.Contains(server.Id) && status.Tools.Count > 0)
        {
            var tools = new StackPanel { Margin = new Thickness(22, 6, 0, 0) };
            foreach (var tool in status.Tools)
            {
                var box = new CheckBox { IsChecked = !server.DisabledTools.Contains(tool.Name), Margin = new Thickness(0, 2, 0, 2) };
                var name = Ui.Text(tool.Name, 11.5, wrap: false);
                name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var description = Ui.Secondary("  " + (tool.ReadOnly ? "read-only · " : "") + TextUtil.Prefix(tool.Description.Replace('\n', ' '), 120), 11.5);
                description.TextWrapping = TextWrapping.NoWrap;
                description.TextTrimming = TextTrimming.CharacterEllipsis;
                box.Content = Ui.Stack(Orientation.Horizontal, name, description);
                box.ToolTip = $"{McpNames.ToolName(server.Name, tool.Name)}\n{tool.Description}";
                box.Click += (_, _) => store.SetToolEnabled(server.Id, tool.Name, box.IsChecked == true);
                tools.Children.Add(box);
            }
            body.Children.Add(tools);
        }
        return Ui.Card(body);
    }

    private static T WithMargin<T>(T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private void Edit(McpServerConfig? server) => new McpServerDialog(_model, server) { Owner = Window.GetWindow(this) }.ShowDialog();

    private void Import() => new McpImportWindow(_model) { Owner = Window.GetWindow(this) }.ShowDialog();
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
    private readonly ComboBox _trust = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _command = Ui.Field(placeholder: "npx", mono: true);
    private readonly TextBox _args = Ui.Field(placeholder: "-y\n@modelcontextprotocol/server-github", mono: true);
    private readonly TextBox _cwd = Ui.Field(placeholder: "your user folder (or the project's)", mono: true);
    private readonly TextBox _url = Ui.Field(placeholder: "https://example.com/mcp", mono: true);
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
        Title = existing is null ? "Add MCP Server" : "Edit " + existing.Name;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _transport.Items.Add(new ComboBoxItem { Content = "Program (stdio)", Tag = McpTransportKind.Stdio });
        _transport.Items.Add(new ComboBoxItem { Content = "URL (Streamable HTTP)", Tag = McpTransportKind.Http });
        _transport.SelectionChanged += (_, _) => ShowTransport();
        foreach (var level in Enum.GetValues<McpTrust>()) _trust.Items.Add(new ComboBoxItem { Content = level.Label(), Tag = level });
        foreach (var box in new[] { _args, _env, _headers })
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.NoWrap;
            box.MinHeight = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        var server = existing ?? new McpServerConfig();
        _name.Text = server.Name;
        _transport.SelectedIndex = server.Transport == McpTransportKind.Http ? 1 : 0;
        _trust.SelectedIndex = (int)server.Trust;
        _command.Text = server.Command ?? "";
        _args.Text = string.Join("\n", server.Args);
        _cwd.Text = server.Cwd ?? "";
        _url.Text = server.Url ?? "";
        _env.Text = string.Join("\n", server.EnvNames.Select(k => $"{k}={Kept}"));
        _headers.Text = string.Join("\n", server.HeaderNames.Select(k => $"{k}: {Kept}"));
        var project = existing?.Project ?? model.Project;
        _projectOnly.Content = project is null ? "Only in one project (open a project first)" : $"Only in the project {project}";
        _projectOnly.IsEnabled = project is not null;
        _projectOnly.IsChecked = existing?.Project is not null;
        _projectOnly.Tag = project;

        Field(_stdio, "Program", "npx, uvx, python, a full path… Windows scripts like npx.cmd run through cmd.exe.", _command);
        Field(_stdio, "Arguments — one per line", null, _args);
        Field(_stdio, "Starts in (optional)", null, _cwd);
        Field(_stdio, "Environment", "NAME=value, one per line.", _env);
        Field(_http, "Address", "Streamable HTTP; a server that only speaks the older SSE transport is reached through it.", _url);
        Field(_http, "Headers", "Name: value, one per line (an Authorization header, say).", _headers);

        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Ui.Subtitle(Title));
        var fields = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Field(fields, "Name", "Its tools are named mcp__<name>__<tool>.", _name);
        Field(fields, "Runs as", null, _transport);
        fields.Children.Add(_stdio);
        fields.Children.Add(_http);
        Field(fields, "Tool calls", null, _trust);
        _projectOnly.Margin = new Thickness(0, 10, 0, 0);
        fields.Children.Add(_projectOnly);
        page.Children.Add(fields);
        var secrets = Ui.Secondary("Values go to Windows Credential Manager, never into the settings file. ${NAME} is filled in from your environment.", 11.5);
        secrets.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(secrets);
        _error.Margin = new Thickness(0, 10, 0, 0);
        _error.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(_error);
        var buttons = Ui.Buttons(Ui.Button("Cancel", Close), Ui.Button("Save", Save, accent: true));
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
    private static Dictionary<string, string>? Pairs(string text, char separator, IReadOnlyDictionary<string, string> stored, out string? error)
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
                error = $"“{TextUtil.Prefix(line, 40)}” needs a name, then {separator} and its value.";
                return null;
            }
            var name = line[..at].Trim();
            var value = line[(at + 1)..].Trim();
            output[name] = value == Kept ? stored.GetValueOrDefault(name) ?? "" : value;
        }
        return output;
    }

    private void Save()
    {
        var store = _model.Host.McpServers;
        var draft = (_existing ?? new McpServerConfig()).Copy();
        draft.Name = McpNames.Sanitize(_name.Text.Trim()).Length == 0 ? _name.Text.Trim() : store.UniqueName(_name.Text.Trim(), _existing?.Id);
        draft.Transport = Transport;
        draft.Trust = (_trust.SelectedItem as ComboBoxItem)?.Tag is McpTrust trust ? trust : McpTrust.Ask;
        IReadOnlyDictionary<string, string> storedEnv = _existing is null ? new Dictionary<string, string>() : McpServerStore.EnvOf(_existing);
        IReadOnlyDictionary<string, string> storedHeaders = _existing is null ? new Dictionary<string, string>() : McpServerStore.HeadersOf(_existing);
        Dictionary<string, string>? env = new(), headers = new();
        string? problem;
        if (Transport == McpTransportKind.Stdio)
        {
            draft.Command = _command.Text.Trim();
            draft.Args = _args.Text.Replace("\r", "").Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            draft.Cwd = _cwd.Text.Trim() is { Length: > 0 } cwd ? cwd : null;
            draft.Url = null;
            env = Pairs(_env.Text, '=', storedEnv, out problem);
        }
        else
        {
            draft.Url = _url.Text.Trim();
            draft.Command = null;
            draft.Args = [];
            headers = Pairs(_headers.Text, ':', storedHeaders, out problem);
        }
        problem ??= draft.Problem;
        if (problem is not null || env is null || headers is null)
        {
            _error.Text = problem ?? "";
            return;
        }
        draft.Project = _projectOnly.IsChecked == true ? _projectOnly.Tag as string : null;
        store.Save(draft, env, headers);
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
    private readonly Button _import;

    /// <summary>Tests and the self-test: where to look instead of this PC's profile.</summary>
    public static ExternalLocations? LocationsOverride { get; set; }

    public McpImportWindow(AppModel model)
    {
        _model = model;
        Title = "Import MCP Servers";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        var page = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = Ui.Stack(Ui.Subtitle("Import MCP Servers"),
            Ui.Secondary("The servers Claude Code (~/.claude.json and this project's .mcp.json), Claude Desktop and Cursor (~/.cursor/mcp.json) " +
                         "have configured. Nothing is started until you import; their keys go to Windows Credential Manager."));
        ((FrameworkElement)header.Children[1]).Margin = new Thickness(0, 4, 0, 10);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        _import = Ui.Button("Import", Import, accent: true);
        var buttons = Ui.Buttons(Ui.Button("Cancel", Close), _import);
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
        var candidates = McpImport.Scan(LocationsOverride ?? ExternalLocations.Standard, _model.Project, _model.Config.RecentProjects);
        var existing = _model.Host.McpServers.Servers;
        _list.Children.Clear();
        _rows.Clear();
        if (candidates.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Secondary("None found — Claude Code, Claude Desktop and Cursor have no MCP servers set up on this PC.")));
            UpdateCount();
            return;
        }
        foreach (var group in candidates.GroupBy(c => c.Server.Source ?? ""))
        {
            _list.Children.Add(Ui.Section(group.Key));
            foreach (var candidate in group)
            {
                var server = candidate.Server;
                var known = candidate.IsKnown(existing);
                var box = new CheckBox { IsChecked = !known && candidate.Preselected, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
                box.Click += (_, _) => UpdateCount();
                var tags = new List<string>();
                if (known) tags.Add("already in Vision");
                // Values written as ${NAME} are filled in at start, not kept.
                var secrets = candidate.Env.Values.Concat(candidate.Headers.Values).Count(v => !v.Contains("${", StringComparison.Ordinal));
                if (secrets > 0) tags.Add($"{secrets} value{(secrets == 1 ? "" : "s")} → Credential Manager");
                if (candidate.FromRepository) tags.Add("project's files");
                if (!server.Enabled) tags.Add("switched off there");
                if (server.Project is { } project) tags.Add($"only in {System.IO.Path.GetFileName(project.TrimEnd('\\', '/'))}");
                var name = Ui.Text(server.Name, 13, FontWeights.SemiBold);
                // The whole command line (an argument with spaces in quotes), so what it runs can be read before importing.
                var commandLine = server.Transport == McpTransportKind.Stdio
                    ? string.Join(" ", new[] { server.Command ?? "" }.Concat(server.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                    : server.Url ?? "";
                var target = new TextBox
                {
                    Text = commandLine + (server.Cwd is { } cwd ? $"\n(in {cwd})" : ""), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 11.5, Padding = new Thickness(0),
                };
                target.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
                target.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
                var detail = Ui.Secondary(string.Join(" · ", tags), 11.5);
                var stack = Ui.Stack(name, target, tags.Count > 0 ? detail : null);
                foreach (var warning in candidate.Warnings)
                {
                    var line = Ui.Status(warning, "SystemFillColorCautionBrush");
                    line.Margin = new Thickness(0, 3, 0, 0);
                    stack.Children.Add(line);
                }
                var row = new DockPanel();
                DockPanel.SetDock(box, Dock.Left);
                row.Children.Add(box);
                row.Children.Add(stack);
                _list.Children.Add(Ui.Card(row, new Thickness(12, 8, 12, 8)));
                _rows.Add((box, candidate));
            }
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        var chosen = _rows.Count(r => r.Box.IsChecked == true);
        _import.Content = $"Import {chosen}";
        _import.IsEnabled = chosen > 0;
        _summary.Text = _rows.Count == 0 ? "" : $"Found {_rows.Count} server{(_rows.Count == 1 ? "" : "s")}.";
    }

    private void Import()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Candidate).ToList();
        if (chosen.Count == 0) return;
        var imported = new List<McpServerConfig>();
        McpImport.Import(_model.Host.McpServers, chosen, imported);
        Dialog.Info("MCP servers imported", McpImport.Summary(imported));
        Close();
    }
}
