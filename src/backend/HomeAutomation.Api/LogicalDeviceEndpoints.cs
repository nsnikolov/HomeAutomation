using HomeAutomation.Application;
using HomeAutomation.Domain;
using Microsoft.AspNetCore.Antiforgery;

namespace HomeAutomation.Api;

/// <summary>
/// Logical device routes under the property membership filter. Any member reads. Owner and Admin change assignments; the
/// store enforces the role and serializes each change on the property row.
/// </summary>
internal static class LogicalDeviceEndpoints
{
    public static void MapLogicalDevices(this RouteGroupBuilder property)
    {
        var logical = property.MapGroup("/logical-devices").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            return await http.RequestServices.GetRequiredService<ILogicalDevices>().SchemaReadyAsync(http.RequestAborted)
                ? await next(context)
                : Results.Problem(statusCode: 503, title: "Logical devices require migrations 017, 018 and 019.");
        });

        logical.MapGet("", (long propertyId, bool? includeArchived, HttpContext http, ILogicalDevices store, CancellationToken ct) =>
            store.ListAsync(propertyId, MonitoringEndpoints.UserId(http.User), includeArchived == true, ct));
        logical.MapGet("/{id:long}", async (long propertyId, long id, HttpContext http, ILogicalDevices store, CancellationToken ct) =>
            await store.GetAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        logical.MapGet("/{id:long}/candidates", async (long propertyId, long id, string? channel, ILogicalDevices store, CancellationToken ct) =>
        {
            channel ??= LogicalDeviceRules.DefaultChannel;
            if (!LogicalDeviceRules.ValidChannel(channel)) return Invalid("Use a valid channel key.");
            return await store.CandidatesAsync(propertyId, id, channel, ct) is { } candidates ? Results.Ok(candidates) : Results.NotFound();
        });
        logical.MapGet("/{id:long}/history", HistoryAsync);
        logical.MapGet("/physical-devices", (long propertyId, HttpContext http, ILogicalDevices store, CancellationToken ct) =>
            store.PhysicalDevicesAsync(propertyId, MonitoringEndpoints.UserId(http.User), ct));
        logical.MapGet("/{id:long}/release-preview", async (long propertyId, long id, long? deviceId, long? endpointId,
            DateTimeOffset? effectiveAt, TimeProvider clock, ILogicalDevices store, CancellationToken ct) =>
        {
            if (deviceId is null) return Invalid("Choose a physical device.");
            var preview = await store.ReleasePreviewAsync(propertyId, id, deviceId.Value, endpointId, effectiveAt ?? clock.GetUtcNow(), ct);
            return preview is null ? Results.NoContent() : Results.Ok(preview);
        });
        logical.MapPost("/{id:long}/unassign", (long propertyId, long id, UnassignPhysicalDevice command, HttpContext http,
            ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.UnassignAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));
        logical.MapPost("/{id:long}/restore", (long propertyId, long id, RestoreLogicalDevice command, HttpContext http,
            ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.RestoreAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));

        logical.MapPost("", (long propertyId, CreateLogicalDevice command, HttpContext http, ILogicalDevices store,
            IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.CreateAsync(propertyId, MonitoringEndpoints.UserId(http.User), command, ct), ct, created: true));
        logical.MapPut("/{id:long}", (long propertyId, long id, UpdateLogicalDevice command, HttpContext http, ILogicalDevices store,
            IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.UpdateAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));
        logical.MapPost("/{id:long}/assignments", (long propertyId, long id, AssignPhysicalDevice command, HttpContext http,
            ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.AssignAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));
        logical.MapPost("/{id:long}/replace", (long propertyId, long id, ReplacePhysicalDevice command, HttpContext http,
            ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.ReplaceAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));
        logical.MapPost("/{id:long}/assignments/{assignment:long}/correction", (long propertyId, long id, long assignment,
            CorrectAssignmentStart command, HttpContext http, ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.CorrectAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, assignment, command, ct), ct));
        logical.MapPost("/{id:long}/archive", (long propertyId, long id, ArchiveLogicalDevice command, HttpContext http,
            ILogicalDevices store, IMonitoringDashboardNotifier notifier, CancellationToken ct) =>
            ChangeAsync(propertyId, notifier, () => store.ArchiveAsync(propertyId, MonitoringEndpoints.UserId(http.User), id, command, ct), ct));
    }

    private static async Task<IResult> HistoryAsync(long propertyId, long id, string? channel, string? metric, string? resolution,
        DateTimeOffset? from, DateTimeOffset? to, DateOnly? fromDate, DateOnly? toDate, DateTimeOffset? after, int? limit,
        TimeProvider clock, ILogicalDevices store, CancellationToken ct)
    {
        channel ??= LogicalDeviceRules.DefaultChannel;
        if (!LogicalDeviceRules.ValidChannel(channel)) return Invalid("Use a valid channel key.");
        if (metric is null || !HistoryEndpoints.TryMetric(metric, out var parsed)) return Invalid("Select a supported metric.");
        if (HistoryEndpoints.ValidateRange(new(null, null, null, null, parsed), resolution, from, to, fromDate, toDate, after, limit,
                clock.GetUtcNow(), out var query) is { } problem)
            return problem;
        var read = await store.HistoryAsync(propertyId, id, channel, parsed, query!, ct);
        return read.Status switch
        {
            HistoryReadStatus.Found => Results.Ok(read.Result),
            HistoryReadStatus.NotFound => Results.NotFound(),
            HistoryReadStatus.Unsupported => Invalid("Status metrics are available at raw resolution only."),
            HistoryReadStatus.InvalidTimeZone => Results.Problem(statusCode: 409, title: "The property or counter time zone is not recognized on this server."),
            HistoryReadStatus.TooLarge => Invalid("The selected range needs too many raw observations. Request a shorter range."),
            HistoryReadStatus.SchemaPending => Results.Problem(statusCode: 503, title: "Logical history requires migrations 017, 018 and 019."),
            _ => Results.Problem(statusCode: 500)
        };
    }

    private static async Task<IResult> ChangeAsync(long property, IMonitoringDashboardNotifier notifier,
        Func<Task<LogicalChangeResult>> change, CancellationToken ct, bool created = false)
    {
        var result = await change();
        if (result.Status == LogicalChangeStatus.Done)
        {
            await notifier.ChangedAsync(property, ct);
            return created ? Results.Created($"/api/v1/properties/{property}/logical-devices/{result.Id}", new { id = result.Id })
                : Results.Ok(new { id = result.Id });
        }
        var (status, code) = result.Status switch
        {
            LogicalChangeStatus.NotFound => (404, "notFound"),
            LogicalChangeStatus.Forbidden => (403, "forbidden"),
            LogicalChangeStatus.Conflict => (409, "conflict"),
            LogicalChangeStatus.Incompatible => (400, "incompatible"),
            LogicalChangeStatus.MissingCapabilities => (400, "missingCapabilities"),
            LogicalChangeStatus.AmbiguousEndpoint => (400, "ambiguousEndpoint"),
            LogicalChangeStatus.SchemaPending => (503, "schemaPending"),
            LogicalChangeStatus.ReleaseRequired => (409, "releaseRequired"),
            LogicalChangeStatus.HistoryConfirmation => (400, "historyConfirmation"),
            _ => (400, "invalid")
        };
        return Results.Problem(statusCode: status, title: result.Message, extensions: new Dictionary<string, object?>
        {
            ["code"] = code, ["missing"] = result.Missing
        });
    }

    private static IResult Invalid(string title) => Results.Problem(statusCode: 400, title: title);
}
