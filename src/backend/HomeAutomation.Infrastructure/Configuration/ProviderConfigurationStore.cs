using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Configuration;

public sealed class ConfigurationStorageException : Exception;

public sealed class ProviderConfigurationStore(Database database, Migrations migrations, ConfigurationVaultOptions options,
    TimeProvider clock) : IProviderConfiguration
{
    private sealed record Definition(string Provider, int Default, int Minimum, int Maximum, int Step, string[] Slots);
    private static readonly Definition[] Providers =
    [
        new("growatt", 300, 300, 1800, 1, ["apiToken"]),
        new("daikin", 900, 300, 86400, 60, ["clientId", "clientSecret"]),
        new("ewelink", 300, 300, 86400, 60, ["appId", "appSecret"]),
        new("tuya", 1800, 1800, 86400, 60, ["accessId", "accessSecret", "userId"])
    ];
    private const string Profiles = """
        SELECT scope_id AS Scope, provider AS Provider, revision AS Revision, poll_seconds AS PollSeconds,
               credentials AS Credentials
        FROM configuration_profiles
        """;
    private const string Membership = """
        SELECT role_code FROM property_memberships WHERE property_id=@property AND user_id=@actor
        """;
    private readonly ConfigurationKeys keys = new(options);

    public async Task<ConfigurationView?> ReadAsync(long? property, string actor, CancellationToken ct)
    {
        if (property is <= 0 || (property is null && !options.IsAdministrator(actor))) return null;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var role = await RoleAsync(db, tx, property, actor, ct);
        if (role is null) return null;
        await EnsureSchemaAsync(ct);
        var rows = (await db.QueryAsync<Profile>(Query(Profiles + " WHERE scope_id=@scope", ct,
            new { scope = property ?? 0 }, tx))).ToDictionary(row => row.Provider, StringComparer.Ordinal);
        var views = Providers.Select(provider =>
        {
            var row = rows.GetValueOrDefault(provider.Provider);
            var envelopes = row is null ? null : ReadEnvelopes(row.Credentials);
            return new ProviderConfigurationView(provider.Provider, row?.Revision ?? 0, row?.PollSeconds ?? provider.Default,
                provider.Minimum, provider.Maximum, provider.Step,
                provider.Slots.ToDictionary(slot => slot, slot => envelopes?.ContainsKey(slot) == true, StringComparer.Ordinal));
        }).ToArray();
        await tx.CommitAsync(ct);
        var canEdit = MayEdit(role);
        return new(canEdit, canEdit && keys.Available(), views);
    }

    public async Task<ConfigurationSaveResult> SaveAsync(long? property, string actor, string provider,
        ProviderConfigurationUpdate update, CancellationToken ct)
    {
        var definition = Providers.SingleOrDefault(candidate => candidate.Provider == provider);
        if (definition is null || property is <= 0 || (property is null && !options.IsAdministrator(actor)))
            return ConfigurationSaveResult.NotFound;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var role = await RoleAsync(db, tx, property, actor, ct, writing: true);
        if (role is null) return ConfigurationSaveResult.NotFound;
        if (!MayEdit(role)) return ConfigurationSaveResult.Forbidden;
        if (!IsValid(definition, update)) return ConfigurationSaveResult.Invalid;
        await EnsureSchemaAsync(ct);
        var scope = property ?? 0;
        var existing = await db.QuerySingleOrDefaultAsync<Profile>(Query(
            Profiles + " WHERE scope_id=@scope AND provider=@provider FOR UPDATE", ct, new { scope, provider }, tx));
        if ((existing?.Revision ?? 0) != update.Revision) return ConfigurationSaveResult.Conflict;

        var envelopes = existing is null ? new Dictionary<string, CredentialEnvelope>(StringComparer.Ordinal) :
            ReadEnvelopes(existing.Credentials);
        var actions = new List<(string Name, string Action)>();
        if (existing is null || existing.PollSeconds != update.PollSeconds)
            actions.Add(("pollSeconds", existing is null ? "created" : "updated"));
        foreach (var name in update.Remove ?? [])
            if (envelopes.Remove(name)) actions.Add((name, "removed"));
        foreach (var (name, value) in update.Replacements ?? EmptyReplacements)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var plaintext = Encoding.UTF8.GetBytes(value);
            try
            {
                var action = envelopes.ContainsKey(name) ? "credentialRotated" : "created";
                envelopes[name] = await SealAsync(db, tx, scope, provider, name, plaintext, ct);
                actions.Add((name, action));
            }
            catch (CryptographicException) { return ConfigurationSaveResult.KeyUnavailable; }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        if (actions.Count == 0) return ConfigurationSaveResult.Saved;
        var row = new { scope, property, provider, update.PollSeconds, credentials = JsonSerializer.Serialize(envelopes) };
        try
        {
            await db.ExecuteAsync(Query(existing is null ? """
                INSERT INTO configuration_profiles(scope_id,property_id,provider,revision,poll_seconds,credentials)
                VALUES(@scope,@property,@provider,1,@PollSeconds,@credentials)
                """ : """
                UPDATE configuration_profiles SET revision=revision+1,poll_seconds=@PollSeconds,credentials=@credentials
                WHERE scope_id=@scope AND provider=@provider
                """, ct, row, tx));
        }
        catch (MySqlException error) when (existing is null && error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return ConfigurationSaveResult.Conflict;
        }
        await WriteAuditAsync(db, tx, actor, property, provider, actions, ct);
        await tx.CommitAsync(ct);
        return ConfigurationSaveResult.Saved;
    }

    public async Task<int> RotateAsync(CancellationToken ct)
    {
        if (!keys.Available()) throw new CryptographicException("The active external key is unavailable.");
        await EnsureSchemaAsync(ct);
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var rows = await db.QueryAsync<Profile>(Query(Profiles + " ORDER BY scope_id, provider FOR UPDATE", ct, transaction: tx));
        var rotated = 0;
        foreach (var row in rows)
        {
            var envelopes = ReadEnvelopes(row.Credentials);
            var actions = new List<(string Name, string Action)>();
            foreach (var (name, envelope) in envelopes.ToArray())
            {
                var plaintext = keys.Open(row.Scope, row.Provider, name, envelope);
                try
                {
                    if (envelope.KeyId == keys.ActiveKeyId) continue;
                    envelopes[name] = await SealAsync(db, tx, row.Scope, row.Provider, name, plaintext, ct);
                    actions.Add((name, "credentialRotated"));
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            if (actions.Count == 0) continue;
            await db.ExecuteAsync(Query("""
                UPDATE configuration_profiles SET revision=revision+1,credentials=@credentials
                WHERE scope_id=@Scope AND provider=@Provider
                """, ct, new { row.Scope, row.Provider, credentials = JsonSerializer.Serialize(envelopes) }, tx));
            await WriteAuditAsync(db, tx, "system:key-rotation", row.Scope == 0 ? null : row.Scope, row.Provider, actions, ct);
            rotated += actions.Count;
        }
        await tx.CommitAsync(ct);
        return rotated;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (!await migrations.VerifyConfigurationAsync(ct)) throw new ConfigurationStorageException();
    }

    private async Task<CredentialEnvelope> SealAsync(MySqlConnection db, MySqlTransaction tx, long scope,
        string provider, string slot, byte[] plaintext, CancellationToken ct)
    {
        var keyId = keys.ActiveKeyId ?? throw new CryptographicException("Set the configuration vault key ID.");
        var nonce = ConfigurationKeys.NewNonce();
        try
        {
            await db.ExecuteAsync(Query("""
                INSERT INTO configuration_nonces(key_id,nonce) VALUES(@keyId,@nonce)
                """, ct, new { keyId, nonce }, tx));
        }
        catch (MySqlException error) when (error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CryptographicException("A configuration nonce reservation collided.");
        }
        return keys.Seal(scope, provider, slot, plaintext, keyId, nonce);
    }

    private async Task<string?> RoleAsync(MySqlConnection db, MySqlTransaction tx, long? property, string actor,
        CancellationToken ct, bool writing = false)
    {
        if (property is null) return options.IsAdministrator(actor) ? "Admin" : null;
        return await db.QuerySingleOrDefaultAsync<string>(Query(Membership + (writing ? " FOR SHARE" : ""),
            ct, new { property, actor }, tx));
    }

    private static bool MayEdit(string role) => role.ToUpperInvariant() is "OWNER" or "ADMIN";
    private static readonly Dictionary<string, string?> EmptyReplacements = new(StringComparer.Ordinal);

    private static bool IsValid(Definition provider, ProviderConfigurationUpdate update)
    {
        if (update.Revision < 0 || update.PollSeconds < provider.Minimum || update.PollSeconds > provider.Maximum ||
            update.PollSeconds % provider.Step != 0) return false;
        var remove = update.Remove ?? [];
        var replace = update.Replacements ?? EmptyReplacements;
        return remove.Count <= provider.Slots.Length && replace.Count <= provider.Slots.Length &&
            remove.All(name => provider.Slots.Contains(name, StringComparer.Ordinal)) &&
            remove.Distinct(StringComparer.Ordinal).Count() == remove.Count &&
            replace.All(pair => provider.Slots.Contains(pair.Key, StringComparer.Ordinal) &&
                (string.IsNullOrWhiteSpace(pair.Value) || (!remove.Contains(pair.Key, StringComparer.Ordinal) &&
                    ValidSecret(provider.Provider, pair.Key, pair.Value))));
    }

    private static bool ValidSecret(string provider, string slot, string value)
    {
        if (value != value.Trim() || value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) > ConfigurationKeys.MaximumSecretBytes) return false;
        if (provider == "growatt") return value.Length <= 512 && value.All(c => c is >= '!' and <= '~');
        if (provider == "tuya" && slot is "accessId" or "userId") return Tuya.TuyaJson.IsId(value);
        return true;
    }

    private static Dictionary<string, CredentialEnvelope> ReadEnvelopes(string value)
    {
        try
        {
            var envelopes = JsonSerializer.Deserialize<Dictionary<string, CredentialEnvelope>>(value);
            if (envelopes is null || envelopes.Values.Any(envelope => envelope is null)) throw new JsonException();
            return envelopes;
        }
        catch (JsonException) { throw new ConfigurationStorageException(); }
    }

    private async Task WriteAuditAsync(MySqlConnection db, MySqlTransaction tx, string actor, long? property, string provider,
        List<(string Name, string Action)> actions, CancellationToken ct)
    {
        var occurred = clock.GetUtcNow().UtcDateTime;
        await db.ExecuteAsync(Query("""
            INSERT INTO configuration_audit(occurred_at_utc,actor,property_id,provider,setting,action)
            VALUES(@occurred,@actor,@property,@provider,@Name,@Action)
            """, ct, actions.Select(change => new { occurred, actor, property, provider, change.Name, change.Action }), tx));
    }

    private sealed record Profile(long Scope, string Provider, long Revision, int PollSeconds, string Credentials);
}
