using System.Net;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Domain;

namespace HomeAutomation.IntegrationTests;

public sealed class HistoryApiTests(ApiTests.Host host) : IClassFixture<ApiTests.Host>
{
    private const string Route = "/api/v1/properties/1/history";

    [Fact]
    public async Task HistoryRequiresAuthenticationAndPropertyMembership()
    {
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route + "?seriesId=5")).StatusCode);
        using var viewer = Viewer();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/history?seriesId=5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/history/series")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Route + "?seriesId=5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Route + "/series?metric=ActivePower")).StatusCode);
    }

    [Fact]
    public async Task HourlyHistoryReturnsUtcTimestampsUnitsCoverageAndEnergyProvenance()
    {
        using var viewer = Viewer();
        using var response = await viewer.GetAsync(Route + "?seriesId=5&resolution=hour&from=2026-11-25T10:00:00Z&to=2026-11-25T12:00:00Z");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("W", root.GetProperty("series").GetProperty("unit").GetString());
        Assert.Equal("activePower", root.GetProperty("series").GetProperty("metric").GetString());
        Assert.Equal("timeWeighted", root.GetProperty("series").GetProperty("averageKind").GetString());
        Assert.Equal("hour", root.GetProperty("resolution").GetString());
        Assert.Equal(0.8, root.GetProperty("incompleteBelowCoverage").GetDouble());
        var buckets = root.GetProperty("buckets").EnumerateArray().ToArray();
        Assert.Equal("2026-11-25T10:00:00+00:00", buckets[0].GetProperty("start").GetString());
        Assert.Equal("complete", buckets[0].GetProperty("quality").GetString());
        Assert.Equal("estimated", buckets[0].GetProperty("energy").GetProperty("status").GetString());
        Assert.Equal("powerIntegration", buckets[0].GetProperty("energy").GetProperty("basis").GetString());
        Assert.Equal("noData", buckets[1].GetProperty("quality").GetString());
        Assert.Equal(0, buckets[1].GetProperty("coverage").GetDouble());
        var received = Assert.Single(HistoryFixture.Queries, query => query.Property == 1 && query.Query.Resolution == HistoryResolution.Hour &&
            query.Query.From == new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(5, received.Query.Selector.SeriesId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?seriesId=5&deviceId=7&metric=ActivePower")]
    [InlineData("?deviceId=7")]
    [InlineData("?seriesId=0")]
    [InlineData("?endpointId=3&metric=NotAMetric")]
    [InlineData("?seriesId=5&resolution=minute")]
    [InlineData("?seriesId=5&from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z")]
    [InlineData("?seriesId=5&from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("?seriesId=5&resolution=day")]
    [InlineData("?seriesId=5&resolution=day&fromDate=2025-01-01&toDate=2026-06-01")]
    [InlineData("?seriesId=5&resolution=day&fromDate=2026-01-01&toDate=2026-01-02&from=2026-01-01T00:00:00Z")]
    [InlineData("?seriesId=5&resolution=hour&fromDate=2026-01-01&toDate=2026-01-02")]
    [InlineData("?seriesId=5&resolution=raw&limit=5001")]
    [InlineData("?seriesId=5&resolution=raw&limit=0")]
    [InlineData("?seriesId=5&resolution=hour&limit=10")]
    [InlineData("?seriesId=5&resolution=raw&from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&after=2026-02-01T00:00:00Z")]
    public async Task InvalidHistoryQueriesAreRejected(string query)
    {
        using var viewer = Viewer();
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(Route + query)).StatusCode);
    }

    [Fact]
    public async Task ReadOutcomesMapToDistinctResponses()
    {
        using var viewer = Viewer();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Route + "?seriesId=404")).StatusCode);
        using var ambiguous = await viewer.GetAsync(Route + "?deviceId=7&metric=ActivePower");
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        Assert.Contains("\"candidates\":[11,12]", await ambiguous.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(Route + "?seriesId=406")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await viewer.GetAsync(Route + "?seriesId=409&resolution=day&fromDate=2026-01-01&toDate=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await viewer.GetAsync(Route + "?seriesId=503")).StatusCode);
    }

    [Fact]
    public async Task RawHistoryIsBoundedAndPagedWithProvenance()
    {
        using var viewer = Viewer();
        using var response = await viewer.GetAsync(Route + "?seriesId=5&resolution=raw&from=2026-11-25T10:00:00Z&to=2026-11-25T12:00:00Z&limit=2");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.RootElement.GetProperty("points").GetArrayLength());
        Assert.Equal("serverReceived", json.RootElement.GetProperty("points")[1].GetProperty("timestampSource").GetString());
        Assert.Equal("2026-11-25T10:05:00+00:00", json.RootElement.GetProperty("nextAfter").GetString());
        Assert.Contains(HistoryFixture.Queries, query => query.Query.Resolution == HistoryResolution.Raw && query.Query.Limit == 2);
    }

    private HttpClient Viewer()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
        return client;
    }

    public sealed class HistoryFixture : ITelemetryHistory
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<(long Property, HistoryQuery Query)> Queries = [];
        private static readonly HistorySeriesInfo Series = new(5, Metric.ActivePower, "W", "ewelink", 7, "Heater", 3, "Device", null, null,
            MetricAggregation.Gauge, "timeWeighted", "estimatedFromPower", true, 120, null, null);

        public Task<IReadOnlyList<HistorySeriesInfo>> SeriesAsync(long propertyId, long? deviceId, Metric? metric, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HistorySeriesInfo>>([Series]);

        public Task<HistoryRead> ReadAsync(long propertyId, HistoryQuery query, CancellationToken ct)
        {
            Queries.Add((propertyId, query));
            return Task.FromResult(query.Selector switch
            {
                { SeriesId: 404 } => HistoryRead.Of(HistoryReadStatus.NotFound),
                { SeriesId: 406 } => HistoryRead.Of(HistoryReadStatus.Unsupported),
                { SeriesId: 409 } => HistoryRead.Of(HistoryReadStatus.InvalidTimeZone),
                { SeriesId: 503 } => HistoryRead.Of(HistoryReadStatus.SchemaPending),
                { DeviceId: 7 } => HistoryRead.Of(HistoryReadStatus.Ambiguous, [11, 12]),
                _ when query.Resolution == HistoryResolution.Raw => new(HistoryReadStatus.Found, new(Series, [], query.Resolution,
                    query.From, query.To, "Europe/Sofia", 0.8,
                    [
                        new(At(0), At(0), 950, null, null, "provider"),
                        new(At(5), At(5), 1000, null, null, "serverReceived")
                    ], null, At(5))),
                _ => new(HistoryReadStatus.Found, new(Series, [], query.Resolution, query.From, query.To, "Europe/Sofia", 0.8, null,
                [
                    new(At(0), At(60), null, 3600, "complete", 1, 3600, 12, 900, 1100, 1000, 1000, At(0), At(55), 1000, null, null,
                        new("estimated", "powerIntegration", null, 1000, 0), false, []),
                    new(At(60), At(120), null, 3600, "noData", 0, 0, 0, null, null, null, null, null, null, null, null, null,
                        new("noData", "powerIntegration", null, null, null), false, [])
                ], null))
            });
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    }
}
