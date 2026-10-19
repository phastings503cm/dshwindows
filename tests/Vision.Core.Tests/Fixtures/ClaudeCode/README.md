# Claude Code stream-json fixtures

Real output of the official `claude` CLI 2.1.299 (the build bundled with the Claude desktop app on macOS), captured once
on 2026-10-19 for Vision's Claude Code engine. Nothing here is hand-written except `auth-status-logged-out.json`'s
paths and the path rewrite below.

How they were made, without any account:

- `not-signed-in.jsonl`, `auth-status-logged-out.json`: the CLI with an empty config folder (no sign-in, no key) —
  exactly what a person who never ran `claude auth login` gets. `auth-status-api-key.json`: the same with only
  `ANTHROPIC_API_KEY` set (Claude Code then bills that key, not a subscription).
- Every other `.jsonl`: the CLI run with `ANTHROPIC_BASE_URL` pointed at a local stand-in for the Messages API that
  streams scripted replies (text, thinking, tool calls). The CLI's own side is real: its framing, its tools really ran
  (`Write` created the file, `Edit` changed it, `Bash` ran `mkdir`), its permission prompts went over the control
  protocol (`--permission-prompt-tool stdio`, answered by a driver the way Vision answers them — see `*.sent.jsonl`
  for what the driver wrote to stdin), `--resume` reloaded the session, `/compact` compacted it, and an `interrupt`
  control request stopped a turn mid-stream. Costs in `result` are the CLI's own estimate for the stand-in's token
  counts.
- Command lines: `claude -p --input-format stream-json --output-format stream-json --verbose [--include-partial-messages]
  --model sonnet|opus|haiku --permission-mode default|acceptEdits|plan --permission-prompt-tool stdio [--resume <id>]`.

The scratch folder the captures ran in was rewritten to `/work` (project `/work/proj2`). Model ids
(`claude-sonnet-5-5`, …) and the 1,000,000-token `contextWindow` are what this CLI resolved the aliases to.
