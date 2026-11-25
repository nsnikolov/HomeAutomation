using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Provider-neutral history for one physical series. Hourly and daily buckets combine materialized hourly rollups with
/// raw calculations for partial hours (non-whole-hour local midnights) and for the most recent unmaterialized hours.
/// </summary>
public sealed class TelemetryHistoryReader(Database database, Migrations migrations, TelemetryHoldPolicy holds,
    TelemetryHistoryOptions options, TimeProvider clock) : ITelemetryHistory
{
    public const double IncompleteBelowCoverage = 0.8;
    internal static readonly TimeSpan LiveRawLimit = TimeSpan.FromHours(48);
    internal const int RawRowLimit = 1_000_000;
    private const int RangesPerQuery = 100;

    public async Task<IReadOnlyList<HistorySeriesInfo>> SeriesAsync(long propertyId, long? deviceId, Metric? metric, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var ready = await migrations.VerifyTelemetryHistoryAsync(ct);
        return Describe(await LoadAsync(db, ready, propertyId, ct))
            .Where(item => (deviceId is null || item.DeviceId == deviceId) && (metric is null || item.Metric == metric))
            .ToList();
    }

    public async Task<HistoryRead> ReadAsync(long propertyId, HistoryQuery query, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var ready = await migrations.VerifyTelemetryHistoryAsync(ct);
        if (query.Resolution != HistoryResolution.Raw && !ready) return HistoryRead.Of(HistoryReadStatus.SchemaPending);
        var all = Describe(await LoadAsync(db, ready, propertyId, ct)).ToList();
        var selector = query.Selector;
        List<HistorySeriesInfo> candidates;
        if (selector.SeriesId is { } id) candidates = all.Where(item => item.SeriesId == id).ToList();
        else if (selector.EndpointId is { } endpoint) candidates = all.Where(item => item.EndpointId == endpoint && item.Metric == selector.Metric).ToList();
        else if (selector.InstallationId is { } installation)
            candidates = all.Where(item => item.InstallationId == installation && item.EndpointId is null && item.Metric == selector.Metric).ToList();
        else if (selector.DeviceId is { } device) candidates = all.Where(item => item.DeviceId == device && item.Metric == selector.Metric).ToList();
        else return HistoryRead.Of(HistoryReadStatus.NotFound);
        if (candidates.Count == 0) return HistoryRead.Of(HistoryReadStatus.NotFound);
        var preferred = candidates.Where(item => item.Preferred || selector.SeriesId is not null).ToList();
        if (preferred.Select(item => (item.EndpointId, item.InstallationId)).Distinct().Count() > 1)
            return HistoryRead.Of(HistoryReadStatus.Ambiguous, preferred.Select(item => item.SeriesId).ToList());
        var series = preferred.Single();
        var alternatives = all.Where(item => item.SeriesId != series.SeriesId && item.Metric == series.Metric &&
            item.EndpointId == series.EndpointId && item.InstallationId == series.InstallationId).Select(item => item.SeriesId).ToList();

        var timeZone = await db.QuerySingleAsync<string>(Query("SELECT time_zone FROM properties WHERE id=@propertyId", ct, new { propertyId }));
        var zone = TelemetryHoldPolicy.Zone(timeZone);
        var now = clock.GetUtcNow().UtcDateTime;
        if (query.Resolution == HistoryResolution.Raw)
            return new(HistoryReadStatus.Found, await RawAsync(db, series, alternatives, timeZone, query, ct));

        var metric = series.Metric;
        if (!TelemetryAggregation.IsAggregated(metric)) return HistoryRead.Of(HistoryReadStatus.Unsupported);
        var policy = holds.Policy(series.Source, metric, zone);
        if (query.Resolution == HistoryResolution.Day && zone is null ||
            TelemetryAggregation.Kind(metric) == MetricAggregation.DailyResetCounter && policy.CounterResetZone is null)
            return HistoryRead.Of(HistoryReadStatus.InvalidTimeZone);

        var windows = new List<(TimeWindow Window, DateOnly? Date)>();
        if (query.Resolution == HistoryResolution.Hour)
        {
            for (var hour = CalendarDays.FloorHour(query.From.UtcDateTime); hour < query.To.UtcDateTime && hour < now; hour = hour.AddHours(1))
                windows.Add((new(hour, hour.AddHours(1)), null));
        }
        else
        {
            for (var date = query.FromDate!.Value; date <= query.ToDate!.Value; date = date.AddDays(1))
            {
                var day = CalendarDays.Day(date, zone!);
                if (day.End > day.Start && day.Start < now) windows.Add((day, date));
            }
        }
        try
        {
            var buckets = await BucketsAsync(db, series, metric, policy, windows, now, ct);
            var from = windows.Count > 0 ? windows[0].Window.Start : query.From.UtcDateTime;
            var to = windows.Count > 0 ? windows[^1].Window.End : query.To.UtcDateTime;
            return new(HistoryReadStatus.Found, new HistoryResult(series, alternatives, query.Resolution, Utc(from), Utc(to), timeZone,
                IncompleteBelowCoverage, null, buckets, null));
        }
        catch (HistoryRangeTooLargeException)
        {
            return HistoryRead.Of(HistoryReadStatus.TooLarge);
        }
    }

    private async Task<HistoryResult> RawAsync(MySqlConnection db, HistorySeriesInfo series, IReadOnlyList<long> alternatives,
        string timeZone, HistoryQuery query, CancellationToken ct)
    {
        var provenance = await migrations.VerifyEwelinkReceiveTimeAsync(ct) ? "timestamp_source" : "CAST(NULL AS CHAR(24))";
        var rows = (await db.QueryAsync<RawRow>(Query($"""
            SELECT observed_at_utc AS ObservedAt, received_at_utc AS ReceivedAt, numeric_value AS Number, boolean_value AS Flag,
                   status_value AS Code, {provenance} AS TimestampSource
            FROM telemetry_samples
            WHERE series_id=@SeriesId AND observed_at_utc >= @from AND observed_at_utc < @to AND (@after IS NULL OR observed_at_utc > @after)
            ORDER BY observed_at_utc LIMIT @take
            """, ct, new
        {
            series.SeriesId, from = query.From.UtcDateTime, to = query.To.UtcDateTime,
            after = query.After?.UtcDateTime, take = query.Limit + 1
        }))).AsList();
        var page = rows.Take(query.Limit).Select(row => new RawHistoryPoint(Utc(row.ObservedAt), Utc(row.ReceivedAt), row.Number,
            row.Flag, row.Code, row.TimestampSource ?? (series.Source == "simulator" ? "simulated" : "provider"))).ToList();
        return new(series, alternatives, HistoryResolution.Raw, query.From, query.To, timeZone, IncompleteBelowCoverage, page, null,
            rows.Count > query.Limit ? page[^1].ObservedAt : null);
    }

    private async Task<IReadOnlyList<HistoryBucket>> BucketsAsync(MySqlConnection db, HistorySeriesInfo series, Metric metric,
        AggregationPolicy policy, IReadOnlyList<(TimeWindow Window, DateOnly? Date)> windows, DateTime now, CancellationToken ct)
    {
        if (windows.Count == 0) return [];
        var aggregatedFrom = series.AggregatedFrom?.UtcDateTime;
        var aggregatedTo = series.AggregatedTo?.UtcDateTime;
        var liveFrom = aggregatedTo ?? CalendarDays.FloorHour(now);
        var liveLimit = now - LiveRawLimit;
        var plans = windows.Select(item => Cap(HistoryPlanner.Split(item.Window, aggregatedFrom, aggregatedTo, now), liveFrom, liveLimit))
            .ToArray();

        var hourly = plans.SelectMany(plan => plan).Where(part => part.Source == HistoryPartSource.Hourly).Select(part => part.Window.Start).ToList();
        var rows = new Dictionary<DateTime, HistoryAggregate>();
        if (hourly.Count > 0)
        {
            var found = await db.QueryAsync<TelemetryRollups.RollupRow>(Query(TelemetryRollups.RollupColumns + """
                 FROM telemetry_rollups_hourly WHERE series_id=@SeriesId AND bucket_start_utc >= @first AND bucket_start_utc <= @last
                """, ct, new { series.SeriesId, first = hourly.Min(), last = hourly.Max() }));
            foreach (var row in found)
            {
                var aggregate = row.ToAggregate();
                rows[aggregate.Start] = aggregate;
            }
        }

        var raw = plans.SelectMany(plan => plan).Where(part => part.Source == HistoryPartSource.Raw).Select(part => part.Window).ToList();
        var computed = new Dictionary<DateTime, HistoryAggregate>();
        if (raw.Count > 0)
        {
            var samples = await ReadRangesAsync(db, series.SeriesId, raw, TelemetryAggregation.Lookback(metric, policy),
                TelemetryAggregation.Lookahead(metric, policy), ct);
            foreach (var aggregate in TelemetryAggregation.Summarize(metric, policy, samples, raw, now))
                computed[aggregate.Start] = aggregate;
        }

        var buckets = new List<HistoryBucket>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
        {
            var (window, date) = windows[index];
            var parts = new List<HistoryAggregate>();
            var missing = false;
            foreach (var part in plans[index])
            {
                switch (part.Source)
                {
                    case HistoryPartSource.Hourly:
                        parts.Add(rows.GetValueOrDefault(part.Window.Start) ?? HistoryAggregate.Empty(part.Window.Start, part.Window.End));
                        break;
                    case HistoryPartSource.Raw:
                        parts.Add(computed[part.Window.Start]);
                        break;
                    default:
                        missing = true;
                        break;
                }
            }
            buckets.Add(Bucket(series, metric, window, date, HistoryAggregate.Combine(window.Start, window.End, parts), missing, now));
        }
        return buckets;
    }

    private HistoryBucket Bucket(HistorySeriesInfo series, Metric metric, TimeWindow window, DateOnly? date,
        HistoryAggregate aggregate, bool notAggregated, DateTime now)
    {
        var elapsed = ((window.End < now ? window.End : now) - window.Start).TotalSeconds;
        var coverage = elapsed > 0 ? Math.Min(1, aggregate.CoveredSeconds / elapsed) : 0;
        var noData = aggregate.SampleCount == 0 && aggregate.CoveredSeconds <= 0 && aggregate.MeasuredWh is null;
        var quality = notAggregated ? "notAggregated" : noData ? "noData" : coverage < IncompleteBelowCoverage ? "incomplete" : "complete";
        var flags = Enum.GetValues<AggregateFlags>().Where(flag => flag != AggregateFlags.None && aggregate.Flags.HasFlag(flag))
            .Select(flag => char.ToLowerInvariant(flag.ToString()[0]) + flag.ToString()[1..]).ToList();
        if (window.End > now) flags.Add("partialPeriod");
        var kind = TelemetryAggregation.Kind(metric);
        HistoryEnergy? energy = null;
        if (TelemetryAggregation.IsCounter(metric))
        {
            var uncertain = (aggregate.Flags & (AggregateFlags.CounterGap | AggregateFlags.CounterDiscontinuity | AggregateFlags.CounterReset)) != 0;
            energy = new(aggregate.MeasuredWh is null ? "noData" : quality == "complete" && !uncertain ? "measured" : "incomplete",
                "counter", aggregate.MeasuredWh, null, null);
        }
        else if (TelemetryAggregation.IsPower(metric))
        {
            energy = new(aggregate.EstimatedPositiveWh is null ? "noData" : quality == "complete" ? "estimated" : "incomplete",
                "powerIntegration", null, aggregate.EstimatedPositiveWh, aggregate.EstimatedNegativeWh);
        }
        var boolean = kind == MetricAggregation.Boolean;
        return new HistoryBucket(Utc(window.Start), Utc(window.End), date, window.Duration.TotalSeconds, quality, coverage,
            aggregate.CoveredSeconds, aggregate.SampleCount, aggregate.Minimum, aggregate.Maximum, aggregate.TimeWeightedAverage,
            aggregate.ArithmeticMean, Utc(aggregate.FirstObservedAt), Utc(aggregate.LastObservedAt), aggregate.LastValue,
            boolean ? aggregate.OnSeconds ?? (noData ? null : 0) : null, boolean ? aggregate.TransitionCount : null, energy,
            window.End > now.AddHours(-options.RecomputeHours), flags);
    }

    /// <summary>Raw calculation of unmaterialized recent time is bounded. Older gaps are reported as not aggregated.</summary>
    internal static IReadOnlyList<HistoryPart> Cap(IReadOnlyList<HistoryPart> parts, DateTime liveFrom, DateTime liveLimit)
    {
        var result = new List<HistoryPart>(parts.Count + 1);
        foreach (var part in parts)
        {
            if (part.Source != HistoryPartSource.Raw || part.Window.Start < liveFrom || part.Window.Start >= liveLimit)
            {
                result.Add(part);
                continue;
            }
            if (part.Window.End <= liveLimit)
            {
                result.Add(new(part.Window, HistoryPartSource.NotAggregated));
                continue;
            }
            result.Add(new(new(part.Window.Start, liveLimit), HistoryPartSource.NotAggregated));
            result.Add(new(new(liveLimit, part.Window.End), HistoryPartSource.Raw));
        }
        return result;
    }

    private static async Task<List<RawObservation>> ReadRangesAsync(MySqlConnection db, long seriesId, IReadOnlyList<TimeWindow> parts,
        TimeSpan lookback, TimeSpan lookahead, CancellationToken ct)
    {
        var ranges = new List<TimeWindow>();
        foreach (var part in parts)
        {
            var range = new TimeWindow(part.Start - lookback, part.End + lookahead);
            if (ranges.Count > 0 && range.Start <= ranges[^1].End)
                ranges[^1] = new(ranges[^1].Start, range.End > ranges[^1].End ? range.End : ranges[^1].End);
            else ranges.Add(range);
        }
        var samples = new List<RawObservation>();
        foreach (var chunk in ranges.Chunk(RangesPerQuery))
        {
            var parameters = new DynamicParameters(new { seriesId, limit = RawRowLimit - samples.Count + 1 });
            var predicates = chunk.Select((range, index) =>
            {
                parameters.Add($"a{index}", range.Start);
                parameters.Add($"b{index}", range.End);
                return $"(observed_at_utc >= @a{index} AND observed_at_utc < @b{index})";
            });
            var rows = (await db.QueryAsync<SampleRow>(Query($"""
                SELECT observed_at_utc AS At, numeric_value AS Number, boolean_value AS Flag FROM telemetry_samples
                WHERE series_id=@seriesId AND ({string.Join(" OR ", predicates)}) ORDER BY observed_at_utc LIMIT @limit
                """, ct, parameters))).AsList();
            if (samples.Count + rows.Count > RawRowLimit) throw new HistoryRangeTooLargeException();
            samples.AddRange(rows.Select(row => new RawObservation(DateTime.SpecifyKind(row.At, DateTimeKind.Utc), row.Number, row.Flag)));
        }
        return samples;
    }

    private async Task<List<SeriesRow>> LoadAsync(MySqlConnection db, bool ready, long propertyId, CancellationToken ct)
    {
        var state = ready
            ? "st.rolled_from_utc AS RolledFrom, st.rolled_to_utc AS RolledTo"
            : "CAST(NULL AS DATETIME(6)) AS RolledFrom, CAST(NULL AS DATETIME(6)) AS RolledTo";
        var join = ready ? "LEFT JOIN telemetry_rollup_state st ON st.series_id=s.id" : "";
        return (await db.QueryAsync<SeriesRow>(Query($"""
            SELECT s.id AS Id, s.metric AS MetricName, s.source_key AS Source, s.endpoint_id AS EndpointId, e.name AS EndpointName,
                   d.id AS DeviceId, d.name AS DeviceName, s.installation_id AS InstallationId, i.name AS InstallationName, {state}
            FROM telemetry_series s
            LEFT JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
            LEFT JOIN devices d ON d.id=e.device_id AND d.property_id=e.property_id
            LEFT JOIN installations i ON i.id=s.installation_id AND i.property_id=s.property_id
            {join}
            WHERE s.property_id=@propertyId
            ORDER BY s.id
            """, ct, new { propertyId }))).AsList();
    }

    /// <summary>
    /// Source selection: when one endpoint or installation reports a metric through several streams, the dedicated
    /// timestamped report stream wins over the provider snapshot stream, then the oldest series. Streams are never merged.
    /// </summary>
    internal IEnumerable<HistorySeriesInfo> Describe(IEnumerable<SeriesRow> rows)
    {
        var valid = rows.Where(row => row.Metric is not null).ToList();
        var preferred = valid.GroupBy(row => (row.EndpointId, row.InstallationId, row.Metric))
            .Select(group => group.OrderBy(row => SourcePriority(row.Source)).ThenBy(row => row.Id).First().Id).ToHashSet();
        foreach (var row in valid)
        {
            var metric = row.Metric!.Value;
            var kind = TelemetryAggregation.Kind(metric);
            yield return new(row.Id, metric, Telemetry.Unit(metric), row.Source, row.DeviceId, row.DeviceName, row.EndpointId,
                row.EndpointName, row.InstallationId, row.InstallationName, kind,
                kind == MetricAggregation.Gauge ? "timeWeighted" : null,
                TelemetryAggregation.IsCounter(metric) ? "measuredCounter" : TelemetryAggregation.IsPower(metric) ? "estimatedFromPower" : "unsupported",
                preferred.Contains(row.Id), holds.Hold(row.Source, metric).TotalSeconds, Utc(row.RolledFrom), Utc(row.RolledTo));
        }
    }

    internal static int SourcePriority(string source) => source == TuyaService.SensorReportSource ? 0 : 1;

    internal sealed record SeriesRow(long Id, string MetricName, string Source, long? EndpointId, string? EndpointName,
        long? DeviceId, string? DeviceName, long? InstallationId, string? InstallationName, DateTime? RolledFrom, DateTime? RolledTo)
    {
        public Metric? Metric => Enum.TryParse<Metric>(MetricName, out var metric) && Enum.IsDefined(metric) ? metric : null;
    }

    private sealed record RawRow(DateTime ObservedAt, DateTime ReceivedAt, double? Number, bool? Flag, string? Code, string? TimestampSource);
    private sealed record SampleRow(DateTime At, double? Number, bool? Flag);
    private sealed class HistoryRangeTooLargeException : Exception;
}
