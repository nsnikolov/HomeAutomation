using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Property, room and device management on the disposable <c>homeautomation_logical_test</c> schema. The runtime account needs its
/// usual <c>SELECT, INSERT, UPDATE</c> plus the documented <c>DELETE ON rooms</c>. No provider is contacted.
/// </summary>
[Collection(LogicalSchemaCollection.Name)]
public sealed class PropertyManagementMySqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [ManagementMySqlFact]
    public async Task CreatedPropertiesAreEmptyIdempotentAndCreatedOnlyByTheInstallationAdministrator()
    {
        var f = await FixtureAsync();
        var store = Store(f);
        Assert.Equal(ManagementStatus.Forbidden, (await store.CreateAsync(f.Owner, new("Lake house", null, null, null), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await store.CreateAsync(f.Administrator, new(new string('x', 129), null, null, null), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await store.CreateAsync(f.Administrator, new("Lake house", null, "Mars/Olympus", null), default)).Status);
        var request = Guid.NewGuid();
        var created = await store.CreateAsync(f.Administrator, new("Lake house", "Summer place", null, request), default);
        Assert.Equal(ManagementStatus.Done, created.Status);
        var repeated = await Store(f).CreateAsync(f.Administrator, new("Lake house", "Summer place", null, request), default);
        Assert.Equal(created.Id, repeated.Id);
        var id = created.Id!.Value;

        await using var db = await f.Admin.OpenAsync(default);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), await db.QuerySingleAsync<(long, long, long, long, long, long, long)>("""
            SELECT (SELECT COUNT(*) FROM rooms WHERE property_id=@id), (SELECT COUNT(*) FROM devices WHERE property_id=@id),
                   (SELECT COUNT(*) FROM installations WHERE property_id=@id), (SELECT COUNT(*) FROM integration_connections WHERE property_id=@id),
                   (SELECT COUNT(*) FROM logical_devices WHERE property_id=@id), (SELECT COUNT(*) FROM configuration_profiles WHERE scope_id=@id),
                   (SELECT COUNT(*) FROM telemetry_series WHERE property_id=@id)
            """, new { id }));
        Assert.Equal(("Europe/Sofia", "Owner"), await db.QuerySingleAsync<(string, string)>("""
            SELECT p.time_zone, m.role_code FROM properties p JOIN property_memberships m ON m.property_id=p.id WHERE p.id=@id
            """, new { id }));
        var listed = (await store.ListAsync(f.Administrator, default)).Properties.Single(item => item.Id == id);
        Assert.Equal((0, 0, 0, true, "Summer place"), (listed.Rooms, listed.Devices, listed.Installations, listed.CanDelete, listed.Description));
        Assert.DoesNotContain((await store.ListAsync(f.Owner, default)).Properties, item => item.Id == id);
        Assert.False((await store.ListAsync(f.Owner, default)).CanCreate);
        Assert.True((await store.ListAsync(f.Administrator, default)).CanCreate);
    }

    [ManagementMySqlFact]
    public async Task PropertyEditsAndDeletionFollowRolesAndOnlyCurrentDevicesBlockDeletion()
    {
        var f = await FixtureAsync();
        var store = Store(f);
        Assert.Equal(ManagementStatus.Forbidden, (await store.UpdateAsync(f.Viewer, f.Property, new("Renamed", null, "UTC"), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await store.UpdateAsync(f.Editor, f.Property, new("Renamed", "Main home", "Europe/London"), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await store.CreateRoomAsync(f.Owner, f.Property, new("Kitchen", null), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await store.CreateRoomAsync(f.Owner, f.Property, new("Garage", null), default)).Status);
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("""
                INSERT INTO configuration_profiles(scope_id,property_id,provider,revision,poll_seconds,credentials,identifiers)
                VALUES(@Property,@Property,'growatt',1,300,'{}','{}')
                """, new { f.Property });

        var impact = (await store.DeletionImpactAsync(f.Owner, f.Property, default))!;
        Assert.Equal((2, 0, true, 1), (impact.Rooms, impact.Devices, impact.CanDelete, impact.VaultProfiles));
        Assert.False((await store.DeletionImpactAsync(f.Editor, f.Property, default))!.CanDelete);
        Assert.Null(await store.DeletionImpactAsync(f.Stranger, f.Property, default));
        Assert.Equal(ManagementStatus.Forbidden, (await store.DeleteAsync(f.Editor, f.Property, new("Renamed"), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await store.DeleteAsync(f.Owner, f.Property, new("renamed"), default)).Status);

        // A current device blocks deletion: a device row, or a provider assignment not yet synchronized into one.
        var (device, _) = await DeviceAsync(f, "ewelink", "meter", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 100)]);
        Assert.Equal(ManagementStatus.Conflict, (await store.DeleteAsync(f.Owner, f.Property, new("Renamed"), default)).Status);
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("UPDATE devices SET retired_at_utc=@now WHERE id=@device", new { now = Now.UtcDateTime, device });
        await ProviderRowAsync(f, "pending-meter", f.Property, null);
        Assert.Equal(1, (await store.DeletionImpactAsync(f.Owner, f.Property, default))!.Devices);
        Assert.Equal(ManagementStatus.Conflict, (await store.DeleteAsync(f.Owner, f.Property, new("Renamed"), default)).Status);
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("UPDATE ewelink_devices SET property_id=NULL WHERE external_id=@id", new { id = f.Property + "-pending-meter" });

        // Rooms, a device that moved away and a vault profile do not block deletion, and all of them are kept.
        Assert.Equal(1, (await store.DeletionImpactAsync(f.Owner, f.Property, default))!.HistoricalDevices);
        Assert.Equal(ManagementStatus.Done, (await store.DeleteAsync(f.Owner, f.Property, new("Renamed"), default)).Status);
        Assert.Equal(ManagementStatus.NotFound, (await store.DeleteAsync(f.Owner, f.Property, new("Renamed"), default)).Status);
        Assert.DoesNotContain((await store.ListAsync(f.Owner, default)).Properties, item => item.Id == f.Property);
        var reader = new MonitoringReader(Runtime(), new FixedClock(Now), new SimulatorOptions(), new GrowattOptions());
        Assert.False(await reader.CanReadAsync(f.Owner, f.Property, default));
        await using var check = await f.Admin.OpenAsync(default);
        Assert.Equal((2L, 1L, 1L, 1L), await check.QuerySingleAsync<(long, long, long, long)>("""
            SELECT (SELECT COUNT(*) FROM rooms WHERE property_id=@Property), (SELECT COUNT(*) FROM devices WHERE property_id=@Property),
                   (SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.property_id=@Property),
                   (SELECT COUNT(*) FROM configuration_profiles WHERE scope_id=@Property)
            """, new { f.Property }));
        Assert.Contains("propertyDeleted", await check.QueryAsync<string>("SELECT action FROM management_audit WHERE property_id=@Property", new { f.Property }));
    }

    [ManagementMySqlFact]
    public async Task DeletionAndAConcurrentAssignmentAreSerializedOnThePropertyRow()
    {
        // Deletion first: the assignment waits for the property row, then finds the property deleted and changes nothing.
        var f = await FixtureAsync();
        await ProviderRowAsync(f, "late-assign", null, null);
        var store = Store(f);
        Task<int>? assignment = null;
        store.BeforeCommit = async ct =>
        {
            assignment = AssignAsync(f, f.Property + "-late-assign", f.Property);
            await Task.Delay(400, ct);
            Assert.False(assignment.IsCompleted);
        };
        Assert.Equal(ManagementStatus.Done, (await store.DeleteAsync(f.Owner, f.Property, new(f.Name), default)).Status);
        Assert.Equal(0, await assignment!);

        // Assignment first: the deletion waits for it and then sees the device.
        var g = await FixtureAsync();
        await ProviderRowAsync(g, "early-assign", null, null);
        await using var db = await g.Admin.OpenAsync(default);
        await using var tx = await db.BeginTransactionAsync();
        Assert.Equal(1, await db.ExecuteAsync("""
            UPDATE ewelink_devices SET property_id=@Property WHERE external_id=@external
              AND EXISTS(SELECT 1 FROM properties p WHERE p.id=@Property AND p.deleted_at_utc IS NULL)
            """, new { g.Property, external = g.Property + "-early-assign" }, tx));
        var deletion = Store(g).DeleteAsync(g.Owner, g.Property, new(g.Name), default);
        await Task.Delay(400);
        Assert.False(deletion.IsCompleted);
        await tx.CommitAsync();
        Assert.Equal(ManagementStatus.Conflict, (await deletion).Status);
    }

    [ManagementMySqlFact]
    public async Task RoomsAreOptionalAndDeletingOneClearsEveryAssignmentButKeepsDevicesAndHistory()
    {
        var f = await FixtureAsync();
        var store = Store(f);
        Assert.Empty((await store.RoomsAsync(f.Viewer, f.Property, default))!);
        Assert.Null(await store.RoomsAsync(f.Stranger, f.Property, default));
        Assert.Equal(ManagementStatus.Forbidden, (await store.CreateRoomAsync(f.Viewer, f.Property, new("Loft", null), default)).Status);
        var loft = (await store.CreateRoomAsync(f.Editor, f.Property, new("Loft", "Attic"), default)).Id!.Value;
        Assert.Equal(ManagementStatus.Conflict, (await store.CreateRoomAsync(f.Editor, f.Property, new(" loft ", null), default)).Status);
        var cellar = (await store.CreateRoomAsync(f.Editor, f.Property, new("Cellar", null), default)).Id!.Value;
        Assert.Equal(ManagementStatus.Conflict, (await store.UpdateRoomAsync(f.Editor, f.Property, cellar, new("Loft", null), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await store.UpdateRoomAsync(f.Editor, f.Property, loft, new("Attic", "Under the roof"), default)).Status);

        var (device, _) = await DeviceAsync(f, "ewelink", "attic-meter", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 50)]);
        await ProviderRowAsync(f, "attic-meter", f.Property, loft);
        Assert.Equal(ManagementStatus.Done, (await store.SetDeviceRoomAsync(f.Editor, f.Property, device, new(loft), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await store.SetDeviceRoomAsync(f.Editor, f.Property, device, new(loft + 999), default)).Status);
        long logical;
        await using (var db = await f.Admin.OpenAsync(default))
            logical = await db.ExecuteScalarAsync<long>("""
                INSERT INTO logical_devices(property_id,name,kind,room_id,created_at_utc,updated_at_utc,creation)
                VALUES(@Property,'Attic power','SmartSwitch',@loft,@now,@now,'manual'); SELECT LAST_INSERT_ID();
                """, new { f.Property, loft, now = Now.UtcDateTime });
        Assert.Equal(1, (await store.RoomsAsync(f.Viewer, f.Property, default))!.Single(room => room.Id == loft).Devices);

        var samples = await SampleCountAsync(f, f.Property);
        Assert.Equal(ManagementStatus.Forbidden, (await store.DeleteRoomAsync(f.Viewer, f.Property, loft, default)).Status);
        // Without the runtime DELETE grant on rooms the deletion reports the missing grant and rolls back the cleared assignments.
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("REVOKE DELETE ON rooms FROM 'logical_runtime'@'127.0.0.1'");
        try
        {
            var denied = await store.DeleteRoomAsync(f.Editor, f.Property, loft, default);
            Assert.Equal(ManagementStatus.SchemaPending, denied.Status);
            Assert.Contains("DELETE grant", denied.Message);
            await using var db = await f.Admin.OpenAsync(default);
            Assert.Equal((loft, loft), await db.QuerySingleAsync<(long, long)>(
                "SELECT (SELECT room_id FROM devices WHERE id=@device), (SELECT room_id FROM logical_devices WHERE id=@logical)",
                new { device, logical }));
        }
        finally
        {
            await using var db = await f.Admin.OpenAsync(default);
            await db.ExecuteAsync("GRANT DELETE ON rooms TO 'logical_runtime'@'127.0.0.1'");
        }
        Assert.Equal(ManagementStatus.Done, (await store.DeleteRoomAsync(f.Editor, f.Property, loft, default)).Status);
        Assert.Equal(ManagementStatus.NotFound, (await store.DeleteRoomAsync(f.Editor, f.Property, loft, default)).Status);
        await using var check = await f.Admin.OpenAsync(default);
        Assert.Equal((0L, 1L, 1L), await check.QuerySingleAsync<(long, long, long)>("""
            SELECT (SELECT COUNT(*) FROM rooms WHERE id=@loft), (SELECT COUNT(*) FROM devices WHERE id=@device AND property_id=@Property),
                   (SELECT COUNT(*) FROM provider_bindings WHERE device_id=@device)
            """, new { loft, device, f.Property }));
        Assert.Equal((null, null, null), await check.QuerySingleAsync<(long?, long?, long?)>("""
            SELECT (SELECT room_id FROM devices WHERE id=@device), (SELECT room_id FROM logical_devices WHERE id=@logical),
                   (SELECT room_id FROM ewelink_devices WHERE external_id=@external)
            """, new { device, logical, external = f.Property + "-attic-meter" }));
        Assert.Equal(samples, await SampleCountAsync(f, f.Property));
        var audit = (await store.DeviceAuditAsync(f.Viewer, f.Property, device, default))!;
        Assert.Contains(audit, entry => entry.Action == "deviceRoomChanged" && entry.Detail == "Attic");
    }

    [ManagementMySqlFact]
    public async Task TheOneTimeClearingRemovesOnlyRoomAssignmentsAndIsIdempotent()
    {
        var f = await FixtureAsync();
        var store = Store(f);
        var room = (await store.CreateRoomAsync(f.Owner, f.Property, new("Boiler room", null), default)).Id!.Value;
        var (device, _) = await DeviceAsync(f, "ewelink", "boiler", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 900)]);
        await ProviderRowAsync(f, "boiler", f.Property, room);
        await store.SetDeviceRoomAsync(f.Owner, f.Property, device, new(room), default);
        var plan = await store.PlanRoomAssignmentClearingAsync(default);
        Assert.Contains(plan, item => item.Property == f.Property && item.Table == "devices" && item.Rows == 1);
        Assert.Contains(plan, item => item.Property == f.Property && item.Table == "ewelink_devices" && item.Rows == 1);
        var samples = await SampleCountAsync(f, f.Property);

        var failing = Store(f);
        failing.BeforeCommit = _ => throw new InvalidOperationException("Simulated failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ClearRoomAssignmentsAsync("test", default));
        Assert.Contains(await store.PlanRoomAssignmentClearingAsync(default), item => item.Property == f.Property);

        await store.ClearRoomAssignmentsAsync("test", default);
        Assert.DoesNotContain(await store.PlanRoomAssignmentClearingAsync(default), item => item.Property == f.Property);
        Assert.Empty(await store.ClearRoomAssignmentsAsync("test", default));
        await using var check = await f.Admin.OpenAsync(default);
        Assert.Equal((1L, 1L, 1L), await check.QuerySingleAsync<(long, long, long)>("""
            SELECT (SELECT COUNT(*) FROM rooms WHERE id=@room), (SELECT COUNT(*) FROM devices WHERE id=@device AND property_id=@Property),
                   (SELECT COUNT(*) FROM ewelink_devices WHERE external_id=@external AND property_id=@Property)
            """, new { room, device, f.Property, external = f.Property + "-boiler" }));
        Assert.Equal(samples, await SampleCountAsync(f, f.Property));
    }

    [ManagementMySqlFact]
    public async Task ATransferMovesTheProviderDeviceAndKeepsEarlierHistoryWithTheSourceProperty()
    {
        var f = await FixtureAsync();
        var destination = await SecondPropertyAsync(f);
        var store = Store(f);
        var room = (await store.CreateRoomAsync(f.Owner, f.Property, new("Plant", null), default)).Id!.Value;
        var before = Enumerable.Range(0, 6).Select(index => Sample.Numeric(Metric.ActivePower, Now.AddHours(-6 + index), 1000 + index)).ToList();
        var (device, endpoint) = await DeviceAsync(f, "ewelink", "heat-pump", f.Property, before);
        await ProviderRowAsync(f, "heat-pump", f.Property, room, relayWrites: true);
        await store.SetDeviceRoomAsync(f.Owner, f.Property, device, new(room), default);
        Assert.Equal(ManagementStatus.Done, (await store.RenameDeviceAsync(f.Owner, f.Property, device, new("Heat Pump"), default)).Status);
        await new LogicalDeviceStore(Runtime(), new Migrations(Runtime()), Reader(), new FixedClock(Now)).ExecuteInitializationAsync(f.Property, default);
        var vault = await VaultChecksumAsync(f);

        var impact = (await store.TransferImpactAsync(f.Owner, f.Property, device, destination, default))!;
        Assert.Equal(("Heat Pump", "Plant", 1, (string?)null), (impact.Name, impact.CurrentRoom, impact.OpenLogicalAssignments, impact.Blocked));
        Assert.Equal(ManagementStatus.Invalid, (await store.TransferAsync(f.Owner, f.Property, device, new(f.Property), default)).Status);
        Assert.Equal(ManagementStatus.Forbidden, (await store.TransferAsync(f.Viewer, f.Property, device, new(destination), default)).Status);
        Assert.Equal(ManagementStatus.NotFound, (await store.TransferAsync(f.Editor, f.Property, device, new(destination), default)).Status);

        var failing = Store(f);
        failing.BeforeCommit = _ => throw new InvalidOperationException("Simulated failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.TransferAsync(f.Owner, f.Property, device, new(destination), default));
        Assert.Equal(f.Property, await ProviderPropertyAsync(f, "heat-pump"));
        Assert.Equal(0, await DeviceCountAsync(f, destination));

        var moved = await store.TransferAsync(f.Owner, f.Property, device, new(destination), default);
        Assert.Equal(ManagementStatus.Done, moved.Status);
        var target = moved.Id!.Value;
        Assert.Equal(ManagementStatus.NotFound, (await store.TransferAsync(f.Owner, f.Property, device, new(destination), default)).Status);
        await using (var db = await f.Admin.OpenAsync(default))
        {
            Assert.Equal((destination, (long?)null, (bool?)null), await db.QuerySingleAsync<(long, long?, bool?)>(
                "SELECT property_id, room_id, relay_write_enabled FROM ewelink_devices WHERE external_id=@external",
                new { external = f.Property + "-heat-pump" }));
            var source = await db.QuerySingleAsync<(DateTime? Retired, long? Room, string Key)>(
                "SELECT retired_at_utc, room_id, inventory_key FROM devices WHERE id=@device", new { device });
            Assert.NotNull(source.Retired);
            Assert.Null(source.Room);
            var copy = await db.QuerySingleAsync<(long Property, string Key, string? Name, long Endpoints, long Capabilities, long Bindings)>("""
                SELECT d.property_id, d.inventory_key, d.display_name,
                       (SELECT COUNT(*) FROM device_endpoints e WHERE e.device_id=d.id),
                       (SELECT COUNT(*) FROM device_capabilities c JOIN device_endpoints e ON e.id=c.endpoint_id WHERE e.device_id=d.id),
                       (SELECT COUNT(*) FROM provider_bindings b JOIN integration_connections i ON i.id=b.connection_id
                        WHERE b.device_id=d.id AND i.property_id=@destination AND i.provider_code='ewelink' AND b.external_id=@external)
                FROM devices d WHERE d.id=@target
                """, new { target, destination, external = f.Property + "-heat-pump" });
            Assert.Equal((destination, source.Key, "Heat Pump", 1L, 1L, 1L), copy);
            Assert.Equal(before.Count, await db.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.endpoint_id=@endpoint AND s.property_id=@Property
                """, new { endpoint, f.Property }));
            Assert.NotNull(await db.ExecuteScalarAsync<DateTime?>(
                "SELECT valid_to_utc FROM logical_device_assignments WHERE property_id=@Property AND device_id=@device", new { f.Property, device }));
        }
        Assert.Equal(vault, await VaultChecksumAsync(f));

        // The next provider synchronization lands on the destination row: no duplicate, the custom name stays.
        var after = Sample.Numeric(Metric.ActivePower, Now.AddMinutes(5), 1500);
        await DeviceAsync(f, "ewelink", "heat-pump", destination, [after], name: "Provider renamed this");
        Assert.Equal(1, await DeviceCountAsync(f, destination));
        var reader = new MonitoringReader(Runtime(), new FixedClock(Now.AddMinutes(6)), new SimulatorOptions(), new GrowattOptions());
        Assert.DoesNotContain((await reader.StateAsync(f.Property, default)).Devices, item => item.Id == device);
        var landed = Assert.Single((await reader.StateAsync(destination, default)).Devices);
        Assert.Equal((target, "Heat Pump", "Provider renamed this", true), (landed.Id, landed.Name, landed.ProviderName, landed.CustomName));

        // History stays attributed: the source keeps the six earlier readings, the destination only the new one.
        var history = Reader();
        async Task<IReadOnlyList<double?>> Raw(long property)
        {
            var series = (await history.SeriesAsync(property, null, Metric.ActivePower, default)).Single();
            var read = await history.ReadAsync(property, new(new(series.SeriesId, null, null, null, null), HistoryResolution.Raw, Now.AddDays(-1), Now.AddHours(1)), default);
            return read.Result!.Points!.Select(point => point.Number).ToList();
        }
        Assert.Equal(before.Select(sample => sample.Number), await Raw(f.Property));
        Assert.Equal([1500d], await Raw(destination));

        var audit = (await store.DeviceAuditAsync(f.Owner, destination, target, default))!;
        Assert.Contains(audit, entry => entry.Action == "deviceTransferredIn" && entry.OtherPropertyId == f.Property);
        Assert.Contains((await store.DeviceAuditAsync(f.Owner, f.Property, device, default))!,
            entry => entry.Action == "deviceTransferredOut" && entry.OtherPropertyId == destination);

        // Moving back reactivates the original source row, so both properties keep one row each for the device.
        Assert.Equal(device, (await store.TransferAsync(f.Owner, destination, target, new(f.Property), default)).Id);
        Assert.Equal(1, await DeviceCountAsync(f, f.Property));
        Assert.Equal(1, await DeviceCountAsync(f, destination));
    }

    [ManagementMySqlFact]
    public async Task ConcurrentTransfersOfOneDeviceLetExactlyOneSucceedAndGrowattIsRefused()
    {
        var f = await FixtureAsync();
        var second = await SecondPropertyAsync(f);
        var third = await SecondPropertyAsync(f);
        var (device, _) = await DeviceAsync(f, "tuya", "sensor", f.Property, [Sample.Numeric(Metric.Temperature, Now.AddHours(-1), 21)]);
        await ProviderRowAsync(f, "sensor", f.Property, null, table: "tuya_devices");
        var results = await Task.WhenAll(Store(f).TransferAsync(f.Owner, f.Property, device, new(second), default),
            Store(f).TransferAsync(f.Owner, f.Property, device, new(third), default));
        Assert.Single(results, result => result.Status == ManagementStatus.Done);
        Assert.Single(results, result => result.Status is ManagementStatus.NotFound or ManagementStatus.Conflict);
        Assert.Equal(1, await DeviceCountAsync(f, second) + await DeviceCountAsync(f, third));

        var (growatt, _) = await DeviceAsync(f, "growatt", "inverter", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 3000)]);
        var refused = await Store(f).TransferAsync(f.Owner, f.Property, growatt, new(second), default);
        Assert.Equal(ManagementStatus.Conflict, refused.Status);
        Assert.Contains("Growatt", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await Store(f).TransferableAsync(f.Owner, second, default), item => item.DeviceId == growatt);
        Assert.Empty(await Store(f).TransferableAsync(f.Viewer, f.Property, default));
    }

    [ManagementMySqlFact]
    public async Task CustomNamesSurviveProviderSynchronizationAndCanBeCleared()
    {
        var f = await FixtureAsync();
        var store = Store(f);
        var (device, _) = await DeviceAsync(f, "ewelink", "kitchen", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-2), 10)]);
        Assert.Equal(ManagementStatus.Forbidden, (await store.RenameDeviceAsync(f.Viewer, f.Property, device, new("Kitchen AC"), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await store.RenameDeviceAsync(f.Owner, f.Property, device, new(new string('n', 129)), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await store.RenameDeviceAsync(f.Owner, f.Property, device, new("  Kitchen AC  "), default)).Status);
        await DeviceAsync(f, "ewelink", "kitchen", f.Property, [Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 12)], name: "Provider name 2");
        await using var db = await f.Admin.OpenAsync(default);
        Assert.Equal(("Kitchen AC", "Provider name 2"), await db.QuerySingleAsync<(string, string)>(
            "SELECT display_name, name FROM devices WHERE id=@device", new { device }));
        Assert.Equal(ManagementStatus.Done, (await store.RenameDeviceAsync(f.Owner, f.Property, device, new(" "), default)).Status);
        Assert.Null(await db.ExecuteScalarAsync<string?>("SELECT display_name FROM devices WHERE id=@device", new { device }));
        Assert.Equal(2, (await store.DeviceAuditAsync(f.Viewer, f.Property, device, default))!.Count(entry => entry.Action == "deviceRenamed"));
        Assert.Null(await store.DeviceAuditAsync(f.Stranger, f.Property, device, default));
    }

    private sealed record Fixture(Database Admin, long Property, string Name, string Owner, string Editor, string Viewer, string Stranger,
        string Administrator);

    private static async Task<Fixture> FixtureAsync()
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        await using var db = await admin.OpenAsync(default);
        var name = "Management fixture " + Guid.NewGuid().ToString("N")[..8];
        var property = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,@name,'UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N"), name });
        var users = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        foreach (var user in users)
            await db.ExecuteAsync("""
                INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
                VALUES(@user,CONCAT(@user,'@example.test'),UPPER(CONCAT(@user,'@example.test')),'x','x','x')
                """, new { user });
        await db.ExecuteAsync("""
            INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@owner,'Owner'),(@property,@editor,'Admin'),(@property,@viewer,'Viewer')
            """, new { property, owner = users[0], editor = users[1], viewer = users[2] });
        return new(admin, property, name, users[0], users[1], users[2], users[3], users[4]);
    }

    /// <summary>Another property where the fixture Owner is Owner and the Viewer is Viewer, but the Admin is not a member.</summary>
    private static async Task<long> SecondPropertyAsync(Fixture f)
    {
        await using var db = await f.Admin.OpenAsync(default);
        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Second fixture','UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
        await db.ExecuteAsync("INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@id,@Owner,'Owner'),(@id,@Viewer,'Viewer')",
            new { id, f.Owner, f.Viewer });
        return id;
    }

    private static async Task<(long Device, long Endpoint)> DeviceAsync(Fixture f, string provider, string id, long property,
        IReadOnlyList<Sample> samples, string? name = null)
    {
        var writer = new TelemetryWriter(f.Admin, new FixedClock(Now));
        var external = f.Property + "-" + id;
        var capability = samples[0].Metric == Metric.Temperature ? Capability.TemperatureMeasurement : Capability.PowerMeasurement;
        var inventory = new DiscoveredDevice(external, name ?? "Fixture " + id, DeviceKind.SmartSwitch, "", "Fixture", "Model " + id,
            [new DeviceEndpoint("main", "Main", [capability])]);
        foreach (var sample in samples)
            await writer.StoreCloudDeviceAsync(provider, property, null, inventory,
                new(sample.ObservedAt, [new(external, Connectivity.Online, null, [new("main", [sample])], sample.ObservedAt)], []), default);
        await using var db = await f.Admin.OpenAsync(default);
        var device = await db.ExecuteScalarAsync<long>("""
            SELECT d.id FROM devices d JOIN provider_bindings b ON b.device_id=d.id WHERE d.property_id=@property AND b.external_id=@external
            """, new { property, external });
        return (device, await db.ExecuteScalarAsync<long>("SELECT id FROM device_endpoints WHERE device_id=@device", new { device }));
    }

    private static async Task ProviderRowAsync(Fixture f, string id, long? property, long? room, bool relayWrites = false,
        string table = "ewelink_devices")
    {
        await using var db = await f.Admin.OpenAsync(default);
        var owner = table == "tuya_devices" ? "project_scope" : "owner_id";
        var extra = table == "ewelink_devices" ? ",relay_write_enabled" : "";
        var value = table == "ewelink_devices" ? ",@relayWrites" : "";
        await db.ExecuteAsync($"""
            INSERT INTO {table}(public_id,{owner},external_id,property_id,room_id,snapshot{extra})
            VALUES(@publicId,@scope,@external,@property,@room,@snapshot{value})
            """, new { publicId = Guid.NewGuid().ToString("N"), scope = "fixture-" + f.Property, external = f.Property + "-" + id, property, room,
                relayWrites, snapshot = "{}" });
    }

    private static async Task<int> AssignAsync(Fixture f, string external, long property)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return await db.ExecuteAsync("""
            UPDATE ewelink_devices SET property_id=@property WHERE external_id=@external AND property_id IS NULL
              AND EXISTS(SELECT 1 FROM properties p WHERE p.id=@property AND p.deleted_at_utc IS NULL)
            """, new { property, external });
    }

    private static async Task<long?> ProviderPropertyAsync(Fixture f, string id)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long?>("SELECT property_id FROM ewelink_devices WHERE external_id=@external", new { external = f.Property + "-" + id });
    }

    private static async Task<int> DeviceCountAsync(Fixture f, long property)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM devices WHERE property_id=@property", new { property });
    }

    private static async Task<long> SampleCountAsync(Fixture f, long property)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.property_id=@property", new { property });
    }

    private static async Task<string> VaultChecksumAsync(Fixture f)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return string.Join("|", await db.QueryAsync<string>(
            "SELECT CONCAT(scope_id,':',provider,':',revision,':',SHA2(credentials,256)) FROM configuration_profiles ORDER BY scope_id, provider"));
    }

    private static PropertyManagementStore Store(Fixture f) =>
        new(Runtime(), new ConfigurationVaultOptions { AdministratorUserId = f.Administrator }, new FixedClock(Now));

    private static TelemetryHistoryReader Reader() =>
        new(Runtime(), new Migrations(Runtime()), new TelemetryHoldPolicy(new(), new(), new(), new(), new()), new TelemetryHistoryOptions(),
            new FixedClock(Now.AddHours(1)));

    private static Database Admin() => Open("HOMEAUTOMATION_LOGICAL_TEST_MYSQL");
    private static Database Runtime() =>
        Environment.GetEnvironmentVariable("HOMEAUTOMATION_LOGICAL_TEST_RUNTIME") is { Length: > 0 } ? Open("HOMEAUTOMATION_LOGICAL_TEST_RUNTIME") : Admin();

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

    private sealed class ManagementMySqlFact : FactAttribute
    {
        public ManagementMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOMEAUTOMATION_LOGICAL_TEST_MYSQL")))
                Skip = "Set HOMEAUTOMATION_LOGICAL_TEST_MYSQL for a disposable homeautomation_logical_test schema.";
        }
    }
}
