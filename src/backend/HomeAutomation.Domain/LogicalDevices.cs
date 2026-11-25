namespace HomeAutomation.Domain;

/// <summary>Capability differences between the current and a replacement physical source of one logical channel.</summary>
public sealed record CapabilityComparison(
    IReadOnlyList<Capability> Shared, IReadOnlyList<Capability> Missing, IReadOnlyList<Capability> Added)
{
    /// <summary>At least one measurement capability continues, so the replacement keeps a continuous history.</summary>
    public bool Compatible => Shared.Any(LogicalDeviceRules.IsMeasurement);
}

public static class LogicalDeviceRules
{
    /// <summary>Start of an assignment created for a device's whole recorded history during initialization.</summary>
    public static readonly DateTime HistoryOrigin = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Open intervals are treated as ending at this instant in calculations.</summary>
    public static readonly DateTime OpenEnd = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public const string DefaultChannel = "main";

    /// <summary>Status capabilities describe device health rather than a measurement that continues across hardware.</summary>
    public static bool IsMeasurement(Capability capability) =>
        capability is not (Capability.FaultStatus or Capability.WarningStatus or Capability.EmergencyStatus);

    /// <summary>Half-open intervals [from, to) overlap when each starts before the other ends.</summary>
    public static bool Overlaps(DateTime fromA, DateTime? toA, DateTime fromB, DateTime? toB) =>
        fromA < (toB ?? OpenEnd) && fromB < (toA ?? OpenEnd);

    /// <summary>Two assignments of one physical device conflict when they share an endpoint or either covers the whole device.</summary>
    public static bool SameSource(long deviceA, long? endpointA, long deviceB, long? endpointB) =>
        deviceA == deviceB && (endpointA is null || endpointB is null || endpointA == endpointB);

    public static CapabilityComparison Compare(IEnumerable<Capability> current, IEnumerable<Capability> replacement)
    {
        var before = current.ToHashSet();
        var after = replacement.ToHashSet();
        return new(before.Intersect(after).Order().ToArray(), before.Except(after).Order().ToArray(),
            after.Except(before).Order().ToArray());
    }

    /// <summary>A device needs an explicit endpoint when one measurement capability exists on more than one endpoint.</summary>
    public static bool RequiresEndpoint(IEnumerable<IEnumerable<Capability>> endpoints) =>
        endpoints.SelectMany(capabilities => capabilities.Where(IsMeasurement).Distinct()).GroupBy(capability => capability)
            .Any(group => group.Count() > 1);

    public static bool ValidChannel(string? channel) =>
        channel is { Length: >= 1 and <= 64 } && (char.IsAsciiLetterLower(channel[0]) || char.IsAsciiDigit(channel[0])) &&
        channel.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    /// <summary>Lower-case channel key derived from a provider endpoint key, made unique with a numeric suffix.</summary>
    public static string ChannelFor(string endpointKey, ISet<string> taken)
    {
        var normalized = new string(endpointKey.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.' ? character : '-')
            .ToArray()).Trim('-', '.', '_');
        if (normalized.Length == 0) normalized = "endpoint";
        if (normalized.Length > 56) normalized = normalized[..56];
        var candidate = normalized;
        for (var suffix = 2; !taken.Add(candidate); suffix++) candidate = $"{normalized}-{suffix}";
        return candidate;
    }
}

/// <summary>
/// Plans one physical series inside one assignment interval. Only observations inside the interval belong to the logical
/// device, so time whose exact value depends on observations outside it is calculated from filtered raw samples. Hourly
/// rollups are reused only where their lookback and lookahead lie inside the interval.
/// </summary>
public static class SegmentPlanner
{
    public static IReadOnlyList<HistoryPart> Split(TimeWindow window, TimeWindow segment, DateTime? aggregatedFrom,
        DateTime? aggregatedTo, TimeSpan lookback, TimeSpan lookahead, DateTime now)
    {
        var start = Max(window.Start, segment.Start);
        var end = Min(Min(window.End, segment.End), now);
        var parts = new List<HistoryPart>();
        if (end <= start) return parts;
        var innerStart = segment.Start + lookback;
        var innerEnd = segment.End == LogicalDeviceRules.OpenEnd ? segment.End : segment.End - lookahead;
        if (innerEnd < innerStart) innerEnd = innerStart;

        Raw(start, Min(end, innerStart));
        var middleStart = Max(start, innerStart);
        var middleEnd = Min(end, innerEnd);
        if (middleEnd > middleStart) parts.AddRange(HistoryPlanner.Split(new(middleStart, middleEnd), aggregatedFrom, aggregatedTo, now));
        Raw(Max(start, innerEnd), end);
        return parts;

        void Raw(DateTime from, DateTime to)
        {
            if (to > from) parts.Add(new(new(from, to), HistoryPartSource.Raw));
        }
    }

    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
}
