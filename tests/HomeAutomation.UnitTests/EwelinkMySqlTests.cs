using System.Transactions;
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
        var connectionString = Environment.GetEnvironmentVariable(MySqlFact.ConnectionVariable) ??
            throw new InvalidOperationException("A disposable eWeLink MySQL test database is required.");
        var connection = new MySqlConnectionStringBuilder(connectionString)
        {
            UseXaTransactions = false
        };
        Assert.Equal("homeautomation_ewelink_test", connection.Database);
        var database = new Database(connection.ConnectionString);
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
