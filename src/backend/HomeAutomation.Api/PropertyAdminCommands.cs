using System.ComponentModel.DataAnnotations;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.Identity;

namespace HomeAutomation.Api;

internal static class PropertyAdminCommands
{
    public static async Task RunAsync(WebApplication app, bool create, CancellationToken ct)
    {
        var settings = app.Configuration.GetSection("PropertyAdmin");
        var email = settings["AccountEmail"]?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email))
            throw new InvalidOperationException("Set PropertyAdmin:AccountEmail to the exact sign-in email of the intended account.");

        await using var scope = app.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>().FindByNameAsync(email)
            ?? throw new InvalidOperationException("The confirmed account does not exist in the selected database.");
        var properties = scope.ServiceProvider.GetRequiredService<PropertyAdministration>();
        if (!create)
        {
            var updated = await properties.PromoteInitialOwnerAsync(account.Id, ct);
            if (updated) app.Logger.LogInformation("The sole membership of property 1 is now Owner.");
            else app.Logger.LogInformation("The confirmed account is already the sole Owner of property 1.");
            return;
        }

        var siteKey = settings["SiteKey"]?.Trim();
        var name = settings["Name"]?.Trim();
        var timeZone = settings["TimeZone"]?.Trim();
        if (string.IsNullOrEmpty(siteKey) || siteKey.Length > 64 || siteKey[0] == '-' || siteKey[^1] == '-' ||
            siteKey.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-'))
            throw new InvalidOperationException("Set PropertyAdmin:SiteKey to a unique key of 1 to 64 lowercase letters, digits, or interior hyphens.");
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new InvalidOperationException("Set PropertyAdmin:Name to a property name of 1 to 128 characters.");
        if (string.IsNullOrEmpty(timeZone) || timeZone.Length > 64 ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _) ||
            (timeZone != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _)))
            throw new InvalidOperationException("Set PropertyAdmin:TimeZone to an IANA time zone such as Europe/Sofia.");

        var property = await properties.CreateAsync(account.Id, siteKey, name, timeZone, ct);
        app.Logger.LogInformation("Created property {PropertyId} with the named creator as Owner.", property);
    }
}
