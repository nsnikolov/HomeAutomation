using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.Extensions.DependencyInjection;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Daikin;

public sealed record DaikinDeviceView(string Id, string Name, string Model, long? RoomId, Connectivity Connectivity,
    DateTimeOffset ObservedAt, DateTimeOffset? SourceAt, ReadingQuality Quality, string? Power, string? Mode,
    decimal? Indoor, decimal? Outdoor, decimal? Target, decimal? Humidity, decimal? Fan, string? FanMode,
    string? Fault, DaikinControls Controls, DaikinOutcome? Command);

public sealed record DaikinView(string Status, DateTimeOffset? LastSuccess, bool Manage, bool Writes,
    IReadOnlyList<DaikinDeviceView> Devices, IReadOnlyList<DaikinDeviceView> Unassigned);

internal sealed class DaikinGate : SemaphoreSlim
{
    public DaikinGate() : base(1, 1) { }
}

public sealed class DaikinService
{
    private readonly DaikinOptions options;
    private readonly DaikinStore store;
    private readonly DaikinClient client;
    private readonly DaikinSecrets secrets;
    private readonly DaikinGate gate;
    private readonly TelemetryWriter telemetry;
    private readonly IMonitoringReader permissions;
    private readonly TimeProvider clock;

    internal DaikinService(DaikinOptions options, DaikinStore store, DaikinClient client, DaikinSecrets secrets,
        DaikinGate gate, TelemetryWriter telemetry, IMonitoringReader permissions, TimeProvider clock)
    {
        this.options = options;
        this.store = store;
        this.client = client;
        this.secrets = secrets;
        this.gate = gate;
        this.telemetry = telemetry;
        this.permissions = permissions;
        this.clock = clock;
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<DaikinGate>();
        services.AddScoped<DaikinStore>();
        services.AddScoped<DaikinSecrets>();
        services.AddHttpClient<DaikinClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(15);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddScoped(provider => new DaikinService(provider.GetRequiredService<DaikinOptions>(), provider.GetRequiredService<DaikinStore>(),
            provider.GetRequiredService<DaikinClient>(), provider.GetRequiredService<DaikinSecrets>(), provider.GetRequiredService<DaikinGate>(),
            provider.GetRequiredService<TelemetryWriter>(), provider.GetRequiredService<IMonitoringReader>(), provider.GetRequiredService<TimeProvider>()));
    }

    public string AuthorizationUrl(string state, string? challenge) => client.Authorize(state, challenge);

    public async Task<DaikinView> ViewAsync(string user, long property, bool manage, CancellationToken ct)
    {
        if (!options.IsConfigured) return new("notConfigured", null, false, false, [], []);
        var account = await store.AccountAsync(ct);
        if (account is null) return new("disconnected", null, manage, false, [], []);
        manage &= account.Owner == user;
        var devices = new List<DaikinDeviceView>();
        var unassigned = new List<DaikinDeviceView>();
        foreach (var row in await store.DevicesAsync(account.Owner, ct))
        {
            if (row.PropertyId != property && (row.PropertyId is not null || !manage)) continue;
            if (row.Device is not { } device) continue;
            var quality = Quality(device);
            var view = new DaikinDeviceView(row.Id, device.Name, device.Model, row.RoomId,
                quality == ReadingQuality.Fresh ? device.Connectivity : Connectivity.Unknown,
                device.ObservedAt, device.SourceAt, quality, device.Power, device.Mode, device.Indoor, device.Outdoor,
                device.Target, device.Humidity, device.Fan, device.FanMode, device.Fault, device.Controls,
                row.PropertyId is not null ? await store.LatestCommandAsync(row.Id, ct) : null);
            (row.PropertyId is null ? unassigned : devices).Add(view);
        }
        return new(manage || devices.Count > 0 ? account.Status : "unavailableForProperty",
            manage || devices.Count > 0 ? Utc(account.LastSuccess) : null, manage,
            manage && options.EnableWrites && account.Envelope is not null && account.Status == "connected", devices, unassigned);
    }

    public async Task ConnectAsync(string user, string code, string? verifier, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await store.ClaimAsync(user, ct);
            var account = await store.AccountAsync(ct) ?? throw new DaikinFailure("disconnected");
            CheckBackoff(account);
            await store.ReserveAsync(false, ct);
            var tokens = await client.TokenAsync("authorization_code", code, verifier, ct);
            await store.ReserveAsync(false, ct);
            var devices = await client.DiscoverAsync(tokens.AccessToken, ct);
            var known = await store.DevicesAsync(user, ct);
            // Do not attach another ONECTA inventory to existing property assignments.
            if (known.Any(row => row.PropertyId is not null) &&
                !devices.Any(device => known.Any(row => row.PropertyId is not null && row.ExternalId == device.ExternalId)))
                throw new DaikinFailure("inventoryMismatch");
            await store.SaveTokensAsync(user, secrets.Seal(user, tokens), "syncing", ct);
            await PersistAsync(user, devices, ct);
        }
        catch (DaikinFailure error)
        {
            var account = await store.AccountAsync(ct);
            if (account?.Owner == user) await RecordFailureAsync(error, ct);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(string user, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if ((await store.AccountAsync(ct))?.Owner != user) throw new DaikinFailure("forbidden");
            await store.SaveTokensAsync(user, null, "disconnected", ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> AssignAsync(string user, string device, long property, long? room, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if ((await store.AccountAsync(ct))?.Owner != user) throw new DaikinFailure("forbidden");
            if (!await store.AssignAsync(user, device, property, room, ct)) return false;
            var row = (await store.DevicesAsync(user, ct)).Single(item => item.Id == device);
            if (row.Device is { } current) await StoreTelemetryAsync(property, room, current, ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task PollAsync(string? user, bool background, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var account = await store.AccountAsync(ct);
            if (user is not null && account?.Owner != user) throw new DaikinFailure("forbidden");
            if (account?.Envelope is null || account.Status == "reauthorizationRequired")
            {
                if (background) return;
                throw new DaikinFailure(account?.Status ?? "disconnected");
            }
            CheckBackoff(account);
            await PollAccountAsync(account, background, ct);
        }
        finally { gate.Release(); }
    }

    private async Task PollAccountAsync(DaikinAccount account, bool background, CancellationToken ct)
    {
        try
        {
            var tokens = await TokensAsync(account, background, force: false, ct);
            await store.ReserveAsync(background, ct);
            IReadOnlyList<DaikinDevice> devices;
            try { devices = await client.DiscoverAsync(tokens.AccessToken, ct); }
            catch (DaikinFailure error) when (error.Status == "reauthorizationRequired")
            {
                tokens = await TokensAsync(account with { Envelope = secrets.Seal(account.Owner, tokens) }, background, force: true, ct);
                await store.ReserveAsync(background, ct);
                devices = await client.DiscoverAsync(tokens.AccessToken, ct);
            }
            await PersistAsync(account.Owner, devices, ct);
        }
        catch (DaikinFailure error)
        {
            if (error.Status != "pollBudgetReserved") await RecordFailureAsync(error, ct);
            throw;
        }
        catch (HttpRequestException)
        {
            await RecordFailureAsync(new("unavailable"), ct);
            throw new DaikinFailure("unavailable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(new("unavailable"), ct);
            throw new DaikinFailure("unavailable");
        }
    }

    public async Task<DaikinOutcome> CommandAsync(string user, long property, string id, DaikinCommand command, CancellationToken ct)
    {
        if (!options.EnableWrites) throw new DaikinFailure("readOnly");
        await gate.WaitAsync(ct);
        try
        {
            var account = await store.AccountAsync(ct);
            if (account?.Owner != user) throw new DaikinFailure("forbidden");
            if (account.Envelope is null || account.Status != "connected") throw new DaikinFailure("reauthorizationRequired");
            CheckBackoff(account);
            var row = (await store.DevicesAsync(user, ct)).SingleOrDefault(item => item.Id == id && item.PropertyId == property);
            if (row?.Device is not { } device) throw new DaikinFailure("notFound");
            if (device.SourceAt is null || Quality(device) != ReadingQuality.Fresh || device.Connectivity != Connectivity.Online)
                throw new DaikinFailure("deviceUnavailable");
            var change = DaikinMapping.Validate(device, command) ?? throw new DaikinFailure("unsupportedCommand");
            if ((await store.LatestCommandAsync(id, ct))?.Status is "pending" or "uncertain")
                throw new DaikinFailure("unresolvedCommand");
            var tokens = change.AlreadyApplied ? null : await TokensAsync(account, false, force: false, ct);
            if (tokens is not null) await store.ReserveAsync(false, ct);
            var audit = await store.StartCommandAsync(user, id, property, command, ct);
            var status = change.AlreadyApplied ? "alreadyApplied" : "uncertain";
            if (tokens is not null)
            {
                try { await client.PatchAsync(tokens.AccessToken, device, change, ct); }
                catch (DaikinFailure error)
                {
                    status = error.Status == "unavailable" ? "uncertain" : "failed";
                    if (error.Status is "rateLimited" or "reauthorizationRequired")
                        await RecordFailureAsync(error, CancellationToken.None);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
            await store.FinishCommandAsync(audit, status, CancellationToken.None);
            return new(audit, command.Operation, command.Value, status, clock.GetUtcNow());
        }
        finally { gate.Release(); }
    }

    private async Task<DaikinTokens> TokensAsync(DaikinAccount account, bool background, bool force, CancellationToken ct)
    {
        var current = secrets.Open(account.Owner, account.Envelope!);
        if (!force && !current.NeedsRefresh(clock.GetUtcNow())) return current;
        await store.ReserveAsync(background, ct);
        var refreshed = await client.TokenAsync("refresh_token", current.RefreshToken, null, ct);
        // Persist a rotated refresh token before making any further provider calls.
        await store.SaveTokensAsync(account.Owner, secrets.Seal(account.Owner, refreshed), account.Status, ct);
        return refreshed;
    }

    private async Task PersistAsync(string owner, IReadOnlyList<DaikinDevice> devices, CancellationToken ct)
    {
        var bindings = (await store.DevicesAsync(owner, ct)).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
        foreach (var device in devices)
        {
            bindings.TryGetValue(device.ExternalId, out var assigned);
            if (assigned?.Device is { } previous && device.ObservedAt < previous.ObservedAt) continue;
            if (assigned?.PropertyId is { } property && !await permissions.CanReadAsync(owner, property, ct)) continue;
            if (assigned?.PropertyId is { } propertyId)
            {
                await StoreTelemetryAsync(propertyId, assigned.RoomId, device, ct);
                var last = await store.LatestCommandAsync(assigned.Id, ct);
                if (last?.Status is "pending" or "uncertain" && device.ObservedAt > last.UpdatedAt &&
                    device.Connectivity == Connectivity.Online && Quality(device) == ReadingQuality.Fresh)
                {
                    if (DaikinMapping.Validate(device, new(last.Operation, last.Value))?.AlreadyApplied == true)
                        await store.FinishCommandAsync(last.Id, "verified", ct);
                    else if (last.Status == "pending")
                        await store.FinishCommandAsync(last.Id, "uncertain", ct);
                }
            }
            await store.SaveSnapshotAsync(owner, device, ct);
        }
        await store.RecordStatusAsync("connected", null, ct);
    }

    private Task StoreTelemetryAsync(long property, long? room, DaikinDevice device, CancellationToken ct)
    {
        var at = device.ObservedAt;
        var readings = new List<Sample>();
        var capabilities = new HashSet<Capability>();
        Add(device.Indoor, Metric.Temperature, Capability.TemperatureMeasurement);
        Add(device.Outdoor, Metric.OutdoorTemperature, Capability.TemperatureMeasurement);
        Add(device.Target, Metric.TargetTemperature, Capability.HeatingSetpoint);
        Add(device.Humidity, Metric.Humidity, Capability.HumidityMeasurement);
        Add(device.Fan, Metric.FanSpeed, Capability.FanSpeed);
        if (device.Power is "on" or "off")
        {
            capabilities.Add(Capability.Switch);
            readings.Add(Sample.Boolean(Metric.SwitchOn, at, device.Power == "on"));
        }
        if (device.Mode is { } mode)
        {
            capabilities.Add(Capability.OperatingMode);
            readings.Add(Sample.Status(Metric.OperatingMode, at, mode));
        }
        if (device.Fault is not null) capabilities.Add(Capability.FaultStatus);
        var discovery = new DiscoveredDevice(device.ExternalId, device.Name, DeviceKind.AirConditioner, "", "Daikin",
            device.Model, [new("climate", "Climate", capabilities)]);
        var batch = new ObservationBatch(at, [new(device.ExternalId, device.Connectivity, device.Fault, [new("climate", readings)], at)], []);
        return telemetry.StoreDaikinAsync(property, room, discovery, batch, ct);

        void Add(decimal? value, Metric metric, Capability capability)
        {
            if (value is null) return;
            capabilities.Add(capability);
            readings.Add(Sample.Numeric(metric, at, (double)value.Value));
        }
    }

    private ReadingQuality Quality(DaikinDevice device) => Telemetry.Quality(device.ObservedAt, clock.GetUtcNow(),
        TimeSpan.FromMinutes(options.FreshMinutes));

    private void CheckBackoff(DaikinAccount account)
    {
        if (Utc(account.RetryAfter) is { } until && until > clock.GetUtcNow())
            throw new DaikinFailure("rateLimited", until);
    }

    private Task RecordFailureAsync(DaikinFailure error, CancellationToken ct) => store.RecordStatusAsync(error.Status,
        error.RetryAt ?? (error.Status == "rateLimited" ? clock.GetUtcNow().AddMinutes(15) : null), ct);
}
