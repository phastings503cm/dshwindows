using System.Text;
using System.Text.Json;

namespace Vision.Core.Tests;

/// <summary>Long files: the budget, the parts, the contents list the model is given, the store beside the chat and the
/// read_attachment tool that reads it back; and the large-paste thresholds.</summary>
[Collection(nameof(OcrEngineCollection))]
public sealed class AttachmentPrepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vision-attach-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Lines(int count, Func<int, string>? line = null) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => line?.Invoke(i) ?? $"line {i}: the quick brown fox jumps over the lazy dog"));

    // MARK: Budget

    [Theory]
    [InlineData(262_144, 1, 65_536, 8_000)]
    [InlineData(32_768, 1, 8_192, 2_048)]
    [InlineData(32_768, 3, 2_730, 1_365)]
    [InlineData(8_192, 1, 2_048, 1_000)]
    [InlineData(1_000, 1, 2_000, 1_000)]
    [InlineData(1_000_000, 4, 62_500, 8_000)]
    public void TheBudgetIsAQuarterOfTheWindowSharedAmongTheFiles(int window, int files, int inline, int part)
    {
        var budget = new AttachmentBudget(window, files);
        Assert.Equal(inline, budget.InlineTokens);
        Assert.Equal(part, budget.PartTokens);
        Assert.Equal(inline * 4, budget.InlineChars);
        Assert.Equal(part * 4, budget.PartChars);
    }

    // MARK: Split

    [Fact]
    public void PartsBreakAtLinesAndNumberThem()
    {
        var parts = AttachmentPrep.Split(Lines(100), 1_000);
        Assert.True(parts.Count > 5);
        Assert.Equal(1, parts[0].FirstLine);
        Assert.Equal(100, parts[^1].LastLine);
        for (var i = 1; i < parts.Count; i++) Assert.Equal(parts[i - 1].LastLine + 1, parts[i].FirstLine);
        Assert.All(parts, p => Assert.True(p.Text.Length <= 1_000));
        Assert.Equal(Lines(100), string.Join("\n", parts.Select(p => p.Text)));
    }

    [Fact]
    public void ANewPageStartsANewPartOnceThePartIsHalfFull()
    {
        var text = string.Join("\n", Enumerable.Range(1, 6).Select(p => $"--- Page {p} of 6 ---\n" + Lines(6)));
        var parts = AttachmentPrep.Split(text, 800);
        Assert.All(parts, p => Assert.StartsWith("--- Page ", p.Text));
        Assert.Equal(1, parts[0].FirstPage);
        Assert.Equal(6, parts[^1].LastPage);
        Assert.Equal("1. pages 1–2 · lines 1–14 · \"line 1: the quick brown fox jumps over the lazy dog\"",
            AttachmentPrep.ContentsLine(1, parts[0], DigestFormat.Pdf.Unit()));
    }

    [Fact]
    public void ASingleHugeLineIsCutIntoPiecesThatFit()
    {
        var line = new string('a', 2_500);
        var parts = AttachmentPrep.Split("before\n" + line + "\nafter", 1_000);
        Assert.Equal("before", parts[0].Text);
        Assert.All(parts, p => Assert.True(p.Text.Length <= 1_000));
        Assert.Equal("before" + line + "after", string.Concat(parts.Select(p => p.Text.Replace("\n", ""))));
        Assert.All(parts.Skip(1).Take(3), p => Assert.Equal(2, p.FirstLine));
    }

    [Fact]
    public void ASurrogatePairIsNeverCutInHalf()
    {
        var line = string.Concat(Enumerable.Repeat("😀", 600));
        foreach (var part in AttachmentPrep.Split(line, 101))
        {
            Assert.False(char.IsHighSurrogate(part.Text[^1]));
            Assert.False(char.IsLowSurrogate(part.Text[0]));
        }
    }

    // MARK: What the model is sent

    [Fact]
    public void AShortFileGoesWholeWithAHeaderAndAnEnd()
    {
        var digest = AttachmentReader.Read("notes.md", Encoding.UTF8.GetBytes("# Notes\n- one\n- two"), AttachmentKind.File);
        var (text, parts) = AttachmentPrep.InlineText("notes.md", digest, new AttachmentBudget(32_768), "notes.md");
        Assert.Equal(1, parts);
        Assert.Equal("[Attached file: notes.md — Markdown · 3 lines]\n# Notes\n- one\n- two\n[End of notes.md]", text);
    }

    [Fact]
    public void ALongFileGoesAsAContentsListAndItsFirstPartsWithHowToReadTheRest()
    {
        var digest = new AttachmentDigest { Format = DigestFormat.Log, Text = Lines(3_000), Lines = 3_000, Bytes = 160_000 };
        var budget = new AttachmentBudget(32_768);
        var (text, parts) = AttachmentPrep.InlineText("server.log", digest, budget, "server.log");
        Assert.True(parts > 10);
        Assert.StartsWith("[Attached file: server.log — Log · 3,000 lines]\nThis file is too long to send whole: about ", text);
        Assert.Contains($"Vision split it into {parts} parts and saved them with this chat. To read another part, call the read_attachment tool with " +
                        "{\"name\": \"server.log\", \"part\": N}; to find which parts mention something, call it with " +
                        "{\"name\": \"server.log\", \"search\": \"words to look for\"}.", text);
        Assert.Contains($"Contents ({parts} parts):\n1. lines 1–", text);
        Assert.Contains($"\n--- Part 1 of {parts} ---\nline 1: ", text);
        Assert.Matches(@"\[End of part \d+ of \d+ of server\.log — \d+ more parts: read them with read_attachment\]$", text);
        Assert.True(text.Length <= budget.InlineChars + 200);
    }

    [Fact]
    public void AFileVisionCouldNotReadSaysWhy()
    {
        var digest = AttachmentReader.Read("blob.bin", [0, 1, 2], AttachmentKind.File);
        Assert.Equal("[Attached file: blob.bin — File · 3 bytes]\nVision can't read this kind of file, so only its name is sent.",
            AttachmentPrep.InlineText("blob.bin", digest, new AttachmentBudget(32_768), null).Text);
    }

    // MARK: Prepare, store, read_attachment

    [Fact]
    public void PreparingSavesTheTextAndTheToolReadsItBackPartByPart()
    {
        var store = AttachmentStore.ForChat("chat-1", _root);
        var log = Lines(5_000, i => i == 4_321 ? "line 4321: ERROR disk full on /var/data" : $"line {i}: ok");
        var prepared = AttachmentPrep.Prepare([PastedText.Attachment(log)], window: 32_768, store);
        var attachment = Assert.Single(prepared.Attachments);
        var badge = Assert.Single(prepared.Badges);
        Assert.Equal("pasted-text.txt", badge.Stored);
        Assert.Equal("Pasted text · 5,000 lines · 67 KB", badge.Label);
        Assert.True(attachment.Parts > 1);
        Assert.Equal("", attachment.Digest!.Text);
        Assert.Equal(log.Length, attachment.Digest.TextLength);
        Assert.Contains("read_attachment", attachment.Text);

        var tool = new ReadAttachmentTool(store);
        var part2 = tool.Run("""{"name":"pasted-text.txt","part":2}""");
        Assert.StartsWith($"[pasted-text.txt — part 2 of {attachment.Parts} · lines ", part2);
        Assert.EndsWith($"[End of part 2 of {attachment.Parts} — next: part 3]", part2);
        var last = tool.Run($$"""{"name":"pasted-text.txt","part":{{attachment.Parts}}}""");
        Assert.Contains("line 5000: ok", last);
        Assert.EndsWith("that was the last part]", last);

        var search = tool.Run("""{"name":"pasted-text","search":"disk FULL"}""");
        Assert.StartsWith("“disk FULL” in pasted-text.txt: 1 match\n- part ", search);
        Assert.Contains(", line 4,321: line 4321: ERROR disk full on /var/data", search);

        Assert.StartsWith("Attachments saved with this chat:\n- pasted-text.txt — Pasted text · 5,000 lines · 67 KB · ", tool.Run("{}"));
        Assert.Equal($"Error: pasted-text.txt has {attachment.Parts} parts (1–{attachment.Parts}).", tool.Run("""{"name":"pasted-text.txt","part":999}"""));
        Assert.Equal("Error: no attachment named “other.pdf” is saved with this chat. Saved: pasted-text.txt.", tool.Run("""{"name":"other.pdf"}"""));
    }

    [Fact]
    public void ADifferentFileOfTheSameNameIsSavedUnderANumberedName()
    {
        var store = AttachmentStore.ForChat("chat-2", _root);
        var a = AttachmentReader.Read("notes.md", "# A"u8.ToArray(), AttachmentKind.File);
        var b = AttachmentReader.Read("notes.md", "# B"u8.ToArray(), AttachmentKind.File);
        Assert.Equal("notes.md", store.Save("notes.md", a, 4_000));
        Assert.Equal("notes (2).md", store.Save("notes.md", b, 4_000));
        Assert.Equal("notes.md", store.Save("notes.md", a, 4_000));
        Assert.Equal(2, store.Entries().Count);
        // The index is the JSON Vision for macOS writes.
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.Directory, "index.json")));
        var first = index.RootElement[0];
        Assert.Equal("notes.md", first.GetProperty("name").GetString());
        Assert.Equal("markdown", first.GetProperty("format").GetString());
        Assert.Equal(4_000, first.GetProperty("partChars").GetInt32());
        Assert.Equal(64, first.GetProperty("sha256").GetString()!.Length);
        store.RemoveAll();
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void PreparingReadsFilesNobodyReadAndBadgesSayWhatTheyAre()
    {
        var pdf = TestDocuments.Pdf(["Quarterly report", "North 120 units"], ["Totals", "200 units"]);
        var prepared = AttachmentPrep.Prepare([
            new MessageAttachment(AttachmentKind.File, "report.pdf", pdf),
            new MessageAttachment(AttachmentKind.File, "spec.docx", TestDocuments.Docx()),
            new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(3, 3)),
        ], window: 262_144, AttachmentStore.ForChat("chat-3", _root));
        Assert.Equal(["PDF · 2 pages", "Word · 8 lines", "Image"], prepared.Badges.Select(b => b.Label));
        Assert.Equal([false, false, true], prepared.Badges.Select(b => b.Image));
        Assert.StartsWith("[Attached file: report.pdf — PDF · 2 pages]\n--- Page 1 of 2 ---\nQuarterly report", prepared.Attachments[0].Text);
        Assert.Equal("[Attached: report.pdf — PDF · 2 pages. Its text is saved with this chat: read it with read_attachment {\"name\": \"report.pdf\"} if you need it again.]",
            prepared.Badges[0].ReplayNote);
        Assert.Equal("[Attached: shot.png — Image. Its content was sent with this message and isn't kept.]", prepared.Badges[2].ReplayNote);
    }

    [Fact]
    public void BadgesRoundTripThroughTheConversationLog()
    {
        var dir = Path.Combine(_root, "conversations");
        var log = new ConversationLog(dir);
        log.Upsert("c1", null, "Chat", null);
        log.RecordItem("c1", "user", "see attached", attachments: [new AttachmentBadge("report.pdf", "PDF · 2 pages", stored: "report.pdf")]);
        log.SetAttachments("c1", [new AttachmentBadge("report.pdf", "PDF · 2 pages · sent as text", stored: "report.pdf"), new AttachmentBadge("a.png", "Image · sent as image", image: true)]);
        var json = File.ReadAllText(Path.Combine(dir, "c1.json"));
        Assert.Contains("\"attachments\":[{\"name\":\"report.pdf\",\"label\":\"PDF · 2 pages · sent as text\",\"image\":false,\"stored\":\"report.pdf\"},{\"name\":\"a.png\",\"label\":\"Image · sent as image\",\"image\":true}]",
            json.Replace("\\u00B7", "·"));
        var reloaded = new ConversationLog(dir).LoadItems("c1").Single();
        Assert.Equal(2, reloaded.Attachments!.Count);
        Assert.Equal("report.pdf", reloaded.Attachments[0].Stored);
        Assert.True(reloaded.Attachments[1].Image);
    }

    // MARK: Large pastes

    [Fact]
    public void APasteBecomesAnAttachmentPastFourHundredLinesOrTwentyThousandCharacters()
    {
        Assert.False(PastedText.IsLarge(Lines(400, _ => "x")));
        Assert.True(PastedText.IsLarge(Lines(401, _ => "x")));
        Assert.True(PastedText.IsLarge(string.Join("\r\n", Enumerable.Repeat("x", 401))));
        Assert.False(PastedText.IsLarge(new string('x', 20_000)));
        Assert.True(PastedText.IsLarge(new string('x', 20_001)));
    }

    [Fact]
    public void APasteIsNamedApartFromTheOnesBeforeIt()
    {
        Assert.Equal("pasted-text.txt", PastedText.Name([]));
        Assert.Equal("pasted-text-2.txt", PastedText.Name(["pasted-text.txt"]));
        Assert.Equal("pasted-text-3.txt", PastedText.Name(["pasted-text.txt", "pasted-text-2.txt", "notes.md"]));
        var attachment = PastedText.Attachment("a\r\nb\r\nc", ["pasted-text.txt"]);
        Assert.Equal("pasted-text-2.txt", attachment.Name);
        Assert.Equal("a\nb\nc", Encoding.UTF8.GetString(attachment.Data));
        Assert.Equal("Pasted text · 3 lines · 5 bytes", attachment.Digest!.Summary);
    }

    [Fact]
    public void TheReportedPasteReadsAsTheChipSays()
    {
        // 10,214 lines, 412 KB: the paste Patrick reported.
        var text = Lines(10_214, i => $"{i:D6} " + new string('x', 33)); // 40 characters a line
        var attachment = PastedText.Attachment(text);
        Assert.Equal(10_214, attachment.Digest!.Lines);
        Assert.Equal("Pasted text · 10,214 lines · 409 KB", attachment.Digest.Summary);
        // A 262,144-token window: 32,000-character parts hold 780 lines of 41 → 14 parts.
        var budget = new AttachmentBudget(262_144);
        Assert.Equal(14, AttachmentPrep.PartCount(attachment.Digest, budget));
        Assert.Equal("sent as text in 14 parts", AttachmentWire.Delivery(attachment, AttachmentRoute.TextOnly, 14).Label());
    }

    [Fact]
    public void ALongMessageShowsItsFirstFortyLines()
    {
        Assert.Null(PastedText.Collapsed(Lines(40)));
        var collapsed = PastedText.Collapsed(Lines(10_214));
        Assert.NotNull(collapsed);
        Assert.Equal(10_214, collapsed.Value.Lines);
        Assert.Equal(Lines(40), collapsed.Value.Preview);
        var wide = PastedText.Collapsed(new string('y', 9_000));
        Assert.Equal(4_000, wide!.Value.Preview.Length);
        Assert.Equal(1, wide.Value.Lines);
    }
}
