using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.Extensions.Configuration;

namespace HomeAutomation.Infrastructure.Configuration;

public sealed class ProviderConfigurationException(string reason, params string[] fields) : Exception(reason)
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

internal sealed class ProviderOptionSnapshot(
    int pollSeconds, string secret, Dictionary<string, string> identifiers, JsonElement operational)
{
    public int PollSeconds { get; } = pollSeconds;
    public string Secret { get; } = secret;
    public Dictionary<string, string> Identifiers { get; } = identifiers;
    public JsonElement Operational { get; } = operational;
}

internal static class ProviderOptionMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private sealed record Operations(string Identity, JsonElement Settings);
    private sealed record GrowattSettings(bool Enabled, string SiteKey, string MasterId, string SlaveId, string BatterySourceId,
        string TimeZone, int FreshnessSeconds, int TimeoutSeconds, bool SumIndependentPv, bool MasterReportsWholeSiteLoad,
        bool TelemetryDiagnostics);
    private sealed record DaikinSettings(string? RedirectUri, string? OperatorEmail, bool UsePkce, bool EnableWrites,
        int DailyLimit, int ManualReserve, int FreshMinutes);
    private sealed record EwelinkSettings(string? RedirectUri, string? OperatorEmail, bool InventoryDiagnostics,
        bool EnableWebSocket, bool WebSocketDiagnostics, bool EnableLiveElectricalReporting, bool EnableWrites,
        Dictionary<string, bool> RelayWriteDevices);
    private sealed record TuyaSettings(string? DataCenter, string? OperatorEmail, int HistoryReadsPerPoll);

    public static object ReadLegacy(string provider, IConfiguration configuration)
    {
        try
        {
            return provider switch
            {
                "growatt" => configuration.GetSection("Growatt").Get<GrowattOptions>() ?? new GrowattOptions(),
                "daikin" => configuration.GetSection("Daikin").Get<DaikinOptions>() ?? new DaikinOptions(),
                "ewelink" => configuration.GetSection("Ewelink").Get<EwelinkOptions>() ?? new EwelinkOptions(),
                "tuya" => configuration.GetSection("Tuya").Get<TuyaOptions>() ?? new TuyaOptions(),
                _ => throw new ProviderConfigurationException("unsupported provider")
            };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new ProviderConfigurationException("invalid legacy field type");
        }
    }

    public static ProviderOptionSnapshot Capture(object options)
    {
        Validate(options);
        return options switch
        {
            GrowattOptions o => Pack(o.PollSeconds, o.ApiToken, [], Identity(o),
                new GrowattSettings(o.Enabled, o.SiteKey, o.MasterId, o.SlaveId, o.BatterySourceId,
                    o.TimeZone, o.FreshnessSeconds, o.TimeoutSeconds, o.SumIndependentPv, o.MasterReportsWholeSiteLoad, o.TelemetryDiagnostics)),
            DaikinOptions o => Pack(o.PollMinutes * 60, o.ClientSecret!, new() { ["clientId"] = o.ClientId! }, Identity(o),
                new DaikinSettings(o.RedirectUri, o.OperatorEmail, o.UsePkce, o.EnableWrites, o.DailyLimit, o.ManualReserve, o.FreshMinutes)),
            EwelinkOptions o => Pack(o.PollMinutes * 60, o.AppSecret!, new() { ["appId"] = o.AppId! }, Identity(o),
                new EwelinkSettings(o.RedirectUri, o.OperatorEmail, o.InventoryDiagnostics, o.EnableWebSocket, o.WebSocketDiagnostics,
                    o.EnableLiveElectricalReporting, o.EnableWrites, o.RelayWriteDevices)),
            TuyaOptions o => Pack(o.PollMinutes * 60, o.AccessSecret!,
                new() { ["accessId"] = o.AccessId!, ["userId"] = o.UserId! }, Identity(o),
                new TuyaSettings(o.DataCenter, o.OperatorEmail, o.HistoryReadsPerPoll)),
            _ => throw new ProviderConfigurationException("unsupported provider")
        };
    }

    public static object Restore(string provider, ProviderOptionSnapshot snapshot, string? daikinGrantKey)
    {
        try
        {
            var saved = snapshot.Operational.Deserialize<Operations>(Json) ?? throw new JsonException();
            var fields = snapshot.Identifiers;
            var minutes = snapshot.PollSeconds / 60;
            object result = provider switch
            {
                "growatt" when saved.Settings.Deserialize<GrowattSettings>(Json) is { } o => new GrowattOptions
                {
                    Enabled = o.Enabled, ApiToken = snapshot.Secret, SiteKey = o.SiteKey, MasterId = o.MasterId, SlaveId = o.SlaveId,
                    BatterySourceId = o.BatterySourceId, TimeZone = o.TimeZone, PollSeconds = snapshot.PollSeconds,
                    FreshnessSeconds = o.FreshnessSeconds, TimeoutSeconds = o.TimeoutSeconds, SumIndependentPv = o.SumIndependentPv,
                    MasterReportsWholeSiteLoad = o.MasterReportsWholeSiteLoad, TelemetryDiagnostics = o.TelemetryDiagnostics
                },
                "daikin" when saved.Settings.Deserialize<DaikinSettings>(Json) is { } o => new DaikinOptions
                {
                    ClientId = fields.GetValueOrDefault("clientId"), ClientSecret = snapshot.Secret, TokenKey = daikinGrantKey,
                    RedirectUri = o.RedirectUri, OperatorEmail = o.OperatorEmail, PollMinutes = minutes, UsePkce = o.UsePkce,
                    EnableWrites = o.EnableWrites, DailyLimit = o.DailyLimit, ManualReserve = o.ManualReserve, FreshMinutes = o.FreshMinutes
                },
                "ewelink" when saved.Settings.Deserialize<EwelinkSettings>(Json) is { RelayWriteDevices: not null } o => new EwelinkOptions
                {
                    AppId = fields.GetValueOrDefault("appId"), AppSecret = snapshot.Secret, RedirectUri = o.RedirectUri,
                    OperatorEmail = o.OperatorEmail, PollMinutes = minutes, InventoryDiagnostics = o.InventoryDiagnostics,
                    EnableWebSocket = o.EnableWebSocket, WebSocketDiagnostics = o.WebSocketDiagnostics,
                    EnableLiveElectricalReporting = o.EnableLiveElectricalReporting, EnableWrites = o.EnableWrites,
                    RelayWriteDevices = new(o.RelayWriteDevices, StringComparer.OrdinalIgnoreCase)
                },
                "tuya" when saved.Settings.Deserialize<TuyaSettings>(Json) is { } o => new TuyaOptions
                {
                    AccessId = fields.GetValueOrDefault("accessId"), UserId = fields.GetValueOrDefault("userId"),
                    AccessSecret = snapshot.Secret, DataCenter = o.DataCenter, OperatorEmail = o.OperatorEmail,
                    PollMinutes = minutes, HistoryReadsPerPoll = o.HistoryReadsPerPoll
                },
                _ => throw new JsonException()
            };
            Validate(result);
            if (saved.Identity != Identity(result))
                throw new ProviderConfigurationException("provider identity differs from the imported profile; review and reimport before activation");
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new ProviderConfigurationException("invalid stored operational settings");
        }
    }

    private static ProviderOptionSnapshot Pack<T>(int poll, string secret, Dictionary<string, string> ids, string identity, T settings) =>
        new(poll, secret, ids, JsonSerializer.SerializeToElement(new Operations(identity, JsonSerializer.SerializeToElement(settings, Json)), Json));

    private static string Identity(object options)
    {
        string?[] fields = options switch
        {
            GrowattOptions o => ["growatt", o.SiteKey, o.MasterId, o.SlaveId, o.BatterySourceId],
            DaikinOptions o => ["daikin", o.ClientId, o.OperatorEmail?.ToUpperInvariant()],
            EwelinkOptions o => ["ewelink", o.AppId, o.OperatorEmail?.ToUpperInvariant()],
            TuyaOptions o => ["tuya", o.AccessId, o.UserId, o.Host, o.OperatorEmail?.ToUpperInvariant()],
            _ => throw new ProviderConfigurationException("unsupported provider")
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    private static void Validate(object options)
    {
        (string Field, string? Value)[] required = options switch
        {
            GrowattOptions o => [(nameof(o.ApiToken), o.ApiToken), (nameof(o.SiteKey), o.SiteKey), (nameof(o.MasterId), o.MasterId),
                (nameof(o.SlaveId), o.SlaveId), (nameof(o.BatterySourceId), o.BatterySourceId), (nameof(o.TimeZone), o.TimeZone)],
            DaikinOptions o => [(nameof(o.ClientId), o.ClientId), (nameof(o.ClientSecret), o.ClientSecret),
                (nameof(o.RedirectUri), o.RedirectUri), (nameof(o.OperatorEmail), o.OperatorEmail), ("Daikin:TokenKey", o.TokenKey)],
            EwelinkOptions o => [(nameof(o.AppId), o.AppId), (nameof(o.AppSecret), o.AppSecret),
                (nameof(o.RedirectUri), o.RedirectUri), (nameof(o.OperatorEmail), o.OperatorEmail)],
            TuyaOptions o => [(nameof(o.AccessId), o.AccessId), (nameof(o.AccessSecret), o.AccessSecret), (nameof(o.UserId), o.UserId),
                (nameof(o.DataCenter), o.DataCenter), (nameof(o.OperatorEmail), o.OperatorEmail)],
            _ => throw new ProviderConfigurationException("unsupported provider")
        };
        var missing = required.Where(field => string.IsNullOrWhiteSpace(field.Value)).Select(field => field.Field).ToArray();
        if (missing.Length != 0) throw new ProviderConfigurationException("missing required fields", missing);
        var invalid = options switch
        {
            GrowattOptions o when !o.HasCloudConfiguration() =>
                "ApiToken, SiteKey, MasterId, SlaveId, BatterySourceId, TimeZone, PollSeconds, FreshnessSeconds, TimeoutSeconds",
            DaikinOptions o when !o.IsConfigured => "RedirectUri, Daikin:TokenKey, PollMinutes, FreshMinutes, DailyLimit, ManualReserve",
            EwelinkOptions o when !o.IsConfigured => "AppId, RedirectUri, PollMinutes",
            TuyaOptions o when !o.IsConfigured || o.HistoryReadsPerPoll is < 0 or > 4 =>
                "DataCenter, AccessId, UserId, PollMinutes, HistoryReadsPerPoll",
            _ => null
        };
        if (invalid is not null) throw new ProviderConfigurationException("provider validation failed; review these fields",
            invalid.Split(", ", StringSplitOptions.RemoveEmptyEntries));
    }
}
