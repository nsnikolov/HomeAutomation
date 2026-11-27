using HomeAutomation.Domain;

namespace HomeAutomation.Application;

// Signals -> rules -> incidents -> notifications. Rules only ever create incidents and notifications; nothing here sends a device command.

/// <summary>A rule as entered in the editor. ExpectedRevision guards concurrent edits.</summary>
public sealed record AlertRuleInput(string Name, string? Description, bool Enabled, AlertSeverity Severity, string? CorrelationKey,
    string? IncidentTitle, RuleGroup Definition, int ActivationSeconds, int RecoverySeconds, NotificationPolicy? Notifications = null,
    string? TemplateCode = null, int? ExpectedRevision = null);

public sealed record AlertRuleStatus(RulePhase Phase, Truth Result, DateTimeOffset? LastEvaluatedAt, DateTimeOffset? LastTriggeredAt,
    long? IncidentId, AlertSeverity? IncidentSeverity, string? IncidentStatus);

/// <param name="Warnings">Problems found when the rule was read, such as a referenced device that moved away.</param>
public sealed record AlertRuleView(long Id, long PropertyId, string Name, string? Description, bool Enabled, AlertSeverity Severity,
    string? CorrelationKey, string? IncidentTitle, RuleGroup Definition, int ActivationSeconds, int RecoverySeconds,
    NotificationPolicy Notifications, string? TemplateCode, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    AlertRuleStatus Status, IReadOnlyList<string> Warnings);

public sealed record AlertRuleList(bool CanManage, IReadOnlyList<AlertRuleView> Rules);

public sealed record AlertRuleRevision(int Revision, string Action, DateTimeOffset ChangedAt);

public sealed record AlertEnabledRequest(bool Enabled, int ExpectedRevision);

public sealed record AlertRuleBatchRequest(IReadOnlyList<AlertRuleInput> Rules);

/// <summary>A signal the editor can offer, with its type, unit, valid operators and latest value.</summary>
public sealed record SignalCatalogEntry(string Key, SignalRef Signal, string Label, long? DeviceId, string? DeviceName, string? EndpointName,
    string Provider, SignalValueType ValueType, string? Unit, IReadOnlyList<ConditionOperator> Operators, IReadOnlyList<string> States,
    int SamplingSeconds, int SuggestedFreshnessSeconds, double? Number, bool? Flag, string? Text, DateTimeOffset? ObservedAt);

public sealed record AlertTemplate(string Code, string Name, string Description, bool Available, string? Unavailable, AlertRuleInput? Draft);

public sealed record AlertTemplateList(IReadOnlyList<AlertTemplate> Templates, IReadOnlyList<AlertRuleInput>? GridOutageSetup);

/// <summary>A read-only evaluation against the latest known telemetry. It never creates incidents or notifications.</summary>
public sealed record AlertRulePreview(bool Valid, IReadOnlyList<string> Errors, Truth Result, IReadOnlyList<ConditionEvidence> Conditions,
    DateTimeOffset EvaluatedAt);

public sealed record IncidentRuleView(long RuleId, string RuleName, int Revision, AlertSeverity Severity, bool Active,
    DateTimeOffset FirstActiveAt, DateTimeOffset LastActiveAt, DateTimeOffset? ClearedAt, string? ClearedReason);

public sealed record IncidentView(long Id, long PropertyId, string PropertyName, string Title, string CorrelationKey, string Status,
    AlertSeverity Severity, AlertSeverity OriginalSeverity, AlertSeverity MaxSeverity, DateTimeOffset StartedAt,
    DateTimeOffset LastChangedAt, DateTimeOffset? ResolvedAt, string? Resolution, DateTimeOffset? AcknowledgedAt,
    IReadOnlyList<IncidentRuleView> Rules);

/// <summary>Evidence stored with an incident event: the rule's conditions, observed values and timestamps.</summary>
public sealed record IncidentEventDetail(string? RuleName = null, IReadOnlyList<ConditionEvidence>? Evidence = null, string? Reason = null,
    double? DurationSeconds = null);

public sealed record IncidentEventView(long Id, DateTimeOffset OccurredAt, DateTimeOffset? ObservedAt, string Kind, long? RuleId,
    int? RuleRevision, AlertSeverity? FromSeverity, AlertSeverity? ToSeverity, IncidentEventDetail? Detail);

public sealed record IncidentDetail(IncidentView Incident, IReadOnlyList<IncidentEventView> Timeline, bool CanAcknowledge);

public sealed record IncidentList(bool CanAcknowledge, IReadOnlyList<IncidentView> Incidents);

public sealed record IncidentQuery(string? Status = null, AlertSeverity? Severity = null, long? RuleId = null, long? DeviceId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 50);

public sealed record DeliveryView(string Channel, string Status, string? Reason, DateTimeOffset UpdatedAt);

public sealed record NotificationView(long Id, long PropertyId, string PropertyName, long IncidentId, NotificationKind Kind,
    AlertSeverity Severity, string Title, string Body, DateTimeOffset CreatedAt, bool InApp, DateTimeOffset? ReadAt, string? SuppressedReason,
    string IncidentStatus, AlertSeverity IncidentSeverity, IReadOnlyList<DeliveryView> Deliveries);

public sealed record NotificationQuery(long? PropertyId = null, long? DeviceId = null, NotificationKind? Kind = null,
    AlertSeverity? Severity = null, long? RuleId = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? IncidentStatus = null,
    bool? Unread = null, bool InAppOnly = false, long? Before = null, int Limit = 50);

public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, long? Next);

/// <param name="ActiveCritical">Active Critical incidents in the user's properties; they stay visible whether or not they were read.</param>
public sealed record NotificationSummary(int Unread, int UnreadCritical, int UnreadWarning, IReadOnlyList<IncidentView> ActiveCritical);

public sealed record MarkReadRequest(IReadOnlyList<long>? Ids, bool All = false, long? PropertyId = null);

/// <summary>What the browser observed for a browser notification: shown, or not shown with a reason such as permission.</summary>
public sealed record DeliveryOutcomeRequest(string Channel, string Outcome, string? Reason);

public sealed record AlertEvaluationResult(long PropertyId, bool IncidentsChanged, IReadOnlyList<long> Notifications)
{
    public static AlertEvaluationResult Unchanged(long property) => new(property, false, []);
}

public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Full sweep cadence for durations, freshness expiry and reminders. Telemetry writes trigger earlier evaluations.</summary>
    public int SweepSeconds { get; set; } = 30;
    /// <summary>Minimum spacing of non-Critical escalation and de-escalation notifications for one incident.</summary>
    public int MinimumRenotifySeconds { get; set; } = 300;
    /// <summary>Browser deliveries not handed over within this period are marked expired instead of shown late.</summary>
    public int DeliveryExpirySeconds { get; set; } = 900;
}

public interface IAlertRules
{
    Task<AlertRuleList?> ListAsync(string user, long property, CancellationToken ct);
    Task<AlertRuleView?> GetAsync(string user, long property, long rule, CancellationToken ct);
    Task<IReadOnlyList<AlertRuleRevision>?> RevisionsAsync(string user, long property, long rule, CancellationToken ct);
    Task<IReadOnlyList<SignalCatalogEntry>?> CatalogAsync(string user, long property, CancellationToken ct);
    Task<AlertTemplateList?> TemplatesAsync(string user, long property, CancellationToken ct);
    Task<AlertRulePreview?> PreviewAsync(string user, long property, AlertRuleInput input, CancellationToken ct);
    Task<ManagementResult> CreateAsync(string user, long property, AlertRuleInput input, CancellationToken ct);
    Task<ManagementResult> CreateManyAsync(string user, long property, AlertRuleBatchRequest request, CancellationToken ct);
    Task<ManagementResult> UpdateAsync(string user, long property, long rule, AlertRuleInput input, CancellationToken ct);
    Task<ManagementResult> SetEnabledAsync(string user, long property, long rule, AlertEnabledRequest request, CancellationToken ct);
    Task<ManagementResult> DuplicateAsync(string user, long property, long rule, CancellationToken ct);
    Task<ManagementResult> DeleteAsync(string user, long property, long rule, int expectedRevision, CancellationToken ct);
}

public interface INotificationCenter
{
    Task<IncidentList?> IncidentsAsync(string user, long property, IncidentQuery query, CancellationToken ct);
    Task<IncidentDetail?> IncidentAsync(string user, long property, long incident, CancellationToken ct);
    Task<ManagementResult> AcknowledgeAsync(string user, long property, long incident, CancellationToken ct);
    Task<NotificationPage> NotificationsAsync(string user, NotificationQuery query, CancellationToken ct);
    Task<NotificationSummary> SummaryAsync(string user, CancellationToken ct);
    Task<int> MarkReadAsync(string user, MarkReadRequest request, CancellationToken ct);
    Task<ManagementResult> ReportDeliveryAsync(string user, long notification, DeliveryOutcomeRequest request, CancellationToken ct);
    Task<NotificationPreferences> PreferencesAsync(string user, CancellationToken ct);
    Task<ManagementResult> SavePreferencesAsync(string user, NotificationPreferences preferences, CancellationToken ct);
}

/// <summary>Wakes the evaluator for a property after telemetry for it was committed. Never blocks the caller.</summary>
public interface IAlertTrigger
{
    void PropertyChanged(long propertyId);
}

/// <summary>One notification for one recipient, as handed to a delivery channel.</summary>
public sealed record NotificationMessage(long NotificationId, string UserId, long PropertyId, long IncidentId, NotificationKind Kind,
    AlertSeverity Severity, string Title, string Body, DateTimeOffset CreatedAt, bool Sound);

/// <summary>Status is sent, failed or unsupported. Sent means handed over; a later client report can record what was observed.</summary>
public sealed record ChannelResult(string Status, string? Reason = null);

/// <summary>
/// An optional delivery channel. In-app presence is the persisted recipient row; channels such as the browser today, or mobile push
/// later, add attempts recorded per recipient. Implementations must not log titles, bodies or user identifiers beyond counts.
/// </summary>
public interface INotificationChannel
{
    string Name { get; }
    Task<ChannelResult> DeliverAsync(NotificationMessage message, CancellationToken ct);
}

/// <summary>Real-time refresh signals for open browsers; persistence never depends on them.</summary>
public interface IAlertLiveNotifier
{
    Task IncidentsChangedAsync(long propertyId, CancellationToken ct);
    Task NotificationsChangedAsync(IReadOnlyCollection<string> users, CancellationToken ct);
}
