using System.Text;

namespace Dsh.Core;

/// <summary>Turns text into the terms the memory index matches on: lower-cased, split on anything that is
/// not a letter or digit (and on camelCase humps), stop words dropped, and a light stem so "deploying",
/// "deployed" and "deploys" meet. Deliberately small and dependency-free — a note store is not a search
/// engine, it just has to find the right three notes quickly.</summary>
internal static class MemoryText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "above", "after", "again", "all", "also", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can", "could",
        "did", "do", "does", "doing", "done", "down", "during", "each", "few", "for", "from", "further", "get",
        "got", "had", "has", "have", "having", "he", "her", "here", "hers", "him", "his", "how", "i", "if",
        "in", "into", "is", "it", "its", "just", "let", "like", "make", "me", "more", "most", "my", "no",
        "nor", "not", "now", "of", "off", "on", "once", "one", "only", "or", "other", "our", "ours", "out",
        "over", "own", "please", "same", "she", "should", "so", "some", "such", "than", "that", "the",
        "their", "theirs", "them", "then", "there", "these", "they", "this", "those", "through", "to", "too",
        "under", "until", "up", "us", "use", "used", "using", "very", "want", "was", "we", "were", "what",
        "when", "where", "which", "while", "who", "whom", "why", "will", "with", "would", "you", "your",
        "yours", "yes", "ok", "okay", "thanks", "thank", "hi", "hello", "hey", "need", "needs", "then",
        // (Inflections are listed as themselves: a stem is not tested against this list, because "notes" and "themes" stem to
        // "not" and "them", and the words "note" and "theme" must stay searchable.)
        "uses", "gets", "getting", "wants", "wanted", "wanting", "needed", "needing", "makes", "making", "made",
        "lets", "letting", "others", "ones",
    };

    /// <summary>The terms of <paramref name="text"/>, in order, repeats included (frequency matters to the index).</summary>
    public static List<string> Terms(string text)
    {
        var output = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inside && start < 0) start = i;
            else if (!inside && start >= 0)
            {
                AddRun(text.AsSpan(start, i - start), output);
                start = -1;
            }
        }
        return output;
    }

    /// <summary>Han, kana and hangul are written without spaces, so a "word" of them is a whole clause.</summary>
    private static bool IsCjk(char c) =>
        c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF';

    /// <summary>Distinct terms, at most <paramref name="max"/> (the longest, when there are more) — a query made from
    /// a pasted stack trace must not turn into a hundred lookups. The index narrows a long query further by how
    /// rare each term is, which it alone knows.</summary>
    public static List<string> QueryTerms(string text, int max = 64)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var term in Terms(text))
        {
            if (seen.Add(term)) unique.Add(term);
        }
        if (unique.Count <= max) return unique;
        return unique.OrderByDescending(t => t.Length).Take(max).ToList();
    }

    private static void AddRun(ReadOnlySpan<char> run, List<string> output)
    {
        // Runs with CJK in them: each stretch of CJK becomes overlapping pairs of characters (a query for
        // 部署到测试服务器 shares 测试 and 服务 with a note that says "测试服务器"); the rest is ordinary words.
        var hasCjk = false;
        foreach (var c in run)
        {
            if (IsCjk(c))
            {
                hasCjk = true;
                break;
            }
        }
        if (hasCjk)
        {
            var i = 0;
            while (i < run.Length)
            {
                var j = i;
                var cjk = IsCjk(run[i]);
                while (j < run.Length && IsCjk(run[j]) == cjk) j++;
                var piece = run[i..j];
                if (!cjk) AddRun(piece, output);
                else if (piece.Length == 1) output.Add(piece.ToString());
                else
                {
                    for (var k = 0; k + 1 < piece.Length; k++) output.Add(piece.Slice(k, 2).ToString());
                }
                i = j;
            }
            return;
        }
        // Split on camelCase humps: "readFile" → "read", "File"; "OPENAI" stays whole.
        var parts = new List<string>();
        var partStart = 0;
        for (var i = 1; i < run.Length; i++)
        {
            if (char.IsLower(run[i - 1]) && char.IsUpper(run[i]))
            {
                parts.Add(run[partStart..i].ToString());
                partStart = i;
            }
        }
        parts.Add(run[partStart..].ToString());
        foreach (var part in parts) AddTerm(part, output);
        // Keep the compound too, so a query for "readfile" still finds "readFile".
        if (parts.Count > 1) AddTerm(run.ToString(), output);
    }

    private static void AddTerm(string word, List<string> output)
    {
        var lower = word.ToLowerInvariant();
        if (lower.Length < 2 || StopWords.Contains(lower)) return;
        // A bare number is noise unless it is part of something (v2, net10).
        if (lower.All(char.IsDigit) && lower.Length < 3) return;
        var stem = Stem(lower);
        if (stem.Length >= 2) output.Add(stem);
    }

    /// <summary>Words that end in "s" (or "us") without being plurals.</summary>
    private static readonly HashSet<string> SingularWithS = new(StringComparer.Ordinal)
    {
        "this", "thus", "plus", "minus", "bonus", "focus", "virus", "nexus", "lotus", "cactus", "status", "campus", "corpus",
        "census", "radius", "genius", "apparatus", "axis", "basis", "oasis", "crisis", "thesis", "analysis", "emphasis",
        "diagnosis", "synopsis", "hypothesis", "always", "perhaps", "series", "species", "canvas", "atlas", "alias", "bias",
        "gas", "lens", "news", "physics", "mathematics", "access", "process", "address", "class", "pass", "mass", "less",
    };

    /// <summary>Words that end in "ed" without being past tenses, so the ending must stay (embed, but not embedded → "embedd").</summary>
    private static readonly HashSet<string> EndsInEd = new(StringComparer.Ordinal)
    {
        "embed", "speed", "proceed", "succeed", "exceed", "shred", "bleed", "breed", "indeed", "hundred", "sacred", "kindred",
        "wicked", "naked", "hatred", "infrared", "unsaid",
    };

    /// <summary>Strip the common English endings so that forms of a word meet: create/creates/created/creating all
    /// become "creat", string/strings "str", query/queries "queri", copy/copied "copi", size/sizes "siz". The stem is
    /// only ever compared with other stems, so it need not be a word — it must just be the same for every form.</summary>
    public static string Stem(string word)
    {
        var w = word;
        // A short word ending in a consonant + y turns to i like the longer ones (try/tries/tried).
        if (w.Length <= 3) return w.Length == 3 && w[2] == 'y' && !"aeiou".Contains(w[1]) ? w[..2] + "i" : w;
        // Plurals and -ied first, so that "settings" and "setting" go on to lose -ing together.
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "i";
        else if (w.EndsWith("ied", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "i";
        else if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !SingularWithS.Contains(w)
                 && !(w.Length > 5 && w.EndsWith("us", StringComparison.Ordinal)))
            w = w[..^1];
        // Verb endings.
        var strippedEd = false;
        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 5) w = UndoubleConsonant(w[..^3]);
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 4 && !EndsInEd.Contains(w))
        {
            w = UndoubleConsonant(w[..^2]);
            strippedEd = true;
        }
        // A final e, and a final y after a consonant, so query/queries and cookie/cookies meet. (A final e that is only there
        // because -ed was taken off "agreed" is kept, as "agree" would lose its own: agre = agre.)
        if (w.EndsWith('e') && w.Length > 3) w = strippedEd ? w : w[..^1];
        else if (w.EndsWith('y') && w.Length >= 3 && !"aeiou".Contains(w[^2])) w = w[..^1] + "i";
        return w;
    }

    /// <summary>"running" → "run", "stopped" → "stop", "committed" → "commit" — but "adding" stays "add" and "stuffed" "stuff",
    /// so only the letters that double in an inflection (and not the ones that double in the word itself: add, call, kiss).</summary>
    private static string UndoubleConsonant(string w) =>
        w.Length > 3 && w[^1] == w[^2] && "bdgmnprt".Contains(w[^1]) ? w[..^1]
        // The l that doubles in an inflection (controlled, cancelled, labelled, modelled, travelled), where call/kill/install keep theirs.
        : w is "controll" or "cancell" or "labell" or "modell" or "travell" or "channell" or "signall" or "fuell" or "levell" ? w[..^1]
        : w;

    /// <summary>Jaccard similarity of the distinct terms of two texts, 0–1.</summary>
    public static double Similarity(string a, string b)
    {
        var setA = new HashSet<string>(Terms(a), StringComparer.Ordinal);
        var setB = new HashSet<string>(Terms(b), StringComparer.Ordinal);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var shared = setA.Count(setB.Contains);
        return shared / (double)(setA.Count + setB.Count - shared);
    }

    /// <summary>The first <paramref name="max"/> characters, never ending in half of a surrogate pair (an emoji cut in two
    /// cannot be written out as JSON).</summary>
    public static string Head(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..max];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    private static readonly System.Text.RegularExpressions.Regex Number =
        new(@"\d+(?:[.,]\d+)*", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "cannot", "without", "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent", "wont", "cant",
        "shouldnt", "mustnt", "couldnt", "wouldnt", "havent", "hasnt", "hadnt", "neither", "nor", "avoid",
    };

    /// <summary>Whether two texts differ in what the word matching cannot see, so that they say different things however many
    /// words they share: numbers that conflict ("Node 3" / "Node 4", Python 3.9 / 3.12 — neither text's numbers are a part of
    /// the other's; one that only adds a number is an elaboration), or a claim turned around by a "not" ("Commit directly to
    /// main" / "Do not commit directly to main" — same words, opposite sense).</summary>
    public static bool Contradicts(string a, string b)
    {
        try
        {
            var numbersA = Number.Matches(a).Select(m => m.Value.TrimEnd('.', ',')).ToHashSet(StringComparer.Ordinal);
            var numbersB = Number.Matches(b).Select(m => m.Value.TrimEnd('.', ',')).ToHashSet(StringComparer.Ordinal);
            if (numbersA.Count > 0 && numbersB.Count > 0 && !numbersA.IsSubsetOf(numbersB) && !numbersB.IsSubsetOf(numbersA)) return true;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
        // (The words that carry the negation are not counted: "don't commit" and "commit" have the same substance, opposite sense.)
        return Negated(a) != Negated(b) && TermSet(a).Except(NegationTerms).ToHashSet(StringComparer.Ordinal).SetEquals(TermSet(b).Except(NegationTerms));
    }

    /// <summary>One text's terms say everything the other's do (and perhaps more): a rewrite that kept the substance.</summary>
    public static bool Covers(string a, string b)
    {
        var setA = TermSet(a);
        var setB = TermSet(b);
        return setA.Count > 0 && setB.Count > 0 && (setA.IsSubsetOf(setB) || setB.IsSubsetOf(setA));
    }

    private static HashSet<string> TermSet(string text) => new(Terms(text), StringComparer.Ordinal);

    /// <summary>The terms a negation leaves behind after splitting and stemming: "don't" → "don", "isn't" → "isn", "never" → "never".</summary>
    private static readonly HashSet<string> NegationTerms = new(
        Negations.Select(Stem).Concat(["don", "doesn", "didn", "isn", "aren", "wasn", "weren", "won", "shouldn", "mustn", "couldn", "wouldn", "haven", "hasn", "hadn"]),
        StringComparer.Ordinal);

    private static bool Negated(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && (char.IsLetter(text[i]) || (text[i] is '\'' or '’' && start >= 0));
            if (inside && start < 0) start = i;
            else if (!inside && start >= 0)
            {
                var word = text.Substring(start, i - start).ToLowerInvariant().Replace("'", "").Replace("’", "");
                if (Negations.Contains(word)) return true;
                start = -1;
            }
        }
        return false;
    }

    /// <summary>Cut <paramref name="text"/> to about <paramref name="max"/> characters at a sentence or word boundary.</summary>
    public static string Clip(string text, int max)
    {
        var flat = text.Trim().Replace("\r\n", "\n").Replace('\n', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        if (flat.Length <= max) return flat;
        var cut = flat[..max];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1]; // never leave half of a surrogate pair
        var sentence = cut.LastIndexOfAny(['.', '!', '?']);
        if (sentence > max * 0.6) return cut[..(sentence + 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max * 0.6 ? cut[..space] : cut).TrimEnd(',', ';', ':', ' ') + "…";
    }
}
