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
    public int PollMinutes { get; set; } = 5;
    public bool InventoryDiagnostics { get; set; }
    public bool EnableWrites { get; set; }
    public Dictionary<string, bool> RelayWriteDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !AppId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(AppSecret) && !string.IsNullOrWhiteSpace(OperatorEmail) &&
        PollMinutes is >= 5 and <= 1440 && Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) &&
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
    ILogger<EwelinkClient>? logger = null, IEwelinkStore? store = null)
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
        var duplicates = 0;
        var coveredHomes = 0;
        var devices = new Dictionary<string, EwelinkDevice>(StringComparer.Ordinal);
        var partial = homes.Length == 0 || homeIds.Any(id => id is null);
        if (options.InventoryDiagnostics)
            logger?.LogInformation("eWeLink inventory listed {ListedHomes} homes and will inspect up to {SelectedHomes} homes.",
                homeIds.Length, homes.Length);
        foreach (var home in homes)
        {
            if (requests == MaxInventoryRequests) break;
            coveredHomes++;
            var index = -9999999;
            var indexes = new HashSet<int>();
            int? reportedTotal = null;
            while (requests < MaxInventoryRequests)
            {
                var path = $"/v2/device/thing?familyid={Uri.EscapeDataString(home)}&num={PageSize}&beginIndex={index.ToString(CultureInfo.InvariantCulture)}";
                requests++;
                var data = await SendAsync(tokens.Region, path, tokens.Access, null, ct);
                var page = EwelinkMapping.Array(data, "thingList").EnumerateArray().ToArray();
                var observed = clock.GetUtcNow();
                var total = EwelinkMapping.Integer(data, "total");
                if (total is >= 0) reportedTotal = total;
                if (options.InventoryDiagnostics && logger is not null)
                    EwelinkInventoryDiagnostics.LogPage(logger, page, total);
                foreach (var item in page)
                {
                    if (EwelinkMapping.Integer(item, "index") is { } position) indexes.Add(position);
                    var device = EwelinkMapping.Map(item, observed);
                    if (device is null && EwelinkMapping.Integer(item, "itemType") is not (3 or 4)) partial = true;
                    if (device is null) continue;
                    if (devices.TryAdd(device.ExternalId, device)) continue;
                    duplicates++;
                    if (devices[device.ExternalId].Shared && !device.Shared) devices[device.ExternalId] = device;
                }
                var complete = reportedTotal is { } expected && indexes.Count >= expected;
                var next = indexes.Count == 0 ? index : indexes.Max();
                var stopReason = complete ? "reportedTotalReached"
                    : page.Length == 0 ? "emptyPage"
                    : reportedTotal is null && page.Length < PageSize ? "shortPageWithoutTotal"
                    : next <= index ? "cursorDidNotAdvance"
                    : requests == MaxInventoryRequests ? "requestBudget"
                    : null;
                if (stopReason is not null)
                {
                    partial |= !complete;
                    if (options.InventoryDiagnostics)
                        logger?.LogInformation("eWeLink inventory pagination stopped with {Reason} after {SeenIndexes} of {ReportedTotal} reported indexes.",
                            stopReason, indexes.Count, reportedTotal);
                    break;
                }
                index = next;
            }
        }
        if (coveredHomes < homes.Length)
        {
            partial = true;
            if (options.InventoryDiagnostics)
                logger?.LogInformation("eWeLink inventory reached the request budget before scanning all homes.");
        }
        if (options.InventoryDiagnostics)
            logger?.LogInformation("eWeLink inventory finished with {UniqueDevices} unique devices, {DuplicateRows} duplicate rows, {HomesScanned} of {Homes} homes scanned, and partial {Partial}.",
                devices.Count, duplicates, coveredHomes, homes.Length, partial);
        return new(devices.Values.ToArray(), partial);
    }

    public async Task WriteRelayAsync(EwelinkTokens tokens, string device, bool on, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = 1, id = device, @params = new { @switch = on ? "on" : "off" } });
        await SendAsync(tokens.Region, "/v2/device/thing/status", tokens.Access, body, ct, allowEmptyData: true);
    }

    public async Task<bool?> ReadRelayAsync(EwelinkTokens tokens, string device, CancellationToken ct)
    {
        var data = await SendAsync(tokens.Region, $"/v2/device/thing/status?type=1&id={Uri.EscapeDataString(device)}&params=switch",
            tokens.Access, null, ct);
        return EwelinkMapping.ReadRelay(EwelinkMapping.Node(data, "params"));
    }

    private async Task<JsonElement> SendAsync(string region, string path, string? token, byte[]? body, CancellationToken ct,
        bool allowEmptyData = false)
    {
        var host = Host(region) ?? throw new EwelinkFailure("unsupportedRegion");
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, host + path);
        request.Headers.Add("X-CK-Appid", options.AppId);
        request.Headers.Add("X-CK-Nonce", Nonce());
        request.Headers.Authorization = token is not null ? new("Bearer", token) : new("Sign", Sign(body!));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (store is not null) await store.ReserveRequestAsync(region, clock.GetUtcNow(), ct);
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
            if (response.IsSuccessStatusCode && providerCode == 0)
            {
                var data = EwelinkMapping.Node(json.RootElement, "data");
                if (data.ValueKind == JsonValueKind.Object) return data.Clone();
                if (allowEmptyData) return default;
            }
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
