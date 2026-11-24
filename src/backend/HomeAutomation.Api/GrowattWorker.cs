using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class GrowattProgress
{
    private volatile GrowattPollStatus current = new("notConfigured", null);
    public GrowattPollStatus Current => current;
    public void Set(string status, DateTimeOffset? lastSuccess = null) =>
        current = new(status, lastSuccess ?? current.LastSuccess);
}

internal sealed record GrowattPollStatus(string Status, DateTimeOffset? LastSuccess);

internal sealed class GrowattWorker(IServiceScopeFactory scopes, GrowattOptions options, GrowattPoller poller,
    GrowattProgress progress, TimeProvider clock, IHubContext<MonitoringHub> hub, ILogger<GrowattWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        if (!options.HasCloudConfiguration())
        {
            progress.Set("invalidConfiguration");
            logger.LogWarning("Growatt requires a token, confirmed inverter and battery mapping, source time zone and polling at least five minutes apart.");
            return;
        }
        var schemaReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.PollSeconds);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var source = scope.ServiceProvider.GetService<IGrowattReadSource>();
                if (source is null)
                {
                    progress.Set("transportUnavailable");
                    logger.LogWarning("Growatt is waiting for an authorized and verified read transport.");
                    return;
                }
                schemaReady = schemaReady || await scope.ServiceProvider.GetRequiredService<Migrations>().VerifyAsync(stoppingToken);
                if (schemaReady)
                {
                    var result = await poller.PollAsync(source, scope.ServiceProvider.GetRequiredService<IGrowattTelemetryStore>(), stoppingToken);
                    delay = result.NextDelay;
                    if (result.PropertyId is { } propertyId)
                    {
                        var synchronizedAt = clock.GetUtcNow();
                        progress.Set("running", synchronizedAt);
                        await hub.Clients.Group(MonitoringHub.Group(propertyId))
                            .SendAsync("stateChanged", new { propertyId, observedAt = synchronizedAt }, stoppingToken);
                    }
                    else
                    {
                        progress.Set(result.CircuitOpen ? "paused" : "degraded");
                        logger.LogWarning("Growatt poll failed with {Failure} for {Operation} (HTTP {HttpStatus}, provider code {ProviderCode}). Next attempt in {Delay}.",
                            result.Failure, result.Provider?.Operation, result.Provider?.HttpStatus, result.Provider?.Code, delay);
                    }
                }
                else
                {
                    progress.Set("waitingForSchema");
                }
            }
            catch (MySqlException error) when (!stoppingToken.IsCancellationRequested)
            {
                progress.Set("degraded");
                logger.LogWarning("Growatt persistence failed with MySQL error {ErrorCode}.", error.ErrorCode);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                progress.Set("degraded");
                logger.LogWarning("Growatt poll failed with {FailureType}. Check adapter and inventory configuration.", error.GetType().Name);
            }
            while (delay > TimeSpan.FromDays(1))
            {
                await Task.Delay(TimeSpan.FromDays(1), clock, stoppingToken);
                delay -= TimeSpan.FromDays(1);
            }
            await Task.Delay(delay, clock, stoppingToken);
        }
    }
}
