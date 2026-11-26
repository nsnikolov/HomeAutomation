using System.Collections.Concurrent;
using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.Ewelink;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeAutomation.UnitTests;

/// <summary>Milestone 7D: background electrical collection without any browser, with mocked provider replies.</summary>
public sealed class EwelinkCollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    private const string Owner = "owner";

    private static EwelinkOptions Collecting(int seconds = 900) => new()
    {
        EnableWebSocket = true, ContinuousElectricalCollection = true, ElectricalCollectionSeconds = seconds
    };

    [Fact]
    public void CollectionNeedsWebSocketAndABoundedIntervalAndDefaultsOff()
    {
        Assert.False(new EwelinkOptions().ElectricalCollectionConfigured);
        Assert.Equal(900, new EwelinkOptions().ElectricalCollectionSeconds);
        Assert.False(new EwelinkOptions { ContinuousElectricalCollection = true }.ElectricalCollectionConfigured);
        Assert.False(Collecting(299).ElectricalCollectionConfigured);
        Assert.False(Collecting(3601).ElectricalCollectionConfigured);
        Assert.True(Collecting(300).ElectricalCollectionConfigured);
        Assert.False(Collecting().LiveElectricalConfigured);
    }

    [Fact]
    public void ScheduledWindowsRunWithoutAnyViewerAfterAStaggeredStart()
    {
        var clock = new ManualClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting());
        Assert.Empty(state.ViewedProperties(Owner));
        Assert.False(state.IsDue("meter", viewed: false));
        Assert.False(state.TryStart(Owner, 1, "meter"));
        var stagger = EwelinkElectricalReporting.Stagger("meter", TimeSpan.FromSeconds(900));
        clock.Advance(EwelinkElectricalReporting.StartupDelay + stagger - TimeSpan.FromSeconds(1));
        Assert.False(state.IsDue("meter", false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(state.IsDue("meter", false));
        Assert.True(state.TryStart(Owner, 1, "meter"));
        Assert.False(state.TryStart(Owner, 1, "meter"));
        state.Accepted("meter");
        Assert.Equal("awaitingReport", state.Status(Meter("meter")).Status);

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("scheduledCollection", state.Status(Meter("meter")).Status);
        clock.Advance(TimeSpan.FromSeconds(900 - 62));
        Assert.False(state.IsDue("meter", false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(state.TryStart(Owner, 1, "meter"));

        // Without the setting, an unviewed meter is never activated: the pre-7D behaviour.
        var viewerOnly = new EwelinkElectricalReporting(clock);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(viewerOnly.IsDue("meter", false));
        Assert.False(viewerOnly.TryStart(Owner, 1, "meter"));
        Assert.Equal("waitingForActivation", viewerOnly.Status(Meter("meter")).Status);
    }

    [Fact]
    public void RestartStaggerIsStableBoundedAndSpread()
    {
        var interval = TimeSpan.FromSeconds(900);
        var offsets = Enumerable.Range(0, 20).Select(index => EwelinkElectricalReporting.Stagger($"meter-{index}", interval)).ToArray();
        Assert.All(offsets, offset => Assert.InRange(offset, TimeSpan.Zero, EwelinkElectricalReporting.MaximumStagger));
        Assert.True(offsets.Distinct().Count() > 10);
        Assert.Equal(offsets[3], EwelinkElectricalReporting.Stagger("meter-3", interval));
        Assert.InRange(EwelinkElectricalReporting.Stagger("meter-3", TimeSpan.FromSeconds(30)), TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ManyBrowserTabsShareOneActivationAndAViewerSatisfiesTheSchedule()
    {
        var clock = new ManualClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting());
        var tabs = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        void Renew(bool active)
        {
            foreach (var tab in tabs) Assert.True(state.View(Owner, 1, tab, active));
        }
        Renew(true);
        Assert.True(state.TryStart(Owner, 1, "meter"));
        Assert.False(state.TryStart(Owner, 1, "meter"));
        state.Accepted("meter");
        for (var second = 10; second <= 40; second += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Renew(true);
            Assert.False(state.TryStart(Owner, 1, "meter"));
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        Renew(true);
        Assert.True(state.TryStart(Owner, 1, "meter"));

        // After the viewers leave, the next background window comes one interval after the last viewer window.
        Renew(false);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(state.ViewedProperties(Owner));
        clock.Advance(TimeSpan.FromSeconds(840));
        Assert.False(state.IsDue("meter", false));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(state.IsDue("meter", false));
    }

    [Fact]
    public void AnUnresponsiveMeterBacksOffAloneWithBoundedJitter()
    {
        var clock = new ManualClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting());
        Assert.False(state.IsDue("offline", false));
        Assert.False(state.IsDue("online", false));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(state.IsDue("offline", false));
        Assert.True(state.IsDue("online", false));
        Assert.True(state.TryStart(Owner, 1, "offline"));
        state.Failed("offline", new("deviceUnavailable", 504));
        Assert.Equal(new EwelinkElectricalStatus("deviceUnavailable", 504), state.Status(Meter("offline")));
        Assert.False(state.IsDue("offline", false));
        Assert.False(state.IsDue("offline", viewed: true));
        Assert.True(state.TryStart(Owner, 1, "online"));
        Assert.False(state.IsStopped);

        for (var failures = 1; failures <= 12; failures++)
        {
            var delay = EwelinkElectricalReporting.Backoff("offline", failures);
            var expected = TimeSpan.FromMinutes(Math.Pow(2, failures - 1));
            if (expected >= EwelinkElectricalReporting.MaximumBackoff) Assert.Equal(EwelinkElectricalReporting.MaximumBackoff, delay);
            else Assert.InRange(delay, expected, expected * 1.1 > EwelinkElectricalReporting.MaximumBackoff
                ? EwelinkElectricalReporting.MaximumBackoff : expected * 1.1);
        }
        Assert.Equal(EwelinkElectricalReporting.Backoff("offline", 3), EwelinkElectricalReporting.Backoff("offline", 3));

        clock.Advance(EwelinkElectricalReporting.Backoff("offline", 1));
        Assert.True(state.TryStart(Owner, 1, "offline"));
        state.Accepted("offline");
        Assert.Equal("awaitingReport", state.Status(Meter("offline")).Status);
    }

    [Fact]
    public void UnconfirmedStreakSpansMetersAndResetsOnAcceptance()
    {
        var state = new EwelinkElectricalReporting(new ManualClock(Now), Collecting());
        Assert.Equal(1, state.Failed("a", new("activationUnconfirmed")));
        Assert.Equal(2, state.Failed("b", new("activationUnconfirmed")));
        Assert.Equal(2, state.Failed("c", new("deviceUnavailable", 504)));
        state.Accepted("d");
        Assert.Equal(1, state.Failed("a", new("activationUnconfirmed")));
        state.ResetUnconfirmed();
        Assert.Equal(1, state.Failed("b", new("activationUnconfirmed")));
    }

    [Theory]
    [InlineData(5, 5, 900)]
    [InlineData(5, 0, 900)]
    [InlineData(5, 10, 1594)]
    [InlineData(5, 20, 3188)]
    [InlineData(5, 50, 7970)]
    [InlineData(60, 50, 3239)]
    public void BudgetStretchesTheIntervalButNeverShortensIt(int pollMinutes, int meters, int expectedSeconds)
    {
        Assert.Equal(1406, EwelinkCollectionBudget.DailyElectricalShare);
        var effective = EwelinkCollectionBudget.EffectiveInterval(TimeSpan.FromSeconds(900), meters, pollMinutes);
        Assert.InRange(effective.TotalSeconds, expectedSeconds, expectedSeconds + 1);
        Assert.True(EwelinkCollectionBudget.ActivationsPerDay(meters, effective) <=
            Math.Max(0, EwelinkCollectionBudget.ElectricalDailyAllowance(pollMinutes)));
        Assert.Equal(TimeSpan.FromHours(1), EwelinkCollectionBudget.EffectiveInterval(TimeSpan.FromHours(1), 1, pollMinutes));
    }

    [Fact]
    public void ProtocolEligibilityFollowsUiidNotModel()
    {
        Assert.True(EwelinkElectricalReporting.IsMeter(Meter("pow") with { Model = "POWR2", Uiid = 32 }));
        Assert.True(EwelinkElectricalReporting.IsMeter(Meter("pow") with { Model = "POW", Uiid = 5 }));
        Assert.False(EwelinkElectricalReporting.IsMeter(Meter("shared") with { Shared = true }));
        Assert.False(EwelinkElectricalReporting.IsMeter(Meter("thermostat") with { Model = "THR320D", Uiid = 181 }));
        Assert.False(EwelinkElectricalReporting.IsMeter(Meter("unknown") with { Uiid = null }));
        // Relay writes keep their separately verified, narrower contract.
        Assert.False((Meter("pow") with { Model = "POWR2" }).SupportsRelayWrites);
    }

    [Fact]
    public void ReceiptTimedReportsKeepGenuineRepeatsAndRejectRepeatedDelivery()
    {
        using var frame = JsonDocument.Parse("""{"power":"120.50","voltage":"230.10","current":"0.52"}""");
        using var change = JsonDocument.Parse("""{"power":"180.00","voltage":"230.10","current":"0.78"}""");
        var trace = new EwelinkSocketTrace();
        (EwelinkDevice Device, IReadOnlyList<Sample> Samples) Apply(EwelinkDevice device, JsonDocument json, DateTimeOffset at) =>
            EwelinkMapping.MergeEvent(device, json.RootElement, at, trace, EwelinkSocketTimeSource.ServerReceived);

        var (first, samples) = Apply(Meter("meter"), frame, Now);
        Assert.Equal(3, samples.Count);
        Assert.All(samples, sample => Assert.True(sample.ServerReceivedTimestamp));

        // The same frame again inside one activation window is a repeated delivery, not a new observation.
        var (repeated, none) = Apply(first, frame, Now.AddSeconds(5));
        Assert.Empty(none);
        Assert.Contains("reject.serverReceivedDuplicate=3", trace.Drain(), StringComparison.Ordinal);

        // A real change keeps its unchanged voltage: the device sent a current report with all three fields.
        var (changed, kept) = Apply(repeated, change, Now.AddSeconds(20));
        Assert.Equal([Metric.ActivePower, Metric.Voltage, Metric.Current], kept.Select(sample => sample.Metric).ToArray());

        // The next background window can report a value equal to an earlier one; it is a current reading and is kept.
        var (_, later) = Apply(changed, change, Now.AddMinutes(15));
        Assert.Equal(3, later.Count);
        Assert.All(later, sample => Assert.Equal(Now.AddMinutes(15), sample.ObservedAt));

        // Invalid values are never stored and never become zero.
        using var invalid = JsonDocument.Parse("""{"power":"NaN","voltage":"-1","current":"Infinity"}""");
        Assert.Empty(Apply(changed, invalid, Now.AddMinutes(16)).Samples);
        Assert.Empty(Apply(changed with { Connectivity = Connectivity.Offline }, change, Now.AddMinutes(17)).Samples);
        Assert.Empty(Apply(Meter("thermostat") with { Model = "THR320D", Uiid = 181 }, change, Now).Samples);
    }

    [Fact]
    public void VaultProfilesImportedBeforeCollectionSettingsStillLoadWithCollectionOff()
    {
        var options = new EwelinkOptions
        {
            AppId = "fixture-app", AppSecret = "fixture-ewelink-secret", OperatorEmail = "operator@example.test",
            RedirectUri = "https://example.test/api/v1/ewelink/callback", EnableWebSocket = true,
            ContinuousElectricalCollection = true, ElectricalCollectionSeconds = 1200
        };
        var snapshot = ProviderOptionMapping.Capture(options);
        var restored = Assert.IsType<EwelinkOptions>(ProviderOptionMapping.Restore("ewelink", snapshot, null));
        Assert.True(restored.ElectricalCollectionConfigured);
        Assert.Equal(1200, restored.ElectricalCollectionSeconds);

        // A snapshot written before Milestone 7D has neither setting.
        var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Operational.GetRawText())!;
        var settings = node["settings"]!.AsObject();
        Assert.True(settings.Remove("continuousElectricalCollection"));
        Assert.True(settings.Remove("electricalCollectionSeconds"));
        var legacy = new ProviderOptionSnapshot(snapshot.PollSeconds, snapshot.Secret, snapshot.Identifiers,
            JsonSerializer.SerializeToElement(node));
        var older = Assert.IsType<EwelinkOptions>(ProviderOptionMapping.Restore("ewelink", legacy, null));
        Assert.False(older.ContinuousElectricalCollection);
        Assert.Equal(900, older.ElectricalCollectionSeconds);
        Assert.True(older.EnableWebSocket);

        Assert.Throws<ProviderConfigurationException>(() =>
            ProviderOptionMapping.Capture(new EwelinkOptions
            {
                AppId = options.AppId, AppSecret = options.AppSecret, OperatorEmail = options.OperatorEmail,
                RedirectUri = options.RedirectUri, ElectricalCollectionSeconds = 60
            }));
    }

    [Fact]
    public async Task CollectorActivatesEveryEligibleMeterWithNoBrowserAndToleratesAnOfflineMeter()
    {
        var clock = new ScaledClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting());
        var stats = new EwelinkCollectionStats(clock);
        var host = new FixtureHost(state, ["m1", "m2", "m3"], id => id == "m2" ? 504 : 0);
        Prime(state, clock, host.Ids);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new EwelinkElectricalCollector(state, stats, clock, NullLogger.Instance).RunAsync(Owner, host, null!, stop.Token);
        await host.WaitForSendsAsync(3, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["m1", "m2", "m3"], host.Sent.Order().ToArray());
        Assert.False(state.IsStopped);
        Assert.Equal("deviceUnavailable", state.Status(Meter("m2")).Status);
        Assert.Equal("awaitingReport", state.Status(Meter("m1")).Status);
        Assert.Empty(state.ViewedProperties(Owner));
        var summary = stats.Drain(TimeSpan.FromMinutes(15)).Counters;
        Assert.Contains("activationAccepted=2", summary, StringComparison.Ordinal);
        Assert.Contains("deviceUnavailable=1", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectorSendsOneWindowPerIntervalRegardlessOfTime()
    {
        var clock = new ScaledClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting(300));
        var host = new FixtureHost(state, ["m1"], _ => 0);
        Prime(state, clock, host.Ids);
        var started = clock.GetUtcNow();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = new EwelinkElectricalCollector(state, new(clock), clock, NullLogger.Instance).RunAsync(Owner, host, null!, stop.Token);
        await host.WaitForSendsAsync(3, stop.Token);
        var elapsed = clock.GetUtcNow() - started;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        // Three windows need two full intervals of virtual time; the collector never renews an unviewed meter early.
        Assert.True(elapsed >= TimeSpan.FromSeconds(600), $"Three windows took only {elapsed}.");
        Assert.Equal(3, host.Sent.Count);
    }

    [Fact]
    public async Task ProviderRejectionStopsButThrottlingDefersUntilTheRetryTime()
    {
        var clock = new ScaledClock(Now);
        var rejected = new EwelinkElectricalReporting(clock, Collecting());
        var host = new FixtureHost(rejected, ["m1", "m2"], _ => 406);
        Prime(rejected, clock, host.Ids);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new EwelinkElectricalCollector(rejected, new(clock), clock, NullLogger.Instance).RunAsync(Owner, host, null!, stop.Token);
        await host.WaitForSendsAsync(1, stop.Token);
        while (!rejected.IsStopped) await Task.Delay(10, stop.Token);
        await Task.Delay(200, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(host.Sent);
        Assert.Equal(new EwelinkElectricalStatus("activationRejected", 406), rejected.Status(Meter("m2")));

        var throttledState = new EwelinkElectricalReporting(clock, Collecting());
        var retryAt = clock.GetUtcNow().AddMinutes(5);
        var throttled = new FixtureHost(throttledState, ["m1"], _ => 0) { Throttle = retryAt };
        Prime(throttledState, clock, throttled.Ids);
        using var again = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var deferred = new EwelinkElectricalCollector(throttledState, new(clock), clock, NullLogger.Instance)
            .RunAsync(Owner, throttled, null!, again.Token);
        await throttled.WaitForSendsAsync(1, again.Token);
        Assert.True(clock.GetUtcNow() >= retryAt, "The collector retried before the provider's retry time.");
        Assert.Equal(1, throttled.Throttled);
        await again.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deferred);
    }

    [Fact]
    public async Task LostAcknowledgementsBackOffPerMeterThenReconnectTheSocket()
    {
        var clock = new ScaledClock(Now);
        var state = new EwelinkElectricalReporting(clock, Collecting());
        var host = new FixtureHost(state, ["m1", "m2", "m3", "m4"], _ => null);
        Prime(state, clock, host.Ids);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<EwelinkFailure>(() =>
            new EwelinkElectricalCollector(state, new(clock), clock, NullLogger.Instance).RunAsync(Owner, host, null!, stop.Token));
        Assert.Equal("activationUnconfirmed", failure.Status);
        Assert.Equal(EwelinkElectricalReporting.UnconfirmedLimit, host.Sent.Count);
        Assert.False(state.IsStopped);
        Assert.Equal("activationUnconfirmed", state.Status(Meter(host.Sent.First())).Status);

        // Viewer-only mode keeps the original conservative behaviour: an unconfirmed command stops activation.
        var viewerOnly = new EwelinkElectricalReporting(clock);
        Assert.True(viewerOnly.View(Owner, 1, Guid.NewGuid(), true));
        var viewed = new FixtureHost(viewerOnly, ["m1"], _ => null, viewed: true);
        using var view = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new EwelinkElectricalCollector(viewerOnly, new(clock), clock, NullLogger.Instance).RunAsync(Owner, viewed, null!, view.Token);
        while (!viewerOnly.IsStopped) await Task.Delay(10, view.Token);
        await view.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("activationUnconfirmed", viewerOnly.Status(Meter("m1")).Status);
    }

    [Fact]
    public async Task CollectorIdlesWithoutDatabaseWorkWhenNothingIsEnabledAndStopsOnCancellation()
    {
        var clock = new ScaledClock(Now);
        var state = new EwelinkElectricalReporting(clock);
        var host = new FixtureHost(state, ["m1"], _ => 0);
        using var stop = new CancellationTokenSource();
        var run = new EwelinkElectricalCollector(state, new(clock), clock, NullLogger.Instance).RunAsync(Owner, host, null!, stop.Token);
        await Task.Delay(300);
        Assert.Equal(0, host.Scans);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void CollectionSummaryCarriesCountsAndAgesOnly()
    {
        var clock = new ManualClock(Now);
        var stats = new EwelinkCollectionStats(clock);
        stats.Eligible(["secret-device-a", "secret-device-b"]);
        stats.Persisted("secret-device-a", 3);
        stats.Count("skipped.offlineOrStale");
        clock.Advance(TimeSpan.FromMinutes(4));
        var logger = new CaptureLogger();
        stats.Report(logger, "backgroundWindows", TimeSpan.FromSeconds(900), TimeSpan.FromMinutes(15));
        var line = Assert.Single(logger.Lines);
        Assert.Contains("eligible meters 2", line, StringComparison.Ordinal);
        Assert.Contains("in the period 1", line, StringComparison.Ordinal);
        Assert.Contains("4m none", line, StringComparison.Ordinal);
        Assert.Contains("electricalSamplesPersisted=3", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-device", line, StringComparison.Ordinal);
        Assert.Equal("", stats.Drain(TimeSpan.FromMinutes(15)).Counters);
    }

    private static void Prime(EwelinkElectricalReporting state, ScaledClock clock, IEnumerable<string> ids)
    {
        foreach (var id in ids) state.IsDue(id, false);
        clock.Advance(EwelinkElectricalReporting.StartupDelay + EwelinkElectricalReporting.MaximumStagger);
    }

    private static EwelinkDevice Meter(string id) => new(id, "Fixture meter", "PSF-X67", "", false, Connectivity.Online,
        Now, true, false, Now, Uiid: 32);

    private sealed class FixtureHost(EwelinkElectricalReporting state, string[] ids, Func<string, int?> reply, bool viewed = false)
        : IEwelinkActivationHost
    {
        private readonly SemaphoreSlim sends = new(0);
        public string[] Ids => ids;
        public ConcurrentQueue<string> Sent { get; } = new();
        public DateTimeOffset? Throttle { get; set; }
        public int Throttled;
        public int Scans;

        public Task<IReadOnlyList<EwelinkRow>> TargetsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Scans);
            return Task.FromResult<IReadOnlyList<EwelinkRow>>(ids.Where(id => state.IsDue(id, viewed))
                .Select(id => new EwelinkRow("row-" + id, id, 1, null, "{}")).ToArray());
        }

        public Task<Task<int>?> SendAsync(EwelinkRow target, EwelinkActivationChannel channel, CancellationToken ct)
        {
            if (Throttle is { } until && state.Interval > TimeSpan.Zero && Throttled == 0)
            {
                Throttled++;
                throw new EwelinkFailure("quotaExceeded", until);
            }
            if (!state.TryStart(Owner, 1, target.ExternalId)) return Task.FromResult<Task<int>?>(null);
            Sent.Enqueue(target.ExternalId);
            sends.Release();
            Task<int> answer = reply(target.ExternalId) is { } code ? Task.FromResult(code) : new TaskCompletionSource<int>().Task;
            return Task.FromResult<Task<int>?>(answer);
        }

        public Task NotifyAsync(long property, CancellationToken ct) => Task.CompletedTask;

        public async Task WaitForSendsAsync(int count, CancellationToken ct)
        {
            for (var i = 0; i < count; i++) await sends.WaitAsync(ct);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current += by;
    }

    /// <summary>Runs timers 200 times faster than virtual time; each firing advances virtual time by its own period.</summary>
    private sealed class ScaledClock(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var step = period > TimeSpan.Zero ? period : dueTime;
            return base.CreateTimer(value =>
            {
                Advance(step);
                callback(value);
            }, state, Scale(dueTime), Scale(period));
        }

        private static TimeSpan Scale(TimeSpan value) =>
            value == Timeout.InfiniteTimeSpan || value <= TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(Math.Max(1, value.TotalMilliseconds / 200));
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
