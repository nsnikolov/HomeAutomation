using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class MonitoringDashboardStore(Database database, Migrations migrations, TuyaOptions tuya) : IMonitoringDashboard
{
    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyAsync(ct, includeDashboard: true);

    public async Task<DashboardSnapshot> ReadAsync(long property, string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var (role, groups) = await ReadLayoutAsync(tx, property, user, ct);
        var keys = Values(groups).Select(value => value.SourceKey).ToHashSet(StringComparer.Ordinal);
        DashboardSource[] sources = keys.Count == 0 || role is null ? [] :
            (await DashboardSources.ReadAsync(tx, property, tuya, ct)).Where(source => keys.Contains(source.Key)).ToArray();
        await tx.CommitAsync(ct);
        return new(Revision(groups), CanEdit(role), groups, sources);
    }

    public async Task<IReadOnlyList<DashboardSource>?> CatalogAsync(long property, string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var role = await db.QuerySingleOrDefaultAsync<string>(Query(Membership, ct, new { property, user }, tx));
        var result = CanEdit(role) ? await DashboardSources.ReadAsync(tx, property, tuya, ct) : null;
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<DashboardSaveResult> ReplaceAsync(long property, string user, DashboardUpdate update, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.QuerySingleOrDefaultAsync<long>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE",
            ct, new { property }, tx));
        var (role, previous) = await ReadLayoutAsync(tx, property, user, ct);
        if (!CanEdit(role)) return DashboardSaveResult.Forbidden;
        if (!string.Equals(update.Revision, Revision(previous), StringComparison.Ordinal)) return DashboardSaveResult.Conflict;

        var kept = Values(previous).ToDictionary(value => value.Id, value => value.SourceKey, StringComparer.Ordinal);
        var bindings = Values(update.Groups).Where(value => kept.GetValueOrDefault(value.Id) != value.SourceKey).ToArray();
        if (bindings.Length > 0)
        {
            var available = (await DashboardSources.ReadAsync(tx, property, tuya, ct)).Select(source => source.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (bindings.Any(value => !available.Contains(value.SourceKey))) return DashboardSaveResult.InvalidSource;
        }

        var groups = update.Groups.Select((group, position) => new
        {
            property, group.Id, group.Title, group.Hidden, group.Collapsed, position
        }).ToArray();
        var widgets = update.Groups.SelectMany(group => group.Widgets.Select((widget, position) => new
        {
            property, widget.Id, GroupId = group.Id, widget.Title, widget.Mode, widget.NavigationSource,
            widget.Width, widget.MeasurementColumns, position
        })).ToArray();
        var values = update.Groups.SelectMany(group => group.Widgets).SelectMany(widget =>
            widget.Values.Select((value, position) => new
            {
                property, value.Id, WidgetId = widget.Id, value.SourceKey, value.Label, value.ColorMode, value.CustomColor, position
            })).ToArray();

        await db.ExecuteAsync(Query("DELETE FROM dashboard_groups WHERE property_id=@property", ct, new { property }, tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO dashboard_groups(property_id,id,position,title,hidden,collapsed)
            VALUES(@property,@Id,@position,@Title,@Hidden,@Collapsed)
            """, ct, groups, tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO dashboard_widgets(property_id,id,group_id,position,title,display_mode,navigation_source,width_columns,measurement_columns)
            VALUES(@property,@Id,@GroupId,@position,@Title,@Mode,@NavigationSource,@Width,@MeasurementColumns)
            """, ct, widgets, tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO dashboard_values(property_id,id,widget_id,position,source_key,label,color_mode,custom_color)
            VALUES(@property,@Id,@WidgetId,@position,@SourceKey,@Label,@ColorMode,@CustomColor)
            """, ct, values, tx));
        await tx.CommitAsync(ct);
        return DashboardSaveResult.Saved;
    }

    private const string Membership =
        "SELECT role_code FROM property_memberships WHERE property_id=@property AND user_id=@user";

    private static async Task<(string? Role, DashboardGroup[] Groups)> ReadLayoutAsync(MySqlTransaction tx,
        long property, string user, CancellationToken ct)
    {
        using var rows = await tx.Connection!.QueryMultipleAsync(Query(Membership + """
            ;
            SELECT id AS Id,title AS Title,hidden AS Hidden,collapsed AS Collapsed
            FROM dashboard_groups WHERE property_id=@property ORDER BY position;
            SELECT id AS Id,group_id AS GroupId,title AS Title,display_mode AS Mode,navigation_source AS NavigationSource,
                   width_columns AS Width,measurement_columns AS MeasurementColumns
            FROM dashboard_widgets WHERE property_id=@property ORDER BY position;
            SELECT id AS Id,widget_id AS WidgetId,source_key AS SourceKey,label AS Label,color_mode AS ColorMode,custom_color AS CustomColor
            FROM dashboard_values WHERE property_id=@property ORDER BY position;
            """, ct, new { property, user }, tx));
        var role = await rows.ReadSingleOrDefaultAsync<string>();
        if (role is null) return (null, []);
        var groups = await rows.ReadAsync<GroupRow>();
        var widgets = (await rows.ReadAsync<WidgetRow>()).ToLookup(widget => widget.GroupId);
        var values = (await rows.ReadAsync<ValueRow>()).ToLookup(value => value.WidgetId);
        return (role, groups.Select(group => new DashboardGroup(group.Id, group.Title, group.Hidden, group.Collapsed,
            widgets[group.Id].Select(widget => new DashboardWidget(widget.Id, widget.Title, widget.Mode, widget.NavigationSource,
                values[widget.Id].Select(value => new DashboardValue(value.Id, value.SourceKey, value.Label, value.ColorMode, value.CustomColor)).ToArray(),
                widget.Width, widget.MeasurementColumns)).ToArray())).ToArray());
    }

    private static bool CanEdit(string? role) => role?.ToUpperInvariant() is "OWNER" or "ADMIN" or "OPERATOR";
    private static string Revision(DashboardGroup[] groups) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(groups)));
    private static IEnumerable<DashboardValue> Values(IEnumerable<DashboardGroup> groups) =>
        groups.SelectMany(group => group.Widgets).SelectMany(widget => widget.Values);

    private sealed record GroupRow(string Id, string Title, bool Hidden, bool Collapsed);
    private sealed record WidgetRow(string Id, string GroupId, string Title, string Mode, string? NavigationSource, int Width, int MeasurementColumns);
    private sealed record ValueRow(string Id, string WidgetId, string SourceKey, string? Label, string ColorMode, string? CustomColor);
}
