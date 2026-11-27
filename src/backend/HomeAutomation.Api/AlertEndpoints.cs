using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;

namespace HomeAutomation.Api;

/// <summary>
/// Alert rules, incidents and notifications. Property routes run behind the membership filter (non-members get 404); the stores enforce
/// roles. Every mutation requires the antiforgery token. Until migration 021 is applied the routes answer 503 and nothing else changes.
/// </summary>
internal static class AlertEndpoints
{
    public static void MapAlerts(this RouteGroupBuilder property)
    {
        var routes = property.MapGroup("").AddEndpointFilter(PropertyManagementEndpoints.Antiforgery).AddEndpointFilter(SchemaAsync);
        routes.MapGet("/alert-rules", async (long propertyId, HttpContext http, IAlertRules rules, CancellationToken ct) =>
            await rules.ListAsync(User(http), propertyId, ct) is { } list ? Results.Ok(list) : Results.NotFound());
        routes.MapGet("/alert-rules/catalog", async (long propertyId, HttpContext http, IAlertRules rules, CancellationToken ct) =>
            await rules.CatalogAsync(User(http), propertyId, ct) is { } catalog ? Results.Ok(catalog) : Results.NotFound());
        routes.MapGet("/alert-rules/templates", async (long propertyId, HttpContext http, IAlertRules rules, CancellationToken ct) =>
            await rules.TemplatesAsync(User(http), propertyId, ct) is { } templates ? Results.Ok(templates) : Results.NotFound());
        routes.MapPost("/alert-rules/preview", async (long propertyId, AlertRuleInput input, HttpContext http, IAlertRules rules, CancellationToken ct) =>
            await rules.PreviewAsync(User(http), propertyId, input, ct) is { } preview ? Results.Ok(preview) : Results.NotFound());
        routes.MapPost("/alert-rules", (long propertyId, AlertRuleInput input, HttpContext http, IAlertRules rules, IAlertLiveNotifier live,
            CancellationToken ct) => ChangeAsync(live, propertyId, rules.CreateAsync(User(http), propertyId, input, ct), ct, created: true));
        routes.MapPost("/alert-rules/batch", (long propertyId, AlertRuleBatchRequest request, HttpContext http, IAlertRules rules,
            IAlertLiveNotifier live, CancellationToken ct) => ChangeAsync(live, propertyId, rules.CreateManyAsync(User(http), propertyId, request, ct), ct,
            created: true));
        routes.MapGet("/alert-rules/{ruleId:long}", async (long propertyId, long ruleId, HttpContext http, IAlertRules rules, CancellationToken ct) =>
            await rules.GetAsync(User(http), propertyId, ruleId, ct) is { } rule ? Results.Ok(rule) : Results.NotFound());
        routes.MapGet("/alert-rules/{ruleId:long}/revisions", async (long propertyId, long ruleId, HttpContext http, IAlertRules rules,
            CancellationToken ct) => await rules.RevisionsAsync(User(http), propertyId, ruleId, ct) is { } revisions ? Results.Ok(revisions) : Results.NotFound());
        routes.MapPut("/alert-rules/{ruleId:long}", (long propertyId, long ruleId, AlertRuleInput input, HttpContext http, IAlertRules rules,
            IAlertLiveNotifier live, CancellationToken ct) => ChangeAsync(live, propertyId, rules.UpdateAsync(User(http), propertyId, ruleId, input, ct), ct));
        routes.MapPost("/alert-rules/{ruleId:long}/enabled", (long propertyId, long ruleId, AlertEnabledRequest request, HttpContext http,
            IAlertRules rules, IAlertLiveNotifier live, CancellationToken ct) =>
            ChangeAsync(live, propertyId, rules.SetEnabledAsync(User(http), propertyId, ruleId, request, ct), ct));
        routes.MapPost("/alert-rules/{ruleId:long}/duplicate", (long propertyId, long ruleId, HttpContext http, IAlertRules rules,
            IAlertLiveNotifier live, CancellationToken ct) => ChangeAsync(live, propertyId, rules.DuplicateAsync(User(http), propertyId, ruleId, ct), ct,
            created: true));
        routes.MapDelete("/alert-rules/{ruleId:long}", (long propertyId, long ruleId, int? revision, HttpContext http, IAlertRules rules,
            IAlertLiveNotifier live, CancellationToken ct) => revision is null
                ? Task.FromResult(Results.Problem(statusCode: 400, title: "Send the rule revision you reviewed."))
                : ChangeAsync(live, propertyId, rules.DeleteAsync(User(http), propertyId, ruleId, revision.Value, ct), ct));

        routes.MapGet("/incidents", async (long propertyId, string? status, string? severity, long? ruleId, long? deviceId, DateTimeOffset? from,
            DateTimeOffset? to, int? limit, HttpContext http, INotificationCenter center, CancellationToken ct) =>
        {
            if (!TryParse(severity, out AlertSeverity? level) || status is not (null or "active" or "resolved"))
                return Results.Problem(statusCode: 400, title: "Use status active or resolved and severity info, warning or critical.");
            return await center.IncidentsAsync(User(http), propertyId, new IncidentQuery(status, level, ruleId, deviceId, from, to, limit ?? 50), ct)
                is { } list ? Results.Ok(list) : Results.NotFound();
        });
        routes.MapGet("/incidents/{incidentId:long}", async (long propertyId, long incidentId, HttpContext http, INotificationCenter center,
            CancellationToken ct) => await center.IncidentAsync(User(http), propertyId, incidentId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        routes.MapPost("/incidents/{incidentId:long}/acknowledge", (long propertyId, long incidentId, HttpContext http, INotificationCenter center,
            IAlertLiveNotifier live, CancellationToken ct) => ChangeAsync(live, propertyId, center.AcknowledgeAsync(User(http), propertyId, incidentId, ct), ct));
    }

    public static void MapNotifications(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/notifications").AddEndpointFilter(PropertyManagementEndpoints.Antiforgery).AddEndpointFilter(SchemaAsync);
        routes.MapGet("", async (long? propertyId, long? deviceId, string? kind, string? severity, long? ruleId, DateTimeOffset? from,
            DateTimeOffset? to, string? incidentStatus, bool? unread, bool? inApp, long? before, int? limit, HttpContext http,
            INotificationCenter center, CancellationToken ct) =>
        {
            if (!TryParse(severity, out AlertSeverity? level) || !TryParse(kind, out NotificationKind? type) ||
                incidentStatus is not (null or "active" or "resolved"))
                return Results.Problem(statusCode: 400, title: "Use a supported severity, notification type and incident status.");
            return Results.Ok(await center.NotificationsAsync(User(http), new NotificationQuery(propertyId, deviceId, type, level, ruleId, from, to,
                incidentStatus, unread, inApp == true, before, limit ?? 50), ct));
        });
        routes.MapGet("/summary", (HttpContext http, INotificationCenter center, CancellationToken ct) => center.SummaryAsync(User(http), ct));
        routes.MapPost("/read", async (MarkReadRequest request, HttpContext http, INotificationCenter center, IAlertLiveNotifier live,
            CancellationToken ct) =>
        {
            var changed = await center.MarkReadAsync(User(http), request, ct);
            if (changed > 0) await live.NotificationsChangedAsync([User(http)], ct);
            return Results.Ok(new { changed });
        });
        routes.MapPost("/{notificationId:long}/delivery", async (long notificationId, DeliveryOutcomeRequest request, HttpContext http,
            INotificationCenter center, CancellationToken ct) =>
            PropertyManagementEndpoints.Result(await center.ReportDeliveryAsync(User(http), notificationId, request, ct)));
        routes.MapGet("/preferences", (HttpContext http, INotificationCenter center, CancellationToken ct) => center.PreferencesAsync(User(http), ct));
        routes.MapPut("/preferences", async (NotificationPreferences preferences, HttpContext http, INotificationCenter center,
            CancellationToken ct) => PropertyManagementEndpoints.Result(await center.SavePreferencesAsync(User(http), preferences, ct)));
    }

    private static string User(HttpContext http) => MonitoringEndpoints.UserId(http.User);

    private static bool TryParse<T>(string? value, out T? parsed) where T : struct, Enum
    {
        parsed = null;
        if (value is null) return true;
        if (!Enum.TryParse<T>(value, true, out var result) || !Enum.IsDefined(result) || value.Any(char.IsDigit)) return false;
        parsed = result;
        return true;
    }

    private static async Task<IResult> ChangeAsync(IAlertLiveNotifier live, long property, Task<ManagementResult> change, CancellationToken ct,
        bool created = false)
    {
        var result = await change;
        if (result.Status == ManagementStatus.Done) await live.IncidentsChangedAsync(property, ct);
        return PropertyManagementEndpoints.Result(result, created);
    }

    private static async ValueTask<object?> SchemaAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ready = await context.HttpContext.RequestServices.GetRequiredService<IAlertSchema>().ReadyAsync(context.HttpContext.RequestAborted);
        return ready ? await next(context) : Results.Problem(statusCode: 503,
            title: "Alerts are not available until database migration 021 is applied.",
            extensions: new Dictionary<string, object?> { ["code"] = "schemaPending" });
    }
}

/// <summary>Whether migration 021 is recorded. An unreachable database counts as not ready.</summary>
internal interface IAlertSchema
{
    Task<bool> ReadyAsync(CancellationToken ct);
}

internal sealed class AlertSchema(Migrations migrations) : IAlertSchema
{
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try { return await migrations.VerifyAlertsAsync(ct); }
        catch (MySqlConnector.MySqlException) { return false; }
    }
}
