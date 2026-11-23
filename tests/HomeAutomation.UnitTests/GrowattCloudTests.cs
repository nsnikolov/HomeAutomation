using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Growatt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.UnitTests;

public sealed class GrowattCloudTests
{
    // This marker has no access to a provider. It checks header placement with an in-memory handler.
    private const string HeaderMarker = "mock-header";
    private const string DevicePage = """
        {"code":0,"data":{"pages":1,"count":2,"data":[{"deviceType":"storage","deviceSn":"fixture-M"},{"deviceType":"storage","deviceSn":"fixture-S"}]}}
        """;

    [Fact]
    public void CloudConfigurationRequiresTheProviderCadenceAndIdentifiedHardware()
    {
        var options = CloudOptions();
        Assert.True(options.HasCloudConfiguration());

        options.PollSeconds = 299;
        Assert.False(options.HasCloudConfiguration());
        options.PollSeconds = 300;

        options.TimeZone = "";
        Assert.False(options.HasCloudConfiguration());
        options.TimeZone = "Europe/Sofia";

        options.SlaveId = "two,devices";
        Assert.False(options.HasCloudConfiguration());
    }

    [Fact]
    public async Task DiscoveryUsesTheDocumentedV1AndV4ReadRequests()
    {
        var responses = new Queue<string>([
            """{"error_code":0,"data":{"count":1,"plants":[{"plant_id":"42"}]}}""",
            """{"code":0,"data":{"pages":1,"count":3,"data":[{"deviceType":"storage","deviceSn":"fixture-M"},{"deviceType":"storage","deviceSn":"fixture-S"},{"deviceType":"min","deviceSn":"other"}]}}"""
        ]);
        using var handler = new CloudHandler(async (request, ct) =>
        {
            CheckHeader(request);
            if (responses.Count == 2)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/v1/plant/list?page=1&perpage=20", request.RequestUri!.PathAndQuery);
                Assert.Null(request.Content);
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v4/new-api/queryDeviceList", request.RequestUri!.PathAndQuery);
                Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal("page=1", await request.Content.ReadAsStringAsync(ct));
            }
            return Json(responses.Dequeue());
        });
        using var http = CreateHttp(handler);
        var client = new GrowattCloudClient(http, new GrowattOptions { ApiToken = HeaderMarker }, TimeProvider.System);

        Assert.Equal(1, await client.CountPlantsAsync(default));
        Assert.Equal(2, await client.CountStorageDevicesAsync(default));
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData(200, """{"code":2}""", GrowattFailure.Authorization)]
    [InlineData(200, """{"code":101}""", GrowattFailure.Authorization)]
    [InlineData(403, "", GrowattFailure.Authorization)]
    [InlineData(200, """{"code":102}""", GrowattFailure.Throttled)]
    [InlineData(429, "", GrowattFailure.Throttled)]
    [InlineData(503, "", GrowattFailure.Unavailable)]
    [InlineData(200, """{"message":"not a valid envelope","data":{}}""", GrowattFailure.InvalidData)]
    public async Task DiscoveryClassifiesFailuresWithoutReturningProviderPayload(
        int status, string body, GrowattFailure expected)
    {
        using var handler = new CloudHandler((_, _) =>
        {
            var response = Json(body, (HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        });
        using var http = CreateHttp(handler);
        var client = new GrowattCloudClient(http, new GrowattOptions { ApiToken = HeaderMarker }, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<GrowattReadException>(() => client.CountStorageDevicesAsync(default));
        Assert.Equal(expected, failure.Failure);
        Assert.Equal(expected.ToString(), failure.Message);
        if (expected is GrowattFailure.Throttled or GrowattFailure.Unavailable)
            Assert.Equal(TimeSpan.FromMinutes(10), failure.RetryAfter);
    }

    [Fact]
    public async Task VerifiedPairSharesOneBatchAndUsesOnlyNominatedBatteryReadings()
    {
        var calls = 0;
        using var handler = new CloudHandler(async (request, ct) =>
        {
            CheckHeader(request);
            if (++calls == 1)
                return Json(DevicePage);

            Assert.Equal("/v4/new-api/queryLastData", request.RequestUri!.PathAndQuery);
            Assert.Equal("deviceType=storage&deviceSn=fixture-M%2Cfixture-S", await request.Content!.ReadAsStringAsync(ct));
            return Json("""
                {"code":0,"data":{"storage":[
                  {"serialNum":"fixture-S","time":"2026-11-21 13:56:00","ppv":900,"vpv1":111.8,"vGrid":229,"capacity":90,"vBat":51.3,
                   "outPutPower":"187.5","pAcCharge":0,"pAcInPut":"126.4","pBat":80.5,"lost":true,"errorCode":3},
                  {"serialNum":"fixture-M","time":"2026-11-21 13:57:00","ppv":"840","vpv":102,"vpv1":"109.4","vGrid":230,"freqGrid":50,
                   "outPutPower":303,"outPutVolt":241,"loadPercent":20.3,"pAcCharge":46,"pAcInPut":118.8,"pBat":"-51.25",
                   "capacity":49,"vBat":52.7,"warnCode":4,"faultCode":6,"lost":false,
                   "spf5000StatusText":"Battery Discharging"}
                ]}}
                """);
        });
        using var http = CreateHttp(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = CloudOptions();
        var clock = new FixedClock();
        var source = new GrowattCloudSource(new GrowattCloudClient(http, options, clock), options, cache, clock);

        var snapshot = await source.ReadAsync(default);
        await source.ReadAsync(default);

        Assert.Equal(3, calls); // A second poll reuses discovery.
        Assert.Empty(snapshot.InstallationReadings);
        var master = Assert.Single(snapshot.Inverters, inverter => inverter.ExternalId == options.MasterId);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 11, 57, 0, TimeSpan.Zero), master.ObservedAt);
        Assert.Equal(Connectivity.Online, master.Connectivity);
        Assert.Equal(49, Assert.Single(master.SharedBatteryReadings, reading => reading.Metric == Metric.BatterySoc).Number);
        Assert.Equal(840, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.PvPower).Number);
        Assert.Equal(102, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.PvVoltage).Number);
        Assert.Equal(109.4, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.Pv1Voltage).Number);
        Assert.Equal(303, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.ActivePower).Number);
        Assert.Equal(241, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.AcOutputVoltage).Number);
        Assert.Equal(20.3, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.InverterLoadPercent).Number);
        Assert.Equal(46, Assert.Single(master.InverterReadings, reading => reading.Metric == Metric.AcChargingPower).Number);
        Assert.DoesNotContain(master.SharedBatteryReadings, reading => reading.Metric == Metric.BatteryPower);
        Assert.Equal("6", master.FaultCode);
        Assert.DoesNotContain(master.InverterReadings, reading => reading.Metric == Metric.GridAvailable);
        var slave = Assert.Single(snapshot.Inverters, inverter => inverter.ExternalId == options.SlaveId);
        Assert.Equal(111.8, Assert.Single(slave.InverterReadings, reading => reading.Metric == Metric.Pv1Voltage).Number);
        Assert.Equal("V", Telemetry.Unit(Metric.Pv1Voltage));
        var (_, batch) = new GrowattMapper(options, clock).Map(snapshot);
        Assert.Empty(batch.Installation);
        VerifyInverterPower(Assert.Single(batch.Devices, device => device.ExternalId == options.SlaveId), 187.5, 0, 126.4, -80.5);
        VerifyInverterPower(Assert.Single(batch.Devices, device => device.ExternalId == options.MasterId), 303, 46, 118.8, 51.25);
        var shared = Assert.Single(batch.Devices, device => device.ExternalId == GrowattMapper.SharedBatteryId);
        Assert.DoesNotContain(Assert.Single(shared.Endpoints).Samples, sample => sample.Metric == Metric.BatteryPower);
        Assert.Equal(Connectivity.Offline, slave.Connectivity);
        Assert.Equal("3", slave.FaultCode);
        Assert.Empty(slave.SharedBatteryReadings);

        static void VerifyInverterPower(DeviceObservation device, double output, double charging, double input, double battery)
        {
            var samples = Assert.Single(device.Endpoints).Samples;
            Assert.Equal(output, Assert.Single(samples, sample => sample.Metric == Metric.ActivePower).Number);
            Assert.Equal(charging, Assert.Single(samples, sample => sample.Metric == Metric.AcChargingPower).Number);
            Assert.Equal(input, Assert.Single(samples, sample => sample.Metric == Metric.AcInputPower).Number);
            Assert.Equal(battery, Assert.Single(samples, sample => sample.Metric == Metric.BatteryPower).Number);
            Assert.DoesNotContain(samples, sample => sample.Metric is Metric.GridImportPower or Metric.HouseholdPower);
        }
    }

    [Fact]
    public async Task NighttimeZerosAreReadingsAndDiagnosticsOmitIdentifiersAndValues()
    {
        using var handler = new CloudHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("queryDeviceList", StringComparison.Ordinal) ? Json(DevicePage) : Json("""
                {"code":0,"data":{"storage":[
                  {"serialNum":"fixture-M","time":"2026-11-21 13:57:00","vpv":0,"ppv":0,"pAcInPut":0,"pBat":0,"ipv":0},
                  {"serialNum":"fixture-S","time":"2026-11-21 13:57:00","vpv":127.65,"pAcInPut":-1,"pBat":"--","pCharge":50,"pDischarge":0}
                ]}}
                """)));
        using var http = CreateHttp(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = CloudOptions();
        var clock = new FixedClock();
        var logger = new CaptureLogger();
        var source = new GrowattCloudSource(new GrowattCloudClient(http, options, clock), options, cache, clock, logger);

        await source.ReadAsync(default);
        Assert.Empty(logger.Messages);
        options.TelemetryDiagnostics = true;
        var snapshot = await source.ReadAsync(default);
        var master = Assert.Single(snapshot.Inverters, device => device.ExternalId == options.MasterId);
        Assert.Equal(4, master.InverterReadings.Count);
        Assert.All(master.InverterReadings, item => Assert.Equal(0d, item.Number));
        var slave = Assert.Single(snapshot.Inverters, device => device.ExternalId == options.SlaveId);
        Assert.Equal(Metric.PvVoltage, Assert.Single(slave.InverterReadings).Metric);
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("pAcInPut=Number/True", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("pBat=String/False", logger.Messages[1], StringComparison.Ordinal);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("fixture-", message, StringComparison.Ordinal);
            Assert.DoesNotContain(HeaderMarker, message, StringComparison.Ordinal);
            Assert.DoesNotContain("127.65", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ADeviceOutsideAuthorizedInventoryPreventsLatestRequest()
    {
        using var handler = new CloudHandler((request, _) =>
        {
            Assert.Equal("/v4/new-api/queryDeviceList", request.RequestUri!.PathAndQuery);
            return Task.FromResult(Json(DevicePage));
        });
        using var http = CreateHttp(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = CloudOptions();
        options.SlaveId = "unverified";
        var clock = new FixedClock();
        var source = new GrowattCloudSource(new GrowattCloudClient(http, options, clock), options, cache, clock);

        var failure = await Assert.ThrowsAsync<GrowattReadException>(() => source.ReadAsync(default));
        Assert.Equal(GrowattFailure.Authorization, failure.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"vpv1\":null")]
    [InlineData(",\"vpv1\":\"--\"")]
    public async Task MissingValuesAndAnAmbiguousSourceTimestampDoNotCreateObservations(string pv1Field)
    {
        using var handler = new CloudHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("queryDeviceList", StringComparison.Ordinal) ? Json(DevicePage) : Json($$"""
                {"code":0,"data":{"storage":[
                  {"serialNum":"fixture-S","time":"2026-10-25 03:15:00","ppv":900},
                  {"serialNum":"fixture-M","time":"2026-11-21 13:57:00","vpv":105,"capacity":0,"vBat":0{{pv1Field}}}
                ]} }
                """)));
        using var http = CreateHttp(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = CloudOptions();
        var clock = new FixedClock();
        var source = new GrowattCloudSource(new GrowattCloudClient(http, options, clock), options, cache, clock);

        var report = Assert.Single((await source.ReadAsync(default)).Inverters);
        Assert.Equal(options.MasterId, report.ExternalId);
        Assert.Equal(Connectivity.Unknown, report.Connectivity);
        Assert.Equal(Metric.PvVoltage, Assert.Single(report.InverterReadings).Metric);
        Assert.Empty(report.SharedBatteryReadings);
    }

    private static GrowattOptions CloudOptions() => new()
    {
        ApiToken = HeaderMarker, SiteKey = "test-site", MasterId = "fixture-M", SlaveId = "fixture-S",
        BatterySourceId = "fixture-M", TimeZone = "Europe/Sofia"
    };

    private static void CheckHeader(HttpRequestMessage request)
    {
        Assert.Equal(HeaderMarker, Assert.Single(request.Headers.GetValues("token")));
        Assert.DoesNotContain(HeaderMarker, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    private static HttpClient CreateHttp(CloudHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.invalid/") };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CloudHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class CaptureLogger : ILogger<GrowattCloudSource>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    }
}
