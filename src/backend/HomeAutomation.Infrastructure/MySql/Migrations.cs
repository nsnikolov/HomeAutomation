using System.Security.Cryptography;
using Dapper;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class Migrations(Database database)
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

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        try
        {
            var applied = await connection.QueryAsync<string>(Query("SELECT name FROM schema_migrations", cancellationToken));
            return applied.ToHashSet(StringComparer.Ordinal).SetEquals(Scripts);
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return false;
        }
    }
}
