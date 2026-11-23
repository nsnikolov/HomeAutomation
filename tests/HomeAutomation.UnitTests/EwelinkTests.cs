using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Ewelink;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

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
    public void PollingDefaultsToFiveMinutesAndRejectsShorterIntervals()
    {
        var options = new EwelinkOptions
        {
            AppId = Options.AppId, AppSecret = Options.AppSecret,
            RedirectUri = Options.RedirectUri, OperatorEmail = Options.OperatorEmail
        };
        Assert.Equal(5, options.PollMinutes);
        Assert.True(options.IsConfigured);
        options.PollMinutes = 4;
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletedDiscoveryReportsInventoryOnlyWhenDiagnosticsAreEnabled(bool enabled)
    {
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Response("""{"error":0,"data":{"familyList":[]}}"""))));
        var clock = new FixtureClock();
        var logger = new CaptureLogger();
        var options = new EwelinkOptions { AppId = Options.AppId, InventoryDiagnostics = enabled };
        var client = new EwelinkClient(http, options, clock, new EwelinkPace(clock), logger);
        await client.DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(enabled, logger.Messages.Any(message => message.Contains("inventory finished", StringComparison.Ordinal)));
    }

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
        Assert.Equal(4, calls);
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
    public async Task CurrentHomeCanSupplyASeventhDeviceWithoutInventingMissingThings(bool returned)
    {
        var homes = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/family")
                return Task.FromResult(Response("""{"error":0,"data":{"familyList":[{"id":"listed"}],"currentFamilyId":"current"}}"""));
            var home = QueryHelpers.ParseQuery(request.RequestUri.Query)["familyid"].ToString();
            homes.Add(home);
            var count = home == "listed" ? 6 : returned ? 2 : 1;
            var items = Enumerable.Range(0, count).Select(index => new
            {
                index, itemType = home == "listed" ? 1 : 2,
                itemData = new { deviceid = home == "listed" ? $"fixture-{index}" : index == 0 ? "fixture-0" : "fixture-missing" }
            });
            return Task.FromResult(Response(JsonSerializer.Serialize(new
            {
                error = 0, data = new { total = home == "listed" ? 6 : 2, thingList = items }
            })));
        }));
        var inventory = await Client(http).DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(returned ? ["listed", "current"] : new[] { "listed", "current", "current" }, homes);
        Assert.Equal(returned ? 7 : 6, inventory.Devices.Count);
        Assert.Equal(!returned, inventory.Partial);
        Assert.False(Assert.Single(inventory.Devices, device => device.ExternalId == "fixture-0").Shared);
    }

    [Theory]
    [InlineData("fixture-thermostat", 1, false)]
    [InlineData("invalid identity", 0, true)]
    public async Task DiscoveryDoesNotExcludeUnknownThermostatProtocols(string identity, int expected, bool partial)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/v2/family"
                ? Response("""{"error":0,"data":{"familyList":[{"id":"fixture"}]}}""")
                : Response(JsonSerializer.Serialize(new
                {
                    error = 0,
                    data = new
                    {
                        total = 1, thingList = new[] { new { index = 0, itemType = 2,
                            itemData = new { deviceid = identity, extra = new { uiid = 999 }, online = true } } }
                    }
                })))));
        var result = await Client(http).DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(expected, result.Devices.Count);
        Assert.Equal(partial, result.Partial);
        Assert.All(result.Devices, device => Assert.Empty(device.SupportedMetrics()));
    }

    [Fact]
    public void DocumentedReadingsUseTheirEngineeringUnitsAndDoNotGenerateNumericHistory()
    {
        var thermostat = MapMeasurements(181, """{"switch":"on","sensorType":"AM2301","currentTemperature":"12.9","currentHumidity":"100"}""");
        var climate = Assert.IsAssignableFrom<IReadOnlyDictionary<Metric, EwelinkMeasurement>>(thermostat.Measurements);
        Assert.Equal(12.9, climate[Metric.Temperature].Value);
        Assert.Equal(100, climate[Metric.Humidity].Value);
        var meter = MapMeasurements(32, """{"switch":"off","power":"815.42","voltage":"230.09","current":"3.51","oneKwhData":"2.98","dayKwh":"1.2"}""");
        var electrical = Assert.IsAssignableFrom<IReadOnlyDictionary<Metric, EwelinkMeasurement>>(meter.Measurements);
        Assert.Equal(815.42, electrical[Metric.ActivePower].Value);
        Assert.Equal(230.09, electrical[Metric.Voltage].Value);
        Assert.Equal(3.51, electrical[Metric.Current].Value);
        Assert.Equal(3, electrical.Count);
        var (inventory, batch) = meter.Telemetry(true);
        Assert.Equal(Access.ReadOnly, Assert.Single(inventory.Endpoints).AccessFor(Capability.PowerMeasurement));
        Assert.Equal(Metric.SwitchOn, Assert.Single(Assert.Single(Assert.Single(batch.Devices).Endpoints).Samples).Metric);
        Assert.Null(MapMeasurements(77, """{"power":"815.42","currentTemperature":"21.9"}""").Measurements);
        Assert.Null(MapMeasurements(32, """{"switch":"on","power":81542,"current":"-3","voltage":"unavailable"}""").Measurements);
        Assert.Null(MapMeasurements(181, """{"sensorType":"MS01","currentHumidity":"50"}""").Measurements);
        Assert.Null(MapMeasurements(181, """{"sensorType":"AM2301","currentHumidity":"101","currentTemperature":"unavailable"}""").Measurements);
    }

    [Fact]
    public void MissingAndRepeatedValuesDoNotBecomeNewReadings()
    {
        var prior = MapMeasurements(15, """{"sensorType":"AM2301","currentTemperature":"12.9","currentHumidity":"100"}""");
        var received = Now.AddMinutes(30);
        var next = MapMeasurements(15, """{"sensorType":"AM2301","currentTemperature":"12.9"}""", received: received);
        var merged = EwelinkMapping.Merge(next, prior);
        Assert.Equal(new(12.9, Now), merged.Measurements![Metric.Temperature]);
        Assert.Equal(new(100, Now, false), merged.Measurements[Metric.Humidity]);
        var offline = EwelinkMapping.Merge(MapMeasurements(15,
            """{"sensorType":"AM2301","currentTemperature":"19"}""", online: false), merged);
        Assert.Equal(new(12.9, Now, false), offline.Measurements![Metric.Temperature]);
    }

    [Fact]
    public void Thr320dModelRecognizesProbeCapabilitiesWithoutInventingReadings()
    {
        using var json = JsonDocument.Parse("""
            {"itemType":1,"itemData":{"deviceid":"fixture","productModel":"THR320D","online":true,
              "params":{"sensorType":"THS01","currentTemperature":"12.9","currentHumidity":"unavailable"}}}
            """);
        var device = EwelinkMapping.Map(json.RootElement, Now)!;
        Assert.Equal(12.9, device.Measurements![Metric.Temperature].Value);
        Assert.False(device.Measurements.ContainsKey(Metric.Humidity));
        Assert.Equal([Metric.Temperature, Metric.Humidity], device.SupportedMetrics());
        var replacedProbe = EwelinkMapping.Merge(MapMeasurements(181,
            """{"sensorType":"MS01","currentHumidity":"60"}"""), device);
        Assert.Empty(replacedProbe.SupportedMetrics());
        Assert.DoesNotContain(Metric.ActivePower, MapMeasurements(32, """{"switch":"on"}""").SupportedMetrics());
    }

    [Fact]
    public void DiagnosticOutputUsesAnAllowlistAndOmitsRawFieldsAndValues()
    {
        using var json = JsonDocument.Parse("""
            [{"itemType":1,"itemData":{"deviceid":"private-id","name":"private-name","productModel":"PSF-X67","online":true,"extra":{"uiid":32},
              "params":{"power":"895.42","private-field":"private-value","devicekey":"private-key"}}},
             {"itemType":2,"itemData":{"deviceid":"private-shared","productModel":"private-model"}}]
            """);
        var logger = new CaptureLogger();
        EwelinkInventoryDiagnostics.LogPage(logger, json.RootElement.EnumerateArray().ToArray(), 3);
        var log = string.Join("\n", logger.Messages);
        Assert.Contains("power=String", log);
        Assert.Contains("PowerMeasurement", log);
        Assert.Contains("returned 2 of 3", log);
        Assert.Contains("model PSF-X67, UIID 32", log);
        Assert.Contains("model other", log);
        Assert.DoesNotContain("private", log);
        Assert.DoesNotContain("895.42", log);
        Assert.DoesNotContain("devicekey", log);
    }

    private static EwelinkDevice MapMeasurements(int uiid, string parameters, bool online = true, DateTimeOffset? received = null)
    {
        using var json = JsonDocument.Parse($$$"""
            {"itemType":1,"itemData":{"deviceid":"fixture","online":{{{(online ? "true" : "false")}}},
              "extra":{"uiid":{{{uiid}}}},"params":{{{parameters}}}}}
            """);
        return EwelinkMapping.Map(json.RootElement, received ?? Now)!;
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
    [InlineData("seventh", 7, "reportedTotalReached")]
    [InlineData("repeated", 6, "cursorDidNotAdvance")]
    [InlineData("empty", 6, "emptyPage")]
    public async Task AShortPageBelowTheReportedTotalContinuesOnlyWithCursorProgress(string nextPage, int expected, string stopReason)
    {
        var cursors = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/family")
                return Task.FromResult(Response("""{"error":0,"data":{"familyList":[{"id":"home"}]}}"""));

            var cursor = QueryHelpers.ParseQuery(request.RequestUri.Query)["beginIndex"].ToString();
            cursors.Add(cursor);
            var positions = cursor switch
            {
                "-9999999" => new[] { -5, -3, 2, 9, 11, 16 },
                "16" when nextPage == "seventh" => [16, 30],
                "16" when nextPage == "repeated" => [16],
                "16" when nextPage == "empty" => [],
                _ => throw new InvalidOperationException("Unexpected pagination request.")
            };
            var rows = positions.Select(index => new { index, itemType = 1, itemData = new { deviceid = $"fixture-{index}" } });
            return Task.FromResult(Response(JsonSerializer.Serialize(new
            {
                error = 0, data = new { total = nextPage == "empty" && cursor == "16" ? (int?)null : 7, thingList = rows }
            })));
        }));
        var clock = new FixtureClock();
        var logger = new CaptureLogger();
        var options = new EwelinkOptions { AppId = Options.AppId, InventoryDiagnostics = true };
        var client = new EwelinkClient(http, options, clock, new EwelinkPace(clock), logger);
        var inventory = await client.DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);

        Assert.Equal(["-9999999", "16"], cursors);
        Assert.Equal(expected, inventory.Devices.Count);
        Assert.Equal(expected == 6, inventory.Partial);
        Assert.Equal(expected == 7, inventory.Devices.Any(device => device.ExternalId == "fixture-30"));
        Assert.Contains(logger.Messages, message => message.Contains(stopReason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdvancingShortPagesStillStopAtTheDiscoveryRequestBudget()
    {
        var pages = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/family")
                return Task.FromResult(Response("""{"error":0,"data":{"familyList":[{"id":"home"}]}}"""));
            var index = ++pages;
            var row = new { index, itemType = 1, itemData = new { deviceid = $"fixture-{index}" } };
            return Task.FromResult(Response(JsonSerializer.Serialize(new { error = 0, data = new { total = 100, thingList = new[] { row } } })));
        }));
        var inventory = await Client(http).DiscoverAsync(new("eu", "fixture-access", "unused", Now, Now), default);
        Assert.Equal(11, pages);
        Assert.Equal(11, inventory.Devices.Count);
        Assert.True(inventory.Partial);
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
    private sealed class CaptureLogger : ILogger<EwelinkClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
