using Dapper;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed record EwelinkRelayCommand(bool? On, bool ConfirmedProtected);
public sealed record EwelinkRelayOutcome(string Id, string Status, bool Open);

internal interface IEwelinkRelayAudit
{
    Task<EwelinkRelayOutcome?> LatestAsync(string device, long property, CancellationToken ct);
    Task<string> BeginAsync(string actor, string device, long property, bool on, DateTimeOffset now, CancellationToken ct);
    Task FinishAsync(string id, string status, DateTimeOffset? resolvedAt, CancellationToken ct);
    Task ReconcileAsync(string device, long property, bool on, DateTimeOffset observedAt, CancellationToken ct);
}

internal sealed class EwelinkRelayAudit(Database database) : IEwelinkRelayAudit
{
    public async Task<EwelinkRelayOutcome?> LatestAsync(string device, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var entry = await db.QuerySingleOrDefaultAsync<AuditEntry>(Query("""
            SELECT id AS Id, status AS Status, resolved_at_utc AS ResolvedAt
            FROM ewelink_relay_commands WHERE device_id=@device AND property_id=@property
            ORDER BY requested_at_utc DESC,id DESC LIMIT 1
            """, ct, new { device, property }));
        return entry is null ? null : new(entry.Id, entry.Status, entry.ResolvedAt is null);
    }

    public async Task<string> BeginAsync(string actor, string device, long property, bool on, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            if (await db.ExecuteAsync(Query("""
                INSERT INTO ewelink_relay_commands(id,device_id,property_id,actor_id,desired_on,requested_at_utc,status)
                SELECT @id,public_id,@property,@actor,@on,@at,'pending'
                FROM ewelink_devices WHERE public_id=@device AND owner_id=@actor AND property_id=@property
                """, ct, new { id, device, property, actor, on, at = now.UtcDateTime })) != 1)
                throw new EwelinkFailure("notFound");
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new EwelinkFailure("commandPending");
        }
        return id;
    }

    public async Task FinishAsync(string id, string status, DateTimeOffset? resolvedAt, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            UPDATE ewelink_relay_commands SET status=@status,resolved_at_utc=@resolved
            WHERE id=@id AND resolved_at_utc IS NULL
            """, ct, new { id, status, resolved = resolvedAt?.UtcDateTime }));
    }

    public async Task ReconcileAsync(string device, long property, bool on, DateTimeOffset observedAt, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            UPDATE ewelink_relay_commands SET status=IF(desired_on=@on,'confirmed','unverified'),resolved_at_utc=@at
            WHERE active_device=@device AND property_id=@property AND requested_at_utc<@at
              AND (status='unverified' OR requested_at_utc<@pendingCutoff)
            """, ct, new { device, property, on, at = observedAt.UtcDateTime, pendingCutoff = observedAt.AddMinutes(-1).UtcDateTime }));
    }

    private sealed record AuditEntry(string Id, string Status, DateTime? ResolvedAt);
}
