using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Removing runtime simulation, on the disposable <c>homeautomation_logical_test</c> schema. Each test builds its own property with
/// real Growatt hardware, simulator hardware, a mixed device, simulator-only and mixed logical identities, rollups and a dashboard.
/// </summary>
[Collection(LogicalSchemaCollection.Name)]
public sealed class SimulatorCleanupMySqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Switch = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [CleanupMySqlFact]
    public async Task DisabledSimulatorDataIsHiddenFromStateHistoryDashboardsAndLogicalManagement()
    {
        var f = await ScenarioAsync();
        var off = new SimulatorOptions();
        var reader = new MonitoringReader(Runtime(), new FixedClock(Now), off, new GrowattOptions(), Holds(off));
        var state = await reader.StateAsync(f.Property, default);
        Assert.DoesNotContain(state.Devices, device => device.Id == f.SimA || device.Id == f.SimB);
        var real = Assert.Single(state.Devices, device => device.Id == f.RealA);
        Assert.Equal(900, real.Endpoints.Single().Measurements.Single(reading => reading.Metric == Metric.ActivePower).Number);
        Assert.Empty(state.Devices.Single(device => device.Id == f.SimC).Endpoints.SelectMany(endpoint => endpoint.Measurements));
        Assert.Empty(await reader.HistoryAsync(f.Property, f.SimAEndpoint, Metric.ActivePower, Now.AddDays(-1), Now, default));
        Assert.Empty(await reader.HistoryAsync(f.Property, null, Metric.PvPower, Now.AddDays(-1), Now, default));
        Assert.Null(await reader.DeviceAsync(f.Property, f.SimA, default));
        Assert.NotEmpty(await reader.HistoryAsync(f.Property, f.RealAEndpoint, Metric.ActivePower, Now.AddDays(-1), Now, default));

        var on = new SimulatorOptions { Enabled = true };
        var demo = new MonitoringReader(Runtime(), new FixedClock(Now), on, new GrowattOptions(), Holds(on));
        Assert.Contains((await demo.StateAsync(f.Property, default)).Devices, device => device.Id == f.SimA);
        Assert.NotEmpty(await demo.HistoryAsync(f.Property, null, Metric.PvPower, Now.AddDays(-1), Now, default));

        var history = new TelemetryHistoryReader(Runtime(), new Migrations(Runtime()), Holds(off), new TelemetryHistoryOptions(), new FixedClock(Now));
        Assert.All(await history.SeriesAsync(f.Property, null, null, default), series => Assert.NotEqual("simulator", series.Source));
        var catalog = (await new MonitoringDashboardStore(Runtime(), new Migrations(Runtime()), new TuyaOptions()).CatalogAsync(f.Property, f.Owner, default))!;
        Assert.DoesNotContain(catalog, source => source.Key.StartsWith("simulator:", StringComparison.Ordinal));
        Assert.Contains(catalog, source => source.Key == f.RealKey);

        var logical = new LogicalDeviceStore(Runtime(), new Migrations(Runtime()), history, new FixedClock(Now));
        Assert.DoesNotContain((await logical.PhysicalDevicesAsync(f.Property, f.Owner, default)).Devices, device => device.Id == f.SimA);
        Assert.Equal("simulated device", (await logical.PlanInitializationAsync(f.Property, default)).Items.Single(item => item.DeviceId == f.SimA).Skipped);
        Assert.Equal(0, await logical.ProvisionAsync(f.Property, default));
        Assert.Equal(LogicalChangeStatus.NotFound, (await logical.CreateAsync(f.Property, f.Owner,
            new("Demo", "Sensor", null, null, f.SimA, null, null, null, null), default)).Status);
    }

    [CleanupMySqlFact]
    public async Task DryRunIdentifiesOnlySimulatorOwnedRowsReportsConflictsAndWritesNothing()
    {
        var f = await ScenarioAsync();
        var before = await SnapshotAsync(f);
        var plan = await Cleanup().PlanAsync(f.Property, default);

        Assert.Equal([f.SimA], plan.DeviceIds);
        Assert.Equal(new[] { f.SimASeries, f.InstallationSeries }.Order(), plan.SeriesIds.Order());
        Assert.Equal([f.LogicalSim], plan.LogicalDeviceIds);
        Assert.Contains(plan.Conflicts, conflict => conflict is { Kind: "device" } && conflict.Id == f.SimC && conflict.Reason.Contains("also bound to growatt"));
        Assert.Contains(plan.Conflicts, conflict => conflict is { Kind: "logicalDevice" } && conflict.Id == f.LogicalMixed);
        Assert.Contains(plan.Conflicts, conflict => conflict is { Kind: "device" } && conflict.Id == f.SimB);
        Assert.Contains(plan.Conflicts, conflict => conflict is { Kind: "series" } && conflict.Id == f.SimBSeries);
        Assert.Equal(f.SimulatorSamples, Count(plan, "telemetry_samples"));
        Assert.Equal(2, Count(plan, "telemetry_series"));
        Assert.Equal(1, Count(plan, "devices"));
        Assert.True(Count(plan, "telemetry_rollups_hourly") > 0);
        Assert.Equal(2, Count(plan, "telemetry_rollup_state"));
        Assert.Equal(1, Count(plan, "logical_device_assignments"));
        Assert.Equal(new[] { f.SimulatorKey, $"logical:{f.LogicalSim}:main:ActivePower" }.Order(StringComparer.Ordinal),
            plan.DashboardReferences.Select(reference => reference.SourceKey).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.DashboardReferences, reference => reference.SourceKey == f.RealKey);
        Assert.Empty(plan.AbsentOptionalTables);
        Assert.Equal(before, await SnapshotAsync(f));

        // Each property is planned on its own.
        var other = await Cleanup().PlanAsync(f.OtherProperty, default);
        Assert.Single(other.DeviceIds);
        Assert.DoesNotContain(f.SimA, other.DeviceIds);
        Assert.False((await Cleanup().PlanAsync(long.MaxValue, default)).PropertyExists);
    }

    [CleanupMySqlFact]
    public async Task ExecutionRemovesOnlySimulatorRowsKeepsRealHistoryAndIsIdempotent()
    {
        var f = await ScenarioAsync();
        var before = await SnapshotAsync(f);
        var cleanup = Cleanup();
        var deleted = await cleanup.ExecuteAsync(await cleanup.PlanAsync(f.Property, default), default);
        Assert.Equal(f.SimulatorSamples, deleted.Single(item => item.Table == "telemetry_samples").Rows);

        var after = await SnapshotAsync(f);
        Assert.Equal(before.Real, after.Real);
        Assert.Equal(before.Preserved, after.Preserved);
        Assert.Equal(before.Dashboard, after.Dashboard);
        Assert.Equal(before.Seeded, after.Seeded);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), after.Removed);
        Assert.True(before.Removed.Item1 > 0);

        var rerun = await cleanup.PlanAsync(f.Property, default);
        Assert.True(rerun.IsEmpty);
        Assert.Contains(rerun.Conflicts, conflict => conflict.Id == f.SimC);
        Assert.Empty(await cleanup.ExecuteAsync(rerun, default));
        Assert.Equal(after, await SnapshotAsync(f));
        Assert.Single((await cleanup.PlanAsync(f.OtherProperty, default)).DeviceIds);

        // Saved layouts are untouched; the removed simulator source simply has no catalog entry.
        var dashboard = new MonitoringDashboardStore(Runtime(), new Migrations(Runtime()), new TuyaOptions());
        var page = await dashboard.ReadAsync(f.Property, f.Owner, default);
        Assert.Equal(3, page.Groups.Single().Widgets.Single().Values.Count());
        Assert.DoesNotContain(page.Sources, source => source.Key == f.SimulatorKey);
        Assert.Contains(page.Sources, source => source.Key == f.RealKey);
    }

    [CleanupMySqlFact]
    public async Task AFailedStructuralTransactionRollsBackAndARerunFinishesTheCleanup()
    {
        var f = await ScenarioAsync();
        var failing = Cleanup();
        failing.BeforeCommit = _ => throw new InvalidOperationException("Simulated failure before commit");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await failing.ExecuteAsync(await failing.PlanAsync(f.Property, default), default));

        var interrupted = await SnapshotAsync(f);
        Assert.Equal((0L, 2L, 1L, 1L), (interrupted.Removed.Item1, interrupted.Removed.Item2, interrupted.Removed.Item3, interrupted.Removed.Item4));
        Assert.True(interrupted.Removed.Item5 > 0);

        var cleanup = Cleanup();
        await cleanup.ExecuteAsync(await cleanup.PlanAsync(f.Property, default), default);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), (await SnapshotAsync(f)).Removed);
    }

    [CleanupMySqlFact]
    public async Task ExecutionRefusesAPlanThatNoLongerMatchesTheData()
    {
        var f = await ScenarioAsync();
        var plan = await Cleanup().PlanAsync(f.Property, default);
        await DeviceAsync(f.Admin, "simulator", f.Property, "late-demo", Capability.TemperatureMeasurement,
            [Sample.Numeric(Metric.Temperature, Now.AddMinutes(-5), 21)]);
        var error = await Assert.ThrowsAsync<SimulatorCleanupException>(() => Cleanup().ExecuteAsync(plan, default));
        Assert.Contains("changed since the dry run", error.Message, StringComparison.Ordinal);
        // Checked before the first batch: nothing at all was deleted.
        var unchanged = (await SnapshotAsync(f)).Removed;
        Assert.Equal(f.SimulatorSamples, unchanged.Item1);
        Assert.Equal(1L, unchanged.Item3);
    }

    [CleanupMySqlFact]
    public async Task CleanupAdaptsToASchemaWithoutRollupLogicalOrDashboardTables()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(CleanupMySqlFact.AdminVariable)!);
        await using (var server = new MySqlConnection(builder.ConnectionString))
        {
            await server.OpenAsync();
            await server.ExecuteAsync("CREATE DATABASE IF NOT EXISTS homeautomation_cleanup_test");
        }
        builder.Database = "homeautomation_cleanup_test";
        var minimal = new Database(builder.ConnectionString);
        await new Migrations(minimal).ApplyAsync(default);
        await using (var db = await minimal.OpenAsync(default))
            await db.ExecuteAsync("""
                DROP TABLE IF EXISTS dashboard_values, dashboard_widgets, dashboard_groups, logical_device_audit, logical_device_assignments,
                    logical_devices, telemetry_rollups_hourly, telemetry_rollup_state;
                """);
        var property = await PropertyAsync(minimal);
        var (device, _) = await DeviceAsync(minimal, "simulator", property, "minimal", Capability.TemperatureMeasurement,
            [Sample.Numeric(Metric.Temperature, Now.AddMinutes(-5), 21)]);

        var cleanup = new SimulatorDataCleanup(minimal);
        var plan = await cleanup.PlanAsync(property, default);
        Assert.Equal([device], plan.DeviceIds);
        Assert.Contains("telemetry_rollups_hourly", plan.AbsentOptionalTables);
        Assert.Contains("logical_devices", plan.AbsentOptionalTables);
        Assert.Contains("dashboard_values", plan.AbsentOptionalTables);
        await cleanup.ExecuteAsync(plan, default);
        Assert.True((await cleanup.PlanAsync(property, default)).IsEmpty);
    }

    private sealed record Scenario(Database Admin, long Property, long OtherProperty, string Owner, long RealA, long RealAEndpoint, long RealB,
        long SimA, long SimAEndpoint, long SimASeries, long SimB, long SimBSeries, long SimC, long InstallationSeries, long LogicalSim,
        long LogicalMixed, long SimulatorSamples, string SimulatorKey, string RealKey);

    private sealed record Snapshot(string Real, string Preserved, string Dashboard, string Seeded, (long, long, long, long, long, long) Removed);

    private static async Task<Scenario> ScenarioAsync()
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        var property = await PropertyAsync(admin);
        var other = await PropertyAsync(admin);
        var owner = Guid.NewGuid().ToString("D");
        await using (var db = await admin.OpenAsync(default))
            await db.ExecuteAsync("""
                INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
                VALUES(@owner,CONCAT(@owner,'@example.test'),UPPER(CONCAT(@owner,'@example.test')),'x','x','x');
                INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@owner,'Owner');
                INSERT INTO rooms(property_id,room_key,name) VALUES(@property,'plant','Plant room');
                INSERT INTO installations(property_id,installation_key,name) VALUES(@property,'solar','Parallel solar installation');
                """, new { owner, property });

        var (realA, realAEndpoint) = await DeviceAsync(admin, "growatt", property, "real-a", Capability.PowerMeasurement,
            Every(Now.AddHours(-6), Now, 15, at => Sample.Numeric(Metric.ActivePower, at, 900)));
        var (realB, _) = await DeviceAsync(admin, "growatt", property, "real-b", Capability.PowerMeasurement,
            Every(Now.AddHours(-6), Now, 15, at => Sample.Numeric(Metric.ActivePower, at, 400)));
        var (simA, simAEndpoint) = await DeviceAsync(admin, "simulator", property, "inverter-master", Capability.PowerMeasurement,
            Every(Now.AddHours(-6), Now, 10, at => Sample.Numeric(Metric.ActivePower, at, 2900)));
        var (simB, _) = await DeviceAsync(admin, "simulator", property, "room-sensor", Capability.TemperatureMeasurement,
            Every(Now.AddHours(-6), Now, 10, at => Sample.Numeric(Metric.Temperature, at, 19.7)));
        var (simC, _) = await DeviceAsync(admin, "simulator", property, "mixed-meter", Capability.PowerMeasurement,
            [Sample.Numeric(Metric.ActivePower, Now.AddMinutes(-3), 1200)]);
        await DeviceAsync(admin, "simulator", other, "other-demo", Capability.PowerMeasurement, [Sample.Numeric(Metric.ActivePower, Now, 1)]);
        await using var conn = await admin.OpenAsync(default);

        // A device bound to the simulator and to a real provider is mixed and must be kept.
        await conn.ExecuteAsync("""
            INSERT INTO provider_bindings(property_id,connection_id,device_id,external_id)
            SELECT @property, id, @simC, 'mixed-growatt' FROM integration_connections WHERE property_id=@property AND provider_code='growatt'
            """, new { property, simC });
        var installationSeries = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO telemetry_series(property_id,installation_id,source_key,metric)
            SELECT @property, id, 'simulator', 'PvPower' FROM installations WHERE property_id=@property AND installation_key='solar';
            SELECT LAST_INSERT_ID();
            """, new { property });
        // Over two sample batches of installation history, to exercise the bounded deletion loop.
        await conn.ExecuteAsync("""
            SET SESSION cte_max_recursion_depth = 20000;
            INSERT INTO telemetry_samples(series_id,observed_at_utc,received_at_utc,numeric_value)
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i < 10999)
            SELECT @installationSeries, TIMESTAMPADD(SECOND, -10*i, @at), TIMESTAMPADD(SECOND, -10*i, @at), 2000 FROM n;
            INSERT INTO latest_readings(series_id,observed_at_utc,received_at_utc,numeric_value) VALUES(@installationSeries,@at,@at,2000);
            """, new { installationSeries, at = Now.UtcDateTime.AddMinutes(-1) });

        long Series(long device, string metric) => conn.ExecuteScalar<long>("""
            SELECT s.id FROM telemetry_series s JOIN device_endpoints e ON e.id=s.endpoint_id WHERE e.device_id=@device AND s.metric=@metric
            """, new { device, metric });
        var simASeries = Series(simA, "ActivePower");
        var simBSeries = Series(simB, "Temperature");

        // A simulator-only identity and an identity whose history moved from simulated to real hardware.
        var now = Now.UtcDateTime;
        var logicalSim = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO logical_devices(property_id,name,kind,origin_device_id,created_at_utc,updated_at_utc,creation)
            VALUES(@property,'Inverter · Master','Inverter',@simA,@now,@now,'initialized'); SELECT LAST_INSERT_ID();
            """, new { property, simA, now });
        await conn.ExecuteAsync("""
            INSERT INTO logical_device_assignments(property_id,logical_device_id,channel_key,device_id,valid_from_utc,assigned_by,assigned_at_utc)
            VALUES(@property,@logicalSim,'main',@simA,'1970-01-01','system:initialize',@now);
            INSERT INTO logical_device_audit(property_id,logical_device_id,occurred_at_utc,actor,action,new_device_id,effective_at_utc)
            VALUES(@property,@logicalSim,@now,'system:initialize','initialized',@simA,'1970-01-01');
            """, new { property, logicalSim, simA, now });
        var logicalMixed = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO logical_devices(property_id,name,kind,created_at_utc,updated_at_utc,creation)
            VALUES(@property,'Lounge','Sensor',@now,@now,'manual'); SELECT LAST_INSERT_ID();
            """, new { property, now });
        await conn.ExecuteAsync("""
            INSERT INTO logical_device_assignments(property_id,logical_device_id,channel_key,device_id,valid_from_utc,valid_to_utc,assigned_by,assigned_at_utc)
            VALUES(@property,@logicalMixed,'main',@simB,'1970-01-01',@switch,'owner',@now),
                  (@property,@logicalMixed,'main',@realB,@switch,NULL,'owner',@now);
            INSERT INTO logical_device_audit(property_id,logical_device_id,occurred_at_utc,actor,action,old_device_id,new_device_id,effective_at_utc)
            VALUES(@property,@logicalMixed,@now,'owner','replaced',@simB,@realB,@switch);
            """, new { property, logicalMixed, simB, realB, now, @switch = Switch });
        await new LogicalDeviceStore(Runtime(), new Migrations(Runtime()), Reader(new()), new FixedClock(Now)).ExecuteInitializationAsync(property, default);

        var rollups = new TelemetryRollups(Runtime(), new Migrations(Runtime()), Holds(new() { Enabled = true }), new TelemetryHistoryOptions(), new FixedClock(Now));
        await rollups.ExecuteBackfillAsync(await rollups.PlanBackfillAsync(new(property, null, null, null), default), default);

        var demo = new MonitoringDashboardStore(Runtime(), new Migrations(Runtime()), new TuyaOptions(), new SimulatorOptions { Enabled = true });
        var catalog = (await demo.CatalogAsync(property, owner, default))!;
        var simulatorKey = catalog.Single(source => source.Key.StartsWith($"simulator:{simA}:", StringComparison.Ordinal)).Key;
        var realKey = catalog.Single(source => source.Key.StartsWith($"growatt:{realA}:", StringComparison.Ordinal)).Key;
        var page = await demo.ReadAsync(property, owner, default);
        var values = new[] { simulatorKey, realKey, $"logical:{logicalSim}:main:ActivePower" }
            .Select(key => new DashboardValue(Guid.NewGuid().ToString("D"), key, null)).ToArray();
        Assert.Equal(DashboardSaveResult.Saved, await demo.ReplaceAsync(property, owner, new(page.Revision,
            [new(Guid.NewGuid().ToString("D"), "Energy", false, false, [new(Guid.NewGuid().ToString("D"), "Inverter", "compact", realKey, values)])]), default));

        var samples = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM telemetry_samples WHERE series_id IN (@simASeries,@installationSeries)",
            new { simASeries, installationSeries });
        return new(admin, property, other, owner, realA, realAEndpoint, realB, simA, simAEndpoint, simASeries, simB, simBSeries, simC,
            installationSeries, logicalSim, logicalMixed, samples, simulatorKey, realKey);
    }

    private static async Task<Snapshot> SnapshotAsync(Scenario f)
    {
        await using var db = await f.Admin.OpenAsync(default);
        async Task<string> Text(string sql) => string.Join("|", await db.QueryAsync<string>(sql, new
        {
            f.Property, ids = new[] { f.RealA, f.RealB }, preserved = new[] { f.SimB, f.SimC }, f.LogicalMixed, f.SimA, f.LogicalSim
        }));
        var real = await Text("""
            SELECT CONCAT(s.id,':',COUNT(h.series_id),':',COALESCE(SUM(h.numeric_value),0)) FROM telemetry_series s
            JOIN device_endpoints e ON e.id=s.endpoint_id LEFT JOIN telemetry_samples h ON h.series_id=s.id
            WHERE e.device_id IN @ids GROUP BY s.id ORDER BY s.id
            """) + "#" + await Text("""
            SELECT CONCAT(r.series_id,':',r.bucket_start_utc,':',r.covered_seconds,':',COALESCE(r.time_weighted_avg,'')) FROM telemetry_rollups_hourly r
            JOIN telemetry_series s ON s.id=r.series_id JOIN device_endpoints e ON e.id=s.endpoint_id WHERE e.device_id IN @ids ORDER BY 1
            """) + "#" + await Text("SELECT CONCAT(id,':',name) FROM devices WHERE id IN @ids ORDER BY id");
        var preserved = await Text("""
            SELECT CONCAT(s.id,':',COUNT(h.series_id)) FROM telemetry_series s JOIN device_endpoints e ON e.id=s.endpoint_id
            LEFT JOIN telemetry_samples h ON h.series_id=s.id WHERE e.device_id IN @preserved GROUP BY s.id ORDER BY s.id
            """) + "#" + await Text("""
            SELECT CONCAT(id,':',device_id,':',valid_from_utc,':',COALESCE(valid_to_utc,'')) FROM logical_device_assignments
            WHERE logical_device_id=@LogicalMixed ORDER BY id
            """) + "#" + await Text("SELECT CONCAT(id,':',action) FROM logical_device_audit WHERE logical_device_id=@LogicalMixed ORDER BY id")
            + "#" + await Text("SELECT CONCAT(id) FROM devices WHERE id IN @preserved ORDER BY id");
        var dashboard = await Text("""
            SELECT CONCAT(w.title,':',v.position,':',v.source_key) FROM dashboard_values v JOIN dashboard_widgets w ON w.id=v.widget_id
            WHERE v.property_id=@Property ORDER BY v.position
            """);
        var seeded = await Text("""
            SELECT CONCAT('i:',installation_key) FROM installations WHERE property_id=@Property
            UNION ALL SELECT CONCAT('c:',provider_code) FROM integration_connections WHERE property_id=@Property
            UNION ALL SELECT CONCAT('r:',room_key) FROM rooms WHERE property_id=@Property
            UNION ALL SELECT CONCAT('p:',id) FROM properties WHERE id=@Property ORDER BY 1
            """);
        var removed = await db.QuerySingleAsync<(long, long, long, long, long, long)>("""
            SELECT
              (SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id
               WHERE s.property_id=@Property AND s.source_key='simulator' AND (s.installation_id IS NOT NULL OR s.endpoint_id IN
                 (SELECT id FROM device_endpoints WHERE device_id=@SimA))),
              (SELECT COUNT(*) FROM telemetry_series s WHERE s.property_id=@Property AND s.source_key='simulator' AND (s.installation_id IS NOT NULL OR
                 s.endpoint_id IN (SELECT id FROM device_endpoints WHERE device_id=@SimA))),
              (SELECT COUNT(*) FROM devices WHERE id=@SimA),
              (SELECT COUNT(*) FROM logical_devices WHERE id=@LogicalSim),
              (SELECT COUNT(*) FROM telemetry_rollups_hourly r JOIN telemetry_series s ON s.id=r.series_id
               WHERE s.property_id=@Property AND s.source_key='simulator' AND (s.installation_id IS NOT NULL OR s.endpoint_id IN
                 (SELECT id FROM device_endpoints WHERE device_id=@SimA))),
              (SELECT COUNT(*) FROM device_endpoints WHERE device_id=@SimA)
            """, new { f.Property, f.SimA, f.LogicalSim });
        return new(real, preserved, dashboard, seeded, removed);
    }

    private static long Count(SimulatorCleanupPlan plan, string table) => plan.Counts.Single(item => item.Table == table).Rows;

    private static async Task<long> PropertyAsync(Database admin)
    {
        await using var db = await admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Cleanup fixture','UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
    }

    private static async Task<(long Device, long Endpoint)> DeviceAsync(Database admin, string provider, long property, string id, Capability capability,
        IReadOnlyList<Sample> samples)
    {
        var writer = new TelemetryWriter(admin, new FixedClock(Now));
        var external = property + "-" + id;
        var inventory = new DiscoveredDevice(external, "Fixture " + id, DeviceKind.Sensor, "", "Fixture", "Model " + id,
            [new DeviceEndpoint("main", "Main", [capability])]);
        foreach (var batch in samples.Chunk(200))
            await writer.StoreCloudDeviceAsync(provider, property, null, inventory,
                new(batch[0].ObservedAt, [new(external, Connectivity.Online, null, [new("main", batch)], batch[^1].ObservedAt)], []), default);
        await using var db = await admin.OpenAsync(default);
        var device = await db.ExecuteScalarAsync<long>("SELECT id FROM devices WHERE property_id=@property AND name=@name",
            new { property, name = inventory.Name });
        return (device, await db.ExecuteScalarAsync<long>("SELECT id FROM device_endpoints WHERE device_id=@device", new { device }));
    }

    private static List<Sample> Every(DateTimeOffset from, DateTimeOffset to, int minutes, Func<DateTimeOffset, Sample> create)
    {
        var samples = new List<Sample>();
        for (var at = from; at < to; at = at.AddMinutes(minutes)) samples.Add(create(at));
        return samples;
    }

    private static TelemetryHoldPolicy Holds(SimulatorOptions simulator) => new(new(), new(), new(), new(), simulator);
    private static TelemetryHistoryReader Reader(SimulatorOptions simulator) =>
        new(Runtime(), new Migrations(Runtime()), Holds(simulator), new TelemetryHistoryOptions(), new FixedClock(Now));
    private static SimulatorDataCleanup Cleanup() => new(Maintenance());

    private static Database Admin() => Open(CleanupMySqlFact.AdminVariable);
    private static Database Runtime() =>
        Environment.GetEnvironmentVariable(CleanupMySqlFact.RuntimeVariable) is { Length: > 0 } ? Open(CleanupMySqlFact.RuntimeVariable) : Admin();
    /// <summary>The cleanup needs DELETE, so it runs as the administrative fixture account rather than the DML-only runtime account.</summary>
    private static Database Maintenance() =>
        Environment.GetEnvironmentVariable(CleanupMySqlFact.MaintenanceVariable) is { Length: > 0 } ? Open(CleanupMySqlFact.MaintenanceVariable) : Admin();

    private static Database Open(string variable)
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException("A disposable logical-device MySQL test database is required."));
        Assert.Equal("homeautomation_logical_test", builder.Database);
        return new Database(builder.ConnectionString);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CleanupMySqlFact : FactAttribute
    {
        public const string AdminVariable = "HOMEAUTOMATION_LOGICAL_TEST_MYSQL";
        public const string RuntimeVariable = "HOMEAUTOMATION_LOGICAL_TEST_RUNTIME";
        public const string MaintenanceVariable = "HOMEAUTOMATION_CLEANUP_TEST_MAINTENANCE";

        public CleanupMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminVariable)))
                Skip = $"Set {AdminVariable} for a disposable homeautomation_logical_test schema.";
        }
    }
}
