using HomeAutomation.Domain;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed record EwelinkElectricalStatus(string Status, int? ProviderCode = null);

internal sealed class EwelinkElectricalReporting(TimeProvider clock)
{
    internal const int MaxDevices = 8;
    internal const int ActivationSeconds = 60;
    internal static readonly TimeSpan Renewal = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan ViewerLifetime = TimeSpan.FromSeconds(25);
    private readonly object sync = new();
    private readonly Dictionary<(string Owner, long Property, Guid Tab), DateTimeOffset> viewers = [];
    private readonly Dictionary<string, Activation> activations = new(StringComparer.Ordinal);
    private EwelinkElectricalStatus? stopped;

    private sealed record Activation(DateTimeOffset FirstSent, DateTimeOffset LastSent, DateTimeOffset? Expires);

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

    public bool IsDue(string id)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var key in activations.Where(item => item.Value.LastSent.AddSeconds(ActivationSeconds) <= now)
                         .Select(item => item.Key).ToArray())
                activations.Remove(key);
            return stopped is null && (activations.TryGetValue(id, out var existing)
                ? existing.LastSent + Renewal <= now : activations.Count < MaxDevices);
        }
    }

    public bool TryStart(string owner, long property, string id)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            PruneViewers(now);
            if (!viewers.Keys.Any(key => key.Owner == owner && key.Property == property) || !IsDue(id)) return false;
            activations.TryGetValue(id, out var previous);
            activations[id] = new(previous?.Expires > now ? previous.FirstSent : now, now, previous?.Expires);
            return true;
        }
    }

    public void Accepted(string id)
    {
        lock (sync)
        {
            if (activations.TryGetValue(id, out var activation))
                activations[id] = activation with { Expires = activation.LastSent.AddSeconds(ActivationSeconds) };
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
            if (!activations.TryGetValue(device.ExternalId, out var activation) || activation.Expires <= now || activation.Expires is null)
                return new("waitingForActivation");
            var received = device.Measurements?.Any(item => IsElectrical(item.Key) && item.Value.FromSocket &&
                item.Value.ReceivedAt >= activation.FirstSent && item.Value.ReceivedAt >= now - EwelinkSocketProtocol.Freshness);
            return new(received == true ? "receivingReports" : "awaitingReport");
        }
    }

    internal static bool IsElectrical(Metric metric) => metric is Metric.ActivePower or Metric.Voltage or Metric.Current;

    internal static bool IsMeter(EwelinkDevice device) => device is { Shared: false, Uiid: 32, Model: "PSF-X67" };

    private void PruneViewers(DateTimeOffset now)
    {
        foreach (var key in viewers.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            viewers.Remove(key);
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

    internal async Task<IReadOnlyList<EwelinkRow>> ElectricalTargetsAsync(EwelinkSocketIdentity identity, CancellationToken ct)
    {
        var properties = electrical!.ViewedProperties(identity.Owner);
        if (properties.Length == 0) return [];
        var account = await store.AccountAsync(ct);
        if (!MayActivate(account, identity)) return [];
        var authorized = new HashSet<long>();
        foreach (var property in properties)
            if (await permissions.CanReadAsync(identity.Owner, property, ct)) authorized.Add(property);
        return (await store.DevicesAsync(identity.Owner, ct))
            .Where(row => row.PropertyId is { } property && authorized.Contains(property) &&
                electrical.IsDue(row.ExternalId) && EligibleMeter(row.Device))
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
            if (current?.PropertyId != property || !EligibleMeter(current.Device) || !electrical!.IsDue(current.ExternalId)) return null;
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
