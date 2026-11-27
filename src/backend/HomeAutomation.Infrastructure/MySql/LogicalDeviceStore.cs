using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed record InitializationChannel(string ChannelKey, long? EndpointId);
public sealed record InitializationItem(long DeviceId, string DeviceName, string Provider, IReadOnlyList<InitializationChannel> Channels,
    int SeriesCount, string? Skipped);
public sealed record InitializationPlan(long PropertyId, bool PropertyExists, IReadOnlyList<InitializationItem> Items)
{
    public IEnumerable<InitializationItem> Work => Items.Where(item => item.Skipped is null);
}

/// <summary>
/// Logical devices map a stable identity to physical devices over half-open intervals. Every mutation locks the property row,
/// so assignment changes of one property are serialized, and validates intervals in the same transaction.
/// </summary>
public sealed class LogicalDeviceStore(Database database, Migrations migrations, TelemetryHistoryReader history, TimeProvider clock)
    : ILogicalDevices
{
    internal const string InitializationActor = "system:initialize";
    internal const string ProvisioningActor = "system:provision";
    internal const int MaximumProvisionedPerRun = 100;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Test seam invoked before a mutation commits.</summary>
    internal Func<CancellationToken, Task>? BeforeCommit { get; set; }

    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyLogicalDevicesAsync(ct);

    public async Task<LogicalDeviceList> ListAsync(long property, string user, bool includeArchived, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var state = await LoadAsync(db, null, property, ct);
        var role = await RoleAsync(db, null, property, user, ct);
        return new(CanManage(role), state.Logical.Values.Where(item => includeArchived || item.ArchivedAt is null)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).Select(item => View(state, item)).ToList());
    }

    public async Task<LogicalDeviceDetail?> GetAsync(long property, string user, long id, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var state = await LoadAsync(db, null, property, ct);
        if (!state.Logical.TryGetValue(id, out var logical)) return null;
        var audit = await db.QueryAsync<AuditRow>(Query("""
            SELECT id AS Id, occurred_at_utc AS OccurredAt, actor AS Actor, action AS Action, channel_key AS ChannelKey,
                   old_device_id AS OldDeviceId, new_device_id AS NewDeviceId, effective_at_utc AS EffectiveAt, reason AS Reason
            FROM logical_device_audit WHERE property_id=@property AND logical_device_id=@id ORDER BY id DESC LIMIT 100
            """, ct, new { property, id }));
        return new(CanManage(await RoleAsync(db, null, property, user, ct)), View(state, logical), audit.Select(row => new LogicalAuditEntry(
            row.Id, Utc(row.OccurredAt), row.Actor, row.Action, row.ChannelKey, row.OldDeviceId, row.NewDeviceId, Utc(row.EffectiveAt),
            row.Reason)).ToList());
    }

    public async Task<IReadOnlyList<ReplacementCandidate>?> CandidatesAsync(long property, long id, string channel, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var state = await LoadAsync(db, null, property, ct);
        if (!state.Logical.ContainsKey(id)) return null;
        var history = state.Assignments.Where(item => item.LogicalDeviceId == id && item.ChannelKey == channel).ToList();
        var reference = history.FirstOrDefault(item => item.ValidTo is null) ?? history.LastOrDefault();
        var referenceCapabilities = reference is null ? [] : Measurements(state.Devices[reference.DeviceId].Capabilities(reference.EndpointId));
        var active = state.Assignments.Where(item => item.ValidTo is null && item.LogicalDeviceId != id).ToList();
        return state.Devices.Values.Where(device => !device.Simulated).Select(device =>
        {
            var capabilities = Measurements(device.Capabilities(null));
            var comparison = LogicalDeviceRules.Compare(referenceCapabilities, capabilities);
            var owner = active.FirstOrDefault(item => item.DeviceId == device.Id);
            return new ReplacementCandidate(device.Id, device.Name, device.Provider, device.Manufacturer, device.Model, device.Kind, device.RoomId,
                device.Endpoints.Select(endpoint => new CandidateEndpoint(endpoint.Id, endpoint.Name, Measurements(endpoint.Capabilities))).ToList(),
                capabilities, comparison.Shared, comparison.Missing, comparison.Added, reference is null || comparison.Compatible,
                device.RequiresEndpoint, owner?.LogicalDeviceId, owner is null ? null : state.Logical[owner.LogicalDeviceId].Name,
                reference?.ValidTo is null && reference?.DeviceId == device.Id, owner is null ? null : state.Logical[owner.LogicalDeviceId].Creation);
        }).OrderByDescending(item => item.Compatible).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<LogicalChangeResult> CreateAsync(long property, string user, CreateLogicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Metadata(context, command.Name, command.Kind, command.RoomId, command.Description) is { } invalid) return invalid;
            var channel = command.ChannelKey ?? LogicalDeviceRules.DefaultChannel;
            if (!LogicalDeviceRules.ValidChannel(channel)) return Invalid("Use a lower-case channel key of up to 64 letters, digits, '-', '_' or '.'.");
            if (Effective(command.EffectiveAt, context.Now, out var effective) is { } time) return time;
            if (command.DeviceId is { } deviceId)
            {
                if (Target(context, deviceId, command.EndpointId) is { } target) return target;
                if (Conflict(context, deviceId, command.EndpointId, effective, null, []) is { } busy) return busy;
            }
            else if (command.EndpointId is not null) return Invalid("Choose a physical device for the endpoint.");
            var id = await context.Db.ExecuteScalarAsync<long>(Query("""
                INSERT INTO logical_devices(property_id,name,kind,room_id,description,created_at_utc,updated_at_utc,creation)
                VALUES(@property,@Name,@Kind,@RoomId,@Description,@now,@now,'manual'); SELECT LAST_INSERT_ID();
                """, context.Ct, new { property, command.Name, command.Kind, command.RoomId, Description = Blank(command.Description), now = context.Now },
                context.Tx));
            // An identity without hardware has no assignment, so it shows no readings until a device is assigned.
            if (command.DeviceId is { } assigned)
                await InsertAssignmentAsync(context, id, channel, assigned, command.EndpointId, effective, Blank(command.Reason));
            await AuditAsync(context, id, "created", command.DeviceId is null ? null : channel, null, command.DeviceId,
                command.DeviceId is null ? null : effective, Blank(command.Reason));
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> UpdateAsync(long property, string user, long id, UpdateLogicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            if (Metadata(context, command.Name, command.Kind, command.RoomId, command.Description) is { } invalid) return invalid;
            if (command.ExpectedUpdatedAt is { } expected &&
                Microseconds(expected.UtcDateTime) != Kind(Microseconds(context.State.Logical[id].UpdatedAt)))
                return Fail(LogicalChangeStatus.Conflict, "Someone else changed this logical device since it was loaded. Reload and try again.");
            await context.Db.ExecuteAsync(Query("""
                UPDATE logical_devices SET name=@Name, kind=@Kind, room_id=@RoomId, description=@Description, updated_at_utc=@now
                WHERE property_id=@property AND id=@id
                """, context.Ct, new { property, id, command.Name, command.Kind, command.RoomId, Description = Blank(command.Description), now = context.Now },
                context.Tx));
            await AuditAsync(context, id, "updated", null, null, null, null, null);
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> AssignAsync(long property, string user, long id, AssignPhysicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            if (!LogicalDeviceRules.ValidChannel(command.ChannelKey)) return Invalid("Use a valid channel key.");
            var channel = context.State.Assignments.Where(item => item.LogicalDeviceId == id && item.ChannelKey == command.ChannelKey).ToList();
            if (channel.Any(item => item.ValidTo is null))
                return Fail(LogicalChangeStatus.Conflict, "This channel already has an active device. Use Replace device instead.");
            if (Effective(command.EffectiveAt, context.Now, out var effective) is { } time) return time;
            if (channel.Count > 0 && channel.Max(item => item.ValidTo!.Value) > effective)
                return Invalid("The start must not be earlier than the end of the channel's previous assignment.");
            if (Target(context, command.DeviceId, command.EndpointId) is { } target) return target;
            if (await ReleaseAsync(context, id, command.DeviceId, command.EndpointId, effective, command.ReleaseFrom,
                    command.AcceptReleasedHistory, command.ArchiveReleased) is { } release) return release;
            if (Conflict(context, command.DeviceId, command.EndpointId, effective, null, []) is { } busy) return busy;
            await InsertAssignmentAsync(context, id, command.ChannelKey, command.DeviceId, command.EndpointId, effective, Blank(command.Reason));
            await AuditAsync(context, id, "assigned", command.ChannelKey, null, command.DeviceId, effective, Blank(command.Reason));
            await TouchAsync(context, id);
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> ReplaceAsync(long property, string user, long id, ReplacePhysicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            if (!LogicalDeviceRules.ValidChannel(command.ChannelKey)) return Invalid("Use a valid channel key.");
            var current = context.State.Assignments.SingleOrDefault(item =>
                item.LogicalDeviceId == id && item.ChannelKey == command.ChannelKey && item.ValidTo is null);
            if (current is null) return Fail(LogicalChangeStatus.Conflict, "This channel has no active device to replace.");
            if (current.Id != command.ExpectedAssignmentId)
                return Fail(LogicalChangeStatus.Conflict, "The active device changed since this page was loaded. Reload and try again.");
            if (Effective(command.EffectiveAt, context.Now, out var effective) is { } time) return time;
            if (effective <= current.ValidFrom)
                return Invalid("The replacement time must be after the current device's assignment started.");
            if (command.DeviceId == current.DeviceId && command.EndpointId == current.EndpointId)
                return Invalid("Choose a different physical device or endpoint.");
            if (Target(context, command.DeviceId, command.EndpointId) is { } target) return target;
            var comparison = LogicalDeviceRules.Compare(
                Measurements(context.State.Devices[current.DeviceId].Capabilities(current.EndpointId)),
                Measurements(context.State.Devices[command.DeviceId].Capabilities(command.EndpointId)));
            if (!comparison.Compatible)
                return Fail(LogicalChangeStatus.Incompatible, "The replacement does not report any measurement of the current device.");
            if (comparison.Missing.Count > 0 && !command.AcceptMissingCapabilities)
                return Fail(LogicalChangeStatus.MissingCapabilities,
                    "The replacement does not report every measurement of the current device. Confirm to continue without them.", comparison.Missing);
            if (await ReleaseAsync(context, id, command.DeviceId, command.EndpointId, effective, command.ReleaseFrom,
                    command.AcceptReleasedHistory, command.ArchiveReleased) is { } release) return release;
            if (Conflict(context, command.DeviceId, command.EndpointId, effective, null, [current.Id]) is { } busy) return busy;
            var closed = await context.Db.ExecuteAsync(Query("""
                UPDATE logical_device_assignments SET valid_to_utc=@effective
                WHERE property_id=@property AND id=@Id AND valid_to_utc IS NULL
                """, context.Ct, new { property, current.Id, effective }, context.Tx));
            if (closed != 1) return Fail(LogicalChangeStatus.Conflict, "The active device changed. Reload and try again.");
            await InsertAssignmentAsync(context, id, command.ChannelKey, command.DeviceId, command.EndpointId, effective, Blank(command.Reason));
            await AuditAsync(context, id, "replaced", command.ChannelKey, current.DeviceId, command.DeviceId, effective, Blank(command.Reason));
            await TouchAsync(context, id);
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> CorrectAsync(long property, string user, long id, long assignment, CorrectAssignmentStart command,
        CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            if (Blank(command.Reason) is null) return Invalid("Explain the correction in the reason.");
            var target = context.State.Assignments.SingleOrDefault(item => item.Id == assignment && item.LogicalDeviceId == id);
            if (target is null) return Fail(LogicalChangeStatus.NotFound, "The assignment does not belong to this logical device.");
            if (Effective(command.EffectiveAt, context.Now, out var start) is { } time) return time;
            var channel = context.State.Assignments.Where(item => item.LogicalDeviceId == id && item.ChannelKey == target.ChannelKey && item.Id != target.Id).ToList();
            var previous = channel.SingleOrDefault(item => item.ValidTo == target.ValidFrom);
            if (previous is not null && start <= previous.ValidFrom)
                return Invalid("The corrected start must be after the previous device's assignment started.");
            var earlier = channel.Where(item => item != previous && item.ValidFrom < target.ValidFrom).Select(item => item.ValidTo!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
            if (start < earlier) return Invalid("The corrected start would overlap an earlier assignment of this channel.");
            if (start >= (target.ValidTo ?? context.Now.AddTicks(1)))
                return Invalid("The corrected start must be before the assignment ends and not in the future.");
            if (start == target.ValidFrom) return Invalid("The corrected start equals the current start.");
            HashSet<long> moved = previous is null ? [target.Id] : [target.Id, previous.Id];
            if (Conflict(context, target.DeviceId, target.EndpointId, start, target.ValidTo, moved) is { } busy) return busy;
            if (previous is not null && Conflict(context, previous.DeviceId, previous.EndpointId, previous.ValidFrom, start, moved) is { } other)
                return other;
            var parameters = new { property, start, Target = target.Id, Previous = previous?.Id };
            if (previous is not null && start < target.ValidFrom)
                await context.Db.ExecuteAsync(Query("UPDATE logical_device_assignments SET valid_to_utc=@start WHERE property_id=@property AND id=@Previous",
                    context.Ct, parameters, context.Tx));
            await context.Db.ExecuteAsync(Query("UPDATE logical_device_assignments SET valid_from_utc=@start WHERE property_id=@property AND id=@Target",
                context.Ct, parameters, context.Tx));
            if (previous is not null && start > target.ValidFrom)
                await context.Db.ExecuteAsync(Query("UPDATE logical_device_assignments SET valid_to_utc=@start WHERE property_id=@property AND id=@Previous",
                    context.Ct, parameters, context.Tx));
            await AuditAsync(context, id, "corrected", target.ChannelKey, previous?.DeviceId, target.DeviceId, start, Blank(command.Reason));
            await TouchAsync(context, id);
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> ArchiveAsync(long property, string user, long id, ArchiveLogicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            var open = context.State.Assignments.Where(item => item.LogicalDeviceId == id && item.ValidTo is null).ToList();
            if (open.Any(item => item.ValidFrom >= context.Now))
                return Fail(LogicalChangeStatus.Conflict, "An assignment starts now. Retry in a moment.");
            await context.Db.ExecuteAsync(Query("""
                UPDATE logical_device_assignments SET valid_to_utc=@now WHERE property_id=@property AND logical_device_id=@id AND valid_to_utc IS NULL;
                UPDATE logical_devices SET archived_at_utc=@now, updated_at_utc=@now WHERE property_id=@property AND id=@id;
                """, context.Ct, new { property, id, now = context.Now }, context.Tx));
            await AuditAsync(context, id, "archived", null, open.FirstOrDefault()?.DeviceId, null, context.Now, Blank(command.Reason));
            return LogicalChangeResult.Done(id);
        });

    public Task<LogicalChangeResult> UnassignAsync(long property, string user, long id, UnassignPhysicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (Active(context, id) is { } missing) return missing;
            if (!LogicalDeviceRules.ValidChannel(command.ChannelKey)) return Invalid("Use a valid channel key.");
            var current = context.State.Assignments.SingleOrDefault(item =>
                item.LogicalDeviceId == id && item.ChannelKey == command.ChannelKey && item.ValidTo is null);
            if (current is null) return Fail(LogicalChangeStatus.Conflict, "This channel has no active device.");
            if (current.Id != command.ExpectedAssignmentId)
                return Fail(LogicalChangeStatus.Conflict, "The active device changed since this page was loaded. Reload and try again.");
            if (Effective(command.EffectiveAt, context.Now, out var effective) is { } time) return time;
            if (effective <= current.ValidFrom) return Invalid("The end time must be after the device's assignment started.");
            if (await CloseAsync(context, current, effective) is { } changed) return changed;
            await AuditAsync(context, id, "unassigned", command.ChannelKey, current.DeviceId, null, effective, Blank(command.Reason));
            await TouchAsync(context, id);
            return LogicalChangeResult.Done(id);
        });

    /// <summary>Returns an archived logical device to the active list. No earlier assignment is reopened.</summary>
    public Task<LogicalChangeResult> RestoreAsync(long property, string user, long id, RestoreLogicalDevice command, CancellationToken ct) =>
        MutateAsync(property, user, ct, async context =>
        {
            if (!context.State.Logical.TryGetValue(id, out var logical)) return Fail(LogicalChangeStatus.NotFound, "The logical device does not exist.");
            if (logical.ArchivedAt is null) return Fail(LogicalChangeStatus.Conflict, "The logical device is not archived.");
            await context.Db.ExecuteAsync(Query("""
                UPDATE logical_devices SET archived_at_utc=NULL, updated_at_utc=@Now WHERE property_id=@Property AND id=@id AND archived_at_utc IS NOT NULL
                """, context.Ct, new { context.Now, context.Property, id }, context.Tx));
            await AuditAsync(context, id, "restored", null, null, null, null, Blank(command.Reason));
            return LogicalChangeResult.Done(id);
        });

    public async Task<PhysicalDeviceList> PhysicalDevicesAsync(long property, string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var state = await LoadAsync(db, null, property, ct);
        var role = await RoleAsync(db, null, property, user, ct);
        var origins = state.Logical.Values.Where(item => item.OriginDeviceId is not null).Select(item => item.OriginDeviceId!.Value).ToHashSet();
        return new(CanManage(role), state.Devices.Values.Where(device => !device.Simulated).Select(device =>
        {
            var owner = state.Assignments.FirstOrDefault(item => item.DeviceId == device.Id && item.ValidTo is null);
            var logical = owner is null ? null : state.Logical[owner.LogicalDeviceId];
            return new PhysicalDeviceView(device.Id, device.Name, device.Provider, device.Manufacturer, device.Model, device.Kind, device.RoomId,
                Measurements(device.Capabilities(null)),
                device.Endpoints.Select(endpoint => new CandidateEndpoint(endpoint.Id, endpoint.Name, Measurements(endpoint.Capabilities))).ToList(),
                device.RequiresEndpoint, logical?.Id, logical?.Name, logical?.Creation,
                origins.Contains(device.Id) || state.Assignments.Any(item => item.DeviceId == device.Id));
        }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToList());
    }

    public async Task<ReleasePreview?> ReleasePreviewAsync(long property, long target, long deviceId, long? endpointId, DateTimeOffset effectiveAt,
        CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var state = await LoadAsync(db, null, property, ct);
        if (!state.Logical.ContainsKey(target) || !state.Devices.ContainsKey(deviceId)) return null;
        var effective = Microseconds(effectiveAt.UtcDateTime);
        if (Releasable(state, target, deviceId, endpointId, effective) is not { } clashes) return null;
        var owner = state.Logical[clashes[0].LogicalDeviceId];
        var readings = await ReadingsAsync(db, null, property, clashes, effective, ct);
        return new(owner.Id, owner.Name, owner.Creation, clashes.Select(item => item.ChannelKey).Distinct().ToList(), readings.Count,
            Utc(readings.First), Utc(readings.Last),
            !state.Assignments.Any(item => item.LogicalDeviceId == owner.Id && item.ValidTo is null && !clashes.Contains(item)),
            state.Assignments.Any(item => item.LogicalDeviceId == owner.Id && item.DeviceId != deviceId),
            await DashboardReferencesAsync(db, property, owner.Id, ct));
    }

    public Task<LogicalHistoryRead> HistoryAsync(long property, long id, string channel, Metric metric, HistoryQuery query, CancellationToken ct) =>
        history.ReadLogicalAsync(property, id, channel, metric, query, ct);

    /// <summary>
    /// One logical device per physical device without one. A device whose measurement capability appears on several endpoints
    /// receives one channel per endpoint. Assignments start at <see cref="LogicalDeviceRules.HistoryOrigin"/>, so all recorded
    /// observations of the device stay attributed to it. No past replacement is inferred.
    /// </summary>
    public async Task<InitializationPlan> PlanInitializationAsync(long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await PlanAsync(db, null, property, ct);
    }

    public async Task<int> ExecuteInitializationAsync(long property, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE", ct, new { property }, tx)) is null)
            return 0;
        var plan = await PlanAsync(db, tx, property, ct);
        var now = Microseconds(clock.GetUtcNow().UtcDateTime);
        var context = new Context(db, tx, property, InitializationActor, now, await LoadAsync(db, tx, property, ct), ct);
        var created = 0;
        foreach (var item in plan.Work)
        {
            var device = context.State.Devices[item.DeviceId];
            await CreateIdentityAsync(context, item, device.Name, "initialized", "initialized", null);
            created++;
        }
        if (BeforeCommit is { } hook) await hook(ct);
        await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>
    /// Properties with at least one physical device that has never had a logical identity or assignment. Only properties that
    /// already use logical devices take part, so the explicit, dry-run-first initialization remains the onboarding step.
    /// </summary>
    public async Task<IReadOnlyList<long>> ProvisioningCandidatesAsync(CancellationToken ct)
    {
        if (!await SchemaReadyAsync(ct)) return [];
        await using var db = await database.OpenAsync(ct);
        return (await db.QueryAsync<long>(Query("""
            SELECT DISTINCT d.property_id FROM devices d
            WHERE EXISTS (SELECT 1 FROM logical_devices l WHERE l.property_id=d.property_id)
              AND NOT EXISTS (SELECT 1 FROM logical_devices o WHERE o.property_id=d.property_id AND o.origin_device_id=d.id)
              AND NOT EXISTS (SELECT 1 FROM logical_device_assignments a WHERE a.property_id=d.property_id AND a.device_id=d.id)
              AND NOT EXISTS (SELECT 1 FROM provider_bindings b JOIN integration_connections c ON c.id=b.connection_id AND c.property_id=b.property_id
                              WHERE b.device_id=d.id AND b.property_id=d.property_id AND c.provider_code='simulator')
              AND d.retired_at_utc IS NULL
            ORDER BY d.property_id
            """, ct))).ToList();
    }

    /// <summary>
    /// Creates one automatic logical identity per accepted physical device of the property that has never had one, using the same
    /// plan and assignment semantics as initialization. The property row lock and the unique origin key make repeated or concurrent
    /// runs create each identity exactly once; existing identities, names, rooms and intervals are never changed.
    /// </summary>
    public async Task<int> ProvisionAsync(long property, CancellationToken ct)
    {
        if (!await SchemaReadyAsync(ct)) return 0;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE", ct, new { property }, tx)) is null)
            return 0;
        var state = await LoadAsync(db, tx, property, ct);
        if (state.Logical.Count == 0) return 0;
        var plan = await PlanAsync(db, tx, property, ct);
        var context = new Context(db, tx, property, ProvisioningActor, Microseconds(clock.GetUtcNow().UtcDateTime), state, ct);
        var names = state.Logical.Values.Where(item => item.ArchivedAt is null).Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        foreach (var item in plan.Work.Where(item => item.Channels.Count > 0 &&
                     state.Devices[item.DeviceId].Capabilities(null).Any(LogicalDeviceRules.IsMeasurement)).Take(MaximumProvisionedPerRun))
        {
            var name = DefaultName(state.Devices[item.DeviceId], names);
            names.Add(name);
            await CreateIdentityAsync(context, item, name, "automatic", "provisioned", "Created automatically for a newly discovered device");
            created++;
        }
        if (created == 0) return 0;
        if (BeforeCommit is { } hook) await hook(ct);
        await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>Inventory name when it is unique among active logical devices; otherwise qualified by model, then by device number.</summary>
    internal static string DefaultName(string name, string manufacturer, string kind, string model, long id, IReadOnlySet<string> taken)
    {
        static string Clip(string value) => value.Length > 128 ? value[..128].TrimEnd() : value;
        var label = string.Concat(kind.Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));
        var basis = !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : $"{manufacturer.Trim()} {(kind == "SmartSwitch" ? "Relay" : kind == "Unknown" ? "Device" : label)}".Trim();
        if (basis.Length == 0) basis = "Device";
        var candidates = new[] { basis, string.IsNullOrWhiteSpace(model) ? null : $"{basis} ({model.Trim()})", $"{basis} #{id}" };
        return Clip(candidates.OfType<string>().FirstOrDefault(candidate => !taken.Contains(Clip(candidate))) ?? $"{basis} #{id}");
    }

    private static string DefaultName(PhysicalDevice device, IReadOnlySet<string> taken) =>
        DefaultName(device.Name, device.Manufacturer, device.Kind, device.Model, device.Id, taken);

    private static async Task CreateIdentityAsync(Context context, InitializationItem item, string name, string creation, string action,
        string? reason)
    {
        var device = context.State.Devices[item.DeviceId];
        var id = await context.Db.ExecuteScalarAsync<long>(Query("""
            INSERT INTO logical_devices(property_id,name,kind,room_id,origin_device_id,created_at_utc,updated_at_utc,creation)
            VALUES(@Property,@Name,@Kind,@RoomId,@DeviceId,@Now,@Now,@creation); SELECT LAST_INSERT_ID();
            """, context.Ct, new { context.Property, Name = name.Length > 128 ? name[..128] : name,
            Kind = device.Kind.Length > 32 ? device.Kind[..32] : device.Kind, device.RoomId, item.DeviceId, context.Now, creation }, context.Tx));
        context.State.Logical[id] = new(id, name, device.Kind, device.RoomId, null, item.DeviceId, creation, context.Now, context.Now, null);
        // Starting at the history origin keeps every recorded observation of the device attributed to it; no observation time is invented.
        foreach (var channel in item.Channels)
            await InsertAssignmentAsync(context, id, channel.ChannelKey, item.DeviceId, channel.EndpointId, LogicalDeviceRules.HistoryOrigin,
                "Initial logical identity for the device's recorded history");
        await AuditAsync(context, id, action, null, null, item.DeviceId, LogicalDeviceRules.HistoryOrigin, reason);
    }

    private async Task<InitializationPlan> PlanAsync(MySqlConnection db, MySqlTransaction? tx, long property, CancellationToken ct)
    {
        if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property", ct, new { property }, tx)) is null)
            return new(property, false, []);
        var state = await LoadAsync(db, tx, property, ct);
        var series = (await db.QueryAsync<(long DeviceId, int Count)>(Query("""
            SELECT e.device_id, COUNT(*) FROM telemetry_series s JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
            WHERE s.property_id=@property GROUP BY e.device_id
            """, ct, new { property }, tx))).ToDictionary(row => row.DeviceId, row => row.Count);
        var origins = state.Logical.Values.Where(item => item.OriginDeviceId is not null).Select(item => item.OriginDeviceId!.Value).ToHashSet();
        var items = new List<InitializationItem>();
        foreach (var device in state.Devices.Values)
        {
            var count = series.GetValueOrDefault(device.Id);
            string? skipped = device.Retired ? "moved to another property" : device.Simulated ? "simulated device"
                : origins.Contains(device.Id) ? "already initialized"
                : state.Assignments.FirstOrDefault(item => item.DeviceId == device.Id) is { } assigned
                    ? $"already assigned to logical device {assigned.LogicalDeviceId}" : null;
            List<InitializationChannel> channels;
            if (!device.RequiresEndpoint) channels = [new(LogicalDeviceRules.DefaultChannel, null)];
            else
            {
                var taken = new HashSet<string>(StringComparer.Ordinal);
                channels = device.Endpoints.Where(endpoint => endpoint.Capabilities.Any(LogicalDeviceRules.IsMeasurement))
                    .Select(endpoint => new InitializationChannel(LogicalDeviceRules.ChannelFor(endpoint.Key, taken), endpoint.Id)).ToList();
            }
            items.Add(new(device.Id, device.Name, device.Provider, channels, count, skipped));
        }
        return new(property, true, items);
    }

    private async Task<LogicalChangeResult> MutateAsync(long property, string user, CancellationToken ct,
        Func<Context, Task<LogicalChangeResult>> action)
    {
        if (!await SchemaReadyAsync(ct)) return Fail(LogicalChangeStatus.SchemaPending, "Logical devices require migrations 017, 018 and 019.");
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE", ct, new { property }, tx)) is null)
            return Fail(LogicalChangeStatus.NotFound, "The property does not exist.");
        if (!CanManage(await RoleAsync(db, tx, property, user, ct)))
            return Fail(LogicalChangeStatus.Forbidden, "Only property Owners and Admins can change logical devices.");
        var context = new Context(db, tx, property, user, Microseconds(clock.GetUtcNow().UtcDateTime), await LoadAsync(db, tx, property, ct), ct);
        try
        {
            var result = await action(context);
            if (result.Status != LogicalChangeStatus.Done) return result;
            if (BeforeCommit is { } hook) await hook(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (MySqlException error) when (error.ErrorCode is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.LockDeadlock)
        {
            return Fail(LogicalChangeStatus.Conflict, "Another change to this logical device was saved at the same time. Reload and try again.");
        }
    }

    private static LogicalChangeResult? Metadata(Context context, string? name, string? kind, long? roomId, string? description)
    {
        if (name is not { Length: >= 1 and <= 128 } || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            return Invalid("Enter a name of 1 to 128 characters without control characters.");
        if (kind is not { Length: >= 1 and <= 32 } || !kind.All(char.IsAsciiLetterOrDigit))
            return Invalid("Choose a device kind of 1 to 32 letters or digits.");
        if (description is { Length: > 256 } || description?.Any(char.IsControl) == true)
            return Invalid("Keep the description under 257 characters without control characters.");
        if (roomId is { } room && !context.State.Rooms.Contains(room)) return Invalid("Choose a room of this property.");
        return null;
    }

    private static LogicalChangeResult? Active(Context context, long id) =>
        !context.State.Logical.TryGetValue(id, out var logical) ? Fail(LogicalChangeStatus.NotFound, "The logical device does not exist.")
        : logical.ArchivedAt is not null ? Fail(LogicalChangeStatus.Conflict, "The logical device is archived. Its history is read-only.")
        : null;

    private static LogicalChangeResult? Effective(DateTimeOffset? requested, DateTime now, out DateTime effective)
    {
        effective = requested is { } value ? Microseconds(value.UtcDateTime) : now;
        if (effective > now + ClockSkew) return Invalid("The effective time cannot be in the future.");
        if (effective < LogicalDeviceRules.HistoryOrigin) return Invalid("The effective time is out of range.");
        return null;
    }

    private static LogicalChangeResult? Target(Context context, long deviceId, long? endpointId)
    {
        if (!context.State.Devices.TryGetValue(deviceId, out var device) || device.Simulated)
            return Fail(LogicalChangeStatus.NotFound, "Choose a physical device of this property.");
        if (endpointId is { } endpoint && device.Endpoints.All(item => item.Id != endpoint))
            return Invalid("Choose an endpoint of the selected device.");
        if (endpointId is null && device.RequiresEndpoint)
            return Fail(LogicalChangeStatus.AmbiguousEndpoint, "This device reports the same measurement on several endpoints. Choose an endpoint.");
        if (!Measurements(device.Capabilities(endpointId)).Any())
            return Fail(LogicalChangeStatus.Incompatible, "The selected device reports no measurement capability.");
        return null;
    }

    private static LogicalChangeResult? Conflict(Context context, long deviceId, long? endpointId, DateTime from, DateTime? to,
        HashSet<long> ignored)
    {
        var clash = context.State.Assignments.FirstOrDefault(item => !ignored.Contains(item.Id) &&
            LogicalDeviceRules.SameSource(item.DeviceId, item.EndpointId, deviceId, endpointId) &&
            LogicalDeviceRules.Overlaps(item.ValidFrom, item.ValidTo, from, to));
        return clash is null ? null : Fail(LogicalChangeStatus.Conflict,
            $"The device is already assigned to \"{context.State.Logical[clash.LogicalDeviceId].Name}\" for an overlapping time.");
    }

    /// <summary>
    /// The open assignments of another logical device that the hardware would leave at <paramref name="effective"/>. Null unless every
    /// overlapping assignment is open, belongs to one other logical device and started before the effective time; any other
    /// overlap remains a conflict.
    /// </summary>
    private static List<AssignmentRow>? Releasable(State state, long target, long deviceId, long? endpointId, DateTime effective)
    {
        var clashes = state.Assignments.Where(item => item.LogicalDeviceId != target &&
            LogicalDeviceRules.SameSource(item.DeviceId, item.EndpointId, deviceId, endpointId) &&
            LogicalDeviceRules.Overlaps(item.ValidFrom, item.ValidTo, effective, null)).ToList();
        return clashes.Count > 0 && clashes.All(item => item.ValidTo is null && item.ValidFrom < effective &&
            item.LogicalDeviceId == clashes[0].LogicalDeviceId && state.Logical[item.LogicalDeviceId].ArchivedAt is null) ? clashes : null;
    }

    /// <summary>
    /// Ends another logical device's use of the hardware at the effective time when the caller named that device explicitly. Readings
    /// before the effective time stay with it and need confirmation; nothing is deleted, and archiving it is the caller's choice.
    /// </summary>
    private async Task<LogicalChangeResult?> ReleaseAsync(Context context, long target, long deviceId, long? endpointId, DateTime effective,
        long? releaseFrom, bool acceptHistory, bool archive)
    {
        var clashes = Releasable(context.State, target, deviceId, endpointId, effective);
        if (clashes is null)
            return releaseFrom is null ? null : Fail(LogicalChangeStatus.Conflict,
                "The selected device is not the active device of that logical device at this time. Reload and try again.");
        var owner = context.State.Logical[clashes[0].LogicalDeviceId];
        if (releaseFrom is null)
            return Fail(LogicalChangeStatus.ReleaseRequired,
                $"The device is the active device of \"{owner.Name}\". Choose it as a replacement to end that assignment at this time.");
        if (releaseFrom != owner.Id)
            return Fail(LogicalChangeStatus.Conflict, $"The device is now used by \"{owner.Name}\". Reload and try again.");
        var readings = await ReadingsAsync(context.Db, context.Tx, context.Property, clashes, effective, context.Ct);
        if (readings.Count > 0 && !acceptHistory)
            return Fail(LogicalChangeStatus.HistoryConfirmation,
                $"{readings.Count} readings recorded before this time stay with \"{owner.Name}\". Confirm to continue.");
        foreach (var clash in clashes)
        {
            if (await CloseAsync(context, clash, effective) is { } changed) return changed;
            await AuditAsync(context, owner.Id, "resolved", clash.ChannelKey, clash.DeviceId, null, effective,
                $"Hardware moved to logical device {target}");
        }
        if (archive && !context.State.Assignments.Any(item => item.LogicalDeviceId == owner.Id && item.ValidTo is null))
        {
            await context.Db.ExecuteAsync(Query("""
                UPDATE logical_devices SET archived_at_utc=@Now, updated_at_utc=@Now WHERE property_id=@Property AND id=@id
                """, context.Ct, new { context.Now, context.Property, id = owner.Id }, context.Tx));
            await AuditAsync(context, owner.Id, "archived", null, deviceId, null, effective, $"Resolved into logical device {target}");
            context.State.Logical[owner.Id] = owner with { ArchivedAt = context.Now };
        }
        else await TouchAsync(context, owner.Id);
        return null;
    }

    private static async Task<LogicalChangeResult?> CloseAsync(Context context, AssignmentRow assignment, DateTime effective)
    {
        var closed = await context.Db.ExecuteAsync(Query("""
            UPDATE logical_device_assignments SET valid_to_utc=@effective
            WHERE property_id=@Property AND id=@Id AND valid_to_utc IS NULL
            """, context.Ct, new { context.Property, assignment.Id, effective }, context.Tx));
        if (closed != 1) return Fail(LogicalChangeStatus.Conflict, "The active device changed. Reload and try again.");
        context.State.Assignments[context.State.Assignments.IndexOf(assignment)] = assignment with { ValidTo = effective };
        return null;
    }

    /// <summary>Readings of the hardware that the assignments attribute to their logical device before <paramref name="effective"/>.</summary>
    private static async Task<(long Count, DateTime? First, DateTime? Last)> ReadingsAsync(MySqlConnection db, MySqlTransaction? tx,
        long property, IReadOnlyList<AssignmentRow> assignments, DateTime effective, CancellationToken ct)
    {
        (long Count, DateTime? First, DateTime? Last) total = (0, null, null);
        foreach (var assignment in assignments)
        {
            var row = await db.QuerySingleAsync<(long Count, DateTime? First, DateTime? Last)>(Query("""
                SELECT COUNT(*), MIN(h.observed_at_utc), MAX(h.observed_at_utc)
                FROM telemetry_samples h
                JOIN telemetry_series s ON s.id=h.series_id
                JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
                WHERE s.property_id=@property AND e.device_id=@DeviceId AND (@EndpointId IS NULL OR s.endpoint_id=@EndpointId)
                  AND h.observed_at_utc>=@ValidFrom AND h.observed_at_utc<@effective
                """, ct, new { property, assignment.DeviceId, assignment.EndpointId, assignment.ValidFrom, effective }, tx));
            total = (total.Count + row.Count, Min(total.First, row.First), Max(total.Last, row.Last));
        }
        return total;

        static DateTime? Min(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a < b ? a : b;
        static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>Dashboard measurements bound to the logical device. They are reported for review and never changed here.</summary>
    private static async Task<int> DashboardReferencesAsync(MySqlConnection db, long property, long logical, CancellationToken ct)
    {
        try
        {
            return await db.ExecuteScalarAsync<int>(Query("""
                SELECT COUNT(*) FROM dashboard_values WHERE property_id=@property AND source_key LIKE @prefix
                """, ct, new { property, prefix = $"logical:{logical}:%" }));
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return 0;
        }
    }

    private static async Task InsertAssignmentAsync(Context context, long logicalId, string channel, long deviceId, long? endpointId,
        DateTime from, string? reason)
    {
        var id = await context.Db.ExecuteScalarAsync<long>(Query("""
            INSERT INTO logical_device_assignments(property_id,logical_device_id,channel_key,device_id,endpoint_id,valid_from_utc,assigned_by,assigned_at_utc,reason)
            VALUES(@Property,@logicalId,@channel,@deviceId,@endpointId,@from,@User,@Now,@reason); SELECT LAST_INSERT_ID();
            """, context.Ct, new { context.Property, logicalId, channel, deviceId, endpointId, from, context.User, context.Now, reason }, context.Tx));
        context.State.Assignments.Add(new(id, logicalId, channel, deviceId, endpointId, from, null, context.User, context.Now, reason));
    }

    private static Task AuditAsync(Context context, long logicalId, string action, string? channel, long? oldDevice, long? newDevice,
        DateTime? effective, string? reason) =>
        context.Db.ExecuteAsync(Query("""
            INSERT INTO logical_device_audit(property_id,logical_device_id,occurred_at_utc,actor,action,channel_key,old_device_id,new_device_id,effective_at_utc,reason)
            VALUES(@Property,@logicalId,@Now,@User,@action,@channel,@oldDevice,@newDevice,@effective,@reason)
            """, context.Ct, new { context.Property, logicalId, context.Now, context.User, action, channel, oldDevice, newDevice, effective, reason },
            context.Tx));

    private static Task TouchAsync(Context context, long id) => context.Db.ExecuteAsync(Query(
        "UPDATE logical_devices SET updated_at_utc=@Now WHERE property_id=@Property AND id=@id", context.Ct,
        new { context.Now, context.Property, id }, context.Tx));

    private static LogicalDeviceView View(State state, LogicalRow logical)
    {
        var assignments = state.Assignments.Where(item => item.LogicalDeviceId == logical.Id).Select(item =>
        {
            var device = state.Devices[item.DeviceId];
            var endpoint = device.Endpoints.FirstOrDefault(entry => entry.Id == item.EndpointId);
            return (item.ValidTo, View: new LogicalAssignmentView(item.Id, item.ChannelKey, device.Id, device.Name, device.Provider,
                device.Manufacturer, device.Model, item.EndpointId, endpoint?.Name, Utc(item.ValidFrom), Utc(item.ValidTo), item.AssignedBy,
                Utc(item.AssignedAt), item.Reason, Measurements(device.Capabilities(item.EndpointId))));
        }).ToList();
        return new(logical.Id, logical.Name, logical.Kind, logical.RoomId, logical.Description, Utc(logical.CreatedAt), Utc(logical.UpdatedAt),
            Utc(logical.ArchivedAt), assignments.Where(item => item.ValidTo is null).Select(item => item.View).OrderBy(item => item.ChannelKey).ToList(),
            assignments.Where(item => item.ValidTo is not null).Select(item => item.View).OrderByDescending(item => item.ValidFrom).ToList(),
            Metrics(state, logical.Id), logical.Creation);
    }

    private static async Task<State> LoadAsync(MySqlConnection db, MySqlTransaction? tx, long property, CancellationToken ct)
    {
        using var rows = await db.QueryMultipleAsync(Query("""
            SELECT d.id AS Id, COALESCE(d.display_name, d.name) AS Name, d.kind AS Kind, d.manufacturer AS Manufacturer, d.model AS Model, d.room_id AS RoomId,
                   COALESCE((SELECT c.provider_code FROM provider_bindings b JOIN integration_connections c
                             ON c.id=b.connection_id AND c.property_id=b.property_id
                             WHERE b.device_id=d.id AND b.property_id=d.property_id ORDER BY b.id LIMIT 1), 'unknown') AS Provider,
                   d.retired_at_utc IS NOT NULL AS Retired
            FROM devices d WHERE d.property_id=@property ORDER BY d.id;
            SELECT id AS Id, device_id AS DeviceId, name AS Name, endpoint_key AS EndpointKey FROM device_endpoints WHERE property_id=@property ORDER BY id;
            SELECT cap.endpoint_id AS EndpointId, cap.capability_code AS Code FROM device_capabilities cap
            JOIN device_endpoints e ON e.id=cap.endpoint_id WHERE e.property_id=@property AND cap.readable;
            SELECT id AS Id, name AS Name, kind AS Kind, room_id AS RoomId, description AS Description, origin_device_id AS OriginDeviceId, creation AS Creation,
                   created_at_utc AS CreatedAt, updated_at_utc AS UpdatedAt, archived_at_utc AS ArchivedAt
            FROM logical_devices WHERE property_id=@property;
            SELECT id AS Id, logical_device_id AS LogicalDeviceId, channel_key AS ChannelKey, device_id AS DeviceId, endpoint_id AS EndpointId,
                   valid_from_utc AS ValidFrom, valid_to_utc AS ValidTo, assigned_by AS AssignedBy, assigned_at_utc AS AssignedAt, reason AS Reason
            FROM logical_device_assignments WHERE property_id=@property ORDER BY valid_from_utc, id;
            SELECT id FROM rooms WHERE property_id=@property;
            SELECT DISTINCT e.device_id AS DeviceId, s.endpoint_id AS EndpointId, s.metric AS MetricName
            FROM telemetry_series s JOIN device_endpoints e ON e.id=s.endpoint_id AND e.property_id=s.property_id
            WHERE s.property_id=@property;
            """, ct, new { property }, tx));
        var devices = (await rows.ReadAsync<DeviceRow>()).ToList();
        var endpoints = (await rows.ReadAsync<EndpointRow>()).ToList();
        var capabilities = (await rows.ReadAsync<CapabilityRow>()).ToLookup(row => row.EndpointId);
        var logical = (await rows.ReadAsync<LogicalRow>()).ToDictionary(row => row.Id);
        var assignments = (await rows.ReadAsync<AssignmentRow>()).Select(row => row with
        {
            ValidFrom = Kind(row.ValidFrom), ValidTo = row.ValidTo is { } to ? Kind(to) : null, AssignedAt = Kind(row.AssignedAt)
        }).ToList();
        var rooms = (await rows.ReadAsync<long>()).ToHashSet();
        var series = (await rows.ReadAsync<SeriesMetricRow>()).Where(row => row.Metric is not null).ToList();
        var byDevice = endpoints.ToLookup(endpoint => endpoint.DeviceId);
        return new(devices.ToDictionary(device => device.Id, device => new PhysicalDevice(device.Id, device.Name, device.Kind, device.Manufacturer,
            device.Model, device.RoomId, device.Provider, device.Retired != 0, byDevice[device.Id].Select(endpoint => new PhysicalEndpoint(endpoint.Id, endpoint.Name,
                endpoint.EndpointKey, capabilities[endpoint.Id].Select(item => Enum.TryParse<Capability>(item.Code, out var code) && Enum.IsDefined(code)
                    ? code : (Capability?)null).OfType<Capability>().Distinct().ToList())).ToList())), logical, assignments, rooms, series);
    }

    /// <summary>Metrics with recorded series on any device assigned to each channel, marking those the active device records.</summary>
    private static List<LogicalMetric> Metrics(State state, long logicalId)
    {
        bool Reports(AssignmentRow assignment, SeriesMetricRow row) =>
            row.DeviceId == assignment.DeviceId && (assignment.EndpointId is null || row.EndpointId == assignment.EndpointId);
        return state.Assignments.Where(item => item.LogicalDeviceId == logicalId).GroupBy(item => item.ChannelKey)
            .SelectMany(channel =>
            {
                var active = channel.FirstOrDefault(item => item.ValidTo is null);
                return state.Series.Where(row => channel.Any(assignment => Reports(assignment, row))).Select(row => row.Metric!.Value)
                    .Distinct().Order().Select(metric => new LogicalMetric(channel.Key, metric, Telemetry.Unit(metric), TelemetryAggregation.Kind(metric),
                        active is not null && state.Series.Any(row => row.Metric == metric && Reports(active, row))));
            }).ToList();
    }

    private static async Task<string?> RoleAsync(MySqlConnection db, MySqlTransaction? tx, long property, string user, CancellationToken ct) =>
        await db.QuerySingleOrDefaultAsync<string>(Query(
            "SELECT m.role_code FROM property_memberships m JOIN properties p ON p.id=m.property_id AND p.deleted_at_utc IS NULL WHERE m.property_id=@property AND m.user_id=@user",
            ct, new { property, user }, tx));

    internal static bool CanManage(string? role) => role?.ToUpperInvariant() is "OWNER" or "ADMIN";
    private static IReadOnlyList<Capability> Measurements(IEnumerable<Capability> capabilities) =>
        capabilities.Where(LogicalDeviceRules.IsMeasurement).Distinct().Order().ToList();
    private static string? Blank(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }
    private static LogicalChangeResult Invalid(string message) => Fail(LogicalChangeStatus.Invalid, message);
    private static LogicalChangeResult Fail(LogicalChangeStatus status, string message, IReadOnlyList<Capability>? missing = null) =>
        LogicalChangeResult.Fail(status, message, missing);
    private static DateTime Kind(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    internal static DateTime Microseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private sealed record Context(MySqlConnection Db, MySqlTransaction Tx, long Property, string User, DateTime Now, State State, CancellationToken Ct);
    private sealed record State(Dictionary<long, PhysicalDevice> Devices, Dictionary<long, LogicalRow> Logical, List<AssignmentRow> Assignments,
        HashSet<long> Rooms, List<SeriesMetricRow> Series);

    private sealed record SeriesMetricRow(long DeviceId, long EndpointId, string MetricName)
    {
        public Metric? Metric => Enum.TryParse<Metric>(MetricName, out var metric) && Enum.IsDefined(metric) ? metric : null;
    }

    private sealed record PhysicalDevice(long Id, string Name, string Kind, string Manufacturer, string Model, long? RoomId, string Provider, bool Retired,
        IReadOnlyList<PhysicalEndpoint> Endpoints)
    {
        public IEnumerable<Capability> Capabilities(long? endpoint) =>
            Endpoints.Where(item => endpoint is null || item.Id == endpoint).SelectMany(item => item.Capabilities).Distinct();
        public bool RequiresEndpoint => LogicalDeviceRules.RequiresEndpoint(Endpoints.Select(item => item.Capabilities));
        /// <summary>Demonstration hardware from the opt-in simulator never receives new logical identities or assignments.</summary>
        /// <summary>Simulated hardware, or a device row retired when the device moved to another property. Neither takes new assignments.</summary>
        public bool Simulated => Provider == TelemetryHoldPolicy.SimulatorSource || Retired;
    }

    private sealed record PhysicalEndpoint(long Id, string Name, string Key, IReadOnlyList<Capability> Capabilities);
    private sealed record DeviceRow(long Id, string Name, string Kind, string Manufacturer, string Model, long? RoomId, string Provider, long Retired);
    private sealed record EndpointRow(long Id, long DeviceId, string Name, string EndpointKey);
    private sealed record CapabilityRow(long EndpointId, string Code);
    private sealed record LogicalRow(long Id, string Name, string Kind, long? RoomId, string? Description, long? OriginDeviceId, string Creation,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? ArchivedAt);
    private sealed record AssignmentRow(long Id, long LogicalDeviceId, string ChannelKey, long DeviceId, long? EndpointId, DateTime ValidFrom,
        DateTime? ValidTo, string AssignedBy, DateTime AssignedAt, string? Reason);
    private sealed record AuditRow(long Id, DateTime OccurredAt, string Actor, string Action, string? ChannelKey, long? OldDeviceId,
        long? NewDeviceId, DateTime? EffectiveAt, string? Reason);
}
