using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class DatabaseOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string? Password { get; set; }
    public MySqlSslMode SslMode { get; set; } = MySqlSslMode.Preferred;
}

internal sealed record PollState(DateTimeOffset? LastSuccess, bool Failed);

internal sealed class PollProgress
{
    private volatile PollState current = new(null, false);
    public PollState Current => current;
    public void Succeeded(DateTimeOffset now) => current = new PollState(now, false);
    public void Failed() => current = current with { Failed = true };
}

internal sealed class DatabaseReadiness(Migrations migrations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await migrations.VerifyAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database schema is not ready.");
        }
        catch (MySqlException)
        {
            return HealthCheckResult.Unhealthy("The database is unavailable.");
        }
    }
}

internal sealed class TelemetryWorker(
    IServiceScopeFactory scopes, IObservationProvider provider, SimulatorOptions options,
    TimeProvider clock, PollProgress progress, Migrations migrations, IHubContext<MonitoringHub> hub, ILogger<TelemetryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        var interval = TimeSpan.FromSeconds(options.PollSeconds);
        long? propertyId = null;
        var failures = 0;
        var announced = false;
        var waitingForSchema = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (propertyId is null && !await migrations.VerifyAsync(stoppingToken))
                {
                    progress.Failed();
                    if (!waitingForSchema)
                        logger.LogWarning("Simulator is waiting for the database schema. Run the API with --migrate using the migration account.");
                    waitingForSchema = true;
                    await Task.Delay(interval, clock, stoppingToken);
                    continue;
                }
                await using var scope = scopes.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<ITelemetryWriter>();
                propertyId ??= await writer.RegisterDemoInventoryAsync(await provider.DiscoverAsync(stoppingToken), stoppingToken);
                var batch = await provider.ReadAsync(stoppingToken);
                await writer.PersistAsync(propertyId.Value, batch, stoppingToken);
                progress.Succeeded(clock.GetUtcNow());
                if (failures > 0 || !announced)
                {
                    logger.LogInformation("Simulator observations persisted for property {PropertyId}", propertyId);
                    announced = true;
                }
                failures = 0;
                await BroadcastAsync(propertyId.Value, batch.ObservedAt, stoppingToken);
            }
            catch (MySqlException error) when (!stoppingToken.IsCancellationRequested)
            {
                progress.Failed();
                failures = Math.Min(failures + 1, 6);
                logger.LogWarning("Simulator poll database failure {ErrorCode}. Consecutive failures {Failures}",
                    error.ErrorCode, failures);
            }

            var delay = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * Math.Pow(2, failures), Math.Max(60, interval.TotalSeconds)));
            await Task.Delay(delay, clock, stoppingToken);
        }
    }

    private async Task BroadcastAsync(long propertyId, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(MonitoringHub.Group(propertyId))
                .SendAsync("stateChanged", new { propertyId, observedAt }, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Live update broadcast failed for property {PropertyId}", propertyId);
        }
    }
}
