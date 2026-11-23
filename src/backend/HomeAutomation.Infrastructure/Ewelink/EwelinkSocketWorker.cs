using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    ILogger<EwelinkSocketWorker> logger) : BackgroundService
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
                        token => CheckIdentityAsync(active.Identity, token), ct);
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
    public static async Task RunAsync(WebSocket socket, EwelinkTokens tokens, string appId, TimeProvider clock, Action authenticated,
        Func<string, JsonElement, DateTimeOffset, CancellationToken, Task> observed,
        Func<CancellationToken, Task<bool>> checkIdentity, CancellationToken ct)
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
        var read = ReceiveAsync(socket, tokens.AccountKey!, buffer, clock, observed, session.Token);
        var ping = HeartbeatAsync(socket, heartbeat, clock, session.Token);
        var identity = WatchIdentityAsync(tokens.AccessExpires, clock, checkIdentity, session.Token);
        var first = await Task.WhenAny(read, ping, identity);
        await session.CancelAsync();
        try { await Task.WhenAll(read, ping, identity); }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        await first;
    }

    private static async Task ReceiveAsync(WebSocket socket, string key, byte[] buffer, TimeProvider clock,
        Func<string, JsonElement, DateTimeOffset, CancellationToken, Task> observed, CancellationToken ct)
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
                if (EwelinkSocketProtocol.DeviceEventTime(data, clock.GetUtcNow()) is { } at &&
                    EwelinkMapping.Text(data, "apikey", 256) == key &&
                    EwelinkMapping.Id(data, "deviceid") is { } id &&
                    EwelinkMapping.Node(data, "params") is { ValueKind: JsonValueKind.Object } parameters)
                    await observed(id, parameters, at, ct);
            }
        }
    }

    private static async Task HeartbeatAsync(WebSocket socket, TimeSpan? interval, TimeProvider clock, CancellationToken ct)
    {
        if (interval is null) await Task.Delay(Timeout.InfiniteTimeSpan, clock, ct);
        else
        {
            using var timer = new PeriodicTimer(interval.Value, clock);
            while (await timer.WaitForNextTickAsync(ct)) await SendAsync(socket, "ping", ct);
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
