using System.Globalization;
using System.Security.Claims;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.Antiforgery;

namespace HomeAutomation.Api;

internal static class TuyaEndpoints
{
    public static void MapTuya(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tuya/{property:long}").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var ct = http.RequestAborted;
            var property = long.Parse((string)http.Request.RouteValues["property"]!, CultureInfo.InvariantCulture);
            if (!await services.GetRequiredService<IMonitoringReader>().CanReadAsync(User(http), property, ct))
                return Results.NotFound();
            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!services.GetRequiredService<TuyaOptions>().IsOperator(http.User.Identity?.Name)) return Results.Forbid();
                try { await services.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
                if (!await services.GetRequiredService<TuyaService>().SchemaReadyAsync(ct))
                    return Results.Json(new { status = "waitingForSchema" }, statusCode: 503);
            }
            try { return await next(context); }
            catch (TuyaFailure error)
            {
                if (error.RetryAt is { } retryAt)
                    http.Response.Headers.RetryAfter = retryAt.ToString("R", CultureInfo.InvariantCulture);
                return Results.Json(new { status = error.Status }, statusCode: error.Status switch
                {
                    "notFound" => 404, "invalidRoom" => 400,
                    "rateLimited" or "quotaExceeded" or "syncDeferred" => 429,
                    "unavailable" => 503, _ => 409
                });
            }
        });
        group.MapGet("/", (HttpContext http, long property, TuyaService service, TuyaOptions options, CancellationToken ct) =>
            service.ViewAsync(property, options.IsOperator(http.User.Identity?.Name), ct));
        group.MapGet("/devices/{device:guid}/sensor-history", async (long property, Guid device, string? period,
            TuyaService service, CancellationToken ct) =>
        {
            var days = period switch { null or "24h" => 1, "7d" => 7, "30d" => 30, _ => 0 };
            if (days == 0) return Results.BadRequest();
            return await service.HistoryAsync(property, device.ToString("N"), TimeSpan.FromDays(days), ct) is { } history
                ? Results.Ok(history) : Results.NotFound();
        });
        group.MapPost("/refresh", async (TuyaService service, CancellationToken ct) =>
        {
            await service.SyncAsync(true, ct);
            return Results.NoContent();
        }).RequireRateLimiting("session");
        group.MapPost("/devices/{device:guid}/assign", async (HttpContext http, long property, Guid device,
            RoomAssignment assignment, TuyaService service, IDeviceAssignmentAudit audit, CancellationToken ct) =>
        {
            var id = device.ToString("N");
            var before = await PropertyManagementEndpoints.AssignmentBeforeAsync(http, audit, "tuya", id, ct);
            await service.AssignAsync(User(http), property, id, assignment.RoomId, ct);
            await PropertyManagementEndpoints.RecordAssignmentAsync(http, audit, "tuya", before, id, ct);
            return Results.NoContent();
        });
    }

    private static string User(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private sealed record RoomAssignment(long? RoomId);
}
