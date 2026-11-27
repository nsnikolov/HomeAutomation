using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HomeAutomation.Api;
using HomeAutomation.Application;
using HomeAutomation.Infrastructure.MySql;
using HomeAutomation.Infrastructure.Configuration;
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
var createProperty = args.Contains("--create-property", StringComparer.Ordinal);
var promoteInitialOwner = args.Contains("--promote-initial-owner", StringComparer.Ordinal);
var rotateConfigurationKeys = args.Contains("--rotate-configuration-keys", StringComparer.Ordinal);
var convertConfigurationIdentifiers = args.Contains("--convert-configuration-identifiers", StringComparer.Ordinal);
ProviderConfigurationStartup.ImportCommand? importProvider;
try { importProvider = ProviderConfigurationStartup.ParseImport(args); }
catch (ProviderConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
TelemetryBackfillCommand.Options? backfillTelemetry;
try { backfillTelemetry = TelemetryBackfillCommand.Parse(args); }
catch (TelemetryBackfillException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
if (backfillTelemetry is not null && (importProvider is not null || migrate || bootstrap || discoverGrowatt || createProperty ||
    promoteInitialOwner || rotateConfigurationKeys || convertConfigurationIdentifiers))
{
    Console.Error.WriteLine("Run the telemetry backfill command by itself.");
    Environment.ExitCode = 1;
    return;
}
LogicalDeviceInitializationCommand.Options? initializeLogical;
try { initializeLogical = LogicalDeviceInitializationCommand.Parse(args); }
catch (LogicalDeviceInitializationException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
if (initializeLogical is not null && (backfillTelemetry is not null || importProvider is not null || migrate || bootstrap ||
    discoverGrowatt || createProperty || promoteInitialOwner || rotateConfigurationKeys || convertConfigurationIdentifiers))
{
    Console.Error.WriteLine("Run the logical device initialization command by itself.");
    Environment.ExitCode = 1;
    return;
}
SimulatorCleanupCommand.Options? cleanupSimulator;
try { cleanupSimulator = SimulatorCleanupCommand.Parse(args); }
catch (SimulatorCleanupArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
if (cleanupSimulator is not null && (initializeLogical is not null || backfillTelemetry is not null || importProvider is not null || migrate ||
    bootstrap || discoverGrowatt || createProperty || promoteInitialOwner || rotateConfigurationKeys || convertConfigurationIdentifiers))
{
    Console.Error.WriteLine("Run the simulator cleanup command by itself.");
    Environment.ExitCode = 1;
    return;
}
RoomAssignmentClearingCommand.Options? clearRooms;
try { clearRooms = RoomAssignmentClearingCommand.Parse(args); }
catch (RoomAssignmentClearingArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
if (clearRooms is not null && (cleanupSimulator is not null || initializeLogical is not null || backfillTelemetry is not null ||
    importProvider is not null || migrate || bootstrap || discoverGrowatt || createProperty || promoteInitialOwner ||
    rotateConfigurationKeys || convertConfigurationIdentifiers))
{
    Console.Error.WriteLine("Run the room assignment clearing command by itself.");
    Environment.ExitCode = 1;
    return;
}
if (importProvider is not null && (migrate || bootstrap || discoverGrowatt || createProperty || promoteInitialOwner ||
    rotateConfigurationKeys || convertConfigurationIdentifiers))
{
    Console.Error.WriteLine("Run the provider import command by itself.");
    Environment.ExitCode = 1;
    return;
}
if ((rotateConfigurationKeys || convertConfigurationIdentifiers) &&
    (migrate || bootstrap || discoverGrowatt || createProperty || promoteInitialOwner ||
     (rotateConfigurationKeys && convertConfigurationIdentifiers)))
{
    Console.Error.WriteLine("Run one configuration maintenance command by itself.");
    Environment.ExitCode = 1;
    return;
}
if ((createProperty || promoteInitialOwner) && (migrate || bootstrap || discoverGrowatt || (createProperty && promoteInitialOwner)))
{
    Console.Error.WriteLine("Run a property administration command by itself.");
    Environment.ExitCode = 1;
    return;
}
var builder = WebApplication.CreateBuilder(args.Where(arg => !ProviderConfigurationStartup.IsImportArgument(arg) &&
    !TelemetryBackfillCommand.IsArgument(arg) && !LogicalDeviceInitializationCommand.IsArgument(arg) &&
    !SimulatorCleanupCommand.IsArgument(arg) && !RoomAssignmentClearingCommand.IsArgument(arg) && arg is not
    ("--migrate" or "--bootstrap-user" or "--discover-growatt" or "--create-property" or "--promote-initial-owner" or
        "--rotate-configuration-keys" or "--convert-configuration-identifiers")).ToArray());
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
builder.Services.AddOptions<ConfigurationVaultOptions>().BindConfiguration("ConfigurationVault");
builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<ConfigurationVaultOptions>>().Value);
builder.Services.AddSingleton<ProviderConfigurationStore>();
builder.Services.AddSingleton<IProviderConfiguration>(services => services.GetRequiredService<ProviderConfigurationStore>());
builder.Services.AddSingleton<IObservationProvider, SimulatorProvider>();
builder.Services.AddScoped<TelemetryWriter>();
builder.Services.AddScoped<DeviceViewPreferences>();
builder.Services.AddScoped<IMonitoringDashboard, MonitoringDashboardStore>();
builder.Services.AddSingleton<IMonitoringDashboardNotifier, MonitoringDashboardNotifier>();
builder.Services.AddScoped<PropertyManagementStore>();
builder.Services.AddScoped<IPropertyManagement>(services => services.GetRequiredService<PropertyManagementStore>());
builder.Services.AddScoped<IDeviceAssignmentAudit>(services => services.GetRequiredService<PropertyManagementStore>());
builder.Services.AddScoped<ITelemetryWriter>(services => services.GetRequiredService<TelemetryWriter>());
builder.Services.AddScoped<IMonitoringReader, MonitoringReader>();
builder.Services.AddScoped<IGrowattTelemetryStore>(services => services.GetRequiredService<TelemetryWriter>());
builder.Services.AddScoped<IGrowattMonitoringReader, MonitoringReader>();
var providerConfiguration = new ProviderConfigurationStartup();
builder.Services.AddSingleton(services =>
{
    if (providerConfiguration.Get<GrowattOptions>("growatt") is { } fromVault) return fromVault;
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
    if (providerConfiguration.Get<DaikinOptions>("daikin") is { } fromVault) return fromVault;
    try { return services.GetRequiredService<IConfiguration>().GetSection("Daikin").Get<DaikinOptions>() ?? new DaikinOptions(); }
    catch (InvalidOperationException) { return new DaikinOptions(); }
});
builder.Services.AddSingleton<DaikinFlow>();
DaikinService.Register(builder.Services);
builder.Services.AddHostedService<DaikinWorker>();
builder.Services.AddSingleton(services =>
{
    if (providerConfiguration.Get<EwelinkOptions>("ewelink") is { } fromVault) return fromVault;
    try { return services.GetRequiredService<IConfiguration>().GetSection("Ewelink").Get<EwelinkOptions>() ?? new EwelinkOptions(); }
    catch (InvalidOperationException) { return new EwelinkOptions(); }
});
builder.Services.AddSingleton<EwelinkConsent>();
builder.Services.AddSingleton<IEwelinkLiveNotifier, EwelinkLiveNotifier>();
EwelinkService.Register(builder.Services);
builder.Services.AddSingleton(services =>
{
    if (providerConfiguration.Get<TuyaOptions>("tuya") is { } fromVault) return fromVault;
    try { return services.GetRequiredService<IConfiguration>().GetSection("Tuya").Get<TuyaOptions>() ?? new TuyaOptions(); }
    catch (InvalidOperationException) { return new TuyaOptions(); }
});
TuyaService.Register(builder.Services);
builder.Services.AddScoped<IdentityStore>();
builder.Services.AddSingleton<PropertyAdministration>();
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
builder.Services.AddTelemetryHistory();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(6));

await using var app = builder.Build();
if (importProvider is not null)
{
    if (!await ProviderConfigurationStartup.ImportAsync(app, importProvider)) Environment.ExitCode = 1;
    return;
}
if (rotateConfigurationKeys || convertConfigurationIdentifiers)
{
    using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        var store = app.Services.GetRequiredService<ProviderConfigurationStore>();
        if (convertConfigurationIdentifiers)
        {
            var converted = await store.ConvertIdentifiersAsync(operation.Token);
            app.Logger.LogInformation("Converted {IdentifierCount} staged configuration identifiers.", converted);
        }
        else
        {
            var rotated = await store.RotateAsync(operation.Token);
            app.Logger.LogInformation("Re-encrypted {CredentialCount} configuration credentials.", rotated);
        }
    }
    catch (Exception error) when (error is CryptographicException or MySqlException or ConfigurationStorageException or
        OptionsValidationException or OperationCanceledException)
    {
        if (convertConfigurationIdentifiers)
            app.Logger.LogError("Identifier conversion failed ({FailureType}). Check migration 015, the original external key files and identifier conflicts.",
                error.GetType().Name);
        else
            app.Logger.LogError("Configuration rotation failed ({FailureType}). Check migration 015, the external key files and pending identifier conversion.",
                error.GetType().Name);
        Environment.ExitCode = 1;
    }
    return;
}
if (backfillTelemetry is not null)
{
    // Load the selected provider profiles so hold intervals match the running service.
    if (!await providerConfiguration.LoadAsync(app))
    {
        Environment.ExitCode = 1;
        return;
    }
    Environment.ExitCode = await TelemetryBackfillCommand.RunAsync(app, backfillTelemetry);
    return;
}
if (initializeLogical is not null)
{
    Environment.ExitCode = await LogicalDeviceInitializationCommand.RunAsync(app, initializeLogical);
    return;
}
if (cleanupSimulator is not null)
{
    Environment.ExitCode = await SimulatorCleanupCommand.RunAsync(app, cleanupSimulator);
    return;
}
if (clearRooms is not null)
{
    Environment.ExitCode = await RoomAssignmentClearingCommand.RunAsync(app, clearRooms);
    return;
}
if (discoverGrowatt)
{
    if (!migrate && !bootstrap && !await providerConfiguration.LoadAsync(app, "growatt"))
    {
        Environment.ExitCode = 1;
        return;
    }
    await GrowattDiscovery.RunAsync(app, migrate || bootstrap);
    return;
}

if (migrate || bootstrap || createProperty || promoteInitialOwner)
{
    using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        if (migrate) await app.Services.GetRequiredService<Migrations>().ApplyAsync(operation.Token);
        if (bootstrap) await SessionEndpoints.BootstrapAsync(app.Services, app.Configuration, operation.Token);
        if (createProperty || promoteInitialOwner) await PropertyAdminCommands.RunAsync(app, createProperty, operation.Token);
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

if (!await providerConfiguration.LoadAsync(app))
{
    Environment.ExitCode = 1;
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
app.MapConfiguration();
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
