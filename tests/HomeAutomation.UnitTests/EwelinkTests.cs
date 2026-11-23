using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Ewelink;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace HomeAutomation.UnitTests;

public sealed class EwelinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
    private static readonly EwelinkOptions Options = new()
    {
        AppId = "fixture-app", AppSecret = "fixture-secret",
        RedirectUri = "https://example.test/api/v1/ewelink/callback", OperatorEmail = "operator@example.test"
    };

    [Fact]
    public async Task ExchangesAndRefreshesUsingOfficialSignedJsonAndRegionalHosts()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("eu-apia.coolkit.cc", request.RequestUri!.Host);
            Assert.Equal("fixture-app", Assert.Single(request.Headers.GetValues("X-CK-Appid")));
            Assert.Matches("^[a-z0-9]{8}$", Assert.Single(request.Headers.GetValues("X-CK-Nonce")));
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Sign", request.Headers.Authorization!.Scheme);
            Assert.Equal(Sign(bytes), request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(bytes);
            if (calls == 1)
            {
                Assert.Equal("/v2/user/oauth/token", request.RequestUri.AbsolutePath);
                Assert.Equal("fixture-code", json.RootElement.GetProperty("code").GetString());
                Assert.Equal(Options.RedirectUri, json.RootElement.GetProperty("redirectUrl").GetString());
                Assert.Equal("authorization_code", json.RootElement.GetProperty("grantType").GetString());
                return Response(JsonSerializer.Serialize(new
                {
                    error = 0, data = new
                    {
                        accessToken = "fixture-access", refreshToken = "fixture-refresh",
                        atExpiredTime = Now.AddDays(10).ToUnixTimeMilliseconds(),
                        rtExpiredTime = Now.AddDays(40).ToUnixTimeMilliseconds()
                    }
                }));
            }
            Assert.Equal("/v2/user/refresh", request.RequestUri.AbsolutePath);
            Assert.Equal("fixture-refresh", json.RootElement.GetProperty("rt").GetString());
            return Response("""{"error":0,"data":{"at":"fixture-rotated","rt":"fixture-new-refresh"}}""");
        }));
        var client = Client(http);
        var authorization = new Uri(client.Authorize("fixture-state"));
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal("c2ccdn.coolkit.cc", authorization.Host);
        Assert.Equal("fixture-state", query["state"].ToString());
        Assert.Equal(Sign(Encoding.UTF8.GetBytes($"fixture-app_{query["seq"]}")), query["authorization"].ToString());
        var tokens = await client.ExchangeAsync("fixture-code", "eu", default);
        Assert.Equal(Now.AddDays(10), tokens.AccessExpires);
        var refreshed = await client.RefreshAsync(tokens, default);
        Assert.Equal("fixture-new-refresh", refreshed.Refresh);
        Assert.Equal(Now.AddDays(30), refreshed.AccessExpires);
        await Assert.ThrowsAsync<EwelinkFailure>(() => client.ExchangeAsync("fixture-code", "untrusted", default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"error":405,"msg":"fixture explanation","data":{}}""", 405, "rejected")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":400,"msg":"fixture explanation","data":{}}""", 400, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "<html>fixture gateway</html>", null, "accessOrQuotaLimited")]
    public async Task ExchangeRetainsOnlySafeRejectionDetails(HttpStatusCode status, string body, int? providerCode, string reason)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }));
        var error = await Assert.ThrowsAsync<EwelinkFailure>(() => Client(http).ExchangeAsync("fixture-code", "eu", default));
        Assert.Equal((int)status, error.HttpStatus);
        Assert.Equal(providerCode, error.ProviderCode);
        Assert.Equal(reason, error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void VaultBindsCiphertextToTheLocalOwnerAndDeveloperApp()
    {
        var vault = new EwelinkVault(new EphemeralDataProtectionProvider());
        var tokens = new EwelinkTokens("eu", "fixture-access", "fixture-refresh", Now, Now);
        var envelope = vault.Seal("owner", "app", tokens);
        Assert.DoesNotContain("fixture-refresh", Encoding.UTF8.GetString(envelope));
        Assert.Equal(tokens, vault.Open("owner", "app", envelope));
        Assert.Throws<EwelinkFailure>(() => vault.Open("other", "app", envelope));
        Assert.Throws<EwelinkFailure>(() => vault.Open("owner", "other", envelope));
    }

    [Fact]
    public async Task DiscoveryReadsEveryReturnedHomeAndPrefersTheOwnedCopy()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-access", request.Headers.Authorization.Parameter);
            if (request.RequestUri!.AbsolutePath == "/v2/family")
                return Task.FromResult(Response("""{"error":0,"data":{"familyList":[{"id":"first"},{"id":"second"}]}}"""));
            Assert.Equal("/v2/device/thing", request.RequestUri.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("30", query["num"].ToString());
            return Task.FromResult(query["familyid"] == "first"
                ? Response("""
                    {"error":0,"data":{"total":1,"thingList":[{"index":1,"itemType":2,
                       "itemData":{"deviceid":"id1","name":"Shared copy","online":true,"params":{"switch":"off"}}}]}}
                    """)
                : Response("""
                    {"error":0,"data":{"total":3,"thingList":[{"index":1,"itemType":1,
                       "itemData":{"deviceid":"id1","name":"Owned relay","online":true,"params":{"switch":"on"}}},
                       {"index":2,"itemType":3,"itemData":{"deviceid":"group"}}]}}
                    """));
        }));
        var inventory = await Client(http).DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(3, calls);
        Assert.True(inventory.Partial);
        var device = Assert.Single(inventory.Devices);
        Assert.Equal("id1", device.ExternalId);
        Assert.Equal("Owned relay", device.Name);
        Assert.True(device.Relay);
        Assert.True(Assert.Single(Assert.Single(Assert.Single(device.Telemetry(true).Batch.Devices).Endpoints).Samples).Flag);
        Assert.Contains(Capability.Switch, Assert.Single(device.Telemetry(true).Inventory.Endpoints).Capabilities);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PaginationHandlesAnOverlappingCursorAndStopsIfItCannotAdvance(bool advances)
    {
        var pages = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/family")
                return Task.FromResult(Response("""{"error":0,"data":{"familyList":[{"id":"home"}]}}"""));
            pages++;
            var cursor = QueryHelpers.ParseQuery(request.RequestUri.Query)["beginIndex"].ToString();
            Assert.Equal(pages == 1 ? "-9999999" : "29", cursor);
            var indexes = pages == 1 || !advances ? Enumerable.Range(0, 30) : [29, 30];
            var items = indexes.Select(index => new
            {
                index, itemType = 1, itemData = new { deviceid = $"fixture-{index}", online = true }
            });
            return Task.FromResult(Response(JsonSerializer.Serialize(new { error = 0, data = new { total = 31, thingList = items } })));
        }));
        var inventory = await Client(http).DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(2, pages);
        Assert.Equal(advances ? 31 : 30, inventory.Devices.Count);
        Assert.Equal(!advances, inventory.Partial);
    }

    [Theory]
    [InlineData("""{"switch":"off"}""", true, false)]
    [InlineData("""{"switch":"ON"}""", false, null)]
    [InlineData("""{"switch":true}""", false, null)]
    [InlineData("""{"switch":"on","switches":[]}""", false, null)]
    [InlineData("""{"currentTemperature":21,"currentHumidity":45}""", false, null)]
    public void OnlyVerifiedRelayFieldsBecomeCapabilities(string parameters, bool capable, bool? relay)
    {
        using var json = JsonDocument.Parse($$$"""
            {"itemType":1,"itemData":{"deviceid":"fixture","name":"Thermostat","online":true,"params":{{{parameters}}} } }
            """);
        var device = EwelinkMapping.Map(json.RootElement, Now)!;
        Assert.Equal(capable, device.HasRelay);
        Assert.Equal(relay, device.Relay);
        Assert.DoesNotContain(Capability.TemperatureMeasurement, Assert.Single(device.Telemetry(true).Inventory.Endpoints).Capabilities);
    }

    [Fact]
    public void OfflineAndMalformedReadingsRetainThePreviousValueWithoutAnotherSample()
    {
        using var online = JsonDocument.Parse("""{"itemType":1,"itemData":{"deviceid":"fixture","online":true,"params":{"switch":"on"}}}""");
        using var offline = JsonDocument.Parse("""{"itemType":1,"itemData":{"deviceid":"fixture","online":false,"params":{"switch":"off"}}}""");
        using var unknown = JsonDocument.Parse("""{"itemType":1,"itemData":{"deviceid":"fixture","online":"false"}}""");
        var previous = EwelinkMapping.Map(online.RootElement, Now)!;
        var disconnected = EwelinkMapping.Merge(EwelinkMapping.Map(offline.RootElement, Now.AddMinutes(30))!, previous);
        Assert.Equal(Connectivity.Offline, disconnected.Connectivity);
        Assert.True(disconnected.Relay);
        Assert.Equal(Now, disconnected.RelayAt);
        Assert.Empty(Assert.Single(Assert.Single(disconnected.Telemetry(false).Batch.Devices).Endpoints).Samples);
        Assert.Equal(Connectivity.Unknown, EwelinkMapping.Map(unknown.RootElement, Now)!.Connectivity);
    }

    [Theory]
    [InlineData(429, 0, "rateLimited")]
    [InlineData(403, 0, "accessOrQuotaLimited")]
    [InlineData(200, 412, "quotaExceeded")]
    [InlineData(200, 407, "accessDenied")]
    [InlineData(200, 402, "reauthorizationRequired")]
    [InlineData(503, 0, "unavailable")]
    public async Task DiscoveryReturnsSafeErrorsWithoutRetrying(int status, int code, string expected)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = Response(JsonSerializer.Serialize(new { error = code, msg = "provider details withheld", data = new { } }),
                (HttpStatusCode)status);
            if (status == 429) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }));
        var failure = await Assert.ThrowsAsync<EwelinkFailure>(() => Client(http)
            .DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default));
        Assert.Equal(expected, failure.Status);
        if (status == 429) Assert.Equal(Now.AddMinutes(10), failure.RetryAt);
        Assert.Equal(1, calls);
    }

    private static EwelinkClient Client(HttpClient http)
    {
        var clock = new FixtureClock();
        return new(http, Options, clock, new EwelinkPace(clock));
    }

    private static string Sign(byte[] bytes) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture-secret"), bytes));
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class FixtureClock : TimeProvider
    {
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref timestamp, TimeSpan.TicksPerSecond);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
