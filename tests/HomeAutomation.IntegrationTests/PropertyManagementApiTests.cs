using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeAutomation.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeAutomation.IntegrationTests;

/// <summary>Routing, antiforgery, the property membership filter and status mapping of the management routes.</summary>
public sealed class PropertyManagementApiTests(ApiTests.Host host) : IClassFixture<ApiTests.Host>
{
    private HttpClient Client(string user, string? csrf = null)
    {
        var client = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPropertyManagement>();
            services.AddSingleton<IPropertyManagement, Fake>();
        })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
    }

    [Fact]
    public async Task ReadsNeedAuthenticationAndMembershipBeforeTheStoreIsReached()
    {
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/property-management")).StatusCode);
        using var viewer = Client("viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/property-management")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/properties/1/room-management")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/room-management")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/deletion-impact")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/properties/1/devices/7/transfer-impact")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/properties/1/devices/7/transfer-impact?destination=3")).StatusCode);
    }

    [Fact]
    public async Task MutationsNeedTheAntiforgeryTokenAndMapStoreOutcomes()
    {
        using var owner = Client("logical-owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/properties/1/delete", new DeletePropertyRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/property-management/properties",
            new CreatePropertyRequest("Lake", null, null, null))).StatusCode);
        await CsrfAsync(owner);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/property-management/properties",
            new CreatePropertyRequest("Lake", null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/v1/property-management/properties",
            new CreatePropertyRequest("forbidden", null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/properties/1/delete", new DeletePropertyRequest("conflict"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/properties/1/delete", new DeletePropertyRequest("invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/properties/1/delete", new DeletePropertyRequest("Fixture"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/properties/1/room-management", new RoomRequest("Loft", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/properties/1/room-management/5", new RoomRequest("Attic", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync("/api/v1/properties/1/room-management/5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/properties/1/devices/7/name", new DeviceNameRequest("Boiler"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/properties/1/devices/7/room", new DeviceRoomRequest(null))).StatusCode);
        using var moved = await owner.PostAsJsonAsync("/api/v1/properties/1/devices/7/transfer", new DeviceTransferRequest(3));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Contains("\"id\":70", await moved.Content.ReadAsStringAsync());
        using var refused = await owner.PostAsJsonAsync("/api/v1/properties/1/devices/8/transfer", new DeviceTransferRequest(3));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Growatt", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/properties/2/devices/7/transfer", new DeviceTransferRequest(3))).StatusCode);
        Assert.Contains("transfer:1:7:3", Fake.Calls);
        Assert.Contains("delete:1:Fixture", Fake.Calls);
    }

    private sealed class Fake : IPropertyManagement
    {
        public static readonly ConcurrentBag<string> Calls = [];

        public Task<PropertyManagementView> ListAsync(string user, CancellationToken ct) =>
            Task.FromResult(new PropertyManagementView(false, [new(1, "Fixture", null, "Europe/Sofia", "Viewer", false, false, 0, 1, 0, null)]));
        public Task<ManagementResult> CreateAsync(string user, CreatePropertyRequest request, CancellationToken ct) =>
            Task.FromResult(request.Name == "forbidden" ? ManagementResult.Fail(ManagementStatus.Forbidden, "No.") : ManagementResult.Done(9));
        public Task<ManagementResult> UpdateAsync(string user, long property, UpdatePropertyRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(property));
        public Task<PropertyDeletionImpact?> DeletionImpactAsync(string user, long property, CancellationToken ct) =>
            Task.FromResult<PropertyDeletionImpact?>(new(property, "Fixture", 0, 1, 0, 0, 1, 0, 0, false));
        public Task<ManagementResult> DeleteAsync(string user, long property, DeletePropertyRequest request, CancellationToken ct)
        {
            Calls.Add($"delete:{property}:{request.ConfirmName}");
            return Task.FromResult(request.ConfirmName switch
            {
                "conflict" => ManagementResult.Fail(ManagementStatus.Conflict, "The property still has 1 device."),
                "invalid" => ManagementResult.Fail(ManagementStatus.Invalid, "Type the exact name."),
                _ => ManagementResult.Done(property)
            });
        }
        public Task<IReadOnlyList<RoomView>?> RoomsAsync(string user, long property, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RoomView>?>([]);
        public Task<ManagementResult> CreateRoomAsync(string user, long property, RoomRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(5));
        public Task<ManagementResult> UpdateRoomAsync(string user, long property, long room, RoomRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(room));
        public Task<ManagementResult> DeleteRoomAsync(string user, long property, long room, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(room));
        public Task<ManagementResult> RenameDeviceAsync(string user, long property, long device, DeviceNameRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(device));
        public Task<ManagementResult> SetDeviceRoomAsync(string user, long property, long device, DeviceRoomRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(device));
        public Task<IReadOnlyList<DeviceAuditEntry>?> DeviceAuditAsync(string user, long property, long device, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeviceAuditEntry>?>([]);
        public Task<IReadOnlyList<TransferableDevice>> TransferableAsync(string user, long property, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TransferableDevice>>([]);
        public Task<DeviceTransferImpact?> TransferImpactAsync(string user, long property, long device, long destination, CancellationToken ct) =>
            Task.FromResult<DeviceTransferImpact?>(new(device, "Boiler", "ewelink", property, "Fixture", destination, "Other", null, 0, null));
        public Task<ManagementResult> TransferAsync(string user, long property, long device, DeviceTransferRequest request, CancellationToken ct)
        {
            Calls.Add($"transfer:{property}:{device}:{request.DestinationPropertyId}");
            return Task.FromResult(device == 8
                ? ManagementResult.Fail(ManagementStatus.Conflict, "Growatt devices follow the property's Growatt site configuration.")
                : ManagementResult.Done(70));
        }
    }
}
