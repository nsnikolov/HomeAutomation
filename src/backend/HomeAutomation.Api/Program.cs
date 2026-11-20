using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HomeAutomation.Api;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MySqlConnector;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var bootstrap = args.Contains("--bootstrap-user", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not ("--migrate" or "--bootstrap-user")).ToArray());
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => UseEnumStrings(options.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(options => UseEnumStrings(options.PayloadSerializerOptions));

builder.Services.AddOptions<SimulatorOptions>().BindConfiguration("Simulator")
    .Validate(options => options.PollSeconds is >= 2 and <= 900 &&
        options.FreshnessSeconds >= 2 * options.PollSeconds && options.FreshnessSeconds <= 3600 &&
        Enum.IsDefined(options.Scenario), "Invalid simulator cadence, freshness or scenario.")
    .ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration("Database")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(options.Name) && !string.IsNullOrWhiteSpace(options.User) &&
        !string.IsNullOrWhiteSpace(options.Password), "Set the database host, port, name, user and password.")
    .ValidateOnStart();
builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<SimulatorOptions>>().Value);
builder.Services.AddSingleton(services =>
{
    var database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    return new Database(new MySqlConnectionStringBuilder
    {
        Server = database.Host,
        Port = (uint)database.Port,
        Database = database.Name,
        UserID = database.User,
        Password = database.Password,
        SslMode = database.SslMode,
        ConnectionTimeout = 5,
        DefaultCommandTimeout = 10
    }.ConnectionString);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Migrations>();
builder.Services.AddSingleton<IObservationProvider, SimulatorProvider>();
builder.Services.AddScoped<ITelemetryWriter, TelemetryWriter>();
builder.Services.AddScoped<IMonitoringReader, MonitoringReader>();
builder.Services.AddScoped<IdentityStore>();
builder.Services.AddIdentityCore<LocalUser>(options =>
{
    options.Password.RequiredLength = 14;
    options.Lockout.MaxFailedAccessAttempts = 6;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
    .AddSignInManager()
    .AddUserStore<IdentityStore>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "homeautomation.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("session", limiter =>
{
    limiter.PermitLimit = 30;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
var protection = builder.Services.AddDataProtection().SetApplicationName("HomeAutomation");
if (!string.IsNullOrWhiteSpace(keyDirectory))
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

builder.Services.AddSingleton<PollProgress>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(6));

await using var app = builder.Build();
if (migrate || bootstrap)
{
    using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        if (migrate) await app.Services.GetRequiredService<Migrations>().ApplyAsync(operation.Token);
        if (bootstrap) await SessionEndpoints.BootstrapAsync(app.Services, app.Configuration, operation.Token);
    }
    catch (MySqlException error)
    {
        app.Logger.LogError("Database setup failed with error {ErrorCode} and SQL state {SqlState}", error.ErrorCode, error.SqlState);
        Environment.ExitCode = 1;
    }
    catch (Exception error) when (error is InvalidOperationException or OptionsValidationException or OperationCanceledException)
    {
        app.Logger.LogError("Database or account setup failed: {Reason}", error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

app.UseExceptionHandler();
app.UseStaticFiles();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/hubs") &&
        context.Request.Headers.Origin is { Count: > 0 } origins &&
        (!Uri.TryCreate(origins.ToString(), UriKind.Absolute, out var origin) ||
            !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.MapSession();
app.MapMonitoring();
app.MapHub<MonitoringHub>("/hubs/monitoring", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization();
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();

static void UseEnumStrings(JsonSerializerOptions options) =>
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

public partial class Program;
