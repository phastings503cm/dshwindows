using System.Text;

namespace Vision.Core;

/// <summary>FROZEN CRYPTOGRAPHIC CONSTANTS. DO NOT CHANGE A SINGLE BYTE.</summary>
/// <remarks>
/// The "entropy" Windows Data Protection (DPAPI) mixes into everything the app encrypts for the user: the credentials
/// vault (vault.bin) and the sensitive-data shield's custom terms in settings.json. Decrypting needs exactly the bytes
/// that encrypting used, so changing either string — even to fix the old app name in it, even a space — makes every
/// existing vault and every user's saved terms unreadable, with no way back.
///
/// The strings carry the app's former name (Vision was called DSH before 0.9) and must keep it forever. They are not
/// shown anywhere. FrozenEntropyTests pins their bytes.
/// </remarks>
public static class FrozenEntropy
{
    // DO NOT CHANGE (see the remarks above): existing vaults and saved terms were encrypted with these exact bytes.
    private const string VaultText = "DSH for Windows · credential vault · v1";

    // DO NOT CHANGE (see the remarks above): existing vaults and saved terms were encrypted with these exact bytes.
    private const string PrivacyTermsText = "DSH for Windows · privacy terms · v1";

    /// <summary>The credentials vault's entropy (a fresh copy every time, so nobody can change a shared array).</summary>
    public static byte[] Vault => Encoding.UTF8.GetBytes(VaultText);

    /// <summary>The shield's custom terms' entropy (a fresh copy every time).</summary>
    public static byte[] PrivacyTerms => Encoding.UTF8.GetBytes(PrivacyTermsText);
}
