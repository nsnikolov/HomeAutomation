using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Daikin;

internal sealed record DaikinAccount(string Owner, byte[]? Envelope, string Status, DateTime? LastSuccess, DateTime? RetryAfter);
internal sealed record DaikinRow(string Id, string ExternalId, long? PropertyId, long? RoomId, string? Snapshot)
{
    public DaikinDevice? Device => Snapshot is null ? null : JsonSerializer.Deserialize<DaikinDevice>(Snapshot);
}

public sealed record DaikinOutcome(string Id, string Operation, string Value, string Status, DateTimeOffset UpdatedAt,
    string? ModeContext = null);

internal static class DaikinCommandStatus
{
    public const string Dispatched = "dispatched";
    public const string Awaiting = "awaitingConfirmation";
    public const string Confirmed = "confirmed";
    public const string Uncertain = "uncertain";
    public static readonly string[] Open = [Dispatched, Awaiting, Uncertain, "pending"];

    public static bool Blocks(DaikinOutcome open, string operation) => open.Status != Awaiting ||
        operation == open.Operation ||
        operation == "mode" && open.Operation is "temperature" or "fan" ||
        open.Operation == "mode" && operation is "temperature" or "fan";
}

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

    public async Task ReserveAsync(bool background, CancellationToken ct, int retain = 0)
    {
        var now = clock.GetUtcNow();
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.QuerySingleAsync<string>(Query("SELECT owner_id FROM daikin_account WHERE id=1 FOR UPDATE", ct, transaction: tx));
        var recent = await db.QueryAsync<DateTime>(Query("""
            SELECT requested_utc FROM daikin_requests WHERE requested_utc > @since ORDER BY requested_utc,id
            """, ct, new { since = now.UtcDateTime.AddDays(-1) }, tx));
        var times = recent.AsList();
        if (BudgetRetryAt(times, options.DailyLimit - retain, options.ManualReserve, background) is { } retry)
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
              AND EXISTS(SELECT 1 FROM properties p WHERE p.id=@property AND p.deleted_at_utc IS NULL)
              AND (@room IS NULL OR EXISTS(SELECT 1 FROM rooms r WHERE r.property_id=@property AND r.id=@room))
            """, ct, new { owner, device, property, room })) == 1;
    }

    public async Task<string> StartCommandAsync(string owner, string device, long property, DaikinCommand command,
        string? modeContext, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(Query("""
            INSERT INTO daikin_commands(id,device_key,property_id,actor_id,operation,desired,status,requested_utc,updated_utc)
            VALUES (@id,@device,@property,@owner,@operation,@desired,@status,@now,@now)
            """, ct, new { id, device, property, owner,
                operation = modeContext is null ? command.Operation : $"{command.Operation}:{modeContext}", desired = command.Value,
                status = DaikinCommandStatus.Dispatched, now = clock.GetUtcNow().UtcDateTime }));
        return id;
    }

    public async Task FinishCommandAsync(string id, string status, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("UPDATE daikin_commands SET status=@status,updated_utc=@now WHERE id=@id",
            ct, new { id, status, now = clock.GetUtcNow().UtcDateTime }));
    }

    public async Task<bool> HasAwaitingAsync(string owner, string[] ids, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.ExecuteScalarAsync<int>(Query("""
            SELECT EXISTS(SELECT 1 FROM daikin_commands WHERE actor_id=@owner AND id IN @ids AND status=@status)
            """, ct, new { owner, ids, status = DaikinCommandStatus.Awaiting })) == 1;
    }

    public async Task<IReadOnlyList<DaikinOutcome>> CommandsAsync(string device, CancellationToken ct, bool includeLatest = false)
    {
        await using var db = await database.OpenAsync(ct);
        var rows = await db.QueryAsync<CommandRow>(Query("""
            SELECT id AS Id, operation AS Operation, desired AS Value, status AS Status, updated_utc AS UpdatedAt
            FROM daikin_commands WHERE device_key=@device AND (status IN @open OR
                (@includeLatest AND id=(SELECT id FROM daikin_commands WHERE device_key=@device
                    ORDER BY requested_utc DESC,id DESC LIMIT 1)))
            ORDER BY requested_utc DESC,id DESC
            """, ct, new { device, includeLatest, open = DaikinCommandStatus.Open }));
        return rows.Select(entry =>
        {
            var operation = entry.Operation.Split(':', 2);
            return new DaikinOutcome(entry.Id, operation[0], entry.Value,
                entry.Status == "verified" ? DaikinCommandStatus.Confirmed : entry.Status,
                Utc(entry.UpdatedAt), operation.Length > 1 ? operation[1] : null);
        }).ToArray();
    }

    public async Task<DaikinWaterHistory> WaterHistoryAsync(long property, string device, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await database.OpenAsync(ct);
        var rows = await db.QueryAsync<WaterHistoryRow>(Query("""
            SELECT s.metric AS Metric, h.observed_at_utc AS ObservedAt, h.numeric_value AS Number
            FROM daikin_devices d
            JOIN provider_bindings b ON b.property_id=d.property_id AND b.external_id=d.external_id
            JOIN integration_connections c ON c.id=b.connection_id AND c.provider_code='daikin'
            JOIN provider_endpoint_bindings e ON e.binding_id=b.id AND e.external_endpoint_key='climate'
            JOIN telemetry_series s ON s.endpoint_id=e.endpoint_id AND s.property_id=d.property_id AND s.source_key='daikin'
            JOIN telemetry_samples h ON h.series_id=s.id
            WHERE d.public_id=@device AND d.property_id=@property
              AND s.metric IN ('LeavingWaterTemperature','TargetLeavingWaterTemperature')
              AND h.observed_at_utc >= @from AND h.observed_at_utc <= @now
            ORDER BY h.observed_at_utc DESC LIMIT 2000
            """, ct, new { device, property, from = now.AddDays(-1), now }));
        return new(options.PollMinutes * 60, rows.Reverse().GroupBy(row => Enum.Parse<Metric>(row.Metric))
            .Select(group => new DaikinHistorySeries(group.Key, group
                .Select(row => new HistoryPoint(Utc(row.ObservedAt), row.Number, null, null)).ToArray())).ToArray());
    }

    private sealed record WaterHistoryRow(string Metric, DateTime ObservedAt, double? Number);

    private sealed record CommandRow(string Id, string Operation, string Value, string Status, DateTime UpdatedAt);
}
