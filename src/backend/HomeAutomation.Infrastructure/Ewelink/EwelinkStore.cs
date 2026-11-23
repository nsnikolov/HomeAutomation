using System.Text.Json;
using Dapper;
using HomeAutomation.Infrastructure.MySql;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

internal sealed record EwelinkAccount(string Owner, byte[]? Envelope, string Status, DateTime? LastSuccess, DateTime? NextSync);
internal sealed record EwelinkRow(string Id, string ExternalId, long? PropertyId, long? RoomId, string Snapshot)
{
    public EwelinkDevice Device => JsonSerializer.Deserialize<EwelinkDevice>(Snapshot)!;
}

internal interface IEwelinkStore
{
    Task<bool> SchemaReadyAsync(CancellationToken ct);
    Task<EwelinkAccount?> AccountAsync(CancellationToken ct);
    Task SaveTokensAsync(string owner, byte[]? envelope, CancellationToken ct);
    Task ReportAsync(string owner, string status, DateTimeOffset? success, DateTimeOffset next, CancellationToken ct);
    Task<IReadOnlyList<EwelinkRow>> DevicesAsync(string owner, CancellationToken ct);
    Task SaveAsync(string owner, EwelinkDevice device, CancellationToken ct);
    Task AssignAsync(EwelinkRow row, string owner, long property, long? room, CancellationToken ct);
}

internal sealed class EwelinkStore(Database database, Migrations migrations) : IEwelinkStore
{
    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyAsync(ct, includeEwelink: true);

    public async Task<EwelinkAccount?> AccountAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<EwelinkAccount>(Query("""
            SELECT owner_id AS Owner,token_envelope AS Envelope,status AS Status,last_success_utc AS LastSuccess,next_sync_utc AS NextSync
            FROM ewelink_account WHERE id=1
            """, ct));
    }

    public async Task SaveTokensAsync(string owner, byte[]? envelope, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var status = envelope is null ? "disconnected" : "connected";
        await db.ExecuteAsync(Query("""
            INSERT INTO ewelink_account(id,owner_id,token_envelope,status) VALUES(1,@owner,@envelope,@status) AS incoming
            ON DUPLICATE KEY UPDATE token_envelope=IF(owner_id=@owner,incoming.token_envelope,token_envelope),
                status=IF(owner_id=@owner,incoming.status,status)
            """, ct, new { owner, envelope, status }));
    }

    public async Task ReportAsync(string owner, string status, DateTimeOffset? success, DateTimeOffset next, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            UPDATE ewelink_account SET status=@status,last_success_utc=COALESCE(@success,last_success_utc),next_sync_utc=@next
            WHERE id=1 AND owner_id=@owner
            """, ct, new { owner, status, success = success?.UtcDateTime, next = next.UtcDateTime }));
    }

    public async Task<IReadOnlyList<EwelinkRow>> DevicesAsync(string owner, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return (await db.QueryAsync<EwelinkRow>(Query("""
            SELECT public_id AS Id,external_id AS ExternalId,property_id AS PropertyId,room_id AS RoomId,snapshot AS Snapshot
            FROM ewelink_devices WHERE owner_id=@owner ORDER BY public_id
            """, ct, new { owner }))).AsList();
    }

    public async Task SaveAsync(string owner, EwelinkDevice device, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO ewelink_devices(public_id,owner_id,external_id,snapshot) VALUES(@id,@owner,@external,@snapshot) AS incoming
            ON DUPLICATE KEY UPDATE snapshot=incoming.snapshot
            """, ct, new { id = Guid.NewGuid().ToString("N"), owner, external = device.ExternalId, snapshot = JsonSerializer.Serialize(device) }));
    }

    public async Task AssignAsync(EwelinkRow row, string owner, long property, long? room, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (room is not null && await db.ExecuteScalarAsync<int>(Query("""
            SELECT EXISTS(SELECT 1 FROM rooms WHERE id=@room AND property_id=@property)
            """, ct, new { room, property })) != 1)
            throw new EwelinkFailure("invalidRoom");
        await db.ExecuteAsync(Query("""
            UPDATE ewelink_devices SET property_id=@property,room_id=@room
            WHERE public_id=@device AND owner_id=@owner AND (property_id IS NULL OR property_id=@property)
            """, ct, new { device = row.Id, owner, property, room }));
    }
}
