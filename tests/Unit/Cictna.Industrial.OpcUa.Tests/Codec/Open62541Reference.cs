using Cictna.Industrial.OpcUa.Native;

namespace Cictna.Industrial.OpcUa.Tests.Codec;

/// <summary>
/// open62541 used as the reference implementation of the OPC UA binary encoding: decodes bytes
/// with open62541 and encodes them again (cictna_ua_codec_roundtrip).
/// </summary>
internal static class Open62541Reference
{
    /// <summary>Type ids accepted by the shim: namespace-0 DataType ids or DefaultBinary encoding ids.</summary>
    public static class TypeIds
    {
        public const uint String = 12;
        public const uint DateTime = 13;
        public const uint Guid = 14;
        public const uint ByteString = 15;
        public const uint NodeId = 17;
        public const uint ExpandedNodeId = 18;
        public const uint QualifiedName = 20;
        public const uint LocalizedText = 21;
        public const uint ExtensionObject = 22;
        public const uint DataValue = 23;
        public const uint Variant = 24;
        public const uint DiagnosticInfo = 25;
        public const uint ReferenceDescription = 520; // DefaultBinary encoding id
        public const uint EndpointDescription = 314;
        public const uint ResponseHeader = 394;
    }

    /// <summary>Decodes with open62541 and re-encodes; fails the test on any decoding problem.</summary>
    public static byte[] RoundTrip(uint typeId, ReadOnlySpan<byte> input, string context = "")
    {
        var (status, output, consumed) = TryRoundTrip(typeId, input);
        Assert.True(status == 0, $"{context}: open62541 rejected the bytes ({OpcUaNativeLibrary.GetStatusName(status)}): {Convert.ToHexString(input.Length > 200 ? input[..200] : input)}");
        Assert.True(consumed == input.Length, $"{context}: open62541 consumed {consumed} of {input.Length} bytes");
        return output;
    }

    public static unsafe (uint Status, byte[] Output, int Consumed) TryRoundTrip(uint typeId, ReadOnlySpan<byte> input)
    {
        fixed (byte* p = input)
        {
            var status = NativeMethods.CodecRoundtrip(typeId, p, (nuint)input.Length, out var output, out var length, out var consumed);
            var bytes = NativeMethods.TakeBuffer(output, length);
            return (status, bytes, (int)consumed);
        }
    }
}
