using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;

namespace Dsh.App.Views.Settings;

/// <summary>Where the Spark Swapper lives, how to log in to it, and what the Spark is serving.</summary>
public sealed class SparkPage : UserControl
{
    private readonly AppModel _model;
    private readonly TextBox _url;
    private readonly TextBox _user;
    private readonly PasswordBox _password = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly StackPanel _certificate = new();
    private readonly StackPanel _status = new();
    private readonly Button _connect;
    private bool _connecting;

    public SparkPage(AppModel model)
    {
        _model = model;
        var spark = model.Spark;
        _url = Ui.Field(spark.Url, "https://192.168.68.69:8999", mono: true);
        _user = Ui.Field(spark.Username);
        _password.Password = spark.Password;
        _connect = Ui.Button("Save & Connect", () => _ = ConnectAsync(), accent: true);

        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("DGX Spark"));
        var intro = Ui.Secondary("If your models run on a DGX Spark with the Spark Swapper, DSH can switch what it serves — from the model menu under the composer or with /swap — and every chat follows the switch.");
        intro.Margin = new Thickness(0, 6, 0, 0);
        page.Children.Add(intro);

        page.Children.Add(Ui.Section("Spark Swapper"));
        var forget = Ui.Button("Forget Certificate", () =>
        {
            spark.PinnedFingerprint = null;
            Refresh();
        });
        forget.Visibility = spark.PinnedFingerprint is null ? Visibility.Collapsed : Visibility.Visible;
        page.Children.Add(Ui.Card(Ui.Stack(
            Field("Address", _url),
            Field("Username", _user),
            Field("Password", _password),
            Ui.Buttons(_connect, forget),
            Hint("The login you created on the swapper's web page. The password is kept in Windows Credential Manager."))));

        page.Children.Add(_certificate);
        page.Children.Add(Ui.Section("Status"));
        page.Children.Add(_status);
        Content = Ui.Scroll(page);

        PropertyChangedEventHandler handler = (_, _) => Dispatcher.BeginInvoke(Refresh);
        spark.PropertyChanged += handler;
        Unloaded += (_, _) => spark.PropertyChanged -= handler;
        Refresh();
    }

    private static StackPanel Field(string label, UIElement control)
    {
        var title = Ui.Text(label, 12.5, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 4);
        var panel = Ui.Stack(title, control);
        panel.Margin = new Thickness(0, 0, 0, 10);
        return panel;
    }

    private static TextBlock Hint(string text)
    {
        var hint = Ui.Secondary(text);
        hint.Margin = new Thickness(0, 10, 0, 0);
        return hint;
    }

    private async Task ConnectAsync()
    {
        _connecting = true;
        _connect.IsEnabled = false;
        _connect.Content = "Connecting…";
        var spark = _model.Spark;
        spark.Url = _url.Text.Trim();
        spark.Username = _user.Text.Trim();
        spark.Password = _password.Password;
        await spark.RefreshAsync();
        spark.StartMonitoring();
        _connecting = false;
        _connect.IsEnabled = true;
        _connect.Content = "Save & Connect";
        Refresh();
    }

    private void Refresh()
    {
        var spark = _model.Spark;
        _certificate.Children.Clear();
        if (spark.UntrustedFingerprint is { } fingerprint)
        {
            _certificate.Children.Add(Ui.Section("Certificate"));
            var print = Ui.Field(fingerprint, mono: true);
            print.IsReadOnly = true;
            print.Margin = new Thickness(0, 8, 0, 8);
            _certificate.Children.Add(Ui.Card(Ui.Stack(
                Ui.Secondary("The swapper uses a self-signed certificate. Trust it if this fingerprint matches the server's " +
                             "(sudo openssl x509 -in /etc/spark-swapper/tls.crt -noout -fingerprint -sha256):"),
                print,
                Ui.Button("Trust This Certificate", spark.TrustPresentedCertificate, accent: true))));
        }

        _status.Children.Clear();
        if (spark.LastError is { } error && spark.UntrustedFingerprint is null)
            _status.Children.Add(Ui.Card(Ui.Status(error, "SystemFillColorCriticalBrush")));
        if (spark.Status is { } status)
        {
            if (spark.ProgressLine is { } line) _status.Children.Add(Ui.Card(Ui.Status(line, "SystemFillColorCautionBrush")));
            foreach (var m in status.Ordered)
            {
                var serving = m.Key == status.Active;
                UIElement action;
                if (serving)
                {
                    action = Ui.Text("Serving", 12, FontWeights.SemiBold, "SystemFillColorSuccessBrush", wrap: false);
                }
                else
                {
                    var button = Ui.Button("Switch", () => _ = SwitchAsync(m.Key, m.Title));
                    button.IsEnabled = !status.IsSwitching;
                    action = button;
                }
                _status.Children.Add(Ui.Row(m.Title, $"{m.ServedId} · {m.Context:N0} ctx · {m.Engine ?? ""}".TrimEnd(' ', '·'),
                    action, serving ? Icons.Completed : Icons.CircleRing));
            }
            if (status.OpenclawPrimary is { } primary) _status.Children.Add(Ui.Secondary($"OpenClaw → {primary}"));
        }
        else if (spark.IsConfigured && !_connecting)
        {
            _status.Children.Add(Ui.Card(Ui.Secondary(spark.Refreshing ? "Connecting…" : "Not connected yet.")));
        }
        else if (!spark.IsConfigured)
        {
            _status.Children.Add(Ui.Card(Ui.Secondary("Not set up.")));
        }
    }

    private async Task SwitchAsync(string key, string title)
    {
        if (!Dialog.Confirm($"Switch the Spark to {title}?",
                "The current model stops and the new one loads (Flash takes about 11 minutes). Chats and OpenClaw follow automatically.",
                "Switch"))
            return;
        if (await _model.Spark.SwapAsync(key) is { } error) Dialog.Info("Couldn't switch the Spark", error);
    }
}
