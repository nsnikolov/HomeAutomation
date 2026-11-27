using System.Security.Claims;
using System.Security.Cryptography;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace HomeAutomation.Api;

internal sealed class EwelinkConsent(TimeProvider clock)
{
    private readonly object sync = new();
    private Pending? pending;

    public Pending Begin(string user, long property)
    {
        lock (sync)
            return pending = new(user, property, Random(), Random(), clock.GetUtcNow().AddMinutes(10));
    }

    public Pending? Take(string? state, string? cookie)
    {
        lock (sync)
        {
            if (pending is not { } flow || flow.Expires <= clock.GetUtcNow() ||
                flow.State != state || flow.Cookie != cookie) return null;
            pending = null;
            return flow;
        }
    }

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    internal sealed record Pending(string User, long Property, string State, string Cookie, DateTimeOffset Expires);
}

internal static class EwelinkEndpoints
{
    private const string Route = "/api/v1/ewelink";
    private const string Cookie = "homeautomation.ewelink";

    public static void MapEwelink(this WebApplication app)
    {
        app.MapGet(Route + "/callback", async (HttpContext http, EwelinkConsent consent, EwelinkService service,
            EwelinkOptions options, UserManager<LocalUser> users, IMonitoringReader access, ILogger<EwelinkConsent> logger,
            string? code, string? region, string? state, CancellationToken ct) =>
        {
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!MatchesOrigin(http.Request, options)) return Results.StatusCode(403);
            if (consent.Take(state, http.Request.Cookies[Cookie]) is not { } pending)
                return Results.BadRequest("eWeLink authorization did not complete. Restart the connection from Home Automation.");

            http.Response.Cookies.Delete(Cookie, CookieSettings());
            var destination = $"/properties/{pending.Property}/ewelink";
            var owner = await users.FindByIdAsync(pending.User);
            if (owner is null || !options.IsOperator(owner.UserName) ||
                !await access.CanReadAsync(pending.User, pending.Property, ct)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096 || !service.IsSupportedRegion(region) ||
                !await service.SchemaReadyAsync(ct)) return Results.Redirect(destination + "?connect=failed");
            try
            {
                await service.ConnectAsync(pending.User, code, region!, ct);
                return Results.Redirect(destination + "?connect=success");
            }
            catch (EwelinkFailure error)
            {
                logger.LogWarning("eWeLink connection failed with {Status}, upstream HTTP {HttpStatus}, CoolKit error {ProviderCode}.",
                    error.Status, error.HttpStatus, error.ProviderCode);
            }
            catch (HttpRequestException error)
            {
                logger.LogWarning("eWeLink connection transport failed with HTTP {HttpStatus}.", (int?)error.StatusCode);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("eWeLink connection timed out.");
            }
            return Results.Redirect(destination + "?connect=failed");
        }).AllowAnonymous().RequireRateLimiting("session");

        var propertyRoutes = app.MapGroup(Route + "/{property:long}").RequireAuthorization();
        propertyRoutes.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var ct = http.RequestAborted;
            var property = long.Parse((string)http.Request.RouteValues["property"]!, System.Globalization.CultureInfo.InvariantCulture);
            if (!await services.GetRequiredService<IMonitoringReader>().CanReadAsync(User(http), property, ct))
                return Results.NotFound();
            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!services.GetRequiredService<EwelinkOptions>().IsOperator(http.User.Identity?.Name)) return Results.Forbid();
                try { await services.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
                if (!await services.GetRequiredService<EwelinkService>().SchemaReadyAsync(ct))
                    return Results.Json(new { status = "waitingForSchema" }, statusCode: 503);
            }
            try { return await next(context); }
            catch (EwelinkFailure error)
            {
                if (error.RetryAt is { } retryAt)
                    http.Response.Headers.RetryAfter = retryAt.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { status = error.Status }, statusCode: error.Status switch
                {
                    "forbidden" => 403, "notFound" => 404, "invalidRoom" or "invalidCommand" => 400,
                    "rateLimited" or "quotaExceeded" or "accessOrQuotaLimited" or "syncDeferred" => 429,
                    "unavailable" => 503, _ => 409
                });
            }
        });
        propertyRoutes.MapGet("/", (HttpContext http, long property, EwelinkService service, EwelinkOptions options, CancellationToken ct) =>
            service.ViewAsync(User(http), property, options.IsOperator(http.User.Identity?.Name), ct));
        propertyRoutes.MapPost("/connect", async (HttpContext http, long property, EwelinkConsent consent,
            EwelinkService service, EwelinkOptions options, CancellationToken ct) =>
        {
            if (!MatchesOrigin(http.Request, options)) return Results.Conflict(new { status = "httpsRequired" });
            var view = await service.ViewAsync(User(http), property, true, ct);
            if (!view.Manage) return Results.Forbid();
            var flow = consent.Begin(User(http), property);
            http.Response.Cookies.Append(Cookie, flow.Cookie, CookieSettings());
            return Results.Ok(new { authorizationUrl = service.AuthorizationUrl(flow.State) });
        }).RequireRateLimiting("session");
        propertyRoutes.MapPost("/disconnect", async (HttpContext http, EwelinkService service, CancellationToken ct) =>
        {
            await service.DisconnectAsync(User(http), ct);
            return Results.NoContent();
        });
        propertyRoutes.MapPost("/refresh", async (HttpContext http, EwelinkService service, CancellationToken ct) =>
        {
            await service.SyncAsync(User(http), ct);
            return Results.NoContent();
        }).RequireRateLimiting("session");
        propertyRoutes.MapPost("/electrical-viewer", async (HttpContext http, long property, ElectricalViewer viewer,
            EwelinkService service, CancellationToken ct) =>
        {
            if (viewer.Tab == Guid.Empty || viewer.Active is not { } active) return Results.BadRequest();
            await service.SetElectricalViewerAsync(User(http), property, viewer.Tab, active, ct);
            return Results.NoContent();
        }).RequireRateLimiting("ewelink-presence");
        propertyRoutes.MapPost("/devices/{device:guid}/assign", async (HttpContext http, long property, Guid device,
            RoomAssignment assignment, EwelinkService service, IDeviceAssignmentAudit audit, CancellationToken ct) =>
        {
            var id = device.ToString("N");
            var before = await PropertyManagementEndpoints.AssignmentBeforeAsync(http, audit, "ewelink", id, ct);
            await service.AssignAsync(User(http), property, id, assignment.RoomId, ct);
            await PropertyManagementEndpoints.RecordAssignmentAsync(http, audit, "ewelink", before, id, ct);
            return Results.NoContent();
        });
        propertyRoutes.MapPost("/devices/{device:guid}/relay", (HttpContext http, long property, Guid device,
            EwelinkRelayCommand command, EwelinkService service, CancellationToken ct) =>
                service.CommandRelayAsync(User(http), property, device.ToString("N"), command, ct))
            .RequireRateLimiting("session");
        propertyRoutes.MapPost("/devices/{device:guid}/relay-preference", async (HttpContext http, long property, Guid device,
            RelayPreference preference, EwelinkService service, CancellationToken ct) =>
        {
            if (preference.Enabled is not { } enabled) return Results.BadRequest();
            await service.SetRelayWritesAsync(User(http), property, device.ToString("N"), enabled, ct);
            return Results.NoContent();
        }).RequireRateLimiting("session");
    }

    private static CookieOptions CookieSettings() => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = Route, MaxAge = TimeSpan.FromMinutes(10), IsEssential = true
    };
    private static bool MatchesOrigin(HttpRequest request, EwelinkOptions options) =>
        request.IsHttps && Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect) &&
        string.Equals(request.Host.Value, redirect.Authority, StringComparison.OrdinalIgnoreCase);
    private static string User(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private sealed record RoomAssignment(long? RoomId);
    private sealed record RelayPreference(bool? Enabled);
    private sealed record ElectricalViewer(Guid Tab, bool? Active);
}
