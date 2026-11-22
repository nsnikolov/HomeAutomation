using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeAutomation.Infrastructure.Tuya;

public sealed class TuyaOptions
{
    public string? AccessId { get; set; }
    public string? AccessSecret { get; set; }
    public string? UserId { get; set; }
    public string? DataCenter { get; set; }
    public string? OperatorEmail { get; set; }
    public int PollMinutes { get; set; } = 30;

    internal string? Host => DataCenter?.ToLowerInvariant() switch
    {
        "central-europe" => "https://openapi.tuyaeu.com",
        "western-europe" => "https://openapi-weaz.tuyaeu.com",
        "western-america" => "https://openapi.tuyaus.com",
        "eastern-america" => "https://openapi-ueaz.tuyaus.com",
        "china" => "https://openapi.tuyacn.com",
        "india" => "https://openapi.tuyain.com",
        "singapore" => "https://openapi-sg.iotbing.com",
        _ => null
    };
    public bool IsConfigured => Host is not null && TuyaJson.IsId(AccessId) && TuyaJson.IsId(UserId) &&
        !string.IsNullOrWhiteSpace(AccessSecret) && !string.IsNullOrWhiteSpace(OperatorEmail) && PollMinutes is >= 30 and <= 1440;
    public bool IsOperator(string? email) => IsConfigured && string.Equals(OperatorEmail, email, StringComparison.OrdinalIgnoreCase);
    internal string Scope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Host}\n{AccessId}\n{UserId}")));
}

public sealed class TuyaFailure(string status, int? providerCode = null, DateTimeOffset? retryAt = null) : Exception(status)
{
    public string Status { get; } = status;
    public int? ProviderCode { get; } = providerCode;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

internal sealed record TuyaToken(string Access, DateTimeOffset Expires);
internal sealed record TuyaInventory(IReadOnlyList<TuyaSeed> Devices, bool Partial);

// All session access runs under its gate, including a token replay and request spacing.
internal sealed class TuyaSession : SemaphoreSlim
{
    public TuyaSession() : base(1, 1) { }
    public TuyaToken? Token { get; set; }
    public long? LastRequest { get; set; }
}

internal sealed class TuyaClient(HttpClient http, TuyaOptions options, TimeProvider clock, TuyaSession session) : IDisposable
{
    private const int PageSize = 25;
    private const int MaxPages = 6;
    private const string TokenPath = "/v1.0/token?grant_type=1";
    private static readonly string EmptyBodyHash = Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty));

    internal static string Sign(string id, string secret, string? token, string time, string nonce, string path) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{id}{token}{time}{nonce}GET\n{EmptyBodyHash}\n\n{path}")));

    public async Task<TuyaInventory> DiscoverAsync(CancellationToken ct)
    {
        var devices = new Dictionary<string, TuyaSeed>(StringComparer.Ordinal);
        var partial = false;
        for (var page = 1; page <= MaxPages; page++)
        {
            var path = FormattableString.Invariant($"/v1.0/users/{options.UserId}/devices?page_no={page}&page_size={PageSize}");
            var list = await AuthorizedAsync(path, ct);
            if (list.ValueKind != JsonValueKind.Array) throw new TuyaFailure("invalidResponse");
            var added = 0;
            var now = clock.GetUtcNow();
            foreach (var item in list.EnumerateArray())
            {
                if (TuyaMapping.Device(item, now) is not { } device) partial = true;
                else if (devices.TryAdd(device.Id, device)) added++;
            }
            if (list.GetArrayLength() < PageSize) return new(devices.Values.ToArray(), partial);
            if (added == 0) break;
        }
        return new(devices.Values.ToArray(), true);
    }

    public async Task<TuyaPoint[]> SpecificationsAsync(string device, CancellationToken ct)
    {
        if (!TuyaJson.IsId(device)) throw new TuyaFailure("invalidResponse");
        var result = await AuthorizedAsync($"/v1.0/iot-03/devices/{device}/specification", ct);
        return TuyaMapping.Specifications(result);
    }

    private async Task<JsonElement> AuthorizedAsync(string path, CancellationToken ct)
    {
        var current = session.Token;
        var renewed = current is null || current.Expires <= clock.GetUtcNow().AddSeconds(30);
        if (renewed) current = await RenewAsync(ct);
        try { return await SendAsync(path, current!.Access, ct); }
        catch (TuyaFailure error) when (error.Status == "tokenRejected")
        {
            session.Token = null;
            if (renewed) throw new TuyaFailure("accessDenied", error.ProviderCode);
            current = await RenewAsync(ct);
            try { return await SendAsync(path, current.Access, ct); }
            catch (TuyaFailure retry) when (retry.Status == "tokenRejected")
            {
                session.Token = null;
                throw new TuyaFailure("accessDenied", retry.ProviderCode);
            }
        }
    }

    private async Task<TuyaToken> RenewAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var result = await SendAsync(TokenPath, null, ct);
        var access = TuyaJson.Text(result, "access_token", 4096);
        var expires = TuyaJson.Int(result, "expire_time");
        if (access is null || access.Any(char.IsWhiteSpace) || expires is not (> 0 and <= int.MaxValue))
            throw new TuyaFailure("invalidResponse");
        return session.Token = new(access, started.AddSeconds(expires.Value));
    }

    private async Task<JsonElement> SendAsync(string path, string? access, CancellationToken ct)
    {
        if (session.LastRequest is { } last && TimeSpan.FromSeconds(1) - clock.GetElapsedTime(last) is { Ticks: > 0 } delay)
            await Task.Delay(delay, clock, ct);
        session.LastRequest = clock.GetTimestamp();

        using var request = new HttpRequestMessage(HttpMethod.Get, options.Host + path);
        var time = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        request.Headers.Add("client_id", options.AccessId);
        request.Headers.Add("t", time);
        request.Headers.Add("nonce", nonce);
        request.Headers.Add("sign_method", "HMAC-SHA256");
        request.Headers.Add("sign", Sign(options.AccessId!, options.AccessSecret!, access, time, nonce, path));
        if (access is not null) request.Headers.Add("access_token", access);

        using var response = await http.SendAsync(request, ct);
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } duration ? clock.GetUtcNow() + duration : null);
        if (!response.IsSuccessStatusCode)
            throw response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => new TuyaFailure("rateLimited", retryAt: retryAt),
                HttpStatusCode.Unauthorized when access is not null => new TuyaFailure("tokenRejected"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new TuyaFailure("accessDenied"),
                HttpStatusCode.NotFound when access is not null => new TuyaFailure("deviceUnavailable"),
                _ => new TuyaFailure("unavailable", retryAt: retryAt)
            };
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var success = TuyaJson.Get(doc.RootElement, "success").ValueKind;
            if (success == JsonValueKind.False) throw Failure(TuyaJson.Int(doc.RootElement, "code"), retryAt);
            var result = TuyaJson.Get(doc.RootElement, "result");
            if (success != JsonValueKind.True || result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new TuyaFailure("invalidResponse");
            return result.Clone();
        }
        catch (JsonException) { throw new TuyaFailure("invalidResponse"); }
    }

    private static TuyaFailure Failure(int? code, DateTimeOffset? retryAt) => code switch
    {
        1010 or 1011 or 1012 or 1400 => new("tokenRejected", code),
        1001 or 1004 or 1005 or 1013 => new("authenticationFailed", code),
        1110 or 1199 => new("rateLimited", code, retryAt),
        28841002 or 28841102 => new("subscriptionExpired", code),
        28841004 or 28841104 => new("quotaExceeded", code, retryAt),
        1106 or 1114 or 2007 or 28841001 or 28841003 or 28841101 or 28841103 or 28841105 or 28841106 =>
            new("accessDenied", code),
        1000 => new("deviceUnavailable", code),
        _ => new("unavailable", code, retryAt)
    };

    public void Dispose() => http.Dispose();
}
