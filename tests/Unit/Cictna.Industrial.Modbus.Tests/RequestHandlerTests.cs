using Cictna.Industrial.Modbus.Protocol;

namespace Cictna.Industrial.Modbus.Tests;

/// <summary>
/// Server side of each function code: the specification's examples answered from a data store,
/// and every exception the specification asks for.
/// </summary>
public class RequestHandlerTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    private static byte[] Handle(IModbusDataStore store, string request)
    {
        var response = new byte[ModbusPdu.MaxLength];
        var length = ModbusRequestHandler.Handle(store, Hex(request), response);
        return response[..length];
    }

    [Fact]
    public void ReadCoilsAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        store.SetCoils(19, ModbusPdu.UnpackBits(Hex("CD 6B 05"), 19));
        Assert.Equal(Hex("01 03 CD 6B 05"), Handle(store, "01 0013 0013"));
    }

    [Fact]
    public void ReadDiscreteInputsAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        store.SetDiscreteInputs(0xC4, ModbusPdu.UnpackBits(Hex("AC DB 35"), 22));
        Assert.Equal(Hex("02 03 AC DB 35"), Handle(store, "02 00C4 0016"));
    }

    [Fact]
    public void ReadHoldingRegistersAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(0x6B, [555, 0, 100]);
        Assert.Equal(Hex("03 06 022B 0000 0064"), Handle(store, "03 006B 0003"));
    }

    [Fact]
    public void ReadInputRegistersAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        store.SetInputRegisters(8, [10]);
        Assert.Equal(Hex("04 02 000A"), Handle(store, "04 0008 0001"));
    }

    [Fact]
    public void WriteSingleCoilEchoesAndWrites()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("05 00AC FF00"), Handle(store, "05 00AC FF00"));
        Assert.True(store.GetCoils(0xAC, 1)[0]);
        Assert.Equal(Hex("05 00AC 0000"), Handle(store, "05 00AC 0000"));
        Assert.False(store.GetCoils(0xAC, 1)[0]);
    }

    [Fact]
    public void WriteSingleCoilRefusesValuesOtherThanOnAndOff()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("85 03"), Handle(store, "05 00AC 1234"));
        Assert.Equal(Hex("85 03"), Handle(store, "05 00AC 00FF"));
    }

    [Fact]
    public void WriteSingleRegisterEchoesAndWrites()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("06 0001 0003"), Handle(store, "06 0001 0003"));
        Assert.Equal([3], store.GetHoldingRegisters(1, 1));
    }

    [Fact]
    public void WriteMultipleCoilsAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("0F 0013 000A"), Handle(store, "0F 0013 000A 02 CD 01"));
        Assert.Equal([true, false, true, true, false, false, true, true, true, false], store.GetCoils(0x13, 10));
        Assert.False(store.GetCoils(0x13 + 10, 1)[0]); // padding bits are not written
    }

    [Fact]
    public void WriteMultipleRegistersAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("10 0001 0002"), Handle(store, "10 0001 0002 04 000A 0102"));
        Assert.Equal([0x000A, 0x0102], store.GetHoldingRegisters(1, 2));
    }

    [Fact]
    public void ReadWriteMultipleRegistersAnswersTheSpecificationExample()
    {
        var store = new ModbusDataStore();
        store.SetHoldingRegisters(3, [0xFE, 0x0ACD, 1, 3, 0x0D, 0xFF]);
        Assert.Equal(Hex("17 0C 00FE 0ACD 0001 0003 000D 00FF"), Handle(store, "17 0003 0006 000E 0003 06 00FF 00FF 00FF"));
        Assert.Equal([0xFF, 0xFF, 0xFF], store.GetHoldingRegisters(0x0E, 3));
    }

    [Fact]
    public void ReadWriteMultipleRegistersWritesBeforeReading()
    {
        var store = new ModbusDataStore();
        Assert.Equal(Hex("17 04 0007 0008"), Handle(store, "17 0000 0002 0000 0002 04 0007 0008"));
    }

    [Theory]
    [InlineData("08 0000 0000", "88 01")]      // diagnostics: not supported
    [InlineData("2B 0E 01 00", "AB 01")]       // device identification: not supported
    [InlineData("41", "C1 01")]                // user-defined code
    [InlineData("81 0000 0001", "81 01")]      // a code with the exception bit set
    public void UnsupportedFunctionsAreIllegalFunction(string request, string response)
    {
        Assert.Equal(Hex(response), Handle(new ModbusDataStore(), request));
    }

    [Theory]
    [InlineData("01 0000 0000", "81 03")]       // quantity 0
    [InlineData("01 0000 07D1", "81 03")]       // 2001 coils
    [InlineData("02 0000 07D1", "82 03")]
    [InlineData("03 0000 007E", "83 03")]       // 126 registers
    [InlineData("04 0000 0000", "84 03")]
    [InlineData("03 0000", "83 03")]            // too short
    [InlineData("03 0000 0001 00", "83 03")]    // too long
    [InlineData("0F 0000 07B1 F7", "8F 03")]    // 1969 coils
    [InlineData("0F 0000 000A 01 FF", "8F 03")] // byte count does not fit the quantity
    [InlineData("0F 0000 000A 02 FF", "8F 03")] // data shorter than the byte count
    [InlineData("10 0000 007C F8", "90 03")]    // 124 registers
    [InlineData("10 0000 0001 02 00", "90 03")] // data short
    [InlineData("10 0000 0002 02 0000", "90 03")] // byte count does not fit the quantity
    [InlineData("17 0000 007E 0000 0001 02 0000", "97 03")] // read 126
    [InlineData("17 0000 0001 0000 007A F4", "97 03")]      // write 122
    [InlineData("17 0000 0001 0000 0001 02", "97 03")]      // data missing
    public void QuantitiesAndLengthsOutsideTheSpecificationAreIllegalDataValue(string request, string response)
    {
        Assert.Equal(Hex(response), Handle(new ModbusDataStore(), request));
    }

    [Theory]
    [InlineData("01 FFFF 0002", "81 02")]   // runs past 65535
    [InlineData("03 FF84 007D", "83 02")]
    [InlineData("0F FFFF 0002 01 03", "8F 02")]
    [InlineData("10 FFFF 0002 04 0001 0002", "90 02")]
    [InlineData("17 FFFF 0002 0000 0001 02 0000", "97 02")]
    [InlineData("17 0000 0001 FFFF 0002 04 0000 0000", "97 02")]
    public void RangesPastTheLastAddressAreIllegalDataAddress(string request, string response)
    {
        Assert.Equal(Hex(response), Handle(new ModbusDataStore(), request));
    }

    [Fact]
    public void TheEdgesOfTheAllowedRangesAreAccepted()
    {
        var store = new ModbusDataStore();
        Assert.Equal(2 + 250, Handle(store, "01 0000 07D0").Length);  // 2000 coils
        Assert.Equal(2 + 250, Handle(store, "03 0000 007D").Length);  // 125 registers
        Assert.Equal(2 + 2, Handle(store, "03 FFFF 0001").Length);    // last register
        Assert.Equal(5, Handle(store, "06 FFFF 0001").Length);
    }

    [Fact]
    public void AddressesTheStoreDoesNotHaveAreIllegalDataAddress()
    {
        var store = new ModbusDataStore(coils: 10, discreteInputs: 10, holdingRegisters: 10, inputRegisters: 10);
        Assert.Equal(Hex("83 02"), Handle(store, "03 0009 0002"));
        Assert.Equal(Hex("86 02"), Handle(store, "06 000A 0001"));
        Assert.Equal(Hex("85 02"), Handle(store, "05 000A FF00"));
        Assert.Equal(Hex("84 02"), Handle(store, "04 000A 0001"));
        Assert.Equal(Hex("97 02"), Handle(store, "17 0000 0001 0009 0002 04 0000 0000"));
        Assert.Equal(Hex("03 02 0000"), Handle(store, "03 0009 0001"));
    }

    [Fact]
    public void AStoreExceptionIsPassedOn()
    {
        Assert.Equal(Hex("83 06"), Handle(new BusyStore(), "03 0000 0001"));
        Assert.Equal(Hex("90 06"), Handle(new BusyStore(), "10 0000 0001 02 0000"));
    }

    [Fact]
    public void ClientWritesRaiseTheWrittenEvent()
    {
        var store = new ModbusDataStore();
        var events = new List<ModbusWrite>();
        store.Written += (_, e) => events.Add(e);
        Handle(store, "05 0001 FF00");
        Handle(store, "10 0005 0002 04 0001 0002");
        Handle(store, "17 0000 0001 0007 0001 02 0001");
        store.SetHoldingRegisters(0, [1]); // the application's own writes are not reported
        Assert.Equal(
            [new(ModbusTable.Coils, 1, 1), new(ModbusTable.HoldingRegisters, 5, 2), new(ModbusTable.HoldingRegisters, 7, 1)],
            events);
    }

    private sealed class BusyStore : IModbusDataStore
    {
        public ModbusExceptionCode ReadCoils(ushort address, Span<bool> values) => ModbusExceptionCode.ServerDeviceBusy;
        public ModbusExceptionCode ReadDiscreteInputs(ushort address, Span<bool> values) => ModbusExceptionCode.ServerDeviceBusy;
        public ModbusExceptionCode ReadHoldingRegisters(ushort address, Span<ushort> values) => ModbusExceptionCode.ServerDeviceBusy;
        public ModbusExceptionCode ReadInputRegisters(ushort address, Span<ushort> values) => ModbusExceptionCode.ServerDeviceBusy;
        public ModbusExceptionCode WriteCoils(ushort address, ReadOnlySpan<bool> values) => ModbusExceptionCode.ServerDeviceBusy;
        public ModbusExceptionCode WriteHoldingRegisters(ushort address, ReadOnlySpan<ushort> values) => ModbusExceptionCode.ServerDeviceBusy;
    }
}
