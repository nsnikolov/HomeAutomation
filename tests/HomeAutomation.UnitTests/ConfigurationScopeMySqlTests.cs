using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Regression for Settings reporting missing credentials: Daikin, eWeLink and Tuya profiles live in the global scope (scope 0) and
/// Growatt in a property scope. A property read must stay scoped, yet report the global profile's presence without its values.
/// Runs on the disposable <c>homeautomation_logical_test</c> schema, which carries migrations 014–016.
/// </summary>
[Collection(LogicalSchemaCollection.Name)]
public sealed class ConfigurationScopeMySqlTests
{
    [ScopeMySqlFact]
    public async Task PropertyReadsStayScopedButReportGlobalPresenceWithoutValues()
    {
        var admin = Open("HOMEAUTOMATION_LOGICAL_TEST_MYSQL");
        var runtime = Environment.GetEnvironmentVariable("HOMEAUTOMATION_LOGICAL_TEST_RUNTIME") is { Length: > 0 }
            ? Open("HOMEAUTOMATION_LOGICAL_TEST_RUNTIME") : admin;
        await new Migrations(admin).ApplyAsync(default);
        var directory = Directory.CreateTempSubdirectory("ha-config-scope-");
        try
        {
            var installationAdmin = Guid.NewGuid().ToString("D");
            var options = new ConfigurationVaultOptions
                { ActiveKeyId = "scope", KeyDirectory = directory.FullName, AdministratorUserId = installationAdmin };
            File.WriteAllBytes(Path.Combine(directory.FullName, "scope.key"), RandomNumberGenerator.GetBytes(32));
            var keys = new ConfigurationKeys(options);
            long property;
            var owner = Guid.NewGuid().ToString("D");
            var viewer = Guid.NewGuid().ToString("D");
            const string globalAppId = "fixture-global-app-id";
            const string globalClientId = "fixture-global-client-id";
            await using (var db = await admin.OpenAsync(default))
            {
                property = await db.ExecuteScalarAsync<long>("""
                    INSERT INTO properties(site_key,name,time_zone) VALUES(@key,'Scope fixture','UTC'); SELECT LAST_INSERT_ID();
                    """, new { key = Guid.NewGuid().ToString("N") });
                await db.ExecuteAsync("""
                    INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
                    VALUES(@owner,CONCAT(@owner,'@example.test'),UPPER(CONCAT(@owner,'@example.test')),'x','x','x'),
                          (@viewer,CONCAT(@viewer,'@example.test'),UPPER(CONCAT(@viewer,'@example.test')),'x','x','x');
                    INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(@property,@owner,'Owner'),(@property,@viewer,'Viewer');
                    DELETE FROM configuration_profiles WHERE scope_id=0;
                    """, new { owner, viewer, property });
                async Task Seed(long scope, string provider, string slot, Dictionary<string, string> identifiers)
                {
                    var envelope = keys.Seal(scope, provider, slot, Encoding.UTF8.GetBytes("fixture-secret-" + provider));
                    await db.ExecuteAsync("INSERT INTO configuration_nonces(key_id,nonce) VALUES(@KeyId,@Nonce)", envelope);
                    await db.ExecuteAsync("""
                        INSERT INTO configuration_profiles(scope_id,property_id,provider,revision,poll_seconds,credentials,identifiers)
                        VALUES(@scope,NULLIF(@scope,0),@provider,1,@poll,@credentials,@identifiers)
                        """, new { scope, provider, poll = provider == "tuya" ? 1800 : 900,
                        credentials = JsonSerializer.Serialize(new Dictionary<string, object> { [slot] = envelope }),
                        identifiers = JsonSerializer.Serialize(identifiers) });
                }
                // The reported layout: three installation-wide accounts in scope 0 and Growatt in the property scope.
                await Seed(0, "daikin", "clientSecret", new() { ["clientId"] = globalClientId });
                await Seed(0, "ewelink", "appSecret", new() { ["appId"] = globalAppId });
                await Seed(0, "tuya", "accessSecret", new() { ["accessId"] = "fixture-access-id" });
                await Seed(property, "growatt", "apiToken", []);
            }
            var store = new ProviderConfigurationStore(runtime, new Migrations(runtime), options, TimeProvider.System);

            var view = (await store.ReadAsync(property, viewer, default))!;
            ProviderConfigurationView Profile(string provider) => view.Providers.Single(item => item.Provider == provider);
            var growatt = Profile("growatt");
            Assert.True(growatt.Credentials["apiToken"]);
            Assert.False(growatt.InstallationWide);
            Assert.Null(growatt.Global);

            var ewelink = Profile("ewelink");
            Assert.True(ewelink.InstallationWide);
            // No inheritance: the property profile itself is still empty, and edits keep targeting the property scope.
            Assert.Equal(0, ewelink.Revision);
            Assert.False(ewelink.Credentials["appSecret"]);
            Assert.Null(ewelink.Identifiers["appId"]);
            Assert.Equal(1, ewelink.Global!.Revision);
            Assert.True(ewelink.Global.Credentials["appSecret"]);
            Assert.True(ewelink.Global.Identifiers["appId"]);
            Assert.True(Profile("daikin").Global!.Credentials["clientSecret"]);
            var tuya = Profile("tuya").Global!;
            Assert.True(tuya.Credentials["accessSecret"]);
            Assert.True(tuya.Identifiers["accessId"]);
            Assert.False(tuya.Identifiers["userId"]);

            // Only presence leaves the global scope: no identifier values, secrets or envelope fields.
            var json = JsonSerializer.Serialize(view);
            foreach (var hidden in new[] { globalAppId, globalClientId, "fixture-access-id", "fixture-secret", "ciphertext", "nonce", "keyId" })
                Assert.DoesNotContain(hidden, json, StringComparison.OrdinalIgnoreCase);

            // The global scope is still reserved for the installation administrator, and shows its own values.
            Assert.Null(await store.ReadAsync(null, viewer, default));
            var global = (await store.ReadAsync(null, installationAdmin, default))!;
            var globalEwelink = global.Providers.Single(item => item.Provider == "ewelink");
            Assert.Equal(globalAppId, globalEwelink.Identifiers["appId"]);
            Assert.Null(globalEwelink.Global);

            // Staging a property eWeLink profile creates a property row and leaves the global profile untouched.
            Assert.Equal(ConfigurationSaveResult.Saved, await store.SaveAsync(property, owner, "ewelink",
                new ProviderConfigurationUpdate(0, 600), default));
            var after = (await store.ReadAsync(property, owner, default))!.Providers.Single(item => item.Provider == "ewelink");
            Assert.Equal((1L, 600), (after.Revision, after.PollSeconds));
            Assert.Equal(1, after.Global!.Revision);
            await using var check = await admin.OpenAsync(default);
            Assert.Equal(1, await check.ExecuteScalarAsync<long>(
                "SELECT revision FROM configuration_profiles WHERE scope_id=0 AND provider='ewelink'"));
        }
        finally
        {
            directory.Delete(recursive: true);
            await using var cleanup = await admin.OpenAsync(default);
            await cleanup.ExecuteAsync("DELETE FROM configuration_profiles WHERE scope_id=0");
        }
    }

    private static Database Open(string variable)
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException("A disposable logical-device MySQL test database is required."));
        Assert.Equal("homeautomation_logical_test", builder.Database);
        return new Database(builder.ConnectionString);
    }

    private sealed class ScopeMySqlFact : FactAttribute
    {
        public ScopeMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOMEAUTOMATION_LOGICAL_TEST_MYSQL")))
                Skip = "Set HOMEAUTOMATION_LOGICAL_TEST_MYSQL for a disposable homeautomation_logical_test schema.";
        }
    }
}
