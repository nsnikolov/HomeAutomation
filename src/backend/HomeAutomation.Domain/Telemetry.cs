namespace HomeAutomation.Domain;

public enum Metric
{
    PvPower, HouseholdPower, GridAvailable, BatterySoc, BatteryPower,
    Voltage, Current, ActivePower, Energy, Temperature, Humidity,
    SwitchOn, TargetTemperature, OperatingMode, GridVoltage, GridFrequency, GridImportPower, PvVoltage, WarningStatus,
    AcOutputVoltage, InverterLoadPercent, AcChargingPower, OutdoorTemperature, FanSpeed,
    LeavingWaterTemperature, TargetLeavingWaterTemperature, HeatingControlMode, WaterSetpointMode,
    HeatingWarning, HeatingEmergency, Pv1Voltage, AcInputPower,
    PvEnergyToday, OutputEnergyToday, BatteryDischargedEnergyToday
}

public static class Telemetry
{
    public static string? Unit(Metric metric) => metric switch
    {
        Metric.PvPower or Metric.HouseholdPower or Metric.BatteryPower or Metric.ActivePower or Metric.GridImportPower => "W",
        Metric.AcChargingPower or Metric.AcInputPower => "W",
        Metric.Voltage or Metric.GridVoltage or Metric.PvVoltage or Metric.Pv1Voltage => "V",
        Metric.AcOutputVoltage => "V",
        Metric.GridFrequency => "Hz",
        Metric.Current => "A",
        Metric.Energy or Metric.PvEnergyToday or Metric.OutputEnergyToday or Metric.BatteryDischargedEnergyToday => "kWh",
        Metric.BatterySoc or Metric.Humidity or Metric.InverterLoadPercent => "%",
        Metric.Temperature or Metric.TargetTemperature or Metric.OutdoorTemperature or
            Metric.LeavingWaterTemperature or Metric.TargetLeavingWaterTemperature => "°C",
        Metric.FanSpeed => "step",
        _ => null
    };

    public static bool IsDailyEnergyCounter(Metric metric) =>
        metric is Metric.PvEnergyToday or Metric.OutputEnergyToday or Metric.BatteryDischargedEnergyToday;

    public static ReadingQuality DailyEnergyQuality(DateTimeOffset? observedAt, DateTimeOffset now,
        TimeSpan maximumAge, TimeZoneInfo timeZone) =>
        observedAt is { } at && TimeZoneInfo.ConvertTime(at, timeZone).Date == TimeZoneInfo.ConvertTime(now, timeZone).Date
            ? Quality(observedAt, now, maximumAge)
            : ReadingQuality.Missing;

    public static ReadingQuality Quality(DateTimeOffset? observedAt, DateTimeOffset now, TimeSpan maximumAge) =>
        observedAt switch
        {
            null => ReadingQuality.Missing,
            var at when at > now || now - at > maximumAge => ReadingQuality.Stale,
            _ => ReadingQuality.Fresh
        };
}

public sealed record Sample
{
    private Sample(Metric metric, DateTimeOffset observedAt, double? number, bool? flag, string? code,
        bool serverReceivedTimestamp = false)
    {
        Metric = metric;
        ObservedAt = observedAt.ToUniversalTime();
        Number = number;
        Flag = flag;
        Code = code;
        ServerReceivedTimestamp = serverReceivedTimestamp;
    }

    public Metric Metric { get; }
    public DateTimeOffset ObservedAt { get; }
    public double? Number { get; }
    public bool? Flag { get; }
    public string? Code { get; }
    public bool ServerReceivedTimestamp { get; }

    public static Sample Numeric(Metric metric, DateTimeOffset time, double value, bool serverReceivedTimestamp = false)
    {
        if (Telemetry.Unit(metric) is null || !double.IsFinite(value) ||
            (metric is Metric.BatterySoc or Metric.Humidity && (value < 0 || value > 100)) ||
            (value < 0 && (Telemetry.IsDailyEnergyCounter(metric) ||
                metric is Metric.PvPower or Metric.HouseholdPower or Metric.Energy or Metric.Voltage or Metric.Current
                or Metric.GridVoltage or Metric.PvVoltage or Metric.Pv1Voltage or Metric.GridImportPower or Metric.GridFrequency
                or Metric.AcOutputVoltage or Metric.InverterLoadPercent or Metric.AcChargingPower or Metric.AcInputPower)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid value for the metric.");
        }

        return new Sample(metric, time, value, null, null, serverReceivedTimestamp);
    }

    public static Sample Boolean(Metric metric, DateTimeOffset time, bool value)
    {
        if (metric is not (Metric.GridAvailable or Metric.SwitchOn or Metric.HeatingWarning or Metric.HeatingEmergency))
            throw new ArgumentException("This metric is not boolean.", nameof(metric));

        return new Sample(metric, time, null, value, null);
    }

    public static Sample Status(Metric metric, DateTimeOffset time, string value)
    {
        if (metric is not (Metric.OperatingMode or Metric.WarningStatus or Metric.HeatingControlMode or Metric.WaterSetpointMode)
            || string.IsNullOrWhiteSpace(value) || value.Length > 32)
            throw new ArgumentException("Invalid status value.", nameof(value));

        return new Sample(metric, time, null, null, value);
    }
}

public sealed record InverterFlow(string Key, double PvPower, double? ReportedSiteLoad);
public sealed record InstallationFlow(double? PvPower, double? HouseholdPower);

public static class SolarInstallation
{
    public static InstallationFlow Aggregate(string masterKey, IReadOnlySet<string> expectedKeys, IEnumerable<InverterFlow> reports)
    {
        var available = reports.ToLookup(report => report.Key, StringComparer.Ordinal);
        var master = available[masterKey].SingleOrDefaultIfUnique();
        var pvPower = expectedKeys.Count != 0 && expectedKeys.All(key => available[key].Count() == 1)
            ? (double?)expectedKeys.Sum(key => available[key].Single().PvPower)
            : null;
        return new InstallationFlow(pvPower, master?.ReportedSiteLoad);
    }

    private static InverterFlow? SingleOrDefaultIfUnique(this IEnumerable<InverterFlow> values) =>
        values.Take(2).ToArray() is [var value] ? value : null;
}
