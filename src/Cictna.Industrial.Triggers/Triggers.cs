using Cictna.Industrial.Core;

namespace Cictna.Industrial.Triggers;

/// <summary>The nine kinds of trigger.</summary>
public enum TriggerKind
{
    /// <summary>Every new value.</summary>
    AnyChange,
    /// <summary>A value crosses a limit.</summary>
    Threshold,
    /// <summary>A value moved far enough from the last reported one.</summary>
    Deadband,
    /// <summary>A value changes faster than allowed.</summary>
    RateOfChange,
    /// <summary>A value goes up or down.</summary>
    Direction,
    /// <summary>A value is different from the previous one, optionally from one value to another.</summary>
    ValueChange,
    /// <summary>One bit of an integer value changes.</summary>
    Bit,
    /// <summary>At a fixed interval, whatever the value does.</summary>
    TimeBased,
    /// <summary>The connection to the data source is lost or restored.</summary>
    Connection,
}

/// <summary>How a threshold compares the value with its limit.</summary>
public enum ThresholdComparison
{
    /// <summary>value &gt; limit</summary>
    Above,
    /// <summary>value &gt;= limit</summary>
    AtOrAbove,
    /// <summary>value &lt; limit</summary>
    Below,
    /// <summary>value &lt;= limit</summary>
    AtOrBelow,
}

/// <summary>When a threshold fires.</summary>
public enum ThresholdMode
{
    /// <summary>Once when the condition becomes true; again only after it was false (with hysteresis).</summary>
    Edge,
    /// <summary>For every value while the condition is true.</summary>
    Level,
}

/// <summary>How a deadband is measured.</summary>
public enum DeadbandKind
{
    /// <summary>In the value's own units.</summary>
    Absolute,
    /// <summary>As a percentage of the last reported value.</summary>
    PercentOfLastReported,
}

/// <summary>A direction of movement.</summary>
public enum Direction
{
    /// <summary>Upwards.</summary>
    Rising,
    /// <summary>Downwards.</summary>
    Falling,
    /// <summary>Either way.</summary>
    Either,
}

/// <summary>Which connection changes a <see cref="ConnectionTrigger"/> reports.</summary>
public enum ConnectionChange
{
    /// <summary>Only losses.</summary>
    Lost,
    /// <summary>Only restorations.</summary>
    Restored,
    /// <summary>Both.</summary>
    Both,
}

/// <summary>A trigger fired.</summary>
/// <param name="Trigger">The definition that fired.</param>
/// <param name="Time">When: the value's source timestamp, else its server timestamp, else the time it was received or ticked.</param>
/// <param name="Value">The value that caused it; for a time-based trigger the latest value; null for connection triggers.</param>
/// <param name="Previous">The value it was compared with, when there is one.</param>
/// <param name="Reason">Why it fired, in a short sentence for logs and operators.</param>
public sealed record TriggerEvent(TriggerDefinition Trigger, DateTime Time, DataValue? Value, DataValue? Previous, string Reason)
{
    /// <summary>The kind of the trigger that fired.</summary>
    public TriggerKind Kind => Trigger.Kind;

    /// <summary>The trigger's name, if it has one.</summary>
    public string? Name => Trigger.Name;
}

/// <summary>
/// What should fire, and when. A definition holds settings only; call <see cref="CreateEvaluator"/>
/// for each value stream (each tag) to get the stateful object that does the work.
/// </summary>
/// <remarks>
/// Quality: by default values with a Bad status neither fire nor change the trigger's memory, so a
/// communication error does not look like a real change (<see cref="IgnoreBadQuality"/>). Uncertain
/// values count as usable. Numeric triggers accept every numeric built-in type and Boolean (0 or 1);
/// other values (strings, arrays) are ignored by them.
/// </remarks>
public abstract record TriggerDefinition
{
    private protected TriggerDefinition() { }

    /// <summary>Optional name, carried into every <see cref="TriggerEvent"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Skip values with a Bad status (default true; false for <see cref="AnyChangeTrigger"/>).</summary>
    public virtual bool IgnoreBadQuality { get; init; } = true;

    /// <summary>The kind of trigger.</summary>
    public abstract TriggerKind Kind { get; }

    /// <summary>Creates a fresh evaluator for one value stream. Evaluators are not thread-safe: one per stream and consumer.</summary>
    public abstract TriggerEvaluator CreateEvaluator();
}

/// <summary>Fires for every new value, including quality changes.</summary>
public sealed record AnyChangeTrigger : TriggerDefinition
{
    /// <inheritdoc />
    public override bool IgnoreBadQuality { get; init; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.AnyChange;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new AnyChangeEvaluator(this);
}

/// <summary>Fires when a numeric value crosses a limit.</summary>
/// <param name="Comparison">How the value is compared.</param>
/// <param name="Limit">The limit.</param>
/// <example><c>new ThresholdTrigger(ThresholdComparison.Above, 80) { Hysteresis = 2, NotifyOnReturn = true }</c>
/// fires when the value goes above 80 and again ("returned") when it falls below 78.</example>
public sealed record ThresholdTrigger(ThresholdComparison Comparison, double Limit) : TriggerDefinition
{
    /// <summary>Edge (once per crossing, default) or Level (every value while beyond the limit).</summary>
    public ThresholdMode Mode { get; init; } = ThresholdMode.Edge;

    /// <summary>
    /// How far back the value must come before the trigger re-arms, so a value that hovers at the
    /// limit does not fire on every jitter. In the value's units; 0 by default.
    /// </summary>
    public double Hysteresis { get; init; }

    /// <summary>Also fire (with reason "returned") when the value comes back past the re-arm point.</summary>
    public bool NotifyOnReturn { get; init; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Threshold;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new ThresholdEvaluator(this);
}

/// <summary>Fires for the first value, then whenever the value has moved at least the deadband from the last one reported.</summary>
/// <param name="Deadband">The minimum change, in units or percent (see <see cref="DeadbandKind"/>).</param>
public sealed record DeadbandTrigger(double Deadband) : TriggerDefinition
{
    /// <summary>Absolute units (default) or percent of the last reported value.</summary>
    public DeadbandKind DeadbandKind { get; init; } = DeadbandKind.Absolute;

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Deadband;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new DeadbandEvaluator(this);
}

/// <summary>Fires when the value changes faster than <paramref name="MaxRatePerSecond"/> units per second between two values.</summary>
/// <param name="MaxRatePerSecond">The allowed rate, positive.</param>
/// <remarks>The rate uses the values' timestamps (source, else server, else receipt time); two values with the same time are skipped.</remarks>
public sealed record RateOfChangeTrigger(double MaxRatePerSecond) : TriggerDefinition
{
    /// <summary>Which direction of change counts (default either).</summary>
    public Direction Direction { get; init; } = Direction.Either;

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.RateOfChange;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new RateOfChangeEvaluator(this);
}

/// <summary>Fires when the value moved up (or down) compared with the previous value.</summary>
/// <param name="Direction">Which way counts.</param>
public sealed record DirectionTrigger(Direction Direction) : TriggerDefinition
{
    /// <summary>Smallest step that counts, to ignore noise. 0 by default (any movement).</summary>
    public double MinStep { get; init; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Direction;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new DirectionEvaluator(this);
}

/// <summary>
/// Fires when the value (not just its status or timestamp) differs from the previous one. With
/// <see cref="From"/> and/or <see cref="To"/> only that transition fires, for example a machine
/// state going from 3 (running) to 5 (fault).
/// </summary>
/// <remarks>Values are compared as OPC UA values: Int32 5 and Double 5.0 are different.</remarks>
public sealed record ValueChangeTrigger : TriggerDefinition
{
    /// <summary>Only changes away from this value count; null for any.</summary>
    public Variant? From { get; init; }

    /// <summary>Only changes to this value count; null for any.</summary>
    public Variant? To { get; init; }

    /// <summary>Fire for the very first value too (nothing to compare with). False by default.</summary>
    public bool FireOnFirstValue { get; init; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.ValueChange;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new ValueChangeEvaluator(this);
}

/// <summary>Fires when one bit of an integer value changes, for status words and packed alarms.</summary>
/// <param name="BitIndex">0 for the least significant bit, up to 63.</param>
public sealed record BitTrigger(int BitIndex) : TriggerDefinition
{
    /// <summary>Rising (0 to 1), Falling (1 to 0) or Either (default).</summary>
    public Direction Edge { get; init; } = Direction.Either;

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Bit;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator()
    {
        if (BitIndex is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(BitIndex), BitIndex, "Bit index must be 0 to 63.");
        }
        return new BitEvaluator(this);
    }
}

/// <summary>
/// Fires every <paramref name="Interval"/>, with the latest value, whether it changed or not. Drive
/// it with <see cref="TriggerEvaluator.OnTick"/>; values only update what it reports.
/// </summary>
/// <param name="Interval">Time between firings.</param>
public sealed record TimeBasedTrigger(TimeSpan Interval) : TriggerDefinition
{
    /// <summary>Fire on round clock times (every 15 minutes at :00, :15, :30, :45) instead of counting from the start.</summary>
    public bool AlignToClock { get; init; }

    /// <summary>Wait for a first value before firing (default true); false fires with no value.</summary>
    public bool RequireValue { get; init; } = true;

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.TimeBased;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator()
    {
        if (Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Interval), Interval, "Interval must be positive.");
        }
        return new TimeBasedEvaluator(this);
    }
}

/// <summary>
/// Fires when the connection to the data source is lost or restored. Drive it with
/// <see cref="TriggerEvaluator.OnConnectionChanged"/>, for example from an OPC UA client's state events.
/// </summary>
public sealed record ConnectionTrigger : TriggerDefinition
{
    /// <summary>Which changes fire (default both).</summary>
    public ConnectionChange On { get; init; } = ConnectionChange.Both;

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Connection;

    /// <inheritdoc />
    public override TriggerEvaluator CreateEvaluator() => new ConnectionEvaluator(this);
}
