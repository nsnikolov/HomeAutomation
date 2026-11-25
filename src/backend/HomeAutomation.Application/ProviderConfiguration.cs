namespace HomeAutomation.Application;

public sealed record ProviderConfigurationView(string Provider, long Revision, int PollSeconds,
    int MinimumPollSeconds, int MaximumPollSeconds, int PollStepSeconds,
    IReadOnlyDictionary<string, bool> Credentials, IReadOnlyDictionary<string, string?> Identifiers,
    IReadOnlyList<string> PendingIdentifierConversion);

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
