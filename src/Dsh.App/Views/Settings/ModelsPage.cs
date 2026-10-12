using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Guide;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views.Settings;

public static class ProviderKinds
{
    public static IReadOnlyList<ProviderKind> All { get; } =
        [ProviderKind.OpenAICompat, ProviderKind.Ollama, ProviderKind.LmStudio, ProviderKind.OpenAI, ProviderKind.OpenRouter];

    public static string Label(this ProviderKind kind) => kind switch
    {
        ProviderKind.Ollama => "Ollama",
        ProviderKind.LmStudio => "LM Studio",
        ProviderKind.OpenAI => "OpenAI",
        ProviderKind.OpenRouter => "OpenRouter",
        ProviderKind.Bedrock => "Amazon Bedrock",
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
        var intro = Ui.Secondary("Any OpenAI-compatible server works: a Spark Swapper box, a local Ollama or LM Studio, vLLM or SGLang on your LAN, a subscription plan (GLM, Kimi, Qwen, OpenAI), or Amazon Bedrock with your AWS sign-in. API keys are kept in Windows Credential Manager, never in the settings file.");
        intro.Margin = new Thickness(0, 6, 0, 8);
        page.Children.Add(intro);
        var add = Ui.Button("Add ▾", () => { }, accent: true);
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(Menus.Item("Spark Swapper (connection code)…", () => model.ShowSwapperGuide(), Icons.Connect));
            menu.Items.Add(Menus.Item("Subscription plan…", () => model.ShowPlanGuide(), Icons.Key));
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
                            "Private models may see sensitive data; for the others, DSH masks it before sending (see Sensitive data below).";
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
        var explain = Ui.Secondary("Private models get the conversation as it is. For the others, DSH replaces sensitive values with placeholders such as ⟦EMAIL_1⟧ before anything leaves this PC, and puts the real values back in what comes back — the model can still work with them. Values in the Credentials Vault are always masked.");
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
        foreach (var kind in bedrock ? [ProviderKind.Bedrock] : ProviderKinds.All)
            _kind.Items.Add(new ComboBoxItem { Content = kind.Label(), Tag = kind, IsSelected = kind == profile.Kind });
        _kind.IsEnabled = !bedrock;
        _url.IsReadOnly = bedrock;
        _kind.SelectionChanged += (_, _) => UpdateKeyVisibility();
        _name.Text = profile.Name;
        _url.Text = profile.BaseUrl;
        _key.Password = model.Config.ApiKey(profile);
        _modelName.Text = profile.Model;
        _modelName.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        _temperature.Text = profile.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _maxTokens.Text = profile.MaxOutputTokens?.ToString() ?? "";
        _context.Text = profile.ContextWindow?.ToString() ?? "";
        _thinking.Items.Add(new ComboBoxItem { Content = "Server default", Tag = "", IsSelected = profile.ReasoningEffort is null });
        foreach (var level in ThinkingLevels.All)
            _thinking.Items.Add(new ComboBoxItem { Content = $"{level.Label()} — {level.Blurb()}", Tag = level.RawValue(), IsSelected = profile.ReasoningEffort == level.RawValue() });
        _vision.IsChecked = profile.Vision ?? true;
        _interactiveOnly.IsChecked = profile.IsInteractiveOnly;
        _interactiveOnly.ToolTip = "The plan's terms allow only interactive use (the Qwen Coding Plan): the task queue and other unattended work won't run on it as the primary.";
        _pin = profile.PinnedCertificate;
        _headers.AcceptsReturn = true;
        _headers.Height = 70;
        _headers.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _headers.Text = string.Join("\n", (profile.CustomHeaders ?? []).Select(h => $"{h.Key}: {h.Value}"));
        Placeholder.SetText(_headers, "X-Org-Id: 1234");

        _keyRow = Labeled("API key", _key, "Stored in Windows Credential Manager and only sent to this server.");
        var form = new StackPanel();
        form.Children.Add(Ui.Section("Server"));
        form.Children.Add(Ui.Card(Ui.Stack(
            Labeled("Kind", _kind),
            Labeled("Name", _name),
            Labeled("Base URL", _url, bedrock
                ? "Bedrock's endpoint for the Region. To change the Region, model or AWS sign-in, use Change… on the Models page."
                : "The OpenAI-compatible endpoint, usually ending in /v1."),
            _keyRow)));
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
            Labeled("Default thinking", _thinking, "Sent as enable_thinking / reasoning_effort. Each chat can override it (/think)."),
            Labeled("Temperature", _temperature),
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
            Vision = _vision.IsChecked == true ? null : false,
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
