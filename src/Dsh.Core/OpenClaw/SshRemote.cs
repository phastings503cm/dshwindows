using System.Net.Sockets;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Dsh.Core;

// MARK: - Reaching another machine
//
// The same sign-in a person would do with `ssh` (or an SFTP client): a password, or a private key, or the
// keys already in ~/.ssh — with the server's host key checked the way `ssh` does it (shown on first
// contact, remembered in known_hosts.json, refused if it ever changes). Two channels are opened: a shell
// (one `find` beats thousands of directory listings) and SFTP (files come over it when the server allows).

/// <summary>How to sign in to a machine.</summary>
public sealed record SshLogin
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? Password { get; init; }
    /// <summary>A private key file to sign in with.</summary>
    public string? PrivateKeyPath { get; init; }
    public string? Passphrase { get; init; }
    /// <summary>Also try the keys in ~/.ssh (id_ed25519, id_rsa, id_ecdsa) that need no passphrase.</summary>
    public bool TryDefaultKeys { get; init; } = true;

    public string Label => $"{Username}@{Host}";

    // The synthesised ToString would print the password and passphrase into any log line, assertion message or crash dump
    // that mentions a login.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host).Append(", Port = ").Append(Port).Append(", Username = ").Append(Username)
            .Append(", Password = ").Append(string.IsNullOrEmpty(Password) ? "(none)" : "(hidden)")
            .Append(", PrivateKeyPath = ").Append(PrivateKeyPath)
            .Append(", Passphrase = ").Append(string.IsNullOrEmpty(Passphrase) ? "(none)" : "(hidden)")
            .Append(", TryDefaultKeys = ").Append(TryDefaultKeys);
        return true;
    }
}

/// <summary>Couldn't sign in or reach the machine, in words for a person.</summary>
public sealed class RemoteLoginException(string message, bool badLogin = false, Exception? inner = null) : Exception(message, inner)
{
    public bool BadLogin { get; } = badLogin;
}

public static class SshRemote
{
    /// <summary>Connect, returning a source that reads that machine's files. Throws
    /// <see cref="SshHostKeyException"/> when the host key is new (show its fingerprint, ask, and call again
    /// with <paramref name="approvedFingerprint"/>) or has changed, and <see cref="RemoteLoginException"/> otherwise.</summary>
    public static async Task<RemoteFileSource> ConnectAsync(SshLogin login, KnownHosts knownHosts, string? approvedFingerprint = null,
                                                            CancellationToken cancellationToken = default)
    {
        var methods = AuthenticationMethods(login);
        if (methods.Count == 0)
            throw new RemoteLoginException("There is nothing to sign in with: give a password or a private key file (or put a key without a passphrase in ~/.ssh).");

        // (Keep-alives: the connection sits idle while someone reads the choices on the next page, and a router or sshd that
        // drops quiet connections would otherwise fail every file fetch after it.)
        var commands = new SshClient(NewConnectionInfo(login, methods)) { KeepAliveInterval = KeepAlive };
        SshHostKey? presented = null;
        string? expected = null;
        var rejected = false;
        Watch(commands, login, knownHosts, approvedFingerprint, key => presented = key, e => expected = e, () => rejected = true);
        try
        {
            await commands.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (rejected && presented is not null && ex is not OperationCanceledException)
        {
            commands.Dispose();
            throw new SshHostKeyException(presented, expected);
        }
        catch (SshAuthenticationException ex)
        {
            commands.Dispose();
            throw new RemoteLoginException($"{login.Host} didn't accept that sign-in. Check the username, and the password or key.", badLogin: true, ex);
        }
        catch (Exception ex) when (ex is SocketException or SshOperationTimeoutException or SshConnectionException or ProxyException)
        {
            commands.Dispose();
            throw new RemoteLoginException($"Couldn't reach {login.Host} on port {login.Port} for remote login (SSH): {ex.Message}", inner: ex);
        }
        catch
        {
            commands.Dispose();
            throw;
        }

        // Files over SFTP when the server offers it (some accounts are shell-only).
        IRemoteFiles? files = null;
        try
        {
            var sftp = new SftpClient(NewConnectionInfo(login, methods)) { KeepAliveInterval = KeepAlive, OperationTimeout = TimeSpan.FromSeconds(90) };
            Watch(sftp, login, knownHosts, approvedFingerprint, _ => { }, _ => { }, () => { });
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            files = new SftpFiles(sftp);
        }
        catch (OperationCanceledException)
        {
            commands.Dispose(); // the caller gave up between the two connections: don't leave the first one open
            throw;
        }
        catch (Exception)
        {
            // The shell can read files too, just more slowly.
        }
        return new RemoteFileSource(login.Label, new SshShell(commands), files, login.Host);
    }

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private static ConnectionInfo NewConnectionInfo(SshLogin login, List<AuthenticationMethod> methods) =>
        new(login.Host, login.Port, login.Username, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Trust on first use: a known host must present the fingerprint we remember; an unknown one only when the user approved exactly this one.</summary>
    private static void Watch(BaseClient client, SshLogin login, KnownHosts knownHosts, string? approvedFingerprint,
                              Action<SshHostKey> presented, Action<string> expected, Action rejected)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var key = new SshHostKey(login.Host, login.Port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
            presented(key);
            var known = knownHosts.Lookup(login.Host, login.Port);
            if (known is not null)
            {
                e.CanTrust = string.Equals(known.Fingerprint, key.Fingerprint, StringComparison.Ordinal);
                if (!e.CanTrust) expected(known.Fingerprint);
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
            if (!e.CanTrust) rejected();
        };
    }

    private static List<AuthenticationMethod> AuthenticationMethods(SshLogin login)
    {
        var methods = new List<AuthenticationMethod>();
        var keys = new List<PrivateKeyFile>();
        if (!string.IsNullOrWhiteSpace(login.PrivateKeyPath))
        {
            try
            {
                var path = ExpandHome(login.PrivateKeyPath!);
                keys.Add(string.IsNullOrEmpty(login.Passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, login.Passphrase));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RemoteLoginException($"Couldn't use the key file {login.PrivateKeyPath}: {ex.Message}" +
                                               (string.IsNullOrEmpty(login.Passphrase) ? " If it has a passphrase, enter it." : ""), badLogin: true, ex);
            }
        }
        else if (login.TryDefaultKeys)
        {
            foreach (var path in DefaultKeyPaths())
            {
                try
                {
                    if (File.Exists(path)) keys.Add(new PrivateKeyFile(path));
                }
                catch (Exception)
                {
                    // Needs a passphrase, or a format this SSH library doesn't read: skip it.
                }
            }
        }
        if (keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(login.Username, keys.ToArray()));
        if (!string.IsNullOrEmpty(login.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(login.Username, login.Password));
            // Some sshd configurations only offer keyboard-interactive; answer it with the same password.
            var keyboard = new KeyboardInteractiveAuthenticationMethod(login.Username);
            var password = login.Password;
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(keyboard);
        }
        return methods;
    }

    public static IEnumerable<string> DefaultKeyPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
            yield return Path.Combine(home, ".ssh", name);
    }

    internal static string ExpandHome(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)) return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    }

    // MARK: - Channels

    private sealed class SshShell(SshClient client) : IRemoteShell
    {
        public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                using var run = client.CreateCommand(command);
                run.CommandTimeout = timeout;
                await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteResult(run.ExitStatus ?? -1, run.Result, run.Error);
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                // A dropped link or a command that timed out: one kind of failure for the callers to handle.
                throw new IOException($"Lost touch with the machine: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                if (client.IsConnected) client.Disconnect();
            }
            catch (Exception)
            {
                // Already gone.
            }
            client.Dispose();
        }
    }

    private sealed class SftpFiles(SftpClient client) : IRemoteFiles
    {
        public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken) =>
            Task.Run<IReadOnlyList<FileEntry>?>(() =>
            {
                try
                {
                    var entries = new List<FileEntry>();
                    foreach (var file in client.ListDirectory(directory))
                    {
                        if (file.Name is "." or "..") continue;
                        // A symlink to a folder lists as a link; follow it once to see what it is.
                        var isDirectory = file.IsDirectory || (file.IsSymbolicLink && IsDirectory(file.FullName));
                        entries.Add(new FileEntry(file.Name, file.FullName, isDirectory));
                    }
                    return entries;
                }
                catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
                {
                    return null;
                }
            }, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken) =>
            Task.Run<byte[]?>(() =>
            {
                try
                {
                    var attributes = client.Get(path);
                    // Only a file: a folder, a pipe, a socket or a device (a link to /dev/zero) is never opened. A link is followed
                    // by the download, so what comes back is counted as it arrives and stops at the limit whatever the server says.
                    if (attributes.IsDirectory || !(attributes.IsRegularFile || attributes.IsSymbolicLink) || attributes.Length > maxBytes) return null;
                    using var buffer = new CappedStream(maxBytes);
                    client.DownloadFile(path, buffer);
                    return buffer.ToArray();
                }
                catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException or TooLargeException)
                {
                    return null;
                }
                catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
                {
                    throw new IOException($"Lost touch with the machine: {ex.Message}", ex);
                }
            }, cancellationToken);

        private sealed class TooLargeException() : IOException("The file is bigger than it said.");

        /// <summary>A memory stream that refuses to grow past a limit.</summary>
        private sealed class CappedStream(long limit) : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Length + count > limit) throw new TooLargeException();
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (Length + buffer.Length > limit) throw new TooLargeException();
                base.Write(buffer);
            }
        }

        private bool IsDirectory(string path)
        {
            try
            {
                // Listing succeeds only for a folder.
                client.ListDirectory(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (client.IsConnected) client.Disconnect();
            }
            catch (Exception)
            {
                // Already gone.
            }
            client.Dispose();
        }
    }
}

// MARK: - ~/.ssh/config

/// <summary>What ~/.ssh/config says about a host alias: enough to fill in the sign-in form (HostName, User,
/// Port, IdentityFile). OpenSSH's rule applies: the first value found for each keyword wins.</summary>
public sealed record SshConfigEntry(string HostName, string? User, int? Port, string? IdentityFile);

public static partial class SshConfigFile
{
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z]+)\s*(?:=|\s)\s*(?<value>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    /// <summary>The settings that apply to <paramref name="alias"/>, or null when no block mentions it.</summary>
    public static SshConfigEntry? Resolve(string configText, string alias)
    {
        string? hostName = null, user = null, identity = null;
        int? port = null;
        var applies = false;
        var matched = false;
        foreach (var raw in configText.Replace("\r\n", "\n").Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var match = Line().Match(text);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value.ToLowerInvariant();
            var value = match.Groups["value"].Value.Trim().Trim('"');
            if (key == "host")
            {
                applies = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(p => Matches(p, alias));
                matched |= applies && !value.Split(' ').All(p => p == "*");
                continue;
            }
            if (key == "match") { applies = false; continue; }
            if (!applies) continue;
            switch (key)
            {
                case "hostname": hostName ??= value; break;
                case "user": user ??= value; break;
                case "port" when port is null && int.TryParse(value, out var p) && p is > 0 and < 65536: port = p; break;
                case "identityfile": identity ??= value; break;
            }
        }
        return matched ? new SshConfigEntry(hostName ?? alias, user, port, identity) : null;
    }

    /// <summary>The names ~/.ssh/config defines (plain aliases, no wildcards), for a pick list.</summary>
    public static IReadOnlyList<string> Aliases(string configText)
    {
        var names = new List<string>();
        foreach (var raw in configText.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Line().Match(raw.Trim());
            if (!match.Success || !match.Groups["key"].Value.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in match.Groups["value"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.Contains('*') && !part.Contains('?') && !part.StartsWith('!') && !names.Contains(part)) names.Add(part);
            }
        }
        return names;
    }

    private static bool Matches(string pattern, string alias)
    {
        if (pattern.StartsWith('!')) return false;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(alias, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
}
