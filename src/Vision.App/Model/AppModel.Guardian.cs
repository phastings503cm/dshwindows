using Vision.Core;

namespace Vision.App.Model;

public sealed partial class AppModel
{
    private GuardianService? _guardian;

    /// <summary>The Guardian connection: the sign-in, the routes through its proxy, the account prompt sync goes to, and the
    /// account's skills, chats, projects and opt-in secrets.</summary>
    public GuardianService Guardian => _guardian ??= new GuardianService(Config, Host, () => Project);

    /// <summary>At launch: Guardian's refusals become dialogs, and a saved sign-in learns its orgs, limits and models (a
    /// sign-in revoked in Guardian says so now, not at the first chat).</summary>
    private void StartGuardian()
    {
        Guardian.Refused += error => Views.Dialogs.GuardianRefusalWindow.Show(this, error);
        if (Guardian.IsSignedIn && SelfTest.Current is null) _ = Guardian.RefreshAsync();
    }

    /// <summary>Open Settings › Guardian.</summary>
    public void ShowGuardian() => ShowSettings(SettingsTab.Guardian);
}
