using System.Globalization;
using System.Text.Json;
using HomeAutomation.Domain;

namespace HomeAutomation.Infrastructure.Daikin;

public sealed record DaikinRange(decimal Min, decimal Max, decimal Step)
{
    public bool Allows(decimal value) => Step > 0 && value >= Min && value <= Max && (value - Min) % Step == 0;
}

public sealed record DaikinControls(string[] Power, string[] Modes, DaikinRange? Temperature, DaikinRange? Fan);
public enum DaikinEquipment { Unknown, AirConditioner, HeatPump }
public sealed record DaikinWater(decimal? Leaving, decimal? Target, DaikinRange? TargetRange,
    string? ControlMode, string? SetpointMode, bool? Warning, bool? Emergency,
    string? ProviderUnit = null, bool? TargetAdvertisedWritable = null);

internal sealed record DaikinDevice(string Gateway, string Point, string Name, string Model, Connectivity Connectivity,
    DateTimeOffset ObservedAt, DateTimeOffset? SourceAt, string? Power, string? Mode, decimal? Indoor, decimal? Outdoor,
    decimal? Target, decimal? Humidity, decimal? Fan, string? FanMode, DaikinControls Controls, string? Fault,
    DaikinEquipment Equipment = DaikinEquipment.Unknown, DaikinWater? Water = null, bool? Available = null,
    DateTimeOffset? GatewayTimestamp = null)
{
    public string ExternalId => Gateway + ":" + Point;
}

public sealed record DaikinCommand(string Operation, string Value);
internal sealed record DaikinChange(string Characteristic, string? Path, object Value, bool AlreadyApplied);

internal static class DaikinMapping
{
    public static DaikinControls ReadOnlyControls { get; } = new([], [], null, null);

    public static IReadOnlyList<DaikinDevice> Discover(JsonElement body, DateTimeOffset received)
    {
        if (body.ValueKind != JsonValueKind.Array) throw new DaikinFailure("invalidResponse");
        var devices = new Dictionary<string, DaikinDevice>(StringComparer.Ordinal);
        foreach (var gateway in body.EnumerateArray())
        {
            var id = gateway.Node("_id").Text();
            if (!SafeId(id)) continue;
            if (gateway.Node("managementPoints").ValueKind != JsonValueKind.Array)
                throw new DaikinFailure("invalidResponse");
            var timestamp = gateway.Node("lastUpdateReceived");
            var source = Timestamp(timestamp.ValueKind == JsonValueKind.Object ? timestamp.Value() : timestamp, received);
            var reachability = gateway.Node("isCloudConnectionUp").Value().ValueKind switch
            {
                JsonValueKind.True => Connectivity.Online,
                JsonValueKind.False => Connectivity.Offline,
                _ => Connectivity.Unknown
            };
            var managementPoints = gateway.Node("managementPoints");
            var gatewayType = gateway.Node("type").Text();
            var hydronicGateway = gatewayType == "heating-wlan" || managementPoints.EnumerateArray().Any(point =>
                point.Node("managementPointType").Text() == "indoorUnitHydro");
            var gatewayModel = gateway.Node("deviceModel").Text();
            var airGateway = gatewayModel is "air-to-air-heat-pump" or "air-conditioner" ||
                gatewayType is "dx23" or "dx4" && gatewayModel == gatewayType &&
                managementPoints.EnumerateArray().Any(point => point.Node("managementPointType").Text() == "indoorUnit");
            var gatewayTimestamp = Timestamp(gateway.Node("timestamp"), received);
            foreach (var point in managementPoints.EnumerateArray())
            {
                var pointId = point.Node("embeddedId").Text();
                if (point.Node("managementPointType").Text() != "climateControl" || !SafeId(pointId)) continue;

                var mode = point.Node("operationMode").Value().Text();
                var power = point.Node("onOffMode").Value().Text();
                var modes = point.Node("temperatureControl").Value().Node("operationModes");
                var setpoint = modes.Node(mode).Node("setpoints").Node("roomTemperature");
                var fan = point.Node("fanControl").Value().Node("operationModes").Node(mode).Node("fanSpeed");
                var fanMode = fan.Node("currentMode").Value().Text();
                var speed = fan.Node("modes").Node("fixed");
                var sensors = point.Node("sensoryData").Value();
                var leaving = sensors.Node("leavingWaterTemperature");
                var heatingWater = modes.Node("heating").Node("setpoints").Node("leavingWaterTemperature");
                var equipment = hydronicGateway || leaving.ValueKind == JsonValueKind.Object ||
                    heatingWater.ValueKind == JsonValueKind.Object ? DaikinEquipment.HeatPump
                    : airGateway ? DaikinEquipment.AirConditioner
                    : DaikinEquipment.Unknown;
                var water = equipment == DaikinEquipment.HeatPump
                    ? new DaikinWater(Celsius(leaving), Celsius(heatingWater),
                        IsCelsius(heatingWater) ? Range(heatingWater, requireSettable: false) : null,
                        Label(point.Node("controlMode").Value().Text(), 32), Label(point.Node("setpointMode").Value().Text(), 32),
                        Flag(point.Node("isInWarningState").Value()), Flag(point.Node("isInEmergencyState").Value()),
                        IsCelsius(leaving) ? leaving.Node("unit").Text() :
                        IsCelsius(heatingWater) ? heatingWater.Node("unit").Text() : null,
                        Flag(heatingWater.Node("settable")))
                    : null;
                var fault = point.Node("isInErrorState").Value().ValueKind == JsonValueKind.True
                    ? Label(point.Node("errorCode").Value().Text(), 64) ?? "DEVICE_FAULT" : null;
                var device = new DaikinDevice(id!, pointId!, Label(point.Node("name").Value().Text(), 128) ?? "Daikin climate device",
                    Label(gateway.Node("deviceModel").Text(), 128) ?? Label(point.Node("modelInfo").Value().Text(), 128) ?? "",
                    reachability, source ?? received, source, Label(power, 32), Label(mode, 32),
                    sensors.Node("roomTemperature").Value().Number(), sensors.Node("outdoorTemperature").Value().Number(),
                    setpoint.Value().Number(), Percent(sensors.Node("roomHumidity").Value().Number()),
                    fanMode == "fixed" && speed.Value().Number() is >= 0 and var fanStep ? fanStep : null, Label(fanMode, 32),
                    equipment == DaikinEquipment.AirConditioner
                        ? new DaikinControls(Values(point.Node("onOffMode"), "on", "off"), Values(point.Node("operationMode"), "heating", "cooling"),
                            mode is "heating" or "cooling" && IsCelsius(setpoint) ? Range(setpoint) : null,
                            fanMode == "fixed" ? Range(speed) : null)
                        : ReadOnlyControls, fault, equipment, water, Available: true, GatewayTimestamp: gatewayTimestamp);
                if (!devices.TryAdd(device.ExternalId, device)) throw new DaikinFailure("duplicateIdentity");
            }
        }
        return devices.Values.ToArray();
    }

    public static DaikinChange? Validate(DaikinDevice device, DaikinCommand command)
    {
        if (device.Equipment != DaikinEquipment.AirConditioner || command.Value is null or { Length: > 32 }) return null;
        return command.Operation switch
        {
            "power" when device.Controls.Power.Contains(command.Value) =>
                new("onOffMode", null, command.Value, device.Power == command.Value),
            "mode" when device.Controls.Modes.Contains(command.Value) =>
                new("operationMode", null, command.Value, device.Mode == command.Value),
            "temperature" when Numeric(command.Value, device.Controls.Temperature) is { } temperature =>
                new("temperatureControl", $"/operationModes/{device.Mode}/setpoints/roomTemperature", temperature, device.Target == temperature),
            "fan" when Numeric(command.Value, device.Controls.Fan) is { } speed &&
                device.Mode is "heating" or "cooling" or "auto" or "fanOnly" or "dry" =>
                new("fanControl", $"/operationModes/{device.Mode}/fanSpeed/modes/fixed", speed, device.Fan == speed),
            _ => null
        };
    }

    private static bool IsCelsius(JsonElement characteristic) => characteristic.Node("unit").Text() is "C" or "°C";

    private static decimal? Celsius(JsonElement characteristic) => IsCelsius(characteristic) ? characteristic.Value().Number() : null;

    private static bool? Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static decimal? Numeric(string value, DaikinRange? range) =>
        range is not null && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result) && range.Allows(result) ? result : null;

    private static DaikinRange? Range(JsonElement node, bool requireSettable = true) => (!requireSettable || node.Settable()) &&
        node.Node("minValue").Number() is >= -100 and <= 200 and var min &&
        node.Node("maxValue").Number() is >= -100 and <= 200 and var max &&
        node.Node("stepValue").Number() is > 0 and <= 100 and var step && min <= max ? new(min, max, step) : null;

    private static string[] Values(JsonElement node, params string[] allowed)
    {
        var values = node.Node("values");
        return node.Settable() && values.ValueKind == JsonValueKind.Array
            ? allowed.Where(value => values.EnumerateArray().Any(item => item.Text() == value)).ToArray() : [];
    }

    private static DateTimeOffset? Timestamp(JsonElement value, DateTimeOffset received) =>
        value.Text() is { Length: >= 20 } text && (text.EndsWith('Z') || text[^6] is '+' or '-') &&
            value.TryGetDateTimeOffset(out var at) &&
            at > DateTimeOffset.UnixEpoch && at <= received ? at.ToUniversalTime() : null;

    private static decimal? Percent(decimal? value) => value is >= 0 and <= 100 ? value : null;
    private static bool SafeId(string? value) => value is { Length: > 0 and <= 63 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string? Label(string? value, int max) => value is not null && !string.IsNullOrWhiteSpace(value) &&
        value.Length <= max && !value.Any(char.IsControl) ? value : null;

    internal static JsonElement Node(this JsonElement node, string? key) => key is not null &&
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    internal static JsonElement Value(this JsonElement node) => node.Node("value");
    internal static string? Text(this JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    internal static decimal? Number(this JsonElement node) =>
        node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out var number) ? number : null;
    private static bool Settable(this JsonElement node) => node.Node("settable").ValueKind == JsonValueKind.True;
}
