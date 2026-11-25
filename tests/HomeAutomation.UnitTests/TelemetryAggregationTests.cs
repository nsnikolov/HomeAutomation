using HomeAutomation.Domain;

namespace HomeAutomation.UnitTests;

public sealed class TelemetryAggregationTests
{
    private static readonly DateTime Later = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GaugeReportsTimeWeightedAverageSeparatelyFromArithmeticMeanAndExtremes()
    {
        var result = Single(Metric.Temperature, 60, [N(T(10), 20), N(T(10, 50), 26)], Hour(10));

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(3600, result.CoveredSeconds, 6);
        Assert.Equal(21, result.TimeWeightedAverage!.Value, 9);
        Assert.Equal(23, result.ArithmeticMean!.Value, 9);
        Assert.Equal(20, result.Minimum);
        Assert.Equal(26, result.Maximum);
        Assert.Equal(T(10), result.FirstObservedAt);
        Assert.Equal(T(10, 50), result.LastObservedAt);
        Assert.Equal(26, result.LastValue);
        Assert.Null(result.EstimatedPositiveWh);
    }

    [Fact]
    public void HoldBoundsCoverageAndLeavesLongerGapsUncovered()
    {
        var results = TelemetryAggregation.Summarize(Metric.Humidity, Hold(15), [N(T(10), 40), N(T(10, 40), 50)],
            [Hour(10), Hour(11)], Later);

        Assert.Equal(1800, results[0].CoveredSeconds, 6);
        Assert.Equal(45, results[0].TimeWeightedAverage!.Value, 9);
        Assert.True(results[1].IsEmpty);
        Assert.Null(results[1].TimeWeightedAverage);
    }

    [Fact]
    public void HeldValueCrossesHourBoundaryWithoutCountingAsAnObservation()
    {
        var results = TelemetryAggregation.Summarize(Metric.Voltage, Hold(15), [N(T(10, 50), 230)], [Hour(10), Hour(11)], Later);

        Assert.Equal(600, results[0].CoveredSeconds, 6);
        Assert.Equal(1, results[0].SampleCount);
        Assert.Equal(300, results[1].CoveredSeconds, 6);
        Assert.Equal(0, results[1].SampleCount);
        Assert.Equal(230, results[1].Minimum);
        Assert.Equal(230, results[1].Maximum);
        Assert.Null(results[1].FirstObservedAt);
        Assert.Null(results[1].ArithmeticMean);
    }

    [Fact]
    public void CoverageNeverExtendsBeyondNow()
    {
        var result = TelemetryAggregation.Summarize(Metric.Current, Hold(60), [N(T(10), 2)], [Hour(10)], T(10, 20)).Single();

        Assert.Equal(1200, result.CoveredSeconds, 6);
    }

    [Fact]
    public void InvalidAndWronglyTypedValuesAreFlaggedAndNeverBecomeZero()
    {
        var result = Single(Metric.Temperature, 30,
            [N(T(10), 20), N(T(10, 10), double.NaN), new RawObservation(T(10, 20), null, true)], Hour(10));

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(1800, result.CoveredSeconds, 6);
        Assert.Equal(20, result.Minimum);
        Assert.Equal(AggregateFlags.InvalidValue, result.Flags);
    }

    [Fact]
    public void OutOfOrderInputMatchesOrderedInputAndDuplicateInstantsKeepTheFirstValue()
    {
        RawObservation[] ordered = [N(T(10), 20), N(T(10, 20), 22), N(T(10, 40), 21)];
        var expected = Single(Metric.Temperature, 30, ordered, Hour(10));

        Assert.Equal(expected, Single(Metric.Temperature, 30, ordered.Reverse().ToArray(), Hour(10)));
        var duplicate = Single(Metric.Temperature, 30, [N(T(10), 20), N(T(10), 99)], Hour(10));
        Assert.Equal(20, duplicate.Maximum);
        Assert.Equal(1, duplicate.SampleCount);
    }

    [Fact]
    public void RecomputationIsDeterministic()
    {
        RawObservation[] samples = [N(T(9, 55), 900), N(T(10, 7), 1300), N(T(10, 31), 0), N(T(10, 58), 450)];
        var first = TelemetryAggregation.Summarize(Metric.ActivePower, Hold(10), samples, [Hour(10), Hour(11)], Later);
        var second = TelemetryAggregation.Summarize(Metric.ActivePower, Hold(10), samples, [Hour(10), Hour(11)], Later);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ConstantPowerIntegratesToEstimatedEnergy()
    {
        var samples = Enumerable.Range(0, 12).Select(index => N(T(10, index * 5), 1000)).ToArray();
        var result = Single(Metric.ActivePower, 7, samples, Hour(10));

        Assert.Equal(1000, result.EstimatedPositiveWh!.Value, 9);
        Assert.Equal(0, result.EstimatedNegativeWh);
        Assert.Equal(3600, result.CoveredSeconds, 6);
        Assert.Null(result.MeasuredWh);
    }

    [Fact]
    public void VariablePowerIntegratesEachHeldInterval()
    {
        var result = Single(Metric.ActivePower, 60, [N(T(10), 600), N(T(10, 30), 1200)], Hour(10));

        Assert.Equal(900, result.EstimatedPositiveWh!.Value, 9);
        Assert.Equal(900, result.TimeWeightedAverage!.Value, 9);
    }

    [Fact]
    public void LongTelemetryGapIsNotFilledWithTheLastWattage()
    {
        var results = TelemetryAggregation.Summarize(Metric.ActivePower, Hold(15), [N(T(10), 1000), N(T(13), 1000)],
            [Hour(10), Hour(11), Hour(12)], Later);

        Assert.Equal(250, results[0].EstimatedPositiveWh!.Value, 9);
        Assert.Equal(900, results[0].CoveredSeconds, 6);
        Assert.True(results[1].IsEmpty);
        Assert.True(results[2].IsEmpty);
        Assert.Null(results[1].EstimatedPositiveWh);
    }

    [Fact]
    public void SignedPowerKeepsChargeAndDischargeEnergySeparate()
    {
        var result = Single(Metric.BatteryPower, 60, [N(T(10), 500), N(T(10, 30), -1000)], Hour(10));

        Assert.Equal(250, result.EstimatedPositiveWh!.Value, 9);
        Assert.Equal(500, result.EstimatedNegativeWh!.Value, 9);
        Assert.Equal(-250, result.TimeWeightedAverage!.Value, 9);
    }

    [Fact]
    public void RelayReportsOnSecondsTransitionsAndOfflineGapsSeparately()
    {
        var result = Single(Metric.SwitchOn, 7,
            [B(T(10), true), B(T(10, 5), true), B(T(10, 10), false), B(T(10, 15), false)], Hour(10));

        Assert.Equal(1320, result.CoveredSeconds, 6);
        Assert.Equal(600, result.OnSeconds!.Value, 6);
        Assert.Equal(1, result.TransitionCount);
        Assert.Equal(4, result.SampleCount);
        Assert.Equal(0, result.LastValue);
        Assert.Null(result.TimeWeightedAverage);
    }

    [Fact]
    public void RelayChangeAcrossAGapIsNotCountedAsAnObservedTransition()
    {
        var result = Single(Metric.SwitchOn, 7, [B(T(10), true), B(T(10, 30), false)], Hour(10));

        Assert.Equal(840, result.CoveredSeconds, 6);
        Assert.Equal(420, result.OnSeconds!.Value, 6);
        Assert.Equal(0, result.TransitionCount);
    }

    [Fact]
    public void RelayLostAtTheStartOfAnHourHasNoOnTime()
    {
        var results = TelemetryAggregation.Summarize(Metric.SwitchOn, Hold(7), [B(T(9, 50), true)], [Hour(10)], Later);

        Assert.True(results[0].IsEmpty);
        Assert.Null(results[0].OnSeconds);
    }

    [Fact]
    public void DailyCounterSplitsShortIncrementsAndFlagsLongGaps()
    {
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(40), TimeZoneInfo.Utc);
        RawObservation[] samples = [N(T(0, 10), 0.1), N(T(10, 20), 2.0), N(T(10, 50), 2.3), N(T(11, 10), 2.7)];
        var results = TelemetryAggregation.Summarize(Metric.PvEnergyToday, policy, samples, [Hour(10), Hour(11)], Later);

        Assert.Equal(2400, results[0].MeasuredWh!.Value, 6);
        Assert.Equal(2400, results[0].CoveredSeconds, 6);
        Assert.Equal(AggregateFlags.CounterGap, results[0].Flags);
        Assert.Equal(200, results[1].MeasuredWh!.Value, 6);
        Assert.Equal(600, results[1].CoveredSeconds, 6);
        Assert.Null(results[0].Minimum);
        Assert.Equal(2.3, results[0].LastValue);
    }

    [Fact]
    public void DailyCounterRestartsAtLocalMidnightWithoutCrossDayDeltas()
    {
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(45), TimeZoneInfo.Utc);
        var day = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        RawObservation[] samples =
        [
            N(day.AddHours(22).AddMinutes(50), 9.7), N(day.AddHours(23).AddMinutes(30), 10.0), N(day.AddHours(23).AddMinutes(50), 10.2),
            N(day.AddDays(1).AddMinutes(10), 0.1), N(day.AddDays(1).AddMinutes(30), 0.3)
        ];
        var results = TelemetryAggregation.Summarize(Metric.OutputEnergyToday, policy, samples,
            [new(day.AddHours(23), day.AddDays(1)), new(day.AddDays(1), day.AddDays(1).AddHours(1))], Later);

        Assert.Equal(425, results[0].MeasuredWh!.Value, 6);
        Assert.Equal(3000, results[0].CoveredSeconds, 6);
        Assert.Equal(300, results[1].MeasuredWh!.Value, 6);
        Assert.Equal(1800, results[1].CoveredSeconds, 6);
        Assert.Equal(AggregateFlags.None, results[0].Flags | results[1].Flags);
    }

    [Fact]
    public void DailyCounterUsesItsOwnResetZone()
    {
        // 20:50 and 21:10 UTC are 23:50 and 00:10 in Sofia summer time, so the counter restarted in between.
        RawObservation[] samples = [N(T(20, 50), 5.0), N(T(21, 10), 0.2)];
        var sofia = TelemetryAggregation.Summarize(Metric.BatteryDischargedEnergyToday,
            new AggregationPolicy(TimeSpan.FromMinutes(30), Zone("Europe/Sofia")), samples, [Hour(21)], Later).Single();
        Assert.Equal(200, sofia.MeasuredWh!.Value, 6);
        Assert.Equal(600, sofia.CoveredSeconds, 6);
        Assert.Equal(AggregateFlags.None, sofia.Flags);

        var utc = TelemetryAggregation.Summarize(Metric.BatteryDischargedEnergyToday,
            new AggregationPolicy(TimeSpan.FromMinutes(30), TimeZoneInfo.Utc), samples, [Hour(21)], Later).Single();
        Assert.Null(utc.MeasuredWh);
        Assert.Equal(AggregateFlags.CounterDiscontinuity, utc.Flags);
    }

    [Fact]
    public void DailyCounterRejectsInDayDecreasesAndImplausibleJumps()
    {
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(30), TimeZoneInfo.Utc);
        var decrease = TelemetryAggregation.Summarize(Metric.PvEnergyToday, policy,
            [N(T(9, 50), 1.9), N(T(10), 2.0), N(T(10, 10), 1.5), N(T(10, 20), 1.7)], [Hour(10)], Later).Single();
        Assert.Equal(200, decrease.MeasuredWh!.Value, 6);
        Assert.Equal(AggregateFlags.CounterDiscontinuity, decrease.Flags);

        var noReset = TelemetryAggregation.Summarize(Metric.PvEnergyToday, policy,
            [N(T(23, 50), 12.0), N(T(0, 5).AddDays(1), 12.1)], [new(T(0).AddDays(1), T(1).AddDays(1))], Later).Single();
        Assert.Null(noReset.MeasuredWh);
        Assert.Equal(AggregateFlags.CounterDiscontinuity, noReset.Flags);
    }

    [Fact]
    public void CumulativeCounterTreatsDecreasesAsResetsOrRolloverWithANewBaseline()
    {
        var result = Single(Metric.Energy, 15, [N(T(10), 100.0), N(T(10, 10), 100.5), N(T(10, 20), 0.2), N(T(10, 30), 0.4)], Hour(10));

        Assert.Equal(700, result.MeasuredWh!.Value, 6);
        Assert.Equal(1200, result.CoveredSeconds, 6);
        Assert.Equal(AggregateFlags.CounterReset, result.Flags);
    }

    [Fact]
    public void MissingCounterSamplesAttributeTheMeasuredDeltaToTheClosingHourWithAQualityFlag()
    {
        var results = TelemetryAggregation.Summarize(Metric.Energy, Hold(15), [N(T(10), 1.0), N(T(13, 30), 4.0)],
            [Hour(10), Hour(11), Hour(12), Hour(13)], Later);

        Assert.Null(results[0].MeasuredWh);
        Assert.True(results[1].IsEmpty);
        Assert.True(results[2].IsEmpty);
        Assert.Equal(3000, results[3].MeasuredWh!.Value, 6);
        Assert.Equal(0, results[3].CoveredSeconds);
        Assert.Equal(AggregateFlags.CounterGap, results[3].Flags);
    }

    [Fact]
    public void CumulativeCounterIsNotDifferencedAcrossMoreThanADay()
    {
        var result = TelemetryAggregation.Summarize(Metric.Energy, Hold(15), [N(T(10), 1.0), N(T(11).AddDays(1), 5.0)],
            [new(T(11).AddDays(1), T(12).AddDays(1))], Later).Single();

        Assert.Null(result.MeasuredWh);
        Assert.Equal(1, result.SampleCount);
    }

    [Fact]
    public void MetricsUseCapabilityKindsRatherThanDeviceModels()
    {
        Assert.Equal(MetricAggregation.Gauge, TelemetryAggregation.Kind(Metric.Temperature));
        Assert.Equal(MetricAggregation.Boolean, TelemetryAggregation.Kind(Metric.SwitchOn));
        Assert.Equal(MetricAggregation.Status, TelemetryAggregation.Kind(Metric.OperatingMode));
        Assert.Equal(MetricAggregation.DailyResetCounter, TelemetryAggregation.Kind(Metric.PvEnergyToday));
        Assert.Equal(MetricAggregation.CumulativeCounter, TelemetryAggregation.Kind(Metric.Energy));
        Assert.True(TelemetryAggregation.IsPower(Metric.ActivePower));
        Assert.True(TelemetryAggregation.IsPower(Metric.BatteryPower));
        Assert.False(TelemetryAggregation.IsPower(Metric.Voltage));
        Assert.Throws<ArgumentException>(() =>
            TelemetryAggregation.Summarize(Metric.OperatingMode, Hold(5), [], [Hour(10)], Later));
    }

    [Theory]
    [InlineData("Europe/Sofia", 2026, 3, 29, "2026-03-28T22:00:00Z", 23)]
    [InlineData("Europe/Sofia", 2026, 10, 25, "2026-10-24T21:00:00Z", 25)]
    [InlineData("Asia/Kolkata", 2026, 6, 1, "2026-05-31T18:30:00Z", 24)]
    [InlineData("Asia/Kathmandu", 2026, 6, 1, "2026-05-31T18:15:00Z", 24)]
    [InlineData("America/Havana", 2026, 3, 8, "2026-03-08T05:00:00Z", 23)]
    public void CalendarDaysFollowTheLocalZone(string zoneId, int year, int month, int day, string expectedStart, int hours)
    {
        var window = CalendarDays.Day(new DateOnly(year, month, day), Zone(zoneId));

        Assert.Equal(DateTime.Parse(expectedStart, null, System.Globalization.DateTimeStyles.AdjustToUniversal), window.Start);
        Assert.Equal(TimeSpan.FromHours(hours), window.Duration);
    }

    [Fact]
    public void PlannerUsesWholeHoursRawEdgesAndMarksUnaggregatedTime()
    {
        var kathmandu = CalendarDays.Day(new DateOnly(2026, 6, 2), Zone("Asia/Kathmandu"));
        var aggregatedFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var aggregatedTo = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 2, 12, 40, 0, DateTimeKind.Utc);

        var parts = HistoryPlanner.Split(kathmandu, aggregatedFrom, aggregatedTo, now);

        Assert.Equal(kathmandu.Start, parts[0].Window.Start);
        Assert.Equal(HistoryPartSource.Raw, parts[0].Source);
        Assert.Equal(TimeSpan.FromMinutes(45), parts[0].Window.Duration);
        Assert.All(parts.Where(part => part.Source == HistoryPartSource.Hourly), part =>
            Assert.Equal(TimeSpan.FromHours(1), part.Window.Duration));
        Assert.Equal(HistoryPartSource.Raw, parts[^1].Source);
        Assert.Equal(new TimeWindow(aggregatedTo, now), parts[^1].Window);
        for (var index = 1; index < parts.Count; index++) Assert.Equal(parts[index - 1].Window.End, parts[index].Window.Start);

        var early = HistoryPlanner.Split(new(aggregatedFrom.AddHours(-3), aggregatedFrom.AddHours(1)), aggregatedFrom, aggregatedTo, now);
        Assert.Equal([HistoryPartSource.NotAggregated, HistoryPartSource.Hourly], early.Select(part => part.Source));
    }

    [Theory]
    [InlineData("Asia/Kathmandu", Metric.Temperature)]
    [InlineData("Asia/Kolkata", Metric.ActivePower)]
    [InlineData("Asia/Kolkata", Metric.SwitchOn)]
    [InlineData("Asia/Kathmandu", Metric.PvEnergyToday)]
    [InlineData("Asia/Kathmandu", Metric.Energy)]
    [InlineData("Europe/Sofia", Metric.BatteryPower)]
    public void DailyFromHourlyRowsAndRawPartialsEqualsADirectDailyCalculation(string zoneId, Metric metric)
    {
        var zone = Zone(zoneId);
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(12), zone);
        var start = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        var samples = Series(metric, start, TimeSpan.FromDays(4));
        var now = start.AddDays(4);
        var aggregatedFrom = start;
        var aggregatedTo = start.AddDays(3).AddHours(6);
        var hours = Enumerable.Range(0, 78).Select(index => new TimeWindow(start.AddHours(index), start.AddHours(index + 1))).ToArray();
        var hourly = TelemetryAggregation.Summarize(metric, policy, samples, hours, now).ToDictionary(item => item.Start);

        foreach (var date in new[] { new DateOnly(2026, 5, 31), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2) })
        {
            var day = CalendarDays.Day(date, zone);
            var parts = HistoryPlanner.Split(day, aggregatedFrom, aggregatedTo, now);
            var raw = parts.Where(part => part.Source == HistoryPartSource.Raw).Select(part => part.Window).ToArray();
            var computed = TelemetryAggregation.Summarize(metric, policy, samples, raw, now).ToDictionary(item => item.Start);
            var combined = HistoryAggregate.Combine(day.Start, day.End, parts.Select(part => part.Source == HistoryPartSource.Hourly
                ? hourly[part.Window.Start] : computed[part.Window.Start]));
            var direct = TelemetryAggregation.Summarize(metric, policy, samples, [day], now).Single();

            Assert.DoesNotContain(parts, part => part.Source == HistoryPartSource.NotAggregated);
            AssertClose(direct, combined);
        }
    }

    private static void AssertClose(HistoryAggregate expected, HistoryAggregate actual)
    {
        Assert.Equal(expected.SampleCount, actual.SampleCount);
        Assert.Equal(expected.TransitionCount, actual.TransitionCount);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.FirstObservedAt, actual.FirstObservedAt);
        Assert.Equal(expected.LastObservedAt, actual.LastObservedAt);
        Assert.Equal(expected.LastValue, actual.LastValue);
        Assert.Equal(expected.Minimum, actual.Minimum);
        Assert.Equal(expected.Maximum, actual.Maximum);
        Close(expected.CoveredSeconds, actual.CoveredSeconds);
        Close(expected.TimeWeightedAverage, actual.TimeWeightedAverage);
        Close(expected.ValueSum, actual.ValueSum);
        Close(expected.OnSeconds, actual.OnSeconds);
        Close(expected.MeasuredWh, actual.MeasuredWh);
        Close(expected.EstimatedPositiveWh, actual.EstimatedPositiveWh);
        Close(expected.EstimatedNegativeWh, actual.EstimatedNegativeWh);

        static void Close(double? left, double? right)
        {
            Assert.Equal(left is null, right is null);
            if (left is { } a && right is { } b) Assert.True(Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(a)), $"{a} != {b}");
        }
    }

    /// <summary>Deterministic irregular stream with gaps longer than the hold, resets and changing values.</summary>
    private static List<RawObservation> Series(Metric metric, DateTime start, TimeSpan length)
    {
        var random = new Random(7);
        var samples = new List<RawObservation>();
        var at = start;
        double counter = 0;
        var day = DateOnly.FromDateTime(start);
        while (at < start + length)
        {
            var step = random.Next(10) == 0 ? TimeSpan.FromMinutes(25 + random.Next(60)) : TimeSpan.FromSeconds(60 + random.Next(540));
            at += step;
            var kind = TelemetryAggregation.Kind(metric);
            if (kind == MetricAggregation.Boolean) samples.Add(B(at, random.Next(3) > 0));
            else if (kind == MetricAggregation.DailyResetCounter)
            {
                var local = CalendarDays.LocalDate(at, Zone("Asia/Kathmandu"));
                if (local != day) { counter = 0; day = local; }
                counter += random.NextDouble() * 0.05;
                samples.Add(N(at, Math.Round(counter, 3)));
            }
            else if (kind == MetricAggregation.CumulativeCounter)
            {
                counter = random.Next(200) == 0 ? 0 : counter + random.NextDouble() * 0.05;
                samples.Add(N(at, Math.Round(counter, 3)));
            }
            else samples.Add(N(at, Math.Round(random.NextDouble() * 2000 - (metric == Metric.BatteryPower ? 1000 : 0), 2)));
        }
        return samples;
    }

    private static HistoryAggregate Single(Metric metric, int holdMinutes, RawObservation[] samples, TimeWindow window) =>
        TelemetryAggregation.Summarize(metric, Hold(holdMinutes), samples, [window], Later).Single();

    private static AggregationPolicy Hold(int minutes) => new(TimeSpan.FromMinutes(minutes), TimeZoneInfo.Utc);
    private static DateTime T(int hour, int minute = 0) => new(2026, 6, 1, hour, minute, 0, DateTimeKind.Utc);
    private static TimeWindow Hour(int hour) => new(T(hour), T(hour).AddHours(1));
    private static RawObservation N(DateTime at, double value) => new(at, value, null);
    private static RawObservation B(DateTime at, bool value) => new(at, null, value);
    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
}
