using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CinemaBot.Service
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length >= 2 &&
                string.Equals(args[0], "--migrate-local-state", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode =
                    StateMigrationRunner
                        .RunAsync(args[1])
                        .GetAwaiter()
                        .GetResult();
                return;
            }

            IHostBuilder hostBuilder =
                Host.CreateDefaultBuilder(args)
                    .UseContentRoot(AppContext.BaseDirectory);

            // Keep Windows Service support for local Windows deployments,
            // while allowing the same application to run cleanly in Linux
            // containers such as Render Background Workers.
            if (OperatingSystem.IsWindows())
            {
                hostBuilder.UseWindowsService(options =>
                {
                    options.ServiceName = "CinemaBot Service";
                });
            }

            hostBuilder
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<CinemaBotWorker>();
                });

            hostBuilder.Build().Run();
        }
    }
}
