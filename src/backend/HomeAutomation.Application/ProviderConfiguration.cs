namespace HomeAutomation.Application;

/// <summary>
/// One provider profile in one scope. Property and global profiles are staged independently; a property read does not inherit
/// global values. <paramref name="InstallationWide"/> marks providers whose account is configured globally (Daikin, eWeLink and
/// Tuya), and <paramref name="Global"/> tells a property view whether that global profile exists and what it holds.
/// </summary>
public sealed record ProviderConfigurationView(string Provider, long Revision, int PollSeconds,
    int MinimumPollSeconds, int MaximumPollSeconds, int PollStepSeconds,
    IReadOnlyDictionary<string, bool> Credentials, IReadOnlyDictionary<string, string?> Identifiers,
    IReadOnlyList<string> PendingIdentifierConversion, bool InstallationWide = false, GlobalProfileStatus? Global = null);

/// <summary>
/// Presence flags of the global profile shown alongside a property profile. Only booleans and the revision are exposed: identifier
/// values and secrets stay restricted to the global scope's administrator.
/// </summary>
public sealed record GlobalProfileStatus(long Revision, IReadOnlyDictionary<string, bool> Credentials,
    IReadOnlyDictionary<string, bool> Identifiers);

public sealed record ConfigurationView(bool CanEdit, bool KeyAvailable, IReadOnlyList<ProviderConfigurationView> Providers,
    bool RequiresHttps = false);

public sealed record ProviderConfigurationUpdate(long Revision, int PollSeconds,
    IReadOnlyDictionary<string, string?>? Replacements = null, IReadOnlyList<string>? Remove = null,
    IReadOnlyDictionary<string, string?>? Identifiers = null);

public enum ConfigurationSaveResult { Saved, Forbidden, NotFound, Conflict, Invalid, KeyUnavailable, IdentifierConversionRequired }

public interface IProviderConfiguration
{
    Task<ConfigurationView?> ReadAsync(long? property, string actor, CancellationToken ct);
    Task<ConfigurationSaveResult> SaveAsync(long? property, string actor, string provider,
        ProviderConfigurationUpdate update, CancellationToken ct);
}
