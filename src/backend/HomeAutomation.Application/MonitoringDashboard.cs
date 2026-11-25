using HomeAutomation.Domain;

namespace HomeAutomation.Application;

public sealed record DashboardValue(string Id, string SourceKey, string? Label, string ColorMode = "automatic", string? CustomColor = null);
public sealed record DashboardWidget(string Id, string Title, string Mode, string? NavigationSource,
    DashboardValue[] Values, int Width = 1, int MeasurementColumns = 2);
public sealed record DashboardGroup(string Id, string Title, bool Hidden, bool Collapsed, DashboardWidget[] Widgets);
public sealed record DashboardUpdate(string Revision, DashboardGroup[] Groups);

public sealed record DashboardSource(string Key, string Provider, string DeviceId, string DeviceName, string? Channel,
    Metric Metric, string? Unit, string Route, DashboardReading? Reading);
public sealed record DashboardReading(double? Number, bool? Flag, string? Code, DateTimeOffset ObservedAt, string Provenance);
public sealed record DashboardSnapshot(string Revision, bool CanEdit, DashboardGroup[] Groups, DashboardSource[] Sources);

public enum DashboardSaveResult { Saved, Forbidden, Conflict, InvalidSource }

public interface IMonitoringDashboard
{
    Task<bool> SchemaReadyAsync(CancellationToken ct);
    Task<DashboardSnapshot> ReadAsync(long property, string user, CancellationToken ct);
    Task<IReadOnlyList<DashboardSource>?> CatalogAsync(long property, string user, CancellationToken ct);
    Task<DashboardSaveResult> ReplaceAsync(long property, string user, DashboardUpdate update, CancellationToken ct);
}

public interface IMonitoringDashboardNotifier
{
    Task ChangedAsync(long property, CancellationToken ct);
}
