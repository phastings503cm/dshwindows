using Vision.Core;

namespace Vision.App.Model;

public sealed partial class AppModel
{
    private GuardianService? _guardian;

    /// <summary>The Guardian connection: the sign-in, the routes through its proxy, prompt sync and the account's skills,
    /// chats, projects and opt-in secrets.</summary>
    public GuardianService Guardian => _guardian ??= new GuardianService(Config, Host, () => Project);

    /// <summary>Signed in to Guardian: a prompt that started a turn goes to the account's notebook (slash commands other than
    /// /goal don't — they aren't prompts).</summary>
    private void SyncPrompt(SessionVM session, string text, bool wasRunning)
    {
        if (wasRunning || !session.Running || !Guardian.IsSignedIn) return;
        if (SlashCommand.Parse(text) is { } command && command is not SlashCommand.Goal) return;
        Guardian.PromptSubmitted(session, text);
    }

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
