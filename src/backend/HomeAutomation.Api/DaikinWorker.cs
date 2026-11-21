using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.MySql;

namespace HomeAutomation.Api;

internal sealed class DaikinWorker(IServiceScopeFactory scopes, DaikinOptions options, TimeProvider clock,
    ILogger<DaikinWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(options.PollMinutes);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<Migrations>().VerifyAsync(stoppingToken, includeDaikin: true))
                    await scope.ServiceProvider.GetRequiredService<DaikinService>().PollAsync(null, true, stoppingToken);
                failures = 0;
            }
            catch (DaikinFailure error) when (!stoppingToken.IsCancellationRequested)
            {
                failures = Math.Min(failures + 1, 4);
                delay = TimeSpan.FromMinutes(options.PollMinutes * (1 << failures));
                if (error.RetryAt is { } retry && retry - clock.GetUtcNow() > delay)
                    delay = retry - clock.GetUtcNow();
                logger.LogWarning("ONECTA poll deferred with {Status}.", error.Status);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                failures = Math.Min(failures + 1, 4);
                delay = TimeSpan.FromMinutes(options.PollMinutes * (1 << failures));
                logger.LogWarning("ONECTA poll deferred with {ErrorType}.", error.GetType().Name);
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
