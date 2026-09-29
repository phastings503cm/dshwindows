using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Guide;
using Dsh.App.Views.Settings;
using Dsh.Core;

namespace Dsh.App.Views.Wizard;

/// <summary>First-run configuration, re-runnable from Settings or the Help menu. The job is narrow:
/// find a model server, prove we can reach it, and pin a permission preset. Everything else in the app
/// assumes those three are done.
///
/// Its first page also offers the DGX Spark guide (<see cref="SparkGuide"/>): a beginner's walk from a
/// Spark in its box to a working connection, shown in this same window in place of the normal steps.</summary>
public sealed class SetupWizardWindow : Window, IGuideHost
{
    private enum Step { Welcome, Backend, Connection, Model, Tuning, Permissions, Project, Done }

    private static readonly (string Label, int Tokens)[] ContextPresets =
        [("32K", 32_768), ("128K", 131_072), ("256K", 262_144), ("512K", 524_288), ("1M", 1_000_000)];

    private readonly AppModel _model;
    private Step _step = Step.Welcome;
    private ProviderKind _kind = ProviderKind.OpenAICompat;
    private string _host = "";
    private string _port = "";
    private string _apiKey = "";
    private string _modelId = "";
    private List<string> _discovered = [];
    private string? _probeError;
    private int? _probeCount;
    private bool _probing;
    private PermissionPreset _preset = PermissionPreset.WorkspaceWrite;
    /// <summary>null = auto-detect; -1 = custom.</summary>
    private int? _contextChoice;
    private string _customContext = "";
    private int? _detectedContext;
    private string? _detectedModel;
    private bool _detecting;
    private string _thinking = "";
    /// <summary>The route being edited when the wizard is re-run, so fields it doesn't show survive.</summary>
    private ProviderProfile? _existing;
    /// <summary>A self-signed certificate the user chose to trust for a server the scanner found.</summary>
    private string? _pinnedCertificate;
    private bool _trustPin = true;
    private ScannerPanel? _scanner;
    private bool _showScanner;
    private string? _scanNote;

    /// <summary>A guide (DGX Spark, Amazon Bedrock) while it is shown instead of the normal steps.</summary>
    private IWizardGuide? _guide;
    private object? _renderedGuidePage;

    private readonly StackPanel _progress = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _stepTitle = Ui.Text("", 12.5, FontWeights.SemiBold, wrap: false);
    private readonly TextBlock _stepCount = Ui.Secondary("");
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly Border _frame;
    private readonly ScrollViewer _scroll;
    private readonly Button _back;
    private readonly Button _skip;
    private readonly Button _next;

    public SetupWizardWindow(AppModel model)
    {
        _model = model;
        Title = "Set Up DSH";
        Width = 780;
        Height = 640;
        MinWidth = 640;
        MinHeight = 520;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _back = Ui.Button("Back", Back);
        _skip = Ui.Button("Skip", Skip);
        _next = Ui.Button("Continue", Advance, accent: true);
        _next.IsDefault = true;

        var header = new StackPanel { Margin = new Thickness(22, 16, 22, 12) };
        header.Children.Add(_progress);
        var titles = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_stepCount, Dock.Right);
        titles.Children.Add(_stepCount);
        titles.Children.Add(_stepTitle);
        header.Children.Add(titles);

        var footer = new DockPanel { Margin = new Thickness(22, 12, 22, 16) };
        DockPanel.SetDock(_back, Dock.Left);
        footer.Children.Add(_back);
        var right = Ui.Buttons(_skip, _next);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        var divider = Ui.Divider();
        DockPanel.SetDock(divider, Dock.Top);
        root.Children.Add(divider);
        _frame = new Border { Child = _body, MaxWidth = 640, Padding = new Thickness(28, 20, 28, 20) };
        _scroll = new ScrollViewer
        {
            Content = _frame,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(_scroll);
        Content = root;
        Closed += (_, _) =>
        {
            _guide?.Dispose();
            _scanner?.Cancel();
        };

        Seed();
        Render();
    }

    // MARK: - The DGX Spark guide

    /// <summary>Show the guide in place of the normal steps (resuming saved progress when asked).</summary>
    private void StartGuide(bool resume)
    {
        _guide?.Dispose();
        var saved = resume ? GuideState.Load() : null;
        if (!resume) GuideState.Clear();
        ShowGuide(new SparkGuide(_model, this, saved));
    }

    /// <summary>Show the Amazon Bedrock guide in place of the normal steps, on <paramref name="page"/>.</summary>
    public void StartBedrockGuide(BedrockPage page = BedrockPage.Welcome, ProviderProfile? route = null)
    {
        _guide?.Dispose();
        ShowGuide(new BedrockGuide(_model, this, page, route));
    }

    /// <summary>For the self-test: the Bedrock guide on <paramref name="page"/>, with sample state.</summary>
    internal void ShowBedrockDemo(BedrockPage page, bool terms = false)
    {
        if (_guide is not BedrockGuide) StartBedrockGuide();
        var guide = (BedrockGuide)_guide!;
        if (terms) guide.ShowTermsDemo();
        else guide.ShowDemo(page);
    }

    /// <summary>Show <paramref name="guide"/> in place of the normal steps.</summary>
    private void ShowGuide(IWizardGuide guide)
    {
        _guide = guide;
        // The guide's pages carry pictures: give them room, within the screen.
        var area = SystemParameters.WorkArea;
        if (Width < 900) Width = Math.Min(900, area.Width);
        if (Height < 760) Height = Math.Min(760, area.Height);
        Render();
        if (SelfTest.Current is null) guide.Activate();
    }

    /// <summary>For the self-test: the guide on <paramref name="page"/>, with sample state.</summary>
    internal void ShowGuideDemo(GuidePage page)
    {
        if (_guide is not SparkGuide) StartGuide(resume: false);
        ((SparkGuide)_guide!).ShowDemo(page);
    }

    /// <summary>For the self-test: scroll the page to a fraction of its height.</summary>
    internal void ScrollGuide(double fraction)
    {
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(_scroll.ScrollableHeight * fraction);
    }

    Window IGuideHost.Window => this;

    void IGuideHost.Render() => Render();

    void IGuideHost.RefreshChrome() => RenderChrome();

    void IGuideHost.LeaveGuide()
    {
        _guide?.Dispose();
        _guide = null;
        _renderedGuidePage = null;
        Go(Step.Welcome);
    }

    void IGuideHost.FinishGuide()
    {
        // The guide saved the model route itself; don't overwrite it with this window's draft.
        _model.Config.WizardCompleted = true;
        if (_model.Host.Sessions.Count == 0) _model.NewChat();
        if (_guide is SparkGuide) GuideState.Clear();
        DialogResult = true;
    }

    // MARK: - State

    private void Seed()
    {
        _preset = _model.Config.AsPreset;
        if (_model.Config.ActiveProvider is not { } active)
        {
            ApplyDefaults(_kind);
            return;
        }
        _existing = active;
        _kind = active.Kind;
        _modelId = active.Model;
        _apiKey = active.ApiKey ?? "";
        _thinking = active.ReasoningEffort ?? "";
        if (active.ContextWindow is { } window)
        {
            if (ContextPresets.Any(p => p.Tokens == window)) _contextChoice = window;
            else
            {
                _contextChoice = -1;
                _customContext = window.ToString();
            }
        }
        if (Uri.TryCreate(active.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.AbsolutePath.TrimEnd('/') is "/v1" or "")
        {
            _host = uri.Host;
            _port = uri.IsDefaultPort ? "" : uri.Port.ToString();
        }
        else
        {
            // https, or a non-default path (a proxy): keep the URL whole.
            _host = active.BaseUrl;
            _port = "";
        }
    }

    private void ApplyDefaults(ProviderKind kind)
    {
        _probeError = null;
        _probeCount = null;
        _discovered = [];
        (_host, _port, _modelId) = kind switch
        {
            ProviderKind.Ollama => ("127.0.0.1", "11434", "qwen3:8b"),
            ProviderKind.LmStudio => ("127.0.0.1", "1234", ""),
            ProviderKind.OpenAI => ("https://api.openai.com/v1", "", "gpt-4o"),
            ProviderKind.OpenRouter => ("https://openrouter.ai/api/v1", "", "qwen/qwen3-32b"),
            _ => ("", "8002", ""),
        };
    }

    private bool UsesPort => _kind is ProviderKind.OpenAICompat or ProviderKind.Ollama or ProviderKind.LmStudio;
    private bool NeedsKey => _kind is ProviderKind.OpenAI or ProviderKind.OpenRouter;
    /// <summary>Your own server may sit behind a key (vLLM/SGLang --api-key, an nginx front).</summary>
    private bool AllowsKey => NeedsKey || _kind == ProviderKind.OpenAICompat;

    /// <summary>The endpoint we will actually call.</summary>
    private string BaseUrl
    {
        get
        {
            var host = _host.Trim().TrimEnd('/');
            if (host.Length == 0) return "—";
            var port = _port.Trim();
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out var uri)) return host;
                var builder = new UriBuilder(uri);
                if (uri.IsDefaultPort && UsesPort && int.TryParse(port, out var p) && !host.Contains($":{uri.Port}")) builder.Port = p;
                if (builder.Path is "" or "/") builder.Path = "/v1";
                var text = builder.Uri.ToString().TrimEnd('/');
                return text;
            }
            return $"http://{(port.Length == 0 ? host : $"{host}:{port}")}/v1";
        }
    }

    private ProviderProfile Draft()
    {
        // Re-running the wizard edits the existing route in place, so settings it doesn't show
        // (headers, temperature, max tokens) are kept.
        var profile = _existing is { } existing && existing.Kind == _kind
            ? existing.DeepCopy()
            : new ProviderProfile(_kind, _kind == ProviderKind.OpenAICompat ? "DGX Spark / server" : _kind.Label(), BaseUrl, _modelId);
        profile.BaseUrl = BaseUrl;
        profile.ApiKey = _apiKey.Length == 0 ? null : _apiKey;
        profile.Model = _modelId.Trim().Length > 0 ? _modelId.Trim() : _detectedModel ?? "model";
        profile.ContextWindow = _contextChoice switch
        {
            null => null,
            -1 => int.TryParse(new string(_customContext.Where(char.IsDigit).ToArray()), out var n) && n > 0 ? n : null,
            var some => some,
        };
        profile.ReasoningEffort = _thinking.Length == 0 ? null : _thinking;
        if (_pinnedCertificate is not null) profile.PinnedCertificate = _trustPin ? _pinnedCertificate : null;
        return profile;
    }

    private void SaveProvider()
    {
        // A Bedrock route is made and changed by its guide; the plain steps leave it as it is.
        if (_kind == ProviderKind.Bedrock) return;
        var profile = Draft();
        var key = _apiKey;
        profile.ApiKey = null;
        var config = _model.Config;
        if (_existing is { } existing && existing.RouteId != profile.RouteId && config.Providers.Any(p => p.RouteId == existing.RouteId))
            config.RemoveProvider(existing);
        config.Activate(profile);
        _existing = profile;
        if (key.Length > 0) config.SetApiKey(key, profile);
        _model.Host.ResetRouteCache();
    }

    // MARK: - Navigation

    private void Go(Step step)
    {
        _step = step;
        Render();
    }

    private void Back()
    {
        if (_guide is { } guide)
        {
            guide.Back();
            return;
        }
        if (_step == Step.Permissions && _kind == ProviderKind.Bedrock) Go(Step.Backend);
        else if (_step > Step.Welcome) Go(_step - 1);
    }

    private void Skip()
    {
        if (_guide is { } guide)
        {
            guide.Secondary();
            return;
        }
        Go(Step.Done);
    }

    private bool CanAdvance => _step switch
    {
        Step.Connection => _host.Trim().Length > 0 && !_probing,
        Step.Model => _modelId.Trim().Length > 0,
        Step.Tuning => _contextChoice != -1 || int.TryParse(new string(_customContext.Where(char.IsDigit).ToArray()), out var n) && n > 0,
        _ => true,
    };

    private async void Advance()
    {
        if (_guide is { } guide)
        {
            guide.Next();
            return;
        }
        if (!CanAdvance) return;
        switch (_step)
        {
            case Step.Connection:
                // Discover models on the way through, so the next step is useful even if Test was
                // never pressed.
                if (_probeCount is null) await ProbeAsync();
                Go(Step.Model);
                break;
            case Step.Model:
                SaveProvider();
                Go(Step.Tuning);
                if (_detectedContext is null) _ = DetectAsync();
                break;
            case Step.Tuning:
                SaveProvider();
                Go(Step.Permissions);
                break;
            case Step.Permissions:
                _model.Config.Preset = _preset.RawValue();
                Go(Step.Project);
                break;
            case Step.Done:
                Finish();
                break;
            case Step.Backend when _kind == ProviderKind.Bedrock:
                // Keeping the Bedrock route: its model and sign-in are the guide's (or Settings'), not these steps'.
                Go(Step.Permissions);
                break;
            default:
                Go(_step + 1);
                break;
        }
    }

    private void Finish()
    {
        SaveProvider();
        _model.Config.Preset = _preset.RawValue();
        _model.Config.WizardCompleted = true;
        if (_model.Host.Sessions.Count == 0) _model.NewChat();
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // MARK: - Rendering

    private void Render()
    {
        if (_guide is { } guide)
        {
            RenderChrome();
            _frame.MaxWidth = 700;
            _body.Content = guide.Render();
            if (!Equals(_renderedGuidePage, guide.PageKey))
            {
                _renderedGuidePage = guide.PageKey;
                _scroll.ScrollToTop();
            }
            return;
        }
        _frame.MaxWidth = 640;
        var steps = Enum.GetValues<Step>();
        RenderBars(steps.Length, (int)_step);
        _stepTitle.Text = StepTitle(_step);
        _stepCount.Text = $"Step {(int)_step + 1} of {steps.Length}";

        _back.Visibility = _step == Step.Welcome ? Visibility.Hidden : Visibility.Visible;
        _skip.Visibility = _step == Step.Project ? Visibility.Visible : Visibility.Collapsed;
        _skip.Content = "Skip";
        _next.Content = _step == Step.Done ? "Start Working" : "Continue";
        _next.IsEnabled = CanAdvance;

        _body.Content = _step switch
        {
            Step.Welcome => Welcome(),
            Step.Backend => Backend(),
            Step.Connection => Connection(),
            Step.Model => ModelStep(),
            Step.Tuning => Tuning(),
            Step.Permissions => Permissions(),
            Step.Project => ProjectStep(),
            _ => Done(),
        };
    }

    private void RenderBars(int count, int current)
    {
        if (_progress.Children.Count != count)
        {
            _progress.Children.Clear();
            for (var i = 0; i < count; i++)
                _progress.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 4, 0) });
        }
        for (var i = 0; i < count; i++)
            ((Border)_progress.Children[i]).SetResourceReference(Border.BackgroundProperty, i <= current ? "AccentFillColorDefaultBrush" : "DividerStrokeColorDefaultBrush");
        _progress.SizeChanged -= LayoutBars;
        _progress.SizeChanged += LayoutBars;
        LayoutBars(null, null);
    }

    /// <summary>The guide's header and footer, without rebuilding its page.</summary>
    private void RenderChrome()
    {
        if (_guide is not { } guide)
        {
            Refresh();
            return;
        }
        RenderBars(guide.SectionCount, guide.SectionIndex);
        _stepTitle.Text = guide.Title;
        _stepCount.Text = guide.StepText;
        _back.Visibility = Visibility.Visible;
        _skip.Visibility = guide.SecondaryLabel is null ? Visibility.Collapsed : Visibility.Visible;
        _skip.Content = guide.SecondaryLabel ?? "Skip";
        _next.Content = guide.NextLabel;
        _next.IsEnabled = guide.CanAdvance;
    }

    private void LayoutBars(object? sender, SizeChangedEventArgs? e)
    {
        var count = _progress.Children.Count;
        var width = Math.Max(0, (ActualWidth - 44) / count - 4);
        foreach (FrameworkElement bar in _progress.Children) bar.Width = width > 0 ? width : 70;
    }

    private void Refresh() => _next.IsEnabled = CanAdvance;

    private static string StepTitle(Step step) => step switch
    {
        Step.Welcome => "Welcome",
        Step.Backend => "Backend",
        Step.Connection => "Connection",
        Step.Model => "Model",
        Step.Tuning => "Context & thinking",
        Step.Permissions => "Permissions",
        Step.Project => "Project",
        _ => "Ready",
    };

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text, 20, FontWeights.SemiBold);
        block.Margin = new Thickness(0, 0, 0, 6);
        return block;
    }

    private static TextBlock Lead(string text)
    {
        var block = Ui.Secondary(text, 13);
        block.Margin = new Thickness(0, 0, 0, 14);
        return block;
    }

    private UIElement Welcome()
    {
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-256.png")),
            Width = 56,
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var title = Ui.Text("Set up DSH", 28, FontWeights.SemiBold);
        var panel = Ui.Stack(logo, title,
            Lead("This app is the agent harness — it runs the tool loop itself and talks straight to a model server. Nothing else has to be installed or kept running."));
        if (GuideState.Load() is { HasProgress: true } saved)
        {
            panel.Children.Add(Choice(Icons.Play, "Continue the DGX Spark guide",
                $"Pick up where you left off{(saved.Host is { } host ? $" with your Spark at {host}" : "")}.", false, () => StartGuide(resume: true)));
        }
        panel.Children.Add(Choice(Icons.Education, "I have a DGX Spark — walk me through everything (beginner)",
            "Step by step, with a picture for each: power it on, find it on your network, install Spark Swapper, and connect DSH. No terminal needed — it can even make a USB stick to reinstall it.",
            false, () => StartGuide(resume: false)));
        panel.Children.Add(Choice(Icons.Cloud, "Use Amazon Bedrock with my AWS account (guided)",
            "Claude, Amazon Nova, Llama and more, billed to your AWS account. DSH installs the AWS CLI if needed, signs you in through your browser, and takes care of each model's first-time form or terms — showing you the price first.",
            false, () => StartBedrockGuide()));
        var or = Ui.Secondary("Or press Continue to set DSH up yourself:");
        or.Margin = new Thickness(0, 6, 0, 12);
        panel.Children.Add(or);
        panel.Children.Add(Bullet(Icons.Connect, "Point it at a model", "A DGX Spark or any OpenAI-compatible server on your network, a local Ollama or LM Studio, or a cloud provider."));
        panel.Children.Add(Bullet(Icons.Wrench, "Give it tools", "Read, write, edit, glob, grep, shell (PowerShell by default), and web fetch — the Qwen Code tool set, so prompts and skills written for it work here."));
        panel.Children.Add(Bullet(Icons.Code, "Work in a project", "Open a folder to get a file tree, an editor, and a terminal beside the chat."));
        return panel;
    }

    private static UIElement Bullet(string glyph, string title, string detail)
    {
        var icon = Ui.Glyph(glyph, 16, "AccentTextFillColorPrimaryBrush");
        icon.Margin = new Thickness(0, 2, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Ui.Stack(Ui.Text(title, 13.5, FontWeights.SemiBold), Ui.Secondary(detail)));
        return row;
    }

    /// <summary>A selectable card (backend and permission choices).</summary>
    private static Button Choice(string glyph, string title, string detail, bool selected, Action choose, string tint = "AccentFillColorDefaultBrush")
    {
        var icon = Ui.Glyph(glyph, 16, selected ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        icon.Margin = new Thickness(0, 1, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var check = Ui.Glyph(Icons.Completed, 16, "AccentTextFillColorPrimaryBrush");
        check.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        check.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(check);
        row.Children.Add(Ui.Stack(Ui.Text(title, 13.5, FontWeights.SemiBold), Ui.Secondary(detail)));
        var card = new Border { Child = row, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, selected ? tint : "CardStrokeColorDefaultBrush");
        var button = new Button
        {
            Content = card,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.SetResourceReference(StyleProperty, "SubtleButton");
        button.Click += (_, _) => choose();
        return button;
    }

    private UIElement Backend()
    {
        var panel = Ui.Stack(Heading("Where does the model run?"), Lead("You can change this later, and keep more than one configured."));
        foreach (var kind in ProviderKinds.All)
        {
            var (title, detail) = kind switch
            {
                ProviderKind.OpenAICompat => ("DGX Spark or another server on your network",
                    "An OpenAI-compatible endpoint — vLLM, SGLang, llama.cpp, or the serving stack on a DGX Spark. Reached over your LAN."),
                ProviderKind.Ollama => ("Ollama", "Models running locally through Ollama on this PC."),
                ProviderKind.LmStudio => ("LM Studio", "Models running locally through LM Studio on this PC."),
                ProviderKind.OpenAI => ("OpenAI", "OpenAI's hosted API. Needs an API key."),
                _ => ("OpenRouter", "Many hosted models behind one API. Needs an API key."),
            };
            var chosen = kind;
            panel.Children.Add(Choice(Icons.ForProvider(kind), title, detail, _kind == kind, () =>
            {
                if (_kind != chosen)
                {
                    _kind = chosen;
                    ApplyDefaults(chosen);
                }
                Render();
            }));
        }
        panel.Children.Add(Choice(Icons.Cloud, "Amazon Bedrock",
            "Models from AWS — Claude, Amazon Nova, Llama, Mistral — with your AWS sign-in. Opens a short guide that sets everything up, including the AWS CLI.",
            _kind == ProviderKind.Bedrock, () => StartBedrockGuide()));
        return panel;
    }

    private UIElement Connection()
    {
        var hint = _kind switch
        {
            ProviderKind.OpenAICompat => "Enter the address of the machine serving the model and the port its OpenAI-compatible API listens on. On a DGX Spark following the setup guide that is port 8002, and the address is the box's hostname or LAN IP.",
            ProviderKind.Ollama => "Ollama serves an OpenAI-compatible API on this PC. The defaults are usually right — start it with `ollama serve` (or the Ollama app) if it isn't already running.",
            ProviderKind.LmStudio => "Start LM Studio's local server (Developer › Start Server) and load a model, then test the connection.",
            _ => "Paste an API key. It is stored in Windows Credential Manager, never in the app's settings file.",
        };
        var placeholder = _kind switch
        {
            ProviderKind.OpenAICompat => "spark.local or 192.168.1.50",
            ProviderKind.Ollama or ProviderKind.LmStudio => "127.0.0.1",
            ProviderKind.OpenAI => "https://api.openai.com/v1",
            _ => "https://openrouter.ai/api/v1",
        };
        var host = Ui.Field(_host, placeholder);
        var port = Ui.Field(_port, "8002", width: 110);
        var key = new PasswordBox { Password = _apiKey, Padding = new Thickness(8, 5, 8, 5) };
        var url = Ui.Text(BaseUrl, 12.5, brushKey: "TextFillColorSecondaryBrush");
        url.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        void Changed()
        {
            _host = host.Text;
            _port = port.Text;
            _apiKey = key.Password;
            url.Text = BaseUrl;
            _probeCount = null;
            Refresh();
        }
        host.TextChanged += (_, _) => Changed();
        port.TextChanged += (_, _) => Changed();
        key.PasswordChanged += (_, _) => Changed();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, UIElement control)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Ui.Text(label, 13, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 12, 10);
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        Row("Address", host);
        if (UsesPort)
        {
            port.HorizontalAlignment = HorizontalAlignment.Left;
            Row("Port", port);
        }
        if (AllowsKey)
        {
            Row("API key", key);
        }
        Row("URL", url);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (_probing)
        {
            var spinner = new Spinner { Margin = new Thickness(0, 0, 8, 0) };
            spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            status.Children.Add(spinner);
            status.Children.Add(Ui.Secondary("Connecting…"));
        }
        else if (_probeCount is { } count)
        {
            status.Children.Add(Ui.Status(count == 0 ? "✔ Reached the server" : $"✔ Reached the server — {count} model(s)", "SystemFillColorSuccessBrush"));
        }
        else if (_probeError is not null)
        {
            status.Children.Add(Ui.Status("✖ Could not connect", "SystemFillColorCriticalBrush"));
        }
        var test = Ui.Button("Test Connection", () => _ = ProbeAsync());
        test.IsEnabled = !_probing && _host.Trim().Length > 0;

        var find = Ui.Button(_showScanner ? "Hide the Scan" : "Find Servers on My Network", ToggleScanner,
            tooltip: "Look for model servers (a DGX Spark, vLLM, SGLang, Ollama, LM Studio, llama.cpp) on your network and on this PC");
        find.Margin = new Thickness(8, 0, 0, 0);
        var panel = Ui.Stack(Heading("Connection"), Lead(hint), grid, Ui.Stack(Orientation.Horizontal, test, find, status));
        if (_pinnedCertificate is { } pin)
        {
            var trust = Ui.Check($"Trust this server's self-signed certificate (SHA-256 {CertificateProbe.Shorten(pin)})", _trustPin, value =>
            {
                _trustPin = value;
                _probeCount = null;
            });
            trust.Margin = new Thickness(0, 10, 0, 0);
            trust.ToolTip = pin;
            panel.Children.Add(trust);
        }
        if (_scanNote is { } note)
        {
            var hintText = Ui.Secondary(note);
            hintText.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(hintText);
        }
        if (_showScanner && _scanner is { } scanner)
        {
            var card = Ui.Card(GuideVisuals.Detach(scanner));
            card.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(card);
        }
        if (_probeError is { } error)
        {
            var troubleshooting = _kind switch
            {
                ProviderKind.OpenAICompat => "Check the server is bound to 0.0.0.0 rather than 127.0.0.1 — a server listening only on loopback is unreachable from another machine. Then confirm the port and that no firewall (on either machine) is in the way.",
                ProviderKind.Ollama => "Run `ollama serve` in a terminal, then `ollama list` to confirm a model is installed.",
                ProviderKind.LmStudio => "In LM Studio, open the Developer tab, load a model, and press Start Server.",
                _ => "Check the key is current and has credit or quota on the account.",
            };
            var card = Ui.Card(Ui.Stack(Ui.Status(error, "SystemFillColorCriticalBrush"), Ui.Secondary(troubleshooting)));
            card.Margin = new Thickness(0, 14, 0, 0);
            card.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
            panel.Children.Add(card);
        }
        return panel;
    }

    private void ToggleScanner()
    {
        _showScanner = !_showScanner;
        if (_showScanner && _scanner is null)
        {
            _scanner = new ScannerPanel(ScannerPanel.Purpose.ModelServer);
            _scanner.ServiceChosen += UseFoundServer;
            if (SelfTest.Current is null) _scanner.Start();
        }
        Render();
    }

    /// <summary>For the self-test: the connection step with the scanner open, showing sample results.</summary>
    internal void ShowScannerDemo(IEnumerable<FoundHost> hosts)
    {
        _kind = ProviderKind.OpenAICompat;
        Go(Step.Connection);
        _showScanner = false;
        ToggleScanner();
        _scanner!.ShowDemo(hosts);
    }

    /// <summary>Fill the connection from a server the scan found.</summary>
    private void UseFoundServer(FoundHost host, FoundService service)
    {
        _kind = service.Kind switch
        {
            ServiceKind.Ollama => ProviderKind.Ollama,
            ServiceKind.LmStudio => ProviderKind.LmStudio,
            _ => ProviderKind.OpenAICompat,
        };
        var address = host.IsLoopback ? "127.0.0.1" : host.Address.ToString();
        if (service.Tls)
        {
            _host = service.BaseUrl ?? $"https://{address}:{service.Port}/v1";
            _port = "";
            _pinnedCertificate = service.CertificateFingerprint;
            _trustPin = true;
        }
        else
        {
            _host = address;
            _port = service.Port.ToString();
            _pinnedCertificate = null;
        }
        _discovered = service.Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        _probeCount = service.Models.Count > 0 ? service.Models.Count : null;
        _probeError = null;
        if (_discovered.Count > 0) _modelId = _discovered[0];
        _scanNote = service.NeedsKey
            ? host.LooksLikeSpark
                ? "This server wants an API key. For a DGX Spark with Spark Swapper, it's under Keys & connection on the Swapper's page — or go back to the first page and use the DGX Spark guide, which fetches it for you."
                : "This server wants an API key — paste it above."
            : $"Filled in from {host.Headline}.";
        _showScanner = false;
        Render();
    }

    private async Task ProbeAsync()
    {
        _probing = true;
        _probeError = null;
        Render();
        try
        {
            var models = await new OpenAiClient(Draft()).ListModelsAsync();
            _discovered = models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            _probeCount = models.Count;
            if (_modelId.Trim().Length == 0 && _discovered.Count > 0) _modelId = _discovered[0];
        }
        catch (Exception error)
        {
            _discovered = [];
            _probeCount = null;
            _probeError = AgentHost.Describe(error);
        }
        finally
        {
            _probing = false;
            if (_step == Step.Connection) Render();
        }
    }

    private static bool LooksToolCapable(string id)
    {
        var lowered = id.ToLowerInvariant();
        return new[] { "qwen", "deepseek", "gpt-4", "gpt-5", "llama-3", "llama3", "mistral", "command-r", "glm", "gpt-oss" }.Any(lowered.Contains);
    }

    private UIElement ModelStep()
    {
        var typed = Ui.Field(_modelId, _discovered.Count == 0 ? "model id" : "or type a model id", mono: true);
        typed.TextChanged += (_, _) =>
        {
            _modelId = typed.Text;
            Refresh();
        };
        var panel = Ui.Stack(Heading("Pick a model"));
        if (_discovered.Count == 0)
        {
            panel.Children.Add(Lead("The server did not return a model list, so type the model id it expects. On a vLLM or SGLang server that is the name it was launched with."));
            panel.Children.Add(typed);
        }
        else
        {
            panel.Children.Add(Lead($"These are the models {BaseUrl} reports."));
            var list = new StackPanel();
            foreach (var candidate in _discovered)
            {
                var radio = new RadioButton { GroupName = "model", IsChecked = candidate == _modelId, Margin = new Thickness(0, 3, 0, 3) };
                var name = Ui.Text(candidate, 12.5, wrap: false);
                name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var content = Ui.Stack(Orientation.Horizontal, name);
                if (LooksToolCapable(candidate))
                {
                    var badge = Views.Skills.SkillVisuals.Badge("tools", Color.FromRgb(0x10, 0x7C, 0x10));
                    badge.Margin = new Thickness(8, 0, 0, 0);
                    content.Children.Add(badge);
                }
                radio.Content = content;
                var value = candidate;
                radio.Checked += (_, _) =>
                {
                    _modelId = value;
                    typed.Text = value;
                };
                list.Children.Add(radio);
            }
            panel.Children.Add(Ui.Card(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
            typed.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(typed);
        }
        var note = Ui.Card(Ui.Secondary("An agent needs a model that can call tools. Qwen3, Qwen2.5-Coder, and DeepSeek-V3-class models work well; very small models will struggle."));
        note.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private async Task DetectAsync()
    {
        _detecting = true;
        if (_step == Step.Tuning) Render();
        var info = await new OpenAiClient(Draft()).ModelInfoAsync();
        _detectedContext = info.ContextWindow;
        _detectedModel = info.Id.Length == 0 ? null : info.Id;
        _detecting = false;
        if (_step == Step.Tuning) Render();
    }

    private UIElement Tuning()
    {
        var panel = Ui.Stack(Heading("Context & thinking"));
        panel.Children.Add(Ui.Text("Context window", 14, FontWeights.SemiBold));
        var detection = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
        var again = Ui.Button("Detect Again", () => _ = DetectAsync());
        again.IsEnabled = !_detecting;
        DockPanel.SetDock(again, Dock.Right);
        detection.Children.Add(again);
        detection.Children.Add(_detecting
            ? Ui.Secondary("Asking the server…")
            : _detectedContext is { } detected
                ? Ui.Status($"✔ The server reports {detected:N0} tokens" + (_detectedModel is { } m && m != _modelId ? $" for {m}" : ""), "SystemFillColorSuccessBrush")
                : Ui.Secondary("The server didn't report a window — pick one below."));
        panel.Children.Add(detection);

        var custom = Ui.Field(_customContext, "tokens, e.g. 400000", width: 200);
        custom.Visibility = _contextChoice == -1 ? Visibility.Visible : Visibility.Collapsed;
        custom.HorizontalAlignment = HorizontalAlignment.Left;
        custom.TextChanged += (_, _) =>
        {
            _customContext = custom.Text;
            Refresh();
        };
        var options = new WrapPanel();
        void Option(string label, int? value)
        {
            var radio = new RadioButton { Content = label, GroupName = "context", IsChecked = _contextChoice == value, Margin = new Thickness(0, 4, 18, 4) };
            radio.Checked += (_, _) =>
            {
                _contextChoice = value;
                custom.Visibility = value == -1 ? Visibility.Visible : Visibility.Collapsed;
                Refresh();
            };
            options.Children.Add(radio);
        }
        Option(_detectedContext is { } d ? $"Auto ({Menus.ShortTokens(d)}, follows the server)" : "Auto-detect", null);
        foreach (var (label, tokens) in ContextPresets) Option(label, tokens);
        Option("Custom…", -1);
        panel.Children.Add(options);
        panel.Children.Add(custom);
        var hint = Ui.Secondary("Auto is best: it re-reads the window every turn, so a model swap on the server (1M ⇄ 512K) is picked up by itself. Choose a fixed size only to cap it lower. Long chats are summarized automatically at 75%, or any time with /compact.");
        hint.Margin = new Thickness(0, 8, 0, 18);
        panel.Children.Add(hint);

        panel.Children.Add(Ui.Text("Default thinking", 14, FontWeights.SemiBold));
        var thinking = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Thinking(string label, string raw)
        {
            var radio = new RadioButton { Content = label, GroupName = "thinking", IsChecked = _thinking == raw, Margin = new Thickness(0, 3, 0, 3) };
            radio.Checked += (_, _) => _thinking = raw;
            thinking.Children.Add(radio);
        }
        Thinking("Server default", "");
        foreach (var level in ThinkingLevels.All) Thinking($"{level.Label()} — {level.Blurb()}", level.RawValue());
        panel.Children.Add(thinking);
        var thinkingHint = Ui.Secondary("For reasoning models (Qwen3.x, DeepSeek, GLM, gpt-oss). Off skips thinking entirely for the fastest replies; Max thinks longest. Change it per chat from the composer or with /think.");
        thinkingHint.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(thinkingHint);
        return panel;
    }

    private UIElement Permissions()
    {
        var panel = Ui.Stack(Heading("How much can the agent do?"),
            Lead("This is the preset new chats start with. Each chat keeps the preset it was created with."));
        foreach (var preset in PermissionPresets.All)
        {
            var chosen = preset;
            panel.Children.Add(Choice(Icons.ForPreset(preset), preset.Label(), preset.Detail(), _preset == preset, () =>
            {
                _preset = chosen;
                Render();
            }, preset == PermissionPreset.FullAccess ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush"));
        }
        return panel;
    }

    private UIElement ProjectStep()
    {
        var panel = Ui.Stack(Heading("Open a project"),
            Lead("The project folder is the agent's working directory and its permission boundary. You can skip this and open one whenever you like."));
        if (_model.Project is { } project)
        {
            panel.Children.Add(Ui.Row(_model.ProjectName ?? project, project, Ui.Button("Change…", () =>
            {
                _model.ChooseProject();
                Render();
            }), Icons.FolderOpen));
        }
        else
        {
            panel.Children.Add(Ui.Button("Choose Folder…", () =>
            {
                _model.ChooseProject();
                Render();
            }, accent: true));
        }
        return panel;
    }

    private UIElement Done()
    {
        var check = Ui.Glyph(Icons.Completed, 40, "SystemFillColorSuccessBrush");
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 0, 0, 12);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, string value, bool mono = false)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Ui.Secondary(label, 13);
            name.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(name, row);
            grid.Children.Add(name);
            var text = Ui.Text(value, 13);
            text.Margin = new Thickness(0, 3, 0, 3);
            if (mono) text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
        }
        Row("Server", BaseUrl, mono: true);
        Row("Model", _modelId.Length == 0 ? "—" : _modelId, mono: true);
        Row("Permissions", _preset.Label());
        Row("Project", _model.Project ?? "none yet");
        var hint = Ui.Secondary("Run this wizard again any time from Settings or the Help menu.");
        hint.Margin = new Thickness(0, 12, 0, 0);
        return Ui.Stack(check, Ui.Text("You're set up", 28, FontWeights.SemiBold), new Border { Height = 12 }, Ui.Card(grid), hint);
    }
}
