using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

// MARK: - A model, what it needs, the connection, and done

public sealed partial class BedrockGuide
{
    private IReadOnlyList<BedrockModel>? _models;
    private bool _loadingModels;
    private string? _modelsError;
    private BedrockModel? _selected;
    private ModelAccess? _access;
    private bool _checking;
    private bool _showAll;
    /// <summary>The chosen model needed nothing: Back from Connect returns to the model list.</summary>
    private bool _skippedAccess;

    private BedrockSetup? Setup => Cli is { } cli ? new BedrockSetup(cli, ProfileName, _region) : null;

    private async Task LoadModelsAsync()
    {
        if (Setup is not { } setup) return;
        _loadingModels = true;
        _modelsError = null;
        RenderIf(BedrockPage.Model);
        try
        {
            _models = await setup.ListModelsAsync();
            // Coming back to a Region that has the model chosen before keeps it.
            if (_selected is { } previous && _models.FirstOrDefault(m => m.ModelId == previous.ModelId) is { } again) _selected = again;
            else if (_selected is not null) _selected = null;
        }
        catch (Exception error)
        {
            _modelsError = Explain(error);
        }
        finally
        {
            _loadingModels = false;
            RenderIf(BedrockPage.Model);
        }
    }

    private async Task ChooseAsync(BedrockModel model)
    {
        _selected = model;
        _access = null;
        _connected = false;
        _hello = null;
        if (Setup is not { } setup || !model.CanInvoke)
        {
            RenderIf(BedrockPage.Model);
            return;
        }
        _checking = true;
        RenderIf(BedrockPage.Model);
        try
        {
            var access = await setup.CheckAccessAsync(model);
            if (_selected == model) _access = access;
        }
        catch (Exception error)
        {
            if (_selected == model) _access = new ModelAccess(ModelAccessState.Error, model.ModelId, Explain(error));
        }
        finally
        {
            _checking = false;
            RenderIf(BedrockPage.Model);
        }
    }

    private static string Explain(Exception error) => error switch
    {
        AwsCliException cli => cli.Error.Permission is { } permission
            ? $"{cli.Error.Message} (It needs the {permission} permission.)"
            : cli.Error.Message,
        _ => AgentHost.Describe(error),
    };

    private UIElement ModelPage()
    {
        var panel = Ui.Stack(
            Picture("bedrock-models", "A list of Bedrock models with what each needs: ready, a one-time form, or terms to accept."),
            Heading("Pick a model"),
            Lead($"These are the chat models Bedrock offers in {BedrockRegions.NameOf(_region)}. The recommended ones are good at coding with tools; you can switch later."));
        if (_loadingModels)
        {
            panel.Children.Add(Busy("Asking AWS which models this Region has…"));
            return panel;
        }
        if (_modelsError is { } error)
        {
            panel.Children.Add(Note(Tone.Error, error, "Couldn't list the models",
                Ui.Buttons(Ui.Button("Try again", () => _ = LoadModelsAsync()))));
            panel.Children.Add(PermissionsHelp());
            return panel;
        }
        var models = _models ?? [];
        var callable = models.Where(m => m.CanInvoke).ToList();
        if (callable.Count == 0)
        {
            panel.Children.Add(Note(Tone.Warning, $"AWS lists no chat models DSH can use in {BedrockRegions.NameOf(_region)}. Go back and pick another Region — US East (N. Virginia) or US West (Oregon) have the most."));
            return panel;
        }
        var recommended = callable.Where(m => m.Recommended).ToList();
        if (recommended.Count == 0) recommended = callable.Take(6).ToList();
        foreach (var model in recommended) panel.Children.Add(ModelCard(model));
        var others = callable.Except(recommended).ToList();
        if (others.Count > 0)
        {
            var list = Ui.Stack();
            foreach (var model in others) list.Children.Add(ModelCard(model));
            var all = new Expander
            {
                Header = $"All models in this Region ({callable.Count})",
                Content = list,
                IsExpanded = _showAll || (_selected is not null && others.Contains(_selected)),
                Margin = new Thickness(0, 4, 0, 8),
            };
            all.Expanded += (_, _) => _showAll = true;
            all.Collapsed += (_, _) => _showAll = false;
            panel.Children.Add(all);
        }
        if (_selected is not null) panel.Children.Add(AccessSummary());
        panel.Children.Add(Aside("Each model is billed by AWS per use (per thousand words or so, in and out). Claude models are made by Anthropic; Amazon Nova by Amazon; Llama by Meta. Some models are only reachable through a \"cross-Region inference profile\" — AWS runs them in several nearby Regions for capacity; DSH picks the right one for you."));
        panel.Children.Add(Stuck("The model you want isn't listed? It may not be offered in this Region: go back and try another one (the model list changes with the Region)."));
        return panel;
    }

    private UIElement ModelCard(BedrockModel model)
    {
        var details = new List<string> { model.Provider };
        if (model.AcceptsImages) details.Add("reads images");
        if (model.UsesInferenceProfile) details.Add("cross-Region");
        if (model.Recommended) details.Add("recommended");
        var selected = _selected?.ModelId == model.ModelId;
        return Choice(model.IsAnthropic ? Icons.Education : Icons.Robot, model.Name, string.Join(" · ", details), selected, () =>
        {
            if (!selected && !_checking) _ = ChooseAsync(model);
        });
    }

    /// <summary>What the chosen model needs, under the list.</summary>
    private UIElement AccessSummary()
    {
        if (_checking) return Busy($"Checking what {_selected!.Name} needs in your account…");
        if (_access is not { } access) return Note(Tone.Warning, "This model can't be called in this Region.");
        return access.State switch
        {
            ModelAccessState.Ready => Note(Tone.Success, access.Message, "Ready to use"),
            ModelAccessState.NeedsUseCaseForm => Note(Tone.Info, "Anthropic asks every AWS account a few short questions once before its first use of Claude. The next page fills them in with you.", "One short form first"),
            ModelAccessState.NeedsAgreement => Note(Tone.Info, "This model is sold through AWS Marketplace. The next page shows its price and licence for you to accept.", "Terms to accept"),
            ModelAccessState.Pending => Note(Tone.Info, access.Message, "AWS is still setting it up"),
            ModelAccessState.NotInRegion => Note(Tone.Warning, access.Message, "Not offered here"),
            ModelAccessState.NotAuthorized => Ui.Stack(Note(Tone.Error, access.Message + (access.MissingPermissions is { Count: > 0 } missing ? $" Missing: {string.Join(", ", missing)}." : ""), "Your AWS user isn't allowed yet"), PermissionsHelp()),
            _ => Note(Tone.Error, access.Message, "Couldn't check this model",
                Ui.Buttons(Ui.Button("Check again", () => _ = ChooseAsync(_selected!)))),
        };
    }

    /// <summary>The IAM policy to hand an administrator.</summary>
    private static UIElement PermissionsHelp()
    {
        var policy = new TextBox
        {
            Text = BedrockPermissions.PolicyJson,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 200,
            FontSize = 11,
            Padding = new Thickness(8),
        };
        policy.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        var copy = Ui.Button("Copy the policy", () => Clipboard.SetText(BedrockPermissions.PolicyJson));
        return new Expander
        {
            Header = "What to ask your AWS administrator for",
            Margin = new Thickness(0, 4, 0, 8),
            Content = Ui.Stack(Paragraph(BedrockPermissions.Explanation, 12.5), policy, Ui.Buttons(copy)),
        };
    }

    // MARK: - Enabling the model

    private bool _working;
    private string? _workStatus;
    private string? _workError;
    private bool _acceptTerms;
    private CancellationTokenSource? _waitCts;
    private TextBox? _company, _website, _otherIndustry, _useCases;
    private ComboBox? _industry;
    private UseCaseAudience _audience = UseCaseAudience.Internal;

    private async Task SubmitUseCaseAsync()
    {
        if (Setup is not { } setup || _selected is not { } model) return;
        var industry = _industry?.SelectedItem as string ?? "";
        var form = new UseCaseForm(_company?.Text.Trim() ?? "", _website?.Text.Trim() ?? "", industry, _useCases?.Text.Trim() ?? "",
            _audience, industry == "Other" ? _otherIndustry?.Text.Trim() ?? "" : "");
        if (form.Problem is { } problem)
        {
            _workError = problem;
            RenderIf(BedrockPage.Access);
            return;
        }
        _working = true;
        _workError = null;
        _workStatus = "Sending the form to AWS…";
        RenderIf(BedrockPage.Access);
        try
        {
            var result = await setup.SubmitUseCaseAsync(form);
            _workStatus = result == UseCaseSubmission.AlreadyOnFile
                ? "Your account already has this form on file. Checking the model…"
                : "Sent. Checking the model…";
            RenderIf(BedrockPage.Access);
            await WaitCoreAsync(setup, model);
        }
        catch (Exception error)
        {
            _workError = Explain(error);
        }
        finally
        {
            _working = false;
            RenderIf(BedrockPage.Access);
        }
    }

    private async Task AcceptTermsAsync()
    {
        if (Setup is not { } setup || _selected is not { } model || _access?.Offer is not { } offer || !_acceptTerms) return;
        _working = true;
        _workError = null;
        _workStatus = "Accepting the terms for your AWS account…";
        RenderIf(BedrockPage.Access);
        try
        {
            await setup.AcceptAgreementAsync(model.ModelId, offer.OfferToken);
            _workStatus = "Accepted. AWS is setting the model up — this can take up to 15 minutes…";
            RenderIf(BedrockPage.Access);
            await WaitCoreAsync(setup, model);
        }
        catch (Exception error)
        {
            _workError = Explain(error);
        }
        finally
        {
            _working = false;
            RenderIf(BedrockPage.Access);
        }
    }

    private async Task WaitForAccessAsync()
    {
        if (Setup is not { } setup || _selected is not { } model) return;
        _working = true;
        _workError = null;
        _workStatus = "AWS is finishing setting this model up for your account — up to 15 minutes. You can leave this page open.";
        RenderIf(BedrockPage.Access);
        try
        {
            await WaitCoreAsync(setup, model);
        }
        catch (Exception error)
        {
            _workError = Explain(error);
        }
        finally
        {
            _working = false;
            RenderIf(BedrockPage.Access);
        }
    }

    private async Task WaitCoreAsync(BedrockSetup setup, BedrockModel model)
    {
        _waitCts?.Cancel();
        _waitCts = new CancellationTokenSource();
        var progress = new Progress<ModelAccess>(access =>
        {
            _access = access;
            if (!access.IsReady) _workStatus = access.Message;
            RenderIf(BedrockPage.Access);
        });
        try
        {
            _access = await setup.WaitUntilReadyAsync(model, progress, cancellationToken: _waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Left the page: waiting resumes when it's shown again.
        }
        if (_access is { IsReady: false } still && still.State != ModelAccessState.Pending) _workError = still.Message;
        else if (_access is { State: ModelAccessState.Pending })
            _workError = "AWS hasn't finished yet. That can happen with a first subscription — press Check again in a few minutes.";
    }

    private UIElement AccessPage()
    {
        var model = _selected;
        var access = _access;
        if (model is null || access is null) return Ui.Stack(Heading("Pick a model first"), Lead("Go back and choose a model."));
        if (access.IsReady)
        {
            return Ui.Stack(
                Picture("bedrock-terms", "A model's terms, accepted, and the model enabled."),
                Heading($"{model.Name} is ready"),
                Note(Tone.Success, $"Your AWS account can use {model.Name} now."),
                Lead("Next, DSH saves the connection and sends a first message."));
        }
        return access.State switch
        {
            ModelAccessState.NeedsUseCaseForm => UseCasePage(model),
            ModelAccessState.NeedsAgreement => TermsPage(model, access),
            _ => WaitPage(model, access),
        };
    }

    private UIElement UseCasePage(BedrockModel model)
    {
        _company ??= Ui.Field(placeholder: "Your company, or your own name");
        _website ??= Ui.Field(placeholder: "https://example.com");
        _useCases ??= new TextBox
        {
            Text = "Coding help: an AI coding agent (DSH) that reads, writes and tests code in my projects on my own PC.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            Padding = new Thickness(8, 5, 8, 5),
        };
        _otherIndustry ??= Ui.Field(placeholder: "Your industry");
        if (_industry is null)
        {
            _industry = new ComboBox { MinWidth = 260 };
            foreach (var industry in UseCaseForm.Industries) _industry.Items.Add(industry);
            _industry.SelectedItem = UseCaseForm.Industries.Contains("Technology") ? "Technology" : UseCaseForm.Industries.FirstOrDefault();
            _industry.SelectionChanged += (_, _) => _host.Render();
        }
        var audience = Ui.Stack();
        foreach (var (value, label) in new[]
                 {
                     (UseCaseAudience.Internal, "Me or people in my company"),
                     (UseCaseAudience.External, "My customers"),
                     (UseCaseAudience.Both, "Both"),
                 })
        {
            var radio = new RadioButton { Content = label, GroupName = "bedrock-audience", IsChecked = _audience == value, Margin = new Thickness(0, 2, 0, 2) };
            radio.Checked += (_, _) => _audience = value;
            audience.Children.Add(radio);
        }
        var panel = Ui.Stack(
            Picture("bedrock-usecase", "A short form: company, website and what Claude will be used for, sent once per AWS account."),
            Heading("A few questions from Anthropic, once"),
            Lead($"Before an AWS account first uses Claude, Anthropic asks what it's for. AWS passes your answers on. It takes a minute, and covers every Claude model — you won't see it again."),
            Field("Company or organization", Detach(_company), "Using it on your own? Your name is fine."),
            Field("Website", Detach(_website), "Your company's site. On your own? A personal site or profile page works."),
            Field("Industry", Detach(_industry)));
        if (_industry.SelectedItem as string == "Other") panel.Children.Add(Field("Your industry", Detach(_otherIndustry)));
        panel.Children.Add(Field("Who will use it", audience));
        panel.Children.Add(Field("What you'll use it for", Detach(_useCases)));
        if (_working) panel.Children.Add(Busy(_workStatus ?? "Working…"));
        else panel.Children.Add(Ui.Buttons(Ui.Button("Send", () => _ = SubmitUseCaseAsync(), accent: true)));
        if (_workError is { } error && !_working) panel.Children.Add(Note(Tone.Error, error));
        panel.Children.Add(Aside("This is Anthropic's \"first time use\" form, the same one the AWS console shows. It's stored for your AWS account (or your whole organization) and can't be changed later, so answer as you would on the website."));
        panel.Children.Add(Stuck("Rather fill it in on the AWS website? Open Amazon Bedrock in the AWS console, choose any Claude model in the Model catalog and submit the form there — then come back and press Next."));
        return panel;
    }

    private UIElement TermsPage(BedrockModel model, ModelAccess access)
    {
        var offer = access.Offer;
        var panel = Ui.Stack(
            Picture("bedrock-terms", "A model's price and licence, with an \"I accept\" box and an \"Accept and enable\" button."),
            Heading($"{model.Name} has terms to accept"),
            Lead($"{model.Provider} sells this model through AWS Marketplace. Accepting its terms subscribes your AWS account to it; you pay only for what you use, on your AWS bill."));
        if (offer is null)
        {
            panel.Children.Add(Note(Tone.Info, access.Message));
        }
        else
        {
            var terms = Ui.Stack();
            terms.Children.Add(Subheading("Price"));
            if (offer.Prices.Count == 0) terms.Children.Add(Paragraph(offer.PricingSummary, 12.5));
            foreach (var price in offer.Prices) terms.Children.Add(Paragraph("• " + price.Display, 12.5));
            if (offer.DurationText is { } duration) terms.Children.Add(Paragraph($"Agreement length: {duration}", 12.5, "TextFillColorSecondaryBrush"));
            if (offer.RefundPolicy is { Length: > 0 } refund) terms.Children.Add(Paragraph($"Refunds: {refund}", 12.5, "TextFillColorSecondaryBrush"));
            terms.Children.Add(Subheading("Licence"));
            terms.Children.Add(offer.LegalTermsUrl is { } url
                ? Link("Read the model's licence (EULA) in your browser", () => ShellIntegration.Open(url))
                : Paragraph("The offer doesn't link a licence; see the model's page in the AWS console.", 12.5));
            panel.Children.Add(Ui.Card(terms));
            var accept = Ui.Check($"I have read and accept these terms for my AWS account", _acceptTerms, on =>
            {
                _acceptTerms = on;
                _host.Render();
            });
            accept.Margin = new Thickness(0, 8, 0, 8);
            accept.IsEnabled = !_working;
            panel.Children.Add(accept);
            if (_working) panel.Children.Add(Busy(_workStatus ?? "Working…"));
            else
            {
                var button = Ui.Button("Accept and enable", () => _ = AcceptTermsAsync(), accent: true);
                button.IsEnabled = _acceptTerms;
                panel.Children.Add(Ui.Buttons(button));
            }
        }
        if (_workError is { } error && !_working) panel.Children.Add(Note(Tone.Error, error, extra: Ui.Buttons(Ui.Button("Check again", () => _ = WaitForAccessAsync()))));
        panel.Children.Add(Aside("Models from other companies are sold on AWS Marketplace. Accepting creates a subscription in your AWS account (you'll see it under AWS Marketplace › Manage subscriptions). There's no monthly fee for Bedrock's on-demand models — only what you use."));
        panel.Children.Add(Stuck("It says your user can't subscribe? Accepting needs AWS Marketplace permissions; the \"What to ask your AWS administrator for\" section on the previous page has the exact policy."));
        return panel;
    }

    private UIElement WaitPage(BedrockModel model, ModelAccess access)
    {
        var panel = Ui.Stack(
            Picture("bedrock-terms", "A model being enabled for an AWS account."),
            Heading($"Getting {model.Name} ready"));
        if (_working) panel.Children.Add(Busy(_workStatus ?? access.Message));
        else panel.Children.Add(Note(access.State == ModelAccessState.Pending ? Tone.Info : Tone.Warning, _workError ?? access.Message,
            extra: Ui.Buttons(Ui.Button("Check again", () => _ = WaitForAccessAsync()))));
        if (access.State == ModelAccessState.NotAuthorized) panel.Children.Add(PermissionsHelp());
        panel.Children.Add(Aside("The first time an AWS account uses a model sold on AWS Marketplace, AWS sets up a subscription in the background. It usually takes a minute or two and at most about 15."));
        return panel;
    }

    // MARK: - Connect

    private bool _connecting;
    private bool _connected;
    private string? _hello;
    private string? _connectError;

    private ProviderProfile BuildProfile(BedrockModel model) => new(ProviderKind.Bedrock, "Amazon Bedrock",
        $"https://bedrock-runtime.{_region}.amazonaws.com", model.InvokeId ?? model.ModelId)
    {
        AwsProfile = ProfileName,
        AwsRegion = _region,
        Vision = model.AcceptsImages,
    };

    private async Task ConnectAsync()
    {
        if (_selected is not { } model) return;
        _connecting = true;
        _connectError = null;
        RenderIf(BedrockPage.Connect);
        var profile = BuildProfile(model);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            _hello = await SayHelloAsync(ProviderClients.Create(profile), profile.Model, timeout.Token);
            SaveProvider(profile);
            _connected = true;
        }
        catch (Exception error)
        {
            _connectError = AgentHost.Describe(error);
        }
        finally
        {
            _connecting = false;
            RenderIf(BedrockPage.Connect);
        }
    }

    private static async Task<string> SayHelloAsync(ILlmClient client, string model, CancellationToken cancellationToken)
    {
        var request = new LlmRequest("You are a helpful assistant.",
            [LlmMessage.User("Say hello to someone who just connected their coding app to Amazon Bedrock, in one short, friendly sentence.")],
            [], model, Temperature: 0.3, MaxTokens: 200, Thinking: ThinkingLevel.Off);
        var reply = new System.Text.StringBuilder();
        await foreach (var e in client.StreamAsync(request, cancellationToken))
            if (e is LlmStreamEvent.Text text) reply.Append(text.Delta);
        var result = reply.ToString().Trim();
        return result.Length == 0 ? "(The model answered with an empty reply.)" : result;
    }

    /// <summary>Make the Bedrock model DSH's route, replacing an older Bedrock route with the same profile
    /// and Region (so switching models doesn't pile up routes).</summary>
    private void SaveProvider(ProviderProfile profile)
    {
        var config = _model.Config;
        foreach (var old in config.Providers.Where(p => p.Kind == ProviderKind.Bedrock && p.AwsProfile == profile.AwsProfile
                                                         && p.AwsRegion == profile.AwsRegion && p.RouteId != profile.RouteId).ToList())
            config.RemoveProvider(old);
        config.Activate(profile);
        _model.Host.ResetRouteCache();
    }

    private UIElement ConnectPage()
    {
        var panel = Ui.Stack(
            Picture("bedrock-connected", "DSH's chat window answering through Amazon Bedrock.", 240),
            Heading("Connect DSH to Bedrock"),
            Lead($"DSH saves the connection — {_selected?.Name ?? "your model"} in {BedrockRegions.NameOf(_region)} — and sends a first message to check everything works."));
        if (_connecting) panel.Children.Add(Busy("Sending a first message through Bedrock…"));
        else if (_connected && _hello is { } hello)
            panel.Children.Add(Note(Tone.Success, hello, $"{_selected?.Name} answered"));
        else if (_connectError is { } error)
            panel.Children.Add(Note(Tone.Error, error, "The first message didn't go through",
                Ui.Buttons(Ui.Button("Try again", () => _ = ConnectAsync(), accent: true))));
        panel.Children.Add(Aside("The saved connection holds the model, the Region and the name of your AWS CLI sign-in (\"" + ProfileName + "\") — no keys. Each request gets fresh, short-lived credentials from the AWS CLI."));
        panel.Children.Add(Stuck("\"Access denied\" right after accepting a model's terms? AWS can take up to 15 minutes to finish a first subscription. Wait a little and press Try again."));
        return panel;
    }

    // MARK: - Done

    private string? _apiKey;
    private string? _apiKeyError;
    private bool _mintingKey;

    private async Task MintApiKeyAsync()
    {
        _mintingKey = true;
        _apiKeyError = null;
        RenderIf(BedrockPage.Done);
        try
        {
            var credentials = await AwsAccounts.CredentialsFor(BuildProfile(_selected!)).GetAsync();
            _apiKey = BedrockApiKeys.Create(credentials, _region, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            _apiKeyError = AgentHost.Describe(error);
        }
        finally
        {
            _mintingKey = false;
            RenderIf(BedrockPage.Done);
        }
    }

    private void SaveKeyToVault()
    {
        if (_apiKey is not { } key) return;
        try
        {
            const string name = "AWS_BEARER_TOKEN_BEDROCK";
            var vault = _model.Host.Vault;
            if (vault.Entry(name) is { } existing) vault.Update(existing with { Description = $"Bedrock API key ({BedrockRegions.NameOf(_region)}), valid 12 hours" }, key);
            else vault.Add(name, key, VaultKind.ApiKey, $"Bedrock API key ({BedrockRegions.NameOf(_region)}), valid 12 hours", ["aws", "bedrock"]);
            _apiKeyError = null;
            _apiKey = null;
            _savedKey = true;
        }
        catch (Exception error)
        {
            _apiKeyError = error.Message;
        }
        RenderIf(BedrockPage.Done);
    }

    private bool _savedKey;

    private UIElement DonePage()
    {
        var panel = Ui.Stack(
            Picture("bedrock-connected", "DSH connected to Amazon Bedrock.", 240),
            Heading("You're all set"),
            Lead($"Chats now run on {_selected?.Name ?? "Bedrock"} in your AWS account."),
            Bullets(
                (Icons.Chat, "**Start chatting** — press the button below."),
                (Icons.Robot, "**Switch models** any time: run this guide again from Settings › Models › Add Amazon Bedrock, or the setup wizard."),
                (Icons.Refresh, "**Sign-in**: when your AWS session ends, DSH says so and signs you in again with one click (the browser opens)."),
                (Icons.Info, "**Costs** show up on your AWS bill, under Amazon Bedrock (and AWS Marketplace for Marketplace models). AWS Budgets can email you before you spend more than you planned.")));
        var keys = Ui.Stack(Paragraph("Some tools (like Claude Code with Bedrock) take a Bedrock API key instead of an AWS sign-in. DSH can make one from your sign-in. It works for 12 hours (or until your sign-in ends).", 12.5));
        if (_mintingKey) keys.Children.Add(Busy("Creating a key…"));
        else if (_apiKey is { } key)
        {
            keys.Children.Add(Copyable(key, "Bedrock API key — treat it like a password"));
            keys.Children.Add(Ui.Buttons(Ui.Button("Save it in the Credentials Vault", SaveKeyToVault)));
        }
        else if (_savedKey)
            keys.Children.Add(Note(Tone.Success, "Saved in the Credentials Vault as AWS_BEARER_TOKEN_BEDROCK. The agent can use it as {{vault:AWS_BEARER_TOKEN_BEDROCK}} without seeing it."));
        else if (_selected is not null)
            keys.Children.Add(Ui.Buttons(Ui.Button("Create a Bedrock API key", () => _ = MintApiKeyAsync())));
        if (_apiKeyError is { } error) keys.Children.Add(Note(Tone.Error, error));
        panel.Children.Add(new Expander { Header = "Need a Bedrock API key for another tool?", Content = keys, Margin = new Thickness(0, 8, 0, 8) });
        panel.Children.Add(Aside("Nothing else was installed or changed: the AWS CLI, its sign-in profile, and (if you accepted any) the model's subscription in your AWS account. To remove the sign-in, run \"aws logout --profile " + ProfileName + "\" or delete the profile in %USERPROFILE%\\.aws\\config."));
        return panel;
    }

    // MARK: - Self-test

    /// <summary>For the self-test's screenshots: show <paramref name="page"/> with sample data.</summary>
    internal void ShowDemo(BedrockPage page)
    {
        _demo = true;
        _cliStatus = new AwsCliStatus.Ready(new Version(2, 37, 5), @"C:\Users\you\AppData\Local\Programs\Amazon\AWSCLIV2\aws.exe");
        _identity = new CallerIdentity("123456789012", "arn:aws:iam::123456789012:user/alice", "AIDAEXAMPLE");
        _models =
        [
            new BedrockModel("anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5", "Anthropic", ["TEXT", "IMAGE"], true, "ACTIVE",
                ["INFERENCE_PROFILE"], "us.anthropic.claude-sonnet-4-5-20250929-v1:0", Recommended: true),
            new BedrockModel("anthropic.claude-opus-4-1-20250805-v1:0", "Claude Opus 4.1", "Anthropic", ["TEXT", "IMAGE"], true, "ACTIVE",
                ["INFERENCE_PROFILE"], "us.anthropic.claude-opus-4-1-20250805-v1:0", Recommended: true),
            new BedrockModel("amazon.nova-pro-v1:0", "Nova Pro", "Amazon", ["TEXT", "IMAGE"], true, "ACTIVE", ["ON_DEMAND"], "amazon.nova-pro-v1:0", Recommended: true),
            new BedrockModel("meta.llama4-maverick-17b-instruct-v1:0", "Llama 4 Maverick 17B Instruct", "Meta", ["TEXT", "IMAGE"], true, "ACTIVE",
                ["INFERENCE_PROFILE"], "us.meta.llama4-maverick-17b-instruct-v1:0", Recommended: true),
            new BedrockModel("mistral.mistral-large-2407-v1:0", "Mistral Large (24.07)", "Mistral AI", ["TEXT"], true, "ACTIVE", ["ON_DEMAND"], "mistral.mistral-large-2407-v1:0"),
        ];
        _selected = _models[0];
        _access = new ModelAccess(ModelAccessState.Ready, _selected.ModelId, "Your account can use this model.");
        switch (page)
        {
            case BedrockPage.Cli:
                _cliStatus = new AwsCliStatus.Missing();
                break;
            case BedrockPage.SignIn:
                _identity = null;
                _signingIn = true;
                _signInLink = new AwsSignInLink(new Uri("https://signin.aws.amazon.com/v1/authorize?client_id=example"));
                _signInLog.Text = "Attempting to open your default browser.\r\nIf the browser does not open, open the following URL:\r\n\r\nhttps://signin.aws.amazon.com/v1/authorize?client_id=example\r\n";
                break;
            case BedrockPage.Access:
                _selected = _models[0];
                _access = new ModelAccess(ModelAccessState.NeedsUseCaseForm, _selected.ModelId, "Anthropic needs the one-time use-case form.", UseCaseSubmitted: false);
                break;
            case BedrockPage.Connect:
            case BedrockPage.Done:
                _connected = true;
                _hello = "Hello! I'm running on Amazon Bedrock and ready to help you build something great.";
                break;
        }
        Page = page;
        _host.Render();
    }

    /// <summary>For the self-test: the terms page of a Marketplace model.</summary>
    internal void ShowTermsDemo()
    {
        ShowDemo(BedrockPage.Model);
        _selected = _models![3];
        _access = new ModelAccess(ModelAccessState.NeedsAgreement, _selected.ModelId, "Accept the model's terms to use it.",
            new ModelOffer("offer-token", "offer-id", "https://aws.amazon.com/marketplace/pp/example-eula",
                [new ModelPrice("Input tokens", "0.00024", "1K tokens", "Input tokens"), new ModelPrice("Output tokens", "0.00097", "1K tokens", "Output tokens")],
                "No refunds on usage already charged.", "P1Y"));
        Page = BedrockPage.Access;
        _host.Render();
    }
}
