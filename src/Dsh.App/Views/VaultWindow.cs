using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views;

/// <summary>The Credentials Vault (Ctrl+Shift+K): browse and search the API keys, tokens and
/// passwords the agent can use as <c>{{vault:NAME}}</c>; reveal a value (after Windows Hello or the
/// Windows password), copy, edit, add and delete. Values show as their SHA-256 fingerprint until
/// revealed.</summary>
public sealed class VaultWindow : Window
{
    private readonly AgentHost _host;
    private CredentialVault Vault => _host.Vault;

    private readonly TextBox _search = Ui.Field(placeholder: "Search name, service, tag…", width: 240);
    private readonly TextBlock _count = Ui.Secondary("", 12);
    private readonly Button _lock = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly TextBlock _error = Ui.Status("", "SystemFillColorCriticalBrush");

    private string? _selectedId;
    private bool _adding;
    /// <summary>Values may be shown for as long as this window stays open, once the owner checked in.</summary>
    private bool _unlocked;

    public VaultWindow(AgentHost host)
    {
        _host = host;
        Title = "Credentials Vault";
        Width = 880;
        Height = 600;
        MinWidth = 700;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        // Header
        var header = new DockPanel { Margin = new Thickness(18, 14, 18, 12) };
        var add = Ui.Button("Add", BeginAdd, accent: true, tooltip: "Add a credential");
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        _lock.Content = Icons.Lock;
        _lock.ToolTip = "Values are unlocked for this window — lock them again";
        _lock.SetResourceReference(StyleProperty, "IconButton");
        _lock.Margin = new Thickness(0, 0, 8, 0);
        _lock.Click += (_, _) =>
        {
            _unlocked = false;
            _lock.Visibility = Visibility.Collapsed;
            Refresh();
        };
        DockPanel.SetDock(_lock, Dock.Right);
        header.Children.Add(_lock);
        _search.Margin = new Thickness(0, 0, 8, 0);
        _search.TextChanged += (_, _) => RefreshList();
        DockPanel.SetDock(_search, Dock.Right);
        header.Children.Add(_search);
        var key = Ui.Glyph(Icons.Key, 18, "AccentTextFillColorPrimaryBrush");
        key.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(key, Dock.Left);
        header.Children.Add(key);
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Subtitle("Credentials Vault"));
        _count.Margin = new Thickness(10, 3, 0, 0);
        titles.Children.Add(_count);
        header.Children.Add(titles);

        // Body: list | detail
        var listScroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = 270,
        };
        var listEdge = new Border { Child = listScroll, BorderThickness = new Thickness(0, 0, 1, 0) };
        listEdge.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        listEdge.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        var detailScroll = new ScrollViewer
        {
            Content = _detail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 14, 20, 16),
        };
        var body = new DockPanel();
        DockPanel.SetDock(listEdge, Dock.Left);
        body.Children.Add(listEdge);
        body.Children.Add(detailScroll);

        // Footer
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14) };
        var done = Ui.Button("Done", Close);
        done.IsCancel = true;
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        var note = Ui.Secondary("Values are encrypted for your Windows account (DPAPI). The agent writes {{vault:NAME}} and never sees them.", 11.5);
        note.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(note);

        _error.Margin = new Thickness(18, 6, 18, 0);
        _error.Visibility = Visibility.Collapsed;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var topRule = Ui.Divider();
        DockPanel.SetDock(topRule, Dock.Top);
        root.Children.Add(topRule);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var bottomRule = Ui.Divider();
        DockPanel.SetDock(bottomRule, Dock.Bottom);
        root.Children.Add(bottomRule);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(_error);
        root.Children.Add(body);
        Content = root;

        InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(() => _search.Focus()), Key.F, ModifierKeys.Control));
        _host.PropertyChanged += OnHostChanged;
        Closed += (_, _) => _host.PropertyChanged -= OnHostChanged;
        Refresh();
    }

    private void OnHostChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Another window (or the agent's use count) changed the vault.
        if (e.PropertyName == nameof(AgentHost.VaultRevision)) RefreshList();
    }

    // MARK: - Rendering

    private void Refresh()
    {
        RefreshList();
        _detail.Content = DetailView();
    }

    private void RefreshList()
    {
        var all = Vault.All;
        _count.Text = $"{all.Count} credential{(all.Count == 1 ? "" : "s")}";
        var query = _search.Text.Trim();
        var entries = Vault.Search(query);
        _list.Children.Clear();
        if (entries.Count == 0)
        {
            var empty = Ui.Text(query.Length == 0 ? "No credentials yet." : $"Nothing matches “{query}”.", 12, brushKey: "TextFillColorTertiaryBrush");
            empty.Margin = new Thickness(10);
            _list.Children.Add(empty);
        }
        foreach (var entry in entries) _list.Children.Add(Row(entry));
        if (_selectedId is not null && all.All(e => e.Id != _selectedId))
        {
            _selectedId = null;
            _detail.Content = DetailView();
        }
    }

    private UIElement Row(VaultEntry entry)
    {
        var selected = entry.Id == _selectedId;
        var icon = Ui.Glyph(entry.Kind switch
        {
            VaultKind.Password => Icons.Lock,
            VaultKind.Other => Icons.Document,
            _ => Icons.Key,
        }, 13);
        icon.Width = 20;
        icon.Margin = new Thickness(0, 0, 8, 0);
        var lines = new StackPanel();
        var name = Ui.Text(entry.Name, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
        name.SetResourceReference(FontFamilyProperty, "MonoFont");
        lines.Children.Add(name);
        var subtitle = string.Join(" · ", new[] { entry.Kind.Label(), entry.Tags.Count == 0 ? null : string.Join(", ", entry.Tags), entry.Fingerprint }
            .Where(p => !string.IsNullOrEmpty(p)));
        lines.Children.Add(Ui.Text(subtitle, 11, brushKey: "TextFillColorTertiaryBrush", wrap: false));
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        if (entry.Access != VaultAccess.Allowed)
        {
            var access = Ui.Glyph(entry.Access == VaultAccess.Never ? Icons.Stop : Icons.Info, 11, "SystemFillColorCautionBrush");
            access.ToolTip = entry.Access.Label();
            access.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(access, Dock.Right);
            dock.Children.Add(access);
        }
        dock.Children.Add(lines);
        var row = new Border
        {
            Child = dock,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseEnter += (_, _) =>
        {
            if (!selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        };
        row.MouseLeave += (_, _) =>
        {
            if (!selected) row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            _adding = false;
            _selectedId = entry.Id;
            ShowError(null);
            Refresh();
        };
        return row;
    }

    private UIElement DetailView()
    {
        if (_adding) return new VaultEditor(this, null);
        if (_selectedId is not null && Vault.All.FirstOrDefault(e => e.Id == _selectedId) is { } entry) return new VaultEditor(this, entry);
        return EmptyDetail();
    }

    private UIElement EmptyDetail()
    {
        var empty = Vault.All.Count == 0;
        var panel = new StackPanel { Margin = new Thickness(30, 60, 30, 20), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440 };
        var glyph = Ui.Glyph(Icons.Key, 32, "TextFillColorTertiaryBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(glyph);
        var title = Ui.Text(empty ? "Keep API keys, tokens and passwords here." : "Select a credential.", 13.5, FontWeights.SemiBold,
            "TextFillColorSecondaryBrush");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 12, 0, 6);
        panel.Children.Add(title);
        var blurb = Ui.Text("The agent finds them with vault_search and uses them as {{vault:NAME}} — in a shell command, a .env file, " +
                            "a request header — without ever seeing the value. Skills can refer to them the same way.", 12,
            brushKey: "TextFillColorTertiaryBrush");
        blurb.TextAlignment = TextAlignment.Center;
        panel.Children.Add(blurb);
        if (empty)
        {
            var add = Ui.Button("Add a credential", BeginAdd, accent: true);
            add.HorizontalAlignment = HorizontalAlignment.Center;
            add.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(add);
        }
        return panel;
    }

    // MARK: - Actions

    private void BeginAdd()
    {
        _adding = true;
        _selectedId = null;
        ShowError(null);
        Refresh();
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool Save(VaultEntry? existing, VaultEntry edited, string? value)
    {
        try
        {
            if (existing is null)
            {
                var added = Vault.Add(edited.Name, value ?? "", edited.Kind, edited.Description, edited.Tags, edited.Username, edited.Url, edited.Access);
                _adding = false;
                _selectedId = added.Id;
            }
            else
            {
                Vault.Update(edited, value);
            }
            ShowError(null);
            Refresh();
            return true;
        }
        catch (VaultException error)
        {
            ShowError(error.Message);
            return false;
        }
    }

    private void Delete(VaultEntry entry)
    {
        if (!Dialog.Confirm($"Delete {entry.Name}?",
                $"The value is removed from the vault. Anything that writes {entry.Placeholder} will stop working.", "Delete", destructive: true))
            return;
        try
        {
            Vault.Delete(entry.Id);
            _selectedId = null;
            ShowError(null);
            Refresh();
        }
        catch (VaultException error)
        {
            ShowError(error.Message);
        }
    }

    private async Task<bool> UnlockAsync()
    {
        if (_unlocked) return true;
        var (ok, error) = await OwnerCheck.ConfirmAsync(this, "show a credential from its vault");
        if (error is not null) ShowError(error);
        _unlocked = ok;
        _lock.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        return ok;
    }

    private string? ValueOf(VaultEntry entry)
    {
        try
        {
            return Vault.Value(entry.Id);
        }
        catch (VaultException error)
        {
            ShowError(error.Message);
            return null;
        }
    }

    // MARK: - Editor

    /// <summary>Add or edit one credential.</summary>
    private sealed class VaultEditor : StackPanel
    {
        private readonly VaultWindow _window;
        private readonly VaultEntry? _entry;
        private readonly TextBox _name;
        private readonly ComboBox _kind = new() { MinWidth = 160 };
        private readonly TextBox _description;
        private readonly TextBox _username;
        private readonly TextBox _url;
        private readonly TextBox _tags;
        private readonly ComboBox _access = new() { MinWidth = 160 };
        private readonly PasswordBox _value = new() { Padding = new Thickness(8, 5, 8, 5) };
        private readonly TextBox _valueShown = Ui.Field(mono: true);
        private readonly TextBlock _newFingerprint = Ui.Text("", 11, brushKey: "TextFillColorTertiaryBrush");
        private readonly TextBlock _current = Ui.Text("", 12.5, brushKey: "TextFillColorSecondaryBrush");
        private string? _revealed;
        private bool _typingShown;

        public VaultEditor(VaultWindow window, VaultEntry? entry)
        {
            _window = window;
            _entry = entry;
            _name = Ui.Field(entry?.Name ?? "", "OPENAI_API_KEY", mono: true);
            _description = Ui.Field(entry?.Description ?? "", "What it's for");
            _username = Ui.Field(entry?.Username ?? "", "optional");
            _url = Ui.Field(entry?.Url ?? "", "https://… (optional)");
            _tags = Ui.Field(entry is null ? "" : string.Join(", ", entry.Tags), "comma, separated");
            foreach (var kind in Enum.GetValues<VaultKind>()) _kind.Items.Add(new ComboBoxItem { Content = kind.Label(), Tag = kind });
            _kind.SelectedIndex = (int)(entry?.Kind ?? VaultKind.ApiKey);
            foreach (var access in Enum.GetValues<VaultAccess>()) _access.Items.Add(new ComboBoxItem { Content = access.Label(), Tag = access });
            _access.SelectedIndex = (int)(entry?.Access ?? VaultAccess.Allowed);

            Children.Add(Ui.Subtitle(entry is null ? "New credential" : entry.Name));
            var intro = Ui.Secondary(entry is null
                ? "Name it the way the agent should write it — letters, digits and _ (e.g. GITHUB_TOKEN)."
                : $"The agent writes {entry.Placeholder} wherever the value goes.", 12);
            intro.Margin = new Thickness(0, 2, 0, 12);
            Children.Add(intro);

            Children.Add(Field("Name", _name));
            Children.Add(Field("Kind", _kind));
            Children.Add(Field("Description", _description));
            Children.Add(Field("Username", _username));
            Children.Add(Field("URL", _url));
            Children.Add(Field("Tags", _tags));
            Children.Add(Field("Agent access", _access,
                "“Ask first” asks once per chat before the agent uses it; “Never” keeps it listed but unusable."));
            Children.Add(ValueSection());
            if (entry is not null)
            {
                var parts = new List<string> { $"used {entry.UseCount} time{(entry.UseCount == 1 ? "" : "s")}" };
                if (entry.LastUsedAt is { } last) parts.Add($"last {QueueLog.When(last)}");
                parts.Add($"updated {QueueLog.When(entry.UpdatedAt)}");
                var usage = Ui.Text(string.Join(" · ", parts), 11, brushKey: "TextFillColorTertiaryBrush");
                usage.Margin = new Thickness(0, 10, 0, 0);
                Children.Add(usage);
            }
            Children.Add(Buttons());
            Loaded += (_, _) =>
            {
                if (_entry is null) _name.Focus();
            };
        }

        private static UIElement Field(string label, FrameworkElement control, string? hint = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = Ui.Text(label, 12.5, brushKey: "TextFillColorSecondaryBrush");
            text.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(text);
            var right = new StackPanel();
            control.HorizontalAlignment = control is ComboBox ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            right.Children.Add(control);
            if (hint is not null)
            {
                var detail = Ui.Text(hint, 11, brushKey: "TextFillColorTertiaryBrush");
                detail.Margin = new Thickness(0, 3, 0, 0);
                right.Children.Add(detail);
            }
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        private UIElement ValueSection()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(Ui.Text("Value", 12.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush"));
            if (_entry is { } entry)
            {
                _current.Text = entry.Fingerprint;
                _current.SetResourceReference(FontFamilyProperty, "MonoFont");
                _current.TextTrimming = TextTrimming.CharacterEllipsis;
                _current.MaxHeight = 60;
                var reveal = SmallIcon(Icons.View, "Show the value", () => _ = ToggleRevealAsync());
                var copy = SmallIcon(Icons.Copy, "Copy the value", () => _ = CopyAsync());
                var row = new DockPanel();
                DockPanel.SetDock(copy, Dock.Right);
                row.Children.Add(copy);
                DockPanel.SetDock(reveal, Dock.Right);
                row.Children.Add(reveal);
                row.Children.Add(_current);
                var box = new Border { Child = row, Padding = new Thickness(10, 6, 6, 6), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 8) };
                box.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                panel.Children.Add(box);
            }
            _value.SetResourceReference(FontFamilyProperty, "MonoFont");
            _valueShown.Visibility = Visibility.Collapsed;
            _value.PasswordChanged += (_, _) => ValueTyped(_value.Password);
            _valueShown.TextChanged += (_, _) => ValueTyped(_valueShown.Text);
            var toggle = SmallIcon(Icons.View, "Show what you type", () =>
            {
                _typingShown = !_typingShown;
                if (_typingShown) _valueShown.Text = _value.Password;
                else _value.Password = _valueShown.Text;
                _value.Visibility = _typingShown ? Visibility.Collapsed : Visibility.Visible;
                _valueShown.Visibility = _typingShown ? Visibility.Visible : Visibility.Collapsed;
            });
            var input = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(toggle, Dock.Right);
            input.Children.Add(toggle);
            var fields = new Grid();
            fields.Children.Add(_value);
            fields.Children.Add(_valueShown);
            input.Children.Add(fields);
            panel.Children.Add(input);
            var hint = Ui.Text(_entry is null ? "The secret itself. It is encrypted as soon as you add it."
                                              : "Type a new value to replace it; leave empty to keep the current one.", 11,
                brushKey: "TextFillColorTertiaryBrush");
            hint.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(hint);
            _newFingerprint.Visibility = Visibility.Collapsed;
            panel.Children.Add(_newFingerprint);
            return panel;
        }

        private string NewValue => _typingShown ? _valueShown.Text : _value.Password;

        private void ValueTyped(string text)
        {
            _newFingerprint.Text = text.Length == 0 ? "" : $"New fingerprint: {CredentialVault.Fingerprint(text)}";
            _newFingerprint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private UIElement Buttons()
        {
            var dock = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            var save = Ui.Button(_entry is null ? "Add Credential" : "Save", Save, accent: true);
            save.IsDefault = true;
            DockPanel.SetDock(save, Dock.Right);
            dock.Children.Add(save);
            if (_entry is null)
            {
                var cancel = Ui.Button("Cancel", () =>
                {
                    _window._adding = false;
                    _window.ShowError(null);
                    _window.Refresh();
                });
                cancel.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(cancel, Dock.Right);
                dock.Children.Add(cancel);
            }
            else
            {
                var delete = Ui.Button("Delete", () => _window.Delete(_entry));
                delete.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
                DockPanel.SetDock(delete, Dock.Left);
                dock.Children.Add(delete);
            }
            return dock;
        }

        private void Save()
        {
            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                _window.ShowError("Give the credential a name.");
                _name.Focus();
                return;
            }
            var value = NewValue;
            if (_entry is null && value.Length == 0)
            {
                _window.ShowError("Enter the secret value.");
                return;
            }
            var edited = (_entry ?? new VaultEntry()) with
            {
                Name = name,
                Kind = (VaultKind)((ComboBoxItem)_kind.SelectedItem).Tag,
                Description = _description.Text.Trim(),
                Username = _username.Text,
                Url = _url.Text,
                Tags = _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Access = (VaultAccess)((ComboBoxItem)_access.SelectedItem).Tag,
            };
            if (_window.Save(_entry, edited, value.Length == 0 ? null : value))
            {
                _value.Password = "";
                _valueShown.Text = "";
            }
        }

        private async Task ToggleRevealAsync()
        {
            if (_entry is null) return;
            if (_revealed is not null)
            {
                _revealed = null;
                _current.Text = _entry.Fingerprint;
                _current.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                return;
            }
            if (!await _window.UnlockAsync()) return;
            _revealed = _window.ValueOf(_entry);
            if (_revealed is null) return;
            _current.Text = _revealed;
            _current.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }

        private async Task CopyAsync()
        {
            if (_entry is null || !await _window.UnlockAsync() || _window.ValueOf(_entry) is not { } value) return;
            try
            {
                Clipboard.SetText(value);
                _window.ShowError(null);
            }
            catch (Exception error)
            {
                _window.ShowError($"Couldn't copy: {error.Message}");
            }
        }

        private static Button SmallIcon(string glyph, string tooltip, Action action)
        {
            var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(4, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, "IconButton");
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            button.Click += (_, _) => action();
            return button;
        }
    }
}
