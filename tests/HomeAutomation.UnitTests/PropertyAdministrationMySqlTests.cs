using Dapper;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

public sealed class PropertyAdministrationMySqlTests
{
    private const string ConnectionVariable = "HOMEAUTOMATION_OWNERSHIP_TEST_MYSQL";

    [OwnershipMySqlFact]
    public async Task CreationAndInitialPromotionAssignOwnershipOnlyToTheIntendedMembership()
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        Assert.Equal("homeautomation_ownership_test", connection.Database);
        Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1" });
        var db = new Database(connection.ConnectionString);
        var migrations = new Migrations(db);
        await migrations.ApplyAsync(default);
        var administration = new PropertyAdministration(db);
        var identity = new IdentityStore(db);
        await using var sql = await db.OpenAsync(default);
        Assert.Equal("countryside", await sql.QuerySingleAsync<string>("SELECT site_key FROM properties WHERE id=1"));
        Assert.Equal(0, await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM property_memberships WHERE property_id=1"));

        var creator = await CreateAccount();
        var other = await CreateAccount();
        await identity.GrantDemoAccessAsync(creator, default);
        Assert.Equal("Viewer", await Role(1, creator));

        var siteKey = "test-" + Guid.NewGuid().ToString("N");
        var created = await administration.CreateAsync(creator, siteKey, "New test property", "Europe/Sofia", default);
        Assert.Equal("Owner", await Role(created, creator));
        Assert.Equal("Viewer", await Role(1, creator));
        await Assert.ThrowsAsync<MySqlException>(() =>
            administration.CreateAsync(other, siteKey, "Duplicate", "UTC", default));
        Assert.Null(await Role(created, other));
        Assert.Equal("New test property", await sql.QuerySingleAsync<string>("SELECT name FROM properties WHERE id=@created", new { created }));

        var rejectedKey = "test-" + Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<MySqlException>(() =>
            administration.CreateAsync(Guid.NewGuid().ToString("D"), rejectedKey, "Rejected", "UTC", default));
        Assert.Equal(0, await sql.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM properties WHERE site_key=@rejectedKey", new { rejectedKey }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.PromoteInitialOwnerAsync(other, default));
        await identity.GrantDemoAccessAsync(other, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.PromoteInitialOwnerAsync(creator, default));
        Assert.Equal("Viewer", await Role(1, other));
        Assert.Equal("Viewer", await Role(1, creator));
        await sql.ExecuteAsync("DELETE FROM property_memberships WHERE property_id=1 AND user_id=@other", new { other });

        await sql.ExecuteAsync("UPDATE property_memberships SET role_code='Operator' WHERE property_id=1 AND user_id=@creator", new { creator });
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.PromoteInitialOwnerAsync(creator, default));
        Assert.Equal("Operator", await Role(1, creator));
        await sql.ExecuteAsync("UPDATE property_memberships SET role_code='Viewer' WHERE property_id=1 AND user_id=@creator", new { creator });

        var group = Guid.NewGuid().ToString("D");
        await sql.ExecuteAsync("""
            INSERT INTO dashboard_groups(property_id,id,position,title,hidden,collapsed) VALUES(1,@group,0,'Fixture',FALSE,TRUE)
            """, new { group });
        Assert.True(await administration.PromoteInitialOwnerAsync(creator, default));
        Assert.False(await administration.PromoteInitialOwnerAsync(creator, default));
        await identity.GrantDemoAccessAsync(creator, default);
        Assert.Equal("Owner", await Role(1, creator));
        Assert.Equal("Owner", await Role(created, creator));
        var store = new MonitoringDashboardStore(db, migrations, new TuyaOptions());
        var dashboard = await store.ReadAsync(1, creator, default);
        Assert.True(dashboard.CanEdit);
        Assert.Equal(group, Assert.Single(dashboard.Groups).Id);

        async Task<string> CreateAccount()
        {
            var email = Guid.NewGuid().ToString("N") + "@example.test";
            var user = new LocalUser { UserName = email, NormalizedUserName = email.ToUpperInvariant(), PasswordHash = "fixture" };
            Assert.True((await identity.CreateAsync(user, default)).Succeeded);
            return user.Id;
        }

        Task<string?> Role(long property, string user) => sql.QuerySingleOrDefaultAsync<string>(
            "SELECT role_code FROM property_memberships WHERE property_id=@property AND user_id=@user", new { property, user });
    }

    private sealed class OwnershipMySqlFact : FactAttribute
    {
        public OwnershipMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a fresh disposable homeautomation_ownership_test schema.";
        }
    }
}
