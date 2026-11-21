using System.Text.Json;
using Dapper;
using HomeAutomation.Infrastructure.MySql;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Daikin;

internal sealed record DaikinAccount(string Owner, byte[]? Envelope, string Status, DateTime? LastSuccess, DateTime? RetryAfter);
internal sealed record DaikinRow(string Id, string ExternalId, long? PropertyId, long? RoomId, string? Snapshot)
{
    public DaikinDevice? Device => Snapshot is null ? null : JsonSerializer.Deserialize<DaikinDevice>(Snapshot);
}

public sealed record DaikinOutcome(string Id, string Operation, string Value, string Status, DateTimeOffset UpdatedAt);
internal sealed class DaikinStore(Database database, TimeProvider clock, DaikinOptions options)
{
    public async Task<DaikinAccount?> AccountAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<DaikinAccount>(Query("""
            SELECT owner_id AS Owner, token_envelope AS Envelope, status AS Status,
                   last_success_utc AS LastSuccess, retry_after_utc AS RetryAfter FROM daikin_account WHERE id=1
            """, ct));
    }

    public async Task ClaimAsync(string owner, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO daikin_account (id,owner_id,status) VALUES (1,@owner,'disconnected')
            ON DUPLICATE KEY UPDATE id=daikin_account.id
            """, ct, new { owner }));
        if (await db.ExecuteScalarAsync<string>(Query("SELECT owner_id FROM daikin_account WHERE id=1", ct)) != owner)
            throw new DaikinFailure("forbidden");
    }

    public async Task SaveTokensAsync(string owner, byte[]? envelope, string status, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("UPDATE daikin_account SET token_envelope=@envelope,status=@status WHERE id=1 AND owner_id=@owner",
            ct, new { owner, envelope, status }));
    }

    public async Task RecordStatusAsync(string status, DateTimeOffset? retryAt, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            UPDATE daikin_account SET status=@status,
              retry_after_utc=COALESCE(@retry,retry_after_utc),
              last_success_utc=COALESCE(@success,last_success_utc) WHERE id=1
            """, ct, new { status, retry = retryAt?.UtcDateTime,
                success = status == "connected" ? clock.GetUtcNow().UtcDateTime : (DateTime?)null }));
    }

    public async Task ReserveAsync(bool background, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.QuerySingleAsync<string>(Query("SELECT owner_id FROM daikin_account WHERE id=1 FOR UPDATE", ct, transaction: tx));
        var recent = await db.QueryAsync<DateTime>(Query("""
            SELECT requested_utc FROM daikin_requests WHERE requested_utc > @since ORDER BY requested_utc,id
            """, ct, new { since = now.UtcDateTime.AddDays(-1) }, tx));
        var times = recent.AsList();
        if (BudgetRetryAt(times, options.DailyLimit, options.ManualReserve, background) is { } retry)
            throw new DaikinFailure(background ? "pollBudgetReserved" : "rateLimited", retry);
        await db.ExecuteAsync(Query("""
            INSERT INTO daikin_requests(requested_utc) VALUES (@now);
            """, ct, new { now = now.UtcDateTime }, tx));
        await tx.CommitAsync(ct);
    }

    internal static DateTimeOffset? BudgetRetryAt(IReadOnlyList<DateTime> ordered, int limit, int reserve, bool background)
    {
        var available = limit - (background ? reserve : 0);
        return ordered.Count >= available ? Utc(ordered[ordered.Count - available]).AddDays(1) : null;
    }

    public async Task<IReadOnlyList<DaikinRow>> DevicesAsync(string owner, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return (await db.QueryAsync<DaikinRow>(Query("""
            SELECT public_id AS Id, external_id AS ExternalId, property_id AS PropertyId, room_id AS RoomId,
                   snapshot AS Snapshot FROM daikin_devices WHERE owner_id=@owner ORDER BY public_id
            """, ct, new { owner }))).AsList();
    }

    public async Task SaveSnapshotAsync(string owner, DaikinDevice device, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO daikin_devices(public_id,owner_id,external_id,snapshot) VALUES (@id,@owner,@key,@snapshot) AS incoming
            ON DUPLICATE KEY UPDATE snapshot=incoming.snapshot
            """, ct, new { id = Guid.NewGuid().ToString("N"), owner, key = device.ExternalId, snapshot = JsonSerializer.Serialize(device) }));
    }

    public async Task<bool> AssignAsync(string owner, string device, long property, long? room, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.ExecuteAsync(Query("""
            UPDATE daikin_devices SET property_id=@property,room_id=@room
            WHERE public_id=@device AND owner_id=@owner AND (property_id IS NULL OR property_id=@property)
              AND (@room IS NULL OR EXISTS(SELECT 1 FROM rooms r WHERE r.property_id=@property AND r.id=@room))
            """, ct, new { owner, device, property, room })) == 1;
    }

    public async Task<string> StartCommandAsync(string owner, string device, long property, DaikinCommand command, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(Query("""
            INSERT INTO daikin_commands(id,device_key,property_id,actor_id,operation,desired,status,requested_utc,updated_utc)
            VALUES (@id,@device,@property,@owner,@operation,@desired,'pending',@now,@now)
            """, ct, new { id, device, property, owner, operation = command.Operation, desired = command.Value,
                now = clock.GetUtcNow().UtcDateTime }));
        return id;
    }

    public async Task FinishCommandAsync(string id, string status, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("UPDATE daikin_commands SET status=@status,updated_utc=@now WHERE id=@id",
            ct, new { id, status, now = clock.GetUtcNow().UtcDateTime }));
    }

    public async Task<DaikinOutcome?> LatestCommandAsync(string device, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var entry = await db.QuerySingleOrDefaultAsync<CommandRow>(Query("""
            SELECT id AS Id, operation AS Operation, desired AS Value, status AS Status, updated_utc AS UpdatedAt
            FROM daikin_commands WHERE device_key=@device ORDER BY requested_utc DESC LIMIT 1
            """, ct, new { device }));
        return entry is null ? null : new(entry.Id, entry.Operation, entry.Value, entry.Status, Utc(entry.UpdatedAt));
    }

    private sealed record CommandRow(string Id, string Operation, string Value, string Status, DateTime UpdatedAt);
}
