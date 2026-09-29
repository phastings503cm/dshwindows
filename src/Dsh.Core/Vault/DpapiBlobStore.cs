using System.Security.Cryptography;
using System.Text;

namespace Dsh.Core;

/// <summary>The vault's values in one file encrypted with Windows Data Protection (DPAPI) for the
/// current user — the Windows counterpart of the Mac app's single Keychain item. Only the Windows
/// account that saved the file can decrypt it (on this PC, or wherever its roaming profile goes),
/// and the app-specific entropy means a generic <c>CryptUnprotectData</c> call by another program
/// running as the same user doesn't decrypt it (a speed bump, not a wall: the entropy ships in the
/// app).
///
/// Off Windows every call throws <see cref="PlatformNotSupportedException"/>; tests use
/// <see cref="MemoryBlobStore"/>.</summary>
public sealed class DpapiBlobStore : ISecretBlobStore
{
    /// <summary>The file's name beside vault.json.</summary>
    public const string DefaultFileName = "vault.bin";

    private static readonly byte[] DefaultEntropy = Encoding.UTF8.GetBytes("DSH for Windows · credential vault · v1");

    private readonly byte[] _entropy;

    /// <param name="path">The encrypted file (usually <c>vault.bin</c> next to vault.json).</param>
    /// <param name="entropy">Extra secret mixed into the encryption; the app's own by default.</param>
    public DpapiBlobStore(string path, byte[]? entropy = null)
    {
        FilePath = path;
        _entropy = entropy is null ? DefaultEntropy : [.. entropy];
    }

    public string FilePath { get; }

    public byte[]? Load()
    {
        if (!OperatingSystem.IsWindows()) throw NotSupported();
        byte[] cipher;
        try
        {
            if (!File.Exists(FilePath)) return null;
            cipher = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        try
        {
            return ProtectedData.Unprotect(cipher, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw VaultException.Storage(
                $"Windows Data Protection refused the operation ({ex.Message.Trim().TrimEnd('.')}). " +
                "The vault can only be opened by the Windows account that saved it.", ex);
        }
    }

    public void Save(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw NotSupported();
        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw VaultException.Storage($"Windows Data Protection refused the operation ({ex.Message.Trim().TrimEnd('.')}).", ex);
        }
        VaultFiles.WriteAtomic(FilePath, cipher);
    }

    private static PlatformNotSupportedException NotSupported() =>
        new("The credential vault's encryption (Windows Data Protection) is only available on Windows.");
}
