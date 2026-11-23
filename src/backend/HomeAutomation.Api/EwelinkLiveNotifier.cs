using HomeAutomation.Infrastructure.Ewelink;
using Microsoft.AspNetCore.SignalR;

namespace HomeAutomation.Api;

internal sealed class EwelinkLiveNotifier(IHubContext<MonitoringHub> hub) : IEwelinkLiveNotifier
{
    public Task ChangedAsync(long property, CancellationToken ct, bool electricalTelemetry = false) =>
        hub.Clients.Group(MonitoringHub.Group(property)).SendAsync("ewelinkChanged", new { propertyId = property, electricalTelemetry }, ct);
}
