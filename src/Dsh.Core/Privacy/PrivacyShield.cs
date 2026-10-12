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

/// <summary>What one request had masked: how many distinct values of each kind (never the values), and whether it
/// carried pictures, which can't be checked.</summary>
public sealed record ShieldReport(string Route, IReadOnlyDictionary<SensitiveKind, int> Counts, bool HadImages)
{
    public int Total => Counts.Values.Sum();

    /// <summary>"Masked 2 emails and 1 key before sending to Kimi Code" — null when nothing was masked.</summary>
    public string? Summary => Describe(Counts, Route);

    /// <summary>The categories and counts, one per line, for the chip's tooltip.</summary>
    public string Details => Total == 0
        ? ""
        : $"Replaced with placeholders before sending to {Route}:\n" +
          string.Join("\n", Ordered(Counts).Select(c => $"• {c.Value} {c.Key.Noun(c.Value)}"));

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

/// <summary>The placeholders of one request: each distinct value, numbered per kind by first appearance.</summary>
public sealed class ShieldMap
{
    private readonly Dictionary<(SensitiveKind Kind, string Value), string> _placeholders = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<SensitiveKind, int> _counts = new();

    /// <summary>How many distinct values of each kind were masked.</summary>
    public IReadOnlyDictionary<SensitiveKind, int> Counts => _counts;

    public bool IsEmpty => _values.Count == 0;

    /// <summary>The placeholder for <paramref name="value"/> (numbering a new one if it is the first of its kind).</summary>
    public string PlaceholderFor(SensitiveKind kind, string value)
    {
        if (_placeholders.TryGetValue((kind, value), out var existing)) return existing;
        var number = _counts.GetValueOrDefault(kind) + 1;
        _counts[kind] = number;
        var placeholder = SensitiveKinds.Placeholder(kind, number);
        _placeholders[(kind, value)] = placeholder;
        _values[placeholder] = value;
        return placeholder;
    }

    /// <summary>The real value behind a placeholder; null for one this request didn't make.</summary>
    public string? ValueOf(string placeholder) => _values.GetValueOrDefault(placeholder);

    /// <summary><paramref name="text"/> with every sensitive value replaced.</summary>
    public string Mask(string text, SensitiveDetector detector)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var matches = detector.Find(text);
        if (matches.Count == 0) return text;
        var output = new StringBuilder(text.Length);
        var at = 0;
        foreach (var match in matches)
        {
            output.Append(text, at, match.Start - at);
            output.Append(PlaceholderFor(match.Kind, text.Substring(match.Start, match.Length)));
            at = match.End;
        }
        output.Append(text, at, text.Length - at);
        return output.ToString();
    }

    /// <summary><paramref name="text"/> with every placeholder this request made replaced by its value. Unknown
    /// placeholders are left as they are.</summary>
    public string Restore(string text)
    {
        if (IsEmpty || string.IsNullOrEmpty(text) || !text.Contains('⟦')) return text;
        return SensitiveKinds.PlaceholderPattern().Replace(text, m => ValueOf(m.Value) ?? m.Value);
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
        var (masked, hadImages) = Mask(request, map, options.Detector);
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
                    options.Report?.Invoke(new ShieldReport(RouteName(inner.Profile), new Dictionary<SensitiveKind, int>(map.Counts), hadImages));
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

    /// <summary>The request as the model may see it, in transcript order: the system prompt, then each message — its
    /// text, the string values in the arguments of its tool calls, and the text of its text-file attachments. Also says
    /// whether it carries pictures (which can't be checked and go as they are).</summary>
    public static (LlmRequest Request, bool HadImages) Mask(LlmRequest request, ShieldMap map, SensitiveDetector detector)
    {
        var hadImages = false;
        var system = map.Mask(request.SystemPrompt, detector);
        var messages = new List<LlmMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            var copy = message with { Content = message.Content is null ? null : map.Mask(message.Content, detector) };
            if (message.ToolCalls is { Count: > 0 } calls)
                copy = copy with { ToolCalls = calls.Select(c => c with { Arguments = MaskArguments(c.Arguments, map, detector) }).ToList() };
            if (message.Attachments is { Count: > 0 } attachments)
            {
                if (attachments.Any(a => a.Kind == AttachmentKind.Image)) hadImages = true;
                copy = copy with { Attachments = attachments.Select(a => MaskAttachment(a, map, detector)).ToList() };
            }
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
        return (request with { SystemPrompt = system, Messages = messages }, hadImages);
    }

    /// <summary>A text file's contents masked like any other text; anything that isn't text (a picture, a PDF) goes as it is.</summary>
    private static MessageAttachment MaskAttachment(MessageAttachment attachment, ShieldMap map, SensitiveDetector detector)
    {
        if (attachment.Kind != AttachmentKind.File || TextOf(attachment.Data) is not { } text) return attachment;
        var masked = map.Mask(text, detector);
        return ReferenceEquals(masked, text) || masked == text ? attachment : attachment with { Data = Encoding.UTF8.GetBytes(masked) };
    }

    /// <summary>The bytes as UTF-8 text, or null when they aren't text.</summary>
    private static string? TextOf(byte[] data)
    {
        if (data.Length == 0 || data.AsSpan().Contains((byte)0)) return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A tool call's arguments with the string values inside the JSON masked — the JSON is parsed and its strings
    /// rewritten, never the text replaced. Arguments that aren't JSON (a call cut off mid-way, which no client sends on)
    /// are masked as plain text.</summary>
    public static string MaskArguments(string arguments, ShieldMap map, SensitiveDetector detector) =>
        JsonStrings.Rewrite(arguments, value => map.Mask(value, detector)) ?? map.Mask(arguments, detector);

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

/// <summary>Rewrites the string literals of a JSON document in place: everything else — the layout, the numbers, the
/// strings left alone — stays byte for byte as it was.</summary>
public static class JsonStrings
{
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
