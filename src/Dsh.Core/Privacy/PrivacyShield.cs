using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dsh.Core;

// MARK: - The sensitive-data shield
//
// A model that isn't private (an online service) never sees the sensitive values in a conversation: each one is
// replaced by a placeholder — ⟦EMAIL_1⟧, ⟦SECRET_2⟧ — on the way out, and the placeholders in what comes back are
// replaced by the real values before anything leaves the wrapper. The engine, the UI and storage only ever see the
// real values; placeholders exist on the wire alone.
//
// Numbering follows first appearance in transcript order, so in an append-only transcript a value keeps its
// placeholder from turn to turn — no mapping is stored anywhere, and the server's prompt cache stays warm.
//
// Each request is masked in two passes. A value is often recognisable only by its context — `password: X`, `Bearer X`,
// `user:X@host` — and once the model has used its placeholder and the app has put X back, X turns up bare in later
// messages (in a reply, a command, a tool's output). So the first pass collects every sensitive value anywhere in the
// request, and the second replaces every occurrence of each, wherever it is.

/// <summary>What the shield masks, and where it reports. One per set of settings (a class, not a record: a copy
/// made with <c>with</c> would carry the old settings' detector along).</summary>
public sealed class ShieldOptions
{
    /// <summary>The master switch ("Mask sensitive data before sending it to models that aren't private").</summary>
    public bool Enabled { get; init; } = true;
    public SensitiveCategories Categories { get; init; } = SensitiveCategories.Default;
    /// <summary>Custom terms, one per entry ("re:" for a regular expression).</summary>
    public IReadOnlyList<string> CustomTerms { get; init; } = [];
    /// <summary>The Credentials Vault's values: always masked.</summary>
    public IReadOnlyList<string> VaultSecrets { get; init; } = [];
    /// <summary>Told after each request what was masked (counts only, never values).</summary>
    public Action<ShieldReport>? Report { get; init; }

    private SensitiveDetector? _detector;

    /// <summary>The detector for these settings (built once).</summary>
    public SensitiveDetector Detector => _detector ??= new SensitiveDetector(Categories, CustomTerms, VaultSecrets);
}

/// <summary>What one request had masked — how many distinct values of each kind (never the values) — and how many
/// pictures and files that aren't plain text it carried, which can't be checked and go as they are.</summary>
public sealed record ShieldReport(string Route, IReadOnlyDictionary<SensitiveKind, int> Counts, int Unchecked = 0)
{
    /// <summary>Said once in a chat the first time something that can't be checked goes to a model that isn't private.</summary>
    public const string UncheckedNotice = "Pictures and files that aren't plain text are sent as they are — DSH can't check them for sensitive details.";

    public int Total => Counts.Values.Sum();

    /// <summary>"Masked 2 emails and 1 key before sending to Kimi Code" — and, when something couldn't be checked,
    /// "… · 1 attachment not checked". Null when nothing was masked.</summary>
    public string? Summary => Describe(Counts, Route) is not { } masked ? null
        : Unchecked > 0 ? $"{masked} · {Unchecked} {(Unchecked == 1 ? "attachment" : "attachments")} not checked"
        : masked;

    /// <summary>The categories and counts, one per line, for the chip's tooltip.</summary>
    public string Details
    {
        get
        {
            if (Total == 0) return "";
            var text = $"Replaced with placeholders before sending to {Route}:\n" +
                       string.Join("\n", Ordered(Counts).Select(c => $"• {c.Value} {c.Key.Noun(c.Value)}"));
            if (Unchecked > 0)
                text += $"\nNot checked, sent as {(Unchecked == 1 ? "it is" : "they are")}: {Unchecked} " +
                        $"{(Unchecked == 1 ? "picture or file" : "pictures or files")} that {(Unchecked == 1 ? "isn't" : "aren't")} plain text.";
            return text;
        }
    }

    /// <summary>Most first, then in the order the settings list the categories.</summary>
    private static IEnumerable<KeyValuePair<SensitiveKind, int>> Ordered(IReadOnlyDictionary<SensitiveKind, int> counts) =>
        counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value).ThenBy(c => c.Key);

    public static string? Describe(IReadOnlyDictionary<SensitiveKind, int> counts, string route)
    {
        var parts = Ordered(counts).Select(c => $"{c.Value} {c.Key.Noun(c.Value)}").ToList();
        if (parts.Count == 0) return null;
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"Masked {list} before sending to {route}";
    }
}

/// <summary>The placeholders of one request. Pass 1 (<see cref="Collect"/>) finds the sensitive values in each text the
/// model would see and numbers each new one, per kind, in order of first detection; pass 2 (<see cref="Replace"/>)
/// replaces every collected value in a text — where a detector found it, and wherever else it appears.</summary>
public sealed class ShieldMap
{
    /// <summary>A value shorter than this is replaced only where a detector found it: anywhere else it could be part of an
    /// ordinary word.</summary>
    public const int MinLiteralLength = 4;

    private readonly Dictionary<string, string> _placeholderOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<SensitiveKind, int> _counts = new();
    /// <summary>Each text pass 1 looked at, and what the detectors found in it.</summary>
    private readonly Dictionary<string, IReadOnlyList<SensitiveMatch>> _found = new(StringComparer.Ordinal);
    private LiteralMatcher? _matcher;
    private int _matcherCount = -1;

    /// <summary>How many distinct values of each kind were collected (and so masked).</summary>
    public IReadOnlyDictionary<SensitiveKind, int> Counts => _counts;

    public bool IsEmpty => _values.Count == 0;

    /// <summary>The placeholder for <paramref name="value"/> (numbering a new one if it is the first of its kind). A value
    /// keeps the placeholder it got first, whatever kind it is found as later.</summary>
    public string PlaceholderFor(SensitiveKind kind, string value)
    {
        if (_placeholderOf.TryGetValue(value, out var existing)) return existing;
        var number = _counts.GetValueOrDefault(kind) + 1;
        _counts[kind] = number;
        var placeholder = SensitiveKinds.Placeholder(kind, number);
        _placeholderOf[value] = placeholder;
        _values[placeholder] = value;
        return placeholder;
    }

    /// <summary>The real value behind a placeholder; null for one this request didn't make.</summary>
    public string? ValueOf(string placeholder) => _values.GetValueOrDefault(placeholder);

    /// <summary>Pass 1: the sensitive values in <paramref name="text"/>, numbered if new.</summary>
    public void Collect(string? text, SensitiveDetector detector)
    {
        if (string.IsNullOrEmpty(text) || _found.ContainsKey(text)) return;
        var matches = detector.Find(text);
        _found[text] = matches;
        foreach (var match in matches) PlaceholderFor(match.Kind, text.Substring(match.Start, match.Length));
    }

    /// <summary>Pass 1 for a value seen where it can't be replaced in place (the raw text of a tool call's arguments,
    /// whose strings are replaced one by one).</summary>
    public void CollectValue(SensitiveKind kind, string value)
    {
        if (value.Length > 0) PlaceholderFor(kind, value);
    }

    /// <summary>Pass 2: <paramref name="text"/> with every collected value replaced — where a detector found it in this
    /// text, and wherever else it appears (values of <see cref="MinLiteralLength"/> characters or more, with no need for
    /// word boundaries: <c>-phunter22x</c> is caught) — longest first, never inside a placeholder already there.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public string? Replace(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsEmpty) return text;
        var candidates = new List<SensitiveMatch>();
        if (_found.TryGetValue(text, out var detected)) candidates.AddRange(detected);
        foreach (var (start, length) in Matcher().Find(text)) candidates.Add(new SensitiveMatch(start, length, SensitiveKind.Secret));
        if (candidates.Count == 0) return text;
        var kept = SensitiveDetector.Resolve(candidates, SensitiveDetector.Placeholders(text));
        var output = new StringBuilder(text.Length);
        var at = 0;
        foreach (var match in kept)
        {
            if (!_placeholderOf.TryGetValue(text.Substring(match.Start, match.Length), out var placeholder)) continue;
            output.Append(text, at, match.Start - at).Append(placeholder);
            at = match.End;
        }
        output.Append(text, at, text.Length - at);
        return output.ToString();
    }

    /// <summary>Both passes over one text (the "Try it" box, a single message).</summary>
    public string Mask(string text, SensitiveDetector detector)
    {
        Collect(text, detector);
        return Replace(text);
    }

    /// <summary>Whether <paramref name="text"/> holds any collected value (a short one as a word of its own).</summary>
    public bool Holds(string text)
    {
        if (IsEmpty || string.IsNullOrEmpty(text)) return false;
        if (Matcher().Find(text).Count > 0) return true;
        foreach (var value in _placeholderOf.Keys.Where(v => v.Length < MinLiteralLength))
        {
            for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
            {
                var end = at + value.Length;
                if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            }
        }
        return false;
    }

    /// <summary>One matcher for every collected value long enough to be looked for anywhere (built once per request,
    /// after pass 1).</summary>
    private LiteralMatcher Matcher()
    {
        if (_matcher is null || _matcherCount != _placeholderOf.Count)
        {
            _matcher = new LiteralMatcher(_placeholderOf.Keys.Where(v => v.Length >= MinLiteralLength));
            _matcherCount = _placeholderOf.Count;
        }
        return _matcher;
    }

    /// <summary><paramref name="text"/> with every placeholder this request made replaced by its value. Unknown
    /// placeholders are left as they are.</summary>
    public string Restore(string text)
    {
        if (IsEmpty || string.IsNullOrEmpty(text) || !text.Contains('⟦')) return text;
        try
        {
            return SensitiveKinds.PlaceholderPattern().Replace(text, m => ValueOf(m.Value) ?? m.Value);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return text;
        }
    }
}

/// <summary>Finds every occurrence of a set of strings in one pass over a text, however many strings there are
/// (Aho–Corasick): masking a spreadsheet of 8,000 email addresses costs one walk over it, not 8,000.</summary>
public sealed class LiteralMatcher
{
    private readonly List<string> _patterns;
    /// <summary>(state, character) → the next state.</summary>
    private readonly Dictionary<long, int> _goto = new();
    private readonly List<int> _fail = [0];
    /// <summary>The pattern that ends at a state, or -1.</summary>
    private readonly List<int> _ends = [-1];
    /// <summary>The nearest state down the failure chain where a pattern ends, or -1.</summary>
    private readonly List<int> _nextEnd = [-1];

    public LiteralMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var parent = new List<int> { 0 };
        var via = new List<char> { '\0' };
        var depth = new List<int> { 0 };
        for (var p = 0; p < _patterns.Count; p++)
        {
            var state = 0;
            foreach (var c in _patterns[p])
            {
                if (!_goto.TryGetValue(Key(state, c), out var next))
                {
                    next = _fail.Count;
                    _goto[Key(state, c)] = next;
                    _fail.Add(0);
                    _ends.Add(-1);
                    _nextEnd.Add(-1);
                    parent.Add(state);
                    via.Add(c);
                    depth.Add(depth[state] + 1);
                }
                state = next;
            }
            _ends[state] = p;
        }
        // Failure links, shallowest states first (a state's failure is always shallower than it).
        var order = Enumerable.Range(1, _fail.Count - 1).OrderBy(state => depth[state]).ToList();
        foreach (var state in order)
        {
            var from = parent[state];
            var c = via[state];
            var fail = 0;
            if (from != 0)
            {
                var f = _fail[from];
                while (true)
                {
                    if (_goto.TryGetValue(Key(f, c), out var target))
                    {
                        fail = target;
                        break;
                    }
                    if (f == 0) break;
                    f = _fail[f];
                }
            }
            _fail[state] = fail;
            _nextEnd[state] = _ends[fail] >= 0 ? fail : _nextEnd[fail];
        }
    }

    private static long Key(int state, char c) => ((long)state << 16) | c;

    /// <summary>Every occurrence of every pattern in <paramref name="text"/> (overlapping ones included).</summary>
    public IReadOnlyList<(int Start, int Length)> Find(string text)
    {
        var found = new List<(int Start, int Length)>();
        if (_patterns.Count == 0 || string.IsNullOrEmpty(text)) return found;
        var state = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            while (true)
            {
                if (_goto.TryGetValue(Key(state, c), out var next))
                {
                    state = next;
                    break;
                }
                if (state == 0) break;
                state = _fail[state];
            }
            for (var end = _ends[state] >= 0 ? state : _nextEnd[state]; end >= 0; end = _nextEnd[end])
            {
                var length = _patterns[_ends[end]].Length;
                found.Add((i - length + 1, length));
            }
        }
        return found;
    }
}

/// <summary>Restores placeholders in streamed text. A placeholder can arrive split over several chunks, so a trailing
/// "⟦…" that could still become one (32 characters at most) is held back until it completes or clearly isn't one.</summary>
public sealed class PlaceholderRestorer(ShieldMap map)
{
    private const int MaxHeld = 32;
    private readonly StringBuilder _held = new();

    /// <summary>The text that can be passed on now.</summary>
    public string Push(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return "";
        _held.Append(delta);
        var text = _held.ToString();
        var open = text.LastIndexOf('⟦');
        if (open >= 0 && text.IndexOf('⟧', open) < 0 && text.Length - open <= MaxHeld && CouldBecomePlaceholder(text.AsSpan(open)))
        {
            _held.Clear().Append(text, open, text.Length - open);
            return map.Restore(text[..open]);
        }
        _held.Clear();
        return map.Restore(text);
    }

    /// <summary>Whatever is still held (the stream ended).</summary>
    public string Flush()
    {
        var text = _held.ToString();
        _held.Clear();
        return map.Restore(text);
    }

    /// <summary>"⟦", "⟦EMA", "⟦EMAIL_", "⟦EMAIL_12" — a start of a placeholder.</summary>
    private static bool CouldBecomePlaceholder(ReadOnlySpan<char> tail)
    {
        var i = 1;
        while (i < tail.Length && tail[i] is >= 'A' and <= 'Z') i++;
        if (i - 1 > 6) return false;
        if (i == tail.Length) return true;
        if (tail[i] != '_' || i == 1) return false;
        i++;
        var digits = 0;
        while (i < tail.Length && char.IsAsciiDigit(tail[i]))
        {
            i++;
            digits++;
        }
        return i == tail.Length && digits <= 6;
    }
}

/// <summary>Wraps the client of a route that isn't private: masks the request, adds a note to the system prompt,
/// restores what comes back, and reports what it masked.</summary>
public sealed class ShieldedClient(IProviderClient inner, ShieldOptions options) : IProviderClient
{
    /// <summary>Appended to the first system message of every masked request.</summary>
    public const string PromptNote =
        "Privacy: some values in this conversation were replaced with placeholders such as ⟦EMAIL_1⟧ or ⟦SECRET_1⟧ before " +
        "they reached you. Use a placeholder exactly as written wherever you need that value — in replies, code or tool calls — " +
        "and the app puts the real value back on the user's computer. Don't ask for the real values and don't guess them.";

    public IProviderClient Inner => inner;
    public ShieldOptions Options => options;
    public ProviderProfile Profile => inner.Profile;

    public Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default) =>
        inner.ModelInfoAsync(model, cancellationToken);

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        inner.ListModelsAsync(cancellationToken);

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var map = new ShieldMap();
        var (masked, notChecked) = Mask(request, map, options.Detector);
        var text = new PlaceholderRestorer(map);
        var reasoning = new PlaceholderRestorer(map);
        await foreach (var ev in inner.StreamAsync(masked, cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                case LlmStreamEvent.Text t:
                    if (text.Push(t.Delta) is { Length: > 0 } restoredText) yield return new LlmStreamEvent.Text(restoredText);
                    break;
                case LlmStreamEvent.Reasoning r:
                    if (reasoning.Push(r.Delta) is { Length: > 0 } restoredReasoning) yield return new LlmStreamEvent.Reasoning(restoredReasoning);
                    break;
                case LlmStreamEvent.Done done:
                    if (reasoning.Flush() is { Length: > 0 } lastReasoning) yield return new LlmStreamEvent.Reasoning(lastReasoning);
                    if (text.Flush() is { Length: > 0 } lastText) yield return new LlmStreamEvent.Text(lastText);
                    options.Report?.Invoke(new ShieldReport(RouteName(inner.Profile), new Dictionary<SensitiveKind, int>(map.Counts), notChecked));
                    // The provider's opaque state goes back exactly as it came: it was made from what the model saw.
                    yield return done with { Calls = done.Calls.Select(c => c with { Arguments = RestoreArguments(c.Arguments, map) }).ToList() };
                    break;
                default:
                    yield return ev;
                    break;
            }
        }
    }

    /// <summary>"Kimi Code" — how the chip names the route.</summary>
    public static string RouteName(ProviderProfile profile) => profile.Name.Length > 0 ? profile.Name : profile.Model;

    /// <summary>The request as the model may see it. Pass 1 goes through everything the model would see, in transcript
    /// order — the system prompt, then each message's text, its tool calls (their raw JSON, so a key is seen beside its
    /// value, and each string in it) and the text of its text attachments — collecting the sensitive values; pass 2
    /// replaces every occurrence of each. Also counts the attachments that couldn't be checked (pictures, PDFs, Office
    /// documents, other binary files), which go as they are.</summary>
    public static (LlmRequest Request, int Unchecked) Mask(LlmRequest request, ShieldMap map, SensitiveDetector detector)
    {
        // Pass 1: collect.
        var attachmentText = new Dictionary<MessageAttachment, string?>(ReferenceEqualityComparer.Instance);
        var notChecked = 0;
        map.Collect(request.SystemPrompt, detector);
        foreach (var message in request.Messages)
        {
            map.Collect(message.Content, detector);
            foreach (var call in message.ToolCalls ?? [])
            {
                if (JsonStrings.Values(call.Arguments) is { } strings)
                {
                    CollectFromJson(call.Arguments, map, detector);
                    foreach (var value in strings) map.Collect(value, detector);
                }
                else
                {
                    map.Collect(call.Arguments, detector); // not JSON (cut off): plain text
                }
            }
            foreach (var attachment in message.Attachments ?? [])
            {
                if (attachmentText.ContainsKey(attachment)) continue;
                var text = AttachmentText.Read(attachment);
                attachmentText[attachment] = text;
                if (text is null) notChecked++;
                else map.Collect(text, detector);
            }
        }

        // Pass 2: replace.
        var system = map.Replace(request.SystemPrompt);
        var messages = new List<LlmMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            var copy = message with { Content = map.Replace(message.Content) };
            if (message.ToolCalls is { Count: > 0 } calls)
                copy = copy with { ToolCalls = calls.Select(c => c with { Arguments = ReplaceArguments(c.Arguments, map) }).ToList() };
            if (message.Attachments is { Count: > 0 } attachments)
                copy = copy with { Attachments = attachments.Select(a => ReplaceAttachment(a, attachmentText[a], map)).ToList() };
            // Bedrock sends a turn's own blocks back verbatim (Claude's signed reasoning): blocks made while nothing was
            // masked (the route was private then, say) would carry a sensitive value straight past the shield. They are
            // left out — privacy over continuity; if the model then won't take the turn without them, that error is shown
            // rather than the value sent.
            if (message.ProviderState is { Length: > 0 } state && StateHolds(state, map))
                copy = copy with { ProviderState = null };
            messages.Add(copy);
        }

        // The note goes on the first system message, once.
        if (system.Length > 0 || !messages.Any(m => m.Role == MessageRole.System))
        {
            if (!system.Contains(PromptNote, StringComparison.Ordinal)) system = system.Length == 0 ? PromptNote : system + "\n\n" + PromptNote;
        }
        else
        {
            var first = messages.FindIndex(m => m.Role == MessageRole.System);
            var content = messages[first].Content ?? "";
            if (!content.Contains(PromptNote, StringComparison.Ordinal))
                messages[first] = messages[first] with { Content = content.Length == 0 ? PromptNote : content + "\n\n" + PromptNote };
        }
        return (request with { SystemPrompt = system, Messages = messages }, notChecked);
    }

    /// <summary>The values the detectors find in a tool call's raw JSON — where a key stands beside its value
    /// (<c>{"password": "…"}</c>) — collected for pass 2, which replaces them inside the JSON's strings.</summary>
    private static void CollectFromJson(string json, ShieldMap map, SensitiveDetector detector)
    {
        foreach (var match in detector.Find(json))
            map.CollectValue(match.Kind, JsonStrings.Unescape(json.Substring(match.Start, match.Length)));
    }

    /// <summary>A tool call's arguments with the collected values replaced inside the JSON's strings — the JSON is parsed
    /// and its strings rewritten, never the text replaced. Arguments that aren't JSON (a call cut off mid-way) are
    /// treated as plain text.</summary>
    private static string ReplaceArguments(string arguments, ShieldMap map) =>
        JsonStrings.Rewrite(arguments, value => map.Replace(value)) ?? map.Replace(arguments);

    /// <summary>A text attachment with the collected values replaced (as UTF-8 then, so the placeholders survive whatever
    /// it was written in); one that isn't text, or had nothing to replace, goes as it is.</summary>
    private static MessageAttachment ReplaceAttachment(MessageAttachment attachment, string? text, ShieldMap map)
    {
        if (text is null) return attachment;
        var replaced = map.Replace(text);
        return replaced == text ? attachment : attachment with { Data = Encoding.UTF8.GetBytes(replaced) };
    }

    /// <summary>Whether a provider's stored state holds any collected value — in its JSON strings (decoded), or anywhere
    /// in it when it isn't JSON.</summary>
    private static bool StateHolds(string state, ShieldMap map)
    {
        if (map.IsEmpty) return false;
        return JsonStrings.Values(state) is { } strings ? strings.Any(map.Holds) : map.Holds(state);
    }

    /// <summary>A tool call's arguments with placeholders inside JSON strings replaced by the real values, escaped for
    /// JSON (quotes, backslashes, control characters).</summary>
    public static string RestoreArguments(string arguments, ShieldMap map)
    {
        if (map.IsEmpty || !arguments.Contains('⟦') && !arguments.Contains("\\u27e6", StringComparison.OrdinalIgnoreCase)) return arguments;
        return JsonStrings.Rewrite(arguments, map.Restore)
               // Not JSON: put the values back escaped, so they can only have been inside strings.
               ?? SensitiveKinds.PlaceholderPattern().Replace(arguments, m => map.ValueOf(m.Value) is { } value ? VaultPlaceholders.JsonEscaped(value) : m.Value);
    }
}

/// <summary>The text of an attached file, when it is text the shield can check: UTF-16 with a byte-order mark, else
/// UTF-8, else Windows-1252. Null for pictures, and for files that aren't plain text (PDF, Office documents, archives,
/// other binaries) — those go as they are.</summary>
public static class AttachmentText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding Windows1252 = CodePage1252();

    /// <summary>Files that are never plain text, whatever their bytes look like.</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".zip", ".gz", ".tgz", ".7z", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff", ".heic", ".exe", ".dll", ".bin", ".so", ".dylib",
        ".class", ".jar", ".pyc", ".mp3", ".mp4", ".mov", ".wav", ".sqlite", ".db",
    };

    public static string? Read(MessageAttachment attachment)
    {
        if (attachment.Kind == AttachmentKind.Image) return null;
        var data = attachment.Data;
        if (data.Length == 0) return "";
        try
        {
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return StrictUtf16.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return StrictUtf16BigEndian.GetString(data, 2, data.Length - 2);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        if (BinaryExtensions.Contains(Path.GetExtension(attachment.Name)) || HasBinarySignature(data)) return null;
        if (data.AsSpan().Contains((byte)0)) return null;
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: a Windows program's text (Notepad's "ANSI", an Excel CSV).
            return Windows1252.GetString(data);
        }
    }

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] GzipSignature = [0x1F, 0x8B];

    /// <summary>The first bytes of a PDF, a zip (and so docx, xlsx, pptx), an old Office file, a picture or an archive.</summary>
    private static bool HasBinarySignature(byte[] data)
    {
        ReadOnlySpan<byte> head = data.AsSpan(0, Math.Min(data.Length, 8));
        return head.StartsWith("%PDF"u8) || head.StartsWith("PK\u0003\u0004"u8) || head.StartsWith(OleSignature)
               || head.StartsWith(PngSignature) || head.StartsWith(JpegSignature) || head.StartsWith("GIF8"u8)
               || head.StartsWith(GzipSignature) || head.StartsWith("7z"u8) || head.StartsWith("Rar!"u8);
    }

    private static Encoding CodePage1252()
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }
}

/// <summary>Rewrites the string literals of a JSON document in place: everything else — the layout, the numbers, the
/// strings left alone — stays byte for byte as it was.</summary>
public static class JsonStrings
{
    /// <summary>Every string in a JSON document (property names included), decoded; null when it isn't valid JSON.</summary>
    public static IReadOnlyList<string>? Values(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var values = new List<string>();
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) values.Add(reader.GetString() ?? "");
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return values;
    }

    /// <summary>A piece of a JSON string's text (escapes and all) as the text it stands for; the piece itself when it
    /// isn't one.</summary>
    public static string Unescape(string piece)
    {
        if (!piece.Contains('\\')) return piece;
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + piece + "\"") ?? piece;
        }
        catch (JsonException)
        {
            return piece;
        }
    }

    /// <summary><paramref name="json"/> with each string literal's value passed through <paramref name="rewrite"/>
    /// (decoded first, re-escaped after, only when it changed). Null when <paramref name="json"/> isn't valid JSON.</summary>
    public static string? Rewrite(string json, Func<string, string> rewrite)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        var output = new StringBuilder(json.Length);
        var i = 0;
        var changed = false;
        while (i < json.Length)
        {
            var c = json[i];
            if (c != '"')
            {
                output.Append(c);
                i++;
                continue;
            }
            var start = i;
            var value = new StringBuilder();
            i++;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] != '\\')
                {
                    value.Append(json[i]);
                    i++;
                    continue;
                }
                var escape = json[i + 1];
                i += 2;
                switch (escape)
                {
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'u':
                        value.Append((char)Convert.ToInt32(json.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: value.Append(escape); break; // \" \\ \/
                }
            }
            i++; // the closing quote
            var original = value.ToString();
            var replaced = rewrite(original);
            if (replaced == original)
            {
                output.Append(json, start, i - start);
            }
            else
            {
                changed = true;
                output.Append('"').Append(VaultPlaceholders.JsonEscaped(replaced)).Append('"');
            }
        }
        return changed ? output.ToString() : json;
    }
}
