namespace Vision.Core;

// MARK: - Chats without a project
//
// A chat with no project folder is a regular conversation, not a coding session: the model is told so, is never
// nudged towards opening a folder, and gets a scratch folder of the chat's own for the times it does need files
// or commands (a skill that runs a script, a document to draft, data to convert). The scratch folder is
// %APPDATA%\Vision\Chats\<chat id>\ (or the portable data folder), made on first use and moved to the Recycle Bin
// with the chat — files the agent made there can still be got back.

public static class PlainChat
{
    /// <summary>Where every chat's scratch folder lives.</summary>
    public static string Root => Path.Combine(AppPaths.Root, "Chats");

    /// <summary>Legacy: where DSH (Vision's former name) kept scratch folders, %APPDATA%\DSH\Chats. A chat whose folder
    /// is recorded there is still a chat without a project — its folder now lives under <see cref="Root"/> (see
    /// <see cref="DataFolderMigration"/>, which also rewrites the recorded folder; this covers anything it missed).</summary>
    public static string LegacyRoot => Path.Combine(AppPaths.LegacyDefaultRoot, "Chats");

    /// <summary>This chat's scratch folder (not created here).</summary>
    public static string Folder(string chatId) => Path.Combine(Root, chatId);

    /// <summary>Whether <paramref name="path"/> is a chat's scratch folder (or inside one) — under <see cref="Root"/>,
    /// or under DSH's <see cref="LegacyRoot"/>.</summary>
    public static bool IsScratch(string? path) => Under(Root, path) || Under(LegacyRoot, path);

    /// <summary><paramref name="path"/> where it is now: a scratch folder recorded under DSH's <see cref="LegacyRoot"/>
    /// becomes the same folder under <see cref="Root"/>; anything else is returned as it is.</summary>
    public static string InCurrentRoot(string path) =>
        !Under(Root, path) && Under(LegacyRoot, path) && DataFolderPaths.Rewrite(path, LegacyRoot, Root) is { } moved ? moved : path;

    private static bool Under(string rootFolder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // A cheap test first: most paths asked about are project folders.
        if (path.IndexOf("Chats", StringComparison.OrdinalIgnoreCase) < 0) return false;
        try
        {
            var root = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                   && full.Length > root.Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The system prompt of a chat with no project: <paramref name="platform"/> is "Windows" (or "macOS"),
    /// <paramref name="scratch"/> the chat's scratch folder.</summary>
    public static string SystemPrompt(string platform, string scratch) =>
        $"You are DSH, a helpful assistant in a native {platform} app. This is a regular conversation: no project folder is open " +
        "and none is needed — never ask the user to open one. Answer directly and naturally; match the length of your answer to " +
        "the question. Use tools only when they help: look things up with web_fetch, use one of the user's skills when it fits " +
        "the request, and remember lasting preferences with memory. When something needs files or commands — a skill that runs " +
        $"a script, a document to draft, data to convert — work in this chat's own scratch folder, {scratch}; it belongs to this " +
        "chat and starts empty. Tell the user where a file you made is when it matters.";

    /// <summary>The base of a chat's system prompt: <paramref name="codingPrompt"/> when it works in
    /// <paramref name="projectFolder"/>, the regular-conversation prompt (with <paramref name="scratch"/>) when there
    /// is no project.</summary>
    public static string BasePrompt(string? projectFolder, string scratch, string codingPrompt, string platform = "Windows") =>
        string.IsNullOrEmpty(projectFolder) ? SystemPrompt(platform, scratch) : codingPrompt;

    /// <summary>Make the scratch folder if it isn't there yet; returns it.</summary>
    public static string Ensure(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The chat still works; a tool that needs the folder reports the problem.
        }
        return folder;
    }

    /// <summary>Moves a folder to the Recycle Bin (the app sets it; returns false when that failed). Unset (tests, other
    /// platforms), a deleted chat's folder is deleted outright.</summary>
    public static Func<string, bool>? RecycleBin { get; set; }

    /// <summary>How many files a chat's scratch folder holds (0 when there is none), counting no further than
    /// <paramref name="upTo"/>.</summary>
    public static int FileCount(string chatId, int upTo = 1000)
    {
        var folder = Folder(chatId);
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(upTo).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Remove a chat's scratch folder (the chat was deleted): to the Recycle Bin when the app can, so files the
    /// agent made there can be got back. Only ever a folder under <see cref="Root"/>.</summary>
    public static void Delete(string chatId)
    {
        var folder = Folder(chatId);
        if (!IsScratch(folder) || !Directory.Exists(folder)) return;
        try
        {
            if (RecycleBin is { } recycle)
            {
                // An empty folder has nothing to get back.
                if (Directory.EnumerateFileSystemEntries(folder).Any()) recycle(folder);
                else Directory.Delete(folder);
                return;
            }
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open somewhere: the folder is left for now.
        }
    }
}
