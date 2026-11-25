using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Api;

/// <summary>Provider-neutral history routes. They inherit the property membership filter of the monitoring group.</summary>
internal static class HistoryEndpoints
{
    internal static readonly TimeSpan MaximumRange = TimeSpan.FromDays(31);
    internal const int MaximumDays = 366;
    internal const int DefaultLimit = 1000;
    internal const int MaximumLimit = 5000;

    public static void MapHistory(this RouteGroupBuilder property)
    {
        property.MapGet("/history/series", SeriesAsync);
        property.MapGet("/history", ReadAsync);
    }

    private static async Task<IResult> SeriesAsync(long propertyId, long? deviceId, string? metric, ITelemetryHistory history,
        CancellationToken cancellationToken)
    {
        Metric? parsed = null;
        if (metric is not null)
        {
            if (!TryMetric(metric, out var value)) return Invalid("Use a supported metric.");
            parsed = value;
        }
        if (deviceId is <= 0) return Invalid("Use a positive device identifier.");
        return Results.Ok(new { series = await history.SeriesAsync(propertyId, deviceId, parsed, cancellationToken) });
    }

    private static async Task<IResult> ReadAsync(long propertyId, long? seriesId, long? deviceId, long? endpointId,
        long? installationId, string? metric, string? resolution, DateTimeOffset? from, DateTimeOffset? to, DateOnly? fromDate,
        DateOnly? toDate, DateTimeOffset? after, int? limit, TimeProvider clock, ITelemetryHistory history,
        CancellationToken cancellationToken)
    {
        if (Validate(seriesId, deviceId, endpointId, installationId, metric, resolution, from, to, fromDate, toDate, after, limit,
                clock.GetUtcNow(), out var query) is { } problem)
            return problem;
        var read = await history.ReadAsync(propertyId, query!, cancellationToken);
        return read.Status switch
        {
            HistoryReadStatus.Found => Results.Ok(read.Result),
            HistoryReadStatus.NotFound => Results.NotFound(),
            HistoryReadStatus.Ambiguous => Results.Problem(statusCode: 400,
                title: "The device reports this metric on more than one endpoint. Select an endpoint or series.",
                extensions: new Dictionary<string, object?> { ["candidates"] = read.Candidates }),
            HistoryReadStatus.Unsupported => Invalid("Status metrics are available at raw resolution only."),
            HistoryReadStatus.InvalidTimeZone => Results.Problem(statusCode: 409,
                title: "The property or counter time zone is not recognized on this server."),
            HistoryReadStatus.TooLarge => Invalid("The selected range needs too many raw observations. Request a shorter range."),
            HistoryReadStatus.SchemaPending => Results.Problem(statusCode: 503,
                title: "Historical aggregation requires migration 017."),
            _ => Results.Problem(statusCode: 500)
        };
    }

    internal static IResult? Validate(long? seriesId, long? deviceId, long? endpointId, long? installationId, string? metric,
        string? resolution, DateTimeOffset? from, DateTimeOffset? to, DateOnly? fromDate, DateOnly? toDate, DateTimeOffset? after,
        int? limit, DateTimeOffset now, out HistoryQuery? query)
    {
        query = null;
        var selectors = new[] { seriesId, deviceId, endpointId, installationId };
        if (selectors.Count(value => value is not null) != 1 || selectors.Any(value => value is <= 0))
            return Invalid("Select exactly one positive seriesId, deviceId, endpointId or installationId.");
        Metric? parsedMetric = null;
        if (metric is not null)
        {
            if (!TryMetric(metric, out var value)) return Invalid("Use a supported metric.");
            parsedMetric = value;
        }
        if (seriesId is null && parsedMetric is null) return Invalid("Select a metric with a device, endpoint or installation.");
        HistoryResolution parsedResolution;
        switch (resolution?.ToLowerInvariant())
        {
            case null or "hour": parsedResolution = HistoryResolution.Hour; break;
            case "raw": parsedResolution = HistoryResolution.Raw; break;
            case "day": parsedResolution = HistoryResolution.Day; break;
            default: return Invalid("Use resolution raw, hour or day.");
        }
        if (parsedResolution != HistoryResolution.Raw && (after is not null || limit is not null))
            return Invalid("after and limit apply to raw resolution only.");
        var selector = new HistorySelector(seriesId, deviceId, endpointId, installationId, parsedMetric);

        if (parsedResolution == HistoryResolution.Day)
        {
            if (from is not null || to is not null || fromDate is null || toDate is null)
                return Invalid("Daily history uses fromDate and toDate as local calendar dates.");
            if (toDate < fromDate || toDate.Value.DayNumber - fromDate.Value.DayNumber + 1 > MaximumDays)
                return Invalid($"Select between 1 and {MaximumDays} calendar days.");
            query = new(selector, parsedResolution, now, now, fromDate, toDate);
            return null;
        }

        if (fromDate is not null || toDate is not null) return Invalid("fromDate and toDate apply to daily resolution only.");
        var end = to ?? now;
        var start = from ?? end.AddHours(-24);
        if (start >= end || end - start > MaximumRange)
            return Invalid($"Use a time range greater than zero and at most {MaximumRange.TotalDays:0} days.");
        if (after is { } cursor && (cursor < start || cursor >= end)) return Invalid("after must lie within the requested range.");
        if (limit is < 1 or > MaximumLimit) return Invalid($"Use a limit between 1 and {MaximumLimit}.");
        query = new(selector, parsedResolution, start, end, null, null, after, limit ?? DefaultLimit);
        return null;
    }

    private static bool TryMetric(string metric, out Metric parsed) =>
        Enum.TryParse(metric, true, out parsed) && Enum.IsDefined(parsed) &&
        string.Equals(metric, parsed.ToString(), StringComparison.OrdinalIgnoreCase);

    private static IResult Invalid(string title) => Results.Problem(statusCode: 400, title: title);
}
