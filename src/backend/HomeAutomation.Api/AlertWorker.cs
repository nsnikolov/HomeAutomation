using System.Collections.Concurrent;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.SignalR;

namespace HomeAutomation.Api;

/// <summary>Collects properties whose telemetry changed. Memory is bounded by the number of properties.</summary>
public sealed class AlertTrigger : IAlertTrigger
{
    private readonly ConcurrentDictionary<long, byte> changed = new();
    private readonly SemaphoreSlim signal = new(0, 1);

    public void PropertyChanged(long propertyId)
    {
        changed.TryAdd(propertyId, 0);
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits until a property changed or the timeout passed, then returns and clears the changed properties.</summary>
    public async Task<IReadOnlyCollection<long>> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (changed.IsEmpty) await signal.WaitAsync(timeout, ct);
        var drained = new List<long>();
        foreach (var property in changed.Keys)
            if (changed.TryRemove(property, out _)) drained.Add(property);
        return drained;
    }
}

/// <summary>
/// Evaluates rules independently of open browsers: immediately (after a one-second coalescing delay) for properties whose telemetry
/// changed, and in a full sweep for durations, freshness expiry, reminders and delivery retries. Failures are logged by type only.
/// </summary>
internal sealed class AlertWorker(AlertTrigger trigger, AlertEvaluator evaluator, NotificationDispatcher dispatcher, Migrations migrations,
    AlertOptions options, TimeProvider clock, ILogger<AlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SchemaRetry = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested && !await ReadyAsync(stoppingToken))
            await Task.Delay(SchemaRetry, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        var nextSweep = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                HashSet<long> due;
                var sweep = now >= nextSweep;
                if (sweep)
                {
                    due = [.. await evaluator.PropertiesDueAsync(stoppingToken), .. await trigger.WaitAsync(TimeSpan.Zero, stoppingToken)];
                    nextSweep = now + TimeSpan.FromSeconds(options.SweepSeconds);
                }
                else
                {
                    var changed = await trigger.WaitAsync(nextSweep - now, stoppingToken);
                    if (changed.Count == 0) continue;
                    await Task.Delay(Coalesce, clock, stoppingToken);
                    due = [.. changed, .. await trigger.WaitAsync(TimeSpan.Zero, stoppingToken)];
                }
                foreach (var property in due.Order())
                {
                    try { await dispatcher.DispatchAsync(await evaluator.EvaluateAsync(property, stoppingToken), stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogWarning("Alert evaluation for property {PropertyId} failed ({ErrorType}).", property, error.GetType().Name);
                    }
                }
                if (sweep)
                {
                    await dispatcher.DeliverPendingAsync(null, stoppingToken);
                    await evaluator.ExpireDeliveriesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Alert sweep failed ({ErrorType}); retrying at the next sweep.", error.GetType().Name);
                nextSweep = clock.GetUtcNow() + TimeSpan.FromSeconds(options.SweepSeconds);
                await Task.Delay(TimeSpan.FromSeconds(options.SweepSeconds), clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            if (await migrations.VerifyAlertsAsync(ct)) return true;
            logger.LogInformation("Alert evaluation waits for database migration 021.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Alert evaluation could not check the database schema ({ErrorType}).", error.GetType().Name);
        }
        return false;
    }
}

internal sealed class AlertLiveNotifier(IHubContext<MonitoringHub> hub, ILogger<AlertLiveNotifier> logger) : IAlertLiveNotifier
{
    public async Task IncidentsChangedAsync(long propertyId, CancellationToken ct)
    {
        try { await hub.Clients.Group(MonitoringHub.Group(propertyId)).SendAsync("alertsChanged", new { propertyId }, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Alert refresh for property {PropertyId} failed ({ErrorType}).", propertyId, error.GetType().Name);
        }
    }

    public async Task NotificationsChangedAsync(IReadOnlyCollection<string> users, CancellationToken ct)
    {
        try { await hub.Clients.Users(users.ToArray()).SendAsync("notificationsChanged", ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Notification refresh for {UserCount} users failed ({ErrorType}).", users.Count, error.GetType().Name);
        }
    }
}

/// <summary>
/// Hands a notification to the recipient's open browser sessions over SignalR. The page decides whether to show a system notification
/// (background tab, permission granted) or an in-page message, and reports what it observed.
/// </summary>
internal sealed class BrowserNotificationChannel(IHubContext<MonitoringHub> hub) : INotificationChannel
{
    public string Name => Domain.NotificationPreferences.Browser;

    public async Task<ChannelResult> DeliverAsync(NotificationMessage message, CancellationToken ct)
    {
        await hub.Clients.User(message.UserId).SendAsync("browserNotification", new
        {
            message.NotificationId, message.PropertyId, message.IncidentId, message.Kind, message.Severity, message.Title, message.Body,
            message.CreatedAt, message.Sound,
        }, ct);
        return new ChannelResult("sent");
    }
}
