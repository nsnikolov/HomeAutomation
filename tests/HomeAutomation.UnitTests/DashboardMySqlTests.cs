using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class DashboardMySqlTests
{
    private const string ConnectionVariable = "HOMEAUTOMATION_DASHBOARD_TEST_MYSQL";

    [DashboardMySqlFact]
    public async Task LayoutEnforcesPropertyAndRoleKeepsOrderingAndRetainsDeletedSources()
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        Assert.Equal("homeautomation_dashboard_test", connection.Database);
        Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1" });
        var database = new Database(connection.ConnectionString);
        var migrations = new Migrations(database);
        await migrations.ApplyAsync(default);
        await migrations.ApplyAsync(default);
        var tuya = new TuyaOptions
        {
            AccessId = Guid.NewGuid().ToString("N"), AccessSecret = "fixture-secret", UserId = "fixture",
            OperatorEmail = "fixture@example.test", DataCenter = "central-europe"
        };
        var dashboard = new MonitoringDashboardStore(database, migrations, tuya);
        Assert.True(await dashboard.SchemaReadyAsync(default));

        await using var db = await database.OpenAsync(default);
        var first = await Property("First");
        var second = await Property("Second");
        var editor = await Member(first, "Operator");
        var viewer = await Member(first, "Viewer");
        var otherEditor = await Member(second, "Owner");
        var at = new DateTimeOffset(2026, 11, 24, 10, 30, 0, TimeSpan.Zero);
        var growatt = await WriteInverter(first, 0);
        var other = await WriteInverter(second, 9);
        var electrical = await WriteSwitch(first);
        var daikinId = await WriteClimate(first);
        var tuyaId = await WriteThermometer(first);

        var firstCatalog = (await dashboard.CatalogAsync(first, editor, default))!;
        var inverterSource = Assert.Single(firstCatalog, item => item.Provider == "growatt");
        Assert.Equal(growatt, inverterSource.DeviceId);
        Assert.DoesNotContain(firstCatalog, item => item.DeviceId == other);
        var foreignSource = Assert.Single((await dashboard.CatalogAsync(second, otherEditor, default))!);
        Assert.Null(await dashboard.CatalogAsync(first, viewer, default));
        var initial = await dashboard.ReadAsync(first, editor, default);
        Assert.True(initial.CanEdit);
        Assert.Empty(initial.Groups);
        var relay = Assert.Single(firstCatalog, item => item.DeviceId == electrical && item.Metric == Metric.SwitchOn);
        var power = Assert.Single(firstCatalog, item => item.DeviceId == electrical && item.Metric == Metric.ActivePower);
        Assert.Equal("W", power.Unit);
        Assert.Equal("serverReceived", power.Reading!.Provenance);
        Assert.Equal(at.AddMinutes(-1), power.Reading.ObservedAt);
        Assert.Equal(false, relay.Reading!.Flag);
        Assert.Equal("provider", relay.Reading.Provenance);
        var setpoint = Assert.Single(firstCatalog, item => item.DeviceId == daikinId && item.Metric == Metric.TargetTemperature);
        Assert.Equal(22.5, setpoint.Reading!.Number);
        Assert.Equal(at.AddMinutes(-4), setpoint.Reading.ObservedAt);
        Assert.Equal("provider", setpoint.Reading.Provenance);
        var temperature = Assert.Single(firstCatalog, item => item.DeviceId == tuyaId);
        Assert.Equal(19.6, temperature.Reading!.Number);
        Assert.Equal(at.AddMinutes(-2), temperature.Reading.ObservedAt);
        Assert.Equal("provider", temperature.Reading.Provenance);

        var mixed = new DashboardWidget(Id(), "Mixed", "compact", relay.Key,
            [new(Id(), inverterSource.Key, "Solar"), new(Id(), relay.Key, null), new(Id(), power.Key, null)]);
        var climate = new DashboardGroup(Id(), "Climate", false, true, []);
        var electricalGroup = new DashboardGroup(Id(), "Electrical", false, true, [mixed]);
        Assert.Equal(DashboardSaveResult.Forbidden, await dashboard.ReplaceAsync(first, viewer,
            new(initial.Revision, [electricalGroup]), default));
        Assert.Equal(DashboardSaveResult.Forbidden, await dashboard.ReplaceAsync(first, otherEditor,
            new(initial.Revision, [electricalGroup]), default));
        var foreign = mixed with { Values = [new(Id(), foreignSource.Key, null)], NavigationSource = null };
        Assert.Equal(DashboardSaveResult.InvalidSource, await dashboard.ReplaceAsync(first, editor,
            new(initial.Revision, [electricalGroup with { Widgets = [foreign] }]), default));
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ReplaceAsync(first, editor,
            new(initial.Revision, [electricalGroup, climate]), default));
        Assert.Equal(DashboardSaveResult.Conflict, await dashboard.ReplaceAsync(first, editor,
            new(initial.Revision, []), default));
        var saved = await dashboard.ReadAsync(first, viewer, default);
        Assert.False(saved.CanEdit);
        Assert.Equal(["Electrical", "Climate"], saved.Groups.Select(group => group.Title));
        Assert.Equal(mixed.Values, saved.Groups[0].Widgets[0].Values);
        Assert.Equal(3, saved.Sources.Length);
        Assert.Equal(0, Assert.Single(saved.Sources, item => item.Key == inverterSource.Key).Reading!.Number);
        Assert.Empty((await dashboard.ReadAsync(second, otherEditor, default)).Groups);

        await db.ExecuteAsync("DELETE FROM ewelink_devices WHERE property_id=@first AND public_id=@electrical", new { first, electrical });
        var missing = await dashboard.ReadAsync(first, editor, default);
        Assert.Equal(3, missing.Groups[0].Widgets[0].Values.Length);
        Assert.DoesNotContain(missing.Sources, item => item.Key == power.Key || item.Key == relay.Key);
        var reordered = mixed with { Title = "Renamed", Mode = "detailed", Values = [.. mixed.Values.Reverse()] };
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ReplaceAsync(first, editor,
            new(missing.Revision, [climate, electricalGroup with { Widgets = [reordered] }]), default));
        var updated = await dashboard.ReadAsync(first, editor, default);
        Assert.Equal(["Climate", "Electrical"], updated.Groups.Select(group => group.Title));
        Assert.Equal(reordered.Values, updated.Groups[1].Widgets[0].Values);
        Assert.Equal("Renamed", updated.Groups[1].Widgets[0].Title);
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ReplaceAsync(first, editor, new(updated.Revision, []), default));
        Assert.Empty((await dashboard.ReadAsync(first, editor, default)).Groups);
        return;

        async Task<long> Property(string title) => await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,@title,'Europe/Sofia');
            SELECT LAST_INSERT_ID();
            """, new { key = Id(), title });

        async Task<string> Member(long property, string role)
        {
            var id = Id();
            await db.ExecuteAsync("""
                INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
                VALUES(@id,@email,@email,'fixture','fixture','fixture');
                INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@id,@role);
                """, new { property, role, id, email = id + "@example.test" });
            return id;
        }

        async Task<string> WriteInverter(long property, double watts)
        {
            var external = Id();
            var writer = new TelemetryWriter(database, TimeProvider.System);
            var model = new DiscoveredDevice(external, "Fixture inverter", DeviceKind.Inverter, "", "Growatt", "SPF",
                [new("main", "Main", [Capability.SolarProduction])]);
            var batch = new ObservationBatch(at, [new(external, Connectivity.Online, null,
                [new("main", [Sample.Numeric(Metric.PvPower, at, watts)])], at)], []);
            await writer.StoreCloudDeviceAsync("growatt", property, null, model, batch, default);
            var id = await db.QuerySingleAsync<long>("SELECT id FROM devices WHERE property_id=@property", new { property });
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        async Task<string> WriteSwitch(long property)
        {
            const string owner = "dashboard-fixture";
            var store = new EwelinkStore(database, migrations);
            await store.SaveTokensAsync(owner, null, default);
            var device = new EwelinkDevice(Id(), "Fixture switch", "PSF-X67", "Sonoff", false, Connectivity.Online,
                at.AddMinutes(-3), true, true, at.AddMinutes(-3),
                new Dictionary<Metric, EwelinkMeasurement>
                {
                    [Metric.ActivePower] = new(4.2, at.AddMinutes(-1), FromSocket: true, ServerReceived: true)
                }, SocketRelay: false, SocketRelayAt: at.AddMinutes(-2));
            await store.SaveAsync(owner, device, default);
            var row = (await store.DevicesAsync(owner, default)).Single(item => item.ExternalId == device.ExternalId);
            await store.AssignAsync(row, owner, property, null, default);
            return row.Id;
        }

        async Task<string> WriteClimate(long property)
        {
            const string owner = "dashboard-fixture";
            var store = new DaikinStore(database, TimeProvider.System, new DaikinOptions());
            await store.ClaimAsync(owner, default);
            var device = new DaikinDevice(Guid.NewGuid().ToString("N"), "climate", "Fixture AC", "dx4",
                Connectivity.Online, at, at.AddMinutes(-4), "on", "heating", 20, null, 22.5m,
                null, null, null, DaikinMapping.ReadOnlyControls, null, ProviderReceivedAt: at);
            await store.SaveSnapshotAsync(owner, device, default);
            var row = (await store.DevicesAsync(owner, default)).Single(item => item.ExternalId == device.ExternalId);
            Assert.True(await store.AssignAsync(owner, row.Id, property, null, default));
            return row.Id;
        }

        async Task<string> WriteThermometer(long property)
        {
            var store = new TuyaStore(database, tuya);
            var id = Guid.NewGuid().ToString("N");
            var device = new TuyaDevice(id, "Fixture thermometer", "fixture", "Fixture", Connectivity.Online, at, at.AddMinutes(-7),
                at, true, [new("temp", Metric.Temperature, Capability.TemperatureMeasurement, "C", 1, null, null)],
                [new("temp", 18.7, null, at.AddMinutes(-7), true)]);
            await store.SaveAsync(device, default);
            var row = Assert.Single(await store.DevicesAsync(default));
            await store.AssignAsync(row.Id, property, null, default);
            var (inventory, observations) = device.Telemetry(sensorReports: new Dictionary<string, Sample>
            {
                ["temp"] = Sample.Numeric(Metric.Temperature, at.AddMinutes(-2), 19.6)
            });
            await new TelemetryWriter(database, TimeProvider.System)
                .StoreCloudDeviceAsync("tuya", property, null, inventory, observations, default, "tuya-sensor-report");
            return row.Id;
        }
    }

    private static string Id() => Guid.NewGuid().ToString("D");

    private sealed class DashboardMySqlFact : FactAttribute
    {
        public DashboardMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable homeautomation_dashboard_test schema.";
        }
    }
}
