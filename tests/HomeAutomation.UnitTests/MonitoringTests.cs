using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Simulator;

namespace HomeAutomation.UnitTests;

public sealed class MonitoringTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EndpointRequiresDistinctCapabilitiesAndProvidesReadOnlyAccess()
    {
        Assert.Throws<ArgumentException>(() => new DeviceEndpoint("", "Main", [Capability.Switch]));
        Assert.Throws<ArgumentException>(() => new DeviceEndpoint("main", "Main", [Capability.Switch, Capability.Switch]));

        var endpoint = new DeviceEndpoint("main", "Main", [Capability.Switch]);
        Assert.Equal(Access.ReadOnly, endpoint.AccessFor(Capability.Switch));
        Assert.Equal(Access.Unsupported, endpoint.AccessFor(Capability.VoltageMeasurement));
    }

    [Fact]
    public void InvalidReadingsCannotBecomeNumericTelemetry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.Numeric(Metric.BatterySoc, Noon, 103));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.Numeric(Metric.PvPower, Noon, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.Numeric(Metric.SwitchOn, Noon, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample.Numeric(Metric.Energy, Noon, -1));
        Assert.Equal(-500, Sample.Numeric(Metric.BatteryPower, Noon, -500).Number);
    }

    [Theory]
    [InlineData(45, ReadingQuality.Fresh)]
    [InlineData(46, ReadingQuality.Stale)]
    [InlineData(-1, ReadingQuality.Stale)]
    public void FreshnessUsesObservationTime(int age, ReadingQuality expected)
    {
        Assert.Equal(expected, Telemetry.Quality(Noon.AddSeconds(-age), Noon, TimeSpan.FromSeconds(45)));
        Assert.Equal(ReadingQuality.Missing, Telemetry.Quality(null, Noon, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void ParallelAggregationAddsIndependentPvButReadsWholeSiteLoadOnce()
    {
        var expected = new HashSet<string> { "master", "slave" };
        InverterFlow master = new("master", 2000, 720);
        InverterFlow slave = new("slave", 1800, 720);

        Assert.Equal(new InstallationFlow(3800, 720), SolarInstallation.Aggregate("master", expected, [master, slave]));
        Assert.Equal(new InstallationFlow(null, 720), SolarInstallation.Aggregate("master", expected, [master]));
        Assert.Equal(new InstallationFlow(null, null), SolarInstallation.Aggregate("master", expected, [master, master, slave]));
    }

    [Fact]
    public async Task SimulatorDiscoversSixDevicesAndProducesRepeatableReadings()
    {
        var provider = new SimulatorProvider(new FixedClock(Noon), new SimulatorOptions { Scenario = Scenario.Normal });
        var inventory = await provider.DiscoverAsync(default);
        Assert.Equal(6, inventory.Count);
        Assert.Single(inventory, device => device.Kind == DeviceKind.Battery);
        Assert.Equal(2, inventory.Count(device => device.Kind == DeviceKind.Inverter));

        var batch = await provider.ReadAsync(default);
        Assert.Equal(JsonSerializer.Serialize(batch), JsonSerializer.Serialize(await provider.ReadAsync(default)));
        var master = batch.Devices.Single(device => device.ExternalId == "growatt-master");
        Assert.Equal(master.Endpoints[0].Samples.Single(sample => sample.Metric == Metric.HouseholdPower).Number,
            batch.Installation.Single(sample => sample.Metric == Metric.HouseholdPower).Number);
        var pv = batch.Devices.Where(device => device.ExternalId.StartsWith("growatt-", StringComparison.Ordinal))
            .SelectMany(device => device.Endpoints[0].Samples).Where(sample => sample.Metric == Metric.PvPower).Sum(sample => sample.Number);
        Assert.Equal(pv, batch.Installation.Single(sample => sample.Metric == Metric.PvPower).Number);
    }

    [Fact]
    public async Task ExplicitScenariosSeparateOfflineStaleFaultAndGridLoss()
    {
        var clock = new FixedClock(Noon);
        var offline = await new SimulatorProvider(clock, new SimulatorOptions { Scenario = Scenario.Offline }).ReadAsync(default);
        var ac = Assert.Single(offline.Devices, device => device.Connectivity == Connectivity.Offline);
        Assert.Empty(ac.Endpoints);
        Assert.True(offline.Installation.Single(sample => sample.Metric == Metric.GridAvailable).Flag);

        var stale = await new SimulatorProvider(clock, new SimulatorOptions { Scenario = Scenario.Stale }).ReadAsync(default);
        var temperature = stale.Devices.Single(device => device.ExternalId == "temperature");
        Assert.Equal(Connectivity.Online, temperature.Connectivity);
        Assert.All(temperature.Endpoints[0].Samples, sample => Assert.True(sample.ObservedAt < Noon.AddSeconds(-45)));

        var fault = await new SimulatorProvider(clock, new SimulatorOptions { Scenario = Scenario.Fault }).ReadAsync(default);
        Assert.Equal("SIM_METER_FAULT", fault.Devices.Single(device => device.ExternalId == "sonoff").FaultCode);

        var outage = await new SimulatorProvider(clock, new SimulatorOptions { Scenario = Scenario.Outage }).ReadAsync(default);
        Assert.False(outage.Installation.Single(sample => sample.Metric == Metric.GridAvailable).Flag);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
