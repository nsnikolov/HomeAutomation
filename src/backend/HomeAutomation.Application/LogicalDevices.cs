using HomeAutomation.Domain;

namespace HomeAutomation.Application;

public sealed record LogicalAssignmentView(
    long Id, string ChannelKey, long DeviceId, string DeviceName, string Provider, string Manufacturer, string Model,
    long? EndpointId, string? EndpointName, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string AssignedBy,
    DateTimeOffset AssignedAt, string? Reason, IReadOnlyList<Capability> Capabilities);

/// <summary>A canonical metric recorded for a logical channel by at least one of its assigned devices.</summary>
/// <param name="Current">The active device of the channel has a telemetry series for this metric.</param>
public sealed record LogicalMetric(string ChannelKey, Metric Metric, string? Unit, MetricAggregation Aggregation, bool Current);

public sealed record LogicalDeviceView(
    long Id, string Name, string Kind, long? RoomId, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt, IReadOnlyList<LogicalAssignmentView> Current, IReadOnlyList<LogicalAssignmentView> Previous,
    IReadOnlyList<LogicalMetric>? Metrics = null, string Creation = "manual");

public sealed record LogicalDeviceList(bool CanManage, IReadOnlyList<LogicalDeviceView> Devices);

public sealed record LogicalAuditEntry(
    long Id, DateTimeOffset OccurredAt, string Actor, string Action, string? ChannelKey, long? OldDeviceId, long? NewDeviceId,
    DateTimeOffset? EffectiveAt, string? Reason);

public sealed record LogicalDeviceDetail(bool CanManage, LogicalDeviceView Device, IReadOnlyList<LogicalAuditEntry> Audit);

public sealed record CandidateEndpoint(long Id, string Name, IReadOnlyList<Capability> Capabilities);

/// <summary>A physical device of the property evaluated against the current source of one logical channel.</summary>
public sealed record ReplacementCandidate(
    long DeviceId, string Name, string Provider, string Manufacturer, string Model, string Kind, long? RoomId,
    IReadOnlyList<CandidateEndpoint> Endpoints, IReadOnlyList<Capability> Capabilities, IReadOnlyList<Capability> Shared,
    IReadOnlyList<Capability> Missing, IReadOnlyList<Capability> Added, bool Compatible, bool RequiresEndpoint,
    long? AssignedLogicalDeviceId, string? AssignedLogicalDeviceName, bool Current, string? AssignedLogicalCreation = null);

/// <summary>A physical device of the property with the logical device that currently uses it, if any.</summary>
/// <param name="EverAssigned">The device has had a logical assignment, so automatic provisioning never creates one for it again.</param>
public sealed record PhysicalDeviceView(
    long Id, string Name, string Provider, string Manufacturer, string Model, string Kind, long? RoomId,
    IReadOnlyList<Capability> Capabilities, IReadOnlyList<CandidateEndpoint> Endpoints, bool RequiresEndpoint,
    long? LogicalDeviceId, string? LogicalDeviceName, string? LogicalCreation, bool EverAssigned);

public sealed record PhysicalDeviceList(bool CanManage, IReadOnlyList<PhysicalDeviceView> Devices);

/// <summary>
/// What assigning hardware to another logical device would do to the logical device that uses it now. Readings of the hardware
/// before the effective time stay with that logical device; dashboard measurements that reference it are counted, never changed.
/// </summary>
public sealed record ReleasePreview(
    long LogicalDeviceId, string Name, string Creation, IReadOnlyList<string> Channels, long Readings, DateTimeOffset? FirstReading,
    DateTimeOffset? LastReading, bool LeavesUnassigned, bool HasOtherHardwareHistory, int DashboardReferences);

public sealed record CreateLogicalDevice(string Name, string Kind, long? RoomId, string? Description, long? DeviceId,
    long? EndpointId, string? ChannelKey, DateTimeOffset? EffectiveAt, string? Reason);
/// <param name="ExpectedUpdatedAt">The device's <c>updatedAt</c> when the form was loaded; a newer save is reported as a conflict.</param>
public sealed record UpdateLogicalDevice(string Name, string Kind, long? RoomId, string? Description, DateTimeOffset? ExpectedUpdatedAt = null);
/// <param name="ReleaseFrom">Another logical device whose open assignment of the same hardware ends at the effective time.</param>
/// <param name="AcceptReleasedHistory">Confirms that readings recorded before the effective time stay with <paramref name="ReleaseFrom"/>.</param>
/// <param name="ArchiveReleased">Archives <paramref name="ReleaseFrom"/> when it is left without an active device.</param>
public sealed record AssignPhysicalDevice(string ChannelKey, long DeviceId, long? EndpointId, DateTimeOffset? EffectiveAt, string? Reason,
    long? ReleaseFrom = null, bool AcceptReleasedHistory = false, bool ArchiveReleased = false);
public sealed record ReplacePhysicalDevice(string ChannelKey, long ExpectedAssignmentId, long DeviceId, long? EndpointId,
    DateTimeOffset? EffectiveAt, string? Reason, bool AcceptMissingCapabilities, long? ReleaseFrom = null, bool AcceptReleasedHistory = false,
    bool ArchiveReleased = false);
public sealed record UnassignPhysicalDevice(string ChannelKey, long ExpectedAssignmentId, DateTimeOffset? EffectiveAt, string? Reason);
public sealed record RestoreLogicalDevice(string? Reason);
public sealed record CorrectAssignmentStart(DateTimeOffset EffectiveAt, string Reason);
public sealed record ArchiveLogicalDevice(string? Reason);

public enum LogicalChangeStatus
{
    Done, NotFound, Forbidden, Invalid, Conflict, Incompatible, MissingCapabilities, AmbiguousEndpoint, SchemaPending,
    /// <summary>The hardware is the active device of another logical device; the caller must name it to release it.</summary>
    ReleaseRequired,
    /// <summary>Releasing leaves recorded readings with the other logical device; the caller must confirm.</summary>
    HistoryConfirmation
}

public sealed record LogicalChangeResult(LogicalChangeStatus Status, string? Message = null, long? Id = null,
    IReadOnlyList<Capability>? Missing = null)
{
    public static LogicalChangeResult Done(long id) => new(LogicalChangeStatus.Done, null, id);
    public static LogicalChangeResult Fail(LogicalChangeStatus status, string message, IReadOnlyList<Capability>? missing = null) =>
        new(status, message, null, missing);
}

/// <summary>One assignment interval of a logical channel resolved to a physical series for a metric.</summary>
/// <param name="Status">available, unsupported (the device does not report the metric) or ambiguous (several endpoints report it).</param>
/// <param name="MaximumHoldSeconds">How long one observation of the segment's series may represent the value; raw charts break lines at longer gaps.</param>
public sealed record LogicalHistorySegment(
    long AssignmentId, long DeviceId, string DeviceName, long? EndpointId, DateTimeOffset From, DateTimeOffset? To, string Status,
    long? SeriesId, string? Source, IReadOnlyList<long> AlternativeSeriesIds, double? MaximumHoldSeconds = null);

public sealed record LogicalRawPoint(
    DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt, double? Number, bool? Flag, string? Code, string TimestampSource,
    long SeriesId, long AssignmentId);

public sealed record LogicalHistoryResult(
    long LogicalDeviceId, string Name, string ChannelKey, Metric Metric, string? Unit, MetricAggregation Aggregation,
    HistoryResolution Resolution, DateTimeOffset From, DateTimeOffset To, string TimeZone, double IncompleteBelowCoverage,
    IReadOnlyList<LogicalHistorySegment> Segments, IReadOnlyList<LogicalRawPoint>? Points, IReadOnlyList<HistoryBucket>? Buckets,
    DateTimeOffset? NextAfter);

public sealed record LogicalHistoryRead(HistoryReadStatus Status, LogicalHistoryResult? Result = null);

public interface ILogicalDevices
{
    Task<bool> SchemaReadyAsync(CancellationToken ct);
    Task<LogicalDeviceList> ListAsync(long property, string user, bool includeArchived, CancellationToken ct);
    Task<LogicalDeviceDetail?> GetAsync(long property, string user, long id, CancellationToken ct);
    Task<IReadOnlyList<ReplacementCandidate>?> CandidatesAsync(long property, long id, string channel, CancellationToken ct);
    Task<LogicalChangeResult> CreateAsync(long property, string user, CreateLogicalDevice command, CancellationToken ct);
    Task<LogicalChangeResult> UpdateAsync(long property, string user, long id, UpdateLogicalDevice command, CancellationToken ct);
    Task<LogicalChangeResult> AssignAsync(long property, string user, long id, AssignPhysicalDevice command, CancellationToken ct);
    Task<LogicalChangeResult> ReplaceAsync(long property, string user, long id, ReplacePhysicalDevice command, CancellationToken ct);
    Task<LogicalChangeResult> CorrectAsync(long property, string user, long id, long assignment, CorrectAssignmentStart command,
        CancellationToken ct);
    Task<LogicalChangeResult> ArchiveAsync(long property, string user, long id, ArchiveLogicalDevice command, CancellationToken ct);
    Task<PhysicalDeviceList> PhysicalDevicesAsync(long property, string user, CancellationToken ct);
    /// <summary>Null when the hardware is not the active device of another logical device at <paramref name="effectiveAt"/>.</summary>
    Task<ReleasePreview?> ReleasePreviewAsync(long property, long target, long deviceId, long? endpointId, DateTimeOffset effectiveAt,
        CancellationToken ct);
    Task<LogicalChangeResult> UnassignAsync(long property, string user, long id, UnassignPhysicalDevice command, CancellationToken ct);
    Task<LogicalChangeResult> RestoreAsync(long property, string user, long id, RestoreLogicalDevice command, CancellationToken ct);
    Task<LogicalHistoryRead> HistoryAsync(long property, long id, string channel, Metric metric, HistoryQuery query, CancellationToken ct);
}
