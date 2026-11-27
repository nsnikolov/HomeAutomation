using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Tuya;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class MonitoringDashboardStore(Database database, Migrations migrations, TuyaOptions tuya,
    Simulator.SimulatorOptions? simulator = null) : IMonitoringDashboard
{
    private bool IncludeSimulator => simulator?.Enabled == true;
    public Task<bool> SchemaReadyAsync(CancellationToken ct) => migrations.VerifyAsync(ct, includeDashboard: true);

    public async Task<DashboardSnapshot> ReadAsync(long property, string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var (role, groups) = await ReadLayoutAsync(tx, property, user, ct);
        var keys = Values(groups).Select(value => value.SourceKey).ToHashSet(StringComparer.Ordinal);
        DashboardSource[] sources = keys.Count == 0 || role is null ? [] :
            (await DashboardSources.ReadAsync(tx, property, tuya, IncludeSimulator, ct, await LogicalReadyAsync(ct)))
                .Where(source => keys.Contains(source.Key)).ToArray();
        await tx.CommitAsync(ct);
        return new(Revision(groups), CanEdit(role), groups, sources);
    }

    public async Task<IReadOnlyList<DashboardSource>?> CatalogAsync(long property, string user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var role = await db.QuerySingleOrDefaultAsync<string>(Query(Membership, ct, new { property, user }, tx));
        var result = CanEdit(role)
            ? (await DashboardSources.ReadAsync(tx, property, tuya, IncludeSimulator, ct, await LogicalReadyAsync(ct))).Where(Selectable).ToArray()
            : null;
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
            var available = (await DashboardSources.ReadAsync(tx, property, tuya, IncludeSimulator, ct, await LogicalReadyAsync(ct)))
                .Where(Selectable).Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
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

    /// <summary>
    /// Proposes logical bindings only where exactly one active logical channel currently uses the saved physical source.
    /// Ambiguous or unmatched values are listed with a reason and never changed.
    /// </summary>
    public async Task<LogicalBindingReview?> LogicalBindingsAsync(long property, string user, CancellationToken ct)
    {
        if (!await LogicalReadyAsync(ct)) return new(Revision([]), [], []);
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var (role, groups) = await ReadLayoutAsync(tx, property, user, ct);
        if (!CanEdit(role)) return null;
        var (proposals, skipped) = await ProposeAsync(tx, property, groups, ct);
        await tx.CommitAsync(ct);
        return new(Revision(groups), proposals, skipped);
    }

    public async Task<DashboardSaveResult> ConvertToLogicalAsync(long property, string user, LogicalBindingUpdate update, CancellationToken ct)
    {
        if (!await LogicalReadyAsync(ct)) return DashboardSaveResult.InvalidSource;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.QuerySingleOrDefaultAsync<long>(Query("SELECT id FROM properties WHERE id=@property FOR UPDATE", ct, new { property }, tx));
        var (role, groups) = await ReadLayoutAsync(tx, property, user, ct);
        if (!CanEdit(role)) return DashboardSaveResult.Forbidden;
        if (!string.Equals(update.Revision, Revision(groups), StringComparison.Ordinal)) return DashboardSaveResult.Conflict;
        var proposals = (await ProposeAsync(tx, property, groups, ct)).Proposals.ToDictionary(item => item.ValueId, StringComparer.Ordinal);
        if (update.ValueIds.Length == 0 || update.ValueIds.Distinct(StringComparer.Ordinal).Count() != update.ValueIds.Length ||
            update.ValueIds.Any(id => !proposals.ContainsKey(id)))
            return DashboardSaveResult.InvalidSource;
        foreach (var proposal in update.ValueIds.Select(id => proposals[id]))
            await db.ExecuteAsync(Query("""
                UPDATE dashboard_widgets w JOIN dashboard_values v ON v.property_id=w.property_id AND v.widget_id=w.id
                SET w.navigation_source=@LogicalKey
                WHERE w.property_id=@property AND v.id=@ValueId AND w.navigation_source=@SourceKey;
                UPDATE dashboard_values SET source_key=@LogicalKey WHERE property_id=@property AND id=@ValueId AND source_key=@SourceKey;
                """, ct, new { property, proposal.ValueId, proposal.SourceKey, proposal.LogicalKey }, tx));
        await tx.CommitAsync(ct);
        return DashboardSaveResult.Saved;
    }

    private async Task<(List<LogicalBindingProposal> Proposals, List<LogicalBindingSkip> Skipped)> ProposeAsync(MySqlTransaction tx,
        long property, DashboardGroup[] groups, CancellationToken ct)
    {
        var physical = await DashboardSources.ReadPhysicalAsync(tx, property, tuya, IncludeSimulator, ct);
        var logical = (await DashboardSources.LogicalAsync(tx, property, physical, ct)).Where(item => item.Source.Status is null).ToList();
        var keys = physical.Select(item => item.Source.Key).ToHashSet(StringComparer.Ordinal);
        var proposals = new List<LogicalBindingProposal>();
        var skipped = new List<LogicalBindingSkip>();
        foreach (var widget in groups.SelectMany(group => group.Widgets))
        foreach (var value in widget.Values.Where(value => !value.SourceKey.StartsWith("logical:", StringComparison.Ordinal)))
        {
            var matches = logical.Where(item => item.PhysicalKey == value.SourceKey).ToList();
            if (!keys.Contains(value.SourceKey)) skipped.Add(new(value.Id, value.SourceKey, "The source is no longer available."));
            else if (matches.Count == 0) skipped.Add(new(value.Id, value.SourceKey, "No active logical device uses this measurement."));
            else if (matches.Count > 1)
                skipped.Add(new(value.Id, value.SourceKey, "Several logical devices use this measurement. Choose one in the dashboard editor."));
            else proposals.Add(new(value.Id, widget.Title, value.SourceKey, matches[0].Source.Key, matches[0].Source.DeviceName,
                matches[0].Source.Metric));
        }
        return (proposals, skipped);
    }

    private Task<bool> LogicalReadyAsync(CancellationToken ct) => migrations.VerifyLogicalDevicesAsync(ct);

    /// <summary>Archived logical sources keep rendering for saved widgets but are not offered for new bindings.</summary>
    private static bool Selectable(DashboardSource source) => source.Status != "archived";

    private const string Membership =
        "SELECT m.role_code FROM property_memberships m JOIN properties p ON p.id=m.property_id AND p.deleted_at_utc IS NULL WHERE m.property_id=@property AND m.user_id=@user";

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
