using MorseSafetyAlerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // When running as a Windows Service, the working directory is often System32.
    // Use the executable directory so appsettings.json is found reliably.
    ContentRootPath = AppContext.BaseDirectory,
});

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

builder.Services.AddHttpClient<NwsClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<ExpoPushClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
