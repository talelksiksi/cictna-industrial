using System.Globalization;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.Triggers;

/// <summary>
/// The stateful side of a trigger for one value stream. Feed it every value
/// (<see cref="OnValue"/>), clock ticks for time-based triggers (<see cref="OnTick"/>) and
/// connection changes for connection triggers (<see cref="OnConnectionChanged"/>); each call returns
/// the event when the trigger fires, otherwise null.
/// </summary>
/// <remarks>Not thread-safe: use one evaluator per stream and consumer, fed from one thread at a time.</remarks>
/// <example>
/// <code>
/// var overheat = new ThresholdTrigger(ThresholdComparison.Above, 80) { Name = "Overheat", Hysteresis = 2 }.CreateEvaluator();
/// await foreach (var change in subscription.DataChanges.ReadAllAsync())
///     if (overheat.OnValue(change.Value) is { } e)
///         logger.LogWarning("{Trigger}: {Reason}", e.Name, e.Reason);
/// </code>
/// </example>
public abstract class TriggerEvaluator
{
    private protected TriggerEvaluator(TriggerDefinition definition) => Definition = definition;

    /// <summary>The definition this evaluator runs.</summary>
    public TriggerDefinition Definition { get; }

    /// <summary>The last value accepted (passed the quality filter), or null.</summary>
    public DataValue? Last { get; private set; }

    /// <summary>Evaluates a new value.</summary>
    /// <param name="value">The value with its status and timestamps.</param>
    /// <param name="receivedAtUtc">When it arrived; used when the value has no timestamps. Defaults to now.</param>
    /// <returns>The event if the trigger fired.</returns>
    public TriggerEvent? OnValue(DataValue value, DateTime? receivedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Definition.IgnoreBadQuality && value.Status.IsBad)
        {
            return null;
        }
        var time = value.SourceTimestamp ?? value.ServerTimestamp ?? receivedAtUtc ?? DateTime.UtcNow;
        var previous = Last;
        Last = value;
        return Evaluate(value, previous, time);
    }

    /// <summary>Lets time pass; only time-based triggers react.</summary>
    /// <param name="nowUtc">The current time.</param>
    public virtual TriggerEvent? OnTick(DateTime nowUtc) => null;

    /// <summary>Reports a connection change; only connection triggers react.</summary>
    /// <param name="connected">Whether the source is connected now.</param>
    /// <param name="atUtc">When it changed.</param>
    public virtual TriggerEvent? OnConnectionChanged(bool connected, DateTime atUtc) => null;

    /// <summary>Forgets everything seen so far, as if newly created.</summary>
    public virtual void Reset() => Last = null;

    /// <summary>Kind-specific decision for an accepted value.</summary>
    private protected abstract TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time);

    private protected TriggerEvent Fire(DateTime time, DataValue? value, DataValue? previous, string reason) =>
        new(Definition, time, value, previous, reason);

    private protected static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>Converts values for the numeric and bit triggers.</summary>
internal static class Numbers
{
    /// <summary>Numeric scalars and Boolean (0/1) as double; everything else is not a number.</summary>
    public static bool TryGetDouble(DataValue? value, out double number)
    {
        number = 0;
        if (value is null || value.Value.IsArray)
        {
            return false;
        }
        switch (value.Value.Value)
        {
            case bool b: number = b ? 1 : 0; return true;
            case sbyte v: number = v; return true;
            case byte v: number = v; return true;
            case short v: number = v; return true;
            case ushort v: number = v; return true;
            case int v: number = v; return true;
            case uint v: number = v; return true;
            case long v: number = v; return true;
            case ulong v: number = v; return true;
            case float v when float.IsFinite(v): number = v; return true;
            case double v when double.IsFinite(v): number = v; return true;
            default: return false;
        }
    }

    /// <summary>Integer scalars and Boolean as their bit pattern (two's complement for signed types).</summary>
    public static bool TryGetBits(DataValue? value, out ulong bits)
    {
        bits = 0;
        if (value is null || value.Value.IsArray)
        {
            return false;
        }
        switch (value.Value.Value)
        {
            case bool b: bits = b ? 1u : 0u; return true;
            case sbyte v: bits = unchecked((byte)v); return true;
            case byte v: bits = v; return true;
            case short v: bits = unchecked((ushort)v); return true;
            case ushort v: bits = v; return true;
            case int v: bits = unchecked((uint)v); return true;
            case uint v: bits = v; return true;
            case long v: bits = unchecked((ulong)v); return true;
            case ulong v: bits = v; return true;
            default: return false;
        }
    }
}

internal sealed class AnyChangeEvaluator(AnyChangeTrigger definition) : TriggerEvaluator(definition)
{
    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time) =>
        Fire(time, value, previous, value.Status.IsGood ? "new value" : $"new value, status {value.Status.Name}");
}

internal sealed class ThresholdEvaluator(ThresholdTrigger definition) : TriggerEvaluator(definition)
{
    private bool _active; // the condition was met and has not re-armed yet

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (!Numbers.TryGetDouble(value, out var v))
        {
            return null;
        }
        var d = definition;
        var met = d.Comparison switch
        {
            ThresholdComparison.Above => v > d.Limit,
            ThresholdComparison.AtOrAbove => v >= d.Limit,
            ThresholdComparison.Below => v < d.Limit,
            _ => v <= d.Limit,
        };
        if (d.Mode == ThresholdMode.Level)
        {
            return met ? Fire(time, value, previous, $"{Format(v)} is {Describe(d)}") : null;
        }
        if (!_active && met)
        {
            _active = true;
            return Fire(time, value, previous, $"{Format(v)} went {Describe(d)}");
        }
        if (_active && !met)
        {
            // Re-arm only once the value is back past the hysteresis band.
            var hysteresis = Math.Abs(d.Hysteresis);
            var rearmed = d.Comparison is ThresholdComparison.Above or ThresholdComparison.AtOrAbove
                ? v <= d.Limit - hysteresis
                : v >= d.Limit + hysteresis;
            if (rearmed)
            {
                _active = false;
                return d.NotifyOnReturn ? Fire(time, value, previous, $"{Format(v)} returned within the limit {Format(d.Limit)}") : null;
            }
        }
        return null;
    }

    private static string Describe(ThresholdTrigger d) => d.Comparison switch
    {
        ThresholdComparison.Above => $"above {Format(d.Limit)}",
        ThresholdComparison.AtOrAbove => $"at or above {Format(d.Limit)}",
        ThresholdComparison.Below => $"below {Format(d.Limit)}",
        _ => $"at or below {Format(d.Limit)}",
    };

    public override void Reset()
    {
        base.Reset();
        _active = false;
    }
}

internal sealed class DeadbandEvaluator(DeadbandTrigger definition) : TriggerEvaluator(definition)
{
    private DataValue? _reported;
    private double _reportedNumber;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (!Numbers.TryGetDouble(value, out var v))
        {
            return null;
        }
        if (_reported is null)
        {
            return Report(value, v, time, "first value");
        }
        var change = Math.Abs(v - _reportedNumber);
        var band = definition.DeadbandKind == DeadbandKind.Absolute
            ? Math.Abs(definition.Deadband)
            : Math.Abs(_reportedNumber) * Math.Abs(definition.Deadband) / 100;
        // A change exactly at the band counts; with a zero reference in percent mode any change counts.
        return change > 0 && change >= band
            ? Report(value, v, time, $"moved {Format(change)} from {Format(_reportedNumber)}")
            : null;
    }

    private TriggerEvent Report(DataValue value, double v, DateTime time, string reason)
    {
        var previous = _reported;
        _reported = value;
        _reportedNumber = v;
        return Fire(time, value, previous, reason);
    }

    public override void Reset()
    {
        base.Reset();
        _reported = null;
    }
}

internal sealed class RateOfChangeEvaluator(RateOfChangeTrigger definition) : TriggerEvaluator(definition)
{
    private DataValue? _previous;
    private double _previousNumber;
    private DateTime _previousTime;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (!Numbers.TryGetDouble(value, out var v))
        {
            return null;
        }
        TriggerEvent? result = null;
        if (_previous is not null)
        {
            var seconds = (time - _previousTime).TotalSeconds;
            if (seconds <= 0)
            {
                return null; // same or older timestamp: no rate; keep the earlier reference
            }
            var rate = (v - _previousNumber) / seconds;
            var counts = definition.Direction switch
            {
                Direction.Rising => rate > 0,
                Direction.Falling => rate < 0,
                _ => true,
            };
            if (counts && Math.Abs(rate) > Math.Abs(definition.MaxRatePerSecond))
            {
                result = Fire(time, value, _previous, $"changing at {Format(rate)}/s, limit {Format(Math.Abs(definition.MaxRatePerSecond))}/s");
            }
        }
        _previous = value;
        _previousNumber = v;
        _previousTime = time;
        return result;
    }

    public override void Reset()
    {
        base.Reset();
        _previous = null;
    }
}

internal sealed class DirectionEvaluator(DirectionTrigger definition) : TriggerEvaluator(definition)
{
    private DataValue? _previous;
    private double _previousNumber;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (!Numbers.TryGetDouble(value, out var v))
        {
            return null;
        }
        TriggerEvent? result = null;
        if (_previous is not null)
        {
            var step = v - _previousNumber;
            var min = Math.Max(Math.Abs(definition.MinStep), double.Epsilon);
            var rising = step >= min;
            var falling = -step >= min;
            if ((rising && definition.Direction != Direction.Falling) || (falling && definition.Direction != Direction.Rising))
            {
                result = Fire(time, value, _previous, $"{(rising ? "rose" : "fell")} from {Format(_previousNumber)} to {Format(v)}");
            }
        }
        _previous = value;
        _previousNumber = v;
        return result;
    }

    public override void Reset()
    {
        base.Reset();
        _previous = null;
    }
}

internal sealed class ValueChangeEvaluator(ValueChangeTrigger definition) : TriggerEvaluator(definition)
{
    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (previous is null)
        {
            return definition.FireOnFirstValue && (definition.To is not { } to || value.Value.Equals(to))
                ? Fire(time, value, null, $"first value {value.Value}")
                : null;
        }
        if (value.Value.Equals(previous.Value))
        {
            return null; // only status or timestamp changed
        }
        if (definition.From is { } from && !previous.Value.Equals(from))
        {
            return null;
        }
        if (definition.To is { } target && !value.Value.Equals(target))
        {
            return null;
        }
        return Fire(time, value, previous, $"changed from {previous.Value} to {value.Value}");
    }
}

internal sealed class BitEvaluator(BitTrigger definition) : TriggerEvaluator(definition)
{
    private bool? _bit;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time)
    {
        if (!Numbers.TryGetBits(value, out var bits))
        {
            return null;
        }
        var bit = ((bits >> definition.BitIndex) & 1) == 1;
        var before = _bit;
        _bit = bit;
        if (before is not { } was || was == bit)
        {
            return null; // first value, or unchanged
        }
        var counts = definition.Edge switch
        {
            Direction.Rising => bit,
            Direction.Falling => !bit,
            _ => true,
        };
        return counts ? Fire(time, value, previous, $"bit {definition.BitIndex} {(bit ? "set" : "cleared")}") : null;
    }

    public override void Reset()
    {
        base.Reset();
        _bit = null;
    }
}

internal sealed class TimeBasedEvaluator(TimeBasedTrigger definition) : TriggerEvaluator(definition)
{
    private DateTime? _due;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time) => null;

    public override TriggerEvent? OnTick(DateTime nowUtc)
    {
        var interval = definition.Interval;
        if (_due is not { } due)
        {
            _due = definition.AlignToClock
                ? new DateTime((nowUtc.Ticks / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Utc)
                : nowUtc + interval;
            return null;
        }
        if (nowUtc < due)
        {
            return null;
        }
        // Next slot strictly in the future: after a long pause the trigger fires once, not once per missed slot.
        var missed = (nowUtc - due).Ticks / interval.Ticks;
        _due = due + TimeSpan.FromTicks(interval.Ticks * (missed + 1));
        if (definition.RequireValue && Last is null)
        {
            return null;
        }
        return Fire(due, Last, null, missed > 0 ? $"interval elapsed ({missed} missed)" : "interval elapsed");
    }

    public override void Reset()
    {
        base.Reset();
        _due = null;
    }
}

internal sealed class ConnectionEvaluator(ConnectionTrigger definition) : TriggerEvaluator(definition)
{
    private bool? _connected;

    private protected override TriggerEvent? Evaluate(DataValue value, DataValue? previous, DateTime time) => null;

    public override TriggerEvent? OnConnectionChanged(bool connected, DateTime atUtc)
    {
        var before = _connected;
        _connected = connected;
        if (before is not { } was || was == connected)
        {
            return null; // the first report only sets the starting point
        }
        var counts = definition.On switch
        {
            ConnectionChange.Lost => !connected,
            ConnectionChange.Restored => connected,
            _ => true,
        };
        return counts ? Fire(atUtc, null, null, connected ? "connection restored" : "connection lost") : null;
    }

    public override void Reset()
    {
        base.Reset();
        _connected = null;
    }
}
