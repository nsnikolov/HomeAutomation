using HomeAutomation.Domain;

namespace HomeAutomation.UnitTests;

/// <summary>Pure rule engine behavior: no database, clock or I/O.</summary>
public sealed class AlertRuleEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 13, 30, 0, TimeSpan.Zero);
    private static readonly SignalRef VoltageA = new(SignalKind.Measurement, DeviceId: 1, EndpointKey: "ac", Metric: Metric.GridVoltage);
    private static readonly SignalRef VoltageB = new(SignalKind.Measurement, DeviceId: 2, EndpointKey: "ac", Metric: Metric.GridVoltage);
    private static readonly SignalRef Soc = new(SignalKind.Measurement, DeviceId: 3, EndpointKey: "battery", Metric: Metric.BatterySoc);
    private static readonly SignalRef Room = new(SignalKind.Measurement, DeviceId: 4, EndpointKey: "main", Metric: Metric.Temperature);
    private static readonly SignalRef Heater = new(SignalKind.Measurement, DeviceId: 5, EndpointKey: "relay", Metric: Metric.SwitchOn);

    private static RuleCondition Below(string id, SignalRef signal, double value, double? recovery = null, int freshness = 900) =>
        new(id, [signal], SignalAggregation.Single, ConditionOperator.LessThan, value, Unit: Telemetry.Unit(signal.Metric!.Value), RecoveryNumber: recovery, FreshnessSeconds: freshness);

    private static RuleCondition AtOrBelow(string id, double value, SignalAggregation aggregation, params SignalRef[] signals) =>
        new(id, signals, aggregation, ConditionOperator.LessThanOrEqual, value, Unit: "V", RecoveryNumber: 180, FreshnessSeconds: 900);

    private static RuleGroup All(params RuleNode[] nodes) => new(LogicalOperator.And, nodes);
    private static RuleGroup Any(params RuleNode[] nodes) => new(LogicalOperator.Or, nodes);
    private static RuleNode Node(RuleCondition condition) => new(condition);
    private static RuleNode Node(RuleGroup group) => new(Group: group);

    private static Func<RuleCondition, SignalRef, SignalReading> Readings(params (SignalRef Signal, double? Number, TimeSpan Age)[] values) =>
        (_, signal) =>
        {
            foreach (var value in values)
                if (value.Signal == signal)
                    return new SignalReading(signal, signal.Metric.ToString()!, Number: value.Number,
                        Flag: signal.Metric == Metric.SwitchOn ? value.Number == 1 : null, ObservedAt: Now - value.Age);
            return SignalReading.Missing(signal, "missing", "No reading has been recorded.");
        };

    private static Truth Evaluate(RuleGroup rule, Func<RuleCondition, SignalRef, SignalReading> read, bool latched = false) =>
        RuleEvaluator.Evaluate(rule, read, Now, latched).Result;

    [Fact]
    public void ASimpleConditionMatchesAndDoesNotMatch()
    {
        var rule = All(Node(AtOrBelow("c1", 10, SignalAggregation.Single, VoltageA)));
        Assert.Equal(Truth.True, Evaluate(rule, Readings((VoltageA, 0, TimeSpan.FromMinutes(1)))));
        Assert.Equal(Truth.False, Evaluate(rule, Readings((VoltageA, 228, TimeSpan.FromMinutes(1)))));
    }

    [Fact]
    public void AndOrAndNestedGroupsUseThreeValuedLogic()
    {
        var grid = Node(AtOrBelow("c1", 10, SignalAggregation.Single, VoltageA));
        var battery = Node(Below("c2", Soc, 50));
        var cold = Node(Below("c3", Room, 10));
        var outage = (Volts: 0d, Soc: 45d, Room: 21d);
        var read = Readings((VoltageA, outage.Volts, TimeSpan.Zero), (Soc, outage.Soc, TimeSpan.Zero), (Room, outage.Room, TimeSpan.Zero));
        Assert.Equal(Truth.True, Evaluate(All(grid, battery), read));
        Assert.Equal(Truth.False, Evaluate(All(grid, cold), read));
        Assert.Equal(Truth.True, Evaluate(Any(cold, battery), read));
        // (A AND B) OR C
        Assert.Equal(Truth.True, Evaluate(Any(Node(All(grid, battery)), cold), read));
        Assert.Equal(Truth.False, Evaluate(Any(Node(All(grid, cold)), Node(Below("c4", Soc, 20))), read));

        var missingSoc = Readings((VoltageA, 0, TimeSpan.Zero), (Room, 21, TimeSpan.Zero));
        Assert.Equal(Truth.Unknown, Evaluate(All(grid, battery), missingSoc));
        Assert.Equal(Truth.False, Evaluate(All(cold, battery), missingSoc));
        Assert.Equal(Truth.True, Evaluate(Any(grid, battery), missingSoc));
        Assert.Equal(Truth.Unknown, Evaluate(Any(cold, battery), missingSoc));
    }

    [Fact]
    public void StaleFutureAndNonNumericReadingsAreUnknownNeverZeroOrFalse()
    {
        // Grid voltage 0 V now, battery SOC 45% from yesterday: never a fresh composite match.
        var composite = All(Node(AtOrBelow("c1", 10, SignalAggregation.Single, VoltageA)), Node(Below("c2", Soc, 50)));
        var evaluation = RuleEvaluator.Evaluate(composite, Readings((VoltageA, 0, TimeSpan.Zero), (Soc, 45, TimeSpan.FromDays(1))), Now, false);
        Assert.Equal(Truth.Unknown, evaluation.Result);
        var stale = evaluation.Conditions.Single(condition => condition.ConditionId == "c2").Signals.Single();
        Assert.Equal((Truth.Unknown, SignalQuality.Stale, 45d), (stale.Result, stale.Quality, stale.Number));
        Assert.Equal(Now, evaluation.NewestObservation);

        Assert.Equal(Truth.Unknown, Evaluate(All(Node(Below("c1", Soc, 50))), Readings((Soc, 45, TimeSpan.FromMinutes(-10)))));
        Assert.Equal(Truth.Unknown, Evaluate(All(Node(Below("c1", Soc, 50))), Readings((Soc, null, TimeSpan.Zero))));
        Assert.Equal(Truth.Unknown, Evaluate(All(Node(Below("c1", Soc, 50))), (_, signal) =>
            SignalReading.Missing(signal, "Battery", "The device moved to another property or was removed.")));
    }

    [Fact]
    public void AggregationSelectsOneAnyOrAllInverters()
    {
        var oneDown = Readings((VoltageA, 0, TimeSpan.Zero), (VoltageB, 230, TimeSpan.Zero));
        Assert.Equal(Truth.False, Evaluate(All(Node(AtOrBelow("c1", 10, SignalAggregation.All, VoltageA, VoltageB))), oneDown));
        Assert.Equal(Truth.True, Evaluate(All(Node(AtOrBelow("c1", 10, SignalAggregation.Any, VoltageA, VoltageB))), oneDown));
        var bothDownOneStale = Readings((VoltageA, 0, TimeSpan.Zero), (VoltageB, 0, TimeSpan.FromHours(3)));
        Assert.Equal(Truth.Unknown, Evaluate(All(Node(AtOrBelow("c1", 10, SignalAggregation.All, VoltageA, VoltageB))), bothDownOneStale));
    }

    [Fact]
    public void CrossDeviceConditionsCombineBatteryAndHeaterRelay()
    {
        var rule = All(Node(Below("c1", Soc, 30)), Node(new RuleCondition("c2", [Heater], SignalAggregation.Single, ConditionOperator.IsTrue)));
        Assert.Equal(Truth.True, Evaluate(rule, Readings((Soc, 25, TimeSpan.Zero), (Heater, 1, TimeSpan.Zero))));
        Assert.Equal(Truth.False, Evaluate(rule, Readings((Soc, 25, TimeSpan.Zero), (Heater, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void HysteresisHoldsALatchedConditionUntilTheRecoveryThreshold()
    {
        var rule = All(Node(Below("c1", Soc, 35, recovery: 40)));
        var at = (double soc) => Readings((Soc, soc, TimeSpan.Zero));
        Assert.Equal(Truth.False, Evaluate(rule, at(37)));
        Assert.Equal(Truth.True, Evaluate(rule, at(37), latched: true));
        Assert.Equal(Truth.True, Evaluate(rule, at(40), latched: true));
        Assert.Equal(Truth.False, Evaluate(rule, at(40.5), latched: true));
    }

    [Fact]
    public void ValidationRejectsUnitsTypesRangesAndExcessiveNesting()
    {
        static IReadOnlyList<string> Errors(RuleCondition condition) => AlertSignals.Validate(new RuleGroup(LogicalOperator.And, [new(condition)]));
        Assert.Empty(Errors(Below("c1", Soc, 35, 40)));
        Assert.Contains(Errors(Below("c1", Soc, 35) with { Unit = "V" }), error => error.Contains("unit"));
        Assert.Contains(Errors(Below("c1", Soc, 135)), error => error.Contains("between 0 and 100"));
        Assert.Contains(Errors(Below("c1", Soc, 35, recovery: 30)), error => error.Contains("recovered side"));
        Assert.Contains(Errors(new RuleCondition("c1", [Heater], SignalAggregation.Single, ConditionOperator.LessThan, 1)),
            error => error.Contains("not available"));
        Assert.Contains(Errors(new RuleCondition("c1", [VoltageA, Soc], SignalAggregation.Any, ConditionOperator.LessThan, 10, Unit: "V")),
            error => error.Contains("one metric"));
        Assert.Contains(Errors(AtOrBelow("c1", 10, SignalAggregation.Single, VoltageA, VoltageB)), error => error.Contains("Any or All"));
        Assert.Contains(Errors(new RuleCondition("c1", [new SignalRef(SignalKind.Connectivity, DeviceId: 1)], SignalAggregation.Single,
            ConditionOperator.Equals, Text: "Sleeping")), error => error.Contains("Online, Stale, Offline or Unknown"));
        Assert.Contains(Errors(new RuleCondition("c1", [new SignalRef(SignalKind.DeviceEvent, DeviceId: 1, EventCode: "DEVICE_OFFLINE")],
            SignalAggregation.Single, ConditionOperator.EventOccurred)), error => error.Contains("look-back window"));

        var deep = new RuleGroup(LogicalOperator.And, [new(Group: new RuleGroup(LogicalOperator.Or, [new(Group: new RuleGroup(LogicalOperator.And,
            [new(Group: new RuleGroup(LogicalOperator.And, [new(Below("c1", Soc, 10))]))]))]))]);
        Assert.Contains(AlertSignals.Validate(deep), error => error.Contains("nested"));
        var crowded = new RuleGroup(LogicalOperator.Or, Enumerable.Range(0, 2).Select(group => new RuleNode(Group: new RuleGroup(LogicalOperator.Or,
            Enumerable.Range(0, 7).Select(index => new RuleNode(Below($"c{group}{index}", Soc, 10))).ToArray()))).ToArray());
        Assert.Contains(AlertSignals.Validate(crowded), error => error.Contains("at most 12 conditions"));
        Assert.Contains(AlertSignals.Validate(new RuleGroup(LogicalOperator.And, [new(Below("c1", Soc, 10)), new(Below("c1", Soc, 20))])),
            error => error.Contains("unique"));
        Assert.Equal([ConditionOperator.EventOccurred], AlertSignals.Operators(SignalValueType.Event, SignalKind.DeviceEvent));
        Assert.DoesNotContain(ConditionOperator.LessThan, AlertSignals.Operators(SignalValueType.Status, SignalKind.Measurement));
    }

    [Fact]
    public void ConfirmationNeedsAContinuousTrueAndUnknownCancelsActivationButNeverRecovers()
    {
        var activation = TimeSpan.FromSeconds(15);
        var recovery = TimeSpan.FromSeconds(60);
        var (pending, none) = RuleStateMachine.Step(RuleRuntime.Initial, Truth.True, Now, Now, activation, recovery);
        Assert.Equal((RulePhase.Pending, RuleTransition.None), (pending.Phase, none));
        Assert.Equal(RulePhase.Inactive, RuleStateMachine.Step(pending, Truth.Unknown, Now, Now.AddSeconds(10), activation, recovery).State.Phase);
        var (active, activated) = RuleStateMachine.Step(pending, Truth.True, Now, Now.AddSeconds(16), activation, recovery);
        Assert.Equal((RulePhase.Active, RuleTransition.Activated, Now.AddSeconds(16)), (active.Phase, activated, active.ActiveSince));

        var (recovering, _) = RuleStateMachine.Step(active, Truth.False, Now, Now.AddSeconds(30), activation, recovery);
        Assert.Equal(RulePhase.Recovering, recovering.Phase);
        var (stillRecovering, held) = RuleStateMachine.Step(recovering, Truth.Unknown, Now, Now.AddHours(2), activation, recovery);
        Assert.Equal((RulePhase.Recovering, RuleTransition.None), (stillRecovering.Phase, held));
        Assert.Equal(RulePhase.Active, RuleStateMachine.Step(recovering, Truth.True, Now, Now.AddSeconds(40), activation, recovery).State.Phase);
        Assert.Equal(RulePhase.Active, RuleStateMachine.Step(active, Truth.Unknown, Now, Now.AddHours(5), activation, recovery).State.Phase);
        var (cleared, transition) = RuleStateMachine.Step(recovering, Truth.False, Now, Now.AddSeconds(91), activation, recovery);
        Assert.Equal((RulePhase.Inactive, RuleTransition.Cleared), (cleared.Phase, transition));
        var (immediate, step) = RuleStateMachine.Step(RuleRuntime.Initial, Truth.True, Now, Now, TimeSpan.Zero, recovery);
        Assert.Equal((RulePhase.Active, RuleTransition.Activated), (immediate.Phase, step));
    }

    [Fact]
    public void TheHighestActiveSeverityDrivesTheIncident()
    {
        Assert.Equal((IncidentChange.Open, AlertSeverity.Warning), IncidentPlanner.Plan(null, [AlertSeverity.Warning]));
        Assert.Equal((IncidentChange.Open, AlertSeverity.Critical), IncidentPlanner.Plan(null, [AlertSeverity.Warning, AlertSeverity.Critical]));
        Assert.Equal((IncidentChange.Escalate, AlertSeverity.Critical),
            IncidentPlanner.Plan(AlertSeverity.Warning, [AlertSeverity.Warning, AlertSeverity.Critical]));
        Assert.Equal((IncidentChange.Deescalate, AlertSeverity.Warning), IncidentPlanner.Plan(AlertSeverity.Critical, [AlertSeverity.Warning]));
        Assert.Equal((IncidentChange.None, AlertSeverity.Warning), IncidentPlanner.Plan(AlertSeverity.Warning, [AlertSeverity.Warning]));
        Assert.Equal(IncidentChange.Resolve, IncidentPlanner.Plan(AlertSeverity.Warning, []).Change);
        Assert.Equal(IncidentChange.None, IncidentPlanner.Plan(null, []).Change);
        Assert.Equal("rule:42", IncidentPlanner.CorrelationKey(42, null));
        Assert.Equal("grid.outage", IncidentPlanner.CorrelationKey(42, " Grid.Outage "));
        Assert.True(IncidentPlanner.ValidCorrelationKey("grid.outage:north"));
        Assert.False(IncidentPlanner.ValidCorrelationKey("rule:42"));
        Assert.False(IncidentPlanner.ValidCorrelationKey("Grid outage"));
    }

    [Fact]
    public void PreferencesRouteBySeverityAndEventButCriticalAlwaysStaysInApp()
    {
        var quiet = new NotificationPreferences(new(false, false), new(true, false), new(false, false), new(Activated: false, Reminder: false));
        Assert.Equal((true, ""), Route(quiet, AlertSeverity.Critical, NotificationKind.Activated));
        Assert.Equal((false, ""), Route(quiet, AlertSeverity.Warning, NotificationKind.Activated));
        Assert.Equal((true, ""), Route(quiet, AlertSeverity.Warning, NotificationKind.Escalated));
        Assert.Equal((false, ""), Route(quiet, AlertSeverity.Info, NotificationKind.Resolved));
        Assert.Equal((true, "browser"), Route(NotificationPreferences.Default, AlertSeverity.Critical, NotificationKind.Escalated));
        Assert.Equal((true, ""), Route(NotificationPreferences.Default, AlertSeverity.Info, NotificationKind.Resolved));
        Assert.True(quiet.Normalize().Critical.InApp);

        static (bool, string) Route(NotificationPreferences preferences, AlertSeverity severity, NotificationKind kind)
        {
            var (inApp, channels) = preferences.Route(severity, kind);
            return (inApp, string.Join(",", channels));
        }
    }

    [Fact]
    public void SignalKeysAreStableAndUseTheJsonSpellingOfMetrics()
    {
        // The browser derives keys from stored SignalRefs, whose metrics arrive as camelCase JSON strings.
        Assert.Equal("m:d3:battery:batterySoc", AlertSignals.Key(Soc));
        Assert.Equal("m:d1:ac:gridVoltage", AlertSignals.Key(VoltageA));
        Assert.Equal("m:i7:batterySoc", AlertSignals.Key(new SignalRef(SignalKind.Measurement, InstallationId: 7, Metric: Metric.BatterySoc)));
        Assert.Equal("m:d5:main:pv1Voltage", AlertSignals.Key(new SignalRef(SignalKind.Measurement, DeviceId: 5, EndpointKey: "main", Metric: Metric.Pv1Voltage)));
        Assert.Equal("c:d4", AlertSignals.Key(new SignalRef(SignalKind.Connectivity, DeviceId: 4)));
        Assert.Equal("e:d4:DEVICE_OFFLINE", AlertSignals.Key(new SignalRef(SignalKind.DeviceEvent, DeviceId: 4, EventCode: "DEVICE_OFFLINE")));
        // Every metric's key segment is its wire name, so no metric can drift between the catalog and the editor.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        foreach (var metric in Enum.GetValues<Metric>())
            Assert.Equal($"\"{AlertSignals.MetricName(metric)}\"", System.Text.Json.JsonSerializer.Serialize(metric, options));
        Assert.Equal(AlertSignals.Key(Soc), AlertSignals.Key(Soc with { }));
        Assert.NotEqual(AlertSignals.Key(VoltageA), AlertSignals.Key(VoltageB));
    }

    [Fact]
    public void EqualDeviceNamesGainTheFirstAttributeThatTellsThemApart()
    {
        var names = SignalOwnerNames.Distinguish([
            new(10, "Heatpump", "Altherma", "Daikin", "Boiler room"), new(11, "heatpump", "PSF-X67", "Sonoff", "Boiler room"),
            new(12, "Growatt Master", "SPF 5000 ES", "Growatt"),
        ]);
        Assert.Equal(["Heatpump (Altherma)", "heatpump (PSF-X67)", "Growatt Master"], [names[10], names[11], names[12]]);

        // Same model: the manufacturer, then the room, then the device number.
        names = SignalOwnerNames.Distinguish([new(1, "Relay", "PSF-X67", "Sonoff", "Hall"), new(2, "Relay", "PSF-X67", "Sonoff", "Garage")]);
        Assert.Equal(["Relay (Hall)", "Relay (Garage)"], [names[1], names[2]]);
        names = SignalOwnerNames.Distinguish([new(1, "Relay", "PSF-X67", "Sonoff"), new(2, "Relay", "PSF-X67", "Sonoff")]);
        Assert.Equal(["Relay (PSF-X67, device 1)", "Relay (PSF-X67, device 2)"], [names[1], names[2]]);

        // A qualified name that equals another device's own name still ends unique.
        names = SignalOwnerNames.Distinguish([new(1, "Pump", "A"), new(2, "Pump", "B"), new(3, "Pump (A)", "C")]);
        Assert.Equal(3, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Pump (B)", names[2]);
    }
}
