using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Tuya;

public sealed record TuyaReadingView(string Code, Metric Metric, string? Unit, double? Number, bool? Flag,
    DateTimeOffset? ReportedAt, ReadingQuality Quality);
public sealed record TuyaDeviceView(string Id, string Name, string Model, long? RoomId, Connectivity Connectivity,
    DateTimeOffset ReceivedAt, ReadingQuality Quality, bool SpecificationsAvailable, IReadOnlyList<TuyaReadingView> Readings);
public sealed record TuyaView(string Status, bool Manage, DateTimeOffset? LastSuccess, DateTimeOffset? NextSync,
    int? ProviderCode, IReadOnlyList<TuyaDeviceView> Devices, IReadOnlyList<TuyaDeviceView> Unassigned);

public sealed class TuyaService
{
    private static readonly TimeSpan SpecificationLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan SpecificationRetry = TimeSpan.FromHours(1);
    private readonly TuyaOptions options;
    private readonly TuyaSession session;
    private readonly TuyaClient client;
    private readonly TuyaStore store;
    private readonly Migrations migrations;
    private readonly TelemetryWriter writer;
    private readonly IMonitoringReader permissions;
    private readonly TimeProvider clock;

    internal TuyaService(TuyaOptions options, TuyaSession session, TuyaClient client, TuyaStore store, Migrations migrations,
        TelemetryWriter writer, IMonitoringReader permissions, TimeProvider clock)
    {
        this.options = options;
        this.session = session;
        this.client = client;
        this.store = store;
        this.migrations = migrations;
        this.writer = writer;
        this.permissions = permissions;
        this.clock = clock;
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<TuyaSession>();
        services.AddScoped<TuyaStore>();
        services.AddHttpClient<TuyaClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(10);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddScoped(provider => new TuyaService(provider.GetRequiredService<TuyaOptions>(), provider.GetRequiredService<TuyaSession>(),
            provider.GetRequiredService<TuyaClient>(), provider.GetRequiredService<TuyaStore>(), provider.GetRequiredService<Migrations>(),
            provider.GetRequiredService<TelemetryWriter>(), provider.GetRequiredService<IMonitoringReader>(), provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<TuyaWorker>();
    }

    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyAsync(ct, includeTuya: true);

    public async Task<TuyaView> ViewAsync(long property, bool manage, CancellationToken ct)
    {
        if (!options.IsConfigured) return new("notConfigured", false, null, null, null, [], []);
        if (!await SchemaReadyAsync(ct)) return new("waitingForSchema", false, null, null, null, [], []);
        var status = await store.StatusAsync(ct);
        var rows = await store.DevicesAsync(ct);
        var assigned = rows.Where(row => row.PropertyId == property).Select(View).ToArray();
        if (!manage && assigned.Length == 0) return new("unavailableForProperty", false, null, null, null, [], []);
        return new(status?.Status ?? "waitingForSync", manage, Utc(status?.LastSuccess),
            manage ? Utc(status?.NextSync) : null, manage ? status?.ProviderCode : null, assigned,
            manage ? rows.Where(row => row.PropertyId is null).Select(View).ToArray() : []);
    }

    private TuyaDeviceView View(TuyaRow row)
    {
        var device = row.Device;
        var now = clock.GetUtcNow();
        var freshness = TimeSpan.FromMinutes(options.PollMinutes * 2 + 5);
        var quality = device.Present ? Telemetry.Quality(device.ReceivedAt, now, freshness) : ReadingQuality.Stale;
        var specifications = HasCurrentSpecification(device, now);
        var readings = device.Readings.ToDictionary(reading => reading.Code, StringComparer.Ordinal);
        var values = device.Points.Select(point =>
        {
            readings.TryGetValue(point.Code, out var reading);
            var readingQuality = reading is null ? ReadingQuality.Missing : !specifications || !reading.InLatestReport ||
                quality != ReadingQuality.Fresh ? ReadingQuality.Stale : Telemetry.Quality(reading.At, now, freshness);
            return new TuyaReadingView(point.Code, point.Metric, Telemetry.Unit(point.Metric),
                reading?.Number, reading?.Flag, reading?.At, readingQuality);
        }).ToArray();
        return new(row.Id, device.Name, device.Model, row.RoomId, quality == ReadingQuality.Fresh ? device.Connectivity : Connectivity.Unknown,
            device.ReceivedAt, quality, specifications, values);
    }

    public async Task AssignAsync(string user, long property, string id, long? room, CancellationToken ct)
    {
        await session.WaitAsync(ct);
        try
        {
            if (!await permissions.CanReadAsync(user, property, ct)) throw new TuyaFailure("notFound");
            var row = (await store.DevicesAsync(ct)).SingleOrDefault(item =>
                item.Id == id && (item.PropertyId is null || item.PropertyId == property));
            if (row is null) throw new TuyaFailure("notFound");
            await store.AssignAsync(row.Id, property, room, ct);
            await IngestAsync(row.Device, property, room, ct, includeLastKnown: true);
        }
        finally { session.Release(); }
    }

    public async Task SyncAsync(bool manual, CancellationToken ct)
    {
        await session.WaitAsync(ct);
        try
        {
            var status = await store.StatusAsync(ct);
            if (Utc(status?.NextSync) > clock.GetUtcNow())
            {
                if (manual) throw new TuyaFailure("syncDeferred", retryAt: Utc(status!.NextSync));
                return;
            }
            await store.ReportAsync("syncing", null, clock.GetUtcNow().AddMinutes(options.PollMinutes), null, ct);
            await PollAsync(ct);
        }
        finally { session.Release(); }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            var inventory = await client.DiscoverAsync(ct);
            var rows = (await store.DevicesAsync(ct)).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
            var previous = rows.ToDictionary(item => item.Key, item => item.Value.Device, StringComparer.Ordinal);
            var now = clock.GetUtcNow();
            var pending = inventory.Devices.Where(seed => SpecificationDue(seed, previous.GetValueOrDefault(seed.Id), now))
                .OrderBy(seed => previous.GetValueOrDefault(seed.Id)?.SpecificationAttemptAt).Take(5)
                .Select(seed => seed.Id).ToHashSet(StringComparer.Ordinal);
            var partial = inventory.Partial;
            int? diagnostic = null;
            foreach (var seed in inventory.Devices)
            {
                rows.TryGetValue(seed.Id, out var row);
                var prior = previous.GetValueOrDefault(seed.Id);
                if (prior?.ProductId != seed.ProductId) prior = null;
                var points = prior?.Points ?? [];
                var attempted = prior?.SpecificationAttemptAt;
                var valid = prior is not null && HasCurrentSpecification(prior, now);
                if (pending.Contains(seed.Id))
                {
                    attempted = clock.GetUtcNow();
                    try
                    {
                        points = await client.SpecificationsAsync(seed.Id, ct);
                        valid = true;
                    }
                    catch (TuyaFailure error) when (error.Status is "deviceUnavailable" or "accessDenied" or "invalidResponse")
                    {
                        diagnostic ??= error.ProviderCode;
                        valid = false;
                    }
                }
                partial |= !valid;
                var device = TuyaMapping.Snapshot(seed, prior, points, attempted, valid);
                if (row?.PropertyId is { } property) await IngestAsync(device, property, row.RoomId, ct);
                await store.SaveAsync(device, ct);
            }
            if (!inventory.Partial)
            {
                var present = inventory.Devices.Select(seed => seed.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var device in previous.Values.Where(device => device.Present && !present.Contains(device.Id)))
                    await store.SaveAsync(device with { Present = false }, ct);
            }
            var finished = clock.GetUtcNow();
            await store.ReportAsync(partial ? "partial" : "connected", finished, finished.AddMinutes(options.PollMinutes), diagnostic, ct);
        }
        catch (TuyaFailure error)
        {
            await RecordFailureAsync(error, ct);
            throw;
        }
        catch (Exception error) when ((error is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            var safe = new TuyaFailure("unavailable");
            await RecordFailureAsync(safe, ct);
            throw safe;
        }
    }

    private static bool HasCurrentSpecification(TuyaDevice device, DateTimeOffset now) => device.SpecificationAvailable &&
        device.SpecificationAttemptAt is { } attempted && now - attempted < SpecificationLifetime;

    private static bool SpecificationDue(TuyaSeed seed, TuyaDevice? device, DateTimeOffset now) =>
        device?.ProductId != seed.ProductId || device.SpecificationAttemptAt is not { } last ||
        now - last >= (device.SpecificationAvailable ? SpecificationLifetime : SpecificationRetry);

    private Task RecordFailureAsync(TuyaFailure failure, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var next = failure.Status switch
        {
            "subscriptionExpired" or "quotaExceeded" => now.AddDays(1),
            "authenticationFailed" or "accessDenied" => now.AddHours(6),
            _ => now.AddMinutes(options.PollMinutes)
        };
        return store.ReportAsync(failure.Status, null, failure.RetryAt > next ? failure.RetryAt.Value : next, failure.ProviderCode, ct);
    }

    private Task IngestAsync(TuyaDevice device, long property, long? room, CancellationToken ct, bool includeLastKnown = false)
    {
        var (inventory, batch) = device.Telemetry(includeLastKnown);
        return writer.StoreCloudDeviceAsync("tuya", property, room, inventory, batch, ct);
    }
}

internal sealed class TuyaWorker(IServiceScopeFactory scopes, TuyaOptions options, TimeProvider clock,
    ILogger<TuyaWorker> logger) : BackgroundService
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
                var integration = scope.ServiceProvider.GetRequiredService<TuyaService>();
                if (await integration.SchemaReadyAsync(ct)) await integration.SyncAsync(false, ct);
            }
            catch (TuyaFailure error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Tuya poll deferred with {Status} ({ProviderCode}).", error.Status, error.ProviderCode);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Tuya poll deferred with {ErrorType}.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
