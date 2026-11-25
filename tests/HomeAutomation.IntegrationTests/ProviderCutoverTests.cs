using System.Security.Cryptography;
using Dapper;
using HomeAutomation.Api;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;

namespace HomeAutomation.IntegrationTests;

public sealed class ProviderCutoverTests
{
    private const string AdminVariable = "HOMEAUTOMATION_PROVIDER_TEST_MYSQL";
    private const string RuntimeVariable = "HOMEAUTOMATION_PROVIDER_TEST_RUNTIME";

    [Fact]
    public void SourceSelectorsDefaultToLegacyAndExplicitlyRejectUnsafeScopeOrInvalidSelectors()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConfigurationVault:Providers:growatt:Source"] = "Vault",
            ["ConfigurationVault:Providers:growatt:PropertyId"] = "4",
            ["ConfigurationVault:Providers:ewelink:Source"] = "Vault",
            ["ConfigurationVault:Providers:ewelink:PropertyId"] = "4",
            ["ConfigurationVault:Providers:tuya:Source"] = "typo"
        }).Build();
        Assert.Equal((false, null), ProviderConfigurationStartup.Select(config, "daikin"));
        Assert.Equal((true, (long?)4), ProviderConfigurationStartup.Select(config, "growatt"));
        Assert.Throws<ProviderConfigurationException>(() => ProviderConfigurationStartup.Select(config, "ewelink"));
        Assert.Throws<ProviderConfigurationException>(() => ProviderConfigurationStartup.Select(config, "tuya"));
        Assert.Equal(new ProviderConfigurationStartup.ImportCommand("growatt", 4, true, 8),
            ProviderConfigurationStartup.ParseImport(["--import-provider=growatt", "--dry-run", "--provider-property=4", "--replace-revision=8"]));
        Assert.Throws<ProviderConfigurationException>(() => ProviderConfigurationStartup.ParseImport(["--import-provider"]));
        Assert.Throws<ProviderConfigurationException>(() => ProviderConfigurationStartup.ParseImport(["--replace-revision=0"]));
    }

    [ProviderMySqlFact]
    public async Task ExplicitImportsLoadAllSingletonsAndPreserveCurrentOauthGrantsAcrossRestart()
    {
        var admin = Fixture(AdminVariable, "root");
        var runtime = Fixture(RuntimeVariable, "provider_runtime");
        var database = new Database(runtime.ConnectionString);
        var migrations = new Migrations(new Database(admin.ConnectionString));
        await migrations.ApplyAsync(default);
        await using var db = await new Database(admin.ConnectionString).OpenAsync(default);
        await db.ExecuteAsync("""
            INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
            VALUES('00000000-0000-0000-0000-000000000001','fixture@example.test','FIXTURE@EXAMPLE.TEST','fixture','fixture','fixture');
            INSERT INTO property_memberships(property_id,user_id,role_code)
            VALUES(1,'00000000-0000-0000-0000-000000000001','Owner');
            """);
        var vaultDirectory = Directory.CreateTempSubdirectory("ha-provider-vault-");
        var protectionDirectory = Directory.CreateTempSubdirectory("ha-provider-dp-");
        try
        {
            File.WriteAllBytes(Path.Combine(vaultDirectory.FullName, "fixture.key"), RandomNumberGenerator.GetBytes(32));
            var protection = DataProtectionProvider.Create(protectionDirectory, builder => builder.SetApplicationName("HomeAutomation"));
            var vault = new ConfigurationVaultOptions { KeyDirectory = vaultDirectory.FullName, ActiveKeyId = "fixture" };
            var store = new ProviderConfigurationStore(database, new Migrations(database), vault, TimeProvider.System);
            var growatt = new GrowattOptions { Enabled = true, ApiToken = "fixture-growatt-token", SiteKey = "countryside",
                MasterId = "master", SlaveId = "slave", BatterySourceId = "slave", PollSeconds = 330, TimeZone = "Europe/Sofia" };
            var daikin = new DaikinOptions { ClientId = "fixture-client", ClientSecret = "fixture-client-secret",
                RedirectUri = "https://example.test/api/v1/daikin/callback", OperatorEmail = "fixture@example.test",
                TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), EnableWrites = true };
            var ewelink = new EwelinkOptions { AppId = "fixture-app", AppSecret = "fixture-app-secret",
                RedirectUri = "https://example.test/api/v1/ewelink/callback", OperatorEmail = "fixture@example.test",
                EnableWebSocket = true, EnableLiveElectricalReporting = true, EnableWrites = true };
            var tuya = new TuyaOptions { AccessId = "fixture-project", AccessSecret = "fixture-project-secret",
                UserId = "fixture-user", DataCenter = "central-europe", OperatorEmail = "fixture@example.test" };
            const string owner = "00000000-0000-0000-0000-000000000001";
            var now = DateTimeOffset.UnixEpoch.AddDays(20);
            var daikinStore = new DaikinStore(database, TimeProvider.System, daikin);
            var ewelinkStore = new EwelinkStore(database, new Migrations(database));
            var daikinSecrets = new DaikinSecrets(daikin);
            var ewelinkVault = new EwelinkVault(protection);
            await daikinStore.ClaimAsync(owner, default);
            await daikinStore.SaveTokensAsync(owner, daikinSecrets.Seal(owner, new("fixture-ac", "fixture-refresh", now)), "connected", default);
            await ewelinkStore.SaveTokensAsync(owner, ewelinkVault.Seal(owner, ewelink.AppId!,
                new("eu", "fixture-access", "fixture-refresh", now, now, "fixture-account-key")), default);
            var grantsBefore = new[] { (await daikinStore.AccountAsync(default))!.Envelope, (await ewelinkStore.AccountAsync(default))!.Envelope };

            await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
                store.LoadProviderAsync("growatt", 1, null, protection, default));
            await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
                store.ImportProviderAsync("growatt", 42, growatt, true, null, protection, default));
            var previews = await store.ImportProviderAsync("growatt", 1, growatt, true, null, protection, default);
            Assert.True(previews.Succeeded);
            Assert.Contains("apiToken", previews.Fields);
            Assert.DoesNotContain(growatt.ApiToken, string.Join(", ", previews.Fields));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_profiles"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_nonces"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_audit"));

            foreach (var (provider, property, settings) in new (string, long?, object)[]
                { ("growatt", 1, growatt), ("daikin", null, daikin), ("ewelink", null, ewelink), ("tuya", null, tuya) })
            {
                var imported = await store.ImportProviderAsync(provider, property, settings, false, null, protection, default);
                Assert.Equal("imported", imported.Status);
                var unchanged = await store.ImportProviderAsync(provider, property, settings, false, null, protection, default);
                Assert.Equal("unchanged", unchanged.Status);
                Assert.Equal(imported.Revision, unchanged.Revision);
            }
            Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_nonces"));
            Assert.Equal(grantsBefore[0], (await daikinStore.AccountAsync(default))!.Envelope);
            Assert.Equal(grantsBefore[1], (await ewelinkStore.AccountAsync(default))!.Envelope);
            var databaseProjection = await db.QueryAsync<string>("SELECT CONCAT(identifiers, credentials, operational_settings) FROM configuration_profiles");
            foreach (var value in new[] { growatt.ApiToken, daikin.ClientSecret!, daikin.TokenKey!, ewelink.AppSecret!, tuya.AccessSecret! })
                Assert.DoesNotContain(databaseProjection, row => row.Contains(value, StringComparison.Ordinal));

            growatt.PollSeconds = 360;
            Assert.False((await store.ImportProviderAsync("growatt", 1, growatt, false, null, protection, default)).Succeeded);
            Assert.False((await store.ImportProviderAsync("growatt", 1, growatt, false, 9, protection, default)).Succeeded);
            Assert.True((await store.ImportProviderAsync("growatt", 1, growatt, false, 1, protection, default)).Succeeded);
            Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_nonces"));
            await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
                store.LoadProviderAsync("daikin", null, Convert.ToBase64String(new byte[32]), protection, default));
            await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
                store.LoadProviderAsync("ewelink", 1, null, protection, default));
            await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
                store.LoadProviderAsync("ewelink", null, null, new EphemeralDataProtectionProvider(), default));

            using var host = Boot(runtime, vaultDirectory, protectionDirectory, daikin, ewelink, useVault: true);
            Assert.Equal(360, host.Services.GetRequiredService<GrowattOptions>().PollSeconds);
            Assert.Equal("slave", host.Services.GetRequiredService<GrowattOptions>().BatterySourceId);
            Assert.Equal(daikin.ClientId, host.Services.GetRequiredService<DaikinOptions>().ClientId);
            Assert.True(host.Services.GetRequiredService<DaikinOptions>().EnableWrites);
            Assert.True(host.Services.GetRequiredService<EwelinkOptions>().LiveElectricalConfigured);
            Assert.Equal(tuya.Scope, host.Services.GetRequiredService<TuyaOptions>().Scope);
            Assert.Equal(2, (await db.QueryAsync<long>("SELECT revision FROM configuration_profiles WHERE provider='growatt'")).Single());

            var activeDaikin = new DaikinSecrets(host.Services.GetRequiredService<DaikinOptions>());
            var activeEwelink = new EwelinkVault(host.Services.GetRequiredService<IDataProtectionProvider>());
            await daikinStore.SaveTokensAsync(owner, activeDaikin.Seal(owner, new("refreshed-access", "refreshed-grant", now)), "connected", default);
            await ewelinkStore.SaveTokensAsync(owner, activeEwelink.Seal(owner, ewelink.AppId!,
                new("eu", "refreshed-access", "refreshed-grant", now, now, "fixture-account-key")), default);
            using var rollback = Boot(runtime, vaultDirectory, protectionDirectory, daikin, ewelink, useVault: false);
            Assert.Equal("legacy-only-fixture", rollback.Services.GetRequiredService<GrowattOptions>().ApiToken);
            var legacyDaikin = rollback.Services.GetRequiredService<DaikinOptions>();
            var legacyEwelink = rollback.Services.GetRequiredService<EwelinkOptions>();
            Assert.True(legacyDaikin.IsConfigured);
            Assert.True(legacyEwelink.IsConfigured);
            Assert.Equal("refreshed-grant", new DaikinSecrets(legacyDaikin)
                .Open(owner, (await daikinStore.AccountAsync(default))!.Envelope!).RefreshToken);
            Assert.Equal("refreshed-grant", new EwelinkVault(rollback.Services.GetRequiredService<IDataProtectionProvider>())
                .Open(owner, legacyEwelink.AppId!, (await ewelinkStore.AccountAsync(default))!.Envelope!).Refresh);
        }
        finally
        {
            vaultDirectory.Delete(recursive: true);
            protectionDirectory.Delete(recursive: true);
        }
    }

    private static WebApplicationFactory<Program> Boot(MySqlConnectionStringBuilder db, DirectoryInfo vault,
        DirectoryInfo protection, DaikinOptions daikin, EwelinkOptions ewelink, bool useVault) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (name, value) in new Dictionary<string, string>
            {
                ["Database:Host"] = db.Server, ["Database:Port"] = db.Port.ToString(), ["Database:Name"] = db.Database,
                ["Database:User"] = db.UserID, ["Database:Password"] = db.Password, ["Database:SslMode"] = "None",
                ["DataProtection:KeyDirectory"] = protection.FullName, ["ConfigurationVault:KeyDirectory"] = vault.FullName,
                ["ConfigurationVault:ActiveKeyId"] = "fixture", ["Daikin:TokenKey"] = daikin.TokenKey!,
                ["Growatt:ApiToken"] = "legacy-only-fixture",
                ["Daikin:ClientId"] = useVault ? "legacy-only-fixture" : daikin.ClientId!,
                ["Daikin:ClientSecret"] = daikin.ClientSecret!, ["Daikin:OperatorEmail"] = daikin.OperatorEmail!,
                ["Daikin:RedirectUri"] = daikin.RedirectUri!,
                ["Ewelink:AppId"] = useVault ? "legacy-only-fixture" : ewelink.AppId!,
                ["Ewelink:AppSecret"] = ewelink.AppSecret!, ["Ewelink:OperatorEmail"] = ewelink.OperatorEmail!,
                ["Ewelink:RedirectUri"] = ewelink.RedirectUri!,
            }) builder.UseSetting(name, value);
            if (useVault)
            {
                foreach (var provider in new[] { "growatt", "daikin", "ewelink", "tuya" })
                    builder.UseSetting($"ConfigurationVault:Providers:{provider}:Source", "Vault");
                builder.UseSetting("ConfigurationVault:Providers:growatt:PropertyId", "1");
            }
            builder.ConfigureTestServices(services =>
            {
                var workers = services.Where(service => service.ServiceType == typeof(IHostedService) &&
                    (service.ImplementationType?.Assembly == typeof(Program).Assembly ||
                     service.ImplementationType?.Assembly == typeof(ProviderConfigurationStore).Assembly)).ToArray();
                foreach (var worker in workers) services.Remove(worker);
            });
        });

    private static MySqlConnectionStringBuilder Fixture(string variable, string user)
    {
        var options = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable)!);
        Assert.Equal("homeautomation_provider_test", options.Database);
        Assert.Contains(options.Server, new[] { "127.0.0.1", "localhost" });
        Assert.NotEqual(3306u, options.Port);
        Assert.Equal(user, options.UserID);
        return options;
    }

    private sealed class ProviderMySqlFact : FactAttribute
    {
        public ProviderMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminVariable)) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RuntimeVariable)))
                Skip = "Requires disposable MySQL and a scoped runtime account.";
        }
    }
}
