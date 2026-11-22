using System.Text.Json;
using HomeAutomation.Infrastructure.Daikin;

namespace HomeAutomation.UnitTests;

public sealed class DaikinConfirmationTests
{
    [Fact]
    public async Task LatestAcceptanceResetsTheSharedWindowAndTheBatchCanOnlyBeClaimedOnce()
    {
        var clock = new ManualClock();
        var schedule = new DaikinConfirmationSchedule(clock);
        schedule.Schedule("power-first-device");
        clock.Advance(10);
        schedule.Schedule("temperature-first-device");
        clock.Advance(10);
        schedule.Schedule("fan-second-device");
        clock.Advance(10);
        Assert.Empty(schedule.TakeDue());
        clock.Advance(19);
        Assert.Empty(schedule.TakeDue());

        clock.Advance(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await schedule.WaitUntilDueAsync(timeout.Token);
        var batch = schedule.TakeDue();
        Assert.Equal(3, batch.Length);
        Assert.Contains("power-first-device", batch);
        Assert.Contains("temperature-first-device", batch);
        Assert.Contains("fan-second-device", batch);
        Assert.Empty(schedule.TakeDue());
    }

    [Fact]
    public void ARegularPollCancelsOnlyTheCommandsItConfirms()
    {
        var clock = new ManualClock();
        var schedule = new DaikinConfirmationSchedule(clock);
        schedule.Schedule("power");
        schedule.Schedule("target");
        clock.Advance(15);
        schedule.Resolve("power");
        clock.Advance(15);
        Assert.Equal(["target"], schedule.TakeDue());

        schedule.Schedule("mode");
        clock.Advance(15);
        schedule.Resolve("mode");
        clock.Advance(15);
        Assert.Empty(schedule.TakeDue());
    }

    [Theory]
    [InlineData("awaitingConfirmation", "power", "temperature", false)]
    [InlineData("awaitingConfirmation", "temperature", "fan", false)]
    [InlineData("awaitingConfirmation", "power", "power", true)]
    [InlineData("awaitingConfirmation", "temperature", "mode", true)]
    [InlineData("awaitingConfirmation", "mode", "fan", true)]
    [InlineData("uncertain", "power", "temperature", true)]
    [InlineData("dispatched", "power", "temperature", true)]
    [InlineData("pending", "power", "temperature", true)]
    public void AdmissionAllowsOnlyIndependentChangesAfterAnAcceptedPatch(string status, string existing,
        string requested, bool blocked)
    {
        var open = new DaikinOutcome("fixture", existing, "on", status, DateTimeOffset.UnixEpoch);
        Assert.Equal(blocked, DaikinCommandStatus.Blocks(open, requested));
    }

    [Fact]
    public void OneDiscoveryCanPartiallyConfirmAcrossDevicesAndCapabilities()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 50, TimeSpan.Zero);
        using var json = JsonDocument.Parse("""
            [{"_id":"first","type":"dx4","deviceModel":"dx4","isCloudConnectionUp":{"value":true},"managementPoints":[
              {"embeddedId":"indoor","managementPointType":"indoorUnit"},
              {"embeddedId":"climate","managementPointType":"climateControl",
               "onOffMode":{"settable":true,"values":["on","off"],"value":"on"},
               "operationMode":{"value":"heating"},
               "temperatureControl":{"value":{"operationModes":{"heating":{"setpoints":{"roomTemperature":
                   {"settable":true,"unit":"C","minValue":16,"maxValue":25,"stepValue":1,"value":19}}}}}}}]},
             {"_id":"second","type":"dx4","deviceModel":"dx4","isCloudConnectionUp":{"value":true},"managementPoints":[
              {"embeddedId":"indoor","managementPointType":"indoorUnit"},
              {"embeddedId":"climate","managementPointType":"climateControl",
               "onOffMode":{"settable":true,"values":["on","off"],"value":"off"}}]}]
            """);
        var devices = DaikinMapping.Discover(json.RootElement, now);
        var first = devices.Single(device => device.Gateway == "first");
        var second = devices.Single(device => device.Gateway == "second");
        var dispatched = now.AddSeconds(-30);
        Assert.True(DaikinService.MatchesCommand(first, new("a", "power", "on", DaikinCommandStatus.Awaiting, dispatched)));
        Assert.False(DaikinService.MatchesCommand(first, new("b", "temperature", "20", DaikinCommandStatus.Awaiting, dispatched, "heating")));
        Assert.True(DaikinService.MatchesCommand(second, new("c", "power", "off", DaikinCommandStatus.Awaiting, dispatched)));
    }

    [Fact]
    public void ExhaustedBackgroundQuotaLeavesTheBatchForNormalPollingWithoutAnotherAttempt()
    {
        var clock = new ManualClock();
        var schedule = new DaikinConfirmationSchedule(clock);
        schedule.Schedule("accepted");
        clock.Advance(30);
        Assert.Equal(["accepted"], schedule.TakeDue());
        var options = new DaikinOptions();
        var requests = Enumerable.Repeat(DateTime.UnixEpoch, options.DailyLimit).ToArray();
        Assert.NotNull(DaikinStore.BudgetRetryAt(requests, options.DailyLimit, options.ManualReserve, background: true));
        clock.Advance(60);
        Assert.Empty(schedule.TakeDue());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
