using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;
using static Vision.App.Views.Guide.GuideVisuals;

namespace Vision.App.Views.Guide;

/// <summary>The pages of "My Guardian account".</summary>
public enum GuardianGuidePage { SignIn, Code, Model, Role, Done }

/// <summary>"My Guardian account — models, prompts and skills in one place": sign in (the browser for SSO, or email,
/// password and the 2FA code), pick one of the models the account may run, how Vision uses it, and a first hello
/// through Guardian's proxy. The session token is the route's key, in Windows Credential Manager; the password is
/// never kept. The rest of the account (notebook, skills, chats, projects, secrets) is in Settings › Guardian.</summary>
public sealed class GuardianGuide : IWizardGuide
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly PropertyChangedEventHandler _changed;
    private bool _disposed;
    private bool _demo;

    private readonly TextBox _address = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly TextBox _email = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly PasswordBox _password = PasswordField();
    private readonly TextBox _code = new() { Padding = new Thickness(8, 5, 8, 5), Width = 160, MaxLength = 7, HorizontalAlignment = HorizontalAlignment.Left };

    private GuardianModel? _chosen;
    private RouteUse _use;

    public GuardianGuide(AppModel model, IGuideHost host)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        var (address, email) = Guardian.Remembered();
        _address.Text = address;
        _email.Text = email;
        _use = model.Config.ActiveProvider is null ? RouteUse.Primary : RouteUse.JustAdd;
        _email.TextChanged += (_, _) => _host.RefreshChrome();
        _password.PasswordChanged += (_, _) => _host.RefreshChrome();
        _code.TextChanged += (_, _) => _host.RefreshChrome();
        _changed = (_, e) =>
        {
            if (_disposed || _demo) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                // Signed in from the browser: straight on to the models.
                if (e.PropertyName == nameof(GuardianService.Client) && Guardian.IsSignedIn && Page == GuardianGuidePage.SignIn) Go(GuardianGuidePage.Model);
                else if (e.PropertyName is nameof(GuardianService.Models) or nameof(GuardianService.Busy) or nameof(GuardianService.Status)) _host.Render();
            });
        };
        Guardian.PropertyChanged += _changed;
    }

    private GuardianService Guardian => _model.Guardian;

    public GuardianGuidePage Page { get; private set; } = GuardianGuidePage.SignIn;
    object IWizardGuide.PageKey => Page;

    public void Activate()
    {
        if (Guardian.IsSignedIn && Page == GuardianGuidePage.SignIn) Go(GuardianGuidePage.Model);
    }

    private static readonly string[] SectionTitles = ["Sign in", "Two-factor code", "Pick the model", "How Vision uses it", "Done"];

    public int SectionCount => SectionTitles.Length;
    public int SectionIndex => (int)Page;
    public string Title => $"My Guardian account · {SectionTitles[SectionIndex]}";
    public string StepText => $"Step {SectionIndex + 1} of {SectionCount}";

    public string NextLabel => Page switch
    {
        GuardianGuidePage.SignIn => Guardian.IsSignedIn ? "Next" : "Sign in",
        GuardianGuidePage.Code => "Verify",
        GuardianGuidePage.Role => "Save and say hello",
        GuardianGuidePage.Done => "Start chatting",
        _ => "Next",
    };

    public bool CanAdvance => !Guardian.Busy && Page switch
    {
        GuardianGuidePage.SignIn => Guardian.IsSignedIn || (_email.Text.Trim().Length > 0 && _password.Password.Length > 0),
        GuardianGuidePage.Code => _code.Text.Count(char.IsAsciiDigit) == 6,
        GuardianGuidePage.Model => _chosen is not null,
        GuardianGuidePage.Done => _saved && !_saving,
        _ => true,
    };

    public string? SecondaryLabel => null;
    public void Secondary() { }

    public void Next()
    {
        if (!CanAdvance) return;
        switch (Page)
        {
            case GuardianGuidePage.SignIn:
                if (Guardian.IsSignedIn) Go(GuardianGuidePage.Model);
                else _ = SignInAsync();
                break;
            case GuardianGuidePage.Code:
                _ = VerifyAsync();
                break;
            case GuardianGuidePage.Done:
                _host.FinishGuide();
                break;
            default:
                Go(Page + 1);
                break;
        }
    }

    public void Back()
    {
        switch (Page)
        {
            case GuardianGuidePage.SignIn:
                _host.LeaveGuide();
                break;
            case GuardianGuidePage.Code:
            case GuardianGuidePage.Model:
                Guardian.CancelTwoFactor();
                Go(GuardianGuidePage.SignIn);
                break;
            default:
                Go(Page - 1);
                break;
        }
    }

    private void Go(GuardianGuidePage page)
    {
        Page = page;
        if (!_demo)
        {
            if (page == GuardianGuidePage.Model && Guardian.Models.Count == 0) _ = Guardian.RefreshAsync();
            if (page == GuardianGuidePage.Done && !_saved && !_saving) _ = SaveAsync();
        }
        _host.Render();
    }

    private async Task SignInAsync()
    {
        _host.RefreshChrome();
        var ok = await Guardian.SignInAsync(_address.Text, _email.Text, _password.Password);
        if (_disposed) return;
        if (ok)
        {
            _password.Clear();
            Go(GuardianGuidePage.Model);
        }
        else if (Guardian.PendingChallenge is not null) Go(GuardianGuidePage.Code);
        else _host.Render();
    }

    private async Task VerifyAsync()
    {
        var ok = await Guardian.VerifyTwoFactorAsync(_code.Text);
        if (_disposed) return;
        if (ok)
        {
            _code.Text = "";
            _password.Clear();
            Go(GuardianGuidePage.Model);
        }
        else _host.Render();
    }

    public UIElement Render()
    {
        foreach (var field in new FrameworkElement[] { _address, _email, _password, _code })
            if (field.Parent is Panel panel) panel.Children.Remove(field);
        return Page switch
        {
            GuardianGuidePage.SignIn => SignInPage(),
            GuardianGuidePage.Code => CodePage(),
            GuardianGuidePage.Model => ModelPage(),
            GuardianGuidePage.Role => RolePage(),
            _ => DonePage(),
        };
    }

    public void Dispose()
    {
        _disposed = true;
        Guardian.PropertyChanged -= _changed;
        _hello?.Cancel();
    }

    // MARK: Pages

    private UIElement StatusNote()
    {
        if (Guardian.Busy) return Busy(Guardian.Status ?? "Talking to Guardian…");
        if (Guardian.Status is { Length: > 0 } status && !Guardian.IsSignedIn && Guardian.PendingChallenge is null)
            return Note(Tone.Warning, status, "Not signed in yet");
        return new Border();
    }

    private UIElement SignInPage()
    {
        var panel = Ui.Stack(
            Heading(Guardian.IsSignedIn ? "You're signed in to Guardian" : "Sign in to Guardian"),
            Lead("Use your guardian.503cm.com account. Your account's models are the proxy; your prompts, skills, chats and code projects sync with Guardian too."));
        if (Guardian.IsSignedIn && Guardian.Account is { } account)
        {
            panel.Children.Add(Note(Tone.Success, $"{account.Address}" + (account.OrgName is { } org ? $" · {org}" : ""), $"Signed in as {account.Label}"));
            return panel;
        }
        panel.Children.Add(Field("Guardian address", _address));
        panel.Children.Add(Choice(Icons.Globe, "Sign in with your browser", "For Microsoft (Entra) sign-in or a passwordless account: Guardian opens in your browser, you pick the organization, and Vision is connected.",
            false, () => _ = Guardian.SignInWithBrowserAsync(_address.Text)));
        panel.Children.Add(Subheading("Or with your email and password"));
        panel.Children.Add(Field("Email", _email));
        panel.Children.Add(Field("Password", _password, "Vision exchanges it for a session token kept in Windows Credential Manager — the password never is."));
        panel.Children.Add(StatusNote());
        return panel;
    }

    private UIElement CodePage()
    {
        var panel = Ui.Stack(
            Heading("Enter your two-factor code"),
            Lead($"2FA is on for {Guardian.PendingEmail}. Enter the 6-digit code from your authenticator app — codes rotate every 30 seconds."));
        panel.Children.Add(Field("Code", _code));
        if (Guardian.Busy) panel.Children.Add(Busy("Checking the code…"));
        else if (Guardian.Status is { Length: > 0 } status && !status.StartsWith("Enter the 6-digit", StringComparison.Ordinal))
            panel.Children.Add(Note(Tone.Warning, status, "That code didn't take"));
        return panel;
    }

    private UIElement ModelPage()
    {
        var panel = Ui.Stack(
            Heading("Pick the model"),
            Lead("These are the models your Guardian organization allows you. guardian/default and guardian/fast follow whatever your org's admins choose."));
        if (Guardian.Models.Count == 0)
        {
            panel.Children.Add(Guardian.Busy ? Busy("Asking Guardian which models you may use…")
                : Note(Tone.Warning, Guardian.Status ?? "Guardian lists no models for you in this organization.", "No models",
                    Ui.Buttons(Ui.Button("Ask again", () => _ = Guardian.RefreshAsync()))));
            return panel;
        }
        _chosen ??= Guardian.Models.FirstOrDefault();
        var list = new StackPanel();
        foreach (var model in Guardian.Models)
        {
            var detail = new List<string>();
            if (model.ContextWindow is { } window) detail.Add($"{Fmt.Short(window)} context");
            if (model.IsAlias) detail.Add("your org picks the model behind it");
            if (model.InteractiveOnly == true) detail.Add("interactive use only");
            var content = Ui.Stack(Ui.Text(model.Title, 13), detail.Count == 0 ? null : Ui.Secondary(string.Join(" · ", detail)));
            var radio = new RadioButton { Content = content, GroupName = "guardian-guide-model", IsChecked = _chosen?.Id == model.Id, Margin = new Thickness(0, 3, 0, 3) };
            var value = model;
            radio.Checked += (_, _) =>
            {
                _chosen = value;
                _host.RefreshChrome();
            };
            list.Children.Add(radio);
        }
        panel.Children.Add(Ui.Card(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        if (Guardian.Limits.FirstOrDefault(l => l.Max is not null) is { } limit)
            panel.Children.Add(Paragraph($"Your allowance: {limit.Title.ToLowerInvariant()} {limit.Describe()}.", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    private UIElement RolePage()
    {
        var panel = Ui.Stack(Heading("How should Vision use it?"), Lead("You can change this any time in Settings › AI Models, and pick a model per chat from the menu under the message box."));
        void Use(RouteUse use, string glyph, string title, string detail) =>
            panel.Children.Add(Choice(glyph, title, detail, _use == use, () =>
            {
                _use = use;
                _host.Render();
            }));
        Use(RouteUse.Primary, Icons.Chat, "Primary", "It answers you, and runs the main agent.");
        Use(RouteUse.Subagent, Icons.People, "Subagent", "It takes side tasks the primary hands out, in parallel.");
        Use(RouteUse.Backup, Icons.Sync, "Backup", "It takes over when the primary stops answering.");
        Use(RouteUse.JustAdd, Icons.Add, "Just add it", "It's there in the model menu when you want it.");
        if (_chosen?.InteractiveOnly == true)
            panel.Children.Add(Note(Tone.Info, "Your organization allows this model only while you're there: as the primary, task lists won't run on it.", "Interactive use only"));
        panel.Children.Add(Paragraph("Not private — Guardian is an online service. Vision masks sensitive details before sending (Settings › AI Models › Sensitive data).", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    private bool _saving;
    private bool _saved;
    private string? _helloText;
    private string? _helloError;
    private ProviderProfile? _route;
    private CancellationTokenSource? _hello;

    private async Task SaveAsync()
    {
        if (_chosen is not { } model) return;
        _saving = true;
        _helloError = null;
        _helloText = null;
        _host.Render();
        try
        {
            _route = Guardian.UseModel(model, _use);
            _saved = _route is not null;
            if (_route is null)
            {
                _helloError = "The Guardian sign-in went away — go back and sign in again.";
                return;
            }
            _host.Render();
            var probe = _model.Config.Providers.First(p => p.RouteId == _route.RouteId).DeepCopy();
            probe.ApiKey = _model.Config.ApiKey(probe);
            _hello = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            _helloText = await SparkSetup.SayHelloAsync(ProviderClients.Create(probe), model.Id,
                "Say hello to someone who just connected Vision to their Guardian account, in one short, friendly sentence.", _hello.Token);
        }
        catch (LlmException error)
        {
            _helloError = error.Message;
        }
        catch (OperationCanceledException)
        {
            _helloError = "No answer within 2 minutes. Try again in a moment.";
        }
        catch (Exception error)
        {
            _helloError = AgentHost.Describe(error);
        }
        finally
        {
            _saving = false;
            if (!_disposed) _host.Render();
        }
    }

    private UIElement DonePage()
    {
        var panel = Ui.Stack(Heading(_saved ? "You're connected" : "Saving the connection"));
        if (_saving && !_saved) panel.Children.Add(Busy("Saving…"));
        if (_saved)
        {
            var use = _use switch
            {
                RouteUse.Primary => "It's your primary model now: new messages go to it through Guardian.",
                RouteUse.Subagent => "It takes subagents' side tasks.",
                RouteUse.Backup => "It stands by as a backup for the primary.",
                _ => "It's in the model menu under the message box.",
            };
            panel.Children.Add(Note(Tone.Success, use, $"{_route?.Model ?? _chosen?.Id} is set up"));
            if (_saving) panel.Children.Add(Busy("Saying hello…"));
            else if (_helloText is { } hello) panel.Children.Add(Note(Tone.Info, hello, "It answered"));
        }
        if (_helloError is { } error)
            panel.Children.Add(Note(Tone.Warning, error, _saved ? "Saved, but the hello didn't go through" : "It didn't save",
                Ui.Buttons(Ui.Button("Try again", () => _ = SaveAsync()))));
        panel.Children.Add(Aside($"Vision saved a model route named \"{_route?.Name ?? GuardianRoutes.RouteName}\" at {Guardian.Client?.ProxyBaseUrl ?? GuardianClient.DefaultAddress + GuardianRoutes.ProxyPath}, " +
                                 "with your Guardian session token as its key in Windows Credential Manager. Prompts you send go to your Guardian notebook. " +
                                 "Settings › Guardian has the rest: limits, skills, chats, code projects and secrets for Vision."));
        return panel;
    }

    // MARK: Self-test

    internal void ShowDemo(GuardianGuidePage page)
    {
        _demo = true;
        _address.Text = GuardianClient.DefaultAddress;
        _email.Text = "pat@503cm.com";
        _password.Password = page == GuardianGuidePage.SignIn ? "password" : "";
        Guardian.ShowDemo(signedIn: page >= GuardianGuidePage.Model, twoFactor: page == GuardianGuidePage.Code);
        if (page == GuardianGuidePage.SignIn) Guardian.Status = null;
        _code.Text = page == GuardianGuidePage.Code ? "1234" : "";
        _chosen = Guardian.Models.FirstOrDefault();
        _use = RouteUse.Primary;
        if (page == GuardianGuidePage.Done)
        {
            _saved = true;
            _route = GuardianRoutes.Route(GuardianClient.DefaultAddress, _chosen!, "org-503");
            _helloText = "Hello from Guardian — your 503CM models are ready when you are.";
        }
        Page = page;
        _host.Render();
    }
}
