using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Ewelink;

internal sealed record EwelinkDevice(string ExternalId, string Name, string Model, string Brand, bool Shared,
    Connectivity Connectivity, DateTimeOffset SeenAt, bool HasRelay, bool? Relay, DateTimeOffset? RelayAt)
{
    public (DiscoveredDevice Inventory, ObservationBatch Batch) Telemetry(bool includeRelay)
    {
        var inventory = new DiscoveredDevice(ExternalId, Name, HasRelay ? DeviceKind.SmartSwitch : DeviceKind.Unknown,
            "", Brand, Model, [new("main", "Device", HasRelay ? [Capability.Switch] : [])]);
        var samples = includeRelay && Relay is { } relay && RelayAt is { } at ? new[] { Sample.Boolean(Metric.SwitchOn, at, relay) } : [];
        return (inventory, new(SeenAt, [new(ExternalId, Connectivity, null, [new("main", samples)], SeenAt)], []));
    }
}

internal static class EwelinkMapping
{
    public static EwelinkDevice? Map(JsonElement item, DateTimeOffset received)
    {
        var type = Integer(item, "itemType");
        if (type is not (1 or 2)) return null;
        var data = Node(item, "itemData");
        if (Id(data, "deviceid") is not { } id) return null;
        var online = Node(data, "online").ValueKind switch
        {
            JsonValueKind.True => Connectivity.Online,
            JsonValueKind.False => Connectivity.Offline,
            _ => Connectivity.Unknown
        };
        var parameters = Node(data, "params");
        // The public OAuth contract identifies switch as the single-channel status.
        bool? relay = Node(parameters, "switches").ValueKind != JsonValueKind.Undefined ? null :
            Text(parameters, "switch", 3) switch { "on" => true, "off" => false, _ => null };
        var live = relay.HasValue && online == Connectivity.Online;
        return new(id, Text(data, "name", 128) ?? "eWeLink device",
            Text(data, "productModel", 96) ?? Text(Node(data, "extra"), "modelInfo", 96) ?? "",
            Text(data, "brandName", 64) ?? "", type == 2, online, received, relay.HasValue,
            live ? relay : null, live ? received : null);
    }

    public static EwelinkDevice Merge(EwelinkDevice next, EwelinkDevice? previous) =>
        next.RelayAt is not null ? next : next with
        {
            HasRelay = next.HasRelay || previous?.HasRelay == true, Relay = previous?.Relay, RelayAt = previous?.RelayAt
        };

    public static JsonElement Node(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) ? value : default;
    public static JsonElement Array(JsonElement data, string name) => Node(data, name) is
        { ValueKind: JsonValueKind.Array } value ? value : throw new EwelinkFailure("invalidResponse");
    public static string? Text(JsonElement data, string name, int max) => Node(data, name) is { ValueKind: JsonValueKind.String } value &&
        value.GetString() is { Length: > 0 } text && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    public static string? Id(JsonElement data, string name) => Text(data, name, 96) is { } id &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? id : null;
    public static int? Integer(JsonElement data, string name) => Node(data, name) is { ValueKind: JsonValueKind.Number } value &&
        value.TryGetInt32(out var number) ? number : null;
}
