using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Growatt;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class MonitoringReader(Database database, TimeProvider timeProvider, SimulatorOptions simulator, GrowattOptions growatt)
    : IMonitoringReader, IGrowattMonitoringReader
{
    public async Task<IReadOnlyList<PropertySummary>> PropertiesAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<PropertySummary>(Query(
            """
            SELECT p.id AS Id, p.name AS Name, p.time_zone AS TimeZone
            FROM properties p JOIN property_memberships m ON m.property_id = p.id
            WHERE m.user_id = @UserId ORDER BY p.name, p.id
            """, cancellationToken, new { UserId = userId }))).AsList();
    }

    public async Task<bool> CanReadAsync(string userId, long propertyId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(Query(
            "SELECT EXISTS(SELECT 1 FROM property_memberships WHERE property_id = @PropertyId AND user_id = @UserId)",
            cancellationToken, new { UserId = userId, PropertyId = propertyId }));
    }

    public async Task<IReadOnlyList<RoomSummary>> RoomsAsync(long propertyId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<RoomSummary>(Query(
            "SELECT id AS Id, name AS Name FROM rooms WHERE property_id = @PropertyId ORDER BY name, id",
            cancellationToken, new { PropertyId = propertyId }))).AsList();
    }

    public Task<PropertyState> StateAsync(long propertyId, CancellationToken cancellationToken) =>
        ReadStateAsync(propertyId, null, "simulator", cancellationToken);

    public async Task<DeviceDetails?> DeviceAsync(long propertyId, long deviceId, CancellationToken cancellationToken) =>
        (await ReadStateAsync(propertyId, deviceId, "simulator", cancellationToken)).Devices.SingleOrDefault();

    public async Task<PropertyState?> GrowattStateAsync(long propertyId, string siteKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var matches = await connection.ExecuteScalarAsync<bool>(Query(
            "SELECT EXISTS(SELECT 1 FROM properties WHERE id = @PropertyId AND site_key = @SiteKey)",
            cancellationToken, new { PropertyId = propertyId, SiteKey = siteKey }));
        return matches ? await ReadStateAsync(propertyId, null, "growatt", cancellationToken) : null;
    }

    private async Task<PropertyState> ReadStateAsync(long propertyId, long? deviceId, string provider, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var results = await connection.QueryMultipleAsync(Query(
            """
            SELECT d.id AS Id, d.room_id AS RoomId, d.name AS Name, d.kind AS Kind, d.manufacturer AS Manufacturer,
                   d.model AS Model, s.connectivity AS Connectivity, s.fault_code AS FaultCode,
                   s.last_contact_utc AS LastContact, s.observed_at_utc AS StatusObserved
            FROM devices d LEFT JOIN device_state s ON s.device_id = d.id
            WHERE d.property_id = @PropertyId AND (@DeviceId IS NULL OR d.id = @DeviceId)
              AND (CASE WHEN @Provider = 'simulator' THEN NOT EXISTS (
                  SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                  WHERE b.device_id = d.id AND c.provider_code IN ('growatt','daikin'))
              ELSE EXISTS (SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                           WHERE b.device_id = d.id AND c.provider_code = @Provider) END)
            ORDER BY d.id;

            SELECT id AS Id, device_id AS DeviceId, name AS Name FROM device_endpoints
            WHERE property_id = @PropertyId AND (@DeviceId IS NULL OR device_id = @DeviceId) ORDER BY id;

            SELECT e.id AS EndpointId, c.capability_code AS Code, c.readable AS Readable
            FROM device_endpoints e JOIN device_capabilities c ON c.endpoint_id = e.id
            WHERE e.property_id = @PropertyId AND (@DeviceId IS NULL OR e.device_id = @DeviceId) ORDER BY c.capability_code;

            SELECT t.endpoint_id AS EndpointId, t.metric AS Metric, l.observed_at_utc AS ObservedAt,
                   l.numeric_value AS Number, l.boolean_value AS Flag, l.status_value AS Code
            FROM telemetry_series t LEFT JOIN latest_readings l ON l.series_id = t.id
            LEFT JOIN device_endpoints e ON e.id = t.endpoint_id
            WHERE t.property_id = @PropertyId AND t.source_key = @Provider
                  AND (@DeviceId IS NULL OR e.device_id = @DeviceId) ORDER BY t.metric;

            SELECT a.device_id AS DeviceId, d.name AS DeviceName, a.alert_code AS Code, a.occurred_at_utc AS OccurredAt
            FROM devices d JOIN device_alerts a ON a.device_id = d.id
            WHERE d.property_id = @PropertyId AND (@DeviceId IS NULL OR d.id = @DeviceId)
              AND (CASE WHEN @Provider = 'simulator' THEN NOT EXISTS (
                  SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                  WHERE b.device_id = d.id AND c.provider_code IN ('growatt','daikin'))
              ELSE EXISTS (SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                           WHERE b.device_id = d.id AND c.provider_code = @Provider) END)
            ORDER BY a.occurred_at_utc DESC, a.id DESC LIMIT 12;
            """, cancellationToken, new { PropertyId = propertyId, DeviceId = deviceId, Provider = provider }));

        var devices = await results.ReadAsync<DeviceRow>();
        var endpoints = (await results.ReadAsync<EndpointRow>()).ToLookup(row => row.DeviceId);
        var capabilities = (await results.ReadAsync<CapabilityRow>()).ToLookup(row => row.EndpointId);
        var readings = (await results.ReadAsync<ReadingRow>()).ToLookup(row => row.EndpointId);
        var alerts = (await results.ReadAsync<AlertRow>()).Select(row =>
            new DeviceAlert(row.DeviceId, row.DeviceName, row.Code, Utc(row.OccurredAt))).ToArray();
        var now = timeProvider.GetUtcNow();
        var freshness = TimeSpan.FromSeconds(provider == "growatt" ? growatt.FreshnessSeconds : simulator.FreshnessSeconds);

        return new PropertyState(devices.Select(device =>
        {
            var deviceEndpoints = endpoints[device.Id].Select(endpoint => new EndpointState(
                endpoint.Id, endpoint.Name,
                capabilities[endpoint.Id].Select(row => new CapabilityState(Enum.Parse<Capability>(row.Code),
                    row.Readable ? Access.ReadOnly : Access.Unsupported)).ToArray(),
                readings[endpoint.Id].Select(ToReading).ToArray())).ToArray();
            var qualities = deviceEndpoints.SelectMany(endpoint => endpoint.Measurements).Select(sample => sample.Quality).ToList();
            var quality = qualities.Contains(ReadingQuality.Fresh) ? ReadingQuality.Fresh
                : qualities.Contains(ReadingQuality.Stale) ? ReadingQuality.Stale : ReadingQuality.Missing;
            var online = Telemetry.Quality(Utc(device.StatusObserved), now, freshness) == ReadingQuality.Fresh
                && Enum.TryParse<Connectivity>(device.Connectivity, out var status) ? status : Connectivity.Unknown;
            return new DeviceDetails(device.Id, device.RoomId, device.Name, Enum.Parse<DeviceKind>(device.Kind),
                device.Manufacturer, device.Model, online, device.FaultCode, Utc(device.LastContact), quality, deviceEndpoints);
        }).ToArray(), readings[null].Select(ToReading).ToArray(), alerts);

        MetricReading ToReading(ReadingRow row)
        {
            var metric = Enum.Parse<Metric>(row.Metric);
            var observedAt = Utc(row.ObservedAt);
            return new MetricReading(metric, Telemetry.Unit(metric), observedAt, row.Number, row.Flag, row.Code,
                Telemetry.Quality(observedAt, now, freshness));
        }
    }

    public async Task<IReadOnlyList<HistoryPoint>> HistoryAsync(long propertyId, long? endpointId,
        Metric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<HistoryRow>(Query(
            """
            SELECT h.observed_at_utc AS ObservedAt, h.numeric_value AS Number,
                   h.boolean_value AS Flag, h.status_value AS Code
            FROM telemetry_series s JOIN telemetry_samples h ON h.series_id = s.id
            WHERE s.property_id = @PropertyId AND s.endpoint_id <=> @EndpointId
              AND s.source_key = 'simulator' AND s.metric = @Metric
              AND h.observed_at_utc >= @From AND h.observed_at_utc <= @To
            ORDER BY h.observed_at_utc DESC LIMIT 5000
            """, cancellationToken, new { PropertyId = propertyId, EndpointId = endpointId, Metric = metric.ToString(), From = from.UtcDateTime, To = to.UtcDateTime }));
        return rows.Reverse().Select(row => new HistoryPoint(Utc(row.ObservedAt), row.Number, row.Flag, row.Code)).ToArray();
    }

    private sealed record DeviceRow(long Id, long? RoomId, string Name, string Kind, string Manufacturer, string Model,
        string? Connectivity, string? FaultCode, DateTime? LastContact, DateTime? StatusObserved);
    private sealed record EndpointRow(long Id, long DeviceId, string Name);
    private sealed record CapabilityRow(long EndpointId, string Code, bool Readable);
    private sealed record ReadingRow(long? EndpointId, string Metric, DateTime? ObservedAt, double? Number, bool? Flag, string? Code);
    private sealed record AlertRow(long DeviceId, string DeviceName, string Code, DateTime OccurredAt);
    private sealed record HistoryRow(DateTime ObservedAt, double? Number, bool? Flag, string? Code);
}
