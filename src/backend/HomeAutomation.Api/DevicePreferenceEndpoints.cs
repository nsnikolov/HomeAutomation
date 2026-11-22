using System.Globalization;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.Antiforgery;

namespace HomeAutomation.Api;

internal static class DevicePreferenceEndpoints
{
    public static void MapDevicePreferences(this RouteGroupBuilder property)
    {
        var preferences = property.MapGroup("/device-preferences/{page}").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Request.RouteValues["page"] is not ("dashboard" or "devices" or "growatt" or "daikin" or "ewelink" or "tuya"))
                return Results.NotFound();
            if (HttpMethods.IsPut(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            var migrations = http.RequestServices.GetRequiredService<Migrations>();
            if (!await migrations.VerifyAsync(http.RequestAborted, includePreferences: true))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await next(context);
        });
        preferences.MapGet("/", (long propertyId, string page, HttpContext http, DeviceViewPreferences store, CancellationToken ct) =>
            store.ReadAsync(propertyId, MonitoringEndpoints.UserId(http.User), page, ct));
        preferences.MapPut("/order", async (long propertyId, string page, CardOrder input, HttpContext http,
            DeviceViewPreferences store, CancellationToken ct) =>
        {
            if (!ValidGroup(page, input.Group) || input.Ids is null || input.Ids.Length > 500 ||
                input.Ids.Any(id => !ValidId(page, id)) || input.Ids.Distinct(StringComparer.Ordinal).Count() != input.Ids.Length)
                return Results.BadRequest();
            await store.OrderAsync(propertyId, MonitoringEndpoints.UserId(http.User), page, input.Group!, input.Ids!, ct);
            return Results.NoContent();
        });
        preferences.MapPut("/visibility/{id:guid}", async (long propertyId, string page, Guid id, Visibility input,
            HttpContext http, DeviceViewPreferences store, TuyaService tuya, TuyaOptions options, CancellationToken ct) =>
        {
            if (page != "tuya") return Results.NotFound();
            var key = id.ToString("N");
            if (input.Hidden && !await tuya.CanHideAsync(propertyId, key, options.IsOperator(http.User.Identity?.Name), ct))
                return Results.NotFound();
            await store.VisibilityAsync(propertyId, MonitoringEndpoints.UserId(http.User), key, input.Hidden, ct);
            return Results.NoContent();
        });
    }

    internal static bool ValidGroup(string page, string? group) => page switch
    {
        "devices" => group == "unassigned" || group is not null && group.StartsWith("room-", StringComparison.Ordinal) &&
            PositiveInteger(group[5..]),
        "dashboard" or "growatt" => group == "assigned",
        _ => group is "assigned" or "unassigned"
    };
    internal static bool ValidId(string page, string? id) => page is "dashboard" or "growatt" or "devices"
        ? PositiveInteger(id) : id is not null && Guid.TryParseExact(id, "N", out var guid) && id == guid.ToString("N");
    private static bool PositiveInteger(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        id > 0 && value == id.ToString(CultureInfo.InvariantCulture);
    private sealed record CardOrder(string? Group, string[]? Ids);
    private sealed record Visibility(bool Hidden);
}
