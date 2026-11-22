using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Tuya;

namespace HomeAutomation.UnitTests;

public sealed class TuyaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
    private static readonly TuyaOptions Options = new()
    {
        AccessId = "fixture-id", AccessSecret = "fixture-secret", UserId = "fixture-user",
        DataCenter = "western-europe", OperatorEmail = "operator@example.test"
    };

    [Theory]
    [InlineData(null, "/v1.0/token?grant_type=1", "B5A5DA4FFD5EC42FC638C01EEB761B6555C1D263B30C64F703DEC4AEDFDC3587")]
    [InlineData("fixture-token", "/v1.0/users/fixture-user/devices?page_no=1&page_size=25",
        "7767F17E2D21313E19D6878E735B7FF4282B8D256AE108CFD15F156E1B4F565D")]
    public void SignsCanonicalPathsWithTheDocumentedTokenAndBusinessInputs(string? token, string path, string expected) =>
        Assert.Equal(expected, TuyaClient.Sign("fixture-id", "fixture-secret", token, "1760000000000", "sample-nonce", path));

    [Fact]
    public async Task DeviceDiscoveryReusesTheProjectTokenAndDeduplicatesPages()
    {
        var clock = new FixtureClock();
        using var session = new TuyaSession();
        var calls = new List<string>();
        using var client = Client(clock, session, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("openapi-weaz.tuyaeu.com", request.RequestUri!.Host);
            Assert.Equal("fixture-id", Header(request, "client_id"));
            Assert.Equal("HMAC-SHA256", Header(request, "sign_method"));
            Assert.Matches("^[a-f0-9]{32}$", Header(request, "nonce"));
            var path = request.RequestUri.PathAndQuery;
            calls.Add(path);
            if (path == "/v1.0/token?grant_type=1")
            {
                Assert.False(request.Headers.Contains("access_token"));
                return Token("fixture-token");
            }
            Assert.Equal("fixture-token", Header(request, "access_token"));
            var ids = path.EndsWith("page_no=1&page_size=25", StringComparison.Ordinal) ? Enumerable.Range(0, 25) : [24, 25];
            return Success(ids.Select(id => new
            {
                id = $"fixture-{id}", name = $"Device {id}", online = id != 25,
                update_time = Now.AddMinutes(-1).ToUnixTimeSeconds(),
                local_key = "private-example", status = Array.Empty<object>()
            }));
        });
        var inventory = await client.DiscoverAsync(default);
        Assert.False(inventory.Partial);
        Assert.Equal(3, calls.Count);
        Assert.Equal(26, inventory.Devices.Count);
        Assert.Equal(Connectivity.Offline, inventory.Devices.Single(device => device.Id == "fixture-25").Connectivity);
        Assert.Equal(Now.AddMinutes(-1), inventory.Devices[0].ReportedAt);
    }

    [Fact]
    public async Task ExpiredTokensAreReissuedAndARejectedCachedTokenIsReplayedOnlyOnce()
    {
        var clock = new FixtureClock();
        using var session = new TuyaSession { Token = new("expired", Now.AddSeconds(-1)) };
        var tokenCalls = 0;
        var reads = 0;
        using var client = Client(clock, session, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/token")
                return Token($"fixture-renewal-{++tokenCalls}");
            reads++;
            return reads == 1 ? Success(Array.Empty<object>()) : Failure(1010);
        });
        Assert.Empty((await client.DiscoverAsync(default)).Devices);
        Assert.Equal(1, tokenCalls);
        Assert.Equal(Now.AddMinutes(2), session.Token!.Expires);
        var rejected = await Assert.ThrowsAsync<TuyaFailure>(() => client.DiscoverAsync(default));
        Assert.Equal("accessDenied", rejected.Status);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(3, reads);
        Assert.Null(session.Token);
    }

    [Fact]
    public async Task SpecificationUsesTheOfficialReadEndpoint()
    {
        using var session = new TuyaSession { Token = new("fixture-token", Now.AddMinutes(5)) };
        using var client = Client(new FixtureClock(), session, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1.0/iot-03/devices/fixture-device/specification", request.RequestUri!.AbsolutePath);
            Assert.Equal("fixture-token", Header(request, "access_token"));
            return Success(new { status = new[] { Point("switch_1", "Boolean", "{}") } });
        });
        Assert.Equal("switch_1", Assert.Single(await client.SpecificationsAsync("fixture-device", default)).Code);
    }

    [Fact]
    public void ScalesOnlyDeclaredReadingsAndKeepsSwitchChannelsSeparate()
    {
        var points = Specs(
            Point("switch_1", "Boolean", "{}"), Point("switch_2", "Boolean", "{}"),
            Point("temp_current_f", "Integer", """{"unit":"℉","scale":1,"min":-400,"max":1200}"""),
            Point("humidity_value", "Integer", """{"unit":"%","scale":1}"""),
            Point("battery_percentage", "Integer", """{"unit":"%","scale":0}"""),
            Point("cur_current", "Integer", """{"unit":"mA","scale":0}"""),
            Point("cur_voltage", "Integer", """{"unit":"V","scale":1}"""),
            Point("cur_power", "Integer", """{"unit":"W","scale":2}"""),
            Point("va_temperature", "Integer", """{"unit":"","scale":1}"""),
            Point("va_humidity", "Integer", """{"unit":"%"}"""),
            Point("setpoint", "Integer", """{"unit":"℃","scale":0}"""));
        var report = Seed(true, Now.AddMinutes(-1), ("switch_1", false), ("switch_2", true),
            ("temp_current_f", 680), ("humidity_value", 523), ("battery_percentage", 86),
            ("cur_current", 250), ("cur_voltage", 2294), ("cur_power", 1120),
            ("va_temperature", 230), ("va_humidity", 51), ("unadvertised", 123));
        var snapshot = TuyaMapping.Snapshot(report, null, points, Now, true);
        var readings = snapshot.Readings.ToDictionary(reading => reading.Code);
        Assert.Equal(8, readings.Count);
        Assert.False(readings["switch_1"].Flag);
        Assert.True(readings["switch_2"].Flag);
        Assert.Equal(20d, readings["temp_current_f"].Number);
        Assert.Equal(52.3, readings["humidity_value"].Number);
        Assert.Equal(86, readings["battery_percentage"].Number);
        Assert.Equal(.25, readings["cur_current"].Number);
        Assert.Equal(229.4, readings["cur_voltage"].Number);
        Assert.Equal(11.2, readings["cur_power"].Number);
        var mapped = snapshot.Telemetry();
        Assert.All(mapped.Inventory.Endpoints, endpoint =>
            Assert.All(endpoint.Capabilities, capability => Assert.Equal(Access.ReadOnly, endpoint.AccessFor(capability))));
        var history = Assert.Single(mapped.Batch.Devices).Endpoints.SelectMany(endpoint => endpoint.Samples).ToArray();
        Assert.Equal(6, history.Length);
        Assert.All(history, value => Assert.Equal(report.ReportedAt, value.ObservedAt));
    }

    [Fact]
    public async Task HistoryUsesTheReportedPropertiesReadEndpoint()
    {
        using var session = new TuyaSession { Token = new("fixture-token", Now.AddMinutes(5)) };
        using var client = Client(new FixtureClock(), session, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v2.0/cloud/thing/fixture-device/shadow/properties", request.RequestUri!.AbsolutePath);
            return Success(new { properties = Array.Empty<object>() });
        });
        Assert.Equal(0, (await client.ReportedPropertiesAsync("fixture-device", default)).GetArrayLength());
    }

    [Fact]
    public void HistoryRequiresMatchingDatapointsAndTheirOwnValidTimestamps()
    {
        var points = Specs(Point("temp_current", "Integer", """{"unit":"℃","scale":1}"""),
            Point("humidity_value", "Integer", """{"unit":"%","scale":0}"""),
            Point("va_humidity", "Integer", """{"unit":"%","scale":0}"""),
            Point("humidity", "Integer", """{"unit":"%","scale":0}"""));
        var reportAt = Now.AddMinutes(-10);
        var reported = JsonSerializer.SerializeToElement(new object[]
        {
            new { code = "temp_current", time = reportAt.ToUnixTimeMilliseconds(), value = 215 },
            new { code = "humidity_value", value = 54 },
            new { code = "va_humidity", time = Now.AddDays(-31).ToUnixTimeMilliseconds(), value = 42 },
            new { code = "humidity", time = Now.AddSeconds(1).ToUnixTimeMilliseconds(), value = 70 },
        });
        var reports = TuyaMapping.SensorReports(reported, points, Now);
        var sample = Assert.Single(reports).Value;
        Assert.Equal(reportAt, sample.ObservedAt);
        Assert.Equal(21.5, sample.Number);

        var inventory = Seed(true, Now, ("temp_current", 215), ("humidity_value", 54));
        var device = TuyaMapping.Snapshot(inventory, null, points, Now, true);
        Assert.Empty(Assert.Single(device.Telemetry().Batch.Devices).Endpoints.SelectMany(x => x.Samples));
        var persisted = Assert.Single(Assert.Single(device.Telemetry(sensorReports: reports).Batch.Devices).Endpoints.SelectMany(x => x.Samples));
        Assert.Equal(sample, persisted);
        Assert.Equal(reportAt, TuyaMapping.SensorReports(reported, points, Now.AddMinutes(30))["temp_current"].ObservedAt);
    }

    [Fact]
    public void KeepsLastKnownValuesAcrossOfflineMissingMalformedAndOutOfOrderReports()
    {
        var points = Specs(Point("switch_1", "Boolean", "{}"), Point("battery_percentage", "Integer", """{"unit":"%","scale":0}"""));
        var prior = TuyaMapping.Snapshot(Seed(true, Now.AddMinutes(-5), ("switch_1", true), ("battery_percentage", 62)),
            null, points, Now, true);
        TuyaSeed[] invalid = [
            Seed(false, Now, ("switch_1", false)),
            Seed(true, Now, ("switch_1", "off"), ("battery_percentage", 125)),
            Seed(true, Now.AddMinutes(-7), ("switch_1", false)),
            Seed(true, Now, ("switch_1", false), ("switch_1", true)),
        ];
        foreach (var report in invalid)
        {
            var snapshot = TuyaMapping.Snapshot(report, prior, points, Now, true);
            Assert.False(snapshot.Readings[0].InLatestReport);
            Assert.True(snapshot.Readings[0].Flag);
            Assert.All(Assert.Single(snapshot.Telemetry().Batch.Devices).Endpoints, endpoint => Assert.Empty(endpoint.Samples));
        }
        var undated = TuyaMapping.Snapshot(Seed(true, null, ("switch_1", true)), null, points, Now, true);
        Assert.Null(Assert.Single(undated.Readings).At);
        Assert.All(Assert.Single(undated.Telemetry().Batch.Devices).Endpoints, endpoint => Assert.Empty(endpoint.Samples));
    }

    [Theory]
    [InlineData(28841002, "subscriptionExpired")]
    [InlineData(28841004, "quotaExceeded")]
    [InlineData(1199, "rateLimited")]
    [InlineData(1106, "accessDenied")]
    public async Task ExposesOnlySafeSubscriptionAndQuotaDiagnostics(int code, string expected)
    {
        using var session = new TuyaSession { Token = new("fixture-token", Now.AddMinutes(5)) };
        var calls = 0;
        using var client = Client(new FixtureClock(), session, _ => { calls++; return Failure(code); });
        var failure = await Assert.ThrowsAsync<TuyaFailure>(() => client.DiscoverAsync(default));
        Assert.Equal(expected, failure.Status);
        Assert.Equal(code, failure.ProviderCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreservesTheProviderRetryAfterOnHttpThrottle()
    {
        using var session = new TuyaSession { Token = new("fixture-token", Now.AddMinutes(5)) };
        using var client = Client(new FixtureClock(), session, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(40));
            return response;
        });
        var failure = await Assert.ThrowsAsync<TuyaFailure>(() => client.DiscoverAsync(default));
        Assert.Equal("rateLimited", failure.Status);
        Assert.Equal(Now.AddMinutes(40), failure.RetryAt);
    }

    private static TuyaSeed Seed(bool online, DateTimeOffset? report, params (string Code, object Value)[] readings) =>
        TuyaMapping.Device(JsonSerializer.SerializeToElement(new
        {
            id = "fixture-device", online, update_time = report?.ToUnixTimeSeconds(),
            status = readings.Select(reading => new { code = reading.Code, value = reading.Value })
        }), Now)!;
    private sealed record Declaration(string code, string type, string values);
    private static Declaration Point(string code, string type, string values) => new(code, type, values);
    private static TuyaPoint[] Specs(params Declaration[] statuses) =>
        TuyaMapping.Specifications(JsonSerializer.SerializeToElement(new { status = statuses }));
    private static HttpResponseMessage Token(string token) => Success(new { access_token = token, expire_time = 120 });
    private static HttpResponseMessage Success(object result) => Json(new { success = true, result });
    private static HttpResponseMessage Failure(int code) => Json(new { success = false, code, msg = "internal upstream details" });
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private static string Header(HttpRequestMessage request, string name) => Assert.Single(request.Headers.GetValues(name));
    private static TuyaClient Client(TimeProvider clock, TuyaSession session, Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new HttpClient(new Handler(send)), Options, clock, session);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
    private sealed class FixtureClock : TimeProvider
    {
        private long tick;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref tick, TimeSpan.TicksPerSecond);
    }
}
