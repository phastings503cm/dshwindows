using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - Learning what a server refuses
//
// The rules table (ModelRequestRules) gets the known providers right. For the rest — a new model id, a proxy, a provider
// that changed its mind — the server's own 400 says what it didn't like: "invalid temperature: only 1 is allowed for this
// model", "Unsupported parameter: 'max_tokens' … Use 'max_completion_tokens' instead", vLLM's "Extra inputs are not
// permitted" on chat_template_kwargs, Z.ai's "Invalid API parameter". The client then retries once without that parameter,
// remembers the lesson for that server and model (on disk, so the next launch starts right), and tells the chat in one
// line instead of failing the turn. Vision for macOS does the same (RequestAdaptations.swift; same file shape).

/// <summary>One lesson: what to change in requests to a server and model.</summary>
public abstract record RequestChange
{
    /// <summary>Leave this body key out ("temperature", "thinking", "chat_template_kwargs.enable_thinking", …).</summary>
    public sealed record Drop(string Key) : RequestChange;
    /// <summary>Send this key under another name ("max_tokens" → "max_completion_tokens").</summary>
    public sealed record Rename(string From, string To) : RequestChange;
    /// <summary>The server refused a parameter without naming it: send only the basics (model, messages, stream, tools).</summary>
    public sealed record Minimal : RequestChange;
    /// <summary>The server wants the model's reasoning back on assistant messages that called tools (reasoning_content).</summary>
    public sealed record EchoReasoning : RequestChange;
}

/// <summary>What was learned for one server and model (stored as JSON: dropped, renamed, minimal, echoReasoning, learnedAt).</summary>
public sealed class RequestAdaptation
{
    public List<string> Dropped { get; set; } = [];
    public Dictionary<string, string> Renamed { get; set; } = new(StringComparer.Ordinal);
    public bool Minimal { get; set; }
    public bool EchoReasoning { get; set; }
    public DateTimeOffset LearnedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public bool IsEmpty => Dropped.Count == 0 && Renamed.Count == 0 && !Minimal && !EchoReasoning;

    /// <summary>Whether this lesson is already part of it.</summary>
    public bool Contains(RequestChange change) => change switch
    {
        RequestChange.Drop drop => Dropped.Contains(drop.Key) || (Minimal && RequestBody.OptionalKeys.Contains(drop.Key)),
        RequestChange.Rename rename => Renamed.GetValueOrDefault(rename.From) == rename.To,
        RequestChange.Minimal => Minimal,
        RequestChange.EchoReasoning => EchoReasoning,
        _ => false,
    };

    internal void Apply(RequestChange change)
    {
        switch (change)
        {
            case RequestChange.Drop drop:
                if (!Dropped.Contains(drop.Key)) Dropped.Add(drop.Key);
                break;
            case RequestChange.Rename rename:
                Renamed[rename.From] = rename.To;
                break;
            case RequestChange.Minimal:
                Minimal = true;
                break;
            case RequestChange.EchoReasoning:
                EchoReasoning = true;
                break;
        }
        LearnedAt = DateTimeOffset.Now;
    }

    internal RequestAdaptation Copy() => new()
    {
        Dropped = [.. Dropped], Renamed = new Dictionary<string, string>(Renamed, StringComparer.Ordinal), Minimal = Minimal,
        EchoReasoning = EchoReasoning, LearnedAt = LearnedAt,
    };

    /// <summary>In a sentence, for Settings: "no `temperature`, `thinking`; `max_completion_tokens` instead of `max_tokens`".</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Minimal) parts.Add("only the basic settings");
            if (Dropped.Count > 0) parts.Add("no " + string.Join(", ", Dropped.Select(d => $"`{d}`")));
            foreach (var (from, to) in Renamed.OrderBy(r => r.Key, StringComparer.Ordinal)) parts.Add($"`{to}` instead of `{from}`");
            if (EchoReasoning) parts.Add("its reasoning sent back with tool calls");
            return string.Join("; ", parts);
        }
    }
}

/// <summary>Edits on the request body.</summary>
public static class RequestBody
{
    /// <summary>The optional parameters Minimal leaves out.</summary>
    public static readonly IReadOnlySet<string> OptionalKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "temperature", "top_p", "reasoning_effort", "thinking", "chat_template_kwargs", "stream_options",
        "tool_choice", "parallel_tool_calls", "response_format", "presence_penalty", "frequency_penalty",
        "max_tokens", "max_completion_tokens", "seed", "stop",
    };

    /// <summary>The body's parameter names, nested ones as "parent.child" (only the nested ones Vision ever sets).</summary>
    public static IReadOnlySet<string> Keys(JsonObject body)
    {
        var keys = new HashSet<string>(body.Select(p => p.Key), StringComparer.Ordinal);
        if (body["chat_template_kwargs"] is JsonObject kwargs)
            foreach (var (key, _) in kwargs) keys.Add($"chat_template_kwargs.{key}");
        return keys;
    }

    /// <summary>Apply what was learned.</summary>
    public static void Apply(RequestAdaptation adaptation, JsonObject body)
    {
        if (adaptation.Minimal)
            foreach (var key in OptionalKeys) body.Remove(key);
        foreach (var key in adaptation.Dropped) Remove(key, body);
        foreach (var (from, to) in adaptation.Renamed)
        {
            if (!body.TryGetPropertyValue(from, out var value)) continue;
            body.Remove(from);
            body[to] = value;
        }
    }

    /// <summary>Remove a key; "parent.child" removes inside the parent object (and the parent when it is left empty).</summary>
    public static void Remove(string key, JsonObject body)
    {
        var parts = key.Split('.', 2);
        if (parts.Length != 2)
        {
            body.Remove(key);
            return;
        }
        if (body[parts[0]] is not JsonObject inner) return;
        inner.Remove(parts[1]);
        if (inner.Count == 0) body.Remove(parts[0]);
    }
}

// MARK: - Reading a refusal

public static partial class ParameterRejection
{
    /// <summary>Parameters a server may refuse, longest names first so "max_tokens" never matches inside
    /// "max_completion_tokens". The whole chat_template_kwargs comes before its parts: a server that refuses the field
    /// refuses all of it.</summary>
    internal static readonly string[] Candidates =
    [
        "max_completion_tokens", "chat_template_kwargs", "parallel_tool_calls", "frequency_penalty",
        "presence_penalty", "response_format", "reasoning_effort", "enable_thinking", "stream_options",
        "include_usage", "tool_choice", "temperature", "max_tokens", "thinking", "top_p", "seed",
    ];

    /// <summary>Words that say a named parameter is the problem (not merely mentioned).</summary>
    internal static readonly string[] RefusalCues =
    [
        "unsupported", "not supported", "does not support", "doesn't support", "not support", "invalid",
        "unknown", "unrecognized", "unrecognised", "not allowed", "not permitted", "extra inputs", "extra_forbidden",
        "extra fields", "unexpected", "only", "cannot", "can not", "can't", "must be", "not available",
        "not a valid", "is deprecated", "no longer supported", "out of range", "should be", "disabled",
    ];

    /// <summary>What <paramref name="body"/> (a refusal: HTTP 400/422) asks to change, given the parameters the request
    /// carried. Null when the refusal isn't about one of them.</summary>
    public static RequestChange? Change(int status, string body, IReadOnlySet<string> sent)
    {
        if (status is not (400 or 422)) return null;
        var lower = Text(body).ToLowerInvariant();

        // Kimi: "thinking is enabled but reasoning_content is missing" (an assistant tool-call message without it).
        if (lower.Contains("reasoning_content", StringComparison.Ordinal)
            && (lower.Contains("missing", StringComparison.Ordinal) || lower.Contains("required", StringComparison.Ordinal)))
            return new RequestChange.EchoReasoning();
        // Structured errors name the parameter: OpenAI's {"error":{"param":"max_tokens"}}, FastAPI/vLLM's
        // {"detail":[{"loc":["body","chat_template_kwargs"]}]}.
        foreach (var named in NamedParameters(body))
        {
            if (Match(named, sent) is not { } key) continue;
            return Decide(key, lower);
        }
        // Otherwise, a sent parameter the message names next to a refusal word.
        if (!RefusalCues.Any(cue => lower.Contains(cue, StringComparison.Ordinal))) return null;
        foreach (var key in Candidates)
        {
            if (!(sent.Contains(key) || (key == "enable_thinking" && sent.Contains("chat_template_kwargs.enable_thinking")))) continue;
            var name = key.Split('.')[^1];
            if (!Mentions(lower, name)) continue;
            var resolved = key == "enable_thinking" ? "chat_template_kwargs.enable_thinking" : key;
            if (resolved == "include_usage") return new RequestChange.Drop("stream_options");
            return Decide(resolved, lower);
        }
        // A refusal of "a parameter" that names none (Z.ai's 1210 "Invalid API parameter, please check the documentation."):
        // fall back to the basics, if there is anything optional to leave out.
        var nameless = lower.Contains("invalid api parameter", StringComparison.Ordinal) || lower.Contains("invalid parameter", StringComparison.Ordinal)
                       || lower.Contains("parameter is invalid", StringComparison.Ordinal) || lower.Contains("parameters are invalid", StringComparison.Ordinal)
                       || lower.Contains("invalid request parameter", StringComparison.Ordinal) || BusinessCode(body) == "1210";
        if (nameless && sent.Any(RequestBody.OptionalKeys.Contains)) return new RequestChange.Minimal();
        return null;
    }

    private static RequestChange Decide(string key, string lower)
    {
        if (key == "max_tokens" && lower.Contains("max_completion_tokens", StringComparison.Ordinal))
            return new RequestChange.Rename("max_tokens", "max_completion_tokens");
        if (key == "max_completion_tokens" && lower.Contains("max_tokens", StringComparison.Ordinal)
            && !lower.Contains("use 'max_completion_tokens'", StringComparison.Ordinal))
            return new RequestChange.Rename("max_completion_tokens", "max_tokens");
        return new RequestChange.Drop(key);
    }

    /// <summary>The sent key a structured error's parameter name refers to.</summary>
    private static string? Match(string named, IReadOnlySet<string> sent)
    {
        var name = named.ToLowerInvariant();
        if (sent.Contains(name)) return name;
        if (name == "enable_thinking" && sent.Contains("chat_template_kwargs.enable_thinking")) return "chat_template_kwargs.enable_thinking";
        if (name.StartsWith("thinking", StringComparison.Ordinal) && sent.Contains("thinking")) return "thinking";
        if (name.StartsWith("stream_options", StringComparison.Ordinal) && sent.Contains("stream_options")) return "stream_options";
        return null;
    }

    private static readonly string[] ThinkingShapes =
    [
        "thinking.type", "thinking type", "'thinking'", "\"thinking\"", "`thinking`",
        "parameter thinking", "invalid thinking", "thinking parameter", "thinking is not supported",
        "thinking: ", "thinking.keep", "thinking cannot", "thinking can not", "thinking must",
    ];

    /// <summary><paramref name="name"/> appears as a word of its own ("temperature", not "temperatures"; "thinking" only as
    /// a parameter).</summary>
    internal static bool Mentions(string lower, string name)
    {
        // "thinking is enabled but …" is about thinking, not the `thinking` parameter.
        if (name == "thinking") return ThinkingShapes.Any(shape => lower.Contains(shape, StringComparison.Ordinal));
        return Regex.IsMatch(lower, $"(?<![a-z0-9_]){Regex.Escape(name)}(?![a-z0-9_])", RegexOptions.CultureInvariant);
    }

    /// <summary>The human-readable part of an error body.</summary>
    public static string Text(string body)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return body;
        }
        if (obj is null) return body;
        var parts = new List<string>();
        if (obj["error"] is JsonObject err)
        {
            if (JsonArgs.String(err, "message") is { } m) parts.Add(m);
            if (JsonArgs.String(err, "param") is { } p) parts.Add($"param {p}");
        }
        else if (JsonArgs.String(obj, "error") is { } text) parts.Add(text);
        if (JsonArgs.String(obj, "message") is { } message) parts.Add(message);
        if (JsonArgs.String(obj, "detail") is { } detail) parts.Add(detail);
        if (obj["detail"] is JsonArray details)
            foreach (var d in details.OfType<JsonObject>())
                if (JsonArgs.String(d, "msg") is { } msg) parts.Add(msg);
        return parts.Count == 0 ? body : string.Join(" — ", parts);
    }

    /// <summary>Parameter names a structured error points at.</summary>
    internal static IReadOnlyList<string> NamedParameters(string body)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }
        if (obj is null) return [];
        var names = new List<string>();
        if (obj["error"] is JsonObject err && JsonArgs.String(err, "param") is { Length: > 0 } param) names.Add(param);
        if (obj["detail"] is JsonArray details)
        {
            foreach (var d in details.OfType<JsonObject>())
            {
                if (d["loc"] is not JsonArray loc) continue;
                var path = loc.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Where(s => s != "body").ToList();
                if (path.Count >= 2 && path[0] == "chat_template_kwargs") names.Add($"chat_template_kwargs.{path[1]}");
                if (path.Count > 0) names.Add(path[0]);
            }
        }
        return names;
    }

    /// <summary>A Z.ai-style business code (<c>{"error":{"code":"1210"}}</c>), as text.</summary>
    public static string? BusinessCode(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject { } obj || obj["error"] is not JsonObject err) return null;
            return err["code"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when JsonNumbers.TryGetInt(v, out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The one line the chat shows after a lesson.</summary>
    public static string Notice(RequestChange change, string provider, string model)
    {
        var who = $"{provider} ({model})";
        return change switch
        {
            RequestChange.Drop drop => $"{who} doesn't take `{drop.Key.Split('.')[^1]}` — Vision stopped sending it for this model and carried on.",
            RequestChange.Rename rename => $"{who} wants `{rename.To}` instead of `{rename.From}` — Vision switched and carried on.",
            RequestChange.Minimal => $"{who} refused one of Vision's optional settings without saying which — Vision now sends only the basics to this model.",
            _ => $"{who} wants the model's reasoning sent back with its tool calls — Vision now does that for this model.",
        };
    }
}

// MARK: - The memory

/// <summary>Per server and model, what was learned — kept in a small JSON file (the app sets <see cref="FilePath"/>), shared
/// by every chat and subagent, so a lesson is learned once. Thread-safe.</summary>
public sealed class RequestAdaptationStore
{
    public static RequestAdaptationStore Shared { get; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Lock _lock = new();
    private Dictionary<string, RequestAdaptation> _map = new(StringComparer.Ordinal);
    private string? _path;

    public RequestAdaptationStore(string? filePath = null)
    {
        if (filePath is not null) Load(filePath);
    }

    /// <summary>Where the lessons live (request-adaptations.json in the data folder); setting it reads what is there.</summary>
    public string? FilePath
    {
        get
        {
            lock (_lock) return _path;
        }
        set
        {
            if (value is not null) Load(value);
            else lock (_lock) _path = null;
        }
    }

    /// <summary>The key for a server and model: the base URL (lower-cased, no trailing slash) and the model id.</summary>
    public static string Key(string baseUrl, string model) => $"{baseUrl.Trim().ToLowerInvariant().Trim('/')}|{model}";

    public RequestAdaptation? Adaptation(string key)
    {
        lock (_lock) return _map.TryGetValue(key, out var found) ? found.Copy() : null;
    }

    /// <summary>Everything learned, by key (for Settings).</summary>
    public IReadOnlyDictionary<string, RequestAdaptation> All
    {
        get
        {
            lock (_lock) return _map.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        }
    }

    /// <summary>Remember a lesson. Returns false when it was already known.</summary>
    public bool Learn(RequestChange change, string key)
    {
        Dictionary<string, RequestAdaptation> snapshot;
        string? path;
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var entry)) _map[key] = entry = new RequestAdaptation();
            if (entry.Contains(change)) return false;
            entry.Apply(change);
            snapshot = _map.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
            path = _path;
        }
        if (path is not null) Write(snapshot, path);
        return true;
    }

    /// <summary>Forget what was learned for keys starting with <paramref name="prefix"/> (a server's base URL), or everything.</summary>
    public void Forget(string? prefix = null)
    {
        Dictionary<string, RequestAdaptation> snapshot;
        string? path;
        lock (_lock)
        {
            if (prefix is null) _map.Clear();
            else
            {
                var p = prefix.Trim().ToLowerInvariant().Trim('/');
                _map = _map.Where(e => !e.Key.StartsWith(p, StringComparison.Ordinal)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            }
            snapshot = _map.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal);
            path = _path;
        }
        if (path is not null) Write(snapshot, path);
    }

    /// <summary>Forget what was learned for one server and model (Settings › AI Models › Forget).</summary>
    public void ForgetKey(string key)
    {
        Dictionary<string, RequestAdaptation> snapshot;
        string? path;
        lock (_lock)
        {
            if (!_map.Remove(key)) return;
            snapshot = _map.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal);
            path = _path;
        }
        if (path is not null) Write(snapshot, path);
    }

    private void Load(string path)
    {
        Dictionary<string, RequestAdaptation> loaded;
        try
        {
            loaded = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, RequestAdaptation>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            loaded = []; // a damaged file only costs a lesson relearned
        }
        lock (_lock)
        {
            _path = path;
            _map = new Dictionary<string, RequestAdaptation>(loaded.Where(e => e.Value is not null), StringComparer.Ordinal);
        }
    }

    private static void Write(Dictionary<string, RequestAdaptation> map, string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map, Json), TextUtil.Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the lesson still holds for this session.
        }
    }
}
