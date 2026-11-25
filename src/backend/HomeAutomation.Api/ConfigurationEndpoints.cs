using System.Net;
using System.Text.Json;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace HomeAutomation.Api;

internal static class ConfigurationEndpoints
{
    public static void MapConfiguration(this WebApplication app)
    {
        var root = app.MapGroup("/api/v1/configuration").RequireAuthorization();
        root.MapGet("/global", (HttpContext http, IProviderConfiguration store, CancellationToken ct) =>
            ReadAsync(null, http, store, app.Environment, ct));
        root.MapGet("/properties/{property:long:min(1)}", (long property, HttpContext http,
            IProviderConfiguration store, CancellationToken ct) => ReadAsync(property, http, store, app.Environment, ct));
        root.MapPut("/global/{provider}", (string provider, HttpContext http, IProviderConfiguration store,
            IAntiforgery antiforgery, CancellationToken ct) => SaveAsync(null, provider, http, store, antiforgery, app.Environment, ct))
            .WithMetadata(new RequestSizeLimitAttribute(32768));
        root.MapPut("/properties/{property:long:min(1)}/{provider}", (long property, string provider, HttpContext http,
            IProviderConfiguration store, IAntiforgery antiforgery, CancellationToken ct) =>
            SaveAsync(property, provider, http, store, antiforgery, app.Environment, ct)).WithMetadata(new RequestSizeLimitAttribute(32768));

        root.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ConfigurationStorageException)
            {
                app.Logger.LogWarning("Configuration vault schema or stored configuration is unavailable. Check migration 014.");
                return Unavailable();
            }
            catch (MySqlException error) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                app.Logger.LogWarning("Configuration vault database error {ErrorCode} ({SqlState}).", error.ErrorCode, error.SqlState);
                return Unavailable();
            }
        });
    }

    private static async Task<IResult> ReadAsync(long? property, HttpContext http, IProviderConfiguration store,
        IHostEnvironment environment, CancellationToken ct)
    {
        var configuration = await store.ReadAsync(property, MonitoringEndpoints.UserId(http.User), ct);
        if (configuration is null) return Results.NotFound();
        return Results.Ok(configuration.CanEdit && !CanUseTransport(http, environment)
            ? configuration with { CanEdit = false, KeyAvailable = false, RequiresHttps = true } : configuration);
    }

    private static async Task<IResult> SaveAsync(long? property, string provider, HttpContext http,
        IProviderConfiguration store, IAntiforgery antiforgery, IHostEnvironment environment, CancellationToken ct)
    {
        if (!CanUseTransport(http, environment))
            return Results.Problem(statusCode: StatusCodes.Status426UpgradeRequired, detail: "Open Settings over HTTPS.");
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        // Handle malformed JSON here so model binding cannot include the submitted value in diagnostics.
        ProviderConfigurationUpdate? update;
        try { update = await http.Request.ReadFromJsonAsync<ProviderConfigurationUpdate>(ct); }
        catch (JsonException) { return InvalidConfiguration(); }
        if (update is null) return InvalidConfiguration();
        return await store.SaveAsync(property, MonitoringEndpoints.UserId(http.User), provider, update, ct) switch
        {
            ConfigurationSaveResult.Saved => Results.NoContent(),
            ConfigurationSaveResult.NotFound => Results.NotFound(),
            ConfigurationSaveResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            ConfigurationSaveResult.Conflict => Results.Problem(statusCode: 409,
                detail: "These settings changed. Reload before saving again."),
            ConfigurationSaveResult.Invalid => InvalidConfiguration(),
            _ => Unavailable()
        };
    }

    private static bool CanUseTransport(HttpContext http, IHostEnvironment environment) => http.Request.IsHttps ||
        (environment.IsDevelopment() && http.Connection.RemoteIpAddress is { } peer &&
         IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer) &&
         http.Request.Host.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]");

    private static IResult InvalidConfiguration() => Results.Problem(statusCode: 400,
        detail: "Check the polling interval and credential fields.");

    private static IResult Unavailable() => Results.Problem(statusCode: 503,
        detail: "Configuration storage or its encryption key is unavailable.");
}
