using System.Data;
using Dapper;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class PropertyAdministration(Database database)
{
    public async Task<long> CreateAsync(string creatorId, string siteKey, string name, string timeZone, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(Query("""
            INSERT INTO properties (site_key, name, time_zone) VALUES (@siteKey, @name, @timeZone)
            """, ct, new { siteKey, name, timeZone }, tx));
        var propertyId = await db.ExecuteScalarAsync<long>(Query("SELECT LAST_INSERT_ID()", ct, transaction: tx));
        await db.ExecuteAsync(Query("""
            INSERT INTO property_memberships (property_id, user_id, role_code) VALUES (@propertyId, @creatorId, 'Owner')
            """, ct, new { propertyId, creatorId }, tx));
        await tx.CommitAsync(ct);
        return propertyId;
    }

    public async Task<bool> PromoteInitialOwnerAsync(string confirmedUserId, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var siteKey = await db.QuerySingleOrDefaultAsync<string>(Query("""
            SELECT site_key FROM properties WHERE id=1 FOR UPDATE
            """, ct, transaction: tx));
        if (siteKey != "countryside")
            throw new InvalidOperationException("Property 1 is not the seeded countryside property.");

        var members = (await db.QueryAsync<Membership>(Query("""
            SELECT user_id AS UserId, role_code AS Role FROM property_memberships
            WHERE property_id=1 FOR UPDATE
            """, ct, transaction: tx))).AsList();
        if (members is not [var member] || !string.Equals(member.UserId, confirmedUserId, StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmed account must be the sole member of property 1.");
        if (string.Equals(member.Role, "Owner", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(member.Role, "Viewer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sole membership must have the Viewer role.");

        var changed = await db.ExecuteAsync(Query("""
            UPDATE property_memberships SET role_code='Owner'
            WHERE property_id=1 AND user_id=@confirmedUserId AND role_code=@previousRole
            """, ct, new { confirmedUserId, previousRole = member.Role }, tx));
        if (changed != 1)
            throw new InvalidOperationException("The expected membership was not updated.");
        await tx.CommitAsync(ct);
        return true;
    }

    private sealed record Membership(string UserId, string Role);
}
