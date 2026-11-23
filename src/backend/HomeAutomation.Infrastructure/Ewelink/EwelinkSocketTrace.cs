using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.Infrastructure.Ewelink;

internal sealed class EwelinkSocketTrace
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    // Counter names come only from code and the closed JsonValueKind enum.
    public void Count(string stage, int amount = 1) => counts.AddOrUpdate(stage, amount, (_, count) => count + amount);

    public void Shape(JsonElement message, JsonElement parameters)
    {
        Count("electricalFrames");
        foreach (var key in EwelinkMapping.ElectricalFields)
        {
            var kind = EwelinkMapping.Node(parameters, key).ValueKind;
            if (kind != JsonValueKind.Undefined) Count($"field.{key}.{kind}");
        }
        foreach (var key in new[] { "d_seq", "sequence", "ts", "userAgent", "from", "apikey" })
            Count($"envelope.{key}.{EwelinkMapping.Node(message, key).ValueKind}");
        foreach (var key in new[] { "userAgent", "from" })
        {
            var source = EwelinkMapping.Text(message, key, 16);
            if (source is "device" or "app") Count($"sender.{key}.{source}");
        }
    }

    internal string Drain()
    {
        var snapshot = new List<string>();
        foreach (var key in counts.Keys.Order(StringComparer.Ordinal))
            if (counts.TryRemove(key, out var count)) snapshot.Add($"{key}={count}");
        return string.Join(", ", snapshot);
    }

    public void Flush(ILogger logger)
    {
        var summary = Drain();
        if (summary.Length > 0) logger.LogInformation("eWeLink WebSocket trace {Counters}.", summary);
    }

    public async Task ReportAsync(ILogger logger, TimeProvider clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Flush(logger);
        }
        finally { Flush(logger); }
    }
}
