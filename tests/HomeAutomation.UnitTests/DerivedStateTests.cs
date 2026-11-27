using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Tuya;

namespace HomeAutomation.UnitTests;

/// <summary>Availability, PV generation and installation summary rules (prompt 0068): pure, no database or clock.</summary>
public sealed class DerivedStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveMinutePoll = DeviceAvailability.Window(TimeSpan.FromSeconds(300));

    private static AvailabilityResult Evaluate(Connectivity? reported, double? reportedMinutesAgo, double? readingMinutesAgo, TimeSpan? window = null) =>
        DeviceAvailability.Evaluate(reported, reportedMinutesAgo is { } status ? Now.AddMinutes(-status) : null,
            readingMinutesAgo is { } reading ? Now.AddMinutes(-reading) : null, Now, window ?? FiveMinutePoll);

    [Fact]
    public void AvailabilityUsesOneAndAHalfPollingIntervalsOfSourceTimedEvidence()
    {
        Assert.Equal(TimeSpan.FromSeconds(450), FiveMinutePoll);
        Assert.Equal(TimeSpan.FromSeconds(180), DeviceAvailability.Window(TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(900), DeviceAvailability.Window(TimeSpan.FromSeconds(600)));

        Assert.Equal(Availability.Online, Evaluate(Connectivity.Online, 4, 4).State);
        Assert.Equal(Availability.Online, Evaluate(Connectivity.Online, 6, 6).State);
        Assert.Equal(Availability.Online, Evaluate(Connectivity.Online, 7.5, 7.5).State);
        var late = Evaluate(Connectivity.Online, 8, 8);
        Assert.Equal((Availability.Stale, AvailabilityBasis.NoRecentEvidence, (DateTimeOffset?)Now.AddMinutes(-8)), (late.State, late.Basis, late.EvidenceAt));
        // Other configured intervals: 2 minutes gives 3, 10 minutes gives 15.
        Assert.Equal(Availability.Stale, Evaluate(Connectivity.Online, 4, 4, DeviceAvailability.Window(TimeSpan.FromMinutes(2))).State);
        Assert.Equal(Availability.Online, Evaluate(Connectivity.Online, 14, 14, DeviceAvailability.Window(TimeSpan.FromMinutes(10))).State);
    }

    [Fact]
    public void ReadingsOutweighAProviderOfflineFlagSentWithThemButNotALaterDisconnection()
    {
        // Growatt sends `lost` in the same record as the readings: fresh readings with an Offline flag mean the inverter communicates.
        var growatt = Evaluate(Connectivity.Offline, 2, 2);
        Assert.Equal((Availability.Online, AvailabilityBasis.RecentReading), (growatt.State, growatt.Basis));
        Assert.Equal(Connectivity.Online, DeviceAvailability.ToConnectivity(growatt.State));
        // A disconnection reported after the newest reading is confirmed.
        var confirmed = Evaluate(Connectivity.Offline, 1, 3);
        Assert.Equal((Availability.Offline, AvailabilityBasis.ProviderOffline), (confirmed.State, confirmed.Basis));
        // An old Offline report with nothing newer is stale information, not a confirmed disconnection.
        Assert.Equal(Availability.Stale, Evaluate(Connectivity.Offline, 30, 40).State);
        Assert.Equal(Connectivity.Unknown, DeviceAvailability.ToConnectivity(Availability.Stale));
        // A recent provider Online report without readings (a relay that has not changed).
        Assert.Equal((Availability.Online, AvailabilityBasis.ProviderOnline), (Evaluate(Connectivity.Online, 1, null).State, Evaluate(Connectivity.Online, 1, null).Basis));
        // No evidence, an Unknown report alone, and an old reading fetched again.
        Assert.Equal((Availability.Unknown, AvailabilityBasis.NoEvidence), (Evaluate(null, null, null).State, Evaluate(null, null, null).Basis));
        Assert.Equal(Availability.Unknown, Evaluate(Connectivity.Unknown, 1, null).State);
        Assert.Equal(Availability.Stale, Evaluate(Connectivity.Online, 60, 60).State);
        Assert.Equal(Availability.Online, Evaluate(Connectivity.Unknown, 1, 1).State);
    }

    [Fact]
    public void FutureTimestampsBeyondTheClockSkewToleranceAreNotEvidence()
    {
        Assert.Equal(Availability.Online, Evaluate(Connectivity.Online, -1, -1).State);
        Assert.Equal(Availability.Unknown, Evaluate(Connectivity.Online, -10, -10).State);
        Assert.Equal(Availability.Stale, Evaluate(Connectivity.Online, -10, 20).State);
    }

    [Fact]
    public void OnlyGrowattAvailabilityFollowsItsPollingIntervalAndReadingFreshnessIsUnchanged()
    {
        var holds = new TelemetryHoldPolicy(new GrowattOptions { PollSeconds = 600, FreshnessSeconds = 1800 }, new DaikinOptions(),
            new EwelinkOptions(), new TuyaOptions(), new SimulatorOptions());
        Assert.Equal(TimeSpan.FromSeconds(900), holds.AvailabilityWindow("growatt"));
        Assert.Equal(TimeSpan.FromSeconds(1800), holds.Hold("growatt", Metric.PvPower));
        Assert.Equal(TimeSpan.FromSeconds(450), TelemetryHoldPolicy.Defaults().AvailabilityWindow("growatt"));
        foreach (var provider in new[] { "daikin", "ewelink", "tuya", "simulator", "other" })
            Assert.Equal(holds.Hold(provider, Metric.SwitchOn), holds.AvailabilityWindow(provider));
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1d, 0d)]
    [InlineData(5d, 0d)]
    [InlineData(10d, 0d)]
    [InlineData(11d, 11d)]
    [InlineData(15d, 15d)]
    [InlineData(20d, 20d)]
    [InlineData(21d, 21d)]
    [InlineData(100d, 100d)]
    [InlineData(null, null)]
    [InlineData(-5d, null)]
    [InlineData(double.NaN, null)]
    public void PvPowerDisplaysZeroUpToTenWattsAndNeverShowsInvalidValuesAsProduction(double? raw, double? shown) =>
        Assert.Equal(shown, PvGeneration.Display(raw));

    [Theory]
    [InlineData(new[] { 0d }, PvState.NotGenerating, PvReason.BelowThreshold)]
    [InlineData(new[] { 1d }, PvState.NotGenerating, PvReason.BelowThreshold)]
    [InlineData(new[] { 5d }, PvState.NotGenerating, PvReason.BelowThreshold)]
    [InlineData(new[] { 10d }, PvState.NotGenerating, PvReason.BelowThreshold)]
    [InlineData(new[] { 11d }, PvState.NotGenerating, PvReason.NoPriorState)]
    [InlineData(new[] { 15d }, PvState.NotGenerating, PvReason.NoPriorState)]
    [InlineData(new[] { 20d }, PvState.NotGenerating, PvReason.NoPriorState)]
    [InlineData(new[] { 21d }, PvState.Generating, PvReason.AboveThreshold)]
    [InlineData(new[] { 100d }, PvState.Generating, PvReason.AboveThreshold)]
    // Through the hysteresis band: rising holds off until above 20 W, falling holds on until 10 W or below.
    [InlineData(new[] { 5d, 15d, 20d }, PvState.NotGenerating, PvReason.HeldBetweenThresholds)]
    [InlineData(new[] { 5d, 15d, 21d }, PvState.Generating, PvReason.AboveThreshold)]
    [InlineData(new[] { 30d, 15d, 11d }, PvState.Generating, PvReason.HeldBetweenThresholds)]
    [InlineData(new[] { 30d, 15d, 10d }, PvState.NotGenerating, PvReason.BelowThreshold)]
    [InlineData(new[] { 30d, 10d, 15d }, PvState.NotGenerating, PvReason.HeldBetweenThresholds)]
    [InlineData(new[] { 30d, -4d, 15d }, PvState.Generating, PvReason.HeldBetweenThresholds)]
    public void PvGenerationStartsAboveTwentyWattsAndEndsAtTenWatts(double[] watts, PvState state, PvReason reason)
    {
        var result = PvGeneration.Evaluate(watts.Select(value => (double?)value).ToArray(), ReadingQuality.Fresh, 120, ReadingQuality.Fresh);
        Assert.Equal((state, reason), (result.State, result.Reason));
        Assert.Equal(watts[^1], result.RawWatts);
        Assert.Equal(PvGeneration.Display(watts[^1]), result.DisplayWatts);
    }

    [Fact]
    public void PvGenerationIsNeverReportedAtZeroVoltsOrFromStaleMissingOrInvalidPower()
    {
        var night = PvGeneration.Evaluate([1], ReadingQuality.Fresh, 0, ReadingQuality.Fresh);
        Assert.Equal((PvState.NotGenerating, PvReason.NoVoltage, (double?)1, (double?)0), (night.State, night.Reason, night.RawWatts, night.DisplayWatts));
        Assert.Equal(PvState.NotGenerating, PvGeneration.Evaluate([40], ReadingQuality.Fresh, 0, ReadingQuality.Fresh).State);
        // A stale 0 V does not veto fresh power; no other voltage threshold is assumed.
        Assert.Equal(PvState.Generating, PvGeneration.Evaluate([40], ReadingQuality.Fresh, 0, ReadingQuality.Stale).State);
        Assert.Equal(PvState.Generating, PvGeneration.Evaluate([40], ReadingQuality.Fresh, 3, ReadingQuality.Fresh).State);
        Assert.Equal((PvState.Unknown, PvReason.Stale), Pick(PvGeneration.Evaluate([500], ReadingQuality.Stale, 120, ReadingQuality.Stale)));
        Assert.Equal((PvState.Unknown, PvReason.NoReading), Pick(PvGeneration.Evaluate([], ReadingQuality.Missing, null, ReadingQuality.Missing)));
        Assert.Equal((PvState.Unknown, PvReason.Invalid), Pick(PvGeneration.Evaluate([null], ReadingQuality.Fresh, 120, ReadingQuality.Fresh)));
        Assert.Equal((PvState.Unknown, PvReason.Invalid), Pick(PvGeneration.Evaluate([-3], ReadingQuality.Fresh, 120, ReadingQuality.Fresh)));
        // Master and Slave are evaluated separately.
        Assert.NotEqual(PvGeneration.Evaluate([300], ReadingQuality.Fresh, 120, ReadingQuality.Fresh).State,
            PvGeneration.Evaluate([1], ReadingQuality.Fresh, 0, ReadingQuality.Fresh).State);

        static (PvState, PvReason) Pick(PvGenerationResult result) => (result.State, result.Reason);
    }

    private static SourceReading Fresh(string source, double? number, double minutesAgo = 1) =>
        new(source, number, Now.AddMinutes(-minutesAgo), ReadingQuality.Fresh);
    private static SourceReading Stale(string source, double number) => new(source, number, Now.AddMinutes(-40), ReadingQuality.Stale);
    private static SourceReading Missing(string source) => new(source, null, null, ReadingQuality.Missing);
    private static readonly TimeSpan Spread = TimeSpan.FromSeconds(300);

    [Fact]
    public void InverterPowerSumsOnlyCompatibleReadingsAndSaysWhenItIsPartialStaleOrUnavailable()
    {
        var pv = InstallationSummary.Sum(SummarySlot.PvPower, "W", [Fresh("Master", 1200), Fresh("Slave", 950)], Spread, PvGeneration.Display);
        Assert.Equal((SummaryState.Available, (double?)2150, 2, 2, (string?)null), (pv.State, pv.Number, pv.Used, pv.Expected, pv.Reason));
        // The 10 W display rule applies once, per inverter: two 8 W readings show 0 W, as the cards do.
        Assert.Equal(0d, InstallationSummary.Sum(SummarySlot.PvPower, "W", [Fresh("Master", 8), Fresh("Slave", 8)], Spread, PvGeneration.Display).Number);
        // A valid zero is a value, not Unavailable.
        var zero = InstallationSummary.Sum(SummarySlot.AcInput, "W", [Fresh("Master", 0), Fresh("Slave", 0)], Spread);
        Assert.Equal((SummaryState.Available, (double?)0), (zero.State, zero.Number));

        var partial = InstallationSummary.Sum(SummarySlot.Consumption, "W", [Fresh("Master", 300), Stale("Slave", 250)], Spread);
        Assert.Equal((SummaryState.Partial, (double?)300, 1, 2), (partial.State, partial.Number, partial.Used, partial.Expected));
        Assert.Contains("Slave", partial.Reason);
        var apart = InstallationSummary.Sum(SummarySlot.Consumption, "W", [Fresh("Master", 300, 1), Fresh("Slave", 250, 9)], Spread);
        Assert.Equal(SummaryState.Partial, apart.State);
        Assert.Contains("8 min apart", apart.Reason);

        var stale = InstallationSummary.Sum(SummarySlot.Consumption, "W", [Stale("Master", 300), Stale("Slave", 250)], Spread);
        Assert.Equal((SummaryState.Stale, (double?)550), (stale.State, stale.Number));
        Assert.Null(InstallationSummary.Sum(SummarySlot.Consumption, "W", [Stale("Master", 300), Missing("Slave")], Spread).Number);
        var none = InstallationSummary.Sum(SummarySlot.PvPower, "W", [Missing("Master"), Missing("Slave")], Spread);
        Assert.Equal((SummaryState.Unavailable, (double?)null), (none.State, none.Number));
        Assert.NotNull(none.Reason);
        Assert.Equal(SummaryState.Unavailable, InstallationSummary.Sum(SummarySlot.PvPower, "W", [new("Master", -2, Now, ReadingQuality.Fresh)], Spread).State);
    }

    [Fact]
    public void AcInputSupplyUsesTheGridOutageThresholdsAndShowsInverterDisagreement()
    {
        Assert.Equal((10d, 180d), (GridSupply.LostAtOrBelowVolts, GridSupply.PresentAboveVolts));
        var present = InstallationSummary.Supply([Fresh("Master", 243.4), Fresh("Slave", 244.5)]);
        Assert.Equal((SummaryState.Available, "present", (double?)244.5), (present.State, present.Text, present.Number));
        var absent = InstallationSummary.Supply([Fresh("Master", 0), Fresh("Slave", 3)]);
        Assert.Equal((SummaryState.Available, "absent"), (absent.State, absent.Text));
        var disagree = InstallationSummary.Supply([Fresh("Master", 243), Fresh("Slave", 0)]);
        Assert.Equal((SummaryState.Partial, "present"), (disagree.State, disagree.Text));
        Assert.Contains("Slave AC input reads 0 V", disagree.Reason);
        // Absent needs every inverter, like the grid outage template's All aggregation.
        Assert.Equal((SummaryState.Partial, "unknown"), Pick(InstallationSummary.Supply([Fresh("Master", 0), Missing("Slave")])));
        Assert.Equal((SummaryState.Partial, "present"), Pick(InstallationSummary.Supply([Fresh("Master", 240), Missing("Slave")])));
        Assert.Equal((SummaryState.Partial, "unknown"), Pick(InstallationSummary.Supply([Fresh("Master", 120), Fresh("Slave", 120)])));
        Assert.Equal((SummaryState.Stale, "unknown"), Pick(InstallationSummary.Supply([Stale("Master", 243), Stale("Slave", 0)])));
        Assert.Equal(SummaryState.Unavailable, InstallationSummary.Supply([Missing("Master"), Missing("Slave")]).State);

        // Voltage present while importing nothing: supply is present and import is a valid 0 W.
        var importing = InstallationSummary.Sum(SummarySlot.AcInput, "W", [Fresh("Master", 0), Fresh("Slave", 0)], Spread);
        Assert.Equal(("present", (double?)0), (present.Text, importing.Number));

        static (SummaryState, string?) Pick(InstallationValue value) => (value.State, value.Text);
    }

    [Fact]
    public void AWholeInstallationReadingIsPreferredWhenCurrent()
    {
        var reading = (Metric metric, double? number, bool? flag, ReadingQuality quality) =>
            new MetricReading(metric, Telemetry.Unit(metric), Now, number, flag, null, quality);
        var inverter = new DeviceDetails(1, null, "Growatt master", DeviceKind.Inverter, "Growatt", "SPF 5000 ES", Connectivity.Online, null, null,
            ReadingQuality.Fresh, [new EndpointState(1, "Main", [], [reading(Metric.PvPower, 900, null, ReadingQuality.Fresh)])]);
        var summary = MonitoringReader.Summarize([inverter], [reading(Metric.PvPower, 1500, null, ReadingQuality.Fresh),
            reading(Metric.GridAvailable, null, false, ReadingQuality.Fresh), reading(Metric.HouseholdPower, 700, null, ReadingQuality.Stale)], Spread);
        Assert.Equal((SummaryBasis.Installation, (double?)1500), (summary[0].Basis, summary[0].Number));
        Assert.Equal((SummaryBasis.Inverters, SummaryState.Unavailable), (summary[1].Basis, summary[1].State));
        Assert.Equal((SummaryBasis.Installation, "absent"), (summary[2].Basis, summary[2].Text));
        Assert.Equal([SummarySlot.PvPower, SummarySlot.Consumption, SummarySlot.GridSupply, SummarySlot.AcInput], summary.Select(item => item.Slot));
    }

    [Fact]
    public void ConnectivityConditionsAcceptStaleAndExistingStatesAndGridTemplatesShareTheSupplyThresholds()
    {
        RuleGroup Rule(string text) => new(LogicalOperator.And, [new(new RuleCondition("c1", [new(SignalKind.Connectivity, DeviceId: 1)],
            SignalAggregation.Single, ConditionOperator.Equals, Text: text))]);
        foreach (var state in new[] { "Online", "Offline", "Unknown", "Stale" }) Assert.Empty(AlertSignals.Validate(Rule(state)));
        Assert.Contains(AlertSignals.Validate(Rule("offline")), error => error.Contains("Online, Stale, Offline or Unknown"));
        Assert.NotEmpty(AlertSignals.Validate(Rule("1")));

        var voltage = new SignalCatalogEntry("m:d1:ac:gridVoltage", new(SignalKind.Measurement, DeviceId: 1, EndpointKey: "ac", Metric: Metric.GridVoltage),
            "Growatt master · AC input voltage", 1, "Growatt master", null, "growatt", SignalValueType.Number, "V", [ConditionOperator.LessThanOrEqual],
            [], 300, 900, 243, null, null, Now);
        var grid = AlertTemplates.Build([voltage]).Templates.Single(template => template.Code == "grid-power-lost").Draft!;
        var condition = AlertSignals.Conditions(grid.Definition).Single();
        Assert.Equal((GridSupply.LostAtOrBelowVolts, (double?)GridSupply.PresentAboveVolts), (condition.Number!.Value, condition.RecoveryNumber));
    }
}
