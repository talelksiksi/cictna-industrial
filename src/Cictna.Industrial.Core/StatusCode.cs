using System.Globalization;

namespace Cictna.Industrial.Core;

/// <summary>Severity part of a <see cref="StatusCode"/>.</summary>
public enum StatusSeverity
{
    /// <summary>The value or operation is fine.</summary>
    Good,
    /// <summary>The value may be usable but is not reliable (for example a sensor out of range).</summary>
    Uncertain,
    /// <summary>The value must not be used, or the operation failed.</summary>
    Bad,
}

/// <summary>
/// An OPC UA status code: the quality of a value or the result of an operation.
/// </summary>
/// <remarks>
/// <para>
/// Layout (Part 4, 7.39): the top two bits are the severity (00 Good, 01 Uncertain, 10 Bad),
/// bits 16 to 29 identify the code, and the low 16 bits carry flags (info type, limit bits,
/// overflow, historian bits). <see cref="Name"/> looks up the code without the flags, so
/// <c>BadTimeout</c> is found even when a server sets flag bits.
/// </para>
/// <para>
/// Every value the stack returns carries a status code, including values from Modbus and the
/// simulator. Code that consumes values should check <see cref="IsGood"/> (or at least
/// <see cref="IsBad"/>) before using them.
/// </para>
/// </remarks>
public readonly struct StatusCode : IEquatable<StatusCode>
{
    private const uint SeverityMask = 0xC000_0000u;

    /// <summary>Creates a status code from its 32-bit value.</summary>
    /// <param name="code">The raw value as defined by OPC UA.</param>
    public StatusCode(uint code) => Code = code;

    /// <summary>The raw 32-bit value.</summary>
    public uint Code { get; }

    /// <summary>Severity from the top two bits. The reserved pattern 11 is treated as Bad.</summary>
    public StatusSeverity Severity => (Code & SeverityMask) switch
    {
        0 => StatusSeverity.Good,
        0x4000_0000u => StatusSeverity.Uncertain,
        _ => StatusSeverity.Bad,
    };

    /// <summary>True when the severity is Good.</summary>
    public bool IsGood => (Code & SeverityMask) == 0;

    /// <summary>True when the severity is Uncertain.</summary>
    public bool IsUncertain => (Code & SeverityMask) == 0x4000_0000u;

    /// <summary>True when the severity is Bad.</summary>
    public bool IsBad => (Code & 0x8000_0000u) != 0;

    /// <summary>The code without the flag bits (low 16 bits cleared).</summary>
    public StatusCode WithoutFlags => new(Code & 0xFFFF_0000u);

    /// <summary>Symbolic name from the OPC UA specification, for example "BadTimeout", or the hex value when unknown.</summary>
    public string Name => StatusCodes.TryGetInfo(this, out var name, out _)
        ? name
        : "0x" + Code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Description from the OPC UA specification, or an empty string when unknown.</summary>
    public string Description => StatusCodes.TryGetInfo(this, out _, out var description) ? description : string.Empty;

    /// <inheritdoc />
    public bool Equals(StatusCode other) => Code == other.Code;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StatusCode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Code.GetHashCode();

    /// <summary>Name and hex value, for example "BadTimeout (0x800A0000)".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} (0x{Code:X8})");

    /// <summary>Compares the raw values.</summary>
    public static bool operator ==(StatusCode left, StatusCode right) => left.Equals(right);

    /// <summary>Compares the raw values.</summary>
    public static bool operator !=(StatusCode left, StatusCode right) => !left.Equals(right);
}

/// <summary>Lookup helpers for the generated status code table.</summary>
public static partial class StatusCodes
{
    /// <summary>Looks up the specification name and description of a code (flag bits ignored).</summary>
    /// <param name="status">The status code.</param>
    /// <param name="name">The symbolic name, when known.</param>
    /// <param name="description">The description, when known.</param>
    /// <returns>True when the code is defined by the specification.</returns>
    public static bool TryGetInfo(StatusCode status, out string name, out string description)
    {
        if (s_known.TryGetValue(status.Code & 0xFFFF_0000u, out var info))
        {
            (name, description) = info;
            return true;
        }
        name = string.Empty;
        description = string.Empty;
        return false;
    }
}
