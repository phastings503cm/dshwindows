using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The shield's second revision: two passes per request (a value found once is masked wherever it turns up),
/// the stricter rule for <c>key = value</c>, attachments in other encodings and ones that can't be checked, a provider's
/// replayed state, and speed. DSH for macOS runs the same cases.</summary>
public sealed class ShieldV2Tests
{
    private static readonly ProviderProfile Kimi = new(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.ai/coding/v1", "k");

    private static LlmRequest Request(params LlmMessage[] messages) => new("sys", messages, [], "k");

    /// <summary>Everything a request puts on the wire, as text.</summary>
    private static string Wire(LlmRequest sent) =>
        sent.SystemPrompt + "\n" + string.Join("\n", sent.Messages.Select(m =>
            m.Content + " " + string.Join(" ", (m.ToolCalls ?? []).Select(c => c.Arguments)) + " " + (m.ProviderState ?? "")));

    private static async Task<(List<LlmStreamEvent> Events, IReadOnlyList<LlmRequest> Sent)> Run(LlmRequest request,
        Func<LlmRequest, int, FakeStream>? reply = null, ShieldOptions? options = null)
    {
        var inner = new FakeProviderClient(Kimi, reply ?? ((_, _) => FakeProviderClient.Text("ok")));
        var shielded = new ShieldedClient(inner, options ?? new ShieldOptions());
        var events = new List<LlmStreamEvent>();
        await foreach (var ev in shielded.StreamAsync(request)) events.Add(ev);
        return (events, inner.Requests);
    }

    // MARK: Two passes

    [Fact]
    public async Task AValueTheModelUsedIsMaskedWhereverItTurnsUpNext()
    {
        // Found only by its context ("password="), used by the model as a placeholder, put back by the app — and then,
        // in the next request, bare in the reply, in a command (with no word boundary: -phunter22x) and in a tool's output.
        var user = LlmMessage.User("Please connect to the db. My DB password=hunter22x and also call me at bob@example.com");
        var inner = new FakeProviderClient(Kimi, (_, n) => n == 1
            ? new FakeStream([
                new LlmStreamEvent.Text("I'll log in with ⟦SECRET_1⟧ now."),
                new LlmStreamEvent.Done([new ToolCall("c1", "run_shell_command", "{\"command\":\"mysql -u root -p⟦SECRET_1⟧ -h db\"}")], "tool_calls", null),
            ])
            : FakeProviderClient.Text("done"));
        var shielded = new ShieldedClient(inner, new ShieldOptions());

        var text = "";
        IReadOnlyList<ToolCall> calls = [];
        await foreach (var ev in shielded.StreamAsync(Request(user)))
        {
            if (ev is LlmStreamEvent.Text t) text += t.Delta;
            if (ev is LlmStreamEvent.Done d) calls = d.Calls;
        }
        Assert.Equal("I'll log in with hunter22x now.", text);
        Assert.Equal("mysql -u root -phunter22x -h db", JsonNode.Parse(calls[0].Arguments)!["command"]!.GetValue<string>());

        var second = Request(user, LlmMessage.Assistant(text, calls),
            LlmMessage.ToolOutput("c1", "run_shell_command", "Welcome to MySQL. Logged in with password hunter22x"));
        await foreach (var _ in shielded.StreamAsync(second)) { }

        var wire = inner.Requests[1];
        Assert.DoesNotContain("hunter22x", Wire(wire));
        Assert.DoesNotContain("bob@example.com", Wire(wire));
        Assert.Equal("I'll log in with ⟦SECRET_1⟧ now.", wire.Messages[1].Content);
        Assert.Equal("mysql -u root -p⟦SECRET_1⟧ -h db", JsonNode.Parse(wire.Messages[1].ToolCalls![0].Arguments)!["command"]!.GetValue<string>());
        Assert.Equal("Welcome to MySQL. Logged in with password ⟦SECRET_1⟧", wire.Messages[2].Content);
    }

    [Fact]
    public async Task AKeyBesideItsValueInAToolCallsJsonIsSeen()
    {
        // The reviewer's second probe: {"password": "…", "api_key": "…"} — the key and its value are separate strings.
        var call = new ToolCall("c1", "http_request",
            "{\"url\":\"https://x.example/login\",\"body\":{\"username\":\"bob\",\"password\":\"hunter22x\",\"api_key\":\"abcdef123456\"}}");
        var (_, sent) = await Run(Request(LlmMessage.User("log in"), LlmMessage.Assistant("", [call]), LlmMessage.ToolOutput("c1", "http_request", "200")));
        var arguments = sent[0].Messages[1].ToolCalls![0].Arguments;
        Assert.DoesNotContain("hunter22x", arguments);
        Assert.DoesNotContain("abcdef123456", arguments);
        var body = JsonNode.Parse(arguments)!["body"]!;
        Assert.Equal("⟦SECRET_1⟧", body["password"]!.GetValue<string>());
        Assert.Equal("⟦SECRET_2⟧", body["api_key"]!.GetValue<string>());
        Assert.Equal("bob", body["username"]!.GetValue<string>());
        Assert.Equal("https://x.example/login", JsonNode.Parse(arguments)!["url"]!.GetValue<string>());
    }

    [Fact]
    public void ShortValuesAreReplacedOnlyWhereTheyWereFound()
    {
        // "Al" is a custom term: replaced where the detector found it, never inside "Also" or "Alan".
        var detector = new SensitiveDetector(SensitiveCategories.Default, ["Al"]);
        var map = new ShieldMap();
        Assert.Equal("Ask ⟦TERM_1⟧. Also Alan.", map.Mask("Ask Al. Also Alan.", detector));
    }

    [Fact]
    public void NumberingFollowsWhereEachValueWasFirstFound()
    {
        var detector = new SensitiveDetector(SensitiveCategories.Default);
        var map = new ShieldMap();
        map.Collect("write to b@x.io", detector);
        map.Collect("then a@x.io, then b@x.io", detector);
        Assert.Equal("⟦EMAIL_2⟧ and ⟦EMAIL_1⟧", map.Replace("a@x.io and b@x.io"));
        Assert.Equal(2, map.Counts[SensitiveKind.Email]);
    }

    [Fact]
    public void TheLongestValueWinsWhereValuesOverlap()
    {
        var detector = new SensitiveDetector(SensitiveCategories.None, vaultSecrets: ["hunter22", "hunter22x-staging"]);
        var map = new ShieldMap();
        map.Collect("one hunter22 and one hunter22x-staging", detector);
        Assert.Equal("⟦SECRET_2⟧ ⟦SECRET_1⟧ x⟦SECRET_1⟧",
            map.Replace("hunter22x-staging hunter22 xhunter22"));
    }

    // MARK: Provider state

    [Fact]
    public async Task ReplayedStateHoldingASensitiveValueIsLeftOut()
    {
        // A turn made while the route was private kept its blocks with the real value in them; another made under the
        // shield holds only placeholders and goes back as it is.
        var leaky = LlmMessage.Assistant("ok") with { ProviderState = "[{\"reasoningContent\":{\"reasoningText\":{\"text\":\"mail ann@x.io\"}}}]" };
        var clean = LlmMessage.Assistant("ok") with { ProviderState = "[{\"text\":\"mail \\u27e6EMAIL_1\\u27e7\"}]" };
        var (_, sent) = await Run(Request(LlmMessage.User("mail ann@x.io"), leaky, LlmMessage.User("again"), clean, LlmMessage.User("thanks")));
        Assert.Null(sent[0].Messages[1].ProviderState);
        Assert.Equal(clean.ProviderState, sent[0].Messages[3].ProviderState);
        Assert.DoesNotContain("ann@x.io", Wire(sent[0]));
    }

    [Fact]
    public void ShortValuesCountInReplayedStateOnlyAsWords()
    {
        var map = new ShieldMap();
        map.Mask("ask Al", new SensitiveDetector(SensitiveCategories.Default, ["Al"]));
        Assert.False(map.Holds("Also, Alan"));
        Assert.True(map.Holds("then ask Al."));
    }

    // MARK: The assignment rule

    public static TheoryData<string> Code() =>
    [
        "let token: String; var password: String?; let apiKey: String",
        "const token = getToken();",
        "password: string;",
        "token = request.headers.get(\"x\")",
        "secret := os.Getenv(\"X\")",
        "token: accessToken",
        "password = user_password",
        "password=$DB_PASSWORD",
        "$env:API_KEY = $env:API_KEY",
        "set TOKEN=%TOKEN%",
        "{\"username\": \"string\", \"password\": \"string\"}",   // an API's documented example
        "PASSWORD=true",
    ];

    [Theory]
    [MemberData(nameof(Code))]
    public void CodeIsNotMistakenForSecrets(string text) =>
        Assert.DoesNotContain(new SensitiveDetector(SensitiveCategories.All).Find(text), m => m.Kind == SensitiveKind.Secret);

    public static TheoryData<string, string> Assignments() => new()
    {
        { "password: \"hunter2hunter2\"", "hunter2hunter2" },
        { "{\"password\": \"hunter2\"}", "hunter2" },
        { "PASSWORD=correcthorse", "correcthorse" },
        { "DB_PASSWORD=supersecret", "supersecret" },
        { "password: correcthorsebattery", "correcthorsebattery" },
        { "token: abc123def", "abc123def" },
        { "export GITHUB_TOKEN=gh-x", "gh-x" },                 // an env line: always a secret
        { "API_KEY='abc'", "abc" },                               // …without its quotes
        { "my password: hunter22x.", "hunter22x" },               // not the full stop
    };

    [Theory]
    [MemberData(nameof(Assignments))]
    public void AssignedSecretsAreFound(string text, string secret)
    {
        var found = new SensitiveDetector(SensitiveCategories.All).Find(text);
        Assert.Contains(found, m => m.Kind == SensitiveKind.Secret && text.Substring(m.Start, m.Length) == secret);
    }

    [Theory]
    [InlineData("String", false)]
    [InlineData("String?", false)]
    [InlineData("os.environ.get", false)]
    [InlineData("AccessToken", false)]
    [InlineData("accessToken", false)]
    [InlineData("user_password", false)]
    [InlineData("hunter22x", true)]       // a digit
    [InlineData("Tr0ub4dorX9", true)]     // reads like PascalCase only if identifiers may hold digits
    [InlineData("hunter-two", true)]      // a symbol
    [InlineData("correcthorse", true)]    // eight letters or more, one case
    [InlineData("CORRECTHORSE", true)]
    [InlineData("horsey", false)]         // a short word
    public void ABareValueIsASecretOnlyWhenItLooksLikeOne(string value, bool secret) =>
        Assert.Equal(secret, SensitiveDetector.IsBareSecret(value));

    // MARK: Attachments

    [Fact]
    public async Task TextInOtherEncodingsIsMaskedToo()
    {
        const string text = "contact: bob@example.com key=sk-proj-abcdefghijklmnopqrstuvwxyz012345";
        var utf16 = new MessageAttachment(AttachmentKind.File, "utf16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        var ansi = new MessageAttachment(AttachmentKind.File, "excel.csv", [.. Encoding.Latin1.GetBytes("Café; "), .. Encoding.ASCII.GetBytes(text)]);
        ShieldReport? report = null;
        var (_, sent) = await Run(Request(LlmMessage.User("see files", [utf16, ansi])), options: new ShieldOptions { Report = r => report = r });
        foreach (var attachment in sent[0].Messages[0].Attachments!)
        {
            var masked = Encoding.UTF8.GetString(attachment.Data);
            Assert.DoesNotContain("bob@example.com", masked);
            Assert.DoesNotContain("sk-proj-", masked);
            Assert.Contains("⟦EMAIL_1⟧", masked);
        }
        Assert.StartsWith("Café; contact:", Encoding.UTF8.GetString(sent[0].Messages[0].Attachments![1].Data));
        Assert.Equal(0, report!.Unchecked);
    }

    [Fact]
    public async Task FilesThatArentTextAreFlaggedNotChecked()
    {
        var pdf = new MessageAttachment(AttachmentKind.File, "statement.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nbob@example.com"));
        var docx = new MessageAttachment(AttachmentKind.File, "notes.docx", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        ShieldReport? report = null;
        var (_, sent) = await Run(Request(LlmMessage.User("mail ann@x.io", [pdf, docx])), options: new ShieldOptions { Report = r => report = r });
        Assert.Same(pdf, sent[0].Messages[0].Attachments![0]); // sent as it is
        Assert.Equal(2, report!.Unchecked);
        Assert.Equal("Masked 1 email before sending to Kimi Code · 2 attachments not checked", report.Summary);
        Assert.Equal("Pictures and files that aren't plain text are sent as they are — DSH can't check them for sensitive details.",
            ShieldReport.UncheckedNotice);
    }

    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData("data.json", true)]
    [InlineData("build.ps1", true)]
    [InlineData("noext", true)]
    [InlineData("report.pdf", false)]
    [InlineData("sheet.xlsx", false)]
    public void TextFilesAreReadAndOthersArent(string name, bool text) =>
        Assert.Equal(text, AttachmentText.Read(new MessageAttachment(AttachmentKind.File, name, Encoding.UTF8.GetBytes("a,b\n1,2"))) is not null);

    [Fact]
    public void ManyOverlappingMatchesResolveLikeOneAtATime()
    {
        var detector = new SensitiveDetector(SensitiveCategories.None, vaultSecrets: ["abcd", "bcdef", "cdefgh", "ghij"]);
        // Longest first: cdefgh, then abcd overlaps it (dropped), bcdef too, ghij too.
        var found = detector.Find("abcdefghij");
        Assert.Equal([(2, 6)], found.Select(m => (m.Start, m.Length)).ToList());
    }

    [Fact]
    public void TheMatcherFindsEveryOccurrence()
    {
        var matcher = new LiteralMatcher(["he", "she", "his", "hers"]);
        Assert.Equal([(1, 3), (2, 2), (2, 4)], matcher.Find("ushers").OrderBy(m => m.Start).ThenBy(m => m.Length).ToList());
        Assert.Empty(new LiteralMatcher([]).Find("anything"));
    }
}

/// <summary>The shield's speed: these measure the clock, so they run on their own (and keep their load off the rest).</summary>
[Collection(TimingSensitive.Name)]
public sealed class ShieldTimingTests
{
    [Fact]
    public void AnEightThousandRowSpreadsheetIsMaskedQuickly()
    {
        static string Rows(int count)
        {
            var csv = new StringBuilder();
            for (var i = 0; i < count; i++) csv.Append("row").Append(i).Append(",user").Append(i).Append("@example.com,+1555").Append(i.ToString("D7")).Append('\n');
            return csv.ToString();
        }
        var text = Rows(8_000);
        var detector = new SensitiveDetector(SensitiveCategories.Default);
        // Warm the code up first on a small sheet (the first run of anything is mostly the JIT compiling it), then take the best of
        // three, so a moment when the machine is busy with something else doesn't count against the shield.
        new ShieldMap().Mask(Rows(500), detector);
        var best = TimeSpan.MaxValue;
        ShieldMap map = null!;
        var masked = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var clock = Stopwatch.StartNew();
            map = new ShieldMap();
            masked = map.Mask(text, detector);
            clock.Stop();
            if (clock.Elapsed < best) best = clock.Elapsed;
        }
        Assert.True(best < TimeSpan.FromMilliseconds(500), $"took {best.TotalMilliseconds:0} ms at best");
        Assert.Equal(8_000, map.Counts[SensitiveKind.Email]);
        Assert.Equal(8_000, map.Counts[SensitiveKind.Phone]);
        Assert.DoesNotContain("@example.com", masked);
        Assert.Equal(text, map.Restore(masked));
    }

    [Fact]
    public void APatternThatTakesTooLongCostsOnlyItsOwnMatches()
    {
        // A custom expression with catastrophic backtracking gives up on this text; the term after it, the emails and the
        // keys are still found — and the slow expression is set aside from then on rather than costing the same again.
        var detector = new SensitiveDetector(SensitiveCategories.Default, ["re:^(a+)+$", "Acme"]);
        var text = new string('a', 40) + "! Acme: write to ann@x.io with sk-" + new string('k', 24);
        var found = detector.Find(text);
        Assert.Contains(found, m => m.Kind == SensitiveKind.Term && text.Substring(m.Start, m.Length) == "Acme");
        Assert.Contains(found, m => m.Kind == SensitiveKind.Email);
        Assert.Contains(found, m => m.Kind == SensitiveKind.Secret);
        var clock = Stopwatch.StartNew();
        Assert.Equal(found, detector.Find(text));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200), $"took {clock.ElapsedMilliseconds} ms");
    }
}
