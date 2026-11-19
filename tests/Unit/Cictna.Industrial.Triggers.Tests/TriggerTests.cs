using Cictna.Industrial.Core;

namespace Cictna.Industrial.Triggers.Tests;

/// <summary>The nine trigger kinds, one value stream at a time.</summary>
public sealed class TriggerTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);

    private static DataValue At(Variant value, double seconds, StatusCode status = default) =>
        new(value, status, T0.AddSeconds(seconds));

    /// <summary>Feeds numbers one second apart and returns which ones fired.</summary>
    private static List<double> Fired(TriggerDefinition definition, params double[] values)
    {
        var evaluator = definition.CreateEvaluator();
        var fired = new List<double>();
        for (var i = 0; i < values.Length; i++)
        {
            if (evaluator.OnValue(At(values[i], i)) is not null)
            {
                fired.Add(values[i]);
            }
        }
        return fired;
    }

    // ---- 1. any change -------------------------------------------------------------------------

    [Fact]
    public void AnyChangeFiresForEveryValueIncludingBadQuality()
    {
        var e = new AnyChangeTrigger().CreateEvaluator();
        Assert.NotNull(e.OnValue(At(1, 0)));
        Assert.NotNull(e.OnValue(At(1, 1))); // same value again still counts: the server reported it
        var bad = e.OnValue(At(Variant.Null, 2, StatusCodes.BadCommunicationError));
        Assert.NotNull(bad);
        Assert.Contains("BadCommunicationError", bad.Reason, StringComparison.Ordinal);
    }

    // ---- 2. threshold --------------------------------------------------------------------------

    [Fact]
    public void ThresholdFiresOncePerCrossingAndReArmsBelowTheHysteresis()
    {
        var trigger = new ThresholdTrigger(ThresholdComparison.Above, 80) { Hysteresis = 2 };
        // 81 fires; 79 is inside the band (no re-arm); 85 does not fire again; 77 re-arms; 82 fires.
        Assert.Equal([81, 82], Fired(trigger, 70, 81, 79, 85, 77, 82));
    }

    [Fact]
    public void ThresholdCanReportTheReturn()
    {
        var e = new ThresholdTrigger(ThresholdComparison.Below, 10) { Hysteresis = 1, NotifyOnReturn = true }.CreateEvaluator();
        Assert.Null(e.OnValue(At(12, 0)));
        Assert.Contains("went below 10", e.OnValue(At(9, 1))!.Reason, StringComparison.Ordinal);
        Assert.Null(e.OnValue(At(10.5, 2))); // not yet past limit + hysteresis
        Assert.Contains("returned", e.OnValue(At(11, 3))!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdLevelModeFiresWhileBeyondTheLimit() =>
        Assert.Equal([5, 6, 5], Fired(new ThresholdTrigger(ThresholdComparison.AtOrAbove, 5) { Mode = ThresholdMode.Level }, 4, 5, 6, 4, 5));

    [Fact]
    public void ThresholdFiresOnTheFirstValueWhenAlreadyBeyond() =>
        Assert.Equal([90], Fired(new ThresholdTrigger(ThresholdComparison.Above, 80), 90, 95));

    [Fact]
    public void BadValuesAreIgnoredByDefault()
    {
        var e = new ThresholdTrigger(ThresholdComparison.Above, 80).CreateEvaluator();
        Assert.Null(e.OnValue(At(0.0, 0, StatusCodes.BadSensorFailure))); // a failed sensor reading 0 is not "below"
        Assert.Null(e.Last);
        Assert.NotNull(e.OnValue(At(81, 1)));
        Assert.NotNull(new ThresholdTrigger(ThresholdComparison.Above, 80).CreateEvaluator()
            .OnValue(At(81, 0, StatusCodes.UncertainLastUsableValue))); // uncertain still counts
    }

    // ---- 3. deadband ---------------------------------------------------------------------------

    [Fact]
    public void DeadbandMeasuresFromTheLastReportedValue()
    {
        // 10 first; 10.4, 10.8 drift but stay within 1 of 10; 11.0 is 1.0 away: fires; then from 11.
        Assert.Equal([10, 11.0, 9.9], Fired(new DeadbandTrigger(1), 10, 10.4, 10.8, 11.0, 11.5, 10.1, 9.9));
    }

    [Fact]
    public void PercentDeadbandScalesWithTheValue() =>
        Assert.Equal([100, 105, 110.25], Fired(new DeadbandTrigger(5) { DeadbandKind = DeadbandKind.PercentOfLastReported }, 100, 104, 105, 109, 110.25));

    // ---- 4. rate of change ---------------------------------------------------------------------

    [Fact]
    public void RateOfChangeUsesTimestamps()
    {
        var e = new RateOfChangeTrigger(5) { Direction = Direction.Rising }.CreateEvaluator();
        Assert.Null(e.OnValue(At(0, 0)));
        Assert.Null(e.OnValue(At(4, 1)));      // 4 per second
        Assert.Null(e.OnValue(At(8, 2)));      // 4 per second
        var fast = e.OnValue(At(20, 3));       // 12 per second
        Assert.NotNull(fast);
        Assert.Contains("12/s", fast.Reason, StringComparison.Ordinal);
        Assert.Null(e.OnValue(At(0, 4)));      // falling fast, but only rising counts
        Assert.Null(e.OnValue(At(100, 4)));    // same timestamp: no rate
    }

    // ---- 5. direction --------------------------------------------------------------------------

    [Fact]
    public void DirectionIgnoresStepsBelowTheMinimum() =>
        Assert.Equal([12, 14], Fired(new DirectionTrigger(Direction.Rising) { MinStep = 1 }, 10, 10.5, 12, 11, 11.5, 14));

    [Fact]
    public void DirectionEitherWay() =>
        Assert.Equal([2, 1, 3], Fired(new DirectionTrigger(Direction.Either), 1, 2, 2, 1, 3));

    // ---- 6. value change -----------------------------------------------------------------------

    [Fact]
    public void ValueChangeIgnoresStatusAndTimestampOnlyChanges()
    {
        var e = new ValueChangeTrigger().CreateEvaluator();
        Assert.Null(e.OnValue(At("Running", 0)));
        Assert.Null(e.OnValue(At("Running", 1, StatusCodes.UncertainLastUsableValue)));
        var change = e.OnValue(At("Stopped", 2));
        Assert.NotNull(change);
        Assert.Equal("Running", change.Previous!.Value.GetValue<string>());
    }

    [Fact]
    public void ValueChangeFromThreeToFiveFiresOnlyForThatTransition()
    {
        var e = new ValueChangeTrigger { From = 3, To = 5 }.CreateEvaluator();
        var states = new[] { 1, 3, 4, 5, 3, 5 };
        var fired = states.Select((s, i) => e.OnValue(new DataValue(s, default, T0.AddSeconds(i))) is not null).ToArray();
        // 3->4 and 4->5 do not count (4->5 is not "from 3"); the last 3->5 does.
        Assert.Equal([false, false, false, false, false, true], fired);
    }

    // ---- 7. bit --------------------------------------------------------------------------------

    [Fact]
    public void BitWatchesOneBitOfAStatusWord()
    {
        var e = new BitTrigger(3) { Edge = Direction.Rising }.CreateEvaluator();
        Assert.Null(e.OnValue(At(Variant.From((ushort)0b0000), 0)));
        Assert.Null(e.OnValue(At(Variant.From((ushort)0b0111), 1)));        // other bits only
        Assert.Contains("bit 3 set", e.OnValue(At(Variant.From((ushort)0b1000), 2))!.Reason, StringComparison.Ordinal);
        Assert.Null(e.OnValue(At(Variant.From((ushort)0b0000), 3)));        // falling, not counted
    }

    [Fact]
    public void BitWorksOnSignedValues()
    {
        var e = new BitTrigger(31).CreateEvaluator();
        Assert.Null(e.OnValue(At(1, 0)));
        Assert.NotNull(e.OnValue(At(-1, 1))); // the sign bit of an Int32
    }

    [Fact]
    public void BitIndexIsChecked() => Assert.Throws<ArgumentOutOfRangeException>(() => new BitTrigger(64).CreateEvaluator());

    // ---- 8. time based -------------------------------------------------------------------------

    [Fact]
    public void TimeBasedFiresOnIntervalsWithTheLatestValue()
    {
        var e = new TimeBasedTrigger(TimeSpan.FromMinutes(1)).CreateEvaluator();
        Assert.Null(e.OnTick(T0));                              // schedules T0 + 1 min
        e.OnValue(new DataValue(42.0, default, T0.AddSeconds(10)));
        Assert.Null(e.OnTick(T0.AddSeconds(59)));
        var fired = e.OnTick(T0.AddSeconds(61));
        Assert.NotNull(fired);
        Assert.Equal(42.0, fired.Value!.Value.GetValue<double>());
        Assert.Equal(T0.AddMinutes(1), fired.Time);
        var late = e.OnTick(T0.AddMinutes(5).AddSeconds(1));    // slots 2, 3, 4 missed
        Assert.NotNull(late);
        Assert.Contains("3 missed", late.Reason, StringComparison.Ordinal);
        Assert.Null(e.OnTick(T0.AddMinutes(5).AddSeconds(30))); // one event, not a burst
    }

    [Fact]
    public void TimeBasedCanAlignToTheClock()
    {
        var e = new TimeBasedTrigger(TimeSpan.FromMinutes(15)) { AlignToClock = true, RequireValue = false }.CreateEvaluator();
        Assert.Null(e.OnTick(T0.AddMinutes(7)));                // 08:07 -> next at 08:15
        Assert.Null(e.OnTick(T0.AddMinutes(14)));
        Assert.Equal(T0.AddMinutes(15), e.OnTick(T0.AddMinutes(15))!.Time);
    }

    [Fact]
    public void TimeBasedWaitsForAValueByDefault()
    {
        var e = new TimeBasedTrigger(TimeSpan.FromSeconds(1)).CreateEvaluator();
        e.OnTick(T0);
        Assert.Null(e.OnTick(T0.AddSeconds(2)));
    }

    // ---- 9. connection -------------------------------------------------------------------------

    [Fact]
    public void ConnectionReportsChangesNotTheStartingState()
    {
        var e = new ConnectionTrigger { On = ConnectionChange.Both }.CreateEvaluator();
        Assert.Null(e.OnConnectionChanged(true, T0));
        Assert.Null(e.OnConnectionChanged(true, T0.AddSeconds(1)));
        Assert.Equal("connection lost", e.OnConnectionChanged(false, T0.AddSeconds(2))!.Reason);
        Assert.Equal("connection restored", e.OnConnectionChanged(true, T0.AddSeconds(3))!.Reason);
        var lostOnly = new ConnectionTrigger { On = ConnectionChange.Lost }.CreateEvaluator();
        lostOnly.OnConnectionChanged(false, T0);
        Assert.Null(lostOnly.OnConnectionChanged(true, T0.AddSeconds(1)));
    }

    // ---- common ----------------------------------------------------------------------------------

    [Fact]
    public void NonNumericValuesAreIgnoredByNumericTriggers()
    {
        foreach (var definition in new TriggerDefinition[] { new ThresholdTrigger(ThresholdComparison.Above, 0), new DeadbandTrigger(1), new RateOfChangeTrigger(1), new DirectionTrigger(Direction.Either), new BitTrigger(0) })
        {
            var e = definition.CreateEvaluator();
            Assert.Null(e.OnValue(At("text", 0)));
            Assert.Null(e.OnValue(At(Variant.FromArray(new[] { 1, 2 }), 1)));
        }
    }

    [Fact]
    public void ResetForgetsTheHistory()
    {
        var e = new ThresholdTrigger(ThresholdComparison.Above, 1).CreateEvaluator();
        Assert.NotNull(e.OnValue(At(2, 0)));
        Assert.Null(e.OnValue(At(3, 1)));
        e.Reset();
        Assert.Null(e.Last);
        Assert.NotNull(e.OnValue(At(3, 2)));
    }

    [Fact]
    public void EventsCarryNameKindAndTime()
    {
        var e = new DirectionTrigger(Direction.Rising) { Name = "Tank 2 filling" }.CreateEvaluator();
        e.OnValue(At(1, 0));
        var fired = e.OnValue(At(2, 1))!;
        Assert.Equal("Tank 2 filling", fired.Name);
        Assert.Equal(TriggerKind.Direction, fired.Kind);
        Assert.Equal(T0.AddSeconds(1), fired.Time); // the value's source timestamp
    }
}
