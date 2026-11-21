using System.Security.Claims;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Growatt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomeAutomation.Api;

internal static class MonitoringEndpoints
{
    public static void MapMonitoring(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/system/health", async (HealthCheckService health, SimulatorOptions options, PollProgress polling,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckHealthAsync(registration => registration.Tags.Contains("ready"), cancellationToken);
            var state = polling.Current;
            var current = !options.Enabled ? "disabled" : state.Failed ? "degraded" : state.LastSuccess is null ? "waiting"
                : clock.GetUtcNow() - state.LastSuccess > TimeSpan.FromSeconds(options.FreshnessSeconds)
                    ? "degraded" : "running";
            return Results.Ok(new { database = report.Status.ToString().ToLowerInvariant(), simulator = current, state.LastSuccess, options.PollSeconds });
        });

        var properties = api.MapGroup("/properties");
        properties.MapGet("/", (ClaimsPrincipal user, IMonitoringReader reader, CancellationToken cancellationToken) =>
            reader.PropertiesAsync(UserId(user), cancellationToken));

        var property = properties.MapGroup("/{propertyId:long}").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var propertyId = long.Parse((string)http.Request.RouteValues["propertyId"]!, System.Globalization.CultureInfo.InvariantCulture);
            var reader = http.RequestServices.GetRequiredService<IMonitoringReader>();
            return await reader.CanReadAsync(UserId(http.User), propertyId, http.RequestAborted)
                ? await next(context) : Results.NotFound();
        });
        property.MapGet("/rooms", (long propertyId, IMonitoringReader reader, CancellationToken cancellationToken) =>
            reader.RoomsAsync(propertyId, cancellationToken));
        property.MapGet("/state", (long propertyId, IMonitoringReader reader, CancellationToken cancellationToken) =>
            reader.StateAsync(propertyId, cancellationToken));
        property.MapGet("/growatt", async (long propertyId, GrowattOptions options, GrowattProgress progress,
            IGrowattMonitoringReader reader, CancellationToken cancellationToken) =>
        {
            var current = progress.Current;
            if (!options.Enabled || current is "notConfigured" or "invalidConfiguration" or "transportUnavailable")
                return Results.Ok(new { status = current, data = (PropertyState?)null });
            var data = await reader.GrowattStateAsync(propertyId, options.SiteKey, cancellationToken);
            return Results.Ok(new { status = data is null ? "notConfigured" : current, data });
        });
        property.MapGet("/devices", async (long propertyId, IMonitoringReader reader, CancellationToken cancellationToken) =>
            (await reader.StateAsync(propertyId, cancellationToken)).Devices);
        property.MapGet("/devices/{deviceId:long}", async (long propertyId, long deviceId,
            IMonitoringReader reader, CancellationToken cancellationToken) =>
            await reader.DeviceAsync(propertyId, deviceId, cancellationToken) is { } device
                ? Results.Ok(device) : Results.NotFound());
        property.MapGet("/devices/{deviceId:long}/capabilities", async (long propertyId, long deviceId,
            IMonitoringReader reader, CancellationToken cancellationToken) =>
            await reader.DeviceAsync(propertyId, deviceId, cancellationToken) is { } device
                ? Results.Ok(device.Endpoints.Select(endpoint => new { endpoint.Id, endpoint.Name, endpoint.Capabilities }))
                : Results.NotFound());
        property.MapGet("/telemetry", HistoryAsync);
    }

    private static async Task<IResult> HistoryAsync(long propertyId, long? endpointId, string metric, DateTimeOffset? from,
        DateTimeOffset? to, TimeProvider clock, IMonitoringReader reader, CancellationToken cancellationToken)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddHours(-1);
        if (!Enum.TryParse<Metric>(metric, true, out var parsed) || !Enum.IsDefined(parsed) ||
            !string.Equals(metric, parsed.ToString(), StringComparison.OrdinalIgnoreCase) ||
            endpointId is <= 0 || start >= end || end - start > TimeSpan.FromHours(24))
        {
            return Results.Problem(statusCode: 400, title: "Use a supported metric and a time range of at most 24 hours.");
        }
        return Results.Ok(await reader.HistoryAsync(propertyId, endpointId, parsed, start, end, cancellationToken));
    }

    internal static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
}

[Authorize]
public sealed class MonitoringHub(IMonitoringReader reader) : Hub
{
    internal static string Group(long propertyId) => $"property:{propertyId}";
    public async Task Subscribe(long propertyId)
    {
        if (Context.User is not { } user ||
            !await reader.CanReadAsync(MonitoringEndpoints.UserId(user), propertyId, Context.ConnectionAborted))
            throw new HubException("The property is unavailable.");

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(propertyId), Context.ConnectionAborted);
    }

    public Task Unsubscribe(long propertyId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(propertyId), Context.ConnectionAborted);
}
