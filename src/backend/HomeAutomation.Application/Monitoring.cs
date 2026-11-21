using HomeAutomation.Domain;

namespace HomeAutomation.Application;

public sealed record DiscoveredDevice(
    string ExternalId, string Name, DeviceKind Kind, string RoomKey,
    string Manufacturer, string Model, IReadOnlyList<DeviceEndpoint> Endpoints,
    string? InstallationRole = null);

public sealed record EndpointObservation(string EndpointKey, IReadOnlyList<Sample> Samples);
public sealed record DeviceObservation(
    string ExternalId, Connectivity Connectivity, string? FaultCode, IReadOnlyList<EndpointObservation> Endpoints,
    DateTimeOffset? ObservedAt = null);

public sealed record ObservationBatch(
    DateTimeOffset ObservedAt, IReadOnlyList<DeviceObservation> Devices, IReadOnlyList<Sample> Installation);

public interface IObservationProvider
{
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken);
    Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken);
}

public interface ITelemetryWriter
{
    Task<long> RegisterDemoInventoryAsync(IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken);
    Task PersistAsync(long propertyId, ObservationBatch batch, CancellationToken cancellationToken);
}

public sealed record PropertySummary(long Id, string Name, string TimeZone);
public sealed record RoomSummary(long Id, string Name);
public sealed record CapabilityState(Capability Code, Access Access);
public sealed record MetricReading(
    Metric Metric, string? Unit, DateTimeOffset? ObservedAt, double? Number, bool? Flag, string? Code, ReadingQuality Quality);
public sealed record EndpointState(long Id, string Name, IReadOnlyList<CapabilityState> Capabilities, IReadOnlyList<MetricReading> Measurements);
public sealed record DeviceDetails(
    long Id, long? RoomId, string Name, DeviceKind Kind, string Manufacturer, string Model,
    Connectivity Connectivity, string? FaultCode, DateTimeOffset? LastContactAt, ReadingQuality Quality,
    IReadOnlyList<EndpointState> Endpoints);
public sealed record DeviceAlert(long DeviceId, string DeviceName, string Code, DateTimeOffset OccurredAt);
public sealed record PropertyState(
    IReadOnlyList<DeviceDetails> Devices, IReadOnlyList<MetricReading> Installation, IReadOnlyList<DeviceAlert> Alerts);
public sealed record HistoryPoint(DateTimeOffset ObservedAt, double? Number, bool? Flag, string? Code);

public interface IMonitoringReader
{
    Task<IReadOnlyList<PropertySummary>> PropertiesAsync(string userId, CancellationToken cancellationToken);
    Task<bool> CanReadAsync(string userId, long propertyId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoomSummary>> RoomsAsync(long propertyId, CancellationToken cancellationToken);
    Task<PropertyState> StateAsync(long propertyId, CancellationToken cancellationToken);
    Task<DeviceDetails?> DeviceAsync(long propertyId, long deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistoryPoint>> HistoryAsync(
        long propertyId, long? endpointId, Metric metric, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);
}
