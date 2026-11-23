using System.Security.Cryptography;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed record EwelinkDeviceView(string Id, string Name, string Model, long? RoomId, Connectivity Connectivity,
    DateTimeOffset SeenAt, ReadingQuality Quality, bool HasRelay, bool? Relay, DateTimeOffset? RelayAt, ReadingQuality RelayQuality,
    IReadOnlyList<MetricReading> Measurements, IReadOnlyList<Metric> SupportedMetrics);
public sealed record EwelinkView(string Status, bool Manage, DateTimeOffset? LastSuccess, DateTimeOffset? NextSync,
    IReadOnlyList<EwelinkDeviceView> Devices, IReadOnlyList<EwelinkDeviceView> Unassigned);

internal sealed class EwelinkGate : SemaphoreSlim
{
    public EwelinkGate() : base(1, 1) { }
}

internal sealed class EwelinkVault(IDataProtectionProvider provider)
{
    private IDataProtector For(string owner, string appId) => provider.CreateProtector("HomeAutomation.Ewelink.Tokens.v1", owner, appId);
    public byte[] Seal(string owner, string appId, EwelinkTokens tokens) =>
        For(owner, appId).Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));
    public EwelinkTokens Open(string owner, string appId, byte[] envelope)
    {
        try
        {
            return JsonSerializer.Deserialize<EwelinkTokens>(For(owner, appId).Unprotect(envelope)) ??
                throw new EwelinkFailure("reauthorizationRequired");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new EwelinkFailure("reauthorizationRequired");
        }
    }
}

public sealed class EwelinkService
{
    private readonly EwelinkOptions options;
    private readonly EwelinkClient client;
    private readonly IEwelinkStore store;
    private readonly EwelinkGate gate;
    private readonly EwelinkVault vault;
    private readonly IMonitoringReader permissions;
    private readonly TelemetryWriter writer;
    private readonly TimeProvider clock;

    internal EwelinkService(EwelinkOptions options, EwelinkClient client, IEwelinkStore store, EwelinkGate gate,
        EwelinkVault vault, IMonitoringReader permissions, TelemetryWriter writer, TimeProvider clock)
    {
        this.options = options;
        this.client = client;
        this.store = store;
        this.gate = gate;
        this.vault = vault;
        this.permissions = permissions;
        this.writer = writer;
        this.clock = clock;
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<EwelinkGate>();
        services.AddSingleton<EwelinkPace>();
        services.AddSingleton<EwelinkVault>();
        services.AddScoped<IEwelinkStore, EwelinkStore>();
        services.AddHttpClient<EwelinkClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(10);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddScoped(provider => new EwelinkService(provider.GetRequiredService<EwelinkOptions>(),
            provider.GetRequiredService<EwelinkClient>(), provider.GetRequiredService<IEwelinkStore>(), provider.GetRequiredService<EwelinkGate>(),
            provider.GetRequiredService<EwelinkVault>(), provider.GetRequiredService<IMonitoringReader>(),
            provider.GetRequiredService<TelemetryWriter>(), provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<EwelinkWorker>();
    }

    public bool IsSupportedRegion(string? region) => EwelinkClient.Host(region) is not null;
    public string AuthorizationUrl(string state) => client.Authorize(state);
    public Task<bool> SchemaReadyAsync(CancellationToken ct) => store.SchemaReadyAsync(ct);

    public async Task<EwelinkView> ViewAsync(string user, long property, bool manage, CancellationToken ct)
    {
        if (!options.IsConfigured) return new("notConfigured", false, null, null, [], []);
        if (!await SchemaReadyAsync(ct)) return new("waitingForSchema", false, null, null, [], []);
        var account = await store.AccountAsync(ct);
        if (account is null) return new("disconnected", manage, null, null, [], []);
        manage &= user == account.Owner;
        var rows = await store.DevicesAsync(account.Owner, ct);
        var reachable = account.Envelope is not null && account.Status != "reauthorizationRequired";
        var assigned = rows.Where(row => row.PropertyId == property).Select(row => View(row, reachable)).ToArray();
        if (!manage && assigned.Length == 0) return new("unavailableForProperty", false, null, null, [], []);
        return new(account.Status, manage, Utc(account.LastSuccess), manage ? Utc(account.NextSync) : null, assigned,
            manage ? rows.Where(row => row.PropertyId is null).Select(row => View(row, reachable)).ToArray() : []);
    }

    private EwelinkDeviceView View(EwelinkRow row, bool authorized)
    {
        var device = row.Device;
        var now = clock.GetUtcNow();
        var freshness = TimeSpan.FromMinutes(options.PollMinutes * 2 + 5);
        var quality = authorized ? Telemetry.Quality(device.SeenAt, now, freshness) : ReadingQuality.Stale;
        var relayQuality = device.RelayAt is null ? ReadingQuality.Missing :
            quality == ReadingQuality.Fresh && device.Connectivity == Connectivity.Online && device.RelayAt == device.SeenAt
                ? ReadingQuality.Fresh : ReadingQuality.Stale;
        var measurements = device.Measurements?.Select(item => new MetricReading(item.Key, Telemetry.Unit(item.Key),
            item.Value.ReceivedAt, item.Value.Value, null, null,
            quality == ReadingQuality.Fresh && device.Connectivity == Connectivity.Online && item.Value.Present
                ? Telemetry.Quality(item.Value.ReceivedAt, now, freshness) : ReadingQuality.Stale)).ToArray() ?? [];
        return new(row.Id, device.Name, device.Model, row.RoomId,
            quality == ReadingQuality.Fresh ? device.Connectivity : Connectivity.Unknown, device.SeenAt, quality,
            device.HasRelay, device.Relay, device.RelayAt, relayQuality, measurements, device.SupportedMetrics());
    }

    public async Task ConnectAsync(string owner, string code, string region, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var saved = await store.AccountAsync(ct);
            if (saved is not null && saved.Owner != owner) throw new EwelinkFailure("forbidden");
            if (saved?.Status is "rateLimited" or "quotaExceeded" or "accessOrQuotaLimited" &&
                Utc(saved.NextSync) > clock.GetUtcNow()) throw new EwelinkFailure(saved.Status);
            ct.ThrowIfCancellationRequested();
            var tokens = await client.ExchangeAsync(code, region, CancellationToken.None);
            await store.SaveTokensAsync(owner, vault.Seal(owner, options.AppId!, tokens), CancellationToken.None);
            if (saved?.NextSync is null || Utc(saved.NextSync) <= clock.GetUtcNow())
                await SyncAccountAsync(owner, tokens, ct);
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(string owner, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if ((await store.AccountAsync(ct))?.Owner != owner) throw new EwelinkFailure("forbidden");
            await store.SaveTokensAsync(owner, null, ct);
        }
        finally { gate.Release(); }
    }

    public async Task AssignAsync(string owner, long property, string id, long? room, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if ((await store.AccountAsync(ct))?.Owner != owner ||
                !await permissions.CanReadAsync(owner, property, ct)) throw new EwelinkFailure("forbidden");
            var row = (await store.DevicesAsync(owner, ct)).SingleOrDefault(item =>
                item.Id == id && (item.PropertyId is null || item.PropertyId == property));
            if (row is null) throw new EwelinkFailure("notFound");
            await store.AssignAsync(row, owner, property, room, ct);
            await IngestAsync(property, room, row.Device, includeRelay: true, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SyncAsync(string? owner, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var account = await store.AccountAsync(ct);
            if (owner is not null && account?.Owner != owner) throw new EwelinkFailure("forbidden");
            if (account?.Envelope is null || account.Status == "reauthorizationRequired")
            {
                if (owner is not null) throw new EwelinkFailure(account?.Status ?? "disconnected");
                return;
            }
            if (Utc(account.NextSync) > clock.GetUtcNow())
            {
                if (owner is not null) throw new EwelinkFailure("syncDeferred", Utc(account.NextSync));
                return;
            }
            EwelinkTokens tokens;
            try { tokens = vault.Open(account.Owner, options.AppId!, account.Envelope); }
            catch (EwelinkFailure error)
            {
                await ReportFailureAsync(account.Owner, error, ct);
                throw;
            }
            await SyncAccountAsync(account.Owner, tokens, ct);
        }
        finally { gate.Release(); }
    }

    private async Task SyncAccountAsync(string owner, EwelinkTokens tokens, CancellationToken ct)
    {
        try
        {
            await store.ReportAsync(owner, "syncing", null, clock.GetUtcNow().AddMinutes(options.PollMinutes), ct);
            var refreshed = tokens.AccessExpires <= clock.GetUtcNow().AddMinutes(5);
            if (refreshed) tokens = await RefreshAsync(owner, tokens, ct);
            EwelinkInventory inventory;
            try { inventory = await client.DiscoverAsync(tokens, ct); }
            catch (EwelinkFailure error) when (error.Status == "reauthorizationRequired" && !refreshed)
            {
                tokens = await RefreshAsync(owner, tokens, ct);
                inventory = await client.DiscoverAsync(tokens, ct);
            }
            var previous = (await store.DevicesAsync(owner, ct)).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
            foreach (var device in inventory.Devices)
            {
                previous.TryGetValue(device.ExternalId, out var row);
                var snapshot = EwelinkMapping.Merge(device, row?.Device);
                if (row?.PropertyId is { } property && await permissions.CanReadAsync(owner, property, ct))
                    await IngestAsync(property, row.RoomId, snapshot, device.RelayAt is not null, ct);
                await store.SaveAsync(owner, snapshot, ct);
            }
            var now = clock.GetUtcNow();
            await store.ReportAsync(owner, inventory.Partial ? "partial" : "connected", now, now.AddMinutes(options.PollMinutes), ct);
        }
        catch (EwelinkFailure error)
        {
            await ReportFailureAsync(owner, error, ct);
            throw;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var safe = new EwelinkFailure("unavailable");
            await ReportFailureAsync(owner, safe, ct);
            throw safe;
        }
    }

    private async Task<EwelinkTokens> RefreshAsync(string owner, EwelinkTokens current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tokens = await client.RefreshAsync(current, CancellationToken.None);
        await store.SaveTokensAsync(owner, vault.Seal(owner, options.AppId!, tokens), CancellationToken.None);
        return tokens;
    }

    private Task ReportFailureAsync(string owner, EwelinkFailure error, CancellationToken ct)
    {
        var minimum = clock.GetUtcNow().AddMinutes(options.PollMinutes);
        return store.ReportAsync(owner, error.Status, null, error.RetryAt > minimum ? error.RetryAt.Value : minimum, ct);
    }

    private Task IngestAsync(long property, long? room, EwelinkDevice device, bool includeRelay, CancellationToken ct)
    {
        var (inventory, batch) = device.Telemetry(includeRelay);
        return writer.StoreCloudDeviceAsync("ewelink", property, room, inventory, batch, ct);
    }
}

internal sealed class EwelinkWorker(IServiceScopeFactory scopes, EwelinkOptions options, TimeProvider clock,
    ILogger<EwelinkWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.IsConfigured) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var integration = scope.ServiceProvider.GetRequiredService<EwelinkService>();
                if (await integration.SchemaReadyAsync(ct)) await integration.SyncAsync(null, ct);
            }
            catch (EwelinkFailure error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("eWeLink synchronization deferred with {Status}.", error.Status);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("eWeLink synchronization deferred with {ErrorType}.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
