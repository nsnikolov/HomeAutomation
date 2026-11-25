using HomeAutomation.Domain;

namespace HomeAutomation.Application;

public sealed record DashboardValue(string Id, string SourceKey, string? Label, string ColorMode = "automatic", string? CustomColor = null);
public sealed record DashboardWidget(string Id, string Title, string Mode, string? NavigationSource,
    DashboardValue[] Values, int Width = 1, int MeasurementColumns = 2);
public sealed record DashboardGroup(string Id, string Title, bool Hidden, bool Collapsed, DashboardWidget[] Widgets);
public sealed record DashboardUpdate(string Revision, DashboardGroup[] Groups);

/// <summary>
/// A dashboard measurement source. Logical sources use provider "logical" and key <c>logical:{id}:{channel}:{Metric}</c>.
/// <see cref="Status"/> is null when the source resolves to one current measurement, otherwise unsupported, unassigned,
/// ambiguous or archived. <see cref="PhysicalProvider"/> names the provider of a logical source's current device.
/// </summary>
public sealed record DashboardSource(string Key, string Provider, string DeviceId, string DeviceName, string? Channel,
    Metric Metric, string? Unit, string Route, DashboardReading? Reading, string? Status = null, string? PhysicalProvider = null);

/// <summary>A proposed rebinding of one saved dashboard value from a physical to a logical source.</summary>
public sealed record LogicalBindingProposal(string ValueId, string WidgetTitle, string SourceKey, string LogicalKey, string LogicalName,
    Metric Metric);
public sealed record LogicalBindingSkip(string ValueId, string SourceKey, string Reason);
public sealed record LogicalBindingReview(string Revision, IReadOnlyList<LogicalBindingProposal> Proposals,
    IReadOnlyList<LogicalBindingSkip> Skipped);
public sealed record LogicalBindingUpdate(string Revision, string[] ValueIds);
public sealed record DashboardReading(double? Number, bool? Flag, string? Code, DateTimeOffset ObservedAt, string Provenance);
public sealed record DashboardSnapshot(string Revision, bool CanEdit, DashboardGroup[] Groups, DashboardSource[] Sources);

public enum DashboardSaveResult { Saved, Forbidden, Conflict, InvalidSource }

public interface IMonitoringDashboard
{
    Task<bool> SchemaReadyAsync(CancellationToken ct);
    Task<DashboardSnapshot> ReadAsync(long property, string user, CancellationToken ct);
    Task<IReadOnlyList<DashboardSource>?> CatalogAsync(long property, string user, CancellationToken ct);
    Task<DashboardSaveResult> ReplaceAsync(long property, string user, DashboardUpdate update, CancellationToken ct);
    Task<LogicalBindingReview?> LogicalBindingsAsync(long property, string user, CancellationToken ct);
    Task<DashboardSaveResult> ConvertToLogicalAsync(long property, string user, LogicalBindingUpdate update, CancellationToken ct);
}

public interface IMonitoringDashboardNotifier
{
    Task ChangedAsync(long property, CancellationToken ct);
}
