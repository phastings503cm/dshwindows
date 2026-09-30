namespace Dsh.Core;

/// <summary>An inverted index over memory notes, scored with BM25. Title terms count three times and tag
/// terms twice, so a note is found by what it is about before what it happens to mention. Not thread-safe:
/// <see cref="MemoryStore"/> holds its lock around every call.</summary>
internal sealed class MemoryIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    /// <summary>A prefix match ("deploy" for "deployment") counts for less than the word itself.</summary>
    private const double PrefixWeight = 0.55;

    private sealed class Doc(string id, int length, List<string> terms)
    {
        public string Id { get; } = id;
        public int Length { get; } = length;
        public List<string> Terms { get; } = terms;
    }

    private readonly Dictionary<string, Doc> _docs = new(StringComparer.Ordinal);
    /// <summary>term → (note id → weighted frequency)</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _vocabulary = new(StringComparer.Ordinal);
    private long _totalLength;

    public int Count => _docs.Count;

    public void Add(MemoryItem item)
    {
        Remove(item.Id);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        void Count(string text, int weight)
        {
            foreach (var term in MemoryText.Terms(text))
            {
                counts[term] = counts.GetValueOrDefault(term) + weight;
                length += weight;
            }
        }
        Count(item.Title, 3);
        Count(string.Join(" ", item.Tags), 2);
        Count(item.Body, 1);
        foreach (var (term, frequency) in counts)
        {
            if (!_postings.TryGetValue(term, out var posting))
            {
                posting = new Dictionary<string, int>(StringComparer.Ordinal);
                _postings[term] = posting;
                _vocabulary.Add(term);
            }
            posting[item.Id] = frequency;
        }
        _docs[item.Id] = new Doc(item.Id, Math.Max(1, length), counts.Keys.ToList());
        _totalLength += Math.Max(1, length);
    }

    public void Remove(string id)
    {
        if (!_docs.Remove(id, out var doc)) return;
        _totalLength -= doc.Length;
        foreach (var term in doc.Terms)
        {
            if (!_postings.TryGetValue(term, out var posting)) continue;
            posting.Remove(id);
            if (posting.Count > 0) continue;
            _postings.Remove(term);
            _vocabulary.Remove(term);
        }
    }

    public void Clear()
    {
        _docs.Clear();
        _postings.Clear();
        _vocabulary.Clear();
        _totalLength = 0;
    }

    /// <summary>A long query is narrowed to this many terms, the rarest first.</summary>
    private const int MaxQueryTerms = 24;

    /// <summary>Score notes against <paramref name="queryTerms"/> (already stemmed). <paramref name="weightOf"/>
    /// scales a note's score, or returns 0 to leave it out (another project's note). Best first.
    /// <paramref name="applyFloor"/> false lists whatever matches at all, however common the words are.</summary>
    public List<(string Id, double Score, double Relevance)> Search(IReadOnlyList<string> queryTerms, int limit,
                                                                   Func<string, double> weightOf, double minRelevance,
                                                                   bool applyFloor = true)
    {
        var results = new List<(string Id, double Score, double Relevance)>();
        if (_docs.Count == 0 || queryTerms.Count == 0) return results;

        var n = _docs.Count;
        var averageLength = Math.Max(1.0, _totalLength / (double)n);
        double Idf(int documentFrequency) => Math.Log(1 + (n - documentFrequency + 0.5) / (documentFrequency + 0.5));

        // A pasted log has dozens of terms, most of them noise: keep the rare ones, which say what it is about.
        // (Terms no note contains are the first to go.)
        if (queryTerms.Count > MaxQueryTerms)
        {
            queryTerms = queryTerms
                .Select(term => (Term: term, Rarity: Expand(term).Select(e => Idf(_postings[e.Term].Count) * e.Weight).DefaultIfEmpty(0).Max()))
                .OrderByDescending(t => t.Rarity).ThenBy(t => t.Term, StringComparer.Ordinal)
                .Take(MaxQueryTerms).Select(t => t.Term).ToList();
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var denominator = 0.0;
        foreach (var term in queryTerms)
        {
            var bestIdf = 0.0;
            foreach (var (candidate, weight) in Expand(term))
            {
                var posting = _postings[candidate];
                var idf = Idf(posting.Count);
                bestIdf = Math.Max(bestIdf, idf * weight);
                foreach (var (id, frequency) in posting)
                {
                    var doc = _docs[id];
                    var norm = frequency + K1 * (1 - B + B * doc.Length / averageLength);
                    var part = weight * idf * frequency * (K1 + 1) / norm;
                    scores[id] = scores.GetValueOrDefault(id) + part;
                }
            }
            // A word no note contains still counts against relevance (a little): a note that covers
            // one word of a long message is a weaker match than one that covers most of it.
            denominator += bestIdf > 0 ? bestIdf * (K1 + 1) : Idf(0) * (K1 + 1) * 0.25;
        }
        if (scores.Count == 0 || denominator <= 0) return results;

        // Big stores drown in common words, so a match must hinge on something reasonably distinctive.
        // A handful of notes has no "common" words: any overlap counts.
        var floor = !applyFloor ? 0 : n < 12 ? 0.05 : 0.35 * Math.Log(1 + n);
        foreach (var (id, score) in scores)
        {
            var weight = weightOf(id);
            if (weight <= 0) continue;
            var weighted = score * weight;
            var relevance = Math.Min(1.0, weighted / denominator);
            if (weighted < floor || relevance < minRelevance) continue;
            results.Add((id, weighted, relevance));
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    /// <summary>The index terms a query term stands for: itself, longer words it begins ("deploy" →
    /// "deployment"), and shorter words it begins with ("deployment" → "deploy").</summary>
    private IEnumerable<(string Term, double Weight)> Expand(string term)
    {
        if (_postings.ContainsKey(term)) yield return (term, 1.0);
        if (term.Length < 4) yield break;
        var taken = 0;
        foreach (var candidate in _vocabulary.GetViewBetween(term, term + "\uffff"))
        {
            if (candidate.Length == term.Length) continue;
            yield return (candidate, PrefixWeight);
            if (++taken >= 6) break;
        }
        for (var length = term.Length - 1; length >= 4; length--)
        {
            var head = term[..length];
            if (_postings.ContainsKey(head)) yield return (head, PrefixWeight);
        }
    }
}
