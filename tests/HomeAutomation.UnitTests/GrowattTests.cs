using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Growatt;

namespace HomeAutomation.UnitTests;

public sealed class GrowattTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static GrowattOptions Options() => new()
    {
        Enabled = true, SiteKey = "test-site", MasterId = "fixture-M", SlaveId = "fixture-S", BatterySourceId = "fixture-M",
        SumIndependentPv = true, MasterReportsWholeSiteLoad = true
    };

    private static GrowattInverter Report(string id, DateTimeOffset at, double pv, double soc,
        Connectivity connectivity = Connectivity.Online) =>
        new(id, at, connectivity,
            [Sample.Numeric(Metric.PvPower, at, pv), Sample.Numeric(Metric.HouseholdPower, at, 750)],
            [Sample.Numeric(Metric.BatterySoc, at, soc), Sample.Numeric(Metric.Energy, at, 5)]);

    [Fact]
    public void FixtureMapsStableRolesAndUsesExactlyOneSharedBatteryAndSiteLoad()
    {
        var options = Options();
        var mapper = new GrowattMapper(options, new FixedClock());
        var at = Now.AddSeconds(-20);
        var snapshot = new GrowattSnapshot([Report(options.SlaveId, at, 1100, 38), Report(options.MasterId, at, 1800, 54),
            Report("unmapped", at, 5000, 90)], []);
        var (devices, batch) = mapper.Map(snapshot);

        Assert.Equal(options.MasterId, Assert.Single(devices, item => item.InstallationRole == "master").ExternalId);
        Assert.Equal(options.SlaveId, Assert.Single(devices, item => item.InstallationRole == "slave").ExternalId);
        Assert.Single(devices, item => item.Kind == DeviceKind.Battery);
        Assert.DoesNotContain(devices, item => item.ExternalId.StartsWith("simulator:", StringComparison.Ordinal));
        var battery = Assert.Single(batch.Devices, item => item.ExternalId == GrowattMapper.SharedBatteryId);
        Assert.Equal(54, battery.Endpoints[0].Samples.Single(item => item.Metric == Metric.BatterySoc).Number);
        Assert.Equal(5, battery.Endpoints[0].Samples.Single(item => item.Metric == Metric.Energy).Number);
        Assert.Equal(750, batch.Installation.Single(item => item.Metric == Metric.HouseholdPower).Number);
        Assert.Equal(2900, batch.Installation.Single(item => item.Metric == Metric.PvPower).Number);
        Assert.Equal(Now, batch.ObservedAt);
        Assert.Equal(at, batch.Devices.Single(item => item.ExternalId == options.MasterId).ObservedAt);
    }

    [Fact]
    public void AuthoritativeTotalWinsAndGridAvailabilityRequiresExplicitSiteEvidence()
    {
        var options = Options();
        var mapper = new GrowattMapper(options, new FixedClock());
        var report = Report(options.MasterId, Now, 600, 30) with
        {
            InverterReadings = [Sample.Boolean(Metric.GridAvailable, Now, true)]
        };
        var result = mapper.Map(new GrowattSnapshot([report, Report(options.SlaveId, Now, 500, 33)],
            [Sample.Numeric(Metric.PvPower, Now, 1234)]));
        Assert.Equal(1234, Assert.Single(result.Batch.Installation).Number);
        Assert.DoesNotContain(result.Batch.Installation, sample => sample.Metric == Metric.GridAvailable);
    }

    [Fact]
    public void StaleOrIncompleteSamplesCannotCreateDerivedTotalsOrFallbackBattery()
    {
        var options = Options();
        var mapper = new GrowattMapper(options, new FixedClock());
        var stale = Now.AddSeconds(-options.FreshnessSeconds - 1);
        var incomplete = mapper.Map(new([Report(options.SlaveId, Now, 300, 33)], []));
        Assert.Empty(incomplete.Batch.Installation);
        Assert.DoesNotContain(incomplete.Batch.Devices, item => item.ExternalId == GrowattMapper.SharedBatteryId);

        var complete = mapper.Map(new([Report(options.MasterId, stale, 500, 50), Report(options.SlaveId, Now, 300, 33)], []));
        Assert.Empty(complete.Batch.Installation);
        var retained = complete.Batch.Devices.Single(item => item.ExternalId == options.MasterId).Endpoints[0].Samples[0];
        Assert.Equal(ReadingQuality.Stale, Telemetry.Quality(retained.ObservedAt, Now, TimeSpan.FromSeconds(options.FreshnessSeconds)));
    }

    [Fact]
    public void DuplicateAndFutureReportsAreRejectedAndMappingMustBeExplicit()
    {
        var options = Options();
        var mapper = new GrowattMapper(options, new FixedClock());
        var report = Report(options.MasterId, Now, 200, 51);
        Assert.Equal(GrowattFailure.InvalidData, Assert.Throws<GrowattReadException>(() => mapper.Map(new([report, report], []))).Failure);
        Assert.Throws<GrowattReadException>(() => mapper.Map(new([report with { ObservedAt = Now.AddMinutes(1) }], [])));
        Assert.True(options.HasValidMapping());
        options.SlaveId = options.MasterId;
        Assert.False(options.HasValidMapping());
        Assert.False(new GrowattOptions { PollSeconds = 0 }.HasValidCadence());
    }

    [Fact]
    public async Task PollsRespectThrottleTimeoutAndCircuitDeferralThenRecover()
    {
        var options = Options();
        var clock = new FixedClock();
        var poller = new GrowattPoller(options, new GrowattMapper(options, clock), clock);
        var store = new RecordingStore();
        var responses = new Queue<Func<GrowattSnapshot>>(
        [
            () => throw new GrowattReadException(GrowattFailure.Throttled, TimeSpan.FromMinutes(45)),
            () => throw new TimeoutException(),
            () => throw new HttpRequestException(),
            () => new GrowattSnapshot([Report(options.MasterId, Now, 800, 46)], [])
        ]);
        var source = new FixtureSource(responses);

        var throttled = await poller.PollAsync(source, store, default);
        Assert.Equal(TimeSpan.FromMinutes(45), throttled.NextDelay);
        Assert.Equal(GrowattFailure.Throttled, throttled.Failure);
        var timeout = await poller.PollAsync(source, store, default);
        Assert.Equal(GrowattFailure.Timeout, timeout.Failure);
        var disconnected = await poller.PollAsync(source, store, default);
        Assert.Equal(GrowattFailure.Unavailable, disconnected.Failure);
        Assert.True(disconnected.CircuitOpen);
        Assert.True(disconnected.NextDelay > timeout.NextDelay);
        Assert.Equal(0, store.Saves);

        var recovered = await poller.PollAsync(source, store, default);
        Assert.Equal(42, recovered.PropertyId);
        Assert.Equal(TimeSpan.FromSeconds(options.PollSeconds), recovered.NextDelay);
        Assert.False(recovered.CircuitOpen);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutPersisting()
    {
        var options = Options();
        var clock = new FixedClock();
        var poller = new GrowattPoller(options, new GrowattMapper(options, clock), clock);
        var store = new RecordingStore();
        var source = new FixtureSource(new Queue<Func<GrowattSnapshot>>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            poller.PollAsync(source, store, new CancellationToken(canceled: true)));
        Assert.Equal(0, store.Saves);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureSource(Queue<Func<GrowattSnapshot>> responses) : IGrowattReadSource
    {
        public Task<GrowattSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responses.Dequeue()());
        }
    }

    private sealed class RecordingStore : IGrowattTelemetryStore
    {
        public int Saves { get; private set; }
        public Task<long> StoreAsync(string siteKey, IReadOnlyList<DiscoveredDevice> devices, ObservationBatch batch,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-site", siteKey);
            Saves++;
            return Task.FromResult(42L);
        }
    }
}
