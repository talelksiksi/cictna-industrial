using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;

namespace Cictna.Industrial.OpcUa.Tests.Codec;

/// <summary>
/// The decoder reads bytes produced by servers we do not control. Whatever the input, it must
/// either decode or throw <see cref="OpcUaDecodingException"/>: never crash with another exception,
/// never allocate absurd amounts of memory, never recurse without bound.
/// </summary>
public sealed class DecoderRobustnessTests
{
    [Fact]
    public void MutatedEncodingsNeverCrashTheDecoder()
    {
        var random = new Random(4242);
        for (var seed = 0; seed < 3000; seed++)
        {
            var original = DifferentialCodecTests.Encode(e => e.WriteVariant(new RandomValues(seed).Variant()));
            for (var round = 0; round < 5; round++)
            {
                var mutated = Mutate(original, random);
                try
                {
                    var decoder = new BinaryDecoder(mutated);
                    var value = decoder.ReadVariant();
                    // Whatever decoded must encode again and decode to the same value (idempotence).
                    var again = DifferentialCodecTests.Encode(e => e.WriteVariant(value));
                    Assert.Equal(value, DifferentialCodecTests.Decode(again, d => d.ReadVariant()));
                }
                catch (OpcUaDecodingException)
                {
                    // Expected for most mutations.
                }
            }
        }
    }

    [Fact]
    public void HugeDeclaredLengthsAreRejectedBeforeAllocating()
    {
        // String claiming 2 GB, array claiming 2 billion elements: both must fail fast.
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }).ReadString());
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(new byte[] { 0x86, 0xFF, 0xFF, 0xFF, 0x7F }).ReadVariant());
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(new byte[] { 0x86, 0x00, 0x00, 0x01, 0x00 }).ReadVariant());
    }

    [Fact]
    public void DeepNestingIsBounded()
    {
        // A DiagnosticInfo whose inner DiagnosticInfo is nested 10 000 times.
        var bytes = Enumerable.Repeat((byte)0x40, 10_000).Append((byte)0).ToArray();
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(bytes).ReadDiagnosticInfo());

        // Variants nested through Variant arrays.
        var nested = new List<byte>();
        for (var i = 0; i < 1000; i++)
        {
            nested.AddRange([0x98, 0x01, 0x00, 0x00, 0x00]); // array of 1 Variant
        }
        nested.Add(0);
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(nested.ToArray()).ReadVariant());
    }

    [Fact]
    public void InvalidUtf8DoesNotCrashTheDecoder()
    {
        var bytes = new byte[] { 0x02, 0x00, 0x00, 0x00, 0xC3, 0x28 };
        // .NET replaces invalid sequences rather than throwing; either outcome is acceptable as long
        // as it is not a crash, and the result is a string.
        var decoder = new BinaryDecoder(bytes);
        Assert.NotNull(decoder.ReadString());
    }

    [Fact]
    public void TrailingBytesAreReported()
    {
        var decoder = new BinaryDecoder(new byte[] { 0x01, 0x05 });
        Assert.True(decoder.ReadBoolean());
        Assert.Throws<OpcUaDecodingException>(decoder.EnsureFullyConsumed);
    }

    [Fact]
    public void UnknownVariantTypeIsADecodingError()
    {
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(new byte[] { 0x1F }).ReadVariant());
        Assert.Throws<OpcUaDecodingException>(() => new BinaryDecoder(new byte[] { 0x18, 0x00 }).ReadVariant()); // scalar Variant in Variant
        Assert.Equal(Variant.Null, new BinaryDecoder(new byte[] { 0x00 }).ReadVariant());
    }

    private static byte[] Mutate(byte[] original, Random random)
    {
        var bytes = (byte[])original.Clone();
        switch (random.Next(4))
        {
            case 0 when bytes.Length > 0: // flip a byte
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                break;
            case 1 when bytes.Length > 1: // truncate
                return bytes[..random.Next(bytes.Length)];
            case 2: // append garbage
                var extra = new byte[random.Next(1, 8)];
                random.NextBytes(extra);
                return [.. bytes, .. extra];
            default: // set a length field to something large
                if (bytes.Length >= 5)
                {
                    var at = random.Next(bytes.Length - 4);
                    BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), random.Next());
                }
                break;
        }
        return bytes;
    }
}
