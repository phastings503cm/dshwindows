using System.Diagnostics;
using System.Text;

namespace Dsh.Core.Tests;

/// <summary>A believable OpenClaw install in a temp folder: JSON5 config with comments and unquoted keys, a
/// .env, auth profiles, a workspace with memory and skills, and the leftovers (cron, sessions).</summary>
internal sealed class OpenClawFixture : IDisposable
{
    public const string SparkKey = "spk-live-4f8a1c9d2e7b6a305d";
    public const string OpenRouterKey = "sk-or-v1-0123456789abcdef0123456789abcdef";
    public const string AnthropicKey = "sk-ant-api03-ZmFrZWZha2VmYWtlZmFrZWZha2U";
    public const string TelegramToken = "1234567890:AAE-fakefakefakefakefakefakefake1";
    public const string GatewayToken = "gw_7d3f9a12c4e84b6fa0c1d2e3f4a5b6c7";
    public const string GithubToken = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
    public const string HuggingFaceToken = "hf_" + "abcdefghijklmnopqrstuvwxyzABCDEFGH";

    public TempDirectory Root { get; } = new("dsh-openclaw");
    public string Home { get; }
    public string State { get; }
    public string Workspace { get; }

    public OpenClawFixture(bool withEnvSpark = true)
    {
        Home = Path.Combine(Root.Path, "home", "sam");
        State = Path.Combine(Home, ".openclaw");
        Workspace = Path.Combine(State, "workspace");
        Directory.CreateDirectory(Workspace);

        Write(Path.Combine(State, "openclaw.json"), $$"""
            // OpenClaw configuration (JSON5)
            {
              identity: { name: "Clawd", emoji: "🦞", },
              agents: {
                defaults: {
                  workspace: "~/.openclaw/workspace",
                  model: { primary: "spark/qwen3-coder", fallbacks: ['openrouter/gpt-4o'] },
                },
                list: [ { id: "main", default: true }, ],
              },
              env: {
                OPENROUTER_API_KEY: "{{OpenRouterKey}}",
                LOG_LEVEL: "debug",
                vars: { HF_TOKEN: "{{HuggingFaceToken}}" },
              },
              models: {
                providers: {
                  spark: {
                    baseUrl: "http://127.0.0.1:8002/v1",
                    apiKey: "${SPARK_KEY}",
                    api: "openai-completions",
                    models: [ { id: "qwen3-coder", name: "Qwen3 Coder", contextWindow: 131072 }, "qwen3-32b" ],
                  },
                  claude: { baseUrl: "https://gw.example.com/anthropic", apiKey: "{{AnthropicKey}}", api: "anthropic-messages", models: [ { id: "claude-x" } ] },
                  ollama: { baseUrl: "http://192.168.1.50:11434/v1", apiKey: "ollama-local", models: [ { id: "llama3.3" } ] },
                },
              },
              channels: {
                telegram: { enabled: true, botToken: "{{TelegramToken}}", allowFrom: ["12345"] },
                discord: { token: "${DISCORD_TOKEN}" },
              },
              gateway: { port: 18789, auth: { mode: "token", token: "{{GatewayToken}}" } },
              skills: {
                load: { extraDirs: ["~/shared-skills"], watch: true },
                entries: { github: { enabled: true, apiKey: "{{GithubToken}}", env: { GH_HOST: "github.com" } } },
              },
              tools: { maxTokens: 4000, tokenLimit: 200000, apiKeyEnv: "OPENROUTER_API_KEY" },
              session: { secretary: "not-a-secret", tokenizer: "cl100k_base" },
            }
            """);
        Write(Path.Combine(State, ".env"), (withEnvSpark ? $"SPARK_KEY={SparkKey}\n" : "") + "PORT=18789\n# a comment\nexport BRAVE_API_KEY=\"BSA-brave-key-1234567890abcdef\"\nDEBUG=true\n");

        // Auth profiles: an API key, a token, an OAuth sign-in (not reusable) and a duplicate of the env key.
        Write(Path.Combine(State, "agents", "main", "agent", "auth-profiles.json"), $$"""
            { "version": 1, "profiles": {
                "anthropic:default": { "type": "api_key", "provider": "anthropic", "key": "{{AnthropicKey}}" },
                "openai:work": { "type": "token", "provider": "openai", "token": "sk-proj-workworkworkworkwork1234" },
                "openai-codex:default": { "type": "oauth", "provider": "openai-codex", "access": "eyJhbGciOi...", "refresh": "rt_1234", "expires": 1900000000 }
            } }
            """);
        Write(Path.Combine(State, "agents", "main", "sessions", "one.jsonl"), "{}\n");
        Write(Path.Combine(State, "agents", "main", "sessions", "two.jsonl"), "{}\n");
        Write(Path.Combine(State, "cron", "jobs.json"), """[ { "id": "a", "schedule": "0 9 * * *" }, { "id": "b" } ]""");

        // Workspace: memory, personality, skills.
        Write(Path.Combine(Workspace, "MEMORY.md"), "# Memory\n\nThe long-term notes.\n\n## Deploy\nDeploys go through scripts/deploy.ps1 against staging first.\n\n## People\nMaria owns payments; Chen owns identity.\n");
        Write(Path.Combine(Workspace, "USER.md"), "# About Sam\n\n- Prefers concise answers\n- Works in C# and TypeScript, on Windows and a DGX Spark\n");
        Write(Path.Combine(Workspace, "SOUL.md"), "You are Clawd, a cheerful space lobster who loves tidy code.\n");
        Write(Path.Combine(Workspace, "AGENTS.md"), "Use the `canvas` tool to draw diagrams. Reply on the channel you were messaged on.\n");
        // Sixty daily logs, 2026-01-01 through 2026-03-01.
        for (var i = 0; i < 60; i++)
        {
            var day = new DateTime(2026, 1, 1).AddDays(i);
            Write(Path.Combine(Workspace, "memory", $"{day:yyyy-MM-dd}.md"), $"Log for {day:yyyy-MM-dd}: worked on ticket {1000 + i}.\n");
        }

        Write(Path.Combine(Workspace, "skills", "pdf-tools", "SKILL.md"), "---\nname: pdf-tools\ndescription: Extract text and tables from PDF files\n---\n# PDF tools\nRun scripts/extract.py on the file.\n");
        Write(Path.Combine(Workspace, "skills", "pdf-tools", "scripts", "extract.py"), "print('extracting')\n");
        Write(Path.Combine(Workspace, "skills", "pdf-tools", "notes.txt"), "extra notes\n");
        Write(Path.Combine(Workspace, "skills", "weekly-report", "SKILL.md"), "---\nname: weekly-report\ndescription: Draft the weekly status report\n---\nSummarise the week's tickets.\n");
        Write(Path.Combine(Workspace, "skills", "team", "onboarding", "SKILL.md"), "---\nname: onboarding\ndescription: Onboard a new teammate\n---\nWalk through the checklist.\n");
        Write(Path.Combine(Workspace, "skills", "not-a-skill", "README.md"), "no manifest here\n");
        Write(Path.Combine(State, "skills", "pdf-tools", "SKILL.md"), "---\nname: pdf-tools\ndescription: The managed copy (shadowed by the workspace one)\n---\nold\n");
        Write(Path.Combine(State, "skills", "weather", "SKILL.md"), "---\nname: weather\ndescription: Look up the forecast\n---\nUse the weather API.\n");
        Write(Path.Combine(Home, "shared-skills", "translate", "SKILL.md"), "---\nname: translate\ndescription: Translate text between languages\n---\nTranslate.\n");
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public LocalFileSource Local(IReadOnlyDictionary<string, string>? environment = null) => new(Home, environment ?? new Dictionary<string, string>());

    /// <summary>The same folder read the way a remote machine is: through a real shell (no SFTP), with HOME
    /// pointing at the fixture's home.</summary>
    public RemoteFileSource Remote(bool withSftp = false, string host = "spark-3.local")
    {
        var shell = new LocalShell(Home);
        return new RemoteFileSource("sam@" + host, shell, withSftp ? new LocalFiles() : null, host);
    }

    public void Dispose() => Root.Dispose();
}

/// <summary>Runs commands with /bin/sh on this machine, with HOME set as given: the remote shell, without SSH.</summary>
internal sealed class LocalShell(string home) : IRemoteShell
{
    public List<string> Commands { get; } = [];

    public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Commands) Commands.Add(command);
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        info.Environment["HOME"] = home;
        info.Environment["OPENCLAW_STATE_DIR"] = "";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new RemoteResult(process.ExitCode, await output, await error);
    }

    public void Dispose()
    {
    }
}

/// <summary>SFTP stand-in: reads the local disk directly.</summary>
internal sealed class LocalFiles : IRemoteFiles
{
    public int Reads;

    public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken) =>
        new LocalFileSource("/").ListAsync(directory, cancellationToken);

    public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        return new LocalFileSource("/").ReadAsync(path, maxBytes, cancellationToken);
    }

    public void Dispose()
    {
    }
}
