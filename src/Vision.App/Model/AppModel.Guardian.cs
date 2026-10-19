using Vision.Core;

namespace Vision.App.Model;

public sealed partial class AppModel
{
    private GuardianService? _guardian;

    /// <summary>The Guardian connection: the sign-in, the routes through its proxy, the account prompt sync goes to, and the
    /// account's skills, chats, projects and opt-in secrets.</summary>
    public GuardianService Guardian => _guardian ??= new GuardianService(Config, Host, () => Project);

    /// <summary>At launch: Guardian's refusals become dialogs, a saved sign-in learns its orgs, limits and models (a sign-in
    /// revoked in Guardian says so now, not at the first chat), and the models are kept fresh for the chats' model menus.</summary>
    private void StartGuardian()
    {
        Guardian.Refused += error => Views.Dialogs.GuardianRefusalWindow.Show(this, error);
        if (SelfTest.Current is not null) return;
        if (Guardian.IsSignedIn) _ = Guardian.RefreshAsync();
        // Signed in, the account's models are in every chat's model menu: asked for again every 15 minutes.
        Guardian.StartModelRefresh();
        // A chat on a Guardian model shows it as waiting for the sign-in, or as usable again.
        Guardian.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GuardianService.Client) or nameof(GuardianService.Models)) Host.NotifyRoutesChanged();
        };
    }

    /// <summary>Open Settings › Guardian.</summary>
    public void ShowGuardian() => ShowSettings(SettingsTab.Guardian);
}
