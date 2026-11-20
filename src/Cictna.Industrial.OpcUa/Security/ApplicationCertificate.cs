using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cictna.Industrial.OpcUa;

// Certificate creation and inspection in .NET, next to the native CreateSelfSigned in
// ClientTypes.cs. .NET can also create certification authorities, certificates signed by them,
// revocation lists and certificates with any validity period (expired ones for tests), which the
// native helper cannot.
//
// What OPC UA expects of an application instance certificate (Part 6, 6.2.2):
// - subject alternative name with the application URI (and the host's DNS names);
// - key usage digitalSignature, nonRepudiation, keyEncipherment, dataEncipherment (self-signed
//   ones also keyCertSign, because they sign themselves);
// - extended key usage serverAuth and clientAuth;
// - basic constraints CA=false. open62541 refuses a CA certificate used as an application
//   certificate (BadCertificateUseNotAllowed).
// - RSA 2048 or 4096 with SHA-256 for Basic256Sha256 and the Aes policies.
public sealed partial record ApplicationCertificate
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Creates an application instance certificate in .NET: self-signed, or signed by
    /// <paramref name="issuer"/> (a certification authority from <see cref="CreateAuthority"/>).
    /// </summary>
    /// <param name="applicationUri">The application URI, written into the subject alternative name.</param>
    /// <param name="commonName">Subject common name, usually the application name.</param>
    /// <param name="organization">Subject organization, optional.</param>
    /// <param name="dnsNames">Host names of the machine, optional but expected for servers.</param>
    /// <param name="issuer">The CA that signs it; null for self-signed.</param>
    /// <param name="notBefore">Start of validity; now minus one hour when null (tolerates clock skew).</param>
    /// <param name="validity">Lifetime from <paramref name="notBefore"/>; two years when null. Capped at the issuer's end of validity.</param>
    /// <param name="keySizeBits">2048 or 4096.</param>
    public static ApplicationCertificate Create(string applicationUri, string commonName, string? organization = null,
        IEnumerable<string>? dnsNames = null, ApplicationCertificate? issuer = null, DateTime? notBefore = null,
        TimeSpan? validity = null, int keySizeBits = 2048)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationUri);
        ArgumentException.ThrowIfNullOrEmpty(commonName);
        CheckKeySize(keySizeBits);
        using var key = RSA.Create(keySizeBits);
        var request = new CertificateRequest(SubjectName(commonName, organization), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(applicationUri));
        foreach (var dns in dnsNames ?? [])
        {
            san.AddDnsName(dns);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation |
                    X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment;
        if (issuer is null)
        {
            usage |= X509KeyUsageFlags.KeyCertSign; // it signs itself
        }
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuth), new Oid(ClientAuth)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var from = notBefore ?? DateTime.UtcNow.AddHours(-1);
        var to = from + (validity ?? TimeSpan.FromDays(730));
        if (issuer is null)
        {
            using var self = request.CreateSelfSigned(from, to);
            return new ApplicationCertificate(self.RawData, key.ExportPkcs8PrivateKey());
        }

        using var ca = issuer.ToX509WithKey();
        if (to > ca.NotAfter.ToUniversalTime())
        {
            to = ca.NotAfter.ToUniversalTime(); // a certificate cannot outlive its issuer
        }
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var signed = request.Create(ca, from, to, NewSerial());
        return new ApplicationCertificate(signed.RawData, key.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// Creates a self-signed certification authority, for a plant or site PKI: servers and clients
    /// that trust the CA trust every certificate it signs, including renewed ones.
    /// </summary>
    /// <param name="commonName">Subject common name, for example "Plant 3 CA".</param>
    /// <param name="organization">Subject organization, optional.</param>
    /// <param name="validity">Lifetime; ten years when null.</param>
    /// <param name="keySizeBits">2048 or 4096.</param>
    public static ApplicationCertificate CreateAuthority(string commonName, string? organization = null, TimeSpan? validity = null, int keySizeBits = 2048)
    {
        ArgumentException.ThrowIfNullOrEmpty(commonName);
        CheckKeySize(keySizeBits);
        using var key = RSA.Create(keySizeBits);
        var request = new CertificateRequest(SubjectName(commonName, organization), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var from = DateTime.UtcNow.AddHours(-1);
        using var ca = request.CreateSelfSigned(from, from + (validity ?? TimeSpan.FromDays(3650)));
        return new ApplicationCertificate(ca.RawData, key.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// Creates a certificate revocation list signed by this CA. Once any CRL is in a trust list,
    /// open62541 expects one from every CA in the chain (otherwise BadCertificateRevocationUnknown),
    /// so issue an empty one when nothing is revoked.
    /// </summary>
    /// <param name="revoked">Certificates (DER) issued by this CA that are no longer valid.</param>
    /// <param name="crlNumber">Increasing number of this list.</param>
    /// <param name="nextUpdate">When a newer list is due; 30 days when null.</param>
    /// <returns>The CRL, DER-encoded.</returns>
    public byte[] CreateRevocationList(IEnumerable<byte[]> revoked, BigInteger crlNumber, DateTime? nextUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        using var ca = ToX509WithKey();
        if (!IsAuthority)
        {
            throw new InvalidOperationException("Only a certification authority can sign a revocation list.");
        }
        var builder = new CertificateRevocationListBuilder();
        foreach (var der in revoked)
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            builder.AddEntry(cert, DateTimeOffset.UtcNow.AddMinutes(-5), X509RevocationReason.KeyCompromise);
        }
        var now = DateTimeOffset.UtcNow;
        var next = nextUpdate is { } n ? new DateTimeOffset(DateTime.SpecifyKind(n, DateTimeKind.Utc)) : now.AddDays(30);
        return builder.Build(ca, crlNumber, next, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now.AddMinutes(-5));
    }

    // ---- inspection (parsed on demand: no cached fields, so record equality stays byte-based) ----

    /// <summary>The subject, for example <c>CN=Plant Data Logger, O=CICTNA RDC</c>.</summary>
    public string Subject => Load().Subject;

    /// <summary>SHA-1 thumbprint in upper-case hex, as OPC UA tools and certificate file names show it.</summary>
    public string Thumbprint => Load().Thumbprint;

    /// <summary>Start of validity, UTC.</summary>
    public DateTime NotBefore => Load().NotBefore.ToUniversalTime();

    /// <summary>End of validity, UTC.</summary>
    public DateTime NotAfter => Load().NotAfter.ToUniversalTime();

    /// <summary>The application URI from the subject alternative name, or null.</summary>
    public string? ApplicationUri => ApplicationUriOf(Certificate);

    /// <summary>Whether this is a certification authority (basic constraints CA=true).</summary>
    public bool IsAuthority =>
        Load().Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;

    /// <summary>Whether the certificate is expired or expires within <paramref name="margin"/> from <paramref name="nowUtc"/>.</summary>
    public bool ExpiresWithin(TimeSpan margin, DateTime? nowUtc = null) => NotAfter - margin <= (nowUtc ?? DateTime.UtcNow);

    private X509Certificate2 Load() => X509CertificateLoader.LoadCertificate(Certificate);

    /// <summary>The certificate with its private key, for signing. Dispose it.</summary>
    internal X509Certificate2 ToX509WithKey()
    {
        using var cert = X509CertificateLoader.LoadCertificate(Certificate);
        using var key = RSA.Create();
        try
        {
            key.ImportPkcs8PrivateKey(PrivateKey, out _);
        }
        catch (CryptographicException)
        {
            key.ImportRSAPrivateKey(PrivateKey, out _); // PKCS#1, as the native helper writes it
        }
        return cert.CopyWithPrivateKey(key);
    }

    /// <summary>The application URI in a certificate's subject alternative name, or null.</summary>
    internal static string? ApplicationUriOf(byte[] certificate)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certificate);
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return null;
        }
        // .NET exposes DNS names and IP addresses but not URIs, so read the GeneralNames directly:
        // uniformResourceIdentifier is the context-specific tag [6] (RFC 5280, 4.2.1.6).
        var reader = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (tag.HasSameClassAndValue(uriTag))
            {
                return reader.ReadCharacterString(UniversalTagNumber.IA5String, uriTag);
            }
            reader.ReadEncodedValue();
        }
        return null;
    }

    private static X500DistinguishedName SubjectName(string commonName, string? organization)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        if (!string.IsNullOrEmpty(organization))
        {
            builder.AddOrganizationName(organization);
        }
        return builder.Build();
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        return serial;
    }

    private static void CheckKeySize(int keySizeBits)
    {
        if (keySizeBits is not (2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(keySizeBits), keySizeBits, "Use 2048 or 4096.");
        }
    }
}
