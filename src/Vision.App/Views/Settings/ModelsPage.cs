using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Guide;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App.Views.Settings;

public static class ProviderKinds
{
    public static IReadOnlyList<ProviderKind> All { get; } =
        [ProviderKind.OpenAICompat, ProviderKind.Ollama, ProviderKind.LmStudio, ProviderKind.OpenAI, ProviderKind.OpenRouter];

    /// <summary>What the route editor's Kind offers: the manual kinds, and Anthropic's Messages API (set up by its plan guide,
    /// editable here).</summary>
    public static IReadOnlyList<ProviderKind> Editable { get; } = [.. All, ProviderKind.Anthropic];

    public static string Label(this ProviderKind kind) => kind switch
    {
        ProviderKind.Ollama => "Ollama",
        ProviderKind.LmStudio => "LM Studio",
        ProviderKind.OpenAI => "OpenAI",
        ProviderKind.OpenRouter => "OpenRouter",
        ProviderKind.Bedrock => "Amazon Bedrock",
        ProviderKind.Anthropic => "Anthropic API (key)",
        _ => "OpenAI-compatible server (vLLM, SGLang, llama.cpp, DGX Spark)",
    };
}

/// <summary>Settings › AI Models: every configured model and its role — the primary that answers you, subagent
/// servers, backups — whether it is on and whether it is private, plus testing, editing and adding models; and the
/// sensitive-data shield that masks what goes to models that aren't private.</summary>
public sealed class ModelsPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();
    private readonly StackPanel _fleet = new();
    private readonly TextBlock _status = Ui.Secondary("");
    private readonly StackPanel _privacy = new();
    private readonly TextBox _tryIn = Ui.Field(placeholder: "Type or paste something to see what a model that isn't private would get");
    private readonly TextBox _tryOut = Ui.Field(mono: true);
    private readonly TextBlock _tryLabel = Ui.Secondary("What a model that isn't private would get:", 11.5);
    /// <summary>"Try it" works a moment after typing stops, off the UI thread (a pasted spreadsheet can be big).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _tryTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _tryRun;
    private readonly TextBlock _termProblems = Ui.Status("", "SystemFillColorCautionBrush");
    private bool _testing;

    public ModelsPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 820 };
        page.Children.Add(Ui.Title("AI Models"));
        var intro = Ui.Secondary("Any OpenAI-compatible server works: a Spark Swapper box, a local Ollama or LM Studio, vLLM or SGLang on your LAN, a subscription or API plan (GLM, Kimi, Qwen, OpenAI, DeepSeek), Anthropic's API with a key, or Amazon Bedrock with your AWS sign-in. Signed in to Guardian, your account's models are in every chat's model menu too. API keys are kept in Windows Credential Manager, never in the settings file.");
        intro.Margin = new Thickness(0, 6, 0, 8);
        page.Children.Add(intro);
        var add = Ui.Button("Add ▾", () => { }, accent: true);
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(Menus.Item("Spark Swapper (connection code)…", () => model.ShowSwapperGuide(), Icons.Connect));
            menu.Items.Add(Menus.Item("Subscription plan…", () => model.ShowPlanGuide(), Icons.Key));
            menu.Items.Add(Menus.Item("Z.ai (GLM)…", () => Edit(new ProviderProfile(ProviderKind.OpenAICompat, ZaiEndpoint.Coding.RouteName(),
                ZaiEndpoint.Coding.BaseUrl(), "glm-5.3") { ContextWindow = 1_000_000 }), Icons.Key));
            menu.Items.Add(Menus.Item("DeepSeek…", () => model.ShowPlanGuide(SubscriptionPlans.DeepSeek), Icons.Key));
            menu.Items.Add(Menus.Item("Anthropic API (key)…", () => model.ShowPlanGuide(SubscriptionPlans.Anthropic), Icons.Key));
            menu.Items.Add(Menus.Item("Amazon Bedrock…", () => model.ShowBedrockGuide(), Icons.Cloud));
            menu.Items.Add(Menus.Item("Other OpenAI-compatible server…",
                () => Edit(new ProviderProfile(ProviderKind.OpenAICompat, "New server", "http://127.0.0.1:8000/v1", "")), Icons.Add));
            Menus.Open(menu, add);
        };
        page.Children.Add(Ui.Buttons(
            add,
            Ui.Button("Test All", () => _ = TestAllAsync(), tooltip: "Check every model that's on answers, and show the result as a dot on each"),
            Ui.Button("Setup Wizard…", model.ShowWizard)));
        _status.Margin = new Thickness(0, 8, 0, 4);
        page.Children.Add(_status);
        page.Children.Add(Ui.Section("Your models"));
        page.Children.Add(_list);
        var roles = Ui.Secondary("Primary answers you. Subagents take side tasks in parallel. Backups take over if the primary stops answering. " +
                                 "Private models may see sensitive data; the others get it masked.");
        roles.Margin = new Thickness(2, 6, 0, 10);
        page.Children.Add(roles);
        page.Children.Add(_fleet);
        page.Children.Add(Ui.Section("Sensitive data"));
        page.Children.Add(_privacy);
        Content = Ui.Scroll(page);
        Rebuild();
        BuildPrivacy();
        model.Config.Providers.CollectionChanged += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var config = _model.Config;
        foreach (var provider in config.Providers.ToList()) _list.Children.Add(RouteRow(provider));
        if (config.Providers.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary("No models yet. Add one, or run the setup wizard.")));
        RebuildFleet();
    }

    /// <summary>One model: its switch, what and where it is, its role, its privacy, how it did when last tested.</summary>
    private Border RouteRow(ProviderProfile provider)
    {
        var config = _model.Config;
        var primary = provider.RouteId == config.ActiveRoute;
        var enabled = provider.IsEnabled;

        void Changed(bool fleet = true)
        {
            config.Persist();
            if (fleet) _model.Host.RefreshFleet();
            Rebuild();
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (provider.Kind == ProviderKind.Bedrock)
        {
            var signIn = Ui.Button("Sign in again", () => _ = SignInAgainAsync(provider));
            signIn.Margin = new Thickness(0, 0, 8, 0);
            signIn.ToolTip = "Opens your browser to sign in to AWS again — for when the sign-in has run out.";
            actions.Children.Add(signIn);
            var change = Ui.Button("Change…", () => _model.ShowBedrockGuide(BedrockPage.Model, provider));
            change.Margin = new Thickness(0, 0, 8, 0);
            change.ToolTip = "Pick another model or Region, or sign in with another AWS account, in the Bedrock guide.";
            actions.Children.Add(change);
        }
        actions.Children.Add(Ui.Button("Edit…", () => Edit(provider)));
        var remove = new Button { Content = Icons.Delete, ToolTip = "Remove", Margin = new Thickness(6, 0, 0, 0) };
        remove.SetResourceReference(StyleProperty, "IconButton");
        remove.Click += (_, _) =>
        {
            var others = config.Providers.Any(p => p.RouteId != provider.RouteId && p.IsEnabled);
            if (primary && others)
            {
                Dialog.Info("Pick another primary first",
                    $"{provider.Name} is the primary model, which answers you. Make another model the primary, then remove this one.");
                return;
            }
            var message = primary
                ? "Its saved API key is removed too. It's the only model that's on, so the setup wizard opens to set up another."
                : "Its saved API key is removed too.";
            if (!Dialog.Confirm($"Remove {provider.DisplayName}?", message, "Remove", destructive: true)) return;
            config.RemoveProvider(provider);
            try { SecretStore.Delete(SecretStore.ProviderTarget(provider.RouteId)); } catch (Exception) { }
            _model.Host.ResetRouteCache();
            if (config.ActiveRoute is null) _model.ShowWizard();
        };
        actions.Children.Add(remove);

        // The switches, on a line of their own under the name.
        var controls = new WrapPanel();
        var on = new CheckBox { Content = "On", IsChecked = enabled, Margin = new Thickness(0, 0, 16, 4), VerticalAlignment = VerticalAlignment.Center };
        on.ToolTip = "Off: this model is never used — not as the primary, not for subagents, not as a backup.";
        on.Click += (_, _) =>
        {
            if (primary && on.IsChecked != true)
            {
                on.IsChecked = true;
                Dialog.Info("Pick another primary first", $"{provider.Name} is the primary model, which answers you. Make another model the primary, then switch this one off.");
                return;
            }
            provider.Enabled = on.IsChecked == true ? null : false;
            Changed();
        };
        controls.Children.Add(on);
        var asPrimary = new RadioButton { Content = "Primary", GroupName = "primary-route", IsChecked = primary, Margin = new Thickness(0, 0, 16, 4) };
        asPrimary.ToolTip = enabled
            ? "The primary model answers you (and runs the main agent)."
            : "Make it the primary model, which answers you (it's switched on too).";
        asPrimary.Checked += (_, _) =>
        {
            if (config.ActiveRoute == provider.RouteId) return;
            config.MakePrimary(provider.RouteId);
            _model.Host.ResetRouteCache();
            Dispatcher.BeginInvoke(Rebuild);
        };
        controls.Children.Add(asPrimary);
        var worker = new CheckBox
        {
            Content = "Subagent",
            IsChecked = provider.SubagentWorker && !primary,
            IsEnabled = enabled && !primary,
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = "Use this server for subagents: side tasks the main model hands out run here, in parallel with the main model.",
        };
        worker.Click += (_, _) =>
        {
            provider.SubagentWorker = worker.IsChecked == true;
            Changed();
        };
        controls.Children.Add(worker);
        if (provider.SubagentWorker && !primary && enabled)
        {
            var parallel = new ComboBox { Width = 54, Margin = new Thickness(0, 0, 4, 4), ToolTip = "How many subagents may work on this server at the same time" };
            for (var n = 1; n <= 8; n++) parallel.Items.Add(n);
            parallel.SelectedItem = Math.Clamp(provider.SubagentParallel ?? 2, 1, 8);
            parallel.SelectionChanged += (_, _) =>
            {
                if (parallel.SelectedItem is not int chosen) return;
                provider.SubagentParallel = chosen;
                config.Persist();
                _model.Host.RefreshFleet();
            };
            controls.Children.Add(parallel);
            controls.Children.Add(WithMargin(Ui.Secondary("at a time", 12), new Thickness(0, 4, 12, 4)));
            // A second model on the main model's own machine (a Spark serves one at a time) adds no worker: say so.
            if (_model.Host.WorkerRoutes.All(w => w.RouteId != provider.RouteId))
            {
                var same = Ui.Secondary("(the same server as the main model, so not used)", 12);
                same.Margin = new Thickness(0, 4, 12, 4);
                same.ToolTip = "A server that is already the main model's runs one model at a time; a second route on it would only queue behind it.";
                controls.Children.Add(same);
            }
        }
        else
        {
            controls.Children.Add(WithMargin(new Border(), new Thickness(0, 0, 10, 0)));
        }
        var backup = new CheckBox
        {
            Content = "Backup",
            IsChecked = provider.IsBackup && !primary,
            IsEnabled = enabled && !primary,
            Margin = new Thickness(0, 0, 16, 4),
            ToolTip = "Use if the primary fails: a turn the primary can't answer runs here instead (the chat says so).",
        };
        backup.Click += (_, _) =>
        {
            provider.Backup = backup.IsChecked == true ? true : null;
            Changed(fleet: false);
        };
        controls.Children.Add(backup);
        var isPrivate = new CheckBox { IsChecked = provider.IsPrivateRoute, Margin = new Thickness(0, 0, 16, 4) };
        isPrivate.Content = Ui.Stack(Orientation.Horizontal, WithMargin(Ui.Glyph(provider.IsPrivateRoute ? Icons.Lock : Icons.Shield, 12), new Thickness(0, 0, 5, 0)),
            Ui.Text("Private", 13, wrap: false));
        isPrivate.ToolTip = (provider.IsPrivate is null
                                ? $"Decided by its address: {(RoutePrivacy.IsPrivateAddress(provider.BaseUrl) ? "your own network, so private" : "an online service, so not private")}. "
                                : "Set by you. ") +
                            "Private models may see sensitive data; for the others, Vision masks it before sending (see Sensitive data below).";
        isPrivate.Click += (_, _) =>
        {
            provider.IsPrivate = isPrivate.IsChecked == true;
            Changed();
        };
        controls.Children.Add(isPrivate);
        if (provider.IsInteractiveOnly)
            controls.Children.Add(WithMargin(Ui.Secondary("interactive use only", 12), new Thickness(0, 4, 16, 4)));
        if (primary && !enabled)
            controls.Children.Add(WithMargin(Ui.Status("Switched off, but it's the primary: switch it on, or make another model primary.", "SystemFillColorCautionBrush"),
                new Thickness(0, 4, 16, 4)));
        // What the server refused before (and Vision stopped sending), with a way to try those settings again.
        if (provider.Kind != ProviderKind.Bedrock
            && RequestAdaptationStore.Shared.Adaptation(RequestAdaptationStore.Key(provider.BaseUrl, provider.Model)) is { IsEmpty: false } learned)
        {
            var text = Ui.Secondary($"Learned from the server: {learned.Summary.Replace("`", "")}", 12);
            text.ToolTip = "The server refused these settings for this model, so Vision stopped sending them (it said so in the chat). Forget it to send them again.";
            var forget = Ui.LinkButton("Forget", () =>
            {
                RequestAdaptationStore.Shared.ForgetKey(RequestAdaptationStore.Key(provider.BaseUrl, provider.Model));
                Rebuild();
            });
            controls.Children.Add(WithMargin(Ui.Stack(Orientation.Horizontal, text, WithMargin(forget, new Thickness(6, 0, 0, 0))), new Thickness(0, 4, 16, 4)));
        }
        if (_model.RouteChecks.TryGetValue(provider.RouteId, out var check))
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, check.Ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            var result = Ui.Stack(Orientation.Horizontal, dot, Ui.Secondary(check.Ok ? "answered" : "didn't answer", 12));
            result.ToolTip = $"{check.Detail} ({Formatting.Relative(check.At)})";
            result.Margin = new Thickness(0, 4, 0, 4);
            controls.Children.Add(result);
        }

        var detail = provider.Kind == ProviderKind.Bedrock
            ? $"{provider.Model} · Amazon Bedrock · {provider.AwsRegion ?? BedrockRegions.Default} · {(provider.AwsProfile is { } aws ? $"AWS profile {aws}" : "Bedrock API key")}"
            : $"{provider.Model} · {provider.BaseUrl}";
        if (provider.ContextWindow is { } window) detail += $" · {window:N0} ctx";
        var row = Ui.Row(provider.Name, detail, actions, Icons.ForProvider(provider.Kind), below: controls);
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Edit(provider);
        };
        if (primary) row.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        if (!enabled) row.Opacity = 0.55;
        return row;
    }

    private static T WithMargin<T>(T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }

    /// <summary>How the main model and the subagent servers are arranged.</summary>
    private void RebuildFleet()
    {
        _fleet.Children.Clear();
        var config = _model.Config;
        var workers = _model.Host.WorkerRoutes;
        var main = config.Providers.FirstOrDefault(p => p.RouteId == config.ActiveRoute);
        if (main is null || config.Providers.Count < 2) return;
        var text = new StackPanel();
        if (workers.Count == 0)
        {
            text.Children.Add(Ui.Text("Several servers? Put them all to work", 13, FontWeights.SemiBold));
            var tip = Ui.Secondary($"{main.Name} is the primary. Tick “Subagent” on another server — a second DGX Spark, say — and the primary hands side tasks to it, so they run in parallel instead of queueing.");
            tip.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(tip);
        }
        else
        {
            text.Children.Add(Ui.Text($"{main.Name} is the primary · subagents run on {string.Join(", ", workers.Select(w => AgentHost.RouteLabel(w) + $" ({w.SubagentParallel ?? 2} at a time{(w.IsPrivateRoute ? "" : ", not private")})"))}", 13, FontWeights.SemiBold));
            var also = Ui.Check("When those are all busy, let subagents use the primary too", config.SubagentsUsePrimary, on =>
            {
                config.SubagentsUsePrimary = on;
                _model.Host.RefreshFleet();
            });
            also.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(also);
        }
        var card = Ui.Card(text);
        card.Margin = new Thickness(0, 0, 0, 10);
        _fleet.Children.Add(card);
    }

    // MARK: - Sensitive data

    private void BuildPrivacy()
    {
        _privacy.Children.Clear();
        var config = _model.Config;
        var privacy = config.Privacy;
        void Set(Func<PrivacySettings, PrivacySettings> change)
        {
            config.Privacy = change(config.Privacy);
            UpdateTry();
        }

        var master = Ui.Check("Mask sensitive data before sending it to models that aren't private", privacy.Mask, on => Set(p => p with { Mask = on }));
        master.FontWeight = FontWeights.SemiBold;
        var explain = Ui.Secondary("Private models get the conversation as it is. For the others, Vision replaces sensitive values with placeholders such as ⟦EMAIL_1⟧ before anything leaves this PC, and puts the real values back in what comes back — the model can still work with them. Values in the Credentials Vault are always masked.");
        explain.Margin = new Thickness(0, 4, 0, 10);
        var categories = new WrapPanel();
        void Category(string label, bool value, Func<PrivacySettings, bool, PrivacySettings> change)
        {
            var box = Ui.Check(label, value, on => Set(p => change(p, on)));
            box.Margin = new Thickness(0, 0, 18, 6);
            categories.Children.Add(box);
        }
        Category("Secrets and keys", privacy.Secrets, (p, on) => p with { Secrets = on });
        Category("Email addresses", privacy.Emails, (p, on) => p with { Emails = on });
        Category("Phone numbers", privacy.Phones, (p, on) => p with { Phones = on });
        Category("ID and card numbers", privacy.Ids, (p, on) => p with { Ids = on });
        Category("IP addresses", privacy.Ips, (p, on) => p with { Ips = on });
        Category("Custom terms", privacy.Terms, (p, on) => p with { Terms = on });

        var terms = Ui.Field(privacy.CustomTerms, "Acme Corp\nJane Doe\nre:PRJ-\\d{4}");
        terms.AcceptsReturn = true;
        terms.TextWrapping = TextWrapping.NoWrap;
        terms.Height = 96;
        terms.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        terms.VerticalContentAlignment = VerticalAlignment.Top;
        terms.TextChanged += (_, _) =>
        {
            config.Privacy = config.Privacy with { CustomTerms = terms.Text };
            UpdateTermProblems();
            UpdateTry();
        };
        var termsHint = Ui.Secondary("One per line — client names, patient names, project code names. Matched without regard to case, as whole words. A line starting with re: is a regular expression.", 11.5);
        termsHint.Margin = new Thickness(0, 4, 0, 0);
        _termProblems.Margin = new Thickness(0, 4, 0, 0);

        _tryIn.AcceptsReturn = true;
        _tryIn.TextWrapping = TextWrapping.Wrap;
        _tryIn.Height = 64;
        _tryIn.VerticalContentAlignment = VerticalAlignment.Top;
        _tryIn.TextChanged += (_, _) => UpdateTry();
        _tryOut.IsReadOnly = true;
        _tryOut.TextWrapping = TextWrapping.Wrap;
        _tryOut.MinHeight = 40;
        _tryOut.Margin = new Thickness(0, 6, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_tryOut, "What a model that isn't private would get");

        var termsTitle = Ui.Text("Custom terms", 12.5, FontWeights.SemiBold);
        termsTitle.Margin = new Thickness(0, 6, 0, 4);
        var tryTitle = Ui.Text("Try it", 12.5, FontWeights.SemiBold);
        tryTitle.Margin = new Thickness(0, 14, 0, 4);
        _privacy.Children.Add(Ui.Card(Ui.Stack(master, explain, categories, termsTitle, terms, termsHint, _termProblems, tryTitle, _tryIn,
            _tryLabel, _tryOut)));
        UpdateTermProblems();
        UpdateTry();
    }

    private void UpdateTermProblems()
    {
        var bad = SensitiveDetector.InvalidTerms(_model.Config.Privacy.TermLines);
        _termProblems.Text = bad.Count == 0 ? "" : $"Not a valid regular expression, so not used: {string.Join(", ", bad)}";
        _termProblems.Visibility = bad.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The "Try it" box: exactly what the shield would send for the text typed — once typing pauses.</summary>
    private void UpdateTry()
    {
        _tryTimer.Stop();
        _tryTimer.Tick -= OnTryTimer;
        _tryTimer.Tick += OnTryTimer;
        _tryTimer.Start();
    }

    private void OnTryTimer(object? sender, EventArgs e)
    {
        _tryTimer.Stop();
        _ = RunTryAsync();
    }

    private async Task RunTryAsync()
    {
        var run = ++_tryRun;
        var privacy = _model.Config.Privacy;
        var text = _tryIn.Text;
        IReadOnlyList<string> vault;
        try { vault = _model.Host.Vault.ValuesForRedaction().Select(v => v.Value).ToList(); }
        catch (Exception) { vault = []; }
        // With the switch off, the Credentials Vault's values are still masked; nothing else is.
        _tryLabel.Text = privacy.Mask
            ? "What a model that isn't private would get:"
            : vault.Count > 0
                ? "The switch is off, so only values from the Credentials Vault are masked. A model that isn't private would get:"
                : "The switch is off, so the text goes as it is:";
        if (text.Length == 0)
        {
            _tryOut.Text = "";
            return;
        }
        var detector = privacy.Mask
            ? new SensitiveDetector(privacy.Categories, privacy.TermLines, vault)
            : new SensitiveDetector(SensitiveCategories.None, vaultSecrets: vault);
        string masked;
        try
        {
            masked = await Task.Run(() => new ShieldMap().Mask(text, detector));
        }
        catch (Exception error)
        {
            masked = $"(Couldn't check it: {error.Message})";
        }
        if (run == _tryRun) _tryOut.Text = masked;
    }

    // MARK: - Actions

    private void Edit(ProviderProfile provider)
    {
        // Claude Code has nothing to edit here (no address, no key): its own page has the CLI, its sign-in and WSL.
        if (ClaudeCodeRoutes.IsClaudeCode(provider))
        {
            _model.ShowClaudeCode();
            return;
        }
        var editor = new ProviderEditorWindow(_model, provider) { Owner = Ui.Owner(this) };
        if (editor.ShowDialog() == true) Rebuild();
    }

    /// <summary>The route's AWS sign-in ran out: sign the same profile in again (the browser opens).</summary>
    private async Task SignInAgainAsync(ProviderProfile provider)
    {
        var profile = provider.AwsProfile ?? AwsSignIn.DefaultProfile;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _status.Text = "Finish signing in to AWS in the browser window that just opened…";
        try
        {
            var result = await Task.Run(() => AwsAccounts.SignInAgainAsync(profile, provider.AwsRegion));
            _status.Text = result.Succeeded ? $"✔ Signed in to AWS again (profile {profile})." : result.Message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, result.Succeeded ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            if (result.Succeeded && _model.Host.AwsSignInProfile == profile) _model.Host.Banner = null;
        }
        catch (Exception error)
        {
            _status.Text = $"Signing in to AWS failed: {error.Message}";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
    }

    /// <summary>Ask every model that is on whether it answers; each row shows how it did.</summary>
    private async Task TestAllAsync()
    {
        if (_testing) return;
        _testing = true;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _status.Text = "Testing…";
        var routes = _model.Config.Providers.Where(p => p.IsEnabled || p.RouteId == _model.Config.ActiveRoute).ToList();
        var results = await Task.WhenAll(routes.Select(async route =>
        {
            var profile = route.DeepCopy();
            var key = _model.Config.ApiKey(route);
            profile.ApiKey = key.Length == 0 ? null : key;
            try
            {
                // Z.ai lists models for any valid key on any endpoint; only a reply proves the endpoint fits the key.
                if (ZaiEndpoints.Of(profile.BaseUrl) is { } endpoint)
                {
                    var result = await ZaiDiagnosis.RunAsync(endpoint, profile.Model, async (where, ct) =>
                    {
                        var request = new LlmRequest("", [LlmMessage.User("Reply with OK.")], [], profile.Model, MaxTokens: 1);
                        await foreach (var _ in ProviderClients.Create(profile with { BaseUrl = where.BaseUrl() }).StreamAsync(request, ct)) { }
                    });
                    return (route, new AppModel.RouteCheck(result.Ok, result.Message, DateTimeOffset.Now));
                }
                var client = ProviderClients.Create(profile);
                var models = await client.ListModelsAsync();
                var info = await client.ModelInfoAsync();
                var window = info.ContextWindow is { } w ? $", {w:N0}-token window" : "";
                return (route, new AppModel.RouteCheck(true, profile.Kind == ProviderKind.Bedrock
                    ? $"Connected to Amazon Bedrock — {models.Count} model(s){window}"
                    : $"Connected — {models.Count} model(s) served{window}", DateTimeOffset.Now));
            }
            catch (Exception error)
            {
                return (route, new AppModel.RouteCheck(false, AgentHost.Describe(error), DateTimeOffset.Now));
            }
        }));
        foreach (var (route, check) in results) _model.RouteChecks[route.RouteId] = check;
        var failed = results.Count(r => !r.Item2.Ok);
        _status.Text = failed == 0 ? $"✔ All {results.Length} answered." : $"{failed} of {results.Length} didn't answer — hover a red dot for why.";
        _status.SetResourceReference(TextBlock.ForegroundProperty, failed == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        _testing = false;
        Rebuild();
    }
}

/// <summary>Edit one route: kind, address, key, model (with discovery), limits, thinking, headers.</summary>
public sealed class ProviderEditorWindow : Window
{
    private readonly AppModel _model;
    private readonly ProviderProfile _original;
    private readonly ComboBox _kind = new();
    private readonly TextBox _name = Ui.Field();
    private readonly TextBox _url = Ui.Field(mono: true);
    private readonly PasswordBox _key = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly ComboBox _modelName = new() { IsEditable = true };
    private readonly TextBox _temperature = Ui.Field(placeholder: "default", width: 100);
    private readonly TextBox _maxTokens = Ui.Field(placeholder: "default", width: 120);
    private readonly TextBox _context = Ui.Field(placeholder: "auto", width: 120);
    private readonly ComboBox _thinking = new() { MinWidth = 260 };
    private readonly CheckBox _vision = new() { Content = "The model can read images" };
    private readonly CheckBox _interactiveOnly = new() { Content = "Interactive use only (plan terms)" };
    /// <summary>The self-signed certificate this route trusts (null once forgotten).</summary>
    private string? _pin;
    private readonly TextBox _headers = Ui.Field(mono: true);
    private readonly TextBlock _status = Ui.Secondary("");
    private readonly StackPanel _keyRow;
    /// <summary>Z.ai's endpoint (Coding Plan, General API, China — Coding Plan, China — General API), shown for a Z.ai route.</summary>
    private readonly ComboBox _endpoint = new() { MinWidth = 260 };
    private readonly TextBlock _endpointDetail = Ui.Secondary("", 11.5);
    private readonly StackPanel _endpointRow;
    private readonly TextBlock _thinkingHint = Ui.Secondary("", 11.5);
    private readonly TextBlock _temperatureHint = Ui.Secondary("", 11.5);
    /// <summary>What the Test button found, in plain words, and a switch to the endpoint the key belongs to.</summary>
    private readonly TextBlock _testResult = Ui.Text("", 12.5);
    private readonly Button _switchEndpoint = Ui.Button("Switch", () => { }, accent: true);
    private ZaiEndpoint? _worksOn;
    private bool _testing;
    /// <summary>The kind picker's entry for Z.ai: an OpenAI-compatible route at one of Z.ai's endpoints.</summary>
    private const string ZaiKind = "zai";

    public ProviderEditorWindow(AppModel model, ProviderProfile profile)
    {
        _model = model;
        _original = profile;
        Title = "Model Server";
        Width = 620;
        Height = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        // A Bedrock route's kind, address and sign-in come from its guide; the rest can be tuned here.
        var bedrock = profile.Kind == ProviderKind.Bedrock;
        var zai = !bedrock && ZaiEndpoints.Of(profile.BaseUrl) is not null;
        foreach (var kind in bedrock ? [ProviderKind.Bedrock] : ProviderKinds.Editable)
            _kind.Items.Add(new ComboBoxItem { Content = kind.Label(), Tag = kind, IsSelected = !zai && kind == profile.Kind });
        // Z.ai (GLM) is first-class: an OpenAI-compatible route, with its endpoints to pick from and its models listed.
        if (!bedrock) _kind.Items.Add(new ComboBoxItem { Content = "Z.ai (GLM)", Tag = ZaiKind, IsSelected = zai });
        _kind.IsEnabled = !bedrock;
        _url.IsReadOnly = bedrock;
        foreach (var endpoint in ZaiEndpoints.All)
            _endpoint.Items.Add(new ComboBoxItem { Content = endpoint.Name(), Tag = endpoint, IsSelected = ZaiEndpoints.Of(profile.BaseUrl) == endpoint });
        _endpoint.SelectionChanged += (_, _) =>
        {
            if (_endpoint.SelectedItem is not ComboBoxItem { Tag: ZaiEndpoint chosen }) return;
            var before = ZaiEndpoints.Of(_url.Text);
            _url.Text = chosen.BaseUrl();
            // A name Vision gave the route follows the endpoint; one the user typed stays.
            if (_name.Text.Trim().Length == 0 || before is { } was && _name.Text.Trim() == was.RouteName()) _name.Text = chosen.RouteName();
            _endpointDetail.Text = chosen.Detail();
            ClearTest();
        };
        _kind.SelectionChanged += (_, _) =>
        {
            // Anthropic's API: its address and current model, unless the route already points at Anthropic (or a proxy for it).
            if (SelectedKind == ProviderKind.Anthropic && _kind.SelectedItem is ComboBoxItem { Tag: ProviderKind.Anthropic }
                && KnownProviders.Of(_url.Text) != KnownProvider.Anthropic && _original.Kind != ProviderKind.Anthropic)
            {
                _url.Text = AnthropicClient.DefaultBaseUrl;
                if (_name.Text.Trim().Length == 0 || _name.Text.Trim() == "New server") _name.Text = "Anthropic";
                if (_modelName.Text.Trim().Length == 0) _modelName.Text = "claude-opus-5-5";
            }
            if (IsZai && ZaiEndpoints.Of(_url.Text) is null)
            {
                _url.Text = ZaiEndpoint.Coding.BaseUrl();
                if (_name.Text.Trim().Length == 0 || _name.Text.Trim() == "New server") _name.Text = ZaiEndpoint.Coding.RouteName();
                if (_modelName.Text.Trim().Length == 0) _modelName.Text = "glm-5.3";
            }
            UpdateKeyVisibility();
            UpdateZai();
        };
        _url.TextChanged += (_, _) =>
        {
            UpdateThinkingChoices();
            ClearTest();
            if (IsZai) SelectEndpoint(ZaiEndpoints.Of(_url.Text));
        };
        _name.Text = profile.Name;
        _url.Text = profile.BaseUrl;
        _key.Password = model.Config.ApiKey(profile);
        _modelName.Text = profile.Model;
        _modelName.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        _temperature.Text = profile.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _maxTokens.Text = profile.MaxOutputTokens?.ToString() ?? "";
        _context.Text = profile.ContextWindow?.ToString() ?? "";
        _chosenEffort = profile.ReasoningEffort;
        _thinking.SelectionChanged += (_, _) =>
        {
            if (!_fillingThinking) _chosenEffort = _thinking.SelectedItem is ComboBoxItem { Tag: string raw } && raw.Length > 0 ? raw : null;
        };
        _modelName.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            UpdateThinkingChoices();
            ClearTest();
        }));
        _modelName.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateThinkingChoices);
        _vision.IsChecked = profile.Vision ?? ModelRequestRules.For(profile).AcceptsImages ?? true;
        _interactiveOnly.IsChecked = profile.IsInteractiveOnly;
        _interactiveOnly.ToolTip = "The plan's terms allow only interactive use (the Qwen Coding Plan): task lists and other unattended work won't run on it as the primary.";
        _pin = profile.PinnedCertificate;
        _headers.AcceptsReturn = true;
        _headers.Height = 70;
        _headers.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _headers.Text = string.Join("\n", (profile.CustomHeaders ?? []).Select(h => $"{h.Key}: {h.Value}"));
        Placeholder.SetText(_headers, "X-Org-Id: 1234");

        _keyRow = Labeled("API key", _key, "Stored in Windows Credential Manager and only sent to this server.");
        _endpointRow = Labeled("Endpoint", _endpoint);
        _endpointDetail.Margin = new Thickness(0, 4, 0, 0);
        _endpointRow.Children.Add(_endpointDetail);
        _testResult.TextWrapping = TextWrapping.Wrap;
        _testResult.Margin = new Thickness(0, 8, 0, 0);
        _switchEndpoint.Click += (_, _) =>
        {
            if (_worksOn is not { } better) return;
            SelectEndpoint(better);
            _ = TestAsync();
        };
        _switchEndpoint.Visibility = Visibility.Collapsed;
        var test = Ui.Button("Test", () => _ = TestAsync(), tooltip: "Send a one-word request with this key and model, and say in plain words what went wrong if it fails");
        var testRow = Ui.Stack(Orientation.Horizontal, test, WithGap(_switchEndpoint));
        testRow.Margin = new Thickness(0, 4, 0, 0);
        var form = new StackPanel();
        form.Children.Add(Ui.Section("Server"));
        form.Children.Add(Ui.Card(Ui.Stack(
            Labeled("Kind", _kind),
            _endpointRow,
            Labeled("Name", _name),
            Labeled("Base URL", _url, bedrock
                ? "Bedrock's endpoint for the Region. To change the Region, model or AWS sign-in, use Change… on the Models page."
                : "The OpenAI-compatible endpoint, usually ending in /v1."),
            _keyRow,
            testRow,
            _testResult)));
        form.Children.Add(Ui.Section("Model"));
        var discover = Ui.Button("Discover", () => _ = DiscoverAsync());
        var modelRow = new DockPanel();
        DockPanel.SetDock(discover, Dock.Right);
        discover.Margin = new Thickness(8, 0, 0, 0);
        modelRow.Children.Add(discover);
        modelRow.Children.Add(_modelName);
        var detect = Ui.Button("Detect", () => _ = DetectAsync());
        detect.Margin = new Thickness(8, 0, 0, 0);
        form.Children.Add(Ui.Card(Ui.Stack(
            Labeled("Model", modelRow),
            Labeled("Context window", Ui.Stack(Orientation.Horizontal, _context, detect),
                "Leave blank to auto-detect from the server (vLLM/SGLang report max_model_len). Only set it to force a smaller window."),
            WithHint(Labeled("Default thinking", _thinking, "Only the levels this model takes are offered. Each chat can override it (/think)."), _thinkingHint),
            WithHint(Labeled("Temperature", _temperature), _temperatureHint),
            Labeled("Max output tokens", _maxTokens),
            _vision)));
        form.Children.Add(Ui.Section("Use"));
        var use = Ui.Stack(_interactiveOnly);
        if (_pin is { } pin)
        {
            var pinText = Ui.Secondary($"Trusts this server's own certificate (SHA-256 {CertificateProbe.Shorten(pin)}).", 12);
            pinText.ToolTip = pin;
            var forget = Ui.LinkButton("Forget", () => { });
            forget.Click += (_, _) =>
            {
                _pin = null;
                pinText.Text = "The certificate will be forgotten when you save: only certificates Windows trusts will be accepted.";
                forget.IsEnabled = false;
            };
            var pinRow = Ui.Stack(Orientation.Horizontal, pinText, forget);
            pinRow.Margin = new Thickness(0, 8, 0, 0);
            use.Children.Add(pinRow);
        }
        form.Children.Add(Ui.Card(use));
        form.Children.Add(Ui.Section("Custom headers"));
        form.Children.Add(Ui.Card(Ui.Stack(_headers, Ui.Secondary("One per line, as Name: value. Sent with every request."))));

        var save = Ui.Button("Save", Save, accent: true);
        save.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => DialogResult = false);
        cancel.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(20, 10, 20, 16) };
        var buttons = Ui.Buttons(cancel, save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(Ui.Scroll(form, new Thickness(20, 4, 20, 8)));
        Content = root;
        UpdateKeyVisibility();
        UpdateZai();
        UpdateThinkingChoices();
    }

    private static T WithGap<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(8, 0, 0, 0);
        return element;
    }

    private static StackPanel WithHint(StackPanel labeled, TextBlock hint)
    {
        hint.Margin = new Thickness(0, 4, 0, 0);
        labeled.Children.Add(hint);
        return labeled;
    }

    private bool IsZai => _kind.SelectedItem is ComboBoxItem { Tag: ZaiKind };

    private void SelectEndpoint(ZaiEndpoint? endpoint)
    {
        foreach (ComboBoxItem item in _endpoint.Items)
            if (item.Tag is ZaiEndpoint e && e == endpoint && !item.IsSelected) item.IsSelected = true;
    }

    /// <summary>A Z.ai route shows its endpoint picker and offers its models.</summary>
    private void UpdateZai()
    {
        _endpointRow.Visibility = IsZai ? Visibility.Visible : Visibility.Collapsed;
        if (!IsZai) return;
        SelectEndpoint(ZaiEndpoints.Of(_url.Text) ?? ZaiEndpoint.Coding);
        _endpointDetail.Text = (ZaiEndpoints.Of(_url.Text) ?? ZaiEndpoint.Coding).Detail();
        if (_modelName.Items.Count == 0 && SubscriptionPlans.Find(SubscriptionPlans.Zai) is { } plan)
            foreach (var m in plan.Models) _modelName.Items.Add(m.Id);
    }

    private string? _chosenEffort;
    private bool _fillingThinking;

    /// <summary>The thinking levels this model takes (GLM-5.3: Low, High, Max; Kimi K2.6: Off, On; Kimi K2.7 Code: none).</summary>
    private void UpdateThinkingChoices()
    {
        var rules = ModelRequestRules.For(Draft());
        _fillingThinking = true;
        try
        {
            _thinking.Items.Clear();
            _thinking.Items.Add(new ComboBoxItem { Content = "Server default", Tag = "", IsSelected = _chosenEffort is null });
            var wanted = ThinkingLevels.FromRaw(_chosenEffort) is { } level ? rules.NearestLevel(level) : null;
            foreach (var offered in rules.ThinkingLevels)
                _thinking.Items.Add(new ComboBoxItem
                {
                    Content = $"{rules.Label(offered)} — {(rules.Thinking.Kind == ThinkingControlKind.Toggle && offered != ThinkingLevel.Off ? "Thinks before answering" : offered.Blurb())}",
                    Tag = offered.RawValue(),
                    IsSelected = wanted == offered,
                });
            if (_thinking.SelectedItem is null) _thinking.SelectedIndex = 0;
        }
        finally
        {
            _fillingThinking = false;
        }
        _thinkingHint.Text = rules.ThinkingLevels.Count == 0
            ? rules.AlwaysThinks ? "This model always thinks and takes no thinking setting." : "This model has no thinking setting."
            : rules.AlwaysThinks ? "This model always thinks; the setting picks how much." : "";
        _thinkingHint.Visibility = _thinkingHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _temperatureHint.Text = !rules.SendsTemperature
            ? "This model fixes its own temperature, so Vision doesn't send one."
            : rules.TemperatureRange is var (min, max) ? $"This model takes {min:0.#}–{max:0.#}; a higher setting is sent as {max:0.#}." : "";
        _temperatureHint.Visibility = _temperatureHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearTest()
    {
        if (_testing) return;
        _testResult.Text = "";
        _worksOn = null;
        _switchEndpoint.Visibility = Visibility.Collapsed;
    }

    /// <summary>A one-word request with this key and model; a failure in plain words. For Z.ai, a key that doesn't fit the
    /// endpoint is tried on the others, so the answer can say which one it belongs to.</summary>
    private async Task TestAsync()
    {
        if (_testing) return;
        _testing = true;
        _worksOn = null;
        _switchEndpoint.Visibility = Visibility.Collapsed;
        _testResult.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _testResult.Text = "Testing…";
        var probe = Probe();
        var model = probe.Model;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (model.Length == 0)
            {
                _testResult.Text = "Pick a model first.";
                return;
            }
            async Task Ping(ProviderProfile route, CancellationToken ct)
            {
                var request = new LlmRequest("", [LlmMessage.User("Reply with OK.")], [], route.Model, MaxTokens: 1);
                await foreach (var _ in ProviderClients.Create(route).StreamAsync(request, ct)) { }
            }
            bool ok;
            string message;
            if (ZaiEndpoints.Of(probe.BaseUrl) is { } endpoint)
            {
                var result = await ZaiDiagnosis.RunAsync(endpoint, model, (where, ct) => Ping(probe with { BaseUrl = where.BaseUrl() }, ct), timeout.Token);
                (ok, message, _worksOn) = (result.Ok, result.Message, result.WorksOn);
            }
            else
            {
                try
                {
                    await Ping(probe, timeout.Token);
                    (ok, message) = (true, $"✔ {probe.Name} answered with {model}.");
                }
                catch (LlmException error) when (error.Kind == LlmErrorKind.Http
                                                 && SubscriptionPlans.ForBaseUrl(probe.BaseUrl) is { } found
                                                 && error.Message == LlmException.Http(error.StatusCode, error.Body).Message)
                {
                    var (plan, region) = found;
                    (ok, message) = (false, SubscriptionPlans.ExplainError(plan, region, error.StatusCode, error.Body, model));
                }
            }
            _testResult.Text = message;
            _testResult.SetResourceReference(TextBlock.ForegroundProperty, ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            if (_worksOn is { } better)
            {
                _switchEndpoint.Content = $"Switch to {better.Name()}";
                _switchEndpoint.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            _testResult.Text = "No answer within a minute. Check the address, then try again.";
            _testResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        catch (Exception error)
        {
            _testResult.Text = AgentHost.Describe(error);
            _testResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        finally
        {
            _testing = false;
        }
    }

    /// <summary>Self-test: the Z.ai editor after a Coding Plan key was tested on the General API (no network).</summary>
    internal void ShowZaiDemo()
    {
        SelectEndpoint(ZaiEndpoint.General);
        _key.Password = "0123456789abcdef.ZyXwVuTsRq";
        _worksOn = ZaiEndpoint.Coding;
        _testResult.Text = ZaiDiagnosis.SwitchAdvice(ZaiEndpoint.General, ZaiEndpoint.Coding);
        _testResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        _switchEndpoint.Content = $"Switch to {ZaiEndpoint.Coding.Name()}";
        _switchEndpoint.Visibility = Visibility.Visible;
        UpdateKeyVisibility();
    }

    private static StackPanel Labeled(string label, UIElement control, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var title = Ui.Text(label, 12.5, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(title);
        panel.Children.Add(control);
        if (hint is not null)
        {
            var detail = Ui.Secondary(hint, 11.5);
            detail.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(detail);
        }
        return panel;
    }

    private ProviderKind SelectedKind => _kind.SelectedItem is ComboBoxItem { Tag: ProviderKind kind } ? kind : ProviderKind.OpenAICompat;

    private void UpdateKeyVisibility()
    {
        var probe = new ProviderProfile(SelectedKind, "", _url.Text.Trim(), "") { AwsProfile = SelectedKind == ProviderKind.Bedrock ? _original.AwsProfile : null };
        _keyRow.Visibility = probe.NeedsApiKey || _key.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProviderProfile Draft()
    {
        var headers = _headers.Text.Split('\n')
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
        // Everything the form doesn't show carries over (whether the route serves subagents and how many at a time, whether it
        // is on, a backup, private) — a fresh profile would silently drop them.
        return _original with
        {
            Kind = SelectedKind,
            Name = _name.Text.Trim(),
            BaseUrl = _url.Text.Trim(),
            Model = _modelName.Text.Trim(),
            ApiKey = null,
            Temperature = double.TryParse(_temperature.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null,
            MaxOutputTokens = int.TryParse(_maxTokens.Text, out var m) && m > 0 ? m : null,
            ContextWindow = int.TryParse(_context.Text.Replace(",", "").Replace("_", ""), out var c) && c > 0 ? c : null,
            ReasoningEffort = _thinking.SelectedItem is ComboBoxItem { Tag: string raw } && raw.Length > 0 ? raw : null,
            // Unticked: no images. Ticked: the default — unless the model is known not to read them (Z.ai's text models), when
            // ticking it is the user's own say-so.
            Vision = _vision.IsChecked != true ? false
                : ModelRequestRules.For(_original with { Kind = SelectedKind, BaseUrl = _url.Text.Trim(), Model = _modelName.Text.Trim() }).AcceptsImages == false ? true : null,
            CustomHeaders = headers.Count == 0 ? null : headers,
            AwsProfile = SelectedKind == ProviderKind.Bedrock ? _original.AwsProfile : null,
            AwsRegion = SelectedKind == ProviderKind.Bedrock ? _original.AwsRegion : null,
            InteractiveOnly = _interactiveOnly.IsChecked == true ? true : null,
            PinnedCertificate = _pin,
        };
    }

    private ProviderProfile Probe()
    {
        var probe = Draft();
        probe.ApiKey = _key.Password.Length == 0 ? null : _key.Password;
        return probe;
    }

    private async Task DiscoverAsync()
    {
        _status.Text = "Connecting…";
        try
        {
            var models = (await ProviderClients.Create(Probe()).ListModelsAsync()).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var current = _modelName.Text;
            _modelName.Items.Clear();
            foreach (var m in models) _modelName.Items.Add(m);
            _modelName.Text = current.Length == 0 && models.Count > 0 ? models[0] : current;
            _status.Text = $"Found {models.Count} model(s).";
            _modelName.IsDropDownOpen = models.Count > 0;
        }
        catch (Exception error)
        {
            _status.Text = AgentHost.Describe(error);
        }
    }

    private async Task DetectAsync()
    {
        _status.Text = "Asking the server…";
        var info = await ProviderClients.Create(Probe()).ModelInfoAsync();
        if (info.ContextWindow is { } limit)
        {
            var serving = info.Id.Length > 0 && info.Id != _modelName.Text ? $" (serving {info.Id})" : "";
            _status.Text = $"Detected {limit:N0} tokens{serving}. Leave the field blank to always use the live value.";
        }
        else if (info.Served.Count > 0)
        {
            _status.Text = $"The server answered but reports no window for {_modelName.Text}; set one here.";
        }
        else
        {
            _status.Text = "Could not reach the server to detect the window.";
        }
    }

    private void Save()
    {
        var profile = Draft();
        if (profile.Model.Length == 0 || profile.BaseUrl.Length == 0 || profile.Name.Length == 0)
        {
            _status.Text = "A name, a base URL and a model are required.";
            return;
        }
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _status.Text = "The base URL must start with http:// or https://.";
            return;
        }
        var config = _model.Config;
        // (Asked before a renamed route is removed below.) A new route becomes the main model; editing an existing one leaves
        // it the main model, a spare or a subagent server, as it was.
        var existed = config.Providers.Any(p => p.RouteId == _original.RouteId);
        var wasActive = _original.RouteId == config.ActiveRoute;
        if (_original.RouteId != profile.RouteId && existed)
        {
            config.RemoveProvider(_original);
            try { SecretStore.Delete(SecretStore.ProviderTarget(_original.RouteId)); } catch (Exception) { }
        }
        if (!existed || wasActive) config.Activate(profile);
        else config.Store(profile);
        config.SetApiKey(_key.Password, profile);
        // The chats on this route follow it to its new name.
        if (existed && _original.RouteId != profile.RouteId) config.NoteRouteRenamed(_original.RouteId, profile.RouteId);
        _model.Host.ResetRouteCache();
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
