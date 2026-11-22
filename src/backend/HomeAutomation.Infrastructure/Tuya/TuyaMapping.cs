using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Tuya;

internal sealed record TuyaSeed(string Id, string Name, string ProductId, string Model, Connectivity Connectivity,
    DateTimeOffset ReceivedAt, DateTimeOffset? ReportedAt, JsonElement Status);

internal sealed record TuyaPoint(string Code, Metric Metric, Capability Capability, string? ProviderUnit,
    int Scale, long? Min, long? Max);

internal sealed record TuyaReading(string Code, double? Number, bool? Flag, DateTimeOffset? At, bool InLatestReport);

internal sealed record TuyaDevice(string Id, string Name, string ProductId, string Model, Connectivity Connectivity,
    DateTimeOffset ReceivedAt, DateTimeOffset? ReportedAt, DateTimeOffset? SpecificationAttemptAt,
    bool SpecificationAvailable, TuyaPoint[] Points, TuyaReading[] Readings, bool Present = true)
{
    public (DiscoveredDevice Inventory, ObservationBatch Batch) Telemetry(bool includeLastKnown = false)
    {
        var endpoints = Points.Select(point => new DeviceEndpoint(point.Code, point.Code, [point.Capability])).ToArray();
        var kind = Points.Length == 0 ? DeviceKind.Unknown :
            Points.Any(point => point.Metric == Metric.SwitchOn) ? DeviceKind.SmartSwitch : DeviceKind.Sensor;
        var device = new DiscoveredDevice(Id, Name, kind, "", "Tuya", Model,
            endpoints.Length == 0 ? [new("device", "Device", [])] : endpoints);
        var readings = Readings.Where(reading => reading.At is not null && (includeLastKnown || reading.InLatestReport))
            .ToDictionary(reading => reading.Code, StringComparer.Ordinal);
        var observations = Points.Select(point =>
        {
            var samples = readings.TryGetValue(point.Code, out var reading)
                ? new[] { reading.Flag is { } flag ? Sample.Boolean(Metric.SwitchOn, reading.At!.Value, flag) :
                    Sample.Numeric(point.Metric, reading.At!.Value, reading.Number!.Value) }
                : [];
            return new EndpointObservation(point.Code, samples);
        }).ToArray();
        return (device, new(ReceivedAt, [new(Id, Connectivity, null, observations, ReceivedAt)], []));
    }
}

internal static class TuyaMapping
{
    public static TuyaSeed? Device(JsonElement data, DateTimeOffset received)
    {
        var id = TuyaJson.Text(data, "id", 96);
        if (!TuyaJson.IsId(id)) return null;
        var online = TuyaJson.Get(data, "online").ValueKind switch
        {
            JsonValueKind.True => Connectivity.Online,
            JsonValueKind.False => Connectivity.Offline,
            _ => Connectivity.Unknown
        };
        var seconds = TuyaJson.Long(data, "update_time");
        DateTimeOffset? reported = seconds > 0 && seconds <= received.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
        return new(id!, TuyaJson.Text(data, "name", 128) ?? "Tuya device",
            TuyaJson.Text(data, "product_id", 96) ?? "", TuyaJson.Text(data, "product_name", 96) ?? "",
            online, received, reported, TuyaJson.Get(data, "status"));
    }

    public static TuyaPoint[] Specifications(JsonElement result)
    {
        var status = TuyaJson.Get(result, "status");
        if (status.ValueKind != JsonValueKind.Array) throw new TuyaFailure("invalidResponse");
        return status.EnumerateArray().GroupBy(item => TuyaJson.Text(item, "code", 64), StringComparer.Ordinal)
            .Where(group => group.Key is not null && group.Count() == 1)
            .Select(group => Point(group.Key!, group.Single())).OfType<TuyaPoint>().ToArray();
    }

    private static TuyaPoint? Point(string code, JsonElement item)
    {
        var type = TuyaJson.Text(item, "type", 24);
        if (code is "switch" or "switch_1" or "switch_2" or "switch_3" or "switch_4" or "switch_5" or "switch_6")
            return type == "Boolean" ? new(code, Metric.SwitchOn, Capability.Switch, null, 0, null, null) : null;
        if (type != "Integer" || TuyaJson.Text(item, "values", 4096) is not { } constraints) return null;
        try
        {
            using var doc = JsonDocument.Parse(constraints);
            var values = doc.RootElement;
            var unit = TuyaJson.Text(values, "unit", 16);
            if (TuyaJson.Int(values, "scale") is not (>= 0 and <= 9) || unit is null) return null;
            var scale = TuyaJson.Int(values, "scale")!.Value;
            var min = TuyaJson.Long(values, "min");
            var max = TuyaJson.Long(values, "max");
            if (min > max) return null;
            return (code, unit) switch
            {
                ("va_temperature" or "temp_current" or "temp_current_f", "℃" or "°C" or "°F" or "℉") =>
                    new(code, Metric.Temperature, Capability.TemperatureMeasurement, unit, scale, min, max),
                ("va_humidity" or "humidity_value" or "humidity_current" or "humidity", "%" or "%RH" or "% RH") =>
                    new(code, Metric.Humidity, Capability.HumidityMeasurement, unit, scale, min, max),
                ("battery_percentage" or "temp_hum_battery_percentage", "%") =>
                    new(code, Metric.BatterySoc, Capability.BatteryLevel, unit, scale, min, max),
                ("cur_voltage", "V" or "v") => new(code, Metric.Voltage, Capability.VoltageMeasurement, unit, scale, min, max),
                ("cur_current", "A" or "mA") => new(code, Metric.Current, Capability.CurrentMeasurement, unit, scale, min, max),
                ("cur_power" or "work_power", "W" or "w") => new(code, Metric.ActivePower, Capability.PowerMeasurement, unit, scale, min, max),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    public static TuyaDevice Snapshot(TuyaSeed seed, TuyaDevice? prior, TuyaPoint[] points,
        DateTimeOffset? attemptedAt, bool specificationAvailable)
    {
        var status = seed.Status.ValueKind == JsonValueKind.Array
            ? seed.Status.EnumerateArray().ToLookup(item => TuyaJson.Text(item, "code", 64), StringComparer.Ordinal)
            : null;
        var old = prior?.ProductId == seed.ProductId ? prior.Readings.ToDictionary(reading => reading.Code, StringComparer.Ordinal) : [];
        var oldPoints = prior?.ProductId == seed.ProductId ? prior.Points.ToDictionary(point => point.Code, StringComparer.Ordinal) : [];
        var readings = new List<TuyaReading>();
        foreach (var point in points)
        {
            old.TryGetValue(point.Code, out var previous);
            if (!oldPoints.TryGetValue(point.Code, out var oldPoint) || oldPoint != point) previous = null;
            var entry = status?[point.Code].Take(2).ToArray();
            var current = specificationAvailable && seed.Connectivity == Connectivity.Online && entry is [var value]
                ? Read(point, TuyaJson.Get(value, "value"), seed.ReportedAt) : null;
            if (previous?.At is { } earlier && (current?.At is not { } updated || updated < earlier ||
                updated == earlier && (current.Number != previous.Number || current.Flag != previous.Flag)))
                current = null;
            if (current is not null) readings.Add(current);
            else if (previous is not null) readings.Add(previous with { InLatestReport = false });
        }
        return new(seed.Id, seed.Name, seed.ProductId, seed.Model, seed.Connectivity, seed.ReceivedAt,
            seed.ReportedAt, attemptedAt, specificationAvailable, points, readings.ToArray());
    }

    private static TuyaReading? Read(TuyaPoint point, JsonElement value, DateTimeOffset? at)
    {
        if (point.Metric == Metric.SwitchOn)
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(point.Code, null, value.GetBoolean(), at, true) : null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var raw) || raw < point.Min || raw > point.Max)
            return null;
        var scaled = raw / Math.Pow(10, point.Scale);
        var number = point.ProviderUnit switch
        {
            "°F" or "℉" => (scaled - 32) * 5 / 9,
            "mA" => scaled / 1000,
            _ => scaled
        };
        if (point.Metric is Metric.BatterySoc or Metric.Humidity && number is < 0 or > 100 ||
            point.Metric is Metric.Current or Metric.Voltage && number < 0) return null;
        return new(point.Code, number, null, at, true);
    }
}

internal static class TuyaJson
{
    public static JsonElement Get(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    public static string? Text(JsonElement parent, string name, int max) =>
        Get(parent, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text &&
        text.Length <= max && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text : null;
    public static bool IsId(string? value) => value is { Length: > 0 and <= 96 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    public static int? Int(JsonElement parent, string name) => Get(parent, name) is { ValueKind: JsonValueKind.Number } value &&
        value.TryGetInt32(out var number) ? number : null;
    public static long? Long(JsonElement parent, string name) => Get(parent, name) is { ValueKind: JsonValueKind.Number } value &&
        value.TryGetInt64(out var number) ? number : null;
}
