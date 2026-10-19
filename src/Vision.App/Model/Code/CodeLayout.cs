namespace Vision.App.Model.Code;

/// <summary>What code mode shows, worked out from the four switches (file tree, editor, terminal, chat). The chat is the
/// main view: it fills the space the editor leaves, and it can only be hidden while the editor is showing — so hiding
/// the file tree, the editor and the terminal leaves the chat and the project status filling the window. A file opened
/// from the tree, a tool card or a changed-file chip turns the editor on, in the centre; closing the last file turns it
/// off again.</summary>
public sealed record CodeLayout(bool Tree, bool Editor, bool Terminal, bool Chat)
{
    /// <summary>The chat takes the centre (the editor is hidden).</summary>
    public bool ChatIsMain => !Editor;

    /// <summary>Nothing but the chat and the project status.</summary>
    public bool ChatOnly => !Tree && !Editor && !Terminal;

    /// <summary>The layout for these switches. <paramref name="hasTerminals"/>: a terminal panel shows only once a terminal exists.</summary>
    public static CodeLayout For(bool showTree, bool showEditor, bool showTerminal, bool showChat, bool hasTerminals) =>
        new(showTree, showEditor, showTerminal && hasTerminals, showChat || !showEditor);
}

/// <summary>What the project status bar says about the chat working on the project.</summary>
public static class ProjectStatus
{
    public enum Tone { Idle, Busy, Waiting }

    /// <summary>"Working · edit src/Stock.cs", "Waiting for your permission", "Idle · plan 3 of 7 done", …</summary>
    public static (string Text, Tone Tone) Agent(SessionVM? session)
    {
        if (session is null) return ("No chat open", Tone.Idle);
        if (session.PendingGates.Count > 0) return ("Waiting for your permission", Tone.Waiting);
        if (!session.Running && session.BlockedGoal is not null) return ("Waiting for your answer", Tone.Waiting);
        var plan = session.Todos.Count > 0 ? $"plan {session.Todos.Count(t => t.Status == Core.TodoStatus.Completed)} of {session.Todos.Count} done" : null;
        if (!session.Running) return (plan is null ? "Idle" : "Idle · " + plan, Tone.Idle);
        string doing;
        if (session.Stopping) doing = "Stopping…";
        else if (session.Retry is not null) doing = "Model unavailable — retrying";
        else if (session.RunningTool is { } tool) doing = $"Working · {tool.Label.ToLowerInvariant()} {tool.Preview.Replace('\n', ' ')}".TrimEnd();
        else if (session.Activity is { } activity) doing = activity;
        else doing = "Working…";
        if (session.Goal is { } goal) doing = $"Goal · round {goal.Round} · " + doing;
        return (plan is null ? doing : doing + " · " + plan, Tone.Busy);
    }

    /// <summary>"3 files changed" by this chat's tools; empty when none.</summary>
    public static string Changes(SessionVM? session) => session?.ChangedFiles.Count switch
    {
        null or 0 => "",
        1 => "1 file changed",
        var n => $"{n} files changed",
    };
}
