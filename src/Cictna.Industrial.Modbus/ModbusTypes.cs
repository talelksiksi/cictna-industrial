using Cictna.Industrial.Core;

namespace Cictna.Industrial.Modbus;

/// <summary>The Modbus function codes this library sends and serves.</summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>01: read 1 to 2000 coils.</summary>
    ReadCoils = 1,
    /// <summary>02: read 1 to 2000 discrete inputs.</summary>
    ReadDiscreteInputs = 2,
    /// <summary>03: read 1 to 125 holding registers.</summary>
    ReadHoldingRegisters = 3,
    /// <summary>04: read 1 to 125 input registers.</summary>
    ReadInputRegisters = 4,
    /// <summary>05: write one coil.</summary>
    WriteSingleCoil = 5,
    /// <summary>06: write one holding register.</summary>
    WriteSingleRegister = 6,
    /// <summary>15 (0x0F): write 1 to 1968 coils.</summary>
    WriteMultipleCoils = 15,
    /// <summary>16 (0x10): write 1 to 123 holding registers.</summary>
    WriteMultipleRegisters = 16,
    /// <summary>23 (0x17): write 1 to 121 holding registers, then read 1 to 125, in one request.</summary>
    ReadWriteMultipleRegisters = 23,
}

/// <summary>
/// Exception codes a Modbus server returns instead of a normal response. <see cref="None"/> means
/// the server answered normally (or did not answer at all; see <see cref="ModbusResult.Status"/>).
/// </summary>
public enum ModbusExceptionCode : byte
{
    /// <summary>No exception response.</summary>
    None = 0,
    /// <summary>01: the server does not support the function code.</summary>
    IllegalFunction = 1,
    /// <summary>02: an address in the request does not exist on the server.</summary>
    IllegalDataAddress = 2,
    /// <summary>03: a value in the request is not allowed (quantity out of range, bad length, coil value).</summary>
    IllegalDataValue = 3,
    /// <summary>04: the server failed while carrying out the request.</summary>
    ServerDeviceFailure = 4,
    /// <summary>05: accepted, but it will take long; ask again later.</summary>
    Acknowledge = 5,
    /// <summary>06: the server is busy with a long request; try again later.</summary>
    ServerDeviceBusy = 6,
    /// <summary>08: the server found a parity error in its own memory.</summary>
    MemoryParityError = 8,
    /// <summary>10 (0x0A): a gateway could not route the request to the unit.</summary>
    GatewayPathUnavailable = 10,
    /// <summary>11 (0x0B): a gateway got no answer from the unit.</summary>
    GatewayTargetDeviceFailedToRespond = 11,
}

/// <summary>The four Modbus data tables.</summary>
public enum ModbusTable
{
    /// <summary>Read/write bits (function codes 1, 5 and 15).</summary>
    Coils,
    /// <summary>Read-only bits (function code 2).</summary>
    DiscreteInputs,
    /// <summary>Read/write 16-bit registers (function codes 3, 6, 16 and 23).</summary>
    HoldingRegisters,
    /// <summary>Read-only 16-bit registers (function code 4).</summary>
    InputRegisters,
}

/// <summary>
/// The outcome of a Modbus request without data (a write). Expected failures are values, not
/// exceptions: check <see cref="IsGood"/>.
/// </summary>
/// <param name="Status">
/// Good, or why the request failed: BadTimeout (no answer in time), BadNotConnected,
/// BadConnectionClosed (lost while waiting), BadDecodingError (the answer did not fit the request),
/// or a code derived from the server's exception (see <see cref="Exception"/>).
/// </param>
/// <param name="Exception">The server's exception code, when it sent one.</param>
public readonly record struct ModbusResult(StatusCode Status, ModbusExceptionCode Exception = ModbusExceptionCode.None)
{
    /// <summary>A successful result.</summary>
    public static ModbusResult Good => new(StatusCodes.Good);

    /// <summary>True when the request succeeded.</summary>
    public bool IsGood => Status.IsGood;

    /// <summary>The status and, when there is one, the server's exception code.</summary>
    public override string ToString() =>
        Exception == ModbusExceptionCode.None ? Status.Name : $"{Status.Name} (Modbus exception {(byte)Exception:D2} {Exception})";

    /// <summary>The same failure for a request that returns data.</summary>
    /// <typeparam name="T">The data type.</typeparam>
    public ModbusResult<T> As<T>() => new(default, Status, Exception);
}

/// <summary>
/// The outcome of a Modbus request that returns data. <see cref="Value"/> is set only when
/// <see cref="IsGood"/> is true.
/// </summary>
/// <typeparam name="T">The data type (for example <c>ushort[]</c> or <c>bool[]</c>).</typeparam>
/// <param name="Value">The data, or default when the request failed.</param>
/// <param name="Status">Good, or why the request failed (see <see cref="ModbusResult.Status"/>).</param>
/// <param name="Exception">The server's exception code, when it sent one.</param>
public readonly record struct ModbusResult<T>(T? Value, StatusCode Status, ModbusExceptionCode Exception = ModbusExceptionCode.None)
{
    /// <summary>True when the request succeeded and <see cref="Value"/> holds the data.</summary>
    public bool IsGood => Status.IsGood;

    /// <summary>The result without its data.</summary>
    public ModbusResult WithoutValue => new(Status, Exception);

    /// <summary>The status and, when there is one, the server's exception code.</summary>
    public override string ToString() => WithoutValue.ToString();
}

/// <summary>Maps Modbus exception codes onto the status codes of the common value model.</summary>
public static class ModbusStatus
{
    /// <summary>
    /// The status code for a server exception: IllegalFunction becomes BadNotSupported,
    /// IllegalDataAddress BadOutOfRange, IllegalDataValue BadInvalidArgument, Acknowledge and
    /// ServerDeviceBusy BadResourceUnavailable, the gateway codes BadNoCommunication, and the rest
    /// BadDeviceFailure.
    /// </summary>
    /// <param name="exception">The server's exception code.</param>
    public static StatusCode FromException(ModbusExceptionCode exception) => exception switch
    {
        ModbusExceptionCode.None => StatusCodes.Good,
        ModbusExceptionCode.IllegalFunction => StatusCodes.BadNotSupported,
        ModbusExceptionCode.IllegalDataAddress => StatusCodes.BadOutOfRange,
        ModbusExceptionCode.IllegalDataValue => StatusCodes.BadInvalidArgument,
        ModbusExceptionCode.Acknowledge or ModbusExceptionCode.ServerDeviceBusy => StatusCodes.BadResourceUnavailable,
        ModbusExceptionCode.GatewayPathUnavailable or ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => StatusCodes.BadNoCommunication,
        _ => StatusCodes.BadDeviceFailure,
    };
}

/// <summary>A failure to connect, with the status code that describes it.</summary>
public sealed class ModbusException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">What went wrong.</param>
    /// <param name="message">Explanation for logs and operators.</param>
    /// <param name="innerException">The underlying socket error, if any.</param>
    public ModbusException(StatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    public ModbusException() : this(StatusCodes.BadUnexpectedError, "Modbus error") { }

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    /// <param name="message">Explanation.</param>
    public ModbusException(string message) : this(StatusCodes.BadUnexpectedError, message) { }

    /// <summary>Creates the exception with BadUnexpectedError.</summary>
    /// <param name="message">Explanation.</param>
    /// <param name="innerException">The cause.</param>
    public ModbusException(string message, Exception innerException) : this(StatusCodes.BadUnexpectedError, message, innerException) { }

    /// <summary>What went wrong, as a status code.</summary>
    public StatusCode StatusCode { get; }
}
