using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class TelemetryWriter(Database database, TimeProvider timeProvider) : ITelemetryWriter
{
    public async Task<long> RegisterDemoInventoryAsync(IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LoadSiteAsync(connection, transaction, null, cancellationToken);
        var rooms = (await connection.QueryAsync<RoomBinding>(Query(
            "SELECT id AS Id, room_key AS RoomKey FROM rooms WHERE property_id = @PropertyId",
            cancellationToken, site, transaction))).ToDictionary(room => room.RoomKey, room => room.Id);

        foreach (var device in devices)
        {
            var deviceId = await InsertIdentityAsync(connection, transaction,
                """
                INSERT INTO devices (property_id, room_id, inventory_key, name, kind, manufacturer, model)
                VALUES (@PropertyId, @RoomId, CONCAT('simulator:', @ExternalId), @Name, @Kind, @Manufacturer, @Model)
                ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
                """,
                new
                {
                    site.PropertyId, RoomId = rooms[device.RoomKey], device.ExternalId, device.Name,
                    Kind = device.Kind.ToString(), device.Manufacturer, device.Model
                }, cancellationToken);

            var bindingId = await InsertIdentityAsync(connection, transaction,
                """
                INSERT INTO provider_bindings (property_id, connection_id, device_id, external_id)
                VALUES (@PropertyId, @ConnectionId, @DeviceId, @ExternalId)
                ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
                """, new { site.PropertyId, site.ConnectionId, DeviceId = deviceId, device.ExternalId }, cancellationToken);

            if (device.InstallationRole is not null)
                await connection.ExecuteAsync(Query(
                    """
                    INSERT INTO installation_devices (property_id, installation_id, device_id, role_code)
                    VALUES (@PropertyId, @InstallationId, @DeviceId, @InstallationRole)
                    ON DUPLICATE KEY UPDATE role_code = installation_devices.role_code
                    """, cancellationToken, new { site.PropertyId, site.InstallationId, DeviceId = deviceId, device.InstallationRole }, transaction));

            foreach (var endpoint in device.Endpoints)
            {
                var endpointId = await InsertIdentityAsync(connection, transaction,
                    """
                    INSERT INTO device_endpoints (property_id, device_id, endpoint_key, name)
                    VALUES (@PropertyId, @DeviceId, @Key, @Name)
                    ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
                    """, new { site.PropertyId, DeviceId = deviceId, endpoint.Key, endpoint.Name }, cancellationToken);

                await connection.ExecuteAsync(Query(
                    """
                    INSERT INTO provider_endpoint_bindings (binding_id, device_id, endpoint_id, external_endpoint_key)
                    VALUES (@BindingId, @DeviceId, @EndpointId, @Key)
                    ON DUPLICATE KEY UPDATE endpoint_id = provider_endpoint_bindings.endpoint_id
                    """, cancellationToken, new { BindingId = bindingId, DeviceId = deviceId, EndpointId = endpointId, endpoint.Key }, transaction));
                await connection.ExecuteAsync(Query(
                    """
                    INSERT INTO device_capabilities (endpoint_id, capability_code, readable, writable)
                    VALUES (@EndpointId, @Code, TRUE, FALSE)
                    ON DUPLICATE KEY UPDATE readable = TRUE, writable = FALSE
                    """, cancellationToken, endpoint.Capabilities.Select(capability =>
                        new { EndpointId = endpointId, Code = capability.ToString() }).ToArray(), transaction));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return site.PropertyId;
    }

    public async Task PersistAsync(long propertyId, ObservationBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LoadSiteAsync(connection, transaction, propertyId, cancellationToken);
        var targets = (await connection.QueryAsync<BoundEndpoint>(Query(
            """
            SELECT b.device_id AS DeviceId, b.external_id AS ExternalId, e.external_endpoint_key AS EndpointKey,
                   e.endpoint_id AS EndpointId
            FROM provider_bindings b JOIN provider_endpoint_bindings e ON e.binding_id = b.id
            WHERE b.property_id = @PropertyId AND b.connection_id = @ConnectionId
            """, cancellationToken, site, transaction))).ToLookup(row => row.ExternalId, StringComparer.Ordinal);

        var knownSeries = (await connection.QueryAsync<Series>(Query(
            """
            SELECT id AS Id, endpoint_id AS EndpointId, installation_id AS InstallationId, metric AS Metric
            FROM telemetry_series WHERE property_id = @PropertyId AND source_key = 'simulator'
            """, cancellationToken, site, transaction))).ToDictionary(row => (row.EndpointId, row.InstallationId, row.Metric), row => row.Id);

        var receivedAt = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var observation in batch.Devices)
        {
            var endpoints = targets[observation.ExternalId].ToDictionary(row => row.EndpointKey, StringComparer.Ordinal);
            if (endpoints.Count == 0) throw new InvalidOperationException("The simulator reported an unregistered identity.");
            var target = endpoints.Values.First();
            var status = new
            {
                target.DeviceId, Connectivity = observation.Connectivity.ToString(),
                Observed = batch.ObservedAt.UtcDateTime, observation.FaultCode,
                Contact = observation.Connectivity == Connectivity.Online ? batch.ObservedAt.UtcDateTime : (DateTime?)null
            };

            var previous = await connection.QuerySingleOrDefaultAsync<StateRow>(Query(
                """
                SELECT connectivity AS Connectivity, fault_code AS FaultCode, observed_at_utc AS Observed
                FROM device_state WHERE device_id = @DeviceId FOR UPDATE
                """, cancellationToken, status, transaction));

            if (previous is null || status.Observed > previous.Observed)
            {
                await connection.ExecuteAsync(Query(
                    """
                    INSERT INTO device_state (device_id, connectivity, observed_at_utc, last_contact_utc, fault_code)
                    VALUES (@DeviceId, @Connectivity, @Observed, @Contact, @FaultCode) AS incoming
                    ON DUPLICATE KEY UPDATE connectivity = incoming.connectivity, observed_at_utc = incoming.observed_at_utc,
                        last_contact_utc = COALESCE(incoming.last_contact_utc, device_state.last_contact_utc),
                        fault_code = incoming.fault_code
                    """, cancellationToken, status, transaction));
                var alert = observation.FaultCode is { } fault && previous?.FaultCode != fault ? fault
                    : observation.Connectivity == Connectivity.Offline && previous?.Connectivity == nameof(Connectivity.Online)
                        ? "DEVICE_OFFLINE" : null;
                if (alert is not null)
                    await connection.ExecuteAsync(Query(
                        "INSERT INTO device_alerts (device_id, occurred_at_utc, alert_code) VALUES (@DeviceId, @Observed, @Alert)",
                        cancellationToken, new { target.DeviceId, status.Observed, Alert = alert }, transaction));
            }

            foreach (var endpoint in observation.Endpoints)
            foreach (var sample in endpoint.Samples)
                await SaveAsync(endpoints[endpoint.EndpointKey].EndpointId, null, sample);
        }

        foreach (var sample in batch.Installation)
            await SaveAsync(null, site.InstallationId, sample);
        await transaction.CommitAsync(cancellationToken);

        async Task SaveAsync(long? endpointId, long? installationId, Sample sample)
        {
            var key = (endpointId, installationId, sample.Metric.ToString());
            if (!knownSeries.TryGetValue(key, out var seriesId))
            {
                seriesId = await InsertIdentityAsync(connection, transaction,
                    """
                    INSERT INTO telemetry_series (property_id, endpoint_id, installation_id, source_key, metric)
                    VALUES (@PropertyId, @EndpointId, @InstallationId, 'simulator', @Metric)
                    ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
                    """, new { site.PropertyId, EndpointId = endpointId, InstallationId = installationId, Metric = key.Item3 }, cancellationToken);
                knownSeries.Add(key, seriesId);
            }

            var values = new { SeriesId = seriesId, Observed = sample.ObservedAt.UtcDateTime, Received = receivedAt, sample.Number, sample.Flag, sample.Code };
            await connection.ExecuteAsync(Query(
                """
                INSERT INTO telemetry_samples (series_id, observed_at_utc, received_at_utc, numeric_value, boolean_value, status_value)
                VALUES (@SeriesId, @Observed, @Received, @Number, @Flag, @Code)
                ON DUPLICATE KEY UPDATE series_id = telemetry_samples.series_id
                """, cancellationToken, values, transaction));
            await connection.ExecuteAsync(Query(
                """
                INSERT INTO latest_readings (series_id, observed_at_utc, received_at_utc, numeric_value, boolean_value, status_value)
                VALUES (@SeriesId, @Observed, @Received, @Number, @Flag, @Code) AS incoming
                ON DUPLICATE KEY UPDATE
                    numeric_value = IF(incoming.observed_at_utc > latest_readings.observed_at_utc, incoming.numeric_value, latest_readings.numeric_value),
                    boolean_value = IF(incoming.observed_at_utc > latest_readings.observed_at_utc, incoming.boolean_value, latest_readings.boolean_value),
                    status_value = IF(incoming.observed_at_utc > latest_readings.observed_at_utc, incoming.status_value, latest_readings.status_value),
                    received_at_utc = IF(incoming.observed_at_utc > latest_readings.observed_at_utc, incoming.received_at_utc, latest_readings.received_at_utc),
                    observed_at_utc = GREATEST(incoming.observed_at_utc, latest_readings.observed_at_utc)
                """, cancellationToken, values, transaction));
        }
    }

    private static Task<Site> LoadSiteAsync(MySqlConnection connection, MySqlTransaction transaction,
        long? propertyId, CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<Site>(Query(
            """
            SELECT p.id AS PropertyId, i.id AS InstallationId, c.id AS ConnectionId
            FROM properties p JOIN installations i ON i.property_id = p.id
            JOIN integration_connections c ON c.property_id = p.id
            WHERE p.site_key = 'countryside' AND i.installation_key = 'solar' AND c.provider_code = 'simulator'
              AND (@PropertyId IS NULL OR p.id = @PropertyId)
            """, cancellationToken, new { PropertyId = propertyId }, transaction));

    private static async Task<long> InsertIdentityAsync(MySqlConnection connection, MySqlTransaction transaction,
        string sql, object parameters, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(Query(sql, cancellationToken, parameters, transaction));
        return await connection.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", cancellationToken, transaction: transaction));
    }

    private sealed record Site(long PropertyId, long InstallationId, long ConnectionId);
    private sealed record RoomBinding(long Id, string RoomKey);
    private sealed record BoundEndpoint(long DeviceId, string ExternalId, string EndpointKey, long EndpointId);
    private sealed record StateRow(string Connectivity, string? FaultCode, DateTime Observed);
    private sealed record Series(long Id, long? EndpointId, long? InstallationId, string Metric);
}
