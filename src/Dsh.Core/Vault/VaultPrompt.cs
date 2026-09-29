namespace Dsh.Core;

// MARK: - System prompt

/// <summary>What the model is told about the vault: which credentials exist (by name — never a
/// value) and how to use one. The app appends <see cref="Section"/> to every chat's system prompt
/// and rebuilds it when <see cref="CredentialVault.Revision"/> moves.</summary>
public static class VaultPrompt
{
    /// <summary>The keyboard shortcut that opens the Credentials Vault window.</summary>
    public const string Shortcut = "Ctrl+Shift+K";
    /// <summary>At most this many credentials are listed; vault_search finds the rest.</summary>
    public const int MaxListed = 30;

    /// <summary>The "--- Credential vault ---" section. <paramref name="shell"/> is the chat's shell,
    /// so the example of setting an environment variable is one the agent can actually run.</summary>
    public static string Section(IEnumerable<VaultEntry> entries, ShellKind shell = ShellKind.PowerShell)
    {
        var all = entries.ToList();
        if (all.Count == 0)
        {
            return "--- Credential vault ---\n" +
                   "The user keeps API keys, tokens and passwords in a credential vault (empty right now). If a task needs one, " +
                   $"ask them to add it in the Credentials Vault ({Shortcut}) — never ask them to paste a secret into the chat.";
        }
        var usable = all.Where(e => e.Access != VaultAccess.Never).ToList();
        var listed = usable.Take(MaxListed).Select(e =>
        {
            var s = $"- {e.Placeholder} ({e.Kind.Label()}";
            if (e.Description.Length > 0) s += ": " + TextUtil.Prefix(e.Description, 80);
            if (e.Access == VaultAccess.Ask) s += "; asks the user first";
            return s + ")";
        }).ToList();
        var text = "--- Credential vault ---\n" +
                   "The user's credentials are in a vault. You never see their values. To use one, write its placeholder where " +
                   $"the value goes in any tool call — a shell command (`{ShellExample(shell, "OPENAI_API_KEY", "OPENAI_API_KEY")}; …`), a " +
                   "file you write (.env), a URL or header. The harness substitutes the real value when the tool runs and shows " +
                   "[vault:NAME] in results — that text holds the real value, so write {{vault:NAME}} to refer to it (in an edit, " +
                   "for instance). Use vault_search to look credentials up; never ask the user to paste a secret.";
        if (listed.Count == 0)
        {
            text += "\nNo credential is currently available to you.";
        }
        else
        {
            text += "\nAvailable:\n" + string.Join("\n", listed);
            if (usable.Count > MaxListed) text += $"\n… and {usable.Count - MaxListed} more (vault_search).";
        }
        return text;
    }

    /// <summary>Setting environment variable <paramref name="variable"/> to a credential in
    /// <paramref name="shell"/>'s syntax: <c>$env:API_KEY = '{{vault:NAME}}'</c> in PowerShell (quoted,
    /// or the value would run as a command), <c>set "API_KEY={{vault:NAME}}"</c> in cmd, and the Mac
    /// app's <c>export API_KEY={{vault:NAME}}</c> in bash.</summary>
    public static string ShellExample(ShellKind shell, string variable, string credential)
    {
        var placeholder = "{{vault:" + credential + "}}";
        return shell switch
        {
            ShellKind.PowerShell => $"$env:{variable} = '{placeholder}'",
            ShellKind.Cmd => $"set \"{variable}={placeholder}\"",
            _ => $"export {variable}={placeholder}",
        };
    }
}
