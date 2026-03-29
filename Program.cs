using MorseSafetyAlerts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // When running as a Windows Service, the working directory is often System32.
    // Use the executable directory so appsettings.json is found reliably.
    ContentRootPath = AppContext.BaseDirectory,
});

// Be explicit about loading config from the executable directory.
// (In practice, Windows services are very sensitive to working dir/base path.)
var baseDir = AppContext.BaseDirectory;
builder.Configuration.AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(baseDir, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);

// When installed as a Windows Service, integrate with SCM.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MorseSafetyAlerts";
});

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

var alertsOpt = builder.Configuration.GetSection("Alerts").Get<AlertsOptions>() ?? new AlertsOptions();
builder.Services.AddSingleton(alertsOpt);

var connStr = builder.Configuration.GetConnectionString("MorseIndiana");
if (string.IsNullOrWhiteSpace(connStr))
{
    throw new InvalidOperationException("Missing ConnectionStrings:MorseIndiana");
}

builder.Services.AddSingleton<SqlRepository>(sp => new SqlRepository(connStr, sp.GetRequiredService<ILogger<SqlRepository>>()));

// Persist small state between restarts (ETag, last-known flags).
builder.Services.AddSingleton<StateStore>(sp =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "state", "state.json");
    return new StateStore(path, sp.GetRequiredService<ILogger<StateStore>>());
});

builder.Services.AddSingleton<StatusWriter>(sp =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "state", "status.json");
    return new StatusWriter(path, sp.GetRequiredService<ILogger<StatusWriter>>());
});

builder.Services.AddHttpClient<NwsClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<ExpoPushClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

// Lightning
builder.Services.AddSingleton<LightningStrikeWindow>(sp => new LightningStrikeWindow(alertsOpt.Lightning));
builder.Services.AddHostedService<LightningMqttListener>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
