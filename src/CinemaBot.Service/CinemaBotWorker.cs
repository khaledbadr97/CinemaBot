using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    public partial class CinemaBotWorker : BackgroundService
    {
        private readonly string _dataDirectory;

        private readonly IConfiguration _configuration;

        private readonly ILogger<CinemaBotWorker> _logger;

        private readonly PostgresStateStore _stateStore;

        private readonly HttpClient _voxHttpClient;

        private readonly HttpClient _telegramHttpClient;


        // Telegram getUpdates is a long-polling request. It must not share the
        // short timeout used by normal send/edit/delete Bot API operations.
        private readonly HttpClient _telegramPollingHttpClient;

        private readonly object _debugFileLock =
                new object();

        private Dictionary<string, DaySchedule> _knownScheduleByCinemaAndDate =
                new Dictionary<string, DaySchedule>(
                    StringComparer.OrdinalIgnoreCase);

        private CinemaOption[] _lastDiscoveredCinemas =
                Array.Empty<CinemaOption>();

        private readonly object _preferencesLock =
                new object();

        private Dictionary<long, UserPreference> _userPreferences =
                new Dictionary<long, UserPreference>();

        private readonly string _preferencesFilePath;

        private long _telegramUpdateOffset;

        private string _botUsername =
                string.Empty;

        private bool _groupWelcomeSent;

        private bool _telegramWebhookCleared;

        private bool _telegramCommandsConfigured;

        private readonly SemaphoreSlim _scanWakeSignal =
                new SemaphoreSlim(0, 1);

        private readonly object _scanRefreshLock =
                new object();

        private DateTime _lastImmediateScanRequestUtc =
                DateTime.MinValue;

        private DateTime _lastSuccessfulScheduleScanUtc =
                DateTime.MinValue;

        private readonly object _movieDetailsLoadLock =
                new object();

        private readonly Dictionary<string, long> _movieDetailsLoadVersions =
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);

        private int? _groupDashboardMessageId;

        private int? _groupArabicDashboardMessageId;

        // Synchronizes all reads and writes to the cached Arabic dashboard
        // schedules used by Dashboard, Telegram, and Current Movies partial files.
        private readonly object _arabicDashboardCacheLock =
                new object();

        private readonly Dictionary<string, DaySchedule> _arabicDashboardSchedulesByKey =
                new Dictionary<string, DaySchedule>(
                    StringComparer.OrdinalIgnoreCase);

        private readonly object _bookingUrlCacheLock =
                new object();

        private readonly Dictionary<string, BookingUrlCacheEntry> _bookingUrlCache =
                new Dictionary<string, BookingUrlCacheEntry>(
                    StringComparer.OrdinalIgnoreCase);

        // Coalesces concurrent requests for the same VOX booking URL.
        // All partial files share this dictionary through CinemaBotWorker.
        private readonly Dictionary<string, Task<string>> _bookingUrlResolveTasks =
                new Dictionary<string, Task<string>>(
                    StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _bookingUrlResolveGate =
                new SemaphoreSlim(12, 12);

        private readonly SemaphoreSlim _telegramMessageGate =
                new SemaphoreSlim(1, 1);

        private readonly Dictionary<string, DateTime> _lastTelegramMessageUtcByChat =
                new Dictionary<string, DateTime>(
                    StringComparer.OrdinalIgnoreCase);

        private DateTime _lastTelegramGlobalMessageUtc =
                DateTime.MinValue;

        private readonly Channel<TelegramQueuedRequest> _telegramAlertQueue =
                Channel.CreateUnbounded<TelegramQueuedRequest>(
                    new UnboundedChannelOptions
                    {
                        SingleReader =
                            true,

                        SingleWriter =
                            false,

                        AllowSynchronousContinuations =
                            false
                    });

        private static readonly string[] BotChallengeMarkers =
            {
            "Just a moment",
            "Enable JavaScript and cookies to continue",
            "Checking your browser before accessing",
            "cf-browser-verification",
            "cf_chl_",
            "Attention Required! | Cloudflare",
            "Request unsuccessful. Incapsula",
            "_Incapsula_Resource",
            "distil_r_captcha",
            "PerimeterX",
            "px-captcha",
            "Human Verification",
            "Pardon Our Interruption",
            "AWS WAF",
            "awswaf",
            "Access Denied",
            "Reference&#32;ID",
            "Reference #",
            "captcha-delivery.com"
        };

        private static readonly string[] WafRevealingHeaderNames =
            {
            "server",
            "cf-ray",
            "cf-mitigated",
            "x-iinfo",
            "x-cdn",
            "x-akamai-transformed",
            "x-px-block-reason",
            "x-datadome",
            "x-amzn-waf-action",
            "x-sucuri-id",
            "x-sucuri-cache"
        };

        public CinemaBotWorker(
                IConfiguration configuration,
                ILogger<CinemaBotWorker> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _stateStore = new PostgresStateStore(configuration, logger);

            var voxHandler =
                new HttpClientHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.GZip |
                        DecompressionMethods.Deflate |
                        DecompressionMethods.Brotli,

                    AllowAutoRedirect = true,
                    UseCookies = true,
                    CookieContainer =
                        new CookieContainer(),

                    MaxConnectionsPerServer =
                        12,

                    SslProtocols =
                        SslProtocols.Tls12 |
                        SslProtocols.Tls13
                };

            _voxHttpClient =
                new HttpClient(voxHandler)
                {
                    Timeout =
                        TimeSpan.FromSeconds(40),

                    DefaultRequestVersion =
                        HttpVersion.Version20,

                    DefaultVersionPolicy =
                        HttpVersionPolicy.RequestVersionOrLower
                };

            ConfigureVoxHeaders();

            var telegramHandler =
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

                    MaxConnectionsPerServer =
                        40,

                    ConnectTimeout =
                        TimeSpan.FromSeconds(8)
                };

            int telegramRequestTimeoutSeconds =
                ReadIntegerSetting(
                    "Telegram:RequestTimeoutSeconds",
                    defaultValue: 30,
                    minimumValue: 10,
                    maximumValue: 120);

            _telegramHttpClient =
                new HttpClient(
                    telegramHandler)
                {
                    Timeout =
                        TimeSpan.FromSeconds(
                            telegramRequestTimeoutSeconds),

                    DefaultRequestVersion =
                        HttpVersion.Version20,

                    DefaultVersionPolicy =
                        HttpVersionPolicy.RequestVersionOrLower
                };

            var telegramPollingHandler =
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

                    MaxConnectionsPerServer =
                        4,

                    ConnectTimeout =
                        TimeSpan.FromSeconds(10)
                };

            _telegramPollingHttpClient =
                new HttpClient(
                    telegramPollingHandler)
                {
                    // The request has its own linked CancellationTokenSource
                    // with a safety timeout greater than Telegram's server-side
                    // long-polling timeout.
                    Timeout =
                        System.Threading.Timeout.InfiniteTimeSpan,

                    DefaultRequestVersion =
                        HttpVersion.Version20,

                    DefaultVersionPolicy =
                        HttpVersionPolicy.RequestVersionOrLower
                };

            _dataDirectory =
                ResolveDataDirectory(
                    configuration);

            Directory.CreateDirectory(
                _dataDirectory);

            _preferencesFilePath =
                Path.Combine(
                    _dataDirectory,
                    "telegram-user-preferences.json");
        }

        protected override async Task ExecuteAsync(
                CancellationToken stoppingToken)
        {
            string appSettingsPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "appsettings.json");

            bool telegramIsValid = false;
            string validatedBotToken =
                string.Empty;

            await InitializePersistenceAsync(stoppingToken);

            LoadUserPreferences();
            LoadComingSoonState();
            LoadComingSoonPreferences();
            LoadCurrentMoviePreferences();
            LoadBotSubscriberIds();
            LoadAdminSettings();
            LoadTelegramUserActivity();

            SetHealthStatus(
                "Starting",
                "CinemaBot is starting.");

            Task heartbeatLoop =
                RunHeartbeatLoopAsync(
                    stoppingToken);

            Task telegramUpdateLoop =
                RunTelegramUpdateLoopAsync(
                    stoppingToken);

            Task telegramAlertSenderLoop =
                RunTelegramAlertSenderAsync(
                    stoppingToken);

            _logger.LogInformation(
                "Application directory: {ApplicationDirectory}",
                AppContext.BaseDirectory);

            _logger.LogInformation(
                "Expected appsettings.json path: {AppSettingsPath}",
                appSettingsPath);

            _logger.LogInformation(
                "VOX Cinema Radar is running continuously. " +
                "Cinema branches are discovered automatically from the VOX page. " +
                "Persistent user state is stored in PostgreSQL when DATABASE_URL is configured; runtime caches remain in memory.");

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime cycleStartedUtc =
                    DateTime.UtcNow;

                int checkIntervalSeconds =
                    ReadIntegerSetting(
                        "CheckIntervalSeconds",
                        defaultValue: 60,
                        minimumValue: 10,
                        maximumValue: 3600);

                try
                {
                    string baseVoxUrl =
                        (_configuration["Vox:Url"] ??
                         string.Empty).Trim();

                    string botToken =
                        NormalizeBotToken(
                            _configuration[
                                "Telegram:BotToken"]);

                    string chatId =
                        (_configuration[
                            "Telegram:ChatId"] ??
                         string.Empty).Trim();

                    int maximumConcurrentRequests =
                        ReadIntegerSetting(
                            "Scan:MaximumConcurrentRequests",
                            defaultValue: 3,
                            minimumValue: 1,
                            maximumValue: 6);

                    bool voxConfigurationIsValid =
                        IsValidVoxBaseUrl(
                            baseVoxUrl);

                    bool telegramConfigurationIsValid =
                        IsValidBotTokenFormat(
                            botToken) &&
                        !string.IsNullOrWhiteSpace(
                            chatId);

                    if (!voxConfigurationIsValid)
                    {
                        _logger.LogError(
                            "Vox:Url is missing or invalid. " +
                            "Use the general showtimes URL without a fixed cinema or date: " +
                            "https://egy.voxcinemas.com/showtimes");
                    }

                    if (!IsValidBotTokenFormat(
                            botToken))
                    {
                        _logger.LogError(
                            "Telegram BotToken is missing or invalid. " +
                            "Check {AppSettingsPath}.",
                            appSettingsPath);

                        telegramIsValid = false;
                        validatedBotToken =
                            string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(
                            chatId))
                    {
                        _logger.LogError(
                            "Telegram ChatId is missing.");

                        telegramIsValid = false;
                    }

                    if (telegramConfigurationIsValid &&
                        (!telegramIsValid ||
                         !string.Equals(
                             validatedBotToken,
                             botToken,
                             StringComparison.Ordinal)))
                    {
                        telegramIsValid =
                            await ValidateTelegramBotAsync(
                                botToken,
                                stoppingToken);

                        validatedBotToken =
                            telegramIsValid
                                ? botToken
                                : string.Empty;
                    }

                    if (voxConfigurationIsValid)
                    {
                        await ScanAllCinemasCurrentMonthAsync(
                            baseVoxUrl,
                            botToken,
                            chatId,
                            telegramIsValid &&
                            telegramConfigurationIsValid,
                            maximumConcurrentRequests,
                            stoppingToken);

                        SetHealthStatus(
                            "Healthy",
                            "Cinema scans, Telegram updates, and background queues are running.");
                    }
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetHealthStatus(
                        "Degraded",
                        LimitText(
                            ex.Message,
                            500));

                    _logger.LogError(
                        ex,
                        "Unexpected error during the VOX all-cinema scan. " +
                        "The EXE is still running and will retry.");
                }

                TimeSpan cycleElapsed =
                    DateTime.UtcNow -
                    cycleStartedUtc;

                TimeSpan remainingDelay =
                    TimeSpan.FromSeconds(
                        checkIntervalSeconds) -
                    cycleElapsed;

                if (remainingDelay <
                    TimeSpan.FromSeconds(1))
                {
                    remainingDelay =
                        TimeSpan.FromSeconds(1);
                }

                _logger.LogInformation(
                    "Next VOX all-cinema scan in {DelaySeconds} second(s). " +
                    "Press Ctrl+C to stop.",
                    (int)Math.Ceiling(
                        remainingDelay.TotalSeconds));

                try
                {
                    await WaitForNextScanAsync(
                        remainingDelay,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            try
            {
                await Task.WhenAll(
                    telegramUpdateLoop,
                    telegramAlertSenderLoop,
                    heartbeatLoop);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Expected during Ctrl+C or shutdown.
            }

            WriteHealthState(
                "Stopped",
                "CinemaBot stopped gracefully.",
                gracefulShutdown: true);

            _logger.LogInformation(
                "CinemaBot stopped.");
        }

        public override void Dispose()
        {
            _voxHttpClient.Dispose();
            _telegramHttpClient.Dispose();
            _telegramPollingHttpClient.Dispose();

            base.Dispose();
        }
    }
}
