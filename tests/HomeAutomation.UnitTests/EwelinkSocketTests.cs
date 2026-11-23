using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Ewelink;

namespace HomeAutomation.UnitTests;

public sealed class EwelinkSocketTests
{
    private const string AccountKey = "fixture-account";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static readonly EwelinkTokens Tokens = new("eu", "fixture-access", "fixture-refresh", Now.AddHours(1), Now.AddDays(1), AccountKey);

    [Fact]
    public async Task DispatchUsesDocumentedRegionalEndpointAndOAuthBearer()
    {
        using var http = new HttpClient(new DispatchHandler());
        var client = new EwelinkClient(http, new() { AppId = "fixture-app" }, TimeProvider.System, new(TimeProvider.System));
        Assert.Equal("wss://eu-pconnect.coolkit.cc:8080/api/ws", (await client.DispatchAsync(Tokens, default)).AbsoluteUri);
    }

    [Theory]
    [InlineData("coolkit.cc.attacker.example")]
    [InlineData("127.0.0.1")]
    [InlineData("coolkit.cc@attacker.example")]
    public void DispatchRejectsUntrustedAddresses(string domain)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { domain, port = 8080 }));
        Assert.Equal("invalidSocketAddress", Assert.Throws<EwelinkFailure>(() => EwelinkSocketProtocol.Address(json.RootElement)).Status);
    }

    [Fact]
    public void DeviceSequenceMustBeARecentProviderTimestamp()
    {
        using var documented = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "update", userAgent = "device", d_seq = Now.AddSeconds(-2).ToUnixTimeMilliseconds(), ts = 0
        }));
        using var untimed = JsonDocument.Parse("""{"action":"update","userAgent":"device","ts":0}""");
        Assert.Equal(Now.AddSeconds(-2), EwelinkSocketProtocol.DeviceEventTime(documented.RootElement, Now));
        Assert.Null(EwelinkSocketProtocol.DeviceEventTime(documented.RootElement, Now.AddMinutes(3)));
        Assert.Null(EwelinkSocketProtocol.DeviceEventTime(untimed.RootElement, Now));
    }

    [Fact]
    public void ReconnectsOnlyWithTheCurrentGrantAfterRotationOrDisconnect()
    {
        var lease = new EwelinkSocketIdentity("fixture-owner", [1, 2], Tokens);
        var account = new EwelinkAccount("fixture-owner", [1, 2], "connected", null, null, null);
        Assert.True(lease.Matches(account, Now));
        Assert.False(lease.Matches(account with { Envelope = [3, 4] }, Now));
        Assert.False(lease.Matches(account with { Envelope = null }, Now));
        Assert.False(lease.Matches(account, Tokens.AccessExpires));
    }

    [Fact]
    public async Task SessionAuthenticatesSendsHeartbeatAndDeliversOnlyDeviceReportsForItsAccount()
    {
        using var socket = new FixtureSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new SessionClock();
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorized = false;
        var session = EwelinkSocketSession.RunAsync(socket, Tokens, "fixture-app", clock, () => authorized = true,
            (id, _, _, _) => { seen.SetResult(id); return Task.CompletedTask; }, _ => Task.FromResult(true), stop.Token);
        using var handshake = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync(stop.Token));
        Assert.Equal("userOnline", handshake.RootElement.GetProperty("action").GetString());
        Assert.Equal("fixture-app", handshake.RootElement.GetProperty("appid").GetString());
        Assert.Equal("fixture-access", handshake.RootElement.GetProperty("at").GetString());
        Assert.Equal(AccountKey, handshake.RootElement.GetProperty("apikey").GetString());
        socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            error = 0, apikey = AccountKey, sequence = handshake.RootElement.GetProperty("sequence").GetString(),
            config = new { hb = 1, hbInterval = 1 }
        }));
        socket.Incoming.Writer.TryWrite(Event("other-account", "device", Now));
        socket.Incoming.Writer.TryWrite(Event(AccountKey, "app", Now));
        socket.Incoming.Writer.TryWrite(Event(AccountKey, "device", Now.AddMinutes(-3)));
        socket.Incoming.Writer.TryWrite(Event(AccountKey, "device", Now));
        Assert.Equal("fixture-device", await seen.Task.WaitAsync(stop.Token));
        Assert.True(authorized);
        Assert.Equal("ping", await socket.Sent.Reader.ReadAsync(stop.Token));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session);
    }

    [Fact]
    public async Task ExpiredSessionStopsAndRejectedHandshakeDoesNotStartReadingEvents()
    {
        using var socket = new FixtureSocket();
        var clock = new SessionClock();
        var authenticated = 0;
        Task Run(EwelinkTokens tokens) => EwelinkSocketSession.RunAsync(socket, tokens, "fixture-app", clock,
            () => authenticated++, (_, _, _, _) => throw new InvalidOperationException(), _ => Task.FromResult(true), default);
        socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { error = 407, sequence = Now.ToUnixTimeMilliseconds().ToString() }));
        var rejection = await Assert.ThrowsAsync<EwelinkFailure>(() => Run(Tokens));
        Assert.Equal(407, rejection.ProviderCode);
        Assert.Equal(0, authenticated);
        socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            error = 0, apikey = AccountKey, sequence = Now.ToUnixTimeMilliseconds().ToString(), config = new { hb = 0 }
        }));
        await Run(Tokens with { AccessExpires = Now });
        Assert.Equal(1, authenticated);
        Assert.Equal(TimeSpan.FromSeconds(15), EwelinkSocketProtocol.ReconnectDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), EwelinkSocketProtocol.ReconnectDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(15), EwelinkSocketProtocol.ReconnectDelay(10));
    }

    [Fact]
    public void DevicePatchesPreserveOmittedFieldsAndIgnoreDuplicateAndOlderObservations()
    {
        var meter = new EwelinkDevice("fixture-device", "Boiler", "PSF-X67", "", false, Connectivity.Online, Now,
            true, false, Now, new Dictionary<Metric, EwelinkMeasurement> { [Metric.Voltage] = new(230, Now) }, Uiid: 32);
        using var power = JsonDocument.Parse("""{"power":"54.2","current":"0.24","switch":"on"}""");
        var time = Now.AddSeconds(5);
        var (snapshot, samples) = EwelinkMapping.MergeEvent(meter, power.RootElement, time);
        Assert.Equal(3, samples.Count);
        Assert.Equal(230, snapshot.Measurements![Metric.Voltage].Value);
        Assert.Equal(54.2, snapshot.Measurements[Metric.ActivePower].Value);
        Assert.True(snapshot.Measurements[Metric.ActivePower].FromSocket);
        Assert.True(snapshot.SocketRelay);
        Assert.False(snapshot.Relay);
        Assert.Empty(EwelinkMapping.MergeEvent(snapshot, power.RootElement, time).Samples);
        Assert.Empty(EwelinkMapping.MergeEvent(snapshot, power.RootElement, time.AddSeconds(-1)).Samples);
        Assert.Equal("relayUnavailable", Assert.Throws<EwelinkFailure>(() =>
            EwelinkService.CheckRelayCommand(snapshot, true, true, time, TimeSpan.FromMinutes(15))).Status);
        Assert.Null(EwelinkMapping.Merge(meter with { SeenAt = time.AddMinutes(5), RelayAt = time.AddMinutes(5) }, snapshot).SocketRelay);
    }

    [Fact]
    public void ThermostatUsesVerifiedProbeAndPreservesIndependentReadings()
    {
        var thermostat = new EwelinkDevice("fixture-device", "Thermostat", "THR320D", "", false, Connectivity.Online, Now,
            true, false, Now, SensorType: "THS01", Uiid: 181);
        using var temperature = JsonDocument.Parse("""{"currentTemperature":"12.9","currentHumidity":"104","power":"10"}""");
        using var humidity = JsonDocument.Parse("""{"currentHumidity":"100"}""");
        var (snapshot, first) = EwelinkMapping.MergeEvent(thermostat, temperature.RootElement, Now.AddSeconds(1));
        Assert.Equal(Metric.Temperature, Assert.Single(first).Metric);
        var (updated, second) = EwelinkMapping.MergeEvent(snapshot, humidity.RootElement, Now.AddSeconds(2));
        Assert.Equal(Metric.Humidity, Assert.Single(second).Metric);
        Assert.Equal(12.9, updated.Measurements![Metric.Temperature].Value);
        Assert.Equal(100, updated.Measurements[Metric.Humidity].Value);
        Assert.False(updated.SupportsRelayWrites);
    }

    private static string Event(string key, string sender, DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        action = "update", apikey = key, userAgent = sender, deviceid = "fixture-device",
        sequence = at.ToUnixTimeMilliseconds().ToString(), @params = new { power = "54.2" }
    });

    private sealed class DispatchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://eu-dispa.coolkit.cc/dispatch/app", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-access", request.Headers.Authorization.Parameter);
            Assert.Equal("fixture-app", Assert.Single(request.Headers.GetValues("X-CK-Appid")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"error":0,"domain":"eu-pconnect.coolkit.cc","port":8080}""")
            });
        }
    }

    private sealed class SessionClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime == TimeSpan.FromSeconds(8) ? TimeSpan.FromMilliseconds(20) : dueTime, period);
    }

    private sealed class FixtureSocket : WebSocket
    {
        public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var message = await Incoming.Reader.ReadAsync(ct);
            var count = Encoding.UTF8.GetBytes(message.AsSpan(), buffer.AsSpan());
            return new(count, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct) =>
            Sent.Writer.WriteAsync(Encoding.UTF8.GetString(buffer), ct).AsTask();
    }
}
