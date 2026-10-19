using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The privacy shield end to end: what a model that isn't private receives, and what the app gets back.</summary>
public sealed class PrivacyShieldTests
{
    private static readonly ProviderProfile Kimi = new(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.ai/coding/v1", "kimi-for-coding");
    private static readonly ProviderProfile Spark = new(ProviderKind.OpenAICompat, "Spark", "https://192.168.1.42:11443/v1", "qwen");

    private static ShieldOptions Options(Action<ShieldReport>? report = null, IReadOnlyList<string>? terms = null, IReadOnlyList<string>? vault = null) =>
        new() { Report = report, CustomTerms = terms ?? [], VaultSecrets = vault ?? [] };

    private static LlmRequest Request(string system, params LlmMessage[] messages) => new(system, messages, [], "kimi-for-coding");

    private static async Task<(List<LlmStreamEvent> Events, FakeProviderClient Inner)> Run(LlmRequest request, Func<LlmRequest, FakeStream> reply,
                                                                                         ShieldOptions? options = null)
    {
        var inner = new FakeProviderClient(Kimi, (r, _) => reply(r));
        var shielded = new ShieldedClient(inner, options ?? Options());
        var events = new List<LlmStreamEvent>();
        await foreach (var ev in shielded.StreamAsync(request)) events.Add(ev);
        return (events, inner);
    }

    private static string TextOf(IEnumerable<LlmStreamEvent> events) => string.Concat(events.OfType<LlmStreamEvent.Text>().Select(t => t.Delta));

    private static string AllWireText(LlmRequest sent) =>
        sent.SystemPrompt + "\n" + string.Join("\n", sent.Messages.Select(m => m.Content + string.Join("", (m.ToolCalls ?? []).Select(c => c.Arguments))));

    [Fact]
    public void TextForTheNotebookIsMaskedTheSameWayEveryTime()
    {
        // Vault values only (the shield off): an email stays, the secret goes.
        var vaultOnly = new ShieldOptions { Categories = SensitiveCategories.None, VaultSecrets = ["hunter2-very-secret"] };
        const string prompt = "Log in as pat@example.com with hunter2-very-secret and fix the build";
        Assert.Equal("Log in as pat@example.com with ⟦SECRET_1⟧ and fix the build", OutgoingText.Masked(prompt, vaultOnly));
        // The shield on: the email goes too.
        var shield = Options(vault: ["hunter2-very-secret"]);
        var masked = OutgoingText.Masked(prompt, shield);
        Assert.DoesNotContain("pat@example.com", masked);
        Assert.DoesNotContain("hunter2-very-secret", masked);
        // The start and the ending of a prompt are pushed separately: both must read the same.
        Assert.Equal(masked, OutgoingText.Masked(prompt, shield));
        Assert.Equal(prompt, OutgoingText.Masked(prompt, null));
    }

    [Fact]
    public void APrivateRouteIsNeverWrapped()
    {
        Assert.IsNotType<ShieldedClient>(ProviderClients.Create(Spark, shield: Options()));
        Assert.IsType<ShieldedClient>(ProviderClients.Create(Kimi, shield: Options()));
        // The master switch off: nothing is wrapped.
        Assert.IsNotType<ShieldedClient>(ProviderClients.Create(Kimi, shield: new ShieldOptions { Enabled = false }));
        Assert.IsNotType<ShieldedClient>(ProviderClients.Create(Kimi));
        // A hosted route marked private by the user is trusted as such.
        Assert.IsNotType<ShieldedClient>(ProviderClients.Create(Kimi with { IsPrivate = true }, shield: Options()));
    }

    [Fact]
    public async Task TheModelSeesPlaceholdersAndTheAppSeesRealValues()
    {
        var request = Request("You help Pat (pat@example.com).",
            LlmMessage.User("Email pat@example.com and bob@example.org, key sk-" + new string('a', 24) + ", call +14155550123."));
        var (events, inner) = await Run(request, sent =>
        {
            var text = AllWireText(sent);
            Assert.DoesNotContain("pat@example.com", text);
            Assert.DoesNotContain("bob@example.org", text);
            Assert.DoesNotContain("sk-aaaa", text);
            Assert.DoesNotContain("+14155550123", text);
            // Numbered by first appearance, the system prompt first; the same value keeps its placeholder.
            Assert.Equal("You help Pat (\u27E6EMAIL_1\u27E7).\n\n" + ShieldedClient.PromptNote, sent.SystemPrompt);
            Assert.Equal("Email \u27E6EMAIL_1\u27E7 and \u27E6EMAIL_2\u27E7, key \u27E6SECRET_1\u27E7, call \u27E6PHONE_1\u27E7.", sent.Messages[0].Content);
            return FakeProviderClient.Text("Sent to \u27E6EMAIL_2\u27E7 using \u27E6SECRET_1\u27E7.");
        });
        Assert.Single(inner.Requests);
        Assert.Equal("Sent to bob@example.org using sk-" + new string('a', 24) + ".", TextOf(events));
    }

    [Fact]
    public void MaskingThenRestoringGivesBackExactlyWhatWasThere()
    {
        var detector = new SensitiveDetector(SensitiveCategories.All, ["Acme"], ["vault-value-9"]);
        var map = new ShieldMap();
        var original = "Acme's admin a.b@c.io, 10.0.0.7, card 4111 1111 1111 1111, SSN 078-05-1120, token=abcdef123456, vault-value-9, 😀 done";
        var masked = map.Mask(original, detector);
        Assert.DoesNotContain("a.b@c.io", masked);
        Assert.DoesNotContain("4111", masked);
        Assert.DoesNotContain("vault-value-9", masked);
        Assert.Equal(original, map.Restore(masked));
    }

    [Fact]
    public async Task NumberingStaysTheSameWhenTheConversationGrows()
    {
        var first = Request("sys", LlmMessage.User("Write to ann@x.io"));
        var second = Request("sys", LlmMessage.User("Write to ann@x.io"), LlmMessage.Assistant("Done, wrote to ann@x.io."),
            LlmMessage.User("Now also bo@y.io, then ann@x.io again"));
        var seen = new List<LlmRequest>();
        await Run(first, r => { seen.Add(r); return FakeProviderClient.Text("ok"); });
        await Run(second, r => { seen.Add(r); return FakeProviderClient.Text("ok"); });
        Assert.Equal("Write to \u27E6EMAIL_1\u27E7", seen[0].Messages[0].Content);
        // The second request starts with exactly what the first sent (so a prompt cache still matches it).
        Assert.Equal(seen[0].SystemPrompt, seen[1].SystemPrompt);
        Assert.Equal(seen[0].Messages[0].Content, seen[1].Messages[0].Content);
        Assert.Equal("Done, wrote to \u27E6EMAIL_1\u27E7.", seen[1].Messages[1].Content);
        Assert.Equal("Now also \u27E6EMAIL_2\u27E7, then \u27E6EMAIL_1\u27E7 again", seen[1].Messages[2].Content);
    }

    [Fact]
    public async Task APlaceholderSplitAcrossDeltasIsRestoredWhole()
    {
        var request = Request("sys", LlmMessage.User("mail ann@x.io and bo@y.io"));
        foreach (var pieces in new[]
                 {
                     new[] { "Hi \u27E6EMA", "IL_1\u27E7!" },
                     new[] { "Hi \u27E6", "EMAIL_", "1\u27E7!" },
                     new[] { "Hi ", "\u27E6EMAIL_1", "\u27E7", "!" },
                 })
        {
            var (events, _) = await Run(request, _ => FakeProviderClient.Text(pieces));
            Assert.Equal("Hi ann@x.io!", TextOf(events));
            Assert.DoesNotContain(events.OfType<LlmStreamEvent.Text>(), t => t.Delta.Contains('\u27E6'));
        }
        // A bracket that never becomes a placeholder isn't held back for ever.
        var (plain, _) = await Run(request, _ => FakeProviderClient.Text("a \u27E6 b", " and \u27E6EMAIL"));
        Assert.Equal("a \u27E6 b and \u27E6EMAIL", TextOf(plain));
    }

    [Fact]
    public async Task UnknownPlaceholdersAreLeftAsTheyAre()
    {
        var (events, _) = await Run(Request("sys", LlmMessage.User("mail ann@x.io")),
            _ => FakeProviderClient.Text("\u27E6EMAIL_1\u27E7 and \u27E6EMAIL_7\u27E7 and \u27E6PHONE_1\u27E7"));
        Assert.Equal("ann@x.io and \u27E6EMAIL_7\u27E7 and \u27E6PHONE_1\u27E7", TextOf(events));
    }

    [Fact]
    public async Task ReasoningIsRestoredToo()
    {
        var (events, _) = await Run(Request("sys", LlmMessage.User("mail ann@x.io")),
            _ => new FakeStream([new LlmStreamEvent.Reasoning("I'll write to \u27E6EMA"), new LlmStreamEvent.Reasoning("IL_1\u27E7."),
                new LlmStreamEvent.Text("ok"), new LlmStreamEvent.Done([], "stop", null)]));
        Assert.Equal("I'll write to ann@x.io.", string.Concat(events.OfType<LlmStreamEvent.Reasoning>().Select(r => r.Delta)));
    }

    [Fact]
    public async Task ToolCallArgumentsGetRealValuesWithCorrectJsonEscaping()
    {
        var awkward = "He said \"hi\" \\ C:\\temp\nnext line\ttab 😀 é";
        var detector = new SensitiveDetector(SensitiveCategories.Default, ["re:(?s)He said.*é"]);
        Assert.Single(detector.Find(awkward));
        var request = Request("sys", LlmMessage.User(awkward));
        var (events, _) = await Run(request, sent =>
        {
            Assert.Equal("\u27E6TERM_1\u27E7", sent.Messages[0].Content);
            return new FakeStream([new LlmStreamEvent.Done([
                new ToolCall("c1", "write_file", "{\"file_path\":\"notes.txt\",\"content\":\"Quote: \u27E6TERM_1\u27E7 end\"}"),
                new ToolCall("c2", "write_file", "{\"content\":\"\\u27e6TERM_1\\u27e7\"}"),
            ], "tool_calls", null)]);
        }, new ShieldOptions { CustomTerms = ["re:(?s)He said.*é"] });
        var calls = events.OfType<LlmStreamEvent.Done>().Single().Calls;
        Assert.Equal("Quote: " + awkward + " end", JsonNode.Parse(calls[0].Arguments)!["content"]!.GetValue<string>());
        Assert.Equal("notes.txt", JsonNode.Parse(calls[0].Arguments)!["file_path"]!.GetValue<string>());
        Assert.StartsWith("{\"file_path\":\"notes.txt\",", calls[0].Arguments); // the rest of the JSON is untouched
        Assert.Equal(awkward, JsonNode.Parse(calls[1].Arguments)!["content"]!.GetValue<string>()); // an escaped placeholder too
    }

    [Fact]
    public async Task PastToolCallsAreMaskedInsideTheirStringsOnly()
    {
        var past = LlmMessage.Assistant("", [new ToolCall("c1", "run_shell_command", """{"command":"mail ann@x.io","timeout":30,"email":true}""")]);
        var request = Request("sys", LlmMessage.User("go"), past, LlmMessage.ToolOutput("c1", "run_shell_command", "sent to ann@x.io"));
        await Run(request, sent =>
        {
            Assert.Equal("{\"command\":\"mail \u27E6EMAIL_1\u27E7\",\"timeout\":30,\"email\":true}", sent.Messages[1].ToolCalls![0].Arguments);
            Assert.Equal("sent to \u27E6EMAIL_1\u27E7", sent.Messages[2].Content);
            return FakeProviderClient.Text("ok");
        });
    }

    [Fact]
    public async Task ThePromptNoteIsAddedExactlyOnce()
    {
        // To the system prompt…
        await Run(Request("sys", LlmMessage.User("hi")), sent =>
        {
            Assert.Equal(1, Count(sent.SystemPrompt, ShieldedClient.PromptNote));
            return FakeProviderClient.Text("ok");
        });
        // …not again when it is already there…
        await Run(Request("sys\n\n" + ShieldedClient.PromptNote, LlmMessage.User("hi")), sent =>
        {
            Assert.Equal(1, Count(sent.SystemPrompt, ShieldedClient.PromptNote));
            return FakeProviderClient.Text("ok");
        });
        // …and to the first system message when there is no system prompt (a compacted transcript).
        await Run(new LlmRequest("", [LlmMessage.SystemText("summary"), LlmMessage.SystemText("more"), LlmMessage.User("hi")], [], "m"), sent =>
        {
            Assert.Equal("", sent.SystemPrompt);
            Assert.Equal("summary\n\n" + ShieldedClient.PromptNote, sent.Messages[0].Content);
            Assert.Equal("more", sent.Messages[1].Content);
            return FakeProviderClient.Text("ok");
        });
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public async Task TextAttachmentsAreMaskedAndPicturesAreReported()
    {
        ShieldReport? report = null;
        var file = new MessageAttachment(AttachmentKind.File, "contacts.csv", Encoding.UTF8.GetBytes("name,email\nAnn,ann@x.io\n"));
        var picture = new MessageAttachment(AttachmentKind.Image, "shot.png", [0x89, 0x50, 0x4E, 0x47, 0, 1, 2]);
        var binary = new MessageAttachment(AttachmentKind.File, "data.bin", [0, 159, 146, 150]);
        await Run(Request("sys", LlmMessage.User("see attached", [file, picture, binary])), sent =>
        {
            var attachments = sent.Messages[0].Attachments!;
            Assert.Equal("name,email\nAnn,\u27E6EMAIL_1\u27E7\n", Encoding.UTF8.GetString(attachments[0].Data));
            Assert.Same(picture, attachments[1]);
            Assert.Same(binary, attachments[2]);
            return FakeProviderClient.Text("ok");
        }, Options(r => report = r));
        Assert.Equal(2, report!.Unchecked); // the picture and the binary file went as they were
        Assert.Equal(1, report.Counts[SensitiveKind.Email]);
        Assert.EndsWith(" · 2 attachments not checked", report.Summary);
        Assert.Contains("Not checked, sent as they are: 2 pictures or files that aren't plain text.", report.Details);
    }

    [Fact]
    public async Task EachRequestReportsWhatItMaskedButNeverTheValues()
    {
        ShieldReport? report = null;
        await Run(Request("sys", LlmMessage.User("ann@x.io bo@y.io sk-" + new string('q', 24))), _ => FakeProviderClient.Text("ok"),
            Options(r => report = r));
        Assert.Equal("Kimi Code", report!.Route);
        Assert.Equal(3, report.Total);
        Assert.Equal("Masked 2 emails and 1 key before sending to Kimi Code", report.Summary);
        Assert.DoesNotContain("ann@x.io", report.Details);
        Assert.Contains("2 emails", report.Details);
        Assert.Equal(0, report.Unchecked);

        Assert.Null(ShieldReport.Describe(new Dictionary<SensitiveKind, int>(), "x"));
        Assert.Equal("Masked 1 key before sending to Kimi Code",
            ShieldReport.Describe(new Dictionary<SensitiveKind, int> { [SensitiveKind.Email] = 0, [SensitiveKind.Secret] = 1 }, "Kimi Code"));
        // Most first, then in the order the settings list them.
        Assert.Equal("Masked 2 card numbers, 1 phone number and 1 custom term before sending to X",
            ShieldReport.Describe(new Dictionary<SensitiveKind, int> { [SensitiveKind.Phone] = 1, [SensitiveKind.Card] = 2, [SensitiveKind.Term] = 1 }, "X"));
    }

    [Fact]
    public async Task VaultSecretsAreAlwaysMasked()
    {
        await Run(Request("sys", LlmMessage.User("deploy with Pr0d-Deploy-Key")), sent =>
        {
            Assert.Equal("deploy with \u27E6SECRET_1\u27E7", sent.Messages[0].Content);
            return FakeProviderClient.Text("ok");
        }, new ShieldOptions { Categories = SensitiveCategories.None, VaultSecrets = ["Pr0d-Deploy-Key"] });
    }

    [Fact]
    public void JsonStringsAreRewrittenInPlace()
    {
        Assert.Equal("""{"a" : "X", "n": 1, "b": ["Y", {"c": "z"}]}""",
            JsonStrings.Rewrite("""{"a" : "x", "n": 1, "b": ["y", {"c": "z"}]}""", s => s is "x" or "y" ? s.ToUpperInvariant() : s));
        Assert.Null(JsonStrings.Rewrite("{\"cut off", s => s));
        var same = """{"a":"b"}""";
        Assert.Same(same, JsonStrings.Rewrite(same, s => s));
    }
}
