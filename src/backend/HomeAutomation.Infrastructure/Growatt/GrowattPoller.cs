using System.Net.Http;
using HomeAutomation.Application;

namespace HomeAutomation.Infrastructure.Growatt;

// One poll at a time. Retry deferrals are measured by the worker after a poll finishes.
public sealed class GrowattPoller(GrowattOptions options, GrowattMapper mapper, TimeProvider clock)
{
    private int failures;

    public async Task<GrowattPollResult> PollAsync(IGrowattReadSource source, IGrowattTelemetryStore store,
        CancellationToken stoppingToken)
    {
        try
        {
            GrowattSnapshot snapshot;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds), clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token))
            {
                try
                {
                    snapshot = await source.ReadAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    throw new GrowattReadException(GrowattFailure.Timeout);
                }
            }

            var (devices, batch) = mapper.Map(snapshot);
            var property = await store.StoreAsync(options.SiteKey, devices, batch, stoppingToken);
            failures = 0;
            return new GrowattPollResult(property, TimeSpan.FromSeconds(options.PollSeconds), null, false);
        }
        catch (HttpRequestException error) when (!stoppingToken.IsCancellationRequested)
        {
            var failure = error.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => GrowattFailure.Throttled,
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => GrowattFailure.Authorization,
                _ => GrowattFailure.Unavailable
            };
            return Defer(failure);
        }
        catch (TimeoutException) when (!stoppingToken.IsCancellationRequested)
        {
            return Defer(GrowattFailure.Timeout);
        }
        catch (GrowattReadException error) when (!stoppingToken.IsCancellationRequested)
        {
            return Defer(error.Failure, error.RetryAfter);
        }
    }

    private GrowattPollResult Defer(GrowattFailure failure, TimeSpan? retryAfter = null)
    {
        // A single delayed probe follows three consecutive failures.
        failures = Math.Min(failures + 1, 6);
        var circuitOpen = failures >= 3 || failure == GrowattFailure.Authorization;
        var baseSeconds = Math.Max(options.PollSeconds, 60);
        var seconds = Math.Min(3600, baseSeconds * (1 << failures));
        if (circuitOpen) seconds = Math.Max(seconds, 900);
        var delay = TimeSpan.FromSeconds(seconds);
        return new GrowattPollResult(null, retryAfter > delay ? retryAfter.Value : delay, failure, circuitOpen);
    }
}

public sealed record GrowattPollResult(long? PropertyId, TimeSpan NextDelay, GrowattFailure? Failure, bool CircuitOpen);
