namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattOptions
{
    public bool Enabled { get; set; }
    public string ApiToken { get; set; } = "";
    public string SiteKey { get; set; } = "";
    public string MasterId { get; set; } = "";
    public string SlaveId { get; set; } = "";
    public string BatterySourceId { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public int PollSeconds { get; set; } = 300;
    public int FreshnessSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 15;
    public bool SumIndependentPv { get; set; }
    public bool MasterReportsWholeSiteLoad { get; set; }
    public bool TelemetryDiagnostics { get; set; }

    public bool HasCloudToken() => ApiToken is { Length: > 0 and <= 512 } &&
        ApiToken.All(character => character is >= '!' and <= '~');

    public bool HasCloudConfiguration() => HasCloudToken() && HasValidMapping() && HasValidCadence() &&
        PollSeconds >= 300 && IsCloudSerial(MasterId) && IsCloudSerial(SlaveId) &&
        !string.IsNullOrWhiteSpace(TimeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _);

    internal static bool IsCloudSerial(string? id) => id is { Length: > 0 and <= 120 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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
