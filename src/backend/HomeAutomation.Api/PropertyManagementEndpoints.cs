using HomeAutomation.Application;
using Microsoft.AspNetCore.Antiforgery;

namespace HomeAutomation.Api;

/// <summary>
/// Property, room and device management. The store enforces every role: the installation administrator creates properties,
/// Owner and Admin manage a property, and only an Owner deletes it. Mutations require the antiforgery token.
/// </summary>
internal static class PropertyManagementEndpoints
{
    public static void MapPropertyAdministration(this RouteGroupBuilder api)
    {
        var management = api.MapGroup("/property-management").AddEndpointFilter(Antiforgery);
        management.MapGet("", (HttpContext http, IPropertyManagement store, CancellationToken ct) =>
            store.ListAsync(MonitoringEndpoints.UserId(http.User), ct));
        management.MapPost("/properties", async (CreatePropertyRequest request, HttpContext http, IPropertyManagement store, CancellationToken ct) =>
            Result(await store.CreateAsync(MonitoringEndpoints.UserId(http.User), request, ct), created: true));
    }

    /// <summary>Routes under the property membership filter; a non-member receives 404 before the store is reached.</summary>
    public static void MapPropertyManagement(this RouteGroupBuilder property)
    {
        var routes = property.MapGroup("").AddEndpointFilter(Antiforgery);
        routes.MapPut("/details", (long propertyId, UpdatePropertyRequest request, HttpContext http, IPropertyManagement store,
            IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId], store.UpdateAsync(MonitoringEndpoints.UserId(http.User), propertyId, request, ct), ct));
        routes.MapGet("/deletion-impact", async (long propertyId, HttpContext http, IPropertyManagement store, CancellationToken ct) =>
            await store.DeletionImpactAsync(MonitoringEndpoints.UserId(http.User), propertyId, ct) is { } impact ? Results.Ok(impact) : Results.NotFound());
        routes.MapPost("/delete", async (long propertyId, DeletePropertyRequest request, HttpContext http, IPropertyManagement store,
            CancellationToken ct) => Result(await store.DeleteAsync(MonitoringEndpoints.UserId(http.User), propertyId, request, ct)));

        routes.MapGet("/room-management", async (long propertyId, HttpContext http, IPropertyManagement store, CancellationToken ct) =>
            await store.RoomsAsync(MonitoringEndpoints.UserId(http.User), propertyId, ct) is { } rooms ? Results.Ok(rooms) : Results.NotFound());
        routes.MapPost("/room-management", async (long propertyId, RoomRequest request, HttpContext http, IPropertyManagement store,
            IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            await ChangeAsync(notifier, [propertyId], store.CreateRoomAsync(MonitoringEndpoints.UserId(http.User), propertyId, request, ct), ct, created: true));
        routes.MapPut("/room-management/{roomId:long}", (long propertyId, long roomId, RoomRequest request, HttpContext http,
            IPropertyManagement store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId], store.UpdateRoomAsync(MonitoringEndpoints.UserId(http.User), propertyId, roomId, request, ct), ct));
        routes.MapDelete("/room-management/{roomId:long}", (long propertyId, long roomId, HttpContext http, IPropertyManagement store,
            IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId], store.DeleteRoomAsync(MonitoringEndpoints.UserId(http.User), propertyId, roomId, ct), ct));

        routes.MapPut("/devices/{deviceId:long}/name", (long propertyId, long deviceId, DeviceNameRequest request, HttpContext http,
            IPropertyManagement store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId], store.RenameDeviceAsync(MonitoringEndpoints.UserId(http.User), propertyId, deviceId, request, ct), ct));
        routes.MapPut("/devices/{deviceId:long}/room", (long propertyId, long deviceId, DeviceRoomRequest request, HttpContext http,
            IPropertyManagement store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId], store.SetDeviceRoomAsync(MonitoringEndpoints.UserId(http.User), propertyId, deviceId, request, ct), ct));
        routes.MapGet("/devices/{deviceId:long}/audit", async (long propertyId, long deviceId, HttpContext http, IPropertyManagement store,
            CancellationToken ct) =>
            await store.DeviceAuditAsync(MonitoringEndpoints.UserId(http.User), propertyId, deviceId, ct) is { } audit ? Results.Ok(audit) : Results.NotFound());
        routes.MapGet("/transferable-devices", (long propertyId, HttpContext http, IPropertyManagement store, CancellationToken ct) =>
            store.TransferableAsync(MonitoringEndpoints.UserId(http.User), propertyId, ct));
        routes.MapGet("/devices/{deviceId:long}/transfer-impact", async (long propertyId, long deviceId, long? destination, HttpContext http,
            IPropertyManagement store, CancellationToken ct) =>
        {
            if (destination is null) return Results.Problem(statusCode: 400, title: "Choose a destination property.");
            return await store.TransferImpactAsync(MonitoringEndpoints.UserId(http.User), propertyId, deviceId, destination.Value, ct) is { } impact
                ? Results.Ok(impact) : Results.NotFound();
        });
        routes.MapPost("/devices/{deviceId:long}/transfer", (long propertyId, long deviceId, DeviceTransferRequest request, HttpContext http,
            IPropertyManagement store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(notifier, [propertyId, request.DestinationPropertyId],
                store.TransferAsync(MonitoringEndpoints.UserId(http.User), propertyId, deviceId, request, ct), ct));
    }

    /// <summary>
    /// Records a provider-page assignment after the provider service committed it. The assignment is not undone when the audit
    /// write fails; the failure is logged without provider data.
    /// </summary>
    /// <summary>The provider record's assignment before a provider-page change; null when it cannot be read, which never blocks the change.</summary>
    internal static async Task<ProviderAssignment?> AssignmentBeforeAsync(HttpContext http, IDeviceAssignmentAudit audit, string provider,
        string device, CancellationToken ct)
    {
        try { return await audit.CurrentAsync(provider, device, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HomeAutomation.DeviceManagement")
                .LogWarning("Device assignment audit for {Provider} could not read the prior assignment ({ErrorType}).", provider, error.GetType().Name);
            return null;
        }
    }

    internal static async Task RecordAssignmentAsync(HttpContext http, IDeviceAssignmentAudit audit, string provider,
        ProviderAssignment? before, string device, CancellationToken ct)
    {
        try { await audit.RecordAsync(MonitoringEndpoints.UserId(http.User), provider, before, device, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HomeAutomation.DeviceManagement")
                .LogWarning("Device assignment audit for {Provider} failed with {ErrorType}.", provider, error.GetType().Name);
        }
    }

    private static async ValueTask<object?> Antiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        }
        return await next(context);
    }

    /// <summary>Notifies the affected properties only after the change committed, so open views refresh without a reload.</summary>
    private static async Task<IResult> ChangeAsync(IMonitoringDashboardNotifier notifier, long[] properties, Task<ManagementResult> change,
        CancellationToken ct, bool created = false)
    {
        var result = await change;
        if (result.Status == ManagementStatus.Done)
            foreach (var property in properties.Distinct())
                await notifier.ChangedAsync(property, ct);
        return Result(result, created);
    }

    private static IResult Result(ManagementResult result, bool created = false)
    {
        if (result.Status == ManagementStatus.Done)
            return created ? Results.Created((string?)null, new { id = result.Id }) : Results.Ok(new { id = result.Id });
        var (status, code) = result.Status switch
        {
            ManagementStatus.NotFound => (404, "notFound"),
            ManagementStatus.Forbidden => (403, "forbidden"),
            ManagementStatus.Conflict => (409, "conflict"),
            ManagementStatus.SchemaPending => (503, "schemaPending"),
            _ => (400, "invalid")
        };
        return Results.Problem(statusCode: status, title: result.Message, extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
