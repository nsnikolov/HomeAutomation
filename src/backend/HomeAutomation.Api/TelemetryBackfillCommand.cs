using System.Globalization;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class TelemetryBackfillException(string message) : Exception(message);

/// <summary>
/// Explicit hourly-rollup backfill. The default is a dry run that only reads. <c>--execute</c> writes rollups in
/// seven-day chunks, newest first, so an interrupted run can be repeated safely.
/// </summary>
internal static class TelemetryBackfillCommand
{
    public const string Command = "--backfill-telemetry-rollups";
    private const string Property = "--history-property";
    private const string Series = "--history-series";
    private const string From = "--history-from";
    private const string To = "--history-to";
    private const string Execute = "--execute";

    internal sealed record Options(long PropertyId, IReadOnlyList<long>? SeriesIds, DateTimeOffset? From, DateTimeOffset? To, bool Execute);

    public static bool IsArgument(string arg) => arg is Command or Execute ||
        new[] { Property, Series, From, To }.Any(key => arg == key || arg.StartsWith(key + "=", StringComparison.Ordinal));

    public static Options? Parse(string[] args)
    {
        var arguments = args.Where(IsArgument).ToArray();
        if (arguments.All(arg => arg == Execute)) return null;
        if (!arguments.Contains(Command)) throw new TelemetryBackfillException($"Use --history-* options only with {Command}.");
        if (arguments.Count(arg => arg == Command) != 1 || arguments.Count(arg => arg == Execute) > 1)
            throw new TelemetryBackfillException("Use each backfill flag once.");
        var property = Value(Property) ?? throw new TelemetryBackfillException($"Select the property with {Property}=<id>.");
        var series = Value(Series)?.Split(',', StringSplitOptions.TrimEntries).Select(Positive).Distinct().ToArray();
        var from = Value(From) is { } start ? Instant(start) : (DateTimeOffset?)null;
        var to = Value(To) is { } end ? Instant(end) : (DateTimeOffset?)null;
        if (from >= to) throw new TelemetryBackfillException($"{From} must be earlier than {To}.");
        return new(Positive(property), series is { Length: > 0 } ? series : null, from, to, arguments.Contains(Execute));

        string? Value(string key)
        {
            var matches = arguments.Where(arg => arg == key || arg.StartsWith(key + "=", StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || matches[0].Length <= key.Length + 1)
                throw new TelemetryBackfillException("Use one value per backfill option in --name=value form.");
            return matches[0][(key.Length + 1)..];
        }
    }

    public static async Task<int> RunAsync(WebApplication app, Options options)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        var logger = app.Logger;
        try
        {
            var ct = cancellation.Token;
            if (!await app.Services.GetRequiredService<Migrations>().VerifyTelemetryHistoryAsync(ct))
            {
                logger.LogError("Telemetry backfill requires migration 017. Apply migrations with the migration account first.");
                return 1;
            }
            var rollups = app.Services.GetRequiredService<TelemetryRollups>();
            var plan = await rollups.PlanBackfillAsync(new(options.PropertyId, options.SeriesIds, options.From, options.To), ct);
            if (!plan.PropertyExists)
            {
                logger.LogError("Telemetry backfill found no property {PropertyId}.", options.PropertyId);
                return 1;
            }
            if (plan.Rejected.Count > 0)
            {
                logger.LogError("Telemetry backfill rejected series {SeriesIds}: they do not belong to property {PropertyId} or are status-only.",
                    string.Join(",", plan.Rejected), options.PropertyId);
                return 1;
            }
            foreach (var item in plan.Series)
                logger.LogInformation(
                    "Backfill plan: series {SeriesId} {Metric} from {Source}, range {From} to {To}, {SampleCount} raw observations, {HourCount} hours, {ExistingRows} existing hourly rows, aggregated {AggregatedFrom} to {AggregatedTo}. {Note}",
                    item.SeriesId, item.Metric, item.Source, Format(item.From), Format(item.To), item.Samples, item.Hours,
                    item.ExistingRows, Format(item.AggregatedFrom), Format(item.AggregatedTo), item.Note ?? "");
            logger.LogInformation("Backfill plan for property {PropertyId}: {SeriesCount} series, {SampleCount} raw observations, {HourCount} hourly buckets. Mode: {Mode}.",
                options.PropertyId, plan.Work.Count(), plan.Samples, plan.Hours, options.Execute ? "execute" : "dry run");
            if (!options.Execute)
            {
                logger.LogInformation("Dry run complete. No rollups were written. Add {ExecuteFlag} to aggregate the planned ranges.", Execute);
                return 0;
            }
            var outcome = await rollups.ExecuteBackfillAsync(plan, ct);
            logger.LogInformation("Backfill {Result}: {SeriesCount} series completed, {ChunkCount} chunks, {RowCount} hourly rows written.",
                outcome.Cancelled ? "interrupted" : "completed", outcome.SeriesCompleted, outcome.Chunks, outcome.Written);
            return outcome.Cancelled ? 2 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logger.LogWarning("Backfill interrupted. Completed chunks are kept; rerun the same command to continue.");
            return 2;
        }
        catch (MySqlException error)
        {
            logger.LogError("Backfill failed with MySQL error {ErrorCode} and SQL state {SqlState}. Completed chunks are kept.",
                error.ErrorCode, error.SqlState);
            return 1;
        }
        catch (InvalidOperationException error)
        {
            logger.LogError("Backfill failed: {Reason}", error.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static string Format(DateTime? value) => value?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "none";

    private static long Positive(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : throw new TelemetryBackfillException("Use positive numeric property and series identifiers.");

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant : throw new TelemetryBackfillException("Use ISO 8601 UTC instants such as 2026-01-01T00:00:00Z.");
}
