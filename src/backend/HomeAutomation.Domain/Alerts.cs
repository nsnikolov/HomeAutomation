namespace HomeAutomation.Domain;

// Event rules, incidents and notification routing. Everything here is pure: no clock, database or I/O.
// Rule definitions are structured data validated against a fixed catalog of signal kinds and operators;
// nothing in a definition is ever executed.

public enum AlertSeverity { Info = 0, Warning = 1, Critical = 2 }

/// <summary>Three-valued result. Unknown is never treated as false or zero.</summary>
public enum Truth { False, True, Unknown }

public enum ConditionOperator
{
    Equals, NotEquals, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, IsTrue, IsFalse, EventOccurred, StateChanged
}

public enum LogicalOperator { And, Or }

/// <summary>A measurement series, a device's reported connectivity, or a device domain event (offline or fault).</summary>
public enum SignalKind { Measurement, Connectivity, DeviceEvent }

/// <summary>How a condition with several signals combines them. Single needs exactly one signal.</summary>
public enum SignalAggregation { Single, Any, All }

public enum SignalValueType { Number, Boolean, Status, Event }

/// <summary>
/// A stable signal reference: a device endpoint and metric, an installation metric, a device's connectivity, or a device event.
/// Devices are referenced by their identity in the rule's property, never by display name.
/// </summary>
public sealed record SignalRef(SignalKind Kind, long? DeviceId = null, string? EndpointKey = null, long? InstallationId = null,
    Metric? Metric = null, string? EventCode = null);

public sealed record RuleCondition(string Id, IReadOnlyList<SignalRef> Signals, SignalAggregation Aggregation, ConditionOperator Operator,
    double? Number = null, string? Text = null, string? Unit = null, double? RecoveryNumber = null, int FreshnessSeconds = 900,
    int? WindowSeconds = null);

/// <summary>A node is either a condition or a nested group.</summary>
public sealed record RuleNode(RuleCondition? Condition = null, RuleGroup? Group = null);

public sealed record RuleGroup(LogicalOperator Operator, IReadOnlyList<RuleNode> Children);

/// <summary>Which incident transitions notify, an optional reminder, and an explicit suppression family.</summary>
/// <param name="SuppressWhileActive">
/// When set, this rule's notifications are recorded but not delivered while an incident of that correlation family is active
/// in the same property. Detection and incident history are never suppressed.
/// </param>
public sealed record NotificationPolicy(bool OnActivation = true, bool OnEscalation = true, bool OnDeescalation = false,
    bool OnResolution = true, int ReminderMinutes = 0, string? SuppressWhileActive = null);

public static class AlertLimits
{
    public const int MaxDepth = 3;
    public const int MaxConditions = 12;
    public const int MaxChildren = 8;
    public const int MaxSignalsPerCondition = 8;
    public const int MinFreshnessSeconds = 30;
    public const int MaxFreshnessSeconds = 7 * 24 * 3600;
    public const int MinWindowSeconds = 60;
    public const int MaxWindowSeconds = 24 * 3600;
    public const int MaxTimingSeconds = 24 * 3600;
    public const int MaxReminderMinutes = 24 * 60;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 500;
    public const int MaxCorrelationKeyLength = 80;
    /// <summary>Clock skew tolerated before an observation from the future counts as invalid.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);
}

public static class AlertSignals
{
    public static SignalValueType ValueType(SignalRef signal) => signal.Kind switch
    {
        SignalKind.Connectivity => SignalValueType.Status,
        SignalKind.DeviceEvent => SignalValueType.Event,
        _ => signal.Metric is { } metric ? ValueType(metric) : SignalValueType.Number,
    };

    public static SignalValueType ValueType(Metric metric) => metric switch
    {
        Metric.GridAvailable or Metric.SwitchOn or Metric.HeatingWarning or Metric.HeatingEmergency => SignalValueType.Boolean,
        Metric.OperatingMode or Metric.WarningStatus or Metric.HeatingControlMode or Metric.WaterSetpointMode => SignalValueType.Status,
        _ => SignalValueType.Number,
    };

    /// <summary>The operators offered for a value type. Numeric comparisons are never offered for non-numeric signals.</summary>
    public static IReadOnlyList<ConditionOperator> Operators(SignalValueType type, SignalKind kind) => type switch
    {
        SignalValueType.Number => [ConditionOperator.LessThan, ConditionOperator.LessThanOrEqual, ConditionOperator.GreaterThan,
            ConditionOperator.GreaterThanOrEqual, ConditionOperator.Equals, ConditionOperator.NotEquals],
        SignalValueType.Boolean => [ConditionOperator.IsTrue, ConditionOperator.IsFalse, ConditionOperator.StateChanged],
        SignalValueType.Status when kind == SignalKind.Connectivity => [ConditionOperator.Equals, ConditionOperator.NotEquals],
        SignalValueType.Status => [ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.StateChanged],
        _ => [ConditionOperator.EventOccurred],
    };

    public static bool UsesWindow(ConditionOperator op) => op is ConditionOperator.EventOccurred or ConditionOperator.StateChanged;

    /// <summary>Validates the structure of a definition. Signal existence and property ownership are checked against the database.</summary>
    public static IReadOnlyList<string> Validate(RuleGroup root)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        Visit(root, 1);
        if (count == 0) errors.Add("Add at least one condition.");
        return errors;

        void Visit(RuleGroup group, int depth)
        {
            if (depth > AlertLimits.MaxDepth) { errors.Add($"Groups can be nested at most {AlertLimits.MaxDepth} levels deep."); return; }
            if (!Enum.IsDefined(group.Operator)) errors.Add("Use AND or OR to combine conditions.");
            if (group.Children is null || group.Children.Count == 0) { errors.Add("A group needs at least one condition."); return; }
            if (group.Children.Count > AlertLimits.MaxChildren) errors.Add($"A group can hold at most {AlertLimits.MaxChildren} items.");
            foreach (var node in group.Children)
            {
                if (node is null || (node.Condition is null) == (node.Group is null))
                {
                    errors.Add("Each item must be either a condition or a group.");
                    continue;
                }
                if (node.Group is { } nested) Visit(nested, depth + 1);
                else if (++count > AlertLimits.MaxConditions)
                {
                    if (count == AlertLimits.MaxConditions + 1) errors.Add($"A rule can have at most {AlertLimits.MaxConditions} conditions.");
                }
                else ValidateCondition(node.Condition!);
            }
        }

        void ValidateCondition(RuleCondition condition)
        {
            var label = string.IsNullOrWhiteSpace(condition.Id) ? "A condition" : $"Condition {condition.Id}";
            if (string.IsNullOrWhiteSpace(condition.Id) || condition.Id.Length > 16 || !ids.Add(condition.Id))
                errors.Add("Each condition needs a unique short identifier.");
            if (condition.Signals is null || condition.Signals.Count == 0) { errors.Add($"{label} needs a signal."); return; }
            if (condition.Signals.Count > AlertLimits.MaxSignalsPerCondition)
                errors.Add($"{label} can use at most {AlertLimits.MaxSignalsPerCondition} signals.");
            if (!Enum.IsDefined(condition.Aggregation) || !Enum.IsDefined(condition.Operator))
            {
                errors.Add($"{label} uses an unsupported operator or aggregation.");
                return;
            }
            if (condition.Aggregation == SignalAggregation.Single && condition.Signals.Count != 1)
                errors.Add($"{label} selects one signal; choose Any or All to combine several.");
            if (condition.Signals.Distinct().Count() != condition.Signals.Count) errors.Add($"{label} lists a signal twice.");
            foreach (var signal in condition.Signals)
            {
                var shapeError = SignalShapeError(signal);
                if (shapeError is not null) { errors.Add($"{label}: {shapeError}"); return; }
            }
            var first = condition.Signals[0];
            var type = ValueType(first);
            if (condition.Signals.Any(signal => signal.Kind != first.Kind || signal.Metric != first.Metric ||
                (first.Kind == SignalKind.DeviceEvent && signal.EventCode != first.EventCode)))
                errors.Add($"{label} combines different kinds of signals. Use one metric per condition.");
            if (!Operators(type, first.Kind).Contains(condition.Operator))
                errors.Add($"{label}: {Describe(condition.Operator)} is not available for {type.ToString().ToLowerInvariant()} signals.");

            var expectedUnit = first.Kind == SignalKind.Measurement && first.Metric is { } metric ? Telemetry.Unit(metric) : null;
            if (type == SignalValueType.Number)
            {
                if (!string.Equals(condition.Unit ?? expectedUnit, expectedUnit, StringComparison.Ordinal))
                    errors.Add($"{label} must use the signal's unit ({expectedUnit}).");
                if (condition.Number is not { } threshold || !double.IsFinite(threshold))
                    errors.Add($"{label} needs a numeric threshold.");
                else if (first.Metric is Metric.BatterySoc or Metric.Humidity or Metric.InverterLoadPercent && threshold is < 0 or > 100)
                    errors.Add($"{label}: a percentage threshold must be between 0 and 100.");
                if (condition.RecoveryNumber is { } recovery)
                {
                    if (!double.IsFinite(recovery)) errors.Add($"{label} has an invalid recovery threshold.");
                    else if (condition.Number is { } value && condition.Operator switch
                    {
                        ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual => recovery < value,
                        ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual => recovery > value,
                        _ => true,
                    })
                        errors.Add(condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals
                            ? $"{label}: a recovery threshold applies only to below or above comparisons."
                            : $"{label}: the recovery threshold must lie on the recovered side of the activation threshold.");
                }
            }
            else
            {
                if (condition.Unit is not null) errors.Add($"{label}: only numeric signals have a unit.");
                if (condition.RecoveryNumber is not null) errors.Add($"{label}: only numeric signals have a recovery threshold.");
                if (condition.Number is not null) errors.Add($"{label}: only numeric signals take a numeric threshold.");
            }
            if (type == SignalValueType.Status && condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals)
            {
                if (string.IsNullOrWhiteSpace(condition.Text) || condition.Text.Length > 32)
                    errors.Add($"{label} needs a state to compare with.");
                else if (first.Kind == SignalKind.Connectivity && !Enum.TryParse<Connectivity>(condition.Text, false, out _))
                    errors.Add($"{label}: connectivity is Online, Offline or Unknown.");
            }
            else if (condition.Text is not null) errors.Add($"{label} does not take a state value.");

            if (UsesWindow(condition.Operator))
            {
                if (condition.WindowSeconds is not { } window || window < AlertLimits.MinWindowSeconds || window > AlertLimits.MaxWindowSeconds)
                    errors.Add($"{label} needs a look-back window between 1 minute and 24 hours.");
            }
            else if (condition.WindowSeconds is not null) errors.Add($"{label} does not use a look-back window.");
            if (condition.Operator != ConditionOperator.EventOccurred &&
                condition.FreshnessSeconds is < AlertLimits.MinFreshnessSeconds or > AlertLimits.MaxFreshnessSeconds)
                errors.Add($"{label}: the maximum reading age must be between 30 seconds and 7 days.");
        }
    }

    private static string? SignalShapeError(SignalRef signal)
    {
        if (signal is null || !Enum.IsDefined(signal.Kind)) return "the signal kind is not supported.";
        if (signal.Metric is { } metric && !Enum.IsDefined(metric)) return "the metric is not supported.";
        return signal.Kind switch
        {
            SignalKind.Measurement when signal.Metric is null => "choose a measurement.",
            SignalKind.Measurement when signal.EventCode is not null => "a measurement has no event code.",
            SignalKind.Measurement when (signal.DeviceId is null) == (signal.InstallationId is null) =>
                "choose either a device endpoint or an installation.",
            SignalKind.Measurement when signal.DeviceId is not null && string.IsNullOrWhiteSpace(signal.EndpointKey) => "choose a device endpoint.",
            SignalKind.Measurement when signal.InstallationId is not null && signal.EndpointKey is not null => "an installation has no endpoint.",
            SignalKind.Connectivity when signal.DeviceId is null || signal.InstallationId is not null || signal.Metric is not null ||
                signal.EndpointKey is not null || signal.EventCode is not null => "connectivity needs exactly one device.",
            SignalKind.DeviceEvent when signal.DeviceId is null || signal.InstallationId is not null || signal.Metric is not null ||
                signal.EndpointKey is not null => "a device event needs exactly one device.",
            SignalKind.DeviceEvent when string.IsNullOrWhiteSpace(signal.EventCode) || signal.EventCode.Length > 64 => "choose an event.",
            _ => null,
        };
    }

    public static string Describe(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => "equals",
        ConditionOperator.NotEquals => "does not equal",
        ConditionOperator.LessThan => "is below",
        ConditionOperator.LessThanOrEqual => "is at or below",
        ConditionOperator.GreaterThan => "is above",
        ConditionOperator.GreaterThanOrEqual => "is at or above",
        ConditionOperator.IsTrue => "is on",
        ConditionOperator.IsFalse => "is off",
        ConditionOperator.EventOccurred => "occurred",
        _ => "changed",
    };

    public static IEnumerable<RuleCondition> Conditions(RuleGroup group) => group.Children.SelectMany(node =>
        node.Condition is { } condition ? [condition] : node.Group is { } nested ? Conditions(nested) : []);

    public static Truth And(IEnumerable<Truth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == Truth.False) return Truth.False;
            unknown |= value == Truth.Unknown;
        }
        return unknown ? Truth.Unknown : Truth.True;
    }

    public static Truth Or(IEnumerable<Truth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == Truth.True) return Truth.True;
            unknown |= value == Truth.Unknown;
        }
        return unknown ? Truth.Unknown : Truth.False;
    }
}

/// <summary>The latest known state of one signal, as loaded for an evaluation.</summary>
/// <param name="Unavailable">Why no value can be used (for example the device moved away); the signal is then Unknown.</param>
/// <param name="Changed">For StateChanged: whether the value changed inside the window.</param>
/// <param name="Events">For EventOccurred: events recorded inside the window.</param>
public sealed record SignalReading(SignalRef Signal, string Label, string? Unavailable = null, double? Number = null, bool? Flag = null,
    string? Text = null, DateTimeOffset? ObservedAt = null, bool Changed = false, int Events = 0, DateTimeOffset? LastEventAt = null)
{
    public static SignalReading Missing(SignalRef signal, string label, string reason) => new(signal, label, reason);
}

public enum SignalQuality { Fresh, Stale, Missing, Invalid, Unavailable }

public sealed record SignalEvidence(SignalRef Signal, string Label, Truth Result, SignalQuality Quality, double? Number, bool? Flag,
    string? Text, DateTimeOffset? ObservedAt, int Events, string? Reason);

public sealed record ConditionEvidence(string ConditionId, Truth Result, IReadOnlyList<SignalEvidence> Signals);

public sealed record RuleEvaluation(Truth Result, IReadOnlyList<ConditionEvidence> Conditions, DateTimeOffset? NewestObservation);

public static class RuleEvaluator
{
    /// <summary>
    /// Evaluates a definition against loaded readings. A signal is Unknown when it is unavailable, missing, invalid, from the future,
    /// or older than the condition's maximum age. <paramref name="latched"/> applies recovery thresholds (hysteresis) while the rule is active.
    /// </summary>
    public static RuleEvaluation Evaluate(RuleGroup root, Func<RuleCondition, SignalRef, SignalReading> read, DateTimeOffset now, bool latched)
    {
        var conditions = new List<ConditionEvidence>();
        DateTimeOffset? newest = null;
        var result = Group(root);
        return new RuleEvaluation(result, conditions, newest);

        Truth Group(RuleGroup group)
        {
            // Evaluate every child so the evidence is complete even when the result is decided early.
            var values = group.Children.Select(node => node.Condition is { } condition ? Condition(condition) : Group(node.Group!)).ToArray();
            return group.Operator == LogicalOperator.And ? AlertSignals.And(values) : AlertSignals.Or(values);
        }

        Truth Condition(RuleCondition condition)
        {
            var signals = condition.Signals.Select(signal => Signal(condition, read(condition, signal))).ToArray();
            var value = condition.Aggregation == SignalAggregation.All ? AlertSignals.And(signals.Select(item => item.Result))
                : AlertSignals.Or(signals.Select(item => item.Result));
            conditions.Add(new ConditionEvidence(condition.Id, value, signals));
            return value;
        }

        SignalEvidence Signal(RuleCondition condition, SignalReading reading)
        {
            SignalEvidence Unknown(SignalQuality quality, string reason) => new(reading.Signal, reading.Label, Truth.Unknown, quality,
                reading.Number, reading.Flag, reading.Text, reading.ObservedAt, reading.Events, reason);
            SignalEvidence Known(bool value) => new(reading.Signal, reading.Label, value ? Truth.True : Truth.False, SignalQuality.Fresh,
                reading.Number, reading.Flag, reading.Text, reading.ObservedAt, reading.Events, null);

            if (reading.Unavailable is { } unavailable) return Unknown(SignalQuality.Unavailable, unavailable);
            if (condition.Operator == ConditionOperator.EventOccurred)
            {
                if (reading.LastEventAt is { } last && (newest is null || last > newest)) newest = last;
                return Known(reading.Events > 0);
            }
            if (reading.ObservedAt is not { } observed) return Unknown(SignalQuality.Missing, "No reading has been recorded.");
            if (observed > now + AlertLimits.FutureTolerance) return Unknown(SignalQuality.Invalid, "The reading is timestamped in the future.");
            if (now - observed > TimeSpan.FromSeconds(condition.FreshnessSeconds))
                return Unknown(SignalQuality.Stale, "The latest reading is older than the allowed age.");
            if (newest is null || observed > newest) newest = observed;

            switch (condition.Operator)
            {
                case ConditionOperator.StateChanged:
                    return Known(reading.Changed);
                case ConditionOperator.IsTrue or ConditionOperator.IsFalse:
                    return reading.Flag is { } flag ? Known(flag == (condition.Operator == ConditionOperator.IsTrue))
                        : Unknown(SignalQuality.Invalid, "The reading is not an on/off value.");
            }
            if (AlertSignals.ValueType(reading.Signal) == SignalValueType.Status)
                return reading.Text is { } text
                    ? Known(string.Equals(text, condition.Text, StringComparison.OrdinalIgnoreCase) == (condition.Operator == ConditionOperator.Equals))
                    : Unknown(SignalQuality.Invalid, "The reading has no state value.");
            if (reading.Number is not { } number || !double.IsFinite(number))
                return Unknown(SignalQuality.Invalid, "The reading is not a number.");
            var threshold = condition.Number!.Value;
            // Hysteresis: while latched, the condition holds until the value crosses the recovery threshold.
            if (latched && condition.RecoveryNumber is { } recovery)
                return condition.Operator switch
                {
                    ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual => Known(number <= recovery),
                    _ => Known(number >= recovery),
                };
            return Known(condition.Operator switch
            {
                ConditionOperator.Equals => number.Equals(threshold),
                ConditionOperator.NotEquals => !number.Equals(threshold),
                ConditionOperator.LessThan => number < threshold,
                ConditionOperator.LessThanOrEqual => number <= threshold,
                ConditionOperator.GreaterThan => number > threshold,
                _ => number >= threshold,
            });
        }
    }
}

public enum RulePhase { Inactive, Pending, Active, Recovering }

public enum RuleTransition { None, Activated, Cleared }

/// <summary>Persisted per-rule detection state. Times are server evaluation instants, except InputsObservedAt.</summary>
public sealed record RuleRuntime(RulePhase Phase, DateTimeOffset? TrueSince = null, DateTimeOffset? FalseSince = null,
    DateTimeOffset? ActiveSince = null, DateTimeOffset? InputsObservedAt = null)
{
    public static readonly RuleRuntime Initial = new(RulePhase.Inactive);
    public bool Latched => Phase is RulePhase.Active or RulePhase.Recovering;
}

public static class RuleStateMachine
{
    /// <summary>
    /// Advances detection state. Activation needs the result to stay True for the confirmation period; Unknown or False cancels it.
    /// Recovery needs the result to stay False for the recovery period; True cancels it, and Unknown neither confirms nor cancels it,
    /// so missing data never resolves an incident. Ordering is guaranteed upstream: each series' latest reading only moves forward in
    /// observation time, so a late or replayed older sample never reaches the evaluation. InputsObservedAt records the newest input used.
    /// </summary>
    public static (RuleRuntime State, RuleTransition Transition) Step(RuleRuntime state, Truth truth, DateTimeOffset? newestInput,
        DateTimeOffset now, TimeSpan activation, TimeSpan recovery)
    {
        var observed = newestInput is { } latest && (state.InputsObservedAt is null || latest > state.InputsObservedAt) ? latest : state.InputsObservedAt;
        RuleRuntime Next(RulePhase phase, DateTimeOffset? trueSince = null, DateTimeOffset? falseSince = null, DateTimeOffset? activeSince = null) =>
            new(phase, trueSince, falseSince, activeSince, observed);

        switch (state.Phase)
        {
            case RulePhase.Inactive or RulePhase.Pending when truth == Truth.True:
                var since = state.Phase == RulePhase.Pending ? state.TrueSince ?? now : now;
                return now - since >= activation
                    ? (Next(RulePhase.Active, activeSince: now), RuleTransition.Activated)
                    : (Next(RulePhase.Pending, trueSince: since), RuleTransition.None);
            case RulePhase.Inactive or RulePhase.Pending:
                return (Next(RulePhase.Inactive), RuleTransition.None);
            case RulePhase.Active or RulePhase.Recovering when truth == Truth.True:
                return (Next(RulePhase.Active, activeSince: state.ActiveSince ?? now), RuleTransition.None);
            case RulePhase.Active when truth == Truth.Unknown:
                return (Next(RulePhase.Active, activeSince: state.ActiveSince ?? now), RuleTransition.None);
            case RulePhase.Recovering when truth == Truth.Unknown:
                return (Next(RulePhase.Recovering, falseSince: state.FalseSince ?? now, activeSince: state.ActiveSince), RuleTransition.None);
            default:
                var falseSince = state.Phase == RulePhase.Recovering ? state.FalseSince ?? now : now;
                return now - falseSince >= recovery
                    ? (Next(RulePhase.Inactive), RuleTransition.Cleared)
                    : (Next(RulePhase.Recovering, falseSince: falseSince, activeSince: state.ActiveSince), RuleTransition.None);
        }
    }
}

public enum IncidentChange { None, Open, Escalate, Deescalate, Resolve }

public static class IncidentPlanner
{
    /// <summary>
    /// The highest severity among active contributing rules determines the incident's severity. An incident stays open while any
    /// contributing rule is active; it resolves only when none is.
    /// </summary>
    public static (IncidentChange Change, AlertSeverity? Severity) Plan(AlertSeverity? current, IEnumerable<AlertSeverity> active)
    {
        var levels = active.ToArray();
        if (levels.Length == 0) return current is null ? (IncidentChange.None, null) : (IncidentChange.Resolve, current);
        var highest = levels.Max();
        if (current is not { } existing) return (IncidentChange.Open, highest);
        return highest > existing ? (IncidentChange.Escalate, highest)
            : highest < existing ? (IncidentChange.Deescalate, highest) : (IncidentChange.None, existing);
    }

    public static string CorrelationKey(long ruleId, string? correlation) =>
        string.IsNullOrWhiteSpace(correlation) ? $"rule:{ruleId}" : correlation.Trim().ToLowerInvariant();

    /// <summary>Lower-case family names such as grid.outage or grid.outage:north.</summary>
    public static bool ValidCorrelationKey(string key) =>
        key.Length is > 0 and <= AlertLimits.MaxCorrelationKeyLength && !key.StartsWith("rule:", StringComparison.Ordinal) &&
        key.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_' or ':') &&
        char.IsAsciiLetterLower(key[0]);
}

public enum NotificationKind { Activated, Escalated, Deescalated, Resolved, Reminder }

public sealed record SeverityPreference(bool InApp, bool Browser, bool Sound = false);

public sealed record EventPreferences(bool Activated = true, bool Escalated = true, bool Deescalated = true, bool Resolved = true,
    bool Reminder = true);

/// <summary>A user's delivery preferences. In-app delivery of Critical notifications cannot be turned off.</summary>
public sealed record NotificationPreferences(SeverityPreference Info, SeverityPreference Warning, SeverityPreference Critical,
    EventPreferences Events)
{
    public static readonly NotificationPreferences Default = new(new(true, false), new(true, true), new(true, true), new());

    public const string InApp = "inApp";
    public const string Browser = "browser";

    public NotificationPreferences Normalize() => this with { Critical = Critical with { InApp = true } };

    public SeverityPreference For(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => Critical with { InApp = true },
        AlertSeverity.Warning => Warning,
        _ => Info,
    };

    public bool Wants(NotificationKind kind) => kind switch
    {
        NotificationKind.Activated => Events.Activated,
        NotificationKind.Escalated => Events.Escalated,
        NotificationKind.Deescalated => Events.Deescalated,
        NotificationKind.Resolved => Events.Resolved,
        _ => Events.Reminder,
    };

    /// <summary>Whether the notification appears in the in-app list and which optional channels receive it.</summary>
    public (bool InApp, IReadOnlyList<string> Channels) Route(AlertSeverity severity, NotificationKind kind)
    {
        var preference = For(severity);
        var wanted = Wants(kind);
        var inApp = severity == AlertSeverity.Critical || (preference.InApp && wanted);
        return (inApp, wanted && preference.Browser ? [Browser] : []);
    }
}
