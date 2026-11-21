namespace HomeAutomation.Domain;

public enum DeviceKind { Inverter, Battery, AirConditioner, SmartSwitch, Sensor }
public enum Capability
{
    SolarProduction, HouseholdConsumption, GridAvailability, BatteryLevel, BatteryPower,
    VoltageMeasurement, CurrentMeasurement, PowerMeasurement, EnergyMeasurement,
    Switch, TemperatureMeasurement, HumidityMeasurement, HeatingSetpoint, OperatingMode, FaultStatus,
    FrequencyMeasurement, WarningStatus, FanSpeed
}
public enum Access { ReadOnly, Unsupported }
public enum Connectivity { Online, Offline, Unknown }
public enum ReadingQuality { Fresh, Stale, Missing }

public sealed record DeviceEndpoint
{
    public DeviceEndpoint(string key, string name, IEnumerable<Capability> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Key = key;
        Name = name;
        Capabilities = Array.AsReadOnly(capabilities.ToArray());
        if (key.Length > 64 || name.Length > 128 ||
            Capabilities.Any(capability => !Enum.IsDefined(capability)) ||
            Capabilities.Distinct().Count() != Capabilities.Count)
        {
            throw new ArgumentException("Invalid endpoint or duplicate capabilities.");
        }
    }

    public string Key { get; }
    public string Name { get; }
    public IReadOnlyList<Capability> Capabilities { get; }
    public Access AccessFor(Capability capability) =>
        Capabilities.Contains(capability) ? Access.ReadOnly : Access.Unsupported;
}
