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

    /// <summary>What Copy puts on the clipboard for a row: a prompt as it was sent, a reply or notice as written, a tool's output.
    /// <paramref name="storedText"/> reads an attachment saved with the chat by its stored name, so a prompt whose large paste
    /// went as an attachment copies with the paste's text after what was typed.</summary>
    internal static string? CopyText(object? row, Func<string, string?>? storedText = null) => row switch
    {
        MessageEntryVM { IsUser: true } message => PromptText(message, storedText),
        MessageEntryVM message => message.Text,
        ToolEntryVM tool => tool.Output ?? tool.Summary ?? tool.Preview,
        CompactionEntryVM compaction => compaction.Summary,
        _ => null,
    };

    private static string PromptText(MessageEntryVM message, Func<string, string?>? storedText)
    {
        var typed = TranscriptCopy.Prompt(message.Text);
        if (storedText is null) return typed;
        var pastes = message.Attachments.Where(a => a.IsPasted && a.Stored is not null).Select(a => storedText(a.Stored!)).OfType<string>().ToList();
        return pastes.Count == 0 ? typed : TranscriptCopy.WithPastes(typed, pastes);
    }

    /// <summary>The text of an attachment saved with the open chat (a large paste), by the name it is stored under.</summary>
    private string? StoredAttachmentText(string name)
    {
        if (_session is not { } session) return null;
        var store = _model.Host.AttachmentStoreFor(session.Id);
        return store.EntryNamed(name) is { } entry ? store.TextOf(entry) : null;
    }

    private void OnCopyMessage(object sender, ExecutedRoutedEventArgs e)
    {
        var row = e.Parameter ?? (e.OriginalSource as FrameworkElement)?.DataContext;
        if (CopyText(row, StoredAttachmentText) is not { Length: > 0 } text) return;
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
