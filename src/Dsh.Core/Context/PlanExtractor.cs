using System.Text.RegularExpressions;

namespace Dsh.Core;

/// <summary>Reads the bullet-point plan a model writes at the start of a job ("Here's my plan: 1. … 2. …")
/// so the plan panel can show it even when the model didn't use <c>todo_write</c>. Deliberately strict —
/// a plan needs a lead-in that says so and at least three steps — because a list of options or findings
/// is not a plan, and showing one as such would mislead.</summary>
public static partial class PlanExtractor
{
    public const int MinSteps = 3;
    public const int MaxSteps = 25;

    [GeneratedRegex(@"^\W*(?:(?:here(?:'|’)?s|here is|this is|my|the|proposed|updated|revised|initial|short|quick|step[- ]by[- ]step)\s+)*(?:plan|approach|steps|game plan|roadmap|strategy|to-?do(?: list)?|checklist|outline)\b[^\n]{0,80}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadIn();

    /// <summary>"Next steps": a plan only as a checklist. As plain bullets it is what the agent suggests you do once it is done.</summary>
    [GeneratedRegex(@"^\W*(?:(?:my|the|proposed|updated|revised)\s+)*next steps\b[^\n]{0,80}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NextSteps();

    [GeneratedRegex(@"^\s*(?:[-*•▪◦]|\d{1,2}[.)])\s+\[[ xX]\]\s", RegexOptions.CultureInvariant)]
    private static partial Regex CheckboxItem();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:[-*•▪◦]|\d{1,2}[.)]|\[[ xX]\]|\((?:\d{1,2}|[a-z])\))\s+(?<text>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Item();

    /// <summary>The steps of a plan in <paramref name="text"/>, or an empty list when it doesn't contain one.</summary>
    public static IReadOnlyList<string> FromAssistantText(string text)
    {
        var lines = GoalProtocol.StripThinking(text).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var head = lines[i].Trim().TrimStart('#', ' ').TrimEnd(':', ' ', '*', '_');
            if (head.Length == 0) continue;
            var next = NextSteps().IsMatch(head);
            if (!next && !LeadIn().IsMatch(head)) continue;
            var steps = Collect(lines, i + 1);
            if (steps.Count < MinSteps) continue;
            // "Next steps" is a plan only as a checklist; as plain bullets it is what the agent suggests you do afterwards.
            if (next && !CheckboxItem().IsMatch(lines.Skip(i + 1).First(l => l.Trim().Length > 0))) continue;
            return steps;
        }
        return [];
    }

    /// <summary>The steps of a plan written into <c>exit_plan_mode</c>: its list items, or — if it is prose —
    /// nothing (the panel then shows the text as it is).</summary>
    public static IReadOnlyList<string> FromPlanText(string plan)
    {
        var steps = Collect(plan.Replace("\r\n", "\n").Split('\n'), 0, skipLeadingProse: true);
        return steps.Count >= 2 ? steps : [];
    }

    /// <summary>Consecutive top-level list items starting at <paramref name="start"/> (blank lines and
    /// indented continuations allowed between them).</summary>
    private static List<string> Collect(string[] lines, int start, bool skipLeadingProse = false)
    {
        var steps = new List<string>();
        var blanks = 0;
        int? level = null;
        for (var i = start; i < lines.Length && steps.Count < MaxSteps; i++)
        {
            var raw = lines[i].TrimEnd();
            if (raw.Trim().Length == 0)
            {
                if (++blanks > 1 && steps.Count > 0) break;
                continue;
            }
            var match = Item().Match(raw);
            if (!match.Success)
            {
                // A wrapped line of the previous step keeps going; anything else ends the list.
                if (steps.Count > 0 && char.IsWhiteSpace(raw[0])) { steps[^1] += " " + raw.Trim(); continue; }
                if (steps.Count == 0 && (skipLeadingProse || (blanks == 0 && raw.Trim().Length < 3))) continue;
                break;
            }
            blanks = 0;
            var indent = match.Groups["indent"].Value.Replace("\t", "    ").Length;
            level ??= indent;
            if (indent > level + 1) continue; // a sub-step of the current step: part of it, not a step of its own
            if (indent < level) break;
            steps.Add(Clean(match.Groups["text"].Value));
        }
        return steps.Where(s => s.Length > 0).ToList();
    }

    /// <summary>Drop markdown emphasis and a trailing colon so a step reads as a label.</summary>
    internal static string Clean(string step)
    {
        var text = step.Trim();
        // "[ ] task" / "[x] task" from a markdown checklist.
        if (text.Length > 3 && text[0] == '[' && text[2] == ']' && text[1] is ' ' or 'x' or 'X') text = text[3..].TrimStart();
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        return TextUtil.Prefix(text.TrimEnd(':', ' '), 240);
    }
}
