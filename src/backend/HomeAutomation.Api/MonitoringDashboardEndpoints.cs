using HomeAutomation.Application;
using Microsoft.AspNetCore.Antiforgery;

namespace HomeAutomation.Api;

internal static class MonitoringDashboardEndpoints
{
    public static void MapDashboard(this RouteGroupBuilder property)
    {
        var dashboard = property.MapGroup("/dashboard").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsPut(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            if (!await http.RequestServices.GetRequiredService<IMonitoringDashboard>().SchemaReadyAsync(http.RequestAborted))
                return Results.Problem(statusCode: 503, title: "Apply the monitoring dashboard database migration.");
            return await next(context);
        });
        dashboard.MapGet("", (long propertyId, HttpContext http, IMonitoringDashboard store, CancellationToken ct) =>
            store.ReadAsync(propertyId, MonitoringEndpoints.UserId(http.User), ct));
        dashboard.MapGet("/sources", async (long propertyId, HttpContext http, IMonitoringDashboard store, CancellationToken ct) =>
            await store.CatalogAsync(propertyId, MonitoringEndpoints.UserId(http.User), ct) is { } sources
                ? Results.Ok(sources) : Results.Forbid());
        dashboard.MapPut("", async (long propertyId, HttpContext http, DashboardUpdate update,
            IMonitoringDashboard store, CancellationToken ct) =>
        {
            if (!Valid(update)) return Results.BadRequest();
            var result = await store.ReplaceAsync(propertyId, MonitoringEndpoints.UserId(http.User), update, ct);
            return result switch
            {
                DashboardSaveResult.Saved => Results.NoContent(),
                DashboardSaveResult.Conflict => Results.Conflict(),
                DashboardSaveResult.InvalidSource => Results.BadRequest(),
                _ => Results.Forbid()
            };
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(262_144));
    }

    internal static bool Valid(DashboardUpdate update)
    {
        if (update.Revision is not { Length: 64 } || !update.Revision.All(char.IsAsciiHexDigit) ||
            update.Groups is not { Length: <= 24 }) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var valueCount = 0;
        foreach (var group in update.Groups)
        {
            if (group is null || !Id(group.Id) || !Title(group.Title) ||
                group.Widgets is not { Length: <= 24 }) return false;
            foreach (var widget in group.Widgets)
            {
                if (widget is null || !Id(widget.Id) || !Title(widget.Title) || widget.Mode is not ("compact" or "detailed") ||
                    widget.Values is not { Length: >= 1 and <= 16 }) return false;
                valueCount += widget.Values.Length;
                if (valueCount > 512 || widget.Values.Any(value => value is null || !Id(value.Id) ||
                    value.SourceKey is not { Length: >= 1 and <= 192 } ||
                    value.SourceKey.Any(c => !char.IsAscii(c) || char.IsControl(c)) ||
                    value.Label is not null && !Title(value.Label))) return false;
                if (widget.NavigationSource is not null && !widget.Values.Any(value => value.SourceKey == widget.NavigationSource))
                    return false;
            }
        }
        return true;

        bool Id(string? id) => Guid.TryParseExact(id, "D", out var guid) && id == guid.ToString("D") && ids.Add(id);
        static bool Title(string? title) => title is { Length: <= 96 } && !string.IsNullOrWhiteSpace(title) &&
            !title.Any(char.IsControl);
    }
}
