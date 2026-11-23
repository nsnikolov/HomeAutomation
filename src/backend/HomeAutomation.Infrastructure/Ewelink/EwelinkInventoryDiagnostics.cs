using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.Ewelink;

internal static class EwelinkInventoryDiagnostics
{
    private static readonly string[] MeasurementFields =
    [
        "switch", "switches", "currentTemperature", "currentHumidity", "sensorType", "tempUnit",
        "power", "voltage", "current", "actPow", "electricity", "oneKwhData", "hundredDaysKwhData",
        "dayKwh", "totalKwh", "updateTime", "timestamp"
    ];

    public static void LogPage(ILogger logger, JsonElement[] page, int? total)
    {
        var owned = page.Count(item => EwelinkMapping.Integer(item, "itemType") == 1);
        var shared = page.Count(item => EwelinkMapping.Integer(item, "itemType") == 2);
        var groups = page.Count(item => EwelinkMapping.Integer(item, "itemType") is 3 or 4);
        var unmapped = page.Count(item => EwelinkMapping.Integer(item, "itemType") is 1 or 2 &&
            EwelinkMapping.Id(EwelinkMapping.Node(item, "itemData"), "deviceid") is null);
        logger.LogInformation("eWeLink inventory page returned {Rows} of {ReportedTotal} things with {Owned} owned and {Shared} shared. Excluded {Groups} groups, {Other} unsupported item types, and {Unmapped} device rows without a usable identity.",
            page.Length, total, owned, shared, groups, page.Length - owned - shared - groups, unmapped);

        var shapes = page.Where(item => EwelinkMapping.Integer(item, "itemType") is 1 or 2)
            .GroupBy(item => Describe(item));
        foreach (var shape in shapes)
            logger.LogInformation("eWeLink inventory found {Count} device rows for model {Model}, UIID {Uiid}, with parameter types {Fields}, {OtherFields} other fields, and supported capabilities {Capabilities}.",
                shape.Count(), shape.Key.Model, shape.Key.Uiid, shape.Key.Fields, shape.Key.OtherFields, shape.Key.Capabilities);
    }

    private static (string Model, int? Uiid, string Fields, int OtherFields, string Capabilities) Describe(JsonElement item)
    {
        var data = EwelinkMapping.Node(item, "itemData");
        var parameters = EwelinkMapping.Node(data, "params");
        var uiid = EwelinkMapping.Uiid(data);
        var fields = MeasurementFields.Select(name => (Name: name, Kind: EwelinkMapping.Node(parameters, name).ValueKind))
            .Where(field => field.Kind != JsonValueKind.Undefined).ToArray();
        var device = EwelinkMapping.Map(item, DateTimeOffset.UnixEpoch);
        var capabilities = device?.Telemetry(false).Inventory.Endpoints[0].Capabilities ?? [];
        var model = EwelinkMapping.Model(data) switch
        {
            "PSF-X67" => "PSF-X67",
            "THR320D" => "THR320D",
            "THR316D" => "THR316D",
            "" => "unreported",
            _ => "other"
        };
        return (model, uiid is > 0 and <= 9999 ? uiid : null, string.Join(", ", fields.Select(field => $"{field.Name}={field.Kind}")),
            parameters.ValueKind == JsonValueKind.Object ? parameters.EnumerateObject().Count() - fields.Length : 0,
            string.Join(", ", capabilities.Order()));
    }
}
