using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

/// <summary>The pages of the Amazon Bedrock guide.</summary>
public enum BedrockPage { Welcome, Cli, SignIn, Model, Access, Connect, Done }

/// <summary>The Amazon Bedrock guide: from "I have an AWS account" to chatting with a Bedrock model,
/// for someone who has never used the AWS CLI. It installs the CLI when it's missing (Amazon's signed
/// installer, per user — no admin prompt), signs in through the browser with `aws login` (or an existing
/// SSO/CLI profile), lists the Region's models, does what a model needs before it can be used (Anthropic's
/// one-time use-case form, a Marketplace model's terms — shown with its price and licence first), waits
/// for AWS to finish, saves the route and says hello.
///
/// Like the DGX Spark guide, pages are rebuilt from state; the sign-in log is a live control moved
/// into each rebuilt page.</summary>
public sealed partial class BedrockGuide : IWizardGuide
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    /// <summary>Self-test screenshots: sample state, no CLI or network.</summary>
    private bool _demo;

    public BedrockGuide(AppModel model, IGuideHost host, BedrockPage start = BedrockPage.Welcome, ProviderProfile? route = null)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        Page = start;
        var profiles = SafeProfiles();
        _region = profiles?.DefaultRegion is { } region && BedrockRegions.Common.Any(r => r.Code == region) ? region : BedrockRegions.Default;
        // Re-running the guide for an existing Bedrock route starts from its profile and Region.
        if ((route ?? model.Config.ActiveProvider) is { Kind: ProviderKind.Bedrock } active)
        {
            if (active.AwsRegion is { Length: > 0 } activeRegion) _region = activeRegion;
            if (active.AwsProfile is { Length: > 0 } activeProfile && activeProfile != AwsSignIn.DefaultProfile)
            {
                _useExisting = true;
                _existingProfile = activeProfile;
            }
        }
    }

    public BedrockPage Page { get; private set; }
    object IWizardGuide.PageKey => Page;

    public void Activate()
    {
        // Opened past the CLI page (Settings › Models: sign in again, change the model): find the CLI
        // first; if it's gone or too old, that page comes first.
        if (Page > BedrockPage.Cli && _cliStatus is null && !_demo) _ = ResumeAsync(Page);
        else OnShown();
    }

    private async Task ResumeAsync(BedrockPage start)
    {
        await CheckCliAsync();
        if (_disposed || Page != start) return;
        if (_cliStatus is AwsCliStatus.Ready)
        {
            OnShown();
            _host.Render();
        }
        else Go(BedrockPage.Cli);
    }

    // MARK: - Header

    private static readonly string[] SectionTitles =
        ["Amazon Bedrock", "The AWS CLI", "Sign in", "Pick a model", "Enable it", "Connect DSH", "Done"];

    public int SectionCount => SectionTitles.Length;
    public int SectionIndex => (int)Page;
    public string Title => $"Amazon Bedrock guide · {SectionTitles[SectionIndex]}";
    public string StepText => $"Step {SectionIndex + 1} of {SectionCount}";

    // MARK: - Navigation

    public string NextLabel => Page switch
    {
        BedrockPage.Welcome => "Let's start",
        BedrockPage.Model => _access?.IsReady == true ? "Use this model" : "Next",
        BedrockPage.Done => "Start chatting",
        _ => "Next",
    };

    public bool CanAdvance => Page switch
    {
        BedrockPage.Cli => _cliStatus is AwsCliStatus.Ready && !_installing,
        BedrockPage.SignIn => _identity is not null && !_signingIn,
        BedrockPage.Model => _selected is not null && _access is { State: ModelAccessState.Ready or ModelAccessState.NeedsUseCaseForm
                                 or ModelAccessState.NeedsAgreement or ModelAccessState.Pending } && !_checking,
        BedrockPage.Access => _access?.IsReady == true && !_working,
        BedrockPage.Connect => _connected && !_connecting,
        _ => true,
    };

    public string? SecondaryLabel => null;

    public void Secondary() { }

    public void Next()
    {
        if (!CanAdvance) return;
        try
        {
            switch (Page)
            {
                case BedrockPage.Model:
                    _skippedAccess = _access!.IsReady;
                    Go(_skippedAccess ? BedrockPage.Connect : BedrockPage.Access);
                    break;
                case BedrockPage.Done:
                    _host.FinishGuide();
                    break;
                default:
                    Go(Page + 1);
                    break;
            }
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error, "bedrock guide");
        }
    }

    public void Back()
    {
        if (Page == BedrockPage.Welcome)
        {
            _host.LeaveGuide();
            return;
        }
        Go(Page == BedrockPage.Connect && _skippedAccess ? BedrockPage.Model : Page - 1);
    }

    public void Go(BedrockPage page)
    {
        OnHidden();
        Page = page;
        OnShown();
        _host.Render();
    }

    private void OnShown()
    {
        if (_demo) return;
        switch (Page)
        {
            case BedrockPage.Cli:
                if (_cliStatus is null && !_checkingCli) _ = CheckCliAsync();
                break;
            case BedrockPage.SignIn:
                if (_identity is null && !_signingIn && !_verifying) _ = VerifyExistingAsync();
                break;
            case BedrockPage.Model:
                if (_models is null && !_loadingModels) _ = LoadModelsAsync();
                break;
            case BedrockPage.Access:
                if (_access is { State: ModelAccessState.Pending } && !_working) _ = WaitForAccessAsync();
                break;
            case BedrockPage.Connect:
                if (!_connected && !_connecting) _ = ConnectAsync();
                break;
        }
    }

    private void OnHidden()
    {
        // Leaving a page doesn't stop a sign-in in progress (the browser may be mid-way); it does stop waiting.
        _waitCts?.Cancel();
        _waitCts = null;
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void RenderIf(BedrockPage page)
    {
        if (_disposed) return;
        if (Page == page) _host.Render();
        else _host.RefreshChrome();
    }

    public UIElement Render() => Page switch
    {
        BedrockPage.Welcome => WelcomePage(),
        BedrockPage.Cli => CliPage(),
        BedrockPage.SignIn => SignInPage(),
        BedrockPage.Model => ModelPage(),
        BedrockPage.Access => AccessPage(),
        BedrockPage.Connect => ConnectPage(),
        _ => DonePage(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        OnHidden();
        _signInCts?.Cancel();
        _installCts?.Cancel();
        _codeRequest?.TrySetResult(null);
        _disposed = true;
    }

    private static AwsProfiles? SafeProfiles()
    {
        try
        {
            return AwsProfiles.Load();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // MARK: - Welcome

    private UIElement WelcomePage() => Ui.Stack(
        Picture("bedrock-welcome", "DSH on this PC connected to Amazon Bedrock, which offers models such as Claude, Amazon Nova and Llama."),
        Heading("Use models from Amazon Bedrock"),
        Lead("Amazon Bedrock runs models like Claude, Amazon Nova, Llama and Mistral in your own AWS account. You pay AWS for what you use — there's no subscription to DSH or anyone else."),
        Subheading("What you need"),
        Bullets(
            (Icons.Cloud, "An **AWS account** you can sign in to on the AWS website. No account yet? Create one at aws.amazon.com — it needs a payment card."),
            (Icons.Stopwatch, "About **5 minutes**. A model sold through AWS Marketplace can take up to 15 more the first time."),
            (Icons.Laptop, "This PC. DSH installs the one tool it needs, the **AWS CLI**, if it isn't here yet.")),
        Subheading("What DSH does for you"),
        Steps(
            "Installs Amazon's **AWS CLI** — or updates it — if needed.",
            "Opens your **browser to sign in** to AWS. DSH never sees your password.",
            "Shows the **models** in your Region and what each one needs before first use.",
            "Fills in or accepts what's needed — **with you**: Anthropic's one-time form for Claude, or a model's **terms and price**.",
            "Saves the connection and says **hello** through Bedrock."),
        Aside("DSH talks to Bedrock directly from this PC with short-lived credentials from your sign-in, which the AWS CLI refreshes by itself. Nothing is stored in DSH's settings but the name of the sign-in (an AWS CLI \"profile\") and the Region."),
        Stuck("Your company manages AWS for you? You can still use this guide: sign in the way your company told you to (often \"IAM Identity Center\" — the guide handles that too). If a step says you're not allowed, it shows exactly what to ask your administrator for."));

    // MARK: - The AWS CLI

    private AwsCliStatus? _cliStatus;
    private bool _checkingCli;
    private bool _installing;
    private AwsCliInstallProgress? _installProgress;
    private AwsCliInstallResult? _installResult;
    private CancellationTokenSource? _installCts;
    private readonly AwsCliInstaller _installer = new();

    private AwsCli? Cli => _cliStatus is AwsCliStatus.Ready ready ? AwsCli.From(ready) : null;

    private async Task CheckCliAsync()
    {
        _checkingCli = true;
        RenderIf(BedrockPage.Cli);
        try
        {
            _cliStatus = await Task.Run(() => AwsCli.CheckAsync());
        }
        catch (Exception error)
        {
            _cliStatus = new AwsCliStatus.Broken("aws", error.Message);
        }
        finally
        {
            _checkingCli = false;
            RenderIf(BedrockPage.Cli);
        }
    }

    private async Task InstallAsync(bool winget = false, AwsCliInstallScope scope = AwsCliInstallScope.CurrentUser)
    {
        if (_installing) return;
        _installing = true;
        _installResult = null;
        _installProgress = new AwsCliInstallProgress("Starting…");
        _installCts = new CancellationTokenSource();
        RenderIf(BedrockPage.Cli);
        var progress = new Progress<AwsCliInstallProgress>(p =>
        {
            _installProgress = p;
            RenderIf(BedrockPage.Cli);
        });
        try
        {
            var current = _cliStatus;
            _installResult = winget
                ? await _installer.InstallWithWingetAsync(progress, _installCts.Token)
                : current is AwsCliStatus.TooOld or AwsCliStatus.Broken && scope == AwsCliInstallScope.CurrentUser
                    ? await _installer.UpgradeAsync(current, progress, _installCts.Token)
                    : await _installer.InstallAsync(scope, progress, _installCts.Token);
            if (_installResult.Cli is { } after) _cliStatus = after;
            else if (_installResult.Succeeded) _cliStatus = await AwsCli.CheckAsync();
        }
        catch (OperationCanceledException)
        {
            _installResult = new AwsCliInstallResult(AwsCliInstallOutcome.Cancelled, "Stopped. Nothing was installed.");
        }
        catch (Exception error)
        {
            _installResult = new AwsCliInstallResult(AwsCliInstallOutcome.Failed, error.Message);
        }
        finally
        {
            _installing = false;
            _installCts?.Dispose();
            _installCts = null;
            RenderIf(BedrockPage.Cli);
        }
    }

    private UIElement CliPage()
    {
        var panel = Ui.Stack(
            Picture("aws-cli-install", "An installer window for the AWS Command Line Interface, with checks: downloaded from Amazon, signature checked, installed."),
            Heading("One tool first: the AWS CLI"),
            Lead("The AWS CLI is Amazon's official command-line tool. DSH uses it for two things only: signing you in, and setting up your account for Bedrock."));
        if (_checkingCli)
        {
            panel.Children.Add(Busy("Looking for the AWS CLI on this PC…"));
        }
        else if (_installing)
        {
            var p = _installProgress;
            var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 6, 0, 6), Minimum = 0, Maximum = 1 };
            if (p?.Fraction is { } fraction) bar.Value = fraction;
            else bar.IsIndeterminate = true;
            var stage = p is null ? "Working…" : p.Total is > 0 ? $"{p.Stage} ({Fmt.Short((int)Math.Min(int.MaxValue, p.Done / 1024))}B of {Fmt.Short((int)Math.Min(int.MaxValue, p.Total.Value / 1024))}B)" : p.Stage;
            panel.Children.Add(Busy(stage));
            panel.Children.Add(bar);
            if (p?.Detail is { Length: > 0 } detail) panel.Children.Add(Paragraph(detail, 12, "TextFillColorSecondaryBrush"));
            panel.Children.Add(Note(Tone.Info, "Windows Installer shows its own small window while it copies files. If Windows asks whether to allow the installer, choose Yes."));
            panel.Children.Add(Ui.Buttons(Ui.Button("Stop", () => _installCts?.Cancel())));
        }
        else
        {
            switch (_cliStatus)
            {
                case AwsCliStatus.Ready ready:
                    panel.Children.Add(Note(Tone.Success, _installResult?.Succeeded == true ? $"Installed. {ready.Summary}" : ready.Summary));
                    if (_installResult?.Outcome == AwsCliInstallOutcome.InstalledRestartRecommended)
                        panel.Children.Add(Note(Tone.Info, "Windows suggests restarting at some point. You don't have to for DSH — carry on."));
                    break;
                case AwsCliStatus.TooOld old:
                    panel.Children.Add(Note(Tone.Warning, old.Summary));
                    panel.Children.Add(InstallButtons("Update the AWS CLI"));
                    break;
                case AwsCliStatus.Broken broken:
                    panel.Children.Add(Note(Tone.Warning, broken.Summary));
                    panel.Children.Add(InstallButtons("Reinstall the AWS CLI"));
                    break;
                default:
                    panel.Children.Add(Note(Tone.Info, "The AWS CLI isn't installed on this PC yet. DSH can install it now: it downloads Amazon's official installer, checks it's signed by Amazon, and installs it just for you — no administrator needed."));
                    panel.Children.Add(InstallButtons("Install the AWS CLI"));
                    break;
            }
            if (_installResult is { Succeeded: false } failed)
            {
                var extra = failed.CanTryWinget
                    ? Ui.Buttons(Ui.Button("Try installing with winget", () => _ = InstallAsync(winget: true)))
                    : null;
                panel.Children.Add(Note(failed.Outcome == AwsCliInstallOutcome.Cancelled ? Tone.Info : Tone.Error, failed.Message,
                    failed.Outcome == AwsCliInstallOutcome.Cancelled ? null : "The install didn't finish", extra));
                if (failed.LogPath is { } log) panel.Children.Add(Copyable(log, "Installer log"));
            }
        }
        panel.Children.Add(Aside("\"CLI\" means command-line interface: a program you'd normally type commands into. You won't have to — DSH runs it for you in the background. It stays installed and is kept up to date by running this guide again (or with Amazon's installer)."));
        panel.Children.Add(Stuck("Your company's PC won't allow installers? Ask your IT team to install \"AWS CLI version 2\", then press Check again. Already installed it somewhere unusual? DSH looks in the usual places and on your PATH."));
        if (!_installing && !_checkingCli && _cliStatus is not AwsCliStatus.Ready)
            panel.Children.Add(Link("Check again", () => _ = CheckCliAsync()));
        return panel;
    }

    private UIElement InstallButtons(string label)
    {
        if (_installer.PlatformProblem is { } problem) return Note(Tone.Error, problem);
        var install = Ui.Button(label, () => _ = InstallAsync(), accent: true);
        var allUsers = Link("Install for everyone on this PC instead (asks for administrator permission)",
            () => _ = InstallAsync(scope: AwsCliInstallScope.AllUsers));
        var panel = Ui.Stack(Ui.Buttons(install), allUsers);
        panel.Margin = new Thickness(0, 4, 0, 8);
        return panel;
    }

    // MARK: - Signing in

    private string _region;
    private bool _useExisting;
    private string? _existingProfile;
    private bool _remote;
    private bool _signingIn;
    private bool _verifying;
    private AwsSignInLink? _signInLink;
    private AwsSignInResult? _signInResult;
    private CallerIdentity? _identity;
    private string? _signInError;
    private CancellationTokenSource? _signInCts;
    private TaskCompletionSource<string?>? _codeRequest;
    private TextBox? _codeField;
    private readonly TextBox _signInLog = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 140,
        FontSize = 11.5,
        Padding = new Thickness(8),
    };

    /// <summary>The AWS CLI profile this guide signs in with and saves on the route.</summary>
    private string ProfileName => _useExisting && _existingProfile is { Length: > 0 } name ? name : AwsSignIn.DefaultProfile;

    private void ResetSignIn()
    {
        _identity = null;
        _signInResult = null;
        _signInError = null;
        _signInLink = null;
        ResetModels();
    }

    private void ResetModels()
    {
        _models = null;
        _selected = null;
        _access = null;
        _modelsError = null;
        _connected = false;
        _hello = null;
    }

    /// <summary>Already signed in (re-running the guide, or a CLI profile that works)? Then there's
    /// nothing to do on this page.</summary>
    private async Task VerifyExistingAsync()
    {
        if (Cli is not { } cli) return;
        var profile = ProfileName;
        if (!_useExisting && SafeProfiles()?.Find(profile) is not { Kind: AwsProfileKind.LoginSession }) return;
        _verifying = true;
        RenderIf(BedrockPage.SignIn);
        try
        {
            var identity = await new AwsSignIn(cli).VerifyAsync(profile, _region);
            if (profile == ProfileName) _identity = identity;
        }
        catch (Exception)
        {
            // Not signed in (or expired): the page offers to sign in.
        }
        finally
        {
            _verifying = false;
            RenderIf(BedrockPage.SignIn);
        }
    }

    private async Task SignInAsync()
    {
        if (Cli is not { } cli || _signingIn) return;
        var profile = ProfileName;
        _signingIn = true;
        _signInError = null;
        _signInResult = null;
        _signInLink = null;
        _signInLog.Clear();
        _signInCts = new CancellationTokenSource();
        RenderIf(BedrockPage.SignIn);
        var options = new AwsSignInOptions
        {
            Remote = _remote,
            OnOutput = line => OnUi(() =>
            {
                _signInLog.AppendText(line + Environment.NewLine);
                _signInLog.ScrollToEnd();
            }),
            OnSignInLink = link => OnUi(() =>
            {
                _signInLink = link;
                RenderIf(BedrockPage.SignIn);
            }),
            RequestAuthorizationCode = (link, ct) =>
            {
                var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => request.TrySetResult(null));
                OnUi(() =>
                {
                    _signInLink = link;
                    _codeRequest = request;
                    RenderIf(BedrockPage.SignIn);
                });
                return request.Task;
            },
        };
        try
        {
            var signIn = new AwsSignIn(cli);
            var kind = SafeProfiles()?.Find(profile)?.Kind;
            _signInResult = kind == AwsProfileKind.Sso
                ? await signIn.SsoLoginAsync(profile, options, _signInCts.Token)
                : kind is AwsProfileKind.AccessKeys or AwsProfileKind.AssumeRole or AwsProfileKind.CredentialProcess or AwsProfileKind.WebIdentity
                    ? new AwsSignInResult(AwsSignInOutcome.SignedIn, "This profile signs in by itself.")
                    : await signIn.LoginAsync(profile, _region, options, _signInCts.Token);
            if (_signInResult.Outcome == AwsSignInOutcome.UseSsoLogin)
                _signInResult = await signIn.SsoLoginAsync(profile, options, _signInCts.Token);
            if (_signInResult.Succeeded)
            {
                AwsAccounts.Invalidate(profile);
                _identity = await signIn.VerifyAsync(profile, _region, _signInCts.Token);
            }
            else
            {
                _signInError = _signInResult.Message;
            }
        }
        catch (OperationCanceledException)
        {
            _signInError = "Stopped. You can sign in again whenever you're ready.";
        }
        catch (Exception error)
        {
            _signInError = AgentHost.Describe(error);
        }
        finally
        {
            _signingIn = false;
            _codeRequest = null;
            _signInCts?.Dispose();
            _signInCts = null;
            RenderIf(BedrockPage.SignIn);
        }
    }

    private UIElement SignInPage()
    {
        var panel = Ui.Stack(
            Picture("aws-signin", "A browser showing the AWS sign-in page, and the AWS CLI confirming the sign-in."),
            Heading("Sign in to AWS"),
            Lead("DSH asks the AWS CLI to open your browser at AWS's own sign-in page — the same one as the AWS website. Sign in there and come back; DSH never sees your password."));

        // Region
        var region = new ComboBox { MinWidth = 320, IsEnabled = !_signingIn };
        foreach (var choice in BedrockRegions.Common)
            region.Items.Add(new ComboBoxItem { Content = choice.Display, Tag = choice.Code, IsSelected = choice.Code == _region });
        if (!BedrockRegions.Common.Any(r => r.Code == _region))
            region.Items.Add(new ComboBoxItem { Content = _region, Tag = _region, IsSelected = true });
        region.SelectionChanged += (_, _) =>
        {
            if (region.SelectedItem is not ComboBoxItem { Tag: string code } || code == _region) return;
            _region = code;
            ResetModels();
            _host.RefreshChrome();
        };
        panel.Children.Add(Field("AWS Region", region,
            "Where your requests run. Models differ by Region — US East (N. Virginia) and US West (Oregon) have the most. Pick one near you if you're unsure."));

        // How to sign in
        panel.Children.Add(Subheading("How do you sign in to AWS?"));
        panel.Children.Add(Choice(Icons.Globe, "With my AWS account in the browser (recommended)",
            "Your AWS email or IAM user and password, exactly as on the AWS website. DSH keeps this sign-in under the name \"dsh-bedrock\".",
            !_useExisting, () =>
            {
                if (!_useExisting) return;
                _useExisting = false;
                ResetSignIn();
                _host.Render();
            }));
        var profiles = SafeProfiles()?.Profiles ?? [];
        panel.Children.Add(Choice(Icons.People, "My company's sign-in, or an AWS CLI profile I already have",
            profiles.Count == 0
                ? "No AWS CLI profiles on this PC yet. If your company uses IAM Identity Center (SSO), ask it for the setup steps, or use the browser option."
                : "IAM Identity Center (SSO), access keys or a role that's already set up in the AWS CLI on this PC.",
            _useExisting, () =>
            {
                if (_useExisting || profiles.Count == 0) return;
                _useExisting = true;
                _existingProfile ??= profiles[0].Name;
                ResetSignIn();
                _host.Render();
            }));
        if (_useExisting && profiles.Count > 0)
        {
            var picker = new ComboBox { MinWidth = 320, IsEnabled = !_signingIn };
            foreach (var p in profiles)
                picker.Items.Add(new ComboBoxItem { Content = $"{p.Name} — {p.KindDescription}{(p.Region is { } r ? $", {r}" : "")}", Tag = p.Name, IsSelected = p.Name == _existingProfile });
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ComboBoxItem { Tag: string name } || name == _existingProfile) return;
                _existingProfile = name;
                ResetSignIn();
                _host.Render();
            };
            panel.Children.Add(Field("Profile", picker));
        }

        // State
        if (_identity is { } identity && !_signingIn)
        {
            panel.Children.Add(Note(Tone.Success, identity.Description, "Signed in"));
            panel.Children.Add(Link("Sign in as someone else", () => _ = SignInAsync()));
        }
        else if (_verifying)
        {
            panel.Children.Add(Busy("Checking whether you're already signed in…"));
        }
        else if (_signingIn)
        {
            panel.Children.Add(Busy(_remote
                ? "Open the link below on a device with a browser, sign in, then paste the code AWS shows you."
                : "Your browser opened the AWS sign-in page. Sign in there, then come back here — this page updates by itself."));
            if (_signInLink is { } link)
            {
                panel.Children.Add(Link("Didn't see it? Open the sign-in page again", () => ShellIntegration.Open(link.Url.ToString())));
                if (link.UserCode is { Length: > 0 } userCode) panel.Children.Add(Copyable(userCode, "Code to confirm in the browser"));
                if (_remote) panel.Children.Add(Copyable(link.Url.ToString(), "Sign-in link"));
            }
            if (_codeRequest is { } request)
            {
                _codeField ??= Ui.Field(placeholder: "Paste the authorization code here", mono: true);
                var submit = Ui.Button("Continue", () =>
                {
                    var code = _codeField.Text.Trim();
                    if (code.Length > 0) request.TrySetResult(code);
                }, accent: true);
                panel.Children.Add(Field("Authorization code", Detach(_codeField)));
                panel.Children.Add(Ui.Buttons(submit));
            }
            panel.Children.Add(Ui.Buttons(Ui.Button("Stop", () =>
            {
                _codeRequest?.TrySetResult(null);
                _signInCts?.Cancel();
            })));
        }
        else
        {
            var label = _useExisting && SafeProfiles()?.Find(ProfileName)?.Kind is AwsProfileKind.AccessKeys or AwsProfileKind.AssumeRole
                or AwsProfileKind.CredentialProcess or AwsProfileKind.WebIdentity
                ? "Check this profile"
                : "Sign in with AWS";
            panel.Children.Add(Ui.Buttons(Ui.Button(label, () => _ = SignInAsync(), accent: true)));
            if (!_useExisting)
            {
                var remote = Ui.Check("My browser is on another computer (show a link and a code instead)", _remote, on => _remote = on);
                remote.Margin = new Thickness(0, 8, 0, 0);
                panel.Children.Add(remote);
            }
        }
        if (_signInError is { } error && !_signingIn)
            panel.Children.Add(Note(Tone.Error, error, "Not signed in", _signInResult?.Details is { Length: > 0 } details ? Paragraph(details, 11.5, "TextFillColorSecondaryBrush") : null));
        if (_signInLog.Text.Length > 0)
        {
            var details = new Expander { Header = "What the AWS CLI said", Content = Detach(_signInLog), Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(details);
        }
        panel.Children.Add(Aside("The AWS CLI signs you in with \"aws login\": after you sign in in the browser, it keeps a session on this PC and swaps it for fresh, short-lived keys whenever DSH needs them. When the session ends (like signing out of the AWS website), DSH asks you to sign in again — one click."));
        panel.Children.Add(Stuck("The browser didn't open? Use the link above. It says you're not allowed? Your AWS administrator needs to give your user the \"SignInLocalDevelopmentAccess\" permission (an AWS managed policy). Using the account's root email works, but AWS recommends an everyday IAM user or IAM Identity Center."));
        return panel;
    }
}
