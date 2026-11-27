namespace HomeAutomation.Application;

/// <summary>A property the user is a member of, with counts read when requested (never cached by the client as authoritative).</summary>
/// <param name="GrowattProfile">Whether a property-scoped Growatt vault profile exists; null when the vault schema is absent.</param>
public sealed record PropertyOverview(long Id, string Name, string? Description, string TimeZone, string Role, bool CanManage,
    bool CanDelete, int Rooms, int Devices, int Installations, bool? GrowattProfile);

public sealed record PropertyManagementView(bool CanCreate, IReadOnlyList<PropertyOverview> Properties);

public sealed record CreatePropertyRequest(string Name, string? Description, string? TimeZone, Guid? RequestId);
public sealed record UpdatePropertyRequest(string Name, string? Description, string TimeZone);
public sealed record DeletePropertyRequest(string ConfirmName);

/// <summary>Everything a deletion would affect. Only <see cref="Devices"/> blocks it.</summary>
/// <param name="HistoricalDevices">Devices that moved away; they and their history stay with the deleted property.</param>
/// <param name="VaultProfiles">Property-scoped Configuration Vault profiles, kept encrypted and untouched.</param>
public sealed record PropertyDeletionImpact(long Id, string Name, int Rooms, int Devices, int Installations, int HistoricalDevices,
    int Members, int VaultProfiles, int DashboardGroups, bool CanDelete);

public sealed record RoomView(long Id, string Name, string? Description, long Devices);
public sealed record RoomRequest(string Name, string? Description);

public sealed record DeviceNameRequest(string? DisplayName);
public sealed record DeviceRoomRequest(long? RoomId);
public sealed record DeviceTransferRequest(long DestinationPropertyId);

/// <summary>A device in another property the user manages that can be moved into the current one.</summary>
public sealed record TransferableDevice(long DeviceId, long PropertyId, string PropertyName, string Name, string ProviderName,
    string Provider, string Model, string? RoomName);

/// <summary>What a transfer would do, checked again when it is executed.</summary>
public sealed record DeviceTransferImpact(long DeviceId, string Name, string Provider, long SourcePropertyId, string SourcePropertyName,
    long DestinationPropertyId, string DestinationPropertyName, string? CurrentRoom, int OpenLogicalAssignments, string? Blocked);

public sealed record DeviceAuditEntry(long Id, DateTimeOffset OccurredAt, string Actor, string Action, long? OtherPropertyId,
    string? OtherPropertyName, string? Detail);

public enum ManagementStatus { Done, NotFound, Forbidden, Invalid, Conflict, SchemaPending }

public sealed record ManagementResult(ManagementStatus Status, string? Message = null, long? Id = null)
{
    public static ManagementResult Done(long? id = null) => new(ManagementStatus.Done, null, id);
    public static ManagementResult Fail(ManagementStatus status, string message) => new(status, message);
}

public interface IPropertyManagement
{
    Task<PropertyManagementView> ListAsync(string user, CancellationToken ct);
    Task<ManagementResult> CreateAsync(string user, CreatePropertyRequest request, CancellationToken ct);
    Task<ManagementResult> UpdateAsync(string user, long property, UpdatePropertyRequest request, CancellationToken ct);
    Task<PropertyDeletionImpact?> DeletionImpactAsync(string user, long property, CancellationToken ct);
    Task<ManagementResult> DeleteAsync(string user, long property, DeletePropertyRequest request, CancellationToken ct);

    Task<IReadOnlyList<RoomView>?> RoomsAsync(string user, long property, CancellationToken ct);
    Task<ManagementResult> CreateRoomAsync(string user, long property, RoomRequest request, CancellationToken ct);
    Task<ManagementResult> UpdateRoomAsync(string user, long property, long room, RoomRequest request, CancellationToken ct);
    Task<ManagementResult> DeleteRoomAsync(string user, long property, long room, CancellationToken ct);

    Task<ManagementResult> RenameDeviceAsync(string user, long property, long device, DeviceNameRequest request, CancellationToken ct);
    Task<ManagementResult> SetDeviceRoomAsync(string user, long property, long device, DeviceRoomRequest request, CancellationToken ct);
    Task<IReadOnlyList<DeviceAuditEntry>?> DeviceAuditAsync(string user, long property, long device, CancellationToken ct);
    Task<IReadOnlyList<TransferableDevice>> TransferableAsync(string user, long property, CancellationToken ct);
    Task<DeviceTransferImpact?> TransferImpactAsync(string user, long property, long device, long destination, CancellationToken ct);
    Task<ManagementResult> TransferAsync(string user, long property, long device, DeviceTransferRequest request, CancellationToken ct);
}

/// <summary>Where a Daikin, eWeLink or Tuya provider record is currently assigned.</summary>
public sealed record ProviderAssignment(long? PropertyId, long? RoomId, string ExternalId);

/// <summary>Records provider-page assignments in the device management audit, after the provider service committed them.</summary>
public interface IDeviceAssignmentAudit
{
    Task<ProviderAssignment?> CurrentAsync(string provider, string publicId, CancellationToken ct);
    Task RecordAsync(string user, string provider, ProviderAssignment? before, string publicId, CancellationToken ct);
}
