using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Dsh.Core;

// MARK: - Spark over SSH
//
// The setup guide installs Spark Swapper by logging in to the Spark with the username and password
// the user created at first boot — the same thing a person would do with `ssh` in a terminal, minus
// the terminal. Host keys are trusted on first use: the first time, the user is shown the key's
// SHA-256 fingerprint (what `ssh` asks "Are you sure you want to continue connecting?" about) and
// says yes; DSH remembers it in known_hosts.json and refuses to connect if it ever changes.

/// <summary>A server's SSH host key, as `ssh` shows it: algorithm and "SHA256:…" fingerprint.</summary>
public sealed record SshHostKey(string Host, int Port, string Algorithm, string Fingerprint);

/// <summary>The server's host key isn't one we know: new (ask the user) or changed (warn them).</summary>
public sealed class SshHostKeyException(SshHostKey presented, string? expected)
    : Exception(expected is null
        ? $"DSH hasn't connected to {presented.Host} before. Its SSH fingerprint is {presented.Fingerprint}."
        : $"The SSH fingerprint of {presented.Host} changed (was {expected}, now {presented.Fingerprint}). If you didn't reinstall the Spark, something may be pretending to be it.")
{
    public SshHostKey Presented { get; } = presented;
    /// <summary>The fingerprint remembered earlier, when this is a change rather than a first meeting.</summary>
    public string? Expected { get; } = expected;
    public bool IsChanged => Expected is not null;
}

/// <summary>Couldn't log in, or couldn't reach the Spark at all — with wording for people.</summary>
public sealed class SparkRemoteException(string message, bool badLogin = false, Exception? inner = null) : Exception(message, inner)
{
    public bool BadLogin { get; } = badLogin;
}

/// <summary>SSH host keys the user has accepted, in <c>known_hosts.json</c> under the app's data
/// folder (not ~/.ssh/known_hosts: DSH doesn't touch other tools' files).</summary>
public sealed class KnownHosts
{
    public sealed record Entry(string Algorithm, string Fingerprint, DateTimeOffset Added);

    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, Entry>? _entries;
    /// <summary>The file exists but couldn't be read just now: what's in memory is not the whole truth, so it must not overwrite the file.</summary>
    private bool _unreadable;

    public KnownHosts(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "known_hosts.json");
    }

    public static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public Entry? Lookup(string host, int port)
    {
        lock (_lock) return Load().GetValueOrDefault(Key(host, port));
    }

    public void Remember(SshHostKey key)
    {
        lock (_lock)
        {
            Load()[Key(key.Host, key.Port)] = new Entry(key.Algorithm, key.Fingerprint, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public void Forget(string host, int port)
    {
        lock (_lock)
        {
            if (Load().Remove(Key(host, port))) Save();
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The pins as the file has them now. Several parts of the app keep their own instance (the setup guide,
    /// the OpenClaw import): each must see what the others added, or the last to save would erase the rest — and with
    /// a pin gone, a changed host key reads as a first contact. The file is a few hundred bytes and read a handful of
    /// times a session, so it is read every time: a timestamp is not proof it hasn't changed (two saves inside one clock
    /// tick — a millisecond or more, depending on the disk — carry the same one).</summary>
    private Dictionary<string, Entry> Load()
    {
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), Json) ?? []
                : [];
            _unreadable = false;
        }
        catch (JsonException)
        {
            // Damaged. Keep it where it can be looked at, and start over: the fingerprint is shown again on the next contact.
            try
            {
                File.Copy(_path, _path + ".damaged", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more to be done.
            }
            _entries = [];
            _unreadable = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program has it open, or it can't be read. Empty is not the truth: use what was known, and don't overwrite the file.
            _entries ??= [];
            _unreadable = true;
        }
        return _entries;
    }

    private void Save()
    {
        if (_unreadable) return; // kept in memory for this session; the file is left as it was
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json), TextUtil.Utf8NoBom);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this session.
        }
    }
}

// MARK: - Connection seams (fakes in tests)

public sealed record RemoteResult(int ExitCode, string Output, string Error);

/// <summary>An interactive shell with a terminal (PTY), so programs like sudo can prompt.</summary>
public interface ISparkShell : IDisposable
{
    /// <summary>The next chunk of output, or null once the shell has closed.</summary>
    Task<string?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(string text, CancellationToken cancellationToken);
}

public interface ISparkConnection : IDisposable
{
    string Host { get; }
    string Username { get; }
    /// <summary>Run a command without a terminal and collect its output.</summary>
    Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
    Task<ISparkShell> OpenShellAsync(CancellationToken cancellationToken);
}

/// <summary>The real thing, over SSH.NET.</summary>
public sealed class SshSparkConnection : ISparkConnection
{
    private readonly SshClient _client;

    public string Host { get; }
    public string Username { get; }
    public SshHostKey HostKey { get; }

    private SshSparkConnection(SshClient client, string host, string username, SshHostKey hostKey)
    {
        _client = client;
        Host = host;
        Username = username;
        HostKey = hostKey;
    }

    /// <summary>Log in with a password. Throws <see cref="SshHostKeyException"/> when the host key
    /// isn't known yet (show it and call again with <paramref name="approvedFingerprint"/>) or has
    /// changed, and <see cref="SparkRemoteException"/> for a wrong password or an unreachable host.</summary>
    public static async Task<SshSparkConnection> ConnectAsync(string host, string username, string password, KnownHosts knownHosts,
                                                              string? approvedFingerprint = null, int port = 22,
                                                              CancellationToken cancellationToken = default)
    {
        // Ubuntu's sshd may offer "password" or only "keyboard-interactive"; answer both with the same password.
        var keyboard = new KeyboardInteractiveAuthenticationMethod(username);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts) prompt.Response = password;
        };
        var info = new ConnectionInfo(host, port, username, new PasswordAuthenticationMethod(username, password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var client = new SshClient(info);
        SshHostKey? presented = null;
        string? expected = null;
        var rejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            var key = new SshHostKey(host, port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
            presented = key;
            var known = knownHosts.Lookup(host, port);
            if (known is not null)
            {
                e.CanTrust = string.Equals(known.Fingerprint, key.Fingerprint, StringComparison.Ordinal);
                if (!e.CanTrust) expected = known.Fingerprint;
            }
            else if (string.Equals(approvedFingerprint, key.Fingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                knownHosts.Remember(key);
            }
            else
            {
                e.CanTrust = false;
            }
            rejected = !e.CanTrust;
        };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (rejected && presented is not null && ex is not OperationCanceledException)
        {
            client.Dispose();
            throw new SshHostKeyException(presented, expected);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new SparkRemoteException(
                $"The Spark didn't accept that username and password. Use the ones you created when you first set it up.", badLogin: true, ex);
        }
        catch (Exception ex) when (ex is SocketException or SshOperationTimeoutException or SshConnectionException or ProxyException)
        {
            client.Dispose();
            throw new SparkRemoteException($"Couldn't reach {host} on port {port} for remote login (SSH): {ex.Message}", inner: ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new SshSparkConnection(client, host, username, presented!);
    }

    public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var run = _client.CreateCommand(command);
        run.CommandTimeout = timeout;
        await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteResult(run.ExitStatus ?? -1, run.Result, run.Error);
    }

    public Task<ISparkShell> OpenShellAsync(CancellationToken cancellationToken)
    {
        // A wide terminal keeps long commands on one line; ECHO off keeps typed text (the sudo
        // password included, belt and braces) out of the output.
        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 0 };
        // Opening the channel waits for the server's replies; keep that off the caller's (UI) thread.
        return Task.Run<ISparkShell>(() => new Shell(_client.CreateShellStream("xterm", 400, 50, 0, 0, 64 * 1024, modes)), cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception)
        {
            // Already gone.
        }
        _client.Dispose();
    }

    private sealed class Shell(ShellStream stream) : ISparkShell
    {
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private readonly byte[] _buffer = new byte[16 * 1024];

        public async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            // ShellStream.Read blocks until data arrives or the channel closes; closing the stream is
            // what unblocks it when the caller gives up.
            await using var registration = cancellationToken.Register(stream.Dispose);
            int read;
            try
            {
                read = await Task.Run(() => stream.Read(_buffer, 0, _buffer.Length), CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) return null;
            var chars = new char[_decoder.GetCharCount(_buffer, 0, read)];
            _decoder.GetChars(_buffer, 0, read, chars, 0);
            return new string(chars);
        }

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            stream.Write(text);
            stream.Flush();
            return Task.CompletedTask;
        }

        public void Dispose() => stream.Dispose();
    }
}

// MARK: - What is on the Spark

/// <summary>A quick look around the Spark before installing anything.</summary>
public sealed record SparkMachineState
{
    public string? OsName { get; init; }
    public string? OsVersion { get; init; }
    /// <summary>DGX OS (NVIDIA's Ubuntu) rather than some other Linux.</summary>
    public bool IsDgxOs { get; init; }
    public string? HostName { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public string? Architecture { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public bool SwapperActive { get; init; }
    public bool SwapperInstalled { get; init; }
    public string? DockerVersion { get; init; }
    public string? Gpu { get; init; }
    /// <summary>Something (nginx, normally) listens on :11443, the HTTPS front for the model API.</summary>
    public bool ModelFrontListening { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Members of "sudo" (Ubuntu's admin group) can install software.</summary>
    public bool CanSudo => Groups.Any(g => g is "sudo" or "admin" or "wheel" or "root");
    public bool HasCurl => Tools.Contains("curl");

    /// <summary>The shell snippet whose output <see cref="Parse"/> reads.</summary>
    public const string ProbeScript =
        "printf '@@os\\n'; cat /etc/os-release 2>/dev/null; " +
        "printf '@@dgx\\n'; cat /etc/dgx-release 2>/dev/null; " +
        "printf '@@host\\n'; hostname 2>/dev/null; " +
        "printf '@@ips\\n'; hostname -I 2>/dev/null; " +
        "printf '@@arch\\n'; uname -m 2>/dev/null; " +
        "printf '@@groups\\n'; id -nG 2>/dev/null; " +
        "printf '@@swapper\\n'; systemctl is-active spark-swapper 2>/dev/null; " +
        "printf '@@swapper_unit\\n'; test -f /etc/systemd/system/spark-swapper.service && echo yes; " +
        "printf '@@docker\\n'; docker --version 2>/dev/null; " +
        "printf '@@gpu\\n'; nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>/dev/null | head -n 1; " +
        "printf '@@front\\n'; (ss -ltnH 2>/dev/null || netstat -ltn 2>/dev/null) | grep -Eq '[:.]11443[[:space:]]' && echo listening; " +
        "printf '@@tools\\n'; for t in curl git python3 openssl; do command -v $t >/dev/null 2>&1 && echo $t; done; " +
        "printf '@@end\\n'";

    public static SparkMachineState Parse(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in TextUtil.NormalizeNewlines(output).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = [];
                sections[line[2..]] = current;
                continue;
            }
            if (line.Length > 0) current?.Add(line);
        }
        List<string> Section(string name) => sections.GetValueOrDefault(name) ?? [];
        string? First(string name) => Section(name).FirstOrDefault()?.Trim();

        var os = Section("os")
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim().Trim('"'), StringComparer.Ordinal);
        var dgx = Section("dgx");
        var gpu = First("gpu");
        return new SparkMachineState
        {
            OsName = os.GetValueOrDefault("PRETTY_NAME") ?? os.GetValueOrDefault("NAME"),
            OsVersion = os.GetValueOrDefault("VERSION_ID"),
            IsDgxOs = dgx.Count > 0 || os.Values.Any(v => v.Contains("DGX", StringComparison.OrdinalIgnoreCase))
                      || (gpu?.Contains("GB10", StringComparison.OrdinalIgnoreCase) ?? false),
            HostName = First("host"),
            Addresses = (First("ips") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => !a.Contains(':')).ToList(),
            Architecture = First("arch"),
            Groups = (First("groups") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            SwapperActive = First("swapper") == "active",
            SwapperInstalled = First("swapper") == "active" || First("swapper_unit") == "yes",
            DockerVersion = First("docker"),
            Gpu = gpu,
            ModelFrontListening = First("front") == "listening",
            Tools = Section("tools").Select(t => t.Trim()).ToList(),
        };
    }
}
