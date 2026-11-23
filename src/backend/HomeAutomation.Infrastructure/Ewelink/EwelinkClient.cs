using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed class EwelinkOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string? RedirectUri { get; set; }
    public string? OperatorEmail { get; set; }
    public int PollMinutes { get; set; } = 30;
    public bool InventoryDiagnostics { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !AppId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(AppSecret) && !string.IsNullOrWhiteSpace(OperatorEmail) &&
        PollMinutes is >= 15 and <= 1440 && Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.AbsolutePath == "/api/v1/ewelink/callback" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public bool IsOperator(string? email) => IsConfigured && string.Equals(email, OperatorEmail, StringComparison.OrdinalIgnoreCase);
}

public sealed class EwelinkFailure(string status, DateTimeOffset? retryAt = null) : Exception(status)
{
    public string Status { get; } = status;
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public int? HttpStatus { get; init; }
    public int? ProviderCode { get; init; }
}

internal sealed record EwelinkTokens(string Region, string Access, string Refresh, DateTimeOffset AccessExpires, DateTimeOffset RefreshExpires);
internal sealed record EwelinkInventory(IReadOnlyList<EwelinkDevice> Devices, bool Partial);

internal sealed class EwelinkPace(TimeProvider clock)
{
    private long? last;

    // All requests are serialized by the integration gate.
    public async Task WaitAsync(CancellationToken ct)
    {
        if (last is { } previous && TimeSpan.FromSeconds(1) - clock.GetElapsedTime(previous) is { Ticks: > 0 } delay)
            await Task.Delay(delay, clock, ct);
        last = clock.GetTimestamp();
    }
}

internal sealed class EwelinkClient(HttpClient http, EwelinkOptions options, TimeProvider clock, EwelinkPace pace,
    ILogger<EwelinkClient>? logger = null)
{
    private const string AuthorizePage = "https://c2ccdn.coolkit.cc/oauth/index.html";
    private const int PageSize = 30;
    private const int MaxInventoryRequests = 12;

    public static string? Host(string? region) => region switch
    {
        "eu" => "https://eu-apia.coolkit.cc",
        "us" => "https://us-apia.coolkit.cc",
        "as" => "https://as-apia.coolkit.cc",
        "cn" => "https://cn-apia.coolkit.cn",
        _ => null
    };

    public string Authorize(string state)
    {
        var seq = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return QueryHelpers.AddQueryString(AuthorizePage, new Dictionary<string, string?>
        {
            ["clientId"] = options.AppId, ["seq"] = seq, ["authorization"] = Sign(Encoding.UTF8.GetBytes($"{options.AppId}_{seq}")),
            ["redirectUrl"] = options.RedirectUri, ["grantType"] = "authorization_code",
            ["state"] = state, ["nonce"] = Nonce(), ["showQRCode"] = "false"
        });
    }

    public async Task<EwelinkTokens> ExchangeAsync(string code, string region, CancellationToken ct)
    {
        var data = await SendAsync(region, "/v2/user/oauth/token", null,
            JsonSerializer.SerializeToUtf8Bytes(new { code, redirectUrl = options.RedirectUri, grantType = "authorization_code" }), ct);
        return new(region, Token(data, "accessToken"), Token(data, "refreshToken"),
            Expiration(data, "atExpiredTime"), Expiration(data, "rtExpiredTime"));
    }

    public async Task<EwelinkTokens> RefreshAsync(EwelinkTokens tokens, CancellationToken ct)
    {
        if (tokens.RefreshExpires <= clock.GetUtcNow()) throw new EwelinkFailure("reauthorizationRequired");
        var data = await SendAsync(tokens.Region, "/v2/user/refresh", null,
            JsonSerializer.SerializeToUtf8Bytes(new { rt = tokens.Refresh }), ct);
        var now = clock.GetUtcNow();
        return new(tokens.Region, Token(data, "at"), Token(data, "rt"), now.AddDays(30), now.AddDays(60));
    }

    public async Task<EwelinkInventory> DiscoverAsync(EwelinkTokens tokens, CancellationToken ct)
    {
        var families = await SendAsync(tokens.Region, "/v2/family", tokens.Access, null, ct);
        var homeIds = EwelinkMapping.Array(families, "familyList").EnumerateArray().Select(home => EwelinkMapping.Id(home, "id")).ToArray();
        var current = EwelinkMapping.Id(families, "currentFamilyId");
        var homes = homeIds.Append(current).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var requests = 1;
        var devices = new Dictionary<string, EwelinkDevice>(StringComparer.Ordinal);
        var partial = homes.Length == 0 || homeIds.Any(id => id is null);
        if (options.InventoryDiagnostics)
            logger?.LogInformation("eWeLink inventory listed {ListedHomes} homes and will inspect up to {SelectedHomes} homes.",
                homeIds.Length, homes.Length);
        foreach (var home in homes)
        {
            var index = -9999999;
            var indexes = new HashSet<int>();
            while (requests < MaxInventoryRequests)
            {
                var path = $"/v2/device/thing?familyid={Uri.EscapeDataString(home)}&num={PageSize}&beginIndex={index.ToString(CultureInfo.InvariantCulture)}";
                requests++;
                var data = await SendAsync(tokens.Region, path, tokens.Access, null, ct);
                var page = EwelinkMapping.Array(data, "thingList").EnumerateArray().ToArray();
                var observed = clock.GetUtcNow();
                var total = EwelinkMapping.Integer(data, "total");
                if (options.InventoryDiagnostics && logger is not null)
                    EwelinkInventoryDiagnostics.LogPage(logger, page, total);
                foreach (var item in page)
                {
                    if (EwelinkMapping.Integer(item, "index") is { } position) indexes.Add(position);
                    var device = EwelinkMapping.Map(item, observed);
                    if (device is null && EwelinkMapping.Integer(item, "itemType") is not (3 or 4)) partial = true;
                    if (device is not null && (!devices.TryGetValue(device.ExternalId, out var prior) || prior.Shared && !device.Shared))
                        devices[device.ExternalId] = device;
                }
                var hasTotal = total is >= 0 && indexes.Count >= total.Value;
                if (hasTotal || page.Length < PageSize)
                {
                    partial |= !hasTotal;
                    break;
                }
                var next = indexes.Count == 0 ? index : indexes.Max();
                if (next <= index || requests == MaxInventoryRequests)
                {
                    partial = true;
                    break;
                }
                index = next;
            }
            if (requests == MaxInventoryRequests && home != homes[^1]) partial = true;
        }
        return new(devices.Values.ToArray(), partial);
    }

    private async Task<JsonElement> SendAsync(string region, string path, string? token, byte[]? body, CancellationToken ct)
    {
        var host = Host(region) ?? throw new EwelinkFailure("unsupportedRegion");
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, host + path);
        request.Headers.Add("X-CK-Appid", options.AppId);
        request.Headers.Add("X-CK-Nonce", Nonce());
        request.Headers.Authorization = body is null ? new("Bearer", token) : new("Sign", Sign(body));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        await pace.WaitAsync(ct);
        using var response = await http.SendAsync(request, ct);
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } delta ? clock.GetUtcNow() + delta : null);
        var httpStatus = (int)response.StatusCode;
        int? providerCode = null;
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            providerCode = EwelinkMapping.Integer(json.RootElement, "error");
            if (response.IsSuccessStatusCode && providerCode == 0 &&
                EwelinkMapping.Node(json.RootElement, "data") is { ValueKind: JsonValueKind.Object } data)
                return data.Clone();
        }
        catch (JsonException)
        {
            // A non-JSON gateway response still has an HTTP status.
        }

        var failure = !response.IsSuccessStatusCode
            ? Failure(httpStatus, retryAt, response.StatusCode == HttpStatusCode.Forbidden)
            : providerCode is { } error && error != 0 ? Failure(error, retryAt) : new EwelinkFailure("invalidResponse");
        throw new EwelinkFailure(failure.Status, failure.RetryAt)
        {
            HttpStatus = httpStatus,
            ProviderCode = providerCode
        };
    }

    private EwelinkFailure Failure(int code, DateTimeOffset? retryAt, bool httpForbidden = false) => code switch
    {
        401 or 402 => new("reauthorizationRequired"),
        412 => new("quotaExceeded", retryAt ?? clock.GetUtcNow().AddDays(1)),
        429 => new("rateLimited", retryAt ?? clock.GetUtcNow().AddHours(1)),
        403 when httpForbidden => new("accessOrQuotaLimited", retryAt ?? clock.GetUtcNow().AddDays(1)),
        403 or 406 or 407 => new("accessDenied", retryAt ?? clock.GetUtcNow().AddDays(1)),
        >= 500 and < 600 => new("unavailable", retryAt),
        _ => new("rejected")
    };

    private string Sign(byte[] bytes) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.AppSecret!), bytes));
    private static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    private static string Token(JsonElement data, string name) => EwelinkMapping.Text(data, name, 4096) is { Length: > 0 } token &&
        !token.Any(char.IsWhiteSpace) ? token : throw new EwelinkFailure("invalidResponse");

    private DateTimeOffset Expiration(JsonElement data, string name)
    {
        var value = EwelinkMapping.Node(data, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms) &&
            ms > clock.GetUtcNow().ToUnixTimeMilliseconds() && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        throw new EwelinkFailure("invalidResponse");
    }
}
