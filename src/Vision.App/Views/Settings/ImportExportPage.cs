using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App.Views.Settings;

/// <summary>Settings › Import &amp; Export: OpenClaw on this PC both ways — preview what it holds and bring in what you
/// tick (skills, memory, keys and passwords, model servers), and send Vision's skills, memory, keys (only after a
/// confirmation) and model servers back — plus the other ways in (Claude Code and Cursor, skill folders, Guardian).
/// Secret values are never shown or logged: names, a short hint and where each was found. The Mac's page has the same
/// sections and words.</summary>
public sealed class ImportExportPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _openClaw = new();
    private readonly StackPanel _export = new();

    private IReadOnlyList<OpenClawInstall> _installs = [];
    private OpenClawInstall? _chosen;
    private OpenClawBundle? _bundle;
    private bool _looking;
    private bool _reading;
    private bool _working;

    private bool _exportSkills = true;
    private bool _exportMemory = true;
    private bool _exportVault;
    private bool _exportServers;
    private OpenClawConflict _conflict = OpenClawConflict.Skip;
    private OpenClawExportResult? _result;

    public ImportExportPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Import & Export"));
        var intro = Ui.Secondary("Bring skills, memory, keys and model servers in from the tools you already use on this PC, and send Vision's back to OpenClaw.");
        intro.Margin = new Thickness(0, 6, 0, 4);
        page.Children.Add(intro);

        page.Children.Add(Ui.Section("OpenClaw on this PC"));
        page.Children.Add(_openClaw);
        page.Children.Add(Ui.Section("Export to OpenClaw"));
        page.Children.Add(_export);

        page.Children.Add(Ui.Section("Other ways in"));
        page.Children.Add(Ui.Row("Claude Code and Cursor", "Skills, commands, rules, subagents, CLAUDE.md and project memory from this PC.",
            Ui.Button("Bring in…", model.ShowExternalImport), Icons.Import));
        page.Children.Add(Ui.Row("A skill folder, zip or GitHub link", "Checked and shown before anything is copied (Settings › Skills).",
            Ui.Button("Import skills…", () => model.ShowSettings(SettingsTab.Skills, SkillsAction.Import)), Icons.Education));
        page.Children.Add(Ui.Row("Guardian", "Your account's skills, chats and code projects, both ways, and the secrets you opted in for Vision.",
            Ui.Button("Guardian settings…", model.ShowGuardian), Icons.Shield));
        page.Children.Add(Ui.Row("Another computer", "OpenClaw on a server or another PC, read over SSH like an SFTP client — nothing there is changed or run.",
            Ui.Button("Bring in over SSH…", model.ShowOpenClawImport), Icons.Connect));
        Content = Ui.Scroll(page);

        RenderOpenClaw();
        RenderExport();
        if (SelfTest.Current is null) _ = LookAsync();
    }

    // MARK: Where OpenClaw is

    private async Task LookAsync()
    {
        _looking = true;
        RenderOpenClaw();
        try
        {
            _installs = await Task.Run(() => OpenClawScanner.FindAsync(new LocalFileSource()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _installs = [];
        }
        finally
        {
            _looking = false;
        }
        if (_chosen is null || !_installs.Contains(_chosen)) _chosen = _installs.FirstOrDefault();
        _bundle = null;
        RenderOpenClaw();
        RenderExport();
        if (_chosen is not null) await PreviewAsync();
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose OpenClaw's state folder (the one holding openclaw.json), or a workspace" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home)) dialog.InitialDirectory = home;
        if (dialog.ShowDialog(Ui.Owner(this)) != true) return;
        _ = UseFolderAsync(dialog.FolderName);
    }

    private async Task UseFolderAsync(string folder)
    {
        var install = await OpenClawScanner.InspectAsync(new LocalFileSource(), folder, "chosen");
        if (install is null)
        {
            Dialog.Info("That isn't OpenClaw", "That folder doesn't look like OpenClaw: no openclaw.json, and no agents\\, workspace\\ or skills\\ with memory.");
            return;
        }
        if (!_installs.Contains(install)) _installs = [.. _installs, install];
        _chosen = install;
        await PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        if (_chosen is not { } install) return;
        _reading = true;
        RenderOpenClaw();
        try
        {
            _bundle = await Task.Run(() => OpenClawReader.ReadAsync(new LocalFileSource(), install));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _bundle = null;
        }
        finally
        {
            _reading = false;
        }
        RenderOpenClaw();
    }

    /// <summary>"12 skills · 41 memory notes · 6 keys and passwords · 2 model servers found in C:\Users\pat\.openclaw".</summary>
    internal static string Counts(OpenClawBundle bundle)
    {
        static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        return string.Join(" · ", N(bundle.Skills.Count(s => !s.Shadowed), "skill", "skills"), N(bundle.NoteCount, "memory note", "memory notes"),
                   N(bundle.Credentials.Count, "key or password", "keys and passwords"), N(bundle.ModelServers.Count(s => s.Compatible), "model server", "model servers"))
               + $" found in {bundle.Install.Title}";
    }

    private void RenderOpenClaw()
    {
        _openClaw.Children.Clear();
        var panel = new StackPanel();
        if (_looking)
        {
            panel.Children.Add(Ui.Buttons(new Spinner { Width = 14, Height = 14 }, Ui.Secondary("Looking for OpenClaw in your home folder…")));
        }
        else if (_installs.Count == 0)
        {
            panel.Children.Add(Ui.Secondary("No OpenClaw found in your home folder (.openclaw or a profile like .openclaw-work). If it lives somewhere else, choose its folder."));
        }
        else
        {
            var picker = new ComboBox { MinWidth = 360 };
            foreach (var install in _installs)
                picker.Items.Add(new ComboBoxItem { Content = install.Title + (install.ConfigPath is null ? " (no config file)" : ""), Tag = install, IsSelected = install == _chosen });
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is ComboBoxItem { Tag: OpenClawInstall install } && install != _chosen)
                {
                    _chosen = install;
                    _bundle = null;
                    _result = null;
                    _ = PreviewAsync();
                    RenderExport();
                }
            };
            panel.Children.Add(picker);
        }
        var buttons = Ui.Buttons(
            Ui.Button("Choose folder…", ChooseFolder),
            Ui.Button("Look again", () => _ = LookAsync()),
            Ui.Button("Preview and import…", () => _model.ShowOpenClawImport(), accent: true));
        buttons.Margin = new Thickness(0, 10, 0, 0);
        ((FrameworkElement)buttons.Children[2]).IsEnabled = !_looking;
        panel.Children.Add(buttons);

        if (_reading) panel.Children.Add(Ui.Secondary("Reading what it holds…"));
        else if (_bundle is { } bundle)
        {
            var counts = Ui.Text(Counts(bundle), 13.5, FontWeights.SemiBold);
            counts.Margin = new Thickness(0, 12, 0, 4);
            panel.Children.Add(counts);
            foreach (var problem in bundle.Problems.Take(4)) panel.Children.Add(Ui.Status(problem, "SystemFillColorCautionBrush"));
            var keys = bundle.Credentials.Take(6).Select(c => $"{c.Name} · {c.Secret.Hint} · {c.Origin}").ToList();
            if (keys.Count > 0)
            {
                var list = Ui.Secondary("Keys and passwords (values are never shown): " + string.Join("; ", keys) + (bundle.Credentials.Count > 6 ? $"; and {bundle.Credentials.Count - 6} more" : ""));
                list.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(list);
            }
        }
        var footer = Ui.Secondary("Nothing in OpenClaw is changed by reading it, and nothing is run. The import shows every item with a tick box and lets you choose what happens when Vision already has something of that name.");
        footer.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(footer);
        _openClaw.Children.Add(Ui.Card(panel));
    }

    // MARK: Export

    private IReadOnlyList<Skill> UserSkills() =>
        SkillCatalog.LoadAll(null, _model.Host.SkillLocations)
            .Where(s => s.Origin == SkillOrigin.Vision && s.Scope == SkillScope.User && !s.Shadowed && s.Kind == SkillKind.Skill)
            .ToList();

    private int MemoryCount() => _model.Host.Memory.All().Count(n => !n.Source.StartsWith("import:openclaw", StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> VaultNames() => _model.Host.Vault.All.Where(e => !_model.Host.Vault.IsSessionOnly(e.Id)).Select(e => e.Name).ToList();

    /// <summary>The routes OpenClaw can use: OpenAI-compatible ones that are on (not Bedrock, not Guardian's — those use your
    /// Guardian sign-in, which stays in Vision).</summary>
    private IReadOnlyList<ProviderProfile> ExportableRoutes() =>
        _model.Config.Providers.Where(p => p.IsEnabled && p.Kind != ProviderKind.Bedrock && !GuardianRoutes.IsGuardian(p)).ToList();

    private string EnvPath => Path.Combine(_chosen?.StateDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw"), ".env");

    private void RenderExport()
    {
        _export.Children.Clear();
        var panel = new StackPanel();
        var skills = UserSkills().Count;
        var notes = MemoryCount();
        var keys = VaultNames().Count;
        var routes = ExportableRoutes().Count;
        CheckBox Tick(string text, bool on, bool enabled, Action<bool> changed)
        {
            var box = Ui.Check(text, on && enabled, changed);
            box.IsEnabled = enabled;
            box.Margin = new Thickness(0, 3, 0, 3);
            return box;
        }
        panel.Children.Add(Tick($"Skills — your {GuardianWords.Count(skills, "skill", "skills")}, into OpenClaw's skills folder", _exportSkills, skills > 0, on => _exportSkills = on));
        panel.Children.Add(Tick($"Memory — {GuardianWords.Count(notes, "memory note", "memory notes")}, into its workspace's MEMORY.md", _exportMemory, notes > 0, on => _exportMemory = on));
        panel.Children.Add(Tick($"Keys and passwords — the {keys} in the vault, into OpenClaw's .env (asks first)", _exportVault, keys > 0, on => _exportVault = on));
        panel.Children.Add(Tick($"Model servers — {GuardianWords.Count(routes, "route", "routes")} from AI Models, into openclaw.json", _exportServers, routes > 0, on => _exportServers = on));
        var picker = OpenClawImportWindow.ConflictPicker(_conflict, choice => _conflict = choice);
        var clash = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(picker, Dock.Right);
        clash.Children.Add(picker);
        var clashLabel = Ui.Text("If OpenClaw already has it", 13);
        clashLabel.VerticalAlignment = VerticalAlignment.Center;
        clash.Children.Add(clashLabel);
        panel.Children.Add(clash);

        var export = Ui.Button("Export to OpenClaw…", () => _ = ExportAsync(), accent: true);
        export.IsEnabled = _chosen is not null && !_working;
        export.HorizontalAlignment = HorizontalAlignment.Left;
        export.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(export);
        if (_chosen is null) panel.Children.Add(Ui.Secondary("Choose an OpenClaw above first."));

        if (_result is { } result)
        {
            var sent = Ui.Status($"Sent {result.Summary}.", "SystemFillColorSuccessBrush");
            sent.Margin = new Thickness(0, 10, 0, 2);
            panel.Children.Add(sent);
            var lines = result.SkillsSkipped.Select(p => $"Skill {p.Key}: {p.Value}")
                .Concat(result.SecretsSkipped.Select(p => $"{p.Key}: {p.Value}"))
                .Concat(result.ServersSkipped.Select(p => $"{p.Key}: {p.Value}"))
                .Concat(result.Problems)
                .ToList();
            if (result.ServersSnippet is { } snippet)
                lines.Insert(0, $"openclaw.json has comments, so Vision didn't rewrite it: the model servers are in {snippet} — copy them under models.providers.");
            foreach (var line in lines.Take(30)) panel.Children.Add(Ui.Secondary(line));
        }
        var footer = Ui.Secondary("Memory goes in as a marked “From Vision” block, so exporting again updates it; what Vision brought in from OpenClaw isn't sent back. " +
                                  "Keys are written only after you confirm, and never shown or logged.");
        footer.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(footer);
        _export.Children.Add(Ui.Card(panel));
    }

    private async Task ExportAsync()
    {
        if (_chosen is not { } install) return;
        var vaultNames = _exportVault ? VaultNames() : [];
        var routes = _exportServers ? ExportableRoutes() : [];
        var routeKeys = routes.Select(r => _model.Config.ApiKey(r)).Count(k => k.Length > 0);
        var confirmed = false;
        if (vaultNames.Count + routeKeys > 0)
        {
            var count = vaultNames.Count + routeKeys;
            var answer = Dialog.Ask("Export keys and passwords to OpenClaw?",
                $"They are written in plain text to {EnvPath}, readable by your Windows account — OpenClaw and every skill it runs can read them. " +
                "Vision never shows or logs the values." + (routeKeys > 0 ? $" ({GuardianWords.Count(routeKeys, "model server's key", "model servers' keys")} included.)" : ""),
                new Dialog.Choice("Cancel", IsCancel: true),
                new Dialog.Choice("Export Without Them"),
                new Dialog.Choice($"Export {GuardianWords.Count(count, "Key or Password", "Keys and Passwords")}", IsDefault: true, IsDestructive: true));
            if (answer is < 1) return;
            confirmed = answer == 2;
        }
        _working = true;
        RenderExport();
        try
        {
            var selection = new OpenClawExportSelection
            {
                Skills = _exportSkills ? UserSkills() : [],
                Memory = _exportMemory,
                Vault = confirmed ? vaultNames.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(),
                VaultConfirmed = confirmed,
                Servers = routes.Select(r => new OpenClawExportServer(r.Name, r.BaseUrl, r.Model, r.ContextWindow,
                    confirmed && _model.Config.ApiKey(r) is { Length: > 0 } key ? key : null)).ToList(),
                Conflict = _conflict,
            };
            var memory = _model.Host.Memory;
            var vault = _model.Host.Vault;
            var workspace = await OpenClawExporter.WorkspaceAsync(install);
            _result = await Task.Run(() => OpenClawExporter.Export(selection, install, workspace, memory, vault));
        }
        finally
        {
            _working = false;
            RenderExport();
        }
    }

    // MARK: Self-test

    /// <summary>For the self-test: scroll to a fraction of the page.</summary>
    internal void ScrollTo(double fraction)
    {
        if (Content is not ScrollViewer scroll) return;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
    }

    /// <summary>For the self-test: a found OpenClaw with its counts and an export's result (no disk is read or written).</summary>
    internal void ShowDemo()
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");
        _chosen = new OpenClawInstall { StateDir = state, ConfigPath = Path.Combine(state, "openclaw.json"), FoundBy = "~/.openclaw" };
        _installs = [_chosen];
        _bundle = new OpenClawBundle
        {
            Install = _chosen,
            Skills = [new OpenClawSkill { Name = "github", Directory = Path.Combine(state, "skills", "github") }, new OpenClawSkill { Name = "freshdesk", Directory = Path.Combine(state, "workspace", "skills", "freshdesk") }],
            Credentials =
            [
                new OpenClawCredential { Name = "OPENROUTER_API_KEY", Secret = new SecretText("sk-or-v1-0123456789abcdef0123456789"), Origin = "openclaw.json env" },
                new OpenClawCredential { Name = "GITHUB_TOKEN", Secret = new SecretText("ghp_abcdefghijklmnopqrstuvwxyz0123"), Origin = "skills.entries.github" },
            ],
            ModelServers = [new OpenClawModelServer { Id = "spark", BaseUrl = "http://192.168.1.40:8002/v1", Models = ["qwen3-coder"] }],
        };
        _exportVault = true;
        _exportServers = true;
        _result = new OpenClawExportResult { MemoryAdded = 1, Notes = 14 };
        _result.Skills.AddRange(["deploy", "release-notes"]);
        _result.Secrets.AddRange(["GITHUB_TOKEN", "FRESHDESK_API_KEY"]);
        _result.Servers.Add("vision-dgx-spark");
        _result.SecretsSkipped["OPENROUTER_API_KEY"] = "OpenClaw already has OPENROUTER_API_KEY with a different value; kept that one.";
        RenderOpenClaw();
        RenderExport();
    }
}
