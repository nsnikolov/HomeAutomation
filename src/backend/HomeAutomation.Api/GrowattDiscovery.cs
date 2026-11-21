using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Growatt;

namespace HomeAutomation.Api;

internal static class GrowattDiscovery
{
    public static async Task RunAsync(WebApplication app, bool databaseSetupRequested)
    {
        if (databaseSetupRequested)
        {
            app.Logger.LogError("Run Growatt discovery as a standalone command.");
            Environment.ExitCode = 1;
            return;
        }

        if (!app.Services.GetRequiredService<GrowattOptions>().HasCloudToken())
        {
            app.Logger.LogError("Set Growatt:ApiToken in a backend secret source before discovery.");
            Environment.ExitCode = 1;
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<GrowattCloudClient>();
        try
        {
            var plants = await client.CountPlantsAsync(timeout.Token);
            app.Logger.LogInformation("Growatt V1 plant discovery found {PlantCount} plants.", plants);
            var storage = await client.CountStorageDevicesAsync(timeout.Token);
            app.Logger.LogInformation("Growatt V4 device discovery found {DeviceCount} storage devices.", storage);
        }
        catch (Exception error) when (error is GrowattReadException or HttpRequestException or OperationCanceledException)
        {
            var reason = error switch
            {
                GrowattReadException provider => provider.Failure,
                OperationCanceledException => GrowattFailure.Timeout,
                _ => GrowattFailure.Unavailable
            };
            app.Logger.LogError("Growatt discovery failed with {Failure}.", reason);
            Environment.ExitCode = 1;
        }
    }
}
