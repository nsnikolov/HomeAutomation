using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class RoomAssignmentClearingArgumentException(string message) : Exception(message);

/// <summary>
/// One-time administrative removal of every current device-to-room assignment in device rows, logical devices and the Daikin,
/// eWeLink and Tuya assignments they are synchronized from. A read-only report unless both <c>--execute</c> and
/// <c>--confirm-room-assignment-clearing</c> are given. Rooms, devices, bindings, telemetry and history are untouched.
/// </summary>
internal static class RoomAssignmentClearingCommand
{
    public const string Command = "--clear-room-assignments";
    private const string Execute = "--execute";
    private const string Confirm = "--confirm-room-assignment-clearing";

    internal sealed record Options(bool Execute);

    public static bool IsArgument(string arg) => arg is Command or Execute or Confirm;

    public static Options? Parse(string[] args)
    {
        var arguments = args.Where(IsArgument).ToArray();
        if (!arguments.Contains(Command))
        {
            if (arguments.Contains(Confirm)) throw new RoomAssignmentClearingArgumentException($"Use {Confirm} only with {Command}.");
            return null;
        }
        if (arguments.Count(arg => arg == Command) != 1 || arguments.Count(arg => arg == Execute) > 1 || arguments.Count(arg => arg == Confirm) > 1)
            throw new RoomAssignmentClearingArgumentException($"Use {Command} once, and {Execute} {Confirm} once each to clear.");
        var execute = arguments.Contains(Execute);
        if (execute != arguments.Contains(Confirm))
            throw new RoomAssignmentClearingArgumentException($"Clearing needs both {Execute} and {Confirm}. Omit both for a dry run.");
        return new(execute);
    }

    public static async Task<int> RunAsync(WebApplication app, Options options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var logger = app.Logger;
        var store = new PropertyManagementStore(app.Services.GetRequiredService<Database>(),
            app.Services.GetRequiredService<ConfigurationVaultOptions>(), TimeProvider.System);
        try
        {
            var plan = await store.PlanRoomAssignmentClearingAsync(timeout.Token);
            logger.LogInformation("Room assignment clearing ({Mode}): {Rows} assignments in {Properties} properties. Rooms, devices, bindings, telemetry, alerts and vault profiles are not changed.",
                options.Execute ? "execute" : "dry run", plan.Sum(item => item.Rows), plan.Select(item => item.Property).Distinct().Count());
            foreach (var (property, table, rows) in plan)
                logger.LogInformation("Property {PropertyId}: {Rows} room assignments in {Table}.", property, rows, table);
            if (plan.Count == 0)
            {
                logger.LogInformation("No room assignments exist. Rerunning is safe.");
                return 0;
            }
            if (!options.Execute)
            {
                logger.LogInformation("Dry run complete. Nothing was written. Back up the database, then add {Execute} {Confirm}.", Execute, Confirm);
                return 0;
            }
            var cleared = await store.ClearRoomAssignmentsAsync("system:room-assignment-clearing", timeout.Token);
            foreach (var (property, table, rows) in cleared)
                logger.LogInformation("Property {PropertyId}: cleared {Rows} room assignments in {Table}.", property, rows, table);
            logger.LogInformation("Room assignment clearing committed in one transaction. Rerun the dry run to confirm nothing remains.");
            return 0;
        }
        catch (MySqlException error)
        {
            logger.LogError("Room assignment clearing failed with MySQL error {ErrorCode} and SQL state {SqlState}. The transaction was rolled back.",
                error.ErrorCode, error.SqlState);
            return 1;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Room assignment clearing timed out. The transaction was rolled back.");
            return 1;
        }
    }
}
