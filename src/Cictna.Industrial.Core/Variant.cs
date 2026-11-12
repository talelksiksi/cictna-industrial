using System.Collections;
using System.Globalization;

namespace Cictna.Industrial.Core;

/// <summary>
/// A value of any OPC UA built-in type: a scalar, a one-dimensional array or a multi-dimensional
/// array (matrix). Immutable. <c>default(Variant)</c> is the empty Variant (<see cref="Null"/>).
/// </summary>
/// <remarks>
/// <para>
/// CLR representation per <see cref="BuiltInType"/>: Boolean bool, SByte sbyte, Byte byte, Int16 short,
/// UInt16 ushort, Int32 int, UInt32 uint, Int64 long, UInt64 ulong, Float float, Double double,
/// String string (nullable), DateTime <see cref="System.DateTime"/> (UTC), Guid <see cref="System.Guid"/>,
/// ByteString byte[] (nullable), XmlElement, NodeId, ExpandedNodeId, StatusCode, QualifiedName,
/// LocalizedText, ExtensionObject, DataValue, DiagnosticInfo. Arrays are CLR arrays of those types;
/// a Variant array (type Variant) is <c>Variant[]</c>.
/// </para>
/// <para>
/// Note the one ambiguity: a <c>byte[]</c> is either a ByteString scalar or a Byte array. The
/// <see cref="Type"/> decides; use <see cref="From(byte[])"/> for a ByteString and
/// <see cref="FromArray(byte[])"/> for an array of Byte.
/// </para>
/// <para>
/// Matrices are stored flat in row-major order (last index changes fastest) with
/// <see cref="ArrayDimensions"/> giving the length of each dimension, exactly as on the wire.
/// Every dimension must be at least 1; use an empty one-dimensional array for "no elements".
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Variant speed = 1450.5;                         // Double scalar
/// var setpoints = Variant.FromArray(new[] { 1, 2, 3 });
/// var grid = Variant.Matrix(BuiltInType.Int16, new short[] { 1, 2, 3, 4, 5, 6 }, 2, 3);
/// if (speed.TryGetValue(out double rpm)) { ... }
/// </code>
/// </example>
public readonly struct Variant : IEquatable<Variant>
{
    private readonly int[]? _dimensions;

    private Variant(BuiltInType type, object? value, bool isArray, int[]? dimensions)
    {
        Type = type;
        Value = value;
        IsArray = isArray;
        _dimensions = dimensions;
    }

    /// <summary>The empty Variant (no type, no value).</summary>
    public static Variant Null => default;

    /// <summary>The built-in type of the value (or of each array element).</summary>
    public BuiltInType Type { get; }

    /// <summary>The value: a scalar of the CLR type for <see cref="Type"/>, or a CLR array of it.</summary>
    public object? Value { get; }

    /// <summary>True for arrays and matrices.</summary>
    public bool IsArray { get; }

    /// <summary>True for the empty Variant.</summary>
    public bool IsNull => Type == BuiltInType.Null;

    /// <summary>True for multi-dimensional arrays (two or more dimensions).</summary>
    public bool IsMatrix => _dimensions is not null;

    /// <summary>Length of each dimension for matrices; null for scalars and one-dimensional arrays.</summary>
    public IReadOnlyList<int>? ArrayDimensions => _dimensions;

    /// <summary>Number of elements for arrays (0 for a null array); 0 for scalars.</summary>
    public int ArrayLength => IsArray && Value is Array a ? a.Length : 0;

    // ---- scalar factories -----------------------------------------------------------------

    /// <summary>Boolean scalar.</summary><param name="value">The value.</param>
    public static Variant From(bool value) => new(BuiltInType.Boolean, value, false, null);
    /// <summary>SByte scalar.</summary><param name="value">The value.</param>
    public static Variant From(sbyte value) => new(BuiltInType.SByte, value, false, null);
    /// <summary>Byte scalar.</summary><param name="value">The value.</param>
    public static Variant From(byte value) => new(BuiltInType.Byte, value, false, null);
    /// <summary>Int16 scalar.</summary><param name="value">The value.</param>
    public static Variant From(short value) => new(BuiltInType.Int16, value, false, null);
    /// <summary>UInt16 scalar.</summary><param name="value">The value.</param>
    public static Variant From(ushort value) => new(BuiltInType.UInt16, value, false, null);
    /// <summary>Int32 scalar.</summary><param name="value">The value.</param>
    public static Variant From(int value) => new(BuiltInType.Int32, value, false, null);
    /// <summary>UInt32 scalar.</summary><param name="value">The value.</param>
    public static Variant From(uint value) => new(BuiltInType.UInt32, value, false, null);
    /// <summary>Int64 scalar.</summary><param name="value">The value.</param>
    public static Variant From(long value) => new(BuiltInType.Int64, value, false, null);
    /// <summary>UInt64 scalar.</summary><param name="value">The value.</param>
    public static Variant From(ulong value) => new(BuiltInType.UInt64, value, false, null);
    /// <summary>Float scalar.</summary><param name="value">The value.</param>
    public static Variant From(float value) => new(BuiltInType.Float, value, false, null);
    /// <summary>Double scalar.</summary><param name="value">The value.</param>
    public static Variant From(double value) => new(BuiltInType.Double, value, false, null);
    /// <summary>String scalar (null allowed).</summary><param name="value">The value.</param>
    public static Variant From(string? value) => new(BuiltInType.String, value, false, null);
    /// <summary>DateTime scalar. Local times are converted to UTC; unspecified kinds are taken as UTC.</summary><param name="value">The value.</param>
    public static Variant From(DateTime value) => new(BuiltInType.DateTime, ToUtc(value), false, null);
    /// <summary>Guid scalar.</summary><param name="value">The value.</param>
    public static Variant From(Guid value) => new(BuiltInType.Guid, value, false, null);
    /// <summary>ByteString scalar (null allowed). The array is not copied; do not modify it afterwards.</summary><param name="value">The value.</param>
    public static Variant From(byte[]? value) => new(BuiltInType.ByteString, value, false, null);
    /// <summary>XmlElement scalar.</summary><param name="value">The value.</param>
    public static Variant From(XmlElement value) => new(BuiltInType.XmlElement, value, false, null);
    /// <summary>NodeId scalar.</summary><param name="value">The value.</param>
    public static Variant From(NodeId value) => new(BuiltInType.NodeId, value, false, null);
    /// <summary>ExpandedNodeId scalar.</summary><param name="value">The value.</param>
    public static Variant From(ExpandedNodeId value) => new(BuiltInType.ExpandedNodeId, value, false, null);
    /// <summary>StatusCode scalar.</summary><param name="value">The value.</param>
    public static Variant From(StatusCode value) => new(BuiltInType.StatusCode, value, false, null);
    /// <summary>QualifiedName scalar.</summary><param name="value">The value.</param>
    public static Variant From(QualifiedName value) => new(BuiltInType.QualifiedName, value, false, null);
    /// <summary>LocalizedText scalar.</summary><param name="value">The value.</param>
    public static Variant From(LocalizedText value) => new(BuiltInType.LocalizedText, value, false, null);
    /// <summary>ExtensionObject scalar.</summary><param name="value">The value.</param>
    public static Variant From(ExtensionObject value) => new(BuiltInType.ExtensionObject, value ?? ExtensionObject.Empty, false, null);
    /// <summary>DataValue scalar.</summary><param name="value">The value.</param>
    public static Variant From(DataValue value) { ArgumentNullException.ThrowIfNull(value); return new(BuiltInType.DataValue, value, false, null); }
    /// <summary>DiagnosticInfo scalar.</summary><param name="value">The value.</param>
    public static Variant From(DiagnosticInfo value) { ArgumentNullException.ThrowIfNull(value); return new(BuiltInType.DiagnosticInfo, value, false, null); }

    // ---- array factories (arrays are not copied; do not modify them afterwards) ------------

    /// <summary>Boolean array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(bool[] values) => Array(BuiltInType.Boolean, values);
    /// <summary>SByte array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(sbyte[] values) => Array(BuiltInType.SByte, values);
    /// <summary>Byte array (not a ByteString; see the remarks on <see cref="Variant"/>).</summary><param name="values">The elements.</param>
    public static Variant FromArray(byte[] values) => Array(BuiltInType.Byte, values);
    /// <summary>Int16 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(short[] values) => Array(BuiltInType.Int16, values);
    /// <summary>UInt16 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(ushort[] values) => Array(BuiltInType.UInt16, values);
    /// <summary>Int32 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(int[] values) => Array(BuiltInType.Int32, values);
    /// <summary>UInt32 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(uint[] values) => Array(BuiltInType.UInt32, values);
    /// <summary>Int64 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(long[] values) => Array(BuiltInType.Int64, values);
    /// <summary>UInt64 array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(ulong[] values) => Array(BuiltInType.UInt64, values);
    /// <summary>Float array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(float[] values) => Array(BuiltInType.Float, values);
    /// <summary>Double array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(double[] values) => Array(BuiltInType.Double, values);
    /// <summary>String array (null elements allowed).</summary><param name="values">The elements.</param>
    public static Variant FromArray(string?[] values) => Array(BuiltInType.String, values);
    /// <summary>DateTime array. Elements must already be UTC.</summary><param name="values">The elements.</param>
    public static Variant FromArray(DateTime[] values) => Array(BuiltInType.DateTime, values);
    /// <summary>Guid array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(Guid[] values) => Array(BuiltInType.Guid, values);
    /// <summary>ByteString array (null elements allowed).</summary><param name="values">The elements.</param>
    public static Variant FromArray(byte[]?[] values) => Array(BuiltInType.ByteString, values);
    /// <summary>NodeId array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(NodeId[] values) => Array(BuiltInType.NodeId, values);
    /// <summary>StatusCode array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(StatusCode[] values) => Array(BuiltInType.StatusCode, values);
    /// <summary>QualifiedName array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(QualifiedName[] values) => Array(BuiltInType.QualifiedName, values);
    /// <summary>LocalizedText array.</summary><param name="values">The elements.</param>
    public static Variant FromArray(LocalizedText[] values) => Array(BuiltInType.LocalizedText, values);
    /// <summary>Variant array (elements of mixed types).</summary><param name="values">The elements.</param>
    public static Variant FromArray(Variant[] values) => Array(BuiltInType.Variant, values);

    /// <summary>Creates a scalar of any built-in type, checking that the value has the right CLR type.</summary>
    /// <param name="type">The built-in type.</param>
    /// <param name="value">The value (see the remarks on <see cref="Variant"/> for CLR types).</param>
    /// <exception cref="ArgumentException">The value does not match the type.</exception>
    public static Variant Scalar(BuiltInType type, object? value)
    {
        if (type == BuiltInType.Null)
        {
            return value is null ? Null : throw new ArgumentException("A Null Variant cannot carry a value.", nameof(value));
        }
        if (type == BuiltInType.Variant)
        {
            throw new ArgumentException("A Variant cannot contain a scalar Variant; use an array.", nameof(type));
        }
        if (value is null)
        {
            if (type is BuiltInType.String or BuiltInType.ByteString)
            {
                return new Variant(type, null, false, null);
            }
            throw new ArgumentException($"A {type} scalar cannot be null.", nameof(value));
        }
        if (value.GetType() != ClrType(type))
        {
            throw new ArgumentException($"A {type} scalar must be {ClrType(type).Name}, not {value.GetType().Name}.", nameof(value));
        }
        if (value is DateTime dt)
        {
            value = ToUtc(dt);
        }
        return new Variant(type, value, false, null);
    }

    /// <summary>
    /// Creates a one-dimensional array of any built-in type, or a matrix when
    /// <paramref name="dimensions"/> has two or more entries.
    /// </summary>
    /// <param name="type">The element type.</param>
    /// <param name="values">A CLR array of the element type (flat, row-major for matrices). Null means a null array.</param>
    /// <param name="dimensions">Matrix dimensions, or null for a one-dimensional array.</param>
    /// <exception cref="ArgumentException">Element type or dimensions do not match.</exception>
    public static Variant Array(BuiltInType type, Array? values, params int[]? dimensions)
    {
        if (type == BuiltInType.Null)
        {
            throw new ArgumentException("An array needs an element type.", nameof(type));
        }
        if (values is not null)
        {
            var expected = ClrType(type).MakeArrayType();
            if (values.GetType() != expected)
            {
                throw new ArgumentException($"A {type} array must be {expected.Name}, not {values.GetType().Name}.", nameof(values));
            }
            if (values.Rank != 1)
            {
                throw new ArgumentException("Pass matrices flat (row-major) with their dimensions.", nameof(values));
            }
        }
        int[]? dims = null;
        if (dimensions is { Length: > 1 })
        {
            long product = 1;
            foreach (var d in dimensions)
            {
                // OPC UA stacks (open62541 among them) reject zero-length dimensions on the wire.
                // An empty matrix is sent as an empty one-dimensional array instead.
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(d, nameof(dimensions));
                product *= d;
            }
            if (product != (values?.Length ?? 0))
            {
                throw new ArgumentException($"Dimensions [{string.Join(",", dimensions)}] need {product} elements, the array has {values?.Length ?? 0}.", nameof(dimensions));
            }
            dims = (int[])dimensions.Clone();
        }
        return new Variant(type, values, true, dims);
    }

    /// <summary>Creates a matrix (shorthand for <see cref="Array"/> with dimensions).</summary>
    /// <param name="type">The element type.</param>
    /// <param name="values">Flat elements in row-major order.</param>
    /// <param name="dimensions">Length of each dimension (two or more).</param>
    public static Variant Matrix(BuiltInType type, Array values, params int[] dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        if (dimensions.Length < 2)
        {
            throw new ArgumentException("A matrix has at least two dimensions.", nameof(dimensions));
        }
        return Array(type, values, dimensions);
    }

    /// <summary>The CLR type used for scalars of a built-in type.</summary>
    /// <param name="type">The built-in type.</param>
    public static Type ClrType(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => typeof(bool),
        BuiltInType.SByte => typeof(sbyte),
        BuiltInType.Byte => typeof(byte),
        BuiltInType.Int16 => typeof(short),
        BuiltInType.UInt16 => typeof(ushort),
        BuiltInType.Int32 => typeof(int),
        BuiltInType.UInt32 => typeof(uint),
        BuiltInType.Int64 => typeof(long),
        BuiltInType.UInt64 => typeof(ulong),
        BuiltInType.Float => typeof(float),
        BuiltInType.Double => typeof(double),
        BuiltInType.String => typeof(string),
        BuiltInType.DateTime => typeof(DateTime),
        BuiltInType.Guid => typeof(Guid),
        BuiltInType.ByteString => typeof(byte[]),
        BuiltInType.XmlElement => typeof(XmlElement),
        BuiltInType.NodeId => typeof(NodeId),
        BuiltInType.ExpandedNodeId => typeof(ExpandedNodeId),
        BuiltInType.StatusCode => typeof(StatusCode),
        BuiltInType.QualifiedName => typeof(QualifiedName),
        BuiltInType.LocalizedText => typeof(LocalizedText),
        BuiltInType.ExtensionObject => typeof(ExtensionObject),
        BuiltInType.DataValue => typeof(DataValue),
        BuiltInType.Variant => typeof(Variant),
        BuiltInType.DiagnosticInfo => typeof(DiagnosticInfo),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown built-in type."),
    };

    // ---- implicit conversions for the common scalar types ---------------------------------

    /// <summary>Boolean scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(bool value) => From(value);
    /// <summary>Int16 scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(short value) => From(value);
    /// <summary>UInt16 scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(ushort value) => From(value);
    /// <summary>Int32 scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(int value) => From(value);
    /// <summary>UInt32 scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(uint value) => From(value);
    /// <summary>Int64 scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(long value) => From(value);
    /// <summary>Float scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(float value) => From(value);
    /// <summary>Double scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(double value) => From(value);
    /// <summary>String scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(string? value) => From(value);
    /// <summary>DateTime scalar.</summary><param name="value">The value.</param>
    public static implicit operator Variant(DateTime value) => From(value);

    // ---- access -----------------------------------------------------------------------------

    /// <summary>Gets the value as <typeparamref name="T"/> when it has exactly that CLR type.</summary>
    /// <typeparam name="T">The expected CLR type (for arrays, the array type, e.g. <c>int[]</c>).</typeparam>
    /// <param name="value">The value, or default.</param>
    /// <returns>True when the value is a <typeparamref name="T"/>.</returns>
    public bool TryGetValue<T>(out T value)
    {
        if (Value is T t)
        {
            value = t;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Gets the value as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The expected CLR type.</typeparam>
    /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
    public T GetValue<T>() =>
        TryGetValue<T>(out var v) ? v : throw new InvalidCastException($"Variant holds {Describe()}, not {typeof(T).Name}.");

    private string Describe() => IsNull ? "no value" : IsArray ? $"{Type}[]" : Type.ToString();

    // ---- equality -----------------------------------------------------------------------------

    /// <summary>Deep value equality: same type, shape and elements.</summary>
    /// <param name="other">The other Variant.</param>
    public bool Equals(Variant other)
    {
        if (Type != other.Type || IsArray != other.IsArray)
        {
            return false;
        }
        if (!DimensionsEqual(_dimensions, other._dimensions))
        {
            return false;
        }
        if (!IsArray)
        {
            return ElementEquals(Type, Value, other.Value);
        }
        if (Value is null || other.Value is null)
        {
            return Value is null && other.Value is null;
        }
        var a = (IList)Value;
        var b = (IList)other.Value;
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!ElementEquals(Type, a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool DimensionsEqual(int[]? a, int[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static bool ElementEquals(BuiltInType type, object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return type == BuiltInType.ByteString
            ? ((byte[])a).AsSpan().SequenceEqual((byte[])b)
            : a.Equals(b);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Variant other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        IsArray ? HashCode.Combine(Type, ArrayLength) : HashCode.Combine(Type, Type == BuiltInType.ByteString ? 0 : Value?.GetHashCode() ?? 0);

    /// <summary>Value equality.</summary>
    public static bool operator ==(Variant left, Variant right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Variant left, Variant right) => !left.Equals(right);

    /// <summary>Readable form for logs: the scalar value, or the element type and length for arrays.</summary>
    public override string ToString()
    {
        if (IsNull)
        {
            return "(null)";
        }
        if (IsArray)
        {
            return IsMatrix
                ? $"{Type}[{string.Join(",", _dimensions!)}]"
                : string.Create(CultureInfo.InvariantCulture, $"{Type}[{ArrayLength}]");
        }
        return Value switch
        {
            null => "(null)",
            byte[] bytes => $"ByteString({bytes.Length} bytes)",
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty,
        };
    }

    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
