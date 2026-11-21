using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeAutomation.Application;

namespace HomeAutomation.Infrastructure.Growatt;

public sealed class GrowattCloudClient(HttpClient http, GrowattOptions options, TimeProvider clock) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    public async Task<int> CountPlantsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/plant/list?page=1&perpage=20");
        var (result, retryAfter) = await SendAsync<PlantResult>(request, cancellationToken);
        if (result.ErrorCode != 0)
            throw new GrowattReadException(result.ErrorCode switch
            {
                10008 or 10011 => GrowattFailure.Authorization,
                10001 => GrowattFailure.Unavailable,
                _ => GrowattFailure.InvalidData
            }, retryAfter);

        return result.Data is { Count: >= 0, Plants: not null } plants &&
            plants.Plants.Count == Math.Min(plants.Count.Value, 20) && plants.Plants.All(plant => plant is { Id: > 0 })
            ? plants.Count.Value
            : throw new GrowattReadException(GrowattFailure.InvalidData);
    }

    public async Task<int> CountStorageDevicesAsync(CancellationToken cancellationToken) =>
        (await DiscoverStorageDevicesAsync(cancellationToken)).Count;

    internal async Task<HashSet<string>> DiscoverStorageDevicesAsync(CancellationToken cancellationToken)
    {
        var storage = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            using var request = Post("queryDeviceList", new KeyValuePair<string, string>("page",
                page.ToString(CultureInfo.InvariantCulture)));
            var result = await ReadV4Async<DevicePage>(request, cancellationToken);
            if (result is not { Pages: >= 0, Count: >= 0, Data: not null } ||
                (result.Count > 0 && result.Pages < page))
                throw new GrowattReadException(GrowattFailure.InvalidData);

            foreach (var device in result.Data)
                if (device is { DeviceType: "storage" } &&
                    (!GrowattOptions.IsCloudSerial(device.DeviceSn) || !storage.Add(device.DeviceSn!)))
                    throw new GrowattReadException(GrowattFailure.InvalidData);

            if (page >= result.Pages) return storage;

            await Task.Delay(TimeSpan.FromSeconds(5), clock, cancellationToken);
        }
    }

    internal async Task<StorageData> ReadLatestAsync(CancellationToken cancellationToken)
    {
        using var request = Post("queryLastData", new("deviceType", "storage"),
            new("deviceSn", $"{options.MasterId},{options.SlaveId}"));
        return await ReadV4Async<StorageData>(request, cancellationToken);
    }

    private static HttpRequestMessage Post(string operation, params KeyValuePair<string, string>[] fields) =>
        new(HttpMethod.Post, $"v4/new-api/{operation}") { Content = new FormUrlEncodedContent(fields) };

    public void Dispose() => http.Dispose();

    private async Task<T> ReadV4Async<T>(HttpRequestMessage request, CancellationToken cancellationToken) where T : class
    {
        var (result, retryAfter) = await SendAsync<V4Response<T>>(request, cancellationToken);
        return result.Code switch
        {
            0 when result.Data is not null => result.Data,
            2 or 3 or 12 or 101 => throw new GrowattReadException(GrowattFailure.Authorization, retryAfter),
            100 or 102 => throw new GrowattReadException(GrowattFailure.Throttled, retryAfter),
            1 or 4 or 5 => throw new GrowattReadException(GrowattFailure.Unavailable, retryAfter),
            _ => throw new GrowattReadException(GrowattFailure.InvalidData)
        };
    }

    private async Task<(T Data, TimeSpan? RetryAfter)> SendAsync<T>(HttpRequestMessage request,
        CancellationToken cancellationToken) where T : class
    {
        if (!options.HasCloudToken())
            throw new GrowattReadException(GrowattFailure.Authorization);

        request.Headers.Add("token", options.ApiToken);
        using var response = await http.SendAsync(request, cancellationToken);
        var retryHeader = response.Headers.RetryAfter;
        TimeSpan? retryAfter = retryHeader?.Delta ?? (retryHeader?.Date - clock.GetUtcNow());
        if (!response.IsSuccessStatusCode)
            throw new GrowattReadException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GrowattFailure.Authorization,
                HttpStatusCode.TooManyRequests => GrowattFailure.Throttled,
                _ => GrowattFailure.Unavailable
            }, retryAfter);

        try
        {
            var data = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            return (data ?? throw new GrowattReadException(GrowattFailure.InvalidData), retryAfter);
        }
        catch (JsonException)
        {
            throw new GrowattReadException(GrowattFailure.InvalidData);
        }
    }

    private sealed record PlantResult([property: JsonPropertyName("error_code")] int? ErrorCode, PlantData? Data);
    private sealed record PlantData(int? Count, IReadOnlyList<Plant>? Plants);
    private sealed record Plant([property: JsonPropertyName("plant_id")] long? Id);
    private sealed record V4Response<T>(int? Code, T? Data);

    internal sealed record DevicePage(int? Pages, int? Count, IReadOnlyList<Device>? Data);
    internal sealed record Device(string? DeviceType, string? DeviceSn);
    internal sealed record StorageData(IReadOnlyList<StorageReading>? Storage);

    internal sealed record StorageReading
    {
        public string? SerialNum { get; init; }
        public string? Time { get; init; }
        public double? Ppv { get; init; }
        public double? Vpv { get; init; }
        public double? VGrid { get; init; }
        public double? FreqGrid { get; init; }
        public double? OutPutPower { get; init; }
        public double? VBat { get; init; }
        public double? Capacity { get; init; }
        public int? FaultCode { get; init; }
        public int? ErrorCode { get; init; }
        public int? WarnCode { get; init; }
        public bool? Lost { get; init; }
        public string? Spf5000StatusText { get; init; }
    }
}
