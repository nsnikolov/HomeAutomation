using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

internal static class AlertJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Read<T>(string? json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    public static string Severity(AlertSeverity severity) => severity.ToString().ToLowerInvariant();
    public static AlertSeverity Severity(string value) => Enum.Parse<AlertSeverity>(value, ignoreCase: true);
}

/// <summary>Signal keys, labels, provider sampling cadences and the database reads behind evaluations, previews and the editor.</summary>
internal static class AlertSignalReader
{
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string AnyFault = "ANY_FAULT";

    public static string Key(SignalRef signal) => Domain.AlertSignals.Key(signal);

    /// <summary>Unambiguous names for the property's current, non-simulated devices (custom name first).</summary>
    public static async Task<IReadOnlyDictionary<long, string>> OwnerNamesAsync(MySqlConnection db, MySqlTransaction? tx, long property,
        CancellationToken ct) => SignalOwnerNames.Distinguish(await db.QueryAsync<SignalOwner>(Query("""
            SELECT d.id AS Id, COALESCE(NULLIF(TRIM(d.display_name), ''), d.name) AS Name, d.model AS Model, d.manufacturer AS Manufacturer,
                   r.name AS Room
            FROM devices d LEFT JOIN rooms r ON r.property_id = d.property_id AND r.id = d.room_id
            WHERE d.property_id = @property AND d.retired_at_utc IS NULL AND d.inventory_key NOT LIKE 'simulator:%'
            """, ct, new { property }, tx)));

    /// <summary>One measurement label: owner, the endpoint when it adds information, and the metric.</summary>
    private static string MeasurementLabel(string owner, string? baseName, string? endpoint, Metric metric) =>
        endpoint is { Length: > 0 } name && !string.Equals(name, baseName ?? owner, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, owner, StringComparison.OrdinalIgnoreCase)
            ? $"{owner} · {name} · {MetricLabel(metric)}" : $"{owner} · {MetricLabel(metric)}";

    /// <summary>Provider report cadences used to suggest a maximum reading age (three missed reports).</summary>
    public static int SamplingSeconds(string provider) => provider switch
    {
        var value when value.StartsWith("growatt", StringComparison.Ordinal) => 300,
        var value when value.StartsWith("daikin", StringComparison.Ordinal) => 900,
        var value when value.StartsWith("ewelink", StringComparison.Ordinal) => 300,
        var value when value.StartsWith("tuya", StringComparison.Ordinal) => 1800,
        _ => 300,
    };

    public static string MetricLabel(Metric metric) => metric switch
    {
        Metric.GridVoltage => "AC input voltage",
        Metric.GridAvailable => "Grid available",
        Metric.GridFrequency => "AC input frequency",
        Metric.GridImportPower => "Grid import power",
        Metric.BatterySoc => "Battery SOC",
        Metric.BatteryPower => "Battery power",
        Metric.PvPower => "PV power",
        Metric.HouseholdPower => "Household power",
        Metric.AcOutputVoltage => "AC output voltage",
        Metric.AcInputPower => "AC input power",
        Metric.AcChargingPower => "AC charging power",
        Metric.InverterLoadPercent => "Inverter load",
        Metric.SwitchOn => "Relay",
        Metric.ActivePower => "Active power",
        Metric.OutdoorTemperature => "Outdoor temperature",
        Metric.TargetTemperature => "Target temperature",
        Metric.LeavingWaterTemperature => "Leaving water temperature",
        Metric.TargetLeavingWaterTemperature => "Leaving water target",
        Metric.OperatingMode => "Operating mode",
        Metric.WarningStatus => "Warning status",
        _ => string.Concat(metric.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : character.ToString())),
    };

    public static string EventLabel(string code) => code switch
    {
        DeviceOffline => "went offline",
        AnyFault => "reported a fault",
        _ => $"reported fault {code}",
    };

    /// <summary>Reads every referenced signal for one property. Signals outside the property, retired or simulated are unavailable.</summary>
    public static async Task<Func<RuleCondition, SignalRef, SignalReading>> LoadAsync(MySqlConnection db, MySqlTransaction? tx, long property,
        IReadOnlyCollection<RuleCondition> conditions, DateTimeOffset now, CancellationToken ct)
    {
        var signals = conditions.SelectMany(condition => condition.Signals).Distinct().ToArray();
        var deviceIds = signals.Where(signal => signal.DeviceId is not null).Select(signal => signal.DeviceId!.Value).Distinct().ToArray();
        var installationIds = signals.Where(signal => signal.InstallationId is not null).Select(signal => signal.InstallationId!.Value).Distinct().ToArray();

        var devices = deviceIds.Length == 0 ? new Dictionary<long, DeviceRow>() : (await db.QueryAsync<DeviceRow>(Query("""
            SELECT d.id AS Id, d.property_id AS PropertyId, COALESCE(d.display_name, d.name) AS Name, d.retired_at_utc AS RetiredAt,
                   CAST(d.inventory_key LIKE 'simulator:%' AS SIGNED) AS Simulated
            FROM devices d WHERE d.id IN @deviceIds
            """, ct, new { deviceIds }, tx))).ToDictionary(row => row.Id);
        var installations = installationIds.Length == 0 ? new Dictionary<long, InstallationRow>() : (await db.QueryAsync<InstallationRow>(Query("""
            SELECT id AS Id, property_id AS PropertyId, name AS Name FROM installations WHERE id IN @installationIds
            """, ct, new { installationIds }, tx))).ToDictionary(row => row.Id);

        var current = deviceIds.Where(id => devices.TryGetValue(id, out var device) && device.PropertyId == property &&
            device.RetiredAt is null && device.Simulated == 0).ToArray();
        var endpointSeries = current.Length == 0 ? [] : await db.QueryAsync<SeriesRow>(Query("""
            SELECT e.device_id AS DeviceId, CAST(NULL AS SIGNED) AS InstallationId, e.endpoint_key AS EndpointKey, e.name AS EndpointName, s.id AS SeriesId,
                   s.metric AS Metric, s.source_key AS Source, r.observed_at_utc AS Observed, r.numeric_value AS Number,
                   r.boolean_value AS Flag, r.status_value AS Text
            FROM device_endpoints e JOIN telemetry_series s ON s.endpoint_id = e.id
            LEFT JOIN latest_readings r ON r.series_id = s.id
            WHERE e.property_id = @property AND e.device_id IN @current AND s.source_key <> 'simulator'
            """, ct, new { property, current }, tx));
        var installationSeries = installationIds.Length == 0 ? [] : await db.QueryAsync<SeriesRow>(Query("""
            SELECT CAST(NULL AS SIGNED) AS DeviceId, s.installation_id AS InstallationId, CAST(NULL AS CHAR) AS EndpointKey, CAST(NULL AS CHAR) AS EndpointName, s.id AS SeriesId,
                   s.metric AS Metric, s.source_key AS Source, r.observed_at_utc AS Observed, r.numeric_value AS Number,
                   r.boolean_value AS Flag, r.status_value AS Text
            FROM telemetry_series s LEFT JOIN latest_readings r ON r.series_id = s.id
            WHERE s.property_id = @property AND s.installation_id IN @installationIds AND s.source_key <> 'simulator'
            """, ct, new { property, installationIds }, tx));
        // Several sources can report one endpoint metric; the newest observation wins.
        var series = endpointSeries.Concat(installationSeries)
            .GroupBy(row => (row.DeviceId, row.InstallationId, row.EndpointKey, row.Metric))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Observed ?? DateTime.MinValue).First());

        var names = current.Length == 0 ? new Dictionary<long, string>() : await OwnerNamesAsync(db, tx, property, ct);
        var connectivity = current.Length == 0 ? new Dictionary<long, StateRow>() : (await db.QueryAsync<StateRow>(Query("""
            SELECT device_id AS DeviceId, connectivity AS Connectivity, observed_at_utc AS Observed FROM device_state WHERE device_id IN @current
            """, ct, new { current }, tx))).ToDictionary(row => row.DeviceId);

        var eventWindow = conditions.Where(condition => condition.Operator == ConditionOperator.EventOccurred)
            .Select(condition => condition.WindowSeconds ?? 0).DefaultIfEmpty(0).Max();
        var eventDevices = signals.Where(signal => signal.Kind == SignalKind.DeviceEvent).Select(signal => signal.DeviceId!.Value)
            .Intersect(current).ToArray();
        var events = eventWindow == 0 || eventDevices.Length == 0 ? [] : (await db.QueryAsync<EventRow>(Query("""
            SELECT device_id AS DeviceId, alert_code AS Code, occurred_at_utc AS Occurred FROM device_alerts
            WHERE device_id IN @eventDevices AND occurred_at_utc >= @since AND occurred_at_utc <= @until
            ORDER BY occurred_at_utc DESC LIMIT 2000
            """, ct, new { eventDevices, since = (now - TimeSpan.FromSeconds(eventWindow)).UtcDateTime, until = now.UtcDateTime }, tx))).ToArray();

        // State changes are read per series and window, bounded to a few hundred samples.
        var changes = new Dictionary<(long Series, int Window), bool>();
        foreach (var condition in conditions.Where(condition => condition.Operator == ConditionOperator.StateChanged))
        foreach (var signal in condition.Signals)
        {
            if (!series.TryGetValue(Lookup(signal), out var row)) continue;
            var window = condition.WindowSeconds ?? 0;
            if (changes.ContainsKey((row.SeriesId, window))) continue;
            var since = (now - TimeSpan.FromSeconds(window)).UtcDateTime;
            var samples = (await db.QueryAsync<ChangeRow>(Query("""
                (SELECT observed_at_utc AS Observed, boolean_value AS Flag, status_value AS Text FROM telemetry_samples
                 WHERE series_id = @SeriesId AND observed_at_utc < @since ORDER BY observed_at_utc DESC LIMIT 1)
                UNION ALL
                (SELECT observed_at_utc AS Observed, boolean_value AS Flag, status_value AS Text FROM telemetry_samples
                 WHERE series_id = @SeriesId AND observed_at_utc >= @since ORDER BY observed_at_utc LIMIT 500)
                """, ct, new { row.SeriesId, since }, tx))).OrderBy(sample => sample.Observed).ToArray();
            changes[(row.SeriesId, window)] = samples.Zip(samples.Skip(1)).Any(pair =>
                pair.Second.Observed >= since && (pair.First.Flag != pair.Second.Flag || pair.First.Text != pair.Second.Text));
        }

        return (condition, signal) =>
        {
            var label = Label(signal, devices, names, installations, series);
            if (signal.DeviceId is { } deviceId)
            {
                if (!devices.TryGetValue(deviceId, out var device) || device.PropertyId != property)
                    return SignalReading.Missing(signal, label, "The device is not in this property.");
                if (device.RetiredAt is not null)
                    return SignalReading.Missing(signal, label, "The device moved to another property or was removed.");
                if (device.Simulated != 0) return SignalReading.Missing(signal, label, "Simulated devices are not evaluated.");
            }
            if (signal.InstallationId is { } installationId &&
                (!installations.TryGetValue(installationId, out var installation) || installation.PropertyId != property))
                return SignalReading.Missing(signal, label, "The installation is not in this property.");

            switch (signal.Kind)
            {
                case SignalKind.Connectivity:
                    return connectivity.TryGetValue(signal.DeviceId!.Value, out var state)
                        ? new SignalReading(signal, label, Text: state.Connectivity, ObservedAt: Utc(state.Observed))
                        : SignalReading.Missing(signal, label, "No connectivity has been reported.");
                case SignalKind.DeviceEvent:
                    var since = now - TimeSpan.FromSeconds(condition.WindowSeconds ?? 0);
                    var matching = events.Where(item => item.DeviceId == signal.DeviceId && Utc(item.Occurred) >= since &&
                        (signal.EventCode == AnyFault ? item.Code != DeviceOffline : item.Code == signal.EventCode)).ToArray();
                    return new SignalReading(signal, label, Events: matching.Length,
                        LastEventAt: matching.Length == 0 ? null : Utc(matching.Max(item => item.Occurred)));
                default:
                    if (!series.TryGetValue(Lookup(signal), out var row))
                        return SignalReading.Missing(signal, label, "This device or installation has not reported the measurement.");
                    return new SignalReading(signal, label, Number: row.Number, Flag: row.Flag, Text: row.Text, ObservedAt: Utc(row.Observed),
                        Changed: condition.Operator == ConditionOperator.StateChanged &&
                            changes.TryGetValue((row.SeriesId, condition.WindowSeconds ?? 0), out var changed) && changed);
            }
        };
    }

    private static (long?, long?, string?, string) Lookup(SignalRef signal) =>
        (signal.InstallationId is null ? signal.DeviceId : null, signal.InstallationId, signal.InstallationId is null ? signal.EndpointKey : null,
            signal.Metric?.ToString() ?? "");

    private static string Label(SignalRef signal, IReadOnlyDictionary<long, DeviceRow> devices, IReadOnlyDictionary<long, string> names,
        IReadOnlyDictionary<long, InstallationRow> installations, IReadOnlyDictionary<(long?, long?, string?, string), SeriesRow> series)
    {
        var baseName = signal.DeviceId is { } device ? devices.TryGetValue(device, out var row) ? row.Name : $"Device {device}"
            : signal.InstallationId is { } installation && installations.TryGetValue(installation, out var site) ? site.Name : "Installation";
        var owner = signal.DeviceId is { } id && names.TryGetValue(id, out var distinct) ? distinct : baseName;
        return signal.Kind switch
        {
            SignalKind.Connectivity => $"{owner} · connectivity",
            SignalKind.DeviceEvent => $"{owner} {EventLabel(signal.EventCode ?? "")}",
            _ => MeasurementLabel(owner, baseName, series.TryGetValue(Lookup(signal), out var endpoint) ? endpoint.EndpointName : null,
                signal.Metric!.Value),
        };
    }

    /// <summary>Every signal the editor may use in the property: current, non-simulated devices and the property's installations.</summary>
    public static async Task<IReadOnlyList<SignalCatalogEntry>> CatalogAsync(MySqlConnection db, long property, CancellationToken ct)
    {
        var devices = (await db.QueryAsync<CatalogDevice>(Query("""
            SELECT d.id AS Id, COALESCE(d.display_name, d.name) AS Name,
                   COALESCE((SELECT MIN(c.provider_code) FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                             WHERE b.device_id = d.id), 'unknown') AS Provider,
                   st.connectivity AS Connectivity, st.observed_at_utc AS Observed
            FROM devices d LEFT JOIN device_state st ON st.device_id = d.id
            WHERE d.property_id = @property AND d.retired_at_utc IS NULL AND d.inventory_key NOT LIKE 'simulator:%'
            ORDER BY Name, d.id
            """, ct, new { property }))).ToArray();
        var ids = devices.Select(device => device.Id).ToArray();
        var rows = ids.Length == 0 ? [] : (await db.QueryAsync<SeriesRow>(Query("""
            SELECT e.device_id AS DeviceId, CAST(NULL AS SIGNED) AS InstallationId, e.endpoint_key AS EndpointKey, e.name AS EndpointName, s.id AS SeriesId,
                   s.metric AS Metric, s.source_key AS Source, r.observed_at_utc AS Observed, r.numeric_value AS Number,
                   r.boolean_value AS Flag, r.status_value AS Text
            FROM device_endpoints e JOIN telemetry_series s ON s.endpoint_id = e.id
            LEFT JOIN latest_readings r ON r.series_id = s.id
            WHERE e.property_id = @property AND e.device_id IN @ids AND s.source_key <> 'simulator'
            """, ct, new { property, ids }))).ToList();
        var installations = (await db.QueryAsync<InstallationRow>(Query(
            "SELECT id AS Id, property_id AS PropertyId, name AS Name FROM installations WHERE property_id = @property AND installation_key <> 'solar'",
            ct, new { property }))).ToDictionary(row => row.Id);
        if (installations.Count > 0)
            rows.AddRange(await db.QueryAsync<SeriesRow>(Query("""
                SELECT CAST(NULL AS SIGNED) AS DeviceId, s.installation_id AS InstallationId, CAST(NULL AS CHAR) AS EndpointKey, CAST(NULL AS CHAR) AS EndpointName, s.id AS SeriesId,
                       s.metric AS Metric, s.source_key AS Source, r.observed_at_utc AS Observed, r.numeric_value AS Number,
                       r.boolean_value AS Flag, r.status_value AS Text
                FROM telemetry_series s LEFT JOIN latest_readings r ON r.series_id = s.id
                WHERE s.property_id = @property AND s.installation_id IN @installationIds AND s.source_key <> 'simulator'
                """, ct, new { property, installationIds = installations.Keys.ToArray() })));
        var faults = ids.Length == 0 ? [] : await db.QueryAsync<EventRow>(Query("""
            SELECT device_id AS DeviceId, alert_code AS Code, MAX(occurred_at_utc) AS Occurred FROM device_alerts
            WHERE device_id IN @ids AND alert_code <> 'DEVICE_OFFLINE' GROUP BY device_id, alert_code ORDER BY Occurred DESC LIMIT 200
            """, ct, new { ids }));

        var byId = devices.ToDictionary(device => device.Id);
        var names = devices.Length == 0 ? new Dictionary<long, string>() : await OwnerNamesAsync(db, null, property, ct);
        string NameOf(CatalogDevice device) => names.TryGetValue(device.Id, out var name) ? name : device.Name;
        var entries = new List<SignalCatalogEntry>();
        foreach (var group in rows.Where(row => Enum.TryParse<Metric>(row.Metric, out var metric) && Enum.IsDefined(metric))
                     .GroupBy(row => (row.DeviceId, row.InstallationId, row.EndpointKey, row.Metric)))
        {
            var row = group.OrderByDescending(item => item.Observed ?? DateTime.MinValue).First();
            var metric = Enum.Parse<Metric>(row.Metric);
            var provider = row.DeviceId is { } device ? byId[device].Provider : row.Source;
            var signal = row.DeviceId is { } deviceId
                ? new SignalRef(SignalKind.Measurement, DeviceId: deviceId, EndpointKey: row.EndpointKey, Metric: metric)
                : new SignalRef(SignalKind.Measurement, InstallationId: row.InstallationId, Metric: metric);
            var baseName = row.DeviceId is { } ownerId ? byId[ownerId].Name : installations[row.InstallationId!.Value].Name;
            var owner = row.DeviceId is { } namedId ? NameOf(byId[namedId]) : baseName;
            var label = MeasurementLabel(owner, baseName, row.EndpointName, metric);
            var type = Domain.AlertSignals.ValueType(metric);
            entries.Add(Entry(signal, label, row.DeviceId, row.DeviceId is null ? null : owner, row.EndpointName, provider, type,
                Telemetry.Unit(metric), [], row.Number, row.Flag, row.Text, Utc(row.Observed)));
        }
        foreach (var device in devices)
        {
            var name = NameOf(device);
            entries.Add(Entry(new SignalRef(SignalKind.Connectivity, DeviceId: device.Id), $"{name} · connectivity", device.Id, name,
                null, device.Provider, SignalValueType.Status, null, Enum.GetNames<Connectivity>(), null, null, device.Connectivity, Utc(device.Observed)));
            foreach (var code in new[] { DeviceOffline, AnyFault }.Concat(faults.Where(fault => fault.DeviceId == device.Id).Select(fault => fault.Code)))
                entries.Add(Entry(new SignalRef(SignalKind.DeviceEvent, DeviceId: device.Id, EventCode: code), $"{name} {EventLabel(code)}",
                    device.Id, name, null, device.Provider, SignalValueType.Event, null, [], null, null, null, null));
        }
        return Distinct(entries).OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        // Entries with the same stable key are one signal; entries with different keys stay separate even when their labels match,
        // and gain the endpoint (or device) so the editor can tell them apart.
        static IEnumerable<SignalCatalogEntry> Distinct(IEnumerable<SignalCatalogEntry> all)
        {
            var unique = all.DistinctBy(entry => entry.Key).ToArray();
            var clashes = unique.GroupBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
                .SelectMany(group => group).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            return unique.Select(entry => !clashes.Contains(entry.Key) ? entry
                : entry.Signal.EndpointKey is { Length: > 0 } endpoint ? entry with { Label = $"{entry.Label} (channel {endpoint})" }
                : entry.DeviceId is { } device ? entry with { Label = $"{entry.Label} (device {device})" } : entry);
        }

        static SignalCatalogEntry Entry(SignalRef signal, string label, long? device, string? deviceName, string? endpoint, string provider,
            SignalValueType type, string? unit, IReadOnlyList<string> states, double? number, bool? flag, string? text, DateTimeOffset? observed)
        {
            var sampling = SamplingSeconds(provider);
            return new(Key(signal), signal, label, device, deviceName, endpoint, provider, type, unit,
                Domain.AlertSignals.Operators(type, signal.Kind), states, sampling, Math.Max(3 * sampling, 600), number, flag, text, observed);
        }
    }

    /// <summary>Checks that every referenced signal exists now in the property; rejects cross-property and retired references.</summary>
    public static async Task<IReadOnlyList<string>> ValidateReferencesAsync(MySqlConnection db, MySqlTransaction? tx, long property,
        RuleGroup definition, CancellationToken ct)
    {
        var errors = new List<string>();
        var signals = Domain.AlertSignals.Conditions(definition).SelectMany(condition => condition.Signals).Distinct().ToArray();
        var deviceIds = signals.Where(signal => signal.DeviceId is not null).Select(signal => signal.DeviceId!.Value).Distinct().ToArray();
        var installationIds = signals.Where(signal => signal.InstallationId is not null).Select(signal => signal.InstallationId!.Value).Distinct().ToArray();
        var devices = deviceIds.Length == 0 ? [] : (await db.QueryAsync<long>(Query("""
            SELECT id FROM devices WHERE id IN @deviceIds AND property_id = @property AND retired_at_utc IS NULL AND inventory_key NOT LIKE 'simulator:%'
            """, ct, new { deviceIds, property }, tx))).ToHashSet();
        var installations = installationIds.Length == 0 ? [] : (await db.QueryAsync<long>(Query(
            "SELECT id FROM installations WHERE id IN @installationIds AND property_id = @property", ct, new { installationIds, property }, tx))).ToHashSet();
        if (deviceIds.Any(id => !devices.Contains(id)) || installationIds.Any(id => !installations.Contains(id)))
        {
            errors.Add("A condition refers to a device or installation that is not currently in this property.");
            return errors;
        }
        var endpoints = signals.Where(signal => signal.Kind == SignalKind.Measurement && signal.DeviceId is not null).ToArray();
        if (endpoints.Length > 0)
        {
            var known = (await db.QueryAsync<(long Device, string Endpoint, string Metric)>(Query("""
                SELECT e.device_id, e.endpoint_key, s.metric FROM device_endpoints e JOIN telemetry_series s ON s.endpoint_id = e.id
                WHERE e.property_id = @property AND e.device_id IN @deviceIds
                """, ct, new { property, deviceIds }, tx))).ToHashSet();
            if (endpoints.Any(signal => !known.Contains((signal.DeviceId!.Value, signal.EndpointKey!, signal.Metric.ToString()!))))
                errors.Add("A condition uses a measurement the selected device has not reported.");
        }
        var installationMetrics = signals.Where(signal => signal.InstallationId is not null).ToArray();
        if (installationMetrics.Length > 0)
        {
            var known = (await db.QueryAsync<(long Installation, string Metric)>(Query(
                "SELECT installation_id, metric FROM telemetry_series WHERE property_id = @property AND installation_id IN @installationIds",
                ct, new { property, installationIds }, tx))).ToHashSet();
            if (installationMetrics.Any(signal => !known.Contains((signal.InstallationId!.Value, signal.Metric.ToString()!))))
                errors.Add("A condition uses a measurement the installation has not reported.");
        }
        return errors;
    }

    private sealed record DeviceRow(long Id, long PropertyId, string Name, DateTime? RetiredAt, long Simulated);
    private sealed record InstallationRow(long Id, long PropertyId, string Name);
    private sealed record SeriesRow(long? DeviceId, long? InstallationId, string? EndpointKey, string? EndpointName, long SeriesId, string Metric,
        string Source, DateTime? Observed, double? Number, bool? Flag, string? Text);
    private sealed record StateRow(long DeviceId, string Connectivity, DateTime Observed);
    private sealed record EventRow(long DeviceId, string Code, DateTime Occurred);
    private sealed record ChangeRow(DateTime Observed, bool? Flag, string? Text);
    private sealed record CatalogDevice(long Id, string Name, string Provider, string? Connectivity, DateTime? Observed);
}
