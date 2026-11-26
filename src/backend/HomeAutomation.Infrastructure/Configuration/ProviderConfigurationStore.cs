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

public sealed partial class ProviderConfigurationStore(Database database, Migrations migrations, ConfigurationVaultOptions options,
    TimeProvider clock) : IProviderConfiguration
{
    /// <param name="InstallationWide">One account for the installation, imported into the global scope; a property profile is
    /// staged separately and cannot substitute for it (see the configuration vault guide).</param>
    private sealed record Definition(string Provider, int Default, int Minimum, int Maximum, int Step,
        string SecretSlot, string[] Identifiers, bool InstallationWide);
    private static readonly Definition[] Providers =
    [
        new("growatt", 300, 300, 1800, 1, "apiToken", [], false),
        new("daikin", 900, 300, 86400, 60, "clientSecret", ["clientId"], true),
        new("ewelink", 300, 300, 86400, 60, "appSecret", ["appId"], true),
        new("tuya", 1800, 1800, 86400, 60, "accessSecret", ["accessId", "userId"], true)
    ];
    private const string Profiles = """
        SELECT scope_id AS Scope, provider AS Provider, revision AS Revision, poll_seconds AS PollSeconds,
               credentials AS Credentials, identifiers AS Identifiers
        FROM configuration_profiles
        """;
    private const string Membership = """
        SELECT role_code FROM property_memberships WHERE property_id=@property AND user_id=@actor
        """;
    private readonly ConfigurationKeys keys = new(options);
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public async Task<ConfigurationView?> ReadAsync(long? property, string actor, CancellationToken ct)
    {
        if (property is <= 0 || (property is null && !options.IsAdministrator(actor))) return null;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var role = await RoleAsync(db, tx, property, actor, ct);
        if (role is null) return null;
        await EnsureSchemaAsync(ct);
        // The requested scope is read as before. A property read also loads the global rows, but only to report their presence:
        // property and global profiles stay independent, and global values are never returned outside the global scope.
        var all = (await db.QueryAsync<Profile>(Query(Profiles + " WHERE scope_id IN (@scope, 0)", ct,
            new { scope = property ?? 0 }, tx))).ToList();
        var rows = all.Where(row => row.Scope == (property ?? 0)).ToDictionary(row => row.Provider, StringComparer.Ordinal);
        var global = property is null ? new Dictionary<string, Profile>(StringComparer.Ordinal)
            : all.Where(row => row.Scope == 0).ToDictionary(row => row.Provider, StringComparer.Ordinal);
        var views = Providers.Select(provider =>
        {
            var row = rows.GetValueOrDefault(provider.Provider);
            var envelopes = row is null ? null : ReadFields<CredentialEnvelope>(row.Credentials);
            var identifiers = row is null ? null : ReadFields<string>(row.Identifiers);
            return new ProviderConfigurationView(provider.Provider, row?.Revision ?? 0, row?.PollSeconds ?? provider.Default,
                provider.Minimum, provider.Maximum, provider.Step,
                new Dictionary<string, bool>(StringComparer.Ordinal) { [provider.SecretSlot] = envelopes?.ContainsKey(provider.SecretSlot) == true },
                provider.Identifiers.ToDictionary(name => name,
                    name => envelopes?.ContainsKey(name) == true ? null : identifiers?.GetValueOrDefault(name),
                    StringComparer.Ordinal),
                provider.Identifiers.Where(name => envelopes?.ContainsKey(name) == true).ToArray(),
                provider.InstallationWide, global.GetValueOrDefault(provider.Provider) is { } shared ? Presence(provider, shared) : null);
        }).ToArray();
        await tx.CommitAsync(ct);
        var canEdit = MayEdit(role);
        return new(canEdit, canEdit && keys.Available(), views);
    }

    /// <summary>Which slots and identifiers the global profile holds. An identifier saved in the previous encrypted format counts as present.</summary>
    private static GlobalProfileStatus Presence(Definition provider, Profile row)
    {
        var envelopes = ReadFields<CredentialEnvelope>(row.Credentials);
        var identifiers = ReadFields<string>(row.Identifiers);
        return new(row.Revision,
            new Dictionary<string, bool>(StringComparer.Ordinal) { [provider.SecretSlot] = envelopes?.ContainsKey(provider.SecretSlot) == true },
            provider.Identifiers.ToDictionary(name => name,
                name => envelopes?.ContainsKey(name) == true || !string.IsNullOrWhiteSpace(identifiers?.GetValueOrDefault(name)),
                StringComparer.Ordinal));
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
            ReadFields<CredentialEnvelope>(existing.Credentials);
        var identifiers = existing is null ? new Dictionary<string, string>(StringComparer.Ordinal) :
            ReadFields<string>(existing.Identifiers);
        var actions = new List<(string Name, string Action)>();
        if (existing is null || existing.PollSeconds != update.PollSeconds)
            actions.Add(("pollSeconds", existing is null ? "created" : "updated"));
        foreach (var name in update.Remove ?? [])
            if (envelopes.Remove(name)) actions.Add((name, "removed"));
        foreach (var (name, value) in update.Identifiers ?? EmptyFields)
        {
            if (envelopes.ContainsKey(name)) return ConfigurationSaveResult.IdentifierConversionRequired;
            if (string.IsNullOrEmpty(value))
            {
                if (identifiers.Remove(name)) actions.Add((name, "removed"));
                continue;
            }
            var previous = identifiers.GetValueOrDefault(name);
            if (previous == value) continue;
            identifiers[name] = value;
            actions.Add((name, previous is null ? "created" : "updated"));
        }
        foreach (var (name, value) in update.Replacements ?? EmptyFields)
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
        var row = new { scope, property, provider, update.PollSeconds, credentials = JsonSerializer.Serialize(envelopes),
            identifiers = JsonSerializer.Serialize(identifiers) };
        try
        {
            await db.ExecuteAsync(Query(existing is null ? """
                INSERT INTO configuration_profiles(scope_id,property_id,provider,revision,poll_seconds,credentials,identifiers)
                VALUES(@scope,@property,@provider,1,@PollSeconds,@credentials,@identifiers)
                """ : """
                UPDATE configuration_profiles SET revision=revision+1,poll_seconds=@PollSeconds,
                    credentials=@credentials,identifiers=@identifiers
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
            var envelopes = ReadFields<CredentialEnvelope>(row.Credentials);
            var definition = Providers.SingleOrDefault(provider => provider.Provider == row.Provider);
            if (definition is null || envelopes.Keys.Any(name => name != definition.SecretSlot))
                throw new ConfigurationStorageException();
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

    public async Task<int> ConvertIdentifiersAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var rows = await db.QueryAsync<Profile>(Query(Profiles + " ORDER BY scope_id, provider FOR UPDATE", ct, transaction: tx));
        var converted = 0;
        foreach (var row in rows)
        {
            var definition = Providers.SingleOrDefault(provider => provider.Provider == row.Provider);
            if (definition is null) continue;
            var envelopes = ReadFields<CredentialEnvelope>(row.Credentials);
            var identifiers = ReadFields<string>(row.Identifiers);
            var actions = new List<(string Name, string Action)>();
            foreach (var name in definition.Identifiers)
            {
                if (!envelopes.TryGetValue(name, out var envelope)) continue;
                var plaintext = keys.Open(row.Scope, row.Provider, name, envelope);
                try
                {
                    var value = StrictUtf8.GetString(plaintext);
                    var previous = identifiers.GetValueOrDefault(name);
                    if (!ValidIdentifier(row.Provider, value) || (previous is not null && previous != value))
                        throw new ConfigurationStorageException();
                    identifiers[name] = value;
                    envelopes.Remove(name);
                    actions.Add((name, "updated"));
                }
                catch (DecoderFallbackException) { throw new ConfigurationStorageException(); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            if (actions.Count == 0) continue;
            await db.ExecuteAsync(Query("""
                UPDATE configuration_profiles SET revision=revision+1,credentials=@credentials,identifiers=@identifiers
                WHERE scope_id=@Scope AND provider=@Provider
                """, ct, new { row.Scope, row.Provider, credentials = JsonSerializer.Serialize(envelopes),
                    identifiers = JsonSerializer.Serialize(identifiers) }, tx));
            await WriteAuditAsync(db, tx, "system:identifier-conversion", row.Scope == 0 ? null : row.Scope, row.Provider, actions, ct);
            converted += actions.Count;
        }
        await tx.CommitAsync(ct);
        return converted;
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
    private static readonly Dictionary<string, string?> EmptyFields = new(StringComparer.Ordinal);

    private static bool IsValid(Definition provider, ProviderConfigurationUpdate update)
    {
        if (update.Revision < 0 || update.PollSeconds < provider.Minimum || update.PollSeconds > provider.Maximum ||
            update.PollSeconds % provider.Step != 0) return false;
        var remove = update.Remove ?? [];
        var replace = update.Replacements ?? EmptyFields;
        var identifiers = update.Identifiers ?? EmptyFields;
        return remove.Count <= 1 && replace.Count <= 1 &&
            remove.All(name => name == provider.SecretSlot) &&
            replace.All(pair => pair.Key == provider.SecretSlot &&
                (string.IsNullOrWhiteSpace(pair.Value) || (!remove.Contains(pair.Key, StringComparer.Ordinal) &&
                    ValidSecret(provider.Provider, pair.Value)))) &&
            identifiers.Count <= provider.Identifiers.Length &&
            identifiers.All(pair => provider.Identifiers.Contains(pair.Key, StringComparer.Ordinal) &&
                (string.IsNullOrEmpty(pair.Value) || ValidIdentifier(provider.Provider, pair.Value)));
    }

    private static bool ValidField(string value) => value.Length > 0 && value == value.Trim() && !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <= ConfigurationKeys.MaximumSecretBytes;

    private static bool ValidSecret(string provider, string value) => ValidField(value) &&
        (provider != "growatt" || (value.Length <= 512 && value.All(c => c is >= '!' and <= '~')));

    private static bool ValidIdentifier(string provider, string value) =>
        ValidField(value) && (provider != "tuya" || Tuya.TuyaJson.IsId(value));

    private static Dictionary<string, T> ReadFields<T>(string value) where T : class
    {
        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, T>>(value);
            if (fields is null || fields.Values.Any(field => field is null)) throw new JsonException();
            return fields;
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

    private sealed record Profile(long Scope, string Provider, long Revision, int PollSeconds, string Credentials, string Identifiers);
}
