using System.Globalization;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class SimulatorCleanupArgumentException(string message) : Exception(message);

/// <summary>
/// Explicit removal of stored simulator data for one property. The default is a read-only plan. Deletion needs both
/// <c>--execute</c> and <c>--confirm-simulator-data-deletion</c>, a disabled simulator, and an account with DELETE on the
/// affected tables. It never runs at startup or during migration.
/// </summary>
internal static class SimulatorCleanupCommand
{
    public const string Command = "--cleanup-simulator-data";
    private const string Property = "--simulator-property";
    private const string Execute = "--execute";
    private const string Confirm = "--confirm-simulator-data-deletion";

    internal sealed record Options(long PropertyId, bool Execute);

    public static bool IsArgument(string arg) => arg is Command or Execute or Confirm || arg == Property ||
        arg.StartsWith(Property + "=", StringComparison.Ordinal);

    public static Options? Parse(string[] args)
    {
        var arguments = args.Where(IsArgument).ToArray();
        if (!arguments.Contains(Command))
        {
            if (arguments.Any(arg => arg != Execute))
                throw new SimulatorCleanupArgumentException($"Use {Property} and {Confirm} only with {Command}.");
            return null;
        }
        var values = arguments.Where(arg => arg.StartsWith(Property + "=", StringComparison.Ordinal)).ToArray();
        if (arguments.Count(arg => arg == Command) != 1 || arguments.Count(arg => arg == Execute) > 1 || arguments.Count(arg => arg == Confirm) > 1 ||
            values.Length != 1 || arguments.Contains(Property))
            throw new SimulatorCleanupArgumentException($"Use {Command} with one {Property}=<id>, and {Execute} {Confirm} to delete.");
        var execute = arguments.Contains(Execute);
        if (execute != arguments.Contains(Confirm))
            throw new SimulatorCleanupArgumentException($"Deleting needs both {Execute} and {Confirm}. Omit both for a dry run.");
        return long.TryParse(values[0][(Property.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? new(id, execute)
            : throw new SimulatorCleanupArgumentException("Use a positive numeric property identifier.");
    }

    public static async Task<int> RunAsync(WebApplication app, Options options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var logger = app.Logger;
        var cleanup = new SimulatorDataCleanup(app.Services.GetRequiredService<Database>());
        try
        {
            if (options.Execute && app.Services.GetRequiredService<SimulatorOptions>().Enabled)
            {
                logger.LogError("Simulator cleanup refuses to delete while Simulator:Enabled is true. Disable the simulator and stop any backend that runs it first.");
                return 1;
            }
            var plan = await cleanup.PlanAsync(options.PropertyId, timeout.Token);
            if (!plan.PropertyExists)
            {
                logger.LogError("Simulator cleanup found no property {PropertyId}.", options.PropertyId);
                return 1;
            }
            Report(logger, plan, options.Execute);
            if (plan.IsEmpty)
            {
                logger.LogInformation("Nothing to delete for property {PropertyId}. Rerunning is safe.", options.PropertyId);
                return 0;
            }
            if (!options.Execute)
            {
                logger.LogInformation("Dry run complete. Nothing was written. Review the counts and conflicts, back up the database, then add {Execute} {Confirm}.",
                    Execute, Confirm);
                return 0;
            }
            var deleted = await cleanup.ExecuteAsync(plan, timeout.Token);
            foreach (var (table, rows) in deleted)
                logger.LogInformation("Simulator cleanup deleted {Rows} rows from {Table} for property {PropertyId}.", rows, table, options.PropertyId);
            logger.LogInformation("Simulator cleanup completed for property {PropertyId}: {Devices} devices, {Series} series and {LogicalDevices} logical devices removed. Keep this output as the cleanup record. Rerunning is safe.",
                options.PropertyId, plan.DeviceIds.Count, plan.SeriesIds.Count, plan.LogicalDeviceIds.Count);
            return 0;
        }
        catch (SimulatorCleanupException error)
        {
            logger.LogError("Simulator cleanup stopped: {Reason} Committed batches of simulator samples stay deleted; rerun to finish.", error.Message);
            return 1;
        }
        catch (MySqlException error) when (error.ErrorCode is MySqlErrorCode.TableAccessDenied or MySqlErrorCode.AccessDenied
            or MySqlErrorCode.DatabaseAccessDenied)
        {
            logger.LogError("Simulator cleanup needs SELECT and DELETE on the affected tables and UPDATE on properties (MySQL error {ErrorCode}). Run it with the maintenance account described in the README.",
                error.ErrorCode);
            return 1;
        }
        catch (MySqlException error)
        {
            logger.LogError("Simulator cleanup failed with MySQL error {ErrorCode} and SQL state {SqlState}. The structural transaction was rolled back; rerun after resolving it.",
                error.ErrorCode, error.SqlState);
            return 1;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Simulator cleanup timed out. Committed sample batches stay deleted; rerun to finish.");
            return 1;
        }
    }

    private static void Report(ILogger logger, SimulatorCleanupPlan plan, bool execute)
    {
        logger.LogInformation("Simulator cleanup plan for property {PropertyId} ({Mode}). Ownership: devices bound only to the simulator connection with simulator inventory keys, and series with source simulator.",
            plan.PropertyId, execute ? "execute" : "dry run");
        if (plan.AbsentOptionalTables.Count > 0)
            logger.LogInformation("Tables not present in this schema, skipped: {Tables}.", string.Join(", ", plan.AbsentOptionalTables));
        foreach (var (table, rows) in plan.Counts)
            logger.LogInformation("Planned deletion: {Rows} rows from {Table}.", rows, table);
        foreach (var conflict in plan.Conflicts)
            logger.LogWarning("Preserved {Kind} {Id} \"{Name}\": {Reason}.", conflict.Kind, conflict.Id, conflict.Name, conflict.Reason);
        foreach (var reference in plan.DashboardReferences)
            logger.LogWarning("Dashboard widget \"{Widget}\" uses {SourceKey} ({Reason}). The layout is not changed; it shows the measurement as unavailable until you edit it.",
                reference.WidgetTitle, reference.SourceKey, reference.Reason);
        logger.LogInformation("Seeded rows are kept: the property, rooms, installations and the simulator connection.");
    }
}
