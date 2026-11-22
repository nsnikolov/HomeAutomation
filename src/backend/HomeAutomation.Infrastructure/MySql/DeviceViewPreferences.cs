using System.Text.Json;
using Dapper;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed record DeviceViewPreference(IReadOnlyDictionary<string, string[]> Order, IReadOnlyList<string> Hidden);

public sealed class DeviceViewPreferences(Database database)
{
    public async Task<DeviceViewPreference> ReadAsync(long property, string user, string page, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        var row = await db.QuerySingleOrDefaultAsync<PreferenceRow>(Query("""
            SELECT card_order AS CardOrder, JSON_KEYS(hidden_devices) AS Hidden
            FROM device_view_preferences WHERE property_id=@property AND user_id=@user AND page=@page
            """, ct, new { property, user, page }));
        return row is null ? new(new Dictionary<string, string[]>(), []) :
            new(JsonSerializer.Deserialize<Dictionary<string, string[]>>(row.CardOrder)!,
                JsonSerializer.Deserialize<string[]>(row.Hidden)!);
    }

    public async Task OrderAsync(long property, string user, string page, string group, string[] ids, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO device_view_preferences(property_id,user_id,page,card_order,hidden_devices)
            VALUES(@property,@user,@page,JSON_SET(JSON_OBJECT(),@path,CAST(@ids AS JSON)),JSON_OBJECT())
            ON DUPLICATE KEY UPDATE card_order=JSON_SET(device_view_preferences.card_order,@path,CAST(@ids AS JSON))
            """, ct, new { property, user, page, path = JsonPath(group), ids = JsonSerializer.Serialize(ids) }));
    }

    public async Task VisibilityAsync(long property, string user, string id, bool hidden, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(Query(hidden ? """
            INSERT INTO device_view_preferences(property_id,user_id,page,card_order,hidden_devices)
            VALUES(@property,@user,'tuya',JSON_OBJECT(),JSON_SET(JSON_OBJECT(),@path,TRUE))
            ON DUPLICATE KEY UPDATE hidden_devices=JSON_SET(device_view_preferences.hidden_devices,@path,TRUE)
            """ : """
            UPDATE device_view_preferences SET hidden_devices=JSON_REMOVE(hidden_devices,@path)
            WHERE property_id=@property AND user_id=@user AND page='tuya'
            """, ct, new { property, user, path = JsonPath(id) }));
    }

    private static string JsonPath(string key) => "$." + JsonSerializer.Serialize(key);
    private sealed record PreferenceRow(string CardOrder, string Hidden);
}
