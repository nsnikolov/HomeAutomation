using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class TelemetryHistoryOptions
{
    public bool RollupsEnabled { get; set; } = true;
    public int RollupIntervalMinutes { get; set; } = 15;
    public int RecomputeHours { get; set; } = 72;
    public int CatchUpDays { get; set; } = 7;

    public bool IsValid => RollupIntervalMinutes is >= 1 and <= 1440 && RecomputeHours is >= 1 and <= 720 &&
        CatchUpDays is >= 1 and <= 31;
}

/// <summary>
/// Maximum observation hold per source and metric. Each value reuses the freshness window that the live views
/// already apply to the same provider, so history and the current state agree on how long a reading stays valid.
/// </summary>
public sealed class TelemetryHoldPolicy(GrowattOptions growatt, DaikinOptions daikin, EwelinkOptions ewelink,
    TuyaOptions tuya, SimulatorOptions simulator)
{
    public static readonly TimeSpan Fallback = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    public TimeSpan Hold(string source, Metric metric) => Clamp(source switch
    {
        "growatt" => TimeSpan.FromSeconds(growatt.FreshnessSeconds),
        "daikin" => TimeSpan.FromMinutes(daikin.FreshMinutes),
        "ewelink" when EwelinkElectricalReporting.IsElectrical(metric) => EwelinkSocketProtocol.Freshness,
        "ewelink" => TimeSpan.FromMinutes(ewelink.PollMinutes + 2),
        "tuya" or TuyaService.SensorReportSource => TimeSpan.FromMinutes(tuya.PollMinutes * 2 + 5),
        "simulator" => TimeSpan.FromSeconds(simulator.FreshnessSeconds),
        _ => Fallback
    });

    /// <summary>Growatt daily counters restart at the inverter-local midnight used by the existing daily-counter freshness rule.</summary>
    public TimeZoneInfo? CounterZone(string source, TimeZoneInfo? propertyZone) =>
        source == "growatt" && Zone(growatt.TimeZone) is { } inverter ? inverter : propertyZone;

    public AggregationPolicy Policy(string source, Metric metric, TimeZoneInfo? propertyZone) =>
        new(Hold(source, metric), TelemetryAggregation.Kind(metric) == MetricAggregation.DailyResetCounter
            ? CounterZone(source, propertyZone) : null);

    public static TimeZoneInfo? Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    private static TimeSpan Clamp(TimeSpan value) => value < Minimum ? Minimum : value > Maximum ? Maximum : value;
}

public sealed record RollupRun(bool Skipped, int Series, int Buckets, int Written, int Failed);

public sealed record BackfillRequest(long PropertyId, IReadOnlyList<long>? SeriesIds, DateTimeOffset? From, DateTimeOffset? To);

public sealed record BackfillSeriesPlan(long SeriesId, Metric Metric, string Source, DateTime? From, DateTime? To,
    long Samples, long Hours, long ExistingRows, DateTime? AggregatedFrom, DateTime? AggregatedTo, string? Note);

public sealed record BackfillPlan(long PropertyId, bool PropertyExists, IReadOnlyList<BackfillSeriesPlan> Series,
    IReadOnlyList<long> Rejected)
{
    public IEnumerable<BackfillSeriesPlan> Work => Series.Where(item => item.Note is null or ContiguityNote);
    public long Samples => Work.Sum(item => item.Samples);
    public long Hours => Work.Sum(item => item.Hours);
    internal const string ContiguityNote = "range ends before the aggregated range starts; history stays notAggregated until the gap is filled";
}

public sealed record BackfillOutcome(int SeriesCompleted, int Chunks, int Written, bool Cancelled);

internal interface ITelemetryRollupRunner
{
    Task<bool> SchemaReadyAsync(CancellationToken ct);
    Task<RollupRun> RefreshRecentAsync(CancellationToken ct);
}

/// <summary>Materializes hourly rollups from <c>telemetry_samples</c>. Raw samples are only read, never changed.</summary>
public sealed class TelemetryRollups(Database database, Migrations migrations, TelemetryHoldPolicy holds,
    TelemetryHistoryOptions options, TimeProvider clock, ILogger<TelemetryRollups>? logger = null) : ITelemetryRollupRunner
{
    internal const string LockName = "homeautomation:telemetry-rollups";
    private const int SeriesBatch = 100;
    private static readonly TimeSpan BackfillChunk = TimeSpan.FromDays(7);

    /// <summary>Test seam invoked before each rollup transaction commits.</summary>
    internal Func<long, CancellationToken, Task>? BeforeCommit { get; set; }

    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyTelemetryHistoryAsync(ct);

    /// <summary>Recomputes the configured recent window and catches up after downtime. Skips when another run holds the lock.</summary>
    public async Task<RollupRun> RefreshRecentAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        if (!await TryLockAsync(connection, 0, ct)) return new(true, 0, 0, 0, 0);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var end = CalendarDays.FloorHour(now);
            var recent = end.AddHours(-options.RecomputeHours);
            int series = 0, buckets = 0, written = 0, failed = 0;
            long after = 0;
            while (true)
            {
                var batch = await LoadSeriesAsync(connection, null, after, ct);
                if (batch.Count == 0) break;
                foreach (var item in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    after = item.Id;
                    if (item.Metric is not { } metric || !TelemetryAggregation.IsAggregated(metric)) continue;
                    var behind = item.RolledTo is { } rolled && rolled < recent;
                    var from = behind ? item.RolledTo!.Value : recent;
                    var to = behind && from.AddDays(options.CatchUpDays) < end ? from.AddDays(options.CatchUpDays) : end;
                    if (from >= to) continue;
                    try
                    {
                        written += await RecomputeAsync(connection, item, metric, from, to, now, ct);
                        buckets += (int)((to - from).Ticks / TimeSpan.TicksPerHour);
                        series++;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidTimeZoneException)
                    {
                        failed++;
                        logger?.LogWarning("Telemetry rollup skipped series {SeriesId} ({FailureType}).", item.Id, error.GetType().Name);
                    }
                }
            }
            return new(false, series, buckets, written, failed);
        }
        finally
        {
            await ReleaseAsync(connection);
        }
    }

    public async Task<BackfillPlan> PlanBackfillAsync(BackfillRequest request, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(Query(
            "SELECT EXISTS(SELECT 1 FROM properties WHERE id=@PropertyId)", ct, request));
        if (!exists) return new(request.PropertyId, false, [], request.SeriesIds ?? []);
        var all = new List<SeriesRow>();
        long after = 0;
        while (true)
        {
            var batch = await LoadSeriesAsync(connection, request.PropertyId, after, ct);
            if (batch.Count == 0) break;
            all.AddRange(batch);
            after = batch[^1].Id;
        }
        var selected = request.SeriesIds is { Count: > 0 } ids ? all.Where(item => ids.Contains(item.Id)).ToList() : all;
        var rejected = (request.SeriesIds ?? []).Where(id => selected.All(item => item.Id != id)).ToList();
        var nowHour = CalendarDays.FloorHour(clock.GetUtcNow().UtcDateTime);
        var plans = new List<BackfillSeriesPlan>();
        foreach (var item in selected)
        {
            if (item.Metric is not { } metric || !TelemetryAggregation.IsAggregated(metric))
            {
                if (request.SeriesIds is { Count: > 0 }) rejected.Add(item.Id);
                continue;
            }
            var earliest = await connection.ExecuteScalarAsync<DateTime?>(Query(
                "SELECT MIN(observed_at_utc) FROM telemetry_samples WHERE series_id=@Id", ct, new { item.Id }));
            if (earliest is null)
            {
                plans.Add(new(item.Id, metric, item.Source, null, null, 0, 0, 0, item.RolledFrom, item.RolledTo,
                    "no raw observations were collected; history cannot be reconstructed"));
                continue;
            }
            var requestedFrom = request.From?.UtcDateTime;
            var from = CalendarDays.FloorHour(requestedFrom is { } start && start > earliest ? start : earliest.Value);
            var requestedTo = request.To?.UtcDateTime ?? item.RolledFrom ?? nowHour;
            var to = CalendarDays.FloorHour(requestedTo < nowHour ? requestedTo : nowHour);
            if (from >= to)
            {
                plans.Add(new(item.Id, metric, item.Source, from, to, 0, 0, 0, item.RolledFrom, item.RolledTo,
                    "nothing to aggregate in the selected range"));
                continue;
            }
            var counts = await connection.QuerySingleAsync<(long Samples, long Rows)>(Query("""
                SELECT (SELECT COUNT(*) FROM telemetry_samples WHERE series_id=@Id AND observed_at_utc >= @from AND observed_at_utc < @to),
                       (SELECT COUNT(*) FROM telemetry_rollups_hourly WHERE series_id=@Id AND bucket_start_utc >= @from AND bucket_start_utc < @to)
                """, ct, new { item.Id, from, to }));
            var note = item.RolledFrom is { } rolledFrom && to < rolledFrom ? BackfillPlan.ContiguityNote : null;
            plans.Add(new(item.Id, metric, item.Source, from, to, counts.Samples, (to - from).Ticks / TimeSpan.TicksPerHour,
                counts.Rows, item.RolledFrom, item.RolledTo, note));
        }
        return new(request.PropertyId, true, plans, rejected);
    }

    /// <summary>
    /// Aggregates the planned ranges newest first in seven-day chunks. Each chunk commits on its own, so an interrupted
    /// run keeps completed chunks and a rerun continues from the remaining range.
    /// </summary>
    public async Task<BackfillOutcome> ExecuteBackfillAsync(BackfillPlan plan, CancellationToken ct)
    {
        int completed = 0, chunks = 0, written = 0;
        await using var connection = await database.OpenAsync(CancellationToken.None);
        foreach (var item in plan.Work)
        {
            if (item.From is not { } from || item.To is not { } to) continue;
            var end = to;
            while (end > from)
            {
                if (ct.IsCancellationRequested) return new(completed, chunks, written, true);
                var start = end - BackfillChunk > from ? end - BackfillChunk : from;
                if (!await TryLockAsync(connection, 60, ct))
                    throw new InvalidOperationException("Another telemetry rollup run holds the lock.");
                try
                {
                    var series = (await LoadSeriesAsync(connection, plan.PropertyId, item.SeriesId - 1, ct, item.SeriesId)).Single();
                    written += await RecomputeAsync(connection, series, item.Metric, start, end,
                        clock.GetUtcNow().UtcDateTime, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new(completed, chunks, written, true);
                }
                finally
                {
                    await ReleaseAsync(connection);
                }
                chunks++;
                logger?.LogInformation("Telemetry backfill aggregated series {SeriesId} from {From:o} to {To:o}.", item.SeriesId, start, end);
                end = start;
            }
            completed++;
        }
        return new(completed, chunks, written, false);
    }

    /// <summary>Recomputes hourly buckets in [from, to) for one series and returns the number of rows written.</summary>
    internal async Task<int> RecomputeAsync(MySqlConnection connection, SeriesRow series, Metric metric,
        DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        var policy = holds.Policy(series.Source, metric, TelemetryHoldPolicy.Zone(series.TimeZone));
        var windows = new List<TimeWindow>();
        for (var hour = from; hour < to; hour = hour.AddHours(1)) windows.Add(new(hour, hour.AddHours(1)));
        var samples = await ReadSamplesAsync(connection, null, series.Id,
            from - TelemetryAggregation.Lookback(metric, policy), to + TelemetryAggregation.Lookahead(metric, policy), ct);
        var aggregates = TelemetryAggregation.Summarize(metric, policy, samples, windows, now);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        var state = await connection.QuerySingleOrDefaultAsync<StateRow>(Query("""
            SELECT rolled_from_utc AS RolledFrom, rolled_to_utc AS RolledTo FROM telemetry_rollup_state
            WHERE series_id=@Id FOR UPDATE
            """, ct, new { series.Id }, transaction));
        var existing = (await connection.QueryAsync<RollupRow>(Query(RollupColumns + """
             FROM telemetry_rollups_hourly WHERE series_id=@Id AND bucket_start_utc >= @from AND bucket_start_utc < @to FOR UPDATE
            """, ct, new { series.Id, from, to }, transaction))).ToDictionary(row => row.BucketStart);
        var changes = new List<object>();
        foreach (var aggregate in aggregates)
        {
            var found = existing.TryGetValue(aggregate.Start, out var row);
            if (!found && aggregate.IsEmpty) continue;
            if (found && row!.ToAggregate() == aggregate) continue;
            changes.Add(new
            {
                SeriesId = series.Id, aggregate.Start, aggregate.SampleCount, aggregate.CoveredSeconds,
                aggregate.Minimum, aggregate.Maximum, Average = aggregate.TimeWeightedAverage, aggregate.ValueSum,
                First = aggregate.FirstObservedAt, Last = aggregate.LastObservedAt, aggregate.LastValue, aggregate.OnSeconds,
                Transitions = aggregate.TransitionCount, aggregate.MeasuredWh, Positive = aggregate.EstimatedPositiveWh,
                Negative = aggregate.EstimatedNegativeWh, Flags = (int)aggregate.Flags, Now = now
            });
        }
        if (changes.Count > 0)
            await connection.ExecuteAsync(Query("""
                INSERT INTO telemetry_rollups_hourly (series_id,bucket_start_utc,sample_count,covered_seconds,min_value,max_value,
                    time_weighted_avg,value_sum,first_observed_utc,last_observed_utc,last_numeric_value,on_seconds,transition_count,
                    measured_wh,estimated_positive_wh,estimated_negative_wh,quality_flags,computed_at_utc)
                VALUES (@SeriesId,@Start,@SampleCount,@CoveredSeconds,@Minimum,@Maximum,@Average,@ValueSum,@First,@Last,
                    @LastValue,@OnSeconds,@Transitions,@MeasuredWh,@Positive,@Negative,@Flags,@Now) AS incoming
                ON DUPLICATE KEY UPDATE sample_count=incoming.sample_count, covered_seconds=incoming.covered_seconds,
                    min_value=incoming.min_value, max_value=incoming.max_value, time_weighted_avg=incoming.time_weighted_avg,
                    value_sum=incoming.value_sum, first_observed_utc=incoming.first_observed_utc,
                    last_observed_utc=incoming.last_observed_utc, last_numeric_value=incoming.last_numeric_value,
                    on_seconds=incoming.on_seconds,
                    transition_count=incoming.transition_count, measured_wh=incoming.measured_wh,
                    estimated_positive_wh=incoming.estimated_positive_wh, estimated_negative_wh=incoming.estimated_negative_wh,
                    quality_flags=incoming.quality_flags, computed_at_utc=incoming.computed_at_utc
                """, ct, changes, transaction));

        // The state records the contiguous range whose hours are materialized. Disjoint ranges leave it unchanged.
        if (state is null)
            await connection.ExecuteAsync(Query("""
                INSERT INTO telemetry_rollup_state (series_id,rolled_from_utc,rolled_to_utc,updated_at_utc)
                VALUES (@Id,@from,@to,@now)
                """, ct, new { series.Id, from, to, now }, transaction));
        else if (from <= state.RolledTo && to >= state.RolledFrom)
            await connection.ExecuteAsync(Query("""
                UPDATE telemetry_rollup_state SET rolled_from_utc=@start, rolled_to_utc=@end, updated_at_utc=@now WHERE series_id=@Id
                """, ct, new
                {
                    series.Id, now,
                    start = from < state.RolledFrom ? from : state.RolledFrom,
                    end = to > state.RolledTo ? to : state.RolledTo
                }, transaction));
        if (BeforeCommit is { } hook) await hook(series.Id, ct);
        await transaction.CommitAsync(ct);
        return changes.Count;
    }

    internal static async Task<List<RawObservation>> ReadSamplesAsync(MySqlConnection connection, MySqlTransaction? transaction,
        long seriesId, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<SampleRow>(Query("""
            SELECT observed_at_utc AS At, numeric_value AS Number, boolean_value AS Flag
            FROM telemetry_samples WHERE series_id=@seriesId AND observed_at_utc >= @from AND observed_at_utc < @to
            ORDER BY observed_at_utc
            """, ct, new { seriesId, from, to }, transaction));
        return rows.Select(row => new RawObservation(DateTime.SpecifyKind(row.At, DateTimeKind.Utc), row.Number, row.Flag)).ToList();
    }

    internal const string RollupColumns = """
        SELECT bucket_start_utc AS BucketStart, CAST(sample_count AS SIGNED) AS SampleCount, covered_seconds AS CoveredSeconds,
            min_value AS Minimum, max_value AS Maximum, time_weighted_avg AS Average, value_sum AS ValueSum,
            first_observed_utc AS FirstObserved, last_observed_utc AS LastObserved, last_numeric_value AS LastValue,
            on_seconds AS OnSeconds, CAST(transition_count AS SIGNED) AS Transitions, measured_wh AS MeasuredWh,
            estimated_positive_wh AS PositiveWh, estimated_negative_wh AS NegativeWh, CAST(quality_flags AS SIGNED) AS Flags
        """;

    private static async Task<List<SeriesRow>> LoadSeriesAsync(MySqlConnection connection, long? property, long after,
        CancellationToken ct, long? only = null)
    {
        var rows = await connection.QueryAsync<SeriesRow>(Query("""
            SELECT s.id AS Id, s.property_id AS PropertyId, s.metric AS MetricName, s.source_key AS Source, p.time_zone AS TimeZone,
                   st.rolled_from_utc AS RolledFrom, st.rolled_to_utc AS RolledTo
            FROM telemetry_series s JOIN properties p ON p.id=s.property_id
            LEFT JOIN telemetry_rollup_state st ON st.series_id=s.id
            WHERE s.id > @after AND (@property IS NULL OR s.property_id=@property) AND (@only IS NULL OR s.id=@only)
            ORDER BY s.id LIMIT @limit
            """, ct, new { after, property, only, limit = SeriesBatch }));
        return rows.AsList();
    }

    // MySQL named locks are server-wide and limited to 64 characters. A schema hash suffix keeps separate schemas on one
    // server independent: "homeautomation:telemetry-rollups:" (32) + 24 hex characters.
    private const string ScopedLock = "CONCAT(@name, ':', LEFT(SHA2(DATABASE(), 256), 24))";

    private static async Task<bool> TryLockAsync(MySqlConnection connection, int seconds, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int?>(Query($"SELECT GET_LOCK({ScopedLock}, @seconds)", ct, new { name = LockName, seconds })) == 1;

    private static Task ReleaseAsync(MySqlConnection connection) =>
        connection.ExecuteScalarAsync<int?>(Query($"SELECT RELEASE_LOCK({ScopedLock})", CancellationToken.None, new { name = LockName }));

    internal sealed record SeriesRow(long Id, long PropertyId, string MetricName, string Source, string TimeZone,
        DateTime? RolledFrom, DateTime? RolledTo)
    {
        public Metric? Metric => Enum.TryParse<Metric>(MetricName, out var metric) && Enum.IsDefined(metric) ? metric : null;
    }

    private sealed record StateRow(DateTime RolledFrom, DateTime RolledTo);
    private sealed record SampleRow(DateTime At, double? Number, bool? Flag);

    internal sealed record RollupRow(DateTime BucketStart, long SampleCount, double CoveredSeconds, double? Minimum, double? Maximum,
        double? Average, double? ValueSum, DateTime? FirstObserved, DateTime? LastObserved, double? LastValue, double? OnSeconds,
        long Transitions, double? MeasuredWh, double? PositiveWh, double? NegativeWh, long Flags)
    {
        public HistoryAggregate ToAggregate()
        {
            var start = DateTime.SpecifyKind(BucketStart, DateTimeKind.Utc);
            return new(start, start.AddHours(1), (int)SampleCount, CoveredSeconds, Minimum, Maximum, Average, ValueSum,
                Kind(FirstObserved), Kind(LastObserved), LastValue, OnSeconds, (int)Transitions, MeasuredWh, PositiveWh, NegativeWh,
                (AggregateFlags)Flags);
        }

        private static DateTime? Kind(DateTime? value) => value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;
    }
}

internal sealed class TelemetryRollupWorker(ITelemetryRollupRunner rollups, TelemetryHistoryOptions options, TimeProvider clock,
    ILogger<TelemetryRollupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RollupsEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.RollupIntervalMinutes), clock);
        var waiting = false;
        do
        {
            try
            {
                if (!await rollups.SchemaReadyAsync(stoppingToken))
                {
                    if (!waiting) logger.LogInformation("Telemetry rollups are waiting for migration 017. Raw ingestion continues.");
                    waiting = true;
                    continue;
                }
                waiting = false;
                var run = await rollups.RefreshRecentAsync(stoppingToken);
                if (run.Skipped) logger.LogInformation("Telemetry rollup run skipped because another run holds the lock.");
                else if (run.Failed > 0)
                    logger.LogWarning("Telemetry rollups refreshed {SeriesCount} series and skipped {FailedCount}.", run.Series, run.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Telemetry rollup run deferred with {ErrorType}.", error.GetType().Name);
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

public static class TelemetryHistoryRegistration
{
    public static IServiceCollection AddTelemetryHistory(this IServiceCollection services)
    {
        services.AddOptions<TelemetryHistoryOptions>().BindConfiguration("TelemetryHistory")
            .Validate(options => options.IsValid, "Invalid TelemetryHistory rollup interval, recompute window or catch-up size.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TelemetryHistoryOptions>>().Value);
        services.AddSingleton<TelemetryHoldPolicy>();
        services.AddSingleton<TelemetryRollups>();
        services.AddSingleton<ITelemetryRollupRunner>(provider => provider.GetRequiredService<TelemetryRollups>());
        services.AddScoped<TelemetryHistoryReader>();
        services.AddScoped<ITelemetryHistory>(provider => provider.GetRequiredService<TelemetryHistoryReader>());
        services.AddScoped<LogicalDeviceStore>();
        services.AddScoped<ILogicalDevices>(provider => provider.GetRequiredService<LogicalDeviceStore>());
        services.AddHostedService<TelemetryRollupWorker>();
        return services;
    }
}
