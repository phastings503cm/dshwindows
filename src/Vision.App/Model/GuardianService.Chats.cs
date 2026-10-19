using Vision.Core;

namespace Vision.App.Model;

public sealed partial class GuardianService
{
    /// <summary>Send one chat to the account (POST /api/harness/chats/:id, the transcript shape both harnesses read). True
    /// when Guardian took it; <see cref="GuardianService.Status"/> says what happened either way.</summary>
    public async Task<bool> SendChatAsync(string chatId)
    {
        if (Client is not { } client)
        {
            Status = "Sign in to Guardian first (Settings › Guardian).";
            return false;
        }
        var log = _host.Log;
        if (log.Session(chatId) is not { } chat) return false;
        var payload = GuardianChats.ToPayload(chat.Id, chat.Title, chat.Cwd, log.LoadItems(chat.Id));
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaxChatBytes)
        {
            Status = "That chat is over 25 MB, the most Guardian keeps for one chat — export it as JSON instead.";
            return false;
        }
        return await Run(async () =>
        {
            await client.PutChatAsync(chat.Id, chat.Title, chat.UpdatedAt, payload).ConfigureAwait(true);
            Status = $"Sent “{chat.Title}” to Guardian. Import chats on another computer brings it back.";
            return true;
        }).ConfigureAwait(true);
    }
}
