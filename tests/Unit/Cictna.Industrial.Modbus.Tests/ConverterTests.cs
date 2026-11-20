using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus.Tests;

/// <summary>Registers and bits to values and back, in all four word and byte orders.</summary>
public class ConverterTests
{
    private static ModbusPoint Holding(ModbusDataType type, ModbusWordOrder words = ModbusWordOrder.HighWordFirst, ModbusByteOrder bytes = ModbusByteOrder.HighByteFirst) =>
        new(ModbusTable.HoldingRegisters, 0, type) { WordOrder = words, ByteOrder = bytes };

    // 123.456f is 0x42F6E979: bytes A=42 B=F6 C=E9 D=79.
    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, ModbusByteOrder.HighByteFirst, 0x42F6, 0xE979)] // ABCD
    [InlineData(ModbusWordOrder.LowWordFirst, ModbusByteOrder.HighByteFirst, 0xE979, 0x42F6)]  // CDAB
    [InlineData(ModbusWordOrder.HighWordFirst, ModbusByteOrder.LowByteFirst, 0xF642, 0x79E9)]  // BADC
    [InlineData(ModbusWordOrder.LowWordFirst, ModbusByteOrder.LowByteFirst, 0x79E9, 0xF642)]   // DCBA
    public void Float32InAllFourOrders(ModbusWordOrder words, ModbusByteOrder bytes, int r0, int r1)
    {
        var point = Holding(ModbusDataType.Float32, words, bytes);
        ushort[] registers = [(ushort)r0, (ushort)r1];
        Assert.Equal(Variant.From(123.456f), ModbusDataConverter.FromRegisters(point, registers));
        var encoded = ModbusDataConverter.ToRegisters(point, Variant.From(123.456f));
        Assert.True(encoded.IsGood);
        Assert.Equal(registers, encoded.Value);
    }

    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, ModbusByteOrder.HighByteFirst, new[] { 0xC093, 0x4A45, 0x6D5C, 0xFAAD })]
    [InlineData(ModbusWordOrder.LowWordFirst, ModbusByteOrder.HighByteFirst, new[] { 0xFAAD, 0x6D5C, 0x4A45, 0xC093 })]
    [InlineData(ModbusWordOrder.HighWordFirst, ModbusByteOrder.LowByteFirst, new[] { 0x93C0, 0x454A, 0x5C6D, 0xADFA })]
    [InlineData(ModbusWordOrder.LowWordFirst, ModbusByteOrder.LowByteFirst, new[] { 0xADFA, 0x5C6D, 0x454A, 0x93C0 })]
    public void Float64InAllFourOrders(ModbusWordOrder words, ModbusByteOrder bytes, int[] expected)
    {
        var point = Holding(ModbusDataType.Float64, words, bytes);
        var registers = expected.Select(r => (ushort)r).ToArray();
        Assert.Equal(Variant.From(-1234.5678), ModbusDataConverter.FromRegisters(point, registers));
        Assert.Equal(registers, ModbusDataConverter.ToRegisters(point, Variant.From(-1234.5678)).Value);
    }

    [Fact]
    public void IntegersOfEverySize()
    {
        Assert.Equal(Variant.From((short)-2), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.Int16), [0xFFFE]));
        Assert.Equal(Variant.From((ushort)0xFFFE), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.UInt16), [0xFFFE]));
        Assert.Equal(Variant.From(-2), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.Int32), [0xFFFF, 0xFFFE]));
        Assert.Equal(Variant.From(-2), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.Int32, ModbusWordOrder.LowWordFirst), [0xFFFE, 0xFFFF]));
        Assert.Equal(Variant.From(0x0102_0304u), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.UInt32), [0x0102, 0x0304]));
        Assert.Equal(Variant.From(-2L), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.Int64), [0xFFFF, 0xFFFF, 0xFFFF, 0xFFFE]));
        Assert.Equal(Variant.From(0x0102_0304_0506_0708UL), ModbusDataConverter.FromRegisters(Holding(ModbusDataType.UInt64), [0x0102, 0x0304, 0x0506, 0x0708]));
        Assert.Equal(Variant.From(0x0102_0304_0506_0708UL),
            ModbusDataConverter.FromRegisters(Holding(ModbusDataType.UInt64, ModbusWordOrder.LowWordFirst, ModbusByteOrder.LowByteFirst), [0x0807, 0x0605, 0x0403, 0x0201]));
    }

    [Fact]
    public void ArraysUseTheOrderPerElement()
    {
        var point = Holding(ModbusDataType.Int32, ModbusWordOrder.LowWordFirst) with { ArrayLength = 3 };
        Assert.Equal(6, point.Quantity);
        ushort[] registers = [1, 0, 0xFFFE, 0xFFFF, 0, 1];
        var value = ModbusDataConverter.FromRegisters(point, registers);
        Assert.Equal(Variant.FromArray(new[] { 1, -2, 65536 }), value);
        Assert.Equal(registers, ModbusDataConverter.ToRegisters(point, value).Value);
    }

    [Fact]
    public void StringsArePaddedAndTrimmed()
    {
        var point = Holding(ModbusDataType.String) with { StringLength = 7 };
        Assert.Equal(4, point.Quantity);
        var encoded = ModbusDataConverter.ToRegisters(point, Variant.From("PUMP1"));
        Assert.Equal(new ushort[] { 0x5055, 0x4D50, 0x3100, 0x0000 }, encoded.Value); // "PU" "MP" "1\0" "\0\0"
        Assert.Equal(Variant.From("PUMP1"), ModbusDataConverter.FromRegisters(point, encoded.Value));

        var swapped = point with { ByteOrder = ModbusByteOrder.LowByteFirst };
        Assert.Equal(Variant.From("PUMP1"), ModbusDataConverter.FromRegisters(swapped, [0x5550, 0x504D, 0x0031, 0x0000]));
        // A full string uses every character, even the odd last byte of the last register.
        Assert.Equal(Variant.From("ABCDEFG"), ModbusDataConverter.FromRegisters(point, [0x4142, 0x4344, 0x4546, 0x4748]));
    }

    [Fact]
    public void BitsOfRegistersAndBitTables()
    {
        var bit = new ModbusPoint(ModbusTable.InputRegisters, 0, ModbusDataType.Boolean) { BitIndex = 15 };
        Assert.Equal(Variant.From(true), ModbusDataConverter.FromRegisters(bit, [0x8000]));
        Assert.Equal(Variant.From(false), ModbusDataConverter.FromRegisters(bit with { BitIndex = 0 }, [0x8000]));
        Assert.Equal(StatusCodes.BadNotWritable, ModbusDataConverter.ToRegisters(bit with { Table = ModbusTable.HoldingRegisters }, Variant.From(true)).Status);

        var coils = new ModbusPoint(ModbusTable.Coils, 0, ModbusDataType.Boolean) { ArrayLength = 3 };
        Assert.Equal(Variant.FromArray(new[] { true, false, true }), ModbusDataConverter.FromBits(coils, [true, false, true]));
        Assert.Equal([true, false, true], ModbusDataConverter.ToBits(coils, Variant.FromArray(new[] { true, false, true })).Value!);
        Assert.Equal([true], ModbusDataConverter.ToBits(coils with { ArrayLength = null }, Variant.From(true)).Value!);
        Assert.Equal(StatusCodes.BadTypeMismatch, ModbusDataConverter.ToBits(coils, Variant.From(true)).Status);
    }

    [Theory]
    [MemberData(nameof(Conversions))]
    public void NumbersConvertOnlyWhenTheyFitExactly(ModbusDataType type, Variant value, uint expectedStatus)
    {
        var point = Holding(type) with { StringLength = type == ModbusDataType.String ? 4 : 0 };
        Assert.Equal(new StatusCode(expectedStatus), ModbusDataConverter.ToRegisters(point, value).Status);
    }

    private const uint Good = 0, Mismatch = 0x80740000, OutOfRange = 0x803C0000;

    public static TheoryData<ModbusDataType, Variant, uint> Conversions => new()
    {
        { ModbusDataType.Int16, Variant.From(7.0), Good },
        { ModbusDataType.Int16, Variant.From(7.5), Mismatch },
        { ModbusDataType.Int16, Variant.From(70000), OutOfRange },
        { ModbusDataType.Int16, Variant.From(-32768), Good },
        { ModbusDataType.UInt16, Variant.From(-1), OutOfRange },
        { ModbusDataType.UInt16, Variant.From(65535L), Good },
        { ModbusDataType.UInt32, Variant.From(4294967296L), OutOfRange },
        { ModbusDataType.Int64, Variant.From(ulong.MaxValue), OutOfRange },
        { ModbusDataType.UInt64, Variant.From(ulong.MaxValue), Good },
        { ModbusDataType.UInt64, Variant.From(1e30), OutOfRange },
        { ModbusDataType.Int32, Variant.From(double.NaN), Mismatch },
        { ModbusDataType.Int32, Variant.From("12"), Mismatch },
        { ModbusDataType.Int32, Variant.From(true), Mismatch },
        { ModbusDataType.Int32, Variant.FromArray(new[] { 1 }), Mismatch },
        { ModbusDataType.Float32, Variant.From(1e39), OutOfRange },
        { ModbusDataType.Float32, Variant.From(42), Good },
        { ModbusDataType.Float32, Variant.From(double.PositiveInfinity), Good },
        { ModbusDataType.Float64, Variant.From(42UL), Good },
        { ModbusDataType.String, Variant.From(42), Mismatch },
    };

    [Fact]
    public void StringsThatDoNotFitAreRefused()
    {
        var point = Holding(ModbusDataType.String) with { StringLength = 4 };
        Assert.Equal(StatusCodes.BadOutOfRange, ModbusDataConverter.ToRegisters(point, Variant.From("TOO LONG")).Status);
        Assert.Equal(StatusCodes.BadOutOfRange, ModbusDataConverter.ToRegisters(point, Variant.From("中")).Status);
        Assert.True(ModbusDataConverter.ToRegisters(point, Variant.From("Ä")).IsGood); // Latin-1 fits
    }

    [Fact]
    public void ArraysMustHaveTheDeclaredLength()
    {
        var point = Holding(ModbusDataType.UInt16) with { ArrayLength = 2 };
        Assert.Equal(StatusCodes.BadTypeMismatch, ModbusDataConverter.ToRegisters(point, Variant.FromArray(new ushort[] { 1, 2, 3 })).Status);
        Assert.Equal(StatusCodes.BadTypeMismatch, ModbusDataConverter.ToRegisters(point, Variant.From((ushort)1)).Status);
        Assert.Equal(new ushort[] { 1, 2 }, ModbusDataConverter.ToRegisters(point, Variant.FromArray(new[] { 1, 2 })).Value);
    }

    [Theory]
    [MemberData(nameof(InvalidPoints))]
    public void InvalidPointsAreRefused(ModbusPoint point)
    {
        Assert.Throws<ArgumentException>(point.Validate);
    }

    public static TheoryData<ModbusPoint> InvalidPoints => new()
    {
        new ModbusPoint(ModbusTable.Coils, 0, ModbusDataType.Int16),
        new ModbusPoint(ModbusTable.Coils, 0, ModbusDataType.Boolean) { BitIndex = 1 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Boolean),
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Boolean) { BitIndex = 16 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Boolean) { BitIndex = 1, ArrayLength = 2 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Int16) { BitIndex = 1 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.String),
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.String) { StringLength = 2, ArrayLength = 2 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 0, ModbusDataType.Int16) { ArrayLength = 0 },
        new ModbusPoint(ModbusTable.HoldingRegisters, 65535, ModbusDataType.Int32),
    };

    [Fact]
    public void TheLastRegisterIsReachable()
    {
        new ModbusPoint(ModbusTable.HoldingRegisters, 65534, ModbusDataType.Int32).Validate();
        new ModbusPoint(ModbusTable.Coils, 65535, ModbusDataType.Boolean).Validate();
    }
}
