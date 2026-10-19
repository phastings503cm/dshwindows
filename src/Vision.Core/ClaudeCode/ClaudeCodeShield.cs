namespace Vision.Core;

// MARK: - The shield on what Vision hands Claude Code
//
// Claude Code reads files and runs commands itself, so the sensitive-data shield can't stand between it and Anthropic the
// way it does for Vision's own engine. What Vision itself hands over is shielded: the recap of the chat so far is masked
// like any request to a model that isn't private (it is history, so nothing has to come back — and earlier turns may have
// been answered by a private model, unmasked); the new message and the text of its attachments have their secrets masked
// (and the vault's values), the other kinds left as the person wrote them, because Claude Code would write a placeholder
// into a file as it is — nothing can put the real value back for it. The same rules as Vision for macOS.

public static class ClaudeCodeShield
{
    /// <summary>What a turn sends after the shield: the recap, the message and the attachments' parts, how many distinct values
    /// of each kind were masked, and how many pictures or PDFs go as they are (the shield can't look into them).</summary>
    public sealed record Masked(string? Recap, string Message, IReadOnlyList<AttachmentWirePart> Parts,
                                IReadOnlyDictionary<SensitiveKind, int> Counts, int Unchecked);

    /// <summary>Put after the recap's header when something in it was masked.</summary>
    public const string RecapNote =
        "[Vision's sensitive-data shield replaced some values in this recap with placeholders such as ⟦EMAIL_1⟧. The real values " +
        "aren't available here — ask the person if you need one.]";

    /// <summary>Said once per chat per launch when a chat with history moves onto Claude Code while the shield is on.</summary>
    public const string Notice =
        "The sensitive-data shield covers what Vision hands Claude Code — the chat so far, and secrets in your messages — but not " +
        "what Claude Code reads or runs itself: that goes to Anthropic as it is.";

    /// <summary>What the turn sends, masked with <paramref name="shield"/> (null = off: everything as it is). One set of
    /// placeholders across the recap, the message and the attachments' text, so a value is the same placeholder in each.</summary>
    public static Masked Mask(string? recap, string message, IReadOnlyList<AttachmentWirePart> parts, ShieldOptions? shield)
    {
        var notChecked = parts.Count(p => p is not AttachmentWirePart.Text);
        if (shield is null) return new Masked(recap, message, parts, new Dictionary<SensitiveKind, int>(), notChecked);
        var secrets = new SensitiveDetector(shield.Categories & SensitiveCategories.Secrets, null, shield.VaultSecrets);
        var map = new ShieldMap();
        // Secrets everywhere first (one numbering)…
        map.Collect(recap, secrets);
        map.Collect(message, secrets);
        foreach (var part in parts.OfType<AttachmentWirePart.Text>()) map.Collect(part.Value, secrets);
        var maskedMessage = map.Replace(message);
        var maskedParts = parts.Select(p => p is AttachmentWirePart.Text t ? new AttachmentWirePart.Text(map.Replace(t.Value)) : p).ToList();
        // …then everything else the shield masks, in the recap only.
        string? maskedRecap = null;
        if (recap is not null)
        {
            var secretsOut = map.Replace(recap);
            map.Collect(secretsOut, shield.Detector);
            var full = map.Replace(secretsOut);
            maskedRecap = full == recap ? recap
                : full.StartsWith(ClaudeCodeRecap.Header, StringComparison.Ordinal)
                    ? ClaudeCodeRecap.Header + "\n" + RecapNote + full[ClaudeCodeRecap.Header.Length..]
                    : RecapNote + "\n\n" + full;
        }
        return new Masked(maskedRecap, maskedMessage, maskedParts,
            map.Counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value), notChecked);
    }
}
