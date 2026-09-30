using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Placeholders

/// <summary><c>{{vault:NAME}}</c> in tool arguments: found, substituted, and scrubbed back.</summary>
public static partial class VaultPlaceholders
{
    [GeneratedRegex(@"\{\{\s*vault:([A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Tools that don't execute their arguments, so they never get real values: a subagent
    /// prompt, a queued task, a skill draft or a todo would carry the secret to a model or to disk.
    /// Their calls keep the placeholder as written (0.12.0 hardening).</summary>
    public static readonly IReadOnlySet<string> NonExecutingTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent", "queue_task", "propose_skill", "todo_write", "use_skill",
        "vault_search", "agent_status", "agent_stop", "exit_plan_mode",
        // Prose the harness shows, logs or stores: a goal verdict, a saved memory, a batch of subagent prompts.
        "goal_complete", "goal_blocked", "memory_save", "memory_update", "memory_search", "memory_forget", "delegate",
    };

    /// <summary>Whether a call to <paramref name="toolName"/> gets its placeholders replaced by values.</summary>
    public static bool SubstitutesInto(string toolName) => !NonExecutingTools.Contains(toolName);

    /// <summary>The credential names a tool call refers to, in order, without repeats (compared
    /// case-insensitively; the first spelling wins).</summary>
    public static IReadOnlyList<string> Names(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (Match match in Pattern().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name.ToLowerInvariant())) names.Add(name);
        }
        return names;
    }

    /// <summary>Replace each placeholder in a JSON-encoded argument string with its value, escaped
    /// so the JSON stays valid. Names are matched case-insensitively; a placeholder with no value in
    /// <paramref name="values"/> is left as written.</summary>
    public static string Substitute(string json, IReadOnlyDictionary<string, string> values) =>
        Pattern().Replace(json, match =>
        {
            var name = match.Groups[1].Value;
            return Find(values, name) is { } value ? JsonEscaped(value) : match.Value;
        });

    /// <summary>Replace every vault value in <paramref name="text"/> with <c>[vault:NAME]</c>. Pass
    /// the values longest first (as <see cref="CredentialVault.ValuesForRedaction"/> returns them).</summary>
    public static string Redact(string text, IEnumerable<(string Name, string Value)> values)
    {
        var output = text;
        foreach (var (name, value) in values)
        {
            if (value.Length > 0 && output.Contains(value, StringComparison.Ordinal))
                output = output.Replace(value, $"[vault:{name}]", StringComparison.Ordinal);
        }
        return output;
    }

    /// <summary>A string's contents as they appear inside a JSON string literal: quotes, backslashes
    /// and control characters escaped; everything else (slashes, non-ASCII) as is.</summary>
    internal static string JsonEscaped(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case < ' ': output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: output.Append(c); break;
            }
        }
        return output.ToString();
    }

    private static string? Find(IReadOnlyDictionary<string, string> values, string name)
    {
        if (values.TryGetValue(name, out var exact)) return exact;
        foreach (var (key, value) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }
}

// MARK: - Grants

/// <summary>Which credentials this chat has been allowed to use (for "ask first"). One per chat;
/// subagents share their parent's. Thread-safe.</summary>
public sealed class VaultGrants
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal);

    public bool Has(string name)
    {
        lock (_lock) return _granted.Contains(name.ToLowerInvariant());
    }

    public void Grant(string name)
    {
        lock (_lock) _granted.Add(name.ToLowerInvariant());
    }
}
