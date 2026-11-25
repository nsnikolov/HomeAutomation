using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.IntegrationTests;

public sealed class LogicalDeviceApiTests(ApiTests.Host host) : IClassFixture<ApiTests.Host>
{
    private const string Route = "/api/v1/properties/1/logical-devices";

    [Fact]
    public async Task ReadsRequireAuthenticationAndMembership()
    {
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        using var viewer = await ClientAsync("viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/logical-devices")).StatusCode);
        using var list = await viewer.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("canManage").GetBoolean());
        Assert.Equal("Heat pump meter", json.RootElement.GetProperty("devices")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Route + "/99")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Route + "/4/candidates")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(Route + "/4/candidates?channel=Bad Channel")).StatusCode);
    }

    [Fact]
    public async Task MutationsRequireAntiforgeryAndTheStoreDecidesTheRole()
    {
        using var noToken = host.CreateClient();
        noToken.DefaultRequestHeaders.Add("X-Test-User", "logical-owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await noToken.PostAsJsonAsync(Route + "/4/replace", Replace())).StatusCode);

        using var viewer = await ClientAsync("viewer", csrf: true);
        using var forbidden = await viewer.PostAsJsonAsync(Route + "/4/replace", Replace());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await forbidden.Content.ReadAsStringAsync());

        using var owner = await ClientAsync("logical-owner", csrf: true);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(Route + "/4/replace", Replace())).StatusCode);
        Assert.Contains(LogicalFixture.Calls, call => call == "replace:1:logical-owner:4");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/properties/2/logical-devices/4/replace", Replace())).StatusCode);
    }

    [Theory]
    [InlineData(10, HttpStatusCode.Conflict, "conflict")]
    [InlineData(11, HttpStatusCode.BadRequest, "missingCapabilities")]
    [InlineData(12, HttpStatusCode.BadRequest, "incompatible")]
    [InlineData(13, HttpStatusCode.BadRequest, "ambiguousEndpoint")]
    [InlineData(14, HttpStatusCode.NotFound, "notFound")]
    [InlineData(15, HttpStatusCode.BadRequest, "invalid")]
    public async Task ReplacementOutcomesMapToDistinctResponses(long device, HttpStatusCode status, string code)
    {
        using var owner = await ClientAsync("logical-owner", csrf: true);
        using var response = await owner.PostAsJsonAsync(Route + "/4/replace", Replace() with { DeviceId = device });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", body);
        if (code == "missingCapabilities") Assert.Contains("\"missing\":[\"humidityMeasurement\"]", body);
    }

    [Fact]
    public async Task LogicalHistoryValidatesItsQueryAndReturnsSegments()
    {
        using var viewer = await ClientAsync("viewer");
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(Route + "/4/history")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(Route + "/4/history?metric=Temperature&resolution=day")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await viewer.GetAsync(Route + "/4/history?metric=Temperature&from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z")).StatusCode);
        using var response = await viewer.GetAsync(Route + "/4/history?metric=Temperature&from=2026-11-26T14:00:00Z&to=2026-11-26T15:00:00Z");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var segments = json.RootElement.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal("2026-11-26T14:37:00+00:00", segments[0].GetProperty("to").GetString());
        Assert.Equal("2026-11-26T14:37:00+00:00", segments[1].GetProperty("from").GetString());
        Assert.Contains("deviceReplaced", json.RootElement.GetProperty("buckets")[0].GetProperty("flags").EnumerateArray().Select(flag => flag.GetString()));
    }

    [Fact]
    public async Task DashboardLogicalBindingConversionIsExplicitAndRevisionGuarded()
    {
        const string path = "/api/v1/properties/1/dashboard/logical-bindings";
        using var viewer = await ClientAsync("viewer", csrf: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(path)).StatusCode);
        using var editor = await ClientAsync("dashboard-editor", csrf: true);
        var review = await editor.GetFromJsonAsync<JsonElement>(path);
        var proposal = Assert.Single(review.GetProperty("proposals").EnumerateArray());
        Assert.Equal("logical:4:main:ActivePower", proposal.GetProperty("logicalKey").GetString());
        Assert.Equal("activePower", proposal.GetProperty("metric").GetString());
        var revision = review.GetProperty("revision").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync(path, new LogicalBindingUpdate(revision, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await editor.PostAsJsonAsync(path, new LogicalBindingUpdate(new string('1', 64), ["value-1"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync(path, new LogicalBindingUpdate(revision, ["value-2"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.PostAsJsonAsync(path, new LogicalBindingUpdate(revision, ["value-1"]))).StatusCode);
    }

    private static ReplacePhysicalDevice Replace() =>
        new("main", 21, 9, null, new DateTimeOffset(2026, 11, 26, 14, 37, 0, TimeSpan.Zero), "Sensor failed", false);

    private async Task<HttpClient> ClientAsync(string user, bool csrf = false)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        if (!csrf) return client;
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        return client;
    }

    public sealed class LogicalFixture : ILogicalDevices
    {
        public static readonly ConcurrentBag<string> Calls = [];
        private static readonly DateTimeOffset Cutover = new(2026, 11, 26, 14, 37, 0, TimeSpan.Zero);
        private static readonly LogicalAssignmentView Current = new(21, "main", 9, "Sonoff POW", "ewelink", "Sonoff", "POWR3", null, null,
            Cutover, null, "logical-owner", Cutover, "Meter failed", [Capability.PowerMeasurement]);
        private static readonly LogicalDeviceView Device = new(4, "Heat pump meter", "SmartSwitch", null, null, Cutover, Cutover, null,
            [Current], []);

        public Task<bool> SchemaReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<LogicalDeviceList> ListAsync(long property, string user, bool includeArchived, CancellationToken ct) =>
            Task.FromResult(new LogicalDeviceList(user == "logical-owner", [Device]));
        public Task<LogicalDeviceDetail?> GetAsync(long property, string user, long id, CancellationToken ct) =>
            Task.FromResult(id == 4 ? new LogicalDeviceDetail(user == "logical-owner", Device, []) : null);
        public Task<IReadOnlyList<ReplacementCandidate>?> CandidatesAsync(long property, long id, string channel, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReplacementCandidate>?>(id == 4 ? [] : null);
        public Task<LogicalChangeResult> CreateAsync(long property, string user, CreateLogicalDevice command, CancellationToken ct) =>
            Task.FromResult(Change(property, user, "create", 4, command.DeviceId));
        public Task<LogicalChangeResult> UpdateAsync(long property, string user, long id, UpdateLogicalDevice command, CancellationToken ct) =>
            Task.FromResult(Change(property, user, "update", id, 9));
        public Task<LogicalChangeResult> AssignAsync(long property, string user, long id, AssignPhysicalDevice command, CancellationToken ct) =>
            Task.FromResult(Change(property, user, "assign", id, command.DeviceId));
        public Task<LogicalChangeResult> ReplaceAsync(long property, string user, long id, ReplacePhysicalDevice command, CancellationToken ct) =>
            Task.FromResult(Change(property, user, "replace", id, command.DeviceId));
        public Task<LogicalChangeResult> CorrectAsync(long property, string user, long id, long assignment, CorrectAssignmentStart command,
            CancellationToken ct) => Task.FromResult(Change(property, user, "correct", id, 9));
        public Task<LogicalChangeResult> ArchiveAsync(long property, string user, long id, ArchiveLogicalDevice command, CancellationToken ct) =>
            Task.FromResult(Change(property, user, "archive", id, 9));

        public Task<LogicalHistoryRead> HistoryAsync(long property, long id, string channel, Metric metric, HistoryQuery query, CancellationToken ct)
        {
            var hour = new DateTimeOffset(2026, 11, 26, 14, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new LogicalHistoryRead(HistoryReadStatus.Found, new(id, "Living room", channel, metric, "°C",
                MetricAggregation.Gauge, query.Resolution, query.From, query.To, "Europe/Sofia", 0.8,
                [
                    new(20, 7, "Tuya A", null, hour.AddDays(-30), Cutover, "available", 31, "tuya-sensor-report", [30]),
                    new(21, 9, "eWeLink B", null, Cutover, null, "available", 40, "ewelink", [])
                ], null,
                [new(hour, hour.AddHours(1), null, 3600, "complete", 1, 3600, 12, 20, 22, 21, 21, hour, hour.AddMinutes(55), 22, null, null,
                    null, false, ["deviceReplaced"], [31, 40])], null)));
        }

        private static LogicalChangeResult Change(long property, string user, string action, long id, long device)
        {
            if (user != "logical-owner") return LogicalChangeResult.Fail(LogicalChangeStatus.Forbidden, "Only Owners and Admins can change logical devices.");
            Calls.Add($"{action}:{property}:{user}:{id}");
            return device switch
            {
                10 => LogicalChangeResult.Fail(LogicalChangeStatus.Conflict, "The active device changed."),
                11 => LogicalChangeResult.Fail(LogicalChangeStatus.MissingCapabilities, "Missing.", [Capability.HumidityMeasurement]),
                12 => LogicalChangeResult.Fail(LogicalChangeStatus.Incompatible, "Incompatible."),
                13 => LogicalChangeResult.Fail(LogicalChangeStatus.AmbiguousEndpoint, "Choose an endpoint."),
                14 => LogicalChangeResult.Fail(LogicalChangeStatus.NotFound, "Not found."),
                15 => LogicalChangeResult.Fail(LogicalChangeStatus.Invalid, "Invalid time."),
                _ => LogicalChangeResult.Done(id)
            };
        }
    }
}
