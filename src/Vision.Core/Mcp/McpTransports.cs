using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Vision.Core;

// MARK: - MCP transports
//
// stdio: Vision starts the server's program and writes one JSON-RPC message per line to its standard input; each line it
// prints is one message back. Its error output is kept (the last lines) to say why it stopped. On Windows a command that
// names a .cmd or .bat script (npx, uvx…) is run through cmd.exe, as Windows can't start a script directly.
//
// Streamable HTTP: every message is POSTed to the server's URL; the answer is JSON, or an event stream carrying one or more
// messages, or 202 for a notification. The session id the server gives at initialize goes back on every later request.
// The older HTTP+SSE transport (an event stream from a GET that names where to POST) is the fallback for servers
// configured as "sse" that don't speak Streamable HTTP.

/// <summary>A server program spoken to over its standard input and output.</summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly string? _cwd;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly LinkedList<string> _stderr = new();
    private readonly Lock _stderrLock = new();
    private Process? _process;

    public StdioMcpTransport(string command, IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string> env)
    {
        _command = command;
        _args = args;
        _cwd = cwd;
        _env = env;
    }

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;
    public string? ProtocolVersion { get; set; }

    public string? Diagnostics
    {
        get
        {
            lock (_stderrLock) return _stderr.Count == 0 ? null : string.Join("\n", _stderr);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var start = Launch(_command, _args, OperatingSystem.IsWindows());
        start.WorkingDirectory = _cwd is { } cwd && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.StandardOutputEncoding = TextUtil.Utf8NoBom;
        start.StandardErrorEncoding = TextUtil.Utf8NoBom;
        start.StandardInputEncoding = TextUtil.Utf8NoBom;
        foreach (var (key, value) in _env) start.Environment[key] = value;
        try
        {
            _process = Process.Start(start) ?? throw new McpException($"The server didn't start: {_command}.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new McpException($"The server didn't start: {error.Message} ({_command} — is it installed, and on the PATH?)");
        }
        _ = Task.Run(() => ReadOutAsync(_process));
        _ = Task.Run(() => ReadErrorsAsync(_process));
        return Task.CompletedTask;
    }

    /// <summary>How to start <paramref name="command"/>: a .cmd or .bat found on the PATH runs through cmd.exe (Windows).</summary>
    public static ProcessStartInfo Launch(string command, IReadOnlyList<string> args, bool windows)
    {
        if (windows && ResolveOnPath(command) is { } resolved
            && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var line = string.Join(" ", new[] { resolved }.Concat(args).Select(CmdQuote));
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { Arguments = $"/d /s /c \"{line}\"" };
        }
        var start = new ProcessStartInfo(windows && ResolveOnPath(command) is { } exe ? exe : command);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>The file <paramref name="command"/> names on the PATH (trying PATHEXT's extensions when it has none); null when
    /// it isn't found (Windows only — elsewhere the system resolves it).</summary>
    public static string? ResolveOnPath(string command)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> Candidates(string basePath) =>
            Path.HasExtension(basePath) ? [basePath] : extensions.Select(e => basePath + e.ToLowerInvariant());
        if (Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/'))
            return Candidates(command).FirstOrDefault(File.Exists);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Candidates(Path.Combine(folder.Trim('"'), command)).FirstOrDefault(File.Exists) is { } found) return found;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>An argument quoted for cmd.exe: in double quotes when it has a space or a cmd metacharacter.</summary>
    public static string CmdQuote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '&', '|', '<', '>', '^', '(', ')', '"', ',', ';', '=']) < 0
            ? arg
            : "\"" + arg.Replace("\"", "\"\"") + "\"";

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false } process) throw new McpException("The server isn't running." + (Diagnostics is { } d ? " " + TextUtil.Suffix(d, 600) : ""));
        var line = message.ToJsonString() + "\n";
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error)
        {
            throw new McpException($"The server stopped taking messages: {error.Message}");
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadOutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // Not protocol (a server that prints a banner on stdout): kept with its error output.
                    Note(line);
                    continue;
                }
                foreach (var message in Messages(node)) await _incoming.Writer.WriteAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed.
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) Note(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed.
        }
    }

    private void Note(string line)
    {
        if (line.Trim().Length == 0) return;
        lock (_stderrLock)
        {
            _stderr.AddLast(TextUtil.Prefix(line, 400));
            while (_stderr.Count > 30) _stderr.RemoveFirst();
        }
    }

    /// <summary>A message, or each message of a batch.</summary>
    internal static IEnumerable<JsonObject> Messages(JsonNode? node) => node switch
    {
        JsonObject one => [one],
        JsonArray batch => batch.OfType<JsonObject>(),
        _ => [],
    };

    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process) return;
        try
        {
            if (!process.HasExited)
            {
                // The polite way first: a closed input is how a stdio server is told to quit.
                try { process.StandardInput.Close(); } catch (Exception) { /* closed already */ }
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone already.
        }
        process.Dispose();
        _incoming.Writer.TryComplete();
    }
}

/// <summary>Streamable HTTP, with the older HTTP+SSE transport as the fallback for a server configured as "sse".</summary>
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly Uri _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _http;
    private readonly bool _allowLegacy;
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly CancellationTokenSource _stop = new();
    private string? _sessionId;
    private Uri? _legacyPost;
    private string? _diagnostics;

    public HttpMcpTransport(Uri url, IReadOnlyDictionary<string, string> headers, bool allowLegacy = false, HttpMessageHandler? handler = null)
    {
        _url = url;
        _headers = headers;
        _allowLegacy = allowLegacy;
        _http = handler is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;
    public string? ProtocolVersion { get; set; }
    public string? Diagnostics => _diagnostics;
    /// <summary>Whether the server turned out to speak only the older HTTP+SSE transport.</summary>
    public bool UsingLegacySse => _legacyPost is not null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_legacyPost is not null)
        {
            await PostLegacyAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var request = Request(HttpMethod.Post, _url);
        request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            _diagnostics = error.Message;
            throw new McpException($"Couldn't reach {_url.Host}: {error.Message}");
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } id) _sessionId = id;
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeBody(response).ConfigureAwait(false);
            response.Dispose();
            var isInitialize = message["method"] is JsonValue m && m.TryGetValue<string>(out var method) && method == "initialize";
            // A server that only speaks the old transport answers a POST to its event-stream URL with 4xx.
            if (_allowLegacy && isInitialize && (int)response.StatusCode is 400 or 404 or 405)
            {
                await StartLegacyAsync(cancellationToken).ConfigureAwait(false);
                await PostLegacyAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }
            _diagnostics = $"HTTP {(int)response.StatusCode}: {TextUtil.Prefix(body, 300)}";
            throw new McpException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    $"The server turned Vision away (HTTP {(int)response.StatusCode}) — check its key or token in Settings › MCP Servers. {TextUtil.Prefix(body, 200)}".Trim(),
                _ => $"The server answered HTTP {(int)response.StatusCode}: {TextUtil.Prefix(body, 200)}".Trim(),
            });
        }
        if (response.StatusCode == HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
        {
            response.Dispose();
            return;
        }
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Read the stream as it comes (a long tool call may send progress before its answer).
            _ = Task.Run(() => ReadEventsAsync(response, null));
            return;
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Trim().Length == 0) return;
            try
            {
                foreach (var reply in StdioMcpTransport.Messages(JsonNode.Parse(text))) await _incoming.Writer.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                _diagnostics = "Not JSON: " + TextUtil.Prefix(text, 200);
                throw new McpException($"{_url.Host} didn't answer with JSON.");
            }
        }
    }

    private HttpRequestMessage Request(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", $"Vision/{AppInfo.Version} (Windows)");
        foreach (var (name, value) in _headers)
            if (!request.Headers.TryAddWithoutValidation(name, value)) _diagnostics = $"Header {name} couldn't be sent.";
        if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (ProtocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        return request;
    }

    // MARK: Server-sent events

    /// <summary>Read an event stream: "message" events (or unnamed ones) carry JSON-RPC; the legacy transport's first event,
    /// "endpoint", names where to POST.</summary>
    private async Task ReadEventsAsync(HttpResponseMessage response, TaskCompletionSource<Uri>? endpoint)
    {
        try
        {
            using (response)
            await using (var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var name = "";
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        await DispatchAsync(name, data.ToString(), endpoint).ConfigureAwait(false);
                        name = "";
                        data.Clear();
                        continue;
                    }
                    if (line.StartsWith(':')) continue;
                    var colon = line.IndexOf(':');
                    var field = colon < 0 ? line : line[..colon];
                    var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
                    if (field == "event") name = value;
                    else if (field == "data") data.Append(data.Length > 0 ? "\n" : "").Append(value);
                }
                if (data.Length > 0) await DispatchAsync(name, data.ToString(), endpoint).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // The stream ended.
        }
        finally
        {
            endpoint?.TrySetException(new McpException($"{_url.Host} closed its event stream before naming where to send messages."));
            // The old transport has one stream for everything: when it ends, so does the connection.
            if (endpoint is not null) _incoming.Writer.TryComplete();
        }
    }

    private async Task DispatchAsync(string name, string data, TaskCompletionSource<Uri>? endpoint)
    {
        if (data.Length == 0) return;
        if (name == "endpoint" && endpoint is not null)
        {
            if (Uri.TryCreate(_url, data.Trim(), out var post)) endpoint.TrySetResult(post);
            return;
        }
        if (name is not ("" or "message")) return;
        try
        {
            foreach (var message in StdioMcpTransport.Messages(JsonNode.Parse(data))) await _incoming.Writer.WriteAsync(message).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            _diagnostics = "An event that isn't JSON: " + TextUtil.Prefix(data, 200);
        }
    }

    private async Task StartLegacyAsync(CancellationToken cancellationToken)
    {
        var request = Request(HttpMethod.Get, _url);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new McpException($"{_url.Host} answered HTTP {status} to both Streamable HTTP and the older SSE transport.");
        }
        var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadEventsAsync(response, endpoint));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        _legacyPost = await endpoint.Task.WaitAsync(wait.Token).ConfigureAwait(false);
    }

    private async Task PostLegacyAsync(JsonObject message, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, _legacyPost!);
        request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _diagnostics = $"HTTP {(int)response.StatusCode}";
            throw new McpException($"{_url.Host} answered HTTP {(int)response.StatusCode}.");
        }
    }

    private static async Task<string> SafeBody(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Tell the server the session is over (Streamable HTTP); a server that doesn't support it says 405, which is fine.
        if (_sessionId is not null && _legacyPost is null)
        {
            try
            {
                using var request = Request(HttpMethod.Delete, _url);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var _ = await _http.SendAsync(request, wait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // Best effort.
            }
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        _incoming.Writer.TryComplete();
        _http.Dispose();
        _stop.Dispose();
    }
}
