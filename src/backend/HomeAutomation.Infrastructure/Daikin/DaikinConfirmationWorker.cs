using HomeAutomation.Infrastructure.MySql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.Daikin;

internal sealed class DaikinConfirmationSchedule(TimeProvider clock)
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastAccepted;

    public void Schedule(string id)
    {
        lock (sync)
        {
            ids.Add(id);
            lastAccepted = clock.GetTimestamp();
            SignalChange();
        }
    }

    public void Resolve(string id)
    {
        lock (sync)
            if (ids.Remove(id)) SignalChange();
    }

    private void SignalChange()
    {
        changed.TrySetResult();
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task WaitUntilDueAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            TimeSpan delay;
            lock (sync)
            {
                signal = changed.Task;
                delay = ids.Count == 0 ? Timeout.InfiniteTimeSpan : QuietPeriod - clock.GetElapsedTime(lastAccepted);
                if (ids.Count > 0 && delay <= TimeSpan.Zero) return;
            }
            try { await signal.WaitAsync(delay, clock, ct); }
            catch (TimeoutException) { } // A newer acceptance may have reset the deadline.
        }
    }

    // The worker claims the window under the same gate that serializes PATCH and polling.
    public string[] TakeDue()
    {
        lock (sync)
        {
            if (ids.Count == 0 || clock.GetElapsedTime(lastAccepted) < QuietPeriod) return [];
            var batch = ids.ToArray();
            ids.Clear();
            return batch;
        }
    }
}

internal sealed class DaikinConfirmationWorker(IServiceScopeFactory scopes, DaikinConfirmationSchedule schedule,
    ILogger<DaikinConfirmationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await schedule.WaitUntilDueAsync(stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DaikinService>().ConfirmDueAsync(
                    scope.ServiceProvider.GetRequiredService<Migrations>(), stoppingToken);
            }
            catch (DaikinFailure error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("ONECTA confirmation deferred to normal polling with {Status}.", error.Status);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("ONECTA confirmation deferred to normal polling with {ErrorType}.", error.GetType().Name);
            }
        }
    }
}
