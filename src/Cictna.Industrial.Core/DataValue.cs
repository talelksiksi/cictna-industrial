namespace Cictna.Industrial.Core;

/// <summary>
/// A value together with its quality and timestamps: what a read returns and what a subscription
/// delivers. Every value in the stack travels as a DataValue, whatever protocol it came from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SourceTimestamp"/> is when the value was produced by the device (or the closest
/// point to it); <see cref="ServerTimestamp"/> is when the server that returned it received it.
/// Either may be null when the source did not provide it. For historical storage prefer the
/// source timestamp and fall back to the server timestamp.
/// </para>
/// <para>
/// A DataValue whose <see cref="Status"/> is Bad usually has no usable <see cref="Value"/>; check
/// <see cref="StatusCode.IsGood"/> before using the value.
/// </para>
/// </remarks>
/// <param name="Value">The value (may be <see cref="Variant.Null"/>).</param>
/// <param name="Status">Quality of the value; Good (0) when not given.</param>
/// <param name="SourceTimestamp">Time the value was produced at the source, UTC.</param>
/// <param name="ServerTimestamp">Time the server received or produced the value, UTC.</param>
/// <param name="SourcePicoseconds">Extra resolution for the source timestamp (0 to 9999, units of 10 ps).</param>
/// <param name="ServerPicoseconds">Extra resolution for the server timestamp (0 to 9999, units of 10 ps).</param>
public sealed record DataValue(
    Variant Value,
    StatusCode Status = default,
    DateTime? SourceTimestamp = null,
    DateTime? ServerTimestamp = null,
    ushort SourcePicoseconds = 0,
    ushort ServerPicoseconds = 0)
{
    /// <summary>A DataValue with only a status, for example to report that a value could not be read.</summary>
    /// <param name="status">The status code.</param>
    public static DataValue FromStatus(StatusCode status) => new(Variant.Null, status);

    /// <summary>Value and status, without timestamps.</summary>
    public override string ToString() =>
        Status.IsGood ? Value.ToString() : $"{Value} [{Status.Name}]";
}
