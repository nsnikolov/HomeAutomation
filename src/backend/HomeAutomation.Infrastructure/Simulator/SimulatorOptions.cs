namespace HomeAutomation.Infrastructure.Simulator;

public enum Scenario { Demonstration, Normal, Outage, Stale, Offline, Fault }

public sealed class SimulatorOptions
{
    /// <summary>Opt-in demonstration data for isolated development and test environments only. Never a fallback for provider data.</summary>
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 10;
    public int FreshnessSeconds { get; set; } = 45;
    public Scenario Scenario { get; set; } = Scenario.Demonstration;
}
