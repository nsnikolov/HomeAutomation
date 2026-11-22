using System.Transactions;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class TuyaMySqlTests
{
    [MySqlFact]
    public async Task SyncUpsertPreservesLastSuccessOnFailureAndUpdatesItOnRecovery()
    {
        var connectionString = Environment.GetEnvironmentVariable(MySqlFact.ConnectionVariable) ??
            throw new InvalidOperationException("A disposable Tuya MySQL test database is required.");
        var connection = new MySqlConnectionStringBuilder(connectionString)
        {
            UseXaTransactions = false
        };
        Assert.Equal("homeautomation_tuya_test", connection.Database);
        var store = new TuyaStore(new Database(connection.ConnectionString),
            new TuyaOptions { AccessId = Guid.NewGuid().ToString("N"), UserId = "fixture", DataCenter = "central-europe" });
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var first = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        await store.ReportAsync("syncing", null, first, null, ct);
        Assert.Null((await store.StatusAsync(ct))!.LastSuccess);
        await store.ReportAsync("connected", first, first.AddMinutes(30), null, ct);
        await store.ReportAsync("rateLimited", null, first.AddHours(1), 1199, ct);
        var failed = (await store.StatusAsync(ct))!;
        Assert.Equal(new("rateLimited", 1199, first.UtcDateTime, first.AddHours(1).UtcDateTime), failed);

        var nextSuccess = first.AddHours(1);
        await store.ReportAsync("connected", nextSuccess, nextSuccess.AddMinutes(30), null, ct);
        Assert.Equal(new("connected", null, nextSuccess.UtcDateTime, nextSuccess.AddMinutes(30).UtcDateTime),
            await store.StatusAsync(ct));
    }

    private sealed class MySqlFact : FactAttribute
    {
        public const string ConnectionVariable = "HOMEAUTOMATION_TUYA_TEST_MYSQL";

        public MySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} for a disposable homeautomation_tuya_test schema with migration 005's tuya_sync table.";
        }
    }
}
