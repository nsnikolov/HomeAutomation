using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace HomeAutomation.Api;

internal sealed class DaikinFlow(TimeProvider clock)
{
    private readonly object sync = new();
    private Pending? pending;

    public Pending Begin(string user, long property, bool pkce)
    {
        lock (sync)
        {
            return pending = new Pending(Random(), Random(), pkce ? Random() : null, user, property,
                clock.GetUtcNow().AddMinutes(10));
        }
    }

    public long? Accept(string? cookie, string? state, string? code, out string? failure)
    {
        lock (sync)
        {
            var flow = pending;
            failure = flow is null ? "noPendingConnection"
                : flow.ExpiresAt <= clock.GetUtcNow() ? "expiredConnection"
                : string.IsNullOrEmpty(cookie) ? "missingCorrelationCookie"
                : cookie != flow.Cookie || state != flow.State ? "correlationMismatch"
                : flow.Code is not null ? "callbackAlreadyAccepted"
                : string.IsNullOrWhiteSpace(code) || code.Length > 4096 ? "invalidAuthorizationCode"
                : null;
            if (flow is null || failure is not null)
                return null;

            pending = flow with { Code = code };
            return flow.Property;
        }
    }

    public Pending? Take(string user, long property, string? cookie)
    {
        lock (sync)
        {
            if (pending is not { Code: not null } flow || flow.ExpiresAt <= clock.GetUtcNow() ||
                flow.User != user || flow.Property != property || flow.Cookie != cookie)
                return null;

            pending = null;
            return flow;
        }
    }

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal sealed record Pending(string Cookie, string State, string? Verifier, string User, long Property,
        DateTimeOffset ExpiresAt, string? Code = null)
    {
        public string? Challenge => Verifier is null ? null :
            WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
    }
}

internal static class DaikinEndpoints
{
    private const string Cookie = "homeautomation.onecta";
    private const string Route = "/api/v1/daikin";

    public static void MapDaikin(this WebApplication app)
    {
        app.MapGet(Route + "/callback", (HttpContext http, DaikinFlow flow, ILogger<DaikinFlow> logger,
            string? code, string? state, string? error) =>
        {
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var property = flow.Accept(http.Request.Cookies[Cookie], state, error is null ? code : null, out var failure);
            if (property is null)
            {
                logger.LogInformation("ONECTA callback rejected with {Reason}.",
                    error is not null && failure == "invalidAuthorizationCode" ? "providerRejected" : failure);
                return Results.BadRequest("ONECTA authorization did not complete. Restart the connection from Home Automation.");
            }

            logger.LogInformation("ONECTA callback accepted. Waiting for local confirmation.");
            return Results.Redirect($"/properties/{property}/daikin?onecta=finish");
        }).RequireRateLimiting("session");

        var group = app.MapGroup(Route + "/{property:long}").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var property = long.Parse((string)http.Request.RouteValues["property"]!, System.Globalization.CultureInfo.InvariantCulture);
            if (!await services.GetRequiredService<IMonitoringReader>().CanReadAsync(User(http), property, http.RequestAborted))
                return Results.NotFound();

            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!services.GetRequiredService<DaikinOptions>().IsOperator(http.User.Identity?.Name))
                    return Results.Forbid();
                try { await services.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
                if (!await services.GetRequiredService<Migrations>().VerifyAsync(http.RequestAborted, includeDaikin: true))
                    return Results.Json(new { status = "waitingForSchema" }, statusCode: 503);
            }

            try { return await next(context); }
            catch (DaikinFailure error)
            {
                return Results.Json(new { status = error.Status }, statusCode: error.Status switch
                {
                    "forbidden" => 403,
                    "notFound" => 404,
                    "rateLimited" or "pollBudgetReserved" or "manualBudgetReserved" => 429,
                    "unavailable" => 503,
                    _ => 409
                });
            }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.StatusCode(503); }
        });

        group.MapGet("/", async (HttpContext http, long property, DaikinService service, DaikinOptions options,
            Migrations migrations, CancellationToken ct) => options.IsConfigured && !await migrations.VerifyAsync(ct, includeDaikin: true)
            ? new DaikinView("waitingForSchema", null, false, false, [], [])
            : await service.ViewAsync(User(http), property, options.IsOperator(http.User.Identity?.Name), ct));

        group.MapPost("/connect", (HttpContext http, long property, DaikinFlow flow, DaikinService service, DaikinOptions options) =>
        {
            var pending = flow.Begin(User(http), property, options.UsePkce);
            http.Response.Cookies.Append(Cookie, pending.Cookie, CookieSettings(options));
            return Results.Ok(new { authorizationUrl = service.AuthorizationUrl(pending.State, pending.Challenge) });
        }).RequireRateLimiting("session");

        group.MapPost("/complete", async (HttpContext http, long property, DaikinFlow flow, DaikinService service,
            DaikinOptions options, CancellationToken ct) =>
        {
            var pending = flow.Take(User(http), property, http.Request.Cookies[Cookie]);
            if (pending?.Code is null) return Results.BadRequest();
            http.Response.Cookies.Delete(Cookie, CookieSettings(options));
            await service.ConnectAsync(User(http), pending.Code, pending.Verifier, ct);
            return Results.NoContent();
        }).RequireRateLimiting("session");

        group.MapPost("/disconnect", async (HttpContext http, DaikinService service, CancellationToken ct) =>
        {
            await service.DisconnectAsync(User(http), ct);
            return Results.NoContent();
        });

        group.MapPost("/refresh", async (HttpContext http, DaikinService service, CancellationToken ct) =>
        {
            await service.PollAsync(User(http), false, ct);
            return Results.NoContent();
        }).RequireRateLimiting("session");

        group.MapPost("/devices/{device:guid}/assign", async (HttpContext http, long property, Guid device, RoomAssignment input,
            DaikinService service, IDeviceAssignmentAudit audit, CancellationToken ct) =>
        {
            var id = device.ToString("N");
            var before = await PropertyManagementEndpoints.AssignmentBeforeAsync(http, audit, "daikin", id, ct);
            if (!await service.AssignAsync(User(http), id, property, input.RoomId, ct)) return Results.Conflict();
            await PropertyManagementEndpoints.RecordAssignmentAsync(http, audit, "daikin", before, id, ct);
            return Results.NoContent();
        });

        group.MapGet("/devices/{device:guid}/water-history", async (long property, Guid device,
            DaikinService service, DaikinOptions options, Migrations migrations, CancellationToken ct) =>
            options.IsConfigured && await migrations.VerifyAsync(ct, includeDaikin: true)
                ? Results.Ok(await service.WaterHistoryAsync(property, device.ToString("N"), ct))
                : Results.NotFound());

        group.MapPost("/devices/{device:guid}/commands", async (HttpContext http, long property, Guid device, DaikinCommand command,
            DaikinService service, CancellationToken ct) => Results.Ok(await service.CommandAsync(User(http), property,
                device.ToString("N"), command, ct))).RequireRateLimiting("session");
    }

    private static CookieOptions CookieSettings(DaikinOptions options) => new()
    {
        HttpOnly = true, Secure = new Uri(options.RedirectUri!).Scheme == "https",
        SameSite = SameSiteMode.Lax, Path = Route, MaxAge = TimeSpan.FromMinutes(10), IsEssential = true
    };

    private static string User(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private sealed record RoomAssignment(long? RoomId);
}
