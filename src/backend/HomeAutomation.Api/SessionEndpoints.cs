using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HomeAutomation.Infrastructure.MySql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace HomeAutomation.Api;

internal static class SessionEndpoints
{
    public static void MapSession(this WebApplication app)
    {
        var session = app.MapGroup("/api/v1/session");
        session.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(http).RequestToken }));
        session.MapGet("/", (ClaimsPrincipal user) => Results.Ok(new { email = user.Identity?.Name }))
            .RequireAuthorization();
        session.MapPost("/login", LoginAsync).RequireRateLimiting("session");
        session.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery, SignInManager<LocalUser> manager) =>
        {
            if (!await CsrfAsync(http, antiforgery)) return Results.BadRequest();
            await manager.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(Login command, HttpContext http, IAntiforgery antiforgery,
        SignInManager<LocalUser> manager)
    {
        if (!await CsrfAsync(http, antiforgery)) return Results.BadRequest();
        if (string.IsNullOrWhiteSpace(command.Email) || command.Email.Length > 254 ||
            string.IsNullOrEmpty(command.Password) || command.Password.Length > 512)
        {
            return Results.Unauthorized();
        }
        var result = await manager.PasswordSignInAsync(command.Email.Trim(), command.Password, false, lockoutOnFailure: true);
        return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
    }

    private static async Task<bool> CsrfAsync(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static async Task BootstrapAsync(IServiceProvider services, IConfiguration config, CancellationToken cancellationToken)
    {
        var email = config["Bootstrap:Email"]?.Trim();
        var password = config["Bootstrap:Password"];
        if (string.IsNullOrEmpty(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email) ||
            string.IsNullOrEmpty(password) || password.Length is < 14 or > 512)
        {
            throw new InvalidOperationException("Set a valid Bootstrap:Email and a strong Bootstrap:Password of 14 to 512 characters.");
        }

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = await users.FindByNameAsync(email);
        if (user is null)
        {
            user = new LocalUser { UserName = email };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Account provisioning failed: " + string.Join(", ", result.Errors.Select(item => item.Code)));
        }
        await scope.ServiceProvider.GetRequiredService<IdentityStore>().GrantDemoAccessAsync(user.Id, cancellationToken);
    }

    private sealed record Login(string? Email, string? Password);
}
