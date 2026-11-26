using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class LogicalDeviceOptions
{
    /// <summary>Create logical identities for newly discovered devices of properties that already use logical devices.</summary>
    public bool AutoProvision { get; set; } = true;
    public int ProvisionIntervalMinutes { get; set; } = 5;
    public bool IsValid => ProvisionIntervalMinutes is >= 1 and <= 1440;
}

/// <summary>
/// Provider-neutral provisioning over the accepted device inventory. It runs outside telemetry ingestion, so a failure never
/// blocks discovery or persistence. A failing property is retried with bounded back-off; other properties continue.
/// </summary>
public sealed class LogicalProvisioning(TimeProvider clock, ILogger<LogicalProvisioning> logger)
{
    internal static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(6);
    private readonly Dictionary<long, (int Failures, DateTimeOffset RetryAt)> backoff = [];
    private readonly SemaphoreSlim running = new(1, 1);
    private bool reportedPending;

    public async Task<(int Created, int Failed)> SweepAsync(LogicalDeviceStore store, CancellationToken ct)
    {
        if (!await running.WaitAsync(0, ct)) return (0, 0);
        try
        {
            if (!await store.SchemaReadyAsync(ct))
            {
                if (!reportedPending) logger.LogInformation("Logical device provisioning waits for migrations 017, 018 and 019.");
                reportedPending = true;
                return (0, 0);
            }
            reportedPending = false;
            int created = 0, failed = 0, properties = 0;
            foreach (var property in await store.ProvisioningCandidatesAsync(ct))
            {
                var now = clock.GetUtcNow();
                if (backoff.TryGetValue(property, out var state) && state.RetryAt > now) continue;
                try
                {
                    var count = await store.ProvisionAsync(property, ct);
                    backoff.Remove(property);
                    if (count > 0) { created += count; properties++; }
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    failed++;
                    var failures = Math.Min((backoff.TryGetValue(property, out var previous) ? previous.Failures : 0) + 1, 10);
                    var delay = BaseBackoff * Math.Pow(2, failures - 1);
                    var retryAt = now + (delay > MaximumBackoff ? MaximumBackoff : delay);
                    backoff[property] = (failures, retryAt);
                    logger.LogWarning("Logical device provisioning for property {PropertyId} failed with {ErrorType} (attempt {Attempt}); retrying after {RetryAt}.",
                        property, error.GetType().Name, failures, retryAt);
                }
            }
            if (created > 0)
                logger.LogInformation("Provisioned {Created} logical devices for newly discovered hardware in {Properties} properties.", created, properties);
            return (created, failed);
        }
        finally { running.Release(); }
    }
}

internal sealed class LogicalProvisioningWorker(IServiceScopeFactory scopes, LogicalProvisioning provisioning, LogicalDeviceOptions options,
    TimeProvider clock, ILogger<LogicalProvisioningWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.AutoProvision) return;
        await Task.Delay(StartupDelay, clock, ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.ProvisionIntervalMinutes), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await provisioning.SweepAsync(scope.ServiceProvider.GetRequiredService<LogicalDeviceStore>(), ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Logical device provisioning sweep failed with {ErrorType}.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
