namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattOptions
{
    public bool Enabled { get; set; }
    public string SiteKey { get; set; } = "";
    public string MasterId { get; set; } = "";
    public string SlaveId { get; set; } = "";
    public string BatterySourceId { get; set; } = "";
    public int PollSeconds { get; set; } = 300;
    public int FreshnessSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 15;
    public bool SumIndependentPv { get; set; }
    public bool MasterReportsWholeSiteLoad { get; set; }

    public bool HasValidCadence() => PollSeconds is >= 30 and <= 1800 &&
        TimeoutSeconds >= 1 && TimeoutSeconds < PollSeconds &&
        FreshnessSeconds >= 2 * PollSeconds && FreshnessSeconds <= 86400;

    public bool HasValidMapping() =>
        SiteKey is { Length: > 0 and <= 64 } && SiteKey == SiteKey.Trim() && !SiteKey.Any(char.IsControl) &&
        ValidId(MasterId) && ValidId(SlaveId) &&
        !string.Equals(MasterId, SlaveId, StringComparison.OrdinalIgnoreCase) &&
        (BatterySourceId == MasterId || BatterySourceId == SlaveId);

    private static bool ValidId(string id) =>
        id is { Length: > 0 and <= 120 } && id == id.Trim() && !id.Any(char.IsControl) &&
        !id.Equals(GrowattMapper.SharedBatteryId, StringComparison.OrdinalIgnoreCase);
}
