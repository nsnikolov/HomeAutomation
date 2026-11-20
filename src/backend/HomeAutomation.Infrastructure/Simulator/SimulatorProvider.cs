using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Simulator;

public sealed class SimulatorProvider(TimeProvider timeProvider, SimulatorOptions options) : IObservationProvider
{
    private const string Master = "growatt-master";
    private const string Slave = "growatt-slave";
    private static readonly HashSet<string> Inverters = [Master, Slave];
    private static readonly IReadOnlyList<DiscoveredDevice> Devices = Array.AsReadOnly(new[]
    {
        Device(Master, "Inverter · Master", DeviceKind.Inverter, "plant", "Growatt", "SPF 5000 ES", "master",
            Capability.SolarProduction, Capability.HouseholdConsumption, Capability.GridAvailability, Capability.FaultStatus),
        Device(Slave, "Inverter · Slave", DeviceKind.Inverter, "plant", "Growatt", "SPF 5000 ES", "slave",
            Capability.SolarProduction, Capability.HouseholdConsumption, Capability.GridAvailability, Capability.FaultStatus),
        Device("battery", "Shared battery", DeviceKind.Battery, "plant", "Simulated", "51.2 V · 200 Ah LiFePO4", "shared-battery",
            Capability.BatteryLevel, Capability.BatteryPower, Capability.VoltageMeasurement, Capability.FaultStatus),
        Device("daikin", "Living room AC", DeviceKind.AirConditioner, "living", "Daikin", "ONECTA simulator", null,
            Capability.Switch, Capability.TemperatureMeasurement, Capability.HeatingSetpoint, Capability.OperatingMode, Capability.FaultStatus),
        Device("sonoff", "Water heater meter", DeviceKind.SmartSwitch, "utility", "Sonoff", "Metering switch simulator", null,
            Capability.Switch, Capability.PowerMeasurement, Capability.VoltageMeasurement,
            Capability.CurrentMeasurement, Capability.EnergyMeasurement, Capability.FaultStatus),
        Device("temperature", "Room sensor", DeviceKind.Sensor, "living", "Simulated", "Temperature and humidity", null,
            Capability.TemperatureMeasurement, Capability.HumidityMeasurement)
    });

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Devices);
    }

    public Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = timeProvider.GetUtcNow();
        var at = DateTimeOffset.FromUnixTimeSeconds(clock.ToUnixTimeSeconds());
        var quarter = at.TimeOfDay.TotalHours;
        var day = Math.Max(0, Math.Sin(Math.PI * (quarter - 6) / 12)) * (quarter is >= 6 and <= 18 ? 1 : 0);
        var wave = Math.Sin(at.ToUnixTimeSeconds() / 105d);
        var pvMaster = Math.Round(day * (2900 + wave * 180));
        var pvSlave = Math.Round(day * (2350 - wave * 120));
        var switchOn = at.Minute % 30 < 12;
        var heater = switchOn ? 1780d + Math.Round(wave * 15) : 0d;
        var load = Math.Round(740 + wave * 90 + heater);
        var grid = options.Scenario != Scenario.Outage;
        var soc = Math.Round(Math.Clamp(62 + day * 25 + wave * 2, 0, 100), 1);
        var indoor = Math.Round(19.7 + wave * 0.5, 1);
        var phase = (at.ToUnixTimeSeconds() / 120) % 5;
        var offline = options.Scenario == Scenario.Offline || options.Scenario == Scenario.Demonstration && phase == 2;
        var fault = options.Scenario == Scenario.Fault || options.Scenario == Scenario.Demonstration && phase == 3;
        var stale = options.Scenario == Scenario.Stale || options.Scenario == Scenario.Demonstration && phase == 4;
        var sensorTime = stale ? at.AddSeconds(-options.FreshnessSeconds - 30) : at;
        var energy = Math.Round((at - DateTimeOffset.UnixEpoch).TotalDays * 2.4, 4);
        var totals = SolarInstallation.Aggregate(Master, Inverters,
        [
            new InverterFlow(Master, pvMaster, load),
            new InverterFlow(Slave, pvSlave, load)
        ]);

        var installation = new List<Sample> { Sample.Boolean(Metric.GridAvailable, at, grid) };
        if (totals.PvPower is { } pv) installation.Add(Sample.Numeric(Metric.PvPower, at, pv));
        if (totals.HouseholdPower is { } totalLoad) installation.Add(Sample.Numeric(Metric.HouseholdPower, at, totalLoad));

        IReadOnlyList<DeviceObservation> observations =
        [
            Observe(Master, Sample.Numeric(Metric.PvPower, at, pvMaster), Sample.Numeric(Metric.HouseholdPower, at, load),
                Sample.Boolean(Metric.GridAvailable, at, grid)),
            Observe(Slave, Sample.Numeric(Metric.PvPower, at, pvSlave), Sample.Numeric(Metric.HouseholdPower, at, load),
                Sample.Boolean(Metric.GridAvailable, at, grid)),
            Observe("battery", Sample.Numeric(Metric.BatterySoc, at, soc),
                Sample.Numeric(Metric.BatteryPower, at, Math.Round((pvMaster + pvSlave - load) * (grid ? .65 : 1))),
                Sample.Numeric(Metric.Voltage, at, Math.Round(50.8 + soc * .035, 2))),
            offline ? new DeviceObservation("daikin", Connectivity.Offline, null, []) :
                Observe("daikin", Sample.Boolean(Metric.SwitchOn, at, true), Sample.Numeric(Metric.Temperature, at, indoor),
                    Sample.Numeric(Metric.TargetTemperature, at, 20), Sample.Status(Metric.OperatingMode, at, "Heating")),
            fault ? new DeviceObservation("sonoff", Connectivity.Online, "SIM_METER_FAULT",
                [new EndpointObservation("main", [])]) :
                Observe("sonoff", Sample.Boolean(Metric.SwitchOn, at, switchOn), Sample.Numeric(Metric.ActivePower, at, heater),
                    Sample.Numeric(Metric.Current, at, Math.Round(heater / 231, 2)), Sample.Numeric(Metric.Voltage, at, 231),
                    Sample.Numeric(Metric.Energy, at, energy)),
            Observe("temperature", Sample.Numeric(Metric.Temperature, sensorTime, indoor),
                Sample.Numeric(Metric.Humidity, sensorTime, Math.Round(49 + wave * 4, 1)))
        ];
        return Task.FromResult(new ObservationBatch(at, observations, installation));
    }

    private static DeviceObservation Observe(string id, params Sample[] samples) =>
        new(id, Connectivity.Online, null, [new EndpointObservation("main", samples)]);

    private static DiscoveredDevice Device(string id, string name, DeviceKind kind, string room, string manufacturer,
        string model, string? role, params Capability[] capabilities) =>
        new(id, name, kind, room, manufacturer, model, [new DeviceEndpoint("main", "Main", capabilities)], role);
}
