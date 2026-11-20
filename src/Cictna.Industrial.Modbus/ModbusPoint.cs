namespace Cictna.Industrial.Modbus;

/// <summary>How the value of a <see cref="ModbusPoint"/> is laid out in its registers or bits.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "The names are the data types they describe, as device manuals name them.")]
public enum ModbusDataType
{
    /// <summary>One bit: a coil, a discrete input, or one bit of a register (<see cref="ModbusPoint.BitIndex"/>).</summary>
    Boolean,
    /// <summary>Signed 16-bit integer, one register.</summary>
    Int16,
    /// <summary>Unsigned 16-bit integer, one register.</summary>
    UInt16,
    /// <summary>Signed 32-bit integer, two registers.</summary>
    Int32,
    /// <summary>Unsigned 32-bit integer, two registers.</summary>
    UInt32,
    /// <summary>Signed 64-bit integer, four registers.</summary>
    Int64,
    /// <summary>Unsigned 64-bit integer, four registers.</summary>
    UInt64,
    /// <summary>IEEE 754 single precision, two registers.</summary>
    Float32,
    /// <summary>IEEE 754 double precision, four registers.</summary>
    Float64,
    /// <summary>Text, two 8-bit characters per register (Latin-1), padded with zero bytes.</summary>
    String,
}

/// <summary>
/// Order of the registers of a value that spans more than one (32 and 64 bits). Modbus itself only
/// defines single registers, so devices differ here.
/// </summary>
public enum ModbusWordOrder
{
    /// <summary>The most significant register first (the "big-endian" layout, ABCD). The default.</summary>
    HighWordFirst,
    /// <summary>The least significant register first (word swap, CDAB).</summary>
    LowWordFirst,
}

/// <summary>Order of the two bytes inside each register.</summary>
public enum ModbusByteOrder
{
    /// <summary>High byte first, as the Modbus specification defines it. The default.</summary>
    HighByteFirst,
    /// <summary>Low byte first (byte swap, BADC; with <see cref="ModbusWordOrder.LowWordFirst"/> DCBA).</summary>
    LowByteFirst,
}

/// <summary>
/// Where a value lives on a Modbus device and how to read it: table, start address (0-based, as
/// on the wire), data type, and the word and byte order the device uses.
/// </summary>
/// <remarks>
/// Addresses are the protocol addresses (0 to 65535). Device manuals often number from 1 or use
/// the old 40001-style notation; holding register 40001 is address 0 here.
/// </remarks>
/// <example>
/// <code>
/// // A float in holding registers 100 and 101, low word first (CDAB).
/// var flow = new ModbusPoint(ModbusTable.HoldingRegisters, 100, ModbusDataType.Float32)
/// {
///     WordOrder = ModbusWordOrder.LowWordFirst,
/// };
/// DataValue value = await client.ReadValueAsync(unitId: 1, flow, cancellationToken);
/// </code>
/// </example>
/// <param name="Table">The data table.</param>
/// <param name="Address">The first coil, input or register, 0-based.</param>
/// <param name="Type">How to interpret the data.</param>
public sealed record ModbusPoint(ModbusTable Table, ushort Address, ModbusDataType Type)
{
    /// <summary>
    /// Number of consecutive elements to read as an array, or null for a single value. Not used
    /// for strings and register bits.
    /// </summary>
    public int? ArrayLength { get; init; }

    /// <summary>Length of a <see cref="ModbusDataType.String"/> in characters (bytes).</summary>
    public int StringLength { get; init; }

    /// <summary>
    /// For a <see cref="ModbusDataType.Boolean"/> in a register table: which bit of the register,
    /// 0 (least significant) to 15. Register bits can be read but not written.
    /// </summary>
    public int? BitIndex { get; init; }

    /// <summary>Order of the registers of 32- and 64-bit values.</summary>
    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;

    /// <summary>Order of the bytes inside each register.</summary>
    public ModbusByteOrder ByteOrder { get; init; } = ModbusByteOrder.HighByteFirst;

    /// <summary>True for coils and discrete inputs.</summary>
    public bool IsBitTable => Table is ModbusTable.Coils or ModbusTable.DiscreteInputs;

    /// <summary>Registers per element for the data type (0 for bits).</summary>
    internal int RegistersPerElement => Type switch
    {
        ModbusDataType.Boolean => IsBitTable ? 0 : 1,
        ModbusDataType.Int16 or ModbusDataType.UInt16 => 1,
        ModbusDataType.Int32 or ModbusDataType.UInt32 or ModbusDataType.Float32 => 2,
        ModbusDataType.Int64 or ModbusDataType.UInt64 or ModbusDataType.Float64 => 4,
        ModbusDataType.String => (StringLength + 1) / 2,
        _ => 0,
    };

    /// <summary>Number of elements: the array length, or 1.</summary>
    internal int ElementCount => ArrayLength ?? 1;

    /// <summary>Number of bits (bit tables) or registers (register tables) the point covers.</summary>
    public int Quantity => IsBitTable ? ElementCount : RegistersPerElement * (Type == ModbusDataType.String || BitIndex is not null ? 1 : ElementCount);

    /// <summary>Checks that the combination of table, type and options makes sense.</summary>
    /// <exception cref="ArgumentException">It does not; the message says why.</exception>
    public void Validate()
    {
        if (ArrayLength is < 1)
        {
            throw new ArgumentException($"ArrayLength must be at least 1, or null for a single value (got {ArrayLength}).");
        }
        if (IsBitTable && Type != ModbusDataType.Boolean)
        {
            throw new ArgumentException($"{Table} hold bits; only the Boolean type fits (got {Type}).");
        }
        if (IsBitTable && BitIndex is not null)
        {
            throw new ArgumentException($"BitIndex is only for Boolean values inside registers, not for {Table}.");
        }
        if (!IsBitTable && Type == ModbusDataType.Boolean)
        {
            if (BitIndex is not (>= 0 and <= 15))
            {
                throw new ArgumentException($"A Boolean in {Table} needs a BitIndex from 0 to 15.");
            }
            if (ArrayLength is not null)
            {
                throw new ArgumentException("A register bit is a single value; ArrayLength is not supported.");
            }
        }
        if (Type != ModbusDataType.Boolean && BitIndex is not null)
        {
            throw new ArgumentException($"BitIndex is only for Boolean values (got {Type}).");
        }
        if (Type == ModbusDataType.String)
        {
            if (StringLength < 1)
            {
                throw new ArgumentException("A String needs a StringLength of at least 1 character.");
            }
            if (ArrayLength is not null)
            {
                throw new ArgumentException("Arrays of strings are not supported; use one point per string.");
            }
        }
        if (Address + Quantity > 65536)
        {
            throw new ArgumentException($"The point runs past the last address: {Address} + {Quantity} > 65536.");
        }
    }
}
