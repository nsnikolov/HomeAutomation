using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeAutomation.IntegrationTests;

public sealed class ApiTests : IClassFixture<ApiTests.Host>
{
    private readonly Host host;
    public ApiTests(Host host) => this.host = host;

    [Fact]
    public async Task PublicLivenessAndAnonymousPropertyRequestsReturnExpectedStatuses()
    {
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/properties")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hubs/monitoring/negotiate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/session/login", new { email = "viewer@example.test", password = "invalid" })).StatusCode);
    }

    [Fact]
    public async Task MembersCanReadOnlyTheirPropertyAndReadOnlyCapabilities()
    {
        using var client = AuthenticatedClient();
        var properties = await client.GetStringAsync("/api/v1/properties");
        Assert.Contains("Fixture", properties, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/properties/99/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/properties/99/growatt")).StatusCode);
        Assert.Contains("\"status\":\"notConfigured\"", await client.GetStringAsync("/api/v1/properties/1/growatt"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/properties/1/rooms")).StatusCode);
        Assert.Contains("readOnly", await client.GetStringAsync("/api/v1/properties/1/devices/7/capabilities"), StringComparison.Ordinal);
        Assert.Contains("Heater meter", await client.GetStringAsync("/api/v1/properties/1/state"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/properties/1/devices/7/commands", null)).StatusCode);
    }

    [Theory]
    [InlineData("metric=householdPower", HttpStatusCode.OK)]
    [InlineData("metric=99", HttpStatusCode.BadRequest)]
    [InlineData("metric=pvPower&from=2026-11-19T00:00:00Z&to=2026-11-21T00:00:00Z", HttpStatusCode.BadRequest)]
    public async Task HistoryValidatesMetricAndRange(string query, HttpStatusCode expected)
    {
        using var client = AuthenticatedClient();
        Assert.Equal(expected, (await client.GetAsync("/api/v1/properties/1/telemetry?" + query)).StatusCode);
    }

    private HttpClient AuthenticatedClient()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
        return client;
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Database:Host", "127.0.0.1");
            builder.UseSetting("Database:Port", "3306");
            builder.UseSetting("Database:Name", "homeautomation_test");
            builder.UseSetting("Database:User", "test");
            builder.UseSetting("Database:Password", "test-configuration-only");
            builder.UseSetting("Simulator:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMonitoringReader>();
                services.AddSingleton<IMonitoringReader, FixtureReader>();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "fixture";
                    options.DefaultChallengeScheme = "fixture";
                }).AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            });
        }
    }

    public sealed class FixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class FixtureReader : IMonitoringReader
    {
        private static readonly DeviceDetails Device = new(7, 2, "Heater meter", DeviceKind.SmartSwitch, "Simulator",
            "Meter", Connectivity.Online, null, null, ReadingQuality.Missing,
            [new EndpointState(1, "Main", [new CapabilityState(Capability.Switch, Access.ReadOnly)], [])]);

        public Task<IReadOnlyList<PropertySummary>> PropertiesAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PropertySummary>>([new PropertySummary(1, "Fixture", "Europe/Sofia")]);
        public Task<bool> CanReadAsync(string userId, long propertyId, CancellationToken cancellationToken) =>
            Task.FromResult(userId == "viewer" && propertyId == 1);
        public Task<IReadOnlyList<RoomSummary>> RoomsAsync(long propertyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RoomSummary>>([new RoomSummary(2, "Utility")]);
        public Task<PropertyState> StateAsync(long propertyId, CancellationToken cancellationToken) =>
            Task.FromResult(new PropertyState([Device], [], []));
        public Task<DeviceDetails?> DeviceAsync(long propertyId, long deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(deviceId == Device.Id ? Device : null);
        public Task<IReadOnlyList<HistoryPoint>> HistoryAsync(long propertyId, long? endpointId, Metric metric,
            DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistoryPoint>>([]);
    }
}
