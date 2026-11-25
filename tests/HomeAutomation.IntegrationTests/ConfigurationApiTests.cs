using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;

namespace HomeAutomation.IntegrationTests;

public sealed class ConfigurationApiTests(ApiTests.Host host) : IClassFixture<ApiTests.Host>
{
    private const string AdminConnection = "HOMEAUTOMATION_CONFIGURATION_TEST_MYSQL";
    private const string RuntimeConnection = "HOMEAUTOMATION_CONFIGURATION_TEST_RUNTIME";
    private const string PropertyRoute = "/api/v1/configuration/properties/1";
    private const string GlobalRoute = "/api/v1/configuration/global";

    [ConfigurationMySqlFact]
    public async Task ScopedConfigurationRedactsSecretsSerializesWritesAndRotatesOnMySql()
    {
        var admin = TestDatabase(AdminConnection, "root");
        var runtime = TestDatabase(RuntimeConnection, "configuration_runtime");
        var migrations = new Migrations(admin);
        await migrations.ApplyAsync(default);
        await migrations.ApplyAsync(default);
        var runtimeMigrations = new Migrations(runtime);
        Assert.True(await runtimeMigrations.VerifyConfigurationAsync(default));
        await using var db = await admin.OpenAsync(default);
        await db.ExecuteAsync("DELETE FROM schema_migrations WHERE name LIKE '%.014_configuration_vault.sql'");
        Assert.True(await runtimeMigrations.VerifyAsync(default, includeDashboard: true));
        Assert.False(await runtimeMigrations.VerifyConfigurationAsync(default));
        await migrations.ApplyAsync(default);
        Assert.True(await runtimeMigrations.VerifyConfigurationAsync(default));
        var editor = Guid.NewGuid().ToString("D");
        var viewer = Guid.NewGuid().ToString("D");
        var installationAdmin = Guid.NewGuid().ToString("D");
        await db.ExecuteAsync("""
            INSERT INTO identity_users(id,email,normalized_email,password_hash,security_stamp,concurrency_stamp)
            VALUES(@editor,'editor@example.test','EDITOR@EXAMPLE.TEST','fixture','fixture','fixture'),
                  (@viewer,'viewer@example.test','VIEWER@EXAMPLE.TEST','fixture','fixture','fixture'),
                  (@installationAdmin,'global@example.test','GLOBAL@EXAMPLE.TEST','fixture','fixture','fixture');
            INSERT INTO property_memberships(property_id,user_id,role_code) VALUES(1,@editor,'Owner'),(1,@viewer,'Viewer');
            INSERT INTO properties(id,site_key,name,time_zone) VALUES(2,'isolated','Isolated','Europe/Sofia');
            """, new { editor, viewer, installationAdmin });

        var directory = Directory.CreateTempSubdirectory("ha-config-api-");
        try
        {
            var options = new ConfigurationVaultOptions
                { ActiveKeyId = "old", KeyDirectory = directory.FullName, AdministratorUserId = installationAdmin };
            File.WriteAllBytes(Path.Combine(directory.FullName, "old.key"), RandomNumberGenerator.GetBytes(32));
            var store = new ProviderConfigurationStore(runtime, runtimeMigrations, options, TimeProvider.System);
            using var fixture = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production").ConfigureTestServices(services =>
            {
                services.RemoveAll<IProviderConfiguration>();
                services.AddSingleton<IProviderConfiguration>(store);
            }));
            using var anonymous = fixture.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(PropertyRoute)).StatusCode);
            var https = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") };
            using var insecure = fixture.CreateClient();
            insecure.DefaultRequestHeaders.Add("X-Test-User", editor);
            var insecureView = await insecure.GetFromJsonAsync<ConfigurationView>(PropertyRoute);
            Assert.True(insecureView!.RequiresHttps);
            Assert.False(insecureView.CanEdit);
            Assert.Equal(HttpStatusCode.UpgradeRequired, (await insecure.PutAsJsonAsync(PropertyRoute + "/growatt",
                new ProviderConfigurationUpdate(0, 300))).StatusCode);
            using var owner = fixture.CreateClient(https);
            owner.DefaultRequestHeaders.Add("X-Test-User", editor);
            const string synthetic = "fixture-credential-that-must-not-be-returned";
            var first = new ProviderConfigurationUpdate(0, 300, new Dictionary<string, string?> { ["apiToken"] = synthetic });
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(PropertyRoute + "/growatt", first)).StatusCode);
            await AddCsrfAsync(owner);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/configuration/properties/2")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/configuration/properties/2/growatt", first)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(GlobalRoute)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(PropertyRoute + "/growatt", first with { PollSeconds = 299 })).StatusCode);
            using var reader = fixture.CreateClient(https);
            reader.DefaultRequestHeaders.Add("X-Test-User", viewer);
            await AddCsrfAsync(reader);
            Assert.False((await reader.GetFromJsonAsync<ConfigurationView>(PropertyRoute))!.CanEdit);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(PropertyRoute + "/growatt", first)).StatusCode);
            using var globalAdmin = fixture.CreateClient(https);
            globalAdmin.DefaultRequestHeaders.Add("X-Test-User", installationAdmin);
            await AddCsrfAsync(globalAdmin);
            Assert.True((await globalAdmin.GetFromJsonAsync<ConfigurationView>(GlobalRoute))!.CanEdit);
            Assert.Equal(HttpStatusCode.NotFound, (await globalAdmin.GetAsync(PropertyRoute)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await globalAdmin.PutAsJsonAsync(GlobalRoute + "/growatt", first)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync(PropertyRoute + "/growatt", first)).StatusCode);

            var viewResponse = await owner.GetAsync(PropertyRoute);
            Assert.True(viewResponse.Headers.CacheControl?.NoStore);
            var json = await viewResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(synthetic, json);
            Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("keyId", json, StringComparison.OrdinalIgnoreCase);
            var view = (await owner.GetFromJsonAsync<ConfigurationView>(PropertyRoute))!.Providers.Single(p => p.Provider == "growatt");
            Assert.True(view.Credentials["apiToken"]);
            var refresh = new ProviderConfigurationUpdate(view.Revision, 360,
                new Dictionary<string, string?> { ["apiToken"] = "" });
            var writers = await Task.WhenAll(owner.PutAsJsonAsync(PropertyRoute + "/growatt", refresh),
                owner.PutAsJsonAsync(PropertyRoute + "/growatt", refresh with { PollSeconds = 420 }));
            Assert.Single(writers, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(writers, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.All(writers, response => response.Dispose());
            var mirror = new ProviderConfigurationStore(runtime, runtimeMigrations, options, TimeProvider.System);
            view = (await mirror.ReadAsync(1, editor, default))!.Providers.Single(p => p.Provider == "growatt");
            Assert.Contains(view.PollSeconds, new[] { 360, 420 });
            Assert.Equal(2, view.Revision);
            var stored = await db.QuerySingleAsync<string>("SELECT credentials FROM configuration_profiles WHERE scope_id=1");
            Assert.DoesNotContain(synthetic, stored);
            var saved = JsonSerializer.Deserialize<Dictionary<string, CredentialEnvelope>>(stored)!["apiToken"];
            Assert.Equal("old", saved.KeyId);
            Assert.Equal(synthetic, System.Text.Encoding.UTF8.GetString(new ConfigurationKeys(options).Open(1, "growatt", "apiToken", saved)));

            File.WriteAllBytes(Path.Combine(directory.FullName, "next.key"), RandomNumberGenerator.GetBytes(32));
            options.ActiveKeyId = "next";
            Assert.Equal(2, await store.RotateAsync(default));
            Assert.Equal(0, await store.RotateAsync(default));
            var rotated = await db.QuerySingleAsync<string>("SELECT credentials FROM configuration_profiles WHERE scope_id=1");
            var envelope = JsonSerializer.Deserialize<Dictionary<string, CredentialEnvelope>>(rotated)!["apiToken"];
            Assert.Equal("next", envelope.KeyId);
            Assert.Equal(synthetic, System.Text.Encoding.UTF8.GetString(new ConfigurationKeys(options).Open(1, "growatt", "apiToken", envelope)));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync(PropertyRoute + "/growatt",
                new ProviderConfigurationUpdate(view.Revision + 1, view.PollSeconds, Remove: ["apiToken"]))).StatusCode);
            view = (await owner.GetFromJsonAsync<ConfigurationView>(PropertyRoute))!.Providers.Single(p => p.Provider == "growatt");
            Assert.False(view.Credentials["apiToken"]);
            options.ActiveKeyId = "missing";
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PutAsJsonAsync(PropertyRoute + "/growatt",
                first with { Revision = view.Revision })).StatusCode);
            Assert.Equal(view.Revision,
                (await owner.GetFromJsonAsync<ConfigurationView>(PropertyRoute))!.Providers.Single(p => p.Provider == "growatt").Revision);
            var audit = await db.QueryAsync<string>("""
                SELECT CONCAT(actor, '|', COALESCE(property_id, 0), '|', provider, '|', setting, '|', action)
                FROM configuration_audit ORDER BY id
                """);
            Assert.Contains($"{editor}|1|growatt|apiToken|created", audit);
            Assert.Contains($"{editor}|1|growatt|apiToken|removed", audit);
            Assert.Contains("system:key-rotation|1|growatt|apiToken|credentialRotated", audit);
            Assert.DoesNotContain(audit, item => item.Contains(synthetic, StringComparison.Ordinal));
            Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM configuration_nonces"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM configuration_nonces WHERE key_id=@KeyId AND nonce=@Nonce", new { saved.KeyId, saved.Nonce }));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static Database TestDatabase(string variable, string user)
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable) ??
            throw new InvalidOperationException("An isolated configuration fixture is required."));
        Assert.Equal("homeautomation_configuration_test", connection.Database);
        Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1" });
        Assert.NotEqual(3306u, connection.Port);
        Assert.Equal(user, connection.UserID);
        return new(connection.ConnectionString);
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
    }

    private sealed class ConfigurationMySqlFact : FactAttribute
    {
        public ConfigurationMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminConnection)) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RuntimeConnection)))
                Skip = "Requires an isolated loopback MySQL configuration fixture and scoped runtime account.";
        }
    }
}
