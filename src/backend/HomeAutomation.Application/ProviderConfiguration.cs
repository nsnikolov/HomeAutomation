namespace HomeAutomation.Application;

public sealed record ProviderConfigurationView(string Provider, long Revision, int PollSeconds,
    int MinimumPollSeconds, int MaximumPollSeconds, int PollStepSeconds,
    IReadOnlyDictionary<string, bool> Credentials);

public sealed record ConfigurationView(bool CanEdit, bool KeyAvailable, IReadOnlyList<ProviderConfigurationView> Providers,
    bool RequiresHttps = false);

public sealed record ProviderConfigurationUpdate(long Revision, int PollSeconds,
    IReadOnlyDictionary<string, string?>? Replacements = null, IReadOnlyList<string>? Remove = null);

public enum ConfigurationSaveResult { Saved, Forbidden, NotFound, Conflict, Invalid, KeyUnavailable }

public interface IProviderConfiguration
{
    Task<ConfigurationView?> ReadAsync(long? property, string actor, CancellationToken ct);
    Task<ConfigurationSaveResult> SaveAsync(long? property, string actor, string provider,
        ProviderConfigurationUpdate update, CancellationToken ct);
}
