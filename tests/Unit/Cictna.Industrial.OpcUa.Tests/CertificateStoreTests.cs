using System.Security.Cryptography.X509Certificates;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Tests;

/// <summary>The certificate store's folders and files, and certificates created in .NET.</summary>
public sealed class CertificateStoreTests : IDisposable
{
    private const string Uri = "urn:cictna:test:store";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cictna-store-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp folder
        }
    }

    private CertificateStore NewStore() => new(_root);

    [Fact]
    public void TheFolderLayoutIsCreated()
    {
        var store = NewStore();
        foreach (var folder in new[] { "own/certs", "own/private", "own/old", "trusted/certs", "trusted/crl", "issuer/certs", "issuer/crl", "rejected/certs" })
        {
            Assert.True(Directory.Exists(Path.Combine(_root, folder)), folder);
        }
        if (OperatingSystem.IsWindows())
        {
            AssertOnlyOwnerSystemAndAdministrators(store.OwnPrivatePath);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.OwnPrivatePath));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOnlyOwnerSystemAndAdministrators(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "the private folder must not inherit permissions");
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var allowed = new HashSet<System.Security.Principal.SecurityIdentifier>
        {
            me.User!,
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Contains((System.Security.Principal.SecurityIdentifier)r.IdentityReference, allowed));
    }

    [Fact]
    public void TheOwnCertificateIsCreatedOnceAndThenReused()
    {
        var store = NewStore();
        var settings = new OwnCertificateSettings(Uri, "Store Test") { Organization = "CICTNA RDC", DnsNames = ["plc-gw01"] };
        var first = store.GetOrCreateApplicationCertificate(settings, out var renewed);
        Assert.False(renewed);
        var second = store.GetOrCreateApplicationCertificate(settings);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
        Assert.Equal(Uri, first.ApplicationUri);
        Assert.Contains("CN=Store Test", first.Subject, StringComparison.Ordinal);
        Assert.False(first.IsAuthority);
        var key = Assert.Single(Directory.GetFiles(store.OwnPrivatePath));
        Assert.Contains(first.Thumbprint, key, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }
    }

    [Fact]
    public void AnExpiringCertificateIsRenewedAndTheOldOneKept()
    {
        var store = NewStore();
        var expiring = ApplicationCertificate.Create(Uri, "Store Test", validity: TimeSpan.FromDays(10));
        store.SetApplicationCertificate(expiring);
        var settings = new OwnCertificateSettings(Uri, "Store Test") { RenewBefore = TimeSpan.FromDays(30) };
        var renewedCertificate = store.GetOrCreateApplicationCertificate(settings, out var renewed);
        Assert.True(renewed);
        Assert.NotEqual(expiring.Thumbprint, renewedCertificate.Thumbprint);
        Assert.Equal(2, Directory.GetFiles(store.OwnOldPath).Length); // certificate and key
        Assert.Single(Directory.GetFiles(store.OwnCertsPath));
        // Not renewed again on the next call.
        store.GetOrCreateApplicationCertificate(settings, out var again);
        Assert.False(again);
    }

    [Fact]
    public void RejectedCertificatesCanBeTrustedByThumbprint()
    {
        var store = NewStore();
        var peer = ApplicationCertificate.Create("urn:peer", "Peer");
        Assert.True(store.Reject(peer.Certificate));
        Assert.False(store.Reject(peer.Certificate)); // the same one again
        var rejected = Assert.Single(store.Rejected);
        Assert.Equal(peer.Thumbprint, rejected.Thumbprint);
        Assert.Equal("urn:peer", rejected.ApplicationUri);
        Assert.Contains($"Peer [{peer.Thumbprint}].der", rejected.Path, StringComparison.Ordinal);

        Assert.True(store.TrustRejected(peer.Thumbprint.ToLowerInvariant()));
        Assert.Empty(store.Rejected);
        Assert.Equal(peer.Thumbprint, Assert.Single(store.Trusted).Thumbprint);
        Assert.False(store.TrustRejected(peer.Thumbprint));
        Assert.True(store.Untrust(peer.Thumbprint));
        Assert.Empty(store.Trusted);
    }

    [Fact]
    public void AtMostAHundredRejectedCertificatesAreKept()
    {
        var store = NewStore();
        using var key = System.Security.Cryptography.RSA.Create(2048); // one key, many certificates: fast
        for (var i = 0; i < CertificateStore.MaxRejected + 3; i++)
        {
            var request = new CertificateRequest($"CN=Peer {i}", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1));
            store.Reject(cert.RawData);
        }
        Assert.Equal(CertificateStore.MaxRejected, store.Rejected.Count);
    }

    [Fact]
    public void PemFilesAndBrokenFilesAreHandled()
    {
        var store = NewStore();
        var peer = ApplicationCertificate.Create("urn:peer", "Peer");
        using var x509 = X509CertificateLoader.LoadCertificate(peer.Certificate);
        File.WriteAllText(Path.Combine(store.TrustedCertsPath, "peer.pem"), x509.ExportCertificatePem());
        File.WriteAllText(Path.Combine(store.TrustedCertsPath, "half-copied.der"), "not a certificate");
        File.WriteAllText(Path.Combine(store.TrustedCertsPath, "readme.txt"), "ignored");
        var list = store.ReadTrustList();
        Assert.Equal(peer.Certificate, Assert.Single(list.Trusted));
        Assert.Equal(StatusCodes.Good, list.Verify(peer.Certificate));
    }

    [Fact]
    public void TheTrustListVersionChangesWithTheTrustedFolders()
    {
        var store = NewStore();
        var empty = store.TrustListVersion();
        Assert.Equal(empty, store.TrustListVersion());
        var peer = ApplicationCertificate.Create("urn:peer", "Peer");
        store.Reject(peer.Certificate);
        Assert.Equal(empty, store.TrustListVersion()); // rejected/ does not count
        store.Trust(peer.Certificate);
        var trusted = store.TrustListVersion();
        Assert.NotEqual(empty, trusted);
        store.Untrust(peer.Thumbprint);
        Assert.NotEqual(trusted, store.TrustListVersion());
    }

    // ---- certificates and verification (open62541's rules) ---------------------------------------

    [Fact]
    public void VerificationFollowsTrustValidityChainsAndRevocation()
    {
        var ca = ApplicationCertificate.CreateAuthority("Plant CA", "CICTNA RDC");
        Assert.True(ca.IsAuthority);
        var issued = ApplicationCertificate.Create("urn:peer", "Peer", issuer: ca);
        var selfSigned = ApplicationCertificate.Create("urn:other", "Other");
        var expired = ApplicationCertificate.Create("urn:old", "Old", notBefore: DateTime.UtcNow.AddDays(-30), validity: TimeSpan.FromDays(10));

        Assert.Equal(StatusCodes.BadCertificateUntrusted, TrustList.Empty.Verify(selfSigned.Certificate));
        Assert.Equal(StatusCodes.Good, new TrustList([selfSigned.Certificate], [], [], []).Verify(selfSigned.Certificate));
        Assert.Equal(StatusCodes.BadCertificateTimeInvalid, new TrustList([expired.Certificate], [], [], []).Verify(expired.Certificate));
        Assert.Equal(StatusCodes.Good, new TrustList([ca.Certificate], [], [], []).Verify(issued.Certificate));
        Assert.Equal(StatusCodes.BadCertificateUntrusted, new TrustList([], [], [ca.Certificate], []).Verify(issued.Certificate));
        Assert.Equal(StatusCodes.BadCertificateRevoked,
            new TrustList([ca.Certificate], [ca.CreateRevocationList([issued.Certificate], 1)], [], []).Verify(issued.Certificate));
        Assert.Equal(StatusCodes.BadCertificateUseNotAllowed, new TrustList([ca.Certificate], [], [], []).Verify(ca.Certificate));
    }

    [Fact]
    public void ASignedCertificateNeverOutlivesItsIssuer()
    {
        var ca = ApplicationCertificate.CreateAuthority("Short CA", validity: TimeSpan.FromDays(100));
        var issued = ApplicationCertificate.Create("urn:peer", "Peer", issuer: ca, validity: TimeSpan.FromDays(730));
        Assert.True(issued.NotAfter <= ca.NotAfter);
    }

    [Fact]
    public void NativeAndDotNetCertificatesCarryTheApplicationUri()
    {
        var native = ApplicationCertificate.CreateSelfSigned("urn:native", "Native");
        Assert.Equal("urn:native", native.ApplicationUri);
        Assert.Equal(StatusCodes.Good, new TrustList([native.Certificate], [], [], []).Verify(native.Certificate));
        Assert.True(native.ExpiresWithin(TimeSpan.FromDays(400)));
        Assert.False(native.ExpiresWithin(TimeSpan.FromDays(300)));
    }

    [Fact]
    public void KeySizeIsChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplicationCertificate.Create("urn:x", "X", keySizeBits: 1024));
        Assert.Throws<InvalidOperationException>(() => ApplicationCertificate.Create("urn:x", "X").CreateRevocationList([], 1));
    }
}
