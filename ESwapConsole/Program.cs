using ESwapConsole;
using ESwapConsole.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", false, true);
builder.Logging.ClearProviders();

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(LogEventLevel.Error)
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "eswap-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true));

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));

builder.Services.AddSingleton<ESwapApplication>();

using IHost host = builder.Build();

try
{
    ESwapApplication app = host.Services.GetRequiredService<ESwapApplication>();
    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
