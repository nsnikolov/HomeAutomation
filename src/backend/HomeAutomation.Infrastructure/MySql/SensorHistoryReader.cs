using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed record SensorSeries(string Code, Metric Metric, string? Unit, IReadOnlyList<HistoryPoint> Points, bool Truncated);
public sealed record SensorHistory(DateTimeOffset From, DateTimeOffset To, int PollSeconds, IReadOnlyList<SensorSeries> Series);

public sealed class SensorHistoryReader(Database database)
{
    private const int PointLimit = 1600;

    internal async Task<IReadOnlyList<SensorSeries>> ReadAsync(long property, string provider, string external,
        string source, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var channels = await db.QueryAsync<Channel>(Query("""
            SELECT s.id AS Id,e.external_endpoint_key AS Code,s.metric AS Metric
            FROM integration_connections c
            JOIN provider_bindings b ON b.connection_id=c.id AND b.property_id=c.property_id
            JOIN provider_endpoint_bindings e ON e.binding_id=b.id AND e.device_id=b.device_id
            JOIN telemetry_series s ON s.endpoint_id=e.endpoint_id AND s.property_id=b.property_id
            WHERE c.property_id=@property AND c.provider_code=@provider AND b.external_id=@external
              AND s.source_key=@source AND s.metric IN ('Temperature','Humidity')
            ORDER BY s.metric,e.external_endpoint_key LIMIT 16
            """, ct, new { property, provider, external, source }));
        var history = new List<SensorSeries>();
        foreach (var channel in channels)
        {
            var metric = Enum.Parse<Metric>(channel.Metric);
            var rows = (await db.QueryAsync<SampleRow>(Query("""
                SELECT observed_at_utc AS At,numeric_value AS Number
                FROM telemetry_samples
                WHERE series_id=@id AND observed_at_utc >= @from AND observed_at_utc <= @to AND numeric_value IS NOT NULL
                ORDER BY observed_at_utc DESC LIMIT @limit
                """, ct, new { id = channel.Id, from = from.UtcDateTime, to = to.UtcDateTime, limit = PointLimit + 1 }))).AsList();
            history.Add(new(channel.Code, metric, Telemetry.Unit(metric), rows.Take(PointLimit).Reverse()
                .Select(row => new HistoryPoint(Utc(row.At), row.Number, null, null)).ToArray(), rows.Count > PointLimit));
        }
        return history;
    }

    private sealed record Channel(long Id, string Code, string Metric);
    private sealed record SampleRow(DateTime At, double Number);
}
