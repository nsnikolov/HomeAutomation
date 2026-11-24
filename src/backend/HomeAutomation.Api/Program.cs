using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HomeAutomation.Api;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Simulator;
using HomeAutomation.Infrastructure.Growatt;
using HomeAutomation.Infrastructure.Daikin;
using HomeAutomation.Infrastructure.Ewelink;
using HomeAutomation.Infrastructure.Tuya;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MySqlConnector;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var bootstrap = args.Contains("--bootstrap-user", StringComparer.Ordinal);
var discoverGrowatt = args.Contains("--discover-growatt", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not ("--migrate" or "--bootstrap-user" or "--discover-growatt")).ToArray());
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
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "Set Database:Host to the intended MySQL server.")
    .Validate(options => options.Port is > 0 and <= 65535, "Set Database:Port to the confirmed MySQL TCP port (1–65535).")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Name), "Set Database:Name to the dedicated Home Automation schema.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.User), "Set Database:User to the scoped MySQL account.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Set Database:Password with Development User Secrets or a private environment variable.")
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
builder.Services.AddScoped<TelemetryWriter>();
builder.Services.AddScoped<DeviceViewPreferences>();
builder.Services.AddScoped<IMonitoringDashboard, MonitoringDashboardStore>();
builder.Services.AddSingleton<IMonitoringDashboardNotifier, MonitoringDashboardNotifier>();
builder.Services.AddScoped<ITelemetryWriter>(services => services.GetRequiredService<TelemetryWriter>());
builder.Services.AddScoped<IMonitoringReader, MonitoringReader>();
builder.Services.AddScoped<IGrowattTelemetryStore>(services => services.GetRequiredService<TelemetryWriter>());
builder.Services.AddScoped<IGrowattMonitoringReader, MonitoringReader>();
builder.Services.AddSingleton(services =>
{
    try
    {
        return services.GetRequiredService<IConfiguration>().GetSection("Growatt").Get<GrowattOptions>() ?? new GrowattOptions();
    }
    catch (InvalidOperationException)
    {
        // Route malformed optional provider settings to its isolated configuration check.
        return new GrowattOptions { Enabled = true };
    }
});
builder.Services.AddSingleton<GrowattMapper>();
builder.Services.AddSingleton<GrowattPoller>();
builder.Services.AddSingleton<GrowattProgress>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<GrowattCloudClient>(client =>
{
    client.BaseAddress = new Uri("https://openapi.growatt.com/");
    client.MaxResponseContentBufferSize = 1024 * 1024;
    client.Timeout = Timeout.InfiniteTimeSpan; // The worker and discovery command each own their deadline.
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    .RemoveAllLoggers();
builder.Services.AddScoped<IGrowattReadSource, GrowattCloudSource>();
builder.Services.AddHostedService<GrowattWorker>();
builder.Services.AddSingleton(services =>
{
    try { return services.GetRequiredService<IConfiguration>().GetSection("Daikin").Get<DaikinOptions>() ?? new DaikinOptions(); }
    catch (InvalidOperationException) { return new DaikinOptions(); }
});
builder.Services.AddSingleton<DaikinFlow>();
DaikinService.Register(builder.Services);
builder.Services.AddHostedService<DaikinWorker>();
builder.Services.AddSingleton(services =>
{
    try { return services.GetRequiredService<IConfiguration>().GetSection("Ewelink").Get<EwelinkOptions>() ?? new EwelinkOptions(); }
    catch (InvalidOperationException) { return new EwelinkOptions(); }
});
builder.Services.AddSingleton<EwelinkConsent>();
builder.Services.AddSingleton<IEwelinkLiveNotifier, EwelinkLiveNotifier>();
EwelinkService.Register(builder.Services);
builder.Services.AddSingleton(services =>
{
    try { return services.GetRequiredService<IConfiguration>().GetSection("Tuya").Get<TuyaOptions>() ?? new TuyaOptions(); }
    catch (InvalidOperationException) { return new TuyaOptions(); }
});
TuyaService.Register(builder.Services);
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
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("session", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddPolicy("ewelink-presence", context => RateLimitPartition.GetFixedWindowLimiter(
        MonitoringEndpoints.UserId(context.User), _ => new()
        {
            PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
var protection = builder.Services.AddDataProtection().SetApplicationName("HomeAutomation");
if (!string.IsNullOrWhiteSpace(keyDirectory))
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

builder.Services.AddSingleton<PollProgress>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(6));

await using var app = builder.Build();
if (discoverGrowatt)
{
    await GrowattDiscovery.RunAsync(app, migrate || bootstrap);
    return;
}

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

if (app.Environment.IsDevelopment())
{
    var localHttps = new HostString("ha-dev.britishlab.org", 5443);
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    forwarded.KnownProxies.Add(IPAddress.Loopback);
    app.UseWhen(context => context.Request.Host == localHttps, proxy => proxy.UseForwardedHeaders(forwarded));
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
app.MapDaikin();
app.MapEwelink();
app.MapTuya();
app.MapHub<MonitoringHub>("/hubs/monitoring", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization();
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
try
{
    await app.RunAsync();
}
catch (OptionsValidationException error)
{
    foreach (var failure in error.Failures)
        app.Logger.LogError("Configuration error: {Reason}", failure);
    Environment.ExitCode = 1;
}

static void UseEnumStrings(JsonSerializerOptions options) =>
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

public partial class Program;
