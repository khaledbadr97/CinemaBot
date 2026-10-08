using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CinemaBot.Watchdog
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            IHostBuilder hostBuilder =
                Host.CreateDefaultBuilder(args)
                    .UseContentRoot(AppContext.BaseDirectory);

            if (OperatingSystem.IsWindows())
            {
                hostBuilder.UseWindowsService(options =>
                {
                    options.ServiceName = "CinemaBot Watchdog";
                });
            }

            hostBuilder
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<CinemaBotWatchdogWorker>();
                });

            hostBuilder.Build().Run();
        }
    }
}
