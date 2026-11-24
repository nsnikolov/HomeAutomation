using System.Globalization;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

internal static class DashboardSources
{
    public static async Task<DashboardSource[]> ReadAsync(MySqlTransaction tx, long property, TuyaOptions tuya,
        CancellationToken ct)
    {
        using var rows = await tx.Connection!.QueryMultipleAsync(Query("""
            SELECT s.id AS Id,s.source_key AS Provider,d.id AS DeviceId,d.name AS DeviceName,e.name AS Channel,
                s.metric AS Metric,l.observed_at_utc AS ObservedAt,l.numeric_value AS Number,
                l.boolean_value AS Flag,l.status_value AS Code
            FROM telemetry_series s
            JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
            JOIN devices d ON d.id=e.device_id AND d.property_id=e.property_id
            LEFT JOIN latest_readings l ON l.series_id=s.id
            WHERE s.property_id=@property AND s.source_key IN ('growatt','simulator')
              AND EXISTS (
                  SELECT 1 FROM provider_bindings b JOIN integration_connections c
                    ON c.id=b.connection_id AND c.property_id=b.property_id
                  WHERE b.device_id=d.id AND b.property_id=@property AND c.provider_code=s.source_key
              );
            SELECT d.public_id AS Id,d.external_id AS ExternalId,d.property_id AS PropertyId,d.room_id AS RoomId,
                d.snapshot AS Snapshot
            FROM daikin_devices d JOIN daikin_account a ON a.id=1 AND a.owner_id=d.owner_id
            WHERE d.property_id=@property AND d.snapshot IS NOT NULL;
            SELECT d.public_id AS Id,d.external_id AS ExternalId,d.property_id AS PropertyId,d.room_id AS RoomId,
                d.snapshot AS Snapshot,d.relay_write_enabled AS RelayWriteEnabled
            FROM ewelink_devices d JOIN ewelink_account a ON a.id=1 AND a.owner_id=d.owner_id
            WHERE d.property_id=@property;
            SELECT public_id AS Id,external_id AS ExternalId,property_id AS PropertyId,room_id AS RoomId,snapshot AS Snapshot
            FROM tuya_devices WHERE property_id=@property AND project_scope=@scope;
            SELECT d.public_id AS DeviceId,e.endpoint_key AS Point,s.metric AS Metric,l.observed_at_utc AS ObservedAt,l.numeric_value AS Number
            FROM tuya_devices d
            JOIN integration_connections c ON c.property_id=d.property_id AND c.provider_code='tuya'
            JOIN provider_bindings b ON b.property_id=c.property_id AND b.connection_id=c.id AND b.external_id=d.external_id
            JOIN device_endpoints e ON e.device_id=b.device_id AND e.property_id=b.property_id
            JOIN telemetry_series s ON s.endpoint_id=e.id AND s.property_id=e.property_id AND s.source_key='tuya-sensor-report'
            JOIN latest_readings l ON l.series_id=s.id
            WHERE d.property_id=@property AND d.project_scope=@scope AND s.metric IN ('Temperature','Humidity');
            """, ct, new { property, scope = tuya.IsConfigured ? tuya.Scope : null }, tx));

        var canonical = await rows.ReadAsync<SeriesRow>();
        var daikin = await rows.ReadAsync<DaikinRow>();
        var ewelink = await rows.ReadAsync<EwelinkRow>();
        var tuyaDevices = await rows.ReadAsync<TuyaRow>();
        var reports = (await rows.ReadAsync<TuyaReport>()).ToLookup(report => (report.DeviceId, report.Point, report.Metric));
        return canonical.SelectMany(ProjectSeries)
            .Concat(daikin.SelectMany(row => row.Device is { Available: not false } device ? ProjectDaikin(row.Id, device, property) : []))
            .Concat(ewelink.SelectMany(row => ProjectEwelink(row.Id, row.Device, property)))
            .Concat(tuyaDevices.SelectMany(row => ProjectTuya(row, reports, property)))
            .OrderBy(source => source.Provider, StringComparer.Ordinal)
            .ThenBy(source => source.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Metric)
            .ThenBy(source => source.Key, StringComparer.Ordinal).ToArray();

        IEnumerable<DashboardSource> ProjectSeries(SeriesRow row)
        {
            if (!Enum.TryParse<Metric>(row.Metric, out var metric) || !Enum.IsDefined(metric)) yield break;
            var device = row.DeviceId.ToString(CultureInfo.InvariantCulture);
            yield return Source(row.Provider, device, row.DeviceName, row.Id.ToString(CultureInfo.InvariantCulture),
                row.Channel, metric, property, Reading(Utc(row.ObservedAt), row.Number, row.Flag, row.Code,
                    row.Provider == "simulator" ? "simulated" : "provider"));
        }
    }

    internal static IEnumerable<DashboardSource> ProjectDaikin(string id, DaikinDevice device, long property)
    {
        var (_, batch) = DaikinService.TelemetryFor(device);
        var providerAt = device.InvalidSourceTime ? null : device.SourceAt;
        var at = providerAt ?? (!device.CachedResponse && !device.InvalidSourceTime ? device.ProviderReceivedAt : null);
        return batch.Devices.Single().Endpoints.Single().Samples.Select(sample => Source("daikin", id, device.Name,
            sample.Metric.ToString(), null, sample.Metric, property,
            Reading(at, sample.Number, sample.Flag, sample.Code, providerAt is null ? "serverReceived" : "provider")));
    }

    internal static IEnumerable<DashboardSource> ProjectEwelink(string id, EwelinkDevice device, long property)
    {
        if (device.HasRelay)
        {
            var socket = device.SocketRelayAt is { } at && (device.RelayAt is null || at > device.RelayAt);
            yield return Source("ewelink", id, device.Name, nameof(Metric.SwitchOn), null, Metric.SwitchOn, property,
                Reading(socket ? device.SocketRelayAt : device.RelayAt, flag: socket ? device.SocketRelay : device.Relay,
                    provenance: socket ? "provider" : "serverReceived"));
        }
        foreach (var metric in device.SupportedMetrics())
        {
            var measurement = device.Measurements?.GetValueOrDefault(metric);
            yield return Source("ewelink", id, device.Name, metric.ToString(), null, metric, property,
                Reading(measurement?.ReceivedAt, measurement?.Value,
                    provenance: measurement is { FromSocket: true, ServerReceived: false } ? "provider" : "serverReceived"));
        }
    }

    private static IEnumerable<DashboardSource> ProjectTuya(TuyaRow row, ILookup<(string, string, string), TuyaReport> reports, long property)
    {
        var device = row.Device;
        if (!device.Present) yield break;
        var readings = device.Readings.ToLookup(reading => reading.Code, StringComparer.Ordinal);
        foreach (var point in device.Points)
        {
            var reading = readings[point.Code].FirstOrDefault();
            var report = reports[(row.Id, point.Code, point.Metric.ToString())].MaxBy(item => item.ObservedAt);
            var projection = report is not null && (reading?.At is not { } at || Utc(report.ObservedAt) >= at)
                ? Reading(Utc(report.ObservedAt), report.Number)
                : Reading(reading?.At, reading?.Number, reading?.Flag);
            yield return Source("tuya", row.Id, device.Name, point.Code, point.Code, point.Metric, property, projection);
        }
    }

    private static DashboardSource Source(string provider, string id, string name, string channelKey, string? channel,
        Metric metric, long property, DashboardReading? reading)
    {
        var prefix = "/properties/" + property.ToString(CultureInfo.InvariantCulture);
        var route = provider == "simulator" ? $"{prefix}/devices/{id}" : $"{prefix}/{provider}";
        return new($"{provider}:{id}:{channelKey}", provider, id, name, channel, metric, Telemetry.Unit(metric), route, reading);
    }

    private static DashboardReading? Reading(DateTimeOffset? at, double? number = null, bool? flag = null,
        string? code = null, string provenance = "provider") =>
        at is { } observed && observed != default &&
        (number is { } scalar ? double.IsFinite(scalar) : flag.HasValue || !string.IsNullOrWhiteSpace(code))
            ? new(number, flag, code, observed, provenance) : null;

    private sealed record SeriesRow(long Id, string Provider, long DeviceId, string DeviceName, string Channel, string Metric,
        DateTime? ObservedAt, double? Number, bool? Flag, string? Code);
    private sealed record TuyaReport(string DeviceId, string Point, string Metric, DateTime ObservedAt, double? Number);
}
