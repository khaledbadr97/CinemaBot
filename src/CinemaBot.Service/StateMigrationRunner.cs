using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    internal static class StateMigrationRunner
    {
        private static readonly string[] StateFileNames =
        {
            "telegram-user-preferences.json",
            "telegram-bot-subscribers.json",
            "current-movie-user-preferences.json",
            "coming-soon-state.json",
            "coming-soon-user-preferences.json",
            "telegram-admin-settings.json",
            "telegram-user-activity.json",
            "scan-range-settings.json",
            "telegram-update-offset.json"
        };

        public static async Task<int> RunAsync(
            string dataDirectory,
            CancellationToken cancellationToken = default)
        {
            IConfiguration configuration =
                new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile(
                        "appsettings.json",
                        optional: true,
                        reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .Build();

            using ILoggerFactory loggerFactory =
                LoggerFactory.Create(builder =>
                {
                    builder.AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    });
                });

            ILogger logger =
                loggerFactory.CreateLogger("CinemaBot.StateMigration");

            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                logger.LogError("A local data directory is required.");
                return 2;
            }

            string fullDataDirectory =
                Path.GetFullPath(dataDirectory);

            if (!Directory.Exists(fullDataDirectory))
            {
                logger.LogWarning(
                    "Local data directory does not exist: {DataDirectory}",
                    fullDataDirectory);
                return 0;
            }

            var store =
                new PostgresStateStore(
                    configuration,
                    logger);

            if (!store.IsEnabled)
            {
                logger.LogError(
                    "PostgreSQL is not enabled. Set DATABASE_URL and Storage__UsePostgres=true before running the migration.");
                return 3;
            }

            await store.EnsureSchemaAsync(cancellationToken);

            int imported = 0;
            int skipped = 0;

            foreach (string fileName in StateFileNames)
            {
                string path =
                    Path.Combine(
                        fullDataDirectory,
                        fileName);

                if (!File.Exists(path))
                {
                    skipped++;
                    continue;
                }

                string json =
                    await File.ReadAllTextAsync(
                        path,
                        cancellationToken);

                if (string.IsNullOrWhiteSpace(json))
                {
                    skipped++;
                    continue;
                }

                if (store.TryGetJson(fileName, out _))
                {
                    logger.LogInformation(
                        "Skipping {FileName}: PostgreSQL already has this state key.",
                        fileName);
                    skipped++;
                    continue;
                }

                store.SetJson(fileName, json);
                imported++;

                logger.LogInformation(
                    "Imported {FileName} into PostgreSQL.",
                    fileName);
            }

            logger.LogInformation(
                "Local state migration completed. Imported: {Imported}; skipped: {Skipped}.",
                imported,
                skipped);

            return 0;
        }
    }
}
