using HomeAutomation.Domain;

namespace HomeAutomation.Application;

// Adapter boundary for authorized, normalized reads. This is not a Growatt wire format.
public interface IGrowattReadSource
{
    Task<GrowattSnapshot> ReadAsync(CancellationToken cancellationToken);
}

public sealed record GrowattInverter(
    string ExternalId,
    DateTimeOffset ObservedAt,
    Connectivity Connectivity,
    IReadOnlyList<Sample> InverterReadings,
    IReadOnlyList<Sample> SharedBatteryReadings,
    IReadOnlyList<Capability>? DiscoveredCapabilities = null,
    string? FaultCode = null);

public sealed record GrowattSnapshot(
    IReadOnlyList<GrowattInverter> Inverters,
    IReadOnlyList<Sample> InstallationReadings);

public enum GrowattFailure { Timeout, Unavailable, Throttled, Authorization, InvalidData }
public enum GrowattOperation { PlantList, DeviceList, LatestStorage }

public sealed record GrowattProviderFailure(GrowattOperation Operation, int? HttpStatus = null, int? Code = null);

public sealed class GrowattReadException(GrowattFailure failure, TimeSpan? retryAfter = null,
    GrowattProviderFailure? provider = null) : Exception(failure.ToString())
{
    public GrowattFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public GrowattProviderFailure? Provider { get; } = provider;
}

public interface IGrowattTelemetryStore
{
    Task<long> StoreAsync(string siteKey, IReadOnlyList<DiscoveredDevice> devices, ObservationBatch batch,
        CancellationToken cancellationToken);
}

public interface IGrowattMonitoringReader
{
    Task<PropertyState?> GrowattStateAsync(long propertyId, string siteKey, CancellationToken cancellationToken);
}
