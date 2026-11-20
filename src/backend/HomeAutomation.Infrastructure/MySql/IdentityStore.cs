using Dapper;
using Microsoft.AspNetCore.Identity;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.MySql;

public sealed class LocalUser : IdentityUser;

public sealed class IdentityStore(Database database) :
    IUserPasswordStore<LocalUser>, IUserSecurityStampStore<LocalUser>, IUserLockoutStore<LocalUser>
{
    private const string Select = """
        SELECT id AS Id, email AS UserName, normalized_email AS NormalizedUserName, password_hash AS PasswordHash,
               security_stamp AS SecurityStamp, concurrency_stamp AS ConcurrencyStamp, failed_count AS AccessFailedCount,
               lockout_enabled AS LockoutEnabled, lockout_end_utc AS LockoutEndUtc FROM identity_users
        """;

    public async Task<IdentityResult> CreateAsync(LocalUser user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(Query(
                """
                INSERT INTO identity_users (id, email, normalized_email, password_hash, security_stamp, concurrency_stamp,
                                            failed_count, lockout_enabled, lockout_end_utc)
                VALUES (@Id, @UserName, @NormalizedUserName, @PasswordHash, @SecurityStamp, @ConcurrencyStamp,
                        @AccessFailedCount, @LockoutEnabled, @LockoutEndUtc)
                """, cancellationToken, Fields(user)));
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().DuplicateUserName(user.UserName ?? string.Empty));
        }
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(LocalUser user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var previous = user.ConcurrencyStamp;
        var next = Guid.NewGuid().ToString("N");
        var parameters = new DynamicParameters(Fields(user));
        parameters.Add("Previous", previous);
        parameters.Add("Next", next);
        var count = await connection.ExecuteAsync(Query(
            """
            UPDATE identity_users SET email = @UserName, normalized_email = @NormalizedUserName, password_hash = @PasswordHash,
                security_stamp = @SecurityStamp, concurrency_stamp = @Next, failed_count = @AccessFailedCount,
                lockout_enabled = @LockoutEnabled, lockout_end_utc = @LockoutEndUtc
            WHERE id = @Id AND concurrency_stamp = @Previous
            """, cancellationToken, parameters));
        if (count != 1) return IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
        user.ConcurrencyStamp = next;
        return IdentityResult.Success;
    }

    public Task<IdentityResult> DeleteAsync(LocalUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "DeletionUnavailable" }));

    public Task<LocalUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        FindAsync(Select + " WHERE id = @Value", userId, cancellationToken);

    public Task<LocalUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        FindAsync(Select + " WHERE normalized_email = @Value", normalizedUserName, cancellationToken);

    private async Task<LocalUser?> FindAsync(string sql, string value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(Query(sql, cancellationToken, new { Value = value }));
        return row is null ? null : new LocalUser
        {
            Id = row.Id, UserName = row.UserName, NormalizedUserName = row.NormalizedUserName,
            PasswordHash = row.PasswordHash, SecurityStamp = row.SecurityStamp, ConcurrencyStamp = row.ConcurrencyStamp,
            AccessFailedCount = row.AccessFailedCount, LockoutEnabled = row.LockoutEnabled, LockoutEnd = Utc(row.LockoutEndUtc)
        };
    }

    public async Task GrantDemoAccessAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(Query(
            """
            INSERT INTO property_memberships (property_id, user_id, role_code)
            SELECT id, @UserId, 'Viewer' FROM properties WHERE site_key = 'countryside'
            ON DUPLICATE KEY UPDATE role_code = property_memberships.role_code
            """, cancellationToken, new { UserId = userId }));
    }

    private static object Fields(LocalUser user) => new
    {
        user.Id, user.UserName, user.NormalizedUserName, user.PasswordHash, user.SecurityStamp,
        user.ConcurrencyStamp, user.AccessFailedCount, user.LockoutEnabled, LockoutEndUtc = user.LockoutEnd?.UtcDateTime
    };

    public Task<string> GetUserIdAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);
    public Task SetUserNameAsync(LocalUser user, string? name, CancellationToken cancellationToken)
    { user.UserName = name; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(LocalUser user, string? name, CancellationToken cancellationToken)
    { user.NormalizedUserName = name; return Task.CompletedTask; }
    public Task SetPasswordHashAsync(LocalUser user, string? hash, CancellationToken cancellationToken)
    { user.PasswordHash = hash; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);
    public Task SetSecurityStampAsync(LocalUser user, string stamp, CancellationToken cancellationToken)
    { user.SecurityStamp = stamp; return Task.CompletedTask; }
    public Task<string?> GetSecurityStampAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);
    public Task SetLockoutEndDateAsync(LocalUser user, DateTimeOffset? end, CancellationToken cancellationToken)
    { user.LockoutEnd = end; return Task.CompletedTask; }
    public Task<int> IncrementAccessFailedCountAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(++user.AccessFailedCount);
    public Task ResetAccessFailedCountAsync(LocalUser user, CancellationToken cancellationToken)
    { user.AccessFailedCount = 0; return Task.CompletedTask; }
    public Task<int> GetAccessFailedCountAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnabled);
    public Task SetLockoutEnabledAsync(LocalUser user, bool enabled, CancellationToken cancellationToken)
    { user.LockoutEnabled = enabled; return Task.CompletedTask; }
    public void Dispose() { }

    private sealed record UserRow(string Id, string UserName, string NormalizedUserName, string PasswordHash,
        string SecurityStamp, string ConcurrencyStamp, int AccessFailedCount, bool LockoutEnabled, DateTime? LockoutEndUtc);
}
