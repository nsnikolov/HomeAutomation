using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public interface IEwelinkLiveNotifier
{
    Task ChangedAsync(long property, CancellationToken ct);
}

internal sealed record EwelinkSocketIdentity(string Owner, byte[] Envelope, EwelinkTokens Tokens)
{
    public bool Matches(EwelinkAccount? account, DateTimeOffset now) =>
        account is { Envelope: not null } && account.Owner == Owner && account.Status != "reauthorizationRequired" &&
        Tokens.AccessExpires > now && account.Envelope.AsSpan().SequenceEqual(Envelope);
}

public sealed partial class EwelinkService
{
    internal async Task<(EwelinkSocketIdentity Identity, Uri Address)?> OpenSocketAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!await SchemaReadyAsync(ct)) return null;
            var account = await store.AccountAsync(ct);
            if (account?.Envelope is null || account.Status == "reauthorizationRequired") return null;
            var now = clock.GetUtcNow();
            if (account.Status is not ("connected" or "partial") && Utc(account.NextSync) > now)
                throw new EwelinkFailure("socketDeferred", Utc(account.NextSync));
            var tokens = vault.Open(account.Owner, options.AppId!, account.Envelope);
            if (tokens.AccessExpires <= now || tokens.AccountKey is null) return null;
            var address = await client.DispatchAsync(tokens, ct);
            return (new(account.Owner, account.Envelope, tokens), address);
        }
        finally { gate.Release(); }
    }

    internal async Task<long?> IngestSocketEventAsync(EwelinkSocketIdentity identity, string externalId, JsonElement parameters,
        DateTimeOffset at, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (at < now - EwelinkSocketProtocol.Freshness || !identity.Matches(await store.AccountAsync(ct), now))
                return null;
            var row = (await store.DevicesAsync(identity.Owner, ct)).SingleOrDefault(device => device.ExternalId == externalId);
            if (row?.PropertyId is not { } property || !await permissions.CanReadAsync(identity.Owner, property, ct))
                return null;
            var device = row.Device;
            if (device.Shared) return null;
            var (snapshot, samples) = EwelinkMapping.MergeEvent(device, parameters, at);
            if (samples.Count == 0) return null;
            await IngestAsync(property, row.RoomId, snapshot, includeRelay: false, ct, samples);
            await store.SaveAsync(identity.Owner, snapshot, ct);
            return property;
        }
        finally { gate.Release(); }
    }
}

internal sealed class EwelinkSocketWorker(IServiceScopeFactory scopes, EwelinkOptions options, TimeProvider clock,
    EwelinkElectricalReporting electrical, ILogger<EwelinkSocketWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.IsConfigured || !options.EnableWebSocket) return;
        using var transport = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = clock.GetTimestamp();
            var delay = TimeSpan.FromMinutes(1);
            try
            {
                (EwelinkSocketIdentity Identity, Uri Address)? connection;
                await using (var scope = scopes.CreateAsyncScope())
                    connection = await scope.ServiceProvider.GetRequiredService<EwelinkService>().OpenSocketAsync(ct);
                if (connection is { } active)
                {
                    using var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock))
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                        await socket.ConnectAsync(active.Address, transport, connect.Token);
                    await EwelinkSocketSession.RunAsync(socket, active.Identity.Tokens, options.AppId!, clock,
                        () => logger.LogInformation("eWeLink WebSocket authenticated."),
                        (id, parameters, at, token) => ReceiveAsync(active.Identity, id, parameters, at, token),
                        token => CheckIdentityAsync(active.Identity, token), ct,
                        options.LiveElectricalConfigured ? (channel, token) => ActivateAsync(active.Identity, channel, token) : null);
                    failures = 0;
                    delay = EwelinkSocketProtocol.ReconnectDelay(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                if (clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(5)) failures = 0;
                failures = Math.Min(failures + 1, 7);
                delay = EwelinkSocketProtocol.ReconnectDelay(failures);
                if (error is EwelinkFailure failure)
                {
                    if (failure.RetryAt - clock.GetUtcNow() is { } wait && wait > delay) delay = wait;
                    logger.LogWarning("eWeLink WebSocket deferred with {Status}, HTTP {HttpStatus}, provider code {ProviderCode}.",
                        failure.Status, failure.HttpStatus, failure.ProviderCode);
                }
                else
                    logger.LogWarning("eWeLink WebSocket deferred with {ErrorType}.", error.GetType().Name);
            }
            await Task.Delay(delay, clock, ct);
        }
    }

    private async Task ActivateAsync(EwelinkSocketIdentity identity, EwelinkActivationChannel channel, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        DateTimeOffset? retryAt = null;
        do
        {
            if (electrical.IsStopped || retryAt > clock.GetUtcNow() || electrical.ViewedProperties(identity.Owner).Length == 0)
                continue;
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<EwelinkService>();
            var notifier = scope.ServiceProvider.GetRequiredService<IEwelinkLiveNotifier>();
            foreach (var target in await service.ElectricalTargetsAsync(identity, ct))
            {
                try
                {
                    var reply = await service.SendElectricalActivationAsync(identity, target, channel, ct);
                    if (reply is null) continue;
                    var code = await reply.WaitAsync(TimeSpan.FromSeconds(4), clock, ct);
                    if (code != 0)
                    {
                        await StopActivatingAsync("activationRejected", code);
                        break;
                    }
                    electrical.Accepted(target.ExternalId);
                    await notifier.ChangedAsync(target.PropertyId!.Value, ct);
                }
                catch (TimeoutException)
                {
                    await StopActivatingAsync("activationUnconfirmed", null);
                    break;
                }
                catch (EwelinkFailure failure) when (failure.Status is "quotaExceeded" or "syncDeferred")
                {
                    retryAt = failure.RetryAt ?? clock.GetUtcNow().AddMinutes(1);
                    logger.LogWarning("eWeLink electrical activation deferred with {Status} until {RetryAt}.", failure.Status, retryAt);
                    break;
                }
            }

            async Task StopActivatingAsync(string reason, int? providerCode)
            {
                electrical.Stop(reason, providerCode);
                logger.LogWarning("eWeLink electrical activation stopped with {Status}, CoolKit code {ProviderCode}.", reason, providerCode);
                foreach (var property in electrical.ViewedProperties(identity.Owner))
                    await notifier.ChangedAsync(property, ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<bool> CheckIdentityAsync(EwelinkSocketIdentity identity, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<IEwelinkStore>().AccountAsync(ct);
        return identity.Matches(account, clock.GetUtcNow());
    }

    private async Task ReceiveAsync(EwelinkSocketIdentity identity, string id, JsonElement parameters, DateTimeOffset at,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EwelinkService>();
        var property = await service.IngestSocketEventAsync(identity, id, parameters, at, ct);
        if (property is { } assigned)
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IEwelinkLiveNotifier>();
            await notifier.ChangedAsync(assigned, ct);
        }
    }
}

internal static class EwelinkSocketSession
{
    private sealed record Report(string Device, JsonElement Parameters, DateTimeOffset At);

    public static async Task RunAsync(WebSocket socket, EwelinkTokens tokens, string appId, TimeProvider clock, Action authenticated,
        Func<string, JsonElement, DateTimeOffset, CancellationToken, Task> observed,
        Func<CancellationToken, Task<bool>> checkIdentity, CancellationToken ct,
        Func<EwelinkActivationChannel, CancellationToken, Task>? activate = null)
    {
        var now = clock.GetUtcNow();
        var sequence = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        TimeSpan? heartbeat;
        var buffer = new byte[64 * 1024];
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock))
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct))
        {
            await SendAsync(socket, EwelinkSocketProtocol.Handshake(tokens, appId, now, sequence), handshake.Token);
            using var reply = JsonDocument.Parse(await ReadAsync(socket, buffer, handshake.Token));
            heartbeat = EwelinkSocketProtocol.Heartbeat(reply.RootElement, sequence, tokens.AccountKey!, clock.GetUtcNow());
        }
        authenticated();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sendGate = new SemaphoreSlim(1, 1);
        async ValueTask Send(string text, CancellationToken token)
        {
            await sendGate.WaitAsync(token);
            try { await SendAsync(socket, text, token); }
            finally { sendGate.Release(); }
        }
        var channel = activate is null ? null : new EwelinkActivationChannel(tokens.AccountKey!, clock, Send, now.ToUnixTimeMilliseconds());
        // Keep ACK handling independent from database ingestion, which can wait behind HTTP reconciliation.
        var reports = Channel.CreateBounded<Report>(new BoundedChannelOptions(128)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var read = ReceiveAsync(socket, tokens.AccountKey!, buffer, clock, reports.Writer, channel, session.Token);
        var process = ProcessAsync();
        var ping = HeartbeatAsync(Send, heartbeat, clock, session.Token);
        var identity = WatchIdentityAsync(tokens.AccessExpires, clock, checkIdentity, session.Token);
        var activation = channel is null ? Task.Delay(Timeout.InfiniteTimeSpan, clock, session.Token)
            : activate!(channel, session.Token);
        var tasks = new[] { read, process, ping, identity, activation };
        var first = await Task.WhenAny(tasks);
        await session.CancelAsync();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        await first;

        async Task ProcessAsync()
        {
            await foreach (var report in reports.Reader.ReadAllAsync(session.Token))
                await observed(report.Device, report.Parameters, report.At, session.Token);
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, string key, byte[] buffer, TimeProvider clock,
        ChannelWriter<Report> reports, EwelinkActivationChannel? channel,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReadAsync(socket, buffer, ct);
            if (message.Span.SequenceEqual("pong"u8)) continue;
            JsonDocument json;
            try { json = JsonDocument.Parse(message); }
            catch (JsonException) { continue; }
            using (json)
            {
                var data = json.RootElement;
                if (channel?.Receive(data) == true) continue;
                if (EwelinkSocketProtocol.DeviceEventTime(data, clock.GetUtcNow()) is { } at &&
                    EwelinkMapping.Text(data, "apikey", 256) == key &&
                    EwelinkMapping.Id(data, "deviceid") is { } id &&
                    EwelinkMapping.Node(data, "params") is { ValueKind: JsonValueKind.Object } parameters)
                    reports.TryWrite(new(id, parameters.Clone(), at));
            }
        }
    }

    private static async Task HeartbeatAsync(Func<string, CancellationToken, ValueTask> send, TimeSpan? interval,
        TimeProvider clock, CancellationToken ct)
    {
        if (interval is null) await Task.Delay(Timeout.InfiniteTimeSpan, clock, ct);
        else
        {
            using var timer = new PeriodicTimer(interval.Value, clock);
            while (await timer.WaitForNextTickAsync(ct)) await send("ping", ct);
        }
    }

    private static async Task WatchIdentityAsync(DateTimeOffset expires, TimeProvider clock,
        Func<CancellationToken, Task<bool>> check, CancellationToken ct)
    {
        while (await check(ct))
        {
            var remaining = expires - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, remaining.TotalSeconds)), clock, ct);
        }
    }

    private static ValueTask SendAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, ct);

    private static async Task<ReadOnlyMemory<byte>> ReadAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var fragment = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            if (fragment.MessageType == WebSocketMessageType.Close) throw new EwelinkFailure("socketClosed");
            if (fragment.MessageType != WebSocketMessageType.Text) break;
            length += fragment.Count;
            if (fragment.EndOfMessage) return buffer.AsMemory(0, length);
        }
        throw new EwelinkFailure("invalidSocketFrame");
    }
}

internal sealed class EwelinkActivationChannel(string key, TimeProvider clock, Func<string, CancellationToken, ValueTask> send,
    long handshakeSequence)
{
    private sealed record Request(string Device, string Sequence, TaskCompletionSource<int> Reply);
    private Request? pending;
    private long lastSequence = handshakeSequence;

    public async Task<Task<int>> SendAsync(string device, CancellationToken ct)
    {
        lastSequence = Math.Max(lastSequence + 1, clock.GetUtcNow().ToUnixTimeMilliseconds());
        var sequence = lastSequence.ToString(CultureInfo.InvariantCulture);
        var request = new Request(device, sequence, new(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref pending, request);
        await send(EwelinkSocketProtocol.ActivateElectricalReporting(key, device, sequence), ct);
        return request.Reply.Task;
    }

    public bool Receive(JsonElement data)
    {
        var request = Volatile.Read(ref pending);
        if (request is null || EwelinkMapping.Text(data, "sequence", 32) != request.Sequence ||
            EwelinkMapping.Node(data, "params").ValueKind != JsonValueKind.Undefined ||
            EwelinkMapping.Text(data, "userAgent", 16) == "device" ||
            EwelinkMapping.Integer(data, "error") is not { } code ||
            (EwelinkMapping.Node(data, "deviceid").ValueKind != JsonValueKind.Undefined &&
                EwelinkMapping.Id(data, "deviceid") != request.Device) ||
            (EwelinkMapping.Node(data, "apikey").ValueKind != JsonValueKind.Undefined &&
                EwelinkMapping.Text(data, "apikey", 256) != key))
            return false;
        return request.Reply.TrySetResult(code);
    }
}
