using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Tests.Codec;

/// <summary>
/// Seeded generator of random OPC UA values covering every built-in type, arrays, matrices,
/// nesting, nulls, extremes and multi-byte text. Seeded so a failure can be reproduced from the
/// seed printed in the assertion message.
/// </summary>
internal sealed class RandomValues
{
    private static readonly string[] TextSamples =
    [
        "", "a", "Pump01.Speed", "é", "عربي", "中文", "emoji 😀", "tab\tnew\nline", "\"quotes\"", new string('x', 300),
    ];

    private readonly Random _random;

    public RandomValues(int seed) => _random = new Random(seed);

    public static readonly BuiltInType[] ScalarTypes =
        Enum.GetValues<BuiltInType>().Where(t => t is not (BuiltInType.Null or BuiltInType.Variant)).ToArray();

    public Variant Variant(int depth = 0)
    {
        var roll = _random.Next(10);
        if (roll == 0)
        {
            return Core.Variant.Null;
        }
        var type = ScalarTypes[_random.Next(ScalarTypes.Length)];
        if (depth > 2 && type is BuiltInType.DataValue or BuiltInType.DiagnosticInfo)
        {
            type = BuiltInType.Int32;
        }
        if (roll < 6)
        {
            return Core.Variant.Scalar(type, Scalar(type, depth));
        }
        if (roll == 6 && depth < 2)
        {
            var items = new Variant[_random.Next(0, 4)];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = Variant(depth + 1);
            }
            return Core.Variant.FromArray(items);
        }
        if (roll == 7)
        {
            // Matrix: 2 or 3 dimensions of 1 to 3 elements each.
            var dims = Enumerable.Range(0, _random.Next(2, 4)).Select(_ => _random.Next(1, 4)).ToArray();
            var count = dims.Aggregate(1, (a, b) => a * b);
            return Core.Variant.Matrix(type, ArrayOf(type, count, depth), dims);
        }
        if (roll == 8 && _random.Next(4) == 0)
        {
            return Core.Variant.Array(type, null); // null array
        }
        return Core.Variant.Array(type, ArrayOf(type, _random.Next(0, 6), depth));
    }

    private Array ArrayOf(BuiltInType type, int count, int depth)
    {
        var array = Array.CreateInstance(Core.Variant.ClrType(type), count);
        for (var i = 0; i < count; i++)
        {
            var element = Scalar(type, depth + 1);
            // open62541 quirk (docs/native-layer.md, "Known upstream quirks"): its length check
            // assumes 4 bytes per ExtensionObject, but an empty one is 3, so an array of empty
            // ExtensionObjects at the very end of a buffer is rejected. Keep array elements non-empty.
            if (element is ExtensionObject { Encoding: ExtensionObjectEncoding.None, TypeId.IsNull: true })
            {
                element = Core.ExtensionObject.FromTypeOnly(new NodeId(1, 2));
            }
            array.SetValue(element, i);
        }
        return array;
    }

    public object? Scalar(BuiltInType type, int depth) => type switch
    {
        BuiltInType.Boolean => _random.Next(2) == 1,
        BuiltInType.SByte => (sbyte)_random.Next(sbyte.MinValue, sbyte.MaxValue + 1),
        BuiltInType.Byte => (byte)_random.Next(256),
        BuiltInType.Int16 => (short)_random.Next(short.MinValue, short.MaxValue + 1),
        BuiltInType.UInt16 => (ushort)_random.Next(ushort.MaxValue + 1),
        BuiltInType.Int32 => Pick(int.MinValue, int.MaxValue, 0, -1, _random.Next(int.MinValue, int.MaxValue)),
        BuiltInType.UInt32 => Pick(uint.MaxValue, 0u, (uint)_random.NextInt64(0, uint.MaxValue)),
        BuiltInType.Int64 => Pick(long.MinValue, long.MaxValue, 0L, _random.NextInt64()),
        BuiltInType.UInt64 => Pick(ulong.MaxValue, 0ul, (ulong)_random.NextInt64() * 2),
        BuiltInType.Float => Pick(float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, float.Epsilon, float.MaxValue, (float)_random.NextDouble() * 1e6f),
        BuiltInType.Double => Pick(double.NaN, double.PositiveInfinity, -0d, double.Epsilon, double.MinValue, _random.NextDouble() * 1e12),
        BuiltInType.String => String(),
        BuiltInType.DateTime => DateTime(),
        BuiltInType.Guid => new Guid(Bytes(16)!),
        BuiltInType.ByteString => _random.Next(5) == 0 ? null : Bytes(_random.Next(0, 40)),
        BuiltInType.XmlElement => new XmlElement(_random.Next(4) == 0 ? null : "<a b=\"1\">" + String() + "</a>"),
        BuiltInType.NodeId => NodeId(),
        BuiltInType.ExpandedNodeId => new ExpandedNodeId(NodeId(), _random.Next(2) == 0 ? null : "urn:plant:" + _random.Next(100), (uint)_random.Next(3)),
        BuiltInType.StatusCode => new StatusCode(Pick(0u, 0x800A0000u, 0x40920000u, (uint)_random.NextInt64(0, uint.MaxValue))),
        BuiltInType.QualifiedName => new QualifiedName((ushort)_random.Next(5), String()),
        BuiltInType.LocalizedText => new LocalizedText(_random.Next(3) == 0 ? null : Pick("en", "ar-TN", "", "fr"), String()),
        BuiltInType.ExtensionObject => ExtensionObject(),
        BuiltInType.DataValue => DataValue(depth + 1),
        BuiltInType.DiagnosticInfo => DiagnosticInfo(depth + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public NodeId NodeId() => _random.Next(5) switch
    {
        0 => new NodeId((uint)_random.Next(0, 300)),                                    // two-byte and above
        1 => new NodeId((uint)_random.Next(0, 70000), (ushort)_random.Next(0, 300)),    // four-byte boundaries
        2 => new NodeId(String() ?? "x", (ushort)_random.Next(0, 10)),
        3 => new NodeId(new Guid(Bytes(16)!), (ushort)_random.Next(0, 10)),
        _ => new NodeId(Bytes(_random.Next(0, 20)), (ushort)_random.Next(0, 10)),
    };

    public DataValue DataValue(int depth) => new(
        depth > 3 || _random.Next(4) == 0 ? Core.Variant.Null : Variant(depth),
        _random.Next(2) == 0 ? default : new StatusCode((uint)_random.NextInt64(1, uint.MaxValue)),
        _random.Next(2) == 0 ? null : DateTime(),
        _random.Next(2) == 0 ? null : DateTime(),
        (ushort)(_random.Next(3) == 0 ? _random.Next(1, 10000) : 0),
        (ushort)(_random.Next(3) == 0 ? _random.Next(1, 10000) : 0));

    public DiagnosticInfo DiagnosticInfo(int depth) => new(
        _random.Next(2) == 0 ? null : _random.Next(),
        _random.Next(2) == 0 ? null : _random.Next(),
        _random.Next(2) == 0 ? null : _random.Next(),
        _random.Next(2) == 0 ? null : _random.Next(),
        _random.Next(2) == 0 ? null : String() ?? "info",
        _random.Next(2) == 0 ? null : new StatusCode((uint)_random.NextInt64(0, uint.MaxValue)),
        depth < 3 && _random.Next(3) == 0 ? DiagnosticInfo(depth + 1) with { SymbolicId = 1 } : null);

    public ExtensionObject ExtensionObject() => _random.Next(4) switch
    {
        0 => Core.ExtensionObject.Empty,
        1 => Core.ExtensionObject.FromXml(new NodeId((uint)_random.Next(1, 1000), 2), "<x/>"),
        2 => Core.ExtensionObject.FromTypeOnly(new NodeId((uint)_random.Next(1, 1000), 3)),
        // Binary bodies use namespace 2 type ids, unknown to open62541, so it keeps them opaque.
        _ => Core.ExtensionObject.FromBinary(new NodeId((uint)_random.Next(1, 1000), 2), Bytes(_random.Next(0, 30))),
    };

    public string? String() => _random.Next(8) == 0 ? null : TextSamples[_random.Next(TextSamples.Length)];

    public DateTime DateTime() => _random.Next(10) switch
    {
        0 => System.DateTime.SpecifyKind(System.DateTime.MinValue, DateTimeKind.Utc),
        1 => System.DateTime.SpecifyKind(System.DateTime.MaxValue, DateTimeKind.Utc),
        _ => new DateTime(_random.NextInt64(new DateTime(1601, 1, 1).Ticks + 1, new DateTime(9999, 1, 1).Ticks), DateTimeKind.Utc),
    };

    public byte[]? Bytes(int length)
    {
        var b = new byte[length];
        _random.NextBytes(b);
        return b;
    }

    private T Pick<T>(params T[] options) => options[_random.Next(options.Length)];
}
