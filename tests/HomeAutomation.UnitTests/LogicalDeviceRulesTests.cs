using HomeAutomation.Domain;

namespace HomeAutomation.UnitTests;

public sealed class LogicalDeviceRulesTests
{
    private static readonly DateTime Cutover = new(2026, 11, 26, 14, 37, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HalfOpenIntervalsTouchingAtTheReplacementInstantDoNotOverlap()
    {
        var start = Cutover.AddDays(-1);
        Assert.False(LogicalDeviceRules.Overlaps(start, Cutover, Cutover, null));
        Assert.True(LogicalDeviceRules.Overlaps(start, Cutover.AddTicks(10), Cutover, null));
        Assert.True(LogicalDeviceRules.Overlaps(start, null, Cutover, null));
        Assert.False(LogicalDeviceRules.Overlaps(Cutover, null, start, Cutover));
    }

    [Fact]
    public void WholeDeviceAssignmentsConflictWithAnyEndpointOfTheSameDevice()
    {
        Assert.True(LogicalDeviceRules.SameSource(5, null, 5, 7));
        Assert.True(LogicalDeviceRules.SameSource(5, 7, 5, 7));
        Assert.False(LogicalDeviceRules.SameSource(5, 7, 5, 8));
        Assert.False(LogicalDeviceRules.SameSource(5, null, 6, null));
    }

    [Fact]
    public void CapabilityComparisonFollowsCanonicalCodesOnly()
    {
        var added = LogicalDeviceRules.Compare([Capability.TemperatureMeasurement],
            [Capability.TemperatureMeasurement, Capability.HumidityMeasurement]);
        Assert.True(added.Compatible);
        Assert.Equal([Capability.HumidityMeasurement], added.Added);
        Assert.Empty(added.Missing);

        var partial = LogicalDeviceRules.Compare([Capability.TemperatureMeasurement, Capability.HumidityMeasurement],
            [Capability.TemperatureMeasurement]);
        Assert.True(partial.Compatible);
        Assert.Equal([Capability.HumidityMeasurement], partial.Missing);

        var electrical = LogicalDeviceRules.Compare([Capability.PowerMeasurement],
            [Capability.PowerMeasurement, Capability.EnergyMeasurement]);
        Assert.Equal([Capability.EnergyMeasurement], electrical.Added);

        Assert.False(LogicalDeviceRules.Compare([Capability.TemperatureMeasurement], [Capability.PowerMeasurement]).Compatible);
        Assert.False(LogicalDeviceRules.Compare([Capability.FaultStatus, Capability.SolarProduction],
            [Capability.FaultStatus, Capability.Switch]).Compatible);
    }

    [Fact]
    public void EndpointSelectionIsRequiredOnlyForRepeatedMeasurements()
    {
        Assert.False(LogicalDeviceRules.RequiresEndpoint([[Capability.TemperatureMeasurement], [Capability.HumidityMeasurement]]));
        Assert.True(LogicalDeviceRules.RequiresEndpoint([[Capability.Switch], [Capability.Switch]]));
        Assert.False(LogicalDeviceRules.RequiresEndpoint([[Capability.FaultStatus], [Capability.FaultStatus, Capability.Switch]]));
    }

    [Fact]
    public void ChannelKeysAreNormalizedAndUnique()
    {
        var taken = new HashSet<string>();
        Assert.Equal("switch_1", LogicalDeviceRules.ChannelFor("Switch_1", taken));
        Assert.Equal("switch_1-2", LogicalDeviceRules.ChannelFor("SWITCH_1", taken));
        Assert.Equal("switch-1", LogicalDeviceRules.ChannelFor("switch 1", taken));
        Assert.Equal("endpoint", LogicalDeviceRules.ChannelFor("***", taken));
        Assert.True(LogicalDeviceRules.ValidChannel("main"));
        Assert.False(LogicalDeviceRules.ValidChannel("Main"));
        Assert.False(LogicalDeviceRules.ValidChannel("-main"));
    }

    [Fact]
    public void SameHourReplacementSplitsTheHourAtTheExactInstant()
    {
        var hour = new TimeWindow(Cutover.AddMinutes(-37), Cutover.AddMinutes(23));
        var hold = TimeSpan.FromMinutes(15);
        var before = SegmentPlanner.Split(hour, new(Cutover.AddDays(-5), Cutover), Cutover.AddDays(-5), Later, hold, TimeSpan.Zero, Later);
        var after = SegmentPlanner.Split(hour, new(Cutover, LogicalDeviceRules.OpenEnd), Cutover.AddDays(-5), Later, hold, TimeSpan.Zero, Later);

        Assert.Equal([new HistoryPart(new(hour.Start, Cutover), HistoryPartSource.Raw)], before);
        Assert.Equal(Cutover, after[0].Window.Start);
        Assert.Equal(hour.End, after[^1].Window.End);
        Assert.All(after, part => Assert.Equal(HistoryPartSource.Raw, part.Source));
    }

    [Fact]
    public void HourlyRollupsAreReusedOnlyAwayFromAssignmentBoundaries()
    {
        var segment = new TimeWindow(Cutover, Cutover.AddDays(2));
        var day = new TimeWindow(Cutover.Date.AddDays(1), Cutover.Date.AddDays(2));
        var parts = SegmentPlanner.Split(day, segment, Cutover.AddDays(-10), Later, TimeSpan.FromHours(26), TimeSpan.FromMinutes(15), Later);

        // The counter lookback (26 h) ends at 16:37 the next day; the rest of that hour is partial and also raw.
        Assert.Equal(HistoryPartSource.Raw, parts[0].Source);
        Assert.Equal(Cutover.AddHours(26), parts[0].Window.End);
        Assert.Equal(new HistoryPart(new(Cutover.AddHours(26), Cutover.AddHours(26).AddMinutes(23)), HistoryPartSource.Raw), parts[1]);
        Assert.All(parts.Skip(2), part => Assert.Equal(HistoryPartSource.Hourly, part.Source));
        Assert.Equal(day.End, parts[^1].Window.End);
    }

    [Theory]
    [InlineData(Metric.Temperature)]
    [InlineData(Metric.ActivePower)]
    [InlineData(Metric.SwitchOn)]
    [InlineData(Metric.Energy)]
    public void LogicalDayFromRollupsAndRawEdgesEqualsEachDevicesOwnObservations(Metric metric)
    {
        var start = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(12), TimeZoneInfo.Utc);
        var oldDevice = Stream(metric, start, 3, 11, 100);
        var newDevice = Stream(metric, start, 3, 29, 2);
        var segments = new[] { new TimeWindow(start, Cutover), new TimeWindow(Cutover, LogicalDeviceRules.OpenEnd) };
        var streams = new[] { oldDevice, newDevice };
        var hours = Enumerable.Range(0, 72).Select(index => new TimeWindow(start.AddHours(index), start.AddHours(index + 1))).ToArray();
        var day = new TimeWindow(Cutover.Date, Cutover.Date.AddDays(1));
        var now = start.AddDays(3);

        var combined = new List<HistoryAggregate>();
        var direct = new List<HistoryAggregate>();
        for (var index = 0; index < 2; index++)
        {
            var hourly = TelemetryAggregation.Summarize(metric, policy, streams[index], hours, now).ToDictionary(item => item.Start);
            var parts = SegmentPlanner.Split(day, segments[index], start, start.AddDays(3), TelemetryAggregation.Lookback(metric, policy),
                TelemetryAggregation.Lookahead(metric, policy), now);
            var inside = streams[index].Where(item => item.At >= segments[index].Start && item.At < segments[index].End).ToList();
            var raw = parts.Where(part => part.Source == HistoryPartSource.Raw).Select(part => part.Window).ToArray();
            var computed = TelemetryAggregation.Summarize(metric, policy, inside, raw, now).ToDictionary(item => item.Start);
            combined.AddRange(parts.Select(part => part.Source == HistoryPartSource.Hourly ? hourly[part.Window.Start] : computed[part.Window.Start]));
            var clipped = new TimeWindow(day.Start > segments[index].Start ? day.Start : segments[index].Start,
                day.End < segments[index].End ? day.End : segments[index].End);
            direct.Add(TelemetryAggregation.Summarize(metric, policy, inside, [clipped], now).Single());
        }

        var expected = HistoryAggregate.Combine(day.Start, day.End, direct);
        var actual = HistoryAggregate.Combine(day.Start, day.End, combined);
        Assert.Equal(expected.SampleCount, actual.SampleCount);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.TransitionCount, actual.TransitionCount);
        Close(expected.CoveredSeconds, actual.CoveredSeconds);
        Close(expected.TimeWeightedAverage, actual.TimeWeightedAverage);
        Close(expected.OnSeconds, actual.OnSeconds);
        Close(expected.MeasuredWh, actual.MeasuredWh);
        Close(expected.EstimatedPositiveWh, actual.EstimatedPositiveWh);
        if (metric == Metric.Energy) Assert.False(actual.Flags.HasFlag(AggregateFlags.CounterReset));
    }

    [Fact]
    public void ObservationsOutsideAnAssignmentNeverCoverItsTime()
    {
        var policy = new AggregationPolicy(TimeSpan.FromMinutes(15), TimeZoneInfo.Utc);
        RawObservation[] replacement = [new(Cutover.AddMinutes(-2), 21, null), new(Cutover.AddMinutes(10), 22, null)];
        var inside = replacement.Where(item => item.At >= Cutover).ToArray();
        var window = new TimeWindow(Cutover, Cutover.AddMinutes(23));

        var strict = TelemetryAggregation.Summarize(Metric.Temperature, policy, inside, [window], Later).Single();
        Assert.Equal(13 * 60, strict.CoveredSeconds, 6);
        Assert.Equal(22, strict.Minimum);
    }

    private static void Close(double? expected, double? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is { } a && actual is { } b) Assert.True(Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(a)), $"{a} != {b}");
    }

    private static List<RawObservation> Stream(Metric metric, DateTime start, int days, int seed, double counterStart)
    {
        var random = new Random(seed);
        var samples = new List<RawObservation>();
        var counter = counterStart;
        for (var at = start.AddSeconds(random.Next(300)); at < start.AddDays(days); at = at.AddSeconds(60 + random.Next(600)))
        {
            if (random.Next(25) == 0) at = at.AddMinutes(40);
            counter += random.NextDouble() * 0.03;
            samples.Add(TelemetryAggregation.Kind(metric) switch
            {
                MetricAggregation.Boolean => new(at, null, random.Next(3) > 0),
                MetricAggregation.CumulativeCounter => new(at, Math.Round(counter, 3), null),
                _ => new(at, Math.Round(random.NextDouble() * 1500, 2), null)
            });
        }
        return samples;
    }
}
