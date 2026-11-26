using System.Globalization;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class LogicalDeviceInitializationException(string message) : Exception(message);

/// <summary>
/// Explicit, idempotent creation of logical identities for existing physical devices. The default is a read-only plan;
/// <c>--execute</c> writes all planned identities for the property in one transaction.
/// </summary>
internal static class LogicalDeviceInitializationCommand
{
    public const string Command = "--initialize-logical-devices";
    private const string Property = "--logical-property";
    private const string Execute = "--execute";

    internal sealed record Options(long PropertyId, bool Execute);

    public static bool IsArgument(string arg) => arg is Command or Execute || arg == Property ||
        arg.StartsWith(Property + "=", StringComparison.Ordinal);

    public static Options? Parse(string[] args)
    {
        var arguments = args.Where(IsArgument).ToArray();
        if (!arguments.Contains(Command))
        {
            if (arguments.Any(arg => arg != Execute))
                throw new LogicalDeviceInitializationException($"Use {Property} only with {Command}.");
            return null;
        }
        var values = arguments.Where(arg => arg.StartsWith(Property + "=", StringComparison.Ordinal)).ToArray();
        if (arguments.Count(arg => arg == Command) != 1 || arguments.Count(arg => arg == Execute) > 1 || values.Length != 1 ||
            arguments.Contains(Property))
            throw new LogicalDeviceInitializationException($"Use {Command} with one {Property}=<id> and optionally {Execute}.");
        return long.TryParse(values[0][(Property.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? new(id, arguments.Contains(Execute))
            : throw new LogicalDeviceInitializationException("Use a positive numeric property identifier.");
    }

    public static async Task<int> RunAsync(WebApplication app, Options options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var logger = app.Logger;
        try
        {
            if (!await app.Services.GetRequiredService<Migrations>().VerifyLogicalDevicesAsync(timeout.Token))
            {
                logger.LogError("Logical device initialization requires migrations 017, 018 and 019. Apply migrations with the migration account first.");
                return 1;
            }
            await using var scope = app.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<LogicalDeviceStore>();
            var plan = await store.PlanInitializationAsync(options.PropertyId, timeout.Token);
            if (!plan.PropertyExists)
            {
                logger.LogError("Logical device initialization found no property {PropertyId}.", options.PropertyId);
                return 1;
            }
            foreach (var item in plan.Items)
                logger.LogInformation("Initialization plan: device {DeviceId} \"{DeviceName}\" from {Provider}, {SeriesCount} telemetry series, channels {Channels}. {Outcome}",
                    item.DeviceId, item.DeviceName, item.Provider, item.SeriesCount,
                    string.Join(", ", item.Channels.Select(channel => channel.EndpointId is { } endpoint ? $"{channel.ChannelKey} (endpoint {endpoint})" : channel.ChannelKey)),
                    item.Skipped is null ? "Will be created." : "Skipped: " + item.Skipped + ".");
            logger.LogInformation("Initialization plan for property {PropertyId}: {CreateCount} logical devices to create, {SkipCount} skipped. Mode: {Mode}.",
                options.PropertyId, plan.Work.Count(), plan.Items.Count - plan.Work.Count(), options.Execute ? "execute" : "dry run");
            if (!options.Execute)
            {
                logger.LogInformation("Dry run complete. Nothing was written. Add {ExecuteFlag} to create the planned logical devices.", Execute);
                return 0;
            }
            var created = await store.ExecuteInitializationAsync(options.PropertyId, timeout.Token);
            logger.LogInformation("Created {CreateCount} logical devices for property {PropertyId}. Rerunning skips them.", created, options.PropertyId);
            return 0;
        }
        catch (MySqlException error)
        {
            logger.LogError("Logical device initialization failed with MySQL error {ErrorCode} and SQL state {SqlState}. Nothing was committed.",
                error.ErrorCode, error.SqlState);
            return 1;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Logical device initialization timed out. Nothing was committed.");
            return 1;
        }
    }
}
