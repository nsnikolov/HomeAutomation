using System.Globalization;
using System.Security.Cryptography;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace HomeAutomation.Api;

internal sealed class ProviderConfigurationStartup
{
    private static readonly string[] Providers = ["growatt", "daikin", "ewelink", "tuya"];
    private readonly Dictionary<string, object> snapshots = new(StringComparer.Ordinal);

    public T? Get<T>(string provider) where T : class => (T?)snapshots.GetValueOrDefault(provider);

    public async Task<bool> LoadAsync(WebApplication app, string? onlyProvider = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var success = true;
        foreach (var provider in Providers)
        {
            if (onlyProvider is not null && provider != onlyProvider) continue;
            var configuredSource = app.Configuration[$"ConfigurationVault:Providers:{provider}:Source"] ?? "Legacy";
            var source = configuredSource.Equals("Vault", StringComparison.OrdinalIgnoreCase) ? "Vault" :
                configuredSource.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ? "Legacy" : "Unknown";
            long? property = null;
            var scope = "Unknown";
            var status = "Invalid";
            try
            {
                var selection = Select(app.Configuration, provider);
                property = selection.Property;
                scope = property.HasValue ? "Property" : "Global";
                if (selection.Vault)
                {
                    snapshots[provider] = await app.Services.GetRequiredService<ProviderConfigurationStore>().LoadProviderAsync(
                        provider, property, provider == "daikin" ? app.Configuration["Daikin:TokenKey"] : null,
                        app.Services.GetRequiredService<IDataProtectionProvider>(), timeout.Token);
                    status = "Valid";
                }
                else status = LegacyStatus(app.Services, provider);
            }
            catch (Exception error) when (IsConfigurationFailure(error))
            {
                LogFailure(app, provider, error);
                success = false;
            }
            app.Logger.LogInformation(new EventId(6100, "ProviderConfigurationStartup"),
                "Provider startup {Provider} uses {Source} at {Scope} (property {PropertyId}), validation {ValidationStatus}, PID {ProcessId}, UTC {ObservedAtUtc}.",
                provider, source, scope, property, status, Environment.ProcessId, DateTimeOffset.UtcNow);
        }
        return success;
    }

    private static string LegacyStatus(IServiceProvider services, string provider)
    {
        var configured = provider switch
        {
            "growatt" => services.GetRequiredService<GrowattOptions>() is { Enabled: true } growatt
                ? growatt.HasCloudConfiguration() : (bool?)null,
            "daikin" => services.GetRequiredService<DaikinOptions>().IsConfigured,
            "ewelink" => services.GetRequiredService<EwelinkOptions>().IsConfigured,
            _ => services.GetRequiredService<TuyaOptions>().IsConfigured
        };
        return configured switch { true => "Valid", false => "NotConfiguredOrInvalid", null => "Disabled" };
    }

    internal static (bool Vault, long? Property) Select(IConfiguration config, string provider)
    {
        var section = config.GetSection($"ConfigurationVault:Providers:{provider}");
        var source = section["Source"] ?? "Legacy";
        var id = section["PropertyId"];
        if (source.Equals("Legacy", StringComparison.OrdinalIgnoreCase))
        {
            if (id is not null) throw new ProviderConfigurationException("PropertyId requires the Vault source");
            return (false, null);
        }
        if (!source.Equals("Vault", StringComparison.OrdinalIgnoreCase))
            throw new ProviderConfigurationException("Source must be Legacy or Vault");
        if (id is null) return (true, null);
        if (provider != "growatt") throw new ProviderConfigurationException("shared provider accounts require the global scope");
        return (true, PositiveNumber(id));
    }

    internal sealed record ImportCommand(string Provider, long? Property, bool DryRun, long? ReplaceRevision);

    public static bool IsImportArgument(string arg) => arg is "--import-provider" or "--provider-property" or "--replace-revision" or "--dry-run" ||
        arg.StartsWith("--import-provider=", StringComparison.Ordinal) ||
        arg.StartsWith("--provider-property=", StringComparison.Ordinal) || arg.StartsWith("--replace-revision=", StringComparison.Ordinal);

    public static ImportCommand? ParseImport(string[] args)
    {
        var arguments = args.Where(IsImportArgument).ToArray();
        if (arguments.Length == 0) return null;
        var provider = SingleValue("--import-provider");
        if (provider is null || !Providers.Contains(provider, StringComparer.Ordinal))
            throw new ProviderConfigurationException("use --import-provider= with growatt, daikin, ewelink or tuya");
        var property = SingleValue("--provider-property");
        var revision = SingleValue("--replace-revision");
        return new(provider, property is null ? null : PositiveNumber(property), arguments.Contains("--dry-run"),
            revision is null ? null : PositiveNumber(revision));

        string? SingleValue(string key)
        {
            var prefix = key + "=";
            var matches = arguments.Where(arg => arg == key || arg.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || !matches[0].StartsWith(prefix, StringComparison.Ordinal))
                throw new ProviderConfigurationException("use one value per import option in --name=value form");
            return matches[0][prefix.Length..];
        }
    }

    public static async Task<bool> ImportAsync(WebApplication app, ImportCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var result = await app.Services.GetRequiredService<ProviderConfigurationStore>().ImportLegacyProviderAsync(
                command.Provider, command.Property, app.Configuration, command.DryRun, command.ReplaceRevision,
                app.Services.GetRequiredService<IDataProtectionProvider>(), timeout.Token);
            app.Logger.LogInformation("Provider import {Provider} (property {PropertyId}): {Status}. Revision {Revision}. Fields: {Fields}.",
                command.Provider, command.Property, result.Status, result.Revision, string.Join(", ", result.Fields));
            return result.Succeeded;
        }
        catch (Exception error) when (IsConfigurationFailure(error))
        {
            LogFailure(app, command.Provider, error);
            return false;
        }
    }

    private static long PositiveNumber(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed :
            throw new ProviderConfigurationException("property and revision must be positive integers");

    private static bool IsConfigurationFailure(Exception error) => error is ProviderConfigurationException or ConfigurationStorageException
        or CryptographicException or MySqlException or OptionsValidationException or OperationCanceledException;

    private static void LogFailure(WebApplication app, string provider, Exception error)
    {
        var reason = error switch
        {
            ProviderConfigurationException validation => validation.Message,
            ConfigurationStorageException => "stored configuration could not be read",
            CryptographicException => "vault key or encrypted secret could not be read",
            MySqlException sql => $"MySQL error {sql.ErrorCode} ({sql.SqlState})",
            OptionsValidationException => "invalid database bootstrap configuration",
            _ => "operation cancelled or timed out"
        };
        var fields = error is ProviderConfigurationException failure ? string.Join(", ", failure.Fields) : "";
        app.Logger.LogError("{Provider} configuration failed: {Reason}. Fields: {Fields}.", provider, reason, fields);
    }
}
