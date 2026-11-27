using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Growatt;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class MonitoringReader(Database database, TimeProvider timeProvider, SimulatorOptions simulator, GrowattOptions growatt,
    TelemetryHoldPolicy? holdPolicy = null)
    : IMonitoringReader, IGrowattMonitoringReader
{
    private readonly TelemetryHoldPolicy holds = holdPolicy ?? new(growatt, new(), new(), new(), simulator);

    public async Task<IReadOnlyList<PropertySummary>> PropertiesAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<PropertySummary>(Query(
            """
            SELECT p.id AS Id, p.name AS Name, p.time_zone AS TimeZone
            FROM properties p JOIN property_memberships m ON m.property_id = p.id
            WHERE m.user_id = @UserId AND p.deleted_at_utc IS NULL ORDER BY p.name, p.id
            """, cancellationToken, new { UserId = userId }))).AsList();
    }

    public async Task<bool> CanReadAsync(string userId, long propertyId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(Query(
            """
            SELECT EXISTS(SELECT 1 FROM property_memberships m JOIN properties p ON p.id = m.property_id AND p.deleted_at_utc IS NULL
                          WHERE m.property_id = @PropertyId AND m.user_id = @UserId)
            """,
            cancellationToken, new { UserId = userId, PropertyId = propertyId }));
    }

    public async Task<IReadOnlyList<RoomSummary>> RoomsAsync(long propertyId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<RoomSummary>(Query(
            "SELECT id AS Id, name AS Name FROM rooms WHERE property_id = @PropertyId ORDER BY name, id",
            cancellationToken, new { PropertyId = propertyId }))).AsList();
    }

    /// <summary>
    /// Every device of the property bound to a real provider, with its latest provider readings. Simulated devices and series
    /// appear only while the opt-in simulator is enabled. Missing readings stay missing.
    /// </summary>
    public Task<PropertyState> StateAsync(long propertyId, CancellationToken cancellationToken) =>
        ReadStateAsync(propertyId, null, null, cancellationToken);

    public async Task<DeviceDetails?> DeviceAsync(long propertyId, long deviceId, CancellationToken cancellationToken) =>
        (await ReadStateAsync(propertyId, deviceId, null, cancellationToken)).Devices.SingleOrDefault();

    public async Task<PropertyState?> GrowattStateAsync(long propertyId, string siteKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var matches = await connection.ExecuteScalarAsync<bool>(Query(
            "SELECT EXISTS(SELECT 1 FROM properties WHERE id = @PropertyId AND site_key = @SiteKey)",
            cancellationToken, new { PropertyId = propertyId, SiteKey = siteKey }));
        return matches ? await ReadStateAsync(propertyId, null, "growatt", cancellationToken) : null;
    }

    /// <param name="provider">One provider's devices and series, or null for every real provider.</param>
    private async Task<PropertyState> ReadStateAsync(long propertyId, long? deviceId, string? provider, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var parameters = new
        {
            PropertyId = propertyId, DeviceId = deviceId, Provider = provider,
            Simulator = simulator.Enabled ? TelemetryHoldPolicy.SimulatorSource : "growatt"
        };
        const string bound = """
            (CASE WHEN @Provider IS NULL THEN EXISTS (
                  SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                  WHERE b.device_id = d.id AND c.provider_code IN ('growatt','daikin','ewelink','tuya',@Simulator))
              ELSE EXISTS (SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                           WHERE b.device_id = d.id AND c.provider_code = @Provider) END)
            """;
        using var results = await connection.QueryMultipleAsync(Query(
            $"""
            SELECT d.id AS Id, d.room_id AS RoomId, COALESCE(d.display_name, d.name) AS Name, d.name AS ProviderName, d.kind AS Kind, d.manufacturer AS Manufacturer,
                   d.model AS Model, s.connectivity AS Connectivity, s.fault_code AS FaultCode,
                   s.last_contact_utc AS LastContact, s.observed_at_utc AS StatusObserved,
                   COALESCE((SELECT c.provider_code FROM provider_bindings b JOIN integration_connections c ON c.id = b.connection_id
                             WHERE b.device_id = d.id ORDER BY c.provider_code = 'simulator', b.id LIMIT 1), 'unknown') AS Provider
            FROM devices d LEFT JOIN device_state s ON s.device_id = d.id
            WHERE d.property_id = @PropertyId AND (@DeviceId IS NULL OR d.id = @DeviceId) AND d.retired_at_utc IS NULL AND {bound}
            ORDER BY d.id;

            SELECT id AS Id, device_id AS DeviceId, name AS Name FROM device_endpoints
            WHERE property_id = @PropertyId AND (@DeviceId IS NULL OR device_id = @DeviceId) ORDER BY id;

            SELECT e.id AS EndpointId, c.capability_code AS Code, c.readable AS Readable
            FROM device_endpoints e JOIN device_capabilities c ON c.endpoint_id = e.id
            WHERE e.property_id = @PropertyId AND (@DeviceId IS NULL OR e.device_id = @DeviceId) ORDER BY c.capability_code;

            SELECT t.endpoint_id AS EndpointId, t.metric AS Metric, l.observed_at_utc AS ObservedAt,
                   l.numeric_value AS Number, l.boolean_value AS Flag, l.status_value AS Code, t.source_key AS Source
            FROM telemetry_series t LEFT JOIN latest_readings l ON l.series_id = t.id
            LEFT JOIN device_endpoints e ON e.id = t.endpoint_id
            WHERE t.property_id = @PropertyId
                  AND (CASE WHEN @Provider IS NULL THEN t.source_key <> 'simulator' OR t.source_key = @Simulator
                       ELSE t.source_key = @Provider END)
                  AND (@DeviceId IS NULL OR e.device_id = @DeviceId) ORDER BY t.metric, t.id;

            SELECT a.device_id AS DeviceId, COALESCE(d.display_name, d.name) AS DeviceName, a.alert_code AS Code, a.occurred_at_utc AS OccurredAt
            FROM devices d JOIN device_alerts a ON a.device_id = d.id
            WHERE d.property_id = @PropertyId AND (@DeviceId IS NULL OR d.id = @DeviceId) AND d.retired_at_utc IS NULL AND {bound}
            ORDER BY a.occurred_at_utc DESC, a.id DESC LIMIT 12;
            """, cancellationToken, parameters));

        var devices = await results.ReadAsync<DeviceRow>();
        var endpoints = (await results.ReadAsync<EndpointRow>()).ToLookup(row => row.DeviceId);
        var capabilities = (await results.ReadAsync<CapabilityRow>()).ToLookup(row => row.EndpointId);
        // Several streams can report one metric for an endpoint (for example Tuya snapshots and sensor reports); show the latest.
        var readings = (await results.ReadAsync<ReadingRow>()).Where(row => Enum.TryParse<Metric>(row.Metric, out _))
            .GroupBy(row => (row.EndpointId, row.Metric))
            .Select(group => group.OrderByDescending(row => row.ObservedAt ?? DateTime.MinValue).First())
            .ToLookup(row => row.EndpointId);
        var alerts = (await results.ReadAsync<AlertRow>()).Select(row =>
            new DeviceAlert(row.DeviceId, row.DeviceName, row.Code, Utc(row.OccurredAt))).ToArray();
        var now = timeProvider.GetUtcNow();
        var growattTimeZone = TelemetryHoldPolicy.Zone(growatt.TimeZone);

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
            var freshness = provider == "growatt" ? TimeSpan.FromSeconds(growatt.FreshnessSeconds) : holds.Hold(device.Provider, Metric.SwitchOn);
            var online = Telemetry.Quality(Utc(device.StatusObserved), now, freshness) == ReadingQuality.Fresh
                && Enum.TryParse<Connectivity>(device.Connectivity, out var status) ? status : Connectivity.Unknown;
            return new DeviceDetails(device.Id, device.RoomId, device.Name, Enum.Parse<DeviceKind>(device.Kind),
                device.Manufacturer, device.Model, online, device.FaultCode, Utc(device.LastContact), quality, deviceEndpoints,
                device.ProviderName, device.Provider, device.ProviderName != device.Name);
        }).ToArray(), readings[null].Select(ToReading).ToArray(), alerts);

        MetricReading ToReading(ReadingRow row)
        {
            var metric = Enum.Parse<Metric>(row.Metric);
            var observedAt = Utc(row.ObservedAt);
            var freshness = provider == "growatt" ? TimeSpan.FromSeconds(growatt.FreshnessSeconds) : holds.Hold(row.Source, metric);
            if (row.Source == "growatt" && growattTimeZone is not null && Telemetry.IsDailyEnergyCounter(metric))
            {
                var quality = Telemetry.DailyEnergyQuality(observedAt, now, freshness, growattTimeZone);
                return quality == ReadingQuality.Missing
                    ? new MetricReading(metric, Telemetry.Unit(metric), null, null, null, null, quality)
                    : new MetricReading(metric, Telemetry.Unit(metric), observedAt, row.Number, null, null, quality);
            }
            return new MetricReading(metric, Telemetry.Unit(metric), observedAt, row.Number, row.Flag, row.Code,
                Telemetry.Quality(observedAt, now, freshness));
        }
    }

    /// <summary>
    /// Raw readings of one endpoint or installation metric from its most recently updated provider series. Simulated series are
    /// used only while the simulator is enabled, and separate series are never merged.
    /// </summary>
    public async Task<IReadOnlyList<HistoryPoint>> HistoryAsync(long propertyId, long? endpointId,
        Metric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var series = await connection.QuerySingleOrDefaultAsync<long?>(Query(
            """
            SELECT s.id FROM telemetry_series s LEFT JOIN latest_readings l ON l.series_id = s.id
            WHERE s.property_id = @PropertyId AND s.endpoint_id <=> @EndpointId AND s.metric = @Metric
              AND (s.source_key <> 'simulator' OR @IncludeSimulator)
            ORDER BY l.observed_at_utc IS NULL, l.observed_at_utc DESC, s.id LIMIT 1
            """, cancellationToken, new { PropertyId = propertyId, EndpointId = endpointId, Metric = metric.ToString(),
                IncludeSimulator = simulator.Enabled }));
        if (series is null) return [];
        var rows = await connection.QueryAsync<HistoryRow>(Query(
            """
            SELECT observed_at_utc AS ObservedAt, numeric_value AS Number, boolean_value AS Flag, status_value AS Code
            FROM telemetry_samples
            WHERE series_id = @Series AND observed_at_utc >= @From AND observed_at_utc <= @To
            ORDER BY observed_at_utc DESC LIMIT 5000
            """, cancellationToken, new { Series = series, From = from.UtcDateTime, To = to.UtcDateTime }));
        return rows.Reverse().Select(row => new HistoryPoint(Utc(row.ObservedAt), row.Number, row.Flag, row.Code)).ToArray();
    }

    private sealed record DeviceRow(long Id, long? RoomId, string Name, string ProviderName, string Kind, string Manufacturer, string Model,
        string? Connectivity, string? FaultCode, DateTime? LastContact, DateTime? StatusObserved, string Provider);
    private sealed record EndpointRow(long Id, long DeviceId, string Name);
    private sealed record CapabilityRow(long EndpointId, string Code, bool Readable);
    private sealed record ReadingRow(long? EndpointId, string Metric, DateTime? ObservedAt, double? Number, bool? Flag, string? Code,
        string Source);
    private sealed record AlertRow(long DeviceId, string DeviceName, string Code, DateTime OccurredAt);
    private sealed record HistoryRow(DateTime ObservedAt, double? Number, bool? Flag, string? Code);
}
