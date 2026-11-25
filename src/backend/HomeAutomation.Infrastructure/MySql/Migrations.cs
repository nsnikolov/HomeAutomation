using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class Migrations(Database database, ILogger<Migrations>? logger = null)
{
    private static readonly string[] Scripts = typeof(Migrations).Assembly.GetManifestResourceNames()
        .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        const string lockName = "homeautomation:schema-migrations";
        var args = new { Name = lockName };
        if (await connection.ExecuteScalarAsync<int?>(Query("SELECT GET_LOCK(@Name, 30)", cancellationToken, args)) != 1)
            throw new InvalidOperationException("Could not acquire the schema migration lock.");

        try
        {
            await connection.ExecuteAsync(Query(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name VARCHAR(160) NOT NULL PRIMARY KEY,
                    checksum CHAR(64) NOT NULL,
                    applied_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                ) ENGINE=InnoDB
                """, cancellationToken));

            foreach (var name in Scripts)
            {
                using var stream = typeof(Migrations).Assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = (await reader.ReadToEndAsync(cancellationToken)).ReplaceLineEndings("\n");
                var checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));
                var migration = new { Name = name, Checksum = checksum };
                var saved = await connection.QuerySingleOrDefaultAsync<string>(
                    Query("SELECT checksum FROM schema_migrations WHERE name = @Name", cancellationToken, migration));
                if (saved == checksum) continue;
                if (saved is not null)
                    throw new InvalidOperationException($"Checksum mismatch for migration {name}.");

                try
                {
                    await connection.ExecuteAsync(Query(sql, cancellationToken));
                    await connection.ExecuteAsync(Query(
                        "INSERT INTO schema_migrations (name, checksum) VALUES (@Name, @Checksum)", cancellationToken, migration));
                }
                catch (MySqlException error)
                {
                    throw new InvalidOperationException(
                        $"Migration {name} failed with MySQL error {error.ErrorCode} (SQL state {error.SqlState}).", error);
                }
            }
        }
        finally
        {
            await connection.ExecuteScalarAsync<int?>(Query("SELECT RELEASE_LOCK(@Name)", CancellationToken.None, args));
        }
    }

    private const string EwelinkReceiveTimeMigration = ".010_ewelink_receive_time_provenance.sql";
    private volatile bool receiveTimeReady;

    internal async Task<bool> VerifyEwelinkReceiveTimeAsync(CancellationToken ct)
    {
        if (receiveTimeReady) return true;
        var name = Scripts.Single(script => script.EndsWith(EwelinkReceiveTimeMigration, StringComparison.Ordinal));
        await using var connection = await database.OpenAsync(ct);
        receiveTimeReady = await connection.ExecuteScalarAsync<bool>(Query(
            "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=@name)", ct, new { name }));
        return receiveTimeReady;
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken, bool includeDaikin = false, bool includeEwelink = false,
        bool includeTuya = false, bool includePreferences = false, bool includeDashboard = false)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        try
        {
            var applied = await connection.QueryAsync<string>(Query("SELECT name FROM schema_migrations", cancellationToken));
            var installed = applied.ToHashSet(StringComparer.Ordinal);
            var required = Scripts.Where(name => (includeDaikin || !name.EndsWith(".003_daikin_onecta.sql", StringComparison.Ordinal)) &&
                (includeEwelink || !name.EndsWith(".004_ewelink_cloud.sql", StringComparison.Ordinal)) &&
                (includeEwelink || !name.EndsWith(".007_ewelink_sync_budget.sql", StringComparison.Ordinal)) &&
                (includeEwelink || !name.EndsWith(".008_ewelink_relay_commands.sql", StringComparison.Ordinal)) &&
                (includeEwelink || !name.EndsWith(".009_ewelink_relay_preferences.sql", StringComparison.Ordinal)) &&
                !name.EndsWith(EwelinkReceiveTimeMigration, StringComparison.Ordinal) &&
                (includeTuya || !name.EndsWith(".005_tuya_cloud.sql", StringComparison.Ordinal)) &&
                (includeDashboard || !name.EndsWith(".011_monitoring_dashboard.sql", StringComparison.Ordinal)) &&
                (includeDashboard || !name.EndsWith(".012_dashboard_widget_layout.sql", StringComparison.Ordinal)) &&
                (includeDashboard || !name.EndsWith(".013_dashboard_measurement_colors.sql", StringComparison.Ordinal)) &&
                (includePreferences || !name.EndsWith(".006_device_view_preferences.sql", StringComparison.Ordinal)));
            if (!includeDashboard) return required.All(installed.Contains);
            var missing = required.Where(name => !installed.Contains(name)).ToArray();
            if (missing.Length != 0)
                logger?.LogWarning("Dashboard schema migration records are missing: {MissingMigrations}", string.Join(", ", missing));
            return missing.Length == 0;
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            if (includeDashboard)
                logger?.LogWarning("Dashboard schema migration ledger is missing ({ErrorCode}, SQL state {SqlState}).",
                    error.ErrorCode, error.SqlState);
            return false;
        }
    }
}
