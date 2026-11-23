using System.Transactions;
using Dapper;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class EwelinkMySqlTests
{
    [MySqlFact]
    public async Task TokenUpsertPreservesOwnershipAndReadsBackEncryptedTokens()
    {
        var database = TestDatabase();
        var store = new EwelinkStore(database, new Migrations(database));
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var vault = new EwelinkVault(new EphemeralDataProtectionProvider());
        const string owner = "fixture-owner";
        const string other = "fixture-other";
        const string appId = "fixture-app";
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var tokens = new EwelinkTokens("eu", "fixture-access", "fixture-refresh", now.AddDays(1), now.AddDays(2));

        Assert.Null(await store.AccountAsync(ct));
        await store.SaveTokensAsync(owner, vault.Seal(owner, appId, tokens), ct);
        await AssertConnectedAsync(tokens);

        var rotated = tokens with { Access = "fixture-rotated" };
        await store.SaveTokensAsync(owner, vault.Seal(owner, appId, rotated), ct);
        await store.SaveTokensAsync(other, vault.Seal(other, appId, tokens), ct);
        await store.SaveTokensAsync(other, null, ct);
        await AssertConnectedAsync(rotated);

        await store.SaveTokensAsync(owner, null, ct);
        await store.SaveTokensAsync(other, vault.Seal(other, appId, tokens), ct);
        var disconnected = Assert.IsType<EwelinkAccount>(await store.AccountAsync(ct));
        Assert.Equal(owner, disconnected.Owner);
        Assert.Equal("disconnected", disconnected.Status);
        Assert.Null(disconnected.Envelope);

        await store.SaveTokensAsync(owner, vault.Seal(owner, appId, tokens), ct);
        await AssertConnectedAsync(tokens);

        async Task AssertConnectedAsync(EwelinkTokens expected)
        {
            var account = Assert.IsType<EwelinkAccount>(await store.AccountAsync(ct));
            Assert.Equal(owner, account.Owner);
            Assert.Equal("connected", account.Status);
            var envelope = Assert.IsType<byte[]>(account.Envelope);
            Assert.Equal(expected, vault.Open(owner, appId, envelope));
            Assert.Throws<EwelinkFailure>(() => vault.Open(other, appId, envelope));
        }
    }

    [MySqlFact]
    public async Task RequestBudgetAndSyncTimestampsPersistAcrossStoreInstances()
    {
        var database = TestDatabase();
        var migrations = new Migrations(database);
        var store = new EwelinkStore(database, migrations);
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        await store.SaveTokensAsync("fixture-owner", [1], ct);
        await store.ReportAsync("fixture-owner", "syncing", null, now.AddMinutes(30), ct, now);
        await store.ReportAsync("fixture-owner", "connected", now.AddSeconds(2), now.AddMinutes(5), ct);
        await using (var db = await database.OpenAsync(ct))
            await db.ExecuteAsync(Database.Query("""
                INSERT INTO ewelink_request_days(region,day_utc,requests) VALUES('eu',@day,49999)
                """, ct, new { day = now.UtcDateTime.Date }));

        await store.ReserveRequestAsync("eu", now, ct);
        var reopened = new EwelinkStore(database, migrations);
        var blocked = await Assert.ThrowsAsync<EwelinkFailure>(() => reopened.ReserveRequestAsync("eu", now, ct));
        Assert.Equal("quotaExceeded", blocked.Status);
        Assert.Equal(now.AddDays(32).Date, blocked.RetryAt!.Value.UtcDateTime);
        await reopened.ReserveRequestAsync("us", now, ct);
        await reopened.ReserveRequestAsync("eu", now.AddDays(32), ct);
        var account = Assert.IsType<EwelinkAccount>(await reopened.AccountAsync(ct));
        Assert.Equal(now.UtcDateTime, account.LastAttempt);
        Assert.Equal(now.AddSeconds(2).UtcDateTime, account.LastSuccess);
    }

    private static Database TestDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable(MySqlFact.ConnectionVariable) ??
            throw new InvalidOperationException("A disposable eWeLink MySQL test database is required.");
        var connection = new MySqlConnectionStringBuilder(connectionString) { UseXaTransactions = false };
        Assert.Equal("homeautomation_ewelink_test", connection.Database);
        return new Database(connection.ConnectionString);
    }

    private sealed class MySqlFact : FactAttribute
    {
        public const string ConnectionVariable = "HOMEAUTOMATION_EWELINK_TEST_MYSQL";

        public MySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} for a disposable homeautomation_ewelink_test schema with the repository migrations.";
        }
    }
}
