namespace HomeAutomation.Domain;

/// <summary>
/// Application-calculated availability. It is derived from evidence and kept apart from what the provider reports:
/// <list type="bullet">
/// <item><b>Online:</b> a valid reading, or a provider Online report, inside the availability window.</item>
/// <item><b>Offline:</b> a recent provider Offline report that no newer reading contradicts (a confirmed disconnection).</item>
/// <item><b>Stale:</b> there was evidence, but none recent enough. An expired window alone never means Offline.</item>
/// <item><b>Unknown:</b> no evidence at all.</item>
/// </list>
/// </summary>
public enum Availability { Online, Stale, Offline, Unknown }

public enum AvailabilityBasis { RecentReading, ProviderOnline, ProviderOffline, NoRecentEvidence, NoEvidence }

/// <param name="EvidenceAt">The source time of the evidence the state rests on: a reading's observation time or the provider status time.</param>
public sealed record AvailabilityResult(Availability State, AvailabilityBasis Basis, DateTimeOffset? EvidenceAt);

public static class DeviceAvailability
{
    /// <summary>Evidence counts as recent for one and a half polling intervals, so one late poll does not flip a device.</summary>
    public const double WindowFactor = 1.5;

    public static TimeSpan Window(TimeSpan pollInterval) => TimeSpan.FromTicks((long)(pollInterval.Ticks * WindowFactor));

    /// <summary>
    /// Evaluates availability from source times only. A fetch that returns an old reading again does not refresh it, because the
    /// reading keeps its source time. Timestamps further in the future than the tolerated clock skew are not evidence.
    /// </summary>
    /// <param name="reported">The provider's own connectivity report, kept as diagnostic information.</param>
    /// <param name="reportedAt">The source time of that report.</param>
    /// <param name="latestReadingAt">The newest source time of any valid reading of the device.</param>
    public static AvailabilityResult Evaluate(Connectivity? reported, DateTimeOffset? reportedAt, DateTimeOffset? latestReadingAt,
        DateTimeOffset now, TimeSpan window)
    {
        var reading = Usable(latestReadingAt, now);
        var status = reported is Connectivity.Online or Connectivity.Offline ? Usable(reportedAt, now) : null;
        bool Recent(DateTimeOffset? at) => at is { } value && now - value <= window;

        // A disconnection reported after the newest reading outweighs that reading. Growatt sends its `lost` flag in the same record
        // as the readings (equal times), so fresh readings arriving with an Offline flag mean the device is communicating.
        var offlineAfterReading = reported == Connectivity.Offline && status is { } offline && (reading is null || offline > reading);
        if (Recent(reading) && !(offlineAfterReading && Recent(status)))
            return new(Availability.Online, AvailabilityBasis.RecentReading, reading);
        if (reported == Connectivity.Offline && Recent(status))
            return new(Availability.Offline, AvailabilityBasis.ProviderOffline, status);
        if (reported == Connectivity.Online && Recent(status))
            return new(Availability.Online, AvailabilityBasis.ProviderOnline, status);

        var latest = reading > status || status is null ? reading : status;
        return latest is null
            ? new(Availability.Unknown, AvailabilityBasis.NoEvidence, null)
            : new(Availability.Stale, AvailabilityBasis.NoRecentEvidence, latest);
    }

    /// <summary>The provider-neutral connectivity value older consumers expect: Stale and Unknown both read as Unknown.</summary>
    public static Connectivity ToConnectivity(Availability availability) => availability switch
    {
        Availability.Online => Connectivity.Online,
        Availability.Offline => Connectivity.Offline,
        _ => Connectivity.Unknown,
    };

    private static DateTimeOffset? Usable(DateTimeOffset? at, DateTimeOffset now) => at switch
    {
        null => null,
        var value when value > now + AlertLimits.FutureTolerance => null,
        var value when value > now => now,
        var value => value,
    };
}

public enum PvState { Generating, NotGenerating, Unknown }

public enum PvReason { NoReading, Invalid, Stale, NoVoltage, AboveThreshold, HeldBetweenThresholds, BelowThreshold, NoPriorState }

/// <param name="RawWatts">The stored reading, unchanged (for example 1 W at night).</param>
/// <param name="DisplayWatts">What the live views show: 0 W for 0–10 W, null when the reading is missing or invalid.</param>
public sealed record PvGenerationResult(PvState State, PvReason Reason, double? RawWatts, double? DisplayWatts);

/// <summary>
/// PV display normalization and generation state. Raw telemetry, history and energy are never changed; this is presentation only.
/// </summary>
public static class PvGeneration
{
    public const double DisplayZeroAtOrBelowWatts = 10;
    public const double ActivateAboveWatts = 20;
    public const double DeactivateAtOrBelowWatts = 10;

    public static bool IsValid(double? watts) => watts is { } value && double.IsFinite(value) && value >= 0;

    /// <summary>0–10 W shows as 0 W (sensor noise or residual power). Negative or non-finite values are invalid, never production.</summary>
    public static double? Display(double? watts) =>
        IsValid(watts) ? watts!.Value <= DisplayZeroAtOrBelowWatts ? 0 : watts : null;

    /// <summary>
    /// Generating starts above 20 W and ends at 10 W or below. Between the thresholds the previous state holds, found by replaying
    /// <paramref name="recentWatts"/> (oldest first, ending with the latest reading); without a prior state the result is not generating.
    /// A fresh PV voltage of exactly 0 V means no PV input, whatever power is reported. No other voltage threshold is assumed, because
    /// voltage ranges differ between inverter models.
    /// </summary>
    public static PvGenerationResult Evaluate(IReadOnlyList<double?> recentWatts, ReadingQuality powerQuality, double? volts,
        ReadingQuality voltageQuality)
    {
        if (recentWatts.Count == 0 || powerQuality == ReadingQuality.Missing)
            return new(PvState.Unknown, PvReason.NoReading, null, null);
        var latest = recentWatts[^1];
        if (!IsValid(latest)) return new(PvState.Unknown, PvReason.Invalid, latest, null);
        var display = Display(latest);
        if (powerQuality != ReadingQuality.Fresh) return new(PvState.Unknown, PvReason.Stale, latest, display);
        if (voltageQuality == ReadingQuality.Fresh && volts == 0) return new(PvState.NotGenerating, PvReason.NoVoltage, latest, display);

        bool? generating = null;
        foreach (var watts in recentWatts.Where(IsValid).Select(value => value!.Value))
            generating = watts > ActivateAboveWatts ? true : watts <= DeactivateAtOrBelowWatts ? false : generating;
        var now = latest!.Value;
        return generating switch
        {
            true => new(PvState.Generating, now > ActivateAboveWatts ? PvReason.AboveThreshold : PvReason.HeldBetweenThresholds, latest, display),
            false => new(PvState.NotGenerating, now <= DeactivateAtOrBelowWatts ? PvReason.BelowThreshold : PvReason.HeldBetweenThresholds,
                latest, display),
            null => new(PvState.NotGenerating, PvReason.NoPriorState, latest, display),
        };
    }
}

/// <summary>
/// AC input supply thresholds shared by the grid outage alert templates and the live installation summary, so both agree. The voltage
/// is measured at the inverters' AC input after the transfer switch: it shows that AC supply is present, not which source feeds it.
/// </summary>
public static class GridSupply
{
    public const double LostAtOrBelowVolts = 10;
    public const double PresentAboveVolts = 180;
}

public enum SummarySlot { PvPower, Consumption, GridSupply, AcInput }

/// <summary>Installation: a value the provider reports for the whole installation. Inverters: derived from the individual inverters.</summary>
public enum SummaryBasis { Installation, Inverters }

public enum SummaryState { Available, Partial, Stale, Unavailable }

/// <summary>One inverter's reading of a metric, as the live view sees it.</summary>
public sealed record SourceReading(string Source, double? Number, DateTimeOffset? ObservedAt, ReadingQuality Quality);

/// <param name="Text">For grid supply: present, absent or unknown.</param>
/// <param name="ObservedAt">The oldest observation the value uses.</param>
/// <param name="Reason">Why the value is partial, stale or unavailable, in plain words.</param>
public sealed record InstallationValue(SummarySlot Slot, SummaryBasis Basis, SummaryState State, double? Number, string? Unit, string? Text,
    DateTimeOffset? ObservedAt, int Used, int Expected, string? Reason);

/// <summary>
/// Installation-level values for the live summary, computed from latest readings only. Nothing is stored and nothing is invented:
/// a value that cannot be formed honestly is Partial, Stale or Unavailable with a reason.
/// </summary>
public static class InstallationSummary
{
    /// <summary>
    /// Sums one power metric across inverters with separate measurements (each inverter's own PV input, AC output or AC input).
    /// Available needs a fresh reading from every inverter, observed within <paramref name="maximumSpread"/> of each other.
    /// </summary>
    public static InstallationValue Sum(SummarySlot slot, string unit, IReadOnlyList<SourceReading> sources, TimeSpan maximumSpread,
        Func<double?, double?>? display = null)
    {
        display ??= value => value;
        var valid = sources.Where(source => source.Quality != ReadingQuality.Missing && PvGeneration.IsValid(source.Number)).ToArray();
        var fresh = valid.Where(source => source.Quality == ReadingQuality.Fresh).ToArray();
        var expected = sources.Count;
        if (expected == 0) return Unavailable(slot, unit, 0, "No inverter is configured.");
        double Total(IEnumerable<SourceReading> items) => items.Sum(item => display(item.Number) ?? 0);
        DateTimeOffset? Oldest(IEnumerable<SourceReading> items) => items.Min(item => item.ObservedAt);

        if (fresh.Length == expected)
        {
            var spread = fresh.Max(item => item.ObservedAt!.Value) - fresh.Min(item => item.ObservedAt!.Value);
            return spread <= maximumSpread
                ? new(slot, SummaryBasis.Inverters, SummaryState.Available, Total(fresh), unit, null, Oldest(fresh), expected, expected, null)
                : new(slot, SummaryBasis.Inverters, SummaryState.Partial, Total(fresh), unit, null, Oldest(fresh), expected, expected,
                    $"The inverter readings are {Minutes(spread)} apart.");
        }
        var absent = string.Join(" and ", sources.Except(fresh).Select(source => source.Source));
        if (fresh.Length > 0)
            return new(slot, SummaryBasis.Inverters, SummaryState.Partial, Total(fresh), unit, null, Oldest(fresh), fresh.Length, expected,
                $"Only {string.Join(" and ", fresh.Select(source => source.Source))} reported a current value; {absent} did not.");
        if (valid.Length == expected)
            return new(slot, SummaryBasis.Inverters, SummaryState.Stale, Total(valid), unit, null, Oldest(valid), expected, expected,
                "The latest inverter readings are older than the freshness window.");
        return valid.Length > 0
            ? new(slot, SummaryBasis.Inverters, SummaryState.Stale, null, unit, null, Oldest(valid), valid.Length, expected,
                "The inverter readings are old or missing.")
            : Unavailable(slot, unit, expected, "No inverter has reported this measurement.");
    }

    /// <summary>
    /// AC input supply from each inverter's AC input voltage, with the grid outage template's thresholds and its All aggregation:
    /// supply is absent only when every inverter reads 10 V or less, and present when one reads above 180 V.
    /// </summary>
    public static InstallationValue Supply(IReadOnlyList<SourceReading> voltages)
    {
        const string unit = "V";
        var expected = voltages.Count;
        if (expected == 0) return Unavailable(SummarySlot.GridSupply, unit, 0, "No inverter is configured.");
        var valid = voltages.Where(source => source.Quality != ReadingQuality.Missing && PvGeneration.IsValid(source.Number)).ToArray();
        var fresh = valid.Where(source => source.Quality == ReadingQuality.Fresh).ToArray();
        if (fresh.Length == 0)
            return valid.Length > 0
                ? new(SummarySlot.GridSupply, SummaryBasis.Inverters, SummaryState.Stale, null, unit, "unknown", valid.Min(item => item.ObservedAt),
                    0, expected, "The AC input voltage readings are older than the freshness window.")
                : Unavailable(SummarySlot.GridSupply, unit, expected, "No inverter has reported AC input voltage.");

        var present = fresh.Where(source => source.Number > GridSupply.PresentAboveVolts).ToArray();
        var lost = fresh.Where(source => source.Number <= GridSupply.LostAtOrBelowVolts).ToArray();
        var others = voltages.Except(present).ToArray();
        DateTimeOffset? Oldest(IEnumerable<SourceReading> items) => items.Min(item => item.ObservedAt);
        if (present.Length > 0)
            return new(SummarySlot.GridSupply, SummaryBasis.Inverters, others.Length == 0 ? SummaryState.Available : SummaryState.Partial,
                present.Max(item => item.Number), unit, "present", Oldest(present), present.Length, expected,
                others.Length == 0 ? null : string.Join(" ", others.Select(source => fresh.Contains(source)
                    ? $"{source.Source} AC input reads {source.Number:0.#} V." : $"{source.Source} has no current AC input reading.")));
        if (lost.Length == expected)
            return new(SummarySlot.GridSupply, SummaryBasis.Inverters, SummaryState.Available, lost.Max(item => item.Number), unit, "absent",
                Oldest(lost), expected, expected, null);
        return new(SummarySlot.GridSupply, SummaryBasis.Inverters, SummaryState.Partial, fresh.Max(item => item.Number), unit, "unknown",
            Oldest(fresh), fresh.Length, expected, lost.Length == fresh.Length
                ? "Supply counts as absent only when every inverter reads 10 V or less; not every inverter reported a current value."
                : "The AC input voltage is outside the normal range.");
    }

    /// <summary>A whole-installation reading from the provider, preferred over a derived value when it is current.</summary>
    public static InstallationValue Reported(SummarySlot slot, string? unit, double? number, bool? flag, DateTimeOffset? observedAt) =>
        new(slot, SummaryBasis.Installation, SummaryState.Available, number, unit,
            flag is { } supply ? supply ? "present" : "absent" : null, observedAt, 1, 1, null);

    private static InstallationValue Unavailable(SummarySlot slot, string unit, int expected, string reason) =>
        new(slot, SummaryBasis.Inverters, SummaryState.Unavailable, null, unit, null, null, 0, expected, reason);

    private static string Minutes(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";
}
