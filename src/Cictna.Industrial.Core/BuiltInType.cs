namespace Cictna.Industrial.Core;

/// <summary>
/// The 25 built-in data types of OPC UA (Part 6, 5.1.2). The numeric values are the wire
/// identifiers used in the Variant encoding, so they must never change.
/// </summary>
/// <remarks>
/// The whole stack uses this type system as its common value model: OPC UA values map onto it
/// directly, Modbus registers are converted into it (for example two registers into a Float),
/// and the simulator defines tags with it.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Member names are the type names defined by the OPC UA specification.")]
public enum BuiltInType : byte
{
    /// <summary>No value (an empty Variant).</summary>
    Null = 0,
    /// <summary>bool.</summary>
    Boolean = 1,
    /// <summary>sbyte.</summary>
    SByte = 2,
    /// <summary>byte.</summary>
    Byte = 3,
    /// <summary>short.</summary>
    Int16 = 4,
    /// <summary>ushort.</summary>
    UInt16 = 5,
    /// <summary>int.</summary>
    Int32 = 6,
    /// <summary>uint.</summary>
    UInt32 = 7,
    /// <summary>long.</summary>
    Int64 = 8,
    /// <summary>ulong.</summary>
    UInt64 = 9,
    /// <summary>float (IEEE 754 single precision).</summary>
    Float = 10,
    /// <summary>double (IEEE 754 double precision).</summary>
    Double = 11,
    /// <summary>string (UTF-8 on the wire, may be null).</summary>
    String = 12,
    /// <summary><see cref="System.DateTime"/>, always UTC.</summary>
    DateTime = 13,
    /// <summary><see cref="System.Guid"/>.</summary>
    Guid = 14,
    /// <summary>byte[] (may be null).</summary>
    ByteString = 15,
    /// <summary><see cref="Core.XmlElement"/>.</summary>
    XmlElement = 16,
    /// <summary><see cref="Core.NodeId"/>.</summary>
    NodeId = 17,
    /// <summary><see cref="Core.ExpandedNodeId"/>.</summary>
    ExpandedNodeId = 18,
    /// <summary><see cref="Core.StatusCode"/>.</summary>
    StatusCode = 19,
    /// <summary><see cref="Core.QualifiedName"/>.</summary>
    QualifiedName = 20,
    /// <summary><see cref="Core.LocalizedText"/>.</summary>
    LocalizedText = 21,
    /// <summary><see cref="Core.ExtensionObject"/> (structured value with a type id).</summary>
    ExtensionObject = 22,
    /// <summary><see cref="Core.DataValue"/>.</summary>
    DataValue = 23,
    /// <summary><see cref="Core.Variant"/> (only as array elements).</summary>
    Variant = 24,
    /// <summary><see cref="Core.DiagnosticInfo"/>.</summary>
    DiagnosticInfo = 25,
}
