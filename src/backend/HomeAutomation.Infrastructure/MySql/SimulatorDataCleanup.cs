using Dapper;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed record SimulatorCleanupConflict(string Kind, long Id, string Name, string Reason);
public sealed record SimulatorDashboardReference(string WidgetTitle, string SourceKey, string Reason);

/// <summary>
/// What a simulator cleanup would delete in one property. Ownership is decided by provenance only: a device must be bound
/// solely to the property's <c>simulator</c> connection and carry a <c>simulator:</c> inventory key, and a series must have the
/// <c>simulator</c> source key. Anything that also relates to real providers or real logical history is preserved and reported.
/// </summary>
public sealed record SimulatorCleanupPlan(
    long PropertyId, bool PropertyExists, IReadOnlyList<string> AbsentOptionalTables, IReadOnlyList<long> DeviceIds,
    IReadOnlyList<long> SeriesIds, IReadOnlyList<long> LogicalDeviceIds, IReadOnlyList<(string Table, long Rows)> Counts,
    IReadOnlyList<SimulatorCleanupConflict> Conflicts, IReadOnlyList<SimulatorDashboardReference> DashboardReferences)
{
    public bool IsEmpty => DeviceIds.Count == 0 && SeriesIds.Count == 0 && LogicalDeviceIds.Count == 0;
}

public sealed class SimulatorCleanupException(string message) : Exception(message);

/// <summary>
/// Explicit, property-scoped removal of stored simulator data. Planning is read-only. Execution deletes raw samples of the
/// planned series in bounded batches (each its own transaction, safe to repeat), then removes every remaining dependent row
/// and the structural rows in one transaction that holds the property row lock and the rollup lock, after re-planning and
/// checking that nothing changed. Seeded rows (property, rooms, installations, the simulator connection) are never deleted.
/// Dashboard layouts are never modified; their simulator bindings are reported for an explicit edit.
/// </summary>
public sealed class SimulatorDataCleanup(Database database)
{
    internal const int BatchSize = 5000;
    private const string Simulator = TelemetryHoldPolicy.SimulatorSource;
    private static readonly string[] OptionalTables =
    [
        "telemetry_rollups_hourly", "telemetry_rollup_state", "logical_devices", "logical_device_assignments", "logical_device_audit",
        "dashboard_widgets", "dashboard_values"
    ];

    /// <summary>Test seam invoked before the structural transaction commits.</summary>
    internal Func<CancellationToken, Task>? BeforeCommit { get; set; }

    public async Task<SimulatorCleanupPlan> PlanAsync(long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await PlanAsync(db, null, property, ct);
    }

    /// <summary>Executes the plan the caller reviewed. Returns the rows deleted per table.</summary>
    public async Task<IReadOnlyList<(string Table, long Rows)>> ExecuteAsync(SimulatorCleanupPlan reviewed, CancellationToken ct)
    {
        if (!reviewed.PropertyExists) throw new SimulatorCleanupException("The property does not exist.");
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string table, long rows) => deleted[table] = deleted.GetValueOrDefault(table) + rows;

        // Phase 1: raw samples in bounded batches. Each batch commits on its own; a rerun continues where it stopped.
        await using (var db = await database.OpenAsync(ct))
        {
            if (!Matches(await PlanAsync(db, null, reviewed.PropertyId, ct), reviewed))
                throw new SimulatorCleanupException("The simulator data changed since the dry run. Review a new dry run before executing.");
            foreach (var series in reviewed.SeriesIds)
            {
                var owner = await db.QuerySingleOrDefaultAsync<SeriesOwner>(Query(
                    "SELECT property_id AS PropertyId, source_key AS SourceKey FROM telemetry_series WHERE id=@series", ct, new { series }));
                if (owner is null) continue;
                if (owner.PropertyId != reviewed.PropertyId || owner.SourceKey != Simulator)
                    throw new SimulatorCleanupException($"Series {series} is no longer a simulator series of property {reviewed.PropertyId}. Run the dry run again.");
                int rows;
                do
                {
                    rows = await db.ExecuteAsync(Query(
                        "DELETE FROM telemetry_samples WHERE series_id=@series ORDER BY observed_at_utc LIMIT @BatchSize", ct,
                        new { series, BatchSize }));
                    Add("telemetry_samples", rows);
                } while (rows == BatchSize);
            }
        }

        // Phase 2: everything else, atomically, under the property lock and the rollup lock.
        await using (var db = await database.OpenAsync(ct))
        {
            var lockName = await db.ExecuteScalarAsync<string>(Query(
                "SELECT CONCAT(@name, ':', LEFT(SHA2(DATABASE(),256),24))", ct, new { name = TelemetryRollups.LockName }));
            if (await db.ExecuteScalarAsync<int?>(Query("SELECT GET_LOCK(@lockName, 30)", ct, new { lockName })) != 1)
                throw new SimulatorCleanupException("The telemetry rollup worker holds its lock. Retry in a few minutes.");
            try
            {
                await using var tx = await db.BeginTransactionAsync(ct);
                if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE", ct,
                        new { property = reviewed.PropertyId }, tx)) is null)
                    throw new SimulatorCleanupException("The property does not exist.");
                var current = await PlanAsync(db, tx, reviewed.PropertyId, ct);
                if (!Matches(current, reviewed))
                    throw new SimulatorCleanupException("The simulator data changed since the dry run. Review a new dry run before executing.");
                var tables = await TablesAsync(db, tx, ct);
                var args = new { series = current.SeriesIds, devices = current.DeviceIds, logical = current.LogicalDeviceIds,
                    property = current.PropertyId };
                async Task Delete(string table, string sql, bool when = true)
                {
                    if (!when) return;
                    Add(table, await db.ExecuteAsync(Query(sql, ct, args, tx)));
                }
                var series = current.SeriesIds.Count > 0;
                var devices = current.DeviceIds.Count > 0;
                var logical = current.LogicalDeviceIds.Count > 0;
                await Delete("telemetry_rollups_hourly", "DELETE FROM telemetry_rollups_hourly WHERE series_id IN @series",
                    series && tables.Contains("telemetry_rollups_hourly"));
                await Delete("telemetry_rollup_state", "DELETE FROM telemetry_rollup_state WHERE series_id IN @series",
                    series && tables.Contains("telemetry_rollup_state"));
                await Delete("latest_readings", "DELETE FROM latest_readings WHERE series_id IN @series", series);
                await Delete("telemetry_samples", "DELETE FROM telemetry_samples WHERE series_id IN @series", series);
                await Delete("telemetry_series", "DELETE FROM telemetry_series WHERE property_id=@property AND id IN @series", series);
                await Delete("logical_device_audit", "DELETE FROM logical_device_audit WHERE property_id=@property AND logical_device_id IN @logical", logical);
                await Delete("logical_device_assignments",
                    "DELETE FROM logical_device_assignments WHERE property_id=@property AND logical_device_id IN @logical", logical);
                await Delete("logical_devices", "DELETE FROM logical_devices WHERE property_id=@property AND id IN @logical", logical);
                await Delete("device_alerts", "DELETE FROM device_alerts WHERE device_id IN @devices", devices);
                await Delete("device_state", "DELETE FROM device_state WHERE device_id IN @devices", devices);
                await Delete("device_capabilities", """
                    DELETE c FROM device_capabilities c JOIN device_endpoints e ON e.id=c.endpoint_id
                    WHERE e.property_id=@property AND e.device_id IN @devices
                    """, devices);
                await Delete("provider_endpoint_bindings", "DELETE FROM provider_endpoint_bindings WHERE device_id IN @devices", devices);
                await Delete("provider_bindings", "DELETE FROM provider_bindings WHERE property_id=@property AND device_id IN @devices", devices);
                await Delete("installation_devices", "DELETE FROM installation_devices WHERE property_id=@property AND device_id IN @devices", devices);
                await Delete("device_endpoints", "DELETE FROM device_endpoints WHERE property_id=@property AND device_id IN @devices", devices);
                await Delete("devices", "DELETE FROM devices WHERE property_id=@property AND id IN @devices", devices);
                if (BeforeCommit is { } hook) await hook(ct);
                await tx.CommitAsync(ct);
            }
            finally
            {
                await db.ExecuteScalarAsync<int?>(Query("SELECT RELEASE_LOCK(@lockName)", CancellationToken.None, new { lockName }));
            }
        }
        return [.. deleted.Where(item => item.Value > 0).Select(item => (item.Key, item.Value))];
    }

    private static async Task<SimulatorCleanupPlan> PlanAsync(MySqlConnection db, MySqlTransaction? tx, long property, CancellationToken ct)
    {
        if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property", ct, new { property }, tx)) is null)
            return new(property, false, [], [], [], [], [], [], []);
        var tables = await TablesAsync(db, tx, ct);
        var absent = OptionalTables.Where(table => !tables.Contains(table)).ToList();
        var conflicts = new List<SimulatorCleanupConflict>();

        // Devices bound to the simulator connection, with every other binding they have.
        var bound = (await db.QueryAsync<BoundDevice>(Query("""
            SELECT d.id AS Id, d.name AS Name, d.inventory_key AS InventoryKey,
                   (SELECT GROUP_CONCAT(DISTINCT c2.provider_code ORDER BY c2.provider_code) FROM provider_bindings b2
                    JOIN integration_connections c2 ON c2.id=b2.connection_id AND c2.property_id=b2.property_id
                    WHERE b2.device_id=d.id AND c2.provider_code<>@Simulator) AS OtherProviders,
                   (SELECT COUNT(*) FROM telemetry_series s JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
                    WHERE e.device_id=d.id AND s.source_key<>@Simulator) AS OtherSeries
            FROM devices d
            WHERE d.property_id=@property AND EXISTS (
                SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id
                WHERE b.device_id=d.id AND b.property_id=d.property_id AND c.provider_code=@Simulator)
            ORDER BY d.id
            """, ct, new { property, Simulator }, tx))).ToList();
        var simulated = new Dictionary<long, BoundDevice>();
        foreach (var device in bound)
        {
            var reason = device.OtherProviders is { Length: > 0 } others ? $"also bound to {others}"
                : !device.InventoryKey.StartsWith(Simulator + ":", StringComparison.Ordinal) ? "inventory key is not a simulator key"
                : device.OtherSeries > 0 ? $"has {device.OtherSeries} non-simulator telemetry series" : null;
            if (reason is null) simulated[device.Id] = device;
            else conflicts.Add(new("device", device.Id, device.Name, reason));
        }

        // Logical devices touching simulated hardware. Only identities whose every assignment and origin is simulated go.
        var logical = new List<long>();
        if (tables.Contains("logical_devices") && tables.Contains("logical_device_assignments") && simulated.Count > 0)
        {
            var rows = (await db.QueryAsync<LogicalRow>(Query("""
                SELECT l.id AS Id, l.name AS Name, l.origin_device_id AS OriginDeviceId, a.device_id AS AssignedDeviceId
                FROM logical_devices l LEFT JOIN logical_device_assignments a ON a.property_id=l.property_id AND a.logical_device_id=l.id
                WHERE l.property_id=@property AND (l.origin_device_id IN @ids OR EXISTS (
                    SELECT 1 FROM logical_device_assignments x WHERE x.property_id=l.property_id AND x.logical_device_id=l.id AND x.device_id IN @ids))
                ORDER BY l.id
                """, ct, new { property, ids = simulated.Keys.ToArray() }, tx))).GroupBy(row => row.Id).ToList();
            var keep = new HashSet<long>();
            foreach (var identity in rows)
            {
                var first = identity.First();
                var hardware = identity.Select(row => row.AssignedDeviceId).OfType<long>().Append(first.OriginDeviceId ?? 0).Where(id => id != 0)
                    .Distinct().ToList();
                if (hardware.All(simulated.ContainsKey)) logical.Add(identity.Key);
                else
                {
                    conflicts.Add(new("logicalDevice", identity.Key, first.Name, "also has non-simulator hardware; its simulated hardware is kept for its history"));
                    foreach (var id in hardware.Where(simulated.ContainsKey)) keep.Add(id);
                }
            }
            foreach (var id in keep)
            {
                conflicts.Add(new("device", id, simulated[id].Name, "referenced by logical history that also has real hardware"));
                simulated.Remove(id);
            }
        }

        // Simulator series: those of deletable devices and those of installations. Others are reported and kept.
        var series = (await db.QueryAsync<SeriesRow>(Query("""
            SELECT s.id AS Id, s.metric AS Metric, e.device_id AS DeviceId, s.installation_id AS InstallationId
            FROM telemetry_series s LEFT JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
            WHERE s.property_id=@property AND s.source_key=@Simulator ORDER BY s.id
            """, ct, new { property, Simulator }, tx))).ToList();
        var seriesIds = new List<long>();
        foreach (var row in series)
        {
            if (row.DeviceId is null && row.InstallationId is not null || row.DeviceId is { } device && simulated.ContainsKey(device))
                seriesIds.Add(row.Id);
            else conflicts.Add(new("series", row.Id, row.Metric, "simulator series of hardware that is kept"));
        }

        var deviceIds = simulated.Keys.Order().ToList();
        var counts = new List<(string, long)>();
        async Task Count(string table, string sql, object args, bool when = true)
        {
            if (when) counts.Add((table, await db.ExecuteScalarAsync<long>(Query(sql, ct, args, tx))));
        }
        var s = new { series = seriesIds, devices = deviceIds, logical, property };
        var anySeries = seriesIds.Count > 0;
        var anyDevice = deviceIds.Count > 0;
        var anyLogical = logical.Count > 0;
        await Count("telemetry_samples", "SELECT COUNT(*) FROM telemetry_samples WHERE series_id IN @series", s, anySeries);
        await Count("latest_readings", "SELECT COUNT(*) FROM latest_readings WHERE series_id IN @series", s, anySeries);
        await Count("telemetry_rollups_hourly", "SELECT COUNT(*) FROM telemetry_rollups_hourly WHERE series_id IN @series", s,
            anySeries && tables.Contains("telemetry_rollups_hourly"));
        await Count("telemetry_rollup_state", "SELECT COUNT(*) FROM telemetry_rollup_state WHERE series_id IN @series", s,
            anySeries && tables.Contains("telemetry_rollup_state"));
        counts.Add(("telemetry_series", seriesIds.Count));
        await Count("logical_device_audit", "SELECT COUNT(*) FROM logical_device_audit WHERE property_id=@property AND logical_device_id IN @logical", s,
            anyLogical && tables.Contains("logical_device_audit"));
        await Count("logical_device_assignments",
            "SELECT COUNT(*) FROM logical_device_assignments WHERE property_id=@property AND logical_device_id IN @logical", s, anyLogical);
        counts.Add(("logical_devices", logical.Count));
        await Count("device_alerts", "SELECT COUNT(*) FROM device_alerts WHERE device_id IN @devices", s, anyDevice);
        await Count("device_state", "SELECT COUNT(*) FROM device_state WHERE device_id IN @devices", s, anyDevice);
        await Count("device_capabilities", """
            SELECT COUNT(*) FROM device_capabilities c JOIN device_endpoints e ON e.id=c.endpoint_id WHERE e.property_id=@property AND e.device_id IN @devices
            """, s, anyDevice);
        await Count("provider_endpoint_bindings", "SELECT COUNT(*) FROM provider_endpoint_bindings WHERE device_id IN @devices", s, anyDevice);
        await Count("provider_bindings", "SELECT COUNT(*) FROM provider_bindings WHERE property_id=@property AND device_id IN @devices", s, anyDevice);
        await Count("installation_devices", "SELECT COUNT(*) FROM installation_devices WHERE property_id=@property AND device_id IN @devices", s, anyDevice);
        await Count("device_endpoints", "SELECT COUNT(*) FROM device_endpoints WHERE property_id=@property AND device_id IN @devices", s, anyDevice);
        counts.Add(("devices", deviceIds.Count));

        var references = new List<SimulatorDashboardReference>();
        if (tables.Contains("dashboard_values") && tables.Contains("dashboard_widgets"))
        {
            var prefixes = logical.Select(id => $"logical:{id}:").ToList();
            var rows = await db.QueryAsync<(string Title, string Value, string? Navigation)>(Query("""
                SELECT w.title, v.source_key, w.navigation_source FROM dashboard_values v
                JOIN dashboard_widgets w ON w.property_id=v.property_id AND w.id=v.widget_id
                WHERE v.property_id=@property AND (v.source_key LIKE 'simulator:%' OR v.source_key LIKE 'logical:%')
                ORDER BY w.title, v.position
                """, ct, new { property }, tx));
            foreach (var row in rows)
            {
                var reason = row.Value.StartsWith(Simulator + ":", StringComparison.Ordinal) ? "simulator source"
                    : prefixes.Any(prefix => row.Value.StartsWith(prefix, StringComparison.Ordinal)) ? "logical device removed by this cleanup" : null;
                if (reason is not null) references.Add(new(row.Title, row.Value, reason));
            }
        }
        return new(property, true, absent, deviceIds, seriesIds, logical.Order().ToList(), counts, conflicts, references);
    }

    /// <summary>The same devices and identities, and no series beyond those reviewed (series already emptied may have gone).</summary>
    private static bool Matches(SimulatorCleanupPlan current, SimulatorCleanupPlan reviewed) =>
        current.DeviceIds.SequenceEqual(reviewed.DeviceIds) && current.LogicalDeviceIds.SequenceEqual(reviewed.LogicalDeviceIds) &&
        !current.SeriesIds.Except(reviewed.SeriesIds).Any();

    private static async Task<HashSet<string>> TablesAsync(MySqlConnection db, MySqlTransaction? tx, CancellationToken ct) =>
        (await db.QueryAsync<string>(Query("""
            SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN @OptionalTables
            """, ct, new { OptionalTables }, tx))).ToHashSet(StringComparer.Ordinal);

    private sealed record SeriesOwner(long PropertyId, string SourceKey);
    private sealed record BoundDevice(long Id, string Name, string InventoryKey, string? OtherProviders, long OtherSeries);
    private sealed record LogicalRow(long Id, string Name, long? OriginDeviceId, long? AssignedDeviceId);
    private sealed record SeriesRow(long Id, string Metric, long? DeviceId, long? InstallationId);
}
