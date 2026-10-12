using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

/// <summary>The pages of "Connect a subscription plan".</summary>
public enum PlanPage { Choose, Key, Paste, Check, Model, Role, Done }

/// <summary>"Connect a subscription plan": GLM (Z.ai), Kimi (Kimi Code, Moonshot), OpenAI or Qwen (Alibaba Cloud) — which
/// plan and region, where to get a key and how, pasting it (kept in Windows Credential Manager), checking it, picking the
/// model, how DSH should use it, and a first hello through it.</summary>
public sealed class PlanGuide : IWizardGuide
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _demo;

    public PlanGuide(AppModel model, IGuideHost host, string? plan = null)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        _plan = SubscriptionPlans.Find(plan);
        _region = _plan?.DefaultRegion.Id ?? SubscriptionPlans.International;
        _use = model.Config.ActiveProvider is null ? RouteUse.Primary : RouteUse.JustAdd;
    }

    public PlanPage Page { get; private set; } = PlanPage.Choose;
    object IWizardGuide.PageKey => Page;

    public void Activate() => OnShown();

    // MARK: - Header

    private static readonly string[] SectionTitles = ["Which plan?", "Get your key", "Paste your key", "Check it", "Pick the model", "How DSH uses it", "Done"];

    public int SectionCount => SectionTitles.Length;
    public int SectionIndex => (int)Page;
    public string Title => $"Connect a subscription plan · {SectionTitles[SectionIndex]}";
    public string StepText => $"Step {SectionIndex + 1} of {SectionCount}";

    public string NextLabel => Page switch
    {
        PlanPage.Key => "I have my key",
        PlanPage.Role => "Save and say hello",
        PlanPage.Done => "Start chatting",
        _ => "Next",
    };

    public bool CanAdvance => Page switch
    {
        PlanPage.Choose => _plan is not null,
        PlanPage.Paste => Key.Length > 0,
        PlanPage.Check => _checked && !_checking,
        PlanPage.Model => _chosen is { Length: > 0 },
        PlanPage.Done => _saved && !_saving,
        _ => true,
    };

    public string? SecondaryLabel => null;
    public void Secondary() { }

    public void Next()
    {
        if (!CanAdvance) return;
        try
        {
            if (Page == PlanPage.Done) _host.FinishGuide();
            else Go(Page + 1);
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error, "plan guide");
        }
    }

    public void Back()
    {
        if (Page == PlanPage.Choose)
        {
            _host.LeaveGuide();
            return;
        }
        Go(Page - 1);
    }

    private void Go(PlanPage page)
    {
        Page = page;
        OnShown();
        _host.Render();
    }

    private void OnShown()
    {
        if (_demo) return;
        switch (Page)
        {
            case PlanPage.Check:
                if (!_checked && !_checking) _ = CheckAsync();
                break;
            case PlanPage.Done:
                if (!_saved && !_saving) _ = SaveAsync();
                break;
        }
    }

    private void RenderIf(PlanPage page)
    {
        if (_disposed) return;
        if (Page == page) _host.Render();
        else _host.RefreshChrome();
    }

    public UIElement Render() => Page switch
    {
        PlanPage.Choose => ChoosePage(),
        PlanPage.Key => KeyPage(),
        PlanPage.Paste => PastePage(),
        PlanPage.Check => CheckPage(),
        PlanPage.Model => ModelPage(),
        PlanPage.Role => RolePage(),
        _ => DonePage(),
    };

    public void Dispose()
    {
        _disposed = true;
        _work?.Cancel();
    }

    // MARK: - Which plan?

    private SubscriptionPlan? _plan;
    private string _region;

    private PlanRegion Region => _plan!.Region(_region);

    private UIElement ChoosePage()
    {
        var panel = Ui.Stack(
            Heading("Which plan do you have?"),
            Lead("Pick the plan or API you pay for. DSH shows where to get its key next — and it never needs your account password."));
        foreach (var plan in SubscriptionPlans.All)
        {
            var chosen = plan;
            panel.Children.Add(Choice(plan.Id == SubscriptionPlans.OpenAi ? Icons.Cloud : Icons.Key, plan.Name, $"{plan.Provider} · {plan.Summary}",
                _plan?.Id == plan.Id, () =>
                {
                    if (_plan?.Id != chosen.Id)
                    {
                        _plan = chosen;
                        _region = chosen.DefaultRegion.Id;
                        Forget();
                    }
                    _host.Render();
                }));
        }
        if (_plan is { HasRegions: true } withRegions)
        {
            panel.Children.Add(Subheading("Where is your account?"));
            var regions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var region in withRegions.Regions)
            {
                var id = region.Id;
                var radio = new RadioButton { Content = region.Label, GroupName = "plan-region", IsChecked = _region == id, Margin = new Thickness(0, 0, 20, 0) };
                radio.Checked += (_, _) =>
                {
                    if (_region == id) return;
                    _region = id;
                    Forget();
                    _host.RefreshChrome();
                };
                regions.Children.Add(radio);
            }
            panel.Children.Add(regions);
            panel.Children.Add(Paragraph("Keys only work in the region where you made them.", 12.5, "TextFillColorSecondaryBrush"));
        }
        panel.Children.Add(Aside("Every plan here is an online service: your messages go to the provider's servers. DSH masks sensitive details — keys, emails, phone numbers, card numbers, your own custom terms — before anything is sent to a model that isn't private (Settings › AI Models › Sensitive data)."));
        return panel;
    }

    /// <summary>A different plan or region: what was checked no longer applies.</summary>
    private void Forget()
    {
        _checked = false;
        _checkError = null;
        _listed = null;
        _chosen = null;
        _saved = false;
        _hello = null;
    }

    // MARK: - Get your key

    private UIElement KeyPage()
    {
        var plan = _plan!;
        var region = Region;
        var panel = Ui.Stack(
            Heading($"Get your {plan.Provider} key"),
            Lead($"{plan.Name}{(plan.HasRegions ? $", {region.Label}" : "")}."));
        panel.Children.Add(Steps(plan.KeySteps.ToArray()));
        var buttons = Ui.Buttons(
            Ui.Button($"Open {new Uri(region.KeyPage).Host}", () => ShellIntegration.Open(region.KeyPage), accent: true),
            plan.SubscribeUrl is { } subscribe && subscribe != region.KeyPage ? Ui.Button("Subscribe", () => ShellIntegration.Open(subscribe)) : null);
        panel.Children.Add(buttons);
        if (region.KeyPageLabel is { } where) panel.Children.Add(Paragraph($"There: {where}.", 12.5, "TextFillColorSecondaryBrush"));
        panel.Children.Add(Note(Tone.Info, plan.KeyHint, "What the key looks like"));
        if (plan.TermsNote is { } terms) panel.Children.Add(Note(Tone.Warning, terms, "The plan's terms"));
        if (plan.Note is { } note) panel.Children.Add(Paragraph(note, 12.5, "TextFillColorSecondaryBrush"));
        panel.Children.Add(Stuck("A key is shown only once. Lost it? Make a new one on the same page and delete the old one."));
        return panel;
    }

    // MARK: - Paste your key

    private readonly PasswordBox _keyBox = PasswordField();
    private bool _keyHooked;
    private string Key => _keyBox.Password.Trim();
    private readonly ContentControl _keyNote = new() { Focusable = false };

    private UIElement PastePage()
    {
        var plan = _plan!;
        if (!_keyHooked)
        {
            _keyHooked = true;
            _keyBox.PasswordChanged += (_, _) =>
            {
                Forget();
                UpdateKeyNote();
                _host.RefreshChrome();
            };
        }
        var panel = Ui.Stack(
            Heading("Paste your key"),
            Lead($"Your {plan.Name} key. DSH keeps it in Windows Credential Manager and sends it only to {new Uri(Region.BaseUrl).Host}."),
            Field("API key", Detach(_keyBox), plan.KeyHint));
        panel.Children.Add(_keyNote);
        UpdateKeyNote();
        return panel;
    }

    private void UpdateKeyNote()
    {
        _keyNote.Content = _plan?.KeyMistake(_keyBox.Password) is { } mistake ? Note(Tone.Warning, mistake) : null;
    }

    // MARK: - Check it

    private bool _checking;
    private bool _checked;
    private string? _checkError;
    /// <summary>What /models listed (null when it wasn't asked or didn't work).</summary>
    private IReadOnlyList<string>? _listed;
    private CancellationTokenSource? _work;

    private ProviderProfile Probe(string model)
    {
        var route = _plan!.Route(_region, model);
        route.ApiKey = Key;
        return route;
    }

    private async Task CheckAsync()
    {
        var plan = _plan!;
        var region = Region;
        _checking = true;
        _checkError = null;
        _listed = null;
        RenderIf(PlanPage.Check);
        _work?.Cancel();
        _work = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = _work.Token;
        try
        {
            var client = ProviderClients.Create(Probe(plan.Recommended.Id));
            var listed = false;
            if (plan.ListsModels)
            {
                try
                {
                    _listed = await client.ListModelsAsync(ct);
                    listed = _listed.Count > 0;
                }
                catch (LlmException error) when (error.Kind == LlmErrorKind.Http && error.StatusCode is 404 or 405)
                {
                    // No model list here: a one-token reply proves the key instead.
                }
            }
            if (!listed)
            {
                var request = new LlmRequest("", [LlmMessage.User("Reply with OK.")], [], plan.Recommended.Id, MaxTokens: 1);
                await foreach (var _ in client.StreamAsync(request, ct)) { }
            }
            _checked = true;
            _chosen ??= Recommend();
        }
        catch (LlmException error) when (error.Kind == LlmErrorKind.Http)
        {
            _checkError = SubscriptionPlans.ExplainError(plan, region, error.StatusCode, error.Body);
        }
        catch (LlmException error) when (error.Kind is LlmErrorKind.Connection or LlmErrorKind.Sse)
        {
            _checkError = $"Couldn't reach {region.BaseUrl}. Check this PC is online (and that nothing blocks the address), then try again.";
        }
        catch (OperationCanceledException) when (!_disposed)
        {
            _checkError = $"{region.BaseUrl} didn't answer within a minute. Try again.";
        }
        catch (Exception error)
        {
            _checkError = AgentHost.Describe(error);
        }
        finally
        {
            _checking = false;
            RenderIf(PlanPage.Check);
        }
    }

    private UIElement CheckPage()
    {
        var plan = _plan!;
        var panel = Ui.Stack(
            Heading("Checking your key"),
            Lead(plan.ListsModels ? "DSH asks the plan which models your key can use." : "DSH sends a one-word request to check the key works."));
        if (_checking) panel.Children.Add(Busy($"Asking {new Uri(Region.BaseUrl).Host}…"));
        else if (_checkError is { } error)
            panel.Children.Add(Note(Tone.Error, error, "The key didn't work", Ui.Buttons(Ui.Button("Try again", () => _ = CheckAsync(), accent: true))));
        else if (_checked)
            panel.Children.Add(Note(Tone.Success, _listed is { Count: > 0 } listed ? $"Your key works — {listed.Count} model(s) available." : "Your key works.", "It's accepted"));
        return panel;
    }

    // MARK: - Pick the model

    private string? _chosen;

    /// <summary>The models to offer: what /models listed (chat models only, for OpenAI), else the plan's presets.</summary>
    private IReadOnlyList<string> Offered()
    {
        var plan = _plan!;
        if (_listed is not { Count: > 0 } listed) return plan.Models.Select(m => m.Id).ToList();
        if (plan.Id != SubscriptionPlans.OpenAi) return listed.Order(StringComparer.OrdinalIgnoreCase).ToList();
        string[] notChat = ["embedding", "tts", "whisper", "dall-e", "moderation", "image", "audio", "realtime", "transcribe", "search", "davinci", "babbage", "sora"];
        return listed.Where(id => !notChat.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(id => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string Recommend()
    {
        var plan = _plan!;
        var offered = Offered();
        if (plan.Id == SubscriptionPlans.OpenAi && SubscriptionPlans.RecommendOpenAiModel(offered) is { } newest) return newest;
        return offered.FirstOrDefault(id => id == plan.Recommended.Id) ?? offered.FirstOrDefault() ?? plan.Recommended.Id;
    }

    private UIElement ModelPage()
    {
        var plan = _plan!;
        var recommended = Recommend();
        var panel = Ui.Stack(
            Heading("Pick the model"),
            Lead(_listed is { Count: > 0 } ? "These are the models your key can use. The recommended one is chosen." : "The models this plan offers. The recommended one is chosen."));
        var list = new StackPanel();
        foreach (var id in Offered())
        {
            var known = plan.Models.FirstOrDefault(m => m.Id == id);
            var label = id + (id == recommended ? "  — recommended" : "") + (known?.Note is { } note ? $"  · {note}" : "")
                        + (known?.Context is { } context ? $"  · {Fmt.Short(context)} context" : "");
            var radio = new RadioButton { Content = label, GroupName = "plan-model", IsChecked = _chosen == id, Margin = new Thickness(0, 3, 0, 3) };
            var value = id;
            radio.Checked += (_, _) =>
            {
                _chosen = value;
                _host.RefreshChrome();
            };
            list.Children.Add(radio);
        }
        panel.Children.Add(Ui.Card(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        if (SubscriptionPlans.AlwaysThinks(_chosen))
            panel.Children.Add(Paragraph("GLM-5 always thinks before it answers; the thinking setting picks how much (Off asks for the least).", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    // MARK: - How DSH uses it

    private RouteUse _use;
    private bool _private;

    private UIElement RolePage()
    {
        var plan = _plan!;
        var panel = Ui.Stack(
            Heading("How should DSH use it?"),
            Lead("You can change this any time in Settings › AI Models."));
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
        if (plan.InteractiveOnly)
            panel.Children.Add(Note(Tone.Info, "Under this plan's terms DSH uses it only while you're there: as the primary, the task queue won't run on it, and a /goal goes one round at a time.", "Interactive use only"));
        var isPrivate = Ui.Check("Private", _private, on => _private = on);
        isPrivate.Margin = new Thickness(0, 12, 0, 0);
        isPrivate.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(isPrivate);
        panel.Children.Add(Paragraph("Not private — this is an online service. DSH masks sensitive details before sending (see Settings › AI Models › Sensitive data).", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    // MARK: - Done

    private bool _saving;
    private bool _saved;
    private string? _saveError;
    private string? _hello;
    private ProviderProfile? _route;

    private async Task SaveAsync()
    {
        var plan = _plan!;
        var model = _chosen ?? plan.Recommended.Id;
        _saving = true;
        _saveError = null;
        _hello = null;
        RenderIf(PlanPage.Done);
        try
        {
            var config = _model.Config;
            var route = plan.Route(_region, model);
            route.IsPrivate = _private ? true : null; // not private is what its address says already
            if (_use == RouteUse.Subagent) route.SubagentWorker = true;
            if (_use == RouteUse.Backup) route.Backup = true;
            // The same plan and model again (a new key, say) replaces the old route rather than adding a second.
            if (config.Providers.FirstOrDefault(p => p.RouteId == route.RouteId) is { } existing)
            {
                route.SubagentWorker |= existing.SubagentWorker;
                route.Backup ??= existing.Backup;
                route.IsPrivate ??= existing.IsPrivate;
            }
            if (_use == RouteUse.Primary || config.ActiveRoute is null) config.Activate(route);
            else config.Store(route);
            config.SetApiKey(Key, route);
            _model.Host.ResetRouteCache();
            _route = route;
            _saved = true;
            RenderIf(PlanPage.Done);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            _hello = await SparkSetup.SayHelloAsync(ProviderClients.Create(Probe(model)), model,
                $"Say hello to someone who just connected DSH to their {plan.Name}, in one short, friendly sentence.", timeout.Token);
        }
        catch (LlmException error) when (error.Kind == LlmErrorKind.Http)
        {
            _saveError = SubscriptionPlans.ExplainError(plan, Region, error.StatusCode, error.Body);
        }
        catch (OperationCanceledException)
        {
            _saveError = "No answer within 2 minutes. Try again in a moment.";
        }
        catch (Exception error)
        {
            _saveError = AgentHost.Describe(error);
        }
        finally
        {
            _saving = false;
            RenderIf(PlanPage.Done);
        }
    }

    private UIElement DonePage()
    {
        var plan = _plan!;
        var panel = Ui.Stack(Heading(_saved ? "You're connected" : "Saving the connection"));
        if (_saving && !_saved) panel.Children.Add(Busy("Saving…"));
        if (_saved)
        {
            var use = _use switch
            {
                RouteUse.Primary => "It's your primary model now: new messages go to it.",
                RouteUse.Subagent => "It takes subagents' side tasks.",
                RouteUse.Backup => "It stands by as a backup for the primary.",
                _ => "It's in the model menu under the message box.",
            };
            panel.Children.Add(Note(Tone.Success, use, $"{plan.Name} is set up"));
            if (_saving) panel.Children.Add(Busy("Saying hello…"));
            else if (_hello is { } hello) panel.Children.Add(Note(Tone.Info, hello, "It answered"));
        }
        if (_saveError is { } error)
            panel.Children.Add(Note(Tone.Warning, error, _saved ? "Saved, but the hello didn't go through" : "It didn't save",
                Ui.Buttons(Ui.Button("Try again", () => _ = SaveAsync()))));
        panel.Children.Add(Aside($"DSH saved a model route named \"{_route?.Name ?? plan.Name}\" at {Region.BaseUrl}, with your key in Windows Credential Manager. It isn't private, so DSH masks sensitive details before sending — change what is masked in Settings › AI Models › Sensitive data."));
        return panel;
    }

    // MARK: - Self-test

    internal void ShowDemo(PlanPage page)
    {
        _demo = true;
        _plan = SubscriptionPlans.Find(SubscriptionPlans.QwenCoding);
        _region = SubscriptionPlans.International;
        _keyBox.Password = page == PlanPage.Paste ? "sk-1234567890abcdef" : "sk-sp-1234567890abcdef";
        _checked = page > PlanPage.Check;
        _chosen = _plan!.Recommended.Id;
        _use = RouteUse.Primary;
        if (page == PlanPage.Done)
        {
            _saved = true;
            _route = _plan.Route(_region, _chosen);
            _hello = "Hi! Qwen here — ready when you are. What shall we build?";
        }
        Page = page;
        _host.Render();
    }
}
