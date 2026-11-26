using System.Net;
using System.Text.Json;
using Dapper;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Milestone 7D end to end on the disposable <c>homeautomation_history_test</c> schema: background activation with no
/// browser lease, the shared request budget, receipt-timed persistence through <see cref="TelemetryWriter"/>, and 7A
/// rollups that report only the coverage the readings support. No provider is contacted: HTTP fails closed and the
/// socket is a fixture channel.
/// </summary>
public sealed class EwelinkCollectionMySqlTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    private const string Owner = "collection-owner";
    private const string App = "fixture-app";
    private const string AccountKey = "fixture-account-key";

    [CollectionMySqlFact]
    public async Task BackgroundWindowsPersistHonestHistoryWithoutAnyViewer()
    {
        var database = await ReadyAsync();
        var clock = new SettableClock(Start);
        var store = new EwelinkStore(database, new Migrations(database));
        var (property, foreign) = await PropertiesAsync(database);
        var vault = new EwelinkVault(new EphemeralDataProtectionProvider());
        var tokens = new EwelinkTokens("eu", "fixture-access", "fixture-refresh", Start.AddDays(1), Start.AddDays(2), AccountKey);
        var envelope = vault.Seal(Owner, App, tokens);
        await store.SaveTokensAsync(Owner, envelope, default);
        await store.ReportAsync(Owner, "connected", Start, Start.AddMinutes(5), default, Start);

        await DeviceAsync(store, Meter("meter-a"), property);
        await DeviceAsync(store, Meter("meter-offline") with { Connectivity = Connectivity.Offline }, property);
        await DeviceAsync(store, Meter("thermostat") with { Model = "THR320D", Uiid = 181 }, property);
        await DeviceAsync(store, Meter("meter-elsewhere"), foreign);
        await DeviceAsync(store, Meter("meter-unassigned"), null);

        var options = new EwelinkOptions { AppId = App, EnableWebSocket = true, ContinuousElectricalCollection = true, PollMinutes = 5 };
        var electrical = new EwelinkElectricalReporting(clock, options);
        var stats = new EwelinkCollectionStats(clock);
        using var http = new HttpClient(new RefusingHandler());
        using var gate = new EwelinkGate();
        var writer = new TelemetryWriter(database, clock);
        var service = new EwelinkService(options, new EwelinkClient(http, options, clock, new EwelinkPace(clock), store: store),
            store, gate, vault, new MonitoringReader(database, clock, new(), new()), writer, clock, new EwelinkRelayAudit(database),
            electrical);
        var identity = new EwelinkSocketIdentity(Owner, envelope, tokens);
        var sent = new List<string>();
        var channel = new EwelinkActivationChannel(AccountKey, clock, (text, _) =>
        {
            sent.Add(text);
            return ValueTask.CompletedTask;
        }, Start.ToUnixTimeMilliseconds());

        // A restart waits for its startup delay and stagger before the first window.
        Assert.Empty(await service.ElectricalTargetsAsync(identity, default, stats));
        clock.Now = Start.AddMinutes(6);
        await RefreshSeenAsync(store, clock.Now);
        var target = Assert.Single(await service.ElectricalTargetsAsync(identity, default, stats));
        Assert.Equal("meter-a", target.ExternalId);
        // Two scans so far: the offline meter and the meter in a property the account cannot read were skipped each time.
        var skipped = stats.Drain(TimeSpan.FromMinutes(15)).Counters;
        Assert.Contains("skipped.offlineOrStale=2", skipped, StringComparison.Ordinal);
        Assert.Contains("skipped.propertyScope=2", skipped, StringComparison.Ordinal);
        Assert.Empty(electrical.ViewedProperties(Owner));

        await ActivateAsync(target);
        var command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("update", command.GetProperty("action").GetString());
        Assert.Equal("uiActive", Assert.Single(command.GetProperty("params").EnumerateObject()).Name);
        Assert.Equal(1, await RequestsAsync(database, clock.Now));

        var firstAt = clock.Now.AddSeconds(3);
        clock.Now = firstAt;
        Assert.Equal((property, 3), await IngestAsync("120.50", "230.10", "0.52", firstAt));
        clock.Now = firstAt.AddSeconds(4);
        Assert.Null(await IngestAsync("120.50", "230.10", "0.52", clock.Now));

        // The next window is one interval later. An identical report there is current and is kept.
        clock.Now = Start.AddMinutes(14);
        await RefreshSeenAsync(store, clock.Now);
        Assert.Empty(await service.ElectricalTargetsAsync(identity, default, stats));
        clock.Now = firstAt.AddSeconds(-3).AddSeconds(900);
        await RefreshSeenAsync(store, clock.Now);
        await ActivateAsync(Assert.Single(await service.ElectricalTargetsAsync(identity, default, stats)));
        var secondAt = clock.Now.AddSeconds(2);
        clock.Now = secondAt;
        Assert.Equal((property, 3), await IngestAsync("120.50", "230.10", "0.52", secondAt));

        await using (var db = await database.OpenAsync(default))
        {
            var rows = (await db.QueryAsync<(string Metric, DateTime At, double Value, string? Source)>("""
                SELECT s.metric, h.observed_at_utc, h.numeric_value, h.timestamp_source
                FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id
                WHERE s.property_id=@property AND s.source_key='ewelink' AND s.metric IN ('ActivePower','Voltage','Current')
                ORDER BY s.metric, h.observed_at_utc
                """, new { property })).ToList();
            Assert.Equal(6, rows.Count);
            Assert.All(rows, row => Assert.Equal("serverReceived", row.Source));
            Assert.Equal([firstAt.UtcDateTime, secondAt.UtcDateTime],
                rows.Where(row => row.Metric == "ActivePower").Select(row => row.At).ToArray());
            Assert.DoesNotContain(rows, row => row.Value == 0);
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id
                WHERE s.property_id=@foreign
                """, new { foreign }));
        }

        // 7A: two readings, each held for the eWeLink electrical hold, give low coverage and partial estimated energy.
        clock.Now = Start.AddHours(1).AddMinutes(30);
        var holds = new TelemetryHoldPolicy(new GrowattOptions(), new DaikinOptions(), options, new TuyaOptions(), new SimulatorOptions());
        // Scoped to this test's property, so parallel tests sharing the fixture schema keep their own rollup state.
        var rollups = new TelemetryRollups(database, new Migrations(database), holds, new TelemetryHistoryOptions(), clock);
        await rollups.ExecuteBackfillAsync(await rollups.PlanBackfillAsync(new(property, null, null, null), default), default);
        var hold = holds.Hold("ewelink", Metric.ActivePower).TotalSeconds;
        await using (var db = await database.OpenAsync(default))
        {
            var hour = await db.QuerySingleAsync<(long Samples, double Covered, double? Positive)>("""
                SELECT CAST(r.sample_count AS SIGNED), r.covered_seconds, r.estimated_positive_wh
                FROM telemetry_rollups_hourly r JOIN telemetry_series s ON s.id=r.series_id
                WHERE s.property_id=@property AND s.metric='ActivePower' AND r.bucket_start_utc=@hour
                """, new { property, hour = Start.UtcDateTime });
            Assert.Equal(2, hour.Samples);
            Assert.Equal(2 * hold, hour.Covered, 6);
            Assert.True(hour.Covered / 3600 < 0.8);
            Assert.Equal(2 * 120.5 * hold / 3600, hour.Positive!.Value, 6);
        }

        // The daily electrical share stops activations for the rest of the UTC day; HTTP reconciliation still reserves.
        await using (var db = await database.OpenAsync(default))
            await db.ExecuteAsync("UPDATE ewelink_request_days SET requests=@share WHERE region='eu' AND day_utc=@day",
                new { share = EwelinkCollectionBudget.DailyElectricalShare, day = clock.Now.UtcDateTime.Date });
        await RefreshSeenAsync(store, clock.Now);
        var due = Assert.Single(await service.ElectricalTargetsAsync(identity, default, stats));
        var limited = await Assert.ThrowsAsync<EwelinkFailure>(() => service.SendElectricalActivationAsync(identity, due, channel, default));
        Assert.Equal("quotaExceeded", limited.Status);
        Assert.Equal(new DateTimeOffset(clock.Now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero), limited.RetryAt);
        await store.ReserveRequestAsync("eu", clock.Now, default);
        Assert.Equal(EwelinkCollectionBudget.DailyElectricalShare + 1, await RequestsAsync(database, clock.Now));
        Assert.Equal(2, sent.Count);

        async Task ActivateAsync(EwelinkRow row)
        {
            var reply = await service.SendElectricalActivationAsync(identity, row, channel, default);
            Assert.NotNull(reply);
            using var latest = JsonDocument.Parse(sent[^1]);
            using var ack = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                error = 0, apikey = AccountKey, deviceid = row.ExternalId, sequence = latest.RootElement.GetProperty("sequence").GetString()
            }));
            Assert.True(channel.Receive(ack.RootElement));
            Assert.Equal(0, await reply);
            electrical.Accepted(row.ExternalId);
        }

        Task<(long Property, int ElectricalSamples)?> IngestAsync(string power, string voltage, string current, DateTimeOffset at)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new { power, voltage, current }));
            return service.IngestSocketEventAsync(identity, "meter-a", parameters.RootElement.Clone(),
                new(at, EwelinkSocketTimeSource.ServerReceived), default, null);
        }
    }

    private static EwelinkDevice Meter(string id) => new(id, "Fixture " + id, "PSF-X67", "", false, Connectivity.Online,
        Start, true, false, Start, Uiid: 32);

    private static async Task DeviceAsync(EwelinkStore store, EwelinkDevice device, long? property)
    {
        await store.SaveAsync(Owner, device, default);
        if (property is { } assigned)
        {
            var row = (await store.DevicesAsync(Owner, default)).Single(item => item.ExternalId == device.ExternalId);
            await store.AssignAsync(row, Owner, assigned, null, default);
        }
    }

    /// <summary>What the five-minute HTTP synchronization does to reachability; socket measurements are preserved.</summary>
    private static async Task RefreshSeenAsync(EwelinkStore store, DateTimeOffset at)
    {
        foreach (var row in await store.DevicesAsync(Owner, default))
            await store.SaveAsync(Owner, row.Device with { SeenAt = at }, default);
    }

    private static async Task<long> RequestsAsync(Database database, DateTimeOffset at)
    {
        await using var db = await database.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(requests),0) FROM ewelink_request_days WHERE day_utc=@day",
            new { day = at.UtcDateTime.Date });
    }

    private static async Task<(long Property, long Foreign)> PropertiesAsync(Database database)
    {
        await using var db = await database.OpenAsync(default);
        var property = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Collection fixture','UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
        var foreign = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Other fixture','UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
        await db.ExecuteAsync("""
            INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
            VALUES(@Owner,'collection@example.test','COLLECTION@EXAMPLE.TEST','fixture','fixture','fixture');
            INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@Owner,'viewer');
            """, new { Owner, property });
        return (property, foreign);
    }

    private static async Task<Database> ReadyAsync()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(CollectionMySqlFact.Variable)
            ?? throw new InvalidOperationException("A disposable history MySQL test database is required."));
        Assert.Equal("homeautomation_history_test", builder.Database);
        var database = new Database(builder.ConnectionString);
        await new Migrations(database).ApplyAsync(default);
        await using var db = await database.OpenAsync(default);
        await db.ExecuteAsync("""
            DELETE FROM ewelink_relay_commands; DELETE FROM ewelink_devices; DELETE FROM ewelink_account; DELETE FROM ewelink_request_days;
            DELETE FROM property_memberships WHERE user_id=@Owner; DELETE FROM identity_users WHERE id=@Owner;
            """, new { Owner });
        return database;
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Milestone 7D tests never contact the provider.");
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CollectionMySqlFact : FactAttribute
    {
        public const string Variable = "HOMEAUTOMATION_HISTORY_TEST_MYSQL";

        public CollectionMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = $"Set {Variable} for a disposable homeautomation_history_test schema.";
        }
    }
}
