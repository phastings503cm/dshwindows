using System.Globalization;
using System.Text;

namespace Dsh.Core;

// MARK: - SKILL.md / .mdc / command documents
//
// Every tool that shares skills (Claude Code, Cursor, Agent Skills, this app) writes the same shape:
// optional "---" YAML frontmatter, then Markdown. The frontmatter in the wild is not just
// "key: value" — descriptions are folded block scalars ("description: >"), Cursor's globs is a list
// or a comma string, Claude's allowed-tools is either. This reads all of those, keeps key order,
// and writes them back out.

public sealed class SkillDocument : IEquatable<SkillDocument>
{
    /// <summary>Scalar fields (description, alwaysApply, …). Booleans stay "true"/"false".</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>List fields (globs, allowed-tools, …).</summary>
    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);
    /// <summary>Original key order, for stable rewrites.</summary>
    public List<string> Order { get; } = [];
    public string Body { get; set; } = "";
    /// <summary>Whether the text opened with a frontmatter block.</summary>
    public bool HadFrontmatter { get; set; }

    public SkillDocument() { }

    public SkillDocument(string body, bool hadFrontmatter = false)
    {
        Body = body;
        HadFrontmatter = hadFrontmatter;
    }

    // MARK: Accessors

    /// <summary>A scalar field; setting null removes the key (scalar or list).</summary>
    public string? this[string key]
    {
        get => Fields.GetValueOrDefault(key);
        set
        {
            if (value is not null)
            {
                if (!Fields.ContainsKey(key) && !Lists.ContainsKey(key)) Order.Add(key);
                Lists.Remove(key);
                Fields[key] = value;
            }
            else
            {
                Fields.Remove(key);
                Lists.Remove(key);
                Order.Remove(key);
            }
        }
    }

    public void SetList(string key, IEnumerable<string> values)
    {
        if (!Fields.ContainsKey(key) && !Lists.ContainsKey(key)) Order.Add(key);
        Fields.Remove(key);
        Lists[key] = values.ToList();
    }

    public bool? Bool(string key) => Fields.GetValueOrDefault(key)?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    /// <summary>A list, whether written as a YAML list, [a, b], or a, b.</summary>
    public IReadOnlyList<string> List(string key)
    {
        if (Lists.TryGetValue(key, out var list)) return list;
        return Fields.TryGetValue(key, out var s) && s.Length > 0 ? SplitList(s) : [];
    }

    internal static List<string> SplitList(string s)
    {
        var t = s.Trim();
        if (t.StartsWith('[') && t.EndsWith(']')) t = t[1..^1];
        // Split on commas that are not inside quotes or braces (globs like "**/*.{ts,tsx}").
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var depth = 0;
        foreach (var ch in t)
        {
            if (quote is { } q)
            {
                if (ch == q) quote = null;
                current.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' or '\'':
                    quote = ch;
                    current.Append(ch);
                    break;
                case '{':
                    depth++;
                    current.Append(ch);
                    break;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    current.Append(ch);
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }
        parts.Add(current.ToString());
        return parts.Select(p => Unquote(p.Trim())).Where(p => p.Length > 0).ToList();
    }

    // MARK: Parsing

    public static SkillDocument Parse(string raw)
    {
        var text = raw;
        if (text.StartsWith('﻿')) text = text[1..];
        text = text.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return new SkillDocument(text);
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t is "---" or "...")
            {
                end = i;
                break;
            }
        }
        if (end < 0) return new SkillDocument(text); // unterminated: not frontmatter

        var doc = new SkillDocument { HadFrontmatter = true };
        var block = lines[1..end];
        var index = 0;
        while (index < block.Length)
        {
            var line = block[index];
            index++;
            if (line.Length == 0 || line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith('#')) continue;
            var colon = KeyColon(line);
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();
            if (key.Length == 0) continue;

            // Following lines that belong to this key.
            var cont = new List<string>();
            while (index < block.Length)
            {
                var next = block[index];
                var isIndented = next.StartsWith(' ') || next.StartsWith('\t');
                var isTopList = next.StartsWith("- ", StringComparison.Ordinal) || next == "-";
                if (next.Length == 0 || isIndented || (rest.Length == 0 && isTopList))
                {
                    cont.Add(next);
                    index++;
                }
                else
                {
                    break;
                }
            }

            if (rest.StartsWith('|') || rest.StartsWith('>'))
            {
                doc.Assign(key, BlockScalar(cont, folded: rest.StartsWith('>')));
                continue;
            }
            var nonBlank = cont.Where(l => l.Trim().Length > 0).ToList();
            if (rest.Length == 0)
            {
                var items = nonBlank.Select(l => l.Trim()).ToList();
                if (items.Count > 0 && items.All(i => i.StartsWith("- ", StringComparison.Ordinal) || i == "-"))
                {
                    doc.AssignList(key, items.Select(i => Unquote(i[1..].Trim())).Where(i => i.Length > 0).ToList());
                }
                else if (items.Count == 0)
                {
                    doc.Assign(key, "");
                }
                // A nested mapping (metadata:, hooks:) is not something we use.
                continue;
            }
            if (rest.StartsWith('['))
            {
                var joined = rest;
                foreach (var l in nonBlank) joined += " " + l.Trim();
                doc.AssignList(key, SplitList(joined));
                continue;
            }
            if (rest.StartsWith('"') || rest.StartsWith('\''))
            {
                var joined = rest;
                // A quoted scalar that wraps onto indented lines.
                if (!ClosesQuote(rest, rest[0]))
                {
                    foreach (var l in nonBlank) joined += " " + l.Trim();
                }
                doc.Assign(key, Unquote(joined));
                continue;
            }
            // Plain scalar, possibly continued on indented lines.
            rest = StripComment(rest);
            foreach (var l in nonBlank) rest += " " + StripComment(l.Trim());
            doc.Assign(key, rest.Trim());
        }

        var body = lines[(end + 1)..].ToList();
        if (body.Count > 0 && body[0].Length == 0) body.RemoveAt(0);
        doc.Body = string.Join("\n", body);
        return doc;
    }

    private void Assign(string key, string scalar)
    {
        if (!Fields.ContainsKey(key) && !Lists.ContainsKey(key)) Order.Add(key);
        Lists.Remove(key);
        Fields[key] = scalar;
    }

    private void AssignList(string key, List<string> list)
    {
        if (!Fields.ContainsKey(key) && !Lists.ContainsKey(key)) Order.Add(key);
        Fields.Remove(key);
        Lists[key] = list;
    }

    /// <summary>The colon ending a top-level key (not prose containing a colon).</summary>
    private static int KeyColon(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return -1;
        var key = line[..colon].Trim();
        if (key.Contains(' ') && !key.StartsWith('"')) return -1; // prose, not a key
        return colon;
    }

    private static bool ClosesQuote(string s, char q)
    {
        var chars = s[1..].TrimEnd(' ');
        if (chars.Length == 0 || chars[^1] != q) return false;
        // An escaped final quote inside a double-quoted string doesn't close it.
        if (q == '"')
        {
            var backslashes = 0;
            for (var i = chars.Length - 2; i >= 0 && chars[i] == '\\'; i--) backslashes++;
            return backslashes % 2 == 0;
        }
        return true;
    }

    private static string BlockScalar(List<string> lines, bool folded)
    {
        var firstContent = lines.FirstOrDefault(l => l.Trim().Length > 0);
        var indent = firstContent is null ? 0 : firstContent.TakeWhile(c => c is ' ' or '\t').Count();
        var dedented = lines.Select(l => l.Trim().Length == 0 ? "" : l[Math.Min(indent, l.Length)..]).ToList();
        string output;
        if (folded)
        {
            var sb = new StringBuilder();
            var prevBlank = true;
            foreach (var l in dedented)
            {
                if (l.Length == 0)
                {
                    sb.Append('\n');
                    prevBlank = true;
                    continue;
                }
                if (!prevBlank) sb.Append(' ');
                sb.Append(l);
                prevBlank = false;
            }
            output = sb.ToString();
        }
        else
        {
            output = string.Join("\n", dedented);
        }
        return output.Trim();
    }

    private static string StripComment(string s)
    {
        var at = s.IndexOf(" #", StringComparison.Ordinal);
        return at < 0 ? s : s[..at].Trim();
    }

    internal static string Unquote(string s)
    {
        var t = s.Trim();
        if (t.Length < 2 || t[0] != t[^1] || t[0] is not ('"' or '\'')) return t;
        var q = t[0];
        var inner = t[1..^1];
        if (q == '\'') return inner.Replace("''", "'");
        var output = new StringBuilder();
        var escaped = false;
        foreach (var c in inner)
        {
            if (escaped)
            {
                output.Append(c switch { 'n' => '\n', 't' => '\t', _ => c });
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    // MARK: Rendering

    public string Render()
    {
        var output = new StringBuilder();
        if (Order.Count > 0)
        {
            output.Append("---\n");
            foreach (var key in Order)
            {
                if (Fields.TryGetValue(key, out var s))
                {
                    output.Append(key).Append(": ").Append(Scalar(s)).Append('\n');
                }
                else if (Lists.TryGetValue(key, out var list))
                {
                    if (list.Count == 0)
                    {
                        output.Append(key).Append(": []\n");
                    }
                    else
                    {
                        output.Append(key).Append(":\n");
                        foreach (var item in list) output.Append("  - ").Append(Scalar(item)).Append('\n');
                    }
                }
            }
            output.Append("---\n\n");
        }
        output.Append(Body);
        if (output.Length == 0 || output[^1] != '\n') output.Append('\n');
        return output.ToString();
    }

    private static readonly HashSet<char> SpecialStart =
        ['-', '?', ':', ',', '[', ']', '{', '}', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`'];

    /// <summary>A YAML scalar: bare when unambiguous, otherwise double-quoted with escapes.</summary>
    public static string Scalar(string s)
    {
        if (s is "true" or "false") return s;
        if (s.Length == 0) return "\"\"";
        var bare = !s.Contains('\n') && !s.StartsWith(' ') && !s.EndsWith(' ')
                   && !s.Contains(": ", StringComparison.Ordinal) && !s.Contains(" #", StringComparison.Ordinal)
                   && !s.EndsWith(':') && !SpecialStart.Contains(s[0])
                   && !(s.ToLowerInvariant() is "null" or "yes" or "no" or "on" or "off" or "~")
                   && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (bare) return s;
        var output = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': output.Append("\\\\"); break;
                case '"': output.Append("\\\""); break;
                case '\n': output.Append("\\n"); break;
                default: output.Append(c); break;
            }
        }
        return output.Append('"').ToString();
    }

    // MARK: Equality (value semantics, like the Swift struct)

    public bool Equals(SkillDocument? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Body == other.Body && HadFrontmatter == other.HadFrontmatter
               && Order.SequenceEqual(other.Order)
               && Fields.Count == other.Fields.Count && Fields.All(kv => other.Fields.TryGetValue(kv.Key, out var v) && v == kv.Value)
               && Lists.Count == other.Lists.Count && Lists.All(kv => other.Lists.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));
    }

    public override bool Equals(object? obj) => Equals(obj as SkillDocument);
    public override int GetHashCode() => HashCode.Combine(Body, HadFrontmatter, Order.Count);
}
