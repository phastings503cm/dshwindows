using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
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
        _ => "OpenAI-compatible server (vLLM, SGLang, llama.cpp, DGX Spark)",
    };
}

/// <summary>The configured model routes: which one is active, add/edit/remove, and a connection test.</summary>
public sealed class ModelsPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = Ui.Secondary("");

    public ModelsPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Models"));
        var intro = Ui.Secondary("Any OpenAI-compatible server works: a local Ollama or LM Studio, vLLM or SGLang on your LAN, a DGX Spark, or a hosted API. API keys are kept in Windows Credential Manager, never in the settings file.");
        intro.Margin = new Thickness(0, 6, 0, 8);
        page.Children.Add(intro);
        page.Children.Add(Ui.Buttons(
            Ui.Button("Add Server…", () => Edit(new ProviderProfile(ProviderKind.OpenAICompat, "New server", "http://127.0.0.1:8000/v1", "")), accent: true),
            Ui.Button("Test Active", () => _ = TestAsync()),
            Ui.Button("Setup Wizard…", model.ShowWizard)));
        _status.Margin = new Thickness(0, 8, 0, 4);
        page.Children.Add(_status);
        page.Children.Add(Ui.Section("Routes"));
        page.Children.Add(_list);
        Content = Ui.Scroll(page);
        Rebuild();
        model.Config.Providers.CollectionChanged += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var config = _model.Config;
        foreach (var provider in config.Providers.ToList())
        {
            var active = provider.RouteId == config.ActiveRoute;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            if (active)
            {
                var badge = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0) };
                badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                badge.Child = Ui.Text("Active", 11, FontWeights.SemiBold, "TextOnAccentFillColorPrimaryBrush", wrap: false);
                badge.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Add(badge);
            }
            else
            {
                var use = Ui.Button("Use", () =>
                {
                    config.ActiveRoute = provider.RouteId;
                    _model.Host.ResetRouteCache();
                    Rebuild();
                });
                use.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(use);
            }
            actions.Children.Add(Ui.Button("Edit…", () => Edit(provider)));
            var remove = new Button { Content = Icons.Delete, ToolTip = "Remove", Margin = new Thickness(6, 0, 0, 0) };
            remove.SetResourceReference(StyleProperty, "IconButton");
            remove.Click += (_, _) =>
            {
                if (!Dialog.Confirm($"Remove {provider.DisplayName}?", "Its saved API key is removed too.", "Remove", destructive: true)) return;
                config.RemoveProvider(provider);
                try { SecretStore.Delete(SecretStore.ProviderTarget(provider.RouteId)); } catch (Exception) { }
                _model.Host.ResetRouteCache();
            };
            actions.Children.Add(remove);

            var detail = $"{provider.Model} · {provider.BaseUrl}";
            if (provider.ContextWindow is { } window) detail += $" · {window:N0} ctx";
            var row = Ui.Row(provider.Name, detail, actions, Icons.ForProvider(provider.Kind));
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) Edit(provider);
            };
            if (active) row.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            _list.Children.Add(row);
        }
        if (config.Providers.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary("No routes yet. Add a server or run the setup wizard.")));
    }

    private void Edit(ProviderProfile provider)
    {
        var editor = new ProviderEditorWindow(_model, provider) { Owner = Ui.Owner(this) };
        if (editor.ShowDialog() == true) Rebuild();
    }

    private async Task TestAsync()
    {
        if (_model.Config.ActiveProvider is not { } provider)
        {
            _status.Text = "No active route.";
            return;
        }
        _status.Text = "Testing…";
        try
        {
            var models = await new OpenAiClient(provider).ListModelsAsync();
            var info = await new OpenAiClient(provider).ModelInfoAsync();
            var window = info.ContextWindow is { } w ? $", {w:N0}-token window" : "";
            _status.Text = $"✔ Connected — {models.Count} model(s) served{window}.";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        catch (Exception error)
        {
            _status.Text = AgentHost.Describe(error);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
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

        foreach (var kind in ProviderKinds.All) _kind.Items.Add(new ComboBoxItem { Content = kind.Label(), Tag = kind, IsSelected = kind == profile.Kind });
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
            Labeled("Base URL", _url, "The OpenAI-compatible endpoint, usually ending in /v1."),
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
        var probe = new ProviderProfile(SelectedKind, "", _url.Text.Trim(), "");
        _keyRow.Visibility = probe.NeedsApiKey || _key.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProviderProfile Draft()
    {
        var headers = _headers.Text.Split('\n')
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
        return new ProviderProfile(SelectedKind, _name.Text.Trim(), _url.Text.Trim(), _modelName.Text.Trim())
        {
            Temperature = double.TryParse(_temperature.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null,
            MaxOutputTokens = int.TryParse(_maxTokens.Text, out var m) && m > 0 ? m : null,
            ContextWindow = int.TryParse(_context.Text.Replace(",", "").Replace("_", ""), out var c) && c > 0 ? c : null,
            ReasoningEffort = _thinking.SelectedItem is ComboBoxItem { Tag: string raw } && raw.Length > 0 ? raw : null,
            Vision = _vision.IsChecked == true ? null : false,
            CustomHeaders = headers.Count == 0 ? null : headers,
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
            var models = (await new OpenAiClient(Probe()).ListModelsAsync()).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
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
        var info = await new OpenAiClient(Probe()).ModelInfoAsync();
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
        if (_original.RouteId != profile.RouteId && config.Providers.Any(p => p.RouteId == _original.RouteId))
        {
            config.RemoveProvider(_original);
            try { SecretStore.Delete(SecretStore.ProviderTarget(_original.RouteId)); } catch (Exception) { }
        }
        config.Activate(profile);
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
