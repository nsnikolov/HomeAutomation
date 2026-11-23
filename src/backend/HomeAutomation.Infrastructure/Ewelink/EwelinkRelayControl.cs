using HomeAutomation.Domain;
using static HomeAutomation.Infrastructure.MySql.Database;

namespace HomeAutomation.Infrastructure.Ewelink;

public sealed partial class EwelinkService
{
    public async Task<EwelinkRelayOutcome> CommandRelayAsync(string actor, long property, string id,
        EwelinkRelayCommand command, CancellationToken ct)
    {
        if (!options.EnableWrites || !options.RelayWriteDevices.GetValueOrDefault(id) ||
            !await permissions.CanReadAsync(actor, property, ct)) throw new EwelinkFailure("forbidden");
        if (command.On is not { } on) throw new EwelinkFailure("invalidCommand");
        if (!await gate.WaitAsync(0, ct)) throw new EwelinkFailure("commandPending");

        try
        {
            var account = await store.AccountAsync(ct);
            if (account?.Owner != actor) throw new EwelinkFailure("forbidden");
            if (account.Envelope is null || account.Status == "reauthorizationRequired")
                throw new EwelinkFailure("relayUnavailable");
            var now = clock.GetUtcNow();
            if (account.Status is not ("connected" or "partial"))
                throw new EwelinkFailure(account.Status is "rateLimited" or "quotaExceeded" or "accessOrQuotaLimited"
                    ? account.Status : "relayUnavailable", Utc(account.NextSync));

            var row = (await store.DevicesAsync(actor, ct)).SingleOrDefault(item => item.Id == id && item.PropertyId == property)
                ?? throw new EwelinkFailure("notFound");
            var device = row.Device;
            CheckRelayCommand(device, on, command.ConfirmedProtected, now, TimeSpan.FromMinutes(options.PollMinutes * 2 + 5));
            var tokens = vault.Open(actor, options.AppId!, account.Envelope);
            if (tokens.AccessExpires <= now.AddSeconds(30))
                throw new EwelinkFailure("relayUnavailable");

            var audit = await relayAudit.BeginAsync(actor, id, property, on, now, ct);
            return await DispatchRelayAsync(actor, property, row, device, tokens, on, audit);
        }
        finally { gate.Release(); }
    }

    internal static void CheckRelayCommand(EwelinkDevice device, bool on, bool confirmed, DateTimeOffset now, TimeSpan freshness)
    {
        if (!device.SupportsRelayWrites) throw new EwelinkFailure("unsupportedCommand");
        if (device.RequiresRelayConfirmation && !confirmed) throw new EwelinkFailure("relayConfirmationRequired");
        if (device.Connectivity != Connectivity.Online || device.Relay is null || device.RelayAt < device.SeenAt ||
            Telemetry.Quality(device.SeenAt, now, freshness) != ReadingQuality.Fresh ||
            Telemetry.Quality(device.RelayAt, now, freshness) != ReadingQuality.Fresh)
            throw new EwelinkFailure("relayUnavailable");
        if (device.Relay == on) throw new EwelinkFailure("relayAlreadyInState");
    }

    private async Task<EwelinkRelayOutcome> DispatchRelayAsync(string actor, long property, EwelinkRow row, EwelinkDevice device,
        EwelinkTokens tokens, bool on, string audit)
    {
        var status = "unverified";
        var accepted = false;
        try
        {
            using var dispatch = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
            await client.WriteRelayAsync(tokens, row.ExternalId, on, dispatch.Token);
            accepted = true;
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
            if (await client.ReadRelayAsync(tokens, row.ExternalId, verification.Token) is { } actual)
            {
                var observed = device with { Relay = actual, RelayAt = clock.GetUtcNow() };
                await IngestAsync(property, row.RoomId, observed, includeRelay: true, CancellationToken.None);
                await store.SaveAsync(actor, observed, CancellationToken.None);
                if (actual == on) status = "confirmed";
            }
        }
        catch (EwelinkFailure error)
        {
            if (!accepted && error.Status is not ("unavailable" or "invalidResponse") &&
                error.HttpStatus is not (408 or (>= 300 and < 400)) && error.ProviderCode != 408)
                status = "failed";
            if (error.RetryAt is not null || error.Status is "reauthorizationRequired" or "unavailable")
                await ReportFailureAsync(actor, error, CancellationToken.None);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            await ReportFailureAsync(actor, new EwelinkFailure("unavailable"), CancellationToken.None);
        }
        finally
        {
            await relayAudit.FinishAsync(audit, status, status == "unverified" ? null : clock.GetUtcNow(), CancellationToken.None);
        }
        return new(audit, status, status == "unverified");
    }
}
