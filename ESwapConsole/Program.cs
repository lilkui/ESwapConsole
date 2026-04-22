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

builder.Services
    .AddOptions<AppConfig>()
    .Bind(builder.Configuration.GetSection("AppConfig"))
    .Validate(
        static config => config.Users.All(static user =>
            !string.IsNullOrWhiteSpace(user.UserId) &&
            !string.IsNullOrWhiteSpace(user.Password) &&
            user.AccountIds.Length > 0),
        "Each user must configure UserId, Password, and at least one AccountId.")
    .ValidateOnStart();

builder.Services.AddSingleton<ESwapApplication>();

using IHost host = builder.Build();

try
{
    ESwapApplication app = host.Services.GetRequiredService<ESwapApplication>();
    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await app.RunAsync(lifetime.ApplicationStopping);
}
finally
{
    await Log.CloseAndFlushAsync();
}
