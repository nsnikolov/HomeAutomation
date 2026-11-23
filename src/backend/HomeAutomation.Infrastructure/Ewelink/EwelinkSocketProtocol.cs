using System.Globalization;
using System.Text.Json;

namespace HomeAutomation.Infrastructure.Ewelink;

internal static class EwelinkSocketProtocol
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(2);

    public static Uri Address(JsonElement dispatch)
    {
        var host = EwelinkMapping.Text(dispatch, "domain", 253);
        var port = EwelinkMapping.Integer(dispatch, "port");
        if (host is null || Uri.CheckHostName(host) != UriHostNameType.Dns ||
            !(host.EndsWith(".coolkit.cc", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".coolkit.cn", StringComparison.OrdinalIgnoreCase)) ||
            port is not (>= 1 and <= 65535))
            throw new EwelinkFailure("invalidSocketAddress");
        return new UriBuilder("wss", host, port.Value, "/api/ws").Uri;
    }

    public static string Handshake(EwelinkTokens tokens, string appId, DateTimeOffset now, string sequence) =>
        JsonSerializer.Serialize(new
        {
            action = "userOnline", version = 8, at = tokens.Access, apikey = tokens.AccountKey, appid = appId,
            nonce = EwelinkClient.Nonce(), ts = now.ToUnixTimeSeconds(), userAgent = "app", sequence
        });

    public static string ActivateElectricalReporting(string key, string device, string sequence) =>
        JsonSerializer.Serialize(new
        {
            action = "update", apikey = key, deviceid = device, userAgent = "app", sequence,
            @params = new { uiActive = EwelinkElectricalReporting.ActivationSeconds }
        });

    public static TimeSpan? Heartbeat(JsonElement reply, string sequence, string key, DateTimeOffset now)
    {
        var error = EwelinkMapping.Integer(reply, "error");
        if (EwelinkMapping.Text(reply, "sequence", 32) != sequence || error is null)
            throw new EwelinkFailure("invalidSocketHandshake");
        if (error != 0)
            throw new EwelinkFailure("socketHandshakeRejected", now.AddMinutes(error is 401 or 402 or 429 ? 60 : 1440))
                { ProviderCode = error };
        if (EwelinkMapping.Text(reply, "apikey", 256) != key)
            throw new EwelinkFailure("invalidSocketHandshake");
        var config = EwelinkMapping.Node(reply, "config");
        if (EwelinkMapping.Integer(config, "hb") == 0) return null;
        var interval = EwelinkMapping.Integer(config, "hbInterval");
        if (interval is not null and not (>= 1 and <= 600))
            throw new EwelinkFailure("invalidSocketHandshake");
        return TimeSpan.FromSeconds(interval is { } seconds ? seconds + 7 : 90);
    }

    public static DateTimeOffset? DeviceEventTime(JsonElement message, DateTimeOffset received, EwelinkSocketTrace? trace = null)
    {
        if (EwelinkMapping.Text(message, "action", 16) != "update")
        {
            trace?.Count("reject.action");
            return null;
        }
        if (EwelinkMapping.Text(message, "userAgent", 16) != "device")
        {
            trace?.Count("reject.sender");
            return null;
        }
        var sequence = EwelinkMapping.Node(message, "d_seq");
        long milliseconds;
        if (sequence.ValueKind != JsonValueKind.Undefined
                ? sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out milliseconds)
                : !long.TryParse(EwelinkMapping.Text(message, "sequence", 16), NumberStyles.None,
                    CultureInfo.InvariantCulture, out milliseconds))
        {
            trace?.Count("reject.timestampMissingOrInvalid");
            return null;
        }
        if (milliseconds < (received - Freshness).ToUnixTimeMilliseconds())
        {
            trace?.Count(milliseconds < 1_000_000_000_000 ? "reject.timestampNotEpochMilliseconds" : "reject.timestampStale");
            return null;
        }
        if (milliseconds > received.ToUnixTimeMilliseconds())
        {
            trace?.Count("reject.timestampFuture");
            return null;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public static TimeSpan ReconnectDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << Math.Clamp(failures - 1, 0, 6))));
}
