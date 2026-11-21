using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using static HomeAutomation.Infrastructure.Daikin.DaikinMapping;

namespace HomeAutomation.Infrastructure.Daikin;

internal sealed class DaikinClient(HttpClient http, DaikinOptions options, TimeProvider clock) : IDisposable
{
    private const string Identity = "https://idp.onecta.daikineurope.com/v1/oidc/";
    private const string Devices = "https://api.onecta.daikineurope.com/v1/gateway-devices";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Authorize(string state, string? challenge)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId, ["redirect_uri"] = options.RedirectUri,
            ["response_type"] = "code", ["scope"] = "openid onecta:basic.integration offline_access", ["state"] = state
        };
        if (challenge is not null)
        {
            parameters["code_challenge"] = challenge;
            parameters["code_challenge_method"] = "S256";
        }
        return QueryHelpers.AddQueryString(Identity + "authorize", parameters);
    }

    public async Task<DaikinTokens> TokenAsync(string grant, string value, string? verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grant, ["client_id"] = options.ClientId!, ["client_secret"] = options.ClientSecret!
        };
        if (grant == "authorization_code")
        {
            form["code"] = value;
            form["redirect_uri"] = options.RedirectUri!;
            if (verifier is not null) form["code_verifier"] = verifier;
        }
        else form["refresh_token"] = value;
        using var request = new HttpRequestMessage(HttpMethod.Post, Identity + "token") { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await ReadAsync<JsonElement>(response, ct);
            if (error.Node("error").Text() is "invalid_grant" or "invalid_client")
                throw new DaikinFailure("reauthorizationRequired");
        }
        Check(response);
        var data = await ReadAsync<JsonElement>(response, ct);
        var access = data.Node("access_token").Text();
        var refresh = data.Node("refresh_token").Text() ?? (grant == "refresh_token" ? value : null);
        var expires = data.Node("expires_in").Number();
        if (data.Node("token_type").Text()?.Equals("Bearer", StringComparison.OrdinalIgnoreCase) != true ||
            string.IsNullOrWhiteSpace(access) || access.Length > 7500 || access.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(refresh) || refresh.Length > 7500 ||
            expires is null or <= 0 or > 86400 * 365)
            throw new DaikinFailure("invalidResponse");
        return new DaikinTokens(access, refresh, clock.GetUtcNow().AddSeconds((double)expires.Value));
    }

    public async Task<IReadOnlyList<DaikinDevice>> DiscoverAsync(string token, CancellationToken ct)
    {
        using var request = Api(HttpMethod.Get, Devices, token);
        using var response = await http.SendAsync(request, ct);
        Check(response);
        return DaikinMapping.Discover(await ReadAsync<JsonElement>(response, ct), clock.GetUtcNow());
    }

    public async Task PatchAsync(string token, DaikinDevice device, DaikinChange change, CancellationToken ct)
    {
        var url = $"{Devices}/{Uri.EscapeDataString(device.Gateway)}/management-points/{Uri.EscapeDataString(device.Point)}/characteristics/{change.Characteristic}";
        using var request = Api(HttpMethod.Patch, url, token);
        request.Content = change.Path is null ? JsonContent.Create(new { value = change.Value }) :
            JsonContent.Create(new { path = change.Path, value = change.Value });
        using var response = await http.SendAsync(request, ct);
        Check(response);
    }

    private static HttpRequestMessage Api(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var retry = response.Headers.RetryAfter;
        throw new DaikinFailure(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "reauthorizationRequired",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.TooManyRequests => "rateLimited",
            _ when (int)response.StatusCode >= 500 => "unavailable",
            _ => "rejected"
        }, retry?.Date ?? (retry?.Delta is { } delay ? clock.GetUtcNow() + delay : null));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new DaikinFailure("invalidResponse"); }
        catch (JsonException) { throw new DaikinFailure("invalidResponse"); }
    }

    public void Dispose() => http.Dispose();
}

public sealed class DaikinFailure(string status, DateTimeOffset? retryAt = null) : Exception(status)
{
    public string Status { get; } = status;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
