using HomeAutomation.Domain;

namespace HomeAutomation.Application;

public enum HistoryResolution { Raw, Hour, Day }

/// <summary>Identifies one physical series directly or by its subject and metric.</summary>
public sealed record HistorySelector(long? SeriesId, long? DeviceId, long? EndpointId, long? InstallationId, Metric? Metric);

public sealed record HistoryQuery(
    HistorySelector Selector,
    HistoryResolution Resolution,
    DateTimeOffset From,
    DateTimeOffset To,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    DateTimeOffset? After = null,
    int Limit = 1000);

/// <summary>
/// One physical telemetry series. Hourly and daily history never combines different series. When a subject reports
/// a metric through more than one source stream, <see cref="Preferred"/> marks the stream that a selector by subject
/// and metric returns.
/// </summary>
public sealed record HistorySeriesInfo(
    long SeriesId,
    Metric Metric,
    string? Unit,
    string Source,
    long? DeviceId,
    string? DeviceName,
    long? EndpointId,
    string? EndpointName,
    long? InstallationId,
    string? InstallationName,
    MetricAggregation Aggregation,
    string? AverageKind,
    string EnergyBasis,
    bool Preferred,
    double MaximumHoldSeconds,
    DateTimeOffset? AggregatedFrom,
    DateTimeOffset? AggregatedTo);

public sealed record RawHistoryPoint(
    DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt, double? Number, bool? Flag, string? Code, string TimestampSource);

/// <summary>Energy for one bucket. Measured and estimated values are never combined.</summary>
/// <param name="Status">measured, estimated, incomplete or noData.</param>
/// <param name="Basis">counter or powerIntegration.</param>
public sealed record HistoryEnergy(string Status, string Basis, double? Wh, double? PositiveWh, double? NegativeWh);

/// <summary>
/// One hourly or daily bucket. <see cref="Average"/> is time-weighted over covered seconds and <see cref="Mean"/> is the
/// arithmetic mean of observations recorded inside the bucket. Coverage is covered seconds divided by the elapsed duration.
/// </summary>
public sealed record HistoryBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    DateOnly? Date,
    double DurationSeconds,
    string Quality,
    double Coverage,
    double CoveredSeconds,
    int SampleCount,
    double? Minimum,
    double? Maximum,
    double? Average,
    double? Mean,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,
    double? LastValue,
    double? OnSeconds,
    int? Transitions,
    HistoryEnergy? Energy,
    bool Provisional,
    IReadOnlyList<string> Flags);

public sealed record HistoryResult(
    HistorySeriesInfo Series,
    IReadOnlyList<long> AlternativeSeriesIds,
    HistoryResolution Resolution,
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    double IncompleteBelowCoverage,
    IReadOnlyList<RawHistoryPoint>? Points,
    IReadOnlyList<HistoryBucket>? Buckets,
    DateTimeOffset? NextAfter);

public enum HistoryReadStatus { Found, NotFound, Ambiguous, Unsupported, InvalidTimeZone, TooLarge, SchemaPending }

public sealed record HistoryRead(HistoryReadStatus Status, HistoryResult? Result = null, IReadOnlyList<long>? Candidates = null)
{
    public static HistoryRead Of(HistoryReadStatus status, IReadOnlyList<long>? candidates = null) => new(status, null, candidates);
}

public interface ITelemetryHistory
{
    Task<IReadOnlyList<HistorySeriesInfo>> SeriesAsync(long propertyId, long? deviceId, Metric? metric, CancellationToken ct);
    Task<HistoryRead> ReadAsync(long propertyId, HistoryQuery query, CancellationToken ct);
}
