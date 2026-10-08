using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    public partial class CinemaBotWorker
    {
        private readonly object _healthLock =
            new object();

        private string _healthStatus =
            "Starting";

        private string _healthReason =
            "CinemaBot is starting.";

        private static string ResolveDataDirectory(
            IConfiguration configuration)
        {
            string? configured =
                configuration["Storage:DataDirectory"];

            if (!string.IsNullOrWhiteSpace(
                    configured))
            {
                string value = configured.Trim();

                if (!OperatingSystem.IsWindows() &&
                    value.IndexOf("%ProgramData%", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Path.Combine(
                        AppContext.BaseDirectory,
                        "data");
                }

                return Environment.ExpandEnvironmentVariables(value);
            }

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "CinemaBot");
            }

            return Path.Combine(
                AppContext.BaseDirectory,
                "data");
        }

        private string HealthStatePath =>
            Path.Combine(
                _dataDirectory,
                "health-state.json");

        private async Task RunHeartbeatLoopAsync(
            CancellationToken stoppingToken)
        {
            int heartbeatSeconds =
                ReadIntegerSetting(
                    "HealthMonitor:HeartbeatSeconds",
                    defaultValue: 10,
                    minimumValue: 5,
                    maximumValue: 60);

            while (!stoppingToken.IsCancellationRequested)
            {
                string status;
                string reason;

                lock (_healthLock)
                {
                    status =
                        _healthStatus;
                    reason =
                        _healthReason;
                }

                WriteHealthState(
                    status,
                    reason,
                    gracefulShutdown: false);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            heartbeatSeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private void SetHealthStatus(
            string status,
            string reason)
        {
            lock (_healthLock)
            {
                _healthStatus =
                    status;
                _healthReason =
                    reason;
            }

            WriteHealthState(
                status,
                reason,
                gracefulShutdown: false);
        }

        private void WriteHealthState(
            string status,
            string reason,
            bool gracefulShutdown)
        {
            try
            {
                var state =
                    new CinemaBotHealthState
                    {
                        ServiceName =
                            "CinemaBot.Service",

                        ProcessId =
                            Process.GetCurrentProcess().Id,

                        UpdatedUtc =
                            DateTime.UtcNow,

                        Status =
                            status,

                        Reason =
                            reason,

                        GracefulShutdown =
                            gracefulShutdown,

                        Version =
                            typeof(CinemaBotWorker)
                                .Assembly
                                .GetName()
                                .Version?
                                .ToString() ??
                            "1.0.0"
                    };

                WriteJsonAtomically(
                    HealthStatePath,
                    state);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not write CinemaBot heartbeat.");
            }
        }

        private static void WriteJsonAtomically<T>(
            string path,
            T value)
        {
            string? directory =
                Path.GetDirectoryName(
                    path);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            string temporaryPath =
                path +
                ".tmp";

            string json =
                JsonSerializer.Serialize(
                    value,
                    new JsonSerializerOptions
                    {
                        WriteIndented =
                            true
                    });

            File.WriteAllText(
                temporaryPath,
                json);

            if (File.Exists(
                    path))
            {
                File.Delete(
                    path);
            }

            File.Move(
                temporaryPath,
                path);
        }
    }
}
