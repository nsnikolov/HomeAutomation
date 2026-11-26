using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using HomeAutomation.Domain;
using Microsoft.Extensions.Logging;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed record EwelinkElectricalStatus(string Status, int? ProviderCode = null);

/// <summary>
/// In-process state of UIID 5/32 electrical reporting activation. CoolKit meters report power, voltage and current
/// only during a <c>uiActive</c> window and only on threshold changes, so every electrical history sample depends on an
/// activation. Activations come from two independent demands: operator viewing leases (renewed every 50 seconds) and,
/// when enabled, the backend collection schedule (one window per meter per interval, with no browser involved).
/// </summary>
internal sealed class EwelinkElectricalReporting(TimeProvider clock, EwelinkOptions? options = null)
{
    internal const int MaxDevices = 8;
    internal const int ActivationSeconds = 60;
    internal const int UnconfirmedLimit = 3;
    internal static readonly TimeSpan Renewal = TimeSpan.FromSeconds(50);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaximumStagger = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(6);
    private static readonly TimeSpan ViewerLifetime = TimeSpan.FromSeconds(25);
    private readonly object sync = new();
    private readonly Dictionary<(string Owner, long Property, Guid Tab), DateTimeOffset> viewers = [];
    private readonly Dictionary<string, Activation> activations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);
    private TimeSpan interval = TimeSpan.FromSeconds(options?.ElectricalCollectionSeconds ?? 900);
    private int unconfirmedStreak;
    private EwelinkElectricalStatus? stopped;

    private sealed record Activation(DateTimeOffset FirstSent, DateTimeOffset LastSent, DateTimeOffset? Expires);
    private sealed record Plan(DateTimeOffset NextDue, int Failures = 0, DateTimeOffset? BlockedUntil = null,
        EwelinkElectricalStatus? Failure = null);

    /// <summary>True when the backend schedules activation windows on its own, without any viewer.</summary>
    public bool Collecting => options?.ElectricalCollectionConfigured == true;

    /// <summary>The effective spacing between collection windows for one meter, after the request budget is applied.</summary>
    public TimeSpan Interval { get { lock (sync) return interval; } }

    public void SetInterval(TimeSpan value) { lock (sync) interval = value; }

    public bool View(string owner, long property, Guid tab, bool active)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            var key = (owner, property, tab);
            PruneViewers(now);
            if (!active) { viewers.Remove(key); return true; }
            if (viewers.Count >= 32 && !viewers.ContainsKey(key)) return false;
            viewers[key] = now + ViewerLifetime;
            return true;
        }
    }

    public long[] ViewedProperties(string owner)
    {
        lock (sync)
        {
            PruneViewers(clock.GetUtcNow());
            return [.. viewers.Keys.Where(key => key.Owner == owner).Select(key => key.Property).Distinct()];
        }
    }

    public bool IsViewed(string owner, long property)
    {
        lock (sync)
        {
            PruneViewers(clock.GetUtcNow());
            return viewers.Keys.Any(key => key.Owner == owner && key.Property == property);
        }
    }

    /// <summary>
    /// Whether a meter needs an activation now. A viewed meter renews every 50 seconds. An unviewed meter is due once
    /// per collection interval when collection is enabled. Devices in back-off and a stopped process are never due.
    /// At most <see cref="MaxDevices"/> windows are open at once, which bounds a burst after a restart.
    /// </summary>
    public bool IsDue(string id, bool viewed)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            PruneActivations(now);
            if (stopped is not null) return false;
            var plan = PlanFor(id, now);
            if (plan.BlockedUntil > now) return false;
            if (activations.TryGetValue(id, out var existing))
                return existing.LastSent + Renewal <= now && (viewed || Collecting && plan.NextDue <= now);
            if (activations.Count >= MaxDevices) return false;
            return viewed || Collecting && plan.NextDue <= now;
        }
    }

    public bool TryStart(string owner, long property, string id)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            PruneViewers(now);
            var viewed = viewers.Keys.Any(key => key.Owner == owner && key.Property == property);
            if (!viewed && !Collecting || !IsDue(id, viewed)) return false;
            activations.TryGetValue(id, out var previous);
            activations[id] = new(previous?.Expires > now ? previous.FirstSent : now, now, previous?.Expires);
            // Any window, viewer or scheduled, satisfies the collection schedule for one interval.
            plans[id] = PlanFor(id, now) with { NextDue = now + interval };
            return true;
        }
    }

    public void Accepted(string id)
    {
        lock (sync)
        {
            unconfirmedStreak = 0;
            if (activations.TryGetValue(id, out var activation))
                activations[id] = activation with { Expires = activation.LastSent.AddSeconds(ActivationSeconds) };
            if (plans.TryGetValue(id, out var plan))
                plans[id] = plan with { Failures = 0, BlockedUntil = null, Failure = null };
        }
    }

    /// <summary>
    /// Records a per-device failure (CoolKit 504 or a missing acknowledgement) with bounded exponential back-off and
    /// deterministic jitter, so one unreachable meter never stops the others. Returns the account-wide streak of
    /// unacknowledged sends, which the collector uses to restart the socket.
    /// </summary>
    public int Failed(string id, EwelinkElectricalStatus failure)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            activations.Remove(id);
            var plan = PlanFor(id, now);
            var failures = Math.Min(plan.Failures + 1, 30);
            var until = now + Backoff(id, failures);
            // The failed window never opened, so the meter retries when its back-off ends rather than a full interval later.
            plans[id] = plan with { Failures = failures, BlockedUntil = until, Failure = failure, NextDue = until };
            return failure.Status == "activationUnconfirmed" ? ++unconfirmedStreak : unconfirmedStreak;
        }
    }

    public void ResetUnconfirmed() { lock (sync) unconfirmedStreak = 0; }

    /// <summary>Drops schedule entries for meters that are no longer eligible, keeping memory bounded by the inventory.</summary>
    public void Retain(IReadOnlyCollection<string> ids)
    {
        lock (sync)
        {
            var keep = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var id in plans.Keys.Where(id => !keep.Contains(id)).ToArray()) plans.Remove(id);
        }
    }

    public void Stop(string reason, int? providerCode)
    {
        lock (sync) stopped = new(reason, providerCode);
    }

    public bool IsStopped { get { lock (sync) return stopped is not null; } }

    public EwelinkElectricalStatus Status(EwelinkDevice device)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            if (stopped is not null) return stopped;
            if (plans.TryGetValue(device.ExternalId, out var plan) && plan.BlockedUntil > now && plan.Failure is { } failure)
                return failure;
            if (!activations.TryGetValue(device.ExternalId, out var activation) || activation.Expires <= now || activation.Expires is null)
                return new(Collecting ? "scheduledCollection" : "waitingForActivation");
            var received = device.Measurements?.Any(item => IsElectrical(item.Key) && item.Value.FromSocket &&
                item.Value.ReceivedAt >= activation.FirstSent && item.Value.ReceivedAt >= now - EwelinkSocketProtocol.Freshness);
            return new(received == true ? "receivingReports" : "awaitingReport");
        }
    }

    internal static bool IsElectrical(Metric metric) => metric is Metric.ActivePower or Metric.Voltage or Metric.Current;

    /// <summary>
    /// Owned devices that implement the documented UIID 5 power-detection protocol, which UIID 32 inherits
    /// (PSF-X67, Sonoff POW-class plugs). The model string is not part of the protocol contract.
    /// </summary>
    internal static bool IsMeter(EwelinkDevice device) => device is { Shared: false, Uiid: 5 or 32 };

    internal static TimeSpan Backoff(string id, int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 12);
        var delay = BaseBackoff * (1L << exponent);
        if (delay >= MaximumBackoff) return MaximumBackoff;
        // Up to 10 % jitter, derived from the device and attempt so tests and restarts stay reproducible.
        var jitter = delay * (Hash($"{id}:{failures}") % 100 / 1000d);
        return delay + jitter >= MaximumBackoff ? MaximumBackoff : delay + jitter;
    }

    internal static TimeSpan Stagger(string id, TimeSpan interval)
    {
        var span = (long)Math.Min(interval.TotalSeconds, MaximumStagger.TotalSeconds);
        return span <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Hash(id) % (ulong)span);
    }

    private Plan PlanFor(string id, DateTimeOffset now)
    {
        if (plans.TryGetValue(id, out var plan)) return plan;
        // A newly seen meter waits for the startup delay plus a stable per-device offset, so a restart never fires
        // every meter at once and the provider sees the same spread on each start.
        plan = new(now + StartupDelay + Stagger(id, interval));
        plans[id] = plan;
        return plan;
    }

    private void PruneActivations(DateTimeOffset now)
    {
        foreach (var key in activations.Where(item => item.Value.LastSent.AddSeconds(ActivationSeconds) <= now)
                     .Select(item => item.Key).ToArray())
            activations.Remove(key);
    }

    private void PruneViewers(DateTimeOffset now)
    {
        foreach (var key in viewers.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            viewers.Remove(key);
    }

    private static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// The shared regional request budget that electrical activations are charged against. Activations are counted
/// conservatively as requests because CoolKit does not state whether WebSocket updates count toward its quota.
/// </summary>
internal static class EwelinkCollectionBudget
{
    /// <summary>The store sums today's bucket and the 31 before it.</summary>
    internal const int RollingDays = 32;
    internal const int MonthlyQuota = 50_000;
    /// <summary>The last 5,000 calls stay available for HTTP reconciliation and other existing operations.</summary>
    internal const int ElectricalCeiling = 45_000;
    /// <summary>Requests per UTC day after which electrical activations wait for the next day: 45,000 / 32.</summary>
    internal const int DailyElectricalShare = ElectricalCeiling / RollingDays;
    /// <summary>Observed normal scan: the family list, one inventory page and the terminating empty page.</summary>
    internal const int CallsPerInventoryScan = 3;
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(24);

    public static int HttpDailyEstimate(int pollMinutes) =>
        (int)Math.Ceiling(CallsPerInventoryScan * 1440d / Math.Max(5, pollMinutes));

    public static int ElectricalDailyAllowance(int pollMinutes) => DailyElectricalShare - HttpDailyEstimate(pollMinutes);

    /// <summary>
    /// The configured interval, stretched when the eligible meters would otherwise spend more than the electrical
    /// share of a day's budget. It never shortens the configured interval.
    /// </summary>
    public static TimeSpan EffectiveInterval(TimeSpan configured, int meters, int pollMinutes)
    {
        if (meters <= 0) return configured;
        var allowance = ElectricalDailyAllowance(pollMinutes);
        if (allowance <= 0) return MaximumInterval;
        var needed = TimeSpan.FromSeconds(Math.Ceiling(meters * 86_400d / allowance));
        if (needed <= configured) return configured;
        return needed > MaximumInterval ? MaximumInterval : needed;
    }

    public static double ActivationsPerDay(int meters, TimeSpan interval) => meters * 86_400d / interval.TotalSeconds;
}

/// <summary>
/// Aggregate collection counters for operators. Summaries carry counts and ages only, never device identities,
/// names, values, tokens or payloads.
/// </summary>
internal sealed class EwelinkCollectionStats(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastObservation = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private string[] eligible = [];
    private DateTimeOffset? lastAccepted;

    public void Count(string stage, int amount = 1) => counts.AddOrUpdate(stage, amount, (_, count) => count + amount);

    public void Eligible(IReadOnlyCollection<string> ids)
    {
        lock (sync)
        {
            eligible = [.. ids];
            foreach (var id in lastObservation.Keys.Where(id => !ids.Contains(id)).ToArray()) lastObservation.TryRemove(id, out _);
        }
    }

    public void Accepted()
    {
        Count("activationAccepted");
        lock (sync) lastAccepted = clock.GetUtcNow();
    }

    public void Persisted(string id, int samples)
    {
        Count("electricalFramesPersisted");
        Count("electricalSamplesPersisted", samples);
        lastObservation[id] = clock.GetUtcNow();
    }

    internal (int Eligible, int Reporting, string Ages, string Counters, int? LastAcceptedSeconds) Drain(TimeSpan period)
    {
        var now = clock.GetUtcNow();
        string[] ids;
        DateTimeOffset? accepted;
        lock (sync) { ids = eligible; accepted = lastAccepted; }
        var ages = ids.Select(id => lastObservation.TryGetValue(id, out var at) ? (double?)(now - at).TotalMinutes : null)
            .OrderBy(age => age ?? double.MaxValue)
            .Select(age => age is { } minutes ? Math.Floor(minutes).ToString(CultureInfo.InvariantCulture) + "m" : "none")
            .ToArray();
        var reporting = ids.Count(id => lastObservation.TryGetValue(id, out var at) && now - at <= period);
        var snapshot = new List<string>();
        foreach (var key in counts.Keys.Order(StringComparer.Ordinal))
            if (counts.TryRemove(key, out var count)) snapshot.Add($"{key}={count}");
        return (ids.Length, reporting, ages.Length == 0 ? "none" : string.Join(" ", ages), string.Join(", ", snapshot),
            accepted is { } at2 ? (int)(now - at2).TotalSeconds : null);
    }

    public void Report(ILogger logger, string mode, TimeSpan interval, TimeSpan period)
    {
        var (meters, reporting, ages, counters, accepted) = Drain(period);
        logger.LogInformation(
            "eWeLink electrical collection summary: mode {Mode}, eligible meters {Eligible}, meters with a persisted observation in the period {Reporting}, effective interval {IntervalSeconds}s, last accepted activation {LastAcceptedSeconds}s ago, last observation ages {Ages}, counters {Counters}.",
            mode, meters, reporting, (int)interval.TotalSeconds, accepted, ages, counters.Length == 0 ? "none" : counters);
    }
}

/// <summary>Scoped operations the collector needs; the socket worker supplies an implementation backed by DI scopes.</summary>
internal interface IEwelinkActivationHost
{
    Task<IReadOnlyList<EwelinkRow>> TargetsAsync(CancellationToken ct);
    Task<Task<int>?> SendAsync(EwelinkRow target, EwelinkActivationChannel channel, CancellationToken ct);
    Task NotifyAsync(long property, CancellationToken ct);
}

/// <summary>
/// The single activation loop of an authenticated account socket. It serves viewer renewals and the background
/// collection schedule together, sends one command at a time and waits for its acknowledgement, so provider
/// traffic does not grow with the number of browsers.
/// </summary>
internal sealed class EwelinkElectricalCollector(EwelinkElectricalReporting electrical, EwelinkCollectionStats stats,
    TimeProvider clock, ILogger logger)
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(4);

    public string Mode => electrical.Collecting ? "backgroundWindows" : "viewerOnly";

    public async Task RunAsync(string owner, IEwelinkActivationHost host, EwelinkActivationChannel channel, CancellationToken ct,
        EwelinkSocketTrace? trace = null)
    {
        electrical.ResetUnconfirmed();
        using var timer = new PeriodicTimer(Tick, clock);
        DateTimeOffset? retryAt = null;
        var nextScan = clock.GetUtcNow();
        var nextSummary = nextScan + SummaryInterval;
        do
        {
            var now = clock.GetUtcNow();
            if (electrical.Collecting && now >= nextSummary)
            {
                stats.Report(logger, Mode, electrical.Interval, SummaryInterval);
                nextSummary = now + SummaryInterval;
            }
            var viewing = electrical.ViewedProperties(owner).Length > 0;
            if (electrical.IsStopped || retryAt > now || !viewing && !(electrical.Collecting && now >= nextScan)) continue;
            nextScan = now + ScanInterval;
            foreach (var target in await host.TargetsAsync(ct))
            {
                try
                {
                    var reply = await host.SendAsync(target, channel, ct);
                    if (reply is null) continue;
                    stats.Count("activationSent");
                    trace?.Count("activationSent");
                    var code = await reply.WaitAsync(ReplyTimeout, clock, ct);
                    if (code == 0)
                    {
                        electrical.Accepted(target.ExternalId);
                        stats.Accepted();
                        trace?.Count("activationAccepted");
                        await host.NotifyAsync(target.PropertyId!.Value, ct);
                    }
                    else if (code == 504)
                    {
                        // CoolKit documents 504 as "the device does not respond (offline or command error)".
                        electrical.Failed(target.ExternalId, new("deviceUnavailable", code));
                        stats.Count("deviceUnavailable");
                        trace?.Count("deviceUnavailable");
                        await host.NotifyAsync(target.PropertyId!.Value, ct);
                    }
                    else
                    {
                        await StopAsync("activationRejected", code);
                        break;
                    }
                }
                catch (TimeoutException)
                {
                    if (!electrical.Collecting)
                    {
                        await StopAsync("activationUnconfirmed", null);
                        break;
                    }
                    // uiActive only opens a reporting window, so a lost acknowledgement is safe to retry after back-off.
                    var streak = electrical.Failed(target.ExternalId, new("activationUnconfirmed"));
                    stats.Count("activationUnconfirmed");
                    trace?.Count("activationUnconfirmed");
                    if (streak >= EwelinkElectricalReporting.UnconfirmedLimit)
                    {
                        logger.LogWarning("eWeLink electrical activation was unacknowledged {Count} times in a row; reconnecting the socket.", streak);
                        throw new EwelinkFailure("activationUnconfirmed");
                    }
                    break;
                }
                catch (EwelinkFailure failure) when (failure.Status is "quotaExceeded" or "syncDeferred")
                {
                    retryAt = failure.RetryAt ?? clock.GetUtcNow().AddMinutes(1);
                    stats.Count($"deferred.{failure.Status}");
                    logger.LogWarning("eWeLink electrical activation deferred with {Status} until {RetryAt}.", failure.Status, retryAt);
                    break;
                }
            }
        } while (await timer.WaitForNextTickAsync(ct));

        async Task StopAsync(string reason, int? providerCode)
        {
            electrical.Stop(reason, providerCode);
            stats.Count(reason);
            trace?.Count(reason);
            logger.LogWarning("eWeLink electrical activation stopped with {Status}, CoolKit code {ProviderCode}.", reason, providerCode);
            foreach (var property in electrical.ViewedProperties(owner))
                await host.NotifyAsync(property, ct);
        }
    }
}

public sealed partial class EwelinkService
{
    public async Task SetElectricalViewerAsync(string owner, long property, Guid tab, bool active, CancellationToken ct)
    {
        if (!options.LiveElectricalConfigured || electrical is null) throw new EwelinkFailure("liveElectricalDisabled");
        var account = await store.AccountAsync(ct);
        if (account?.Owner != owner || (active && account.Envelope is null)) throw new EwelinkFailure("forbidden");
        if (!electrical.View(owner, property, tab, active)) throw new EwelinkFailure("rateLimited");
    }

    internal async Task<IReadOnlyList<EwelinkRow>> ElectricalTargetsAsync(EwelinkSocketIdentity identity, CancellationToken ct,
        EwelinkCollectionStats? stats = null)
    {
        var viewed = electrical!.ViewedProperties(identity.Owner).ToHashSet();
        var collecting = electrical.Collecting;
        if (viewed.Count == 0 && !collecting) return [];
        var account = await store.AccountAsync(ct);
        if (!MayActivate(account, identity)) return [];
        var readable = new Dictionary<long, bool>();
        var eligible = new List<EwelinkRow>();
        foreach (var row in await store.DevicesAsync(identity.Owner, ct))
        {
            // Devices without the documented power-detection protocol never receive activations or power samples.
            if (row.PropertyId is not { } property || !EwelinkElectricalReporting.IsMeter(row.Device)) continue;
            if (!collecting && !viewed.Contains(property)) continue;
            if (!readable.TryGetValue(property, out var allowed))
                readable[property] = allowed = await permissions.CanReadAsync(identity.Owner, property, ct);
            if (!allowed)
            {
                stats?.Count("skipped.propertyScope");
                continue;
            }
            if (!EligibleMeter(row.Device))
            {
                stats?.Count("skipped.offlineOrStale");
                continue;
            }
            eligible.Add(row);
        }
        if (collecting)
        {
            var ids = eligible.Select(row => row.ExternalId).ToArray();
            electrical.Retain(ids);
            stats?.Eligible(ids);
            electrical.SetInterval(EwelinkCollectionBudget.EffectiveInterval(
                TimeSpan.FromSeconds(options.ElectricalCollectionSeconds), ids.Length, options.PollMinutes));
        }
        return eligible.Where(row => electrical.IsDue(row.ExternalId, viewed.Contains(row.PropertyId!.Value)))
            .Take(EwelinkElectricalReporting.MaxDevices).ToArray();
    }

    internal async Task<Task<int>?> SendElectricalActivationAsync(EwelinkSocketIdentity identity, EwelinkRow target,
        EwelinkActivationChannel channel, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return null;
        try
        {
            if (!MayActivate(await store.AccountAsync(ct), identity) || target.PropertyId is not { } property ||
                !await permissions.CanReadAsync(identity.Owner, property, ct))
                return null;
            var current = (await store.DevicesAsync(identity.Owner, ct)).SingleOrDefault(row => row.Id == target.Id);
            if (current?.PropertyId != property || !EligibleMeter(current.Device) ||
                !electrical!.IsDue(current.ExternalId, electrical.IsViewed(identity.Owner, property))) return null;
            await client.ReserveElectricalActivationAsync(identity.Tokens.Region, ct);
            if (!electrical!.TryStart(identity.Owner, property, current.ExternalId)) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4), clock);
            using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await channel.SendAsync(current.ExternalId, send.Token);
        }
        finally { gate.Release(); }
    }

    private bool MayActivate(EwelinkAccount? account, EwelinkSocketIdentity identity) =>
        account?.Status is ("connected" or "partial") && identity.Matches(account, clock.GetUtcNow());

    private bool EligibleMeter(EwelinkDevice device) =>
        EwelinkElectricalReporting.IsMeter(device) && device.Connectivity == Connectivity.Online &&
        Telemetry.Quality(device.SeenAt, clock.GetUtcNow(), TimeSpan.FromMinutes(options.PollMinutes + 2)) == ReadingQuality.Fresh;
}
