using System.Globalization;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Ewelink;

internal sealed record EwelinkMeasurement(double Value, DateTimeOffset ReceivedAt, bool Present = true);

internal sealed record EwelinkDevice(string ExternalId, string Name, string Model, string Brand, bool Shared,
    Connectivity Connectivity, DateTimeOffset SeenAt, bool HasRelay, bool? Relay, DateTimeOffset? RelayAt,
    IReadOnlyDictionary<Metric, EwelinkMeasurement>? Measurements = null)
{
    public (DiscoveredDevice Inventory, ObservationBatch Batch) Telemetry(bool includeRelay)
    {
        var capabilities = new HashSet<Capability>();
        if (HasRelay) capabilities.Add(Capability.Switch);
        foreach (var metric in (Measurements ?? EmptyMeasurements).Keys) capabilities.Add(EwelinkMapping.CapabilityFor(metric));
        var kind = HasRelay ? DeviceKind.SmartSwitch : capabilities.Count > 0 ? DeviceKind.Sensor : DeviceKind.Unknown;
        var inventory = new DiscoveredDevice(ExternalId, Name, kind, "", Brand, Model, [new("main", "Device", capabilities)]);
        var samples = includeRelay && Relay is { } relay && RelayAt is { } at ? new[] { Sample.Boolean(Metric.SwitchOn, at, relay) } : [];
        return (inventory, new(SeenAt, [new(ExternalId, Connectivity, null, [new("main", samples)], SeenAt)], []));
    }

    private static readonly IReadOnlyDictionary<Metric, EwelinkMeasurement> EmptyMeasurements =
        new Dictionary<Metric, EwelinkMeasurement>();
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
        var measurements = online == Connectivity.Online ? ReadMeasurements(Uiid(data), parameters, received) : null;
        return new(id, Text(data, "name", 128) ?? "eWeLink device",
            Text(data, "productModel", 96) ?? Text(Node(data, "extra"), "modelInfo", 96) ?? "",
            Text(data, "brandName", 64) ?? "", type == 2, online, received, relay.HasValue,
            live ? relay : null, live ? received : null, measurements);
    }

    public static EwelinkDevice Merge(EwelinkDevice next, EwelinkDevice? previous)
    {
        if (previous?.Measurements is { Count: > 0 } previousMeasurements)
        {
            var merged = next.Measurements?.ToDictionary() ?? [];
            foreach (var (metric, last) in previousMeasurements)
            {
                if (!merged.TryGetValue(metric, out var reading))
                    merged.Add(metric, last with { Present = false });
                else if (reading.Value == last.Value)
                    merged[metric] = reading with { ReceivedAt = last.ReceivedAt };
            }
            next = next with { Measurements = merged };
        }
        return next.RelayAt is not null ? next : next with
        {
            HasRelay = next.HasRelay || previous?.HasRelay == true, Relay = previous?.Relay, RelayAt = previous?.RelayAt
        };
    }

    public static int? Uiid(JsonElement data) => Integer(Node(data, "extra"), "uiid");

    public static Capability CapabilityFor(Metric metric) => metric switch
    {
        Metric.Temperature => Capability.TemperatureMeasurement,
        Metric.Humidity => Capability.HumidityMeasurement,
        Metric.ActivePower => Capability.PowerMeasurement,
        Metric.Voltage => Capability.VoltageMeasurement,
        Metric.Current => Capability.CurrentMeasurement,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private static IReadOnlyDictionary<Metric, EwelinkMeasurement>? ReadMeasurements(int? uiid, JsonElement parameters, DateTimeOffset received)
    {
        var values = new Dictionary<Metric, EwelinkMeasurement>();
        if (uiid is 15 or 181)
        {
            var sensor = Text(parameters, "sensorType", 16);
            if (sensor is "DHT11" or "DS18B20" or "AM2301" or "THS01")
                Add(Metric.Temperature, "currentTemperature", -55, 125);
            if (sensor is "DHT11" or "AM2301" or "THS01")
                Add(Metric.Humidity, "currentHumidity", 0, 100);
        }
        else if (uiid is 5 or 32)
        {
            Add(Metric.ActivePower, "power", 0, double.MaxValue);
            Add(Metric.Voltage, "voltage", 0, double.MaxValue);
            Add(Metric.Current, "current", 0, double.MaxValue);
        }
        return values.Count == 0 ? null : values;

        void Add(Metric metric, string key, double minimum, double maximum)
        {
            if (double.TryParse(Text(parameters, key, 24), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum)
                values.Add(metric, new(number, received));
        }
    }

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
