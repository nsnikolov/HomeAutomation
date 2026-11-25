using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Configuration;

public sealed record ProviderImportResult(bool Succeeded, string Status, long Revision, IReadOnlyList<string> Fields);

public sealed partial class ProviderConfigurationStore
{
    private const string ReadRuntimeProfile = """
        SELECT revision AS Revision, poll_seconds AS PollSeconds, credentials AS Credentials, identifiers AS Identifiers,
               operational_settings AS Operational FROM configuration_profiles WHERE scope_id=@scope AND provider=@provider
        """;

    public Task<ProviderImportResult> ImportLegacyProviderAsync(string provider, long? property, IConfiguration configuration,
        bool dryRun, long? replaceRevision, IDataProtectionProvider protection, CancellationToken ct) =>
        ImportProviderAsync(provider, property, ProviderOptionMapping.ReadLegacy(provider, configuration), dryRun, replaceRevision, protection, ct);

    public async Task<object> LoadProviderAsync(string provider, long? property, string? daikinGrantKey,
        IDataProtectionProvider protection, CancellationToken ct)
    {
        var definition = ProviderForScope(provider, property);
        await EnsureProviderSchemaAsync(ct);
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = property ?? 0;
        var row = await db.QuerySingleOrDefaultAsync<RuntimeProfile>(Query(ReadRuntimeProfile, ct, new { scope, provider }, tx));
        if (row?.Operational is null)
            throw new ProviderConfigurationException("missing imported operational settings");
        var envelopes = ReadFields<CredentialEnvelope>(row.Credentials);
        var identifiers = ReadFields<string>(row.Identifiers);
        if (envelopes.Keys.Any(slot => slot != definition.SecretSlot))
            throw new ProviderConfigurationException("convert legacy identifiers first");
        var missing = definition.Identifiers.Where(name => !identifiers.ContainsKey(name)).ToList();
        var envelope = envelopes.GetValueOrDefault(definition.SecretSlot);
        if (envelope is null) missing.Add(definition.SecretSlot);
        if (missing.Count != 0) throw new ProviderConfigurationException("missing required fields", missing.ToArray());
        var snapshot = new ProviderOptionSnapshot(row.PollSeconds, OpenSecret(scope, definition, envelope!),
            identifiers, ReadOperational(row.Operational));
        ValidateSnapshot(definition, snapshot);
        var result = ProviderOptionMapping.Restore(provider, snapshot, daikinGrantKey);
        await VerifyProviderBindingAsync(db, tx, property, result, protection, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProviderImportResult> ImportProviderAsync(string provider, long? property, object legacy,
        bool dryRun, long? replaceRevision, IDataProtectionProvider protection, CancellationToken ct)
    {
        var definition = ProviderForScope(provider, property);
        var snapshot = ProviderOptionMapping.Capture(legacy);
        ValidateSnapshot(definition, snapshot);
        await EnsureProviderSchemaAsync(ct);
        await using var db = await database.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var hasGrant = await VerifyProviderBindingAsync(db, tx, property, legacy, protection, ct, verifyGrant: !dryRun);
        var scope = property ?? 0;
        var existing = await db.QuerySingleOrDefaultAsync<RuntimeProfile>(Query(
            ReadRuntimeProfile + (dryRun ? "" : " FOR UPDATE"), ct, new { scope, provider }, tx));
        var fields = new[] { "pollSeconds", definition.SecretSlot }.Concat(definition.Identifiers)
            .Concat(snapshot.Operational.GetProperty("settings").EnumerateObject().Select(field => field.Name)).ToArray();
        var revision = existing?.Revision ?? 0;
        if (replaceRevision is not null && replaceRevision != revision)
            return new(false, "revision conflict", revision, fields);
        var envelopes = existing is null ? [] : ReadFields<CredentialEnvelope>(existing.Credentials);
        if (envelopes.Keys.Any(slot => slot != definition.SecretSlot))
            throw new ProviderConfigurationException("convert legacy identifiers first");
        var currentIds = existing is null ? [] : ReadFields<string>(existing.Identifiers);
        var currentSecret = envelopes.GetValueOrDefault(definition.SecretSlot);
        var unchangedSecret = currentSecret is not null && OpenSecret(scope, definition, currentSecret) == snapshot.Secret;
        var previousOperational = existing?.Operational is { } raw ? ReadOperational(raw) : (JsonElement?)null;
        var sameOperational = previousOperational is { } previous && JsonElement.DeepEquals(previous, snapshot.Operational);
        var identifiersCompatible = currentIds.All(item => snapshot.Identifiers.GetValueOrDefault(item.Key) == item.Value);
        if (hasGrant && previousOperational is { } pinned && !JsonElement.DeepEquals(
            ReadIdentity(pinned), ReadIdentity(snapshot.Operational)))
            throw new ProviderConfigurationException("changing a linked OAuth identity requires disconnecting the existing grant first");
        if (existing is not null && (existing.PollSeconds != snapshot.PollSeconds || !identifiersCompatible ||
            (currentSecret is not null && !unchangedSecret) || (previousOperational is not null && !sameOperational)) && replaceRevision is null)
            return new(false, "existing values differ; explicit revision required to replace", revision, fields);
        if (existing is not null && sameOperational && identifiersCompatible && currentIds.Count == snapshot.Identifiers.Count &&
            unchangedSecret && existing.PollSeconds == snapshot.PollSeconds)
            return new(true, "unchanged", revision, fields);
        if (!unchangedSecret && !keys.Available())
            throw new ProviderConfigurationException("active vault key unavailable");
        if (dryRun) return new(true, "valid; no values written", revision, fields);

        if (!unchangedSecret)
        {
            var plaintext = Encoding.UTF8.GetBytes(snapshot.Secret);
            try { envelopes[definition.SecretSlot] = await SealAsync(db, tx, scope, provider, definition.SecretSlot, plaintext, ct); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        var write = new
        {
            scope, property, provider, snapshot.PollSeconds, credentials = JsonSerializer.Serialize(envelopes),
            identifiers = JsonSerializer.Serialize(snapshot.Identifiers), operational = snapshot.Operational.GetRawText(),
        };
        try
        {
            await db.ExecuteAsync(Query(existing is null ? """
                INSERT INTO configuration_profiles(scope_id,property_id,provider,revision,poll_seconds,credentials,identifiers,operational_settings)
                VALUES(@scope,@property,@provider,1,@PollSeconds,@credentials,@identifiers,@operational)
                """ : """
                UPDATE configuration_profiles SET revision=revision+1,poll_seconds=@PollSeconds,credentials=@credentials,
                    identifiers=@identifiers,operational_settings=@operational WHERE scope_id=@scope AND provider=@provider
                """, ct, write, tx));
        }
        catch (MySqlException error) when (existing is null && error.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return new(false, "revision conflict", revision, fields);
        }
        var actions = new List<(string Name, string Action)>();
        if (existing is null || existing.PollSeconds != snapshot.PollSeconds) actions.Add(("pollSeconds", existing is null ? "created" : "updated"));
        if (!sameOperational) actions.Add(("operationalSettings", previousOperational is null ? "created" : "updated"));
        foreach (var (name, value) in snapshot.Identifiers)
            if (currentIds.GetValueOrDefault(name) != value) actions.Add((name, currentIds.ContainsKey(name) ? "updated" : "created"));
        if (!unchangedSecret) actions.Add((definition.SecretSlot, currentSecret is null ? "created" : "credentialRotated"));
        await WriteAuditAsync(db, tx, "system:provider-import", property, provider, actions, ct);
        await tx.CommitAsync(ct);
        return new(true, "imported", revision + 1, fields);
    }

    private static Definition ProviderForScope(string provider, long? property)
    {
        var definition = Providers.SingleOrDefault(candidate => candidate.Provider == provider) ??
            throw new ProviderConfigurationException("unsupported provider");
        if (property is <= 0 || (property is not null && provider != "growatt"))
            throw new ProviderConfigurationException("shared provider accounts require the global scope");
        return definition;
    }

    private async Task EnsureProviderSchemaAsync(CancellationToken ct)
    {
        if (!await migrations.VerifyProviderConfigurationAsync(ct))
            throw new ProviderConfigurationException("apply migration 016 through the migration runner");
    }

    private string OpenSecret(long scope, Definition provider, CredentialEnvelope envelope)
    {
        var plaintext = keys.Open(scope, provider.Provider, provider.SecretSlot, envelope);
        try { return StrictUtf8.GetString(plaintext); }
        catch (DecoderFallbackException) { throw new ProviderConfigurationException("invalid stored secret encoding", provider.SecretSlot); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static JsonElement ReadOperational(string value)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { throw new ProviderConfigurationException("invalid stored operational settings"); }
    }

    private static JsonElement ReadIdentity(JsonElement operations) =>
        operations.ValueKind == JsonValueKind.Object && operations.TryGetProperty("identity", out var identity) &&
        identity.ValueKind == JsonValueKind.String ? identity :
        throw new ProviderConfigurationException("invalid stored operational settings");

    private static void ValidateSnapshot(Definition definition, ProviderOptionSnapshot snapshot)
    {
        var update = new Application.ProviderConfigurationUpdate(0, snapshot.PollSeconds,
            new Dictionary<string, string?> { [definition.SecretSlot] = snapshot.Secret },
            Identifiers: snapshot.Identifiers.ToDictionary(field => field.Key, field => (string?)field.Value));
        if (!IsValid(definition, update) || string.IsNullOrWhiteSpace(snapshot.Secret))
            throw new ProviderConfigurationException("invalid profile field", "pollSeconds", definition.SecretSlot,
                string.Join(", ", definition.Identifiers));
    }

    private static async Task<bool> VerifyProviderBindingAsync(MySqlConnection db, MySqlTransaction tx, long? property,
        object options, IDataProtectionProvider protection, CancellationToken ct, bool verifyGrant = true)
    {
        if (options is GrowattOptions growatt)
        {
            var site = await db.QuerySingleOrDefaultAsync<long?>(Query("SELECT id FROM properties WHERE site_key=@SiteKey",
                ct, new { growatt.SiteKey }, tx));
            if (site is null || (property is not null && property != site))
                throw new ProviderConfigurationException("Growatt SiteKey does not match the selected property");
            return false;
        }
        var query = options switch
        {
            DaikinOptions => "FROM daikin_account",
            EwelinkOptions => "FROM ewelink_account",
            _ => null,
        };
        if (query is null) return false;
        var account = await db.QuerySingleOrDefaultAsync<GrantBinding>(Query(
            "SELECT owner_id AS Owner, token_envelope AS Envelope, email AS Email " + query +
            " LEFT JOIN identity_users ON identity_users.id=owner_id WHERE " +
            (options is DaikinOptions ? "daikin_account.id=1" : "ewelink_account.id=1"), ct, transaction: tx));
        if (account is null) return false;
        var email = options is DaikinOptions daikinOptions ? daikinOptions.OperatorEmail : ((EwelinkOptions)options).OperatorEmail;
        if (!string.Equals(email, account.Email, StringComparison.OrdinalIgnoreCase))
            throw new ProviderConfigurationException("provider operator does not match the existing account owner");
        if (account.Envelope is null) return false;
        if (!verifyGrant) return true;
        try
        {
            if (options is DaikinOptions daikin) _ = new DaikinSecrets(daikin).Open(account.Owner, account.Envelope);
            if (options is EwelinkOptions ewelink) _ = new EwelinkVault(protection).Open(account.Owner, ewelink.AppId!, account.Envelope);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or EwelinkFailure)
        {
            throw new ProviderConfigurationException("existing OAuth grant could not be opened with the configured grant keys");
        }
        return true;
    }

    private sealed record RuntimeProfile(long Revision, int PollSeconds, string Credentials, string Identifiers, string? Operational);
    private sealed record GrantBinding(string Owner, byte[]? Envelope, string? Email);
}
