using System.Text;

namespace Dsh.Core;

// MARK: - The vault_search tool

/// <summary>Lets the model look credentials up: placeholders, descriptions and fingerprints — never
/// values. The app adds it to the registry next to the vault.</summary>
public sealed class VaultSearchTool(CredentialVault vault) : IToolExecutor
{
    public const string ToolName = "vault_search";
    /// <summary>At most this many hits are listed.</summary>
    public const int MaxListed = 50;

    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Search the user's credential vault (API keys, tokens, passwords) by name, service, tag or description. Returns names, descriptions and fingerprints — never values. To use a credential, write {{vault:NAME}} where the value goes in any tool call (a shell command, a file you write, a URL or header); the harness substitutes the real value when the tool runs and hides it from results. Never ask the user to paste a secret that is in the vault.",
        """{"type":"object","properties":{"query":{"type":"string","description":"Words to match, e.g. 'openai' or 'stripe test'. Empty lists everything."}}}""");

    public CredentialVault Vault { get; } = vault;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>(Execute(arguments, context.Shell.Kind));

    private string Execute(string arguments, ShellKind shell)
    {
        var query = (JsonArgs.String(arguments, "query") ?? "").Trim();
        var hits = Vault.Search(query);
        if (hits.Count == 0)
        {
            var total = Vault.All.Count;
            return total == 0
                ? $"The vault is empty. Ask the user to add the credential in the Credentials Vault ({VaultPrompt.Shortcut}) — never ask them to paste it into chat."
                : $"No credential matches “{query}” ({total} in the vault). Try a broader word, or an empty query to list them all.";
        }
        var lines = new List<string> { $"{hits.Count} credential{(hits.Count == 1 ? "" : "s")}:" };
        foreach (var e in hits.Take(MaxListed))
        {
            var line = new StringBuilder($"- {e.Placeholder} — {e.Kind.Label()}");
            if (e.Description.Length > 0) line.Append(": ").Append(e.Description);
            var details = new List<string>();
            if (e.Username is { } user) details.Add($"user {user}");
            if (e.Url is { } url) details.Add(url);
            if (e.Tags.Count > 0) details.Add("tags " + string.Join(", ", e.Tags));
            details.Add(e.Fingerprint);
            switch (e.Access)
            {
                case VaultAccess.Ask: details.Add("asks the user before first use"); break;
                case VaultAccess.Never: details.Add("NOT available to the agent"); break;
            }
            line.Append(" (").Append(string.Join("; ", details)).Append(')');
            lines.Add(line.ToString());
        }
        if (hits.Count > MaxListed) lines.Add($"… {hits.Count - MaxListed} more; narrow the query.");
        lines.Add($"Use one by writing its placeholder, e.g. {VaultPrompt.ShellExample(shell, "API_KEY", hits[0].Name)} in a shell command.");
        return string.Join("\n", lines);
    }
}
