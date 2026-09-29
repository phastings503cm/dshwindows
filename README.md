<p align="center">
  <img src="assets/logo-256.png" alt="DSH for Windows" width="112" />
</p>

<h1 align="center">DSH for Windows</h1>

<p align="center">
  A native Windows coding agent that <em>is</em> the harness. It runs the tool loop itself and talks
  straight to an OpenAI-compatible model server — a DGX Spark on your LAN, a local Ollama or
  LM Studio, or a hosted provider. No Node, no sidecar process, no web view.
</p>

---

This is a Windows port of [DSH for macOS](https://github.com/gnubyte/deepharness), rewritten in C#
(.NET 10, WPF). The agent engine, tools, permission model, skills, plugins and conversation format
match the macOS app; the shell, terminal, paths, secrets and installer are Windows-native.

```
you → Dsh.Core.Engine → OpenAI-compatible /chat/completions → tool calls → files & shell → you
```

## Download

Every push to the default branch builds, tests and publishes a release. Get the latest from
**[Releases](https://github.com/phastings503cm/dshwindows/releases/latest)**:

| | Installer | Portable |
|---|---|---|
| **Windows x64** (most PCs) | `DSH-<version>-win-x64-setup.exe` | `DSH-<version>-win-x64-portable.zip` |
| **Windows on ARM** | `DSH-<version>-win-arm64-setup.exe` | `DSH-<version>-win-arm64-portable.zip` |

- **Installer** — no administrator rights needed (installs for your user under
  `%LOCALAPPDATA%\Programs\DSH`), adds a Start menu entry, and optionally **Open with DSH** on the
  folder right-click menu. Uninstall from *Settings › Apps*; your chats and settings stay.
- **Portable** — unzip anywhere and run `DSH\DSH.exe`. Put an empty file named `portable` next to
  `DSH.exe` to keep settings and chats in a `data` folder beside it instead of `%APPDATA%\DSH`.

Requires Windows 10 1809 or later, or Windows 11. Builds are self-contained (no .NET install
needed). They aren't code-signed yet, so SmartScreen may show *Windows protected your PC* — choose
**More info → Run anyway**. `SHA256SUMS.txt` in each release lists the checksums.

DSH checks for a newer release on startup (Settings › General › Check for updates) and offers a
download link; it never installs anything on its own.

## First run

The setup wizard runs on first launch and any time from **Settings › General › Run Again…** or
**Help › Run Setup Wizard…**. Eight steps: pick a backend, enter its address (and key, if your server
wants one), test the connection, choose a model, set the context window and default thinking level,
pin a permission preset, and optionally open a project folder.

| Backend | Address | Notes |
|---|---|---|
| DGX Spark / vLLM / SGLang / llama.cpp | `http://<host>:<port>/v1` | Port `8002` on a Spark set up with the guide |
| Ollama | `http://127.0.0.1:11434/v1` | Start the Ollama app or `ollama serve` |
| LM Studio | `http://127.0.0.1:1234/v1` | Developer › Start Server |
| OpenAI, OpenRouter | hosted | API key required |

API keys are stored in **Windows Credential Manager**, never in the settings file. If a server on
another machine can't be reached, check it's bound to `0.0.0.0` rather than `127.0.0.1`, and that the
Windows firewall (on either machine) isn't in the way.

An agent needs a model that can call tools. Qwen3, Qwen2.5-Coder, and DeepSeek-V3-class models work
well; very small models struggle. Models without native function calling still work: tool calls
written as XML in plain text are recovered and run.

**Find Servers on My Network** on the connection step scans this PC and your network (each /24 your
PC is on, up to 1,024 hosts) for vLLM, SGLang, llama.cpp, Ollama, LM Studio and DGX Sparks, and fills
in the address of the one you pick.

## Setting up a DGX Spark

On the wizard's first page, **I have a DGX Spark — walk me through everything (beginner)** starts a
guide with a picture for every step, written for people who have never opened a terminal. It can be
closed at any point and picks up where you left off.

1. **What you need**, then **brand new or start fresh**. A new Spark ships with DGX OS: the guide
   walks through cables (power last), the Quick Start card's hotspot sticker, joining that Wi-Fi, the
   setup page (username, password, home Wi-Fi) and the ~10-minute update, while it watches your
   network for the Spark to come back. *Start fresh* makes NVIDIA's recovery USB stick for you: pick
   the recovery archive you downloaded from NVIDIA (`.tar.gz`, `.zip` or an unpacked folder) and a USB
   stick; DSH checks the files fit FAT32, asks Windows for permission (a small elevated helper,
   `DSH.exe --write-recovery-usb`, does the erasing — only USB disks between 8 and 256 GB that don't
   hold Windows are offered, and the disk is re-checked right before it is wiped), writes the
   known-good layout (MBR, one active FAT32 partition ≤ 31 GB labelled `BOOTME`,
   `EFI\BOOT\recovery.txt`) and shows how to boot from it.
2. **Find your Spark** — a network scan shows friendly cards ("DGX Spark at 192.168.1.42 —
   spark-3f2a"); an address can also be typed in.
3. **Install Spark Swapper** — DSH signs in over SSH with the account from the Spark's setup page
   (the host key is trusted on first use and remembered in `known_hosts.json`), runs
   [Spark Swapper](https://github.com/gnubyte/DGX-Spark-Swapper)'s one-line installer in a terminal,
   answers `sudo`'s password prompt itself (never shown or saved), and adds an nginx HTTPS front for
   the model API on port 11443 if there isn't one.
4. **The padlock warning** — what a self-signed certificate is, why browsers say *Your connection
   isn't private*, why that's fine for your own Spark, and its fingerprint (checked against the
   certificate file on the Spark over SSH). DSH pins it; optionally Windows trusts it too.
5. **Your Swapper login** — creates Spark Swapper's admin account right away (the first visitor
   becomes admin).
6. **Get a model running** — installs what's missing through the Swapper's Provisioning API and
   starts a model, following the Spark's log.
7. **Connect DSH** — fetches the model's API key, saves the route
   `https://<spark>:11443/v1` with the certificate pinned, sets up Settings › DGX Spark (so `/swap`
   works), and says hello.

Later: manage the Spark at `https://<spark>:8999` or in Settings › DGX Spark; switch models with
`/swap`. Update Spark Swapper by running the guide again (*It's already set up* → *Reinstall or
update*); reset a forgotten Swapper login on the Spark with `sudo spark-swapper-reset-login`. Spark
Swapper logs: `journalctl -u spark-swapper -f` on the Spark.

The guide's pictures are drawn by `src/Dsh.App/Assets/Guide/make_art.py`; the Spark Swapper
screenshots in them are from its repository (MIT, see `SWAPPER-SCREENSHOTS-LICENSE.txt` there).

## The window

**Chat** (Ctrl+1) — the transcript interleaves messages and tool calls in the order they happened,
each tool card expandable to its full output. Markdown renders with headings, code blocks (with
copy), lists, quotes and tables. **Enter** sends, **Shift+Enter** adds a line. Drop files on the chat
or paste an image (Ctrl+V) to attach it. Under the composer: the chat's permission preset, the model
menu (with DGX Spark model switching), thinking level, skills, and a live context gauge.

**Code** (Ctrl+2) — a VS Code-shaped workspace with the agent beside it:

- **File tree** — lazy, filterable (Ctrl+P), live-updating as the agent or a build writes files.
  Right-click to open, reveal in Explorer, rename, create, delete (to the Recycle Bin), open a
  terminal there, or mention the file in the chat as `@path`.
- **Editor** — tabbed, with syntax colours, line numbers, find/replace (Ctrl+F / Ctrl+H), and a
  remembered position per file. Files keep their line endings (CRLF or LF) and byte-order mark. When
  the agent rewrites a file you have open, a clean tab just follows it; a tab with unsaved edits
  shows **Reload from Disk** / **Keep Mine** instead of being overwritten.
- **Terminal** (Ctrl+\`) — real shells on ConPTY (PowerShell by default; Windows PowerShell, cmd,
  Git Bash or any command line in Settings), with colour, the alternate screen, and scrollback.
  Ctrl+Shift+C / Ctrl+Shift+V copy and paste; Ctrl+C copies when text is selected.

Open tabs, the terminal panel and the panes' visibility are remembered per project. F1 lists the
keyboard shortcuts.

## Permissions

| Preset | What happens |
|---|---|
| **Workspace write** (default) | Reads anywhere. Writes inside the project run without asking; outside it, the agent asks. Shell commands that only read run; anything that looks like it changes something asks. |
| **Plan only** | Every write and every command asks, and the agent is told to research and return a plan. |
| **Full access** | Writes and runs without asking. For projects you'd hand the keys to. |

Each chat keeps the preset it was created with. The agent's commands run in the shell chosen in
**Settings › General › Agent shell** (PowerShell 7 when installed, otherwise Windows PowerShell), and
the agent is told which shell it is so it writes commands for it.

## Tools

The tool set matches [Qwen Code](https://qwenlm.github.io/qwen-code-docs/en/developers/tools/introduction/)
name for name: `read_file`, `read_many_files`, `write_file`, `edit`, `list_directory`, `glob`,
`grep`, `run_shell_command`, `web_fetch`, `todo_write`, `exit_plan_mode`, and `agent` (a subagent for
a scoped task). Plugins add more (below).

## Slash commands

| Command | What it does |
|---|---|
| `/goal <task>` | Works on the task round after round until the model ends a reply with `GOAL_COMPLETE` (or `GOAL_BLOCKED: …`). Ctrl+. stops it. |
| `/compact [focus]` | Summarizes the conversation now. Long chats also compact automatically at 75% of the window. |
| `/think off\|low\|medium\|high\|max\|default` | Thinking level for this chat. |
| `/context` | Window size, usage, and where the window figure came from. |
| `/swap [model]` | Lists the DGX Spark's models, or switches what it serves. |
| `/skills` · `/skill <name>` · `/skill new <what>` · `/<name> args` | List skills, select one for this chat, have the model write one, or run one. |
| `/help` | Lists these. |

## Memory, skills and plugins

**Instruction files** in the project root are loaded into every prompt: `AGENTS.md`, `QWEN.md`,
`CLAUDE.md`, `.claude/CLAUDE.md`, `CLAUDE.local.md`, `.cursorrules`, `DSH.md`, `MEMORY.md`, and
today's `memory/YYYY-MM-DD.md`. **Session › Memory & Skills…** (Ctrl+Shift+M) edits them and shows
the exact system prompt.

**Skills** are folders with a `SKILL.md` — the format Claude Code, Cursor and Agent Skills share, so
DSH reads their folders in place (`.claude`, `.cursor`, `.agents`, `.qwen`, and `%USERPROFILE%`
equivalents) alongside its own (`<project>\.dsh\skills`, `%APPDATA%\DSH\skills`). The Skills button
under the composer picks the skills a chat uses; **Settings › Skills** switches them on or off, edits,
generates new ones with the model (nothing is active until you approve it), and imports from a
folder, a zip, a file or a GitHub link, or exports for Claude Code, Cursor or Agent Skills.

**Plugins** are JSON manifests declaring tools backed by shell commands — drop one in
`%APPDATA%\DSH\plugins` or `<project>\.dsh\plugins` and reload in **Settings › Plugins**.

```json
{
  "name": "dotnet",
  "description": ".NET helpers",
  "tools": [
    {
      "name": "dotnet_test",
      "description": "Run the test suite. Use after changing code.",
      "parameters": {"type": "object", "properties": {"filter": {"type": "string"}}},
      "command": "dotnet test --filter ${filter}",
      "requiresApproval": false
    }
  ]
}
```

## Where things live

| What | Where |
|---|---|
| Settings, conversations, skills, plugins, crash logs | `%APPDATA%\DSH` (or the portable `data` folder; `DSH_HOME` overrides both) |
| API keys, Spark password | Windows Credential Manager (`DSH/…` entries) |
| Program | `%LOCALAPPDATA%\Programs\DSH` (installer) |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app builds on Windows; the core
library and its tests also build and run on Linux and macOS.

```powershell
dotnet build DSH.sln
dotnet test tests/Dsh.Core.Tests
dotnet run --project src/Dsh.App
```

To produce the release files locally (installers need [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3 or later;
6 and 7 both work):

```powershell
./scripts/package.ps1                          # x64 + arm64: installers, portable zips, SHA256SUMS
./scripts/package.ps1 -Arch x64 -SkipInstaller # just a portable x64 build
```

`DSH.exe --self-test <folder> [--theme light|dark]` renders every main view and window to PNGs using
a throwaway data folder and a demo project, and exits non-zero on any error. CI runs it on Windows.

### Layout

| Path | What |
|---|---|
| `src/Dsh.Core` | The harness: OpenAI-compatible client, engine, tools, permissions, compaction, skills, plugins, VT emulator, ConPTY, conversation log |
| `src/Dsh.App` | The WPF app |
| `tests/Dsh.Core.Tests` | xUnit tests (Windows-only ones are skipped elsewhere) |
| `installer/DSH.iss` | Inno Setup script |
| `scripts/` | Packaging, self-test and installer-test scripts used by CI |
| `.github/workflows` | `ci.yml` (pull requests and branches), `release.yml` (default branch → GitHub release) |

## Release pipeline

- **`ci.yml`** runs on pull requests and pushes to any branch other than the default one: core tests
  on Linux; on Windows a Release build, the full test suite (including the ConPTY, path and
  PowerShell tests), the x64 zip and installer, the UI self-test (screenshots are uploaded as an
  artifact), and an installer test: silent install, launch the installed app, check the Start menu
  and folder-menu entries, uninstall, and check nothing is left behind.
- **`release.yml`** runs on every push to the default branch, whatever it is named: tests, packages
  x64 and ARM64 (ReadyToRun, self-contained) into installers and portable zips, runs the self-test
  and the installer test on the packaged x64 build, and publishes a GitHub release tagged
  `v<major>.<minor>.<commits>` with checksums and notes built from the commits since the previous
  release. Bump the major/minor in `Directory.Build.props` (`VersionPrefix`); the patch number
  counts itself.
