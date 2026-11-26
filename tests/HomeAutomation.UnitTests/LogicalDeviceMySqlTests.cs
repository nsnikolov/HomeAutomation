using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Opt-in tests against a disposable <c>homeautomation_logical_test</c> schema. The optional runtime connection should use an
/// account limited to SELECT, INSERT and UPDATE (plus the documented DELETE on dashboard_groups for layout saves).
/// </summary>
public sealed class LogicalDeviceMySqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutover = new DateTimeOffset(2026, 11, 26, 14, 37, 12, TimeSpan.Zero).AddTicks(3_456_780);

    [LogicalMySqlFact]
    public async Task MigrationUpgradesAPopulated017SchemaWithoutChangingExistingData()
    {
        var admin = Admin();
        var fixture = await FixtureAsync("UTC");
        var (device, _) = await DeviceAsync(fixture, "growatt", "upgrade", [("main", [Capability.PowerMeasurement])],
            [("main", Sample.Numeric(Metric.ActivePower, Now.AddHours(-1), 500))]);
        // Return the disposable schema to its 017 shape while keeping the populated inventory and telemetry.
        await using (var db = await admin.OpenAsync(default))
            await db.ExecuteAsync("""
                DROP TABLE IF EXISTS logical_device_audit, logical_device_assignments, logical_devices;
                DELETE FROM schema_migrations WHERE name LIKE '%.018_logical_devices.sql';
                """);
        var before = await LedgerAsync(admin);
        var rows = await CountsAsync(admin, fixture.Property);

        Assert.True(await new Migrations(Runtime()).VerifyAsync(default, includeDaikin: true, includeEwelink: true, includeTuya: true,
            includePreferences: true, includeDashboard: true));
        Assert.False(await new Migrations(Runtime()).VerifyLogicalDevicesAsync(default));
        Assert.Equal(LogicalChangeStatus.SchemaPending, (await Store(fixture).CreateAsync(fixture.Property, fixture.Owner,
            new("Meter", "SmartSwitch", null, null, device, null, null, null, null), default)).Status);

        await new Migrations(admin).ApplyAsync(default);

        Assert.Equal(before, (await LedgerAsync(admin)).Where(item => !item.Name.EndsWith(".018_logical_devices.sql")).ToList());
        Assert.True(await new Migrations(Runtime()).VerifyLogicalDevicesAsync(default));
        Assert.Equal(rows, await CountsAsync(admin, fixture.Property));
    }

    [LogicalMySqlFact]
    public async Task InitializationIsDeterministicIdempotentAndSplitsRepeatedEndpoints()
    {
        var fixture = await FixtureAsync("UTC");
        var (thermometer, _) = await DeviceAsync(fixture, "tuya", "thermo",
            [("va_temperature", [Capability.TemperatureMeasurement]), ("va_humidity", [Capability.HumidityMeasurement])],
            [("va_temperature", Sample.Numeric(Metric.Temperature, Now.AddHours(-2), 21))], TuyaService.SensorReportSource);
        var (relay, _) = await DeviceAsync(fixture, "ewelink", "relay", [("main", [Capability.Switch, Capability.PowerMeasurement])], []);
        var (gang, gangEndpoints) = await DeviceAsync(fixture, "tuya", "gang",
            [("switch_1", [Capability.Switch]), ("switch_2", [Capability.Switch])], []);
        var store = Store(fixture);

        var plan = await store.PlanInitializationAsync(fixture.Property, default);
        Assert.Equal(3, plan.Work.Count());
        Assert.Equal([new InitializationChannel("main", null)], plan.Items.Single(item => item.DeviceId == thermometer).Channels);
        Assert.Equal(1, plan.Items.Single(item => item.DeviceId == thermometer).SeriesCount);
        Assert.Equal(0, plan.Items.Single(item => item.DeviceId == relay).SeriesCount);
        Assert.Equal([new InitializationChannel("switch_1", gangEndpoints["switch_1"]), new InitializationChannel("switch_2", gangEndpoints["switch_2"])],
            plan.Items.Single(item => item.DeviceId == gang).Channels);
        Assert.Equal(0, await ScalarAsync(fixture.Admin, "SELECT COUNT(*) FROM logical_devices WHERE property_id=@property", fixture.Property));

        Assert.Equal(3, await store.ExecuteInitializationAsync(fixture.Property, default));
        Assert.Equal(0, await store.ExecuteInitializationAsync(fixture.Property, default));
        Assert.All((await store.PlanInitializationAsync(fixture.Property, default)).Items, item => Assert.Equal("already initialized", item.Skipped));
        Assert.Equal(3, await ScalarAsync(fixture.Admin, "SELECT COUNT(*) FROM logical_devices WHERE property_id=@property", fixture.Property));
        Assert.Equal(4, await ScalarAsync(fixture.Admin,
            "SELECT COUNT(*) FROM logical_device_assignments WHERE property_id=@property AND valid_from_utc='1970-01-01' AND valid_to_utc IS NULL",
            fixture.Property));
        Assert.Equal(3, await ScalarAsync(fixture.Admin,
            "SELECT COUNT(*) FROM logical_device_audit WHERE property_id=@property AND action='initialized' AND actor='system:initialize'", fixture.Property));
        var list = await store.ListAsync(fixture.Property, fixture.Viewer, false, default);
        Assert.False(list.CanManage);
        Assert.Contains(list.Devices, item => item.Name == "Fixture thermo" && item.Current.Single().Capabilities.SequenceEqual(
            [Capability.TemperatureMeasurement, Capability.HumidityMeasurement]));
    }

    [LogicalMySqlFact]
    public async Task CrossProviderReplacementKeepsOneContinuousTemperatureHistory()
    {
        var fixture = await FixtureAsync("Europe/Sofia");
        var hour = new DateTimeOffset(2026, 11, 26, 14, 0, 0, TimeSpan.Zero);
        var (tuya, tuyaEndpoints) = await DeviceAsync(fixture, "tuya", "sensor-a",
            [("va_temperature", [Capability.TemperatureMeasurement]), ("va_humidity", [Capability.HumidityMeasurement])],
            Every(hour.AddHours(-2), hour.AddHours(1), 5, at => [("va_temperature", Sample.Numeric(Metric.Temperature, at, 20)),
                ("va_humidity", Sample.Numeric(Metric.Humidity, at, 50))]), TuyaService.SensorReportSource);
        await DeviceAsync(fixture, "tuya", "sensor-a", [("va_temperature", [Capability.TemperatureMeasurement]), ("va_humidity", [Capability.HumidityMeasurement])],
            Every(hour.AddHours(-2), hour.AddHours(1), 30, at => [("va_temperature", Sample.Numeric(Metric.Temperature, at.AddSeconds(7), 99))]));
        var store = Store(fixture);
        await store.ExecuteInitializationAsync(fixture.Property, default);
        // The replacement sensor is discovered after initialization and reports before it is installed.
        var (ewelink, _) = await DeviceAsync(fixture, "ewelink", "sensor-b", [("main", [Capability.TemperatureMeasurement])],
            Every(hour.AddHours(-1), hour.AddHours(3), 5, at => [("main", Sample.Numeric(Metric.Temperature, at.AddSeconds(30), 24))]));
        var logical = await LogicalForAsync(fixture, tuya);
        var current = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!.Device.Current.Single();

        var missing = await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, ewelink, null, Cutover, "Sensor A battery failed", false), default);
        Assert.Equal(LogicalChangeStatus.MissingCapabilities, missing.Status);
        Assert.Equal([Capability.HumidityMeasurement], missing.Missing);
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, ewelink, null, Cutover, "Sensor A battery failed", true), default)).Status);

        var detail = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!;
        Assert.Equal(ewelink, detail.Device.Current.Single().DeviceId);
        Assert.Equal(Cutover, detail.Device.Current.Single().ValidFrom);
        Assert.Equal(Cutover, detail.Device.Previous.Single().ValidTo);
        Assert.Equal(new[] { (Metric.Temperature, true), (Metric.Humidity, false) }.OrderBy(item => item.Item1),
            detail.Device.Metrics!.Select(item => (item.Metric, item.Current)).OrderBy(item => item.Metric));
        Assert.All(detail.Device.Metrics!, item => Assert.Equal("main", item.ChannelKey));
        Assert.Equal("%", detail.Device.Metrics!.Single(item => item.Metric == Metric.Humidity).Unit);
        var audit = detail.Audit.First();
        Assert.Equal(("replaced", tuya, ewelink, Cutover, "Sensor A battery failed", fixture.Owner),
            (audit.Action, audit.OldDeviceId!.Value, audit.NewDeviceId!.Value, audit.EffectiveAt!.Value, audit.Reason, audit.Actor));

        await Rollups(fixture).ExecuteBackfillAsync(await Rollups(fixture).PlanBackfillAsync(new(fixture.Property, null, null, null), default), default);
        var raw = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, hour, hour.AddHours(1), Limit: 5000), default)).Result!;
        Assert.DoesNotContain(raw.Points!, point => point.Number == 99);
        Assert.All(raw.Points!.Where(point => point.ObservedAt < Cutover), point => Assert.Equal(20, point.Number));
        Assert.All(raw.Points!.Where(point => point.ObservedAt >= Cutover), point => Assert.Equal(24, point.Number));
        Assert.Equal(raw.Points!.OrderBy(point => point.ObservedAt), raw.Points);
        Assert.Equal("tuya-sensor-report", raw.Segments[0].Source);
        Assert.NotEmpty(raw.Segments[0].AlternativeSeriesIds);
        Assert.Equal([65 * 60d, 7 * 60d], raw.Segments.Select(segment => segment.MaximumHoldSeconds!.Value));

        var paged = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, hour, hour.AddHours(1), Limit: 8), default)).Result!;
        var next = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Raw, hour, hour.AddHours(1), After: paged.NextAfter, Limit: 5000),
            default)).Result!;
        Assert.Equal(raw.Points!.Select(point => point.ObservedAt), paged.Points!.Concat(next.Points!).Select(point => point.ObservedAt));

        var hourly = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Hour, hour, hour.AddHours(1)), default)).Result!;
        var bucket = Assert.Single(hourly.Buckets!);
        Assert.Contains("deviceReplaced", bucket.Flags);
        Assert.Equal(2, bucket.SeriesIds!.Count);
        // Sensor A covers 14:00 until the exact cutover. Sensor B's first own observation is 14:40:30; its earlier
        // 14:35:30 reading belongs to no assignment and is not held across the cutover.
        var sensorA = (Cutover - hour).TotalSeconds;
        var sensorB = (hour.AddHours(1) - hour.AddMinutes(40).AddSeconds(30)).TotalSeconds;
        Assert.Equal((20 * sensorA + 24 * sensorB) / (sensorA + sensorB), bucket.Average!.Value, 6);
        Assert.Equal(sensorA + sensorB, bucket.CoveredSeconds, 3);
        Assert.Equal(20, bucket.Minimum);
        Assert.Equal(24, bucket.Maximum);

        var humidity = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Humidity,
            new(new(null, null, null, null, Metric.Humidity), HistoryResolution.Hour, hour.AddHours(-1), hour.AddHours(2)), default)).Result!;
        Assert.Equal(["available", "unsupported"], humidity.Segments.Select(segment => segment.Status));
        Assert.Equal("complete", humidity.Buckets![0].Quality);
        Assert.Equal("incomplete", humidity.Buckets[1].Quality);
        Assert.Contains("notReported", humidity.Buckets[1].Flags);
        Assert.Equal(50, humidity.Buckets[1].Maximum);
        Assert.Equal(sensorA, humidity.Buckets[1].CoveredSeconds, 3);
        Assert.Equal("noData", humidity.Buckets[2].Quality);
        Assert.Contains("notReported", humidity.Buckets[2].Flags);

        var day = (await store.HistoryAsync(fixture.Property, logical, "main", Metric.Temperature,
            new(new(null, null, null, null, Metric.Temperature), HistoryResolution.Day, Now, Now, new DateOnly(2026, 11, 26),
                new DateOnly(2026, 11, 26)), default)).Result!;
        var daily = Assert.Single(day.Buckets!);
        Assert.Equal(20, daily.Minimum);
        Assert.Equal(24, daily.Maximum);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 22, 0, 0, TimeSpan.Zero), daily.Start);
    }

    [LogicalMySqlFact]
    public async Task ElectricalReplacementNeverDifferencesCountersAcrossDevicesAndKeepsEnergyBasesApart()
    {
        var fixture = await FixtureAsync("Europe/Sofia");
        var day = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);
        var cutover = new DateTimeOffset(2026, 10, 25, 9, 13, 0, TimeSpan.Zero);
        Sample[] Meter(DateTimeOffset at, double power, double counter) =>
            [Sample.Numeric(Metric.ActivePower, at, power), Sample.Numeric(Metric.Energy, at, counter)];
        // A 15-minute hold covers both 5-minute streams. Meter A keeps reporting after the cutover; meter B reported before it.
        var (oldMeter, _) = await DeviceAsync(fixture, "growatt", "meter-a", [("main", [Capability.PowerMeasurement, Capability.EnergyMeasurement])],
            Every(day, cutover.AddHours(2), 5, at => Meter(at, 600, Math.Round(100 + (at - day).TotalHours * 0.6, 6)).Select(sample => ("main", sample)).ToArray()));
        var (newMeter, _) = await DeviceAsync(fixture, "growatt", "meter-b", [("main", [Capability.PowerMeasurement, Capability.EnergyMeasurement])],
            Every(cutover.AddHours(-1), day.AddHours(25), 5, at => Meter(at, 1200, Math.Round(2 + (at - cutover).TotalHours * 1.2, 6)).Select(sample => ("main", sample)).ToArray()));
        var store = Store(fixture);
        await store.ExecuteInitializationAsync(fixture.Property, default);
        var logical = await LogicalForAsync(fixture, oldMeter);
        var current = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!.Device.Current.Single();
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, newMeter, null, cutover, null, false), default)).Status);
        var other = await LogicalForAsync(fixture, newMeter);
        Assert.Equal(LogicalChangeStatus.Done, (await store.ArchiveAsync(fixture.Property, fixture.Owner, other, new("Merged"), default)).Status);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, newMeter, null, cutover, null, false), default)).Status);
        await using (var db = await fixture.Admin.OpenAsync(default))
            await db.ExecuteAsync("UPDATE logical_device_assignments SET valid_to_utc='1970-01-02' WHERE logical_device_id=@other", new { other });
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, newMeter, null, cutover, "New meter", false), default)).Status);
        await Rollups(fixture).ExecuteBackfillAsync(await Rollups(fixture).PlanBackfillAsync(new(fixture.Property, null, null, null), default), default);

        HistoryQuery Day(Metric metric) => new(new(null, null, null, null, metric), HistoryResolution.Day, Now, Now,
            new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25));
        var energy = Assert.Single((await store.HistoryAsync(fixture.Property, logical, "main", Metric.Energy, Day(Metric.Energy), default)).Result!.Buckets!);
        var power = Assert.Single((await store.HistoryAsync(fixture.Property, logical, "main", Metric.ActivePower, Day(Metric.ActivePower), default)).Result!.Buckets!);
        Assert.Equal(90000, energy.DurationSeconds);
        // Meter A's counter is differenced only between its own readings before 09:13 (last at 09:10). Meter B starts a new
        // baseline at its first own reading (09:13). The 100 kWh versus 2 kWh counter values are never subtracted.
        var lastA = day.AddMinutes(Math.Floor((cutover - day).TotalMinutes / 5) * 5);
        if (lastA == cutover) lastA = lastA.AddMinutes(-5);
        var lastB = cutover.AddHours(-1).AddMinutes(Math.Floor((day.AddHours(25) - cutover.AddHours(-1)).TotalMinutes / 5 - 1e-9) * 5);
        Assert.Equal((lastA - day).TotalHours * 600 + (lastB - cutover).TotalHours * 1200, energy.Energy!.Wh!.Value, 1);
        Assert.DoesNotContain("counterReset", energy.Flags);
        Assert.DoesNotContain("counterDiscontinuity", energy.Flags);
        Assert.Equal("counter", energy.Energy.Basis);
        Assert.Null(energy.Energy.PositiveWh);
        Assert.Equal("powerIntegration", power.Energy!.Basis);
        Assert.Null(power.Energy.Wh);
        Assert.Equal((cutover - day).TotalHours * 600 + (day.AddHours(25) - cutover).TotalHours * 1200, power.Energy.PositiveWh!.Value, 1);
        Assert.Equal(1, power.Coverage, 6);
        Assert.Contains("deviceReplaced", power.Flags);
    }

    [LogicalMySqlFact]
    public async Task ReplacementValidatesPermissionsTimesConflictsAndRollsBackAtomically()
    {
        var fixture = await FixtureAsync("UTC");
        var other = await FixtureAsync("UTC");
        var (sensor, _) = await DeviceAsync(fixture, "tuya", "validate-a", [("main", [Capability.TemperatureMeasurement])], []);
        var (spare, _) = await DeviceAsync(fixture, "ewelink", "validate-b", [("main", [Capability.TemperatureMeasurement])], []);
        var (relay, _) = await DeviceAsync(fixture, "ewelink", "validate-c", [("main", [Capability.Switch])], []);
        var (foreign, _) = await DeviceAsync(other, "tuya", "validate-d", [("main", [Capability.TemperatureMeasurement])], []);
        var store = Store(fixture);
        var created = await store.CreateAsync(fixture.Property, fixture.Owner, new("Outdoor", "Sensor", null, null, sensor, null, null,
            Now.AddDays(-3), "Installed"), default);
        var id = created.Id!.Value;
        var current = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!.Device.Current.Single();
        ReplacePhysicalDevice Replace(long device, DateTimeOffset? at = null, long? expected = null) =>
            new("main", expected ?? current.Id, device, null, at ?? Now.AddHours(-1), null, false);

        Assert.Equal(LogicalChangeStatus.Forbidden, (await store.ReplaceAsync(fixture.Property, fixture.Viewer, id, Replace(spare), default)).Status);
        Assert.Equal(LogicalChangeStatus.Forbidden, (await store.ReplaceAsync(other.Property, fixture.Owner, id, Replace(spare), default)).Status);
        Assert.Equal(LogicalChangeStatus.NotFound, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(foreign), default)).Status);
        Assert.Equal(LogicalChangeStatus.Incompatible, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(relay), default)).Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(spare, Now.AddDays(-4)), default)).Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(spare, Now.AddHours(1)), default)).Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(sensor), default)).Status);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(spare, expected: 999), default)).Status);

        store.BeforeCommit = _ => throw new InvalidOperationException("fixture failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(spare), default));
        store.BeforeCommit = null;
        Assert.Equal(1, await ScalarAsync(fixture.Admin, "SELECT COUNT(*) FROM logical_device_assignments WHERE property_id=@property", fixture.Property));
        Assert.Equal(1, await ScalarAsync(fixture.Admin, "SELECT COUNT(*) FROM logical_device_audit WHERE property_id=@property", fixture.Property));

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Store(fixture).ReplaceAsync(fixture.Property, fixture.Owner, id, Replace(spare), default)));
        Assert.Single(concurrent, result => result.Status == LogicalChangeStatus.Done);
        Assert.Equal(3, concurrent.Count(result => result.Status == LogicalChangeStatus.Conflict));
        Assert.Equal(1, await ScalarAsync(fixture.Admin,
            "SELECT COUNT(*) FROM logical_device_assignments WHERE property_id=@property AND valid_to_utc IS NULL", fixture.Property));

        var second = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!.Device.Current.Single();
        var corrected = await store.CorrectAsync(fixture.Property, fixture.Owner, id, second.Id, new(Now.AddHours(-2).AddMinutes(-13), "Installer log"), default);
        Assert.Equal(LogicalChangeStatus.Done, corrected.Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.CorrectAsync(fixture.Property, fixture.Owner, id, second.Id,
            new(Now.AddDays(-4), "Too early"), default)).Status);
        Assert.Equal(LogicalChangeStatus.Invalid, (await store.CorrectAsync(fixture.Property, fixture.Owner, id, second.Id,
            new(Now.AddHours(-1), " "), default)).Status);
        var after = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!;
        Assert.Equal(Now.AddHours(-2).AddMinutes(-13), after.Device.Current.Single().ValidFrom);
        Assert.Equal(Now.AddHours(-2).AddMinutes(-13), after.Device.Previous.Single().ValidTo);
        Assert.Equal("corrected", after.Audit.First().Action);

        var repeat = await store.ReplaceAsync(fixture.Property, fixture.Owner, id, new("main", second.Id, sensor, null, Now.AddMinutes(-30), "Back", false), default);
        Assert.Equal(LogicalChangeStatus.Done, repeat.Status);
        Assert.Equal(LogicalChangeStatus.Done, (await store.ArchiveAsync(fixture.Property, fixture.Owner, id, new("Removed"), default)).Status);
        var archived = (await store.GetAsync(fixture.Property, fixture.Owner, id, default))!;
        Assert.Empty(archived.Device.Current);
        Assert.Equal(3, archived.Device.Previous.Count);
        Assert.Equal(LogicalChangeStatus.Conflict, (await store.UpdateAsync(fixture.Property, fixture.Owner, id,
            new("Renamed", "Sensor", null, null), default)).Status);
        Assert.Equal(["archived", "replaced", "corrected", "replaced", "created"], archived.Audit.Select(item => item.Action));
    }

    [LogicalMySqlFact]
    public async Task DashboardLogicalSourcesFollowReplacementsWithoutShowingRetiredReadings()
    {
        var fixture = await FixtureAsync("UTC");
        var (meterA, _) = await DeviceAsync(fixture, "growatt", "dash-a", [("main", [Capability.PowerMeasurement, Capability.VoltageMeasurement])],
            [("main", Sample.Numeric(Metric.ActivePower, Now.AddMinutes(-40), 700)), ("main", Sample.Numeric(Metric.Voltage, Now.AddMinutes(-40), 230))]);
        var (meterB, _) = await DeviceAsync(fixture, "growatt", "dash-b", [("main", [Capability.PowerMeasurement])],
            [("main", Sample.Numeric(Metric.ActivePower, Now.AddMinutes(-20), 900))]);
        var dashboard = new MonitoringDashboardStore(Runtime(), new Migrations(Runtime()), new TuyaOptions());
        var catalog = (await dashboard.CatalogAsync(fixture.Property, fixture.Owner, default))!;
        var physicalA = catalog.Single(source => source.Provider == "growatt" && source.DeviceName == "Fixture dash-a" && source.Metric == Metric.ActivePower).Key;
        var physicalB = catalog.Single(source => source.Provider == "growatt" && source.DeviceName == "Fixture dash-b").Key;
        var empty = await dashboard.ReadAsync(fixture.Property, fixture.Owner, default);
        var values = new[] { new DashboardValue(Guid.NewGuid().ToString("D"), physicalA, null), new DashboardValue(Guid.NewGuid().ToString("D"), physicalB, null) };
        var widget = new DashboardWidget(Guid.NewGuid().ToString("D"), "Heat pump", "compact", physicalA, values);
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ReplaceAsync(fixture.Property, fixture.Owner,
            new(empty.Revision, [new(Guid.NewGuid().ToString("D"), "Energy", false, false, [widget])]), default));

        var store = Store(fixture);
        await store.ExecuteInitializationAsync(fixture.Property, default);
        var logical = await LogicalForAsync(fixture, meterA);
        Assert.Equal(LogicalChangeStatus.Done, (await store.ArchiveAsync(fixture.Property, fixture.Owner, await LogicalForAsync(fixture, meterB),
            new("Spare"), default)).Status);
        var review = (await dashboard.LogicalBindingsAsync(fixture.Property, fixture.Owner, default))!;
        var proposal = Assert.Single(review.Proposals);
        Assert.Equal((values[0].Id, $"logical:{logical}:main:ActivePower"), (proposal.ValueId, proposal.LogicalKey));
        Assert.Contains(review.Skipped, skip => skip.ValueId == values[1].Id);
        Assert.Null(await dashboard.LogicalBindingsAsync(fixture.Property, fixture.Viewer, default));
        Assert.Equal(DashboardSaveResult.Conflict, await dashboard.ConvertToLogicalAsync(fixture.Property, fixture.Owner,
            new(new string('0', 64), [values[0].Id]), default));
        Assert.Equal(DashboardSaveResult.InvalidSource, await dashboard.ConvertToLogicalAsync(fixture.Property, fixture.Owner,
            new(review.Revision, [values[1].Id]), default));
        Assert.Equal(DashboardSaveResult.Saved, await dashboard.ConvertToLogicalAsync(fixture.Property, fixture.Owner,
            new(review.Revision, [values[0].Id]), default));

        var converted = await dashboard.ReadAsync(fixture.Property, fixture.Owner, default);
        var saved = converted.Groups.Single().Widgets.Single();
        Assert.Equal([proposal.LogicalKey, physicalB], saved.Values.Select(value => value.SourceKey));
        Assert.Equal(proposal.LogicalKey, saved.NavigationSource);
        Assert.Equal(700, converted.Sources.Single(source => source.Key == proposal.LogicalKey).Reading!.Number);
        Assert.Equal(900, converted.Sources.Single(source => source.Key == physicalB).Reading!.Number);

        await using (var db = await fixture.Admin.OpenAsync(default))
            await db.ExecuteAsync("UPDATE logical_device_assignments SET valid_to_utc=@to WHERE property_id=@property AND device_id=@meterB",
                new { to = Now.AddDays(-1).UtcDateTime.AddHours(-1), property = fixture.Property, meterB });
        var current = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!.Device.Current.Single();
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, meterB, null, Now.AddMinutes(-30), null, true), default)).Status);
        var followed = (await dashboard.ReadAsync(fixture.Property, fixture.Owner, default)).Sources.Single(source => source.Key == proposal.LogicalKey);
        Assert.Equal((900d, "growatt", (string?)null), (followed.Reading!.Number!.Value, followed.PhysicalProvider!, followed.Status));

        current = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!.Device.Current.Single();
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, meterA, null, Now.AddMinutes(-10), null, false), default)).Status);
        var noStale = (await dashboard.ReadAsync(fixture.Property, fixture.Owner, default)).Sources.Single(source => source.Key == proposal.LogicalKey);
        Assert.Null(noStale.Reading);
        Assert.Null(noStale.Status);

        current = (await store.GetAsync(fixture.Property, fixture.Owner, logical, default))!.Device.Current.Single();
        Assert.Equal(LogicalChangeStatus.Done, (await store.ReplaceAsync(fixture.Property, fixture.Owner, logical,
            new("main", current.Id, meterB, null, Now.AddMinutes(-5), null, true), default)).Status);
        var voltage = (await dashboard.CatalogAsync(fixture.Property, fixture.Owner, default))!
            .Single(source => source.Key == $"logical:{logical}:main:Voltage");
        Assert.Equal("unsupported", voltage.Status);
        Assert.Null(voltage.Reading);
    }

    private static async Task<Fixture> FixtureAsync(string zone)
    {
        var admin = Admin();
        await new Migrations(admin).ApplyAsync(default);
        await using var db = await admin.OpenAsync(default);
        var property = await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Logical fixture',@zone); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N"), zone });
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
        var writer = new TelemetryWriter(fixture.Admin, new FixedClock(Now));
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

    private static async Task<long> LogicalForAsync(Fixture fixture, long device) =>
        await ScalarAsync(fixture.Admin, "SELECT id FROM logical_devices WHERE property_id=@property AND origin_device_id=" + device, fixture.Property);

    private static async Task<long> ScalarAsync(Database db, string sql, long property)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.ExecuteScalarAsync<long>(sql, new { property });
    }

    private static async Task<(long, long, long)> CountsAsync(Database db, long property)
    {
        await using var connection = await db.OpenAsync(default);
        return await connection.QuerySingleAsync<(long, long, long)>("""
            SELECT (SELECT COUNT(*) FROM devices WHERE property_id=@property),
                   (SELECT COUNT(*) FROM telemetry_series WHERE property_id=@property),
                   (SELECT COUNT(*) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.property_id=@property)
            """, new { property });
    }

    private static async Task<List<(string Name, string Checksum)>> LedgerAsync(Database db)
    {
        await using var connection = await db.OpenAsync(default);
        return (await connection.QueryAsync<(string, string)>("SELECT name, checksum FROM schema_migrations ORDER BY name")).ToList();
    }

    private static LogicalDeviceStore Store(Fixture fixture) =>
        new(Runtime(), new Migrations(Runtime()), Reader(), new FixedClock(Now));

    private static TelemetryHistoryReader Reader() =>
        new(Runtime(), new Migrations(Runtime()), Holds(), new TelemetryHistoryOptions(), new FixedClock(Now));

    private static TelemetryRollups Rollups(Fixture fixture) =>
        new(Runtime(), new Migrations(Runtime()), Holds(), new TelemetryHistoryOptions(), new FixedClock(Now));

    private static TelemetryHoldPolicy Holds() => new(new(), new(), new(), new(), new());

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
