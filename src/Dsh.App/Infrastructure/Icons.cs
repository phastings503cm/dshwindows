namespace Dsh.App.Infrastructure;

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points (the two fonts share them). Every
/// glyph the app draws comes from here so the chat, the editor, and the terminal read as one tool.</summary>
public static class Icons
{
    public const string Chat = "\uE8F2";
    public const string Message = "\uE8BD";
    public const string Code = "\uE943";
    public const string Folder = "\uE8B7";
    public const string FolderOpen = "\uE838";
    public const string NewFolder = "\uE8F4";
    public const string OpenFolder = "\uED25";
    public const string Add = "\uE710";
    public const string Settings = "\uE713";
    public const string Stop = "\uE71A";
    public const string Send = "\uE724";
    public const string Attach = "\uE723";
    public const string Picture = "\uE8B9";
    public const string Document = "\uE8A5";
    public const string Page = "\uE7C3";
    public const string Contact = "\uE77B";
    public const string Info = "\uE946";
    public const string Warning = "\uE7BA";
    public const string Error = "\uEA39";
    public const string CheckMark = "\uE73E";
    public const string Completed = "\uE930";
    public const string CircleRing = "\uEA3A";
    public const string CircleFill = "\uEA3B";
    public const string ChevronRight = "\uE76C";
    public const string ChevronDown = "\uE70D";
    public const string ChevronUp = "\uE70E";
    public const string ChevronLeft = "\uE76B";
    public const string Close = "\uE711";
    public const string Terminal = "\uE756";
    public const string Edit = "\uE70F";
    public const string Save = "\uE74E";
    public const string Refresh = "\uE72C";
    public const string Search = "\uE721";
    public const string Filter = "\uE71C";
    public const string Globe = "\uE774";
    public const string Wrench = "\uE90F";
    public const string CheckList = "\uE9D5";
    public const string People = "\uE716";
    public const string Education = "\uE7BE";
    public const string Shield = "\uEA18";
    public const string Admin = "\uE7EF";
    public const string Lock = "\uE72E";
    public const string Bolt = "\uE945";
    public const string Robot = "\uE99A";
    public const string Library = "\uE8F1";
    public const string Lightbulb = "\uEA80";
    public const string Cut = "\uE8C6";
    public const string Flag = "\uE7C1";
    public const string Recent = "\uE823";
    public const string Copy = "\uE8C8";
    public const string Delete = "\uE74D";
    public const string Rename = "\uE8AC";
    public const string More = "\uE712";
    public const string Import = "\uE8B5";
    public const string Export = "\uEDE1";
    public const string Puzzle = "\uEA86";
    public const string Keyboard = "\uE765";
    public const string Mouse = "\uE962";
    public const string Camera = "\uE722";
    public const string OpenInWindow = "\uE8A7";
    public const string Link = "\uE71B";
    public const string Pane = "\uE8A0";
    public const string Cloud = "\uE753";
    public const string Laptop = "\uE7F8";
    public const string Connect = "\uE703";
    public const string Stopwatch = "\uE916";
    public const string Erase = "\uE75C";
    public const string Sync = "\uE895";
    public const string Download = "\uE896";
    public const string Up = "\uE74A";
    public const string Pin = "\uE718";
    public const string Play = "\uE768";
    public const string Mail = "\uE715";
    public const string Pause = "\uE769";
    public const string Down = "\uE74B";
    public const string Key = "\uE8D7";
    public const string Queue = "\uE8FD";
    public const string History = "\uE81C";
    public const string View = "\uE890";
    public const string Hide = "\uED1A";
    public const string Next = "\uE893";
    public const string Undo = "\uE7A7";
    public const string Tray = "\uE7B8";

    /// <summary>Glyph for a tool call, so a transcript is scannable at a glance.</summary>
    public static string ForTool(string name) => name switch
    {
        "read_file" or "read_many_files" => Page,
        "write_file" => Edit,
        "edit" => Edit,
        "list_directory" => Folder,
        "glob" => Search,
        "grep" => Search,
        "run_shell_command" => Terminal,
        "web_fetch" => Globe,
        "todo_write" => CheckList,
        "agent" => People,
        "exit_plan_mode" => CheckList,
        "use_skill" or "propose_skill" => Education,
        "screenshot" or "screen_watch" => Camera,
        "keyboard" => Keyboard,
        "mouse" => Mouse,
        "view_image" => Picture,
        "vault_search" => Key,
        "agent_status" or "agent_stop" => People,
        "queue_task" => Queue,
        "process_start" or "process_read" or "process_write" or "process_stop" or "process_list" => Terminal,
        _ => Wrench,
    };

    /// <summary>Human label for a tool call.</summary>
    public static string ToolLabel(string name) => name switch
    {
        "read_file" => "Read",
        "read_many_files" => "Read files",
        "write_file" => "Write",
        "edit" => "Edit",
        "list_directory" => "List",
        "glob" => "Find files",
        "grep" => "Search",
        "run_shell_command" => "Run",
        "web_fetch" => "Fetch",
        "todo_write" => "Plan",
        "agent" => "Subagent",
        "exit_plan_mode" => "Present plan",
        "use_skill" => "Skill",
        "propose_skill" => "Propose skill",
        "screenshot" => "Screenshot",
        "list_windows" => "Windows",
        "screen_watch" => "Watch screen",
        "ui_tree" => "UI tree",
        "mouse" => "Mouse",
        "keyboard" => "Keyboard",
        "focus_app" => "Focus app",
        "view_image" => "View image",
        "inspect_process" => "Inspect process",
        "process_start" => "Start process",
        "process_read" => "Read output",
        "process_write" => "Send input",
        "process_stop" => "Stop process",
        "process_list" => "Processes",
        "vault_search" => "Vault",
        "agent_status" => "Agent status",
        "agent_stop" => "Stop agent",
        "queue_task" => "Queue task",
        _ => name,
    };

    public static string ForPreset(Dsh.Core.PermissionPreset preset) => preset switch
    {
        Dsh.Core.PermissionPreset.Plan => CheckList,
        Dsh.Core.PermissionPreset.FullAccess => Admin,
        _ => Shield,
    };

    public static string ForProvider(Dsh.Core.ProviderKind kind) => kind switch
    {
        Dsh.Core.ProviderKind.Ollama or Dsh.Core.ProviderKind.LmStudio => Laptop,
        Dsh.Core.ProviderKind.OpenAICompat => Connect,
        _ => Cloud,
    };

    /// <summary>Glyph and tint key for a file in the tree, from its extension.</summary>
    public static (string Glyph, string Tint) ForFile(string name, bool isDirectory, bool expanded)
    {
        if (isDirectory) return (expanded ? FolderOpen : Folder, "accent");
        return System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
        {
            "cs" or "csx" => (Code, "purple"),
            "swift" => (Code, "orange"),
            "md" or "markdown" or "mdx" or "txt" or "rst" => (Document, "secondary"),
            "json" or "yaml" or "yml" or "toml" or "xml" or "plist" or "config" => (Page, "yellow"),
            "csproj" or "sln" or "slnx" or "props" or "targets" => (Wrench, "purple"),
            "png" or "jpg" or "jpeg" or "gif" or "svg" or "ico" or "webp" or "bmp" => (Picture, "purple"),
            "ps1" or "psm1" or "bat" or "cmd" or "sh" or "bash" => (Terminal, "green"),
            "pdf" => (Document, "red"),
            "zip" or "gz" or "tar" or "7z" or "msi" or "exe" or "dll" => (Page, "secondary"),
            "html" or "htm" or "css" or "scss" or "xaml" => (Code, "orange"),
            "js" or "jsx" or "ts" or "tsx" or "py" or "rb" or "go" or "rs" or "c" or "h" or "cpp" or "java" or "kt" => (Code, "accent"),
            _ => (Page, "secondary"),
        };
    }
}
