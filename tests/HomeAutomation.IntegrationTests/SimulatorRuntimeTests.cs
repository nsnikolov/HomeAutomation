using HomeAutomation.Api;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.Simulator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HomeAutomation.IntegrationTests;

/// <summary>Runtime simulation is opt-in: a host built from the shipped configuration never polls, persists or broadcasts it.</summary>
public sealed class SimulatorRuntimeTests
{
    [Fact]
    public void ShippedConfigurationAndOptionsKeepTheSimulatorOff()
    {
        Assert.False(new SimulatorOptions().Enabled);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HomeAutomation.slnx"))) directory = directory.Parent;
        var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory!.FullName, "src", "backend", "HomeAutomation.Api",
            "appsettings.json"), optional: false).Build();
        Assert.Equal("false", settings["Simulator:Enabled"], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostWithTheDefaultSettingsNeverReadsOrBroadcastsSimulatedData()
    {
        var provider = new CountingProvider();
        await using var host = new DefaultHost(provider);
        using var client = host.CreateClient();
        Assert.False(host.Services.GetRequiredService<SimulatorOptions>().Enabled);
        var worker = host.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "TelemetryWorker");
        await Task.Delay(200);
        Assert.True(worker.ExecuteTask?.IsCompletedSuccessfully);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(new[] { "--cleanup-simulator-data", "--simulator-property=4" }, 4L, false)]
    [InlineData(new[] { "--cleanup-simulator-data", "--simulator-property=4", "--execute", "--confirm-simulator-data-deletion" }, 4L, true)]
    public void CleanupIsADryRunUnlessBothDeletionFlagsAreGiven(string[] args, long property, bool execute)
    {
        var options = SimulatorCleanupCommand.Parse(args)!;
        Assert.Equal((property, execute), (options.PropertyId, options.Execute));
    }

    [Theory]
    [InlineData("--cleanup-simulator-data", "--simulator-property=4", "--execute")]
    [InlineData("--cleanup-simulator-data", "--simulator-property=4", "--confirm-simulator-data-deletion")]
    [InlineData("--cleanup-simulator-data")]
    [InlineData("--cleanup-simulator-data", "--simulator-property=0")]
    [InlineData("--cleanup-simulator-data", "--simulator-property=x")]
    [InlineData("--simulator-property=4")]
    [InlineData("--confirm-simulator-data-deletion")]
    public void CleanupRejectsIncompleteOrAmbiguousArguments(params string[] args) =>
        Assert.Throws<SimulatorCleanupArgumentException>(() => SimulatorCleanupCommand.Parse(args));

    [Fact]
    public void OtherCommandsMayShareTheExecuteFlagWithoutTriggeringCleanup()
    {
        Assert.Null(SimulatorCleanupCommand.Parse(["--initialize-logical-devices", "--logical-property=1", "--execute"]));
        Assert.Null(SimulatorCleanupCommand.Parse([]));
    }

    private sealed class CountingProvider : IObservationProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);
        }

        public Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("The simulator must not run.");
        }
    }

    /// <summary>The synthetic API host with every setting the shipped configuration provides, including Simulator:Enabled.</summary>
    private sealed class DefaultHost(IObservationProvider provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var sources = new Dictionary<string, string?>();
                foreach (var name in new[] { "growatt", "daikin", "ewelink", "tuya" })
                {
                    sources[$"ConfigurationVault:Providers:{name}:Source"] = "Legacy";
                    sources[$"ConfigurationVault:Providers:{name}:PropertyId"] = null;
                }
                configuration.AddInMemoryCollection(sources);
            });
            builder.UseSetting("Database:Host", "127.0.0.1");
            builder.UseSetting("Database:Port", "3306");
            builder.UseSetting("Database:Name", "homeautomation_test");
            builder.UseSetting("Database:User", "test");
            builder.UseSetting("Database:Password", "test-configuration-only");
            builder.UseSetting("TelemetryHistory:RollupsEnabled", "false");
            builder.UseSetting("LogicalDevices:AutoProvision", "false");
            builder.UseSetting("Growatt:Enabled", "false");
            builder.UseSetting("Daikin:ClientId", "");
            builder.UseSetting("Daikin:ClientSecret", "");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObservationProvider>();
                services.AddSingleton(provider);
            });
        }
    }
}
