using System.Collections.Concurrent;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeAutomation.IntegrationTests;

public sealed class ProviderStartupDiagnosticsTests
{
    [Fact]
    public void LegacyStartupLogsExactlyOneRedactedSummaryPerProvider()
    {
        const string fixture = "startup-fixture-token-that-must-not-be-logged";
        using var capture = new StartupLogger();
        using var host = new ApiTests.Host().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Growatt:ApiToken", fixture);
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(capture));
        });
        using var client = host.CreateClient();
        Assert.Equal(4, capture.Entries.Count);
        Assert.Equal(new[] { "daikin", "ewelink", "growatt", "tuya" },
            capture.Entries.Select(entry => (string)entry["Provider"]!).Order().ToArray());
        Assert.All(capture.Entries, entry =>
        {
            Assert.Equal("Legacy", entry["Source"]);
            Assert.Equal("Global", entry["Scope"]);
            Assert.Contains((string)entry["ValidationStatus"]!, new[] { "Disabled", "NotConfiguredOrInvalid" });
            Assert.Equal(Environment.ProcessId, entry["ProcessId"]);
            Assert.DoesNotContain(fixture, string.Join(", ", entry.Values));
        });
    }

    private sealed class StartupLogger : ILoggerProvider, ILogger
    {
        public ConcurrentBag<Dictionary<string, object?>> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (id.Id == 6100 && state is IEnumerable<KeyValuePair<string, object?>> fields)
                Entries.Add(fields.ToDictionary());
        }
    }
}
