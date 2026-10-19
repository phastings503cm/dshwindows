using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;
using static Vision.App.Views.Guide.GuideVisuals;

namespace Vision.App.Views.Guide;

/// <summary>The pages of "Claude Code (your Claude subscription)".</summary>
public enum ClaudeCodeGuidePage { Check, Model, Done }

/// <summary>"Claude Code — your Claude Pro or Max plan": find the official claude CLI on this computer and check it is
/// signed in (with the install and sign-in steps when it isn't), pick the model chats start on and how Vision uses it,
/// and put Claude Code · Opus, Sonnet and Haiku in the model menu. Vision never touches Claude Code's sign-in: the CLI does
/// the talking. The rest (path, WSL) is in Settings › Claude Code.</summary>
public sealed class ClaudeCodeGuide : IWizardGuide
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly PropertyChangedEventHandler _changed;
    private bool _disposed;
    private bool _demo;
    private string _alias = ClaudeCodeRoutes.DefaultModel;
    private RouteUse _use;
    private ProviderProfile? _route;

    public ClaudeCodeGuide(AppModel model, IGuideHost host)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        _use = model.Config.ActiveProvider is null ? RouteUse.Primary : RouteUse.JustAdd;
        _changed = (_, _) =>
        {
            if (_disposed) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) _host.Render();
            });
        };
        Service.PropertyChanged += _changed;
    }

    private ClaudeCodeService Service => _model.ClaudeCode;

    public ClaudeCodeGuidePage Page { get; private set; } = ClaudeCodeGuidePage.Check;
    object IWizardGuide.PageKey => Page;

    public void Activate()
    {
        if (!_demo && Service.Status is null && !Service.Busy) _ = Service.CheckAsync();
    }

    private static readonly string[] SectionTitles = ["Find Claude Code", "Pick the model", "Done"];

    public int SectionCount => SectionTitles.Length;
    public int SectionIndex => (int)Page;
    public string Title => $"Claude Code · {SectionTitles[SectionIndex]}";
    public string StepText => $"Step {SectionIndex + 1} of {SectionCount}";

    public string NextLabel => Page switch
    {
        ClaudeCodeGuidePage.Check => Service.Status is ClaudeCodeStatus.Ready or ClaudeCodeStatus.SignedOut ? "Next" : "Check again",
        ClaudeCodeGuidePage.Model => "Add to the model menu",
        _ => "Start chatting",
    };

    public bool CanAdvance => !Service.Busy;

    public string? SecondaryLabel => null;
    public void Secondary() { }

    public void Next()
    {
        if (!CanAdvance) return;
        switch (Page)
        {
            case ClaudeCodeGuidePage.Check:
                if (Service.Status is ClaudeCodeStatus.Ready or ClaudeCodeStatus.SignedOut) Go(ClaudeCodeGuidePage.Model);
                else _ = Service.CheckAsync();
                break;
            case ClaudeCodeGuidePage.Model:
                if (!_demo) _route = Service.Use(_use, _alias, Service.UsesWsl, Service.WslDistro);
                Go(ClaudeCodeGuidePage.Done);
                break;
            default:
                _host.FinishGuide();
                break;
        }
    }

    public void Back()
    {
        if (Page == ClaudeCodeGuidePage.Check) _host.LeaveGuide();
        else Go(Page - 1);
    }

    private void Go(ClaudeCodeGuidePage page)
    {
        Page = page;
        _host.Render();
    }

    public UIElement Render() => Page switch
    {
        ClaudeCodeGuidePage.Check => CheckPage(),
        ClaudeCodeGuidePage.Model => ModelPage(),
        _ => DonePage(),
    };

    public void Dispose()
    {
        _disposed = true;
        Service.PropertyChanged -= _changed;
    }

    // MARK: Pages

    private UIElement CheckPage()
    {
        var panel = Ui.Stack(Heading("Use your Claude plan through Claude Code"), Lead(ClaudeCodeRoutes.Explanation));
        switch (Service.Status)
        {
            case null:
                panel.Children.Add(Busy("Looking for Claude Code on this computer…"));
                break;
            case ClaudeCodeStatus.Ready ready:
                panel.Children.Add(Note(Tone.Success, ready.Summary, $"Found Claude Code {ready.Version}"));
                break;
            case ClaudeCodeStatus.SignedOut signedOut:
                panel.Children.Add(Note(Tone.Warning, "It's installed, but it isn't signed in to your Claude account yet. Sign in once in a terminal — " +
                                                      "it opens your browser — then press Check again.", $"Claude Code {signedOut.Version} isn't signed in"));
                panel.Children.Add(Copyable(ClaudeCodeCli.SignInCommand, "Sign-in command"));
                panel.Children.Add(Paragraph("You can also go on now: chats on Claude Code will say they need the sign-in until you've done it.", 12.5,
                    "TextFillColorSecondaryBrush"));
                break;
            default:
                panel.Children.Add(Note(Tone.Warning, Service.Status!.Summary, "Claude Code isn't ready"));
                panel.Children.Add(Subheading("Install it"));
                panel.Children.Add(Paragraph(OperatingSystem.IsWindows() ? "Open PowerShell and run:" : "Open Terminal and run:", 13));
                panel.Children.Add(Copyable(ClaudeCodeCli.InstallCommand, "Install command"));
                panel.Children.Add(Paragraph("Or, if you have Node.js 18 or newer:", 13));
                panel.Children.Add(Copyable(ClaudeCodeCli.NpmInstallCommand, "npm install command"));
                panel.Children.Add(Subheading("Then sign in once"));
                panel.Children.Add(Copyable(ClaudeCodeCli.SignInCommand, "Sign-in command"));
                panel.Children.Add(Paragraph("Choose the Claude account with your Pro or Max plan, finish in the browser, and press Check again.", 13));
                break;
        }
        if (Service.Busy && Service.Status is not null) panel.Children.Add(Busy("Checking…"));
        panel.Children.Add(Aside("Vision finds the claude program (on PATH, or where its installers put it) and asks it `claude --version` and " +
                                 "`claude auth status` — which say whether it's signed in, never with what. Vision doesn't read, copy or store " +
                                 "Claude Code's sign-in: Claude Code talks to Anthropic itself, under your plan's terms."));
        return panel;
    }

    private UIElement ModelPage()
    {
        var panel = Ui.Stack(Heading("Pick the model chats start on"),
            Lead("All three go in every chat's model menu; this is the one Vision picks first. You can switch per chat at any time."));
        foreach (var m in ClaudeCodeRoutes.Models)
        {
            var alias = m.Alias;
            panel.Children.Add(Choice(Icons.Code, $"Claude Code · {m.Title}", char.ToUpperInvariant(m.Blurb[0]) + m.Blurb[1..] + ".", _alias == alias, () =>
            {
                _alias = alias;
                _host.Render();
            }));
        }
        panel.Children.Add(Subheading("How should Vision use it?"));
        void Use(RouteUse use, string glyph, string title, string detail) =>
            panel.Children.Add(Choice(glyph, title, detail, _use == use, () =>
            {
                _use = use;
                _host.Render();
            }));
        Use(RouteUse.Primary, Icons.Chat, "Primary", "New projects' chats start on it.");
        Use(RouteUse.JustAdd, Icons.Add, "Just add it", "It's in the model menu for the chats you put on it.");
        panel.Children.Add(Paragraph("Vision's own one-off requests on it (a summary, a skill draft) go to Claude Code as a single request without tools. " +
                                     "Sensitive chats can't use Claude Code, since it reads files itself.", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    private UIElement DonePage()
    {
        var title = ClaudeCodeRoutes.ModelTitle(_route?.Model ?? _alias);
        var panel = Ui.Stack(Heading("Claude Code is in the model menu"));
        panel.Children.Add(Note(Tone.Success,
            _use == RouteUse.Primary ? $"Claude Code · {title} is your primary model: new chats in new projects start on it."
                                     : $"Pick Claude Code · {title} (or Opus, or Haiku) from the model menu under the message box, for any chat.",
            "You're set"));
        panel.Children.Add(Bullets(
            (Icons.Shield, "Your permission setting decides what Claude Code may do without asking — it asks here, in the chat, when it needs you."),
            (Icons.Play, "/goal, the task list and Stop work as with any model; after each turn the chat shows what it used."),
            (Icons.Settings, "Settings › Claude Code has the rest: where claude is, running it in WSL, and taking it out of the menu.")));
        return panel;
    }

    // MARK: Self-test

    internal void ShowDemo(ClaudeCodeGuidePage page, ClaudeCodeStatus? status)
    {
        _demo = true;
        Service.ShowDemo(status);
        _alias = ClaudeCodeRoutes.DefaultModel;
        _use = RouteUse.Primary;
        _route = page == ClaudeCodeGuidePage.Done ? ClaudeCodeRoutes.Route(_alias) : null;
        Page = page;
        _host.Render();
    }
}
