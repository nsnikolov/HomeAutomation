using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Opt-in tests against a disposable <c>homeautomation_history_test</c> schema. The optional runtime connection should use
/// an account limited to SELECT, INSERT and UPDATE so the tests prove rollups and reads need no further grants.
/// </summary>
public sealed class TelemetryHistoryMySqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 20, 0, TimeSpan.Zero);

    [HistoryMySqlFact]
    public async Task MigrationUpgradesAPopulatedSchemaWithoutChangingEarlierMigrationsOrRawHistory()
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        await using (var db = await admin.OpenAsync(default))
            await db.ExecuteAsync("""
                DROP TABLE IF EXISTS telemetry_rollups_hourly, telemetry_rollup_state;
                DELETE FROM schema_migrations WHERE name LIKE '%.017_telemetry_rollups.sql';
                """);
        var before = await LedgerAsync(admin);
        var writer = new TelemetryWriter(admin, new FixedClock(Now));
        var property = await PropertyAsync(admin, "UTC");
        await writer.StoreCloudDeviceAsync("ewelink", property, null, Device("upgrade"),
            Batch("upgrade", Sample.Numeric(Metric.Temperature, Now.AddHours(-2), 20)), default);

        Assert.True(await new Migrations(Runtime()).VerifyAsync(default, includeDaikin: true, includeEwelink: true,
            includeTuya: true, includePreferences: true, includeDashboard: true));
        Assert.False(await new Migrations(Runtime()).VerifyTelemetryHistoryAsync(default));
        var pending = await Reader(new FixedClock(Now)).ReadAsync(property,
            new(new(await SeriesIdAsync(admin, property, "Temperature"), null, null, null, null), HistoryResolution.Hour,
                Now.AddHours(-3), Now), default);
        Assert.Equal(HistoryReadStatus.SchemaPending, pending.Status);
        var raw = await Reader(new FixedClock(Now)).ReadAsync(property,
            new(new(await SeriesIdAsync(admin, property, "Temperature"), null, null, null, null), HistoryResolution.Raw,
                Now.AddHours(-3), Now), default);
        Assert.Single(raw.Result!.Points!);

        await new Migrations(admin).ApplyAsync(default);

        Assert.Equal(before, (await LedgerAsync(admin)).Where(item => !item.Name.EndsWith(".017_telemetry_rollups.sql")).ToList());
        Assert.True(await new Migrations(Runtime()).VerifyTelemetryHistoryAsync(default));
        await using var check = await admin.OpenAsync(default);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.property_id=@property
            """, new { property }));
    }

    [HistoryMySqlFact]
    public async Task IngestionKeepsFirstWriteOrderingProvenanceAndRollupsFollowLateObservations()
    {
        var admin = await ReadyAsync();
        var clock = new FixedClock(Now);
        var writer = new TelemetryWriter(admin, clock);
        var property = await PropertyAsync(admin, "UTC");
        var hour = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
        await writer.StoreCloudDeviceAsync("ewelink", property, null, Device("integrity"),
            Batch("integrity", Sample.Numeric(Metric.ActivePower, hour.AddMinutes(10), 1000)), default);
        await writer.StoreCloudDeviceAsync("ewelink", property, null, Device("integrity"),
            Batch("integrity", Sample.Numeric(Metric.ActivePower, hour.AddMinutes(9), 500, serverReceivedTimestamp: true)), default);
        await writer.StoreCloudDeviceAsync("ewelink", property, null, Device("integrity"),
            Batch("integrity", Sample.Numeric(Metric.ActivePower, hour.AddMinutes(10), 4000)), default);
        var series = await SeriesIdAsync(admin, property, "ActivePower");

        await using (var db = await admin.OpenAsync(default))
        {
            var rows = (await db.QueryAsync<(DateTime At, double Value, string? Source)>("""
                SELECT observed_at_utc, numeric_value, timestamp_source FROM telemetry_samples WHERE series_id=@series ORDER BY observed_at_utc
                """, new { series })).ToList();
            Assert.Equal([(hour.AddMinutes(9).UtcDateTime, 500d, "serverReceived"), (hour.AddMinutes(10).UtcDateTime, 1000d, (string?)null)], rows);
            Assert.Equal(1000, await db.ExecuteScalarAsync<double>("SELECT numeric_value FROM latest_readings WHERE series_id=@series", new { series }));
        }

        var rollups = Rollups(clock);
        await rollups.RefreshRecentAsync(default);
        var first = await RowAsync(admin, series, hour.UtcDateTime);
        Assert.Equal(2, first.SampleCount);
        var ewelinkHold = new TelemetryHoldPolicy(new(), new(), new(), new(), new()).Hold("ewelink", Metric.ActivePower).TotalSeconds;
        Assert.Equal(60 + ewelinkHold, first.CoveredSeconds, 6);

        await rollups.RefreshRecentAsync(default);
        Assert.Equal(first.ComputedAt, (await RowAsync(admin, series, hour.UtcDateTime)).ComputedAt);

        clock.Now = Now.AddMinutes(1);
        await writer.StoreCloudDeviceAsync("ewelink", property, null, Device("integrity"),
            Batch("integrity", Sample.Numeric(Metric.ActivePower, hour.AddMinutes(5), 200)), default);
        await rollups.RefreshRecentAsync(default);
        var late = await RowAsync(admin, series, hour.UtcDateTime);
        Assert.Equal(3, late.SampleCount);
        Assert.NotEqual(first.ComputedAt, late.ComputedAt);
    }

    [HistoryMySqlFact]
    public async Task FailedRollupTransactionCommitsNothingAndReleasesTheLock()
    {
        var admin = await ReadyAsync();
        var property = await PropertyAsync(admin, "UTC");
        var series = await SeriesAsync(admin, property, Metric.Temperature, "simulator");
        await SamplesAsync(admin, series, Now.AddHours(-5).UtcDateTime, TimeSpan.FromMinutes(1), 120, index => 20 + index % 3);
        var failing = Rollups(new FixedClock(Now));
        failing.BeforeCommit = (id, _) => id == series ? throw new InvalidOperationException("fixture failure") : Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RefreshRecentAsync(default));

        await using (var db = await admin.OpenAsync(default))
        {
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM telemetry_rollups_hourly WHERE series_id=@series", new { series }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM telemetry_rollup_state WHERE series_id=@series", new { series }));
        }
        var run = await Rollups(new FixedClock(Now)).RefreshRecentAsync(default);
        Assert.False(run.Skipped);
        await using var after = await admin.OpenAsync(default);
        Assert.Equal(3, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM telemetry_rollups_hourly WHERE series_id=@series", new { series }));
    }

    [HistoryMySqlFact]
    public async Task BackfillDryRunWritesNothingAndAnInterruptedRunResumesWithoutDuplicates()
    {
        var admin = await ReadyAsync();
        var property = await PropertyAsync(admin, "UTC");
        var other = await PropertyAsync(admin, "UTC");
        var series = await SeriesAsync(admin, property, Metric.Humidity, "tuya");
        var foreign = await SeriesAsync(admin, other, Metric.Humidity, "tuya");
        var start = Now.UtcDateTime.AddDays(-20).Date;
        await SamplesAsync(admin, series, start.AddMinutes(7), TimeSpan.FromMinutes(30), 20 * 48, index => 40 + index % 10);
        var clock = new FixedClock(Now);
        var rollups = Rollups(clock);

        var rejected = await rollups.PlanBackfillAsync(new(property, [series, foreign], null, null), default);
        Assert.Equal([foreign], rejected.Rejected);
        var plan = await rollups.PlanBackfillAsync(new(property, [series], null, null), default);
        var item = Assert.Single(plan.Series);
        Assert.Equal(20 * 48, item.Samples);
        Assert.Equal(start, item.From);
        Assert.Equal(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), item.To);
        Assert.Equal(0, item.ExistingRows);
        Assert.Equal(0, await CountRowsAsync(admin, series));

        using var interrupt = new CancellationTokenSource();
        var commits = 0;
        rollups.BeforeCommit = (_, _) =>
        {
            if (++commits == 2) interrupt.Cancel();
            return Task.CompletedTask;
        };
        var stopped = await rollups.ExecuteBackfillAsync(plan, interrupt.Token);
        Assert.True(stopped.Cancelled);
        var partial = await StateAsync(admin, series);
        Assert.Equal((item.To!.Value.AddDays(-7), item.To.Value), partial);
        var partialRows = await CountRowsAsync(admin, series);
        Assert.Equal(7 * 24, partialRows);

        rollups.BeforeCommit = null;
        var resume = await rollups.PlanBackfillAsync(new(property, [series], null, null), default);
        Assert.Equal(item.To.Value.AddDays(-7), Assert.Single(resume.Series).To);
        var completed = await rollups.ExecuteBackfillAsync(resume, default);
        Assert.False(completed.Cancelled);
        Assert.Equal((start, item.To.Value), await StateAsync(admin, series));
        var total = await CountRowsAsync(admin, series);
        Assert.Equal(20 * 24, total);

        var repeat = await rollups.PlanBackfillAsync(new(property, [series], null, Now), default);
        var again = await rollups.ExecuteBackfillAsync(repeat, default);
        Assert.Equal(0, again.Written);
        Assert.Equal(total, await CountRowsAsync(admin, series));
    }

    [HistoryMySqlFact]
    public async Task ReaderIsolatesPropertiesSelectsOneSourceAndPagesRawHistory()
    {
        var admin = await ReadyAsync();
        var property = await PropertyAsync(admin, "Europe/Sofia");
        var other = await PropertyAsync(admin, "Europe/Sofia");
        var (device, endpoint) = await EndpointAsync(admin, property, "tuya-thermometer");
        var snapshot = await SeriesAsync(admin, property, Metric.Temperature, "tuya", endpoint);
        var report = await SeriesAsync(admin, property, Metric.Temperature, "tuya-sensor-report", endpoint);
        var foreign = await SeriesAsync(admin, other, Metric.Temperature, "tuya");
        var from = Now.UtcDateTime.AddHours(-3);
        await SamplesAsync(admin, report, from, TimeSpan.FromMinutes(30), 5, index => 20 + index);
        await SamplesAsync(admin, snapshot, from, TimeSpan.FromMinutes(30), 5, index => 99);
        var reader = Reader(new FixedClock(Now));

        var catalog = await reader.SeriesAsync(property, device, Metric.Temperature, default);
        Assert.Equal([report], catalog.Where(item => item.Preferred).Select(item => item.SeriesId));
        Assert.All(catalog, item => Assert.Equal("°C", item.Unit));
        Assert.DoesNotContain(catalog, item => item.SeriesId == foreign);

        var page = await reader.ReadAsync(property, new(new(null, null, endpoint, null, Metric.Temperature), HistoryResolution.Raw,
            Now.AddHours(-4), Now, Limit: 2), default);
        Assert.Equal(report, page.Result!.Series.SeriesId);
        Assert.Equal([snapshot], page.Result.AlternativeSeriesIds);
        Assert.Equal([20d, 21d], page.Result.Points!.Select(point => point.Number!.Value));
        Assert.All(page.Result.Points!, point => Assert.Equal(TimeSpan.Zero, point.ObservedAt.Offset));
        Assert.Equal("provider", page.Result.Points![0].TimestampSource);
        var next = await reader.ReadAsync(property, new(new(report, null, null, null, null), HistoryResolution.Raw,
            Now.AddHours(-4), Now, After: page.Result.NextAfter, Limit: 10), default);
        Assert.Equal([22d, 23d, 24d], next.Result!.Points!.Select(point => point.Number!.Value));
        Assert.Null(next.Result.NextAfter);

        Assert.Equal(HistoryReadStatus.NotFound, (await reader.ReadAsync(property,
            new(new(foreign, null, null, null, null), HistoryResolution.Raw, Now.AddHours(-4), Now), default)).Status);
        Assert.Equal(HistoryReadStatus.NotFound, (await reader.ReadAsync(other,
            new(new(report, null, null, null, null), HistoryResolution.Hour, Now.AddHours(-4), Now), default)).Status);
    }

    [HistoryMySqlFact]
    public async Task ReaderReportsHourlyGapsAndExactNonWholeHourDays()
    {
        var admin = await ReadyAsync();
        var property = await PropertyAsync(admin, "Asia/Kathmandu");
        var series = await SeriesAsync(admin, property, Metric.ActivePower, "growatt");
        var start = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 3 * 24 * 60 / 2).Select(index => start.AddMinutes(index * 2))
            .Where(at => at < start.AddHours(30) || at >= start.AddHours(33))
            .Select((at, index) => (at, value: 500d + index % 7 * 100)).ToList();
        await InsertAsync(admin, series, samples);
        var clock = new FixedClock(Now);
        var rollups = Rollups(clock);
        var plan = await rollups.PlanBackfillAsync(new(property, [series], null, null), default);
        await rollups.ExecuteBackfillAsync(plan, default);
        var reader = Reader(clock);

        var hours = (await reader.ReadAsync(property, new(new(series, null, null, null, null), HistoryResolution.Hour,
            new DateTimeOffset(start.AddHours(28)), new DateTimeOffset(start.AddHours(35))), default)).Result!;
        Assert.Equal("W", hours.Series.Unit);
        // The last observation before the gap is held for the 15-minute Growatt freshness window only.
        Assert.Equal(["complete", "complete", "incomplete", "noData", "noData", "complete", "complete"], hours.Buckets!.Select(bucket => bucket.Quality));
        Assert.Equal("estimated", hours.Buckets![0].Energy!.Status);
        Assert.Equal(780, hours.Buckets[2].CoveredSeconds, 6);
        Assert.Equal("incomplete", hours.Buckets[2].Energy!.Status);
        Assert.Null(hours.Buckets[3].Energy!.PositiveWh);
        Assert.Equal("noData", hours.Buckets[3].Energy!.Status);
        Assert.Equal(0, hours.Buckets[3].Coverage);

        var days = (await reader.ReadAsync(property, new(new(series, null, null, null, null), HistoryResolution.Day, Now, Now,
            new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26)), default)).Result!;
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 18, 15, 0, TimeSpan.Zero), days.Buckets![0].Start);
        Assert.All(days.Buckets, bucket => Assert.Equal(86400, bucket.DurationSeconds));
        var observations = samples.Select(item => new RawObservation(item.at, item.value, null)).ToList();
        var holds = new TelemetryHoldPolicy(new(), new(), new(), new(), new());
        foreach (var bucket in days.Buckets)
        {
            var direct = TelemetryAggregation.Summarize(Metric.ActivePower, holds.Policy("growatt", Metric.ActivePower, null),
                observations, [new(bucket.Start.UtcDateTime, bucket.End.UtcDateTime)], Now.UtcDateTime).Single();
            Assert.Equal(direct.EstimatedPositiveWh!.Value, bucket.Energy!.PositiveWh!.Value, 6);
            Assert.Equal(direct.CoveredSeconds, bucket.CoveredSeconds, 6);
            Assert.Equal(direct.SampleCount, bucket.SampleCount);
        }
        Assert.Equal("notAggregated", days.Buckets[0].Quality);
        Assert.Equal("complete", days.Buckets[1].Quality);
        Assert.InRange(days.Buckets[1].Coverage, 0.8, 0.9);
        Assert.Equal("complete", days.Buckets[2].Quality);
        Assert.Equal(1, days.Buckets[2].Coverage, 6);
    }

    [HistoryMySqlFact]
    public async Task DailyHistoryHandlesDstAndKeepsGrowattSeriesSeparate()
    {
        var admin = await ReadyAsync();
        var property = await PropertyAsync(admin, "Europe/Sofia");
        var installation = await InstallationAsync(admin, property);
        var (_, master) = await EndpointAsync(admin, property, "growatt-master");
        var (_, slave) = await EndpointAsync(admin, property, "growatt-slave");
        var siteLoad = await SeriesAsync(admin, property, Metric.HouseholdPower, "growatt", installationId: installation);
        var masterLoad = await SeriesAsync(admin, property, Metric.HouseholdPower, "growatt", master);
        var slaveLoad = await SeriesAsync(admin, property, Metric.HouseholdPower, "growatt", slave);
        var masterPv = await SeriesAsync(admin, property, Metric.PvEnergyToday, "growatt", master);
        var slavePv = await SeriesAsync(admin, property, Metric.PvEnergyToday, "growatt", slave);
        var sofia = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sofia");
        var day = CalendarDays.Day(new DateOnly(2026, 10, 25), sofia);
        var points = Enumerable.Range(0, (int)(day.Duration.TotalMinutes / 5)).Select(index => day.Start.AddMinutes(index * 5)).ToList();
        foreach (var load in new[] { siteLoad, masterLoad, slaveLoad })
            await InsertAsync(admin, load, points.Select(at => (at, 1000d)).ToList());
        await InsertAsync(admin, masterPv, points.Select((at, index) => (at, Math.Round(index * 0.01, 3))).ToList());
        await InsertAsync(admin, slavePv, points.Select((at, index) => (at, Math.Round(index * 0.02, 3))).ToList());
        var clock = new FixedClock(Now);
        var rollups = Rollups(clock);
        await rollups.ExecuteBackfillAsync(await rollups.PlanBackfillAsync(new(property, null, null, null), default), default);
        var reader = Reader(clock);

        HistoryBucket Day(HistoryRead read) => Assert.Single(read.Result!.Buckets!);
        var site = Day(await reader.ReadAsync(property, new(new(null, null, null, installation, Metric.HouseholdPower),
            HistoryResolution.Day, Now, Now, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)), default));
        Assert.Equal(90000, site.DurationSeconds);
        Assert.Equal(25000, site.Energy!.PositiveWh!.Value, 3);
        Assert.Equal(siteLoad, (await reader.ReadAsync(property, new(new(null, null, null, installation, Metric.HouseholdPower),
            HistoryResolution.Raw, Now.AddDays(-3), Now), default)).Result!.Series.SeriesId);

        var masterDay = Day(await reader.ReadAsync(property, new(new(masterPv, null, null, null, null), HistoryResolution.Day,
            Now, Now, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)), default));
        var slaveDay = Day(await reader.ReadAsync(property, new(new(slavePv, null, null, null, null), HistoryResolution.Day,
            Now, Now, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)), default));
        Assert.Equal("counter", masterDay.Energy!.Basis);
        Assert.Equal((points.Count - 1) * 10, masterDay.Energy.Wh!.Value, 3);
        Assert.Equal((points.Count - 1) * 20, slaveDay.Energy!.Wh!.Value, 3);
        Assert.Equal("measured", masterDay.Energy.Status);
        Assert.Null(masterDay.Energy.PositiveWh);
    }

    private static TelemetryRollups Rollups(TimeProvider clock) =>
        new(Runtime(), new Migrations(Runtime()), new TelemetryHoldPolicy(new GrowattOptions(), new DaikinOptions(), new EwelinkOptions(),
            new TuyaOptions(), new SimulatorOptions()), new TelemetryHistoryOptions(), clock);

    private static TelemetryHistoryReader Reader(TimeProvider clock) =>
        new(Runtime(), new Migrations(Runtime()), new TelemetryHoldPolicy(new(), new(), new(), new(), new()),
            new TelemetryHistoryOptions(), clock);

    private static async Task<Database> ReadyAsync()
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        return admin;
    }

    private static DiscoveredDevice Device(string id) => new(id, "Fixture " + id, DeviceKind.SmartSwitch, "", "Fixture", "Fixture",
        [new DeviceEndpoint("main", "Device", [Capability.PowerMeasurement, Capability.TemperatureMeasurement])]);

    private static ObservationBatch Batch(string id, Sample sample) =>
        new(sample.ObservedAt, [new(id, Connectivity.Online, null, [new("main", [sample])], sample.ObservedAt)], []);

    private static async Task<long> PropertyAsync(Database db, string zone)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'History fixture',@zone); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N"), zone });
    }

    private static async Task<long> InstallationAsync(Database db, long property)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO installations(property_id,installation_key,name) VALUES(@property,'growatt','Fixture installation'); SELECT LAST_INSERT_ID();
            """, new { property });
    }

    private static async Task<(long Device, long Endpoint)> EndpointAsync(Database db, long property, string name)
    {
        await using var connection = await db.OpenAsync(default);
        var device = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO devices(property_id,inventory_key,name,kind,manufacturer,model) VALUES(@property,@key,@name,'Sensor','Fixture','Fixture');
            SELECT LAST_INSERT_ID();
            """, new { property, key = Guid.NewGuid().ToString("N"), name });
        var endpoint = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO device_endpoints(property_id,device_id,endpoint_key,name) VALUES(@property,@device,'main','Main'); SELECT LAST_INSERT_ID();
            """, new { property, device });
        return (device, endpoint);
    }

    private static async Task<long> SeriesAsync(Database db, long property, Metric metric, string source, long? endpoint = null,
        long? installationId = null)
    {
        endpoint ??= installationId is null ? (await EndpointAsync(db, property, metric + " fixture")).Endpoint : null;
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO telemetry_series(property_id,endpoint_id,installation_id,source_key,metric) VALUES(@property,@endpoint,@installationId,@source,@metric);
            SELECT LAST_INSERT_ID();
            """, new { property, endpoint, installationId, source, metric = metric.ToString() });
    }

    private static Task SamplesAsync(Database db, long series, DateTime start, TimeSpan step, int count, Func<int, double> value) =>
        InsertAsync(db, series, Enumerable.Range(0, count).Select(index => (start + step * index, value(index))).ToList());

    private static async Task InsertAsync(Database db, long series, IReadOnlyList<(DateTime At, double Value)> samples)
    {
        await using var connection = await db.OpenAsync(default);
        await connection.ExecuteAsync("""
            INSERT INTO telemetry_samples(series_id,observed_at_utc,received_at_utc,numeric_value) VALUES(@series,@At,@At,@Value)
            """, samples.Select(sample => new { series, sample.At, sample.Value }));
    }

    private static async Task<long> SeriesIdAsync(Database db, long property, string metric)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>("SELECT id FROM telemetry_series WHERE property_id=@property AND metric=@metric",
            new { property, metric });
    }

    private static async Task<(long SampleCount, double CoveredSeconds, DateTime ComputedAt)> RowAsync(Database db, long series, DateTime hour)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.QuerySingleAsync<(long, double, DateTime)>("""
            SELECT CAST(sample_count AS SIGNED), covered_seconds, computed_at_utc FROM telemetry_rollups_hourly
            WHERE series_id=@series AND bucket_start_utc=@hour
            """, new { series, hour });
    }

    private static async Task<int> CountRowsAsync(Database db, long series)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM telemetry_rollups_hourly WHERE series_id=@series", new { series });
    }

    private static async Task<(DateTime From, DateTime To)> StateAsync(Database db, long series)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.QuerySingleAsync<(DateTime, DateTime)>(
            "SELECT rolled_from_utc, rolled_to_utc FROM telemetry_rollup_state WHERE series_id=@series", new { series });
    }

    private static async Task<List<(string Name, string Checksum)>> LedgerAsync(Database db)
    {
        await using var connection = await db.OpenAsync(default);
        return (await connection.QueryAsync<(string, string)>("SELECT name, checksum FROM schema_migrations ORDER BY name")).ToList();
    }

    private static Database Admin() => Open(HistoryMySqlFact.AdminVariable);

    private static Database Runtime() =>
        Environment.GetEnvironmentVariable(HistoryMySqlFact.RuntimeVariable) is { Length: > 0 } ? Open(HistoryMySqlFact.RuntimeVariable) : Admin();

    private static Database Open(string variable)
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException("A disposable history MySQL test database is required."));
        Assert.Equal("homeautomation_history_test", builder.Database);
        return new Database(builder.ConnectionString);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HistoryMySqlFact : FactAttribute
    {
        public const string AdminVariable = "HOMEAUTOMATION_HISTORY_TEST_MYSQL";
        public const string RuntimeVariable = "HOMEAUTOMATION_HISTORY_TEST_RUNTIME";

        public HistoryMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminVariable)))
                Skip = $"Set {AdminVariable} for a disposable homeautomation_history_test schema.";
        }
    }
}
