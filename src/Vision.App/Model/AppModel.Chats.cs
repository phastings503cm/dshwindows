using System.IO;
using System.Windows;
using Microsoft.Win32;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App.Model;

/// <summary>Chats in and out: the import of other tools' conversations, export as Markdown or JSON, sending one to Guardian,
/// and the one search over every chat.</summary>
public sealed partial class AppModel
{
    /// <summary>Open "Import chats".</summary>
    public event Action? ChatImportRequested;
    /// <summary>Open "Search all chats" (with a query to start from).</summary>
    public event Action<string?>? ChatSearchRequested;
    /// <summary>Show one stored row of a chat (a search result): the chat's id and the row's Seq.</summary>
    public event Action<string, int>? ChatRowRequested;

    public void ShowChatImport() => ChatImportRequested?.Invoke();
    public void ShowChatSearch(string? query = null) => ChatSearchRequested?.Invoke(query);
    public void ShowChatRow(string chatId, int seq) => ChatRowRequested?.Invoke(chatId, seq);

    /// <summary>Save a chat as Markdown (to read or share) or JSON (to import into Vision elsewhere). Asks where.</summary>
    public void ExportChat(string chatId, bool json, Window? owner = null)
    {
        var log = Host.Log;
        if (log.Session(chatId) is not { } session) return;
        var dialog = new SaveFileDialog
        {
            Title = json ? "Export the chat as JSON" : "Export the chat as Markdown",
            FileName = ChatExport.FileName(session, json ? ".json" : ".md"),
            Filter = json ? "Vision chat (*.json)|*.json" : "Markdown (*.md)|*.md",
            AddExtension = true,
        };
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(documents)) dialog.InitialDirectory = documents;
        owner ??= Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        var rows = log.LoadItems(chatId);
        try
        {
            File.WriteAllText(dialog.FileName, json ? ChatExport.Json(session, rows) : ChatExport.Markdown(session, rows), TextUtil.Utf8NoBom);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't save the chat", error.Message);
        }
    }

    /// <summary>Send one chat to the Guardian account (it is kept there for the other harnesses and computers), and say how it
    /// went.</summary>
    public async Task SendChatToGuardianAsync(string chatId)
    {
        if (!Guardian.IsSignedIn)
        {
            if (Dialog.Confirm("Sign in to Guardian first", "Sending a chat keeps a copy in your Guardian account, where Vision on another computer (or the Mac) can import it. Sign in at Settings › Guardian.", "Open Guardian Settings"))
                ShowGuardian();
            return;
        }
        if (await Guardian.SendChatAsync(chatId)) Dialog.Info("Sent to Guardian", Guardian.Status ?? "The chat is in your Guardian account.");
        else if (Guardian.Status is { } why) Dialog.Info("Couldn't send the chat", why);
    }

    /// <summary>After an import: the new chats join the list (in their own date order).</summary>
    public void ChatsImported() => Host.Reload();
}
