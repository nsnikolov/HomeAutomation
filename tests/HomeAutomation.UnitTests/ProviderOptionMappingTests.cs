using System.Text.Json;
using HomeAutomation.Infrastructure.Configuration;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.Extensions.Configuration;

namespace HomeAutomation.UnitTests;

public sealed class ProviderOptionMappingTests
{
    [Theory]
    [InlineData("growatt")]
    [InlineData("daikin")]
    [InlineData("ewelink")]
    [InlineData("tuya")]
    public void RoundTripPreservesProviderFieldsAndKeepsSecretsOutsideOperationalSettings(string provider)
    {
        var key = Convert.ToBase64String(new byte[32]);
        object original = provider switch
        {
            "growatt" => new GrowattOptions
            {
                Enabled = true, ApiToken = "fixture-growatt-secret", SiteKey = "countryside", MasterId = "master", SlaveId = "slave",
                BatterySourceId = "slave", TimeZone = "Europe/Sofia", PollSeconds = 420, FreshnessSeconds = 1000, TimeoutSeconds = 9,
                SumIndependentPv = true, MasterReportsWholeSiteLoad = true, TelemetryDiagnostics = true,
            },
            "daikin" => new DaikinOptions
            {
                ClientId = "fixture-client", ClientSecret = "fixture-daikin-secret", TokenKey = key,
                OperatorEmail = "operator@example.test", RedirectUri = "https://example.test/api/v1/daikin/callback",
                PollMinutes = 17, DailyLimit = 150, ManualReserve = 30, FreshMinutes = 40, EnableWrites = true, UsePkce = false,
            },
            "ewelink" => new EwelinkOptions
            {
                AppId = "fixture-app", AppSecret = "fixture-ewelink-secret", OperatorEmail = "operator@example.test",
                RedirectUri = "https://example.test/api/v1/ewelink/callback", PollMinutes = 7, InventoryDiagnostics = true,
                EnableWebSocket = true, WebSocketDiagnostics = true, EnableLiveElectricalReporting = true, EnableWrites = true,
                ContinuousElectricalCollection = true, ElectricalCollectionSeconds = 1200,
                RelayWriteDevices = new(StringComparer.OrdinalIgnoreCase) { ["Stable-Relay"] = true },
            },
            _ => new TuyaOptions
            {
                AccessId = "fixture-project", UserId = "fixture-user", AccessSecret = "fixture-tuya-secret",
                DataCenter = "central-europe", OperatorEmail = "operator@example.test", PollMinutes = 45, HistoryReadsPerPoll = 1,
            }
        };
        var snapshot = ProviderOptionMapping.Capture(original);
        var reloaded = ProviderOptionMapping.Restore(provider, snapshot, key);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original, original.GetType()),
            JsonSerializer.SerializeToElement(reloaded, reloaded.GetType())));
        Assert.DoesNotContain(snapshot.Secret, snapshot.Operational.GetRawText());
        Assert.DoesNotContain(key, snapshot.Operational.GetRawText());
        if (reloaded is EwelinkOptions relay) Assert.True(relay.RelayWriteDevices["STABLE-RELAY"]);
    }

    [Fact]
    public void LegacyConfigurationUsesNormalPrecedenceAndReportsMissingFieldsWithoutValues()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Growatt:ApiToken"] = "first-fixture", ["Growatt:PollSeconds"] = "600"
        }).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Growatt:ApiToken"] = "higher-precedence-fixture"
        }).Build();
        var settings = Assert.IsType<GrowattOptions>(ProviderOptionMapping.ReadLegacy("growatt", configuration));
        Assert.Equal("higher-precedence-fixture", settings.ApiToken);
        Assert.Equal(600, settings.PollSeconds);
        var failure = Assert.Throws<ProviderConfigurationException>(() => ProviderOptionMapping.Capture(settings));
        Assert.Contains("SiteKey", failure.Fields);
        Assert.Contains("BatterySourceId", failure.Fields);
        Assert.DoesNotContain(settings.ApiToken, failure.Message);
    }
}
