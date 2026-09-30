using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views;

/// <summary>"Bring in from OpenClaw": finds an OpenClaw install on this PC or on another computer (over SSH,
/// exploring it the way a person would with an SFTP client) and brings over what you tick — its skills, its
/// memory, its keys and passwords, and the model servers it uses. Nothing on the other machine is changed,
/// nothing there is run, and secrets go straight into the encrypted Credentials Vault: they are never shown
/// in full and never written anywhere else.</summary>
public sealed class OpenClawImportWindow : Window
{
    private enum Step { Where, Choose, Done }

    private readonly AppModel _model;
    private AppConfig Config => _model.Config;
    private AgentHost Host => _model.Host;

    private readonly ContentControl _page = new() { Focusable = false };
    private readonly TextBlock _status = Ui.Secondary("", 12);
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
    private readonly Button _back = Ui.Button("Back", () => { }, tooltip: "Go back a step");
    private readonly Button _next;
    private readonly Button _close;

    private Step _step = Step.Where;
    private CancellationTokenSource? _cts;
    private IFileSource? _source;
    private OpenClawBundle? _bundle;
    private OpenClawImportResult? _result;
    private IReadOnlyList<string> _routeNotes = [];
    private bool _working;
    /// <summary>The window has been closed: whatever was still running is answered with silence, not a dialog.</summary>
    private bool _closed;

    // Where
    private readonly RadioButton _thisPc = new() { Content = "This PC", IsChecked = true, GroupName = "where" };
    private readonly RadioButton _remote = new() { Content = "Another computer (over SSH)", GroupName = "where" };
    private readonly ComboBox _host = new() { IsEditable = true, MinWidth = 260 };
    private readonly TextBox _port = Ui.Field("22", width: 70);
    private readonly TextBox _user = Ui.Field(Environment.UserName.ToLowerInvariant(), placeholder: "user name", width: 200);
    private readonly PasswordBox _password = new() { Padding = new Thickness(8, 5, 8, 5), MinWidth = 260 };
    private readonly TextBox _keyPath = Ui.Field(placeholder: "Private key file (optional)", width: 300);
    private readonly PasswordBox _passphrase = new() { Padding = new Thickness(8, 5, 8, 5), MinWidth = 200 };
    private readonly TextBox _folder = Ui.Field(placeholder: "Leave blank to search for it", width: 380);
    private readonly StackPanel _remoteFields = new() { Margin = new Thickness(26, 6, 0, 0) };

    // Choose
    private readonly Dictionary<string, CheckBox> _skillChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _noteChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _credentialChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _credentialAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _serverChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _serverWorker = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Made afresh each time the choices are shown (a control can have only one parent).</summary>
    private CheckBox _draftSkills = new();
    /// <summary>The first page is built once: its controls live as long as the window and cannot be moved into a second copy.</summary>
    private ScrollViewer? _wherePage;

    public OpenClawImportWindow(AppModel model)
    {
        _model = model;
        Title = "Bring in from OpenClaw";
        Width = 760;
        Height = 680;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _next = Ui.Button("Look for OpenClaw", () => _ = NextAsync(), accent: true);
        _close = Ui.Button("Close", Close);
        _close.IsCancel = true;
        _back.Click += (_, _) => GoBack();
        _status.TextWrapping = TextWrapping.Wrap;

        var buttons = new DockPanel { Margin = new Thickness(20, 10, 20, 14) };
        var right = Ui.Buttons(_back, _next, _close);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_status);

        var footer = new StackPanel();
        footer.Children.Add(_busy);
        footer.Children.Add(Ui.Divider());
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_page);
        Content = root;

        FillHostChoices();
        _remote.Checked += (_, _) => UpdateRemoteVisibility();
        _thisPc.Checked += (_, _) => UpdateRemoteVisibility();
        Closed += (_, _) =>
        {
            _closed = true;
            Cleanup();
        };
        ShowWhere();
    }

    // MARK: - Steps

    private void ShowWhere()
    {
        _step = Step.Where;
        _bundle = null;
        _wherePage ??= BuildWherePage();
        _page.Content = _wherePage;
        UpdateRemoteVisibility();
        _back.Visibility = Visibility.Collapsed;
        _next.Visibility = Visibility.Visible;
        _next.Content = "Look for OpenClaw";
        _next.IsEnabled = true;
        _close.Content = "Close";
        SetStatus("");
    }

    private ScrollViewer BuildWherePage()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 16) };
        panel.Children.Add(Ui.Title("Bring in from OpenClaw"));
        var intro = Ui.Secondary("DSH finds your OpenClaw install and brings over what you choose: its skills, its memory, its keys and passwords, " +
                                 "and the model servers it uses (a DGX Spark, say). Nothing on the other machine is changed or run.", 13);
        intro.Margin = new Thickness(0, 6, 0, 18);
        panel.Children.Add(intro);

        panel.Children.Add(Ui.Subtitle("Where is OpenClaw running?"));
        _thisPc.Margin = new Thickness(0, 10, 0, 0);
        _remote.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_thisPc);
        panel.Children.Add(_remote);
        BuildRemoteFields();
        panel.Children.Add(_remoteFields);

        var folderLabel = Ui.Text("A folder to look in", 12.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
        folderLabel.Margin = new Thickness(0, 20, 0, 4);
        panel.Children.Add(folderLabel);
        panel.Children.Add(_folder);
        var hint = Ui.Text("Only if you know it — for example /home/sam/.openclaw. Otherwise DSH looks in the usual places and searches for openclaw.json.", 11.5,
            brushKey: "TextFillColorTertiaryBrush");
        hint.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(hint);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void BuildRemoteFields()
    {
        _remoteFields.Children.Clear();
        UIElement Row(string label, UIElement field)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var text = Ui.Text(label, 12.5, brushKey: "TextFillColorSecondaryBrush", wrap: false);
            text.Width = 110;
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            row.Children.Add(field);
            return row;
        }
        var hostAndPort = new StackPanel { Orientation = Orientation.Horizontal };
        hostAndPort.Children.Add(_host);
        var portLabel = Ui.Text("port", 12, brushKey: "TextFillColorSecondaryBrush", wrap: false);
        portLabel.Margin = new Thickness(10, 0, 6, 0);
        portLabel.VerticalAlignment = VerticalAlignment.Center;
        hostAndPort.Children.Add(portLabel);
        hostAndPort.Children.Add(_port);
        _remoteFields.Children.Add(Row("Address", hostAndPort));
        _remoteFields.Children.Add(Row("User name", _user));
        _remoteFields.Children.Add(Row("Password", _password));
        var browse = Ui.Button("Browse…", BrowseForKey);
        browse.MinWidth = 0;
        browse.Margin = new Thickness(8, 0, 0, 0);
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(_keyPath);
        keyRow.Children.Add(browse);
        _remoteFields.Children.Add(Row("Key file", keyRow));
        _remoteFields.Children.Add(Row("Key passphrase", _passphrase));
        var note = Ui.Text("Leave the password and key blank to try the keys in your ~/.ssh folder. Aliases from ~/.ssh/config work as the address. " +
                           "The first time, DSH shows the computer's fingerprint and asks you to confirm it.", 11.5, brushKey: "TextFillColorTertiaryBrush");
        note.Margin = new Thickness(0, 6, 0, 0);
        _remoteFields.Children.Add(note);
    }

    private void FillHostChoices()
    {
        try
        {
            if (File.Exists(SshConfigFile.DefaultPath))
            {
                foreach (var alias in SshConfigFile.Aliases(File.ReadAllText(SshConfigFile.DefaultPath))) _host.Items.Add(alias);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No ssh config to offer; the address can still be typed.
        }
        foreach (var route in Config.Providers)
        {
            if (Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback && !_host.Items.Contains(uri.Host)) _host.Items.Add(uri.Host);
        }
    }

    private void UpdateRemoteVisibility() =>
        _remoteFields.Visibility = _remote.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseForKey()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a private key file",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) _keyPath.Text = dialog.FileName;
    }

    private void GoBack()
    {
        if (_working) return;
        Cleanup();
        ShowWhere();
    }

    private async Task NextAsync()
    {
        if (_working) return;
        try
        {
            switch (_step)
            {
                case Step.Where:
                    await FindAndReadAsync();
                    break;
                case Step.Choose:
                    await ApplyAsync();
                    break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException && !_closed)
        {
            // Whatever it was (a link that dropped, a command that timed out), it must not leave the window spinning for ever.
            Busy(false, "");
            Dialog.Info("Something went wrong", error.Message);
        }
    }

    // MARK: - Find and read

    private async Task FindAndReadAsync()
    {
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Busy(true, "Connecting…");
        IFileSource? source = null;
        try
        {
            source = await OpenSourceAsync(ct);
            ct.ThrowIfCancellationRequested(); // the window was closed while signing in
            if (source is null) return;
            _source?.Dispose();
            _source = source;

            var progress = new Progress<string>(SetStatus);
            IReadOnlyList<OpenClawInstall> installs;
            var typed = _folder.Text.Trim();
            if (typed.Length > 0)
            {
                // "~/.openclaw" means the other machine's home, not ours.
                if (typed == "~" || typed.StartsWith("~/", StringComparison.Ordinal) || typed.StartsWith("~\\", StringComparison.Ordinal))
                    typed = source.Combine(await source.HomeAsync(ct), typed.Length > 2 ? typed[2..] : "");
                var one = await Task.Run(() => OpenClawScanner.InspectAsync(source, typed.TrimEnd('/', '\\'), "typed", ct), ct);
                installs = one is null ? [] : [one];
            }
            else
            {
                installs = await Task.Run(() => OpenClawScanner.FindAsync(source, progress, ct), ct);
            }
            ct.ThrowIfCancellationRequested();
            if (installs.Count == 0)
            {
                Busy(false, "");
                Dialog.Info("OpenClaw wasn't found",
                    $"DSH looked in the usual places on {source.Label} and searched for openclaw.json, and didn't find an OpenClaw install." +
                    "\n\nIf it lives somewhere unusual, type its folder in “A folder to look in” and try again.");
                return;
            }

            var install = installs.Count == 1 ? installs[0] : ChooseInstall(installs, source.Label);
            if (install is null)
            {
                Busy(false, "");
                return;
            }
            SetStatus($"Reading {install.StateDir}…");
            var read = await Task.Run(() => OpenClawReader.ReadAsync(source, install, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            _bundle = read;
            // Signed in and read: the secrets typed for it are not needed again. (Kept after a failure, so a retry doesn't
            // mean typing them over.)
            _password.Clear();
            _passphrase.Clear();
            Busy(false, "");
            ShowChoose();
        }
        catch (Exception error) when (error is OperationCanceledException || ct.IsCancellationRequested)
        {
            // Cancelled, or the window was closed under it (which disposes what the read was using, so it can fail any way
            // it likes): a connection opened but not yet handed to the window would otherwise be left open.
            if (source is not null && !ReferenceEquals(source, _source)) source.Dispose();
            Busy(false, "Cancelled.");
        }
        catch (RemoteLoginException error)
        {
            Busy(false, "");
            Dialog.Info("Couldn't sign in", error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Busy(false, "");
            Dialog.Info("Couldn't read OpenClaw", error.Message);
        }
    }

    /// <summary>This PC, or a signed-in connection to another computer. Null when the user backed out.</summary>
    private async Task<IFileSource?> OpenSourceAsync(CancellationToken ct)
    {
        if (_remote.IsChecked != true) return new LocalFileSource();

        var host = (_host.Text ?? "").Trim();
        if (host.Length == 0)
        {
            Busy(false, "");
            Dialog.Info("Which computer?", "Type the computer's address (or a name from your ~/.ssh/config).");
            return null;
        }
        // An alias in ~/.ssh/config supplies the real address, user, port and key.
        string? configKey = null;
        var user = _user.Text.Trim();
        var port = int.TryParse(_port.Text, out var p) && p is > 0 and < 65536 ? p : 22;
        try
        {
            if (File.Exists(SshConfigFile.DefaultPath) && SshConfigFile.Resolve(File.ReadAllText(SshConfigFile.DefaultPath), host) is { } entry)
            {
                host = entry.HostName;
                if (entry.User is { Length: > 0 } configured && (user.Length == 0 || user == Environment.UserName.ToLowerInvariant())) user = configured;
                if (entry.Port is { } configuredPort && _port.Text.Trim() is "" or "22") port = configuredPort;
                configKey = entry.IdentityFile;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable ssh config just means no alias.
        }
        if (user.Length == 0)
        {
            Busy(false, "");
            Dialog.Info("Which user?", "Type the user name to sign in with.");
            return null;
        }
        var login = new SshLogin
        {
            Host = host,
            Port = port,
            Username = user,
            Password = _password.Password.Length > 0 ? _password.Password : null,
            PrivateKeyPath = _keyPath.Text.Trim().Length > 0 ? _keyPath.Text.Trim() : configKey,
            Passphrase = _passphrase.Password.Length > 0 ? _passphrase.Password : null,
        };
        var knownHosts = new KnownHosts();
        string? approved = null;
        while (true)
        {
            try
            {
                SetStatus($"Signing in to {login.Label}…");
                return await SshRemote.ConnectAsync(login, knownHosts, approved, ct);
            }
            catch (SshHostKeyException unknown) when (!unknown.IsChanged && approved is null)
            {
                Busy(false, "");
                // (Cancel is the default answer: trusting a computer is a decision, not a habit.)
                var picked = Dialog.Ask($"Trust {unknown.Presented.Host}?",
                    $"DSH hasn't connected to {unknown.Presented.Host} before. Its SSH fingerprint is:\n\n{unknown.Presented.Fingerprint}\n\n" +
                    "Only continue if this is your own computer. DSH will remember it and warn you if it ever changes.",
                    new Dialog.Choice("Trust and continue"), new Dialog.Choice("Cancel", IsDefault: true, IsCancel: true));
                if (picked != 0) return null;
                approved = unknown.Presented.Fingerprint;
                Busy(true, "Connecting…");
            }
            catch (SshHostKeyException changed)
            {
                Busy(false, "");
                Dialog.Info("The computer's fingerprint changed",
                    (changed.Expected is null
                        ? $"{changed.Presented.Host} showed a different SSH fingerprint just now from the one you confirmed a moment ago.\n\n" +
                          $"Confirmed: {approved}\nNow: {changed.Presented.Fingerprint}\n\n"
                        : $"{changed.Presented.Host} presented a different SSH fingerprint from the one DSH remembered.\n\n" +
                          $"Remembered: {changed.Expected}\nNow: {changed.Presented.Fingerprint}\n\n") +
                    "If you didn't reinstall it, something may be pretending to be it, so DSH won't connect.");
                return null;
            }
        }
    }

    private static OpenClawInstall? ChooseInstall(IReadOnlyList<OpenClawInstall> installs, string machine)
    {
        var offered = installs.Take(4).ToList();
        var choices = offered.Select((install, n) => new Dialog.Choice(install.StateDir, IsDefault: n == 0))
            .Append(new Dialog.Choice("Cancel", IsCancel: true)).ToArray();
        var picked = Dialog.Ask("More than one OpenClaw was found",
            $"{installs.Count} OpenClaw folders were found on {machine}. Which one do you want to bring in from?", choices);
        return picked >= 0 && picked < offered.Count ? offered[picked] : null;
    }

    // MARK: - Choose

    private void ShowChoose()
    {
        _step = Step.Choose;
        var bundle = _bundle!;
        _skillChecks.Clear();
        _noteChecks.Clear();
        _credentialChecks.Clear();
        _credentialAccess.Clear();
        _serverChecks.Clear();
        _serverWorker.Clear();

        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 16) };
        panel.Children.Add(Ui.Title("What do you want to bring in?"));
        var where = Ui.Secondary($"Found on {bundle.SourceLabel} in {bundle.Install.StateDir}", 12.5);
        where.Margin = new Thickness(0, 4, 0, 6);
        where.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(where);
        foreach (var problem in bundle.Problems)
        {
            var warning = Ui.Status(problem, "SystemFillColorCautionBrush");
            warning.TextWrapping = TextWrapping.Wrap;
            warning.Margin = new Thickness(0, 2, 0, 2);
            panel.Children.Add(warning);
        }
        if (bundle.IsEmpty)
        {
            var nothing = Ui.Text("There is nothing here DSH can bring in.", 13, brushKey: "TextFillColorSecondaryBrush");
            nothing.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(nothing);
        }

        if (bundle.Skills.Count > 0) panel.Children.Add(SkillsSection(bundle));
        if (bundle.NoteFiles.Count > 0) panel.Children.Add(NotesSection(bundle));
        if (bundle.Credentials.Count > 0) panel.Children.Add(CredentialsSection(bundle));
        if (bundle.ModelServers.Count > 0) panel.Children.Add(ServersSection(bundle));
        if (bundle.Left.Count > 0)
        {
            var left = new StackPanel();
            foreach (var line in bundle.Left)
            {
                var text = Ui.Text("• " + line, 12, brushKey: "TextFillColorSecondaryBrush");
                text.Margin = new Thickness(0, 2, 0, 2);
                left.Children.Add(text);
            }
            panel.Children.Add(Section("Left behind", "Found, but not something DSH brings over.", left));
        }

        _page.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _back.Visibility = Visibility.Visible;
        _next.Content = "Bring in the selected";
        _next.IsEnabled = !bundle.IsEmpty;
        SetStatus("Nothing is copied until you press the button.");
    }

    private UIElement SkillsSection(OpenClawBundle bundle)
    {
        var body = new StackPanel();
        foreach (var skill in bundle.Skills)
        {
            var box = new CheckBox { IsChecked = !skill.Shadowed, IsEnabled = !skill.Shadowed, Margin = new Thickness(0, 3, 0, 3) };
            var text = new StackPanel();
            var name = skill.Name + (skill.HasScripts ? "   ⚙ has scripts" : "") + (skill.Shadowed ? "   (hidden by a skill of the same name)" : "");
            text.Children.Add(Ui.Text(name, 12.5, FontWeights.SemiBold, wrap: false));
            if (skill.Description.Length > 0) text.Children.Add(Ui.Text(TextUtil.Prefix(skill.Description, 120), 11.5, brushKey: "TextFillColorSecondaryBrush", wrap: false));
            box.Content = text;
            box.ToolTip = skill.Directory;
            _skillChecks[skill.Directory] = box;
            body.Children.Add(box);
        }
        _draftSkills = new CheckBox
        {
            Content = "Hold the skills for my approval (Settings › Skills) instead of switching them on",
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = bundle.Skills.Any(s => s.HasScripts),
        };
        body.Children.Add(_draftSkills);
        var count = bundle.Skills.Count(s => !s.Shadowed);
        return Section($"Skills ({count})", "Copied into your DSH skills. Bundled scripts come along untouched — importing never runs them.", body, allCheck: _skillChecks.Values);
    }

    private UIElement NotesSection(OpenClawBundle bundle)
    {
        var body = new StackPanel();
        foreach (var group in bundle.NoteFiles.GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var (title, hint) = group.Key switch
            {
                OpenClawNoteKind.Memory => ("MEMORY.md", "the curated long-term memory"),
                OpenClawNoteKind.About => ("USER.md", "what the agent knows about you"),
                OpenClawNoteKind.Daily => ("Daily notes", "memory/YYYY-MM-DD.md — the newest are ticked"),
                OpenClawNoteKind.Persona => ("Personality (SOUL.md, IDENTITY.md)", "the agent's persona — off unless you want it remembered"),
                _ => ("Operating instructions (AGENTS.md, TOOLS.md)", "written for OpenClaw's own tools — off unless you want them"),
            };
            var heading = Ui.Text($"{title} — {hint}", 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
            heading.Margin = new Thickness(0, 8, 0, 2);
            body.Children.Add(heading);
            foreach (var file in group.Take(group.Key == OpenClawNoteKind.Daily ? 200 : 20))
            {
                var box = new CheckBox { IsChecked = file.SelectedByDefault, Margin = new Thickness(0, 1, 0, 1) };
                box.Content = Ui.Text($"{file.Name} · {Formatting.Plural(file.Notes.Count, "note")}", 12, wrap: false);
                _noteChecks[file.Path] = box;
                body.Children.Add(box);
            }
        }
        var notes = bundle.NoteFiles.Sum(f => f.Notes.Count);
        return Section($"Memory ({Formatting.Plural(notes, "note")} in {Formatting.Plural(bundle.NoteFiles.Count, "file")})",
            "Cut into notes the agent finds by relevance — never loaded all at once. Bringing them in again updates them rather than adding copies.",
            body, allCheck: _noteChecks.Values);
    }

    private UIElement CredentialsSection(OpenClawBundle bundle)
    {
        var body = new StackPanel();
        foreach (var credential in bundle.Credentials)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var access = new ComboBox { MinWidth = 130, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            access.Items.Add(VaultAccess.Ask.Label());
            access.Items.Add(VaultAccess.Allowed.Label());
            access.Items.Add(VaultAccess.Never.Label());
            access.SelectedIndex = 0;
            DockPanel.SetDock(access, Dock.Right);
            row.Children.Add(access);
            var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel();
            text.Children.Add(Ui.Text(credential.Name, 12.5, FontWeights.SemiBold, wrap: false));
            var detail = $"{credential.Kind.Label()} · {credential.Secret.Hint} · from {credential.Origin}" +
                         (credential.AlsoNamed.Count > 0 ? $" (also {string.Join(", ", credential.AlsoNamed.Take(2))})" : "");
            text.Children.Add(Ui.Text(detail, 11, brushKey: "TextFillColorTertiaryBrush", wrap: false));
            box.Content = text;
            row.Children.Add(box);
            _credentialChecks[credential.Name] = box;
            _credentialAccess[credential.Name] = access;
            body.Children.Add(row);
        }
        var note = Ui.Text("Values go straight into the encrypted Credentials Vault. Set what the agent may do with each: “Ask first” asks the first time a chat uses one.", 11.5,
            brushKey: "TextFillColorTertiaryBrush");
        note.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(note);
        return Section($"Keys and passwords ({bundle.Credentials.Count})", "Only the ones you keep ticked are copied.", body, allCheck: _credentialChecks.Values);
    }

    private UIElement ServersSection(OpenClawBundle bundle)
    {
        var body = new StackPanel();
        foreach (var server in bundle.ModelServers)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var box = new CheckBox { IsChecked = server.Compatible, IsEnabled = server.Compatible };
            var text = new StackPanel();
            text.Children.Add(Ui.Text($"{server.Id} — {server.BaseUrl}", 12.5, FontWeights.SemiBold, wrap: false));
            var models = server.Models.Count == 0 ? "no models listed" : string.Join(", ", server.Models.Take(3)) + (server.Models.Count > 3 ? $" and {server.Models.Count - 3} more" : "");
            text.Children.Add(Ui.Text(models + (server.Note is null ? "" : " · " + server.Note), 11, brushKey: "TextFillColorTertiaryBrush"));
            box.Content = text;
            row.Children.Add(box);
            if (server.Compatible)
            {
                var worker = new CheckBox
                {
                    Content = "Also use it for subagents (runs side tasks in parallel with the main model)",
                    IsChecked = true,
                    FontSize = 11.5,
                    Margin = new Thickness(24, 2, 0, 0),
                };
                _serverWorker[server.Id] = worker;
                row.Children.Add(worker);
            }
            _serverChecks[server.Id] = box;
            body.Children.Add(row);
        }
        return Section($"Model servers ({bundle.ModelServers.Count})",
            "Added as routes you can pick in the model menu. With more than one, the main model does the thinking and the others take subagent work.",
            body, allCheck: _serverChecks.Values.Where(c => c.IsEnabled));
    }

    /// <summary>A titled card with a note and, if given, an "all / none" link.</summary>
    private static UIElement Section(string title, string note, UIElement body, IEnumerable<CheckBox>? allCheck = null)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        if (allCheck is not null)
        {
            var boxes = allCheck.ToList();
            var toggle = Ui.LinkButton("All / none", () =>
            {
                var target = boxes.Where(b => b.IsEnabled).Any(b => b.IsChecked != true);
                foreach (var b in boxes.Where(b => b.IsEnabled)) b.IsChecked = target;
            });
            DockPanel.SetDock(toggle, Dock.Right);
            head.Children.Add(toggle);
        }
        head.Children.Add(Ui.Subtitle(title));
        panel.Children.Add(head);
        var noteText = Ui.Secondary(note, 12);
        noteText.Margin = new Thickness(0, 2, 0, 8);
        panel.Children.Add(noteText);
        panel.Children.Add(body);
        var card = Ui.Card(panel);
        card.Margin = new Thickness(0, 16, 0, 0);
        return card;
    }

    // MARK: - Apply

    private async Task ApplyAsync()
    {
        if (_bundle is not { } bundle || _source is null) return;
        var selection = new OpenClawSelection
        {
            SkillsAsDrafts = _draftSkills.IsChecked == true,
        };
        foreach (var (key, box) in _skillChecks) if (box.IsChecked == true) selection.Skills.Add(key);
        foreach (var (key, box) in _noteChecks) if (box.IsChecked == true) selection.NoteFiles.Add(key);
        foreach (var (name, box) in _credentialChecks)
        {
            if (box.IsChecked != true) continue;
            selection.Credentials[name] = _credentialAccess[name].SelectedIndex switch
            {
                1 => VaultAccess.Allowed,
                2 => VaultAccess.Never,
                _ => VaultAccess.Ask,
            };
        }
        foreach (var (id, box) in _serverChecks) if (box.IsChecked == true && box.IsEnabled) selection.ModelServers.Add(id);
        if (selection.Skills.Count + selection.NoteFiles.Count + selection.Credentials.Count + selection.ModelServers.Count == 0)
        {
            Dialog.Info("Nothing selected", "Tick at least one thing to bring in.");
            return;
        }

        var source = _source;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Busy(true, "Bringing it in…");
        try
        {
            var progress = new Progress<string>(SetStatus);
            var targets = new OpenClawTargets(Host.Memory, Host.Vault, Host.SkillLocations);
            _result = await Task.Run(() => OpenClawImporter.ApplyAsync(source, bundle, selection, targets, progress, ct), ct);
            var workers = _serverWorker.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _routeNotes = AddServers(_result.ModelServers, workers);
            if (_result.SkillDrafts.Count > 0 || _result.Skills.Count > 0) Host.RefreshDrafts();
            Host.RefreshFleet();
            Busy(false, "");
            // The secrets have been moved: let go of them.
            _bundle = null;
            ShowDone();
        }
        catch (OperationCanceledException)
        {
            Busy(false, "Cancelled.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or VaultException or InvalidOperationException)
        {
            // What was brought in before the failure stays; the choices stay too, so it can be tried again.
            Busy(false, "");
            Dialog.Info("Couldn't finish", error.Message);
        }
    }

    /// <summary>Add the chosen model servers as routes. The first becomes the active model only when none is
    /// set yet; the rest can serve as subagent servers.</summary>
    private IReadOnlyList<string> AddServers(IReadOnlyList<OpenClawModelServer> servers, ISet<string> workers)
    {
        var notes = new List<string>();
        foreach (var server in servers)
        {
            var models = server.Models.Count == 0 ? [server.DefaultModel ?? "default"] : server.Models.ToList();
            if (server.DefaultModel is { } preferred && models.Remove(preferred)) models.Insert(0, preferred);
            var added = 0;
            foreach (var model in models.Take(3))
            {
                var profile = new ProviderProfile(ProviderKind.OpenAICompat, server.Id, server.BaseUrl, model)
                {
                    ContextWindow = server.ContextWindow,
                    // (One machine is one worker however many of its models are listed as routes.)
                    SubagentWorker = workers.Contains(server.Id) && added == 0,
                };
                if (Config.Providers.Any(p => p.RouteId == profile.RouteId))
                {
                    notes.Add($"{profile.DisplayName} was already there.");
                    continue;
                }
                Config.Providers.Add(profile);
                if (server.ApiKey is { } key) Config.SetApiKey(key.Reveal(), profile);
                if (Config.ActiveRoute is null || !Config.Providers.Any(p => p.RouteId == Config.ActiveRoute)) Config.ActiveRoute = profile.RouteId;
                added++;
            }
            if (added > 0) notes.Add($"Added {server.Id} ({added} model{(added == 1 ? "" : "s")})" + (workers.Contains(server.Id) ? ", also used for subagents." : "."));
        }
        return notes;
    }

    // MARK: - Done

    private void ShowDone()
    {
        _step = Step.Done;
        var result = _result!;
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 16) };
        var title = Ui.Title(result.BroughtAnything ? "Brought in" : "Nothing was brought in");
        panel.Children.Add(title);
        var summary = Ui.Text(result.BroughtAnything ? $"DSH now has {result.Summary}." : "Everything you ticked was already here or couldn't be read.", 13.5);
        summary.Margin = new Thickness(0, 6, 0, 14);
        panel.Children.Add(summary);

        void Lines(string heading, IEnumerable<string> lines, string brush = "TextFillColorSecondaryBrush")
        {
            var list = lines.ToList();
            if (list.Count == 0) return;
            var head = Ui.Text(heading, 12.5, FontWeights.SemiBold);
            head.Margin = new Thickness(0, 10, 0, 3);
            panel.Children.Add(head);
            foreach (var line in list.Take(30))
            {
                var text = Ui.Text("• " + line, 12, brushKey: brush);
                text.Margin = new Thickness(0, 1, 0, 1);
                panel.Children.Add(text);
            }
            if (list.Count > 30) panel.Children.Add(Ui.Text($"…and {list.Count - 30} more.", 12, brushKey: "TextFillColorTertiaryBrush"));
        }
        Lines("Skills", result.Skills.Concat(result.SkillDrafts.Select(d => d + " (waiting for your approval in Settings › Skills)")));
        if (result.Notes + result.NotesUpdated > 0)
            Lines("Memory", [$"{Formatting.Plural(result.Notes, "note")} added" + (result.NotesUpdated > 0 ? $", {result.NotesUpdated} updated" : "") + ". See them under Session › Remembered Notes."]);
        Lines("Keys and passwords (in the Credentials Vault)", result.Credentials);
        Lines("Model servers", _routeNotes);
        Lines("Not brought in", result.SkillsSkipped.Select(p => $"skill {p.Key}: {p.Value}")
            .Concat(result.CredentialsSkipped.Select(p => $"{p.Key}: {p.Value}")).Concat(result.Problems), "SystemFillColorCautionBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        if (result.Notes + result.NotesUpdated > 0) buttons.Children.Add(Ui.Button("Open remembered notes", () => _model.ShowMemories()));
        if (result.Credentials.Count > 0)
        {
            var vault = Ui.Button("Open the vault", () => (Application.Current.MainWindow as MainWindow)?.ShowVault());
            vault.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(vault);
        }
        if (result.ModelServers.Count > 0)
        {
            var models = Ui.Button("Model settings", () => _model.ShowSettings(SettingsTab.Models));
            models.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(models);
        }
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);

        _page.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _back.Visibility = Visibility.Collapsed;
        _next.Visibility = Visibility.Collapsed;
        _close.Content = "Done";
        SetStatus("");
    }

    // MARK: - Chrome

    private void Busy(bool on, string status)
    {
        _working = on;
        _busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _next.IsEnabled = !on && (_step != Step.Choose || _bundle is { IsEmpty: false });
        _back.IsEnabled = !on;
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.ToolTip = text;
    }

    /// <summary>Stop anything running, drop the connection and let go of what was read (it holds secrets).</summary>
    private void Cleanup()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
        _cts?.Dispose();
        _cts = null;
        _source?.Dispose();
        _source = null;
        _bundle = null;
        _password.Clear();
        _passphrase.Clear();
        _working = false;
    }
}
