using HomeAutomation.Application;
using Microsoft.AspNetCore.SignalR;

namespace HomeAutomation.Api;

internal sealed class MonitoringDashboardNotifier(IHubContext<MonitoringHub> hub, ILogger<MonitoringDashboardNotifier> logger)
    : IMonitoringDashboardNotifier
{
    public async Task ChangedAsync(long property, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(MonitoringHub.Group(property)).SendAsync("dashboardChanged", new { propertyId = property }, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Dashboard notification failed for property {PropertyId}", property);
        }
    }
}
