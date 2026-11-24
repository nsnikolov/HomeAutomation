using System.Globalization;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattCloudSource(GrowattCloudClient client, GrowattOptions options, IMemoryCache cache,
    TimeProvider clock, ILogger<GrowattCloudSource>? logger = null)
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
            {
                if (options.TelemetryDiagnostics)
                    logger?.LogInformation("Growatt {Role} latest storage observation skipped because its source time could not be resolved.",
                        serial == options.MasterId ? "Master" : "Slave");
                continue;
            }

            var observedAt = new DateTimeOffset(localTime, zone.GetUtcOffset(localTime)).ToUniversalTime();
            if (observedAt > receivedAt)
            {
                if (options.TelemetryDiagnostics)
                    logger?.LogInformation("Growatt {Role} latest storage observation skipped because its source time is in the future.",
                        serial == options.MasterId ? "Master" : "Slave");
                continue;
            }
            var inverter = new List<Sample>();
            AddNumber(inverter, Metric.PvPower, reading.Ppv, observedAt);
            AddNumber(inverter, Metric.PvVoltage, reading.Vpv, observedAt);
            var pv1 = OptionalNumber(reading.Vpv1);
            AddNumber(inverter, Metric.Pv1Voltage, pv1, observedAt);
            AddNumber(inverter, Metric.GridVoltage, reading.VGrid, observedAt);
            AddNumber(inverter, Metric.GridFrequency, reading.FreqGrid, observedAt);
            AddNumber(inverter, Metric.ActivePower, reading.OutPutPower, observedAt);
            AddNumber(inverter, Metric.AcOutputVoltage, reading.OutPutVolt, observedAt);
            AddNumber(inverter, Metric.InverterLoadPercent, reading.LoadPercent, observedAt);
            AddNumber(inverter, Metric.AcChargingPower, reading.PAcCharge, observedAt);
            var inputPower = OptionalNumber(reading.AcInputPower);
            AddNumber(inverter, Metric.AcInputPower, inputPower, observedAt);
            var batteryPower = OptionalNumber(reading.BatteryPower);
            // SPF pBat is positive for discharge. The domain uses positive for charging.
            if (batteryPower is { } watts)
                inverter.Add(Sample.Numeric(Metric.BatteryPower, observedAt, watts == 0 ? 0 : -watts));
            if (options.TelemetryDiagnostics)
                logger?.LogInformation(
                    "Growatt {Role} latest storage fields at {ObservedAt}. vpv={PvVoltage}, vpv1={Pv1Kind}/{Pv1Mapped}, ppv={PvPower}, " +
                    "ipv={PvCurrentKind}, iChargePV1={PvChargingCurrentKind}, pAcInPut={AcInputKind}/{AcInputState}, " +
                    "pAcCharge={AcCharging}, pBat={BatteryKind}/{BatteryState}, pCharge={ChargeKind}, pDischarge={DischargeKind}.",
                    serial == options.MasterId ? "Master" : "Slave", observedAt, reading.Vpv.HasValue, reading.Vpv1.ValueKind, pv1 >= 0,
                    reading.Ppv.HasValue, reading.PvCurrent.ValueKind, reading.Pv1ChargingCurrent.ValueKind,
                    reading.AcInputPower.ValueKind, Describe(reading.AcInputPower, inputPower >= 0 ? inputPower : null),
                    reading.PAcCharge.HasValue, reading.BatteryPower.ValueKind, Describe(reading.BatteryPower, batteryPower),
                    reading.ChargePower.ValueKind, reading.DischargePower.ValueKind);
            if (reading.WarnCode is >= 0)
                inverter.Add(Sample.Status(Metric.WarningStatus, observedAt, reading.WarnCode.Value.ToString(CultureInfo.InvariantCulture)));
            var mode = ValidMode(reading.Spf5000StatusText) ? reading.Spf5000StatusText : reading.StatusText;
            if (ValidMode(mode) && mode is not null)
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

    private static double? OptionalNumber(JsonElement field)
    {
        if (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number))
            return double.IsFinite(number) ? number : null;
        if (field.ValueKind == JsonValueKind.String &&
            double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return number;
        return null;
    }

    private static string Describe(JsonElement field, double? value) => field.ValueKind switch
    {
        JsonValueKind.Undefined => "absent",
        JsonValueKind.Null => "null",
        _ when value == 0 => "zero",
        _ when value is not null => "mapped",
        _ => "invalid"
    };

    private static bool ValidMode(string? mode) => mode is { Length: > 0 and <= 32 } &&
        !string.IsNullOrWhiteSpace(mode) &&
        mode.All(character => char.IsAsciiLetter(character) || character is ' ' or '-' or '/' or '&');

    private static void AddNumber(List<Sample> samples, Metric metric, double? input, DateTimeOffset observedAt)
    {
        if (input is { } value && double.IsFinite(value) && value >= 0)
            samples.Add(Sample.Numeric(metric, observedAt, value));
    }
}
