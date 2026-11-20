using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cictna.Industrial.OpcUa;

/// <summary>A certificate file in a <see cref="CertificateStore"/>.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Subject">Certificate subject.</param>
/// <param name="Thumbprint">SHA-1 thumbprint, upper-case hex.</param>
/// <param name="NotBefore">Start of validity, UTC.</param>
/// <param name="NotAfter">End of validity, UTC.</param>
/// <param name="ApplicationUri">Application URI from the subject alternative name, or null.</param>
/// <param name="IsAuthority">Whether it is a certification authority.</param>
public sealed record StoredCertificate(string Path, string Subject, string Thumbprint, DateTime NotBefore, DateTime NotAfter,
    string? ApplicationUri, bool IsAuthority);

/// <summary>How the store creates (and renews) the application's own certificate.</summary>
/// <param name="ApplicationUri">The application URI; the certificate is found and created by it.</param>
/// <param name="CommonName">Subject common name, usually the application name.</param>
public sealed record OwnCertificateSettings(string ApplicationUri, string CommonName)
{
    /// <summary>Subject organization, optional.</summary>
    public string? Organization { get; init; }

    /// <summary>Host names for the subject alternative name; the machine name when null.</summary>
    public IReadOnlyList<string>? DnsNames { get; init; }

    /// <summary>Lifetime of a new certificate. Two years by default.</summary>
    public TimeSpan Validity { get; init; } = TimeSpan.FromDays(730);

    /// <summary>
    /// Replace the certificate when it expires within this time. 30 days by default: long enough
    /// for operators to trust the new one on every peer before the old one runs out.
    /// </summary>
    public TimeSpan RenewBefore { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// A CA that signs the certificate instead of the certificate signing itself. Peers that trust
    /// this CA keep trusting the application across renewals; with a self-signed certificate every
    /// peer has to trust the renewed certificate again.
    /// </summary>
    public ApplicationCertificate? Issuer { get; init; }

    /// <summary>2048 or 4096.</summary>
    public int KeySizeBits { get; init; } = 2048;
}

/// <summary>
/// The application's certificates on disk, in the folder layout OPC UA uses (Part 12, F.1):
/// <code>
/// root/
///   own/certs       this application's certificate (.der)
///   own/private     its private key (.key, readable by the owner only)
///   own/old         replaced own certificates and keys, kept after a renewal
///   trusted/certs   trusted peer certificates and trusted CAs (.der)
///   trusted/crl     revocation lists of trusted CAs (.crl)
///   issuer/certs    CA certificates needed to build chains, not trusted by themselves
///   issuer/crl      their revocation lists
///   rejected/certs  peer certificates that were refused, for an operator to review
/// </code>
/// To trust a rejected peer, move its file from rejected/certs to trusted/certs (by hand or with
/// <see cref="TrustRejected"/>). Certificates may be DER or PEM; files are named
/// "&lt;common name&gt; [&lt;thumbprint&gt;].der" so a directory listing says what is what.
/// </summary>
/// <remarks>Safe to use from several threads and processes for reading; writes replace files atomically.</remarks>
public sealed class CertificateStore
{
    /// <summary>Most rejected certificates kept; the oldest are deleted beyond that.</summary>
    public const int MaxRejected = 100;

    private static readonly string[] CertificateExtensions = [".der", ".cer", ".crt", ".pem"];

    /// <summary>Opens (and creates, when missing) a store at <paramref name="path"/>.</summary>
    /// <param name="path">Root folder, for example <c>C:\ProgramData\Cictna\pki</c> or <c>/var/lib/cictna/pki</c>.</param>
    public CertificateStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
        foreach (var folder in new[] { OwnCertsPath, OwnPrivatePath, OwnOldPath, TrustedCertsPath, TrustedCrlPath, IssuerCertsPath, IssuerCrlPath, RejectedCertsPath })
        {
            Directory.CreateDirectory(folder);
        }
        RestrictToOwner(OwnPrivatePath, directory: true);
    }

    /// <summary>The root folder.</summary>
    public string Path { get; }

    /// <summary>own/certs</summary>
    public string OwnCertsPath => Combine("own", "certs");

    /// <summary>own/private</summary>
    public string OwnPrivatePath => Combine("own", "private");

    /// <summary>own/old</summary>
    public string OwnOldPath => Combine("own", "old");

    /// <summary>trusted/certs</summary>
    public string TrustedCertsPath => Combine("trusted", "certs");

    /// <summary>trusted/crl</summary>
    public string TrustedCrlPath => Combine("trusted", "crl");

    /// <summary>issuer/certs</summary>
    public string IssuerCertsPath => Combine("issuer", "certs");

    /// <summary>issuer/crl</summary>
    public string IssuerCrlPath => Combine("issuer", "crl");

    /// <summary>rejected/certs</summary>
    public string RejectedCertsPath => Combine("rejected", "certs");

    // ---- trust list ----------------------------------------------------------------------------

    /// <summary>Reads the trusted and issuer certificates and CRLs. Unreadable files are skipped.</summary>
    public TrustList ReadTrustList() => new(
        ReadCertificates(TrustedCertsPath).Select(c => c.Der).ToArray(),
        ReadCrls(TrustedCrlPath),
        ReadCertificates(IssuerCertsPath).Select(c => c.Der).ToArray(),
        ReadCrls(IssuerCrlPath));

    /// <summary>
    /// A value that changes whenever a file in the trusted or issuer folders is added, removed or
    /// replaced. Cheap to compute; used to reload the trust list of a running server.
    /// </summary>
    public string TrustListVersion()
    {
        var builder = new StringBuilder();
        foreach (var folder in new[] { TrustedCertsPath, TrustedCrlPath, IssuerCertsPath, IssuerCrlPath })
        {
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                builder.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Trusts a peer certificate (or a CA and everything it signs).</summary>
    /// <param name="certificate">The certificate, DER or PEM.</param>
    public StoredCertificate Trust(byte[] certificate) => Write(TrustedCertsPath, certificate);

    /// <summary>Adds a CA certificate needed to build chains, without trusting it by itself.</summary>
    /// <param name="certificate">The CA certificate, DER or PEM.</param>
    public StoredCertificate AddIssuer(byte[] certificate) => Write(IssuerCertsPath, certificate);

    /// <summary>Adds or replaces a revocation list.</summary>
    /// <param name="crl">The CRL, DER or PEM.</param>
    /// <param name="ofIssuer">True for a CA in issuer/certs, false for one in trusted/certs.</param>
    /// <returns>The file written.</returns>
    public string AddRevocationList(byte[] crl, bool ofIssuer = false)
    {
        var der = Der(crl, "X509 CRL");
        var folder = ofIssuer ? IssuerCrlPath : TrustedCrlPath;
        var file = System.IO.Path.Combine(folder, $"{Convert.ToHexString(SHA256.HashData(der))[..16]}.crl");
        WriteAtomically(file, der);
        return file;
    }

    /// <summary>Removes a trusted certificate. Returns false when it was not there.</summary>
    /// <param name="thumbprint">Its thumbprint.</param>
    public bool Untrust(string thumbprint) => Delete(TrustedCertsPath, thumbprint);

    /// <summary>The trusted certificates.</summary>
    public IReadOnlyList<StoredCertificate> Trusted => List(TrustedCertsPath);

    /// <summary>The issuer certificates.</summary>
    public IReadOnlyList<StoredCertificate> Issuers => List(IssuerCertsPath);

    // ---- rejected --------------------------------------------------------------------------------

    /// <summary>The refused peer certificates, newest first.</summary>
    public IReadOnlyList<StoredCertificate> Rejected =>
        List(RejectedCertsPath).OrderByDescending(c => File.GetLastWriteTimeUtc(c.Path)).ToArray();

    /// <summary>
    /// Records a refused peer certificate for review. Returns true when it was not there yet. At
    /// most <see cref="MaxRejected"/> are kept; the oldest go first.
    /// </summary>
    /// <param name="certificate">The certificate, DER or PEM.</param>
    public bool Reject(byte[] certificate)
    {
        var der = Der(certificate, "CERTIFICATE");
        using var cert = X509CertificateLoader.LoadCertificate(der);
        if (Find(RejectedCertsPath, cert.Thumbprint) is not null)
        {
            File.SetLastWriteTimeUtc(Find(RejectedCertsPath, cert.Thumbprint)!.Path, DateTime.UtcNow); // seen again: keep it
            return false;
        }
        Write(RejectedCertsPath, der);
        foreach (var old in Rejected.Skip(MaxRejected))
        {
            File.Delete(old.Path);
        }
        return true;
    }

    /// <summary>Moves a rejected certificate to the trusted ones. Returns false when it is not in rejected/certs.</summary>
    /// <param name="thumbprint">Its thumbprint, as shown in <see cref="Rejected"/> and in the file name.</param>
    public bool TrustRejected(string thumbprint)
    {
        var rejected = Find(RejectedCertsPath, thumbprint);
        if (rejected is null)
        {
            return false;
        }
        Trust(File.ReadAllBytes(rejected.Path));
        File.Delete(rejected.Path);
        return true;
    }

    // ---- own certificate ---------------------------------------------------------------------------

    /// <summary>
    /// Returns this application's certificate for <see cref="OwnCertificateSettings.ApplicationUri"/>,
    /// creating it when there is none and replacing it when it expires within
    /// <see cref="OwnCertificateSettings.RenewBefore"/>. A replaced certificate and key move to own/old.
    /// </summary>
    /// <param name="settings">How to find and create the certificate.</param>
    /// <param name="renewed">True when a new certificate was created because the old one was expiring.</param>
    public ApplicationCertificate GetOrCreateApplicationCertificate(OwnCertificateSettings settings, out bool renewed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        renewed = false;
        var current = FindOwn(settings.ApplicationUri);
        if (current is not null && !current.Value.Certificate.ExpiresWithin(settings.RenewBefore))
        {
            return current.Value.Certificate;
        }
        var created = ApplicationCertificate.Create(settings.ApplicationUri, settings.CommonName, settings.Organization,
            settings.DnsNames ?? [Environment.MachineName], settings.Issuer, validity: settings.Validity, keySizeBits: settings.KeySizeBits);
        if (current is { } old)
        {
            MoveToOld(old.CertificatePath);
            MoveToOld(old.KeyPath);
            renewed = true;
        }
        var stored = Write(OwnCertsPath, created.Certificate);
        var keyPath = System.IO.Path.Combine(OwnPrivatePath, System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(stored.Path), ".key"));
        WriteAtomically(keyPath, created.PrivateKey);
        RestrictToOwner(keyPath, directory: false);
        return created;
    }

    /// <summary>Same as <see cref="GetOrCreateApplicationCertificate(OwnCertificateSettings, out bool)"/>, without the renewal flag.</summary>
    /// <param name="settings">How to find and create the certificate.</param>
    public ApplicationCertificate GetOrCreateApplicationCertificate(OwnCertificateSettings settings) =>
        GetOrCreateApplicationCertificate(settings, out _);

    /// <summary>Puts an existing certificate and key (for example one issued by a plant CA) in own/.</summary>
    /// <param name="certificate">The certificate with its private key.</param>
    public StoredCertificate SetApplicationCertificate(ApplicationCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var uri = certificate.ApplicationUri ?? throw new ArgumentException("The certificate has no application URI.", nameof(certificate));
        if (FindOwn(uri) is { } old)
        {
            MoveToOld(old.CertificatePath);
            MoveToOld(old.KeyPath);
        }
        var stored = Write(OwnCertsPath, certificate.Certificate);
        var keyPath = System.IO.Path.Combine(OwnPrivatePath, System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(stored.Path), ".key"));
        WriteAtomically(keyPath, certificate.PrivateKey);
        RestrictToOwner(keyPath, directory: false);
        return stored;
    }

    /// <summary>The newest own certificate with a private key for the URI, or null.</summary>
    private (ApplicationCertificate Certificate, string CertificatePath, string KeyPath)? FindOwn(string applicationUri)
    {
        foreach (var (der, path) in ReadCertificates(OwnCertsPath).OrderByDescending(c => NotAfter(c.Der)))
        {
            if (ApplicationCertificate.ApplicationUriOf(der) != applicationUri)
            {
                continue;
            }
            var keyPath = System.IO.Path.Combine(OwnPrivatePath, System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(path), ".key"));
            if (File.Exists(keyPath))
            {
                return (new ApplicationCertificate(der, File.ReadAllBytes(keyPath)), path, keyPath);
            }
        }
        return null;
    }

    // ---- files -----------------------------------------------------------------------------------

    private string Combine(string a, string b) => System.IO.Path.Combine(Path, a, b);

    private static StoredCertificate Write(string folder, byte[] certificate)
    {
        var der = Der(certificate, "CERTIFICATE");
        using var cert = X509CertificateLoader.LoadCertificate(der);
        var name = SafeName(cert.GetNameInfo(X509NameType.SimpleName, false));
        var file = System.IO.Path.Combine(folder, $"{name} [{cert.Thumbprint}].der");
        WriteAtomically(file, der);
        return Describe(file, der);
    }

    private static StoredCertificate[] List(string folder) =>
        ReadCertificates(folder).Select(c => Describe(c.Path, c.Der)).ToArray();

    private static StoredCertificate? Find(string folder, string thumbprint) =>
        List(folder).FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    private static bool Delete(string folder, string thumbprint)
    {
        var found = Find(folder, thumbprint);
        if (found is null)
        {
            return false;
        }
        File.Delete(found.Path);
        return true;
    }

    private void MoveToOld(string file)
    {
        var target = System.IO.Path.Combine(OwnOldPath, System.IO.Path.GetFileName(file));
        File.Move(file, target, overwrite: true);
    }

    private static StoredCertificate Describe(string path, byte[] der)
    {
        var c = new ApplicationCertificate(der, []);
        return new StoredCertificate(path, c.Subject, c.Thumbprint, c.NotBefore, c.NotAfter, c.ApplicationUri, c.IsAuthority);
    }

    private static DateTime NotAfter(byte[] der)
    {
        using var cert = X509CertificateLoader.LoadCertificate(der);
        return cert.NotAfter.ToUniversalTime();
    }

    /// <summary>Every readable certificate in a folder; files that are not certificates are skipped.</summary>
    private static List<(byte[] Der, string Path)> ReadCertificates(string folder)
    {
        var result = new List<(byte[], string)>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            if (!CertificateExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var der = Der(File.ReadAllBytes(file), "CERTIFICATE");
                using var check = X509CertificateLoader.LoadCertificate(der);
                result.Add((der, file));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or FormatException)
            {
                // A broken or half-copied file must not make the whole store unusable.
            }
        }
        return result;
    }

    private static byte[][] ReadCrls(string folder)
    {
        var result = new List<byte[]>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.crl").Order(StringComparer.Ordinal))
        {
            try
            {
                result.Add(Der(File.ReadAllBytes(file), "X509 CRL"));
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                // skipped, as above
            }
        }
        return result.ToArray();
    }

    /// <summary>DER bytes from DER or PEM input.</summary>
    private static byte[] Der(byte[] data, string pemLabel)
    {
        if (data.Length > 0 && data[0] == 0x30)
        {
            return data; // DER always starts with a SEQUENCE
        }
        var text = System.Text.Encoding.ASCII.GetString(data);
        if (!PemEncoding.TryFind(text, out var fields))
        {
            throw new FormatException("Neither DER nor PEM.");
        }
        if (text[fields.Label] != pemLabel)
        {
            throw new FormatException($"Expected PEM \"{pemLabel}\", found \"{text[fields.Label]}\".");
        }
        return Convert.FromBase64String(text[fields.Base64Data]);
    }

    private static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) || ch is '[' or ']' ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? "certificate" : clean;
    }

    private static void WriteAtomically(string file, byte[] content)
    {
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Private keys are readable by the owner only (Unix permissions; on Windows the folder's ACL decides).</summary>
    private static void RestrictToOwner(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
