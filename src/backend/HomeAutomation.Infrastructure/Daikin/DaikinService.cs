using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Daikin;

public sealed record DaikinDeviceView(string Id, string Name, string Model, long? RoomId, Connectivity Connectivity,
    DateTimeOffset ObservedAt, DateTimeOffset? SourceAt, ReadingQuality Quality, string? Power, string? Mode,
    decimal? Indoor, decimal? Outdoor, decimal? Target, decimal? Humidity, decimal? Fan, string? FanMode,
    string? Fault, DaikinControls Controls, DaikinOutcome? Command, DaikinEquipment Equipment, DaikinWater? Water,
    DateTimeOffset? ProviderReceivedAt, IReadOnlyList<DaikinOutcome> OpenCommands);

public sealed record DaikinHistorySeries(Metric Metric, IReadOnlyList<HistoryPoint> Points);
public sealed record DaikinWaterHistory(int PollSeconds, IReadOnlyList<DaikinHistorySeries> Series);

public sealed record DaikinView(string Status, DateTimeOffset? LastSuccess, bool Manage, bool Writes,
    IReadOnlyList<DaikinDeviceView> Devices, IReadOnlyList<DaikinDeviceView> Unassigned);

internal sealed class DaikinGate : SemaphoreSlim
{
    public DaikinGate() : base(1, 1) { }
}

public sealed class DaikinService
{
    private static readonly TimeSpan ManualReadAge = TimeSpan.FromMinutes(2);
    private readonly DaikinOptions options;
    private readonly DaikinStore store;
    private readonly DaikinClient client;
    private readonly DaikinSecrets secrets;
    private readonly DaikinGate gate;
    private readonly DaikinConfirmationSchedule confirmation;
    private readonly TelemetryWriter telemetry;
    private readonly IMonitoringReader permissions;
    private readonly TimeProvider clock;
    private readonly ILogger<DaikinService> logger;
    private readonly IMonitoringDashboardNotifier dashboard;

    internal DaikinService(DaikinOptions options, DaikinStore store, DaikinClient client, DaikinSecrets secrets,
        DaikinGate gate, DaikinConfirmationSchedule confirmation, TelemetryWriter telemetry, IMonitoringReader permissions,
        TimeProvider clock, ILogger<DaikinService> logger, IMonitoringDashboardNotifier dashboard)
    {
        this.options = options;
        this.store = store;
        this.client = client;
        this.secrets = secrets;
        this.gate = gate;
        this.confirmation = confirmation;
        this.telemetry = telemetry;
        this.permissions = permissions;
        this.clock = clock;
        this.logger = logger;
        this.dashboard = dashboard;
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<DaikinGate>();
        services.AddSingleton<DaikinConfirmationSchedule>();
        services.AddHostedService<DaikinConfirmationWorker>();
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
            provider.GetRequiredService<DaikinConfirmationSchedule>(), provider.GetRequiredService<TelemetryWriter>(),
            provider.GetRequiredService<IMonitoringReader>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<DaikinService>>(), provider.GetRequiredService<IMonitoringDashboardNotifier>()));
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
            var commands = row.PropertyId is null ? [] : await store.CommandsAsync(row.Id, ct, includeLatest: true);
            var view = new DaikinDeviceView(row.Id, device.Name, device.Model, row.RoomId,
                quality == ReadingQuality.Fresh ? device.Connectivity : Connectivity.Unknown,
                device.ObservedAt, device.SourceAt, quality, device.Power, device.Mode, device.Indoor, device.Outdoor,
                device.Target, device.Humidity, device.Fan, device.FanMode, device.Fault,
                device.Equipment == DaikinEquipment.AirConditioner ? device.Controls : DaikinMapping.ReadOnlyControls,
                commands.FirstOrDefault(), device.Equipment, device.Water, device.ProviderReceivedAt,
                commands.Where(command => DaikinCommandStatus.Open.Contains(command.Status)).ToArray());
            (row.PropertyId is null ? unassigned : devices).Add(view);
        }
        return new(manage || devices.Count > 0 ? account.Status : "unavailableForProperty",
            manage || devices.Count > 0 ? Utc(account.LastSuccess) : null, manage,
            manage && options.EnableWrites && account.Envelope is not null && account.Status == "connected", devices, unassigned);
    }

    public Task<DaikinWaterHistory> WaterHistoryAsync(long property, string id, CancellationToken ct) =>
        store.WaterHistoryAsync(property, id, ct);

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
            await dashboard.ChangedAsync(property, ct);
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

    internal async Task ConfirmDueAsync(Migrations migrations, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var ids = confirmation.TakeDue();
            if (ids.Length == 0 || !await migrations.VerifyAsync(ct, includeDaikin: true)) return;
            var account = await store.AccountAsync(ct);
            if (account?.Envelope is null || account.Status == "reauthorizationRequired" ||
                !await store.HasAwaitingAsync(account.Owner, ids, ct)) return;

            CheckBackoff(account);
            await PollAccountAsync(account, background: true, ct, confirmationOnly: true);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<DaikinDevice>> PollAccountAsync(DaikinAccount account, bool background, CancellationToken ct,
        ManualCalls? manual = null, bool confirmationOnly = false)
    {
        try
        {
            var tokens = await TokensAsync(account, background, force: false, ct, manual);
            await ReserveAsync(background, ct, manual);
            if (manual is not null) manual.Reads++;
            IReadOnlyList<DaikinDevice> devices;
            try { devices = await client.DiscoverAsync(tokens.AccessToken, ct); }
            catch (DaikinFailure error) when (!confirmationOnly && error.Status == "reauthorizationRequired")
            {
                tokens = await TokensAsync(account with { Envelope = secrets.Seal(account.Owner, tokens) }, background, force: true, ct, manual);
                await ReserveAsync(background, ct, manual);
                if (manual is not null) manual.Reads++;
                devices = await client.DiscoverAsync(tokens.AccessToken, ct);
            }
            await PersistAsync(account.Owner, devices, ct);
            return devices;
        }
        catch (DaikinFailure error)
        {
            if (error.Status != "pollBudgetReserved" && !(manual is not null && error.Status == "manualBudgetReserved") &&
                !(confirmationOnly && error.Status == "reauthorizationRequired"))
                await RecordFailureAsync(error, ct);
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
        if (!await permissions.CanReadAsync(user, property, ct)) throw new DaikinFailure("notFound");
        await gate.WaitAsync(ct);
        var manual = new ManualCalls();
        string? audit = null;
        try
        {
            var account = await store.AccountAsync(ct);
            if (account?.Owner != user) throw new DaikinFailure("forbidden");
            if (account.Envelope is null || account.Status != "connected") throw new DaikinFailure("reauthorizationRequired");
            CheckBackoff(account);
            var row = (await store.DevicesAsync(user, ct)).SingleOrDefault(item => item.Id == id && item.PropertyId == property);
            if (row?.Device is not { } device) throw new DaikinFailure("notFound");
            if (device.Equipment != DaikinEquipment.AirConditioner) throw new DaikinFailure("unsupportedCommand");
            if ((await store.CommandsAsync(id, ct)).Any(open => DaikinCommandStatus.Blocks(open, command.Operation)))
                throw new DaikinFailure("unresolvedCommand");
            if (!RecentlyRead(device, clock.GetUtcNow()))
            {
                var fresh = await PollAccountAsync(account, false, ct, manual);
                var previousSource = device.SourceAt;
                device = fresh.SingleOrDefault(candidate => candidate.ExternalId == row.ExternalId)
                    ?? throw new DaikinFailure("deviceUnavailable");
                if (device.SourceAt is { } source && previousSource is { } previous && source < previous)
                    throw new DaikinFailure("deviceUnavailable");
                account = await store.AccountAsync(ct) ?? throw new DaikinFailure("disconnected");
            }
            if (device.Equipment != DaikinEquipment.AirConditioner) throw new DaikinFailure("unsupportedCommand");
            if (!CanUseForManualCommand(device, clock.GetUtcNow(), TimeSpan.FromMinutes(options.FreshMinutes)))
                throw new DaikinFailure("deviceUnavailable");
            var change = ResolveManualChange(device, command);
            var tokens = change.AlreadyApplied ? null : await TokensAsync(account, false, force: false, ct, manual);
            if (!await permissions.CanReadAsync(user, property, ct)) throw new DaikinFailure("notFound");
            if (tokens is not null) await ReserveAsync(false, ct, manual);
            if (!CanUseForManualCommand(device, clock.GetUtcNow(), TimeSpan.FromMinutes(options.FreshMinutes)))
                throw new DaikinFailure("deviceUnavailable");
            var modeContext = command.Operation is "temperature" or "fan" ? device.Mode : null;
            audit = await store.StartCommandAsync(user, id, property, command, modeContext, ct);
            var status = change.AlreadyApplied ? "alreadyApplied" : DaikinCommandStatus.Awaiting;
            if (tokens is not null)
            {
                manual.Writes++;
                try { await client.PatchAsync(tokens.AccessToken, device, change, ct); }
                catch (DaikinFailure error)
                {
                    status = error.Status == "unavailable" ? DaikinCommandStatus.Uncertain : "failed";
                    if (error.Status is "rateLimited" or "reauthorizationRequired")
                        await RecordFailureAsync(error, CancellationToken.None);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { status = DaikinCommandStatus.Uncertain; }
            }
            await store.FinishCommandAsync(audit, status, CancellationToken.None);
            if (status == DaikinCommandStatus.Awaiting) confirmation.Schedule(audit);
            return new(audit, command.Operation, command.Value, status, clock.GetUtcNow(), modeContext);
        }
        finally
        {
            gate.Release();
            if (audit is not null || manual.Reads + manual.Writes + manual.Refreshes > 0)
                logger.LogInformation("ONECTA manual command {AuditId} used {Reads} GET, {Writes} PATCH and {Refreshes} token requests.",
                    audit, manual.Reads, manual.Writes, manual.Refreshes);
        }
    }

    private async Task ReserveAsync(bool background, CancellationToken ct, ManualCalls? manual)
    {
        var retained = manual is null ? 0 : options.ManualCommandHeadroom;
        if (options.DailyLimit <= retained)
            throw new DaikinFailure("manualBudgetReserved");
        try
        {
            await store.ReserveAsync(background, ct, retain: retained);
        }
        catch (DaikinFailure error) when (manual is not null && error.Status == "rateLimited")
        {
            throw new DaikinFailure("manualBudgetReserved", error.RetryAt);
        }
    }

    private sealed class ManualCalls
    {
        public int Reads { get; set; }
        public int Writes { get; set; }
        public int Refreshes { get; set; }
    }

    private async Task<DaikinTokens> TokensAsync(DaikinAccount account, bool background, bool force, CancellationToken ct,
        ManualCalls? manual = null)
    {
        var current = secrets.Open(account.Owner, account.Envelope!);
        if (!force && !current.NeedsRefresh(clock.GetUtcNow())) return current;
        await ReserveAsync(background, ct, manual);
        if (manual is not null) manual.Refreshes++;
        var refreshed = await client.TokenAsync("refresh_token", current.RefreshToken, null, ct);
        // Persist a rotated refresh token before making any further provider calls.
        await store.SaveTokensAsync(account.Owner, secrets.Seal(account.Owner, refreshed), account.Status, ct);
        return refreshed;
    }

    private async Task PersistAsync(string owner, IReadOnlyList<DaikinDevice> devices, CancellationToken ct)
    {
        var bindings = (await store.DevicesAsync(owner, ct)).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
        var reported = devices.Select(device => device.ExternalId).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in bindings.Values)
            if (!reported.Contains(binding.ExternalId) && binding.Device is { Available: true } previous)
                await store.SaveSnapshotAsync(owner, previous with { Available = false }, ct);
        foreach (var device in devices)
        {
            bindings.TryGetValue(device.ExternalId, out var assigned);
            if (device.SourceAt is { } source && assigned?.Device?.SourceAt is { } previousSource && source < previousSource)
                continue;
            if (assigned?.PropertyId is { } property && !await permissions.CanReadAsync(owner, property, ct)) continue;
            if (assigned?.PropertyId is { } propertyId)
                await StoreTelemetryAsync(propertyId, assigned.RoomId, device, ct);
            await store.SaveSnapshotAsync(owner, device, ct);
            if (assigned?.PropertyId is not null &&
                CanUseForManualCommand(device, clock.GetUtcNow(), TimeSpan.FromMinutes(options.FreshMinutes)))
            {
                foreach (var open in await store.CommandsAsync(assigned.Id, ct))
                {
                    if (MatchesCommand(device, open))
                    {
                        await store.FinishCommandAsync(open.Id, DaikinCommandStatus.Confirmed, ct);
                        confirmation.Resolve(open.Id);
                    }
                }
            }
        }
        await store.RecordStatusAsync("connected", null, ct);
        foreach (var property in bindings.Values.Select(row => row.PropertyId).OfType<long>().Distinct())
            await dashboard.ChangedAsync(property, ct);
    }

    private Task StoreTelemetryAsync(long property, long? room, DaikinDevice device, CancellationToken ct)
    {
        var (discovery, batch) = TelemetryFor(device);
        return telemetry.StoreDaikinAsync(property, room, discovery, batch, ct);
    }

    private static bool RecentlyRead(DaikinDevice device, DateTimeOffset now) =>
        device.Available == true && !device.CachedResponse &&
        device.ProviderReceivedAt is { } received && received <= now && now - received <= ManualReadAge;

    internal static bool CanUseForManualCommand(DaikinDevice device, DateTimeOffset now, TimeSpan sourceLifetime) =>
        device.Equipment == DaikinEquipment.AirConditioner && RecentlyRead(device, now) &&
        device.Connectivity == Connectivity.Online && !device.InvalidSourceTime &&
        (device.SourceAt is not { } source || source <= now && now - source <= sourceLifetime);

    internal static DaikinChange ResolveManualChange(DaikinDevice device, DaikinCommand command)
    {
        if (command.Operation is "temperature" or "fan" &&
            (command.ExpectedMode is null || command.ExpectedMode != device.Mode))
            throw new DaikinFailure("modeChanged");
        return DaikinMapping.Validate(device, command) ?? throw new DaikinFailure("unsupportedCommand");
    }

    private static bool IsNewerRead(DaikinDevice device, DaikinOutcome command) =>
        device.ProviderReceivedAt > command.UpdatedAt &&
        (device.SourceAt is not { } source || source > command.UpdatedAt);

    internal static bool MatchesCommand(DaikinDevice device, DaikinOutcome command) =>
        IsNewerRead(device, command) && !device.CachedResponse && !device.InvalidSourceTime && device.Connectivity == Connectivity.Online &&
        (command.Operation is not ("temperature" or "fan") ||
            command.ModeContext is not null && command.ModeContext == device.Mode) &&
        DaikinMapping.Validate(device, new(command.Operation, command.Value))?.AlreadyApplied == true;

    internal static (DiscoveredDevice Device, ObservationBatch Batch) TelemetryFor(DaikinDevice device)
    {
        var at = device.ObservedAt;
        var readings = new List<Sample>();
        var capabilities = new HashSet<Capability>();
        Add(device.Indoor, Metric.Temperature, Capability.TemperatureMeasurement);
        Add(device.Outdoor, Metric.OutdoorTemperature, Capability.TemperatureMeasurement);
        Add(device.Target, Metric.TargetTemperature, Capability.HeatingSetpoint);
        Add(device.Humidity, Metric.Humidity, Capability.HumidityMeasurement);
        Add(device.Fan, Metric.FanSpeed, Capability.FanSpeed);
        if (device.Water is { } water)
        {
            Add(water.Leaving, Metric.LeavingWaterTemperature, Capability.WaterTemperature);
            Add(water.Target, Metric.TargetLeavingWaterTemperature, Capability.WaterTemperatureSetpoint);
            AddStatus(water.ControlMode, Metric.HeatingControlMode, Capability.HeatingControlMode);
            AddStatus(water.SetpointMode, Metric.WaterSetpointMode, Capability.WaterSetpointMode);
            AddFlag(water.Warning, Metric.HeatingWarning, Capability.WarningStatus);
            AddFlag(water.Emergency, Metric.HeatingEmergency, Capability.EmergencyStatus);
        }
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
        var discovery = new DiscoveredDevice(device.ExternalId, device.Name,
            device.Equipment == DaikinEquipment.HeatPump ? DeviceKind.HeatPump : DeviceKind.AirConditioner, "", "Daikin",
            device.Model, [new("climate", "Climate", capabilities)]);
        var batch = new ObservationBatch(at, [new(device.ExternalId, device.Connectivity, device.Fault, [new("climate", readings)], at)], []);
        return (discovery, batch);

        void Add(decimal? value, Metric metric, Capability capability)
        {
            if (value is null) return;
            capabilities.Add(capability);
            readings.Add(Sample.Numeric(metric, at, (double)value.Value));
        }

        void AddStatus(string? value, Metric metric, Capability capability)
        {
            if (value is null) return;
            capabilities.Add(capability);
            readings.Add(Sample.Status(metric, at, value));
        }

        void AddFlag(bool? value, Metric metric, Capability capability)
        {
            if (value is null) return;
            capabilities.Add(capability);
            readings.Add(Sample.Boolean(metric, at, value.Value));
        }
    }

    private ReadingQuality Quality(DaikinDevice device) => device.Available == false ? ReadingQuality.Stale :
        Telemetry.Quality(device.ObservedAt, clock.GetUtcNow(), TimeSpan.FromMinutes(options.FreshMinutes));

    private void CheckBackoff(DaikinAccount account)
    {
        if (Utc(account.RetryAfter) is { } until && until > clock.GetUtcNow())
            throw new DaikinFailure("rateLimited", until);
    }

    private Task RecordFailureAsync(DaikinFailure error, CancellationToken ct) => store.RecordStatusAsync(error.Status,
        error.RetryAt ?? (error.Status == "rateLimited" ? clock.GetUtcNow().AddMinutes(15) : null), ct);
}
