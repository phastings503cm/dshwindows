using System.Net;
using System.Text;

namespace Dsh.Core.Tests;

/// <summary>An in-memory HTTP endpoint for <see cref="OpenAiClient"/>: every request is answered by
/// <paramref name="respond"/> and recorded, so the client's wire format and stream parsing can be
/// tested without a network.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, Uri Url, string Body, IReadOnlyDictionary<string, string> Headers);

    private readonly Lock _lock = new();
    private readonly List<Recorded> _requests = [];

    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    private void Record(HttpRequestMessage request, string body)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_lock) _requests.Add(new Recorded(request.Method, request.RequestUri!, body, headers));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Record(request, body);
        return respond(request);
    }

    /// <summary>A server-sent-event stream: each payload becomes one "data:" event, then [DONE].</summary>
    public static HttpResponseMessage Sse(params string[] payloads) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(payloads.Select(p => $"data: {p}\n\n")) + "data: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
