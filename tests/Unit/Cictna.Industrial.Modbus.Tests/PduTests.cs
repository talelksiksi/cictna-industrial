using Cictna.Industrial.Core;
using Cictna.Industrial.Modbus.Protocol;

namespace Cictna.Industrial.Modbus.Tests;

/// <summary>
/// Client side of each function code, checked byte for byte against the examples in the Modbus
/// Application Protocol Specification V1.1b3.
/// </summary>
public class PduTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Fact]
    public void ReadCoilsMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadCoils, 0x13, 0x13);
        Assert.Equal(Hex("01 0013 0013"), request);

        var response = Hex("01 03 CD 6B 05");
        Assert.True(ModbusPdu.Check(request, response).IsGood);
        var bits = ModbusPdu.UnpackBits(ModbusPdu.Data(response), 0x13);
        // Outputs 27..20 = CD = 1100 1101, so output 20 (address 19) is the lowest bit.
        Assert.Equal([true, false, true, true, false, false, true, true], bits[..8]);
        // The last byte 05 holds outputs 36..38; the unused high bits are padding.
        Assert.Equal([true, false, true], bits[16..]);
    }

    [Fact]
    public void ReadDiscreteInputsMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadDiscreteInputs, 0xC4, 0x16);
        Assert.Equal(Hex("02 00C4 0016"), request);
        var response = Hex("02 03 AC DB 35");
        Assert.True(ModbusPdu.Check(request, response).IsGood);
        var bits = ModbusPdu.UnpackBits(ModbusPdu.Data(response), 0x16);
        Assert.Equal([false, false, true, true, false, true, false, true], bits[..8]); // AC
        Assert.Equal([true, false, true, false, true, true], bits[16..]);              // 35, 6 bits
    }

    [Fact]
    public void ReadHoldingRegistersMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0x6B, 3);
        Assert.Equal(Hex("03 006B 0003"), request);
        var response = Hex("03 06 022B 0000 0064");
        Assert.True(ModbusPdu.Check(request, response).IsGood);
        Assert.Equal([555, 0, 100], ModbusPdu.ReadRegisters(ModbusPdu.Data(response), 3));
    }

    [Fact]
    public void ReadInputRegistersMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadInputRegisters, 8, 1);
        Assert.Equal(Hex("04 0008 0001"), request);
        var response = Hex("04 02 000A");
        Assert.True(ModbusPdu.Check(request, response).IsGood);
        Assert.Equal([10], ModbusPdu.ReadRegisters(ModbusPdu.Data(response), 1));
    }

    [Fact]
    public void WriteSingleCoilMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.WriteSingleCoil(0xAC, true);
        Assert.Equal(Hex("05 00AC FF00"), request);
        Assert.Equal(Hex("05 00AC 0000"), ModbusPdu.WriteSingleCoil(0xAC, false));
        Assert.True(ModbusPdu.Check(request, Hex("05 00AC FF00")).IsGood);
        Assert.Equal(StatusCodes.BadDecodingError, ModbusPdu.Check(request, Hex("05 00AC 0000")).Status);
    }

    [Fact]
    public void WriteSingleRegisterMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.WriteSingleRegister(1, 3);
        Assert.Equal(Hex("06 0001 0003"), request);
        Assert.True(ModbusPdu.Check(request, Hex("06 0001 0003")).IsGood);
        Assert.Equal(StatusCodes.BadDecodingError, ModbusPdu.Check(request, Hex("06 0002 0003")).Status);
    }

    [Fact]
    public void WriteMultipleCoilsMatchesTheSpecificationExample()
    {
        bool[] coils = [true, false, true, true, false, false, true, true, true, false];
        var request = ModbusPdu.WriteMultipleCoils(0x13, coils);
        Assert.Equal(Hex("0F 0013 000A 02 CD 01"), request);
        Assert.True(ModbusPdu.Check(request, Hex("0F 0013 000A")).IsGood);
        Assert.Equal(StatusCodes.BadDecodingError, ModbusPdu.Check(request, Hex("0F 0013 0009")).Status);
    }

    [Fact]
    public void WriteMultipleRegistersMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.WriteMultipleRegisters(1, [0x000A, 0x0102]);
        Assert.Equal(Hex("10 0001 0002 04 000A 0102"), request);
        Assert.True(ModbusPdu.Check(request, Hex("10 0001 0002")).IsGood);
    }

    [Fact]
    public void ReadWriteMultipleRegistersMatchesTheSpecificationExample()
    {
        var request = ModbusPdu.ReadWriteMultipleRegisters(3, 6, 0x0E, [0xFF, 0xFF, 0xFF]);
        Assert.Equal(Hex("17 0003 0006 000E 0003 06 00FF 00FF 00FF"), request);
        var response = Hex("17 0C 00FE 0ACD 0001 0003 000D 00FF");
        Assert.True(ModbusPdu.Check(request, response).IsGood);
        Assert.Equal([0xFE, 0x0ACD, 1, 3, 0x0D, 0xFF], ModbusPdu.ReadRegisters(ModbusPdu.Data(response), 6));
    }

    [Theory]
    [InlineData(0x01, ModbusExceptionCode.IllegalFunction, 0x803D0000u)]    // BadNotSupported
    [InlineData(0x02, ModbusExceptionCode.IllegalDataAddress, 0x803C0000u)] // BadOutOfRange
    [InlineData(0x03, ModbusExceptionCode.IllegalDataValue, 0x80AB0000u)]   // BadInvalidArgument
    [InlineData(0x04, ModbusExceptionCode.ServerDeviceFailure, 0x808B0000u)] // BadDeviceFailure
    [InlineData(0x06, ModbusExceptionCode.ServerDeviceBusy, 0x80040000u)]   // BadResourceUnavailable
    [InlineData(0x0B, ModbusExceptionCode.GatewayTargetDeviceFailedToRespond, 0x80310000u)] // BadNoCommunication
    public void ExceptionResponsesCarryTheCodeAndAStatus(byte code, ModbusExceptionCode expected, uint status)
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0, 1);
        var result = ModbusPdu.Check(request, [0x83, code]);
        Assert.Equal(expected, result.Exception);
        Assert.Equal(new StatusCode(status), result.Status);
    }

    [Theory]
    [InlineData("")]                    // empty
    [InlineData("84 02")]               // exception for another function
    [InlineData("83")]                  // exception without code
    [InlineData("83 00")]               // exception code 0
    [InlineData("83 02 00")]            // exception too long
    [InlineData("04 02 0000")]          // other function
    [InlineData("03 02 0000")]          // byte count does not match the quantity asked
    [InlineData("03 04 0000")]          // byte count right, data short
    [InlineData("03 04 0000 0000 00")]  // data too long
    public void MalformedResponsesAreDecodingErrors(string hex)
    {
        var request = ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0, 2);
        Assert.Equal(StatusCodes.BadDecodingError, ModbusPdu.Check(request, Hex(hex)).Status);
    }

    [Fact]
    public void TheMbapHeaderRoundTrips()
    {
        var frame = MbapHeader.Frame(0x1234, 7, Hex("03 006B 0003"));
        Assert.Equal(Hex("1234 0000 0006 07 03 006B 0003"), frame);
        var header = MbapHeader.Read(frame);
        Assert.Equal(new MbapHeader(0x1234, 0, 6, 7), header);
        Assert.True(header.HasValidLength);
        Assert.Equal(5, header.PduLength);
        Assert.False(new MbapHeader(1, 0, 1, 1).HasValidLength);   // no room for a function code
        Assert.False(new MbapHeader(1, 0, 255, 1).HasValidLength); // PDU longer than 253
        Assert.True(new MbapHeader(1, 0, 254, 1).HasValidLength);
    }
}
