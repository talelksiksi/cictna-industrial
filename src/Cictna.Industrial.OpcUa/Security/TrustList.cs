using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Cictna.Industrial.OpcUa.Native;

namespace Cictna.Industrial.OpcUa;

/// <summary>
/// What a peer's certificate is checked against (Part 12, 7.8.2): trusted certificates (a peer's
/// own self-signed certificate, or a CA whose certificates are all trusted), issuer certificates
/// (CAs needed to build a chain but not trusted by themselves), and revocation lists for both.
/// </summary>
/// <param name="Trusted">Trusted certificates, DER.</param>
/// <param name="TrustedCrls">Revocation lists of trusted CAs, DER.</param>
/// <param name="Issuers">Issuer (intermediate or root CA) certificates, DER.</param>
/// <param name="IssuerCrls">Revocation lists of issuer CAs, DER.</param>
public sealed record TrustList(
    IReadOnlyList<byte[]> Trusted,
    IReadOnlyList<byte[]> TrustedCrls,
    IReadOnlyList<byte[]> Issuers,
    IReadOnlyList<byte[]> IssuerCrls)
{
    /// <summary>Nothing trusted: every certificate is rejected.</summary>
    public static TrustList Empty { get; } = new([], [], [], []);

    /// <summary>
    /// Checks a certificate against this trust list exactly as open62541 checks a peer during the
    /// handshake (Part 6, 6.1.3): structure, not a CA, chain to a trusted certificate, validity
    /// period of every certificate in the chain, and revocation when CRLs are present.
    /// </summary>
    /// <param name="certificate">The certificate, DER.</param>
    /// <returns>Good, or why it would be refused (BadCertificateUntrusted, BadCertificateTimeInvalid, BadCertificateRevoked...).</returns>
    public unsafe StatusCode Verify(byte[] certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        OpcUaNativeLibrary.EnsureCompatible();
        var list = Encode();
        fixed (byte* l = list)
        fixed (byte* c = certificate)
        {
            return new StatusCode(NativeMethods.VerifyCertificate(l, (nuint)list.Length, c, (nuint)certificate.Length));
        }
    }

    /// <summary>Encodes as an OPC UA TrustListDataType, the form open62541 takes it in.</summary>
    internal byte[] Encode()
    {
        var e = new BinaryEncoder();
        e.WriteUInt32(15); // specifiedLists: TrustedCertificates | TrustedCrls | IssuerCertificates | IssuerCrls
        e.WriteArray(Trusted, (x, b) => x.WriteByteString(b));
        e.WriteArray(TrustedCrls, (x, b) => x.WriteByteString(b));
        e.WriteArray(Issuers, (x, b) => x.WriteByteString(b));
        e.WriteArray(IssuerCrls, (x, b) => x.WriteByteString(b));
        return e.ToArray();
    }

    /// <summary>Decodes a TrustListDataType (lists that are not specified come back empty).</summary>
    internal static TrustList Decode(BinaryDecoder d)
    {
        d.ReadUInt32(); // specifiedLists
        static byte[][] List(BinaryDecoder x) => (x.ReadArray(y => y.ReadByteString() ?? []) ?? []);
        return new TrustList(List(d), List(d), List(d), List(d));
    }
}
