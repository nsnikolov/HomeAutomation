using System.Transactions;
using System.Net;
using Dapper;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class EwelinkMySqlTests
{
    [RelayMySqlFact]
    public async Task ConfirmedMockedCommandPersistsActualRelayTelemetry()
    {
        var configured = Environment.GetEnvironmentVariable(RelayMySqlFact.ConnectionVariable) ??
            throw new InvalidOperationException("A disposable relay test database is required.");
        var connectionString = new MySqlConnectionStringBuilder(configured);
        Assert.Equal("homeautomation_ewelink_relay_test", connectionString.Database);
        var database = new Database(connectionString.ConnectionString);
        var store = new EwelinkStore(database, new Migrations(database));
        var audit = new EwelinkRelayAudit(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var clock = TimeProvider.System;
        var now = clock.GetUtcNow();
        const string owner = "fixture-owner";
        const string app = "fixture-app";
        Assert.Null(await store.AccountAsync(ct));
        long property;
        await using (var db = await database.OpenAsync(ct))
        {
            property = await db.ExecuteScalarAsync<long>("""
                INSERT INTO properties(site_key,name,time_zone) VALUES('fixture','Fixture','UTC');
                SELECT LAST_INSERT_ID();
                """);
            await db.ExecuteAsync("""
                INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
                VALUES(@owner,'fixture@example.test','FIXTURE@EXAMPLE.TEST','fixture','fixture','fixture');
                INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@owner,'viewer');
                """, new { owner, property });
        }
        var vault = new EwelinkVault(new EphemeralDataProtectionProvider());
        var tokens = new EwelinkTokens("eu", "fixture-access", "fixture-refresh", now.AddDays(1), now.AddDays(2));
        await store.SaveTokensAsync(owner, vault.Seal(owner, app, tokens), ct);
        var device = new EwelinkDevice("fixture-relay", "Boiler", "PSF-X67", "", false,
            Connectivity.Online, now.AddSeconds(-2), true, false, now.AddSeconds(-2), Uiid: 32);
        await store.SaveAsync(owner, device, ct);
        var row = Assert.Single(await store.DevicesAsync(owner, ct));
        await store.AssignAsync(row, owner, property, null, ct);

        var options = new EwelinkOptions { AppId = app, EnableWrites = true };
        using var gate = new EwelinkGate();
        using var handler = new RelayHandler();
        using var http = new HttpClient(handler);
        var client = new EwelinkClient(http, options, clock, new EwelinkPace(clock), store: store);
        var service = new EwelinkService(options, client, store, gate, vault, new MonitoringReader(database, clock, new(), new()),
            new TelemetryWriter(database, clock), clock, audit);
        var blocked = await Assert.ThrowsAsync<EwelinkFailure>(() =>
            service.CommandRelayAsync(owner, property, row.Id, new(true, false), ct));
        Assert.Equal("forbidden", blocked.Status);
        Assert.Empty(handler.Methods);
        await service.SetRelayWritesAsync(owner, property, row.Id, true, ct);
        Assert.True(Assert.Single(await store.DevicesAsync(owner, ct)).RelayWriteEnabled);
        var result = await service.CommandRelayAsync(owner, property, row.Id, new(true, false), ct);
        Assert.Equal("confirmed", result.Status);
        Assert.False(result.Open);
        Assert.Equal(result, await audit.LatestAsync(row.Id, property, ct));
        Assert.True(Assert.Single(await store.DevicesAsync(owner, ct)).Device.Relay);
        Assert.True(Assert.Single(await store.DevicesAsync(owner, ct)).RelayWriteEnabled);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Methods);
        options.RelayWriteDevices[row.Id] = true;
        await service.SetRelayWritesAsync(owner, property, row.Id, false, ct);
        var disabled = await Assert.ThrowsAsync<EwelinkFailure>(() =>
            service.CommandRelayAsync(owner, property, row.Id, new(false, false), ct));
        Assert.Equal("forbidden", disabled.Status);
        Assert.Equal(2, handler.Methods.Count);
        await using var telemetry = await database.OpenAsync(ct);
        Assert.True(await telemetry.ExecuteScalarAsync<bool>("""
            SELECT lr.boolean_value FROM latest_readings lr
            JOIN telemetry_series ts ON ts.id=lr.series_id
            WHERE ts.property_id=@property AND ts.source_key='ewelink' AND ts.metric='SwitchOn'
            """, new { property }));
    }

    private sealed class RelayHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v2/device/thing/status", request.RequestUri!.AbsolutePath);
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post
                    ? """{"error":0}"""
                    : """{"error":0,"data":{"params":{"switch":"on"}}}""")
            });
        }
    }

    private sealed class RelayMySqlFact : FactAttribute
    {
        public const string ConnectionVariable = "HOMEAUTOMATION_EWELINK_RELAY_TEST_MYSQL";
        public RelayMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} for a fresh disposable homeautomation_ewelink_relay_test schema with the repository migrations.";
        }
    }

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

    [MySqlFact]
    public async Task RelayAuditPreventsOverlappingCommandsAndReconcilesOnlyFreshEvidence()
    {
        var database = TestDatabase();
        var audit = new EwelinkRelayAudit(database);
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var ct = CancellationToken.None;
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var device = Guid.NewGuid().ToString("N");
        long property;
        await using (var connection = await database.OpenAsync(ct))
        {
            property = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Fixture','UTC');
                SELECT LAST_INSERT_ID();
                """, new { key = device });
            await connection.ExecuteAsync("""
                INSERT INTO ewelink_devices(public_id,owner_id,external_id,property_id,snapshot)
                VALUES(@device,'fixture-owner',@device,@property,'{}');
                """, new { device, property });
        }

        var id = await audit.BeginAsync("fixture-owner", device, property, true, now, ct);
        var duplicate = await Assert.ThrowsAsync<EwelinkFailure>(() =>
            audit.BeginAsync("fixture-owner", device, property, false, now, ct));
        Assert.Equal("commandPending", duplicate.Status);
        await audit.ReconcileAsync(device, property, true, now.AddSeconds(1), ct);
        Assert.Equal(new(id, "pending", true), await audit.LatestAsync(device, property, ct));
        await audit.FinishAsync(id, "unverified", null, ct);
        await audit.ReconcileAsync(device, property, false, now.AddSeconds(2), ct);
        Assert.Equal(new(id, "unverified", false), await audit.LatestAsync(device, property, ct));
        Assert.Null(await audit.LatestAsync(device, property + 1, ct));
        var next = await audit.BeginAsync("fixture-owner", device, property, true, now.AddSeconds(3), ct);
        await audit.FinishAsync(next, "confirmed", now.AddSeconds(4), ct);
        Assert.Equal(new(next, "confirmed", false), await audit.LatestAsync(device, property, ct));
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
