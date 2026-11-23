using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattMapper(GrowattOptions options, TimeProvider clock)
{
    public const string SharedBatteryId = "shared-battery";

    private static readonly IReadOnlyDictionary<Metric, Capability> InverterMetrics = new Dictionary<Metric, Capability>
    {
        [Metric.GridVoltage] = Capability.VoltageMeasurement,
        [Metric.GridFrequency] = Capability.FrequencyMeasurement,
        [Metric.GridAvailable] = Capability.GridAvailability,
        [Metric.GridImportPower] = Capability.PowerMeasurement,
        [Metric.PvVoltage] = Capability.VoltageMeasurement,
        [Metric.Pv1Voltage] = Capability.VoltageMeasurement,
        [Metric.PvPower] = Capability.SolarProduction,
        [Metric.ActivePower] = Capability.PowerMeasurement,
        [Metric.AcOutputVoltage] = Capability.VoltageMeasurement,
        [Metric.InverterLoadPercent] = Capability.PowerMeasurement,
        [Metric.AcChargingPower] = Capability.PowerMeasurement,
        [Metric.HouseholdPower] = Capability.HouseholdConsumption,
        [Metric.OperatingMode] = Capability.OperatingMode,
        [Metric.WarningStatus] = Capability.WarningStatus
    };
    private static readonly IReadOnlyDictionary<Metric, Capability> BatteryMetrics = new Dictionary<Metric, Capability>
    {
        [Metric.Voltage] = Capability.VoltageMeasurement, [Metric.BatterySoc] = Capability.BatteryLevel,
        [Metric.BatteryPower] = Capability.BatteryPower, [Metric.Energy] = Capability.EnergyMeasurement
    };
    private static readonly HashSet<Metric> SiteMetrics = [Metric.GridAvailable, Metric.GridImportPower, Metric.PvPower, Metric.HouseholdPower];

    public (IReadOnlyList<DiscoveredDevice> Devices, ObservationBatch Batch) Map(GrowattSnapshot snapshot)
    {
        var now = clock.GetUtcNow();
        var freshness = TimeSpan.FromSeconds(options.FreshnessSeconds);
        var reports = new Dictionary<string, GrowattInverter>(StringComparer.Ordinal);
        foreach (var report in snapshot.Inverters)
        {
            if (report.ExternalId != options.MasterId && report.ExternalId != options.SlaveId) continue;
            if (!reports.TryAdd(report.ExternalId, report) || !Enum.IsDefined(report.Connectivity) ||
                report.ObservedAt == default || report.ObservedAt > now ||
                (report.FaultCode is { } fault && (fault.Length is 0 or > 64 || fault.Any(char.IsControl))))
            {
                throw new GrowattReadException(GrowattFailure.InvalidData);
            }
            Validate(report.InverterReadings, InverterMetrics.ContainsKey, report.ObservedAt);
            Validate(report.SharedBatteryReadings, BatteryMetrics.ContainsKey, report.ObservedAt);
        }

        Validate(snapshot.InstallationReadings, SiteMetrics.Contains, now);
        var installation = snapshot.InstallationReadings.ToList();
        reports.TryGetValue(options.MasterId, out var master);
        reports.TryGetValue(options.SlaveId, out var slave);
        var masterPv = FreshInverterSample(master, Metric.PvPower);
        var slavePv = FreshInverterSample(slave, Metric.PvPower);
        if (options.SumIndependentPv && !installation.Exists(sample => sample.Metric == Metric.PvPower) &&
            masterPv is not null && slavePv is not null && masterPv.ObservedAt == slavePv.ObservedAt)
        {
            var total = masterPv.Number!.Value + slavePv.Number!.Value;
            if (!double.IsFinite(total)) throw new GrowattReadException(GrowattFailure.InvalidData);
            installation.Add(Sample.Numeric(Metric.PvPower, masterPv.ObservedAt, total));
        }

        if (options.MasterReportsWholeSiteLoad && !installation.Exists(sample => sample.Metric == Metric.HouseholdPower) &&
            FreshInverterSample(master, Metric.HouseholdPower) is { } load)
            installation.Add(load);

        reports.TryGetValue(options.BatterySourceId, out var batterySource);
        var batteryReadings = batterySource?.SharedBatteryReadings ?? [];
        var devices = new[]
        {
            Device(options.MasterId, "Growatt master", DeviceKind.Inverter, "master", master),
            Device(options.SlaveId, "Growatt slave", DeviceKind.Inverter, "slave", slave),
            new DiscoveredDevice(SharedBatteryId, "Growatt shared battery", DeviceKind.Battery, "", "", "Shared battery",
                [new DeviceEndpoint("main", "Main", batteryReadings.Select(item => BatteryMetrics[item.Metric]).Distinct())], "shared-battery")
        };
        var observations = reports.Values.Select(report => new DeviceObservation(
            report.ExternalId, report.Connectivity, report.FaultCode, [new("main", report.InverterReadings)], report.ObservedAt)).ToList();
        if (batteryReadings.Count > 0)
        {
            var batteryAt = batteryReadings.Max(sample => sample.ObservedAt);
            observations.Add(new DeviceObservation(SharedBatteryId, Connectivity.Unknown, null,
                [new EndpointObservation("main", batteryReadings)], batteryAt));
        }

        return (devices, new ObservationBatch(now, observations, installation));

        Sample? FreshInverterSample(GrowattInverter? device, Metric metric) =>
            device?.Connectivity == Connectivity.Online
                ? device.InverterReadings.FirstOrDefault(sample => sample.Metric == metric &&
                    Telemetry.Quality(sample.ObservedAt, now, freshness) == ReadingQuality.Fresh)
                : null;
    }

    private static DiscoveredDevice Device(string id, string name, DeviceKind kind, string role, GrowattInverter? report)
    {
        var allowed = InverterMetrics.Values.Append(Capability.FaultStatus).ToHashSet();
        var capabilities = (report?.DiscoveredCapabilities ?? []).Where(allowed.Contains)
            .Concat((report?.InverterReadings ?? []).Select(item => InverterMetrics[item.Metric]))
            .Concat(report?.FaultCode is null ? [] : new[] { Capability.FaultStatus }).Distinct();
        return new DiscoveredDevice(id, name, kind, "", "Growatt", "SPF 5000 ES", [new DeviceEndpoint("main", "Main", capabilities)], role);
    }

    private static void Validate(IReadOnlyList<Sample> samples, Func<Metric, bool> allowed, DateTimeOffset latest)
    {
        var seen = new HashSet<Metric>();
        foreach (var sample in samples)
        {
            if (!allowed(sample.Metric) || !seen.Add(sample.Metric) || sample.ObservedAt == default || sample.ObservedAt > latest ||
                sample.Code?.Any(char.IsControl) == true)
                throw new GrowattReadException(GrowattFailure.InvalidData);
        }
    }
}
