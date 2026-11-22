using System.Net;
using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Daikin;
using Microsoft.AspNetCore.WebUtilities;

namespace HomeAutomation.UnitTests;

public sealed class DaikinTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
    private const string Fixture = """
        [{"_id":"fixture","type":"dx4","deviceModel":"dx4","lastUpdateReceived":"2026-11-21T11:55:00Z",
          "timestamp":"2026-11-21T11:57:00Z",
          "isCloudConnectionUp":{"value":false},"managementPoints":[
          {"embeddedId":"zone","managementPointType":"climateControl","name":{"value":"Upstairs"},
           "onOffMode":{"settable":true,"values":["on","off"],"value":"on"},
           "operationMode":{"settable":true,"values":["heating","cooling","dry"],"value":"heating"},
           "sensoryData":{"value":{"roomTemperature":{"value":18},"outdoorTemperature":{"value":5}}},
           "temperatureControl":{"value":{"operationModes":{"heating":{"setpoints":{"roomTemperature":
             {"settable":true,"unit":"C","minValue":16,"maxValue":24,"stepValue":0.5,"value":18}}}}}}
          },{"embeddedId":"indoor","managementPointType":"indoorUnit"}]}]
        """;

    [Fact]
    public void MapsStableIdentityFreshnessAndDeviceSpecificControls()
    {
        using var json = JsonDocument.Parse(Fixture);
        var device = Assert.Single(DaikinMapping.Discover(json.RootElement, Now));
        Assert.Equal("fixture:zone", device.ExternalId);
        Assert.Equal(Connectivity.Offline, device.Connectivity);
        Assert.Equal(DaikinEquipment.AirConditioner, device.Equipment);
        Assert.Null(device.Water);
        Assert.Equal(Now.AddMinutes(-3), device.GatewayTimestamp);
        Assert.Equal(Now.AddMinutes(-5), device.SourceAt);
        Assert.Equal(ReadingQuality.Stale, Telemetry.Quality(device.ObservedAt, Now.AddHours(1), TimeSpan.FromMinutes(35)));
        Assert.Equal(18, device.Indoor);
        Assert.Equal(5, device.Outdoor);
        Assert.Null(device.Humidity);
        Assert.True(DaikinMapping.Validate(device, new("temperature", "18"))?.AlreadyApplied);
        Assert.NotNull(DaikinMapping.Validate(device, new("temperature", "19.5")));
        Assert.Null(DaikinMapping.Validate(device, new("temperature", "10")));
        Assert.Null(DaikinMapping.Validate(device, new("temperature", "19.2")));
        Assert.Null(DaikinMapping.Validate(device, new("mode", "dry")));
        Assert.Null(DaikinMapping.Validate(device, new("fan", "3")));

        using var duplicate = JsonDocument.Parse($"[{Fixture[1..^1]},{Fixture[1..^1]}]");
        Assert.Equal("duplicateIdentity", Assert.Throws<DaikinFailure>(() => DaikinMapping.Discover(duplicate.RootElement, Now)).Status);
    }

    [Fact]
    public void GatewayTimestampAloneDoesNotSupplyDeviceSourceTime()
    {
        using var json = JsonDocument.Parse(Fixture.Replace(
            "\"lastUpdateReceived\":\"2026-11-21T11:55:00Z\",", ""));
        var device = Assert.Single(DaikinMapping.Discover(json.RootElement, Now));
        Assert.Equal(Now.AddMinutes(-3), device.GatewayTimestamp);
        Assert.Null(device.SourceAt);
        Assert.Equal(Now, device.ObservedAt);
    }

    [Theory]
    [InlineData("C", true)]
    [InlineData("°C", true)]
    [InlineData("unknown", false)]
    public void HydronicTelemetryIsReadOnlyAndPreservesUnitsMetadataAndHistorySamples(string unit, bool usable)
    {
        var input = Fixture.Replace("roomTemperature", "leavingWaterTemperature")
            .Replace("\"unit\":\"C\"", $"\"unit\":\"{unit}\"")
            .Replace("\"leavingWaterTemperature\":{\"value\":18}",
                $"\"leavingWaterTemperature\":{{\"value\":41,\"unit\":\"{unit}\"}}")
            .Replace("\"onOffMode\":", """
                "controlMode":{"value":"leavingWaterTemperature"},
                "setpointMode":{"value":"fixed"},
                "isInWarningState":{"value":false},
                "isInEmergencyState":{"value":true},
                "onOffMode":
                """);
        using var json = JsonDocument.Parse(input);
        var pump = Assert.Single(DaikinMapping.Discover(json.RootElement, Now));
        Assert.Equal(DaikinEquipment.HeatPump, pump.Equipment);
        Assert.Empty(pump.Controls.Power);
        Assert.Null(DaikinMapping.Validate(pump, new("power", "off")));
        Assert.True(pump.Water!.TargetAdvertisedWritable);
        var mapped = DaikinService.TelemetryFor(pump);
        Assert.Equal(DeviceKind.HeatPump, mapped.Device.Kind);
        Assert.Equal(pump.ExternalId, mapped.Device.ExternalId);
        var samples = Assert.Single(Assert.Single(mapped.Batch.Devices).Endpoints).Samples;
        Assert.DoesNotContain(samples, sample => sample.Metric == Metric.TargetTemperature);
        Assert.Equal("leavingWaterTemperature", Assert.Single(samples, sample => sample.Metric == Metric.HeatingControlMode).Code);
        Assert.Equal("fixed", Assert.Single(samples, sample => sample.Metric == Metric.WaterSetpointMode).Code);
        Assert.False(Assert.Single(samples, sample => sample.Metric == Metric.HeatingWarning).Flag);
        Assert.True(Assert.Single(samples, sample => sample.Metric == Metric.HeatingEmergency).Flag);
        if (usable)
        {
            var leaving = Assert.Single(samples, sample => sample.Metric == Metric.LeavingWaterTemperature);
            Assert.Equal(41, leaving.Number);
            Assert.Equal("°C", Telemetry.Unit(leaving.Metric));
            Assert.Equal(pump.SourceAt, leaving.ObservedAt);
            Assert.Equal(18, Assert.Single(samples, sample => sample.Metric == Metric.TargetLeavingWaterTemperature).Number);
        }
        else
        {
            Assert.Null(pump.Water.Leaving);
            Assert.DoesNotContain(samples, sample => sample.Metric == Metric.TargetLeavingWaterTemperature);
        }
    }

    [Fact]
    public void MatchingProviderStateMustBeNewerAndBelongToTheDispatchedMode()
    {
        using var json = JsonDocument.Parse(Fixture);
        var ac = Assert.Single(DaikinMapping.Discover(json.RootElement, Now)) with { Connectivity = Connectivity.Online };
        var command = new DaikinOutcome("fixture", "temperature", "18", "uncertain", Now.AddMinutes(-10), "heating");
        Assert.True(DaikinService.MatchesCommand(ac, command));
        Assert.False(DaikinService.MatchesCommand(ac with { SourceAt = null }, command));
        Assert.False(DaikinService.MatchesCommand(ac with { SourceAt = command.UpdatedAt }, command));
        Assert.False(DaikinService.MatchesCommand(ac, command with { ModeContext = "cooling" }));
        Assert.False(DaikinService.MatchesCommand(ac with { Target = 19 }, command));
    }

    [Fact]
    public async Task WriteTransportRejectsReadOnlyOrHydronicDevicesBeforeCallingProvider()
    {
        using var json = JsonDocument.Parse(Fixture);
        var ac = Assert.Single(DaikinMapping.Discover(json.RootElement, Now));
        var operation = DaikinMapping.Validate(ac, new("power", "off"))!;
        using var disabled = Client((_, _) => throw new InvalidOperationException("Unexpected provider call."));
        Assert.Equal("readOnly", (await Assert.ThrowsAsync<DaikinFailure>(() =>
            disabled.PatchAsync("fixture", ac, operation, default))).Status);
        using var enabled = Client((_, _) => throw new InvalidOperationException("Unexpected provider call."), writes: true);
        Assert.Equal("readOnly", (await Assert.ThrowsAsync<DaikinFailure>(() =>
            enabled.PatchAsync("fixture", ac with { Equipment = DaikinEquipment.HeatPump }, operation, default))).Status);
    }

    [Fact]
    public async Task OAuthUsesPkceAndRefreshRotationAndRejectsRevokedGrant()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal("https://idp.onecta.daikineurope.com/v1/oidc/token", request.RequestUri!.AbsoluteUri);
            if (++calls == 1)
            {
                Assert.Contains("code_verifier=fixture-verifier", form);
                Assert.Equal(Options().RedirectUri, QueryHelpers.ParseQuery(form)["redirect_uri"].ToString());
                return Response(HttpStatusCode.OK, """{"token_type":"Bearer","access_token":"fixture-access","refresh_token":"fixture-refresh","expires_in":3600}""");
            }
            if (calls == 2)
            {
                Assert.Contains("refresh_token=fixture-refresh", form);
                return Response(HttpStatusCode.OK, """{"token_type":"Bearer","access_token":"fixture-next","refresh_token":"fixture-rotated","expires_in":3600}""");
            }
            return Response(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        });
        var link = client.Authorize("state", "challenge");
        Assert.Contains("code_challenge_method=S256", link);
        Assert.DoesNotContain("client_secret", link);
        var tokens = await client.TokenAsync("authorization_code", "fixture-code", "fixture-verifier", default);
        Assert.False(tokens.NeedsRefresh(Now));
        Assert.True(tokens.NeedsRefresh(Now.AddMinutes(58)));
        var next = await client.TokenAsync("refresh_token", tokens.RefreshToken, null, default);
        Assert.Equal("fixture-rotated", next.RefreshToken);
        Assert.Equal("reauthorizationRequired", (await Assert.ThrowsAsync<DaikinFailure>(() =>
            client.TokenAsync("refresh_token", next.RefreshToken, null, default))).Status);
    }

    [Theory]
    [InlineData("http://localhost:5173/api/v1/daikin/callback")]
    [InlineData("https://ha-dev.example.com:5443/api/v1/daikin/callback")]
    public void OAuthAuthorizePreservesTheExactRedirectAndEncodesTheExpectedParameters(string redirect)
    {
        var options = Options();
        options.RedirectUri = redirect;
        Assert.True(options.IsConfigured);
        using var client = new DaikinClient(new HttpClient(new Handler((_, _) =>
            throw new InvalidOperationException("URL generation should not make a request."))), options, new FixedClock());

        var uri = new Uri(client.Authorize("fixture-state", "fixture-challenge"));
        Assert.Equal("https://idp.onecta.daikineurope.com/v1/oidc/authorize", uri.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(7, query.Count);
        Assert.All(query.Values, value => Assert.Single(value));
        Assert.Equal(options.ClientId, query["client_id"].ToString());
        Assert.Equal(redirect, query["redirect_uri"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("openid onecta:basic.integration offline_access", query["scope"].ToString());
        Assert.Equal("fixture-state", query["state"].ToString());
        Assert.Equal("fixture-challenge", query["code_challenge"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
    }

    [Fact]
    public void TokenEnvelopeAndQuotaKeepCredentialsAndManualReserveSeparate()
    {
        var storage = new DaikinSecrets(Options());
        var tokens = new DaikinTokens("fixture-access", "fixture-refresh", Now);
        var envelope = storage.Seal("owner", tokens);
        Assert.Equal(tokens, storage.Open("owner", envelope));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => storage.Open("other", envelope));
        var history = Enumerable.Range(0, 160).Select(i => Now.UtcDateTime.AddMinutes(i - 160)).ToArray();
        Assert.Equal(Now.AddMinutes(-160).AddDays(1), DaikinStore.BudgetRetryAt(history, 200, 40, true));
        Assert.Null(DaikinStore.BudgetRetryAt(history, 200, 40, false));
    }

    [Fact]
    public async Task ThrottleCarriesRetryAfterAndAmbiguousPatchIsSentOnlyOnce()
    {
        var requests = 0;
        using var client = Client(async (request, ct) =>
        {
            requests++;
            if (request.Method == HttpMethod.Patch)
            {
                Assert.EndsWith("/characteristics/onOffMode", request.RequestUri!.AbsolutePath);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("off", body.RootElement.GetProperty("value").GetString());
                return Response(HttpStatusCode.GatewayTimeout, "");
            }
            var throttle = Response(HttpStatusCode.TooManyRequests, "");
            throttle.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return throttle;
        }, writes: true);
        var error = await Assert.ThrowsAsync<DaikinFailure>(() => client.DiscoverAsync("fixture", default));
        Assert.Equal("rateLimited", error.Status);
        Assert.Equal(Now.AddMinutes(10), error.RetryAt);
        using var json = JsonDocument.Parse(Fixture);
        var device = Assert.Single(DaikinMapping.Discover(json.RootElement, Now));
        var change = DaikinMapping.Validate(device, new("power", "off"))!;
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<DaikinFailure>(() =>
            client.PatchAsync("fixture", device, change, default))).Status);
        Assert.Equal(2, requests);
    }

    private static DaikinOptions Options() => new()
    {
        ClientId = "fixture-client", ClientSecret = "fixture-marker",
        RedirectUri = "http://localhost:5173/api/v1/daikin/callback",
        OperatorEmail = "operator@example.test", TokenKey = Convert.ToBase64String(new byte[32])
    };
    private static DaikinClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> call, bool writes = false)
    {
        var options = Options();
        options.EnableWrites = writes;
        return new(new HttpClient(new Handler(call)), options, new FixedClock());
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
