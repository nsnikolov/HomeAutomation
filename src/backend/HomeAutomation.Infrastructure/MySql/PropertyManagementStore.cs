using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Configuration;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Properties, rooms and device ownership.
/// <list type="bullet">
/// <item>A property is soft-deleted: it is hidden from every membership check, and its rooms, historical devices, telemetry and
/// vault profiles are kept. Only current devices block deletion.</item>
/// <item>A device row belongs to one property, together with its endpoints and telemetry series. A transfer therefore retires the
/// source row, which keeps its history, and opens a row in the destination; no telemetry is rewritten.</item>
/// <item>Mutations lock the affected property rows. Foreign-key checks on device and provider rows take shared locks on the same
/// rows, so a concurrent assignment and a deletion are serialized.</item>
/// </list>
/// </summary>
public sealed class PropertyManagementStore(Database database, ConfigurationVaultOptions vault, TimeProvider clock)
    : IPropertyManagement, IDeviceAssignmentAudit
{
    internal const string DefaultTimeZone = "Europe/Sofia";
    /// <summary>Providers whose device-to-property assignment lives in the provider's own table and can therefore move.</summary>
    internal static readonly IReadOnlyDictionary<string, string> TransferableProviders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["daikin"] = "daikin_devices", ["ewelink"] = "ewelink_devices", ["tuya"] = "tuya_devices"
    };
    private static readonly string[] OptionalTables =
    [
        "daikin_devices", "ewelink_devices", "tuya_devices", "daikin_commands", "ewelink_relay_commands", "logical_devices",
        "logical_device_assignments", "logical_device_audit", "configuration_profiles", "dashboard_groups"
    ];

    /// <summary>Test seam invoked before a transfer or deletion commits.</summary>
    internal Func<CancellationToken, Task>? BeforeCommit { get; set; }

    // ---------------------------------------------------------------- properties

    public async Task<PropertyManagementView> ListAsync(string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var tables = await TablesAsync(db, null, ct);
        var rows = (await db.QueryAsync<PropertyRow>(Query("""
            SELECT p.id AS Id, p.name AS Name, p.description AS Description, p.time_zone AS TimeZone, m.role_code AS Role
            FROM properties p JOIN property_memberships m ON m.property_id=p.id AND m.user_id=@user
            WHERE p.deleted_at_utc IS NULL ORDER BY p.name, p.id
            """, ct, new { user }))).ToList();
        var list = new List<PropertyOverview>();
        foreach (var row in rows)
        {
            var counts = await CountsAsync(db, null, row.Id, tables, ct);
            bool? growatt = tables.Contains("configuration_profiles")
                ? await db.ExecuteScalarAsync<bool>(Query(
                    "SELECT EXISTS(SELECT 1 FROM configuration_profiles WHERE scope_id=@Id AND provider='growatt')", ct, new { row.Id }))
                : null;
            list.Add(new(row.Id, row.Name, row.Description, row.TimeZone, row.Role, CanManage(row.Role), IsOwner(row.Role),
                counts.Rooms, counts.Devices, counts.Installations, growatt));
        }
        return new(vault.IsAdministrator(user), list);
    }

    /// <summary>
    /// Creates an empty property with its creator as Owner. No room, installation, device, logical device or vault profile is
    /// created. A repeated request id returns the property that request already created.
    /// </summary>
    public async Task<ManagementResult> CreateAsync(string user, CreatePropertyRequest request, CancellationToken ct)
    {
        if (!vault.IsAdministrator(user))
            return Fail(ManagementStatus.Forbidden, "Only the installation administrator can create properties.");
        var name = Clean(request.Name);
        if (InvalidName(name)) return Invalid("Enter a property name of 1 to 128 characters.");
        var description = Clean(request.Description);
        if (description is { Length: > 256 } || description?.Any(char.IsControl) == true)
            return Invalid("Keep the description to 256 characters.");
        var timeZone = Clean(request.TimeZone) ?? DefaultTimeZone;
        if (!ValidTimeZone(timeZone)) return Invalid("Choose an IANA time zone such as Europe/Sofia.");
        var requestId = request.RequestId?.ToString("D");
        await using var db = await database.OpenAsync(ct);
        if (requestId is not null && await ByRequestAsync(db, requestId, user, ct) is { } existing) return existing;
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var id = await db.ExecuteScalarAsync<long>(Query("""
                INSERT INTO properties(site_key,name,time_zone,description,creation_request_id) VALUES(@key,@name,@timeZone,@description,@requestId);
                SELECT LAST_INSERT_ID();
                """, ct, new { key = SiteKey(name!), name, timeZone, description, requestId }, tx));
            await db.ExecuteAsync(Query("INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@id,@user,'Owner')",
                ct, new { id, user }, tx));
            await AuditAsync(db, tx, id, user, "propertyCreated", detail: name, ct: ct);
            await tx.CommitAsync(ct);
            return ManagementResult.Done(id);
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry && requestId is not null)
        {
            return await ByRequestAsync(db, requestId, user, ct) ?? Fail(ManagementStatus.Conflict, "The property could not be created. Try again.");
        }
    }

    public async Task<ManagementResult> UpdateAsync(string user, long property, UpdatePropertyRequest request, CancellationToken ct)
    {
        var name = Clean(request.Name);
        if (InvalidName(name)) return Invalid("Enter a property name of 1 to 128 characters.");
        var description = Clean(request.Description);
        if (description is { Length: > 256 } || description?.Any(char.IsControl) == true) return Invalid("Keep the description to 256 characters.");
        var timeZone = Clean(request.TimeZone);
        if (timeZone is null || !ValidTimeZone(timeZone)) return Invalid("Choose an IANA time zone such as Europe/Sofia.");
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        await db.ExecuteAsync(Query("UPDATE properties SET name=@name, description=@description, time_zone=@timeZone WHERE id=@property",
            ct, new { property, name, description, timeZone }, tx));
        await AuditAsync(db, tx, property, user, "propertyUpdated", detail: name, ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(property);
    }

    public async Task<PropertyDeletionImpact?> DeletionImpactAsync(string user, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var role = await RoleAsync(db, null, property, user, ct);
        if (role is null) return null;
        return await ImpactAsync(db, null, property, role, await TablesAsync(db, null, ct), ct);
    }

    /// <summary>
    /// Deletes a property without current devices. The property is marked deleted and disappears from every membership check.
    /// Rooms, devices that moved away, their telemetry, dashboards and vault profiles stay in the database untouched.
    /// </summary>
    public async Task<ManagementResult> DeleteAsync(string user, long property, DeletePropertyRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var name = await LockAsync(db, tx, property, ct);
        if (name is null) return NotFound();
        var role = await RoleAsync(db, tx, property, user, ct);
        if (role is null) return NotFound();
        if (!IsOwner(role)) return Fail(ManagementStatus.Forbidden, "Only a property Owner can delete the property.");
        if (!string.Equals(request.ConfirmName, name, StringComparison.Ordinal))
            return Invalid("Type the exact property name to confirm.");
        var impact = await ImpactAsync(db, tx, property, role, await TablesAsync(db, tx, ct), ct);
        if (impact.Devices > 0)
            return Fail(ManagementStatus.Conflict,
                $"The property still has {impact.Devices} device{(impact.Devices == 1 ? "" : "s")}. Move them to another property first.");
        await db.ExecuteAsync(Query("UPDATE properties SET deleted_at_utc=@now WHERE id=@property AND deleted_at_utc IS NULL",
            ct, new { property, now = Now() }, tx));
        await AuditAsync(db, tx, property, user, "propertyDeleted", detail:
            $"{impact.Rooms} rooms, {impact.Installations} installations, {impact.HistoricalDevices} historical devices and {impact.VaultProfiles} vault profiles kept", ct: ct);
        if (BeforeCommit is { } hook) await hook(ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(property);
    }

    // ---------------------------------------------------------------- rooms

    public async Task<IReadOnlyList<RoomView>?> RoomsAsync(string user, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await RoleAsync(db, null, property, user, ct) is null) return null;
        return (await db.QueryAsync<RoomView>(Query("""
            SELECT r.id AS Id, r.name AS Name, r.description AS Description,
                   CAST((SELECT COUNT(*) FROM devices d WHERE d.property_id=r.property_id AND d.room_id=r.id AND d.retired_at_utc IS NULL) AS SIGNED) AS Devices
            FROM rooms r WHERE r.property_id=@property ORDER BY r.name, r.id
            """, ct, new { property }))).ToList();
    }

    public async Task<ManagementResult> CreateRoomAsync(string user, long property, RoomRequest request, CancellationToken ct)
    {
        var (name, description, invalid) = Room(request);
        if (invalid is not null) return invalid;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        if (await RoomNameTakenAsync(db, tx, property, name!, null, ct)) return Fail(ManagementStatus.Conflict, $"A room named \"{name}\" already exists.");
        var id = await db.ExecuteScalarAsync<long>(Query("""
            INSERT INTO rooms(property_id,room_key,name,description) VALUES(@property,@key,@name,@description); SELECT LAST_INSERT_ID();
            """, ct, new { property, key = SiteKey(name!), name, description }, tx));
        await AuditAsync(db, tx, property, user, "roomCreated", room: id, detail: name, ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(id);
    }

    public async Task<ManagementResult> UpdateRoomAsync(string user, long property, long room, RoomRequest request, CancellationToken ct)
    {
        var (name, description, invalid) = Room(request);
        if (invalid is not null) return invalid;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        if (await RoomNameTakenAsync(db, tx, property, name!, room, ct)) return Fail(ManagementStatus.Conflict, $"A room named \"{name}\" already exists.");
        if (await db.ExecuteAsync(Query("UPDATE rooms SET name=@name, description=@description WHERE property_id=@property AND id=@room",
                ct, new { property, room, name, description }, tx)) == 0 &&
            !await db.ExecuteScalarAsync<bool>(Query("SELECT EXISTS(SELECT 1 FROM rooms WHERE property_id=@property AND id=@room)", ct,
                new { property, room }, tx)))
            return Fail(ManagementStatus.NotFound, "The room does not exist.");
        await AuditAsync(db, tx, property, user, "roomUpdated", room: room, detail: name, ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(room);
    }

    /// <summary>
    /// Deletes a room after clearing every reference to it: device, logical device and provider assignments. Devices stay in the
    /// property with their bindings, state, telemetry, alerts and audit.
    /// </summary>
    public async Task<ManagementResult> DeleteRoomAsync(string user, long property, long room, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        var name = await db.QuerySingleOrDefaultAsync<string>(Query("SELECT name FROM rooms WHERE property_id=@property AND id=@room FOR UPDATE",
            ct, new { property, room }, tx));
        if (name is null) return Fail(ManagementStatus.NotFound, "The room does not exist.");
        var cleared = 0;
        foreach (var table in RoomTables(await TablesAsync(db, tx, ct)))
            cleared += await db.ExecuteAsync(Query($"UPDATE {table} SET room_id=NULL WHERE property_id=@property AND room_id=@room",
                ct, new { property, room }, tx));
        try
        {
            await db.ExecuteAsync(Query("DELETE FROM rooms WHERE property_id=@property AND id=@room", ct, new { property, room }, tx));
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.TableAccessDenied)
        {
            // The transaction is disposed without commit, so the cleared assignments are rolled back as well.
            return Fail(ManagementStatus.SchemaPending,
                "Room deletion needs the DELETE grant on the rooms table for the runtime database account. Nothing was changed.");
        }
        await AuditAsync(db, tx, property, user, "roomDeleted", room: room, detail: $"{name}; {cleared} assignments cleared", ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(room);
    }

    // ---------------------------------------------------------------- devices

    public async Task<ManagementResult> RenameDeviceAsync(string user, long property, long device, DeviceNameRequest request, CancellationToken ct)
    {
        var name = Clean(request.DisplayName);
        if (name is { Length: > 128 } || name?.Any(char.IsControl) == true) return Invalid("Keep the name to 128 characters.");
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        var current = await DeviceAsync(db, tx, property, device, ct);
        if (current is null) return Fail(ManagementStatus.NotFound, "The device is not in this property.");
        await db.ExecuteAsync(Query("UPDATE devices SET display_name=@name WHERE property_id=@property AND id=@device",
            ct, new { property, device, name }, tx));
        await AuditAsync(db, tx, property, user, "deviceRenamed", device: device,
            detail: Truncate($"{current.DisplayName ?? current.Name} → {name ?? current.Name}"), ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(device);
    }

    /// <summary>Sets or clears a device's room in the device row and in the provider assignment it is synchronized from.</summary>
    public async Task<ManagementResult> SetDeviceRoomAsync(string user, long property, long device, DeviceRoomRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockAsync(db, tx, property, ct) is null) return NotFound();
        if (!CanManage(await RoleAsync(db, tx, property, user, ct))) return Forbidden();
        var current = await DeviceAsync(db, tx, property, device, ct);
        if (current is null) return Fail(ManagementStatus.NotFound, "The device is not in this property.");
        string? roomName = null;
        if (request.RoomId is { } room && (roomName = await db.QuerySingleOrDefaultAsync<string>(Query(
                "SELECT name FROM rooms WHERE property_id=@property AND id=@room", ct, new { property, room }, tx))) is null)
            return Invalid("Choose a room of this property.");
        await db.ExecuteAsync(Query("UPDATE devices SET room_id=@room WHERE property_id=@property AND id=@device",
            ct, new { property, device, room = request.RoomId }, tx));
        var tables = await TablesAsync(db, tx, ct);
        if (await BindingAsync(db, tx, property, device, ct) is { } binding &&
            TransferableProviders.TryGetValue(binding.Provider, out var table) && tables.Contains(table))
            await db.ExecuteAsync(Query($"UPDATE {table} SET room_id=@room WHERE property_id=@property AND external_id=@ExternalId",
                ct, new { property, room = request.RoomId, binding.ExternalId }, tx));
        await AuditAsync(db, tx, property, user, "deviceRoomChanged", device: device, room: request.RoomId,
            detail: roomName ?? "No room", ct: ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(device);
    }

    public async Task<IReadOnlyList<DeviceAuditEntry>?> DeviceAuditAsync(string user, long property, long device, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await RoleAsync(db, null, property, user, ct) is null) return null;
        if (!await db.ExecuteScalarAsync<bool>(Query("SELECT EXISTS(SELECT 1 FROM devices WHERE property_id=@property AND id=@device)",
                ct, new { property, device })))
            return null;
        var rows = await db.QueryAsync<AuditRow>(Query("""
            SELECT a.id AS Id, a.occurred_at_utc AS OccurredAt, a.actor AS Actor, a.action AS Action,
                   a.other_property_id AS OtherPropertyId, o.name AS OtherPropertyName, a.detail AS Detail
            FROM management_audit a LEFT JOIN properties o ON o.id=a.other_property_id
            WHERE a.property_id=@property AND a.device_id=@device ORDER BY a.occurred_at_utc DESC, a.id DESC LIMIT 100
            """, ct, new { property, device }));
        return rows.Select(row => new DeviceAuditEntry(row.Id, Utc(row.OccurredAt), row.Actor, row.Action, row.OtherPropertyId,
            row.OtherPropertyName, row.Detail)).ToList();
    }

    public async Task<IReadOnlyList<TransferableDevice>> TransferableAsync(string user, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (!CanManage(await RoleAsync(db, null, property, user, ct))) return [];
        return (await db.QueryAsync<TransferableDevice>(Query("""
            SELECT d.id AS DeviceId, p.id AS PropertyId, p.name AS PropertyName, COALESCE(d.display_name, d.name) AS Name,
                   d.name AS ProviderName, c.provider_code AS Provider, d.model AS Model, r.name AS RoomName
            FROM devices d
            JOIN properties p ON p.id=d.property_id AND p.deleted_at_utc IS NULL
            JOIN property_memberships m ON m.property_id=p.id AND m.user_id=@user AND UPPER(m.role_code) IN ('OWNER','ADMIN')
            JOIN provider_bindings b ON b.device_id=d.id AND b.property_id=d.property_id
            JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id AND c.provider_code IN ('daikin','ewelink','tuya')
            LEFT JOIN rooms r ON r.property_id=d.property_id AND r.id=d.room_id
            WHERE d.property_id<>@property AND d.retired_at_utc IS NULL
            ORDER BY p.name, Name, d.id
            """, ct, new { user, property }))).ToList();
    }

    public async Task<DeviceTransferImpact?> TransferImpactAsync(string user, long property, long device, long destination, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var plan = await PlanTransferAsync(db, null, user, property, device, destination, ct);
        return plan.Impact;
    }

    /// <summary>
    /// Moves a provider device to another property in one transaction. The steps are:
    /// <list type="number">
    /// <item>The provider assignment moves, the source row is retired and its open logical assignments close at the transfer
    /// instant.</item>
    /// <item>A destination row (or the row it had before) is opened with the same inventory key, custom name, endpoints, capabilities
    /// and a binding to the destination's connection to the same global provider account.</item>
    /// <item>Room assignments are cleared.</item>
    /// </list>
    /// Readings before the transfer stay attributed to the source property and new readings accrue to the destination.
    /// </summary>
    public async Task<ManagementResult> TransferAsync(string user, long property, long device, DeviceTransferRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var destination = request.DestinationPropertyId;
        foreach (var id in new[] { property, destination }.Distinct().Order())
            await LockAsync(db, tx, id, ct);
        var plan = await PlanTransferAsync(db, tx, user, property, device, destination, ct, forUpdate: true);
        if (plan.Failure is { } failure) return failure;
        var impact = plan.Impact!;
        var binding = plan.Binding!;
        var source = plan.Device!;
        var now = Now();
        var tables = await TablesAsync(db, tx, ct);
        var table = TransferableProviders[binding.Provider];
        var relayReset = binding.Provider == "ewelink" ? ", relay_write_enabled=NULL" : "";
        if (await db.ExecuteAsync(Query($"""
                UPDATE {table} SET property_id=@destination, room_id=NULL{relayReset}
                WHERE external_id=@ExternalId AND property_id=@property
                """, ct, new { destination, property, binding.ExternalId }, tx)) != 1)
            return Fail(ManagementStatus.Conflict, "The provider record of this device changed. Reload and try again.");
        if (await db.ExecuteAsync(Query("""
                UPDATE devices SET retired_at_utc=@now, room_id=NULL WHERE property_id=@property AND id=@device AND retired_at_utc IS NULL
                """, ct, new { property, device, now }, tx)) != 1)
            return Fail(ManagementStatus.Conflict, "The device was moved at the same time. Reload and try again.");

        if (tables.Contains("logical_device_assignments"))
        {
            var open = (await db.QueryAsync<(long Id, long Logical, string Channel)>(Query("""
                SELECT id, logical_device_id, channel_key FROM logical_device_assignments
                WHERE property_id=@property AND device_id=@device AND valid_to_utc IS NULL AND valid_from_utc<@now FOR UPDATE
                """, ct, new { property, device, now }, tx))).ToList();
            foreach (var (id, logical, channel) in open)
            {
                await db.ExecuteAsync(Query("UPDATE logical_device_assignments SET valid_to_utc=@now WHERE id=@id", ct, new { id, now }, tx));
                if (tables.Contains("logical_device_audit"))
                    await db.ExecuteAsync(Query("""
                        INSERT INTO logical_device_audit(property_id,logical_device_id,occurred_at_utc,actor,action,channel_key,old_device_id,effective_at_utc,reason)
                        VALUES(@property,@logical,@now,@user,'unassigned',@channel,@device,@now,@reason)
                        """, ct, new { property, logical, now, user, channel, device, reason = Truncate($"Device moved to {impact.DestinationPropertyName}") }, tx));
            }
        }

        await db.ExecuteAsync(Query("""
            INSERT INTO integration_connections(property_id,provider_code) VALUES(@destination,@Provider)
            ON DUPLICATE KEY UPDATE id=integration_connections.id
            """, ct, new { destination, binding.Provider }, tx));
        var connection = await db.ExecuteScalarAsync<long>(Query(
            "SELECT id FROM integration_connections WHERE property_id=@destination AND provider_code=@Provider", ct, new { destination, binding.Provider }, tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO devices(property_id,room_id,inventory_key,name,kind,manufacturer,model,display_name)
            VALUES(@destination,NULL,@InventoryKey,@Name,@Kind,@Manufacturer,@Model,@DisplayName) AS incoming
            ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(devices.id), retired_at_utc=NULL, room_id=NULL, display_name=incoming.display_name
            """, ct, new { destination, source.InventoryKey, source.Name, source.Kind, source.Manufacturer, source.Model, source.DisplayName }, tx));
        var target = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO provider_bindings(property_id,connection_id,device_id,external_id) VALUES(@destination,@connection,@target,@ExternalId)
            ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(provider_bindings.id)
            """, ct, new { destination, connection, target, binding.ExternalId }, tx));
        var targetBinding = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
        if (await db.ExecuteScalarAsync<long>(Query("SELECT device_id FROM provider_bindings WHERE id=@targetBinding", ct, new { targetBinding }, tx)) != target)
            return Fail(ManagementStatus.Conflict, "The destination already binds this provider device to another device record.");
        var endpoints = await db.QueryAsync<(long Id, string Key, string Name)>(Query(
            "SELECT id, endpoint_key, name FROM device_endpoints WHERE property_id=@property AND device_id=@device ORDER BY id", ct, new { property, device }, tx));
        foreach (var (id, key, name) in endpoints)
        {
            await db.ExecuteAsync(Query("""
                INSERT INTO device_endpoints(property_id,device_id,endpoint_key,name) VALUES(@destination,@target,@key,@name)
                ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)
                """, ct, new { destination, target, key, name }, tx));
            var endpoint = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
            await db.ExecuteAsync(Query("""
                INSERT INTO device_capabilities(endpoint_id,capability_code,readable,writable)
                SELECT @endpoint, src.capability_code, src.readable, src.writable FROM device_capabilities src WHERE src.endpoint_id=@id
                ON DUPLICATE KEY UPDATE readable=src.readable, writable=src.writable
                """, ct, new { endpoint, id }, tx));
            await db.ExecuteAsync(Query("""
                INSERT INTO provider_endpoint_bindings(binding_id,device_id,endpoint_id,external_endpoint_key)
                SELECT @targetBinding, @target, @endpoint, src.external_endpoint_key FROM provider_endpoint_bindings src
                WHERE src.binding_id=@BindingId AND src.endpoint_id=@id
                ON DUPLICATE KEY UPDATE endpoint_id=provider_endpoint_bindings.endpoint_id
                """, ct, new { targetBinding, target, endpoint, binding.BindingId, id }, tx));
        }
        await AuditAsync(db, tx, property, user, "deviceTransferredOut", device: device, otherProperty: destination, otherDevice: target,
            detail: Truncate($"{source.DisplayName ?? source.Name} moved to {impact.DestinationPropertyName}"), ct: ct);
        await AuditAsync(db, tx, destination, user, "deviceTransferredIn", device: target, otherProperty: property, otherDevice: device,
            detail: Truncate($"{source.DisplayName ?? source.Name} moved from {impact.SourcePropertyName}"), ct: ct);
        if (BeforeCommit is { } hook) await hook(ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(target);
    }

    // ---------------------------------------------------------------- provider-page assignment audit

    public async Task<ProviderAssignment?> CurrentAsync(string provider, string publicId, CancellationToken ct)
    {
        if (!TransferableProviders.TryGetValue(provider, out var table)) return null;
        await using var db = await database.OpenAsync(ct);
        if (!(await TablesAsync(db, null, ct)).Contains(table)) return null;
        return await db.QuerySingleOrDefaultAsync<ProviderAssignment>(Query(
            $"SELECT property_id AS PropertyId, room_id AS RoomId, external_id AS ExternalId FROM {table} WHERE public_id=@publicId",
            ct, new { publicId }));
    }

    /// <summary>
    /// Audits an assignment made on a provider page: a first assignment to the property, or a changed room. The provider service has
    /// already committed it and synchronized the device row, which is found through its binding.
    /// </summary>
    public async Task RecordAsync(string user, string provider, ProviderAssignment? before, string publicId, CancellationToken ct)
    {
        var after = await CurrentAsync(provider, publicId, ct);
        if (after?.PropertyId is not { } property) return;
        var assigned = before?.PropertyId != property;
        if (!assigned && before?.RoomId == after.RoomId) return;
        await using var db = await database.OpenAsync(ct);
        var device = await db.QuerySingleOrDefaultAsync<long?>(Query("""
            SELECT d.id FROM provider_bindings b
            JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id AND c.provider_code=@provider
            JOIN devices d ON d.id=b.device_id AND d.property_id=b.property_id AND d.retired_at_utc IS NULL
            WHERE b.property_id=@property AND b.external_id=@ExternalId
            """, ct, new { provider, property, after.ExternalId }));
        var room = after.RoomId is { } roomId ? await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT name FROM rooms WHERE property_id=@property AND id=@roomId", ct, new { property, roomId })) : null;
        await using var tx = await db.BeginTransactionAsync(ct);
        await AuditAsync(db, tx, property, user, assigned ? "deviceAssigned" : "deviceRoomChanged", device: device, room: after.RoomId,
            detail: Truncate(assigned ? $"Assigned from {provider}{(room is null ? "" : $" to {room}")}" : room ?? "No room"), ct: ct);
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- one-time room assignment clearing

    public async Task<IReadOnlyList<(long Property, string Table, long Rows)>> PlanRoomAssignmentClearingAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await RoomAssignmentsAsync(db, null, ct);
    }

    /// <summary>
    /// Clears every current device, logical device and provider room assignment in one transaction. Rooms, devices, bindings,
    /// telemetry and history are untouched. Rerunning finds nothing.
    /// </summary>
    public async Task<IReadOnlyList<(long Property, string Table, long Rows)>> ClearRoomAssignmentsAsync(string actor, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var planned = await RoomAssignmentsAsync(db, tx, ct);
        foreach (var property in planned.Select(item => item.Property).Distinct().Order())
            await LockAsync(db, tx, property, ct, includeDeleted: true);
        var cleared = new List<(long, string, long)>();
        foreach (var (property, table, _) in planned)
            cleared.Add((property, table, await db.ExecuteAsync(Query($"UPDATE {table} SET room_id=NULL WHERE property_id=@property AND room_id IS NOT NULL",
                ct, new { property }, tx))));
        foreach (var group in cleared.GroupBy(item => item.Item1))
            await AuditAsync(db, tx, group.Key, actor, "roomAssignmentsCleared",
                detail: Truncate(string.Join(", ", group.Select(item => $"{item.Item2}={item.Item3}"))), ct: ct);
        if (BeforeCommit is { } hook) await hook(ct);
        await tx.CommitAsync(ct);
        return cleared;
    }

    private static async Task<List<(long Property, string Table, long Rows)>> RoomAssignmentsAsync(MySqlConnection db, MySqlTransaction? tx,
        CancellationToken ct)
    {
        var result = new List<(long, string, long)>();
        foreach (var table in RoomTables(await TablesAsync(db, tx, ct)))
            result.AddRange(await db.QueryAsync<(long, string, long)>(Query($"""
                SELECT property_id, '{table}', COUNT(*) FROM {table} WHERE property_id IS NOT NULL AND room_id IS NOT NULL
                GROUP BY property_id ORDER BY property_id
                """, ct, transaction: tx)));
        return result.OrderBy(item => item.Item1).ThenBy(item => item.Item2, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TransferPlan> PlanTransferAsync(MySqlConnection db, MySqlTransaction? tx, string user, long property, long device,
        long destination, CancellationToken ct, bool forUpdate = false)
    {
        if (destination == property) return new(Fail(ManagementStatus.Invalid, "Choose a different destination property."));
        var sourceName = await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT name FROM properties WHERE id=@property AND deleted_at_utc IS NULL", ct, new { property }, tx));
        var destinationName = await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT name FROM properties WHERE id=@destination AND deleted_at_utc IS NULL", ct, new { destination }, tx));
        var sourceRole = await RoleAsync(db, tx, property, user, ct);
        var destinationRole = await RoleAsync(db, tx, destination, user, ct);
        if (sourceName is null || sourceRole is null) return new(NotFound());
        if (destinationName is null || destinationRole is null)
            return new(Fail(ManagementStatus.NotFound, "The destination property does not exist or is not available to you."));
        if (!CanManage(sourceRole) || !CanManage(destinationRole))
            return new(Fail(ManagementStatus.Forbidden, "Moving a device needs the Owner or Admin role in both properties."));
        var current = await DeviceAsync(db, tx, property, device, ct, forUpdate);
        if (current is null) return new(Fail(ManagementStatus.NotFound, "The device is not in this property."));
        var binding = await BindingAsync(db, tx, property, device, ct);
        var tables = await TablesAsync(db, tx, ct);
        var room = current.RoomId is { } roomId ? await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT name FROM rooms WHERE property_id=@property AND id=@roomId", ct, new { property, roomId }, tx)) : null;
        var open = tables.Contains("logical_device_assignments") ? await db.ExecuteScalarAsync<int>(Query("""
            SELECT COUNT(*) FROM logical_device_assignments WHERE property_id=@property AND device_id=@device AND valid_to_utc IS NULL
            """, ct, new { property, device }, tx)) : 0;
        string? blocked = binding is null ? "This device has no provider connection that can move."
            : binding.Provider == "growatt" ? "Growatt devices follow the property's Growatt site configuration. Change that configuration instead."
            : !TransferableProviders.TryGetValue(binding.Provider, out var table) ? $"{binding.Provider} devices cannot move between properties."
            : !tables.Contains(table) ? "The provider's tables are not available."
            : await OpenCommandAsync(db, tx, binding, property, tables, ct) ? "A device command is still awaiting confirmation. Wait for it to finish."
            : null;
        var impact = new DeviceTransferImpact(device, current.DisplayName ?? current.Name, binding?.Provider ?? "unknown", property, sourceName,
            destination, destinationName, room, open, blocked);
        return blocked is null ? new(null, impact, current, binding) : new(Fail(ManagementStatus.Conflict, blocked), impact);
    }

    private static async Task<bool> OpenCommandAsync(MySqlConnection db, MySqlTransaction? tx, Binding binding, long property,
        HashSet<string> tables, CancellationToken ct) => binding.Provider switch
    {
        "daikin" when tables.Contains("daikin_commands") => await db.ExecuteScalarAsync<bool>(Query("""
            SELECT EXISTS(SELECT 1 FROM daikin_commands c JOIN daikin_devices d ON d.public_id=c.device_key
                WHERE d.external_id=@ExternalId AND d.property_id=@property AND c.status IN ('dispatched','awaitingConfirmation','uncertain','pending'))
            """, ct, new { binding.ExternalId, property }, tx)),
        "ewelink" when tables.Contains("ewelink_relay_commands") => await db.ExecuteScalarAsync<bool>(Query("""
            SELECT EXISTS(SELECT 1 FROM ewelink_relay_commands c JOIN ewelink_devices d ON d.public_id=c.device_id
                WHERE d.external_id=@ExternalId AND d.property_id=@property AND c.resolved_at_utc IS NULL)
            """, ct, new { binding.ExternalId, property }, tx)),
        _ => false
    };

    private async Task<PropertyDeletionImpact> ImpactAsync(MySqlConnection db, MySqlTransaction? tx, long property, string role,
        HashSet<string> tables, CancellationToken ct)
    {
        var name = await db.QuerySingleAsync<string>(Query("SELECT name FROM properties WHERE id=@property", ct, new { property }, tx));
        var counts = await CountsAsync(db, tx, property, tables, ct);
        int Scalar(string sql) => db.ExecuteScalar<int>(Query(sql, ct, new { property }, tx));
        var historical = Scalar("SELECT COUNT(*) FROM devices WHERE property_id=@property AND retired_at_utc IS NOT NULL");
        var members = Scalar("SELECT COUNT(*) FROM property_memberships WHERE property_id=@property");
        var vaultProfiles = tables.Contains("configuration_profiles") ? Scalar("SELECT COUNT(*) FROM configuration_profiles WHERE scope_id=@property") : 0;
        var dashboards = tables.Contains("dashboard_groups") ? Scalar("SELECT COUNT(*) FROM dashboard_groups WHERE property_id=@property") : 0;
        return new(property, name, counts.Rooms, counts.Devices, counts.Installations, historical, members, vaultProfiles, dashboards,
            IsOwner(role) && counts.Devices == 0);
    }

    /// <summary>Current devices: device rows that have not moved away, plus provider assignments not yet synchronized into one.</summary>
    private static async Task<(int Rooms, int Devices, int Installations)> CountsAsync(MySqlConnection db, MySqlTransaction? tx, long property,
        HashSet<string> tables, CancellationToken ct)
    {
        var rooms = await db.ExecuteScalarAsync<int>(Query("SELECT COUNT(*) FROM rooms WHERE property_id=@property", ct, new { property }, tx));
        var installations = await db.ExecuteScalarAsync<int>(Query("SELECT COUNT(*) FROM installations WHERE property_id=@property", ct, new { property }, tx));
        var devices = await db.ExecuteScalarAsync<int>(Query(
            "SELECT COUNT(*) FROM devices WHERE property_id=@property AND retired_at_utc IS NULL", ct, new { property }, tx));
        foreach (var (provider, table) in TransferableProviders)
            if (tables.Contains(table))
                devices += await db.ExecuteScalarAsync<int>(Query($"""
                    SELECT COUNT(*) FROM {table} x WHERE x.property_id=@property AND NOT EXISTS (
                        SELECT 1 FROM provider_bindings b
                        JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id AND c.provider_code=@provider
                        JOIN devices d ON d.id=b.device_id AND d.property_id=b.property_id AND d.retired_at_utc IS NULL
                        WHERE b.property_id=@property AND b.external_id=x.external_id)
                    """, ct, new { property, provider }, tx));
        return (rooms, devices, installations);
    }

    private static IEnumerable<string> RoomTables(HashSet<string> tables) =>
        new[] { "devices", "logical_devices", "daikin_devices", "ewelink_devices", "tuya_devices" }
            .Where(table => table == "devices" || tables.Contains(table));

    private static async Task<string?> LockAsync(MySqlConnection db, MySqlTransaction tx, long property, CancellationToken ct,
        bool includeDeleted = false) =>
        await db.QuerySingleOrDefaultAsync<string>(Query($"""
            SELECT name FROM properties WHERE id=@property {(includeDeleted ? "" : "AND deleted_at_utc IS NULL")} FOR UPDATE
            """, ct, new { property }, tx));

    internal static async Task<string?> RoleAsync(MySqlConnection db, MySqlTransaction? tx, long property, string user, CancellationToken ct) =>
        await db.QuerySingleOrDefaultAsync<string>(Query("""
            SELECT m.role_code FROM property_memberships m JOIN properties p ON p.id=m.property_id AND p.deleted_at_utc IS NULL
            WHERE m.property_id=@property AND m.user_id=@user
            """, ct, new { property, user }, tx));

    private static async Task<DeviceRow?> DeviceAsync(MySqlConnection db, MySqlTransaction? tx, long property, long device, CancellationToken ct,
        bool forUpdate = false) =>
        await db.QuerySingleOrDefaultAsync<DeviceRow>(Query($"""
            SELECT id AS Id, name AS Name, display_name AS DisplayName, kind AS Kind, manufacturer AS Manufacturer, model AS Model,
                   inventory_key AS InventoryKey, room_id AS RoomId
            FROM devices WHERE property_id=@property AND id=@device AND retired_at_utc IS NULL {(forUpdate ? "FOR UPDATE" : "")}
            """, ct, new { property, device }, tx));

    private static async Task<Binding?> BindingAsync(MySqlConnection db, MySqlTransaction? tx, long property, long device, CancellationToken ct) =>
        await db.QueryFirstOrDefaultAsync<Binding>(Query("""
            SELECT b.id AS BindingId, b.external_id AS ExternalId, c.provider_code AS Provider
            FROM provider_bindings b JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id
            WHERE b.property_id=@property AND b.device_id=@device ORDER BY c.provider_code='simulator', b.id LIMIT 1
            """, ct, new { property, device }, tx));

    private static async Task<bool> RoomNameTakenAsync(MySqlConnection db, MySqlTransaction tx, long property, string name, long? except,
        CancellationToken ct) =>
        await db.ExecuteScalarAsync<bool>(Query("""
            SELECT EXISTS(SELECT 1 FROM rooms WHERE property_id=@property AND LOWER(name)=LOWER(@name) AND (@except IS NULL OR id<>@except))
            """, ct, new { property, name, except }, tx));

    private static async Task<ManagementResult?> ByRequestAsync(MySqlConnection db, string requestId, string user, CancellationToken ct)
    {
        var existing = await db.QuerySingleOrDefaultAsync<long?>(Query("""
            SELECT p.id FROM properties p JOIN property_memberships m ON m.property_id=p.id AND m.user_id=@user
            WHERE p.creation_request_id=@requestId
            """, ct, new { requestId, user }));
        return existing is { } id ? ManagementResult.Done(id) : null;
    }

    private async Task AuditAsync(MySqlConnection db, MySqlTransaction tx, long property, string actor, string action, long? device = null,
        long? room = null, long? otherProperty = null, long? otherDevice = null, string? detail = null, CancellationToken ct = default) =>
        await db.ExecuteAsync(Query("""
            INSERT INTO management_audit(property_id,occurred_at_utc,actor,action,device_id,room_id,other_property_id,other_device_id,detail)
            VALUES(@property,@now,@actor,@action,@device,@room,@otherProperty,@otherDevice,@detail)
            """, ct, new { property, now = Now(), actor = Truncate(actor, 64), action, device, room, otherProperty, otherDevice, detail = Truncate(detail) }, tx));

    internal static async Task<HashSet<string>> TablesAsync(MySqlConnection db, MySqlTransaction? tx, CancellationToken ct) =>
        (await db.QueryAsync<string>(Query(
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN @OptionalTables",
            ct, new { OptionalTables }, tx))).ToHashSet(StringComparer.Ordinal);

    private DateTime Now() => LogicalDeviceStore.Microseconds(clock.GetUtcNow().UtcDateTime);

    private static (string? Name, string? Description, ManagementResult? Invalid) Room(RoomRequest request)
    {
        var name = Clean(request.Name);
        var description = Clean(request.Description);
        if (InvalidName(name)) return (null, null, Invalid("Enter a room name of 1 to 128 characters."));
        if (description is { Length: > 256 } || description?.Any(char.IsControl) == true) return (null, null, Invalid("Keep the description to 256 characters."));
        return (name, description, null);
    }

    /// <summary>A unique, readable key: a slug of the name and eight random hexadecimal characters.</summary>
    internal static string SiteKey(string name)
    {
        var slug = new StringBuilder();
        foreach (var character in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)) slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '-' && (char.IsWhiteSpace(character) || character is '-' or '_' or '.')) slug.Append('-');
            if (slug.Length >= 48) break;
        }
        var stem = slug.ToString().Trim('-');
        return (stem.Length == 0 ? "property" : stem) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    internal static bool ValidTimeZone(string zone) => zone.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _) &&
        (zone == "UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out _));

    private static bool InvalidName(string? name) => name is not { Length: >= 1 and <= 128 } || name.Any(char.IsControl);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Truncate(string? value, int length = 256) => value is null ? null : value.Length <= length ? value : value[..length];
    internal static bool CanManage(string? role) => role?.ToUpperInvariant() is "OWNER" or "ADMIN";
    private static bool IsOwner(string? role) => string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase);
    private static ManagementResult NotFound() => Fail(ManagementStatus.NotFound, "The property does not exist or is not available to you.");
    private static ManagementResult Forbidden() => Fail(ManagementStatus.Forbidden, "Only property Owners and Admins can change this.");
    private static ManagementResult Invalid(string message) => Fail(ManagementStatus.Invalid, message);
    private static ManagementResult Fail(ManagementStatus status, string message) => ManagementResult.Fail(status, message);

    private sealed record PropertyRow(long Id, string Name, string? Description, string TimeZone, string Role);
    private sealed record DeviceRow(long Id, string Name, string? DisplayName, string Kind, string Manufacturer, string Model, string InventoryKey,
        long? RoomId);
    private sealed record Binding(long BindingId, string ExternalId, string Provider);
    private sealed record AuditRow(long Id, DateTime OccurredAt, string Actor, string Action, long? OtherPropertyId, string? OtherPropertyName,
        string? Detail);
    private sealed record TransferPlan(ManagementResult? Failure, DeviceTransferImpact? Impact = null, DeviceRow? Device = null, Binding? Binding = null);
}
