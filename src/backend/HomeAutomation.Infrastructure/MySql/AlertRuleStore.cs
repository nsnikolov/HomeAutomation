using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Alert rule persistence. Any member reads rules; Owner, Admin and Operator change them, as for the shared dashboard.
/// Rules are soft-deleted so incidents and notifications keep their history. Every change writes a revision snapshot.
/// </summary>
public sealed class AlertRuleStore(Database database, TimeProvider clock, IAlertTrigger? trigger = null) : IAlertRules
{
    internal static bool CanConfigure(string? role) => role?.ToUpperInvariant() is "OWNER" or "ADMIN" or "OPERATOR";

    public async Task<AlertRuleList?> ListAsync(string user, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var role = await PropertyManagementStore.RoleAsync(db, null, property, user, ct);
        if (role is null) return null;
        var rows = (await db.QueryAsync<RuleRow>(Query(SelectRules + " WHERE r.property_id = @property AND r.deleted_at_utc IS NULL ORDER BY r.name, r.id",
            ct, new { property }))).ToArray();
        return new AlertRuleList(CanConfigure(role), await ViewsAsync(db, property, rows, ct));
    }

    public async Task<AlertRuleView?> GetAsync(string user, long property, long rule, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await PropertyManagementStore.RoleAsync(db, null, property, user, ct) is null) return null;
        var row = await db.QuerySingleOrDefaultAsync<RuleRow>(Query(SelectRules +
            " WHERE r.id = @rule AND r.property_id = @property AND r.deleted_at_utc IS NULL", ct, new { rule, property }));
        return row is null ? null : (await ViewsAsync(db, property, [row], ct))[0];
    }

    public async Task<IReadOnlyList<AlertRuleRevision>?> RevisionsAsync(string user, long property, long rule, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await PropertyManagementStore.RoleAsync(db, null, property, user, ct) is null) return null;
        var rows = await db.QueryAsync<(int Revision, string Action, DateTime ChangedAt)>(Query("""
            SELECT v.revision, v.action, v.changed_at_utc FROM alert_rule_revisions v JOIN alert_rules r ON r.id = v.rule_id
            WHERE v.rule_id = @rule AND r.property_id = @property ORDER BY v.revision DESC LIMIT 100
            """, ct, new { rule, property }));
        return rows.Select(row => new AlertRuleRevision(row.Revision, row.Action, Utc(row.ChangedAt))).ToArray();
    }

    public async Task<IReadOnlyList<SignalCatalogEntry>?> CatalogAsync(string user, long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await PropertyManagementStore.RoleAsync(db, null, property, user, ct) is null) return null;
        return await AlertSignalReader.CatalogAsync(db, property, ct);
    }

    public async Task<AlertTemplateList?> TemplatesAsync(string user, long property, CancellationToken ct)
    {
        var catalog = await CatalogAsync(user, property, ct);
        return catalog is null ? null : AlertTemplates.Build(catalog);
    }

    public async Task<AlertRulePreview?> PreviewAsync(string user, long property, AlertRuleInput input, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        if (await PropertyManagementStore.RoleAsync(db, null, property, user, ct) is null) return null;
        var now = clock.GetUtcNow();
        var (_, errors) = Normalize(input);
        if (errors.Count == 0) errors.AddRange(await AlertSignalReader.ValidateReferencesAsync(db, null, property, input.Definition, ct));
        if (errors.Count > 0) return new AlertRulePreview(false, errors, Truth.Unknown, [], now);
        // Read-only: no transaction, no state, incident or notification writes.
        var read = await AlertSignalReader.LoadAsync(db, null, property, Domain.AlertSignals.Conditions(input.Definition).ToArray(), now, ct);
        var evaluation = RuleEvaluator.Evaluate(input.Definition, read, now, latched: false);
        return new AlertRulePreview(true, [], evaluation.Result, evaluation.Conditions, now);
    }

    public async Task<ManagementResult> CreateAsync(string user, long property, AlertRuleInput input, CancellationToken ct) =>
        await CreateManyAsync(user, property, new AlertRuleBatchRequest([input]), ct);

    public async Task<ManagementResult> CreateManyAsync(string user, long property, AlertRuleBatchRequest request, CancellationToken ct)
    {
        if (request.Rules is not { Count: > 0 and <= 10 }) return Invalid("Send between one and ten rules.");
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockPropertyAsync(db, tx, property, user, ct) is { } denied) return denied;
        long? last = null;
        foreach (var input in request.Rules)
        {
            var (rule, errors) = Normalize(input);
            if (errors.Count == 0) errors.AddRange(await AlertSignalReader.ValidateReferencesAsync(db, tx, property, input.Definition, ct));
            if (errors.Count > 0) return Invalid(string.Join(" ", errors));
            var now = clock.GetUtcNow().UtcDateTime;
            await db.ExecuteAsync(Query("""
                INSERT INTO alert_rules (property_id, name, description, enabled, severity, correlation_key, incident_title, definition,
                    activation_seconds, recovery_seconds, notification_policy, template_code, device_ids, revision, created_by, created_at_utc,
                    updated_by, updated_at_utc)
                VALUES (@property, @Name, @Description, @Enabled, @Severity, @CorrelationKey, @IncidentTitle, @Definition, @ActivationSeconds,
                    @RecoverySeconds, @Policy, @TemplateCode, @DeviceIds, 1, @user, @now, @user, @now)
                """, ct, Parameters(rule!, property, user, now), tx));
            last = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
            await RevisionAsync(db, tx, last.Value, 1, "created", rule!, user, now, ct);
        }
        await tx.CommitAsync(ct);
        trigger?.PropertyChanged(property);
        return ManagementResult.Done(last);
    }

    public async Task<ManagementResult> UpdateAsync(string user, long property, long rule, AlertRuleInput input, CancellationToken ct)
    {
        var (normalized, errors) = Normalize(input);
        if (errors.Count > 0) return Invalid(string.Join(" ", errors));
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockPropertyAsync(db, tx, property, user, ct) is { } denied) return denied;
        var current = await LockRuleAsync(db, tx, property, rule, ct);
        if (current is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The rule no longer exists.");
        if (input.ExpectedRevision != current.Revision) return Conflict();
        var references = await AlertSignalReader.ValidateReferencesAsync(db, tx, property, input.Definition, ct);
        if (references.Count > 0) return Invalid(string.Join(" ", references));
        var now = clock.GetUtcNow().UtcDateTime;
        var revision = current.Revision + 1;
        await db.ExecuteAsync(Query("""
            UPDATE alert_rules SET name = @Name, description = @Description, enabled = @Enabled, severity = @Severity,
                correlation_key = @CorrelationKey, incident_title = @IncidentTitle, definition = @Definition, activation_seconds = @ActivationSeconds,
                recovery_seconds = @RecoverySeconds, notification_policy = @Policy, template_code = @TemplateCode, device_ids = @DeviceIds,
                revision = @revision, updated_by = @user, updated_at_utc = @now
            WHERE id = @rule
            """, ct, new DynamicParameters(Parameters(normalized!, property, user, now)).With(new { revision, rule }), tx));
        // Detection continues across an edit: the next evaluation applies the new revision to the current state. A disabled rule restarts.
        if (!normalized!.Enabled) await ResetStateAsync(db, tx, rule, ct);
        await RevisionAsync(db, tx, rule, revision, "updated", normalized, user, now, ct);
        await tx.CommitAsync(ct);
        trigger?.PropertyChanged(property);
        return ManagementResult.Done(rule);
    }

    public async Task<ManagementResult> SetEnabledAsync(string user, long property, long rule, AlertEnabledRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockPropertyAsync(db, tx, property, user, ct) is { } denied) return denied;
        var current = await LockRuleAsync(db, tx, property, rule, ct);
        if (current is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The rule no longer exists.");
        if (request.ExpectedRevision != current.Revision) return Conflict();
        if (current.Enabled == request.Enabled) return ManagementResult.Done(rule);
        var input = current.Input() with { Enabled = request.Enabled };
        if (request.Enabled)
        {
            var references = await AlertSignalReader.ValidateReferencesAsync(db, tx, property, input.Definition, ct);
            if (references.Count > 0) return Invalid(string.Join(" ", references) + " Edit the rule before enabling it.");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var revision = current.Revision + 1;
        await db.ExecuteAsync(Query("""
            UPDATE alert_rules SET enabled = @Enabled, revision = @revision, updated_by = @user, updated_at_utc = @now WHERE id = @rule
            """, ct, new { request.Enabled, revision, user, now, rule }, tx));
        await ResetStateAsync(db, tx, rule, ct);
        await RevisionAsync(db, tx, rule, revision, request.Enabled ? "enabled" : "disabled", Normalize(input).Rule!, user, now, ct);
        await tx.CommitAsync(ct);
        trigger?.PropertyChanged(property);
        return ManagementResult.Done(rule);
    }

    public async Task<ManagementResult> DuplicateAsync(string user, long property, long rule, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockPropertyAsync(db, tx, property, user, ct) is { } denied) return denied;
        var current = await LockRuleAsync(db, tx, property, rule, ct);
        if (current is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The rule no longer exists.");
        var name = "Copy of " + current.Name;
        // A copy starts disabled so it never doubles alerts until someone reviews and enables it.
        var copy = Normalize(current.Input() with { Name = name.Length > AlertLimits.MaxNameLength ? name[..AlertLimits.MaxNameLength] : name,
            Enabled = false }).Rule!;
        var now = clock.GetUtcNow().UtcDateTime;
        await db.ExecuteAsync(Query("""
            INSERT INTO alert_rules (property_id, name, description, enabled, severity, correlation_key, incident_title, definition,
                activation_seconds, recovery_seconds, notification_policy, template_code, device_ids, revision, created_by, created_at_utc,
                updated_by, updated_at_utc)
            VALUES (@property, @Name, @Description, @Enabled, @Severity, @CorrelationKey, @IncidentTitle, @Definition, @ActivationSeconds,
                @RecoverySeconds, @Policy, @TemplateCode, @DeviceIds, 1, @user, @now, @user, @now)
            """, ct, Parameters(copy, property, user, now), tx));
        var id = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
        await RevisionAsync(db, tx, id, 1, "created", copy, user, now, ct);
        await tx.CommitAsync(ct);
        return ManagementResult.Done(id);
    }

    public async Task<ManagementResult> DeleteAsync(string user, long property, long rule, int expectedRevision, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await LockPropertyAsync(db, tx, property, user, ct) is { } denied) return denied;
        var current = await LockRuleAsync(db, tx, property, rule, ct);
        if (current is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The rule no longer exists.");
        if (expectedRevision != current.Revision) return Conflict();
        var now = clock.GetUtcNow().UtcDateTime;
        var revision = current.Revision + 1;
        // Soft delete: incidents, their timelines and notifications keep referring to the rule.
        await db.ExecuteAsync(Query("""
            UPDATE alert_rules SET enabled = FALSE, deleted_at_utc = @now, revision = @revision, updated_by = @user, updated_at_utc = @now
            WHERE id = @rule
            """, ct, new { now, revision, user, rule }, tx));
        await ResetStateAsync(db, tx, rule, ct);
        await RevisionAsync(db, tx, rule, revision, "deleted", Normalize(current.Input() with { Enabled = false }).Rule!, user, now, ct);
        await tx.CommitAsync(ct);
        trigger?.PropertyChanged(property);
        return ManagementResult.Done(rule);
    }

    internal sealed record NormalizedRule(string Name, string? Description, bool Enabled, AlertSeverity Severity, string? CorrelationKey,
        string? IncidentTitle, RuleGroup Definition, int ActivationSeconds, int RecoverySeconds, NotificationPolicy Notifications,
        string? TemplateCode)
    {
        public IReadOnlyList<long> DeviceIds => Domain.AlertSignals.Conditions(Definition).SelectMany(condition => condition.Signals)
            .Where(signal => signal.DeviceId is not null).Select(signal => signal.DeviceId!.Value).Distinct().Order().ToArray();
    }

    /// <summary>Trims and validates everything that does not need the database.</summary>
    internal static (NormalizedRule? Rule, List<string> Errors) Normalize(AlertRuleInput input)
    {
        var errors = new List<string>();
        if (input is null) return (null, ["Send a rule."]);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > AlertLimits.MaxNameLength) errors.Add("Give the rule a name of at most 120 characters.");
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description?.Length > AlertLimits.MaxDescriptionLength) errors.Add("The description can have at most 500 characters.");
        if (!Enum.IsDefined(input.Severity)) errors.Add("Choose Info, Warning or Critical.");
        var correlation = string.IsNullOrWhiteSpace(input.CorrelationKey) ? null : input.CorrelationKey.Trim().ToLowerInvariant();
        if (correlation is not null && !IncidentPlanner.ValidCorrelationKey(correlation))
            errors.Add("Use a correlation group such as grid.outage: lower-case letters, digits, dots, dashes and colons.");
        var title = string.IsNullOrWhiteSpace(input.IncidentTitle) ? null : input.IncidentTitle.Trim();
        if (title?.Length > AlertLimits.MaxNameLength) errors.Add("The incident title can have at most 120 characters.");
        if (input.ActivationSeconds is < 0 or > AlertLimits.MaxTimingSeconds || input.RecoverySeconds is < 0 or > AlertLimits.MaxTimingSeconds)
            errors.Add("Confirmation and recovery periods must be between 0 seconds and 24 hours.");
        var policy = input.Notifications ?? new NotificationPolicy();
        var suppress = string.IsNullOrWhiteSpace(policy.SuppressWhileActive) ? null : policy.SuppressWhileActive.Trim().ToLowerInvariant();
        if (policy.ReminderMinutes is < 0 or > AlertLimits.MaxReminderMinutes) errors.Add("Reminders can repeat at most once a day and at least every minute.");
        else if (policy.ReminderMinutes is > 0 and < 5) errors.Add("Reminders repeat at most every 5 minutes.");
        if (suppress is not null && (!IncidentPlanner.ValidCorrelationKey(suppress) || suppress == correlation))
            errors.Add("Suppression needs another correlation group.");
        var template = string.IsNullOrWhiteSpace(input.TemplateCode) ? null : input.TemplateCode.Trim();
        if (template?.Length > 48) errors.Add("The template code is too long.");
        if (input.Definition is null) errors.Add("Add at least one condition.");
        else errors.AddRange(Domain.AlertSignals.Validate(input.Definition));
        return errors.Count > 0 ? (null, errors) : (new NormalizedRule(name, description, input.Enabled, input.Severity, correlation, title,
            input.Definition!, input.ActivationSeconds, input.RecoverySeconds, policy with { SuppressWhileActive = suppress }, template), errors);
    }

    private static object Parameters(NormalizedRule rule, long property, string user, DateTime now) => new
    {
        property, user, now, rule.Name, rule.Description, rule.Enabled, Severity = AlertJson.Severity(rule.Severity), rule.CorrelationKey,
        rule.IncidentTitle, Definition = AlertJson.Write(rule.Definition), rule.ActivationSeconds, rule.RecoverySeconds,
        Policy = AlertJson.Write(rule.Notifications), rule.TemplateCode, DeviceIds = AlertJson.Write(rule.DeviceIds),
    };

    private static Task RevisionAsync(MySqlConnection db, MySqlTransaction tx, long rule, int revision, string action, NormalizedRule snapshot,
        string user, DateTime now, CancellationToken ct) =>
        db.ExecuteAsync(Query("""
            INSERT INTO alert_rule_revisions (rule_id, revision, action, snapshot, changed_by, changed_at_utc)
            VALUES (@rule, @revision, @action, @snapshot, @user, @now)
            """, ct, new { rule, revision, action, snapshot = AlertJson.Write(snapshot), user, now }, tx));

    internal static Task ResetStateAsync(MySqlConnection db, MySqlTransaction tx, long rule, CancellationToken ct) =>
        db.ExecuteAsync(Query("""
            UPDATE alert_rule_state SET phase = 'inactive', truth = 'unknown', true_since_utc = NULL, false_since_utc = NULL,
                active_since_utc = NULL, inputs_observed_at_utc = NULL
            WHERE rule_id = @rule
            """, ct, new { rule }, tx));

    /// <summary>Locks the property row so rule changes serialize with property deletion. Null when the caller may change rules.</summary>
    private static async Task<ManagementResult?> LockPropertyAsync(MySqlConnection db, MySqlTransaction tx, long property, string user, CancellationToken ct)
    {
        var exists = await db.ExecuteScalarAsync<long?>(Query(
            "SELECT id FROM properties WHERE id = @property AND deleted_at_utc IS NULL FOR UPDATE", ct, new { property }, tx));
        var role = exists is null ? null : await PropertyManagementStore.RoleAsync(db, tx, property, user, ct);
        if (role is null) return ManagementResult.Fail(ManagementStatus.NotFound, "The property is unavailable.");
        return CanConfigure(role) ? null
            : ManagementResult.Fail(ManagementStatus.Forbidden, "Only property Owners, Admins and Operators can change alert rules.");
    }

    private static Task<RuleRow?> LockRuleAsync(MySqlConnection db, MySqlTransaction tx, long property, long rule, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<RuleRow?>(Query(SelectRules +
            " WHERE r.id = @rule AND r.property_id = @property AND r.deleted_at_utc IS NULL FOR UPDATE", ct, new { rule, property }, tx));

    private static ManagementResult Invalid(string message) => ManagementResult.Fail(ManagementStatus.Invalid, message);
    private static ManagementResult Conflict() =>
        ManagementResult.Fail(ManagementStatus.Conflict, "Someone changed this rule in the meantime. Reload it and try again.");

    private static async Task<AlertRuleView[]> ViewsAsync(MySqlConnection db, long property, RuleRow[] rows, CancellationToken ct)
    {
        if (rows.Length == 0) return [];
        var ids = rows.Select(row => row.Id).ToArray();
        var incidents = (await db.QueryAsync<(long RuleId, long IncidentId, string Severity, string Status)>(Query("""
            SELECT RuleId, IncidentId, Severity, Status FROM (
                SELECT ir.rule_id AS RuleId, i.id AS IncidentId, i.severity AS Severity, i.status AS Status,
                       ROW_NUMBER() OVER (PARTITION BY ir.rule_id ORDER BY i.id DESC) AS position
                FROM alert_incident_rules ir JOIN alert_incidents i ON i.id = ir.incident_id WHERE ir.rule_id IN @ids) latest
            WHERE position = 1
            """, ct, new { ids }))).ToDictionary(row => row.RuleId);
        var devices = (await db.QueryAsync<long>(Query("""
            SELECT id FROM devices WHERE property_id = @property AND retired_at_utc IS NULL AND inventory_key NOT LIKE 'simulator:%'
            """, ct, new { property }))).ToHashSet();
        var installations = (await db.QueryAsync<long>(Query("SELECT id FROM installations WHERE property_id = @property", ct, new { property })))
            .ToHashSet();
        return rows.Select(row =>
        {
            var input = row.Input();
            var signals = Domain.AlertSignals.Conditions(input.Definition).SelectMany(condition => condition.Signals).ToArray();
            var warnings = new List<string>();
            if (signals.Any(signal => signal.DeviceId is { } device && !devices.Contains(device)))
                warnings.Add("A referenced device moved to another property or was removed. Its conditions evaluate as unknown.");
            if (signals.Any(signal => signal.InstallationId is { } installation && !installations.Contains(installation)))
                warnings.Add("A referenced installation is no longer available.");
            incidents.TryGetValue(row.Id, out var incident);
            return new AlertRuleView(row.Id, row.PropertyId, row.Name, row.Description, row.Enabled, input.Severity, row.CorrelationKey,
                row.IncidentTitle, input.Definition, row.ActivationSeconds, row.RecoverySeconds, input.Notifications!, row.TemplateCode, row.Revision,
                Utc(row.CreatedAt), Utc(row.UpdatedAt),
                new AlertRuleStatus(row.Phase is null ? RulePhase.Inactive : Enum.Parse<RulePhase>(row.Phase, true),
                    row.Truth is null ? Truth.Unknown : Enum.Parse<Truth>(row.Truth, true), Utc(row.LastEvaluatedAt), Utc(row.LastTriggeredAt),
                    incident.IncidentId == 0 ? null : incident.IncidentId,
                    incident.IncidentId == 0 ? null : AlertJson.Severity(incident.Severity), incident.Status),
                warnings);
        }).ToArray();
    }

    internal const string SelectRules = """
        SELECT r.id AS Id, r.property_id AS PropertyId, r.name AS Name, r.description AS Description, r.enabled AS Enabled,
               r.severity AS Severity, r.correlation_key AS CorrelationKey, r.incident_title AS IncidentTitle, r.definition AS Definition,
               r.activation_seconds AS ActivationSeconds, r.recovery_seconds AS RecoverySeconds, r.notification_policy AS NotificationPolicy,
               r.template_code AS TemplateCode, r.revision AS Revision, r.created_at_utc AS CreatedAt, r.updated_at_utc AS UpdatedAt,
               s.phase AS Phase, s.truth AS Truth, s.last_evaluated_at_utc AS LastEvaluatedAt, s.last_triggered_at_utc AS LastTriggeredAt
        FROM alert_rules r LEFT JOIN alert_rule_state s ON s.rule_id = r.id
        """;

    internal sealed record RuleRow(long Id, long PropertyId, string Name, string? Description, bool Enabled, string Severity, string? CorrelationKey,
        string? IncidentTitle, string Definition, int ActivationSeconds, int RecoverySeconds, string NotificationPolicy, string? TemplateCode,
        int Revision, DateTime CreatedAt, DateTime UpdatedAt, string? Phase, string? Truth, DateTime? LastEvaluatedAt, DateTime? LastTriggeredAt)
    {
        public AlertRuleInput Input() => new(Name, Description, Enabled, AlertJson.Severity(Severity), CorrelationKey, IncidentTitle,
            AlertJson.Read<RuleGroup>(Definition)!, ActivationSeconds, RecoverySeconds,
            AlertJson.Read<NotificationPolicy>(NotificationPolicy) ?? new NotificationPolicy(), TemplateCode, Revision);
    }
}

internal static class DynamicParameterExtensions
{
    public static DynamicParameters With(this DynamicParameters parameters, object values)
    {
        parameters.AddDynamicParams(values);
        return parameters;
    }
}
