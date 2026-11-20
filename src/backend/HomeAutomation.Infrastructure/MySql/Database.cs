using Dapper;
using MySqlConnector;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class Database
{
    private readonly string connectionString;

    public Database(string connectionString)
    {
        this.connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Utc,
            GuidFormat = MySqlGuidFormat.None
        }.ConnectionString;
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(Query("SET time_zone = '+00:00'", cancellationToken));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static CommandDefinition Query(
        string sql, CancellationToken cancellationToken, object? parameters = null, MySqlTransaction? transaction = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    internal static DateTimeOffset? Utc(DateTime? value) => value is { } instant ? Utc(instant) : null;
}
