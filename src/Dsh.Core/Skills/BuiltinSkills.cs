namespace Dsh.Core;

// MARK: - Builtin skills
//
// Skills that ship with the app, written to SkillLocations.BuiltinSkills on launch ("Skills shipped
// with the app, re-installed on launch"). The shipped text is authoritative: a copy at that path is
// rewritten whenever it differs, so an app update always brings the builtin set current. For a
// skill the user wants to own and edit, copy it into the user skills folder (higher precedence)
// instead of editing the builtin one.

/// <summary>One skill shipped with the app: its SKILL.md and the reference files bundled next to it.</summary>
public sealed record BuiltinSkill(string Slug, string SkillMarkdown)
{
    /// <summary>Written alongside SKILL.md (relative path → text) as reference the model reads on demand.</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
}

public static class BuiltinSkills
{
    /// <summary>Every skill the app ships.</summary>
    public static IReadOnlyList<BuiltinSkill> All => [GodotDebugging];

    /// <summary>Write every builtin skill into <paramref name="directory"/> unless an identical copy
    /// is already there, and return the slugs whose SKILL.md or bundled files changed (for a launch
    /// note). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the
    /// folder can't be written.</summary>
    public static IReadOnlyList<string> Install(string directory)
    {
        Directory.CreateDirectory(directory);
        var changed = new List<string>();
        foreach (var skill in All)
        {
            var folder = Path.Combine(directory, skill.Slug);
            var wrote = WriteIfDifferent(Path.Combine(folder, "SKILL.md"), skill.SkillMarkdown);
            foreach (var (relative, text) in skill.Files)
            {
                wrote |= WriteIfDifferent(Path.Combine([folder, .. relative.Split('/')]), text);
            }
            if (wrote) changed.Add(skill.Slug);
        }
        return changed;
    }

    /// <summary>Atomic replace (temp file + move), skipped when the file already holds exactly this text.</summary>
    private static bool WriteIfDifferent(string path, string text)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        }
        catch (IOException)
        {
            // Unreadable (locked, damaged): rewrite it.
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text, TextUtil.Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return true;
    }

    // MARK: - godot-debugging

    public static BuiltinSkill GodotDebugging { get; } = new("godot-debugging",
        """
        ---
        name: godot-debugging
        description: >-
          Debug a Godot 4 game/editor on Windows efficiently — use when the user
          mentions Godot, GDScript errors, a game that won't start, a black or
          frozen game window, or asks to verify a change "in the game". Covers
          running the engine as a background process, streaming its output,
          screenshotting the window, and watching for a freeze.
        ---

        # Goal

        Find and fix Godot problems with the fewest tokens: read the engine's own
        output first, look at pixels only when output can't answer the question.

        # Find the binary

        Godot on Windows is usually an unzipped download, so there is no fixed
        path. Try in order (run_shell_command, PowerShell):
        - `Get-Command godot, godot4 -ErrorAction SilentlyContinue` (scoop, winget
          and Chocolatey put one on PATH).
        - `Get-ChildItem $env:USERPROFILE\Downloads, $env:USERPROFILE\Desktop, C:\Tools, "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Depth 3 -Filter 'Godot*_console.exe' -ErrorAction SilentlyContinue | Select-Object -First 10 FullName`
        - Steam: `C:\Program Files (x86)\Steam\steamapps\common\Godot Engine\`.

        Every release ships two executables: `Godot_v4.x-stable_win64.exe` is a
        GUI app and prints nothing you can read; `Godot_v4.x-stable_win64_console.exe`
        relays the engine's output. Always start the `_console.exe` one.
        Ask the user if none exist. The project folder is the one containing
        `project.godot` (`glob **/project.godot`).

        # Run it as a background process — never run_shell_command

        Godot is interactive and long-running: it belongs on `process_start`.
        In PowerShell a quoted path needs the call operator `&`.
        ```
        process_start(command: "& '<godot>_console.exe' --path <project> --editor", description: "godot editor")
        process_read(id: "p1", until: "Godot Engine v", timeout: 30)
        ```
        If `until` times out, the returned output already shows why (graphics
        driver, import errors). A game run is the same without `--editor`.
        Send input to it with `process_write` (REPLs, prompts, in-game keys).

        # The cheap-first debug order

        1. `process_read` — GDScript errors print to the terminal:
           `SCRIPT ERROR:`, `ERROR:`, `user://` paths, stack-ish `at:` lines.
           `until: "SCRIPT ERROR"` waits for exactly this. Grep what you get for
           `res://` paths and open those files.
        2. Import/editor corruption: `--headless --import` (fast, no window, dies
           cleanly). Re-read its full output.
        3. Suspected crash/hang of the game: `inspect_process(match: "godot")` —
           ~100% CPU = render loop alive (look at pixels); 0% + no output, or
           "hung" = wedged (read the end of the log file in
           `%APPDATA%\Godot\app_userdata\<project>\logs\godot.log` and the
           Application event log — windows-notes.md has the commands).
        4. Only now `screenshot(window: "Godot")` — black window vs wrong scene
           vs dialog overlay are different bugs. Name what you expect to see via
           `description:` and describe it back before acting.
        5. `screen_watch(window: "Godot")` distinguishes "renders once then
           freezes" (hung _process) from "animates but wrong".

        # Driving the app

        - `keyboard(app: "Godot Engine", keys: ["F5"])` runs the project from the
          editor (["F6"] the current scene); ["F8"] stops. Always pass `app` — it
          focuses first (the reliable pattern; "Godot Engine" matches the editor's
          title, not the game's), and a `screenshot` after verifies.
        - `mouse(x:, y:)` for in-game UI clicks; coordinates are screen pixels.
          Screenshots are downscaled — the screenshot result states the capture
          origin and scale; map the image position back with them (and say so).
        - `ui_tree(app:)` is for NATIVE dialogs only (file pickers, message boxes).
          Godot's window is one big Vulkan/D3D12/OpenGL surface: pixels, not UI
          Automation.

        # Verify, then stop the loop

        After each fix: `process_stop` the old run, `process_start` a new one,
        `process_read(until:)` for the line that used to error. Don't claim a
        fix without that observation. Clean up: stop processes you started;
        leave the user's own running.

        Gotchas, binary names, headless flags, log locations and a worked session:
        `read_file` the bundled `windows-notes.md` next to this skill.
        """)
    {
        Files = new Dictionary<string, string>
        {
            ["windows-notes.md"] = """
                # Godot on Windows — reference

                ## Binary / invocation
                - A release zip holds two executables: `Godot_v4.3-stable_win64.exe`
                  (GUI subsystem: its output never reaches a console) and
                  `Godot_v4.3-stable_win64_console.exe` (starts the same engine and
                  relays stdout/stderr). Use `_console.exe` with process_start, or
                  process_read has nothing to read. .NET builds
                  (`Godot_v4.3-stable_mono_win64\`) ship the same pair.
                - PowerShell needs the call operator for a quoted path:
                  `& 'C:\Tools\Godot\Godot_v4.3-stable_win64_console.exe' --path C:\src\game`
                - `--path` selects the project; without it Godot opens the project manager.
                - Headless import (no window, exits): `--headless --import --path <proj>`
                - One-shot script run: `--headless --path <proj> --script res://tools/check.gd --quit`
                - Verbose logging for hard cases: `--verbose`; demos: `--quit-after 200`
                  quits after N frames (turns an infinite run into a bounded one).
                - Black window or a crash at startup (VMs, Remote Desktop, old GPUs):
                  try `--rendering-driver opengl3` or `--rendering-driver d3d12`
                  (Vulkan is the default for Forward+ and Mobile).
                - A freshly downloaded exe can be stopped by SmartScreen ("Windows
                  protected your PC"): a screenshot shows the dialog. `Unblock-File <exe>`
                  clears the downloaded-file mark — only with the user's OK.

                ## Reading Godot's output
                - `SCRIPT ERROR: ... at: res://foo.gd:LINE` — the line to open.
                - `ERROR: ...` lines without SCRIPT are engine-level (resource
                  load fails, null refs). Often the real cause sits two lines above.
                - First-run import is LOUD and mostly harmless; only judge errors on
                  a second run after `--headless --import` finished once.
                - The same output also lands in a log file (on by default):
                  `%APPDATA%\Godot\app_userdata\<project name>\logs\godot.log`, with older
                  runs next to it as `godot<timestamp>.log`. A project with
                  `application/config/use_custom_user_dir` logs to `%APPDATA%\<custom dir>\logs`.
                  Tail it: `Get-Content "$env:APPDATA\Godot\app_userdata\My Game\logs\godot.log" -Tail 60`
                  Search it: `Select-String -Path <log> -Pattern 'SCRIPT ERROR|ERROR:'`
                - Editor settings live in `%APPDATA%\Godot\` (`editor_settings-4.tres`);
                  the import cache is `<project>\.godot\` (deleting it forces a full
                  reimport — ask first).

                ## Processes (PowerShell equivalents)
                - Running? `inspect_process(match: "godot")`, or
                  `Get-Process -Name Godot* | Select-Object Id, CPU, WorkingSet64, StartTime, MainWindowTitle, Responding`
                - A game run from the editor is a second Godot process (a child of the
                  editor): `inspect_process(pid: <editor pid>)` lists its children.
                - Stop a run you started: `process_stop(id)`; one the user started
                  (ask first): `Stop-Process -Id <pid>`.
                - Crashes: `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'} -MaxEvents 10 | Where-Object Message -match 'Godot' | Format-List TimeCreated, Message`
                  Dumps, when Windows Error Reporting keeps them: `%LOCALAPPDATA%\CrashDumps`.
                - process_start runs the command on a pseudo console (ConPTY), so the
                  engine's colored output arrives cleaned up, and
                  `process_write(id, keys: ["ctrl-c"])` interrupts it like Ctrl+C.

                ## Window / screenshot specifics
                - The editor's window title is `<project name> - Godot Engine`; the game's
                  is the project name (`config/name=...`), with ` (DEBUG)` when run from
                  the editor. Match the game on its project name — "Godot" alone may
                  hit the editor (both processes are Godot executables).
                - `list_windows(app: "godot")` shows both, with pid and geometry.
                - All machine tools use physical screen pixels, so list_windows,
                  screenshot and mouse agree even with display scaling at 125–200%.
                  Screenshots come downscaled to 1600 px long edge; the result says
                  `screen = X,Y + image × S`: multiply an image position by S and add X,Y
                  before clicking.
                - A frozen window gets " (Not Responding)" in its title after ~5 s and
                  a ghost frame drawn by Windows: `screen_watch` sees no change and
                  `inspect_process` reports it "hung" — the main loop is wedged.
                - A minimized window has no pixels: `focus_app(app: "Godot")` first.

                ## A worked session (pattern to copy)
                1. `process_start(command: "& '<godot>_console.exe' --path C:\src\game --editor", ...)` →
                   `process_read(id, until: "Godot Engine v")`.
                2. `keyboard(app: "Godot Engine", keys: ["F5"])` — run the project.
                3. `process_read(id, until: "SCRIPT ERROR", timeout: 10)` — timeout
                   means clean; a match gives file:line.
                4. `screenshot(window: "<project name>", description: "player at spawn")`.
                5. Edit the script → `keyboard(app: "Godot Engine", keys: ["F8"])` (stop) →
                   `keyboard(app: "Godot Engine", keys: ["F5"])` → repeat from 3.
                6. Done: `process_stop(id)`.
                """,
        },
    };
}
