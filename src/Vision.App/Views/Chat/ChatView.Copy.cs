using System.Windows;
using System.Windows.Input;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Chat;

// MARK: - Copy (10-19 #13: "I want to be able to copy existing prompts")
//
// Every message has a Copy button (shown while the pointer is over it), "Copy prompt" / "Copy reply" in its menu, and Ctrl+C
// in it with nothing selected. A prompt is copied as it was sent — the whole of it, however little of it the transcript shows
// — and the button says "Copied" for a moment.

public partial class ChatView
{
    private void InitCopy() =>
        Transcript.CommandBindings.Add(new CommandBinding(TranscriptCommands.CopyMessage, OnCopyMessage, (_, e) => e.CanExecute = true));

    /// <summary>What Copy puts on the clipboard for a row: a prompt as it was sent, a reply or notice as written, a tool's output.</summary>
    internal static string? CopyText(object? row) => row switch
    {
        MessageEntryVM { IsUser: true } message => TranscriptCopy.Prompt(message.Text),
        MessageEntryVM message => message.Text,
        ToolEntryVM tool => tool.Output ?? tool.Summary ?? tool.Preview,
        CompactionEntryVM compaction => compaction.Summary,
        _ => null,
    };

    /// <summary><see cref="CopyText"/>, with the text of the large pastes a prompt carried as attachments after what was typed.</summary>
    private string? CopyTextFor(object? row) =>
        row is MessageEntryVM { IsUser: true, HasAttachments: true } prompt && _session is { } session
            && _model.Host.PastedTexts(session.Id, prompt) is { Count: > 0 } pasted
            ? TranscriptCopy.WithPastes(TranscriptCopy.Prompt(prompt.Text), pasted)
            : CopyText(row);

    private void OnCopyMessage(object sender, ExecutedRoutedEventArgs e)
    {
        var row = e.Parameter ?? (e.OriginalSource as FrameworkElement)?.DataContext;
        if (CopyTextFor(row) is not { Length: > 0 } text) return;
        var ok = SetClipboard(text);
        var source = e.OriginalSource as DependencyObject;
        (source as CopyButton ?? (source is null ? null : CopyButton.InRow(source)))?.ShowCopied(ok);
        e.Handled = true;
    }

    /// <summary>The clipboard is shared with every app and is sometimes briefly held by another: try a few times.</summary>
    internal static bool SetClipboard(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
