using System.Transactions;
using Dapper;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class TuyaMySqlTests
{
    [MySqlFact]
    public async Task SyncUpsertPreservesLastSuccessOnFailureAndUpdatesItOnRecovery()
    {
        var store = new TuyaStore(TestDatabase(), TestProject());
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var first = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        await store.ReportAsync("syncing", null, first, null, ct);
        Assert.Null((await store.StatusAsync(ct))!.LastSuccess);
        await store.ReportAsync("connected", first, first.AddMinutes(30), null, ct);
        await store.ReportAsync("rateLimited", null, first.AddHours(1), 1199, ct);
        var failed = (await store.StatusAsync(ct))!;
        Assert.Equal(new("rateLimited", 1199, first.UtcDateTime, first.AddHours(1).UtcDateTime), failed);

        var nextSuccess = first.AddHours(1);
        await store.ReportAsync("connected", nextSuccess, nextSuccess.AddMinutes(30), null, ct);
        Assert.Equal(new("connected", null, nextSuccess.UtcDateTime, nextSuccess.AddMinutes(30).UtcDateTime),
            await store.StatusAsync(ct));
    }

    [MySqlFact]
    public async Task DeviceOrderAndHiddenStateSurviveRediscoveryAndStayInTheirPropertyUserAndPage()
    {
        var db = TestDatabase();
        var store = new TuyaStore(db, TestProject());
        var preferences = new DeviceViewPreferences(db);
        var property = await CreatePropertyAsync(db);
        var other = await CreatePropertyAsync(db);
        var member = await AddMemberAsync(db, property);
        var otherMember = await AddMemberAsync(db, property);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var device = Device("fixture", now);
        await store.SaveAsync(device, default);
        var id = Assert.Single(await store.DevicesAsync(default)).Id;
        await store.AssignAsync(id, property, null, default);
        var second = Guid.NewGuid().ToString("N");

        await preferences.OrderAsync(property, member, "tuya", "assigned", [second, id], default);
        await preferences.VisibilityAsync(property, member, id, true, default);
        await preferences.OrderAsync(property, member, "tuya", "unassigned", [id], default);
        await store.SaveAsync(device with { Name = "Rediscovered" }, default);
        Assert.Equal(id, Assert.Single(await store.DevicesAsync(default)).Id);
        var saved = await preferences.ReadAsync(property, member, "tuya", default);
        Assert.Equal([second, id], saved.Order["assigned"]);
        Assert.Equal([id], saved.Order["unassigned"]);
        Assert.Equal([id], saved.Hidden);
        Assert.Empty((await preferences.ReadAsync(property, otherMember, "tuya", default)).Hidden);
        Assert.Empty((await preferences.ReadAsync(other, member, "tuya", default)).Order);
        Assert.Empty((await preferences.ReadAsync(property, member, "devices", default)).Order);

        await preferences.VisibilityAsync(property, member, id, false, default);
        var restored = await preferences.ReadAsync(property, member, "tuya", default);
        Assert.Empty(restored.Hidden);
        Assert.Equal(saved.Order["assigned"], restored.Order["assigned"]);
    }

    [MySqlFact]
    public async Task HistoryDeduplicatesReportsAndExcludesOtherPropertiesSourcesAndTimes()
    {
        const string source = "tuya-sensor-report";
        var db = TestDatabase();
        var store = new TuyaStore(db, TestProject());
        var writer = new TelemetryWriter(db, TimeProvider.System);
        var history = new SensorHistoryReader(db);
        var property = await CreatePropertyAsync(db);
        var other = await CreatePropertyAsync(db);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var device = Device("fixture", now);
        await store.SaveAsync(device, default);
        var id = Assert.Single(await store.DevicesAsync(default)).Id;
        Assert.Null(await store.FindIdentityAsync(id, property, default));
        Assert.Equal(device.Id, await store.FindIdentityAsync(id, property, default, allowUnassigned: true));
        await store.AssignAsync(id, property, null, default);
        Assert.Null(await store.FindIdentityAsync(id, other, default, allowUnassigned: true));
        Assert.Equal(device.Id, await store.FindIdentityAsync(id, property, default));

        await WriteAsync(property, now.AddDays(-2), 19, source);
        await WriteAsync(property, now.AddHours(-1), 20, source);
        await WriteAsync(property, now.AddHours(-1), 99, source);
        await WriteAsync(property, now.AddMinutes(-30), 91, null);
        await WriteAsync(property, now.AddMinutes(1), 90, source);
        await WriteAsync(other, now.AddHours(-1), 77, source);
        var actual = Assert.Single(await history.ReadAsync(property, "tuya", device.Id, source, now.AddDays(-1), now, default));
        Assert.Equal("°C", actual.Unit);
        var point = Assert.Single(actual.Points);
        Assert.Equal(20, point.Number);
        Assert.Equal(now.AddHours(-1), point.ObservedAt);
        Assert.Equal(2, Assert.Single(await history.ReadAsync(property, "tuya", device.Id, source, now.AddDays(-7), now, default)).Points.Count);
        return;

        async Task WriteAsync(long target, DateTimeOffset at, double value, string? sampleSource)
        {
            var (inventory, batch) = device.Telemetry(sensorReports: new Dictionary<string, Sample>
            {
                ["temp_current"] = Sample.Numeric(Metric.Temperature, at, value)
            });
            await writer.StoreCloudDeviceAsync("tuya", target, null, inventory, batch, default, sampleSource);
        }
    }

    private static TuyaDevice Device(string id, DateTimeOffset now) =>
        new(id, "Fixture", "fixture-product", "Fixture", Connectivity.Online, now, now, now, true,
            [new("temp_current", Metric.Temperature, Capability.TemperatureMeasurement, "C", 0, null, null)], []);

    private static async Task<long> CreatePropertyAsync(Database db)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Fixture','UTC');
            SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
    }

    private static async Task<string> AddMemberAsync(Database db, long property)
    {
        var id = Guid.NewGuid().ToString("D");
        await using var connection = await db.OpenAsync(default);
        await connection.ExecuteAsync("""
            INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
            VALUES(@id,@email,@email,'fixture','fixture','fixture');
            INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@id,'viewer');
            """, new { property, id, email = id + "@example.test" });
        return id;
    }

    private static Database TestDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable(MySqlFact.ConnectionVariable) ??
            throw new InvalidOperationException("A disposable Tuya MySQL test database is required.");
        var connection = new MySqlConnectionStringBuilder(connectionString) { UseXaTransactions = false };
        Assert.Equal("homeautomation_tuya_test", connection.Database);
        return new Database(connection.ConnectionString);
    }

    private static TuyaOptions TestProject() =>
        new() { AccessId = Guid.NewGuid().ToString("N"), UserId = "fixture", DataCenter = "central-europe" };

    private sealed class MySqlFact : FactAttribute
    {
        public const string ConnectionVariable = "HOMEAUTOMATION_TUYA_TEST_MYSQL";

        public MySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} for a disposable homeautomation_tuya_test schema with the repository migrations.";
        }
    }
}
