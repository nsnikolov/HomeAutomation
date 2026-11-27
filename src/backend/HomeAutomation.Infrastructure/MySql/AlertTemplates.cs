using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.MySql;

/// <summary>
/// Predefined rule drafts built from the signals a property actually reports. Nothing is saved or enabled until a user reviews a
/// draft in the editor and saves it, or confirms the explicit grid-outage setup.
/// </summary>
internal static class AlertTemplates
{
    public const string GridOutageFamily = "grid.outage";
    public const string BatteryFamily = "battery.low";

    public static AlertTemplateList Build(IReadOnlyList<SignalCatalogEntry> catalog)
    {
        var voltage = Measurements(catalog, Metric.GridVoltage);
        var battery = Measurements(catalog, Metric.BatterySoc);
        var temperature = Measurements(catalog, Metric.Temperature);
        var connectivity = catalog.Where(entry => entry.Signal.Kind == SignalKind.Connectivity).ToArray();
        const string noVoltage = "No device in this property reports AC input voltage.";
        const string noBattery = "No device in this property reports battery SOC.";

        var gridLost = voltage.Length == 0 ? null : Draft("Grid power lost", "AC input voltage is at or below 10 V.", AlertSeverity.Warning,
            GridOutageFamily, "Grid power unavailable", Group(LogicalOperator.And, VoltageCondition(voltage)), 15, 60, "grid-power-lost");
        var gridLowBattery = voltage.Length == 0 || battery.Length == 0 ? null : Draft("Grid outage with low battery",
            "AC input voltage is at or below 10 V and battery SOC is below 50%.", AlertSeverity.Critical, GridOutageFamily, "Grid power unavailable",
            Group(LogicalOperator.And, VoltageCondition(voltage), BatteryCondition(battery, "c2", 50, 55)), 60, 120, "grid-outage-low-battery");
        var templates = new List<AlertTemplate>
        {
            Template("grid-power-lost", "Grid power lost", "Warning when AC input voltage is at or below 10 V.", gridLost, noVoltage),
            Template("grid-outage-low-battery", "Grid outage with low battery",
                "Critical when the grid is lost and battery SOC is below 50%. Escalates the same grid outage incident.", gridLowBattery,
                voltage.Length == 0 ? noVoltage : noBattery),
            Template("battery-low", "Battery low", "Warning when battery SOC is below 35%, recovering above 40%.",
                battery.Length == 0 ? null : Draft("Battery low", "Battery SOC is below 35%.", AlertSeverity.Warning, BatteryFamily, "Battery low",
                    Group(LogicalOperator.And, BatteryCondition(battery, "c1", 35, 40)), 120, 300, "battery-low"), noBattery),
            Template("battery-critical", "Battery critically low", "Critical when battery SOC is below 20%. Escalates the battery incident.",
                battery.Length == 0 ? null : Draft("Battery critically low", "Battery SOC is below 20%.", AlertSeverity.Critical, BatteryFamily,
                    "Battery low", Group(LogicalOperator.And, BatteryCondition(battery, "c1", 20, 25)), 60, 300, "battery-critical"), noBattery),
            Template("high-temperature", "High room temperature", "Warning when a temperature stays above the threshold you choose.",
                temperature.Length == 0 ? null : Draft("High room temperature", "Temperature is above 30 °C for 10 minutes.", AlertSeverity.Warning,
                    null, null, Group(LogicalOperator.And, Threshold(temperature[0], "c1", ConditionOperator.GreaterThan, 30, 28)), 600, 300,
                    "high-temperature"), "No device in this property reports temperature."),
            Template("low-temperature", "Low room temperature", "Warning when a temperature stays below the threshold you choose.",
                temperature.Length == 0 ? null : Draft("Low room temperature", "Temperature is below 10 °C for 10 minutes.", AlertSeverity.Warning,
                    null, null, Group(LogicalOperator.And, Threshold(temperature[0], "c1", ConditionOperator.LessThan, 10, 12)), 600, 300,
                    "low-temperature"), "No device in this property reports temperature."),
            Template("device-offline", "Device offline", "Warning when a device reports Offline for 5 minutes.",
                connectivity.Length == 0 ? null : Draft("Device offline", "The device reports Offline for 5 minutes.", AlertSeverity.Warning, null, null,
                    Group(LogicalOperator.And, new RuleCondition("c1", [connectivity[0].Signal], SignalAggregation.Single, ConditionOperator.Equals,
                        Text: nameof(Connectivity.Offline), FreshnessSeconds: connectivity[0].SuggestedFreshnessSeconds)), 300, 60, "device-offline"),
                "No device in this property reports connectivity."),
        };
        return new AlertTemplateList(templates, gridLost is null || gridLowBattery is null ? null : [gridLost, gridLowBattery]);
    }

    private static SignalCatalogEntry[] Measurements(IReadOnlyList<SignalCatalogEntry> catalog, Metric metric) =>
        catalog.Where(entry => entry.Signal.Kind == SignalKind.Measurement && entry.Signal.Metric == metric).ToArray();

    /// <summary>
    /// Several inverters: the grid counts as lost only when every selected inverter sees it (All), so one tripped input breaker does not
    /// look like an outage. Users can switch to Any or a single authoritative inverter.
    /// </summary>
    private static RuleCondition VoltageCondition(SignalCatalogEntry[] voltage) => new("c1", voltage.Select(entry => entry.Signal).ToArray(),
        voltage.Length == 1 ? SignalAggregation.Single : SignalAggregation.All, ConditionOperator.LessThanOrEqual, GridSupply.LostAtOrBelowVolts, Unit: "V",
        RecoveryNumber: GridSupply.PresentAboveVolts, FreshnessSeconds: voltage.Max(entry => entry.SuggestedFreshnessSeconds));

    /// <summary>A shared battery is one physical source: the first reporting device is proposed as the authoritative one.</summary>
    private static RuleCondition BatteryCondition(SignalCatalogEntry[] battery, string id, double below, double recover) =>
        Threshold(battery[0], id, ConditionOperator.LessThan, below, recover);

    private static RuleCondition Threshold(SignalCatalogEntry entry, string id, ConditionOperator op, double value, double recovery) =>
        new(id, [entry.Signal], SignalAggregation.Single, op, value, Unit: entry.Unit, RecoveryNumber: recovery,
            FreshnessSeconds: entry.SuggestedFreshnessSeconds);

    private static RuleGroup Group(LogicalOperator op, params RuleCondition[] conditions) =>
        new(op, conditions.Select(condition => new RuleNode(condition)).ToArray());

    private static AlertRuleInput Draft(string name, string description, AlertSeverity severity, string? family, string? title,
        RuleGroup definition, int activation, int recovery, string code) =>
        new(name, description, true, severity, family, title, definition, activation, recovery, new NotificationPolicy(), code);

    private static AlertTemplate Template(string code, string name, string description, AlertRuleInput? draft, string unavailable) =>
        new(code, name, description, draft is not null, draft is null ? unavailable : null, draft);
}
