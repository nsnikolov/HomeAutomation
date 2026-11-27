using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Evaluates one property's enabled rules and reconciles its incidents and notifications in a single transaction.
/// A per-property lock row serializes concurrent evaluations (several workers or a telemetry trigger racing the sweep), and a unique
/// key allows at most one active incident per correlation family. All state is in MySQL, so a restart resumes where it left off.
/// This class never sends device commands.
/// </summary>
public sealed class AlertEvaluator(Database database, TimeProvider clock, AlertOptions options, TelemetryHoldPolicy? holdPolicy = null)
{
    private const int CacheLimit = 2000;
    private readonly TelemetryHoldPolicy holds = holdPolicy ?? TelemetryHoldPolicy.Defaults();
    private readonly ConcurrentDictionary<(long Rule, int Revision), (RuleGroup Definition, NotificationPolicy Policy)> cache = new();

    /// <summary>Properties with enabled rules or active incidents. Deleted properties are skipped.</summary>
    public async Task<IReadOnlyList<long>> PropertiesDueAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return (await db.QueryAsync<long>(Query("""
            SELECT p.id FROM properties p WHERE p.deleted_at_utc IS NULL AND (
                EXISTS (SELECT 1 FROM alert_rules r WHERE r.property_id = p.id AND r.enabled AND r.deleted_at_utc IS NULL) OR
                EXISTS (SELECT 1 FROM alert_incidents i WHERE i.property_id = p.id AND i.status = 'active'))
            ORDER BY p.id
            """, ct))).ToArray();
    }

    /// <summary>Evaluates one property. A deadlock with a concurrent evaluation is retried; the loser then sees the winner's state.</summary>
    public async Task<AlertEvaluationResult> EvaluateAsync(long property, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await EvaluateOnceAsync(property, ct); }
            catch (MySqlException error) when (attempt < 4 && error.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }
    }

    private async Task<AlertEvaluationResult> EvaluateOnceAsync(long property, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var db = await database.OpenAsync(ct);
        // Deleted properties, and properties without rules or active incidents, write nothing.
        if (!await db.ExecuteScalarAsync<bool>(Query("""
                SELECT EXISTS (SELECT 1 FROM properties WHERE id = @property AND deleted_at_utc IS NULL) AND (
                       EXISTS (SELECT 1 FROM alert_rules WHERE property_id = @property AND enabled AND deleted_at_utc IS NULL) OR
                       EXISTS (SELECT 1 FROM alert_incidents WHERE property_id = @property AND status = 'active'))
                """, ct, new { property })))
            return AlertEvaluationResult.Unchanged(property);
        // The lock row is created outside the transaction: INSERT IGNORE never upgrades a shared lock, so concurrent first evaluations
        // cannot deadlock on it. Taking the lock is the transaction's first statement, so every later consistent read starts after
        // any evaluation that held the lock before this one had committed.
        await db.ExecuteAsync(Query("INSERT IGNORE INTO alert_evaluation_locks (property_id) SELECT id FROM properties WHERE id = @property", ct,
            new { property }));
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.ExecuteScalarAsync<long?>(Query("SELECT property_id FROM alert_evaluation_locks WHERE property_id = @property FOR UPDATE", ct,
                new { property }, tx)) is null)
            return AlertEvaluationResult.Unchanged(property);

        var rows = (await db.QueryAsync<AlertRuleStore.RuleRow>(Query(AlertRuleStore.SelectRules +
            " WHERE r.property_id = @property AND r.enabled AND r.deleted_at_utc IS NULL ORDER BY r.id", ct, new { property }, tx))).ToArray();
        var states = (await db.QueryAsync<StateRow>(Query("""
            SELECT rule_id AS RuleId, phase AS Phase, true_since_utc AS TrueSince, false_since_utc AS FalseSince, active_since_utc AS ActiveSince,
                   inputs_observed_at_utc AS InputsObservedAt, last_triggered_at_utc AS LastTriggeredAt
            FROM alert_rule_state WHERE property_id = @property FOR UPDATE
            """, ct, new { property }, tx))).ToDictionary(row => row.RuleId);

        var parsed = rows.Select(row => (Row: row, Parsed: Parse(row))).ToArray();
        var read = await AlertSignalReader.LoadAsync(db, tx, property, holds,
            parsed.SelectMany(rule => Domain.AlertSignals.Conditions(rule.Parsed.Definition)).ToArray(), now, ct);

        var evaluated = new List<Evaluated>();
        foreach (var (row, (definition, policy)) in parsed)
        {
            states.TryGetValue(row.Id, out var stored);
            var runtime = stored is null ? RuleRuntime.Initial : new RuleRuntime(Enum.Parse<RulePhase>(stored.Phase, true), Utc(stored.TrueSince),
                Utc(stored.FalseSince), Utc(stored.ActiveSince), Utc(stored.InputsObservedAt));
            var evaluation = RuleEvaluator.Evaluate(definition, read, now, runtime.Latched);
            var (next, transition) = RuleStateMachine.Step(runtime, evaluation.Result, evaluation.NewestObservation, now,
                TimeSpan.FromSeconds(row.ActivationSeconds), TimeSpan.FromSeconds(row.RecoverySeconds));
            var triggered = transition == RuleTransition.Activated ? now : Utc(stored?.LastTriggeredAt);
            await db.ExecuteAsync(Query("""
                INSERT INTO alert_rule_state (rule_id, property_id, revision, phase, truth, true_since_utc, false_since_utc, active_since_utc,
                    inputs_observed_at_utc, last_evaluated_at_utc, last_triggered_at_utc, evidence)
                VALUES (@Id, @property, @Revision, @Phase, @Truth, @TrueSince, @FalseSince, @ActiveSince, @Observed, @now, @Triggered, @Evidence) AS incoming
                ON DUPLICATE KEY UPDATE revision = incoming.revision, phase = incoming.phase, truth = incoming.truth,
                    true_since_utc = incoming.true_since_utc, false_since_utc = incoming.false_since_utc, active_since_utc = incoming.active_since_utc,
                    inputs_observed_at_utc = incoming.inputs_observed_at_utc, last_evaluated_at_utc = incoming.last_evaluated_at_utc,
                    last_triggered_at_utc = incoming.last_triggered_at_utc, evidence = incoming.evidence
                """, ct, new
            {
                row.Id, property, row.Revision, Phase = Lower(next.Phase), Truth = Lower(evaluation.Result), TrueSince = next.TrueSince?.UtcDateTime,
                FalseSince = next.FalseSince?.UtcDateTime, ActiveSince = next.ActiveSince?.UtcDateTime, Observed = next.InputsObservedAt?.UtcDateTime,
                now = now.UtcDateTime, Triggered = triggered?.UtcDateTime, Evidence = AlertJson.Write(evaluation.Conditions),
            }, tx));
            evaluated.Add(new Evaluated(row, AlertJson.Severity(row.Severity), IncidentPlanner.CorrelationKey(row.Id, row.CorrelationKey), policy,
                next, transition, evaluation));
        }

        var context = new Context(db, tx, property, now, ct);
        await ReconcileAsync(context, evaluated);
        await db.ExecuteAsync(Query("UPDATE alert_evaluation_locks SET last_evaluated_at_utc = @now WHERE property_id = @property", ct,
            new { now = now.UtcDateTime, property }, tx));
        await tx.CommitAsync(ct);
        return new AlertEvaluationResult(property, context.Changed, context.Notifications);
    }

    /// <summary>Marks browser and future-channel deliveries that were never handed over as expired, rather than showing them late.</summary>
    public async Task<int> ExpireDeliveriesAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var cutoff = clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(options.DeliveryExpirySeconds);
        return await db.ExecuteAsync(Query("""
            UPDATE notification_deliveries SET status = 'expired', updated_at_utc = @now
            WHERE status = 'pending' AND created_at_utc < @cutoff
            """, ct, new { now = clock.GetUtcNow().UtcDateTime, cutoff }));
    }

    private (RuleGroup Definition, NotificationPolicy Policy) Parse(AlertRuleStore.RuleRow row)
    {
        if (cache.TryGetValue((row.Id, row.Revision), out var cached)) return cached;
        if (cache.Count >= CacheLimit) cache.Clear();
        var value = (AlertJson.Read<RuleGroup>(row.Definition)!, AlertJson.Read<NotificationPolicy>(row.NotificationPolicy) ?? new NotificationPolicy());
        cache[(row.Id, row.Revision)] = value;
        return value;
    }

    private async Task ReconcileAsync(Context context, IReadOnlyList<Evaluated> evaluated)
    {
        var (db, tx, property, now, ct) = (context.Db, context.Tx, context.Property, context.Now, context.Ct);
        var incidents = (await db.QueryAsync<IncidentRow>(Query("""
            SELECT id AS Id, correlation_key AS CorrelationKey, title AS Title, severity AS Severity, started_at_utc AS StartedAt,
                   last_notified_at_utc AS LastNotifiedAt
            FROM alert_incidents WHERE property_id = @property AND status = 'active' FOR UPDATE
            """, ct, new { property }, tx))).ToDictionary(row => row.CorrelationKey, StringComparer.Ordinal);
        var incidentIds = incidents.Values.Select(row => row.Id).ToArray();
        var contributors = incidentIds.Length == 0 ? [] : (await db.QueryAsync<ContributorRow>(Query("""
            SELECT incident_id AS IncidentId, rule_id AS RuleId, severity AS Severity, active AS Active
            FROM alert_incident_rules WHERE incident_id IN @incidentIds FOR UPDATE
            """, ct, new { incidentIds }, tx))).ToList();
        // Policies and status of every rule that contributed, including disabled or deleted ones.
        var ruleIds = contributors.Select(row => row.RuleId).Distinct().ToArray();
        var known = ruleIds.Length == 0 ? new Dictionary<long, RuleInfo>() : (await db.QueryAsync<RuleInfo>(Query("""
            SELECT id AS Id, name AS Name, enabled AS Enabled, deleted_at_utc AS DeletedAt, correlation_key AS CorrelationKey,
                   notification_policy AS NotificationPolicy
            FROM alert_rules WHERE id IN @ruleIds
            """, ct, new { ruleIds }, tx))).ToDictionary(row => row.Id);

        var byId = evaluated.ToDictionary(rule => rule.Row.Id);
        var activeByKey = evaluated.Where(rule => rule.State.Latched).GroupBy(rule => rule.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(rule => rule.Severity).ThenBy(rule => rule.Row.Id).ToArray(),
                StringComparer.Ordinal);
        // Active families after this pass, for explicit suppression policies.
        var activeFamilies = activeByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var pending = new List<PendingNotification>();

        foreach (var key in activeByKey.Keys.Union(incidents.Keys).Order(StringComparer.Ordinal))
        {
            var active = activeByKey.GetValueOrDefault(key) ?? [];
            if (!incidents.TryGetValue(key, out var incident))
            {
                if (active.Length == 0) continue;
                var opener = active[0];
                var severity = opener.Severity;
                var started = active.Min(rule => rule.State.ActiveSince ?? now);
                await db.ExecuteAsync(Query("""
                    INSERT INTO alert_incidents (property_id, correlation_key, title, status, severity, original_severity, max_severity,
                        started_at_utc, last_changed_at_utc)
                    VALUES (@property, @key, @Title, 'active', @Severity, @Severity, @Severity, @started, @now)
                    """, ct, new { property, key, Title = opener.Row.IncidentTitle ?? opener.Row.Name, Severity = AlertJson.Severity(severity),
                        started = started.UtcDateTime, now = now.UtcDateTime }, tx));
                var id = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
                context.Changed = true;
                foreach (var rule in active) await ContributeAsync(context, id, rule);
                var opened = await EventAsync(context, id, "opened", opener, null, severity, Detail(opener));
                foreach (var rule in active.Skip(1)) await EventAsync(context, id, "ruleActivated", rule, null, null, Detail(rule));
                if (opener.Policy.OnActivation)
                    pending.Add(new(id, opened, NotificationKind.Activated, severity, opener.Row.IncidentTitle ?? opener.Row.Name,
                        $"{Label(severity)}: {opener.Row.Name}. {Summary(opener.Evaluation)}", active.Select(rule => rule.Policy).ToArray()));
                continue;
            }

            var current = AlertJson.Severity(incident.Severity);
            var rows = contributors.Where(row => row.IncidentId == incident.Id).ToArray();
            var activeIds = active.Select(rule => rule.Row.Id).ToHashSet();
            var newlyActive = new List<Evaluated>();
            foreach (var rule in active)
            {
                var existing = rows.FirstOrDefault(row => row.RuleId == rule.Row.Id);
                if (existing is { Active: true })
                {
                    await db.ExecuteAsync(Query("""
                        UPDATE alert_incident_rules SET last_active_at_utc = @now, rule_revision = @Revision, rule_name = @Name, severity = @Severity
                        WHERE incident_id = @incident AND rule_id = @rule
                        """, ct, new { now = now.UtcDateTime, rule.Row.Revision, rule.Row.Name, Severity = AlertJson.Severity(rule.Severity),
                            incident = incident.Id, rule = rule.Row.Id }, tx));
                    continue;
                }
                await ContributeAsync(context, incident.Id, rule);
                await EventAsync(context, incident.Id, "ruleActivated", rule, null, null, Detail(rule));
                newlyActive.Add(rule);
                context.Changed = true;
            }

            var cleared = new List<(ContributorRow Row, string Reason)>();
            foreach (var row in rows.Where(row => row.Active && !activeIds.Contains(row.RuleId)))
            {
                var reason = byId.TryGetValue(row.RuleId, out var still) && still.Key == key ? "recovered"
                    : known.TryGetValue(row.RuleId, out var info) && info.DeletedAt is not null ? "ruleDeleted"
                    : info is { Enabled: false } ? "ruleDisabled" : byId.ContainsKey(row.RuleId) ? "ruleChanged" : "ruleDisabled";
                await db.ExecuteAsync(Query("""
                    UPDATE alert_incident_rules SET active = FALSE, cleared_at_utc = @now, cleared_reason = @reason
                    WHERE incident_id = @incident AND rule_id = @rule
                    """, ct, new { now = now.UtcDateTime, reason, incident = incident.Id, rule = row.RuleId }, tx));
                await EventAsync(context, incident.Id, "ruleCleared", still, null, null,
                    still is null ? new IncidentEventDetail(known.GetValueOrDefault(row.RuleId)?.Name, null, reason)
                        : Detail(still) with { Reason = reason }, row.RuleId);
                cleared.Add((row, reason));
                context.Changed = true;
            }

            var policies = rows.Select(row => byId.TryGetValue(row.RuleId, out var rule) ? rule.Policy
                    : AlertJson.Read<NotificationPolicy>(known.GetValueOrDefault(row.RuleId)?.NotificationPolicy) ?? new NotificationPolicy())
                .Concat(newlyActive.Select(rule => rule.Policy)).ToArray();
            var (change, target) = IncidentPlanner.Plan(current, active.Select(rule => rule.Severity));
            switch (change)
            {
                case IncidentChange.Escalate:
                {
                    var cause = active[0];
                    // An escalation needs a fresh acknowledgment even if the lower severity was acknowledged.
                    await db.ExecuteAsync(Query("""
                        UPDATE alert_incidents SET severity = @to, max_severity = IF(FIELD(max_severity, 'info', 'warning', 'critical') <
                            FIELD(@to, 'info', 'warning', 'critical'), @to, max_severity), last_changed_at_utc = @now,
                            acknowledged_at_utc = NULL, acknowledged_by = NULL
                        WHERE id = @id
                        """, ct, new { to = AlertJson.Severity(target!.Value), now = now.UtcDateTime, id = incident.Id }, tx));
                    var escalated = await EventAsync(context, incident.Id, "escalated", cause, current, target, Detail(cause));
                    if (cause.Policy.OnEscalation)
                        pending.Add(new(incident.Id, escalated, NotificationKind.Escalated, target!.Value,
                            $"{incident.Title} escalated to {Label(target!.Value)}", $"{cause.Row.Name}. {Summary(cause.Evaluation)}", policies));
                    context.Changed = true;
                    break;
                }
                case IncidentChange.Deescalate:
                {
                    await db.ExecuteAsync(Query("UPDATE alert_incidents SET severity = @to, last_changed_at_utc = @now WHERE id = @id", ct,
                        new { to = AlertJson.Severity(target!.Value), now = now.UtcDateTime, id = incident.Id }, tx));
                    var reasons = cleared.Select(item => item.Reason).Distinct().ToArray();
                    var deescalated = await EventAsync(context, incident.Id, "deescalated", active[0], current, target,
                        new IncidentEventDetail(active[0].Row.Name, active[0].Evaluation.Conditions, string.Join(", ", reasons)));
                    var wants = cleared.Any(item => (byId.TryGetValue(item.Row.RuleId, out var rule) ? rule.Policy
                        : AlertJson.Read<NotificationPolicy>(known.GetValueOrDefault(item.Row.RuleId)?.NotificationPolicy))?.OnDeescalation == true);
                    if (wants)
                        pending.Add(new(incident.Id, deescalated, NotificationKind.Deescalated, target!.Value,
                            $"{incident.Title} is now {Label(target!.Value)}", $"{Label(current)} conditions cleared. {active[0].Row.Name} remains active.",
                            policies));
                    context.Changed = true;
                    break;
                }
                case IncidentChange.Resolve:
                {
                    var reason = cleared.Count == 0 || cleared.Any(item => item.Reason == "recovered") ? "recovered" : cleared[0].Reason;
                    var duration = now - Utc(incident.StartedAt);
                    await db.ExecuteAsync(Query("""
                        UPDATE alert_incidents SET status = 'resolved', resolved_at_utc = @now, resolution = @reason, last_changed_at_utc = @now
                        WHERE id = @id
                        """, ct, new { now = now.UtcDateTime, reason, id = incident.Id }, tx));
                    var resolved = await EventAsync(context, incident.Id, "resolved", null, current, null,
                        new IncidentEventDetail(null, null, reason, duration.TotalSeconds));
                    if (policies.Any(policy => policy.OnResolution))
                        pending.Add(new(incident.Id, resolved, NotificationKind.Resolved, AlertSeverity.Info, $"{incident.Title} resolved",
                            reason == "recovered" ? $"Recovered after {Duration(duration)}."
                                : $"Closed after {Duration(duration)} because its {(reason == "ruleDeleted" ? "rule was deleted" : reason == "ruleChanged" ? "rule changed" : "rule was disabled")}.",
                            [], Resolved: true));
                    activeFamilies.Remove(key);
                    context.Changed = true;
                    break;
                }
            }

            if (change != IncidentChange.Resolve && active.Length > 0)
            {
                var interval = active.Select(rule => rule.Policy.ReminderMinutes).Where(minutes => minutes > 0).DefaultIfEmpty(0).Min();
                var since = Utc(incident.LastNotifiedAt ?? incident.StartedAt);
                if (interval > 0 && now - since >= TimeSpan.FromMinutes(interval))
                {
                    var sequence = (long)((now - Utc(incident.StartedAt)).TotalMinutes / interval);
                    var reminder = await EventAsync(context, incident.Id, "reminder", null, null, target ?? current, null,
                        dedupe: $"reminder:{sequence.ToString(CultureInfo.InvariantCulture)}");
                    if (reminder is { } id)
                        pending.Add(new(incident.Id, id, NotificationKind.Reminder, target ?? current, $"{incident.Title} is still active",
                            $"{Label(target ?? current)} since {Duration(now - Utc(incident.StartedAt))} ago.", policies));
                }
            }
        }

        foreach (var notification in pending.Where(item => item.EventId is not null))
            await NotifyAsync(context, notification, activeFamilies);
    }

    private static async Task ContributeAsync(Context context, long incident, Evaluated rule) =>
        await context.Db.ExecuteAsync(Query("""
            INSERT INTO alert_incident_rules (incident_id, rule_id, rule_revision, rule_name, severity, active, first_active_at_utc, last_active_at_utc)
            VALUES (@incident, @Id, @Revision, @Name, @Severity, TRUE, @now, @now)
            ON DUPLICATE KEY UPDATE active = TRUE, cleared_at_utc = NULL, cleared_reason = NULL, last_active_at_utc = @now,
                rule_revision = @Revision, rule_name = @Name, severity = @Severity
            """, context.Ct, new { incident, rule.Row.Id, rule.Row.Revision, rule.Row.Name, Severity = AlertJson.Severity(rule.Severity),
                now = context.Now.UtcDateTime }, context.Tx));

    /// <returns>The event id, or null when a deduplicated event already exists.</returns>
    private static async Task<long?> EventAsync(Context context, long incident, string kind, Evaluated? rule, AlertSeverity? from,
        AlertSeverity? to, IncidentEventDetail? detail, long? ruleId = null, string? dedupe = null)
    {
        // Deduplicated events (reminders) are checked under the property lock; plain events never collide.
        if (dedupe is not null && await context.Db.ExecuteScalarAsync<long?>(Query(
                "SELECT id FROM alert_incident_events WHERE incident_id = @incident AND dedupe_key = @dedupe", context.Ct,
                new { incident, dedupe }, context.Tx)) is not null)
            return null;
        await context.Db.ExecuteAsync(Query("""
            INSERT INTO alert_incident_events (incident_id, property_id, occurred_at_utc, observed_at_utc, kind, rule_id, rule_revision,
                from_severity, to_severity, dedupe_key, detail)
            VALUES (@incident, @property, @now, @observed, @kind, @rule, @revision, @from, @to, @dedupe, @detail)
            """, context.Ct, new
        {
            incident, property = context.Property, now = context.Now.UtcDateTime, observed = rule?.Evaluation.NewestObservation?.UtcDateTime, kind,
            rule = rule?.Row.Id ?? ruleId, revision = rule?.Row.Revision, from = from is { } f ? AlertJson.Severity(f) : null,
            to = to is { } t ? AlertJson.Severity(t) : null, dedupe, detail = detail is null ? null : AlertJson.Write(detail),
        }, context.Tx));
        return await context.Db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", context.Ct, transaction: context.Tx));
    }

    private async Task NotifyAsync(Context context, PendingNotification notification, IReadOnlySet<string> activeFamilies)
    {
        var (db, tx, now, ct) = (context.Db, context.Tx, context.Now, context.Ct);
        string? suppressed = notification.Resolved ? null : notification.Policies.Select(policy => policy.SuppressWhileActive)
            .FirstOrDefault(family => family is not null && activeFamilies.Contains(family)) is { } family ? $"suppressed:{family}" : null;
        if (suppressed is null && notification.Kind is NotificationKind.Escalated or NotificationKind.Deescalated &&
            notification.Severity != AlertSeverity.Critical &&
            await db.ExecuteScalarAsync<long>(Query("""
                SELECT COUNT(*) FROM notifications WHERE incident_id = @IncidentId AND kind IN ('escalated', 'deescalated')
                  AND suppressed_reason IS NULL AND created_at_utc >= @since
                """, ct, new { notification.IncidentId, since = (now - TimeSpan.FromSeconds(options.MinimumRenotifySeconds)).UtcDateTime }, tx)) > 0)
            suppressed = "rateLimited";

        await db.ExecuteAsync(Query("""
            INSERT INTO notifications (property_id, incident_id, event_id, kind, severity, title, body, created_at_utc, suppressed_reason)
            VALUES (@property, @IncidentId, @EventId, @kind, @severity, @title, @body, @now, @suppressed)
            """, ct, new
        {
            property = context.Property, notification.IncidentId, notification.EventId, kind = Lower(notification.Kind),
            severity = AlertJson.Severity(notification.Severity), title = Truncate(notification.Title, 160), body = Truncate(notification.Body, 500),
            now = now.UtcDateTime, suppressed,
        }, tx));
        var id = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
        var members = (await db.QueryAsync<string>(Query("SELECT user_id FROM property_memberships WHERE property_id = @property ORDER BY user_id",
            ct, new { property = context.Property }, tx))).ToArray();
        var preferences = await NotificationStore.PreferencesAsync(db, tx, members, ct);
        foreach (var user in members)
        {
            var (inApp, channels) = preferences[user].Route(notification.Severity, notification.Kind);
            await db.ExecuteAsync(Query("""
                INSERT INTO notification_recipients (notification_id, user_id, in_app) VALUES (@id, @user, @inApp)
                """, ct, new { id, user, inApp = inApp && suppressed is null }, tx));
            foreach (var channel in channels)
                await db.ExecuteAsync(Query("""
                    INSERT INTO notification_deliveries (notification_id, user_id, channel, status, reason, created_at_utc, updated_at_utc)
                    VALUES (@id, @user, @channel, @status, @suppressed, @now, @now)
                    """, ct, new { id, user, channel, status = suppressed is null ? "pending" : "suppressed", suppressed, now = now.UtcDateTime }, tx));
        }
        if (suppressed is null)
            await db.ExecuteAsync(Query("UPDATE alert_incidents SET last_notified_at_utc = @now WHERE id = @IncidentId", ct,
                new { now = now.UtcDateTime, notification.IncidentId }, tx));
        context.Notifications.Add(id);
    }

    private static IncidentEventDetail Detail(Evaluated rule) => new(rule.Row.Name, rule.Evaluation.Conditions);

    /// <summary>The values behind a rule's current result, for notification text. Never includes identifiers or credentials.</summary>
    internal static string Summary(RuleEvaluation evaluation)
    {
        var parts = evaluation.Conditions.SelectMany(condition => condition.Signals.Where(signal => signal.Result == Truth.True))
            .Select(signal => $"{signal.Label} {Value(signal)}").Distinct().Take(4).ToArray();
        return parts.Length == 0 ? "" : string.Join("; ", parts) + ".";
    }

    private static string Value(SignalEvidence signal) => signal.Events > 0 ? $"({signal.Events}×)"
        : signal.Number is { } number ? $"{number.ToString("0.##", CultureInfo.InvariantCulture)}{Unit(signal)}"
        : signal.Flag is { } flag ? flag ? "on" : "off" : signal.Text ?? "";

    private static string Unit(SignalEvidence signal) =>
        signal.Signal.Metric is { } metric && Telemetry.Unit(metric) is { } unit ? unit == "%" ? "%" : $" {unit}" : "";

    internal static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? "less than a minute"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{(int)span.TotalDays} d {span.Hours} h";

    private static string Label(AlertSeverity severity) => severity.ToString();
    private static string Lower<T>(T value) where T : struct, Enum => JsonNamingCamel(value.ToString());
    private static string JsonNamingCamel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    private sealed class Context(MySqlConnection db, MySqlTransaction tx, long property, DateTimeOffset now, CancellationToken ct)
    {
        public MySqlConnection Db { get; } = db;
        public MySqlTransaction Tx { get; } = tx;
        public long Property { get; } = property;
        public DateTimeOffset Now { get; } = now;
        public CancellationToken Ct { get; } = ct;
        public bool Changed { get; set; }
        public List<long> Notifications { get; } = [];
    }

    private sealed record Evaluated(AlertRuleStore.RuleRow Row, AlertSeverity Severity, string Key, NotificationPolicy Policy, RuleRuntime State,
        RuleTransition Transition, RuleEvaluation Evaluation);
    private sealed record PendingNotification(long IncidentId, long? EventId, NotificationKind Kind, AlertSeverity Severity, string Title,
        string Body, IReadOnlyList<NotificationPolicy> Policies, bool Resolved = false);
    private sealed record StateRow(long RuleId, string Phase, DateTime? TrueSince, DateTime? FalseSince, DateTime? ActiveSince,
        DateTime? InputsObservedAt, DateTime? LastTriggeredAt);
    private sealed record IncidentRow(long Id, string CorrelationKey, string Title, string Severity, DateTime StartedAt, DateTime? LastNotifiedAt);
    private sealed record ContributorRow(long IncidentId, long RuleId, string Severity, bool Active);
    private sealed record RuleInfo(long Id, string Name, bool Enabled, DateTime? DeletedAt, string? CorrelationKey, string NotificationPolicy);
}
