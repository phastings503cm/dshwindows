using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Views.Guide;
using Vision.Core;

namespace Vision.App.Model;

/// <summary>The app's Guardian connection (one per app): the sign-in and its token, the account's identity, orgs, limits
/// and models, the routes through its proxy, prompt sync on submit, and the Settings › Guardian actions. Every refusal
/// Guardian sends (401/403/402/429) is shown as a dialog naming what is missing or which limit was reached
/// (<see cref="Refused"/>); the page shows the rest as a status line.</summary>
public sealed partial class GuardianService : ObservableObject
{
    private readonly AppConfig _config;
    private readonly AgentHost _host;
    private readonly Func<string?> _project;
    private readonly GuardianAccountStore _accounts;
    private readonly Dispatcher _dispatcher;
    private readonly Func<HttpMessageHandler?> _handler;

    /// <summary>The signed-in client, or null.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSignedIn))] private GuardianClient? _client;
    [ObservableProperty] private GuardianAccount? _account;
    [ObservableProperty] private GuardianIdentity? _identity;
    [ObservableProperty] private IReadOnlyList<GuardianLimit> _limits = [];
    [ObservableProperty] private IReadOnlyList<GuardianModel> _models = [];
    /// <summary>A sign-in waiting on its authenticator code.</summary>
    [ObservableProperty] private string? _pendingChallenge;
    [ObservableProperty] private string? _pendingEmail;
    [ObservableProperty] private bool _busy;
    /// <summary>What the last action said (the page's status line).</summary>
    [ObservableProperty] private string? _status;

    /// <param name="project">The open project (its skills are deposited too).</param>
    /// <param name="folder">Where guardian.json lives (the data folder by default).</param>
    /// <param name="handler">Replaces the network (tests).</param>
    public GuardianService(AppConfig config, AgentHost host, Func<string?> project, string? folder = null, Func<HttpMessageHandler?>? handler = null)
    {
        _config = config;
        _host = host;
        _project = project;
        _accounts = new GuardianAccountStore(folder);
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _handler = handler ?? (() => null);
        if (_accounts.Load() is { } saved)
        {
            try
            {
                var session = new GuardianSession(saved.Account.UserId ?? saved.Account.Email, saved.Account.Email, saved.Account.Name, saved.Token,
                    saved.Account.SignedInAt) { OrgId = saved.Account.OrgId };
                _client = new GuardianClient(saved.Account.Address, session, _handler()) { OrgId = saved.Account.OrgId };
                _account = saved.Account;
            }
            catch (GuardianError)
            {
                // An address that no longer reads as one: signed out.
            }
        }
        GuardianProxyRefusals.Reported += OnProxyRefusal;
    }

    public bool IsSignedIn => Client is not null;

    /// <summary>The address and email the sign-in form starts with: the last account's, else Guardian's own.</summary>
    public (string Address, string Email) Remembered()
    {
        var last = Account ?? _accounts.LastAccount();
        return (last?.Address ?? GuardianClient.DefaultAddress, last?.Email ?? "");
    }

    /// <summary>A refusal to show as a dialog (the main window shows it; the page passes it on).</summary>
    public event Action<GuardianError>? Refused;

    // MARK: - Signing in

    /// <summary>Sign in with the email and password. A TOTP account stops at <see cref="PendingChallenge"/>. True when
    /// signed in.</summary>
    public async Task<bool> SignInAsync(string address, string email, string password)
    {
        Status = null;
        PendingChallenge = null;
        PendingEmail = null;
        if (GuardianClient.NormalizeAddress(address) is not { } normalized)
        {
            Status = GuardianError.BadAddress().Message;
            return false;
        }
        var client = new GuardianClient(normalized, handler: _handler());
        return await Run(async () =>
        {
            try
            {
                var session = await client.SignInAsync(email, password).ConfigureAwait(true);
                await CompleteAsync(client, session, "password").ConfigureAwait(true);
                return true;
            }
            catch (GuardianError error) when (error.Kind == GuardianErrorKind.TwoFactorRequired)
            {
                _pendingClient = client;
                PendingChallenge = error.Challenge;
                PendingEmail = email.Trim();
                Status = "Enter the 6-digit code from your authenticator app. Codes rotate every 30 seconds.";
                return false;
            }
        }).ConfigureAwait(true);
    }

    private GuardianClient? _pendingClient;

    /// <summary>Finish a sign-in that stopped at the 2FA step.</summary>
    public async Task<bool> VerifyTwoFactorAsync(string code)
    {
        if (PendingChallenge is not { } challenge || _pendingClient is not { } client)
        {
            Status = "No sign-in is waiting for a code — sign in again.";
            return false;
        }
        return await Run(async () =>
        {
            var session = await client.VerifyTwoFactorAsync(challenge, code, PendingEmail).ConfigureAwait(true);
            CancelTwoFactor();
            await CompleteAsync(client, session, "password").ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
    }

    public void CancelTwoFactor()
    {
        PendingChallenge = null;
        PendingEmail = null;
        _pendingClient = null;
    }

    private CancellationTokenSource? _browser;

    /// <summary>Opens Guardian's consent page (the default browser; tests stand in for it).</summary>
    public Action<string> OpenBrowser { get; set; } = url => Infrastructure.ShellIntegration.Open(url);

    /// <summary>The consent page of the browser sign-in in progress, for "copy the link" when no browser opened.</summary>
    [ObservableProperty] private string? _browserUrl;

    /// <summary>Sign in in the browser (SSO and passwordless accounts): Guardian's consent page, then back here.</summary>
    public async Task<bool> SignInWithBrowserAsync(string address)
    {
        Status = null;
        CancelTwoFactor();
        if (GuardianClient.NormalizeAddress(address) is not { } normalized)
        {
            Status = GuardianError.BadAddress().Message;
            return false;
        }
        _browser?.Cancel();
        _browser = new CancellationTokenSource();
        var token = _browser.Token;
        var client = new GuardianClient(normalized, handler: _handler());
        Status = "Finish signing in in your browser — Vision is waiting for Guardian's answer.";
        return await Run(async () =>
        {
            var session = await GuardianBrowserSignIn.SignInAsync(client, url =>
            {
                BrowserUrl = url;
                OpenBrowser(url);
            }, cancellationToken: token).ConfigureAwait(true);
            BrowserUrl = null;
            await CompleteAsync(client, session, "browser").ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
    }

    /// <summary>Stop waiting for the browser.</summary>
    public void CancelBrowser()
    {
        _browser?.Cancel();
        BrowserUrl = null;
        Status = null;
    }

    /// <summary>Both paths end here: keep the token (Credential Manager, in parts when long), name the account, learn its
    /// orgs and models, and give every Guardian route on this address the new token.</summary>
    private async Task CompleteAsync(GuardianClient client, GuardianSession session, string method)
    {
        var account = new GuardianAccount
        {
            Address = client.Address,
            Email = session.Email,
            Name = session.Name,
            UserId = session.UserId,
            OrgId = session.OrgId,
            Method = method,
            SignedInAt = DateTimeOffset.Now,
        };
        try
        {
            _accounts.Save(account, session.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Signed in for this session at least; the next start asks again.
            Status = $"Signed in, but the sign-in couldn't be saved on this PC ({error.Message}).";
        }
        Client = client;
        Account = account;
        ApplyTokenToRoutes();
        Status ??= $"Signed in as {account.Label}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Sign out on this PC: the token goes from Credential Manager and from every Guardian route here, and the
    /// secrets revealed from Guardian's vault are let go. (Guardian still lists the sign-in under Profile › API keys ›
    /// Connected apps until it is revoked there.)</summary>
    public void SignOut()
    {
        _browser?.Cancel();
        CancelTwoFactor();
        _accounts.SignOut();
        var address = Client?.Address ?? Account?.Address;
        foreach (var route in GuardianRoutesHere(address)) _config.SetApiKey("", route);
        _host.ResetRouteCache();
        _host.Vault.ForgetSession(VaultTag);
        Client = null;
        Identity = null;
        Limits = [];
        Models = [];
        Status = "Signed out of Guardian on this PC.";
    }

    // MARK: - Who, which org, what limits

    /// <summary>Ask Guardian who this is, which orgs they're in, what they may run and how much is left.</summary>
    public async Task RefreshAsync()
    {
        if (Client is not { } client) return;
        await Run(async () =>
        {
            var me = await client.MeAsync().ConfigureAwait(true);
            Identity = me;
            Limits = me.Limits.Count > 0 ? me.Limits : await client.LimitsAsync().ConfigureAwait(true);
            if (Account is { } account)
            {
                var org = me.Orgs.FirstOrDefault(o => o.Id == (account.OrgId ?? me.OrgId)) ?? me.ActiveOrg;
                var updated = account with { Name = me.Name ?? account.Name, UserId = me.UserId, OrgId = account.OrgId ?? org?.Id, OrgName = org?.Name };
                if (updated != account) SaveAccount(updated);
                // From now on the org is said, not left to Guardian's default (which can change under the routes).
                client.OrgId ??= updated.OrgId;
            }
            Models = await client.ModelsAsync().ConfigureAwait(true);
            return true;
        }, quiet: true).ConfigureAwait(true);
    }

    /// <summary>Work in another org: Guardian calls and every Guardian route carry it from now on.</summary>
    public async Task SwitchOrgAsync(GuardianOrg org)
    {
        if (Client is not { } client || Account is not { } account) return;
        client.OrgId = org.Id;
        SaveAccount(account with { OrgId = org.Id, OrgName = org.Name });
        foreach (var route in GuardianRoutesHere(client.Address))
        {
            var changed = route.DeepCopy();
            GuardianRoutes.SetOrg(changed, org.Id);
            _config.Store(changed);
        }
        _host.ResetRouteCache();
        Status = $"Now working in {org.Name}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    private void SaveAccount(GuardianAccount account)
    {
        Account = account;
        try
        {
            _accounts.Update(account);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Kept for this session.
        }
    }

    // MARK: - Routes through the proxy

    /// <summary>The configured routes that go through this Guardian (any address when null).</summary>
    public IReadOnlyList<ProviderProfile> GuardianRoutesHere(string? address = null) =>
        _config.Providers.Where(p => GuardianRoutes.IsGuardian(p) && (address is null || GuardianRoutes.SameAddress(GuardianRoutes.AddressOf(p), address))).ToList();

    private void ApplyTokenToRoutes()
    {
        if (Client is not { Session: { } session } client) return;
        foreach (var route in GuardianRoutesHere(client.Address)) _config.SetApiKey(session.Token, route);
        _host.ResetRouteCache();
    }

    /// <summary>Use <paramref name="model"/> through Guardian: a route with the session token as its key, as the primary or
    /// beside it. Returns the route as stored.</summary>
    public ProviderProfile? UseModel(GuardianModel model, RouteUse use)
    {
        if (Client is not { Session: { } session } client) return null;
        var config = _config;
        var route = GuardianRoutes.Route(client.Address, model, client.OrgId);
        if (use == RouteUse.Subagent) route.SubagentWorker = true;
        if (use == RouteUse.Backup) route.Backup = true;
        if (config.Providers.FirstOrDefault(p => p.RouteId == route.RouteId) is { } existing)
        {
            route.SubagentWorker |= existing.SubagentWorker;
            route.Backup ??= existing.Backup;
            route.IsPrivate ??= existing.IsPrivate;
        }
        if (use == RouteUse.Primary || config.ActiveRoute is null) config.Activate(route);
        else config.Store(route);
        config.SetApiKey(session.Token, route);
        _host.ResetRouteCache();
        return route;
    }

    // MARK: - Refusals

    private DateTimeOffset _lastDialog = DateTimeOffset.MinValue;
    private string? _lastDialogKey;

    private void OnProxyRefusal(ProviderProfile route, GuardianError error)
    {
        if (Client is { } client && !GuardianRoutes.SameAddress(GuardianRoutes.AddressOf(route), client.Address)) return;
        _ = _dispatcher.BeginInvoke(() => Report(error));
    }

    /// <summary>Show a refusal as a dialog — the same one again within two minutes only once (a queue retrying, several
    /// chats at once).</summary>
    public void Report(GuardianError error)
    {
        if (!error.IsRefusal) return;
        var key = $"{error.Status}|{error.Code}|{error.Reason}|{error.Needed}";
        if (key == _lastDialogKey && DateTimeOffset.Now - _lastDialog < TimeSpan.FromMinutes(2)) return;
        _lastDialogKey = key;
        _lastDialog = DateTimeOffset.Now;
        Refused?.Invoke(error);
    }

    /// <summary>Run an action: busy while it runs, refusals as dialogs, other failures as the status line. False on failure.</summary>
    private async Task<bool> Run(Func<Task<bool>> action, bool quiet = false)
    {
        Busy = true;
        try
        {
            return await action().ConfigureAwait(true);
        }
        catch (GuardianError error)
        {
            if (error.IsRefusal) Report(error);
            if (!quiet || !error.IsRefusal) Status = error.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    // MARK: - Prompt sync (on submit, then the outcome)

    private sealed record Pending(GuardianPrompt Prompt, DateTimeOffset Started);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>A prompt the person sent in a chat: it goes to Guardian's notebook now, and again with how the turn ended.</summary>
    public void PromptSubmitted(SessionVM vm, string text)
    {
        if (Client is not { } client || string.IsNullOrWhiteSpace(text)) return;
        var prompt = new GuardianPrompt(text, DateTimeOffset.Now)
        {
            ChatId = vm.Id,
            ChatTitle = vm.Title,
            Project = vm.ProjectName,
            Model = _config.ActiveProvider?.Model,
        };
        _ = Push(client, prompt);
        if (_pending.Remove(vm.Id, out _)) { /* an older turn's outcome is no longer coming */ }
        _pending[vm.Id] = new Pending(prompt, DateTimeOffset.Now);
        vm.PropertyChanged -= OnSessionChanged;
        vm.PropertyChanged += OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SessionVM vm || e.PropertyName != nameof(SessionVM.Running) || vm.Running) return;
        if (!_pending.Remove(vm.Id, out var pending) || Client is not { } client) return;
        var outcome = Outcome(vm);
        _ = Push(client, pending.Prompt with { Outcome = outcome, DurationSeconds = (DateTimeOffset.Now - pending.Started).TotalSeconds, ChatTitle = vm.Title });
    }

    /// <summary>How a turn ended, in the Mac's words: "completed", "needs you", "failed", "stopped".</summary>
    public static string Outcome(SessionVM vm)
    {
        if (vm.Stopping) return "stopped";
        if (vm.BlockedGoal is not null || vm.PendingGates.Count > 0) return "needs you";
        if (vm.Entries.LastOrDefault() is MessageEntryVM { Role: MessageRole.Error }) return "failed";
        return "completed";
    }

    private async Task Push(GuardianClient client, GuardianPrompt prompt)
    {
        try
        {
            await client.PushPromptsAsync([prompt]).ConfigureAwait(false);
        }
        catch (GuardianError error) when (error.IsRefusal)
        {
            _ = _dispatcher.BeginInvoke(() => Report(error));
        }
        catch (GuardianError)
        {
            // The notebook is a record, not the work: a failed push never stops a chat.
        }
    }

    /// <summary>The notebook, newest first.</summary>
    public async Task<IReadOnlyList<GuardianPrompt>> NotebookAsync(int limit = 20)
    {
        IReadOnlyList<GuardianPrompt> prompts = [];
        if (Client is not { } client) return prompts;
        await Run(async () =>
        {
            prompts = await client.PromptsAsync(limit).ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
        return prompts;
    }

    // MARK: - Skills

    /// <summary>Take the account's skills into your skills folder. A skill you changed here is kept unless
    /// <paramref name="replace"/>.</summary>
    public async Task TakeSkillsAsync(bool replace)
    {
        if (Client is not { } client) return;
        await Run(async () =>
        {
            var skills = await client.SkillsAsync().ConfigureAwait(true);
            int added = 0, updated = 0, unchanged = 0, kept = 0;
            foreach (var skill in skills)
            {
                switch (GuardianSkills.Take(skill, _host.SkillLocations.UserSkills, replace, out _))
                {
                    case GuardianSkills.TakeOutcome.Added: added++; break;
                    case GuardianSkills.TakeOutcome.Updated: updated++; break;
                    case GuardianSkills.TakeOutcome.Unchanged: unchanged++; break;
                    default: kept++; break;
                }
            }
            _host.RefreshDrafts();
            _host.RefreshProjectContext();
            Status = skills.Count == 0 ? "Guardian has no skills for you yet."
                : $"Took {GuardianWords.Count(added + updated, "skill", "skills")} from Guardian"
                  + (unchanged > 0 ? $"; {unchanged} already up to date" : "")
                  + (kept > 0 ? $"; kept your own copy of {GuardianWords.Count(kept, "skill", "skills")} (choose Replace to take Guardian's)" : "") + ".";
            return true;
        }).ConfigureAwait(true);
    }

    /// <summary>The skills a deposit sends: your own (Settings › Skills) and the open project's .vision\skills.</summary>
    public IReadOnlyList<string> DepositableSkillFolders()
    {
        var folders = new List<string>();
        void Add(string root)
        {
            try
            {
                if (Directory.Exists(root)) folders.AddRange(Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        Add(_host.SkillLocations.UserSkills);
        if (_project() is { } project) Add(SkillLocations.ProjectSkills(project));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task DepositSkillsAsync()
    {
        if (Client is not { } client) return;
        var folders = DepositableSkillFolders();
        if (folders.Count == 0)
        {
            Status = "You have no skills of your own to deposit yet (Settings › Skills).";
            return;
        }
        await Run(async () =>
        {
            int sent = 0;
            var taken = new List<string>();
            foreach (var folder in folders)
            {
                if (GuardianSkills.Gather(folder) is not { } skill) continue;
                try
                {
                    await client.PutSkillAsync(skill).ConfigureAwait(true);
                    sent++;
                }
                catch (GuardianError error) when (error.Kind == GuardianErrorKind.Conflict)
                {
                    taken.Add(skill.Name);
                }
            }
            Status = $"Deposited {GuardianWords.Count(sent, "skill", "skills")} to Guardian."
                     + (taken.Count > 0 ? $" Someone else in your org already has {string.Join(", ", taken)}; rename yours to deposit it." : "");
            return true;
        }).ConfigureAwait(true);
    }

    // MARK: - Chats

    /// <summary>Guardian keeps one chat's transcript up to this size.</summary>
    public const int MaxChatBytes = 25_000_000;

    public async Task ExportChatsAsync()
    {
        if (Client is not { } client) return;
        var log = _host.Log;
        var chats = log.List();
        await Run(async () =>
        {
            int sent = 0, tooBig = 0;
            foreach (var chat in chats)
            {
                var payload = GuardianChats.ToPayload(chat.Id, chat.Title, chat.Cwd, log.LoadItems(chat.Id));
                if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaxChatBytes)
                {
                    tooBig++;
                    continue;
                }
                await client.PutChatAsync(chat.Id, chat.Title, chat.UpdatedAt, payload).ConfigureAwait(true);
                sent++;
                Status = $"Exporting chats… {sent} of {chats.Count}";
            }
            Status = $"Exported {GuardianWords.Count(sent, "chat", "chats")} to Guardian." + (tooBig > 0 ? $" {tooBig} over 25 MB stayed here." : "");
            return true;
        }).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<GuardianChatSummary>> ChatsAsync()
    {
        IReadOnlyList<GuardianChatSummary> chats = [];
        if (Client is not { } client) return chats;
        await Run(async () =>
        {
            chats = await client.ChatsAsync().ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
        return chats;
    }

    /// <summary>Bring chats back from Guardian into the chat list (one already here is left as it is).</summary>
    public async Task ImportChatsAsync(IReadOnlyList<GuardianChatSummary> chosen)
    {
        if (Client is not { } client) return;
        var log = _host.Log;
        await Run(async () =>
        {
            int brought = 0, already = 0, unreadable = 0;
            foreach (var summary in chosen)
            {
                if (log.List().Any(r => r.Id == summary.Id))
                {
                    already++;
                    continue;
                }
                var payload = await client.ChatAsync(summary.Id).ConfigureAwait(true);
                if (GuardianChats.FromPayload(payload, summary.Id) is not { } chat || log.List().Any(r => r.Id == chat.Id))
                {
                    unreadable++;
                    continue;
                }
                var cwd = chat.Cwd is { } folder && Directory.Exists(folder) ? folder : null;
                log.Upsert(chat.Id, cwd, chat.Title, PermissionPreset.WorkspaceWrite.RawValue());
                log.Resync(chat.Id, chat.Rows);
                brought++;
            }
            _host.Reload();
            Status = $"Brought {GuardianWords.Count(brought, "chat", "chats")} back from Guardian."
                     + (already > 0 ? $" {already} {(already == 1 ? "was" : "were")} already here." : "")
                     + (unreadable > 0 ? $" {unreadable} couldn't be read as a chat." : "");
            return true;
        }).ConfigureAwait(true);
    }

    // MARK: - Code projects

    public async Task UploadProjectAsync(string folder)
    {
        if (Client is not { } client) return;
        await Run(async () =>
        {
            Status = "Reading the project's files…";
            var manifest = await Task.Run(() => GuardianProjects.Collect(folder)).ConfigureAwait(true);
            if (manifest.Files.Count == 0)
            {
                Status = "That folder has no text files to upload.";
                return false;
            }
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            var progress = new Progress<(int Chunk, int Total)>(p => Status = $"Uploading {name}… part {p.Chunk} of {p.Total}");
            await client.PutProjectAsync(name, manifest.Files, progress).ConfigureAwait(true);
            var left = new List<string>();
            if (manifest.SkippedSecret > 0) left.Add($"{GuardianWords.Count(manifest.SkippedSecret, "file", "files")} with keys");
            if (manifest.SkippedBinary > 0) left.Add($"{manifest.SkippedBinary} binary");
            if (manifest.SkippedLarge > 0) left.Add($"{manifest.SkippedLarge} over 400 KB");
            Status = $"Uploaded {GuardianWords.Count(manifest.Files.Count, "file", "files")} from {name} to Guardian."
                     + (left.Count > 0 ? $" Left out: {string.Join(", ", left)}." : "")
                     + (manifest.Truncated ? " (The first 2,000 files.)" : "");
            return true;
        }).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<GuardianProjectSummary>> ProjectsAsync()
    {
        IReadOnlyList<GuardianProjectSummary> projects = [];
        if (Client is not { } client) return projects;
        await Run(async () =>
        {
            projects = await client.ProjectsAsync().ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
        return projects;
    }

    // MARK: - Vault (opt-in per secret, memory only here)

    /// <summary>The tag on secrets revealed from Guardian (signing out forgets them).</summary>
    public const string VaultTag = "guardian";

    public async Task<IReadOnlyList<GuardianVaultSecret>> VaultAsync()
    {
        IReadOnlyList<GuardianVaultSecret> secrets = [];
        if (Client is not { } client) return secrets;
        await Run(async () =>
        {
            secrets = await client.VaultAsync().ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
        return secrets;
    }

    /// <summary>Whether a Guardian secret is already held for this session.</summary>
    public bool IsHeld(GuardianVaultSecret secret) =>
        _host.Vault.SessionEntries.Any(e => e.Tags.Contains(VaultTag) && e.Description.EndsWith($"[{secret.Id}]", StringComparison.Ordinal));

    /// <summary>Reveal one secret (Guardian audits it) and hold it for this session only, usable as {{vault:NAME}}, asked
    /// before its first use in a chat. Its value is never written on this PC and never shown.</summary>
    public async Task<bool> UseSecretAsync(GuardianVaultSecret secret)
    {
        if (Client is not { } client) return false;
        return await Run(async () =>
        {
            var value = await client.RevealAsync(secret.Id).ConfigureAwait(true);
            var name = OpenClawExporter.EnvName(secret.Name);
            var kind = secret.Kind?.Contains("password", StringComparison.OrdinalIgnoreCase) == true ? VaultKind.Password : VaultKind.ApiKey;
            var held = _host.Vault.HoldForSession(name, value, kind,
                $"From Guardian, for this session only [{secret.Id}]", [VaultTag], VaultAccess.Ask);
            Status = $"{held.Name} is usable in chats until Vision quits or you sign out — as {held.Placeholder}, asked before first use.";
            return true;
        }).ConfigureAwait(true);
    }

    // MARK: - Self-test

    /// <summary>For the self-test: a signed-in look without a network (nothing is saved).</summary>
    internal void ShowDemo(bool signedIn, bool twoFactor = false)
    {
        CancelTwoFactor();
        if (!signedIn)
        {
            Client = null;
            Account = null;
            Identity = null;
            Limits = [];
            Models = [];
            if (twoFactor)
            {
                _pendingClient = new GuardianClient(GuardianClient.DefaultAddress);
                PendingChallenge = "demo-challenge";
                PendingEmail = "pat@503cm.com";
                Status = "Enter the 6-digit code from your authenticator app. Codes rotate every 30 seconds.";
            }
            return;
        }
        var session = new GuardianSession("u1", "pat@503cm.com", "Pat Hastings", "demo-token", DateTimeOffset.Now) { OrgId = "org-503" };
        Client = new GuardianClient(GuardianClient.DefaultAddress, session) { OrgId = "org-503" };
        Account = new GuardianAccount { Email = "pat@503cm.com", Name = "Pat Hastings", OrgId = "org-503", OrgName = "503CM", Method = "browser" };
        var reset = new DateTimeOffset(DateTime.Today.AddDays(7 - (int)DateTime.Today.DayOfWeek), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        Limits =
        [
            new GuardianLimit("spend", "week", 18.4, 50, "usd", reset),
            new GuardianLimit("tokens", "day", 1_240_000, 4_000_000, "tokens", DateTimeOffset.Now.Date.AddDays(1)),
            new GuardianLimit("requests", "run", 2, 6, "streams", null),
        ];
        Identity = new GuardianIdentity("u1", "pat@503cm.com", "Pat Hastings",
            [new GuardianOrg("org-503", "503CM", true), new GuardianOrg("org-cyti", "Cyticorp", false)], "org-503",
            ["chat", "files", "ai-coders"], ["chat.read", "chat.create"], Limits);
        Models =
        [
            new GuardianModel("guardian/default", "Guardian default (Claude Sonnet 5.5)", 1_000_000),
            new GuardianModel("guardian/fast", "Guardian fast", 300_000),
            new GuardianModel("global.anthropic.claude-sonnet-5-5", "Claude Sonnet 5.5", 1_000_000, 64_000),
            new GuardianModel("us.anthropic.claude-opus-5-5", "Claude Opus 5.5", 1_000_000, 48_000, InteractiveOnly: true),
        ];
        Status = "Signed in as Pat Hastings (pat@503cm.com).";
    }
}
