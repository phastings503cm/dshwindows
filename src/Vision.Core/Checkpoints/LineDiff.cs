using System.Text;

namespace Vision.Core;

// MARK: - Line diffs
//
// The unified diff a checkpoint shows for each file ("Files changed (N)" → a file → its diff), and the +added −removed
// counts beside it. Myers' algorithm over lines after the common head and tail are set aside; a pair of files so different
// that the edit script would pass MaxEdits is shown as one replacement of the differing middle.

public static class LineDiff
{
    /// <summary>Past this many differing lines the middle is shown as one replacement (the diff stays fast for any file).</summary>
    public const int MaxEdits = 1_000;

    public enum Op { Same, Add, Remove }

    public readonly record struct Line(Op Op, string Text, int OldNumber, int NewNumber);

    /// <summary>Lines of <paramref name="text"/> (CRLF and CR treated as LF; a final newline doesn't make an extra empty line).</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }

    /// <summary>The edit script from <paramref name="before"/> to <paramref name="after"/>, every line once.</summary>
    public static IReadOnlyList<Line> Compute(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var output = new List<Line>(Math.Max(a.Length, b.Length));
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        for (var i = 0; i < head; i++) output.Add(new Line(Op.Same, a[i], i + 1, i + 1));
        var middle = Middle(a, head, a.Length - tail, b, head, b.Length - tail);
        output.AddRange(middle);
        for (var i = 0; i < tail; i++)
        {
            var oldIndex = a.Length - tail + i;
            var newIndex = b.Length - tail + i;
            output.Add(new Line(Op.Same, a[oldIndex], oldIndex + 1, newIndex + 1));
        }
        return output;
    }

    /// <summary>Lines added and removed.</summary>
    public static (int Added, int Removed) Count(string before, string after)
    {
        var script = Compute(before, after);
        return (script.Count(l => l.Op == Op.Add), script.Count(l => l.Op == Op.Remove));
    }

    /// <summary>A unified diff with <paramref name="context"/> lines around each change ("--- a/name", "+++ b/name", "@@ … @@").
    /// Empty when the texts are the same.</summary>
    public static string Unified(string before, string after, string oldName, string newName, int context = 3)
    {
        var script = Compute(before, after);
        if (script.All(l => l.Op == Op.Same)) return "";
        var text = new StringBuilder();
        text.Append("--- ").Append(oldName).Append('\n');
        text.Append("+++ ").Append(newName).Append('\n');
        var i = 0;
        while (i < script.Count)
        {
            // The next change, and the hunk around it: changes closer than 2×context lines share one hunk.
            while (i < script.Count && script[i].Op == Op.Same) i++;
            if (i >= script.Count) break;
            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < script.Count)
            {
                if (script[end].Op != Op.Same) lastChange = end;
                else if (end - lastChange > context * 2) break;
                end++;
            }
            end = Math.Min(script.Count, lastChange + context + 1);
            var hunk = script.Skip(start).Take(end - start).ToList();
            var oldStart = hunk.FirstOrDefault(l => l.Op != Op.Add).OldNumber;
            var newStart = hunk.FirstOrDefault(l => l.Op != Op.Remove).NewNumber;
            var oldCount = hunk.Count(l => l.Op != Op.Add);
            var newCount = hunk.Count(l => l.Op != Op.Remove);
            // An empty side is numbered by the line before it, as diff does.
            if (oldCount == 0) oldStart = PreviousOld(script, start);
            if (newCount == 0) newStart = PreviousNew(script, start);
            text.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            foreach (var line in hunk)
                text.Append(line.Op switch { Op.Add => '+', Op.Remove => '-', _ => ' ' }).Append(line.Text).Append('\n');
            i = end;
        }
        return text.ToString();
    }

    private static int PreviousOld(IReadOnlyList<Line> script, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (script[i].Op != Op.Add) return script[i].OldNumber;
        return 0;
    }

    private static int PreviousNew(IReadOnlyList<Line> script, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (script[i].Op != Op.Remove) return script[i].NewNumber;
        return 0;
    }

    /// <summary>Myers' diff of a[aStart..aEnd) against b[bStart..bEnd), line numbers 1-based in the whole files.</summary>
    private static List<Line> Middle(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var output = new List<Line>();
        if (n == 0 && m == 0) return output;
        if (n == 0 || m == 0 || Math.Abs(n - m) > MaxEdits)
            return Replacement(a, aStart, aEnd, b, bStart, bEnd);
        var max = Math.Min(n + m, MaxEdits);
        var offset = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }
        if (!found) return Replacement(a, aStart, aEnd, b, bStart, bEnd);
        // Walk the trace back from (n, m) to (0, 0), collecting moves in reverse.
        var moves = new List<Line>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            int prevK;
            if (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1])) prevK = k + 1;
            else prevK = k - 1;
            var prevX = vd[offset + prevK];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                moves.Add(new Line(Op.Same, a[aStart + cx - 1], aStart + cx, bStart + cy));
                cx--;
                cy--;
            }
            if (d > 0)
            {
                if (cx == prevX) moves.Add(new Line(Op.Add, b[bStart + cy - 1], 0, bStart + cy));
                else moves.Add(new Line(Op.Remove, a[aStart + cx - 1], aStart + cx, 0));
            }
            cx = prevX;
            cy = prevY;
        }
        moves.Reverse();
        return moves;
    }

    private static List<Line> Replacement(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd)
    {
        var output = new List<Line>();
        for (var i = aStart; i < aEnd; i++) output.Add(new Line(Op.Remove, a[i], i + 1, 0));
        for (var j = bStart; j < bEnd; j++) output.Add(new Line(Op.Add, b[j], 0, j + 1));
        return output;
    }
}
