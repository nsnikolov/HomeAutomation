using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Milestone 7E on the disposable <c>homeautomation_logical_test</c> schema: automatic provisioning, lifecycle management and the
/// explicit resolution of an auto-created identity when its hardware replaces another device. No provider is contacted.
/// </summary>
[Collection(LogicalSchemaCollection.Name)]
public sealed class LogicalDeviceManagementMySqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutover = new DateTimeOffset(2026, 11, 26, 14, 37, 12, TimeSpan.Zero).AddTicks(3_456_780);

    [LogicalMySqlFact]
    public async Task Migration019RecordsHowExistingIdentitiesWereCreatedAndAllowsTheNewAuditActions()
    {
        var fixture = await FixtureAsync();
        var (device, _) = await DeviceAsync(fixture, "tuya", "upgrade", [("main", [Capability.TemperatureMeasurement])], []);
        var store = Store();
        await store.ExecuteInitializationAsync(fixture.Property, default);
        Assert.Equal(LogicalChangeStatus.Done, (await store.CreateAsync(fixture.Property, fixture.Owner,
            new("Spare", "Sensor", null, null, null, null, null, null, null), default)).Status);
        var admin = fixture.Admin;
        // Return the schema to its 018 shape with the identities still present, then upgrade it.
        await using (var db = await admin.OpenAsync(default))
            await db.ExecuteAsync("""
                ALTER TABLE logical_devices DROP CHECK ck_logical_device_creation;
                ALTER TABLE logical_devices DROP COLUMN creation;
                ALTER TABLE logical_device_audit DROP CHECK ck_logical_audit_action;
                ALTER TABLE logical_device_audit ADD CONSTRAINT ck_logical_audit_action CHECK (action IN
                    ('created', 'initialized', 'updated', 'assigned', 'replaced', 'corrected', 'archived')) NOT ENFORCED;
                DELETE FROM schema_migrations WHERE name LIKE '%.019_logical_device_management.sql';
                """);
        Assert.False(await new Migrations(Runtime()).VerifyLogicalDevicesAsync(default));
        Assert.Equal(LogicalChangeStatus.SchemaPending, (await Store().CreateAsync(fixture.Property, fixture.Owner,
            new("Blocked", "Sensor", null, null, null, null, null, null, null), default)).Status);
        Assert.Empty(await Store().ProvisioningCandidatesAsync(default));
        var before = await LedgerAsync(admin);

        await new Migrations(admin).ApplyAsync(default);

        Assert.Equal(before, (await LedgerAsync(admin)).Where(item => !item.Name.EndsWith(".019_logical_device_management.sql")).ToList());
        Assert.True(await new Migrations(Runtime()).VerifyLogicalDevicesAsync(default));
        var list = await Store().ListAsync(fixture.Property, fixture.Owner, false, default);
        Assert.Equal("initialized", list.Devices.Single(item => item.Current.Any(current => current.DeviceId == device)).Creation);
        Assert.Equal("manual", list.Devices.Single(item => item.Name == "Spare").Creation);
    }

    [LogicalMySqlFact]
    public async Task ProvisioningCreatesOneIdentityPerNewDeviceExactlyOnceAndOnlyForPropertiesUsingLogicalDevices()
    {
        var fixture = await FixtureAsync();
        var other = await FixtureAsync();
        var (initial, _) = await DeviceAsync(fixture, "tuya", "first", [("main", [Capability.TemperatureMeasurement])],
            [("main", Sample.Numeric(Metric.Temperature, Now.AddDays(-3), 19))]);
        var (foreign, _) = await DeviceAsync(other, "tuya", "foreign", [("main", [Capability.TemperatureMeasurement])], []);
        var store = Store();
        Assert.Equal(0, await store.ProvisionAsync(fixture.Property, default));
        Assert.DoesNotContain(fixture.Property, await store.ProvisioningCandidatesAsync(default));

        // Initialization stays the explicit onboarding step; afterwards new hardware is covered automatically.
        Assert.Equal(1, await store.ExecuteInitializationAsync(fixture.Property, default));
        var (thermometer, _) = await DeviceAsync(fixture, "tuya", "thermometer", [("main", [Capability.TemperatureMeasurement, Capability.HumidityMeasurement])],
            [("main", Sample.Numeric(Metric.Temperature, Now.AddDays(-2), 21))]);
        var (strip, endpoints) = await DeviceAsync(fixture, "ewelink", "strip",
            [("left", [Capability.PowerMeasurement]), ("right", [Capability.PowerMeasurement])], []);
        var (faultOnly, _) = await DeviceAsync(fixture, "daikin", "faults", [("main", [Capability.FaultStatus])], []);
        var (twoStreams, _) = await DeviceAsync(fixture, "tuya", "streams", [("main", [Capability.TemperatureMeasurement])],
            [("main", Sample.Numeric(Metric.Temperature, Now.AddHours(-2), 20))]);
        await DeviceAsync(fixture, "tuya", "streams", [("main", [Capability.TemperatureMeasurement])],
            [("main", Sample.Numeric(Metric.Temperature, Now.AddHours(-1), 20.5))], TuyaService.SensorReportSource);
        Assert.Contains(fixture.Property, await store.ProvisioningCandidatesAsync(default));

        // Two sweeps at once, as after a restart racing a second instance: the property lock and unique origin key decide.
        var counts = await Task.WhenAll(Store().ProvisionAsync(fixture.Property, default), Store().ProvisionAsync(fixture.Property, default));
        Assert.Equal(3, counts.Sum());
        Assert.Equal(0, await Store().ProvisionAsync(fixture.Property, default));
        Assert.Equal(0, await Store().ProvisionAsync(other.Property, default));

        var list = (await store.ListAsync(fixture.Property, fixture.Viewer, false, default)).Devices;
        Assert.Equal(4, list.Count);
        LogicalDeviceView For(long device) => Assert.Single(list, item => item.Current.Any(current => current.DeviceId == device));
        Assert.Equal("initialized", For(initial).Creation);
        var automatic = new[] { thermometer, strip, twoStreams }.Select(For).ToList();
        Assert.All(automatic, item => Assert.Equal("automatic", item.Creation));
        Assert.All(automatic.SelectMany(item => item.Current), current =>
        {
            Assert.Equal(LogicalDeviceRules.HistoryOrigin, current.ValidFrom.UtcDateTime);
            Assert.Equal(LogicalDeviceStore.ProvisioningActor, current.AssignedBy);
        });
        Assert.Equal([endpoints["left"], endpoints["right"]], For(strip).Current.Select(current => current.EndpointId!.Value).Order());
        Assert.Single(For(twoStreams).Current);
        Assert.DoesNotContain(list, item => item.Current.Any(current => current.DeviceId == faultOnly));
        Assert.Equal("Fixture thermometer", For(thermometer).Name);
        var audit = (await store.GetAsync(fixture.Property, fixture.Owner, For(thermometer).Id, default))!.Audit.Single();
        Assert.Equal(("provisioned", LogicalDeviceStore.ProvisioningActor, (long?)thermometer), (audit.Action, audit.Actor, audit.NewDeviceId));

        // Existing history stays attributed: the identity reads the device's earlier observation.
        var history = await store.HistoryAsync(fixture.Property, For(thermometer).Id, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, Now.AddDays(-3), Now), default);
        Assert.Equal(21, Assert.Single(history.Result!.Points!).Number);
        Assert.Empty((await store.ListAsync(other.Property, other.Owner, true, default)).Devices);
        Assert.Contains((await store.PhysicalDevicesAsync(other.Property, other.Owner, default)).Devices, item => item.Id == foreign && !item.EverAssigned);

        // Unassigned hardware keeps its history and is never provisioned again.
        var current = For(thermometer).Current.Single();
        Assert.Equal(LogicalChangeStatus.Done, (await store.UnassignAsync(fixture.Property, fixture.Owner, For(thermometer).Id,
            new("main", current.Id, Now.AddHours(-1), "Moved to the shed"), default)).Status);
        Assert.Equal(0, await Store().ProvisionAsync(fixture.Property, default));
        var physical = (await store.PhysicalDevicesAsync(fixture.Property, fixture.Viewer, default)).Devices.Single(item => item.Id == thermometer);
        Assert.Equal((null, true), (physical.LogicalDeviceId, physical.EverAssigned));
    }

    [LogicalMySqlFact]
    public async Task ProvisioningFailureCommitsNothingBacksOffAndSucceedsLater()
    {
        var fixture = await FixtureAsync();
        await DeviceAsync(fixture, "tuya", "seed", [("main", [Capability.TemperatureMeasurement])], []);
        await Store().ExecuteInitializationAsync(fixture.Property, default);
        var (late, _) = await DeviceAsync(fixture, "tuya", "late", [("main", [Capability.TemperatureMeasurement])], []);
        var clock = new SettableClock(Now);
        var provisioning = new LogicalProvisioning(clock, NullLogger<LogicalProvisioning>.Instance);
        var failing = Store();
        failing.BeforeCommit = _ => throw new InvalidOperationException("Simulated database failure");

        Assert.True((await provisioning.SweepAsync(failing, default)).Failed >= 1);
        Assert.False(await CoveredAsync(fixture, late));
        // Inside the back-off window the property is skipped, even with a healthy store.
        await provisioning.SweepAsync(Store(), default);
        Assert.False(await CoveredAsync(fixture, late));
        clock.Now = Now + LogicalProvisioning.BaseBackoff;
        await provisioning.SweepAsync(Store(), default);
        Assert.True(await CoveredAsync(fixture, late));
    }

    [LogicalMySqlFact]
    public async Task ManagementCoversEmptyIdentitiesEditsAssignmentLifecycleArchiveAndRestoreWithAudit()
    {
        var fixture = await FixtureAsync();
        var (sensor, _) = await DeviceAsync(fixture, "tuya", "life", [("main", [Capability.TemperatureMeasurement])],
            [("main", Sample.Numeric(Metric.Temperature, Now.AddHours(-5), 18)), ("main", Sample.Numeric(Metric.Temperature, Now.AddHours(-1), 19))]);
        long room;
        await using (var db = await fixture.Admin.OpenAsync(default))
            room = await db.ExecuteScalarAsync<long>("INSERT INTO rooms(property_id,room_key,name) VALUES(@Property,'loft','Loft'); SELECT LAST_INSERT_ID();",
                new { fixture.Property });
        var store = Store();

        Assert.Equal(LogicalChangeStatus.Forbidden, (await store.CreateAsync(fixture.Property, fixture.Viewer,
            new("Loft thermometer", "Sensor", null, null, null, null, null, null, null), default)).Status);
        var created = await store.CreateAsync(fixture.Property, fixture.Owner, new("Loft thermometer", "Sensor", null, "Spare probe", null, null, null, null, null), default);
        Assert.Equal(LogicalChangeStatus.Done, created.Status);
        var id = created.Id!.Value;
        var empty = (await store.GetAsync(fixture.Property, fixture.Viewer, id, default))!;
        Assert.False(empty.CanManage);
        Assert.Empty(empty.Device.Current);
        Assert.Empty(empty.Device.Metrics!);
        Assert.Equal(HistoryReadStatus.NotFound, (await store.HistoryAsync(fixture.Property, id, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, Now.AddDays(-1), Now), default)).Status);

        var loaded = empty.Device.UpdatedAt;
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.UpdateAsync(fixture.Property, fixture.Owner, id,
            new("Loft thermometer", "Sensor", room + 999, null, loaded), default)).Status);
        Assert.Equal(LogicalChangeStatus.Done, (await Store(Now.AddMinutes(1)).UpdateAsync(fixture.Property, fixture.Owner, id,
            new("Loft temperature", "Sensor", room, "Attic probe", loaded), default)).Status);
        // A second editor still holding the original load time is told to reload.
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.UpdateAsync(fixture.Property, fixture.Owner, id,
            new("Stale edit", "Sensor", null, null, loaded), default)).Status);

        Assert.Equal(LogicalChangeStatus.Done, (await store.AssignAsync(fixture.Property, fixture.Owner, id,
            new("main", sensor, null, Now.AddHours(-6), "Installed"), default)).Status);
        var assigned = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!.Device;
        Assert.Equal(("Loft temperature", room, "Attic probe"), (assigned.Name, assigned.RoomId, assigned.Description));
        var current = assigned.Current.Single();
        Assert.Equal(LogicalChangeStatus.Forbidden, (await store.UnassignAsync(fixture.Property, fixture.Viewer, id,
            new("main", current.Id, Now.AddHours(-2), null), default)).Status);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.UnassignAsync(fixture.Property, fixture.Owner, id,
            new("main", current.Id + 1000, Now.AddHours(-2), null), default)).Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.UnassignAsync(fixture.Property, fixture.Owner, id,
            new("main", current.Id, Now.AddHours(-7), null), default)).Status);
        Assert.Equal(LogicalChangeStatus.Done, (await store.UnassignAsync(fixture.Property, fixture.Owner, id,
            new("main", current.Id, Now.AddHours(-2), "Probe removed"), default)).Status);
        var unassigned = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!.Device;
        Assert.Empty(unassigned.Current);
        Assert.Equal(Now.AddHours(-2), Assert.Single(unassigned.Previous).ValidTo);

        Assert.Equal(LogicalChangeStatus.Done, (await store.ArchiveAsync(fixture.Property, fixture.Owner, id, new("Retired"), default)).Status);
        Assert.DoesNotContain((await store.ListAsync(fixture.Property, fixture.Owner, false, default)).Devices, item => item.Id == id);
        Assert.Contains((await store.ListAsync(fixture.Property, fixture.Owner, true, default)).Devices, item => item.Id == id);
        var archivedHistory = await store.HistoryAsync(fixture.Property, id, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, Now.AddDays(-1), Now), default);
        Assert.Equal([18d], archivedHistory.Result!.Points!.Select(point => point.Number!.Value));
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.UnassignAsync(fixture.Property, fixture.Owner, id,
            new("main", current.Id, null, null), default)).Status);

        Assert.Equal(LogicalChangeStatus.Forbidden, (await store.RestoreAsync(fixture.Property, fixture.Viewer, id, new(null), default)).Status);
        Assert.Equal(LogicalChangeStatus.Done, (await store.RestoreAsync(fixture.Property, fixture.Owner, id, new("Probe back in service"), default)).Status);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.RestoreAsync(fixture.Property, fixture.Owner, id, new(null), default)).Status);
        var restored = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!;
        Assert.Null(restored.Device.ArchivedAt);
        Assert.Empty(restored.Device.Current);
        Assert.Equal(["created", "updated", "assigned", "unassigned", "archived", "restored"],
            restored.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Action));
        Assert.All(restored.Audit, entry => Assert.Equal(fixture.Owner, entry.Actor));
        var unassignAudit = restored.Audit.Single(entry => entry.Action == "unassigned");
        Assert.Equal((sensor, Now.AddHours(-2), "Probe removed"), (unassignAudit.OldDeviceId!.Value, unassignAudit.EffectiveAt!.Value, unassignAudit.Reason!));
    }

    [LogicalMySqlFact]
    public async Task CrossProviderAutoCreatedIdentityResolvesIntoAReplacementWithExplicitConfirmationAndNoRewrittenHistory()
    {
        var fixture = await FixtureAsync();
        var (tuya, _) = await DeviceAsync(fixture, "tuya", "old-sensor", [("main", [Capability.TemperatureMeasurement, Capability.HumidityMeasurement])],
            Every(Now.AddHours(-6), Cutover, 10, at => [("main", Sample.Numeric(Metric.Temperature, at, 20))]));
        var store = Store();
        await store.ExecuteInitializationAsync(fixture.Property, default);
        var original = await LogicalForAsync(fixture, tuya);
        var (ewelink, _) = await DeviceAsync(fixture, "growatt", "new-sensor", [("main", [Capability.TemperatureMeasurement])],
            Every(Cutover.AddMinutes(-30), Now, 10, at => [("main", Sample.Numeric(Metric.Temperature, at, 23))]));
        Assert.Equal(1, await store.ProvisionAsync(fixture.Property, default));
        var temporary = await LogicalForAsync(fixture, ewelink);
        var dashboard = new MonitoringDashboardStore(Runtime(), new Migrations(Runtime()), new TuyaOptions());
        var temporaryKey = $"logical:{temporary}:main:Temperature";
        var page = await dashboard.ReadAsync(fixture.Property, fixture.Owner, default);
        var widget = new DashboardWidget(Guid.NewGuid().ToString("D"), "Lounge", "compact", temporaryKey,
            [new DashboardValue(Guid.NewGuid().ToString("D"), temporaryKey, null)]);
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ReplaceAsync(fixture.Property, fixture.Owner,
            new(page.Revision, [new(Guid.NewGuid().ToString("D"), "Rooms", false, false, [widget])]), default));
        var rawBefore = await SampleCountAsync(fixture);
        var active = (await store.GetAsync(fixture.Property, fixture.Owner, original, default))!.Device.Current.Single();

        // The software never guesses: the hardware in use elsewhere must be released explicitly.
        var replacement = new ReplacePhysicalDevice("main", active.Id, ewelink, null, Cutover, "Sensor swapped", true);
        Assert.Equal(LogicalChangeStatus.ReleaseRequired, (await store.ReplaceAsync(fixture.Property, fixture.Owner, original, replacement, default)).Status);
        var candidate = (await store.CandidatesAsync(fixture.Property, original, "main", default))!.Single(item => item.DeviceId == ewelink);
        Assert.Equal((temporary, "automatic"), (candidate.AssignedLogicalDeviceId!.Value, candidate.AssignedLogicalCreation!));
        Assert.Equal([Capability.HumidityMeasurement], candidate.Missing);
        var preview = (await store.ReleasePreviewAsync(fixture.Property, original, ewelink, null, Cutover, default))!;
        Assert.Equal((temporary, "automatic", 3L, true, false, 1), (preview.LogicalDeviceId, preview.Creation, preview.Readings,
            preview.LeavesUnassigned, preview.HasOtherHardwareHistory, preview.DashboardReferences));
        Assert.Null(await store.ReleasePreviewAsync(fixture.Property, original, tuya, null, Cutover, default));
        Assert.Equal(LogicalChangeStatus.HistoryConfirmation, (await store.ReplaceAsync(fixture.Property, fixture.Owner, original,
            replacement with { ReleaseFrom = temporary }, default)).Status);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.ReplaceAsync(fixture.Property, fixture.Owner, original,
            replacement with { ReleaseFrom = original, AcceptReleasedHistory = true }, default)).Status);
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, original,
            replacement with { ReleaseFrom = temporary, AcceptReleasedHistory = true, ArchiveReleased = true }, default)).Status);

        // One continuous logical history, split at the exact instant; the temporary identity keeps the earlier readings.
        var history = await store.HistoryAsync(fixture.Property, original, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, Now.AddHours(-7), Now), default);
        Assert.Equal([tuya, ewelink], history.Result!.Segments.Select(segment => segment.DeviceId));
        Assert.Equal(Cutover, history.Result.Segments[0].To);
        Assert.All(history.Result.Points!, point => Assert.Equal(point.ObservedAt < Cutover ? 20 : 23, point.Number));
        var hourly = await store.HistoryAsync(fixture.Property, original, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Hour, Now.AddHours(-7), Now), default);
        Assert.Equal(HistoryReadStatus.Found, hourly.Status);
        var resolved = (await store.GetAsync(fixture.Property, fixture.Owner, temporary, default))!;
        Assert.NotNull(resolved.Device.ArchivedAt);
        Assert.Equal(Cutover, Assert.Single(resolved.Device.Previous).ValidTo);
        Assert.Equal(["provisioned", "resolved", "archived"], resolved.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Action));
        var kept = await store.HistoryAsync(fixture.Property, temporary, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, Now.AddHours(-7), Now), default);
        Assert.Equal(3, kept.Result!.Points!.Count);
        Assert.Equal(rawBefore, await SampleCountAsync(fixture));
        Assert.DoesNotContain((await store.ListAsync(fixture.Property, fixture.Owner, false, default)).Devices, item => item.Id == temporary);
        Assert.Equal(original, (await store.PhysicalDevicesAsync(fixture.Property, fixture.Owner, default)).Devices.Single(item => item.Id == ewelink).LogicalDeviceId);
        Assert.Equal(0, await store.ProvisionAsync(fixture.Property, default));

        // The widget is neither deleted nor retargeted: its source now reports the archived identity.
        var after = await dashboard.ReadAsync(fixture.Property, fixture.Owner, default);
        Assert.Equal(temporaryKey, after.Groups.Single().Widgets.Single().Values.Single().SourceKey);
        Assert.Equal("archived", after.Sources.Single(source => source.Key == temporaryKey).Status);
        var catalog = (await dashboard.CatalogAsync(fixture.Property, fixture.Owner, default))!;
        Assert.Equal(23, catalog.Single(source => source.Key == $"logical:{original}:main:Temperature").Reading!.Number);
    }

    [LogicalMySqlFact]
    public async Task RelayReplacementReportsCapabilityLossBeforeReleasingAndAssignsAnEmptyIdentity()
    {
        var fixture = await FixtureAsync();
        var (tongou, _) = await DeviceAsync(fixture, "ewelink", "tongou", [("main", [Capability.PowerMeasurement, Capability.VoltageMeasurement, Capability.Switch])],
            [("main", Sample.Numeric(Metric.ActivePower, Now.AddHours(-2), 900))]);
        var store = Store();
        await store.ExecuteInitializationAsync(fixture.Property, default);
        var meter = await LogicalForAsync(fixture, tongou);
        var (sonoff, _) = await DeviceAsync(fixture, "ewelink", "sonoff", [("main", [Capability.PowerMeasurement, Capability.Switch])], []);
        Assert.Equal(1, await store.ProvisionAsync(fixture.Property, default));
        var temporary = await LogicalForAsync(fixture, sonoff);
        var active = (await store.GetAsync(fixture.Property, fixture.Owner, meter, default))!.Device.Current.Single();
        var at = Now.AddMinutes(-20);

        var missing = await store.ReplaceAsync(fixture.Property, fixture.Owner, meter,
            new("main", active.Id, sonoff, null, at, null, false, temporary, true, true), default);
        Assert.Equal(LogicalChangeStatus.MissingCapabilities, missing.Status);
        Assert.Equal([Capability.VoltageMeasurement], missing.Missing!);
        Assert.NotNull((await store.GetAsync(fixture.Property, fixture.Owner, temporary, default))!.Device.Current.SingleOrDefault());
        // No readings before the replacement time, so no history confirmation is needed; leaving the identity active is allowed.
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, meter,
            new("main", active.Id, sonoff, null, at, null, true, temporary, false, false), default)).Status);
        var kept = (await store.GetAsync(fixture.Property, fixture.Owner, temporary, default))!.Device;
        Assert.Null(kept.ArchivedAt);
        Assert.Empty(kept.Current);

        // The now-empty identity can take other hardware later through an ordinary assignment.
        var (spare, _) = await DeviceAsync(fixture, "tuya", "spare-plug", [("main", [Capability.PowerMeasurement])], []);
        Assert.Equal(LogicalChangeStatus.Done, (await store.AssignAsync(fixture.Property, fixture.Owner, temporary,
            new("main", spare, null, null, null), default)).Status);
        Assert.Equal(spare, (await store.GetAsync(fixture.Property, fixture.Owner, temporary, default))!.Device.Current.Single().DeviceId);
        Assert.Equal(0, await store.ProvisionAsync(fixture.Property, default));
    }

    [Theory]
    [InlineData("Lounge sensor", "Tuya", "Sensor", "TH01", 7, "Lounge sensor")]
    [InlineData("Taken", "Tuya", "Sensor", "TH01", 7, "Taken (TH01)")]
    [InlineData("Twin", "Sonoff", "SmartSwitch", "POWR3", 8, "Twin #8")]
    [InlineData("", "Tongou", "SmartSwitch", "", 9, "Tongou Relay")]
    [InlineData(" ", "Daikin", "HeatPump", "", 10, "Daikin Heat Pump")]
    [InlineData("", "", "Unknown", "", 11, "Device")]
    public void DefaultNamesKeepInventoryNamesAndAvoidDuplicates(string name, string manufacturer, string kind, string model, long id, string expected)
    {
        var taken = new HashSet<string>(["taken", "Twin", "Twin (POWR3)"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, LogicalDeviceStore.DefaultName(name, manufacturer, kind, model, id, taken));
    }

    private static async Task<bool> CoveredAsync(Fixture fixture, long device)
    {
        await using var db = await fixture.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM logical_devices WHERE property_id=@Property AND origin_device_id=@device)", new { fixture.Property, device });
    }

    private static async Task<long> SampleCountAsync(Fixture fixture)
    {
        await using var db = await fixture.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.property_id=@Property
            """, new { fixture.Property });
    }

    private static async Task<Fixture> FixtureAsync()
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        await using var db = await admin.OpenAsync(default);
        var property = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Management fixture','UTC'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N") });
        var owner = Guid.NewGuid().ToString("D");
        var viewer = Guid.NewGuid().ToString("D");
        await db.ExecuteAsync("""
            INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
            VALUES(@owner,CONCAT(@owner,'@example.test'),UPPER(CONCAT(@owner,'@example.test')),'x','x','x'),
                  (@viewer,CONCAT(@viewer,'@example.test'),UPPER(CONCAT(@viewer,'@example.test')),'x','x','x');
            INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@owner,'Owner'),(@property,@viewer,'Viewer');
            """, new { owner, viewer, property });
        return new(admin, property, owner, viewer);
    }

    private static async Task<(long Device, Dictionary<string, long> Endpoints)> DeviceAsync(Fixture fixture, string provider, string id,
        (string Key, Capability[] Capabilities)[] endpoints, IReadOnlyList<(string Endpoint, Sample Sample)> samples, string? reportSource = null)
    {
        var writer = new TelemetryWriter(fixture.Admin, new SettableClock(Now));
        var external = fixture.Property + "-" + id;
        var inventory = new DiscoveredDevice(external, "Fixture " + id, DeviceKind.Sensor, "", "Fixture", "Model " + id,
            endpoints.Select(endpoint => new DeviceEndpoint(endpoint.Key, endpoint.Key, endpoint.Capabilities)).ToArray());
        var batches = samples.Count == 0 ? [Batch(external, endpoints[0].Key, [])] :
            samples.GroupBy(sample => sample.Sample.ObservedAt).Select(group => Batch(external, null, group.ToList())).ToList();
        foreach (var batch in batches)
            await writer.StoreCloudDeviceAsync(provider, fixture.Property, null, inventory, batch, default, reportSource);
        await using var db = await fixture.Admin.OpenAsync(default);
        var device = await db.ExecuteScalarAsync<long>("SELECT id FROM devices WHERE property_id=@Property AND name=@name",
            new { fixture.Property, name = inventory.Name });
        var keys = (await db.QueryAsync<(string, long)>("SELECT endpoint_key, id FROM device_endpoints WHERE device_id=@device", new { device }))
            .ToDictionary(row => row.Item1, row => row.Item2);
        return (device, keys);
    }

    private static ObservationBatch Batch(string external, string? endpoint, IReadOnlyList<(string Endpoint, Sample Sample)> samples)
    {
        var at = samples.Count > 0 ? samples[0].Sample.ObservedAt : Now;
        var observations = samples.Count == 0 ? [new EndpointObservation(endpoint!, [])]
            : samples.GroupBy(item => item.Endpoint).Select(group => new EndpointObservation(group.Key, group.Select(item => item.Sample).ToArray())).ToArray();
        return new(at, [new(external, Connectivity.Online, null, observations, at)], []);
    }

    private static List<(string Endpoint, Sample Sample)> Every(DateTimeOffset from, DateTimeOffset to, int minutes,
        Func<DateTimeOffset, (string Endpoint, Sample Sample)[]> create)
    {
        var samples = new List<(string, Sample)>();
        for (var at = from; at < to; at = at.AddMinutes(minutes)) samples.AddRange(create(at));
        return samples;
    }

    private static async Task<long> LogicalForAsync(Fixture fixture, long device)
    {
        await using var db = await fixture.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>("SELECT id FROM logical_devices WHERE property_id=@Property AND origin_device_id=@device",
            new { fixture.Property, device });
    }

    private static async Task<List<(string Name, string Checksum)>> LedgerAsync(Database db)
    {
        await using var connection = await db.OpenAsync(default);
        return (await connection.QueryAsync<(string, string)>("SELECT name, checksum FROM schema_migrations ORDER BY name")).ToList();
    }

    private static LogicalDeviceStore Store(DateTimeOffset? now = null) =>
        new(Runtime(), new Migrations(Runtime()), new TelemetryHistoryReader(Runtime(), new Migrations(Runtime()),
            new TelemetryHoldPolicy(new(), new(), new(), new(), new()), new TelemetryHistoryOptions(), new SettableClock(Now)),
            new SettableClock(now ?? Now));

    private static Database Admin() => Open(LogicalMySqlFact.AdminVariable);

    private static Database Runtime() =>
        Environment.GetEnvironmentVariable(LogicalMySqlFact.RuntimeVariable) is { Length: > 0 } ? Open(LogicalMySqlFact.RuntimeVariable) : Admin();

    private static Database Open(string variable)
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException("A disposable logical-device MySQL test database is required."));
        Assert.Equal("homeautomation_logical_test", builder.Database);
        return new Database(builder.ConnectionString);
    }

    private sealed record Fixture(Database Admin, long Property, string Owner, string Viewer);

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LogicalMySqlFact : FactAttribute
    {
        public const string AdminVariable = "HOMEAUTOMATION_LOGICAL_TEST_MYSQL";
        public const string RuntimeVariable = "HOMEAUTOMATION_LOGICAL_TEST_RUNTIME";

        public LogicalMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminVariable)))
                Skip = $"Set {AdminVariable} for a disposable homeautomation_logical_test schema.";
        }
    }
}

/// <summary>Tests that share, and in places reshape, the disposable logical-device schema run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class LogicalSchemaCollection
{
    public const string Name = "Logical device schema";
}
