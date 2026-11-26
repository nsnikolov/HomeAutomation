using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Provider-neutral history. Physical history reads one series. Logical history reads one series per assignment interval
/// and uses only observations inside that interval. Hourly and daily buckets combine materialized hourly rollups with raw
/// calculations for partial hours, assignment boundaries and the most recent unmaterialized hours.
/// </summary>
public sealed class TelemetryHistoryReader(Database database, Migrations migrations, TelemetryHoldPolicy holds,
    TelemetryHistoryOptions options, TimeProvider clock) : ITelemetryHistory
{
    public const double IncompleteBelowCoverage = 0.8;
    internal static readonly TimeSpan LiveRawLimit = TimeSpan.FromHours(48);
    internal const int RawRowLimit = 1_000_000;
    private const int RangesPerQuery = 100;
    private static readonly TimeWindow WholeHistory = new(LogicalDeviceRules.HistoryOrigin, LogicalDeviceRules.OpenEnd);

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
        var alternatives = Alternatives(all, series);

        var timeZone = await TimeZoneAsync(db, propertyId, ct);
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

        var windows = Windows(query, zone, now);
        try
        {
            var buckets = await BucketsAsync(db, metric, [new(series, policy, WholeHistory, null)], windows, now, ct);
            var (from, to) = Range(windows, query);
            return new(HistoryReadStatus.Found, new HistoryResult(series, alternatives, query.Resolution, from, to, timeZone,
                IncompleteBelowCoverage, null, buckets, null));
        }
        catch (HistoryRangeTooLargeException)
        {
            return HistoryRead.Of(HistoryReadStatus.TooLarge);
        }
    }

    /// <summary>
    /// History of one logical channel. Each assignment contributes its preferred physical series for the metric, limited to
    /// [valid_from, valid_to). Counter deltas, held values and coverage never cross an assignment boundary.
    /// </summary>
    public async Task<LogicalHistoryRead> ReadLogicalAsync(long propertyId, long logicalId, string channel, Metric metric,
        HistoryQuery query, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (!await migrations.VerifyLogicalDevicesAsync(ct)) return new(HistoryReadStatus.SchemaPending);
        var name = await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT name FROM logical_devices WHERE property_id=@propertyId AND id=@logicalId", ct, new { propertyId, logicalId }));
        if (name is null) return new(HistoryReadStatus.NotFound);
        var assignments = (await db.QueryAsync<AssignmentRow>(Query("""
            SELECT a.id AS Id, a.device_id AS DeviceId, d.name AS DeviceName, a.endpoint_id AS EndpointId,
                   a.valid_from_utc AS ValidFrom, a.valid_to_utc AS ValidTo
            FROM logical_device_assignments a JOIN devices d ON d.id=a.device_id AND d.property_id=a.property_id
            WHERE a.property_id=@propertyId AND a.logical_device_id=@logicalId AND a.channel_key=@channel
            ORDER BY a.valid_from_utc
            """, ct, new { propertyId, logicalId, channel }))).AsList();
        if (assignments.Count == 0) return new(HistoryReadStatus.NotFound);

        var all = Describe(await LoadAsync(db, true, propertyId, ct)).ToList();
        var timeZone = await TimeZoneAsync(db, propertyId, ct);
        var zone = TelemetryHoldPolicy.Zone(timeZone);
        var now = clock.GetUtcNow().UtcDateTime;
        var resolved = assignments.Select(assignment => Resolve(all, assignment, metric)).ToList();
        var segments = resolved.Select(item => item.Segment).ToList();

        if (query.Resolution == HistoryResolution.Raw)
        {
            var (points, next) = await LogicalRawAsync(db, resolved, query, ct);
            return new(HistoryReadStatus.Found, new(logicalId, name, channel, metric, Telemetry.Unit(metric), TelemetryAggregation.Kind(metric),
                query.Resolution, query.From, query.To, timeZone, IncompleteBelowCoverage, segments, points, null, next));
        }
        if (!TelemetryAggregation.IsAggregated(metric)) return new(HistoryReadStatus.Unsupported);
        if (query.Resolution == HistoryResolution.Day && zone is null) return new(HistoryReadStatus.InvalidTimeZone);
        var specs = new List<SegmentSpec>();
        foreach (var item in resolved.Where(item => item.Series is not null))
        {
            var policy = holds.Policy(item.Series!.Source, metric, zone);
            if (TelemetryAggregation.Kind(metric) == MetricAggregation.DailyResetCounter && policy.CounterResetZone is null)
                return new(HistoryReadStatus.InvalidTimeZone);
            specs.Add(new(item.Series, policy, item.Interval, item.Assignment.Id));
        }
        var windows = Windows(query, zone, now);
        try
        {
            var assigned = resolved.Select(item => item.Interval).ToList();
            var unavailable = resolved.Where(item => item.Series is null).Select(item => item.Interval).ToList();
            var buckets = await BucketsAsync(db, metric, specs, windows, now, ct, assigned, unavailable);
            var (from, to) = Range(windows, query);
            return new(HistoryReadStatus.Found, new(logicalId, name, channel, metric, Telemetry.Unit(metric), TelemetryAggregation.Kind(metric),
                query.Resolution, from, to, timeZone, IncompleteBelowCoverage, segments, null, buckets, null));
        }
        catch (HistoryRangeTooLargeException)
        {
            return new(HistoryReadStatus.TooLarge);
        }
    }

    private static (LogicalHistorySegment Segment, HistorySeriesInfo? Series, TimeWindow Interval, AssignmentRow Assignment) Resolve(
        IReadOnlyList<HistorySeriesInfo> all, AssignmentRow assignment, Metric metric)
    {
        var interval = new TimeWindow(Kind(assignment.ValidFrom), assignment.ValidTo is { } end ? Kind(end) : LogicalDeviceRules.OpenEnd);
        var matches = all.Where(item => item.DeviceId == assignment.DeviceId && item.Metric == metric &&
            (assignment.EndpointId is null || item.EndpointId == assignment.EndpointId)).ToList();
        var preferred = matches.Where(item => item.Preferred).ToList();
        var status = preferred.Count == 0 ? "unsupported" : preferred.Select(item => item.EndpointId).Distinct().Count() > 1 ? "ambiguous" : "available";
        var series = status == "available" ? preferred.Single() : null;
        var segment = new LogicalHistorySegment(assignment.Id, assignment.DeviceId, assignment.DeviceName, assignment.EndpointId,
            Utc(interval.Start), assignment.ValidTo is null ? null : Utc(interval.End), status, series?.SeriesId, series?.Source,
            series is null ? [] : Alternatives(all, series), series?.MaximumHoldSeconds);
        return (segment, series, interval, assignment);
    }

    private static List<long> Alternatives(IEnumerable<HistorySeriesInfo> all, HistorySeriesInfo series) =>
        all.Where(item => item.SeriesId != series.SeriesId && item.Metric == series.Metric &&
            item.EndpointId == series.EndpointId && item.InstallationId == series.InstallationId).Select(item => item.SeriesId).ToList();

    private static List<(TimeWindow Window, DateOnly? Date)> Windows(HistoryQuery query, TimeZoneInfo? zone, DateTime now)
    {
        var windows = new List<(TimeWindow Window, DateOnly? Date)>();
        if (query.Resolution == HistoryResolution.Hour)
        {
            for (var hour = CalendarDays.FloorHour(query.From.UtcDateTime); hour < query.To.UtcDateTime && hour < now; hour = hour.AddHours(1))
                windows.Add((new(hour, hour.AddHours(1)), null));
            return windows;
        }
        for (var date = query.FromDate!.Value; date <= query.ToDate!.Value; date = date.AddDays(1))
        {
            var day = CalendarDays.Day(date, zone!);
            if (day.End > day.Start && day.Start < now) windows.Add((day, date));
        }
        return windows;
    }

    private static (DateTimeOffset From, DateTimeOffset To) Range(IReadOnlyList<(TimeWindow Window, DateOnly? Date)> windows, HistoryQuery query) =>
        windows.Count > 0 ? (Utc(windows[0].Window.Start), Utc(windows[^1].Window.End)) : (query.From, query.To);

    private async Task<HistoryResult> RawAsync(MySqlConnection db, HistorySeriesInfo series, IReadOnlyList<long> alternatives,
        string timeZone, HistoryQuery query, CancellationToken ct)
    {
        var rows = await RawRowsAsync(db, series.SeriesId, query.From.UtcDateTime, query.To.UtcDateTime, query.After?.UtcDateTime,
            query.Limit + 1, ct);
        var page = rows.Take(query.Limit).Select(row => Point(row, series.Source)).ToList();
        return new(series, alternatives, HistoryResolution.Raw, query.From, query.To, timeZone, IncompleteBelowCoverage, page, null,
            rows.Count > query.Limit ? page[^1].ObservedAt : null);
    }

    private async Task<(List<LogicalRawPoint> Points, DateTimeOffset? Next)> LogicalRawAsync(MySqlConnection db,
        IReadOnlyList<(LogicalHistorySegment Segment, HistorySeriesInfo? Series, TimeWindow Interval, AssignmentRow Assignment)> resolved,
        HistoryQuery query, CancellationToken ct)
    {
        var points = new List<LogicalRawPoint>();
        foreach (var item in resolved)
        {
            if (item.Series is null) continue;
            var from = Max(query.From.UtcDateTime, item.Interval.Start);
            var to = Min(query.To.UtcDateTime, item.Interval.End);
            if (to <= from || query.After?.UtcDateTime >= to) continue;
            var rows = await RawRowsAsync(db, item.Series.SeriesId, from, to, query.After?.UtcDateTime, query.Limit + 1 - points.Count, ct);
            points.AddRange(rows.Select(row =>
            {
                var point = Point(row, item.Series.Source);
                return new LogicalRawPoint(point.ObservedAt, point.ReceivedAt, point.Number, point.Flag, point.Code, point.TimestampSource,
                    item.Series.SeriesId, item.Assignment.Id);
            }));
            if (points.Count > query.Limit) break;
        }
        var page = points.Take(query.Limit).ToList();
        return (page, points.Count > query.Limit ? page[^1].ObservedAt : null);
    }

    private async Task<List<RawRow>> RawRowsAsync(MySqlConnection db, long seriesId, DateTime from, DateTime to, DateTime? after,
        int take, CancellationToken ct)
    {
        var provenance = await migrations.VerifyEwelinkReceiveTimeAsync(ct) ? "timestamp_source" : "CAST(NULL AS CHAR(24))";
        return (await db.QueryAsync<RawRow>(Query($"""
            SELECT observed_at_utc AS ObservedAt, received_at_utc AS ReceivedAt, numeric_value AS Number, boolean_value AS Flag,
                   status_value AS Code, {provenance} AS TimestampSource
            FROM telemetry_samples
            WHERE series_id=@seriesId AND observed_at_utc >= @from AND observed_at_utc < @to AND (@after IS NULL OR observed_at_utc > @after)
            ORDER BY observed_at_utc LIMIT @take
            """, ct, new { seriesId, from, to, after, take }))).AsList();
    }

    private static RawHistoryPoint Point(RawRow row, string source) => new(Utc(row.ObservedAt), Utc(row.ReceivedAt), row.Number,
        row.Flag, row.Code, row.TimestampSource ?? (source == "simulator" ? "simulated" : "provider"));

    private async Task<IReadOnlyList<HistoryBucket>> BucketsAsync(MySqlConnection db, Metric metric, IReadOnlyList<SegmentSpec> segments,
        IReadOnlyList<(TimeWindow Window, DateOnly? Date)> windows, DateTime now, CancellationToken ct,
        IReadOnlyList<TimeWindow>? assigned = null, IReadOnlyList<TimeWindow>? unavailable = null)
    {
        if (windows.Count == 0) return [];
        var plans = new IReadOnlyList<HistoryPart>[segments.Count][];
        var rows = new Dictionary<DateTime, HistoryAggregate>[segments.Count];
        var computed = new Dictionary<DateTime, HistoryAggregate>[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var aggregatedFrom = segment.Series.AggregatedFrom?.UtcDateTime;
            var aggregatedTo = segment.Series.AggregatedTo?.UtcDateTime;
            var lookback = TelemetryAggregation.Lookback(metric, segment.Policy);
            var lookahead = TelemetryAggregation.Lookahead(metric, segment.Policy);
            plans[index] = windows.Select(item => Cap(SegmentPlanner.Split(item.Window, segment.Interval, aggregatedFrom, aggregatedTo,
                lookback, lookahead, now), aggregatedTo ?? CalendarDays.FloorHour(now), now - LiveRawLimit)).ToArray();
            var parts = plans[index].SelectMany(plan => plan).ToList();

            rows[index] = [];
            var hourly = parts.Where(part => part.Source == HistoryPartSource.Hourly).Select(part => part.Window.Start).ToList();
            if (hourly.Count > 0)
            {
                var found = await db.QueryAsync<TelemetryRollups.RollupRow>(Query(TelemetryRollups.RollupColumns + """
                     FROM telemetry_rollups_hourly WHERE series_id=@SeriesId AND bucket_start_utc >= @first AND bucket_start_utc <= @last
                    """, ct, new { segment.Series.SeriesId, first = hourly.Min(), last = hourly.Max() }));
                foreach (var row in found)
                {
                    var aggregate = row.ToAggregate();
                    rows[index][aggregate.Start] = aggregate;
                }
            }

            computed[index] = [];
            var raw = parts.Where(part => part.Source == HistoryPartSource.Raw).Select(part => part.Window).ToList();
            if (raw.Count > 0)
            {
                var samples = (await ReadRangesAsync(db, segment.Series.SeriesId, raw, lookback, lookahead, ct))
                    .Where(sample => sample.At >= segment.Interval.Start && sample.At < segment.Interval.End).ToList();
                foreach (var aggregate in TelemetryAggregation.Summarize(metric, segment.Policy, samples, raw, now))
                    computed[index][aggregate.Start] = aggregate;
            }
        }

        var buckets = new List<HistoryBucket>(windows.Count);
        for (var window = 0; window < windows.Count; window++)
        {
            var (span, date) = windows[window];
            var parts = new List<HistoryAggregate>();
            var missing = false;
            var series = new List<long>();
            for (var index = 0; index < segments.Count; index++)
            {
                if (plans[index][window].Count == 0) continue;
                series.Add(segments[index].Series.SeriesId);
                foreach (var part in plans[index][window])
                {
                    switch (part.Source)
                    {
                        case HistoryPartSource.Hourly:
                            parts.Add(rows[index].GetValueOrDefault(part.Window.Start) ?? HistoryAggregate.Empty(part.Window.Start, part.Window.End));
                            break;
                        case HistoryPartSource.Raw:
                            parts.Add(computed[index][part.Window.Start]);
                            break;
                        default:
                            missing = true;
                            break;
                    }
                }
            }
            var extra = new List<string>();
            if (assigned is not null)
            {
                var elapsedEnd = span.End < now ? span.End : now;
                var covered = assigned.Sum(interval => Math.Max(0,
                    ((interval.End < elapsedEnd ? interval.End : elapsedEnd) - (interval.Start > span.Start ? interval.Start : span.Start)).TotalSeconds));
                if (covered + 0.000_001 < (elapsedEnd - span.Start).TotalSeconds) extra.Add("unassigned");
                if (assigned.Count(interval => interval.Start < elapsedEnd && interval.End > span.Start) > 1) extra.Add("deviceReplaced");
                if (unavailable?.Any(interval => interval.Start < elapsedEnd && interval.End > span.Start) == true) extra.Add("notReported");
            }
            buckets.Add(Bucket(metric, span, date, HistoryAggregate.Combine(span.Start, span.End, parts), missing, now, extra,
                series.Distinct().ToList()));
        }
        return buckets;
    }

    private HistoryBucket Bucket(Metric metric, TimeWindow window, DateOnly? date, HistoryAggregate aggregate, bool notAggregated,
        DateTime now, IReadOnlyList<string> extra, IReadOnlyList<long> series)
    {
        var elapsed = ((window.End < now ? window.End : now) - window.Start).TotalSeconds;
        var coverage = elapsed > 0 ? Math.Min(1, aggregate.CoveredSeconds / elapsed) : 0;
        var noData = aggregate.SampleCount == 0 && aggregate.CoveredSeconds <= 0 && aggregate.MeasuredWh is null;
        var quality = notAggregated ? "notAggregated" : noData ? "noData" : coverage < IncompleteBelowCoverage ? "incomplete" : "complete";
        var flags = Enum.GetValues<AggregateFlags>().Where(flag => flag != AggregateFlags.None && aggregate.Flags.HasFlag(flag))
            .Select(flag => char.ToLowerInvariant(flag.ToString()[0]) + flag.ToString()[1..]).ToList();
        if (window.End > now) flags.Add("partialPeriod");
        flags.AddRange(extra);
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
            window.End > now.AddHours(-options.RecomputeHours), flags, series);
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
            samples.AddRange(rows.Select(row => new RawObservation(Kind(row.At), row.Number, row.Flag)));
        }
        return samples;
    }

    private static Task<string> TimeZoneAsync(MySqlConnection db, long propertyId, CancellationToken ct) =>
        db.QuerySingleAsync<string>(Query("SELECT time_zone FROM properties WHERE id=@propertyId", ct, new { propertyId }));

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
            """, ct, new { propertyId }))).Where(row => holds.Shows(row.Source)).ToList();
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

    private static DateTime Kind(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;

    internal sealed record SeriesRow(long Id, string MetricName, string Source, long? EndpointId, string? EndpointName,
        long? DeviceId, string? DeviceName, long? InstallationId, string? InstallationName, DateTime? RolledFrom, DateTime? RolledTo)
    {
        public Metric? Metric => Enum.TryParse<Metric>(MetricName, out var metric) && Enum.IsDefined(metric) ? metric : null;
    }

    private sealed record SegmentSpec(HistorySeriesInfo Series, AggregationPolicy Policy, TimeWindow Interval, long? AssignmentId);
    private sealed record AssignmentRow(long Id, long DeviceId, string DeviceName, long? EndpointId, DateTime ValidFrom, DateTime? ValidTo);
    private sealed record RawRow(DateTime ObservedAt, DateTime ReceivedAt, double? Number, bool? Flag, string? Code, string? TimestampSource);
    private sealed record SampleRow(DateTime At, double? Number, bool? Flag);
    private sealed class HistoryRangeTooLargeException : Exception;
}
