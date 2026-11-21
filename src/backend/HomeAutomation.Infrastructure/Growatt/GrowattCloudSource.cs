using System.Globalization;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattCloudSource(GrowattCloudClient client, GrowattOptions options, IMemoryCache cache,
    TimeProvider clock)
    : IGrowattReadSource
{
    private static readonly object VerifiedInventoryKey = new();

    public async Task<GrowattSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(VerifiedInventoryKey, out _))
        {
            var devices = await client.DiscoverStorageDevicesAsync(cancellationToken);
            if (!devices.Contains(options.MasterId) || !devices.Contains(options.SlaveId))
                throw new GrowattReadException(GrowattFailure.Authorization);

            cache.Set(VerifiedInventoryKey, true, TimeSpan.FromHours(1));
        }

        GrowattCloudClient.StorageData latest;
        try
        {
            latest = await client.ReadLatestAsync(cancellationToken);
        }
        catch (GrowattReadException error) when (error.Failure == GrowattFailure.Authorization)
        {
            cache.Remove(VerifiedInventoryKey);
            throw;
        }

        return Map(latest);
    }

    private GrowattSnapshot Map(GrowattCloudClient.StorageData latest)
    {
        if (latest.Storage is null) throw new GrowattReadException(GrowattFailure.InvalidData);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var receivedAt = clock.GetUtcNow();
        var inverters = new List<GrowattInverter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in latest.Storage)
        {
            if (reading?.SerialNum is not { } serial ||
                (serial != options.MasterId && serial != options.SlaveId) || !seen.Add(serial))
                throw new GrowattReadException(GrowattFailure.InvalidData);

            if (!DateTime.TryParseExact(reading.Time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var localTime) ||
                zone.IsInvalidTime(localTime) || zone.IsAmbiguousTime(localTime))
                continue;

            var observedAt = new DateTimeOffset(localTime, zone.GetUtcOffset(localTime)).ToUniversalTime();
            if (observedAt > receivedAt) continue;
            var inverter = new List<Sample>();
            AddNumber(inverter, Metric.PvPower, reading.Ppv, observedAt);
            AddNumber(inverter, Metric.PvVoltage, reading.Vpv, observedAt);
            AddNumber(inverter, Metric.GridVoltage, reading.VGrid, observedAt);
            AddNumber(inverter, Metric.GridFrequency, reading.FreqGrid, observedAt);
            AddNumber(inverter, Metric.ActivePower, reading.OutPutPower, observedAt);
            if (reading.WarnCode is >= 0)
                inverter.Add(Sample.Status(Metric.WarningStatus, observedAt, reading.WarnCode.Value.ToString(CultureInfo.InvariantCulture)));
            if (reading.Spf5000StatusText is { Length: > 0 and <= 32 } mode &&
                !string.IsNullOrWhiteSpace(mode) &&
                mode.All(character => char.IsAsciiLetter(character) || character is ' ' or '-' or '/'))
                inverter.Add(Sample.Status(Metric.OperatingMode, observedAt, mode));

            var battery = new List<Sample>();
            if (serial == options.BatterySourceId && reading.VBat is > 0)
            {
                AddNumber(battery, Metric.Voltage, reading.VBat, observedAt);
                if (reading.Capacity is >= 0 and <= 100)
                    AddNumber(battery, Metric.BatterySoc, reading.Capacity, observedAt);
            }
            var faultCode = reading.FaultCode is > 0 ? reading.FaultCode : reading.ErrorCode;
            var fault = faultCode is > 0 ? faultCode.Value.ToString(CultureInfo.InvariantCulture) : null;
            var connectivity = reading.Lost switch
            {
                true => Connectivity.Offline,
                false => Connectivity.Online,
                null => Connectivity.Unknown
            };
            inverters.Add(new GrowattInverter(serial, observedAt, connectivity, inverter, battery, FaultCode: fault));
        }
        if (inverters.Count == 0) throw new GrowattReadException(GrowattFailure.Unavailable);
        return new GrowattSnapshot(inverters, []);
    }

    private static void AddNumber(List<Sample> samples, Metric metric, double? input, DateTimeOffset observedAt)
    {
        if (input is { } value && double.IsFinite(value) && value >= 0)
            samples.Add(Sample.Numeric(metric, observedAt, value));
    }
}
