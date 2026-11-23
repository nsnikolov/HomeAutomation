using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using HomeAutomation.Application;
using HomeAutomation.Api;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task DeviceHistoryAndPreferencesRequirePropertyAuthorizationAndCsrf()
    {
        using var guest = host.CreateClient();
        using var member = AuthenticatedClient();
        const string device = "00000000000000000000000000000001";
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/properties/1/device-preferences/tuya")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync("/api/v1/properties/99/device-preferences/tuya")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync("/api/v1/properties/99/device-preferences/tuya/order",
            new { group = "assigned", ids = new[] { device } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PutAsJsonAsync($"/api/v1/properties/1/device-preferences/tuya/visibility/{device}",
            new { hidden = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/tuya/99/devices/{device}/sensor-history?period=30d")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync($"/api/v1/tuya/1/devices/{device}/sensor-history?period=365d")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/tuya/1/devices/{device}/sensor-history?period=7d")).StatusCode);
    }

    [Fact]
    public async Task TuyaIsPropertyScopedAndHasNoDeviceCommandRoute()
    {
        using var guest = host.CreateClient();
        using var viewer = AuthenticatedClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/tuya/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/tuya/99")).StatusCode);
        Assert.Contains("notConfigured", await viewer.GetStringAsync("/api/v1/tuya/1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/tuya/1/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync(
            "/api/v1/tuya/1/devices/00000000000000000000000000000001/commands", new { operation = "power" })).StatusCode);
    }

    [Fact]
    public async Task EwelinkRequiresAPropertySessionAndExposesNoDeviceCommandRoute()
    {
        using var guest = host.CreateClient();
        using var viewer = AuthenticatedClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/ewelink/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/ewelink/99")).StatusCode);
        Assert.Contains("notConfigured", await viewer.GetStringAsync("/api/v1/ewelink/1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/ewelink/1/connect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync(
            "/api/v1/ewelink/1/devices/00000000000000000000000000000001/commands", new { operation = "power" })).StatusCode);
    }

    [Fact]
    public void EwelinkConsentIsOneTimeAndBoundToTheSameBrowserAndUser()
    {
        var clock = new FlowClock();
        var consent = new EwelinkConsent(clock);
        var flow = consent.Begin("operator", 1);
        Assert.Null(consent.Take(flow.State, null));
        Assert.Null(consent.Take(null, flow.Cookie));
        Assert.Null(consent.Take("unrelated", flow.Cookie));
        Assert.Null(consent.Take(flow.State, "another-browser"));
        Assert.Equal(flow, consent.Take(flow.State, flow.Cookie));
        Assert.Null(consent.Take(flow.State, flow.Cookie));
        flow = consent.Begin("operator", 1);
        clock.Advance();
        Assert.Null(consent.Take(flow.State, flow.Cookie));
    }

    [Fact]
    public async Task EwelinkMeasurementsReturnOnlyForDevicesInTheRequestedProperty()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new EwelinkStoreFixture
        {
            Rows = [Row("visible", 1, 12.9), Row("another-property", 99, 41), Row("unassigned", null, 42)]
        };
        await store.SaveTokensAsync("operator", [1], default);
        using var configured = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<EwelinkOptions>();
            services.AddSingleton(new EwelinkOptions
            {
                AppId = "fixture", AppSecret = "fixture", OperatorEmail = "operator@example.test",
                RedirectUri = "https://example.test/api/v1/ewelink/callback"
            });
            services.RemoveAll<IEwelinkStore>();
            services.AddSingleton<IEwelinkStore>(store);
            services.Remove(services.Single(item => item.ServiceType == typeof(IHostedService) &&
                item.ImplementationType == typeof(EwelinkWorker)));
        }));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/ewelink/99")).StatusCode);
        using var payload = JsonDocument.Parse(await client.GetStringAsync("/api/v1/ewelink/1"));
        Assert.Empty(payload.RootElement.GetProperty("unassigned").EnumerateArray());
        var device = Assert.Single(payload.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal("visible", device.GetProperty("id").GetString());
        var measurements = device.GetProperty("measurements").EnumerateArray()
            .ToDictionary(item => item.GetProperty("metric").GetString()!);
        Assert.Equal(12.9, measurements["temperature"].GetProperty("number").GetDouble());
        Assert.Equal("fresh", measurements["temperature"].GetProperty("quality").GetString());
        Assert.Equal("stale", measurements["humidity"].GetProperty("quality").GetString());

        EwelinkRow Row(string id, long? property, double temperature) => new(id, id, property, null,
            JsonSerializer.Serialize(new EwelinkDevice(id, id, "Fixture", "", false, Connectivity.Online, now, false, null, null,
                new Dictionary<Metric, EwelinkMeasurement>
                {
                    [Metric.Temperature] = new(temperature, now),
                    [Metric.Humidity] = new(100, now.AddHours(-2), false)
                })));
    }

    [Fact]
    public async Task DaikinEndpointsKeepTheExistingSessionAndPropertyBoundary()
    {
        using var guest = host.CreateClient();
        using var viewer = AuthenticatedClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/daikin/1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.GetAsync("/api/v1/daikin/callback?state=forged&code=fixture")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/daikin/99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(
            "/api/v1/daikin/99/devices/00000000000000000000000000000001/water-history")).StatusCode);
        Assert.Contains("notConfigured", await viewer.GetStringAsync("/api/v1/daikin/1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(
            "/api/v1/daikin/1/devices/00000000000000000000000000000001/commands", new { operation = "power", value = "off" })).StatusCode);
    }

    [Fact]
    public void DaikinStateIsBrowserAndUserBoundSingleUseAndExpiring()
    {
        var clock = new FlowClock();
        var flow = new DaikinFlow(clock);
        var pending = flow.Begin("owner", 1, true);
        Assert.NotEqual(pending.Verifier, pending.Challenge);
        Assert.Null(flow.Accept(pending.Cookie, "forged", "one", out var mismatch));
        Assert.Equal("correlationMismatch", mismatch);
        Assert.Null(flow.Accept(null, pending.State, "one", out var missingCookie));
        Assert.Equal("missingCorrelationCookie", missingCookie);
        Assert.Null(flow.Accept("another-browser", pending.State, "one", out _));
        Assert.Equal(1L, flow.Accept(pending.Cookie, pending.State, "one", out _));
        Assert.Null(flow.Take("another-user", 1, pending.Cookie));
        Assert.Null(flow.Accept(pending.Cookie, pending.State, "two", out _));
        Assert.Equal("one", flow.Take("owner", 1, pending.Cookie)?.Code);
        Assert.Null(flow.Take("owner", 1, pending.Cookie));
        pending = flow.Begin("owner", 1, true);
        clock.Advance();
        Assert.Null(flow.Accept(pending.Cookie, pending.State, "expired", out var expired));
        Assert.Equal("expiredConnection", expired);
    }

    [Fact]
    public async Task DaikinCallbackRedirectsToTheLocalConfirmationWithoutExposingTheCode()
    {
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pending = host.Services.GetRequiredService<DaikinFlow>().Begin("viewer", 1, true);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/daikin/callback?state={pending.State}&code=fixture-code");
        request.Headers.TryAddWithoutValidation("Cookie", $"homeautomation.onecta={pending.Cookie}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/properties/1/daikin?onecta=finish", response.Headers.Location?.OriginalString);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Theory]
    [InlineData("ha-dev.britishlab.org:5443", "127.0.0.1", true)]
    [InlineData("ha-dev.britishlab.org:5443", "192.0.2.20", false)]
    [InlineData("localhost:5080", "127.0.0.1", false)]
    public async Task DevelopmentProxyMarksCsrfCookieSecureOnlyForTheNamedLoopbackHop(
        string authority, string upstream, bool secure)
    {
        var response = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(upstream);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(authority);
            context.Request.Path = "/api/v1/session/csrf";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var cookie = Assert.Single(response.Response.Headers.SetCookie)!;
        Assert.Equal(secure, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardedHttpsLoginAndEwelinkCallbackRespectCrossSiteCookieBoundaries()
    {
        var store = new EwelinkStoreFixture();
        var provider = new EwelinkProviderFixture();
        using var loginHost = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            });
            services.RemoveAll<IUserStore<LocalUser>>();
            services.AddSingleton<IUserStore<LocalUser>, PasswordFixture>();
            services.RemoveAll<EwelinkOptions>();
            services.AddSingleton(new EwelinkOptions
            {
                AppId = "fixture", AppSecret = "fixture", OperatorEmail = PasswordFixture.Email,
                RedirectUri = "https://ha-dev.britishlab.org:5443/api/v1/ewelink/callback"
            });
            services.RemoveAll<IEwelinkStore>();
            services.AddSingleton<IEwelinkStore>(store);
            services.Remove(services.Single(item => item.ServiceType == typeof(IHostedService) &&
                item.ImplementationType == typeof(EwelinkWorker)));
            services.AddHttpClient<EwelinkClient>().ConfigurePrimaryHttpMessageHandler(() => provider);
        }));

        var csrf = await Request("/api/v1/session/csrf");
        using var payload = await JsonDocument.ParseAsync(csrf.Response.Body);
        var token = payload.RootElement.GetProperty("token").GetString()!;
        var csrfCookie = Assert.Single(csrf.Response.Headers.SetCookie)!.Split(';')[0];
        var form = JsonSerializer.SerializeToUtf8Bytes(new
        {
            email = PasswordFixture.Email, password = PasswordFixture.Password
        });
        var missingCsrf = await Request("/api/v1/session/login", SetForm);
        Assert.Equal(StatusCodes.Status400BadRequest, missingCsrf.Response.StatusCode);
        var login = await Request("/api/v1/session/login", context =>
        {
            SetForm(context);
            context.Request.Headers.Cookie = csrfCookie;
            context.Request.Headers["X-CSRF-TOKEN"] = token;
        });
        Assert.Equal(StatusCodes.Status204NoContent, login.Response.StatusCode);
        var session = Assert.Single(login.Response.Headers.SetCookie, value =>
            value?.StartsWith("homeautomation.session=", StringComparison.Ordinal) == true)!;
        Assert.Contains("; secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);

        var result = await Request("/api/v1/properties", context => context.Request.Headers.Cookie = session.Split(';')[0]);
        Assert.Equal(StatusCodes.Status200OK, result.Response.StatusCode);
        using var properties = await JsonDocument.ParseAsync(result.Response.Body);
        Assert.Equal(1, properties.RootElement[0].GetProperty("id").GetInt32());

        var sessionCookie = session.Split(';')[0];
        var connectCsrf = await Request("/api/v1/session/csrf", context => context.Request.Headers.Cookie = sessionCookie);
        using var connectPayload = await JsonDocument.ParseAsync(connectCsrf.Response.Body);
        var connectToken = connectPayload.RootElement.GetProperty("token").GetString()!;
        var connectCookies = sessionCookie + "; " + Assert.Single(connectCsrf.Response.Headers.SetCookie)!.Split(';')[0];
        Assert.Equal(StatusCodes.Status400BadRequest, (await Start(1, false)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Start(99, true)).Response.StatusCode);

        var (state, correlation) = await Begin();
        var callback = await ReturnFromProvider(state, correlation, "192.0.2.20");
        Assert.Equal(StatusCodes.Status403Forbidden, callback.Response.StatusCode);
        callback = await ReturnFromProvider(state, null);
        Assert.Equal(StatusCodes.Status400BadRequest, callback.Response.StatusCode);
        Assert.Equal(0, provider.Exchanges);

        callback = await ReturnFromProvider(state, correlation);
        Assert.Equal(StatusCodes.Status302Found, callback.Response.StatusCode);
        Assert.Equal("/properties/1/ewelink?connect=success", callback.Response.Headers.Location);
        Assert.Equal("no-referrer", callback.Response.Headers["Referrer-Policy"]);
        Assert.Equal("no-store", callback.Response.Headers.CacheControl);
        callback = await ReturnFromProvider(state, correlation);
        Assert.Equal(StatusCodes.Status400BadRequest, callback.Response.StatusCode);
        Assert.Equal(1, provider.Exchanges);
        Assert.Equal("viewer", store.Owner);
        var envelope = Assert.IsType<byte[]>(store.Envelope);
        var vault = loginHost.Services.GetRequiredService<EwelinkVault>();
        Assert.Equal("fixture-access", vault.Open("viewer", "fixture", envelope).Access);

        var consent = loginHost.Services.GetRequiredService<EwelinkConsent>();
        var invalid = consent.Begin("missing-user", 1);
        callback = await ReturnFromProvider(invalid.State, "homeautomation.ewelink=" + invalid.Cookie);
        Assert.Equal(StatusCodes.Status403Forbidden, callback.Response.StatusCode);
        var revoked = consent.Begin("viewer", 99);
        callback = await ReturnFromProvider(revoked.State, "homeautomation.ewelink=" + revoked.Cookie);
        Assert.Equal(StatusCodes.Status403Forbidden, callback.Response.StatusCode);
        Assert.Equal(1, provider.Exchanges);

        (state, correlation) = await Begin();
        provider.Reject = true;
        callback = await ReturnFromProvider(state, correlation);
        Assert.Equal(StatusCodes.Status302Found, callback.Response.StatusCode);
        Assert.Equal("/properties/1/ewelink?connect=failed", callback.Response.Headers.Location);
        Assert.Equal(StatusCodes.Status400BadRequest, (await ReturnFromProvider(state, correlation)).Response.StatusCode);
        Assert.Equal(2, provider.Exchanges);
        Assert.Equal(1, store.TokenWrites);
        Assert.Same(envelope, store.Envelope);

        Task<HttpContext> ReturnFromProvider(string stateValue, string? correlationCookie, string upstream = "127.0.0.1") =>
            Request("/api/v1/ewelink/callback", context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(upstream);
                context.Request.QueryString = new QueryString($"?state={stateValue}&code=fixture&region=eu&property=99&user=another");
                context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
                context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
                // A top-level cross-site GET includes the Lax correlation cookie and omits the Strict session.
                if (correlationCookie is not null) context.Request.Headers.Cookie = correlationCookie;
            });

        Task<HttpContext> Start(long property, bool includeCsrf) =>
            Request($"/api/v1/ewelink/{property}/connect", context =>
            {
                context.Request.Method = "POST";
                context.Request.Headers.Cookie = connectCookies;
                if (includeCsrf) context.Request.Headers["X-CSRF-TOKEN"] = connectToken;
            });

        async Task<(string State, string Cookie)> Begin()
        {
            var start = await Start(1, true);
            Assert.Equal(StatusCodes.Status200OK, start.Response.StatusCode);
            var cookie = Assert.Single(start.Response.Headers.SetCookie)!;
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            using var result = await JsonDocument.ParseAsync(start.Response.Body);
            var url = new Uri(result.RootElement.GetProperty("authorizationUrl").GetString()!);
            return (QueryHelpers.ParseQuery(url.Query)["state"].ToString(), cookie.Split(';')[0]);
        }

        void SetForm(HttpContext context)
        {
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = form.Length;
            context.Request.Body = new MemoryStream(form, writable: false);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestHasBody());
        }

        Task<HttpContext> Request(string path, Action<HttpContext>? configure = null) =>
            loginHost.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("ha-dev.britishlab.org", 5443);
                context.Request.Path = path;
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                configure?.Invoke(context);
            });
    }

    private sealed class RequestHasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class EwelinkStoreFixture : IEwelinkStore
    {
        public string? Owner { get; private set; }
        public byte[]? Envelope { get; private set; }
        public int TokenWrites { get; private set; }
        public IReadOnlyList<EwelinkRow> Rows { get; init; } = [];
        public Task<bool> SchemaReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<EwelinkAccount?> AccountAsync(CancellationToken ct) => Task.FromResult(
            Owner is null ? null : new EwelinkAccount(Owner, Envelope, "connected", null, null));
        public Task SaveTokensAsync(string owner, byte[]? envelope, CancellationToken ct)
        {
            Owner = owner;
            Envelope = envelope;
            TokenWrites++;
            return Task.CompletedTask;
        }
        public Task ReportAsync(string owner, string status, DateTimeOffset? success, DateTimeOffset next, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<EwelinkRow>> DevicesAsync(string owner, CancellationToken ct) => Task.FromResult(Rows);
        public Task SaveAsync(string owner, EwelinkDevice device, CancellationToken ct) => throw new NotSupportedException();
        public Task AssignAsync(EwelinkRow row, string owner, long property, long? room, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class EwelinkProviderFixture : HttpMessageHandler
    {
        public int Exchanges { get; private set; }
        public bool Reject { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("eu-apia.coolkit.cc", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/v2/user/oauth/token")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Exchanges++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = Reject ? new StringContent("""{"error":405,"msg":"fixture rejection","data":{}}""") :
                        JsonContent.Create(new
                        {
                            error = 0,
                            data = new
                            {
                                accessToken = "fixture-access", refreshToken = "fixture-refresh",
                                atExpiredTime = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
                                rtExpiredTime = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds()
                            }
                        })
                });
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v2/family", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"error":0,"data":{"familyList":[]}}""")
            });
        }
    }

    private sealed class PasswordFixture : IUserPasswordStore<LocalUser>
    {
        public const string Email = "viewer@example.test";
        public const string Password = "fixture-password-for-tests";
        private readonly LocalUser user = new() { Id = "viewer", UserName = Email, NormalizedUserName = Email.ToUpperInvariant() };

        public PasswordFixture() => user.PasswordHash = new PasswordHasher<LocalUser>().HashPassword(user, Password);
        public Task<LocalUser?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult(id == user.Id ? user : null);
        public Task<LocalUser?> FindByNameAsync(string name, CancellationToken ct) =>
            Task.FromResult(name == user.NormalizedUserName ? user : null);
        public Task<string> GetUserIdAsync(LocalUser item, CancellationToken ct) => Task.FromResult(item.Id);
        public Task<string?> GetUserNameAsync(LocalUser item, CancellationToken ct) => Task.FromResult(item.UserName);
        public Task<string?> GetNormalizedUserNameAsync(LocalUser item, CancellationToken ct) => Task.FromResult(item.NormalizedUserName);
        public Task<string?> GetPasswordHashAsync(LocalUser item, CancellationToken ct) => Task.FromResult(item.PasswordHash);
        public Task<bool> HasPasswordAsync(LocalUser item, CancellationToken ct) => Task.FromResult(item.PasswordHash is not null);
        public Task SetUserNameAsync(LocalUser item, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(LocalUser item, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task SetPasswordHashAsync(LocalUser item, string? hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(LocalUser item, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(LocalUser item, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(LocalUser item, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class FlowClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddMinutes(11);
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
            builder.UseSetting("Growatt:Enabled", "false");
            builder.UseSetting("Daikin:ClientId", "");
            builder.UseSetting("Daikin:ClientSecret", "");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMonitoringReader>();
                services.AddSingleton<IMonitoringReader, FixtureReader>();
                services.RemoveAll<TuyaOptions>();
                services.AddSingleton(new TuyaOptions());
                services.RemoveAll<EwelinkOptions>();
                services.AddSingleton(new EwelinkOptions());
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
