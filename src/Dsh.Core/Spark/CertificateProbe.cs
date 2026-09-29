using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dsh.Core;

// MARK: - Certificate probe
//
// The Spark's web pages use a self-signed certificate: one the Spark made for itself instead of one
// a public authority vouched for. Browsers warn about that, which is right for a bank's website and
// expected for your own box on your own network. This looks at the certificate a server presents so
// the setup guide can show its fingerprint (the thing to compare), and then pin it in DSH
// (ProviderProfile.PinnedCertificate, SparkSwapperClient's pin) or, if the user asks, add it to
// Windows' trusted roots for their account.

/// <summary>What a TLS server presented.</summary>
public sealed record CertificateDetails(
    string Fingerprint, string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    SslPolicyErrors Errors, IReadOnlyList<string> Names, byte[] RawData)
{
    /// <summary>Windows (the platform's normal trust) accepts it for the name we connected to.</summary>
    public bool TrustedBySystem => Errors == SslPolicyErrors.None;
    public bool IsSelfSigned => string.Equals(Subject, Issuer, StringComparison.Ordinal);
    public bool IsExpired => DateTimeOffset.UtcNow > NotAfter || DateTimeOffset.UtcNow < NotBefore;
    /// <summary>The certificate lists the address we used (its subjectAltName).</summary>
    public bool NameMatches => (Errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;

    /// <summary>"CN=Spark Swapper (spark-3f2a)" → "Spark Swapper (spark-3f2a)".</summary>
    public string CommonName => CertificateProbe.CommonName(Subject);

    /// <summary>The first and last few bytes, for a quick visual match: "AB:CD:EF … 12:34:56".</summary>
    public string ShortFingerprint => CertificateProbe.Shorten(Fingerprint);

    /// <summary>PEM text, as the Swapper's /cert.crt serves it.</summary>
    public string Pem => CertificateProbe.ToPem(RawData);
}

public static class CertificateProbe
{
    /// <summary>Connect to <paramref name="host"/>:<paramref name="port"/>, complete a TLS handshake
    /// and return the leaf certificate — whatever it is; this only reads it.</summary>
    public static async Task<CertificateDetails> ProbeAsync(string host, int port, TimeSpan? timeout = null,
                                                            CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{host}:{port} didn't answer.");
        }
        catch (SocketException ex)
        {
            throw new IOException($"Couldn't connect to {host}:{port} ({ex.Message}).", ex);
        }

        X509Certificate2? seen = null;
        var errors = SslPolicyErrors.None;
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                {
                    if (certificate is not null) seen = new X509Certificate2(certificate);
                    errors = policyErrors;
                    // Read-only: accept so the handshake finishes, then hang up.
                    return true;
                },
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{host}:{port} didn't finish a secure (TLS) handshake.");
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            if (seen is null) throw new IOException($"{host}:{port} doesn't speak HTTPS ({ex.Message}).", ex);
        }
        if (seen is null) throw new IOException($"{host}:{port} presented no certificate.");
        using (seen) return Describe(seen, errors);
    }

    public static CertificateDetails Describe(X509Certificate2 certificate, SslPolicyErrors errors)
    {
        var raw = certificate.RawData;
        return new CertificateDetails(
            SparkSwapperClient.Fingerprint(raw),
            certificate.Subject,
            certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            errors,
            Names(certificate),
            raw);
    }

    /// <summary>DNS names and IP addresses from the subjectAltName extension.</summary>
    public static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san) continue;
            names.AddRange(san.EnumerateDnsNames());
            names.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
        }
        return names;
    }

    public static string CommonName(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return trimmed[3..].Trim('"');
        }
        return subject;
    }

    public static string Shorten(string fingerprint)
    {
        var parts = fingerprint.Split(':');
        return parts.Length <= 8 ? fingerprint : $"{string.Join(':', parts[..4])} … {string.Join(':', parts[^4..])}";
    }

    public static string ToPem(byte[] der) => PemEncoding.WriteString("CERTIFICATE", der) + "\n";

    /// <summary>Normalize a fingerprint the user typed or copied (spaces, lower case, "SHA256
    /// Fingerprint=" prefix from openssl) to "AB:CD:…". Null when it isn't 32 hex bytes.</summary>
    public static string? NormalizeFingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var equals = value.LastIndexOf('=');
        if (equals >= 0) value = value[(equals + 1)..];
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 64) return null;
        return string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    // MARK: - Windows trust store

    /// <summary>Is a certificate with this fingerprint already in the current user's Trusted Root
    /// store?</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsTrustedByWindows(string fingerprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Any(c => string.Equals(SparkSwapperClient.Fingerprint(c.RawData), fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Add the certificate to the current user's Trusted Root Certification Authorities, so
    /// browsers on this PC stop warning about it. Windows itself asks the user to confirm (a
    /// "Security Warning" dialog), so this blocks until they answer — call it off the UI thread.
    /// Returns false when they said no.</summary>
    [SupportedOSPlatform("windows")]
    public static bool TrustInWindows(byte[] der)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            store.Add(certificate);
        }
        catch (CryptographicException)
        {
            // The user pressed "No" in Windows' confirmation.
            return false;
        }
        return IsTrustedByWindows(SparkSwapperClient.Fingerprint(der));
    }

    /// <summary>Remove it again (Settings, or a Spark that was reinstalled with a new certificate).</summary>
    [SupportedOSPlatform("windows")]
    public static void RemoveFromWindows(string fingerprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates)
        {
            if (string.Equals(SparkSwapperClient.Fingerprint(certificate.RawData), fingerprint, StringComparison.OrdinalIgnoreCase))
                store.Remove(certificate);
        }
    }
}
