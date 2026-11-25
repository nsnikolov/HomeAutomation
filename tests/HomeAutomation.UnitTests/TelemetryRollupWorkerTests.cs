using HomeAutomation.Infrastructure.MySql;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeAutomation.UnitTests;

public sealed class TelemetryRollupWorkerTests
{
    [Fact]
    public async Task WorkerWaitsForTheMigrationAndStopsPromptlyWhenCancelled()
    {
        var runner = new Runner(ready: false);
        using var worker = new TelemetryRollupWorker(runner, new TelemetryHistoryOptions(), TimeProvider.System,
            NullLogger<TelemetryRollupWorker>.Instance);

        await worker.StartAsync(default);
        await runner.Checked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = worker.StopAsync(default);

        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, runner.Refreshes);
    }

    [Fact]
    public async Task WorkerPassesCancellationIntoARunningRefresh()
    {
        var runner = new Runner(ready: true, blockUntilCancelled: true);
        using var worker = new TelemetryRollupWorker(runner, new TelemetryHistoryOptions(), TimeProvider.System,
            NullLogger<TelemetryRollupWorker>.Instance);

        await worker.StartAsync(default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(runner.Cancelled);
    }

    [Fact]
    public async Task DisabledWorkerNeverTouchesTheDatabase()
    {
        var runner = new Runner(ready: true);
        using var worker = new TelemetryRollupWorker(runner, new TelemetryHistoryOptions { RollupsEnabled = false }, TimeProvider.System,
            NullLogger<TelemetryRollupWorker>.Instance);

        await worker.StartAsync(default);
        await worker.StopAsync(default);

        Assert.False(runner.Checked.Task.IsCompleted);
    }

    private sealed class Runner(bool ready, bool blockUntilCancelled = false) : ITelemetryRollupRunner
    {
        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Refreshes;
        public bool Cancelled;

        public Task<bool> SchemaReadyAsync(CancellationToken ct)
        {
            Checked.TrySetResult();
            return Task.FromResult(ready);
        }

        public async Task<RollupRun> RefreshRecentAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Refreshes);
            Started.TrySetResult();
            if (!blockUntilCancelled) return new(false, 0, 0, 0, 0);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(false, 0, 0, 0, 0);
        }
    }
}
