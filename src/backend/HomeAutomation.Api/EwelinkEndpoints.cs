using System.Security.Claims;
using System.Security.Cryptography;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Ewelink;
using Microsoft.AspNetCore.Antiforgery;
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

    public long? Accept(string? state, string? cookie, string code, string region)
    {
        lock (sync)
        {
            if (pending is not { Grant: null } flow || flow.Expires <= clock.GetUtcNow() ||
                flow.State != state || flow.Cookie != cookie) return null;
            pending = flow with { Grant = new(code, region) };
            return flow.Property;
        }
    }

    public Grant? Take(string user, long property, string? cookie)
    {
        lock (sync)
        {
            if (pending is not { Grant: not null } flow || flow.Expires <= clock.GetUtcNow() ||
                flow.User != user || flow.Property != property || flow.Cookie != cookie) return null;
            pending = null;
            return flow.Grant;
        }
    }

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    internal sealed record Pending(string User, long Property, string State, string Cookie, DateTimeOffset Expires,
        Grant? Grant = null);
    internal sealed record Grant(string Code, string Region);
}

internal static class EwelinkEndpoints
{
    private const string Route = "/api/v1/ewelink";
    private const string Cookie = "homeautomation.ewelink";

    public static void MapEwelink(this WebApplication app)
    {
        app.MapGet(Route + "/callback", (HttpContext http, EwelinkConsent consent, EwelinkService service,
            EwelinkOptions options, string? code, string? region, string? state) =>
        {
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!MatchesOrigin(http.Request, options)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096 || !service.IsSupportedRegion(region) ||
                consent.Accept(state, http.Request.Cookies[Cookie], code, region!) is not { } property)
                return Results.BadRequest("eWeLink authorization did not complete. Restart the connection from Home Automation.");

            return Results.Redirect($"/properties/{property}/ewelink?connect=finish");
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
                    "forbidden" => 403, "notFound" => 404, "invalidRoom" => 400,
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
        propertyRoutes.MapPost("/complete", async (HttpContext http, long property, EwelinkConsent consent,
            EwelinkService service, EwelinkOptions options, CancellationToken ct) =>
        {
            if (!MatchesOrigin(http.Request, options)) return Results.Conflict(new { status = "httpsRequired" });
            var grant = consent.Take(User(http), property, http.Request.Cookies[Cookie]);
            if (grant is null) return Results.BadRequest();
            http.Response.Cookies.Delete(Cookie, CookieSettings());
            try { await service.ConnectAsync(User(http), grant.Code, grant.Region, ct); }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
            return Results.NoContent();
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
        propertyRoutes.MapPost("/devices/{device:guid}/assign", async (HttpContext http, long property, Guid device,
            RoomAssignment assignment, EwelinkService service, CancellationToken ct) =>
        {
            await service.AssignAsync(User(http), property, device.ToString("N"), assignment.RoomId, ct);
            return Results.NoContent();
        });
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
}
