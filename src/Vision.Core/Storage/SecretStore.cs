using System.Runtime.InteropServices;
using System.Text;

namespace Vision.Core;

/// <summary>One entry of a credential store: the secret and the user name stored with it.</summary>
public sealed record CredentialEntry(string Secret, string? UserName);

/// <summary>Where <see cref="SecretStore"/> keeps entries: Windows Credential Manager, or memory (tests, other
/// platforms). Tests can stand in a fake one with <see cref="SecretStore.StoreOverride"/>.</summary>
public interface ICredentialStore
{
    CredentialEntry? Read(string target);
    void Write(string target, string secret, string userName);
    void Delete(string target);
}

/// <summary>Secrets (API keys, Swapper tokens and passwords) in Windows Credential Manager — never in the
/// settings file. Entries show up under "Windows Credentials" as Vision/…. On other platforms (tests)
/// secrets live in memory for the life of the process.</summary>
/// <remarks>Vision was called DSH before 0.9 and kept its entries under DSH/…. Reading an entry that isn't under
/// Vision/… yet looks for DSH's: when there is one it is copied to the new name, read back to check, and only then
/// removed (<see cref="Read"/>). Writing or deleting an entry also removes DSH's copy of it, so an old key can never
/// come back.</remarks>
public static class SecretStore
{
    /// <summary>Every target starts with this.</summary>
    public const string Prefix = "Vision/";

    /// <summary>Legacy: the prefix DSH (Vision's former name) used. Read once per entry, to bring it over.</summary>
    public const string LegacyPrefix = "DSH/";

    private static readonly MemoryCredentialStore Memory = new();

    /// <summary>For tests: keep everything in memory even on Windows.</summary>
    public static bool UseMemoryOnly { get; set; }

    /// <summary>For tests: a store of their own (a fake that fails on purpose, say). Wins over everything.</summary>
    public static ICredentialStore? StoreOverride { get; set; }

    private static ICredentialStore Store =>
        StoreOverride ?? (OperatingSystem.IsWindows() && !UseMemoryOnly ? WindowsCredentialStore.Instance : Memory);

    public static string ProviderTarget(string routeId) => $"{Prefix}provider/{routeId}";
    /// <summary>The one Swapper login of versions before several boxes; read once, to move it to its connection.</summary>
    public const string SparkTarget = Prefix + "spark/login";
    /// <summary>A Swapper connection's app token or password.</summary>
    public static string SwapperTarget(string connectionId) => $"{Prefix}swapper/{connectionId}";

    /// <summary>Legacy: the name DSH kept <paramref name="target"/> under ("Vision/x" → "DSH/x"), or null for a target
    /// that isn't one of ours.</summary>
    public static string? LegacyTarget(string target) =>
        target.StartsWith(Prefix, StringComparison.Ordinal) ? LegacyPrefix + target[Prefix.Length..] : null;

    /// <summary>Bringing an entry over is read, write, read back, delete: one thread at a time, so a second reader
    /// never finds DSH's entry gone and the new one not there yet.</summary>
    private static readonly Lock BringOverLock = new();

    public static string? Read(string target)
    {
        var store = Store;
        if (store.Read(target) is { } entry) return Joined(store, target, entry.Secret);
        if (LegacyTarget(target) is null) return null;
        lock (BringOverLock)
        {
            // Another thread may have brought it over a moment ago.
            if (store.Read(target) is { } again) return Joined(store, target, again.Secret);
            return BringOver(store, target, out _);
        }
    }

    // MARK: Long secrets
    //
    // Credential Manager holds at most 2,560 bytes per entry — 1,280 UTF-16 characters. A longer secret (a session
    // token with many claims, say) is kept in parts: "<target>#part1"…"#partN", with the entry itself holding
    // "vision-parts:N". Reading puts it back together; writing a shorter value, or deleting, removes the parts.

    /// <summary>The longest secret one entry holds (2,560 bytes of UTF-16).</summary>
    public const int MaxEntryChars = 1_280;
    /// <summary>The size of each part of a longer one.</summary>
    internal const int PartChars = 1_200;
    internal const string PartsMarker = "vision-parts:";
    /// <summary>No secret is cut into more parts than this (about 300 KB).</summary>
    internal const int MaxParts = 256;

    public static string PartTarget(string target, int n) => $"{target}#part{n}";

    /// <summary>The secret an entry stands for: itself, or its parts put back together (null when one is missing).</summary>
    private static string? Joined(ICredentialStore store, string target, string stored)
    {
        if (!stored.StartsWith(PartsMarker, StringComparison.Ordinal)
            || !int.TryParse(stored.AsSpan(PartsMarker.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count is < 1 or > MaxParts)
            return stored;
        var builder = new StringBuilder(count * PartChars);
        for (var n = 1; n <= count; n++)
        {
            if (store.Read(PartTarget(target, n)) is not { } part) return null;
            builder.Append(part.Secret);
        }
        return builder.ToString();
    }

    /// <summary>Cut <paramref name="secret"/> into parts that each fit one entry, never between the two halves of a
    /// surrogate pair.</summary>
    internal static IReadOnlyList<string> Parts(string secret)
    {
        var parts = new List<string>();
        var at = 0;
        while (at < secret.Length)
        {
            var length = Math.Min(PartChars, secret.Length - at);
            if (at + length < secret.Length && char.IsHighSurrogate(secret[at + length - 1])) length--;
            parts.Add(secret.Substring(at, length));
            at += length;
        }
        return parts;
    }

    /// <summary>Remove parts numbered from <paramref name="from"/> on, as far as they go.</summary>
    private static void DeleteParts(ICredentialStore store, string target, int from)
    {
        for (var n = from; n <= MaxParts; n++)
        {
            var part = PartTarget(target, n);
            if (store.Read(part) is null) break;
            store.Delete(part);
        }
    }

    /// <summary>Bring DSH's entries for <paramref name="targets"/> over to their Vision names now (the app does this
    /// for every route and Swapper box at launch, so nothing waits for its first use). Returns how many were brought over.</summary>
    public static int BringOver(IEnumerable<string> targets)
    {
        var store = Store;
        var count = 0;
        foreach (var target in targets)
        {
            try
            {
                lock (BringOverLock)
                {
                    if (store.Read(target) is null && BringOver(store, target, out var moved) is not null && moved) count++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
            {
                // One unreadable entry doesn't stop the rest; it is tried again when it is read.
            }
        }
        return count;
    }

    /// <summary>DSH's entry for <paramref name="target"/>, if there is one: copied to <paramref name="target"/> with the
    /// same user name, read back, and removed only when the copy reads back the same (a copy that doesn't is removed
    /// instead, so DSH's stays the one in use). If the copy can't be made, DSH's entry stays too. Returns DSH's secret,
    /// or null when it had no such entry; <paramref name="moved"/> says whether it now lives under the new name only.
    /// Never logs or reports the secret.</summary>
    private static string? BringOver(ICredentialStore store, string target, out bool moved)
    {
        moved = false;
        if (LegacyTarget(target) is not { } legacy || store.Read(legacy) is not { } old) return null;
        try
        {
            store.Write(target, old.Secret, old.UserName ?? Environment.UserName);
            if (store.Read(target) is { } copied && string.Equals(copied.Secret, old.Secret, StringComparison.Ordinal))
            {
                store.Delete(legacy);
                moved = true;
            }
            else
            {
                store.Delete(target);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
        {
            // Keep DSH's entry: it still works, and the next read tries again.
        }
        return old.Secret;
    }

    /// <summary>Store <paramref name="secret"/>; an empty secret deletes the entry.</summary>
    public static void Write(string target, string secret, string? userName = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            Delete(target);
            return;
        }
        var store = Store;
        var user = userName ?? Environment.UserName;
        if (secret.Length <= MaxEntryChars && !secret.StartsWith(PartsMarker, StringComparison.Ordinal))
        {
            store.Write(target, secret, user);
            DeleteParts(store, target, 1);
        }
        else
        {
            var parts = Parts(secret);
            if (parts.Count > MaxParts) throw new ArgumentException("That secret is too long to store in Credential Manager.");
            for (var n = 1; n <= parts.Count; n++) store.Write(PartTarget(target, n), parts[n - 1], user);
            store.Write(target, PartsMarker + parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), user);
            DeleteParts(store, target, parts.Count + 1);
        }
        // DSH's older copy would otherwise come back if this entry were ever deleted.
        if (LegacyTarget(target) is { } legacy)
        {
            try { store.Delete(legacy); } catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { }
        }
    }

    /// <summary>Remove the entry — and DSH's copy of it, if one was never brought over.</summary>
    public static void Delete(string target)
    {
        var store = Store;
        if (store.Read(target) is { Secret: var stored } && stored.StartsWith(PartsMarker, StringComparison.Ordinal)) DeleteParts(store, target, 1);
        store.Delete(target);
        if (LegacyTarget(target) is { } legacy) store.Delete(legacy);
    }
}

/// <summary>Entries in memory, for the life of the process (tests; platforms without Credential Manager).</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, CredentialEntry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public CredentialEntry? Read(string target)
    {
        lock (_lock) return _entries.GetValueOrDefault(target);
    }

    public void Write(string target, string secret, string userName)
    {
        lock (_lock) _entries[target] = new CredentialEntry(secret, userName);
    }

    public void Delete(string target)
    {
        lock (_lock) _entries.Remove(target);
    }
}

/// <summary>Generic credentials in Windows Credential Manager, saved for this user on this PC.</summary>
internal sealed class WindowsCredentialStore : ICredentialStore
{
    public static readonly WindowsCredentialStore Instance = new();

    /// <summary>Credential Manager's limit on a secret: 5 × 512 bytes (2,560), in UTF-16.</summary>
    public const int MaxSecretBytes = 5 * 512;

    public CredentialEntry? Read(string target)
    {
        if (!Native.CredRead(target, Native.CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Native.Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return new CredentialEntry("", credential.UserName);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return new CredentialEntry(Encoding.Unicode.GetString(bytes), credential.UserName);
        }
        finally
        {
            Native.CredFree(handle);
        }
    }

    public void Write(string target, string secret, string userName)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxSecretBytes) throw new ArgumentException("That secret is too long to store in Credential Manager.");
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Native.Credential
            {
                Type = Native.CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = Native.CredPersistLocalMachine,
                UserName = userName,
                Comment = "Stored by Vision for Windows",
            };
            if (!Native.CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Couldn't save to Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string target) => Native.CredDelete(target, Native.CredTypeGeneric, 0);

    private static class Native
    {
        public const uint CredTypeGeneric = 1;
        public const uint CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        public static extern void CredFree(IntPtr buffer);
    }
}
