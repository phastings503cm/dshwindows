using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - What a shell command's text can tell the stall detector
//
// A run that repeats one command and gets the same answer is going in circles — unless something changed in between. The
// detector cannot see what a shell command did, only what it says, so it reads the text for three things:
//  * that it WAITS (sleep, a watch, a wait): polling a build or a deploy looks the same until it finishes, which is what
//    polling is for;
//  * that it CHANGES FILES (sed -i, mv, a redirect into a file, an install, git checkout, a formatter): a check that ran before
//    such a command can honestly answer differently after it;
//  * that it MIGHT change files: it runs a script, a generator or a task-runner target that nobody here can read, and what
//    such a thing does is anyone's guess — so, like an edit, it counts as progress the first time it is seen.
// All three are read where a command actually is — the first word of each piece of the line (between | ; && || & { } ( ) and
// line breaks, after `sudo`, `then`, `timeout 30` and the like), with anything quoted, commented or in a heredoc blanked out
// — so `grep "a -> b"`, `pytest tests/watch` and `echo rm` are none of them. The line is read the way its own shell reads
// it: bash, PowerShell and cmd escape, comment and continue lines differently. It is a heuristic; what it misses only costs
// the run being judged more strictly.

/// <summary>What the text of a shell command says about it.</summary>
/// <param name="Waits">It waits.</param>
/// <param name="Writes">It changes files.</param>
/// <param name="MightWrite">It runs code the harness cannot read, which may well have changed files.</param>
internal readonly record struct ShellReading(bool Waits, bool Writes, bool MightWrite)
{
    public static ShellReading operator |(ShellReading a, ShellReading b) =>
        new(a.Waits || b.Waits, a.Writes || b.Writes, a.MightWrite || b.MightWrite);
}

internal static class ShellHeuristics
{
    /// <summary>How much of a command is read: the rest of a longer one is not looked at (it is a heredoc body, as a rule).</summary>
    private const int MaxLength = 20_000;

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    /// <summary>A redirect into a file: `> out`, `>> log`, `2> err`, `1>out`, `echo x>out` — not into nothing (`/dev/null`, `nul`,
    /// `$null`), not a duplicated descriptor (`2>&1`), and only where a redirect can start (not the `>` of `->`, `=>`, `>=`
    /// or `List&lt;Foo&gt;`).</summary>
    private static readonly Regex Redirect = new(@"(?<![-=<>])(?<!<[^\s<>]*)\d?(?>>{1,2})(?!\s*(?:&|/dev/null\b|nul\b|\$null\b))\s*[^\s>|&;=]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Limit);

    /// <summary>PowerShell reaching straight into .NET to write: `[IO.File]::WriteAllText(...)`.</summary>
    private static readonly Regex DotNetWrite = new(@"\[(?:system\.)?io\.(?:file|directory)\]::(?:write|append|delete|move|copy|create|replace|set)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Limit);

    private static readonly Regex DotNetSleep = new(@"thread\]::sleep\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Limit);

    /// <summary>Inline code (a `python - &lt;&lt;EOF` script, a `node -e '…'`) that says it writes.</summary>
    private static readonly Regex WritingCode = new(
        @"\bopen\s*\([^)\n]*,\s*['""][^'""\n]*[wax+]|\b(?:write_text|write_bytes|writeFile(?:Sync)?|appendFile(?:Sync)?|createWriteStream|shutil\.(?:copy|move|rmtree)\w*|os\.(?:remove|rename|unlink|makedirs|mkdir|rmdir|replace)|fs\.(?:write|append|rm|unlink|mkdir|rename|copy)\w*|File\.(?:Write|Append|Delete|Move|Copy|Create)\w*|Set-Content|Add-Content|Out-File|Remove-Item|New-Item|Copy-Item|Move-Item)\b|\.write\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Limit);

    private static readonly Regex InPlaceFlag = new(@"^-[a-zA-Z]*i[a-zA-Z.]*$", RegexOptions.CultureInvariant, Limit);

    private static readonly HashSet<string> WaitingHeads = new(StringComparer.Ordinal)
    {
        "sleep", "usleep", "start-sleep", "watch", "wait-process", "wait-job", "wait-event", "pause",
    };

    private static readonly HashSet<string> WritingHeads = new(StringComparer.Ordinal)
    {
        "mv", "cp", "rm", "rmdir", "mkdir", "touch", "patch", "truncate", "ln", "install", "chmod", "chown", "tee", "rsync", "scp",
        "zip", "unzip", "7z", "7za", "gzip", "gunzip", "bzip2", "bunzip2", "xz", "unxz", "dos2unix", "unix2dos", "sponge",
        "copy", "move", "del", "erase", "ren", "rename", "rd", "md", "xcopy", "robocopy", "attrib", "icacls", "mklink",
        "set-content", "add-content", "out-file", "new-item", "remove-item", "copy-item", "move-item", "rename-item",
        "clear-content", "expand-archive", "compress-archive", "export-csv", "export-clixml", "tee-object",
        "ri", "rni", "ni", "mi", "cpi",
        "black", "isort", "autopep8", "yapf", "rustfmt", "swiftformat", "wget",
    };

    /// <summary>Programs that run another command line: bash -c '…', powershell -Command …, cmd /c …, wsl ….</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "powershell", "pwsh", "cmd", "bash", "sh", "zsh", "dash", "fish", "ksh", "wsl",
    };

    /// <summary>Programs that just run the rest of the line (`sudo -u root mv a b`), with the options that take a value.</summary>
    private static readonly Dictionary<string, string[]> Prefixes = new(StringComparer.Ordinal)
    {
        ["sudo"] = ["-u", "-g", "-C", "-h", "-p", "-r", "-t", "-U", "-D", "-R", "-T", "--user", "--group"],
        ["doas"] = ["-u", "-C"],
        ["env"] = ["-u", "-C", "-S", "--unset", "--chdir"],
        ["time"] = ["-f", "-o", "--format", "--output"],
        ["nice"] = ["-n", "--adjustment"],
        ["ionice"] = ["-c", "-n", "-p"],
        ["nohup"] = [],
        ["exec"] = ["-a"],
        ["command"] = [],
        ["call"] = [],
        ["builtin"] = [],
        ["setsid"] = [],
        ["stdbuf"] = ["-i", "-o", "-e"],
        ["timeout"] = ["-k", "-s", "--kill-after", "--signal"],
        ["xargs"] = ["-n", "-I", "-P", "-L", "-d", "-E", "-s", "-a", "-J"],
    };

    /// <summary>Words that come before a command without being one: `then rm -rf build`, `do sleep 1`, `! grep -q x f`.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "elseif", "fi", "do", "done", "while", "until", "for", "foreach", "case", "esac", "select",
        "function", "!", "begin", "end", "try", "catch", "finally", "switch", "return", "exit", "break", "continue", "throw",
    };

    private static readonly HashSet<string> PackageManagers = new(StringComparer.Ordinal)
    {
        "npm", "pnpm", "yarn", "bun", "deno", "pip", "pip3", "cargo", "dotnet", "go", "gem", "bundle", "composer", "apt", "apt-get",
        "brew", "winget", "choco", "scoop", "nuget", "poetry", "uv", "pipenv", "pdm", "conda", "dart", "flutter", "pod", "yum", "dnf",
        "apk", "pacman",
    };

    private static readonly HashSet<string> ChangingSubcommands = new(StringComparer.Ordinal)
    {
        "install", "i", "add", "remove", "rm", "uninstall", "update", "upgrade", "get", "restore", "fmt", "format", "tidy", "fix",
        "new", "init", "create", "ci", "clean", "generate", "sync", "lock", "require", "link", "unlink", "dedupe", "prune", "rebuild",
    };

    /// <summary>Sub-commands that take another sub-command: `go mod tidy`, `dotnet tool install`, `dotnet package add`.</summary>
    private static readonly HashSet<string> ParentSubcommands = new(StringComparer.Ordinal)
    {
        "mod", "tool", "workload", "nuget", "sln", "package", "reference", "cache", "pip",
    };

    private static readonly HashSet<string> ChangingGit = new(StringComparer.Ordinal)
    {
        "apply", "am", "checkout", "restore", "switch", "stash", "reset", "clean", "rm", "mv", "merge", "rebase", "pull", "cherry-pick",
        "revert", "clone", "init", "submodule", "worktree",
    };

    /// <summary>Interpreters: given a script they run code nobody here can read; given inline code they run what is quoted.</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "python", "pypy", "node", "nodejs", "tsx", "ts-node", "ruby", "php", "lua", "luajit", "rscript", "julia", "groovy", "perl",
        "deno", "bun", "java",
    };

    /// <summary>Task runners: what a target does is up to its file; the usual check-like ones are told apart by name.</summary>
    private static readonly HashSet<string> TaskRunners = new(StringComparer.Ordinal)
    {
        "make", "gmake", "nmake", "rake", "just", "task", "mage", "invoke", "fab", "gradle", "gradlew", "mvn", "mvnw", "ant", "sbt",
        "bazel", "bazelisk", "buck", "pants", "cake", "nuke",
    };

    /// <summary>Well-known tools that only check, build or read — a path to one of them (`./node_modules/.bin/tsc`) is not a project script.</summary>
    private static readonly HashSet<string> CheckTools = new(StringComparer.Ordinal)
    {
        "tsc", "eslint", "prettier", "jest", "vitest", "mocha", "ava", "playwright", "cypress", "karma", "jasmine", "stylelint",
        "pytest", "mypy", "pyright", "ruff", "flake8", "pylint", "pycodestyle", "bandit", "tox", "nox", "coverage",
        "rspec", "rubocop", "phpunit", "phpstan", "psalm", "shellcheck", "hadolint", "golangci-lint", "staticcheck",
        "rustc", "javac", "gcc", "g++", "clang", "clang++", "cc", "c++", "msbuild", "xcodebuild", "cmake", "ninja", "meson",
        "ls", "dir", "cat", "type", "grep", "rg", "find", "echo", "git", "gh", "jq", "diff", "head", "tail", "wc", "docker",
        "kubectl", "terraform", "curl", "ping", "sleep",
    };

    private static readonly HashSet<string> KnownTools = BuildKnownTools();

    private static HashSet<string> BuildKnownTools()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in new[] { WaitingHeads, WritingHeads, Wrappers, PackageManagers, Interpreters, TaskRunners, CheckTools })
            known.UnionWith(set);
        known.UnionWith(Prefixes.Keys);
        return known;
    }

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.Ordinal)
    {
        ".sh", ".bash", ".zsh", ".py", ".ps1", ".psm1", ".bat", ".cmd", ".js", ".mjs", ".cjs", ".ts", ".rb", ".pl", ".php", ".lua", ".fsx", ".csx",
    };

    /// <summary>Modules `python -m …` runs that check, list or install rather than run project code.</summary>
    private static readonly HashSet<string> CheckModules = new(StringComparer.Ordinal)
    {
        "pytest", "unittest", "pyflakes", "flake8", "pylint", "mypy", "pycodestyle", "pydocstyle", "coverage", "doctest", "py_compile",
        "compileall", "ruff", "tox", "nox", "bandit", "pyright", "pip", "json", "timeit", "cprofile", "trace", "site", "sysconfig",
        "platform", "http", "ensurepip",
    };

    /// <summary>Tools `npx` / `bunx` / `dlx` run that only check.</summary>
    private static readonly string[] CheckNames =
    [
        "test", "lint", "check", "typecheck", "type-check", "tsc", "build", "compile", "verify", "validate", "vet", "clippy", "analy",
        "spec", "e2e", "coverage", "cover", "unit", "integration", "bench", "format:check", "fmt:check", "prettier:check", "doctor",
        "audit", "start", "dev", "serve", "watch", "preview", "storybook",
        // Build-tool goals that build rather than run.
        "clean", "install", "package", "assemble", "dependencies", "tasks",
    ];

    private static readonly HashSet<string> CheckNamesExact = new(StringComparer.Ordinal)
    {
        "ci", "all", "default", "help", "jar", "types", "docs",
    };

    // MARK: Entry points

    /// <summary>Read a command line. <paramref name="kind"/> is the shell it is written for.</summary>
    public static ShellReading Read(string command, ShellKind kind = ShellKind.Bash) => Analyze(command, kind, 0);

    /// <summary>Whether the command waits (polling with a sleep or a watch is not a hot loop).</summary>
    public static bool Waits(string command, ShellKind kind = ShellKind.Bash) => Read(command, kind).Waits;

    /// <summary>Whether the command looks like it changes files.</summary>
    public static bool ChangesFiles(string command, ShellKind kind = ShellKind.Bash) => Read(command, kind).Writes;

    /// <summary>Whether the command runs something (a script, a generator, a task) that may change files.</summary>
    public static bool MightChangeFiles(string command, ShellKind kind = ShellKind.Bash) => Read(command, kind).MightWrite;

    private static ShellReading Analyze(string command, ShellKind kind, int depth)
    {
        if (string.IsNullOrWhiteSpace(command)) return default;
        var raw = JoinContinuations(command.Length > MaxLength ? command[..MaxLength] : command, kind);
        var lexed = Lex(raw, kind);
        var writes = RedirectsIntoFile(lexed.Blanked)
                     || (kind == ShellKind.PowerShell && Matches(DotNetWrite, lexed.Blanked));
        var reading = new ShellReading(kind == ShellKind.PowerShell && Matches(DotNetSleep, lexed.Blanked), writes, false);
        foreach (var (start, end) in Segments(lexed.Blanked))
            reading |= Segment(raw, lexed, start, end, kind, depth);
        return reading;
    }

    // MARK: One piece of the line

    private static ShellReading Segment(string raw, Lexed lexed, int start, int end, ShellKind kind, int depth)
    {
        var words = Words(lexed.Blanked, start, end);
        var at = 0;
        var viaPackage = false; // run by npx / dlx: a package that could be anything
        for (var guard = 0; guard <= words.Count && at < words.Count; guard++)
        {
            var (text, index) = words[at];
            var lower = text.ToLowerInvariant();
            if (kind == ShellKind.Cmd && lower is "if" or "for")
            {
                at = lower == "if" ? SkipCmdCondition(words, at + 1) : AfterDo(words, at + 1);
                continue;
            }
            if (IsAssignment(text) || Keywords.Contains(lower))
            {
                at++;
                continue;
            }

            // The program: a plain word, or a quoted path (`"C:\Program Files\Git\bin\git.exe" status`).
            var rawProgram = text;
            var next = at + 1;
            if ((text[0] is '"' or '\'') && lexed.QuoteAt(index) is var q and >= 0)
            {
                var (open, close) = lexed.Quotes[q];
                rawProgram = raw[(open + 1)..Math.Min(close, raw.Length)];
                while (next < words.Count && words[next].Index <= close) next++;
            }
            var program = ProgramOf(rawProgram);

            if (Prefixes.TryGetValue(program, out var valueFlags) && !IsWindowsTimeout(program, words, next))
            {
                at = SkipOptions(words, next, valueFlags, program == "timeout" ? 1 : 0);
                continue;
            }
            if (Wrappers.Contains(program))
                return depth < 2 ? Wrapper(raw, lexed, words, next, program, end, kind, depth) : default;
            if (Launched(program, words, next) is { } launched)
            {
                at = launched.At;
                viaPackage |= launched.Package;
                continue;
            }
            return Classify(raw, lexed, start, end, rawProgram, program, Rest(words, next), viaPackage);
        }
        return default;
    }

    private static List<string> Rest(List<Word> words, int from)
    {
        var rest = new List<string>(Math.Max(0, words.Count - from));
        for (var i = from; i < words.Count; i++) rest.Add(words[i].Text);
        return rest;
    }

    private static ShellReading Classify(string raw, Lexed lexed, int start, int end, string rawProgram, string program,
                                         List<string> rest, bool viaPackage)
    {
        var waits = WaitsFor(program, rest);
        // `black --check .`, `Remove-Item -WhatIf`, `git clean -n`: says what it would do, does nothing.
        if (IsDryRun(program, rest)) return new ShellReading(waits, false, false);
        var writes = ChangesThings(program, rest, 0) || InlineCodeWrites(raw, lexed, start, end, program);
        var might = !writes && (RunsUnseenCode(rawProgram, program, rest) || (viaPackage && !KnownTools.Contains(program)));
        return new ShellReading(waits, writes, might);
    }

    // MARK: Programs that run other programs

    private static bool IsWindowsTimeout(string program, List<Word> words, int from)
    {
        if (program != "timeout") return false;
        for (var i = from; i < words.Count; i++)
        {
            if (words[i].Text.Equals("/t", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Step over the options of a prefix program (and, for `timeout`, its duration).</summary>
    private static int SkipOptions(List<Word> words, int from, string[] valueFlags, int positional)
    {
        var i = from;
        while (i < words.Count)
        {
            var w = words[i].Text;
            if (w == "--")
            {
                i++;
                break;
            }
            if (w.Length > 1 && w[0] == '-')
            {
                i += Array.IndexOf(valueFlags, w) >= 0 ? 2 : 1;
                continue;
            }
            if (positional > 0 && !IsAssignment(w))
            {
                positional--;
                i++;
                continue;
            }
            break;
        }
        return Math.Min(i, words.Count);
    }

    /// <summary>`bundle exec rspec`, `uv run pytest`, `npx tsc`, `pnpm dlx create-vite`: where the command that is really run starts.</summary>
    private static (int At, bool Package)? Launched(string program, List<Word> words, int from)
    {
        var first = NextPlainWord(words, from);
        if (first < 0) return null;
        var verb = words[first].Text.ToLowerInvariant();
        var package = false;
        switch (program)
        {
            case "npx" or "pnpx" or "bunx":
                return (first, true);
            case "npm" when verb is "exec" or "x":
            case "pnpm" when verb is "exec" or "dlx":
            case "yarn" when verb is "exec" or "dlx":
            case "bun" when verb is "x":
                package = verb is not "exec" || program == "npm";
                break;
            case "bundle" when verb == "exec":
            case "poetry" or "uv" or "pipenv" or "pdm" or "hatch" or "rye" or "conda" or "mamba" when verb == "run":
                break;
            default:
                return null;
        }
        var command = NextPlainWord(words, first + 1);
        return command < 0 ? null : (command, package);
    }

    /// <summary>The index of the first word from <paramref name="from"/> that is not an option (or a quote mark).</summary>
    private static int NextPlainWord(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            if (words[i].Text.Length > 0 && words[i].Text[0] != '-') return i;
        }
        return -1;
    }

    private static int NextPlainWord(List<string> rest, int from)
    {
        for (var i = from; i < rest.Count; i++)
        {
            if (rest[i].Length > 0 && rest[i][0] != '-') return i;
        }
        return -1;
    }

    /// <summary>`bash -c '…'`, `powershell -Command …`, `cmd /c …`, `wsl …`: read the command line it runs; a script it runs is unseen code.</summary>
    private static ShellReading Wrapper(string raw, Lexed lexed, List<Word> words, int from, string program, int end,
                                        ShellKind kind, int depth)
    {
        var inner = program switch
        {
            "powershell" or "pwsh" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Bash,
        };
        var (commandAt, script) = program switch
        {
            "cmd" => CmdCommand(words, from),
            "powershell" or "pwsh" => PowerShellCommand(words, from),
            "wsl" => WslCommand(words, from),
            _ => BashCommand(words, from),
        };
        if (commandAt >= 0 && commandAt < words.Count)
        {
            var (_, index) = words[commandAt];
            var quote = lexed.QuoteAt(index);
            var line = quote >= 0
                ? raw[(lexed.Quotes[quote].Open + 1)..Math.Min(lexed.Quotes[quote].Close, raw.Length)]
                : raw[index..Math.Min(end, raw.Length)];
            return Analyze(line, inner, depth + 1);
        }
        return script ? new ShellReading(false, false, true) : default;
    }

    private static (int At, bool Script) BashCommand(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            var w = words[i].Text;
            if (w == "--") return (-1, i + 1 < words.Count);
            if (w is "-o" or "+o" or "-O" or "+O")
            {
                i++;
                continue;
            }
            if (w.StartsWith("--", StringComparison.Ordinal)) continue;
            if (w.Length > 1 && w[0] == '-')
            {
                if (w.Contains('c')) return (i + 1, false);
                continue;
            }
            return (-1, w != "-");
        }
        return (-1, false);
    }

    private static (int At, bool Script) PowerShellCommand(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            var w = words[i].Text.ToLowerInvariant();
            if (w is "-command" or "-c") return (i + 1, false);
            if (w is "-file" or "-f") return (-1, true);
            if (w is "-encodedcommand" or "-e" or "-ec") return (-1, false);
            if (w is "-executionpolicy" or "-ep" or "-windowstyle" or "-workingdirectory" or "-wd" or "-version" or "-inputformat"
                or "-outputformat" or "-configurationname")
            {
                i++;
                continue;
            }
            if (w.Length > 1 && w[0] == '-') continue;
            return (i, false); // `powershell Start-Sleep 5`
        }
        return (-1, false);
    }

    private static (int At, bool Script) CmdCommand(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            var w = words[i].Text.ToLowerInvariant();
            if (w is "/c" or "/k" or "/r") return (i + 1, false);
            if (w.Length > 1 && w[0] == '/') continue; // /d /s /q /a /u /v:on …
            return (-1, false);
        }
        return (-1, false);
    }

    private static (int At, bool Script) WslCommand(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            var w = words[i].Text.ToLowerInvariant();
            if (w is "-d" or "--distribution" or "-u" or "--user" or "--cd")
            {
                i++;
                continue;
            }
            if (w is "-e" or "--exec" or "--") return (i + 1, false);
            if (w.Length > 1 && w[0] == '-') continue;
            return (i, false);
        }
        return (-1, false);
    }

    /// <summary>cmd's `if [not] exist X …`, `if errorlevel 1 …`, `if defined X …`: the words before the command.</summary>
    private static int SkipCmdCondition(List<Word> words, int from)
    {
        var i = from;
        if (i < words.Count && words[i].Text.Equals("/i", StringComparison.OrdinalIgnoreCase)) i++;
        if (i < words.Count && words[i].Text.Equals("not", StringComparison.OrdinalIgnoreCase)) i++;
        if (i < words.Count && words[i].Text.ToLowerInvariant() is "exist" or "defined" or "errorlevel" or "cmdextversion") return Math.Min(i + 2, words.Count);
        // A comparison: `a==b` is one word; `a equ b` is three.
        if (i < words.Count && words[i].Text.Contains("==", StringComparison.Ordinal)) return i + 1;
        return Math.Min(i + 3, words.Count);
    }

    /// <summary>cmd's `for %i in (…) do command`: the word after `do`.</summary>
    private static int AfterDo(List<Word> words, int from)
    {
        for (var i = from; i < words.Count; i++)
        {
            if (words[i].Text.Equals("do", StringComparison.OrdinalIgnoreCase)) return i + 1;
        }
        return words.Count;
    }

    // MARK: What a program does

    private static bool WaitsFor(string program, List<string> rest)
    {
        if (WaitingHeads.Contains(program)) return true;
        switch (program)
        {
            case "timeout": // the Windows one: `timeout /t 30` (the GNU one is a prefix and never gets here)
                return rest.Any(w => w.Equals("/t", StringComparison.OrdinalIgnoreCase));
            case "ping": // a count of packets is a pause
                for (var i = 0; i < rest.Count; i++)
                {
                    if (rest[i] == "-t") return true;
                    if (rest[i] is "-n" or "-c" && i + 1 < rest.Count && int.TryParse(rest[i + 1], out var count) && count >= 3) return true;
                }
                return false;
            case "gh":
                return rest.Count >= 2 && rest[0] == "run" && rest[1] == "watch";
            case "kubectl":
                return rest.Count >= 1 && (rest[0] == "wait" || (rest.Count >= 2 && rest[0] == "rollout" && rest[1] == "status"));
            default:
                return false;
        }
    }

    private static bool IsDryRun(string program, List<string> rest)
    {
        foreach (var word in rest)
        {
            if (word.ToLowerInvariant() is "--dry-run" or "--dryrun" or "--check" or "--check-only" or "-check" or "--verify-no-changes"
                or "-whatif" or "--whatif" or "--what-if" or "--list-different")
                return true;
        }
        return program is "git" or "rsync" or "make" or "gmake" && rest.Contains("-n");
    }

    private static bool ChangesThings(string program, List<string> rest, int depth)
    {
        if (WritingHeads.Contains(program)) return program != "unzip" || !rest.Any(w => w is "-l" or "-t" or "-v" or "-Z" or "-z");
        if (rest.Any(w => w is "--write" or "--fix" or "--in-place" || w.Equals("-outfile", StringComparison.OrdinalIgnoreCase))) return true;
        switch (program)
        {
            case "curl":
                return CurlWritesAFile(rest);
            case "sed":
                return rest.Any(w => Regex.IsMatch(w, @"^-[a-zA-Z]*i", RegexOptions.CultureInvariant, Limit));
            case "perl" or "ruby":
                return rest.Any(w => Matches(InPlaceFlag, w));
            case "awk" or "gawk":
                return rest.Contains("inplace");
            case "gofmt":
                return rest.Contains("-w");
            case "clang-format":
                return rest.Contains("-i");
            case "ruff":
                return rest.Contains("format");
            case "python":
                return PythonModuleWrites(rest);
            case "tar":
                return TarWrites(rest);
            case "find":
                if (rest.Contains("-delete")) return true;
                var exec = rest.FindIndex(w => w is "-exec" or "-execdir" or "-ok" or "-okdir");
                return exec >= 0 && exec + 1 < rest.Count && depth < 2
                       && ChangesThings(ProgramOf(rest[exec + 1]), rest.GetRange(exec + 2, rest.Count - exec - 2), depth + 1);
            case "git":
                return GitChangesFiles(rest);
            default:
                return PackageManagers.Contains(program) && PackageChangesThings(program, rest);
        }
    }

    /// <summary>`python -m pip install x`, `python -m black .`, `python -m venv .venv`.</summary>
    private static bool PythonModuleWrites(List<string> rest)
    {
        var at = rest.IndexOf("-m");
        if (at < 0 || at + 1 >= rest.Count) return false;
        var module = rest[at + 1].ToLowerInvariant();
        if (module is "black" or "isort" or "autopep8" or "yapf" or "venv") return true;
        return module == "pip" && PackageChangesThings("pip", rest.GetRange(at + 2, rest.Count - at - 2));
    }

    private static bool CurlWritesAFile(List<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var w = rest[i];
            if (w is "-O" or "--remote-name" or "--remote-name-all") return true;
            if (w is "-o" or "--output" or "--output-dir")
            {
                // `-o /dev/null` (with -w '%{http_code}') only asks for a status.
                if (i + 1 >= rest.Count || !IsNullDevice(rest[i + 1])) return true;
                i++;
            }
        }
        return false;
    }

    private static bool IsNullDevice(string word) => word.ToLowerInvariant() is "/dev/null" or "nul" or "$null" or "-";

    private static bool TarWrites(List<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var w = rest[i];
            if (w.StartsWith("--", StringComparison.Ordinal))
            {
                if (w is "--extract" or "--get" or "--create" or "--append" or "--update" or "--delete") return true;
                continue;
            }
            // `tar -xzf a.tgz`, `tar xzf a.tgz`: x extracts, c/r/u write an archive; t only lists.
            if (w.Length > 1 && (w[0] == '-' || i == 0) && w.Skip(w[0] == '-' ? 1 : 0).Any(c => c is 'x' or 'c' or 'r' or 'u')) return true;
        }
        return false;
    }

    private static bool GitChangesFiles(List<string> rest)
    {
        var at = SubcommandAt(rest);
        if (at < 0) return false;
        var sub = rest[at].ToLowerInvariant();
        if (sub == "stash") return !(at + 1 < rest.Count && rest[at + 1] is "list" or "show"); // `git stash list` only reads
        return ChangingGit.Contains(sub);
    }

    private static bool PackageChangesThings(string program, List<string> rest)
    {
        var at = SubcommandAt(rest);
        if (at < 0) return program == "yarn" && !rest.Any(w => w is "--version" or "-v" or "--help" or "-h"); // a bare `yarn` installs
        var sub = rest[at].ToLowerInvariant();
        if (ChangingSubcommands.Contains(sub)) return true;
        return ParentSubcommands.Contains(sub) && at + 1 < rest.Count && ChangingSubcommands.Contains(rest[at + 1].ToLowerInvariant());
    }

    /// <summary>The index of the first word of the arguments that is not an option or an option's value (`git -C repo status` → status).</summary>
    private static int SubcommandAt(List<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var word = rest[i].ToLowerInvariant();
            if (word is "-c" or "--git-dir" or "--work-tree" or "--prefix" or "--cwd") i++; // takes a value
            else if (!word.StartsWith('-')) return i;
        }
        return -1;
    }

    // MARK: Code nobody here can read

    /// <summary>A script, a generator or a task-runner target: something that may change files without saying so.</summary>
    private static bool RunsUnseenCode(string rawProgram, string program, List<string> rest)
    {
        if (LooksLikeScript(rawProgram, program)) return true;
        switch (program)
        {
            case "python":
                return PythonRunsScript(rest);
            case "node" or "nodejs" or "tsx" or "ts-node" or "ruby" or "php" or "lua" or "luajit" or "rscript" or "julia" or "groovy" or "perl":
                return !HasInlineFlag(rest) && NextPlainWord(rest, 0) is var script and >= 0 && !IsQuoteMark(rest[script]);
            case "deno" or "bun":
                return DenoOrBunRunsCode(rest);
            case "java":
                return rest.Contains("-jar") || (NextPlainWord(rest, 0) is var main and >= 0 && !rest.Contains("-version"));
            case "dotnet":
                return NextPlainWord(rest, 0) is var sub and >= 0 && rest[sub].ToLowerInvariant() is "run" or "watch" or "fsi" or "script" or "ef" or "exec" or "user-secrets";
            case "go":
                return NextPlainWord(rest, 0) is var verb and >= 0 && rest[verb] == "run";
            case "cargo":
                return NextPlainWord(rest, 0) is var cmd and >= 0 && rest[cmd] is "run" or "r" or "xtask";
            case "npm" or "pnpm" or "yarn":
                return ScriptRunnerRunsCode(program, rest);
            default:
                return TaskRunners.Contains(program) && RunnerTargetRunsCode(rest);
        }
    }

    /// <summary>`./gen.sh`, `tools/gen.py`, `.\build.ps1`, `build.bat`: a script by its path or its name.</summary>
    private static bool LooksLikeScript(string rawProgram, string program)
    {
        if (rawProgram.Length == 0 || KnownTools.Contains(program)) return false;
        var word = rawProgram.Replace('\\', '/');
        if (ScriptExtensions.Contains(Path.GetExtension(word).ToLowerInvariant())) return true;
        // A relative path into the project (`./bin/gen`, `tools/gen`); an absolute one is usually an installed tool.
        return word.Contains('/') && word[0] != '/' && word[0] != '~' && !(word.Length > 1 && word[1] == ':');
    }

    private static bool IsQuoteMark(string word) => word.Length > 0 && word.All(c => c is '"' or '\'');

    private static bool HasInlineFlag(List<string> rest) => rest.Any(w => w is "-c" or "-e" or "-E" or "-p" or "-r" or "--eval" or "--print");

    private static bool PythonRunsScript(List<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var w = rest[i];
            if (w is "-c" or "--version" or "-V" or "-h" or "--help" or "-") return false;
            if (w == "-m")
            {
                var module = i + 1 < rest.Count ? rest[i + 1].ToLowerInvariant() : "";
                var top = module.Split('.')[0];
                // The formatters and virtual environments are handled as writers elsewhere; anything else is somebody's module.
                return !CheckModules.Contains(top) && top is not ("black" or "isort" or "autopep8" or "yapf" or "venv");
            }
            if (w.Length > 0 && w[0] != '-' && !IsQuoteMark(w)) return true;
        }
        return false;
    }

    private static bool DenoOrBunRunsCode(List<string> rest)
    {
        var at = NextPlainWord(rest, 0);
        if (at < 0) return false;
        var sub = rest[at].ToLowerInvariant();
        if (sub is "run" or "task") return at + 1 < rest.Count && !IsCheckName(rest[at + 1]);
        if (sub is "test" or "lint" or "check" or "info" or "doc" or "types" or "eval" or "bench" or "fmt" or "x" or "pm" or "install"
            or "add" or "remove" or "init" or "upgrade" or "update" or "create" or "outdated" or "link" or "unlink" or "publish")
            return false;
        return !IsQuoteMark(rest[at]); // a script file
    }

    /// <summary>`npm run build` is a check; `npm run codegen` is unseen code. `yarn build` / `pnpm test` are the same without `run`.</summary>
    private static bool ScriptRunnerRunsCode(string program, List<string> rest)
    {
        var at = NextPlainWord(rest, 0);
        if (at < 0) return false;
        var sub = rest[at].ToLowerInvariant();
        if (sub is "run" or "run-script" or "rum" or "urn")
        {
            var name = NextPlainWord(rest, at + 1);
            return name >= 0 && !IsCheckName(rest[name]);
        }
        if (program == "npm") return false; // npm needs `run`; its other commands are its own
        if (NpmLikeBuiltins.Contains(sub) || IsQuoteMark(sub)) return false;
        return !IsCheckName(sub); // `yarn codegen`
    }

    private static readonly HashSet<string> NpmLikeBuiltins = new(StringComparer.Ordinal)
    {
        "install", "i", "add", "remove", "rm", "upgrade", "update", "up", "dlx", "exec", "run", "test", "t", "why", "list", "ls", "info", "view",
        "audit", "outdated", "workspaces", "workspace", "config", "cache", "init", "create", "link", "unlink", "pack", "publish",
        "version", "help", "node", "set", "bin", "dedupe", "prune", "rebuild", "store", "patch", "import", "fetch", "deploy", "env",
        "licenses", "recursive", "self-update", "global", "check", "start", "--version", "-v",
    };

    /// <summary>`make` alone, `make test`, `gradle build`: builds. `make gen`, `just release`: whatever the file says.</summary>
    private static bool RunnerTargetRunsCode(List<string> rest)
    {
        foreach (var word in rest)
        {
            if (word.Length == 0 || word[0] == '-' || IsQuoteMark(word) || word.Contains('=')) continue;
            if (!IsCheckName(word)) return true;
        }
        return false;
    }

    private static bool IsCheckName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (CheckNamesExact.Contains(lower)) return true;
        foreach (var prefix in CheckNames)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return lower.Contains(":test", StringComparison.Ordinal) || lower.Contains(":lint", StringComparison.Ordinal)
               || lower.Contains(":check", StringComparison.Ordinal) || lower.Contains(":build", StringComparison.Ordinal);
    }

    /// <summary>An interpreter given code to run (`python -c '…'`, a heredoc): it writes if the code says it does.</summary>
    private static bool InlineCodeWrites(string raw, Lexed lexed, int start, int end, string program)
    {
        if (!Interpreters.Contains(program) || program == "java") return false;
        for (var q = lexed.FirstQuoteFrom(start); q < lexed.Quotes.Count && lexed.Quotes[q].Open < end; q++)
        {
            var (open, close) = lexed.Quotes[q];
            if (Matches(WritingCode, raw[(open + 1)..Math.Min(close, raw.Length)])) return true;
        }
        foreach (var (marker, body) in lexed.Heredocs)
        {
            if (marker >= start && marker < end && Matches(WritingCode, body)) return true;
        }
        return false;
    }

    // MARK: Reading the line

    private readonly record struct Word(string Text, int Index);

    /// <summary>The line as one of its shell's own reads it: the inside of every string, comment and heredoc replaced by spaces
    /// (same length, so positions still line up), and where the strings and heredocs were.</summary>
    private sealed class Lexed(string blanked, List<(int Open, int Close)> quotes, List<(int Marker, string Body)> heredocs)
    {
        public string Blanked { get; } = blanked;

        /// <summary>Every '…' and "…" in the code (not in a comment or a heredoc), as the positions of its quote marks — Close is
        /// the length of the text when the string never ends — in order.</summary>
        public List<(int Open, int Close)> Quotes { get; } = quotes;

        /// <summary>The heredocs: where their `&lt;&lt;` is, and what they hold.</summary>
        public List<(int Marker, string Body)> Heredocs { get; } = heredocs;

        public int FirstQuoteFrom(int position)
        {
            int low = 0, high = Quotes.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (Quotes[mid].Open < position) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        public int QuoteAt(int position)
        {
            var q = FirstQuoteFrom(position);
            return q < Quotes.Count && Quotes[q].Open == position ? q : -1;
        }
    }

    /// <summary>A backslash (bash), a backtick (PowerShell) or a caret (cmd) before a line break joins the two lines.</summary>
    private static string JoinContinuations(string command, ShellKind kind)
    {
        var mark = kind switch { ShellKind.PowerShell => '`', ShellKind.Cmd => '^', _ => '\\' };
        if (command.IndexOf(mark) < 0) return command;
        var text = command.ToCharArray();
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != mark) continue;
            if (text[i + 1] == '\n')
            {
                text[i] = text[i + 1] = ' ';
                i++;
            }
            else if (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')
            {
                text[i] = text[i + 1] = text[i + 2] = ' ';
                i += 2;
            }
        }
        return new string(text);
    }

    private static Lexed Lex(string raw, ShellKind kind)
    {
        var n = raw.Length;
        var text = raw.ToCharArray();
        var quotes = new List<(int, int)>();
        var heredocs = new List<(int, string)>();
        var pending = new List<(int Marker, string Delimiter, bool StripTabs)>();

        void Blank(int from, int to)
        {
            for (var k = Math.Max(from, 0); k < to && k < n; k++) text[k] = ' ';
        }

        var i = 0;
        while (i < n)
        {
            var c = raw[i];
            if (c == '\n' && pending.Count > 0)
            {
                i = SkipHeredocBodies(raw, text, i + 1, pending, heredocs);
                continue;
            }
            switch (c)
            {
                case '\\' when kind == ShellKind.Bash:
                case '`' when kind == ShellKind.PowerShell:
                case '^' when kind == ShellKind.Cmd:
                    Blank(i, i + 2); // an escaped character: not a quote, not a separator
                    i += 2;
                    continue;
                case '\'' when kind != ShellKind.Cmd:
                case '"':
                    var close = CloseOfQuote(raw, i, kind);
                    quotes.Add((i, close));
                    Blank(i + 1, close);
                    i = close + 1;
                    continue;
                case '@' when kind == ShellKind.PowerShell && i + 1 < n && raw[i + 1] is '"' or '\'':
                    if (HereStringEnd(raw, i) is { } hereEnd)
                    {
                        Blank(i, hereEnd);
                        i = hereEnd;
                        continue;
                    }
                    break;
                case '#' when kind != ShellKind.Cmd && StartsWord(raw, i):
                    var eol = raw.IndexOf('\n', i);
                    var commentEnd = eol < 0 ? n : eol;
                    Blank(i, commentEnd);
                    i = commentEnd;
                    continue;
                case '<' when kind == ShellKind.PowerShell && i + 1 < n && raw[i + 1] == '#':
                    var blockEnd = raw.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    blockEnd = blockEnd < 0 ? n : blockEnd + 2;
                    Blank(i, blockEnd);
                    i = blockEnd;
                    continue;
                case '<' when kind == ShellKind.Bash && i + 1 < n && raw[i + 1] == '<' && !(i + 2 < n && raw[i + 2] == '<'):
                    if (TryHeredoc(raw, i, out var markerEnd, out var delimiter, out var stripTabs))
                    {
                        Blank(i, markerEnd);
                        pending.Add((i, delimiter, stripTabs));
                        i = markerEnd;
                        continue;
                    }
                    break;
            }
            i++;
        }
        // A heredoc whose body starts on a line that never comes (`cat <<EOF` with nothing after it) holds nothing.
        foreach (var (marker, _, _) in pending) heredocs.Add((marker, ""));
        return new Lexed(new string(text), quotes, heredocs);
    }

    private static bool StartsWord(string raw, int i) =>
        i == 0 || char.IsWhiteSpace(raw[i - 1]) || raw[i - 1] is ';' or '&' or '|' or '(';

    /// <summary>Where the string that opens at <paramref name="open"/> ends (the length of the text when it does not).</summary>
    private static int CloseOfQuote(string raw, int open, ShellKind kind)
    {
        var quote = raw[open];
        for (var j = open + 1; j < raw.Length; j++)
        {
            var d = raw[j];
            if (d == quote)
            {
                // PowerShell writes a quote inside a string of the same kind by doubling it.
                if (kind == ShellKind.PowerShell && j + 1 < raw.Length && raw[j + 1] == quote)
                {
                    j++;
                    continue;
                }
                return j;
            }
            if ((d == '\\' && quote == '"' && kind == ShellKind.Bash) || (d == '`' && quote == '"' && kind == ShellKind.PowerShell)) j++;
        }
        return raw.Length;
    }

    /// <summary>A PowerShell here-string `@"` … `"@` (the quote mark then a line break opens it; the same mark and `@` at the start of a line closes it).</summary>
    private static int? HereStringEnd(string raw, int at)
    {
        var quote = raw[at + 1];
        var j = at + 2;
        while (j < raw.Length && raw[j] is ' ' or '\t' or '\r') j++;
        if (j >= raw.Length || raw[j] != '\n') return null;
        var end = raw.IndexOf("\n" + quote + "@", j, StringComparison.Ordinal);
        return end < 0 ? raw.Length : end + 3;
    }

    /// <summary>`&lt;&lt;EOF`, `&lt;&lt;-'EOF'`, `&lt;&lt;"EOF"`, `&lt;&lt;\EOF` — but not `&lt;&lt;&lt;` and not a shift (`1 &lt;&lt; 3`).</summary>
    private static bool TryHeredoc(string raw, int at, out int end, out string delimiter, out bool stripTabs)
    {
        end = at + 2;
        delimiter = "";
        stripTabs = false;
        var j = at + 2;
        if (j < raw.Length && raw[j] == '-')
        {
            stripTabs = true;
            j++;
        }
        while (j < raw.Length && raw[j] is ' ' or '\t') j++;
        if (j >= raw.Length) return false;
        if (raw[j] is '\'' or '"')
        {
            var close = raw.IndexOf(raw[j], j + 1);
            if (close < 0) return false;
            delimiter = raw[(j + 1)..close];
            end = close + 1;
            return delimiter.Length > 0 && !delimiter.Contains('\n');
        }
        if (raw[j] == '\\') j++;
        var k = j;
        if (k >= raw.Length || !(char.IsLetter(raw[k]) || raw[k] == '_')) return false;
        while (k < raw.Length && (char.IsLetterOrDigit(raw[k]) || raw[k] is '_' or '-' or '.')) k++;
        delimiter = raw[j..k];
        end = k;
        return true;
    }

    /// <summary>Blank the heredoc bodies that start at <paramref name="position"/> (in the order their markers came), each up to and
    /// including its delimiter line. Returns where the ordinary text carries on.</summary>
    private static int SkipHeredocBodies(string raw, char[] text, int position, List<(int Marker, string Delimiter, bool StripTabs)> pending,
                                         List<(int Marker, string Body)> bodies)
    {
        foreach (var (marker, delimiter, stripTabs) in pending)
        {
            var bodyStart = position;
            var bodyEnd = raw.Length;
            while (position < raw.Length)
            {
                var eol = raw.IndexOf('\n', position);
                var lineEnd = eol < 0 ? raw.Length : eol;
                var line = raw.AsSpan(position, lineEnd - position).TrimEnd('\r');
                if (stripTabs) line = line.TrimStart('\t');
                var terminator = line.SequenceEqual(delimiter);
                for (var k = position; k < lineEnd; k++) text[k] = ' ';
                var lineStart = position;
                position = eol < 0 ? raw.Length : eol + 1;
                if (terminator)
                {
                    bodyEnd = lineStart;
                    break;
                }
            }
            bodies.Add((marker, raw[bodyStart..Math.Max(bodyStart, bodyEnd)]));
        }
        pending.Clear();
        return position;
    }

    private static bool RedirectsIntoFile(string blanked) => Matches(Redirect, blanked);

    private static bool Matches(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsAssignment(string word)
    {
        var eq = word.IndexOf('=');
        return eq > 0 && (char.IsLetter(word[0]) || word[0] == '_') && word[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>The program a word names: no folder, no .exe/.cmd/.bat/.ps1, lower case, `python3.12` → `python`.</summary>
    private static string ProgramOf(string word)
    {
        var name = word.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1" })
        {
            if (name.EndsWith(extension, StringComparison.Ordinal))
            {
                name = name[..^extension.Length];
                break;
            }
        }
        foreach (var family in new[] { "python", "pypy" })
        {
            if (name.StartsWith(family, StringComparison.Ordinal) && name[family.Length..].All(c => char.IsDigit(c) || c == '.')) return family;
        }
        return name;
    }

    /// <summary>The pieces of a line between | ; && || & { } ( ) and line breaks, as (start, end) positions. The brackets of
    /// `${x}`, `$(x)`, `@(x)` and `@{x}` are part of a word, not a block.</summary>
    private static List<(int Start, int End)> Segments(string blanked)
    {
        var pieces = new List<(int, int)>();
        var start = 0;
        var data = new Stack<bool>();
        for (var i = 0; i < blanked.Length; i++)
        {
            var c = blanked[i];
            bool separator;
            switch (c)
            {
                case '{' or '(':
                    var inWord = i > 0 && blanked[i - 1] is '$' or '@';
                    data.Push(inWord);
                    separator = !inWord;
                    break;
                case '}' or ')':
                    separator = !(data.Count > 0 && data.Pop());
                    break;
                case '\n' or ';' or '|':
                    separator = true;
                    break;
                case '&':
                    // Not the & of `2>&1` or `&>file`.
                    separator = !(i > 0 && blanked[i - 1] == '>') && !(i + 1 < blanked.Length && blanked[i + 1] == '>');
                    break;
                default:
                    separator = false;
                    break;
            }
            if (!separator) continue;
            pieces.Add((start, i));
            start = i + 1;
        }
        pieces.Add((start, blanked.Length));
        return pieces;
    }

    private static List<Word> Words(string blanked, int start, int end)
    {
        var words = new List<Word>();
        var i = start;
        while (i < end)
        {
            while (i < end && blanked[i] is ' ' or '\t' or '\r' or '\n') i++;
            if (i >= end) break;
            var from = i;
            while (i < end && blanked[i] is not (' ' or '\t' or '\r' or '\n')) i++;
            words.Add(new Word(blanked[from..i], from));
        }
        return words;
    }
}
