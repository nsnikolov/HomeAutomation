namespace HomeAutomation.Domain;

/// <summary>How a metric's observations are summarized over time.</summary>
public enum MetricAggregation
{
    /// <summary>Instantaneous numeric value. Time-weighted with a bounded zero-order hold.</summary>
    Gauge,
    /// <summary>Two-state observation such as a relay. Reports ON seconds within the covered time.</summary>
    Boolean,
    /// <summary>Text status. Kept as raw transitions only.</summary>
    Status,
    /// <summary>Energy counter (kWh) that restarts at local midnight of the counter's own time zone.</summary>
    DailyResetCounter,
    /// <summary>Monotonic cumulative energy counter (kWh) without a known modulus.</summary>
    CumulativeCounter
}

[Flags]
public enum AggregateFlags
{
    None = 0,
    /// <summary>A cumulative counter decreased. A new baseline started and energy across the reset is unknown.</summary>
    CounterReset = 1,
    /// <summary>A counter increment spanned more than the maximum hold and was attributed to the bucket of its closing observation.</summary>
    CounterGap = 2,
    /// <summary>A counter step was rejected as implausible or as an in-day decrease of a daily counter.</summary>
    CounterDiscontinuity = 4,
    /// <summary>A stored observation could not be used for this metric (for example a non-finite or wrongly typed value).</summary>
    InvalidValue = 8
}

/// <summary>One stored observation. <see cref="At"/> is a UTC instant.</summary>
public readonly record struct RawObservation(DateTime At, double? Number, bool? Flag);

/// <summary>Bounded-hold policy for one series.</summary>
/// <param name="MaximumHold">How long an observation may represent the measured quantity when no newer observation exists.</param>
/// <param name="CounterResetZone">The zone whose local midnight restarts a daily-reset counter.</param>
public sealed record AggregationPolicy(TimeSpan MaximumHold, TimeZoneInfo? CounterResetZone = null);

/// <summary>A half-open UTC interval [Start, End).</summary>
public readonly record struct TimeWindow(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Summary of one series over a half-open UTC window. Summaries of adjacent windows combine exactly with
/// <see cref="Combine"/>, so hourly rows plus partial-hour raw calculations reproduce any longer interval.
/// </summary>
public sealed record HistoryAggregate(
    DateTime Start,
    DateTime End,
    int SampleCount,
    double CoveredSeconds,
    double? Minimum,
    double? Maximum,
    double? TimeWeightedAverage,
    double? ValueSum,
    DateTime? FirstObservedAt,
    DateTime? LastObservedAt,
    double? LastValue,
    double? OnSeconds,
    int TransitionCount,
    double? MeasuredWh,
    double? EstimatedPositiveWh,
    double? EstimatedNegativeWh,
    AggregateFlags Flags)
{
    public bool IsEmpty => SampleCount == 0 && CoveredSeconds <= 0 && MeasuredWh is null && Flags == AggregateFlags.None;

    /// <summary>Arithmetic mean of the observations recorded inside the window.</summary>
    public double? ArithmeticMean => SampleCount > 0 && ValueSum is { } sum ? sum / SampleCount : null;

    public static HistoryAggregate Empty(DateTime start, DateTime end) =>
        new(start, end, 0, 0, null, null, null, null, null, null, null, null, 0, null, null, null, AggregateFlags.None);

    /// <summary>Combines summaries of non-overlapping parts of [start, end).</summary>
    public static HistoryAggregate Combine(DateTime start, DateTime end, IEnumerable<HistoryAggregate> parts)
    {
        var result = Empty(start, end);
        double weighted = 0, weightedSeconds = 0;
        foreach (var part in parts)
        {
            if (part.TimeWeightedAverage is { } average && part.CoveredSeconds > 0)
            {
                weighted += average * part.CoveredSeconds;
                weightedSeconds += part.CoveredSeconds;
            }
            var later = part.LastObservedAt is { } last && (result.LastObservedAt is null || last > result.LastObservedAt);
            result = result with
            {
                SampleCount = result.SampleCount + part.SampleCount,
                CoveredSeconds = result.CoveredSeconds + part.CoveredSeconds,
                Minimum = Min(result.Minimum, part.Minimum),
                Maximum = Max(result.Maximum, part.Maximum),
                ValueSum = Sum(result.ValueSum, part.ValueSum),
                FirstObservedAt = part.FirstObservedAt is { } first && (result.FirstObservedAt is null || first < result.FirstObservedAt)
                    ? first : result.FirstObservedAt,
                LastObservedAt = later ? part.LastObservedAt : result.LastObservedAt,
                LastValue = later ? part.LastValue : result.LastValue,
                OnSeconds = Sum(result.OnSeconds, part.OnSeconds),
                TransitionCount = result.TransitionCount + part.TransitionCount,
                MeasuredWh = Sum(result.MeasuredWh, part.MeasuredWh),
                EstimatedPositiveWh = Sum(result.EstimatedPositiveWh, part.EstimatedPositiveWh),
                EstimatedNegativeWh = Sum(result.EstimatedNegativeWh, part.EstimatedNegativeWh),
                Flags = result.Flags | part.Flags
            };
        }
        return result with { TimeWeightedAverage = weightedSeconds > 0 ? weighted / weightedSeconds : null };
    }

    private static double? Min(double? left, double? right) => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
    private static double? Max(double? left, double? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
    private static double? Sum(double? left, double? right) => left is null ? right : right is null ? left : left + right;
}

public static class TelemetryAggregation
{
    /// <summary>Raw history a counter needs before a window: the previous local midnight or the cumulative predecessor limit.</summary>
    public static readonly TimeSpan CounterLookback = TimeSpan.FromHours(26);

    /// <summary>The longest step between cumulative counter observations that can still be differenced.</summary>
    public static readonly TimeSpan CumulativePredecessorLimit = TimeSpan.FromHours(24);

    /// <summary>Counter increments implying more than this average power are rejected as discontinuities.</summary>
    public const double MaximumCounterPowerWatts = 100_000;

    public static MetricAggregation Kind(Metric metric) => metric switch
    {
        Metric.GridAvailable or Metric.SwitchOn or Metric.HeatingWarning or Metric.HeatingEmergency => MetricAggregation.Boolean,
        Metric.OperatingMode or Metric.WarningStatus or Metric.HeatingControlMode or Metric.WaterSetpointMode => MetricAggregation.Status,
        _ when Telemetry.IsDailyEnergyCounter(metric) => MetricAggregation.DailyResetCounter,
        Metric.Energy => MetricAggregation.CumulativeCounter,
        _ => MetricAggregation.Gauge
    };

    public static bool IsAggregated(Metric metric) => Kind(metric) != MetricAggregation.Status;

    public static bool IsCounter(Metric metric) =>
        Kind(metric) is MetricAggregation.DailyResetCounter or MetricAggregation.CumulativeCounter;

    /// <summary>Power gauges in W can be integrated into an estimated energy.</summary>
    public static bool IsPower(Metric metric) => Kind(metric) == MetricAggregation.Gauge && Telemetry.Unit(metric) == "W";

    /// <summary>Raw history required before a window starts for an exact summary of that window.</summary>
    public static TimeSpan Lookback(Metric metric, AggregationPolicy policy) =>
        IsCounter(metric) && CounterLookback > policy.MaximumHold ? CounterLookback : policy.MaximumHold;

    /// <summary>Raw history required after a window ends for an exact summary of that window.</summary>
    public static TimeSpan Lookahead(Metric metric, AggregationPolicy policy) => IsCounter(metric) ? policy.MaximumHold : TimeSpan.Zero;

    /// <summary>
    /// Summarizes ordered observations over sorted, non-overlapping windows. The stream must contain every stored
    /// observation in [first window start − <see cref="Lookback"/>, last window end + <see cref="Lookahead"/>).
    /// Coverage never extends beyond <paramref name="now"/>.
    /// </summary>
    public static IReadOnlyList<HistoryAggregate> Summarize(Metric metric, AggregationPolicy policy,
        IReadOnlyList<RawObservation> observations, IReadOnlyList<TimeWindow> windows, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaximumHold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy), "The maximum hold must be positive.");
        for (var index = 0; index < windows.Count; index++)
        {
            if (windows[index].End <= windows[index].Start || index > 0 && windows[index].Start < windows[index - 1].End)
                throw new ArgumentException("Windows must be non-empty, sorted and non-overlapping.", nameof(windows));
        }
        var ordered = IsOrdered(observations) ? observations : observations.OrderBy(item => item.At).ToArray();
        var accumulators = windows.Select(window => new Accumulator(window)).ToArray();
        if (accumulators.Length == 0) return [];
        switch (Kind(metric))
        {
            case MetricAggregation.Gauge:
            case MetricAggregation.Boolean:
                SummarizeHeld(metric, policy, ordered, accumulators, now);
                break;
            case MetricAggregation.DailyResetCounter:
            case MetricAggregation.CumulativeCounter:
                SummarizeCounter(metric, policy, ordered, accumulators);
                break;
            default:
                throw new ArgumentException("Status metrics are not aggregated.", nameof(metric));
        }
        return accumulators.Select(item => item.Build(metric)).ToArray();
    }

    private static void SummarizeHeld(Metric metric, AggregationPolicy policy, IReadOnlyList<RawObservation> ordered,
        Accumulator[] windows, DateTime now)
    {
        var boolean = Kind(metric) == MetricAggregation.Boolean;
        var power = IsPower(metric);
        var valid = new List<(DateTime At, double Value)>(ordered.Count);
        foreach (var observation in ordered)
        {
            double? value = boolean
                ? observation.Flag is { } flag ? (flag ? 1 : 0) : null
                : observation.Number is { } number && double.IsFinite(number) ? number : null;
            if (value is null)
            {
                Find(windows, observation.At)?.MarkInvalid();
                continue;
            }
            if (valid.Count > 0 && valid[^1].At == observation.At) continue; // Stored rows are unique; keep the first.
            valid.Add((observation.At, value.Value));
        }

        var cursor = 0;
        for (var index = 0; index < valid.Count; index++)
        {
            var (at, value) = valid[index];
            if (Find(windows, at) is { } owner)
            {
                var transition = boolean && index > 0 && valid[index - 1].Value != value &&
                    at - valid[index - 1].At <= policy.MaximumHold;
                owner.Observe(at, value, transition);
            }

            var end = at + policy.MaximumHold;
            if (index + 1 < valid.Count && valid[index + 1].At < end) end = valid[index + 1].At;
            if (now < end) end = now;
            if (end <= at) continue;
            while (cursor < windows.Length && windows[cursor].Window.End <= at) cursor++;
            for (var window = cursor; window < windows.Length && windows[window].Window.Start < end; window++)
            {
                var overlap = Overlap(windows[window].Window, at, end);
                if (overlap <= 0) continue;
                windows[window].Hold(value, overlap, boolean, power);
            }
        }
    }

    private static void SummarizeCounter(Metric metric, AggregationPolicy policy, IReadOnlyList<RawObservation> ordered,
        Accumulator[] windows)
    {
        var daily = Kind(metric) == MetricAggregation.DailyResetCounter;
        if (daily && policy.CounterResetZone is null)
            throw new ArgumentException("A daily-reset counter requires its reset time zone.", nameof(policy));
        (DateTime At, double Value)? previous = null;
        foreach (var observation in ordered)
        {
            if (observation.Number is not { } value || !double.IsFinite(value) || value < 0)
            {
                Find(windows, observation.At)?.MarkInvalid();
                continue;
            }
            if (previous is { } duplicate && duplicate.At == observation.At) continue;
            var owner = Find(windows, observation.At);
            owner?.Observe(observation.At, value, transition: false, countValue: false);

            (DateTime At, double Value)? baseline;
            if (daily)
            {
                var midnight = CalendarDays.StartOfDayUtc(CalendarDays.LocalDate(observation.At, policy.CounterResetZone!), policy.CounterResetZone!);
                baseline = previous is { } same && same.At >= midnight ? same : (midnight, 0d);
            }
            else
            {
                baseline = previous is { } prior && observation.At - prior.At <= CumulativePredecessorLimit ? prior : null;
            }
            previous = (observation.At, value);
            if (baseline is not { } start) continue;

            if (value < start.Value)
            {
                owner?.Mark(daily ? AggregateFlags.CounterDiscontinuity : AggregateFlags.CounterReset);
                continue;
            }
            var increment = (value - start.Value) * 1000; // kWh to Wh
            var elapsed = observation.At - start.At;
            if (increment > 0 && (elapsed <= TimeSpan.Zero ||
                increment / elapsed.TotalHours > MaximumCounterPowerWatts))
            {
                owner?.Mark(AggregateFlags.CounterDiscontinuity);
                continue;
            }
            if (elapsed <= policy.MaximumHold)
            {
                if (elapsed <= TimeSpan.Zero)
                {
                    owner?.Measure(increment, 0);
                    continue;
                }
                for (var index = FirstEndingAfter(windows, start.At);
                     index < windows.Length && windows[index].Window.Start < observation.At; index++)
                {
                    var overlap = Overlap(windows[index].Window, start.At, observation.At);
                    if (overlap > 0) windows[index].Measure(increment * overlap / elapsed.TotalSeconds, overlap);
                }
            }
            else if (owner is not null)
            {
                owner.Measure(increment, 0);
                owner.Mark(AggregateFlags.CounterGap);
            }
        }
    }

    private static double Overlap(TimeWindow window, DateTime start, DateTime end)
    {
        var from = start > window.Start ? start : window.Start;
        var to = end < window.End ? end : window.End;
        return to > from ? (to - from).TotalSeconds : 0;
    }

    private static int FirstEndingAfter(Accumulator[] windows, DateTime at)
    {
        int low = 0, high = windows.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (windows[middle].Window.End <= at) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static Accumulator? Find(Accumulator[] windows, DateTime at)
    {
        int low = 0, high = windows.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var window = windows[middle].Window;
            if (at < window.Start) high = middle - 1;
            else if (at >= window.End) low = middle + 1;
            else return windows[middle];
        }
        return null;
    }

    private static bool IsOrdered(IReadOnlyList<RawObservation> observations)
    {
        for (var index = 1; index < observations.Count; index++)
            if (observations[index].At < observations[index - 1].At) return false;
        return true;
    }

    private sealed class Accumulator(TimeWindow window)
    {
        public TimeWindow Window { get; } = window;
        private int samples;
        private double covered, weighted, on, positive, negative, measured;
        private double? minimum, maximum, sum, last;
        private DateTime? first, latest;
        private int transitions;
        private bool held, metered;
        private AggregateFlags flags;

        public void MarkInvalid() => flags |= AggregateFlags.InvalidValue;
        public void Mark(AggregateFlags flag) => flags |= flag;

        public void Observe(DateTime at, double value, bool transition, bool countValue = true)
        {
            samples++;
            first ??= at;
            latest = at;
            last = value;
            if (transition) transitions++;
            if (!countValue) return;
            sum = (sum ?? 0) + value;
            minimum = minimum is null ? value : Math.Min(minimum.Value, value);
            maximum = maximum is null ? value : Math.Max(maximum.Value, value);
        }

        public void Hold(double value, double seconds, bool boolean, bool power)
        {
            held = true;
            covered += seconds;
            weighted += value * seconds;
            minimum = minimum is null ? value : Math.Min(minimum.Value, value);
            maximum = maximum is null ? value : Math.Max(maximum.Value, value);
            if (boolean && value > 0) on += seconds;
            if (!power) return;
            if (value > 0) positive += value * seconds / 3600;
            else if (value < 0) negative += -value * seconds / 3600;
        }

        public void Measure(double wattHours, double seconds)
        {
            metered = true;
            measured += wattHours;
            covered += seconds;
        }

        public HistoryAggregate Build(Metric metric)
        {
            var kind = Kind(metric);
            var boolean = kind == MetricAggregation.Boolean;
            var counter = kind is MetricAggregation.DailyResetCounter or MetricAggregation.CumulativeCounter;
            return new HistoryAggregate(Window.Start, Window.End, samples, covered,
                counter ? null : minimum, counter ? null : maximum,
                !counter && !boolean && held && covered > 0 ? weighted / covered : null,
                counter || boolean ? null : sum, first, latest, last,
                boolean && held ? on : null, transitions,
                counter && metered ? measured : null,
                IsPower(metric) && held ? positive : null,
                IsPower(metric) && held ? negative : null, flags);
        }
    }
}

/// <summary>Local calendar days in IANA or Windows time zones, including DST and non-whole-hour offsets.</summary>
public static class CalendarDays
{
    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>The first UTC instant of a local date. Skipped midnights start at the first valid local time.</summary>
    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 26 * 60) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var largest = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - largest, DateTimeKind.Utc);
        }
        return DateTime.SpecifyKind(local - zone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    public static TimeWindow Day(DateOnly date, TimeZoneInfo zone) =>
        new(StartOfDayUtc(date, zone), StartOfDayUtc(date.AddDays(1), zone));

    public static DateTime FloorHour(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    public static DateTime CeilingHour(DateTime utc) =>
        utc.Ticks % TimeSpan.TicksPerHour == 0 ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : FloorHour(utc).AddHours(1);
}

public enum HistoryPartSource { Hourly, Raw, NotAggregated }

public readonly record struct HistoryPart(TimeWindow Window, HistoryPartSource Source);

/// <summary>
/// Splits a reporting window into materialized UTC hours, raw-calculated partial or recent intervals, and
/// intervals outside the aggregated range. The parts are contiguous and cover the window up to <c>now</c>.
/// </summary>
public static class HistoryPlanner
{
    public static IReadOnlyList<HistoryPart> Split(TimeWindow window, DateTime? aggregatedFrom, DateTime? aggregatedTo, DateTime now)
    {
        var end = window.End < now ? window.End : now;
        var parts = new List<HistoryPart>();
        if (end <= window.Start) return parts;
        var from = aggregatedFrom ?? CalendarDays.FloorHour(now);
        var to = aggregatedTo ?? from;
        var cursor = window.Start;
        while (cursor < end)
        {
            if (cursor < from)
            {
                Add(cursor, Min(end, from), HistoryPartSource.NotAggregated);
                cursor = Min(end, from);
                continue;
            }
            var hourEnd = CalendarDays.FloorHour(cursor).AddHours(1);
            if (cursor == CalendarDays.FloorHour(cursor) && hourEnd <= end && hourEnd <= to)
            {
                Add(cursor, hourEnd, HistoryPartSource.Hourly);
                cursor = hourEnd;
                continue;
            }
            var next = Min(end, hourEnd);
            if (cursor < to && to < next) next = to;
            Add(cursor, next, HistoryPartSource.Raw);
            cursor = next;
        }
        return parts;

        void Add(DateTime start, DateTime stop, HistoryPartSource source)
        {
            if (parts.Count > 0 && parts[^1].Source == source && source != HistoryPartSource.Hourly && parts[^1].Window.End == start)
                parts[^1] = new(new(parts[^1].Window.Start, stop), source);
            else parts.Add(new(new(start, stop), source));
        }
    }

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
}
