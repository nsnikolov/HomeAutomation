using System.Text.Json;
using Dapper;
using HomeAutomation.Infrastructure.MySql;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Tuya;

internal sealed record TuyaSync(string Status, int? ProviderCode, DateTime? LastSuccess, DateTime NextSync);
internal sealed record TuyaRow(string Id, string ExternalId, long? PropertyId, long? RoomId, string Snapshot)
{
    public TuyaDevice Device => JsonSerializer.Deserialize<TuyaDevice>(Snapshot)!;
}

internal sealed class TuyaStore(Database database, TuyaOptions options)
{
    public async Task<TuyaSync?> StatusAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<TuyaSync>(Query("""
            SELECT status AS Status,provider_code AS ProviderCode,last_success_utc AS LastSuccess,next_sync_utc AS NextSync
            FROM tuya_sync WHERE project_scope=@scope
            """, ct, new { scope = options.Scope }));
    }

    public async Task ReportAsync(string status, DateTimeOffset? success, DateTimeOffset next, int? code, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO tuya_sync(project_scope,status,provider_code,last_success_utc,next_sync_utc)
            VALUES(@scope,@status,@code,@success,@next) AS incoming
            ON DUPLICATE KEY UPDATE status=incoming.status,provider_code=incoming.provider_code,
                last_success_utc=COALESCE(incoming.last_success_utc,tuya_sync.last_success_utc),next_sync_utc=incoming.next_sync_utc
            """, ct, new { scope = options.Scope, status, code, success = success?.UtcDateTime, next = next.UtcDateTime }));
    }

    public async Task<IReadOnlyList<TuyaRow>> DevicesAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return (await db.QueryAsync<TuyaRow>(Query("""
            SELECT public_id AS Id,external_id AS ExternalId,property_id AS PropertyId,room_id AS RoomId,snapshot AS Snapshot
            FROM tuya_devices WHERE project_scope=@scope ORDER BY public_id
            """, ct, new { scope = options.Scope }))).AsList();
    }

    public async Task<string?> FindIdentityAsync(string id, long property, CancellationToken ct, bool allowUnassigned = false)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<string>(Query("""
            SELECT external_id FROM tuya_devices WHERE project_scope=@scope AND public_id=@id
              AND (property_id=@property OR (@allowUnassigned AND property_id IS NULL))
            """, ct, new { scope = options.Scope, id, property, allowUnassigned }));
    }

    public async Task SaveAsync(TuyaDevice device, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO tuya_devices(public_id,project_scope,external_id,snapshot) VALUES(@id,@scope,@external,@snapshot) AS incoming
            ON DUPLICATE KEY UPDATE snapshot=incoming.snapshot
            """, ct, new { id = Guid.NewGuid().ToString("N"), scope = options.Scope, external = device.Id,
                snapshot = JsonSerializer.Serialize(device) }));
    }

    public async Task AssignAsync(string id, long property, long? room, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (room is not null && await db.ExecuteScalarAsync<int>(Query("""
            SELECT EXISTS(SELECT 1 FROM rooms WHERE id=@room AND property_id=@property)
            """, ct, new { room, property })) != 1)
            throw new TuyaFailure("invalidRoom");
        var affected = await db.ExecuteAsync(Query("""
            UPDATE tuya_devices SET property_id=@property,room_id=@room
            WHERE project_scope=@scope AND public_id=@id AND (property_id IS NULL OR property_id=@property)
              AND EXISTS(SELECT 1 FROM properties p WHERE p.id=@property AND p.deleted_at_utc IS NULL)
            """, ct, new { scope = options.Scope, id, property, room }));
        if (affected == 0) throw new TuyaFailure("notFound");
    }
}
