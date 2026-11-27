using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Incidents, the notification registry and preferences. Members read their property's incidents and their own notifications;
/// Owner, Admin and Operator acknowledge incidents. Acknowledging never stops detection or resolves an incident.
/// </summary>
public sealed class NotificationStore(Database database, TimeProvider clock) : INotificationCenter
{
    private static readonly string[] BrowserReasons = ["permission", "unsupported", "visible", "error"];

    public async Task<IncidentList?> IncidentsAsync(string user, long property, IncidentQuery query, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var role = await PropertyManagementStore.RoleAsync(db, null, property, user, ct);
        if (role is null) return null;
        var status = query.Status is "active" or "resolved" ? query.Status : null;
        var rows = await db.QueryAsync<IncidentRow>(Query(SelectIncidents + """
             WHERE i.property_id = @property AND (@status IS NULL OR i.status = @status)
              AND (@severity IS NULL OR i.max_severity = @severity)
              AND (@from IS NULL OR i.resolved_at_utc IS NULL OR i.resolved_at_utc >= @from)
              AND (@to IS NULL OR i.started_at_utc <= @to)
              AND (@rule IS NULL OR EXISTS (SELECT 1 FROM alert_incident_rules ir WHERE ir.incident_id = i.id AND ir.rule_id = @rule))
              AND (@device IS NULL OR EXISTS (SELECT 1 FROM alert_incident_rules ir JOIN alert_rules r ON r.id = ir.rule_id
                   WHERE ir.incident_id = i.id AND JSON_CONTAINS(r.device_ids, CAST(@device AS JSON))))
            ORDER BY i.status = 'active' DESC, i.started_at_utc DESC, i.id DESC LIMIT @limit
            """, ct, new
        {
            property, status, severity = query.Severity is { } severity ? AlertJson.Severity(severity) : null, from = query.From?.UtcDateTime,
            to = query.To?.UtcDateTime, rule = query.RuleId, device = query.DeviceId, limit = Math.Clamp(query.Limit, 1, 200),
        }));
        return new IncidentList(AlertRuleStore.CanConfigure(role), await ViewsAsync(db, rows.ToArray(), ct));
    }

    public async Task<IncidentDetail?> IncidentAsync(string user, long property, long incident, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var role = await PropertyManagementStore.RoleAsync(db, null, property, user, ct);
        if (role is null) return null;
        var row = await db.QuerySingleOrDefaultAsync<IncidentRow>(Query(SelectIncidents + " WHERE i.id = @incident AND i.property_id = @property",
            ct, new { incident, property }));
        if (row is null) return null;
        var view = (await ViewsAsync(db, [row], ct))[0];
        var events = await db.QueryAsync<EventRow>(Query("""
            SELECT id AS Id, occurred_at_utc AS OccurredAt, observed_at_utc AS ObservedAt, kind AS Kind, rule_id AS RuleId,
                   rule_revision AS RuleRevision, from_severity AS FromSeverity, to_severity AS ToSeverity, detail AS Detail
            FROM alert_incident_events WHERE incident_id = @incident ORDER BY occurred_at_utc, id LIMIT 1000
            """, ct, new { incident }));
        return new IncidentDetail(view, events.Select(item => new IncidentEventView(item.Id, Utc(item.OccurredAt), Utc(item.ObservedAt), item.Kind,
            item.RuleId, item.RuleRevision, item.FromSeverity is null ? null : AlertJson.Severity(item.FromSeverity),
            item.ToSeverity is null ? null : AlertJson.Severity(item.ToSeverity), AlertJson.Read<IncidentEventDetail>(item.Detail))).ToArray(),
            AlertRuleStore.CanConfigure(role) && view.Status == "active");
    }

    public async Task<ManagementResult> AcknowledgeAsync(string user, long property, long incident, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var role = await PropertyManagementStore.RoleAsync(db, tx, property, user, ct);
        if (role is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The property is unavailable.");
        if (!AlertRuleStore.CanConfigure(role))
            return ManagementResult.Fail(ManagementStatus.Forbidden, "Only property Owners, Admins and Operators can acknowledge incidents.");
        var current = await db.QuerySingleOrDefaultAsync<AcknowledgementRow>(Query("""
            SELECT status AS Status, acknowledged_at_utc AS AcknowledgedAt FROM alert_incidents WHERE id = @incident AND property_id = @property FOR UPDATE
            """, ct, new { incident, property }, tx));
        if (current is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The incident no longer exists.");
        if (current.Status != "active") return ManagementResult.Fail(ManagementStatus.Invalid, "Only active incidents can be acknowledged.");
        if (current.AcknowledgedAt is not null) return ManagementResult.Done(incident);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.ExecuteAsync(Query("""
            UPDATE alert_incidents SET acknowledged_at_utc = @now, acknowledged_by = @user WHERE id = @incident;
            INSERT INTO alert_incident_events (incident_id, property_id, occurred_at_utc, kind, actor) VALUES (@incident, @property, @now, 'acknowledged', @user);
            """, ct, new { now, user, incident, property }, tx));
        await tx.CommitAsync(ct);
        return ManagementResult.Done(incident);
    }

    public async Task<NotificationPage> NotificationsAsync(string user, NotificationQuery query, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var rows = (await db.QueryAsync<NotificationRow>(Query(SelectNotifications + """
             WHERE r.user_id = @user AND (@property IS NULL OR n.property_id = @property)
              AND (@kind IS NULL OR n.kind = @kind) AND (@severity IS NULL OR n.severity = @severity)
              AND (@from IS NULL OR n.created_at_utc >= @from) AND (@to IS NULL OR n.created_at_utc <= @to)
              AND (@incidentStatus IS NULL OR i.status = @incidentStatus)
              AND (@unread IS NULL OR (@unread = TRUE AND r.read_at_utc IS NULL) OR (@unread = FALSE AND r.read_at_utc IS NOT NULL))
              AND (@inAppOnly = FALSE OR r.in_app)
              AND (@before IS NULL OR n.id < @before)
              AND (@rule IS NULL OR EXISTS (SELECT 1 FROM alert_incident_rules ir WHERE ir.incident_id = n.incident_id AND ir.rule_id = @rule))
              AND (@device IS NULL OR EXISTS (SELECT 1 FROM alert_incident_rules ir JOIN alert_rules ar ON ar.id = ir.rule_id
                   WHERE ir.incident_id = n.incident_id AND JSON_CONTAINS(ar.device_ids, CAST(@device AS JSON))))
            ORDER BY n.id DESC LIMIT @take
            """, ct, new
        {
            user, property = query.PropertyId, kind = query.Kind is { } kind ? Camel(kind.ToString()) : null,
            severity = query.Severity is { } severity ? AlertJson.Severity(severity) : null, from = query.From?.UtcDateTime, to = query.To?.UtcDateTime,
            incidentStatus = query.IncidentStatus is "active" or "resolved" ? query.IncidentStatus : null, unread = query.Unread,
            inAppOnly = query.InAppOnly, before = query.Before, rule = query.RuleId, device = query.DeviceId, take = limit + 1,
        }))).ToArray();
        var page = rows.Take(limit).ToArray();
        var ids = page.Select(row => row.Id).ToArray();
        var deliveries = (ids.Length == 0 ? Enumerable.Empty<DeliveryRow>() : await db.QueryAsync<DeliveryRow>(Query("""
            SELECT notification_id AS NotificationId, channel AS Channel, status AS Status, reason AS Reason, updated_at_utc AS UpdatedAt
            FROM notification_deliveries WHERE user_id = @user AND notification_id IN @ids
            """, ct, new { user, ids }))).ToLookup(row => row.NotificationId);
        return new NotificationPage(page.Select(row => new NotificationView(row.Id, row.PropertyId, row.PropertyName, row.IncidentId,
            Enum.Parse<NotificationKind>(row.Kind, true), AlertJson.Severity(row.Severity), row.Title, row.Body, Utc(row.CreatedAt), row.InApp,
            Utc(row.ReadAt), row.SuppressedReason, row.IncidentStatus, AlertJson.Severity(row.IncidentSeverity),
            deliveries[row.Id].Select(item => new DeliveryView(item.Channel, item.Status, item.Reason, Utc(item.UpdatedAt))).ToArray())).ToArray(),
            rows.Length > limit ? page[^1].Id : null);
    }

    public async Task<NotificationSummary> SummaryAsync(string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var counts = (await db.QueryAsync<(string Severity, long Count)>(Query("""
            SELECT n.severity, COUNT(*) FROM notification_recipients r JOIN notifications n ON n.id = r.notification_id
            JOIN property_memberships m ON m.property_id = n.property_id AND m.user_id = r.user_id
            JOIN properties p ON p.id = n.property_id AND p.deleted_at_utc IS NULL
            WHERE r.user_id = @user AND r.in_app AND r.read_at_utc IS NULL GROUP BY n.severity
            """, ct, new { user }))).ToDictionary(row => row.Severity, row => (int)row.Count);
        var critical = await db.QueryAsync<IncidentRow>(Query(SelectIncidents + """
             JOIN property_memberships m ON m.property_id = i.property_id AND m.user_id = @user
             WHERE i.status = 'active' AND i.severity = 'critical' AND p.deleted_at_utc IS NULL
            ORDER BY i.started_at_utc DESC LIMIT 20
            """, ct, new { user }));
        return new NotificationSummary(counts.Values.Sum(), counts.GetValueOrDefault("critical"), counts.GetValueOrDefault("warning"),
            await ViewsAsync(db, critical.ToArray(), ct));
    }

    public async Task<int> MarkReadAsync(string user, MarkReadRequest request, CancellationToken ct)
    {
        var ids = request.Ids?.Distinct().Take(500).ToArray() ?? [];
        if (!request.All && ids.Length == 0) return 0;
        await using var db = await database.OpenAsync(ct);
        return await db.ExecuteAsync(Query($"""
            UPDATE notification_recipients r JOIN notifications n ON n.id = r.notification_id SET r.read_at_utc = @now
            WHERE r.user_id = @user AND r.read_at_utc IS NULL AND (@property IS NULL OR n.property_id = @property)
            {(request.All ? "" : "AND r.notification_id IN @ids")}
            """, ct, new { now = clock.GetUtcNow().UtcDateTime, user, property = request.PropertyId, ids }));
    }

    public async Task<ManagementResult> ReportDeliveryAsync(string user, long notification, DeliveryOutcomeRequest request, CancellationToken ct)
    {
        if (request.Channel != NotificationPreferences.Browser || request.Outcome is not ("shown" or "notShown") ||
            (request.Outcome == "notShown" && !BrowserReasons.Contains(request.Reason)))
            return ManagementResult.Fail(ManagementStatus.Invalid, "Report shown, or notShown with a permission, unsupported, visible or error reason.");
        await using var db = await database.OpenAsync(ct);
        var changed = await db.ExecuteAsync(Query("""
            UPDATE notification_deliveries SET status = @status, reason = @reason, updated_at_utc = @now
            WHERE notification_id = @notification AND user_id = @user AND channel = 'browser' AND status IN ('pending', 'sent')
            """, ct, new
        {
            status = request.Outcome == "shown" ? "delivered" : "notShown", reason = request.Outcome == "shown" ? null : request.Reason,
            now = clock.GetUtcNow().UtcDateTime, notification, user,
        }));
        return changed == 1 ? ManagementResult.Done(notification) : ManagementResult.Fail(ManagementStatus.NotFound, "No open browser delivery.");
    }

    public async Task<NotificationPreferences> PreferencesAsync(string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return (await PreferencesAsync(db, null, [user], ct))[user];
    }

    public async Task<ManagementResult> SavePreferencesAsync(string user, NotificationPreferences preferences, CancellationToken ct)
    {
        if (preferences?.Info is null || preferences.Warning is null || preferences.Critical is null || preferences.Events is null)
            return ManagementResult.Fail(ManagementStatus.Invalid, "Send preferences for every severity and event type.");
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO notification_preferences (user_id, settings, updated_at_utc) VALUES (@user, @settings, @now) AS incoming
            ON DUPLICATE KEY UPDATE settings = incoming.settings, updated_at_utc = incoming.updated_at_utc
            """, ct, new { user, settings = AlertJson.Write(preferences.Normalize()), now = clock.GetUtcNow().UtcDateTime }));
        return ManagementResult.Done();
    }

    /// <summary>Stored preferences for each user, or the defaults. Critical in-app delivery is always on.</summary>
    internal static async Task<Dictionary<string, NotificationPreferences>> PreferencesAsync(MySqlConnection db, MySqlTransaction? tx,
        IReadOnlyCollection<string> users, CancellationToken ct)
    {
        var stored = users.Count == 0 ? [] : (await db.QueryAsync<(string User, string Settings)>(Query(
            "SELECT user_id, settings FROM notification_preferences WHERE user_id IN @users", ct, new { users }, tx)))
            .ToDictionary(row => row.User, row => row.Settings, StringComparer.Ordinal);
        return users.ToDictionary(user => user, user =>
        {
            try { return (stored.TryGetValue(user, out var json) ? AlertJson.Read<NotificationPreferences>(json) : null)?.Normalize() ?? NotificationPreferences.Default; }
            catch (System.Text.Json.JsonException) { return NotificationPreferences.Default; }
        }, StringComparer.Ordinal);
    }

    private static async Task<IncidentView[]> ViewsAsync(MySqlConnection db, IncidentRow[] rows, CancellationToken ct)
    {
        if (rows.Length == 0) return [];
        var ids = rows.Select(row => row.Id).ToArray();
        var rules = (await db.QueryAsync<RuleRow>(Query("""
            SELECT incident_id AS IncidentId, rule_id AS RuleId, rule_name AS RuleName, rule_revision AS Revision, severity AS Severity,
                   active AS Active, first_active_at_utc AS FirstActiveAt, last_active_at_utc AS LastActiveAt, cleared_at_utc AS ClearedAt,
                   cleared_reason AS ClearedReason
            FROM alert_incident_rules WHERE incident_id IN @ids ORDER BY first_active_at_utc, rule_id
            """, ct, new { ids }))).ToLookup(row => row.IncidentId);
        return rows.Select(row => new IncidentView(row.Id, row.PropertyId, row.PropertyName, row.Title, row.CorrelationKey, row.Status,
            AlertJson.Severity(row.Severity), AlertJson.Severity(row.OriginalSeverity), AlertJson.Severity(row.MaxSeverity), Utc(row.StartedAt),
            Utc(row.LastChangedAt), Utc(row.ResolvedAt), row.Resolution, Utc(row.AcknowledgedAt),
            rules[row.Id].Select(rule => new IncidentRuleView(rule.RuleId, rule.RuleName, rule.Revision, AlertJson.Severity(rule.Severity), rule.Active,
                Utc(rule.FirstActiveAt), Utc(rule.LastActiveAt), Utc(rule.ClearedAt), rule.ClearedReason)).ToArray())).ToArray();
    }

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private const string SelectIncidents = """
        SELECT i.id AS Id, i.property_id AS PropertyId, p.name AS PropertyName, i.title AS Title, i.correlation_key AS CorrelationKey,
               i.status AS Status, i.severity AS Severity, i.original_severity AS OriginalSeverity, i.max_severity AS MaxSeverity,
               i.started_at_utc AS StartedAt, i.last_changed_at_utc AS LastChangedAt, i.resolved_at_utc AS ResolvedAt, i.resolution AS Resolution,
               i.acknowledged_at_utc AS AcknowledgedAt
        FROM alert_incidents i JOIN properties p ON p.id = i.property_id
        """;

    private const string SelectNotifications = """
        SELECT n.id AS Id, n.property_id AS PropertyId, p.name AS PropertyName, n.incident_id AS IncidentId, n.kind AS Kind, n.severity AS Severity,
               n.title AS Title, n.body AS Body, n.created_at_utc AS CreatedAt, r.in_app AS InApp, r.read_at_utc AS ReadAt,
               n.suppressed_reason AS SuppressedReason, i.status AS IncidentStatus, i.severity AS IncidentSeverity
        FROM notification_recipients r JOIN notifications n ON n.id = r.notification_id
        JOIN alert_incidents i ON i.id = n.incident_id
        JOIN properties p ON p.id = n.property_id AND p.deleted_at_utc IS NULL
        JOIN property_memberships m ON m.property_id = n.property_id AND m.user_id = r.user_id
        """;

    private sealed record IncidentRow(long Id, long PropertyId, string PropertyName, string Title, string CorrelationKey, string Status,
        string Severity, string OriginalSeverity, string MaxSeverity, DateTime StartedAt, DateTime LastChangedAt, DateTime? ResolvedAt,
        string? Resolution, DateTime? AcknowledgedAt);
    private sealed record RuleRow(long IncidentId, long RuleId, string RuleName, int Revision, string Severity, bool Active, DateTime FirstActiveAt,
        DateTime LastActiveAt, DateTime? ClearedAt, string? ClearedReason);
    private sealed record EventRow(long Id, DateTime OccurredAt, DateTime? ObservedAt, string Kind, long? RuleId, int? RuleRevision,
        string? FromSeverity, string? ToSeverity, string? Detail);
    private sealed record NotificationRow(long Id, long PropertyId, string PropertyName, long IncidentId, string Kind, string Severity, string Title,
        string Body, DateTime CreatedAt, bool InApp, DateTime? ReadAt, string? SuppressedReason, string IncidentStatus, string IncidentSeverity);
    private sealed record AcknowledgementRow(string Status, DateTime? AcknowledgedAt);
    private sealed record DeliveryRow(long NotificationId, string Channel, string Status, string? Reason, DateTime UpdatedAt);
}

/// <summary>
/// Hands pending deliveries to their channels after the evaluation committed, and refreshes open browsers. A delivery is claimed with a
/// conditional update, so concurrent dispatchers never send it twice. A channel that is not installed records the attempt as unsupported.
/// </summary>
public sealed class NotificationDispatcher(Database database, IEnumerable<INotificationChannel> channels, IAlertLiveNotifier live,
    TimeProvider clock, AlertOptions options)
{
    private const int MaximumAttempts = 3;
    private readonly IReadOnlyDictionary<string, INotificationChannel> byName =
        channels.ToDictionary(channel => channel.Name, StringComparer.Ordinal);

    public async Task DispatchAsync(AlertEvaluationResult result, CancellationToken ct)
    {
        if (result.IncidentsChanged) await live.IncidentsChangedAsync(result.PropertyId, ct);
        if (result.Notifications.Count == 0) return;
        await using (var db = await database.OpenAsync(ct))
        {
            var users = (await db.QueryAsync<string>(Query(
                "SELECT DISTINCT user_id FROM notification_recipients WHERE notification_id IN @ids", ct, new { ids = result.Notifications }))).ToArray();
            if (users.Length > 0) await live.NotificationsChangedAsync(users, ct);
        }
        await DeliverPendingAsync(result.Notifications, ct);
    }

    /// <summary>Delivers pending attempts: for the given notifications, or every recent pending attempt when retrying in the sweep.</summary>
    public async Task<int> DeliverPendingAsync(IReadOnlyList<long>? notifications, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var since = clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(options.DeliveryExpirySeconds);
        var pending = (await db.QueryAsync<PendingRow>(Query($"""
            SELECT d.id AS Id, d.attempts AS Attempts, d.channel AS Channel, d.user_id AS UserId, n.id AS NotificationId, n.property_id AS PropertyId,
                   n.incident_id AS IncidentId, n.kind AS Kind, n.severity AS Severity, n.title AS Title, n.body AS Body, n.created_at_utc AS CreatedAt
            FROM notification_deliveries d JOIN notifications n ON n.id = d.notification_id
            WHERE d.status = 'pending' AND d.attempts < {MaximumAttempts} AND d.created_at_utc >= @since
              {(notifications is null ? "" : "AND d.notification_id IN @notifications")}
            ORDER BY d.id LIMIT 500
            """, ct, new { since, notifications = notifications?.ToArray() ?? [] }))).ToArray();
        if (pending.Length == 0) return 0;
        var preferences = await NotificationStore.PreferencesAsync(db, null, pending.Select(row => row.UserId).Distinct().ToArray(), ct);
        var delivered = 0;
        foreach (var row in pending)
        {
            var claimed = await db.ExecuteAsync(Query("""
                UPDATE notification_deliveries SET attempts = attempts + 1, updated_at_utc = @now
                WHERE id = @Id AND status = 'pending' AND attempts = @Attempts
                """, ct, new { now = clock.GetUtcNow().UtcDateTime, row.Id, row.Attempts }));
            if (claimed != 1) continue;
            ChannelResult outcome;
            if (!byName.TryGetValue(row.Channel, out var channel)) outcome = new ChannelResult("unsupported", "channelNotInstalled");
            else
            {
                var severity = AlertJson.Severity(row.Severity);
                var message = new NotificationMessage(row.NotificationId, row.UserId, row.PropertyId, row.IncidentId,
                    Enum.Parse<NotificationKind>(row.Kind, true), severity, row.Title, row.Body, Utc(row.CreatedAt),
                    severity == AlertSeverity.Critical && preferences[row.UserId].Critical.Sound);
                try { outcome = await channel.DeliverAsync(message, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    outcome = new ChannelResult(row.Attempts + 1 >= MaximumAttempts ? "failed" : "pending", error.GetType().Name);
                }
            }
            await db.ExecuteAsync(Query("""
                UPDATE notification_deliveries SET status = @status, reason = @reason, updated_at_utc = @now WHERE id = @Id AND status = 'pending'
                """, ct, new { status = outcome.Status, reason = outcome.Reason is { Length: > 64 } text ? text[..64] : outcome.Reason,
                    now = clock.GetUtcNow().UtcDateTime, row.Id }));
            if (outcome.Status == "sent") delivered++;
        }
        return delivered;
    }

    private sealed record PendingRow(long Id, int Attempts, string Channel, string UserId, long NotificationId, long PropertyId, long IncidentId,
        string Kind, string Severity, string Title, string Body, DateTime CreatedAt);
}
