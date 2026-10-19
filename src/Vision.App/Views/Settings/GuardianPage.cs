using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Guide;
using Vision.Core;

namespace Vision.App.Views.Settings;

/// <summary>Settings › Guardian: sign in with the guardian.503cm.com account (the browser for SSO, or email, password and
/// 2FA), pick the org, see what is left of the limits, use the account's models, and the syncs that follow from it —
/// prompts (automatic on submit), skills both ways, chats out and back, code projects up, and secrets opted in for
/// Vision (this session only). The same sections and words as Vision for macOS.</summary>
public sealed class GuardianPage : UserControl
{
    private readonly AppModel _model;
    private GuardianService Guardian => _model.Guardian;
    private readonly StackPanel _body = new();
    private bool _refreshQueued;

    // The sign-in fields live as long as the page: rebuilding the page must not take what was typed (or the focus).
    private readonly TextBox _address = Ui.Field(placeholder: GuardianClient.DefaultAddress, width: 340);
    private readonly TextBox _email = Ui.Field(placeholder: "you@503cm.com", width: 340);
    private readonly PasswordBox _password = new() { Padding = new Thickness(8, 5, 8, 5), Width = 340 };
    private readonly TextBox _code = Ui.Field(placeholder: "6-digit code", width: 160);
    private bool _replaceSkills;

    // What was fetched on request.
    private IReadOnlyList<GuardianPrompt>? _notebook;
    private IReadOnlyList<GuardianChatSummary>? _chats;
    private readonly HashSet<string> _chatPicks = new(StringComparer.Ordinal);
    private IReadOnlyList<GuardianProjectSummary>? _projects;
    private IReadOnlyList<GuardianVaultSecret>? _secrets;
    private GuardianModel? _chosenModel;

    public GuardianPage(AppModel model)
    {
        _model = model;
        var (address, email) = Guardian.Remembered();
        _address.Text = address;
        _email.Text = email;
        _code.MaxLength = 7;
        _password.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) _ = SignIn();
        };
        _code.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) _ = Verify();
        };

        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Guardian"));
        var intro = Ui.Secondary("Your Guardian account (guardian.503cm.com) in Vision: its models through one sign-in, with the prompts you send, your skills, " +
                                 "chats and code projects shared between Guardian and every Vision you use. Vision gets whatever your Guardian role allows.");
        intro.Margin = new Thickness(0, 6, 0, 4);
        page.Children.Add(intro);
        page.Children.Add(_body);
        Content = Ui.Scroll(page);

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName is nameof(GuardianService.Status)) UpdateStatus();
            else QueueRefresh();
        };
        Guardian.PropertyChanged += handler;
        Unloaded += (_, _) => Guardian.PropertyChanged -= handler;
        Refresh();
    }

    private TextBlock? _statusLine;

    private void UpdateStatus()
    {
        if (_statusLine is null) return;
        _statusLine.Text = Guardian.Status ?? "";
        _statusLine.Visibility = string.IsNullOrEmpty(Guardian.Status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
        else if (element.Parent is Decorator decorator) decorator.Child = null;
        else if (element.Parent is ContentControl content) content.Content = null;
    }

    private void Refresh()
    {
        foreach (var field in new FrameworkElement[] { _address, _email, _password, _code }) Detach(field);
        _body.Children.Clear();
        _body.Children.Add(Ui.Section("Account"));
        _body.Children.Add(AccountCard());
        _statusLine = Ui.Status("", "TextFillColorSecondaryBrush");
        _statusLine.Margin = new Thickness(2, 4, 0, 0);
        _body.Children.Add(_statusLine);
        UpdateStatus();
        if (!Guardian.IsSignedIn) return;

        _body.Children.Add(Ui.Section("Limits"));
        _body.Children.Add(LimitsCard());
        _body.Children.Add(Ui.Section("Models"));
        _body.Children.Add(ModelsCard());
        _body.Children.Add(Ui.Section("Prompt notebook"));
        _body.Children.Add(NotebookCard());
        _body.Children.Add(Ui.Section("Skills"));
        _body.Children.Add(SkillsCard());
        _body.Children.Add(Ui.Section("Chats"));
        _body.Children.Add(ChatsCard());
        _body.Children.Add(Ui.Section("Code projects"));
        _body.Children.Add(ProjectsCard());
        _body.Children.Add(Ui.Section("Secrets for Vision"));
        _body.Children.Add(VaultCard());
    }

    // MARK: Account

    private static FrameworkElement Labeled(string label, FrameworkElement field)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var text = Ui.Text(label, 13, wrap: false);
        text.Width = 90;
        text.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(field);
        return row;
    }

    private UIElement AccountCard()
    {
        var busy = Guardian.Busy;
        if (Guardian.IsSignedIn && Guardian.Account is { } account)
        {
            var panel = new StackPanel();
            var who = Ui.Text($"Signed in as {account.Label}", 14, FontWeights.SemiBold);
            var how = Ui.Secondary($"{account.Address} · {(account.Method == "browser" ? "signed in with the browser" : "signed in with email and password")}" +
                                   $" · since {account.SignedInAt.LocalDateTime:d MMM, HH:mm}");
            var actions = Ui.Buttons(
                Ui.Button("Refresh", () => _ = Guardian.RefreshAsync()),
                Ui.Button("Sign out", () =>
                {
                    if (!Dialogs.Dialog.Confirm("Sign out of Guardian?",
                            "Vision forgets the sign-in on this PC: Guardian routes stop working until you sign in again, and secrets revealed from Guardian's vault are let go. " +
                            "Guardian still lists this sign-in under Profile › API keys › Connected apps until you revoke it there.", "Sign out", destructive: true)) return;
                    Guardian.SignOut();
                }));
            var head = new DockPanel();
            DockPanel.SetDock(actions, Dock.Right);
            actions.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(actions);
            head.Children.Add(Ui.Stack(who, how));
            panel.Children.Add(head);

            if (Guardian.Identity is { Orgs.Count: > 0 } identity)
            {
                var orgs = new ComboBox { MinWidth = 220 };
                var current = account.OrgId ?? identity.ActiveOrg?.Id;
                foreach (var org in identity.Orgs) orgs.Items.Add(new ComboBoxItem { Content = org.Name, Tag = org, IsSelected = org.Id == current });
                orgs.SelectionChanged += (_, _) =>
                {
                    if (orgs.SelectedItem is ComboBoxItem { Tag: GuardianOrg org } && org.Id != (Guardian.Account?.OrgId ?? current)) _ = Guardian.SwitchOrgAsync(org);
                };
                orgs.IsEnabled = identity.Orgs.Count > 1 && !busy;
                var orgRow = Labeled("Organization", orgs);
                orgRow.Margin = new Thickness(0, 12, 0, 0);
                panel.Children.Add(orgRow);
                panel.Children.Add(Ui.Secondary(identity.Orgs.Count > 1
                    ? "Models, limits, skills and the notebook follow the organization you pick. Its routes in AI Models follow it too."
                    : "Your account is in one organization."));
            }
            var note = Ui.Secondary((account.Method == "browser"
                                        ? "Vision holds a session token for this PC in Windows Credential Manager; your password never passed through Vision. "
                                        : "Your password was exchanged for a session token kept in Windows Credential Manager — the password is never stored. ") +
                                    "Prompts you send in chats go to your Guardian notebook automatically.");
            note.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(note);
            return Ui.Card(panel);
        }

        if (Guardian.PendingChallenge is not null)
        {
            var panel = Ui.Stack(
                Ui.Text($"Two-factor sign-in for {Guardian.PendingEmail}", 14, FontWeights.SemiBold),
                Ui.Secondary("Enter the 6-digit code from your authenticator app. Codes rotate every 30 seconds."));
            var row = Ui.Buttons(_code, Ui.Button("Verify", () => _ = Verify(), accent: true), Ui.LinkButton("Start over", () =>
            {
                Guardian.CancelTwoFactor();
                _code.Text = "";
            }));
            row.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(row);
            row.IsEnabled = !busy;
            return Ui.Card(panel);
        }

        var form = new StackPanel();
        form.Children.Add(Labeled("Address", _address));
        var browser = Ui.Button("Sign in with your browser…", () => _ = Guardian.SignInWithBrowserAsync(_address.Text), accent: true,
            tooltip: "For accounts that sign in with Microsoft (Entra SSO) or without a password: Guardian opens in your browser and asks which organization to connect");
        browser.HorizontalAlignment = HorizontalAlignment.Left;
        browser.Margin = new Thickness(90, 8, 0, 2);
        form.Children.Add(browser);
        var browserHint = Ui.Secondary("For Microsoft (Entra) and passwordless sign-ins: Guardian opens in your browser and asks which organization to connect.");
        browserHint.Margin = new Thickness(90, 2, 0, 4);
        form.Children.Add(browserHint);
        if (busy && Guardian.Status?.StartsWith("Finish signing in", StringComparison.Ordinal) == true)
        {
            var waiting = Ui.Buttons(new Spinner { Width = 14, Height = 14 }, Ui.Secondary("Waiting for your browser…"), Ui.LinkButton("Cancel", Guardian.CancelBrowser));
            waiting.Margin = new Thickness(90, 4, 0, 4);
            form.Children.Add(waiting);
            if (Guardian.BrowserUrl is { } link)
            {
                var copy = Ui.LinkButton("No browser opened? Copy the sign-in link", () =>
                {
                    try { Clipboard.SetText(link); } catch (System.Runtime.InteropServices.ExternalException) { }
                });
                copy.HorizontalAlignment = HorizontalAlignment.Left;
                copy.Margin = new Thickness(86, 0, 0, 4);
                form.Children.Add(copy);
            }
        }
        var or = Ui.Secondary("Or with the email and password you use at Guardian:");
        or.Margin = new Thickness(0, 12, 0, 2);
        form.Children.Add(or);
        form.Children.Add(Labeled("Email", _email));
        form.Children.Add(Labeled("Password", _password));
        var signIn = Ui.Button("Sign in", () => _ = SignIn());
        var signInRow = Ui.Buttons(signIn);
        signInRow.Margin = new Thickness(90, 8, 0, 0);
        form.Children.Add(signInRow);
        form.IsEnabled = !busy;
        var footnote = Ui.Secondary("Your password is exchanged for a session token and never stored; the token lives in Windows Credential Manager.");
        footnote.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(footnote);
        return Ui.Card(form);
    }

    private async Task SignIn()
    {
        if (await Guardian.SignInAsync(_address.Text, _email.Text, _password.Password)) _password.Clear();
        else if (Guardian.PendingChallenge is not null) _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _code.Focus());
    }

    private async Task Verify()
    {
        if (await Guardian.VerifyTwoFactorAsync(_code.Text))
        {
            _code.Text = "";
            _password.Clear();
        }
    }

    // MARK: Limits

    private UIElement LimitsCard()
    {
        var panel = new StackPanel();
        if (Guardian.Limits.Count == 0)
        {
            panel.Children.Add(Ui.Secondary("Guardian sets no spend or token limits on your account right now."));
            return Ui.Card(panel);
        }
        foreach (var limit in Guardian.Limits)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            var head = new DockPanel();
            var reset = Ui.Secondary((limit.ResetPhrase() is { } resets ? char.ToUpperInvariant(resets[0]) + resets[1..] : limit.Window == "run" ? "Frees up as work finishes" : ""));
            DockPanel.SetDock(reset, Dock.Right);
            head.Children.Add(reset);
            head.Children.Add(Ui.Text($"{limit.Title}: {limit.Describe()}", 13.5));
            row.Children.Add(head);
            if (limit.Fraction is { } fraction)
                row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, Height = 4, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(row);
        }
        panel.Children.Add(Ui.Secondary("When a limit is reached, Vision says which one and when it resets."));
        return Ui.Card(panel);
    }

    // MARK: Models

    private UIElement ModelsCard()
    {
        var panel = new StackPanel();
        var routes = Guardian.GuardianRoutesHere(Guardian.Client?.Address);
        if (routes.Count > 0)
        {
            var active = _model.Config.ActiveRoute;
            panel.Children.Add(Ui.Text("In use: " + string.Join(", ", routes.Select(r => r.Model + (r.RouteId == active ? " (primary)" : r.SubagentWorker ? " (subagents)" : r.IsBackup ? " (backup)" : ""))), 13.5));
            panel.Children.Add(Ui.Secondary("Each is a route in Settings › AI Models with your Guardian token as its key; pick it per chat from the model menu."));
        }
        if (Guardian.Models.Count == 0)
        {
            panel.Children.Add(Ui.Secondary(Guardian.Busy ? "Asking Guardian which models you may use…" : "Guardian lists no models for you in this organization."));
            return Ui.Card(panel);
        }
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _chosenModel ??= Guardian.Models.FirstOrDefault();
        foreach (var model in Guardian.Models)
        {
            var detail = new List<string>();
            if (model.ContextWindow is { } window) detail.Add($"{Fmt.Short(window)} context");
            if (model.IsAlias) detail.Add("your org picks the model behind it");
            if (model.InteractiveOnly == true) detail.Add("interactive use only");
            var radio = new RadioButton
            {
                GroupName = "guardian-model",
                IsChecked = _chosenModel?.Id == model.Id,
                Margin = new Thickness(0, 3, 0, 3),
                Content = Ui.Stack(Ui.Text(model.Title, 13), detail.Count == 0 ? null : Ui.Secondary(string.Join(" · ", detail))),
            };
            var value = model;
            radio.Checked += (_, _) => _chosenModel = value;
            list.Children.Add(radio);
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = Ui.Buttons(
            Ui.Button("Use as my primary model", () => Use(RouteUse.Primary), accent: true),
            Ui.Button("Use for subagents", () => Use(RouteUse.Subagent)),
            Ui.Button("Just add it", () => Use(RouteUse.JustAdd)));
        buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(buttons);
        return Ui.Card(panel);
    }

    private void Use(RouteUse use)
    {
        if (_chosenModel is not { } model) return;
        if (Guardian.UseModel(model, use) is { } route)
            Guardian.Status = use switch
            {
                RouteUse.Primary => $"{route.Model} through Guardian is your primary model now.",
                RouteUse.Subagent => $"{route.Model} through Guardian takes subagents' side tasks.",
                _ => $"{route.Model} through Guardian is in the model menu.",
            };
        Refresh();
    }

    // MARK: Notebook

    private UIElement NotebookCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Every prompt you send in a chat goes to your Guardian notebook, with how the turn ended — completed, needs you, failed or stopped.", 13));
        var masking = Ui.Secondary("Before a prompt goes up, the keys and passwords in your vault are masked, and — with Settings › Privacy's shield on — whatever it masks for models that aren't private.");
        masking.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(masking);
        var sync = Ui.Check("Send my prompts to the notebook", _model.Config.GuardianPromptSync, on => _model.Config.GuardianPromptSync = on);
        sync.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(sync);
        var buttons = Ui.Buttons(
            Ui.Button(_notebook is null ? "Show my latest prompts" : "Refresh", async () =>
            {
                _notebook = await Guardian.NotebookAsync(10);
                Refresh();
            }),
            Ui.LinkButton("Open the notebook in Guardian", () => ShellIntegration.Open((Guardian.Client?.Address ?? GuardianClient.DefaultAddress) + "/app/notebook")));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(buttons);
        if (_notebook is { } prompts)
        {
            var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (prompts.Count == 0) list.Children.Add(Ui.Secondary("Your notebook is empty so far."));
            foreach (var prompt in prompts)
            {
                var facts = new List<string> { prompt.At.LocalDateTime.ToString("d MMM HH:mm") };
                if (prompt.Source is { } source) facts.Add(source switch { "vision-windows" => "Vision Windows", "vision-mac" => "Vision Mac", "guardian" => "Guardian", _ => source });
                if (prompt.ChatTitle is { } title) facts.Add(title);
                facts.Add(prompt.Outcome ?? "no outcome yet");
                var row = Ui.Stack(Ui.Text(TextUtil.Prefix(prompt.Text.Replace("\r", "").Replace('\n', ' '), 140), 13, wrap: false), Ui.Secondary(string.Join(" · ", facts)));
                row.Margin = new Thickness(0, 3, 0, 5);
                list.Children.Add(row);
            }
            panel.Children.Add(list);
        }
        return Ui.Card(panel);
    }

    // MARK: Skills

    private UIElement SkillsCard()
    {
        var panel = new StackPanel();
        var mine = Guardian.DepositableSkillFolders().Count;
        var replace = Ui.Check("Replace my changed copies with Guardian's", _replaceSkills, on => _replaceSkills = on);
        var buttons = Ui.Buttons(
            Ui.Button("Take skills from Guardian", () => _ = Guardian.TakeSkillsAsync(_replaceSkills)),
            Ui.Button(mine == 0 ? "Deposit my skills" : $"Deposit my {GuardianWords.Count(mine, "skill", "skills")}", () => _ = Guardian.DepositSkillsAsync()),
            replace);
        replace.VerticalAlignment = VerticalAlignment.Center;
        replace.Margin = new Thickness(12, 0, 0, 0);
        panel.Children.Add(buttons);
        var note = Ui.Secondary("A skill taken from Guardian is a copy in your skills (Settings › Skills); one you changed here is kept unless you choose to replace it. " +
                                "Depositing publishes your own skills and the open project's .vision\\skills to your account, where any Vision can take them.");
        note.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(note);
        return Ui.Card(panel);
    }

    // MARK: Chats

    private UIElement ChatsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Buttons(
            Ui.Button($"Export all chats to Guardian ({_model.Host.Log.List().Count})", () => _ = Guardian.ExportChatsAsync()),
            Ui.Button("Bring chats back from Guardian…", async () =>
            {
                _chats = await Guardian.ChatsAsync();
                _chatPicks.Clear();
                foreach (var chat in _chats.Take(20).Where(c => _model.Host.Log.List().All(r => r.Id != c.Id))) _chatPicks.Add(chat.Id);
                Refresh();
            })));
        if (_chats is { } chats)
        {
            var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (chats.Count == 0) list.Children.Add(Ui.Secondary("Guardian holds no chats for you yet."));
            foreach (var chat in chats)
            {
                var here = _model.Host.Log.List().Any(r => r.Id == chat.Id);
                var id = chat.Id;
                var check = Ui.Check($"{chat.Title} · {chat.UpdatedAt.LocalDateTime:d MMM yyyy}" + (here ? " · already here" : ""), _chatPicks.Contains(id), on =>
                {
                    if (on) _chatPicks.Add(id); else _chatPicks.Remove(id);
                });
                check.IsEnabled = !here;
                check.Margin = new Thickness(0, 2, 0, 2);
                list.Children.Add(check);
            }
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (chats.Count > 0)
            {
                var bring = Ui.Button("Bring the ticked chats here", async () =>
                {
                    await Guardian.ImportChatsAsync(chats.Where(c => _chatPicks.Contains(c.Id)).ToList());
                    _chats = null;
                    Refresh();
                }, accent: true);
                bring.HorizontalAlignment = HorizontalAlignment.Left;
                bring.Margin = new Thickness(0, 8, 0, 0);
                panel.Children.Add(bring);
            }
        }
        var note = Ui.Secondary("Exported chats appear in Guardian under your account, and in the same form Vision for macOS uses — a chat from either one comes back on the other.");
        note.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(note);
        return Ui.Card(panel);
    }

    // MARK: Projects

    private UIElement ProjectsCard()
    {
        var panel = new StackPanel();
        var project = _model.Project;
        var upload = Ui.Button(project is null ? "Upload the open project" : $"Upload {_model.ProjectName}", () =>
        {
            if (_model.Project is { } folder) _ = Guardian.UploadProjectAsync(folder);
        }, accent: true);
        upload.IsEnabled = project is not null;
        panel.Children.Add(Ui.Buttons(upload, Ui.Button(_projects is null ? "Show my uploaded projects" : "Refresh", async () =>
        {
            _projects = await Guardian.ProjectsAsync();
            Refresh();
        })));
        if (_projects is { } projects)
        {
            var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (projects.Count == 0) list.Children.Add(Ui.Secondary("No projects uploaded yet."));
            foreach (var item in projects)
                list.Children.Add(Ui.Text($"{item.Name}" + (item.FileCount is { } n ? $" · {GuardianWords.Count(n, "file", "files")}" : "")
                                          + (item.UpdatedAt is { } at ? $" · {at.LocalDateTime:d MMM yyyy, HH:mm}" : ""), 13));
            panel.Children.Add(list);
        }
        var note = Ui.Secondary("The project's text files go to your account's private store (build output, dependencies and files with keys are left out). " +
                                "From there Guardian's AI Coders can be pointed at it.");
        note.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(note);
        return Ui.Card(panel);
    }

    // MARK: Vault

    private UIElement VaultCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Secrets you marked “Usable in Vision” in Guardian's vault. Each one is fetched only when you choose it, " +
                                   "kept in memory until Vision quits, and every hand-out is recorded in Guardian.", 13));
        var load = Ui.Button(_secrets is null ? "Show my secrets for Vision" : "Refresh", async () =>
        {
            _secrets = await Guardian.VaultAsync();
            Refresh();
        });
        load.HorizontalAlignment = HorizontalAlignment.Left;
        load.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(load);
        if (_secrets is { } secrets)
        {
            var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (secrets.Count == 0)
                list.Children.Add(Ui.Secondary("None yet. In Guardian, open a secret in your vault and switch on “Usable in Vision”."));
            foreach (var secret in secrets)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var held = Guardian.IsHeld(secret);
                UIElement action = held
                    ? Ui.Secondary("In use until Vision quits")
                    : Ui.Button("Use in this session", async () =>
                    {
                        if (await Guardian.UseSecretAsync(secret)) Refresh();
                    });
                if (action is FrameworkElement element) DockPanel.SetDock(element, Dock.Right);
                row.Children.Add(action);
                var name = Ui.Text(secret.Name, 13, wrap: false);
                name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                row.Children.Add(Ui.Stack(name, secret.Description is { Length: > 0 } d ? Ui.Secondary(d) : null));
                list.Children.Add(row);
            }
            panel.Children.Add(list);
        }
        return Ui.Card(panel);
    }

    // MARK: Self-test

    /// <summary>For the self-test: scroll to a fraction of the page.</summary>
    internal void ScrollTo(double fraction)
    {
        if (Content is not ScrollViewer scroll) return;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
    }

    /// <summary>For the self-test: the fetched lists filled in (nothing is contacted).</summary>
    internal void ShowDemoLists()
    {
        var now = DateTimeOffset.Now;
        _notebook =
        [
            new GuardianPrompt("Fix the failing StockTests and tell me what was wrong", now.AddMinutes(-12)) { Source = "vision-windows", ChatTitle = "Inventory tests", Outcome = "completed" },
            new GuardianPrompt("/goal ship the release notes for 0.9", now.AddHours(-3)) { Source = "vision-mac", ChatTitle = "Release", Outcome = "needs you" },
            new GuardianPrompt("Summarise this week's helpdesk tickets", now.AddDays(-1)) { Source = "guardian", ChatTitle = "Helpdesk", Outcome = "answered" },
        ];
        _projects = [new GuardianProjectSummary("p1", "inventory-service", now.AddDays(-2), 48)];
        _secrets =
        [
            new GuardianVaultSecret("v1", "FRESHDESK_API_KEY", "api_key", "Helpdesk API"),
            new GuardianVaultSecret("v2", "GITHUB_TOKEN", "token", null),
        ];
        Refresh();
    }
}
