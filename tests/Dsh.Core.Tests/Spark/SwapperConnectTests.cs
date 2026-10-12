using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>A Swapper 2.0 stand-in: app tokens (bearer auth on every /api route, a 401 once revoked), GET /api/connect,
/// GET /api/status in the 2.0 shape, and POST /api/tokens/create for a signed-in (cookie) session.</summary>
public sealed class FakeSwapper2 : IAsyncDisposable
{
    private const string Cookie = "c2Vzc2lvbg";
    public const string Token = "sst_1a2b3c4d_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
    public TestHttpServer Server { get; }
    public bool Revoked { get; set; }
    /// <summary>An older Swapper: no tokens, no /api/connect.</summary>
    public bool Old { get; init; }
    private int _jobPolls = -1;
    private readonly Lock _lock = new();

    public FakeSwapper2(bool old = false)
    {
        Old = old;
        Server = new TestHttpServer(Handle, tls: true);
    }

    public Uri Url => new($"https://127.0.0.1:{Server.Port}");
    public string Fingerprint => SparkSwapperClient.Fingerprint(Server.Certificate!.RawData);

    public string Connect => $$"""
        {"name": "DGX Spark (spark-3f2a)", "hostname": "spark-3f2a", "platform": "nvidia-gb10", "version": "2.0.0",
         "swapper_url": "{{Url.ToString().TrimEnd('/')}}", "api_base": "https://127.0.0.1:11443/v1", "api_key": "vllm-local",
         "model": "Qwen3.8-27B", "context": 262144, "fingerprint": "{{Fingerprint}}"}
        """;

    private static TestHttpServer.Response Json(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, body, "application/json", headers);

    private TestHttpServer.Response Handle(TestHttpServer.Request request)
    {
        var path = request.Path.Split('?')[0];
        var cookie = request.Headers.TryGetValue("Cookie", out var c) && c.Contains($"swapper_session={Cookie}");
        var bearer = request.Headers.TryGetValue("Authorization", out var auth) ? auth : null;
        var tokenOk = !Old && !Revoked && bearer == $"Bearer {Token}";
        if (bearer is not null && !tokenOk && path != "/api/session") return Json(401, """{"error": "This app's token isn't valid."}""");
        if (path == "/api/session")
            return Json(200, Old
                ? """{"setup_needed": false, "user": null, "role": null, "demo": false}"""
                : $$"""{"setup_needed": false, "user": {{(tokenOk || cookie ? "\"pat\"" : "null")}}, "role": "admin", "demo": false, "version": "2.0.0", "platform": "nvidia-gb10", "hostname": "spark-3f2a"}""");
        if (path == "/api/login" && request.Method == "POST")
        {
            return request.Body.Contains("\"correct horse\"")
                ? Json(200, """{"ok": true}""", new Dictionary<string, string> { ["Set-Cookie"] = $"swapper_session={Cookie}; Path=/; Secure" })
                : Json(401, """{"error": "That username and password don't match."}""");
        }
        if (!tokenOk && !cookie) return Json(401, """{"error": "Please sign in."}""");
        switch (path)
        {
            case "/api/status":
                return Json(200, """
                    {"active": "standard", "loading": null, "busy": false, "version": "2.0.0", "platform": "nvidia-gb10", "hostname": "spark-3f2a",
                     "models": {
                       "standard": {"key": "standard", "title": "Qwen3.8 27B", "context": 262144, "served_id": "Qwen3.8-27B", "running": true,
                                    "healthy": true, "order": 1, "installed": true, "platform": "nvidia-gb10", "family": "qwen3.8-27b",
                                    "perf": {"decode": 58.3, "decode_concurrent": 220.6, "concurrency": 8, "prefill": 1793.0, "source": "sparkDash (MiaAI-Lab)", "estimate": false},
                                    "measured": {"decode": 27.1, "at": 1791234567.0}},
                       "flash": {"key": "flash", "title": "Qwen3.8 Flash", "context": 524288, "served_id": "Qwen3.8-Flash", "running": false,
                                 "healthy": false, "order": 2, "installed": false, "perf": {"decode": 42, "estimate": true, "range": "30-50"}, "measured": null,
                                 "install": {"size_gb": 165, "time": "1–2 hours: builds the engine image", "requires": ["git", "docker"]},
                                 "eta": "a few minutes", "recommended": false, "superseded_by": null, "kind": "vllm-docker"}},
                     "job": null, "install_job": null, "memory": {"total": 128.0}, "update": null}
                    """);
            case "/api/swap" when request.Method == "POST":
                return request.Body.Contains("\"flash\"")
                    ? Json(400, """{"error": "Qwen3.8 Flash isn't installed yet. Install it first.", "not_installed": true}""")
                    : Json(202, """{"ok": true, "job": {"id": "s1", "target": "standard", "source": null, "state": "running", "error": null, "note": null, "started": 1.0, "finished": null, "steps": [], "log": []}}""");
            case "/api/models/install" when !Old && request.Method == "POST":
                lock (_lock)
                {
                    if (_jobPolls >= 0 && _jobPolls < 2) return Json(409, """{"error": "An install is already in progress."}""");
                    _jobPolls = 0;
                }
                return Json(202, """{"ok": true, "job": {"id": "i1", "target": "install:model:flash", "source": null, "state": "running", "error": null, "note": null, "started": 1.0, "finished": null, "steps": [{"key": "requires", "label": "Checking what it needs", "state": "running"}], "log": [{"t": 1.0, "m": "Installing Qwen3.8 Flash"}]}}""");
            case "/api/provisioning/job":
                lock (_lock)
                {
                    var done = ++_jobPolls >= 2;
                    var state = done ? "done" : "running";
                    var finished = done ? "9.0" : "null";
                    return Json(200, "{\"job\": {\"id\": \"i1\", \"target\": \"install:model:flash\", \"source\": null, \"state\": \"" + state +
                                     "\", \"error\": null, \"note\": null, \"started\": 1.0, \"finished\": " + finished +
                                     ", \"steps\": [], \"log\": [{\"t\": 2.0, \"m\": \"poll " + _jobPolls + "\"}]}}");
                }
            case "/api/connect" when !Old:
                return Json(200, Connect);
            case "/api/tokens/create" when !Old && cookie && request.Method == "POST":
                return Json(200, "{\"id\": \"1a2b3c4d\", \"token\": \"" + Token + "\", \"code\": \"SSC1:x\", \"connect\": " + Connect + "}");
            case "/api/credentials":
                return Json(200, """{"api": {"key": "vllm-local", "base_url": "https://127.0.0.1:11443/v1", "model": "Qwen3.8-27B"}, "openclaw": {}}""");
        }
        return Json(404, """{"error": "not found"}""");
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

public sealed class SwapperConnectTests
{
    private const string Fingerprint = "3A:9F:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:C2";

    private static JsonObject Fields(Action<JsonObject>? change = null)
    {
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["name"] = "DGX Spark (spark-3f2a)",
            ["hostname"] = "spark-3f2a",
            ["platform"] = "nvidia-gb10",
            ["version"] = "2.0.0",
            ["swapper_url"] = "https://192.168.1.42:8999",
            ["api_base"] = "https://192.168.1.42:11443/v1",
            ["api_key"] = "vllm-local",
            ["model"] = "Qwen3.8-27B",
            ["context"] = 262144,
            ["fingerprint"] = Fingerprint,
            ["token"] = "sst_1a2b3c4d_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ",
            ["user"] = "pat",
        };
        change?.Invoke(fields);
        return fields;
    }

    private static string Base64Url(string json, bool pad = false)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        if (!pad) b64 = b64.TrimEnd('=');
        return b64.Replace('+', '-').Replace('/', '_');
    }

    // MARK: - Connection codes

    [Fact]
    public void DecodesACode()
    {
        var code = SwapperConnectionCode.Parse(SwapperConnectionCode.Encode(Fields()));
        Assert.Equal("DGX Spark (spark-3f2a)", code.Name);
        Assert.Equal("https://192.168.1.42:8999", code.SwapperUrl);
        Assert.Equal("https://192.168.1.42:11443/v1", code.ApiBase);
        Assert.Equal("vllm-local", code.ApiKey);
        Assert.Equal("Qwen3.8-27B", code.Model);
        Assert.Equal(262144, code.Context);
        Assert.Equal(Fingerprint, code.Fingerprint);
        Assert.Equal("nvidia-gb10", code.Platform);
        Assert.Equal("2.0.0", code.Version);
        Assert.Equal("pat", code.User);
        Assert.StartsWith("sst_1a2b3c4d_", code.Token);
    }

    [Fact]
    public void AcceptsEveryWayACodeGetsPasted()
    {
        // The name has characters whose base64 differs between the two alphabets, so both are exercised.
        var json = Fields(f => f["name"] = "Spark ??>>~~ (évo)")
            .ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var url = Base64Url(json);
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Contains('+', standard); // so the standard alphabet really differs from base64url here
        string[] pasted =
        [
            "SSC1:" + url,
            "ssc1:" + url,
            url,                                                         // no prefix
            "SSC1:" + Base64Url(json, pad: true),                        // padded
            "SSC1:" + standard,                                          // standard base64
            "  SSC1:" + url[..20] + "\n" + url[20..40] + "\r\n  " + url[40..] + "\n", // wrapped by a mail client
            json,                                                        // the JSON itself
            "\n" + json + "\n",
        ];
        foreach (var text in pasted)
        {
            Assert.True(SwapperConnectionCode.TryParse(text, out var code), text);
            Assert.Equal("Spark ??>>~~ (évo)", code!.Name);
            Assert.Equal(Fingerprint, code.Fingerprint);
        }
    }

    [Fact]
    public void IgnoresUnknownKeysAndAcceptsAMissingVersion()
    {
        var code = SwapperConnectionCode.Parse(SwapperConnectionCode.Encode(Fields(f =>
        {
            f.Remove("v");
            f["future_field"] = new JsonObject { ["x"] = 1 };
        })));
        Assert.Equal("https://192.168.1.42:8999", code.SwapperUrl);
    }

    [Fact]
    public void ANameIsMadeUpWhenTheCodeHasNone()
    {
        var code = SwapperConnectionCode.Parse(SwapperConnectionCode.Encode(Fields(f => f.Remove("name"))));
        Assert.Equal("DGX Spark (spark-3f2a)", code.Name);
        var halo = SwapperConnectionCode.Parse(SwapperConnectionCode.Encode(Fields(f =>
        {
            f.Remove("name");
            f["platform"] = "amd-strix-halo";
            f["hostname"] = "evo-x2";
        })));
        Assert.Equal("Strix Halo (evo-x2)", halo.Name);
    }

    public static TheoryData<string> Broken() =>
    [
        "",
        "   ",
        "hello",
        "SSC1:",
        "SSC1:!!!not base64!!!",
        "SSC2:" + Base64Url(Fields().ToJsonString()),                                    // another prefix is not ours
        SwapperConnectionCode.Encode(Fields(f => f["v"] = 2)),                           // a version we don't know
        SwapperConnectionCode.Encode(Fields(f => f.Remove("token"))),
        SwapperConnectionCode.Encode(Fields(f => f.Remove("swapper_url"))),
        SwapperConnectionCode.Encode(Fields(f => f.Remove("api_base"))),
        SwapperConnectionCode.Encode(Fields(f => f.Remove("fingerprint"))),
        SwapperConnectionCode.Encode(Fields(f => f["swapper_url"] = "http://192.168.1.42:8999")),   // not https
        SwapperConnectionCode.Encode(Fields(f => f["api_base"] = "ftp://192.168.1.42/v1")),
        SwapperConnectionCode.Encode(Fields(f => f["fingerprint"] = Fingerprint[3..])),            // 31 pairs
        SwapperConnectionCode.Encode(Fields(f => f["fingerprint"] = Fingerprint.Replace("3A", "3G"))), // not hex
        SwapperConnectionCode.Encode(Fields(f => f["fingerprint"] = Fingerprint.Replace(":", ""))), // no separators
        "SSC1:" + Base64Url("[1, 2, 3]"),
    ];

    [Theory]
    [MemberData(nameof(Broken))]
    public void RefusesWhatIsntACode(string text)
    {
        Assert.False(SwapperConnectionCode.TryParse(text, out _));
        var error = Assert.Throws<FormatException>(() => SwapperConnectionCode.Parse(text));
        Assert.Equal("That doesn't look like a Spark Swapper connection code. Copy it again from the Swapper's Connect tab.", error.Message);
    }

    [Fact]
    public void FingerprintsAreNormalisedToUpperCase()
    {
        var code = SwapperConnectionCode.Parse(SwapperConnectionCode.Encode(Fields(f => f["fingerprint"] = Fingerprint.ToLowerInvariant())));
        Assert.Equal(Fingerprint, code.Fingerprint);
    }

    // MARK: - Status in the 2.0 shape

    [Fact]
    public void DecodesTheNewStatusFieldsAndTreatsAnOlderSwapperAsInstalled()
    {
        var status = JsonSerializer.Deserialize<SwapperStatus>("""
            {"active": "standard", "busy": false, "version": "2.0.0", "platform": "amd-strix-halo", "hostname": "evo-x2",
             "models": {
               "standard": {"key": "standard", "title": "Qwen3.8 27B", "context": 262144, "served_id": "q", "running": true, "healthy": true,
                            "installed": true, "platform": "amd-strix-halo", "family": "qwen3.8-27b",
                            "perf": {"decode": 58.3, "decode_concurrent": 220.6, "concurrency": 8, "prefill": 1793.0, "source": "sparkDash", "estimate": false},
                            "measured": {"decode": 27.1, "at": 1791234567.0}},
               "big": {"key": "big", "title": "Big", "context": 131072, "served_id": "b", "running": false, "healthy": false, "installed": false},
               "old": {"key": "old", "title": "Old", "context": 32768, "served_id": "o", "running": false, "healthy": false}}}
            """, SwapperStatus.Json)!;
        Assert.Equal("2.0.0", status.Version);
        Assert.Equal("amd-strix-halo", status.Platform);
        Assert.Equal("evo-x2", status.Hostname);
        var standard = status.AllModels["standard"];
        Assert.True(standard.IsInstalled);
        Assert.Equal("qwen3.8-27b", standard.Family);
        Assert.Equal(220.6, standard.Perf!.DecodeConcurrent);
        Assert.Equal(8, standard.Perf.Concurrency);
        Assert.Equal(27.1, standard.Measured!.Decode);
        Assert.False(status.AllModels["big"].IsInstalled);
        Assert.True(status.AllModels["old"].IsInstalled); // an older Swapper doesn't say: installed
        Assert.Equal(["old", "standard"], status.Installed.Select(m => m.Key).Order());
        Assert.Equal("Strix Halo", SwapperPlatforms.Label(status.Platform));
        Assert.Equal("DGX Spark", SwapperPlatforms.Label("nvidia-gb10"));
        Assert.Null(SwapperPlatforms.Label("unknown"));
    }

    [Fact]
    public void CatalogModelsSayWhatInstallingThemTakesAndWhichOneIsRecommended()
    {
        var status = JsonSerializer.Deserialize<SwapperStatus>("""
            {"active": null, "busy": false,
             "models": {
               "tf27b": {"key": "tf27b", "title": "Qwen3.8 27B", "context": 262144, "served_id": "a", "running": false, "healthy": false,
                         "order": 1, "installed": false, "recommended": true, "eta": "about a minute", "install_detail": "not downloaded",
                         "install": {"size_gb": 60, "time": "15–30 minutes", "requires": ["git", "docker"]}},
               "standard": {"key": "standard", "title": "Qwen3.8 27B (SGLang)", "context": 262144, "served_id": "b", "running": false,
                            "healthy": false, "order": 4, "installed": true, "recommended": false, "superseded_by": "tf27b", "install": null}},
             "install_job": {"id": "i1", "target": "install:model:tf27b", "state": "running", "started": 1, "steps": []}}
            """, SwapperStatus.Json)!;
        var tf = status.AllModels["tf27b"];
        Assert.True(tf.IsRecommended);
        Assert.Equal("60 GB · 15–30 minutes", tf.InstallSummary);
        Assert.Equal("about a minute", tf.Eta);
        Assert.Equal("not downloaded", tf.InstallDetail);
        Assert.Null(status.AllModels["standard"].InstallSummary);
        Assert.Equal("tf27b", status.AllModels["standard"].SupersededBy);
        // The pick: recommended and installed first; here only an uninstalled one is recommended.
        Assert.Equal("tf27b", status.Recommended!.Key);
        Assert.Equal("tf27b", status.Installing);
        Assert.Equal("tf27b", SwapperJob.ModelOf("install:model:tf27b"));
        Assert.Null(SwapperJob.ModelOf("install:docker"));
    }

    [Fact]
    public void SpeedPrefersWhatWasMeasuredHereAndNeverPassesAnEstimateOffAsMeasured()
    {
        static SwapperModel M(SwapperPerf? perf, SwapperMeasured? measured) =>
            new("k", "T", null, null, 1, null, "s", null, false, false, null, Perf: perf, Measured: measured);
        Assert.Equal("27 tok/s measured here", M(new SwapperPerf(58.3, null, null, null, null, false), new SwapperMeasured(27.1, 1)).Speed());
        Assert.Equal("~58 tok/s", M(new SwapperPerf(58.3, null, null, null, null, false), null).Speed());
        Assert.Equal("~58 tok/s", M(new SwapperPerf(58.3, null, null, null, null, null), new SwapperMeasured(null, null)).Speed());
        Assert.Equal("≈35–50 tok/s (estimate)", M(new SwapperPerf(42, null, null, null, null, true), null).Speed());
        Assert.Equal("≈30–50 tok/s (estimate)", M(new SwapperPerf(40, null, null, null, null, true, "30–50"), null).Speed());
        Assert.Null(M(new SwapperPerf(42, null, null, null, null, true), null).Speed(includeEstimate: false));
        Assert.Null(M(null, null).Speed());
    }

    // MARK: - The client with an app token

    [Fact]
    public async Task ATokenIsSentOnEveryRequestAndNoLoginHappens()
    {
        await using var swapper = new FakeSwapper2();
        using var client = SparkSwapperClient.WithToken(swapper.Url, FakeSwapper2.Token, swapper.Fingerprint);
        var status = await client.StatusAsync();
        Assert.Equal("standard", status.Active);
        Assert.Equal("2.0.0", status.Version);
        Assert.Equal("27 tok/s measured here", status.ActiveModel!.Speed());
        var connect = await client.ConnectInfoAsync();
        Assert.Equal("DGX Spark (spark-3f2a)", connect.Name);
        Assert.Equal("https://127.0.0.1:11443/v1", connect.ApiBase);
        Assert.Equal(swapper.Fingerprint, connect.Fingerprint);
        Assert.Equal(262144, connect.Context);
        var session = await client.SessionAsync();
        Assert.Equal("pat", session.User);
        Assert.Equal("nvidia-gb10", session.Platform);

        Assert.DoesNotContain(swapper.Server.Requests, r => r.Path == "/api/login");
        Assert.All(swapper.Server.Requests, r => Assert.Equal($"Bearer {FakeSwapper2.Token}", r.Headers["Authorization"]));
        Assert.All(swapper.Server.Requests, r => Assert.Equal("1", r.Headers["X-Swapper"]));
    }

    [Fact]
    public async Task ARevokedTokenSaysToPasteANewCode()
    {
        await using var swapper = new FakeSwapper2 { Revoked = true };
        using var client = SparkSwapperClient.WithToken(swapper.Url, FakeSwapper2.Token, swapper.Fingerprint);
        var error = await Assert.ThrowsAsync<SwapperException>(() => client.StatusAsync());
        Assert.Equal(SwapperErrorKind.Revoked, error.Kind);
        Assert.Equal("The Swapper no longer accepts this app's connection (it was revoked or the Swapper was reset). Paste a new connection code.", error.Message);
        Assert.DoesNotContain(swapper.Server.Requests, r => r.Path == "/api/login");
    }

    [Fact]
    public async Task ThePinOnlyCheckAcceptsExactlyTheCodesCertificate()
    {
        await using var swapper = new FakeSwapper2();
        using (var right = SparkSwapperClient.WithToken(swapper.Url, FakeSwapper2.Token, swapper.Fingerprint, pinOnly: true))
            Assert.Equal("standard", (await right.StatusAsync()).Active);

        using var wrong = SparkSwapperClient.WithToken(swapper.Url, FakeSwapper2.Token, Fingerprint, pinOnly: true);
        var error = await Assert.ThrowsAsync<SwapperException>(() => wrong.StatusAsync());
        Assert.Equal(SwapperErrorKind.UntrustedCertificate, error.Kind);
        Assert.Equal(swapper.Fingerprint, error.Fingerprint);
    }

    [Fact]
    public async Task ALoginGetsATokenFromASwapper2AndNothingFromAnOlderOne()
    {
        await using var swapper = new FakeSwapper2();
        using (var client = new SparkSwapperClient(swapper.Url, "pat", "correct horse", swapper.Fingerprint))
        {
            var grant = await client.CreateTokenAsync("DSH on TESTPC");
            Assert.NotNull(grant);
            Assert.Equal(FakeSwapper2.Token, grant.Token);
            Assert.Equal("1a2b3c4d", grant.Id);
            Assert.Equal("https://127.0.0.1:11443/v1", grant.Connect!.ApiBase);
            var post = swapper.Server.Requests.Single(r => r.Path == "/api/tokens/create");
            Assert.Contains("\"label\":\"DSH on TESTPC\"", post.Body);
            Assert.Contains(swapper.Server.Requests, r => r.Path == "/api/login");
        }

        await using var old = new FakeSwapper2(old: true);
        using var legacy = new SparkSwapperClient(old.Url, "pat", "correct horse", old.Fingerprint);
        Assert.Null(await legacy.CreateTokenAsync("DSH on TESTPC"));
        Assert.Equal("vllm-local", (await legacy.CredentialsAsync()).ApiKey); // the password route still works
    }

    [Fact]
    public async Task AModelThatIsntInstalledCanBeInstalledAndIsntSwitchedTo()
    {
        await using var swapper = new FakeSwapper2();
        using var client = SparkSwapperClient.WithToken(swapper.Url, FakeSwapper2.Token, swapper.Fingerprint);

        var refused = await Assert.ThrowsAsync<SwapperException>(() => client.StartSwapAsync("flash"));
        Assert.Equal(SwapperErrorKind.NotInstalled, refused.Kind);
        Assert.Equal("Qwen3.8 Flash isn't installed yet. Install it first.", refused.Detail);

        var job = await client.InstallModelAsync("flash");
        Assert.Equal("install:model:flash", job!.Target);
        Assert.True(job.IsRunning);
        var post = swapper.Server.Requests.Single(r => r.Path == "/api/models/install");
        Assert.Contains("\"key\":\"flash\"", post.Body);
        Assert.Equal("1", post.Headers["X-Swapper"]);

        // Another install while one runs is refused with a 409.
        var busy = await Assert.ThrowsAsync<SwapperException>(() => client.InstallModelAsync("flash"));
        Assert.Equal(409, busy.StatusCode);

        SwapperJob? polled;
        do polled = await client.ProvisioningJobAsync();
        while (polled is { IsRunning: true });
        Assert.True(polled!.IsDone);
    }

    [Fact]
    public void TokenLabelsNameTheComputer() => Assert.Equal("DSH on TESTPC", SwapperAddress.TokenLabel("TESTPC"));

    // MARK: - Addresses

    [Theory]
    [InlineData("192.168.1.42", "https://192.168.1.42:8999")]
    [InlineData("  spark-3f2a.local ", "https://spark-3f2a.local:8999")]
    [InlineData("evo-x2", "https://evo-x2:8999")]
    [InlineData("https://192.168.1.42:8999/", "https://192.168.1.42:8999")]
    [InlineData("https://192.168.1.42:8999/settings?x=1", "https://192.168.1.42:8999")]
    [InlineData("http://192.168.1.42:8999", "https://192.168.1.42:8999")]
    [InlineData("192.168.1.42:9443", "https://192.168.1.42:9443")]
    [InlineData("https://box.lan:443", "https://box.lan:443")]
    [InlineData("https://box.lan", "https://box.lan:8999")]
    [InlineData("[fe80::1]", "https://[fe80::1]:8999")]
    public void AnAddressBecomesTheSwappersUrl(string typed, string expected) =>
        Assert.Equal(expected, SwapperAddress.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://box")]
    public void SomethingThatIsntAnAddressIsRefused(string typed) => Assert.Null(SwapperAddress.Normalize(typed));

    // MARK: - Moving the single Spark into the connections list

    private static readonly ProviderProfile[] Routes =
    [
        new(ProviderKind.OpenAICompat, "DGX Spark", "https://192.168.1.42:11443/v1", "qwen3.8-27b"),
        new(ProviderKind.OpenAICompat, "Spark plain", "http://192.168.1.42:8002/v1", "qwen3.8-27b"),
        new(ProviderKind.Ollama, "Ollama", "http://127.0.0.1:11434/v1", "qwen3:8b"),
    ];

    [Fact]
    public void TheOldSparkSetupBecomesTheFirstConnection()
    {
        var connection = SwapperConnections.MigrateLegacy("https://192.168.1.42:8999/", "pat", Fingerprint, hasPassword: true, Routes);
        Assert.NotNull(connection);
        Assert.Equal("DGX Spark", connection.Name);
        Assert.Equal("https://192.168.1.42:8999", connection.Url);
        Assert.Equal(SwapperAuth.Password, connection.Auth);
        Assert.Equal("pat", connection.Username);
        Assert.Equal(Fingerprint, connection.PinnedFingerprint);
        Assert.Equal("https://192.168.1.42:11443/v1", connection.ApiBase);
        Assert.Equal(["DGX Spark|qwen3.8-27b", "Spark plain|qwen3.8-27b"], connection.RouteIds);
        Assert.True(connection.Serves(Routes[0]));
        Assert.True(connection.Serves(Routes[1]));
        Assert.False(connection.Serves(Routes[2]));
        Assert.Equal($"DSH/swapper/{connection.Id}", connection.SecretTarget);

        // Saved and read back, it is the same connection (the secret is never in it).
        var json = JsonSerializer.Serialize(connection, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"auth\":\"password\"", json);
        Assert.DoesNotContain("correct horse", json);
        var back = JsonSerializer.Deserialize<SwapperConnection>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(connection.Id, back.Id);
        Assert.Equal(connection.RouteIds, back.RouteIds);
        Assert.Equal(SwapperAuth.Password, back.Auth);
    }

    [Fact]
    public void AnAddressTheAppOnlyGuessedIsNotASetup()
    {
        // Older versions filled in the address from the model route; with no login, nothing was set up.
        Assert.Null(SwapperConnections.MigrateLegacy("https://192.168.1.42:8999", "", null, hasPassword: false, Routes));
        Assert.Null(SwapperConnections.MigrateLegacy("", "pat", null, hasPassword: true, Routes));
        Assert.Null(SwapperConnections.MigrateLegacy(null, null, null, hasPassword: false, Routes));
        // A pinned certificate (or a username, or a password) means the user was there.
        Assert.NotNull(SwapperConnections.MigrateLegacy("https://192.168.1.42:8999", "", Fingerprint, hasPassword: false, Routes));
    }

    [Fact]
    public void RoutesMatchTheirBoxByAddress()
    {
        var box = new SwapperConnection { ApiBase = "https://192.168.1.42:11443/v1" };
        Assert.True(box.Serves(new ProviderProfile(ProviderKind.OpenAICompat, "x", "https://192.168.1.42:11443/v1/", "m")));
        Assert.True(box.Serves(new ProviderProfile(ProviderKind.OpenAICompat, "x", "HTTPS://192.168.1.42:11443/v1", "m")));
        Assert.False(box.Serves(new ProviderProfile(ProviderKind.OpenAICompat, "x", "https://192.168.1.43:11443/v1", "m")));
        Assert.False(box.Serves(new ProviderProfile(ProviderKind.OpenAICompat, "x", "https://192.168.1.42:11443/v2", "m")));
    }

    // MARK: - /swap with several boxes

    private static readonly (string Name, string? Hostname)[] Boxes = [("DGX Spark (spark-3f2a)", "spark-3f2a"), ("Strix Halo (evo-x2)", "evo-x2")];

    [Fact]
    public void SwapPicksTheBoxBehindThePrimaryElseTheOnlyBoxElseAsks()
    {
        Assert.Equal(new SwapTarget(1, "flash", false), SwapTargets.Resolve("flash", Boxes, current: 1));
        Assert.Equal(new SwapTarget(0, "flash", false), SwapTargets.Resolve("flash", [Boxes[0]], current: null));
        Assert.Equal(new SwapTarget(null, "flash", true), SwapTargets.Resolve("flash", Boxes, current: null));
    }

    [Theory]
    [InlineData("strix flash", 1, "flash")]
    [InlineData("Strix Halo (evo-x2) qwen3.8 27b", 1, "qwen3.8 27b")]
    [InlineData("evo flash", 1, "flash")]
    [InlineData("DGX qwen", 0, "qwen")]
    [InlineData("spark-3f2a flash next", 0, "flash next")]
    public void SwapCanNameTheBoxByPrefix(string argument, int box, string model) =>
        Assert.Equal(new SwapTarget(box, model, false), SwapTargets.Resolve(argument, Boxes, current: null));

    // MARK: - The HTTPS front Spark Swapper 2.0 writes itself

    [Fact]
    public void TheFrontScriptLeavesTheSwappersOwnFrontAlone()
    {
        var script = SparkInstaller.ModelFrontScript;
        var check = script.IndexOf("Written by Spark Swapper", StringComparison.Ordinal);
        Assert.True(check > 0);
        Assert.Contains("Spark Swapper manages the HTTPS front.", script);
        // The check comes before anything is installed or written.
        Assert.True(check < script.IndexOf("apt-get", StringComparison.Ordinal));
        Assert.True(check < script.IndexOf("cat > \"$CONF\"", StringComparison.Ordinal));
    }
}
