using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Ewelink;

internal sealed record EwelinkMeasurement(double Value, DateTimeOffset ReceivedAt, bool Present = true, bool FromSocket = false,
    DateTimeOffset? LastSocketAt = null)
{
    [JsonIgnore] public DateTimeOffset? SocketTime => FromSocket ? ReceivedAt : LastSocketAt;
}

internal sealed record EwelinkDevice(string ExternalId, string Name, string Model, string Brand, bool Shared,
    Connectivity Connectivity, DateTimeOffset SeenAt, bool HasRelay, bool? Relay, DateTimeOffset? RelayAt,
    IReadOnlyDictionary<Metric, EwelinkMeasurement>? Measurements = null, string? SensorType = null,
    int? Uiid = null, bool ProtectedRelay = false, bool? SocketRelay = null, DateTimeOffset? SocketRelayAt = null)
{
    [JsonIgnore] public bool SupportsRelayWrites => HasRelay && !Shared && Uiid == 32 && Model == "PSF-X67";
    [JsonIgnore] public bool RequiresRelayConfirmation => ProtectedRelay || EwelinkMapping.IsProtectedRelayName(Name);

    public IReadOnlyList<Metric> SupportedMetrics()
    {
        var metrics = Measurements?.Keys.ToHashSet() ?? [];
        if (EwelinkMapping.MeasuresTemperature(SensorType)) metrics.Add(Metric.Temperature);
        if (EwelinkMapping.MeasuresHumidity(SensorType)) metrics.Add(Metric.Humidity);
        return metrics.Order().ToArray();
    }

    public (DiscoveredDevice Inventory, ObservationBatch Batch) Telemetry(bool includeRelay, IReadOnlyList<Sample>? observations = null)
    {
        var capabilities = new HashSet<Capability>();
        if (HasRelay) capabilities.Add(Capability.Switch);
        foreach (var metric in SupportedMetrics()) capabilities.Add(EwelinkMapping.CapabilityFor(metric));
        var kind = HasRelay ? DeviceKind.SmartSwitch : capabilities.Count > 0 ? DeviceKind.Sensor : DeviceKind.Unknown;
        var inventory = new DiscoveredDevice(ExternalId, Name, kind, "", Brand, Model, [new("main", "Device", capabilities)]);
        var samples = observations ?? (includeRelay && Relay is { } relay && RelayAt is { } at
            ? [Sample.Boolean(Metric.SwitchOn, at, relay)] : []);
        return (inventory, new(SeenAt, [new(ExternalId, Connectivity, null, [new("main", samples)], SeenAt)], []));
    }
}

internal static class EwelinkMapping
{
    internal static readonly string[] ElectricalFields = ["power", "voltage", "current"];

    public static bool HasElectricalFields(JsonElement parameters) =>
        ElectricalFields.Any(key => Node(parameters, key).ValueKind != JsonValueKind.Undefined);

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
        var model = Model(data);
        var uiid = Uiid(data);
        var climate = uiid is 15 or 181 || (uiid is null && model is ("THR320D" or "THR316D"));
        var sensor = climate ? Text(parameters, "sensorType", 16) switch
        {
            null => null,
            "DHT11" => "DHT11",
            "DS18B20" => "DS18B20",
            "AM2301" => "AM2301",
            "THS01" => "THS01",
            _ => "unsupported"
        } : null;
        // The public OAuth contract identifies switch as the single-channel status.
        var relay = ReadRelay(parameters);
        var live = relay.HasValue && online == Connectivity.Online;
        var measurements = online == Connectivity.Online ? ReadMeasurements(uiid, climate, sensor, parameters, received) : null;
        var name = Text(data, "name", 128) ?? "eWeLink device";
        return new(id, name,
            model,
            Text(data, "brandName", 64) ?? "", type == 2, online, received, relay.HasValue,
            live ? relay : null, live ? received : null, measurements, sensor, uiid, IsProtectedRelayName(name));
    }

    public static EwelinkDevice Merge(EwelinkDevice next, EwelinkDevice? previous)
    {
        var sensor = next.SensorType ?? previous?.SensorType;
        if (next.Measurements is not null || previous?.Measurements is not null)
        {
            var merged = next.Measurements?.ToDictionary() ?? [];
            if (sensor is not null && !MeasuresTemperature(sensor)) merged.Remove(Metric.Temperature);
            if (sensor is not null && !MeasuresHumidity(sensor)) merged.Remove(Metric.Humidity);
            foreach (var (metric, last) in previous?.Measurements ?? Enumerable.Empty<KeyValuePair<Metric, EwelinkMeasurement>>())
            {
                if (sensor is not null &&
                    (metric == Metric.Temperature && !MeasuresTemperature(sensor) ||
                     metric == Metric.Humidity && !MeasuresHumidity(sensor)))
                    continue;
                if (!merged.TryGetValue(metric, out var reading))
                    merged.Add(metric, last with { Present = last.FromSocket && last.Present });
                else if (last.FromSocket && EwelinkElectricalReporting.IsElectrical(metric) &&
                         next.SeenAt - last.ReceivedAt < EwelinkSocketProtocol.Freshness)
                    merged[metric] = last;
                else if (reading.Value == last.Value)
                    merged[metric] = last with { Present = true };
                else if (EwelinkElectricalReporting.IsElectrical(metric))
                    merged[metric] = reading with { LastSocketAt = last.SocketTime };
            }
            next = next with { Measurements = merged.Count == 0 ? null : merged };
        }
        next = next with { SensorType = sensor, ProtectedRelay = next.RequiresRelayConfirmation || previous?.RequiresRelayConfirmation == true };
        return next.RelayAt is not null ? next : next with
        {
            HasRelay = next.HasRelay || previous?.HasRelay == true, Relay = previous?.Relay, RelayAt = previous?.RelayAt,
            SocketRelay = previous?.SocketRelay, SocketRelayAt = previous?.SocketRelayAt
        };
    }

    public static (EwelinkDevice Snapshot, IReadOnlyList<Sample> Samples) MergeEvent(
        EwelinkDevice device, JsonElement parameters, DateTimeOffset at, EwelinkSocketTrace? trace = null)
    {
        if (device.Connectivity != Connectivity.Online)
        {
            trace?.Count("reject.deviceOffline");
            return (device, []);
        }
        var samples = new List<Sample>();
        var climate = device.Uiid == 181 && device.Model == "THR320D";
        var meter = EwelinkElectricalReporting.IsMeter(device);
        if (!climate && !meter)
        {
            trace?.Count("reject.unsupportedDevice");
            return (device, []);
        }

        var measurements = device.Measurements?.ToDictionary() ?? [];
        foreach (var (metric, reading) in ReadMeasurements(device.Uiid, climate, device.SensorType, parameters, at, trace)
                     ?? Enumerable.Empty<KeyValuePair<Metric, EwelinkMeasurement>>())
        {
            var electrical = meter && EwelinkElectricalReporting.IsElectrical(metric);
            measurements.TryGetValue(metric, out var previous);
            var watermark = electrical ? previous?.SocketTime : previous?.ReceivedAt;
            if ((!electrical && at <= device.SeenAt) || watermark >= at)
            {
                if (electrical) trace?.Count(watermark == at ? "reject.electricalDuplicate" : "reject.electricalOutOfOrder");
                continue;
            }
            measurements[metric] = reading with { FromSocket = true };
            samples.Add(Sample.Numeric(metric, at, reading.Value));
            if (electrical) trace?.Count("mappedElectricalSamples");
        }
        if (at > device.SeenAt && device.HasRelay && ReadRelay(parameters) is { } relay &&
            (device.RelayAt is null || at > device.RelayAt) && (device.SocketRelayAt is null || at > device.SocketRelayAt))
        {
            device = device with { SocketRelay = relay, SocketRelayAt = at };
            samples.Add(Sample.Boolean(Metric.SwitchOn, at, relay));
        }
        return (device with { Measurements = measurements }, samples);
    }

    public static int? Uiid(JsonElement data) => Integer(Node(data, "extra"), "uiid");
    public static bool IsProtectedRelayName(string name) =>
        string.Equals(name.Trim(), "Inverter In", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name.Trim(), "Inverter out", StringComparison.OrdinalIgnoreCase);

    public static bool? ReadRelay(JsonElement parameters) =>
        Node(parameters, "switches").ValueKind != JsonValueKind.Undefined ? null :
            Text(parameters, "switch", 3) switch { "on" => true, "off" => false, _ => null };
    public static string Model(JsonElement data) =>
        Text(data, "productModel", 96) ?? Text(Node(data, "extra"), "modelInfo", 96) ?? "";

    public static bool MeasuresTemperature(string? sensor) => sensor is "DHT11" or "DS18B20" or "AM2301" or "THS01";
    public static bool MeasuresHumidity(string? sensor) => sensor is "DHT11" or "AM2301" or "THS01";

    public static Capability CapabilityFor(Metric metric) => metric switch
    {
        Metric.Temperature => Capability.TemperatureMeasurement,
        Metric.Humidity => Capability.HumidityMeasurement,
        Metric.ActivePower => Capability.PowerMeasurement,
        Metric.Voltage => Capability.VoltageMeasurement,
        Metric.Current => Capability.CurrentMeasurement,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private static IReadOnlyDictionary<Metric, EwelinkMeasurement>? ReadMeasurements(
        int? uiid, bool climate, string? sensor, JsonElement parameters, DateTimeOffset received, EwelinkSocketTrace? trace = null)
    {
        var values = new Dictionary<Metric, EwelinkMeasurement>();
        if (climate)
        {
            if (sensor is null || MeasuresTemperature(sensor))
                Add(Metric.Temperature, "currentTemperature", -55, 125);
            if (MeasuresHumidity(sensor))
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
            else if (EwelinkElectricalReporting.IsElectrical(metric) && Node(parameters, key).ValueKind != JsonValueKind.Undefined)
                trace?.Count($"reject.electricalValue.{key}");
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
