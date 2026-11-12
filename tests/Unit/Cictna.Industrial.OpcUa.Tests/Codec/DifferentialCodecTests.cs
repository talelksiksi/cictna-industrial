using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;

namespace Cictna.Industrial.OpcUa.Tests.Codec;

/// <summary>
/// Differential tests: the C# codec against open62541 as the reference.
/// </summary>
/// <remarks>
/// For each value: C# encodes it, open62541 decodes and re-encodes those bytes, and the two byte
/// sequences must be identical (so open62541 reads exactly what we meant and our encoding is the
/// canonical one). Then C# decodes open62541's bytes and must get the original value back. This
/// covers both directions of the native boundary: requests we send and responses we receive.
/// </remarks>
public sealed class DifferentialCodecTests
{
    private const int RandomCases = 3000;

    [Fact]
    public void RandomVariantsMatchOpen62541ByteForByte()
    {
        for (var seed = 0; seed < RandomCases; seed++)
        {
            var value = new RandomValues(seed).Variant();
            AssertVariantRoundTrip(value, seed);
        }
    }

    [Fact]
    public void RandomDataValuesMatchOpen62541ByteForByte()
    {
        for (var seed = 0; seed < RandomCases / 3; seed++)
        {
            var value = new RandomValues(seed).DataValue(0);
            var bytes = Encode(e => e.WriteDataValue(value));
            var reference = Open62541Reference.RoundTrip(Open62541Reference.TypeIds.DataValue, bytes, $"seed {seed}: {value}");
            Assert.True(bytes.AsSpan().SequenceEqual(reference), $"seed {seed}: {Hex(bytes)} vs {Hex(reference)}");
            Assert.Equal(value, Decode(reference, d => d.ReadDataValue()));
        }
    }

    [Fact]
    public void RandomDiagnosticInfosMatchOpen62541ByteForByte()
    {
        for (var seed = 0; seed < RandomCases / 3; seed++)
        {
            var value = new RandomValues(seed).DiagnosticInfo(0);
            var bytes = Encode(e => e.WriteDiagnosticInfo(value));
            var reference = Open62541Reference.RoundTrip(Open62541Reference.TypeIds.DiagnosticInfo, bytes);
            Assert.True(bytes.AsSpan().SequenceEqual(reference), $"seed {seed}");
            var decoded = Decode(reference, d => d.ReadDiagnosticInfo());
            // An all-empty DiagnosticInfo encodes as mask 0 and decodes as "none".
            Assert.Equal(value == new DiagnosticInfo() ? null : value, decoded);
        }
    }

    [Fact]
    public void RandomNodeIdsUseTheSameCompactFormsAsOpen62541()
    {
        for (var seed = 0; seed < RandomCases / 3; seed++)
        {
            var random = new RandomValues(seed);
            var id = random.NodeId();
            var bytes = Encode(e => e.WriteNodeId(id));
            Assert.True(bytes.AsSpan().SequenceEqual(Open62541Reference.RoundTrip(Open62541Reference.TypeIds.NodeId, bytes)), $"seed {seed}: {id}");
            Assert.Equal(id, Decode(bytes, d => d.ReadNodeId()));

            var expanded = (ExpandedNodeId)random.Scalar(BuiltInType.ExpandedNodeId, 0)!;
            var eb = Encode(e => e.WriteExpandedNodeId(expanded));
            Assert.True(eb.AsSpan().SequenceEqual(Open62541Reference.RoundTrip(Open62541Reference.TypeIds.ExpandedNodeId, eb)), $"seed {seed}: {expanded}");
            Assert.Equal(expanded, Decode(eb, d => d.ReadExpandedNodeId()));
        }
    }

    [Theory]
    [InlineData(0u, 0, new byte[] { 0x00, 0x00 })]                        // two-byte
    [InlineData(255u, 0, new byte[] { 0x00, 0xFF })]                      // two-byte max
    [InlineData(256u, 0, new byte[] { 0x01, 0x00, 0x00, 0x01 })]          // four-byte
    [InlineData(1u, 1, new byte[] { 0x01, 0x01, 0x01, 0x00 })]            // four-byte with namespace
    [InlineData(65536u, 0, new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 })] // full numeric
    [InlineData(1u, 256, new byte[] { 0x02, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00 })]   // namespace > 255
    public void NumericNodeIdEncodingsMatchThePartSixExamples(uint id, int ns, byte[] expected)
    {
        Assert.Equal(expected, Encode(e => e.WriteNodeId(new NodeId(id, (ushort)ns))));
    }

    [Fact]
    public void ServiceStructuresMatchOpen62541()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var random = new RandomValues(seed);
            var reference = new ReferenceDescription(random.NodeId(), seed % 2 == 0,
                new ExpandedNodeId(random.NodeId()), new QualifiedName(2, random.String()),
                new LocalizedText("en", random.String()), NodeClass.Variable, new ExpandedNodeId(random.NodeId()));
            var rb = Encode(reference.Encode);
            Assert.True(rb.AsSpan().SequenceEqual(Open62541Reference.RoundTrip(Open62541Reference.TypeIds.ReferenceDescription, rb)), $"seed {seed}");
            Assert.Equal(reference, Decode(rb, ReferenceDescription.Decode));

            var endpoint = new EndpointDescription("opc.tcp://host:4840",
                new ApplicationDescription("urn:x", null, new LocalizedText(null, "Server"), ApplicationType.Server, null, null, ["opc.tcp://host:4840"]),
                random.Bytes(seed % 50), MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256,
                [new UserTokenPolicy("anon", UserTokenType.Anonymous, null, null, null), new UserTokenPolicy("user", UserTokenType.UserName, null, null, SecurityPolicies.Basic256Sha256)],
                "http://opcfoundation.org/UA-Profile/Transport/uatcp-uasc-uabinary", (byte)seed);
            var eb = Encode(endpoint.Encode);
            Assert.True(eb.AsSpan().SequenceEqual(Open62541Reference.RoundTrip(Open62541Reference.TypeIds.EndpointDescription, eb)), $"seed {seed}");
            var decoded = Decode(eb, EndpointDescription.Decode);
            Assert.Equal(endpoint.EndpointUrl, decoded.EndpointUrl);
            Assert.Equal(endpoint.UserIdentityTokens, decoded.UserIdentityTokens);
            Assert.Equal(endpoint.ServerCertificate, decoded.ServerCertificate);

            var header = new ResponseHeader(random.DateTime(), (uint)seed, new StatusCode((uint)seed << 16),
                seed % 3 == 0 ? random.DiagnosticInfo(0) : null, ["a", null, "c"]);
            var hb = Encode(header.Encode);
            Assert.True(hb.AsSpan().SequenceEqual(Open62541Reference.RoundTrip(Open62541Reference.TypeIds.ResponseHeader, hb)), $"seed {seed}");
        }
    }

    [Fact]
    public void RequestsAreDecodedByOpen62541()
    {
        var random = new RandomValues(1);
        var read = new ReadRequest(new RequestHeader(5000), 0, TimestampsToReturn.Both,
            [new ReadValueId(new NodeId(2258)), new ReadValueId(new NodeId("A", 2), AttributeId.DisplayName, "1:2")]).Encode();
        // open62541 keeps every header field as sent, so the request survives byte for byte.
        Assert.Equal(read, Open62541Reference.RoundTrip(EncodingIds.ReadRequest, read));

        var write = new WriteRequest(new RequestHeader(), [new WriteValue(new NodeId("B", 2), new DataValue(random.Variant()))]).Encode();
        Open62541Reference.RoundTrip(EncodingIds.WriteRequest, write);

        var browse = new BrowseRequest(new RequestHeader(), 100, [new BrowseDescription(WellKnownNodes.ObjectsFolder)]).Encode();
        Open62541Reference.RoundTrip(EncodingIds.BrowseRequest, browse);

        var browseNext = new BrowseNextRequest(new RequestHeader(), false, [[1, 2, 3]]).Encode();
        Open62541Reference.RoundTrip(EncodingIds.BrowseNextRequest, browseNext);

        var variable = new VariableAttributes(new LocalizedText(null, "Speed"), default, 1.5, new NodeId(11), -1, null, 3).Encode();
        Open62541Reference.RoundTrip(EncodingIds.VariableAttributes, variable);

        var obj = new ObjectAttributes(new LocalizedText(null, "Pumps"), default).Encode();
        Open62541Reference.RoundTrip(EncodingIds.ObjectAttributes, obj);

        var add = new AddNodesItem(WellKnownNodes.ObjectsFolder, WellKnownNodes.Organizes, new NodeId("Pumps", 2),
            new QualifiedName(2, "Pumps"), NodeClass.Object, ExtensionObject.FromBinary(new NodeId(EncodingIds.ObjectAttributes), obj),
            WellKnownNodes.FolderType).Encode();
        Open62541Reference.RoundTrip(EncodingIds.AddNodesItem, add);
    }

    [Fact]
    public void SpecialDateTimesFollowPartSix()
    {
        Assert.Equal(new byte[8], Encode(e => e.WriteDateTime(DateTime.MinValue)));
        Assert.Equal(BitConverter.GetBytes(long.MaxValue), Encode(e => e.WriteDateTime(DateTime.MaxValue)));
        Assert.Equal(DateTime.MinValue, Decode(new byte[8], d => d.ReadDateTime()));
        Assert.Equal(DateTime.MinValue, Decode(BitConverter.GetBytes(-5L), d => d.ReadDateTime()));
        Assert.Equal(DateTime.MaxValue, Decode(BitConverter.GetBytes(long.MaxValue), d => d.ReadDateTime()));
        var t = new DateTime(2026, 11, 12, 10, 30, 0, DateTimeKind.Utc).AddTicks(1234567);
        Assert.Equal(t, Decode(Encode(e => e.WriteDateTime(t)), d => d.ReadDateTime()));
        Assert.Equal(DateTimeKind.Utc, Decode(Encode(e => e.WriteDateTime(t)), d => d.ReadDateTime()).Kind);
    }

    private static void AssertVariantRoundTrip(Variant value, int seed)
    {
        var bytes = Encode(e => e.WriteVariant(value));
        var reference = Open62541Reference.RoundTrip(Open62541Reference.TypeIds.Variant, bytes, $"seed {seed}: {value}");
        Assert.True(bytes.AsSpan().SequenceEqual(reference),
            $"seed {seed}: {value}\nC#:        {Hex(bytes)}\nopen62541: {Hex(reference)}");
        var decoded = Decode(reference, d => d.ReadVariant());
        Assert.True(value.Equals(decoded), $"seed {seed}: decoded {decoded} differs from {value}");
    }

    internal static byte[] Encode(Action<BinaryEncoder> write)
    {
        var e = new BinaryEncoder();
        write(e);
        return e.ToArray();
    }

    internal static T Decode<T>(byte[] bytes, Func<BinaryDecoder, T> read)
    {
        var d = new BinaryDecoder(bytes);
        var value = read(d);
        d.EnsureFullyConsumed();
        return value;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b.Length > 200 ? b[..200] : b);
}
