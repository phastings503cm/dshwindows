using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>What each kind of route is sent for each kind of attachment, checked against the servers' own rules: vLLM's
/// content-part whitelist and llama.cpp's no-mmproj refusal, pinned from their sources (vllm/renderers/chat_utils/
/// chat_message.py and tools/server/server-common.cpp on their main branches, 2026-10-19).</summary>
[Collection(nameof(OcrEngineCollection))]
public sealed class AttachmentWireTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vision-wire-" + Guid.NewGuid().ToString("N"));

    public AttachmentWireTests() => VisionRejections.Shared.Reset();

    public void Dispose()
    {
        VisionRejections.Shared.Reset();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly ProviderProfile Spark = new(ProviderKind.OpenAICompat, "DGX Spark", "http://192.168.1.40:8002/v1", "Qwen3-Coder-30B");
    private static readonly ProviderProfile Ollama = new(ProviderKind.Ollama, "Ollama", "http://127.0.0.1:11434/v1", "qwen3:8b");
    private static readonly ProviderProfile Hosted = new(ProviderKind.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "qwen/qwen3-32b");
    private const string Claude = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    private const string Llama = "meta.llama3-3-70b-instruct-v1:0";

    private static ProviderProfile Bedrock(string model) =>
        new(ProviderKind.Bedrock, "Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com", model) { AwsRegion = "us-east-1", AwsProfile = "p" };

    /// <summary>vLLM's supported part types (MM_PARSER_MAP plus the parts it skips), and its error for anything else.</summary>
    private static readonly HashSet<string> VllmParts =
    [
        "text", "thinking", "input_text", "output_text", "input_image", "image_url", "image_embeds", "audio_embeds", "video_embeds",
        "prompt_embeds", "image_pil", "audio_url", "input_audio", "refusal", "video_url", "tool_reference",
    ];

    private static string VllmError(string type) =>
        $$"""{"object":"error","message":"Unsupported chat content part type: '{{type}}'. Supported types: {{string.Join(", ", VllmParts.Order())}}.","type":"BadRequestError","param":"type","code":400}""";

    /// <summary>The content parts of every user message in a chat-completions body.</summary>
    private static IEnumerable<JsonObject> Parts(string body) =>
        JsonNode.Parse(body)!["messages"]!.AsArray()
            .Where(m => m!["role"]!.GetValue<string>() == "user" && m["content"] is JsonArray)
            .SelectMany(m => m!["content"]!.AsArray().Select(p => p!.AsObject()));

    /// <summary>A vLLM server: refuses any part type outside its whitelist exactly as vLLM does; otherwise answers.</summary>
    private static FakeHttpHandler Vllm() => new(request =>
    {
        var body = request.Content!.ReadAsStringAsync().Result;
        var bad = Parts(body).Select(p => p["type"]!.GetValue<string>()).FirstOrDefault(t => !VllmParts.Contains(t));
        return bad is null
            ? FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""")
            : FakeHttpHandler.Json(VllmError(bad), HttpStatusCode.BadRequest);
    });

    private static async Task<List<LlmStreamEvent>> Send(IProviderClient client, params MessageAttachment[] attachments)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var ev in client.StreamAsync(new LlmRequest("sys", [LlmMessage.User("Summarise these", attachments)], [], client.Profile.Model)))
            events.Add(ev);
        return events;
    }

    private IReadOnlyList<MessageAttachment> Prepared(int window, params MessageAttachment[] attachments) =>
        AttachmentPrep.Prepare(attachments, window, AttachmentStore.ForChat("c", _root)).Attachments;

    private static MessageAttachment File(string name, byte[] data) => new(AttachmentKind.File, name, data);

    // MARK: The bug: a Markdown or PDF file to a self-hosted Qwen

    [Fact]
    public async Task TheOldFilePartIsWhatVllmRefuses()
    {
        // The request Vision 0.9 and DSH 0.8 sent for notes.md, replayed at the fake vLLM: refused as the real one does.
        var handler = Vllm();
        var oldBody = """{"model":"q","messages":[{"role":"user","content":[{"type":"text","text":"hi"},{"type":"file","file":{"filename":"notes.md","file_data":"data:application/octet-stream;base64,IyBOb3Rlcw=="}}]}],"stream":true}""";
        using var http = new HttpClient(handler);
        var response = await http.PostAsync("http://127.0.0.1:8002/v1/chat/completions", new StringContent(oldBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("{\"object\":\"error\",\"message\":\"Unsupported chat content part type: 'file'.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMarkdownAndAPdfGoToVllmAsOneTextMessage()
    {
        var handler = Vllm();
        var client = ProviderClients.Create(Spark, handler);
        var attachments = Prepared(262_144,
            File("notes.md", "# Notes\n- reserve expires after 15 min\n"u8.ToArray()),
            File("report.pdf", TestDocuments.Pdf(["Quarterly report", "North 120 units"], ["Totals", "200 units"])));
        var events = await Send(client, [.. attachments]);

        Assert.Equal("ok", string.Concat(events.OfType<LlmStreamEvent.Text>().Select(t => t.Delta)));
        var request = Assert.Single(handler.Requests);
        var content = JsonNode.Parse(request.Body)!["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!["content"]!;
        // All text: one string, which every server and chat template takes.
        var text = content.GetValue<string>();
        Assert.StartsWith("Summarise these\n\n[Attached file: notes.md — Markdown · 2 lines]\n# Notes\n- reserve expires after 15 min\n\n[End of notes.md]", text);
        Assert.Contains("[Attached file: report.pdf — PDF · 2 pages]\n--- Page 1 of 2 ---\nQuarterly report", text);
        Assert.Contains("--- Page 2 of 2 ---\nTotals", text);
        Assert.DoesNotContain("\"type\":\"file\"", request.Body);
        Assert.DoesNotContain("base64", request.Body);
    }

    [Theory]
    [InlineData("spec.docx")]
    [InlineData("deck.pptx")]
    [InlineData("totals.xlsx")]
    [InlineData("notes.md")]
    [InlineData("data.csv")]
    [InlineData("blob.bin")]
    public void NoAttachmentEverBecomesAFilePartOnAnyOpenAiCompatibleRoute(string name)
    {
        var data = Path.GetExtension(name) switch
        {
            ".docx" => TestDocuments.Docx(),
            ".pptx" => TestDocuments.Pptx(),
            ".xlsx" => TestDocuments.Xlsx(),
            ".bin" => new byte[] { 0, 1, 2, 3 },
            _ => "a,b\n1,2\n"u8.ToArray(),
        };
        foreach (var profile in new[] { Spark, Ollama, Hosted })
        {
            foreach (var attachment in new[] { File(name, data), Prepared(32_768, File(name, data))[0] })
            {
                var body = new OpenAiClient(profile).MakeBody(new LlmRequest("", [LlmMessage.User("x", [attachment])], [], profile.Model));
                Assert.DoesNotContain("\"file\"", body);
                Assert.Contains($"[Attached file: {name}", body);
            }
        }
    }

    [Fact]
    public async Task ALongPasteGoesAsItsContentsAndFirstPartsWithinTheBudget()
    {
        var handler = Vllm();
        var log = string.Join("\n", Enumerable.Range(1, 10_214).Select(i => $"09:00:{i % 60:00}.137 req {4711 + i} {i % 900} ms /reserve"));
        var attachments = Prepared(32_768, PastedText.Attachment(log));
        await Send(ProviderClients.Create(Spark, handler), [.. attachments]);
        var text = JsonNode.Parse(handler.Requests[0].Body)!["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("[Attached file: pasted-text.txt — Pasted text · 10,214 lines · ", text);
        Assert.Contains("call the read_attachment tool with {\"name\": \"pasted-text.txt\", \"part\": N}", text);
        // A quarter of a 32,768-token window, at four characters a token, plus the user's own words.
        Assert.True(text.Length < new AttachmentBudget(32_768).InlineChars + 100, $"{text.Length} characters sent");
    }

    // MARK: Pictures

    [Fact]
    public void APictureGoesAsAnImageOnlyWhereTheModelTakesImages()
    {
        var png = TestImages.Png(4, 4);
        AttachmentReader.Ocr = new FakeOcr("Error 5: access denied");
        try
        {
            var image = Prepared(32_768, new MessageAttachment(AttachmentKind.Image, "error.png", png))[0];
            var vision = new OpenAiClient(Spark).MakeBody(new LlmRequest("", [LlmMessage.User("why?", [image])], [], "q"));
            Assert.Contains($"data:image/png;base64,{Convert.ToBase64String(png)}", vision);
            var blind = new OpenAiClient(Spark with { Vision = false }).MakeBody(new LlmRequest("", [LlmMessage.User("why?", [image])], [], "q"));
            Assert.DoesNotContain("image_url", blind);
            var text = JsonNode.Parse(blind)!["messages"]![0]!["content"]!.GetValue<string>();
            Assert.Equal("why?\n\n[Image: error.png — this model can't take images; the text in it, read by OCR:]\nError 5: access denied", text);
        }
        finally
        {
            AttachmentReader.Ocr = null;
        }
    }

    [Fact]
    public void AScanGoesAsPageImagesToAVisionModelAndAsTextToTheRest()
    {
        var scan = Prepared(32_768, File("scan.pdf", TestDocuments.ScanPdf(3)))[0];
        Assert.Equal("sent as 3 page images", AttachmentWire.Delivery(scan, AttachmentRoute.OpenAi(Spark)).Label());
        var parts = Parts(new OpenAiClient(Spark).MakeBody(new LlmRequest("", [LlmMessage.User("read", [scan])], [], "q"))).ToList();
        Assert.Equal(["text", "text", "image_url", "image_url", "image_url"], parts.Select(p => p["type"]!.GetValue<string>()));
        Assert.Equal("[Attached file: scan.pdf — PDF · 3 pages · scanned — it has no text layer, so its pages follow as images]", parts[1]["text"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,iVBOR", parts[2]["image_url"]!["url"]!.GetValue<string>());

        // Without OCR on this machine, a model without vision gets the pages' markers and "(no text on this page)".
        Assert.Equal("sent as text", AttachmentWire.Delivery(scan, AttachmentRoute.OpenAi(Spark with { Vision = false })).Label());
        var blind = new OpenAiClient(Spark with { Vision = false }).MakeBody(new LlmRequest("", [LlmMessage.User("read", [scan])], [], "q"));
        Assert.Contains("--- Page 1 of 3 ---\\n(no text on this page)", blind);
    }

    [Fact]
    public async Task AServerThatTakesNoImagesIsAskedAgainWithTheTextAndRemembered()
    {
        // llama.cpp started without --mmproj, word for word.
        const string refusal = """{"error":{"code":500,"message":"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj","type":"server_error"}}""";
        var handler = new FakeHttpHandler(request =>
            request.Content!.ReadAsStringAsync().Result.Contains("image_url")
                ? FakeHttpHandler.Json(refusal, HttpStatusCode.InternalServerError)
                : FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"It says access denied."},"finish_reason":"stop"}]}"""));
        var profile = new ProviderProfile(ProviderKind.OpenAICompat, "llama.cpp", "http://127.0.0.1:8080/v1", "qwen3-8b");
        var image = new MessageAttachment(AttachmentKind.Image, "error.png", TestImages.Png(4, 4));
        var events = await Send(ProviderClients.Create(profile, handler), image);
        Assert.Equal("It says access denied.", string.Concat(events.OfType<LlmStreamEvent.Text>().Select(t => t.Delta)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("image_url", handler.Requests[0].Body);
        Assert.DoesNotContain("image_url", handler.Requests[1].Body);
        Assert.Contains("[Attached file: error.png", handler.Requests[1].Body);
        // Learned: the next request goes as text straight away.
        await Send(ProviderClients.Create(profile, handler), image);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("image_url", handler.Requests[2].Body);
        Assert.False(AttachmentRoute.OpenAi(profile).Vision);
    }

    [Theory]
    [InlineData("image input is not supported - hint: if this is unexpected, you may need to provide the mmproj", true)]
    [InlineData("Resolving modality 'image' requires a multimodal processor but none is available.", true)]
    [InlineData("At most 0 image(s) may be provided in one prompt.", true)]
    [InlineData("Unsupported chat content part type: 'file'. Supported types: text.", false)]
    [InlineData("This model's maximum context length is 8192 tokens", false)]
    public void ARefusalOfImagesIsRecognised(string body, bool noImages) => Assert.Equal(noImages, VisionRejections.SaysNoImages(body));

    // MARK: Bedrock

    [Fact]
    public void BedrockGetsAPdfAsADocumentAndEverythingElseAsText()
    {
        var pdf = TestDocuments.Pdf(["Quarterly report", "North 120 units"]);
        var attachments = Prepared(200_000, File("Q3 report (final).pdf", pdf), File("spec.docx", TestDocuments.Docx()),
            new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(2, 2)));
        var body = BedrockConverse.Request(new LlmRequest("sys", [LlmMessage.User("read these", attachments)], [], Claude), Bedrock(Claude));
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("read these", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("pdf", content[1]!["document"]!["format"]!.GetValue<string>());
        Assert.Equal("Q3 report (final)", content[1]!["document"]!["name"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(pdf), content[1]!["document"]!["source"]!["bytes"]!.GetValue<string>());
        Assert.StartsWith("[Attached file: spec.docx — Word · 8 lines]\n# Stock service", content[2]!["text"]!.GetValue<string>());
        Assert.Equal("png", content[3]!["image"]!["format"]!.GetValue<string>());
        Assert.Equal("sent as PDF document", AttachmentWire.Delivery(attachments[0], AttachmentRoute.Of(Bedrock(Claude))).Label());
    }

    [Fact]
    public void BedrockModelsWithoutDocumentsAndLongPdfsGetText()
    {
        var pdf = TestDocuments.Pdf(["Quarterly report", "North 120 units"]);
        var llama = BedrockConverse.Request(new LlmRequest("", [LlmMessage.User("read", Prepared(128_000, File("r.pdf", pdf)))], [], Llama), Bedrock(Llama));
        Assert.StartsWith("[Attached file: r.pdf — PDF · 1 page]", llama["messages"]![0]!["content"]![1]!["text"]!.GetValue<string>());

        // Long enough to go in parts: the parts go as text (a document would be sent whole, past the budget).
        var pages = Enumerable.Range(1, 40).Select(p => Enumerable.Range(1, 40).Select(l => $"Page {p} line {l}: lorem ipsum dolor sit amet").ToArray()).ToArray();
        var big = Prepared(8_192, File("big.pdf", TestDocuments.Pdf(pages)))[0];
        Assert.True(big.Parts > 1);
        Assert.StartsWith("sent as text in ", AttachmentWire.Delivery(big, AttachmentRoute.Of(Bedrock(Claude))).Label());
        var body = BedrockConverse.Request(new LlmRequest("", [LlmMessage.User("read", [big])], [], Claude), Bedrock(Claude));
        Assert.DoesNotContain("\"document\"", body.ToJsonString());
    }

    // MARK: The shield checks what is sent

    [Fact]
    public async Task TheShieldMasksTheTextReadOutOfAFileAndCountsOnlyWhatGoesAsBytes()
    {
        var kimi = new ProviderProfile(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.ai/coding/v1", "k");
        var inner = new FakeProviderClient(kimi, (_, _) => FakeProviderClient.Text("ok"));
        ShieldReport? report = null;
        var shielded = new ShieldedClient(inner, new ShieldOptions { Report = r => report = r });
        var notes = Prepared(32_768, File("contacts.md", "# Contacts\nWrite to jane.doe@example.com about the order.\n"u8.ToArray()))[0];
        // A PDF's text is only in what Vision read out of it: its bytes are no help to the shield.
        var pdf = Prepared(32_768, File("invoice.pdf", TestDocuments.Pdf(["Invoice 4711", "Bill to bob.smith@example.org"])))[0];
        var image = Prepared(32_768, new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(2, 2)))[0];
        await foreach (var _ in shielded.StreamAsync(new LlmRequest("sys", [LlmMessage.User("see", [notes, pdf, image])], [], "k"))) { }
        var sent = Assert.Single(inner.Requests).Messages.Single(m => m.Role == MessageRole.User).Attachments!;
        Assert.DoesNotContain("jane.doe@example.com", sent[0].Text);
        Assert.StartsWith("[Attached file: contacts.md — Markdown · 2 lines]\n# Contacts\nWrite to ⟦EMAIL_1⟧ about the order.", sent[0].Text);
        Assert.DoesNotContain("bob.smith", sent[1].Text);
        Assert.Contains("Bill to ⟦EMAIL_2⟧", sent[1].Text);
        // The files went as text (checked); only the picture's bytes go unchecked.
        Assert.Equal(1, report!.Unchecked);
        var body = new OpenAiClient(kimi).MakeBody(inner.Requests[0]);
        Assert.DoesNotContain("jane.doe", body);
        Assert.DoesNotContain("bob.smith", body);
    }
}
