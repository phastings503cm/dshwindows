using System.IO;
using System.Text;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>Attachments end to end through AgentHost against a fake OpenAI-compatible server: what the model is sent for
/// a Markdown file and a 10,000-line paste, the badges under the message and in its log row, read_attachment reading a
/// later part back, what a reopened chat tells the model, and the saved text going with a deleted chat.</summary>
public sealed class AttachmentTurnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-attach-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public AttachmentTurnTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AgentHost MakeHost(ConversationLog? log = null)
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        return new AgentHost(config, log ?? new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            AttachmentsRootForTesting = Path.Combine(_dir, "attachments"),
        };
    }

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static string Log10k() =>
        string.Join("\n", Enumerable.Range(1, 10_214).Select(i => $"{i:D6} " + (i == 9_000 ? "ERROR disk full on /var/data" : new string('x', 33))));

    [Fact]
    public void AFileAndALargePasteGoAsTextAndTheModelReadsAnotherPart() => UiThread.Run(async () =>
    {
        var project = Path.Combine(_dir, "main");
        Directory.CreateDirectory(project);
        var host = MakeHost();
        host.AdoptProject(project);
        try
        {
            _server.Reset((request, n) => n == 1
                ? new FakeReply.ToolCall("read_attachment", """{"name":"pasted-text.txt","search":"disk full"}""")
                : new FakeReply.Text("Line 9,000 says the disk is full."));
            var vm = host.NewSession(project);
            host.Send("What went wrong?", vm.Id, [
                new MessageAttachment(AttachmentKind.File, "notes.md", Encoding.UTF8.GetBytes("# Notes\n- the reserve job runs at 9\n")),
                PastedText.Attachment(Log10k()),
            ]);
            await WaitUntil("the turn", () => !vm.Running && _server.Seen.Count >= 2);

            // The model got text — the file whole, the paste as a contents list and its first parts — and the tool to read on.
            var first = _server.Seen[0];
            Assert.NotNull(first.LastUser);
            Assert.Contains("What went wrong?\n\n[Attached file: notes.md — Markdown · 2 lines]\n# Notes\n- the reserve job runs at 9\n\n[End of notes.md]", first.LastUser);
            Assert.Contains("[Attached file: pasted-text.txt — Pasted text · 10,214 lines · 409 KB]\nThis file is too long to send whole", first.LastUser);
            Assert.Contains("read_attachment", first.Tools);
            // The model searched it and got the line back as a tool result.
            var second = _server.Seen[1];
            var result = second.Messages.Last(m => m.Role == "tool").Content ?? "";
            Assert.StartsWith("“disk full” in pasted-text.txt: 1 match\n- part ", result);
            Assert.Contains("line 9,000: 009000 ERROR disk full on /var/data", result);
            Assert.Equal("Line 9,000 says the disk is full.", vm.Entries.OfType<MessageEntryVM>().Last(m => m.Role == MessageRole.Assistant).Text);

            // Under the message, and in its log row: what each attachment was and how it went.
            var message = vm.Entries.OfType<MessageEntryVM>().First(m => m.IsUser);
            Assert.Equal(["notes.md", "pasted-text.txt"], message.Attachments.Select(a => a.Name));
            Assert.Equal("Markdown · 2 lines · sent as text", message.Attachments[0].Label);
            Assert.Matches(@"^Pasted text · 10,214 lines · 409 KB · sent as text in \d+ parts$", message.Attachments[1].Label);
            Assert.Equal("pasted-text.txt", message.Attachments[1].Stored);
            var row = host.Log.LoadItems(vm.Id).First(r => r.Kind == "user");
            Assert.Equal(message.Attachments, row.Attachments);

            // A reopened chat tells the model where the text is.
            var replay = AgentHost.ReplayMessages(vm.Entries);
            Assert.Contains("[Attached: pasted-text.txt — ", replay.First(m => m.Role == Core.MessageRole.User).Content);
            Assert.Contains("read it with read_attachment {\"name\": \"pasted-text.txt\"}", replay.First(m => m.Role == Core.MessageRole.User).Content);

            // The saved text goes with the chat.
            var folder = host.AttachmentStoreFor(vm.Id).Directory;
            Assert.True(Directory.Exists(folder));
            host.DeleteSession(vm.Id);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    [Fact]
    public void ALongMessageShowsItsFirstLinesInTheTranscript()
    {
        var entry = new MessageEntryVM(MessageRole.User, Log10k());
        Assert.True(entry.IsLong);
        Assert.Equal(40, AttachmentFormat.LineCount(entry.ShownText));
        Assert.Equal("Show all 10,214 lines", entry.ShowAllLabel);
        var reply = new MessageEntryVM(MessageRole.Assistant, Log10k());
        Assert.False(reply.IsLong);
        Assert.Same(reply.Text, reply.ShownText);
        var shortOne = new MessageEntryVM(MessageRole.User, "hello");
        Assert.False(shortOne.IsLong);
        Assert.Equal("hello", shortOne.ShownText);
    }
}
