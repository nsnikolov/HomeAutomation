namespace HomeAutomation.Infrastructure.Simulator;

public enum Scenario { Demonstration, Normal, Outage, Stale, Offline, Fault }

public sealed class SimulatorOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 10;
    public int FreshnessSeconds { get; set; } = 45;
    public Scenario Scenario { get; set; } = Scenario.Demonstration;
}
