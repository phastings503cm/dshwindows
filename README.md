<p align="center">
  <img src="assets/logo-256.png" alt="DSH for Windows" width="112" />
</p>

<h1 align="center">DSH for Windows</h1>

<p align="center">
  A native Windows coding agent that <em>is</em> the harness. It runs the tool loop itself and talks
  straight to an OpenAI-compatible model server — a DGX Spark on your LAN, a local Ollama or
  LM Studio, a hosted provider, or Amazon Bedrock. No Node, no sidecar process, no web view.
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
| Amazon Bedrock | `https://bedrock-runtime.<region>.amazonaws.com` | Set up by its own guide — see below |

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

## Two or more model servers

Add a second DGX Spark (or any other OpenAI-compatible server) as another route in **Settings ›
Models** and tick **Use for subagents** on it. The route you are chatting with stays the *primary* — the main
agent talks to it — and the ticked routes become *workers*: subagents (`agent`, `delegate`, background
agents) run on the workers, least busy first and several at a time (the count is set per server),
while the main agent carries on with the primary. When every worker is busy, subagents may use the
primary too (a checkbox in the same place turns that off). A worker that stops answering is skipped for
a minute and its task moves to another server (told, if the task had already changed things, to look
before repeating a step), so one Spark going down doesn't lose a job; with a single server, a subagent
waits out an outage as long as the main agent does. One machine counts as one worker however many of its
models are listed, and editing a route never makes it the main model. Each worker
is asked which model it is serving right now, so a Spark that swapped models is still used correctly.
The Plan panel shows where each subagent runs, and `/agents` lists the servers with how busy each is.

## Using Amazon Bedrock

On the wizard's first page, **Use Amazon Bedrock with my AWS account (guided)** — or **Settings ›
Models › Add Amazon Bedrock…** — starts a guide for people who have never used the AWS CLI. All it
needs is an AWS account you can sign in to on the website.

1. **The AWS CLI.** DSH looks for it and installs it if it's missing (or updates it if it's too old
   for the browser sign-in, which needs 2.32 or later): Amazon's per-user MSI, no administrator
   prompt, with its Authenticode signature checked (signer *Amazon Web Services, Inc.*) before it
   runs. If that's blocked, it can install for all users (Windows asks for permission) or with
   `winget`.
2. **Sign in.** Pick a Region, then **Sign in with my browser**: DSH runs `aws login` into its own
   profile, `dsh-bedrock`, so profiles you already have are left alone. Your browser opens to the
   AWS sign-in page; DSH never sees your password. Signing in on a PC without a browser works too
   (the CLI's `--remote` mode: open the link elsewhere and paste the code back). Already set up with
   IAM Identity Center (SSO), keys or a role? Choose **Use a profile I already have** — DSH runs
   `aws sso login` for SSO profiles. The guide then shows the account you're signed in to.
3. **Pick a model.** The Region's chat models (Claude, Amazon Nova, Llama, Mistral, DeepSeek, …)
   with the best of each family first, using a cross-Region inference profile where the model needs
   one. DSH checks what each one needs before first use.
4. **Enable it** — only when needed; most models are ready immediately:
   - **Claude** needs Anthropic's one-time *use case* form per AWS account. DSH asks the same
     questions (company, website, industry, who will use it, what for) and submits it
     (`PutUseCaseForModelAccess`).
   - **Marketplace models** show their offer first — price per unit, licence (EULA) link, refund
     policy — and are enabled only after you tick *I accept* and press **Accept and enable**
     (`CreateFoundationModelAgreement`).
   - Then DSH waits for AWS to finish (usually seconds, up to 15 minutes for a new subscription).
   If your AWS identity isn't allowed to do something, the page says which permission is missing
   and has a ready-made IAM policy to copy for your administrator.
5. **Connect** — saves the route (profile, Region, model) and says hello through Bedrock.

Requests are signed with SigV4 using short-lived credentials the AWS CLI exports for the profile
(`aws configure export-credentials`), refreshed before they expire; nothing secret is stored in
DSH's settings. Chats use Bedrock's Converse API with streaming, tools, images, prompt caching for
Claude and Nova, and Claude's extended thinking. When the sign-in runs out, the banner offers **Sign
in to AWS…** (the browser opens, and your next message goes through); **Settings › Models** has
**Sign in again** and **Change…** (another model, Region or account) for each Bedrock route.

Need a **Bedrock API key** for another tool? The guide's last page can create one from your sign-in
(valid up to 12 hours) and save it in the Credentials Vault as `AWS_BEARER_TOKEN_BEDROCK`.

Costs are billed by AWS to your account (Amazon Bedrock, and AWS Marketplace for Marketplace models).
To remove DSH's sign-in: `aws logout --profile dsh-bedrock`.

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

**Task Queue** (Ctrl+Shift+Q, or the list button in the top bar) — a panel on the right for work
that should happen unattended. **Credentials Vault** (Ctrl+Shift+K, or the key button) — the API
keys and passwords the agent may use. Both are described below.

**Plan** (Ctrl+Shift+P, or the checklist button at the top right, whose counter shows how many steps
are done) — a panel on the right that lays out what the agent is working through in the chat you're
looking at: what it is doing right now, the goal it is on, the steps of its plan ticked off as it
goes, a plan proposed in *Plan only* mode, and the subagents it has running and which model server each
is on. It reads the agent's `todo_write` list, or a plan written in a reply ("Here's my plan: 1. …
2. … 3. …"). It opens by itself the first time a chat makes a plan; a checkbox at the bottom turns that
off.

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

The core tool set matches [Qwen Code](https://qwenlm.github.io/qwen-code-docs/en/developers/tools/introduction/)
name for name: `read_file`, `read_many_files`, `write_file`, `edit`, `list_directory`, `glob`,
`grep`, `run_shell_command`, `web_fetch`, `todo_write`, `exit_plan_mode`, and `agent` (a subagent for
a scoped task). On top of those:

- **Background subagents** — `agent` with `run_in_background: true` works while the main agent
  carries on (up to four at once). `agent_status` checks on them or waits for one, `agent_stop` ends
  one. When they finish, the main agent is told automatically; an idle chat picks the work back up
  by itself. A bar above the composer shows what is running, with a stop button.
- **Long-running programs** — `process_start`, `process_read`, `process_write`, `process_stop`,
  `process_list`: dev servers, game engines, REPLs and watchers run in a pseudo console and keep
  running between tool calls. The agent reads their output (optionally waiting for a line to appear),
  types into them, and stops them with Ctrl+C, then the whole process tree. They end with the app.
- **Seeing and using the PC** — `screenshot`, `list_windows`, `screen_watch`, `ui_tree`,
  `inspect_process`, `view_image`, `mouse`, `keyboard`, `focus_app`, for testing what the agent
  built. Each chat asks before the first look and the first click (full-access chats don't). Typing
  into terminals, the Run box, Explorer or Task Manager is always refused. Switch them off in
  **Settings › General › Computer use**.
- **`queue_task`** adds follow-up work to the chat's own task list; **`vault_search`** finds credentials by
  name (never their values).
- **Kinds of subagent, and fan-out** — `agent` takes an `agent_type`: `general` (the default),
  `explore` (read-only search), `plan`, `review`, `worker`, or your own. Your own are Claude
  Code-style agent files — a `*.md` with `name`, `description`, `tools`, `server` and `max_steps` in
  its frontmatter and the instructions as the body — in `%APPDATA%\DSH\agents`,
  `%USERPROFILE%\.claude\agents`, or a project's `.dsh\agents` / `.claude\agents`. (A file with no
  `tools` line gets every tool; a `tools` line that is blank, or names only tools DSH doesn't have,
  gets just the ones that read.) Several `agent` calls in one turn run at the same time, and
  `delegate` hands a list of independent tasks out and returns every report together. `/agents` shows
  what exists and what is running.
- **`memory_save`, `memory_search`, `memory_forget`** — the agent's long-term memory (see *Remembered
  notes*). **`goal_complete`, `goal_blocked`** are offered only while a `/goal` runs.
- **A result cache** — a file read again a moment later, or a search repeated before anything has
  changed, is answered from a per-chat cache instead of being redone and sent to the model a second
  time. It only reuses an answer it can prove is still right (a file's timestamp and size are
  unchanged; any write or command, a new message from you, or a background agent reporting clears the
  cached searches; a search asked a third time runs again; pages on this PC or the local network are
  never cached), and the Plan panel shows what it saved.

Plugins add more (below).

## Task lists

Every chat has its own **task list** (the panel on the right, **Ctrl+Shift+Q**; it shows the chat
you're in). Line up work — one task per item — and press **Start**: the chat works its tasks one at a
time, top to bottom, *in that chat*, each as an unattended goal that runs until the model declares it
complete. Each task sees the conversation so far, including what the tasks before it did. If you're
mid-turn when the list starts, your turn finishes first. Several chats can work their lists at the
same time; the toolbar's list button shows a dot while any of them is running.

A task that needs you is marked blocked and that chat's list pauses, so the next task doesn't talk
over the question: answer in the chat and press **Resume** on the task. Drag waiting tasks (or use
the arrows) to reorder them; edit or delete any task that isn't running. The log (the clock button)
records when each task started, every round, retries, and how it ended, with token counts and speed.

Task lists are saved to `task-queue.json` and survive restarts: if the app quits or crashes mid-task,
the task goes back in line and that chat's list picks up where it left off on the next launch (unless
you had pressed Stop). Three failed tasks in a row pause a list. Deleting a chat deletes its list.
Windows is kept awake while a list runs. Permission questions in a task are answered "no" after five
minutes, so an unattended run never stalls on one. Tasks queued before lists were per chat are moved
into a chat called *📋 Earlier tasks* (one per project folder) the first time this version starts.

## Credentials vault

Keep API keys, tokens and passwords in the vault and the agent can use them without ever seeing
them: it writes `{{vault:NAME}}` wherever the value goes — a shell command, a `.env` file, a request
header — and DSH substitutes the real value only when the tool runs. Tool output shows
`[vault:NAME]` instead of the value, so the secret never reaches the model, the transcript or the
logs. Each credential is **Agent may use**, **Ask first** (once per chat) or **Never**. Values are
encrypted for your Windows account with DPAPI; the window shows a fingerprint until you reveal a
value, which asks for Windows Hello or your Windows password first.

## Model outages

When the model server is down, restarting, or switching models, requests are retried automatically
— after 2, 4, 8, 16 and then every 30 seconds — until it answers, and the chat shows the countdown.
Press Stop to give up. Nothing already done is lost: tool calls that ran stay in the conversation.

## Slash commands

| Command | What it does |
|---|---|
| `/goal <task>` | Works on the task round after round, with no round limit and nobody typing "continue", until the agent calls its `goal_complete` tool (models that can't call tools end a reply with `GOAL_COMPLETE` instead). If it needs you it calls `goal_blocked` and waits: your next message is the answer, and the goal carries on with it (even after a restart); `/goal stop` drops a paused goal instead. Outages are retried; Ctrl+. stops it. A bare `/goal` picks an unfinished goal back up. |
| `/queue` | Starts this chat's task list (or says what it is doing). |
| `/compact [focus]` | Summarizes the conversation now. Long chats also compact automatically at 75% of the window. |
| `/think off\|low\|medium\|high\|max\|default` | Thinking level for this chat. |
| `/context` | Window size, usage, and where the window figure came from. |
| `/swap [model]` | Lists the DGX Spark's models, or switches what it serves. |
| `/skills` · `/skill <name>` · `/skill new <what>` · `/<name> args` | List skills, select one for this chat, have the model write one, or run one. |
| `/remember <note>` · `/memory [search]` | Save a note to long-term memory, or open the remembered-notes window (with a search word: list the matches). |
| `/agents` | The kinds of subagent, the model servers they run on, and what is running now. |
| `/help` | Lists these. |

### No "continue"

A chat doesn't stop after a fixed number of steps and wait for you to say "continue": the step limit is
only a checkpoint, and the agent carries on until it has answered. So does a reply the model's output
limit cut short (a tool call cut off mid-way is never run — the agent is asked to send it again in
smaller pieces), and a chat whose background subagents have just finished. What does stop a run is you
pressing Stop, the agent saying it needs you, or it going in circles: the same call with the same
result again and again — even when it alternates between two calls (a longer cycle of up to eight
different calls is caught too, a little later), or a todo update comes between, or every attempt fails
with the same error — or a call you keep refusing. The agent is warned at the fourth
repeat and the run ends at the eighth; DSH then says why and waits for a new direction, and a `/goal`
that goes in circles for three rounds in a row pauses the same way. (Waiting on something — a command
with `sleep` in it, polling a process or a background agent — is not going in circles, up to a point: the same wait
giving the same answer twenty times gets a note and forty ends the run, and a wait that *fails* is not waiting. Neither is a
check that gives the same answer after every real edit, like a quiet build — including an edit made by running a
script or a generator with new arguments; one that keeps *failing* the same way is a loop.) As a last backstop a single turn stops
after 360 steps; a `/goal` simply carries on in its next round. `goal_complete` counts only when it is
the only call in its turn, so the agent has seen the results of everything else first.

## Bringing in Claude Code and Cursor

If Claude Code or Cursor is on your PC, DSH offers to bring your setup across: once, with a notice at the
top of the window, and any time after that from **Session › Bring in from Claude Code & Cursor…**,
**Settings › Skills**, or **Memory & Skills**. It looks in `%USERPROFILE%\.claude` and
`%USERPROFILE%\.cursor` (or wherever `CLAUDE_CONFIG_DIR` points), lists what it found with the new,
safe items already ticked, and **Import** copies them into DSH. Nothing in those folders is changed and
nothing is run.

| Found | Becomes in DSH |
|---|---|
| `CLAUDE.md` in Claude Code's folder (your global instructions) | One of **your instructions** (`%APPDATA%\DSH\instructions`): read on every request, in every project and in chats with no folder. Edit it in *Memory & Skills*. |
| Skills — your own and those of installed plugins, from both tools | DSH skills, with their bundled files. |
| Commands (`/name`) | Skills only you run, by typing `/name` in a chat. |
| Rules (`.claude\rules`, `.cursor\rules`) | Rules: always on, or attached to the files they name. |
| Subagents | Skills that tell the agent to hand the work to a subagent (the `agent` tool) with those instructions. |
| The notes Claude Code saved per project (`.claude\projects\<project>\memory`) | Notes for that project (`%APPDATA%\DSH\project-notes`), kept out of the repository. When you open the project, the model is shown the `MEMORY.md` index and reads the notes it needs. |

Only installed plugins count (Claude Code's install list, or Cursor's plugin cache); one installed for a
single project isn't ticked. Cursor's own built-in skills are listed but not ticked. Anything already in
DSH shows as *Already in DSH*, so running the import again changes nothing. If a skill has changed since,
ticking it keeps both, the second under a numbered name (`review-2`) and switched on. Notes Claude Code
has added to since show as *Updated since* and aren't ticked; updating them, or re-importing changed
instructions, keeps the files it replaces beside them as `.bak`. **Let me approve skills first** holds
skills in *Settings › Skills › To approve* instead of switching them on, and they show as *Waiting for
your approval* until you decide.

Listed at the bottom of the window and left alone: MCP servers (DSH can't run them yet), hooks,
tool-permission rules, conversation history, and what Cursor keeps inside its app (User Rules and
Memories). Anything that looks like a key or token, including in a file bundled with a skill (a `.env`, a
`.pem`), isn't ticked for you, because instructions and notes go to your model provider with each
request. A plugin's files and folders that are links to elsewhere on the PC are skipped. Claude Code
running inside WSL keeps its files on the Linux side; use *Somewhere else…* in the window to point at
them.

## Bringing in OpenClaw

**Session › Bring in from OpenClaw…** copies what an OpenClaw install has learned into DSH: its
**skills**, its **memory** (`MEMORY.md`, the daily logs, `USER.md` and the like, cut into searchable
remembered notes), the **keys and passwords** in its configuration (into the Credentials Vault, *ask
first* unless you choose otherwise) and its **model servers** (as routes). The first page asks where it
runs:

- **This PC** — DSH looks in the usual places (`~/.openclaw`, the older `.clawdbot` and `.moltbot`
  names, profile folders such as `.openclaw-work`, `OPENCLAW_STATE_DIR`) and, failing that, searches for
  `openclaw.json`.
- **Another computer (over SSH)** — give an address (a name from your `~/.ssh/config` works) and a
  password or key file, or leave both blank to try the keys in your `~/.ssh` folder. DSH signs in,
  finds the install the same way (Docker bind mounts and the usual `/data`, `/app` and `/home/node`
  places included), and reads the files over SFTP — or with shell commands on a server that has none —
  fetching only what you tick. The server's host key is trusted on first use, remembered in
  `known_hosts.json`, and refused if it ever changes.

If it can't find the install, *A folder to look in* takes the path. Nothing is copied until you press
the button, and nothing on the other machine is changed or run. Keys and passwords go straight into the
encrypted vault and are never shown or logged. Nothing that looks like a credential is carried across
anywhere else: notes that hold one are left out, and so are the files of a skill that do (`.env`, private
keys, a file with a key inside) — the window says which. Skills that bundle scripts can be held for
approval in **Settings › Skills** (importing never runs a script); importing again updates notes rather
than duplicating them, and a skill that hasn't changed isn't copied twice.

## Remembered notes

DSH keeps a long-term memory that survives between chats: short notes about you and your projects — *the
staging server is called orion*, *tests run with `npm run t:unit`*, *the user prefers tabs*. Nothing is
loaded up front, so a long list costs nothing. When you send a message, DSH searches the notes (full
text with word stemming, weighted by how well they match, how recent they are and how often they have
helped) and puts the few that bear on it in front of the message inside `<recalled_memory>` tags: at
most four, about 600 tokens, none twice within eight turns, and nothing at all when nothing matches. The
chat shows a one-line *Remembered 2 notes: …* when it happens.

Notes come from you (`/remember the staging server is called orion` — it belongs to the project the
chat works in; start with `global:` for a note that applies everywhere — or **Session › Remembered
Notes…** to add, edit, pin and delete them), from the agent (`memory_save` when it learns something
durable and non-obvious; `memory_search` and `memory_forget` to look things up and correct them) and
from OpenClaw. They are plain text in `%APPDATA%\DSH\memory`. Anything that looks like a key or a
password is refused, whoever wrote it and however it got there (an import, a mirrored file) — that
belongs in the vault. Saving a note that repeats one of the agent's own updates it, but the agent never
rewrites a note you wrote or pinned (it adds its own beside it), can't pin a note, and can't delete one
of yours — or one you've pinned or edited — without asking; in *Plan only* mode it can look things up
but not change them. What a note says is shown to the model as reference data, never as instructions. A *pinned* note is always in the system prompt, for a few
standing facts (the window warns when more are pinned than fit). A large `MEMORY.md` or daily log in a
project is cut to its opening in the prompt and made searchable, so it no longer takes over the window;
the searchable pieces follow the file (trim the file and they go), and can be deleted in the notes
window. The switch is at the top of the notes window.

## Memory, skills and plugins

**Instruction files** in the project root are loaded into every prompt: `AGENTS.md`, `QWEN.md`,
`CLAUDE.md`, `.claude/CLAUDE.md`, `CLAUDE.local.md`, `.cursorrules`, `DSH.md`, `MEMORY.md`, and
today's `memory/YYYY-MM-DD.md`. Your own instruction files in `%APPDATA%\DSH\instructions` (any `*.md`)
are loaded into every prompt in every project, ahead of the project's. **Session › Memory & Skills…**
(Ctrl+Shift+M) edits them all and shows the exact system prompt.

**Skills** are folders with a `SKILL.md` — the format Claude Code, Cursor and Agent Skills share, so
DSH reads their folders in place (`.claude`, `.cursor`, `.agents`, `.qwen`, and `%USERPROFILE%`
equivalents) alongside its own (`<project>\.dsh\skills`, `%APPDATA%\DSH\skills`). The Skills button
under the composer picks the skills a chat uses; **Settings › Skills** switches them on or off, edits,
generates new ones with the model (nothing is active until you approve it), and imports from a
folder, a zip, a file or a GitHub link, or exports for Claude Code, Cursor or Agent Skills. One skill
ships with the app: **godot-debugging**, which runs a Godot game, reads its output and drives it
with the computer-use tools.

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
| Your instructions (every project) | `%APPDATA%\DSH\instructions\*.md` |
| Remembered notes | `%APPDATA%\DSH\memory\memories.jsonl` |
| Your subagent types | `%APPDATA%\DSH\agents\*.md` |
| Notes imported per project | `%APPDATA%\DSH\project-notes\<project>` |
| Task lists (every chat's) | `%APPDATA%\DSH\task-queue.json` |
| Credentials vault | `%APPDATA%\DSH\vault.json` (names and details) and `vault.bin` (values, DPAPI-encrypted) |
| Program | `%LOCALAPPDATA%\Programs\DSH` (installer) |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app builds on Windows; the core
library and its tests also build and run on Linux and macOS.

```powershell
dotnet build DSH.sln
dotnet test tests/Dsh.Core.Tests
dotnet test tests/Dsh.App.Tests       # Windows: task lists, /goal, background agents, vault
dotnet test tests/Dsh.Windows.Tests   # Windows: screenshots, windows, mouse and keyboard
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
| `src/Dsh.Core` | The harness: OpenAI-compatible client, engine, tools, permissions, compaction, skills, plugins, importing from Claude Code, Cursor and OpenClaw, long-term memory, the subagent fleet, VT emulator, ConPTY, conversation log |
| `src/Dsh.App` | The WPF app |
| `src/Dsh.Windows` | The computer-use tools (screen capture, UI Automation, input) |
| `tests/Dsh.Core.Tests` | xUnit tests (Windows-only ones are skipped elsewhere) |
| `tests/Dsh.App.Tests` | The app host against a fake model server: task lists, /goal, background agents, vault |
| `tests/Dsh.Windows.Tests` | The computer-use tools against real windows |
| `installer/DSH.iss` | Inno Setup script |
| `scripts/` | Packaging, self-test and installer-test scripts used by CI |
| `.github/workflows` | `ci.yml` (pull requests and branches), `release.yml` (default branch → GitHub release) |

## Release pipeline

- **`ci.yml`** runs on pull requests and pushes to any branch other than the default one: core tests
  on Linux; on Windows a Release build, the full test suite (including the ConPTY, path and
  PowerShell tests, the app tests and the computer-use tests), the x64 zip and installer, the UI self-test (screenshots are uploaded as an
  artifact), and an installer test: silent install, launch the installed app, check the Start menu
  and folder-menu entries, uninstall, and check nothing is left behind.
- **`release.yml`** runs on every push to the default branch, whatever it is named: tests, packages
  x64 and ARM64 (ReadyToRun, self-contained) into installers and portable zips, runs the self-test
  and the installer test on the packaged x64 build, and publishes a GitHub release tagged
  `v<major>.<minor>.<commits>` with checksums and notes built from the commits since the previous
  release. Bump the major/minor in `Directory.Build.props` (`VersionPrefix`); the patch number
  counts itself.
