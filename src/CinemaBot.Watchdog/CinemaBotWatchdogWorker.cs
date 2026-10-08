using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Watchdog
{
    public sealed class CinemaBotWatchdogWorker : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CinemaBotWatchdogWorker> _logger;
        private readonly HttpClient _telegramClient;
        private readonly string _dataDirectory;
        private WatchdogState _state = new WatchdogState();
        private int _offlineObservations;

        public CinemaBotWatchdogWorker(
            IConfiguration configuration,
            ILogger<CinemaBotWatchdogWorker> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _dataDirectory =
                Environment.ExpandEnvironmentVariables(
                    configuration["Storage:DataDirectory"] ??
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.CommonApplicationData),
                        "CinemaBot"));

            Directory.CreateDirectory(
                _dataDirectory);

            var handler =
                new SocketsHttpHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.GZip |
                        DecompressionMethods.Deflate |
                        DecompressionMethods.Brotli,
                    PooledConnectionLifetime =
                        TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout =
                        TimeSpan.FromMinutes(2),
                    ConnectTimeout =
                        TimeSpan.FromSeconds(8)
                };

            _telegramClient =
                new HttpClient(
                    handler)
                {
                    Timeout =
                        TimeSpan.FromSeconds(35)
                };

            LoadState();
        }

        private string HealthPath =>
            Path.Combine(
                _dataDirectory,
                "health-state.json");

        private string StatePath =>
            Path.Combine(
                _dataDirectory,
                "watchdog-state.json");

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            int checkSeconds =
                ReadInt(
                    "Watchdog:CheckEverySeconds",
                    15,
                    5,
                    120);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckCinemaBotAsync(
                        stoppingToken);

                    await DeleteExpiredHealthMessagesAsync(
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "CinemaBot watchdog check failed. It will retry.");
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            checkSeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task CheckCinemaBotAsync(
            CancellationToken stoppingToken)
        {
            CinemaBotHealthState? health =
                ReadHealthState();

            int staleSeconds =
                ReadInt(
                    "HealthMonitor:StaleAfterSeconds",
                    90,
                    30,
                    600);

            bool stale =
                health is null ||
                DateTime.UtcNow -
                health.UpdatedUtc >
                TimeSpan.FromSeconds(
                    staleSeconds);

            bool degraded =
                health is not null &&
                (string.Equals(
                     health.Status,
                     "Degraded",
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     health.Status,
                     "Stopped",
                     StringComparison.OrdinalIgnoreCase));

            bool offline =
                stale ||
                degraded;

            if (offline)
            {
                _offlineObservations++;
            }
            else
            {
                _offlineObservations =
                    0;
            }

            int requiredObservations =
                ReadInt(
                    "Watchdog:ConsecutiveFailuresBeforeAlert",
                    2,
                    1,
                    10);

            if (offline &&
                _offlineObservations >=
                requiredObservations &&
                !_state.IsOffline)
            {
                string reason =
                    health?.Reason ??
                    "No recent heartbeat was received from CinemaBot.Service.";

                int? messageId =
                    await SendMessageAsync(
                        BuildOfflineMessage(
                            reason,
                            health?.UpdatedUtc),
                        stoppingToken);

                if (messageId.HasValue)
                {
                    _state.IsOffline =
                        true;
                    _state.OfflineMessageId =
                        messageId;
                    _state.OfflineSinceUtc =
                        DateTime.UtcNow;
                    SaveState();
                }

                return;
            }

            if (!offline &&
                _state.IsOffline)
            {
                TimeSpan downtime =
                    DateTime.UtcNow -
                    (_state.OfflineSinceUtc ??
                     DateTime.UtcNow);

                int? messageId =
                    await SendMessageAsync(
                        BuildOnlineMessage(
                            downtime),
                        stoppingToken);

                if (messageId.HasValue)
                {
                    _state.OnlineMessageId =
                        messageId;
                    _state.DeleteMessagesAfterUtc =
                        DateTime.UtcNow.AddMinutes(
                            ReadInt(
                                "HealthMonitor:DeleteRecoveryMessagesAfterMinutes",
                                5,
                                1,
                                60));
                    _state.IsOffline =
                        false;
                    SaveState();
                }
            }
        }

        private async Task DeleteExpiredHealthMessagesAsync(
            CancellationToken stoppingToken)
        {
            if (!_state.DeleteMessagesAfterUtc.HasValue ||
                _state.DeleteMessagesAfterUtc.Value >
                DateTime.UtcNow)
            {
                return;
            }

            if (_state.OfflineMessageId.HasValue)
            {
                await DeleteMessageAsync(
                    _state.OfflineMessageId.Value,
                    stoppingToken);
            }

            if (_state.OnlineMessageId.HasValue)
            {
                await DeleteMessageAsync(
                    _state.OnlineMessageId.Value,
                    stoppingToken);
            }

            _state.OfflineMessageId =
                null;
            _state.OnlineMessageId =
                null;
            _state.OfflineSinceUtc =
                null;
            _state.DeleteMessagesAfterUtc =
                null;
            SaveState();
        }

        private CinemaBotHealthState? ReadHealthState()
        {
            try
            {
                if (!File.Exists(
                        HealthPath))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<CinemaBotHealthState>(
                    File.ReadAllText(
                        HealthPath));
            }
            catch
            {
                return null;
            }
        }

        private string BuildOfflineMessage(
            string reason,
            DateTime? lastHeartbeatUtc)
        {
            string lastSeen =
                lastHeartbeatUtc.HasValue
                    ? lastHeartbeatUtc.Value
                        .ToLocalTime()
                        .ToString(
                            "dd MMM yyyy, hh:mm:ss tt",
                            CultureInfo.InvariantCulture)
                    : "Unknown";

            return
                "🚨 <b>CinemaBot has a problem | توجد مشكلة في البوت</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "🇪🇬 البوت غير متاح حالياً أو إحدى خدماته الأساسية متوقفة.\n" +
                "سيتم الاستمرار في إعادة المحاولة تلقائياً.\n\n" +
                "🇬🇧 CinemaBot is currently unavailable or a required service is degraded.\n" +
                "Automatic recovery attempts are continuing.\n\n" +
                "Reason | السبب: <code>" +
                EscapeHtml(
                    reason) +
                "</code>\n" +
                "Last heartbeat | آخر اتصال: " +
                EscapeHtml(
                    lastSeen);
        }

        private static string BuildOnlineMessage(
            TimeSpan downtime)
        {
            return
                "✅ <b>CinemaBot is online again | البوت عاد للعمل</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "🇪🇬 عاد البوت للعمل بصورة طبيعية.\n" +
                "🇬🇧 CinemaBot is operating normally again.\n\n" +
                "Downtime | مدة التوقف: " +
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        downtime.TotalMinutes)) +
                " minute(s).\n\n" +
                "🧹 سيتم حذف رسالتي التوقف والعودة تلقائياً بعد 5 دقائق.";
        }

        private async Task<int?> SendMessageAsync(
            string message,
            CancellationToken stoppingToken)
        {
            string token =
                (_configuration["Telegram:BotToken"] ??
                 string.Empty).Trim();
            string chatId =
                (_configuration["Telegram:ChatId"] ??
                 string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(
                    token) ||
                string.IsNullOrWhiteSpace(
                    chatId))
            {
                return null;
            }

            string url =
                "https://api.telegram.org/bot" +
                token +
                "/sendMessage";

            while (!stoppingToken.IsCancellationRequested)
            {
                string payload =
                    JsonSerializer.Serialize(
                        new
                        {
                            chat_id =
                                chatId,
                            text =
                                message,
                            parse_mode =
                                "HTML",
                            disable_web_page_preview =
                                true
                        });

                using var content =
                    new StringContent(
                        payload,
                        Encoding.UTF8,
                        "application/json");

                using HttpResponseMessage response =
                    await _telegramClient.PostAsync(
                        url,
                        content,
                        stoppingToken);

                string body =
                    await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument document =
                        JsonDocument.Parse(
                            body);

                    if (document.RootElement.TryGetProperty(
                            "result",
                            out JsonElement result) &&
                        result.TryGetProperty(
                            "message_id",
                            out JsonElement messageId))
                    {
                        return messageId.GetInt32();
                    }

                    return null;
                }

                if ((int)response.StatusCode ==
                    429)
                {
                    int retry =
                        ReadRetryAfter(
                            body) +
                        3;

                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            retry),
                        stoppingToken);

                    continue;
                }

                _logger.LogWarning(
                    "Watchdog Telegram send failed with HTTP {Status}: {Body}",
                    (int)response.StatusCode,
                    body.Length > 500
                        ? body.Substring(0, 500)
                        : body);

                return null;
            }

            return null;
        }

        private async Task DeleteMessageAsync(
            int messageId,
            CancellationToken stoppingToken)
        {
            string token =
                (_configuration["Telegram:BotToken"] ??
                 string.Empty).Trim();
            string chatId =
                (_configuration["Telegram:ChatId"] ??
                 string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(chatId))
            {
                return;
            }

            string url =
                "https://api.telegram.org/bot" +
                token +
                "/deleteMessage";

            string payload =
                JsonSerializer.Serialize(
                    new
                    {
                        chat_id =
                            chatId,
                        message_id =
                            messageId
                    });

            using var content =
                new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json");

            try
            {
                await _telegramClient.PostAsync(
                    url,
                    content,
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not delete health message {MessageId}. It will retry.",
                    messageId);
                throw;
            }
        }

        private static int ReadRetryAfter(
            string body)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        body);

                if (document.RootElement.TryGetProperty(
                        "parameters",
                        out JsonElement parameters) &&
                    parameters.TryGetProperty(
                        "retry_after",
                        out JsonElement retryAfter) &&
                    retryAfter.TryGetInt32(
                        out int seconds))
                {
                    return Math.Max(
                        1,
                        seconds);
                }
            }
            catch
            {
            }

            return 5;
        }

        private int ReadInt(
            string key,
            int defaultValue,
            int minimum,
            int maximum)
        {
            return int.TryParse(
                       _configuration[key],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out int value)
                ? Math.Max(
                    minimum,
                    Math.Min(
                        maximum,
                        value))
                : defaultValue;
        }

        private static string EscapeHtml(
            string value)
        {
            return (value ??
                    string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        private void LoadState()
        {
            try
            {
                if (File.Exists(
                        StatePath))
                {
                    _state =
                        JsonSerializer.Deserialize<WatchdogState>(
                            File.ReadAllText(
                                StatePath)) ??
                        new WatchdogState();
                }
            }
            catch
            {
                _state =
                    new WatchdogState();
            }
        }

        private void SaveState()
        {
            string temporary =
                StatePath +
                ".tmp";

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    _state,
                    new JsonSerializerOptions
                    {
                        WriteIndented =
                            true
                    }));

            if (File.Exists(
                    StatePath))
            {
                File.Delete(
                    StatePath);
            }

            File.Move(
                temporary,
                StatePath);
        }

        public override void Dispose()
        {
            _telegramClient.Dispose();
            base.Dispose();
        }
    }

    internal sealed class CinemaBotHealthState
    {
        public string ServiceName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool GracefulShutdown { get; set; }
        public string Version { get; set; } = string.Empty;
    }

    internal sealed class WatchdogState
    {
        public bool IsOffline { get; set; }
        public int? OfflineMessageId { get; set; }
        public int? OnlineMessageId { get; set; }
        public DateTime? OfflineSinceUtc { get; set; }
        public DateTime? DeleteMessagesAfterUtc { get; set; }
    }
}
