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
        Assert.Equal(new EwelinkSocketTime(Now.AddSeconds(-2), EwelinkSocketTimeSource.ProviderSequence),
            EwelinkSocketProtocol.DeviceEventTime(documented.RootElement, Now));
        Assert.Null(EwelinkSocketProtocol.DeviceEventTime(documented.RootElement, Now.AddMinutes(3)));
        Assert.Null(EwelinkSocketProtocol.DeviceEventTime(untimed.RootElement, Now));
    }

    [Fact]
    public async Task DiagnosedUntimedElectricalFrameIsReceivedWithItsServerTimestamp()
    {
        using var socket = new FixtureSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new SessionClock();
        var seen = new TaskCompletionSource<EwelinkSocketTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trace = new EwelinkSocketTrace();
        var session = EwelinkSocketSession.RunAsync(socket, Tokens, "fixture-app", clock, () => { }, (id, values, time, _) =>
        {
            Assert.Equal("fixture-device", id);
            var (_, samples) = EwelinkMapping.MergeEvent(Meter(), values, time.At, source: time.Source);
            Assert.Equal(3, samples.Count);
            Assert.All(samples, sample =>
            {
                Assert.True(sample.ServerReceivedTimestamp);
                Assert.Equal(Now, sample.ObservedAt);
            });
            seen.SetResult(time);
            return Task.CompletedTask;
        }, _ => Task.FromResult(true), stop.Token, trace: trace);
        using var handshake = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync(stop.Token));
        socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            error = 0, apikey = AccountKey, sequence = handshake.RootElement.GetProperty("sequence").GetString()
        }));
        const string frame = """
            {"action":"update","userAgent":"device","apikey":"fixture-account","deviceid":"fixture-device",
             "params":{"power":"500.00","voltage":"239.83","current":"2.10"}}
            """;
        socket.Incoming.Writer.TryWrite(frame.Replace("fixture-account", "other-account", StringComparison.Ordinal));
        socket.Incoming.Writer.TryWrite(frame.Replace("\"userAgent\":\"device\"", "\"userAgent\":\"app\"", StringComparison.Ordinal));
        socket.Incoming.Writer.TryWrite(frame);
        Assert.Equal(new EwelinkSocketTime(Now, EwelinkSocketTimeSource.ServerReceived), await seen.Task.WaitAsync(stop.Token));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session);
        var summary = trace.Drain();
        Assert.Contains("reject.accountKey=1", summary, StringComparison.Ordinal);
        Assert.Contains("reject.sender=1", summary, StringComparison.Ordinal);
        Assert.Contains("queuedElectricalFrames=1", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UntimedElectricalFramesKeepPerFieldOrderingAndIgnoreRelayAndInvalidValues()
    {
        var trace = new EwelinkSocketTrace();
        using var verified = JsonDocument.Parse("""{"power":"500.00"}""");
        using var untimed = JsonDocument.Parse("""{"power":"999.00","voltage":"239.83","current":"31.0","switch":"on"}""");
        var (meter, _) = EwelinkMapping.MergeEvent(Meter(), verified.RootElement, Now);
        var (first, samples) = ApplyUntimed(meter, Now.AddSeconds(1));
        var sample = Assert.Single(samples);
        Assert.Equal(Metric.Voltage, sample.Metric);
        Assert.True(sample.ServerReceivedTimestamp);
        Assert.False(first.Relay);
        Assert.Null(first.SocketRelay);
        Assert.Equal(500, first.Measurements![Metric.ActivePower].Value);
        Assert.Equal(239.83, first.Measurements[Metric.Voltage].Value);
        Assert.Empty(ApplyUntimed(first, Now.AddSeconds(2)).Samples);
        Assert.Empty(ApplyUntimed(first, Now).Samples);
        using var olderReport = JsonDocument.Parse("""{"voltage":"200.00"}""");
        Assert.Empty(EwelinkMapping.MergeEvent(first, olderReport.RootElement, Now).Samples);
        Assert.Empty(ApplyUntimed(Meter() with { Model = "THR320D", Uiid = 181 }, Now).Samples);

        var afterExpiry = ApplyUntimed(first, Now.AddMinutes(3));
        Assert.Equal(Metric.ActivePower, Assert.Single(afterExpiry.Samples).Metric);
        Assert.Contains("reject.freshProviderTimestamp", trace.Drain(), StringComparison.Ordinal);

        (EwelinkDevice Snapshot, IReadOnlyList<Sample> Samples) ApplyUntimed(EwelinkDevice device, DateTimeOffset at) =>
            EwelinkMapping.MergeEvent(device, untimed.RootElement, at, trace, EwelinkSocketTimeSource.ServerReceived);
    }

    [Fact]
    public void ElectricalEnvelopeTimeCanBeUsedButInvalidOrStaleTimestampsDoNotFallBack()
    {
        Assert.Equal(new EwelinkSocketTime(Now.AddSeconds(-1), EwelinkSocketTimeSource.ProviderEnvelope),
            Parse(Now.AddSeconds(-1).ToUnixTimeSeconds().ToString()));
        Assert.Null(Parse(Now.AddMinutes(-3).ToUnixTimeSeconds().ToString()));
        Assert.Null(Parse(Now.AddSeconds(1).ToUnixTimeSeconds().ToString()));
        Assert.Null(Parse("\"untrusted\""));

        static EwelinkSocketTime? Parse(string timestamp)
        {
            using var message = JsonDocument.Parse($$$"""
                {"action":"update","userAgent":"device","ts":{{{timestamp}}},"params":{"power":"500.00"}}
                """);
            return EwelinkSocketProtocol.DeviceEventTime(message.RootElement, Now);
        }
    }

    private static EwelinkDevice Meter() => new("fixture-device", "Fixture meter", "PSF-X67", "", false,
        Connectivity.Online, Now.AddSeconds(-5), true, false, Now.AddSeconds(-5), Uiid: 32);

    [Fact]
    public void RecentElectricalReportsUseTheirOwnWatermarkAcrossHttpReconciliation()
    {
        var meter = new EwelinkDevice("fixture-device", "Fixture meter", "PSF-X67", "", false, Connectivity.Online, Now,
            true, false, Now, new Dictionary<Metric, EwelinkMeasurement> { [Metric.ActivePower] = new(241.44, Now) }, Uiid: 32);
        using var packet = JsonDocument.Parse("""{"power":"500.00","switch":"on"}""");
        var measuredAt = Now.AddMilliseconds(-100);
        var (live, samples) = EwelinkMapping.MergeEvent(meter, packet.RootElement, measuredAt);
        Assert.Equal(Metric.ActivePower, Assert.Single(samples).Metric);
        Assert.Equal(measuredAt, samples[0].ObservedAt);
        Assert.Null(live.SocketRelay); // Electrical timestamp arbitration cannot bypass the relay check.
        Assert.False(live.Relay);
        Assert.Equal(500, live.Measurements![Metric.ActivePower].Value);

        var immediatePoll = EwelinkMapping.Merge(meter with { SeenAt = Now.AddSeconds(10) }, live);
        Assert.Equal(500, immediatePoll.Measurements![Metric.ActivePower].Value);
        Assert.True(immediatePoll.Measurements[Metric.ActivePower].FromSocket);
        var laterPoll = EwelinkMapping.Merge(meter with { SeenAt = Now.AddMinutes(3) }, immediatePoll);
        Assert.Equal(241.44, laterPoll.Measurements![Metric.ActivePower].Value);
        Assert.Equal(measuredAt, laterPoll.Measurements[Metric.ActivePower].SocketTime);
        var trace = new EwelinkSocketTrace();
        Assert.Empty(EwelinkMapping.MergeEvent(laterPoll, packet.RootElement, measuredAt, trace).Samples);
        Assert.Empty(EwelinkMapping.MergeEvent(laterPoll, packet.RootElement, measuredAt.AddMilliseconds(-1), trace).Samples);
        Assert.Equal("reject.electricalDuplicate=1, reject.electricalOutOfOrder=1", trace.Drain());
    }

    [Fact]
    public void ElectricalTraceRecordsOnlyKnownKindsAndReasonCodes()
    {
        var trace = new EwelinkSocketTrace();
        using var packet = JsonDocument.Parse("""
            {"action":"update","userAgent":"device","apikey":"dummy-secret","deviceid":"fixture-id",
             "d_seq":123,"ts":0,"params":{"power":"500.0","current":3.2,"unrecognized":"unused"}}
            """);
        trace.Shape(packet.RootElement, packet.RootElement.GetProperty("params"));
        Assert.Null(EwelinkSocketProtocol.DeviceEventTime(packet.RootElement, Now, trace));
        var summary = trace.Drain();
        Assert.Contains("electricalFrames=1", summary, StringComparison.Ordinal);
        Assert.Contains("field.power.String=1", summary, StringComparison.Ordinal);
        Assert.Contains("field.current.Number=1", summary, StringComparison.Ordinal);
        Assert.Contains("reject.timestampNotEpochMilliseconds=1", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy-secret", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-id", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("500.0", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("unrecognized", summary, StringComparison.Ordinal);
        Assert.Empty(trace.Drain());
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

    [Theory]
    [InlineData(0)]
    [InlineData(504)]
    public async Task ElectricalActivationSendsOnlyUiActiveAndMatchesItsReply(int providerCode)
    {
        using var socket = new FixtureSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new SessionClock();
        var acknowledged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestionBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = EwelinkSocketSession.RunAsync(socket, Tokens, "fixture-app", clock, () => { },
            (_, _, _, ct) =>
            {
                ingestionBlocked.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, _ => Task.FromResult(true), stop.Token,
            async (channel, ct) =>
            {
                var reply = await channel.SendAsync("fixture-device", ct);
                acknowledged.SetResult(await reply.WaitAsync(ct));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        using var handshake = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync(stop.Token));
        socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            error = 0, apikey = AccountKey, sequence = handshake.RootElement.GetProperty("sequence").GetString(),
            config = new { hb = 0 }
        }));
        using var activation = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync(stop.Token));
        var command = activation.RootElement;
        Assert.Equal("update", command.GetProperty("action").GetString());
        Assert.Equal("fixture-device", command.GetProperty("deviceid").GetString());
        Assert.Equal(AccountKey, command.GetProperty("apikey").GetString());
        Assert.Equal("app", command.GetProperty("userAgent").GetString());
        var parameter = Assert.Single(command.GetProperty("params").EnumerateObject());
        Assert.Equal("uiActive", parameter.Name);
        Assert.Equal(60, parameter.Value.GetInt32());
        var sequence = command.GetProperty("sequence").GetString();
        string Reply(string id, string? seq, int code) => JsonSerializer.Serialize(new { deviceid = id, sequence = seq, error = code });
        socket.Incoming.Writer.TryWrite(Reply("fixture-device", "wrong-sequence", 7));
        socket.Incoming.Writer.TryWrite(Reply("another-device", sequence, 7));
        socket.Incoming.Writer.TryWrite(Event(AccountKey, "device", Now));
        await ingestionBlocked.Task.WaitAsync(stop.Token);
        Assert.False(acknowledged.Task.IsCompleted);
        socket.Incoming.Writer.TryWrite(Reply("fixture-device", sequence, providerCode));
        Assert.Equal(providerCode, await acknowledged.Task.WaitAsync(stop.Token));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session);
    }

    [Fact]
    public void ElectricalViewingCoalescesTabsRenewalAndReconnectAndExpiresWithoutAViewer()
    {
        var clock = new ElectricalClock();
        var state = new EwelinkElectricalReporting(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        const string owner = "owner";
        const string device = "meter";
        const long property = 1;
        Assert.False(state.TryStart(owner, property, device));
        Assert.True(state.View(owner, property, first, true));
        Assert.True(state.View(owner, property, second, true));
        Assert.Equal([property], state.ViewedProperties(owner));
        Assert.Empty(state.ViewedProperties("other-owner"));
        Assert.True(state.TryStart(owner, property, device));
        Assert.False(state.TryStart(owner, property, device));
        state.Accepted(device);
        var meter = new EwelinkDevice(device, "Boiler", "PSF-X67", "", false, Connectivity.Online,
            Now.AddSeconds(-2), true, false, Now.AddSeconds(-2), Uiid: 32);
        Assert.Equal("awaitingReport", state.Status(meter).Status);
        using var report = JsonDocument.Parse("""{"power":"1908.02","voltage":"239.83","current":"7.97"}""");
        var (observed, samples) = EwelinkMapping.MergeEvent(meter, report.RootElement, Now.AddSeconds(1));
        Assert.Equal(3, samples.Count);
        Assert.False(observed.Relay);
        Assert.Null(observed.SocketRelay);
        Assert.Equal("receivingReports", state.Status(observed).Status);

        state.View(owner, property, first, false);
        clock.Advance(20);
        state.View(owner, property, second, true);
        clock.Advance(20);
        state.View(owner, property, second, true);
        clock.Advance(9);
        Assert.False(state.TryStart(owner, property, device)); // The account socket can reconnect before the renewal is due.
        clock.Advance(1);
        Assert.True(state.TryStart(owner, property, device));
        state.Accepted(device);
        Assert.Equal("receivingReports", state.Status(observed).Status);
        state.View(owner, property, second, false);
        Assert.Empty(state.ViewedProperties(owner));

        state.View(owner, property, first, true);
        clock.Advance(60);
        Assert.Empty(state.ViewedProperties(owner));
        Assert.Equal("waitingForActivation", state.Status(observed).Status);
        Assert.False(state.TryStart(owner, property, device));
    }

    [Fact]
    public void ElectricalActivationCapsDevicesAndLatchesProviderRejectionAcrossSessions()
    {
        var clock = new ElectricalClock();
        var state = new EwelinkElectricalReporting(clock);
        Assert.True(state.View("owner", 1, Guid.NewGuid(), true));
        for (var i = 0; i < EwelinkElectricalReporting.MaxDevices; i++) Assert.True(state.TryStart("owner", 1, $"meter-{i}"));
        Assert.False(state.TryStart("owner", 1, "overflow"));
        state.Stop("activationRejected", 504);
        clock.Advance(100);
        Assert.True(state.View("owner", 1, Guid.NewGuid(), true));
        Assert.False(state.TryStart("owner", 1, "meter-1"));
        var meter = new EwelinkDevice("meter-1", "Boiler", "PSF-X67", "", false, Connectivity.Online, Now,
            true, false, Now, Uiid: 32);
        Assert.Equal(new EwelinkElectricalStatus("activationRejected", 504), state.Status(meter));
    }

    [Fact]
    public async Task SessionAuthenticatesSendsHeartbeatAndDeliversOnlyDeviceReportsForItsAccount()
    {
        using var socket = new FixtureSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new SessionClock();
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorized = false;
        var trace = new EwelinkSocketTrace();
        var session = EwelinkSocketSession.RunAsync(socket, Tokens, "fixture-app", clock, () => authorized = true,
            (id, _, _, _) => { seen.SetResult(id); return Task.CompletedTask; }, _ => Task.FromResult(true), stop.Token,
            trace: trace);
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
        var summary = trace.Drain();
        Assert.Contains("electricalFrames=4", summary, StringComparison.Ordinal);
        Assert.Contains("queuedElectricalFrames=1", summary, StringComparison.Ordinal);
        Assert.Contains("reject.accountKey=1", summary, StringComparison.Ordinal);
        Assert.Contains("reject.sender=1", summary, StringComparison.Ordinal);
        Assert.Contains("reject.timestampStale=1", summary, StringComparison.Ordinal);
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

    private sealed class ElectricalClock : TimeProvider
    {
        private DateTimeOffset current = Now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(int seconds) => current = current.AddSeconds(seconds);
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
