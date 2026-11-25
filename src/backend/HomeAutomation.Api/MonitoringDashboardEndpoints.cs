using HomeAutomation.Application;
using Microsoft.AspNetCore.Antiforgery;
using MySqlConnector;

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
            if (ValidationError(update) is { } error) return Invalid(error);
            DashboardSaveResult result;
            try
            {
                result = await store.ReplaceAsync(propertyId, MonitoringEndpoints.UserId(http.User), update, ct);
            }
            catch (MySqlException failure) when (failure.ErrorCode is MySqlErrorCode.TableAccessDenied or MySqlErrorCode.ColumnAccessDenied)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MonitoringDashboardEndpoints))
                    .LogWarning("Dashboard save for property {PropertyId} was denied by MySQL ({ErrorCode}).", propertyId, failure.ErrorCode);
                return Results.Problem(statusCode: 503, title: "Dashboard storage permissions are incomplete.",
                    detail: "Ask a database administrator to verify the dashboard table permissions, then retry.");
            }
            return result switch
            {
                DashboardSaveResult.Saved => Results.NoContent(),
                DashboardSaveResult.Conflict => Results.Conflict(),
                DashboardSaveResult.InvalidSource => Invalid("A selected measurement is no longer available for this property. Choose another source."),
                _ => Results.Forbid()
            };
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(262_144));
    }

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: 400, title: "Invalid dashboard configuration.", detail: detail);

    private static string? ValidationError(DashboardUpdate update)
    {
        if (update.Revision is not { Length: 64 } || !update.Revision.All(char.IsAsciiHexDigit))
            return "The layout revision is missing or invalid. Open the current dashboard before saving.";
        if (update.Groups is not { Length: <= 24 }) return "Provide a list of up to 24 dashboard groups.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var valueCount = 0;
        foreach (var group in update.Groups)
        {
            if (group is null || !Id(group.Id)) return "Each group, widget and measurement must have a unique valid ID.";
            if (!Title(group.Title)) return "Enter a group title of 1 to 96 characters without control characters.";
            if (group.Widgets is not { Length: <= 24 }) return "Provide a list of up to 24 widgets per group.";
            foreach (var widget in group.Widgets)
            {
                if (widget is null || !Id(widget.Id)) return "Each group, widget and measurement must have a unique valid ID.";
                if (!Title(widget.Title)) return "Enter a widget title of 1 to 96 characters without control characters.";
                if (widget.Mode is not ("compact" or "detailed")) return "Choose Compact or Detailed display for each widget.";
                if (widget.Width is < 1 or > 4 || widget.MeasurementColumns is < 1 or > 4)
                    return "Choose 1 to 4 columns for widget width and measurement layout.";
                if (widget.Values is not { Length: >= 1 and <= 16 }) return "Choose 1 to 16 measurements per widget.";
                valueCount += widget.Values.Length;
                if (valueCount > 512) return "A dashboard can contain up to 512 measurements.";
                foreach (var value in widget.Values)
                {
                    if (value is null || !Id(value.Id)) return "Each group, widget and measurement must have a unique valid ID.";
                    if (value.SourceKey is not { Length: >= 1 and <= 192 } ||
                        value.SourceKey.Any(c => !char.IsAscii(c) || char.IsControl(c))) return "Choose a valid source for each measurement.";
                    if (value.Label is not null && !Title(value.Label))
                        return "Custom measurement labels must contain 1 to 96 characters without control characters.";
                    if (!ValidColor(value))
                        return "Choose automatic coloring or a hexadecimal color with at least 4.5:1 contrast against the measurement tile.";
                }
                if (widget.NavigationSource is not null && !widget.Values.Any(value => value.SourceKey == widget.NavigationSource))
                    return "Choose a navigation destination from the widget's own measurements.";
            }
        }
        return null;

        bool Id(string? id) => Guid.TryParseExact(id, "D", out var guid) && id == guid.ToString("D") && ids.Add(id);
        static bool Title(string? title) => title is { Length: <= 96 } && !string.IsNullOrWhiteSpace(title) &&
            !title.Any(char.IsControl);
    }

    private static bool ValidColor(DashboardValue value)
    {
        if (value.ColorMode == "automatic") return value.CustomColor is null;
        if (value.ColorMode != "fixed" || value.CustomColor is not { Length: 7 } color ||
            color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)) return false;
        var rgb = Convert.ToInt32(color[1..], 16);
        var luminance = 0.2126 * Linear(rgb >> 16) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
        var background = Linear(244);
        return (Math.Max(background, luminance) + 0.05) / (Math.Min(background, luminance) + 0.05) >= 4.5;

        static double Linear(int value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
    }
}
