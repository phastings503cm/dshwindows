using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Core;

/// <summary>Secrets (API keys, the Spark password) in Windows Credential Manager — never in the
/// settings file. Entries show up under "Windows Credentials" as DSH/…. On other platforms (tests)
/// secrets live in memory for the life of the process.</summary>
public static class SecretStore
{
    private static readonly Dictionary<string, string> Memory = new(StringComparer.Ordinal);
    private static readonly Lock MemoryLock = new();

    /// <summary>For tests: keep everything in memory even on Windows.</summary>
    public static bool UseMemoryOnly { get; set; }

    private static bool UseCredentialManager => OperatingSystem.IsWindows() && !UseMemoryOnly;

    public static string ProviderTarget(string routeId) => $"DSH/provider/{routeId}";
    public const string SparkTarget = "DSH/spark/login";

    public static string? Read(string target)
    {
        if (!UseCredentialManager)
        {
            lock (MemoryLock) return Memory.GetValueOrDefault(target);
        }
        if (!Native.CredRead(target, Native.CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Native.Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            Native.CredFree(handle);
        }
    }

    /// <summary>Store <paramref name="secret"/>; an empty secret deletes the entry.</summary>
    public static void Write(string target, string secret, string? userName = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            Delete(target);
            return;
        }
        if (!UseCredentialManager)
        {
            lock (MemoryLock) Memory[target] = secret;
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 5 * 512) throw new ArgumentException("That secret is too long to store in Credential Manager.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
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
                UserName = userName ?? Environment.UserName,
                Comment = "Stored by DSH for Windows",
            };
            if (!Native.CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Couldn't save to Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string target)
    {
        if (!UseCredentialManager)
        {
            lock (MemoryLock) Memory.Remove(target);
            return;
        }
        Native.CredDelete(target, Native.CredTypeGeneric, 0);
    }

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
