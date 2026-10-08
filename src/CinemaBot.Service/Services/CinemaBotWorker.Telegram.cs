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

        // Admin statistics / channel welcome state.
        private readonly object _telegramAdminStateLock =
                new object();

        private readonly Dictionary<long, DateTime> _telegramUserLastSeenUtc =
                new Dictionary<long, DateTime>();

        private readonly HashSet<long> _channelWelcomeSentUsers =
                new HashSet<long>();

        private bool _telegramActivityLoaded;

        private DateTime _lastTelegramActivitySaveUtc =
                DateTime.MinValue;

        private int? _adminMaxBotUsersOverride;

        private readonly HashSet<long> _dynamicAdminUserIds = new HashSet<long>();

        private readonly HashSet<long> _bannedUserIds = new HashSet<long>();

        // Exceptional early booking-date alerts. Regular users use the global
        // delay, while admins/super admins always receive these alerts immediately.
        // Per-user values: -1 = disabled, 0+ = explicit delay in minutes.
        private readonly Dictionary<long, int> _earlyBookingAlertUserOverrides =
            new Dictionary<long, int>();

        private bool _earlyBookingAlertsEnabled = true;

        private int _earlyBookingAlertDelayMinutes = 5;

        private readonly object _earlyBookingAlertLock =
            new object();

        private bool _adminLimitLoaded;

        private readonly Dictionary<string, long> _telegramUsernameToUserId =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<long, string> _telegramUsernames =
                new Dictionary<long, string>();

        private bool _telegramUsernameRegistryLoaded;

        private string AdminSettingsFilePath =>
            Path.Combine(
                _dataDirectory,
                "telegram-admin-settings.json");

        private string TelegramActivityFilePath =>
            Path.Combine(
                _dataDirectory,
                "telegram-user-activity.json");

        private string ScanRangeSettingsFilePath =>
            Path.Combine(
                _dataDirectory,
                "scan-range-settings.json");

        private async Task<bool> DeleteTelegramWebhookAsync(
                string botToken,
                CancellationToken stoppingToken)
        {
            bool deleted =
                await PostTelegramJsonAsync(
                    botToken,
                    "deleteWebhook",
                    new
                    {
                        drop_pending_updates =
                            false
                    },
                    stoppingToken);

            if (deleted)
            {
                _logger.LogInformation(
                    "Telegram webhook is cleared. Long polling for button " +
                    "and preference updates is active.");
            }

            return deleted;
        }

        private async Task<bool> EnsureTelegramBotMenuAsync(
                string botToken,
                CancellationToken stoppingToken)
        {
            if (_telegramCommandsConfigured)
            {
                return true;
            }

            object[] englishCommands =
            {
                new
                {
                    command =
                        "start",

                    description =
                        "Open CinemaBot home"
                },

                new
                {
                    command =
                        "current",

                    description =
                        "Follow current movies"
                },

                new
                {
                    command =
                        "comingsoon",

                    description =
                        "Follow Coming Soon movies"
                },

                new
                {
                    command =
                        "cinemas",

                    description =
                        "Browse live cinema showtimes"
                },

                new
                {
                    command =
                        "alerts",

                    description =
                        "Manage my alerts"
                }
            };

            object[] arabicCommands =
            {
                new
                {
                    command =
                        "start",

                    description =
                        "فتح القائمة الرئيسية"
                },

                new
                {
                    command =
                        "current",

                    description =
                        "متابعة الأفلام الحالية"
                },

                new
                {
                    command =
                        "comingsoon",

                    description =
                        "متابعة أفلام قريباً"
                },

                new
                {
                    command =
                        "cinemas",

                    description =
                        "تصفح مواعيد السينمات المباشرة"
                },

                new
                {
                    command =
                        "alerts",

                    description =
                        "إدارة تنبيهاتي"
                }
            };

            bool englishConfigured =
                await PostTelegramJsonAsync(
                    botToken,
                    "setMyCommands",
                    new
                    {
                        commands =
                            englishCommands,

                        scope =
                            new
                            {
                                type =
                                    "all_private_chats"
                            },

                        language_code =
                            string.Empty
                    },
                    stoppingToken);

            bool arabicConfigured =
                await PostTelegramJsonAsync(
                    botToken,
                    "setMyCommands",
                    new
                    {
                        commands =
                            arabicCommands,

                        scope =
                            new
                            {
                                type =
                                    "all_private_chats"
                            },

                        language_code =
                            "ar"
                    },
                    stoppingToken);

            bool menuConfigured =
                await PostTelegramJsonAsync(
                    botToken,
                    "setChatMenuButton",
                    new
                    {
                        menu_button =
                            new
                            {
                                type =
                                    "commands"
                            }
                    },
                    stoppingToken);

            bool adminMenusConfigured =
                true;

            foreach (long adminUserId in GetConfiguredAdminUserIds())
            {
                if (adminUserId <= 0)
                {
                    continue;
                }

                bool isSuperAdmin = IsSuperAdminUser(adminUserId);

                var englishAdminCommandList =
                    new List<object>(
                        englishCommands)
                    {
                        new
                        {
                            command = "admin",
                            description = "Open admin statistics"
                        },
                        new
                        {
                            command = "myid",
                            description = "Show my Telegram ID"
                        },
                        new
                        {
                            command = "setlimit",
                            description = "Set bot user limit"
                        }
                    };

                if (isSuperAdmin)
                {
                    englishAdminCommandList.Add(
                        new
                        {
                            command = "addadmin",
                            description = "Add an admin by username"
                        });
                    englishAdminCommandList.Add(
                        new
                        {
                            command = "removeadmin",
                            description = "Remove an admin by username"
                        });
                    englishAdminCommandList.Add(new { command = "scan", description = "Set scan range" });
                    englishAdminCommandList.Add(new { command = "ban", description = "Ban a user" });
                    englishAdminCommandList.Add(new { command = "unban", description = "Unban a user" });
                    englishAdminCommandList.Add(new { command = "users", description = "List known users" });
                    englishAdminCommandList.Add(new { command = "earlyalerts", description = "Early booking alerts" });
                }

                object[] englishAdminCommands =
                    englishAdminCommandList.ToArray();

                var arabicAdminCommandList =
                    new List<object>(
                        arabicCommands)
                    {
                        new
                        {
                            command = "admin",
                            description = "فتح لوحة الإدارة"
                        },
                        new
                        {
                            command = "myid",
                            description = "عرض Telegram ID"
                        },
                        new
                        {
                            command = "setlimit",
                            description = "تحديد حد مستخدمي البوت"
                        }
                    };

                if (isSuperAdmin)
                {
                    arabicAdminCommandList.Add(
                        new
                        {
                            command = "addadmin",
                            description = "إضافة أدمن باليوزرنيم"
                        });
                    arabicAdminCommandList.Add(
                        new
                        {
                            command = "removeadmin",
                            description = "حذف أدمن باليوزرنيم"
                        });
                    arabicAdminCommandList.Add(new { command = "scan", description = "تحديد نطاق الفحص" });
                    arabicAdminCommandList.Add(new { command = "ban", description = "حظر مستخدم" });
                    arabicAdminCommandList.Add(new { command = "unban", description = "إلغاء حظر مستخدم" });
                    arabicAdminCommandList.Add(new { command = "users", description = "عرض المستخدمين المعروفين" });
                    arabicAdminCommandList.Add(new { command = "earlyalerts", description = "تنبيهات الحجز المبكر" });
                }

                object[] arabicAdminCommands =
                    arabicAdminCommandList.ToArray();

                bool englishAdminConfigured =
                    await PostTelegramJsonAsync(
                        botToken,
                        "setMyCommands",
                        new
                        {
                            commands =
                                englishAdminCommands,
                            scope =
                                new
                                {
                                    type =
                                        "chat",
                                    chat_id =
                                        adminUserId
                                },
                            language_code =
                                string.Empty
                        },
                        stoppingToken);

                bool arabicAdminConfigured =
                    await PostTelegramJsonAsync(
                        botToken,
                        "setMyCommands",
                        new
                        {
                            commands =
                                arabicAdminCommands,
                            scope =
                                new
                                {
                                    type =
                                        "chat",
                                    chat_id =
                                        adminUserId
                                },
                            language_code =
                                "ar"
                        },
                        stoppingToken);

                adminMenusConfigured &=
                    englishAdminConfigured &&
                    arabicAdminConfigured;
            }

            if (!adminMenusConfigured)
            {
                _logger.LogWarning(
                    "One or more Telegram admin command menus could not be configured.");
            }

            _telegramCommandsConfigured =
                englishConfigured &&
                arabicConfigured &&
                menuConfigured;

            if (_telegramCommandsConfigured)
            {
                _logger.LogInformation(
                    "Telegram private-chat command menu configured successfully.");
            }
            else
            {
                _logger.LogWarning(
                    "Telegram bot is available, but its private-chat command menu could not be fully configured.");
            }

            return _telegramCommandsConfigured;
        }

        private async Task RunTelegramUpdateLoopAsync(
                CancellationToken stoppingToken)
        {
            int consecutiveFailures =
                0;

            while (!stoppingToken.IsCancellationRequested)
            {
                string botToken =
                    NormalizeBotToken(
                        _configuration[
                            "Telegram:BotToken"]);

                if (!IsValidBotTokenFormat(
                        botToken))
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);

                    continue;
                }

                int longPollingTimeoutSeconds =
                    ReadIntegerSetting(
                        "Telegram:LongPollingTimeoutSeconds",
                        defaultValue: 25,
                        minimumValue: 5,
                        maximumValue: 50);

                int longPollingGraceSeconds =
                    ReadIntegerSetting(
                        "Telegram:LongPollingGraceSeconds",
                        defaultValue: 20,
                        minimumValue: 5,
                        maximumValue: 60);

                int safetyTimeoutSeconds =
                    longPollingTimeoutSeconds +
                    longPollingGraceSeconds;

                try
                {
                    if (!_telegramWebhookCleared)
                    {
                        _telegramWebhookCleared =
                            await DeleteTelegramWebhookAsync(
                                botToken,
                                stoppingToken);

                        if (!_telegramWebhookCleared)
                        {
                            await Task.Delay(
                                TimeSpan.FromSeconds(3),
                                stoppingToken);

                            continue;
                        }
                    }

                    string apiUrl =
                        $"https://api.telegram.org/bot{botToken}/getUpdates";

                    using var content =
                        new FormUrlEncodedContent(
                            new Dictionary<string, string>
                            {
                                ["offset"] =
                                    _telegramUpdateOffset.ToString(
                                        CultureInfo.InvariantCulture),

                                ["timeout"] =
                                    longPollingTimeoutSeconds.ToString(
                                        CultureInfo.InvariantCulture),

                                ["limit"] =
                                    "100",

                                ["allowed_updates"] =
                                    "[\"message\",\"callback_query\"]"
                            });

                    using var request =
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            apiUrl)
                        {
                            Content =
                                content
                        };

                    using var pollCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            stoppingToken);

                    // This is deliberately greater than Telegram's own timeout.
                    // It removes the old 20-seconds-vs-20-seconds cancellation race.
                    pollCancellation.CancelAfter(
                        TimeSpan.FromSeconds(
                            safetyTimeoutSeconds));

                    using HttpResponseMessage response =
                        await _telegramPollingHttpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            pollCancellation.Token);

                    string responseBody =
                        await response.Content.ReadAsStringAsync(
                            pollCancellation.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        consecutiveFailures++;

                        _logger.LogWarning(
                            "Telegram getUpdates failed. HTTP {StatusCode}: {Response}",
                            (int)response.StatusCode,
                            LimitText(
                                responseBody,
                                500));

                        await DelayTelegramPollingRetryAsync(
                            consecutiveFailures,
                            stoppingToken);

                        continue;
                    }

                    consecutiveFailures =
                        0;

                    using JsonDocument document =
                        JsonDocument.Parse(
                            responseBody);

                    if (!document.RootElement.TryGetProperty(
                            "result",
                            out JsonElement results) ||
                        results.ValueKind !=
                        JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement update in
                             results.EnumerateArray())
                    {
                        if (update.TryGetProperty(
                                "update_id",
                                out JsonElement updateIdElement))
                        {
                            _telegramUpdateOffset =
                                updateIdElement.GetInt64() +
                                1;
                        }

                        if (update.TryGetProperty(
                                "message",
                                out JsonElement message))
                        {
                            await HandleTelegramMessageAsync(
                                botToken,
                                message,
                                stoppingToken);
                        }
                        else if (update.TryGetProperty(
                                     "callback_query",
                                     out JsonElement callbackQuery))
                        {
                            await HandleTelegramCallbackAsync(
                                botToken,
                                callbackQuery,
                                stoppingToken);
                        }
                    }

                    if (results.GetArrayLength() > 0)
                    {
                        SaveTelegramUpdateOffset();
                    }
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // The local safety timeout is expected recovery behavior,
                    // not an application failure. Reconnect without a warning.
                    consecutiveFailures =
                        0;

                    _logger.LogDebug(
                        "Telegram long polling safety timeout elapsed; reconnecting.");
                }
                catch (HttpRequestException ex)
                {
                    consecutiveFailures++;

                    int retryDelayMilliseconds =
                        CalculateTelegramPollingRetryMilliseconds(
                            consecutiveFailures);

                    if (consecutiveFailures == 1 ||
                        consecutiveFailures % 10 == 0)
                    {
                        _logger.LogWarning(
                            ex,
                            "Telegram long polling has a temporary network failure. " +
                            "Retrying in {RetryDelayMilliseconds} ms.",
                            retryDelayMilliseconds);
                    }
                    else
                    {
                        _logger.LogDebug(
                            ex,
                            "Telegram long polling retry {FailureCount}.",
                            consecutiveFailures);
                    }

                    await Task.Delay(
                        retryDelayMilliseconds,
                        stoppingToken);
                }
                catch (JsonException ex)
                {
                    consecutiveFailures++;

                    _logger.LogWarning(
                        ex,
                        "Telegram returned an invalid getUpdates response. " +
                        "The polling loop will reconnect.");

                    await DelayTelegramPollingRetryAsync(
                        consecutiveFailures,
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;

                    _logger.LogWarning(
                        ex,
                        "Telegram update loop encountered a temporary failure. " +
                        "It will reconnect automatically.");

                    await DelayTelegramPollingRetryAsync(
                        consecutiveFailures,
                        stoppingToken);
                }
            }
        }

        private static int CalculateTelegramPollingRetryMilliseconds(
                int consecutiveFailures)
        {
            int normalizedFailureCount =
                Math.Max(
                    1,
                    Math.Min(
                        consecutiveFailures,
                        6));

            return Math.Min(
                250 * (1 << (normalizedFailureCount - 1)),
                5000);
        }

        private static async Task DelayTelegramPollingRetryAsync(
                int consecutiveFailures,
                CancellationToken stoppingToken)
        {
            await Task.Delay(
                CalculateTelegramPollingRetryMilliseconds(
                    consecutiveFailures),
                stoppingToken);
        }

        private async Task HandleTelegramMessageAsync(
                string botToken,
                JsonElement message,
                CancellationToken stoppingToken)
        {
            if (!message.TryGetProperty(
                    "chat",
                    out JsonElement chat) ||
                !chat.TryGetProperty(
                    "id",
                    out JsonElement chatIdElement))
            {
                return;
            }

            string chatType =
                chat.TryGetProperty(
                    "type",
                    out JsonElement chatTypeElement)
                    ? chatTypeElement.GetString() ??
                      string.Empty
                    : string.Empty;

            if (!string.Equals(
                    chatType,
                    "private",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long privateChatId =
                chatIdElement.GetInt64();

            int? incomingMessageId =
                message.TryGetProperty(
                    "message_id",
                    out JsonElement messageIdElement)
                    ? messageIdElement.GetInt32()
                    : null;

            long userId =
                privateChatId;

            string firstName =
                string.Empty;

            if (message.TryGetProperty(
                    "from",
                    out JsonElement from))
            {
                if (from.TryGetProperty(
                        "id",
                        out JsonElement userIdElement))
                {
                    userId =
                        userIdElement.GetInt64();
                }

                if (from.TryGetProperty(
                        "first_name",
                        out JsonElement firstNameElement))
                {
                    firstName =
                        firstNameElement.GetString() ??
                        string.Empty;
                }
            }

            string messageText =
                message.TryGetProperty(
                    "text",
                    out JsonElement textElement)
                    ? textElement.GetString() ??
                      string.Empty
                    : string.Empty;

            bool buttonsOnlyMode =
                ReadBooleanSetting(
                    "Telegram:ButtonsOnlyMode",
                    defaultValue: true);

            bool deleteTypedMessages =
                ReadBooleanSetting(
                    "Telegram:DeleteTypedMessages",
                    defaultValue: true);

            UserPreference preference =
                GetOrCreatePreference(
                    userId,
                    privateChatId,
                    firstName);

            RegisterTelegramUsername(
                userId,
                GetTelegramUsername(message));

            // Banned users are completely silent: do not send messages, alerts,
            // subscription prompts, or command errors. The channel ban is
            // enforced separately by Telegram via banChatMember.
            if (IsBannedUser(userId))
            {
                return;
            }

            // Always record the latest interaction before any command or
            // subscription gate. This keeps /users Last Use accurate.
            RegisterTelegramUserActivity(userId);

            // Temporary/admin commands are handled before the subscription gate.
            // This lets the owner recover their ID or open the admin panel even
            // when the channel-subscription state needs attention.
            string commandBeforeGate =
                GetTelegramCommand(messageText);

            if (IsAdminUser(userId) &&
                (string.Equals(
                     commandBeforeGate,
                     "/admin",
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     commandBeforeGate,
                     "/stats",
                     StringComparison.OrdinalIgnoreCase)))
            {
                if (buttonsOnlyMode &&
                    deleteTypedMessages &&
                    incomingMessageId.HasValue)
                {
                    await DeleteIncomingPrivateMessageAsync(
                        botToken,
                        privateChatId,
                        incomingMessageId.Value,
                        stoppingToken);
                }

                await SendAdminDashboardAsync(
                    botToken,
                    privateChatId,
                    preference.Language,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    commandBeforeGate,
                    "/myid",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (buttonsOnlyMode &&
                    deleteTypedMessages &&
                    incomingMessageId.HasValue)
                {
                    await DeleteIncomingPrivateMessageAsync(
                        botToken,
                        privateChatId,
                        incomingMessageId.Value,
                        stoppingToken);
                }

                await TelegramMyIdCommand.HandleAsync(
                    SendTelegramMessageForMyIdAsync,
                    _logger,
                    userId,
                    privateChatId,
                    GetTelegramUsername(message),
                    firstName,
                    IsAdminUser(userId),
                    stoppingToken);

                return;
            }

            if (string.Equals(commandBeforeGate, "/setlimit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandBeforeGate, "/limit", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        "⛔ <b>الأمر ده للـ Admin فقط.</b>",
                        stoppingToken);
                    return;
                }

                string rawLimit = GetTelegramCommandArgument(messageText);
                if (!int.TryParse(rawLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestedLimit) ||
                    requestedLimit < 0 || requestedLimit > 100000)
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        "اكتب رقم صحيح من 0 إلى 100000. مثال: <code>/setlimit 1500</code>\n\nاكتب <code>0</code> لو عايز تلغي الحد.",
                        stoppingToken);
                    return;
                }

                SetMaxBotUsersLimit(requestedLimit);
                await SendSimpleTelegramHtmlAsync(
                    botToken,
                    privateChatId,
                    requestedLimit == 0
                        ? "✅ تم إلغاء حد مستخدمي البوت."
                        : $"✅ تم تحديد الحد الأقصى لمستخدمي البوت على <b>{requestedLimit:N0}</b>.",
                    stoppingToken);
                return;
            }

            if (string.Equals(commandBeforeGate, "/scan", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSuperAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId, "⛔ <b>الأمر ده للـ Super Admin فقط.</b>", stoppingToken);
                    return;
                }

                string argument = GetTelegramCommandArgument(messageText);
                string[] parts = argument.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                bool changed = false;
                string summary = string.Empty;

                if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days >= 1 && days <= 180)
                {
                    SaveScanRange(days, null, null);
                    summary = $"✅ تم ضبط الفحص على <b>{days:N0} يوم</b> من النهارده.";
                    changed = true;
                }
                else if (parts.Length == 1 && string.Equals(parts[0], "current", StringComparison.OrdinalIgnoreCase))
                {
                    SaveScanRange(null, "current", null);
                    summary = "✅ تم ضبط الفحص على باقي الشهر الحالي.";
                    changed = true;
                }
                else if (parts.Length == 1 && string.Equals(parts[0], "nextmonth", StringComparison.OrdinalIgnoreCase))
                {
                    SaveScanRange(null, "nextmonth", null);
                    summary = "✅ تم ضبط الفحص على الشهر القادم بالكامل.";
                    changed = true;
                }
                else if (parts.Length == 2 && string.Equals(parts[0], "day", StringComparison.OrdinalIgnoreCase) &&
                         DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime scanDate))
                {
                    SaveScanRange(null, null, scanDate.Date);
                    summary = $"✅ تم ضبط الفحص على يوم <b>{scanDate:yyyy-MM-dd}</b>.";
                    changed = true;
                }

                if (!changed)
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId,
                        "❌ مثال: <code>/scan 30</code> أو <code>/scan 40</code> أو <code>/scan current</code> أو <code>/scan nextmonth</code> أو <code>/scan day 2026-09-15</code>.", stoppingToken);
                    return;
                }

                RequestImmediateScan();
                await SendSimpleTelegramHtmlAsync(botToken, privateChatId, summary + "\n🔄 بدأ الفحص بالنطاق الجديد.", stoppingToken);
                return;
            }

            if (string.Equals(commandBeforeGate, "/ban", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandBeforeGate, "/unban", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSuperAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId, "⛔ <b>الأمر ده للـ Super Admin فقط.</b>", stoppingToken);
                    return;
                }

                string targetArgument = GetTelegramCommandArgument(messageText).Trim();
                if (string.IsNullOrWhiteSpace(targetArgument))
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId,
                        string.Equals(commandBeforeGate, "/ban", StringComparison.OrdinalIgnoreCase)
                            ? "اكتب: <code>/ban @username</code> أو <code>/ban TELEGRAM_ID</code>"
                            : "اكتب: <code>/unban @username</code> أو <code>/unban TELEGRAM_ID</code>", stoppingToken);
                    return;
                }

                LoadAdminSettings();
                long targetId = 0;
                string resolvedUsername = NormalizeTelegramUsername(targetArgument);
                if (!long.TryParse(targetArgument.TrimStart('@'), NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId) || targetId <= 0)
                {
                    targetId = 0;
                    lock (_telegramAdminStateLock)
                    {
                        _telegramUsernameToUserId.TryGetValue(resolvedUsername, out targetId);
                    }
                }

                if (targetId <= 0)
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId,
                        $"❌ مش لاقي <b>{EscapeTelegramHtml(targetArgument)}</b>. استخدم Telegram ID أو username لمستخدم معروف للبوت.", stoppingToken);
                    return;
                }

                lock (_telegramAdminStateLock)
                {
                    if (_telegramUsernames.TryGetValue(targetId, out string knownUsername) && !string.IsNullOrWhiteSpace(knownUsername))
                        resolvedUsername = knownUsername;
                }

                if (IsSuperAdminUser(targetId))
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId, "⛔ مينفعش حظر الـSuper Admin.", stoppingToken);
                    return;
                }

                bool ban = string.Equals(commandBeforeGate, "/ban", StringComparison.OrdinalIgnoreCase);
                bool apiOk = await SetTelegramChannelBanAsync(botToken, targetId, ban, stoppingToken);
                if (apiOk)
                {
                    lock (_telegramAdminStateLock)
                    {
                        if (ban) _bannedUserIds.Add(targetId); else _bannedUserIds.Remove(targetId);
                        SaveAdminSettingsUnsafe();
                    }
                    RemoveKnownBotSubscriber(targetId);
                    string displayTarget = !string.IsNullOrWhiteSpace(resolvedUsername)
                        ? "@" + resolvedUsername
                        : targetId.ToString(CultureInfo.InvariantCulture);
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId,
                        ban ? $"🚫 تم حظر <b>{EscapeTelegramHtml(displayTarget)}</b> من القناة ومن استخدام البوت." : $"✅ تم إلغاء حظر <b>{EscapeTelegramHtml(displayTarget)}</b>.", stoppingToken);
                }
                else
                {
                    await SendSimpleTelegramHtmlAsync(botToken, privateChatId, "❌ Telegram رفض العملية. تأكد إن البوت Admin في القناة ومعاه صلاحية حظر الأعضاء.", stoppingToken);
                }
                return;
            }

            if (string.Equals(commandBeforeGate, "/users", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSuperAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        "⛔ <b>الأمر ده للـ Super Admin فقط.</b>",
                        stoppingToken);
                    return;
                }

                if (buttonsOnlyMode && deleteTypedMessages && incomingMessageId.HasValue)
                {
                    await DeleteIncomingPrivateMessageAsync(
                        botToken,
                        privateChatId,
                        incomingMessageId.Value,
                        stoppingToken);
                }

                int page = 0;
                string usersArgument = GetTelegramCommandArgument(messageText);
                if (!string.IsNullOrWhiteSpace(usersArgument))
                {
                    int.TryParse(usersArgument, NumberStyles.Integer, CultureInfo.InvariantCulture, out page);
                    page = Math.Max(0, page);
                }

                await SendKnownUsersPageAsync(
                    botToken,
                    privateChatId,
                    preference.Language,
                    page,
                    stoppingToken);
                return;
            }

            if (string.Equals(commandBeforeGate, "/earlyalerts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandBeforeGate, "/earlyalert", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSuperAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        "⛔ <b>Early booking alerts are controlled by the Super Admin only.</b>",
                        stoppingToken);
                    return;
                }

                await HandleEarlyBookingAlertsCommandAsync(
                    botToken,
                    privateChatId,
                    GetTelegramCommandArgument(messageText),
                    stoppingToken);
                return;
            }

            if (string.Equals(commandBeforeGate, "/addadmin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandBeforeGate, "/removeadmin", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSuperAdminUser(userId))
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        "⛔ <b>الأمر ده للـ Super Admin فقط.</b>",
                        stoppingToken);
                    return;
                }

                string argument = GetTelegramCommandArgument(messageText);
                string username = NormalizeTelegramUsername(argument);

                if (string.IsNullOrWhiteSpace(username))
                {
                    await SendSimpleTelegramHtmlAsync(
                        botToken,
                        privateChatId,
                        string.Equals(commandBeforeGate, "/addadmin", StringComparison.OrdinalIgnoreCase)
                            ? "اكتب اليوزرنيم كده: <code>/addadmin @username</code>"
                            : "اكتب اليوزرنيم كده: <code>/removeadmin @username</code>",
                        stoppingToken);
                    return;
                }

                await HandleAdminUserManagementCommandAsync(
                    botToken,
                    privateChatId,
                    userId,
                    commandBeforeGate,
                    username,
                    stoppingToken);
                return;
            }

            // A plain /start is an unconditional entry point. It must work for
            // new/old users, admins, super admins, and users whose local history
            // was cleared. The subscription gate applies to actual bot features
            // after the network picker, not to opening this screen.
            if (string.Equals(commandBeforeGate, "/start", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(GetTelegramStartParameter(messageText)))
            {
                if (buttonsOnlyMode && deleteTypedMessages && incomingMessageId.HasValue)
                {
                    await DeleteIncomingPrivateMessageAsync(
                        botToken, privateChatId, incomingMessageId.Value, stoppingToken);
                }

                await SendProviderSelectionAsync(
                    botToken, preference, stoppingToken);
                return;
            }

            // v1.2.2 Channel + Bot subscription gate.
            if (ReadBooleanSetting(
                    "Telegram:RequireChannelSubscription",
                    defaultValue: false))
            {
                bool subscribed =
                    await EnsureUserCanUseBotAsync(
                        botToken,
                        userId,
                        privateChatId,
                        preference,
                        stoppingToken);

                if (!subscribed)
                {
                    return;
                }

                await SendChannelWelcomeOnceAsync(
                    botToken,
                    userId,
                    privateChatId,
                    preference.Language,
                    stoppingToken);

            }

            // Telegram does not provide a Bot API setting that removes the
            // user's message composer in a normal private bot chat.
            //
            // Buttons-only mode keeps the chat clean by deleting every incoming
            // private message immediately. This includes text, photos, stickers,
            // voice notes, documents and the automatic /start message.
            //
            // /start is still processed after deletion so deep links and all
            // button navigation continue to work normally.
            if (buttonsOnlyMode &&
                deleteTypedMessages &&
                incomingMessageId.HasValue)
            {
                await DeleteIncomingPrivateMessageAsync(
                    botToken,
                    privateChatId,
                    incomingMessageId.Value,
                    stoppingToken);
            }

            string command =
                GetTelegramCommand(
                    messageText);

            if (!string.IsNullOrWhiteSpace(
                    command))
            {
                string startParameter =
                    string.Equals(
                        command,
                        "/start",
                        StringComparison.OrdinalIgnoreCase)
                        ? GetTelegramStartParameter(
                            messageText)
                        : string.Empty;

                RequestImmediateScan();

                if (string.Equals(
                        command,
                        "/start",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        startParameter,
                        "comingsoon",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SendComingSoonMenuAsync(
                        botToken,
                        preference,
                        page: 0,
                        stoppingToken);

                    return;
                }

                if ((string.Equals(
                         command,
                         "/start",
                         StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         startParameter,
                         "currentmovies",
                         StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(
                        command,
                        "/current",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        command,
                        "/movies",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SendCurrentMoviesMenuAsync(
                        botToken,
                        preference,
                        page: 0,
                        stoppingToken);

                    return;
                }

                if (string.Equals(
                        command,
                        "/comingsoon",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        command,
                        "/coming",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SendComingSoonMenuAsync(
                        botToken,
                        preference,
                        page: 0,
                        stoppingToken);

                    return;
                }

                if ((string.Equals(
                         command,
                         "/start",
                         StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         startParameter,
                         "browse",
                         StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(
                        command,
                        "/cinemas",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        command,
                        "/showtimes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SendBrowseCinemasAsync(
                        botToken,
                        preference,
                        stoppingToken);

                    return;
                }

                if (string.Equals(
                        command,
                        "/alerts",
                        StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(
                         command,
                         "/start",
                         StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(
                         startParameter,
                         "setup",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    await SendButtonsOnlyReminderAsync(
                        botToken,
                        preference,
                        stoppingToken);

                    return;
                }

                if (string.Equals(
                        command,
                        "/start",
                        StringComparison.OrdinalIgnoreCase) &&
                    startParameter.StartsWith(
                        "cin_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string cinemaSlug =
                        startParameter.Substring(
                            "cin_".Length);

                    await SendCinemaDatesAsync(
                        botToken,
                        preference,
                        cinemaSlug,
                        stoppingToken);

                    return;
                }

                if (string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase))
                {
                    // A plain /start is always a clean entry point and must not
                    // depend on saved preferences/history.
                    await SendProviderSelectionAsync(
                        botToken,
                        preference,
                        stoppingToken);
                    return;
                }

                if (string.Equals(command, "/home", StringComparison.OrdinalIgnoreCase))
                {
                    await SendBotHomeAsync(
                        botToken,
                        preference,
                        stoppingToken);
                    return;
                }
            }

            if (buttonsOnlyMode)
            {
                // Do not send a reminder or any additional outgoing message.
                // The incoming user message has already been deleted.
                return;
            }

            // Normal mode can be restored later from appsettings.json.
            await SendButtonsOnlyReminderAsync(
                botToken,
                preference,
                stoppingToken);
        }

        private static string GetTelegramCommand(
                string messageText)
        {
            if (string.IsNullOrWhiteSpace(
                    messageText))
            {
                return string.Empty;
            }

            string firstToken =
                messageText
                    .Trim()
                    .Split(
                        new[]
                        {
                            ' ',
                            '\t',
                            '\r',
                            '\n'
                        },
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ??
                string.Empty;

            if (!firstToken.StartsWith(
                    "/",
                    StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int mentionIndex =
                firstToken.IndexOf(
                    '@');

            if (mentionIndex > 0)
            {
                firstToken =
                    firstToken.Substring(
                        0,
                        mentionIndex);
            }

            return firstToken.ToLowerInvariant();
        }

        private async Task HandleTelegramCallbackAsync(
                string botToken,
                JsonElement callbackQuery,
                CancellationToken stoppingToken)
        {
            string callbackId =
                callbackQuery.TryGetProperty(
                    "id",
                    out JsonElement callbackIdElement)
                    ? callbackIdElement.GetString() ??
                      string.Empty
                    : string.Empty;

            string data =
                callbackQuery.TryGetProperty(
                    "data",
                    out JsonElement dataElement)
                    ? dataElement.GetString() ??
                      string.Empty
                    : string.Empty;

            if (!callbackQuery.TryGetProperty(
                    "from",
                    out JsonElement from) ||
                !from.TryGetProperty(
                    "id",
                    out JsonElement userIdElement))
            {
                return;
            }

            long userId =
                userIdElement.GetInt64();

            string firstName =
                from.TryGetProperty(
                    "first_name",
                    out JsonElement firstNameElement)
                    ? firstNameElement.GetString() ??
                      string.Empty
                    : string.Empty;

            if (!callbackQuery.TryGetProperty(
                    "message",
                    out JsonElement message) ||
                !message.TryGetProperty(
                    "chat",
                    out JsonElement chat) ||
                !chat.TryGetProperty(
                    "id",
                    out JsonElement chatIdElement) ||
                !message.TryGetProperty(
                    "message_id",
                    out JsonElement messageIdElement))
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    "Please open the bot in a private chat.",
                    true,
                    stoppingToken);

                return;
            }

            string callbackChatType =
                chat.TryGetProperty(
                    "type",
                    out JsonElement callbackChatTypeElement)
                    ? callbackChatTypeElement.GetString() ??
                      string.Empty
                    : string.Empty;

            if (!string.Equals(
                    callbackChatType,
                    "private",
                    StringComparison.OrdinalIgnoreCase))
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    "Open CinemaBot privately to manage your movies.",
                    true,
                    stoppingToken);

                return;
            }

            long privateChatId =
                chatIdElement.GetInt64();

            // Banned users receive no visible bot alert. Answering the callback
            // silently only stops Telegram's loading spinner.
            if (IsBannedUser(userId))
            {
                await AnswerCallbackQueryAsync(botToken, callbackId, string.Empty, false, stoppingToken);
                return;
            }

            RegisterTelegramUserActivity(userId);

            int messageId =
                messageIdElement.GetInt32();

            InvalidatePendingMovieDetailsLoad(
                privateChatId,
                messageId);

            RequestImmediateScan();

            UserPreference preference =
                GetOrCreatePreference(
                    userId,
                    privateChatId,
                    firstName);

            if (ReadBooleanSetting(
                    "Telegram:RequireChannelSubscription",
                    defaultValue: false) &&
                !string.Equals(
                    data,
                    "subscription:check",
                    StringComparison.OrdinalIgnoreCase))
            {
                bool subscribed =
                    await EnsureUserCanUseBotAsync(
                        botToken,
                        userId,
                        privateChatId,
                        preference,
                        stoppingToken);

                if (!subscribed)
                {
                    await AnswerCallbackQueryAsync(
                        botToken,
                        callbackId,
                        preference.Language == "ar"
                            ? "برجاء الاشتراك في قناة CinemaBot أولاً."
                            : "Please subscribe to the CinemaBot channel first.",
                        true,
                        stoppingToken);

                    return;
                }

            }

            if (data.StartsWith(
                    "admin:",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleAdminCallbackAsync(
                    botToken,
                    callbackId,
                    data,
                    userId,
                    privateChatId,
                    preference,
                    messageId,
                    stoppingToken);

                return;
            }

            string callbackMessage =
                "Updated";

            bool showAlert =
                false;

            bool callbackAnswered =
                false;

            if (string.Equals(
        data,
        "subscription:check",
        StringComparison.OrdinalIgnoreCase))
            {
                bool subscribed =
                    await EnsureUserCanUseBotAsync(
                        botToken,
                        userId,
                        privateChatId,
                        preference,
                        stoppingToken,
                        forceRefresh: true);

                if (subscribed)
                {
                    await AnswerCallbackQueryAsync(
                        botToken,
                        callbackId,
                        preference.Language == "ar"
                            ? "تم تأكيد الاشتراك ✅"
                            : "Subscription confirmed ✅",
                        false,
                        stoppingToken);

                    await SendChannelWelcomeOnceAsync(
                        botToken,
                        userId,
                        privateChatId,
                        preference.Language,
                        stoppingToken);

                    await SendBotHomeAsync(
                        botToken,
                        preference,
                        stoppingToken);

                    RegisterTelegramUserActivity(userId);
                }
                else
                {
                    await AnswerCallbackQueryAsync(
                        botToken,
                        callbackId,
                        preference.Language == "ar"
                            ? "لم يتم العثور على اشتراكك بعد. اشترك ثم اضغط مرة أخرى."
                            : "Your subscription was not found yet. Subscribe, then try again.",
                        true,
                        stoppingToken);
                }

                callbackAnswered = true;
            }
            else if (data.StartsWith(
                    "noop:",
                    StringComparison.OrdinalIgnoreCase))
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    preference.Language == "ar"
                        ? "جاري تحميل روابط الحجز المباشرة..."
                        : "Loading live booking links...",
                    false,
                    stoppingToken);

                callbackAnswered =
                    true;
            }
            else if (data.StartsWith(
                    "coming:",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleComingSoonCallbackAsync(
                    botToken,
                    callbackId,
                    data,
                    preference,
                    messageId,
                    stoppingToken);

                callbackAnswered =
                    true;
            }
            else if (data.StartsWith(
                         "current:",
                         StringComparison.OrdinalIgnoreCase))
            {
                await HandleCurrentMoviesCallbackAsync(
                    botToken,
                    callbackId,
                    data,
                    preference,
                    messageId,
                    stoppingToken);

                callbackAnswered =
                    true;
            }
            else if (string.Equals(
                         data,
                         "networks:home",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditProviderSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر شبكة السينما"
                        : "Choose your cinema network";
            }
            else if (string.Equals(
                         data,
                         "home:main",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditBotHomeAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "القائمة الرئيسية"
                        : "Main menu";
            }
            else if (string.Equals(
                    data,
                    "browse:home",
                    StringComparison.OrdinalIgnoreCase))
            {
                await EditBrowseCinemasAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر السينما"
                        : "Choose a cinema";
            }
            else if (data.StartsWith(
                         "browse:cin:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string cinemaSlug =
                    data.Substring(
                        "browse:cin:".Length);

                await EditCinemaDatesAsync(
                    botToken,
                    preference,
                    messageId,
                    cinemaSlug,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر اليوم"
                        : "Choose a date";
            }
            else if (data.StartsWith(
                         "browse:date:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    data.Split(
                        ':');

                if (parts.Length >= 4)
                {
                    await EditDateMoviesAsync(
                        botToken,
                        preference,
                        messageId,
                        parts[2],
                        parts[3],
                        stoppingToken);
                }

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر الفيلم"
                        : "Choose a movie";
            }
            else if (data.StartsWith(
                         "browse:movie:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    data.Split(
                        ':');

                if (parts.Length >= 5 &&
                    int.TryParse(
                        parts[4],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int movieIndex))
                {
                    await AnswerCallbackQueryAsync(
                        botToken,
                        callbackId,
                        string.Empty,
                        false,
                        stoppingToken);

                    callbackAnswered =
                        true;

                    await EditMovieDetailsAsync(
                        botToken,
                        preference,
                        messageId,
                        parts[2],
                        parts[3],
                        movieIndex,
                        stoppingToken);
                }

                callbackMessage =
                    preference.Language == "ar"
                        ? "تفاصيل الفيلم"
                        : "Movie details";
            }
            else if (data.StartsWith(
                         "scope:cin:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string cinemaSlug =
                    data.Substring(
                        "scope:cin:".Length);

                ToggleBroadCinemaScope(
                    preference,
                    cinemaSlug);

                SaveUserPreferences();

                await EditCinemaDatesAsync(
                    botToken,
                    preference,
                    messageId,
                    cinemaSlug,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث متابعة السينما"
                        : "Cinema alert scope updated";
            }
            else if (data.StartsWith(
                         "scope:day:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    data.Split(
                        ':');

                if (parts.Length >= 4)
                {
                    ToggleDayScope(
                        preference,
                        parts[2],
                        parts[3]);

                    SaveUserPreferences();

                    await EditDateMoviesAsync(
                        botToken,
                        preference,
                        messageId,
                        parts[2],
                        parts[3],
                        stoppingToken);
                }

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث متابعة اليوم"
                        : "Date alert scope updated";
            }
            else if (data.StartsWith(
                         "scope:movie:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string[] parts =
                    data.Split(
                        ':');

                if (parts.Length >= 5 &&
                    int.TryParse(
                        parts[4],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int scopeMovieIndex))
                {
                    DaySchedule? scopeSchedule =
                        FindDaySchedule(
                            parts[2],
                            parts[3]);

                    if (scopeSchedule is not null &&
                        scopeMovieIndex >= 0 &&
                        scopeMovieIndex <
                        scopeSchedule.Movies.Length)
                    {
                        ToggleMovieScope(
                            preference,
                            parts[2],
                            parts[3],
                            scopeSchedule.Movies[scopeMovieIndex].Title);

                        SaveUserPreferences();

                        await EditMovieDetailsAsync(
                            botToken,
                            preference,
                            messageId,
                            parts[2],
                            parts[3],
                            scopeMovieIndex,
                            stoppingToken);
                    }
                }

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث متابعة الفيلم"
                        : "Movie alert scope updated";
            }
            else if (string.Equals(
                         data,
                         "setup:scopes",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditAlertScopesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "نطاقات التنبيه"
                        : "Alert scopes";
            }
            else if (string.Equals(
                         data,
                         "scope:clear",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.AlertScopes.Clear();
                preference.SelectedCinemaSlugs.Clear();
                preference.IsCompleted =
                    false;

                SaveUserPreferences();

                await EditAlertScopesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم مسح نطاقات التنبيه"
                        : "Alert scopes cleared";
            }
            else if (string.Equals(
                    data,
                    "setup:provider:vox",
                    StringComparison.OrdinalIgnoreCase))
            {
                preference.Provider =
                    "vox";

                SaveUserPreferences();

                await EditLanguageSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    "VOX Cinemas selected";
            }
            else if (data.StartsWith(
                         "setup:lang:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string language =
                    data.Substring(
                        "setup:lang:".Length);

                preference.Language =
                    string.Equals(
                        language,
                        "ar",
                        StringComparison.OrdinalIgnoreCase)
                        ? "ar"
                        : "en";

                SaveUserPreferences();

                await EditCinemaSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم اختيار العربية"
                        : "English selected";
            }
            else if (data.StartsWith(
                         "setup:cinema:",
                         StringComparison.OrdinalIgnoreCase))
            {
                string slug =
                    data.Substring(
                        "setup:cinema:".Length);

                ToggleCinemaSelection(
                    preference,
                    slug);

                SaveUserPreferences();

                await EditCinemaSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث الاختيار"
                        : "Selection updated";
            }
            else if (string.Equals(
                         data,
                         "setup:all",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.SelectedCinemaSlugs =
                    new HashSet<string>(
                        _lastDiscoveredCinemas.Select(
                            cinema =>
                                cinema.Slug),
                        StringComparer.OrdinalIgnoreCase);

                preference.AlertScopes.Clear();

                SaveUserPreferences();

                await EditCinemaSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم اختيار كل السينمات"
                        : "All cinemas selected";
            }
            else if (string.Equals(
                         data,
                         "setup:clear",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.SelectedCinemaSlugs.Clear();
                preference.AlertScopes.Clear();

                SaveUserPreferences();

                await EditCinemaSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم مسح الاختيارات"
                        : "Selection cleared";
            }
            else if (string.Equals(
                         data,
                         "setup:save",
                         StringComparison.OrdinalIgnoreCase))
            {
                if (preference.SelectedCinemaSlugs.Count == 0 &&
                    preference.AlertScopes.Count == 0)
                {
                    callbackMessage =
                        preference.Language == "ar"
                            ? "اختر سينما واحدة على الأقل"
                            : "Select at least one cinema";

                    showAlert =
                        true;
                }
                else
                {
                    preference.IsCompleted =
                        true;

                    SaveUserPreferences();

                    await EditSavedPreferenceAsync(
                        botToken,
                        preference,
                        messageId,
                        stoppingToken);

                    callbackMessage =
                        preference.Language == "ar"
                            ? "تم حفظ تنبيهاتك"
                            : "Your alerts were saved";
                }
            }
            else if (string.Equals(
                         data,
                         "setup:edit",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditCinemaSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "يمكنك تعديل السينمات"
                        : "Edit your cinemas";
            }
            else if (string.Equals(
                         data,
                         "setup:language",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditLanguageSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر اللغة"
                        : "Choose your language";
            }
            else if (string.Equals(
                         data,
                         "setup:alerts",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر أنواع التنبيهات"
                        : "Choose alert types";
            }
            else if (string.Equals(
                         data,
                         "setup:alert:soldout",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.NotifySoldOut =
                    !preference.NotifySoldOut;

                SaveUserPreferences();

                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث تنبيه اكتمال الحجز"
                        : "Sold-out alert updated";
            }
            else if (string.Equals(
                         data,
                         "setup:alert:reopen",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.NotifyBookingOpenAgain =
                    !preference.NotifyBookingOpenAgain;

                SaveUserPreferences();

                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث تنبيه عودة الحجز"
                        : "Booking-open-again alert updated";
            }
            else if (string.Equals(
                         data,
                         "setup:alert:newtime",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.NotifyNewShowtime =
                    !preference.NotifyNewShowtime;

                SaveUserPreferences();

                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث تنبيه الموعد الجديد"
                        : "New-time alert updated";
            }
            else if (string.Equals(
                         data,
                         "setup:alert:removedtime",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.NotifyShowtimeRemoved =
                    !preference.NotifyShowtimeRemoved;

                SaveUserPreferences();

                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث تنبيه حذف الموعد"
                        : "Removed-time alert updated";
            }
            else if (string.Equals(
                         data,
                         "setup:alert:hallremoved",
                         StringComparison.OrdinalIgnoreCase))
            {
                preference.NotifyHallRemoved =
                    !preference.NotifyHallRemoved;

                SaveUserPreferences();

                await EditAlertTypesAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "تم تحديث تنبيه حذف القاعة"
                        : "Hall-removed alert updated";
            }
            else if (string.Equals(
                         data,
                         "setup:provider",
                         StringComparison.OrdinalIgnoreCase))
            {
                await EditProviderSelectionAsync(
                    botToken,
                    preference,
                    messageId,
                    stoppingToken);

                callbackMessage =
                    preference.Language == "ar"
                        ? "اختر شبكة السينما"
                        : "Choose the cinema network";
            }

            if (!callbackAnswered)
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    callbackMessage,
                    showAlert,
                    stoppingToken);
            }
        }

        private static string GetTelegramStartParameter(
                string messageText)
        {
            string[] parts =
                messageText.Split(
                    new[]
                    {
                        ' '
                    },
                    2,
                    StringSplitOptions.RemoveEmptyEntries);

            return parts.Length > 1
                ? parts[1].Trim()
                : string.Empty;
        }

        private async Task SendBrowseCinemasAsync(
                string botToken,
                UserPreference preference,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildBrowseCinemasText(
                    preference.Language),
                BuildBrowseCinemasKeyboard(
                    preference.Language),
                stoppingToken);
        }

        private async Task SendCinemaDatesAsync(
                string botToken,
                UserPreference preference,
                string cinemaSlug,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildCinemaDatesText(
                    cinemaSlug,
                    preference.Language),
                BuildCinemaDatesKeyboard(
                    cinemaSlug,
                    preference),
                stoppingToken);
        }

        private async Task EditBrowseCinemasAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildBrowseCinemasText(
                    preference.Language),
                BuildBrowseCinemasKeyboard(
                    preference.Language),
                stoppingToken);
        }

        private async Task EditCinemaDatesAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                string cinemaSlug,
                CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildCinemaDatesText(
                    cinemaSlug,
                    preference.Language),
                BuildCinemaDatesKeyboard(
                    cinemaSlug,
                    preference),
                stoppingToken);
        }

        private async Task EditDateMoviesAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                string cinemaSlug,
                string dateKey,
                CancellationToken stoppingToken)
        {
            DaySchedule? schedule =
                FindDaySchedule(
                    cinemaSlug,
                    dateKey);

            if (schedule is null)
            {
                await EditTelegramHtmlWithMarkupAsync(
                    botToken,
                    preference.PrivateChatId,
                    messageId,
                    preference.Language == "ar"
                        ? "⚠️ الجدول غير متاح حاليًا."
                        : "⚠️ The schedule is not currently available.",
                    BuildBackToCinemaKeyboard(
                        cinemaSlug,
                        preference.Language),
                    stoppingToken);

                return;
            }

            DaySchedule displaySchedule =
                await GetDisplayScheduleAsync(
                    preference,
                    schedule,
                    stoppingToken);

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildDateMoviesText(
                    displaySchedule,
                    preference.Language),
                BuildDateMoviesKeyboard(
                    displaySchedule,
                    preference),
                stoppingToken);
        }

        private async Task EditMovieDetailsAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                string cinemaSlug,
                string dateKey,
                int movieIndex,
                CancellationToken stoppingToken)
        {
            DaySchedule? schedule =
                FindDaySchedule(
                    cinemaSlug,
                    dateKey);

            if (schedule is null)
            {
                return;
            }

            DaySchedule displaySchedule =
                await GetDisplayScheduleAsync(
                    preference,
                    schedule,
                    stoppingToken);

            if (movieIndex < 0 ||
                movieIndex >=
                displaySchedule.Movies.Length)
            {
                return;
            }

            MovieSchedule movie =
                displaySchedule.Movies[movieIndex];

            long loadVersion =
                BeginMovieDetailsLoad(
                    preference.PrivateChatId,
                    messageId);

            // Show halls and times immediately from the live in-memory scan.
            // Direct /guest links are resolved in the background and then the
            // same message is upgraded without blocking Telegram navigation.
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildMovieDetailsText(
                    displaySchedule,
                    movie,
                    preference.Language),
                BuildMovieDetailsKeyboard(
                    cinemaSlug,
                    dateKey,
                    movieIndex,
                    movie,
                    preference,
                    linksAreLoading:
                        true),
                stoppingToken);

            _ = CompleteMovieDetailsBookingLinksAsync(
                    botToken,
                    preference.Clone(),
                    messageId,
                    cinemaSlug,
                    dateKey,
                    movieIndex,
                    displaySchedule,
                    movie,
                    loadVersion,
                    stoppingToken);
        }

        private async Task CompleteMovieDetailsBookingLinksAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                string cinemaSlug,
                string dateKey,
                int movieIndex,
                DaySchedule displaySchedule,
                MovieSchedule movie,
                long loadVersion,
                CancellationToken stoppingToken)
        {
            try
            {
                MovieSchedule resolvedMovie =
                    await ResolveMovieBookingUrlsAsync(
                        movie,
                        displaySchedule.Url,
                        stoppingToken);

                if (!IsMovieDetailsLoadCurrent(
                        preference.PrivateChatId,
                        messageId,
                        loadVersion))
                {
                    return;
                }

                await EditTelegramHtmlWithMarkupAsync(
                    botToken,
                    preference.PrivateChatId,
                    messageId,
                    BuildMovieDetailsText(
                        displaySchedule,
                        resolvedMovie,
                        preference.Language),
                    BuildMovieDetailsKeyboard(
                        cinemaSlug,
                        dateKey,
                        movieIndex,
                        resolvedMovie,
                        preference,
                        linksAreLoading:
                            false),
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not upgrade movie details with direct booking links.");
            }
        }

        private DaySchedule? FindDaySchedule(
                string cinemaSlug,
                string dateKey)
        {
            return _knownScheduleByCinemaAndDate.TryGetValue(
                       cinemaSlug +
                       "|" +
                       dateKey,
                       out DaySchedule? schedule)
                ? schedule
                : null;
        }

        private async Task<DaySchedule> GetDisplayScheduleAsync(
                UserPreference preference,
                DaySchedule schedule,
                CancellationToken stoppingToken)
        {
            if (!string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase))
            {
                return schedule;
            }

            string key =
                FormatStateKey(
                    schedule.CinemaSlug,
                    schedule.Date);

            lock (_arabicDashboardCacheLock)
            {
                if (_arabicDashboardSchedulesByKey.TryGetValue(
                        key,
                        out DaySchedule? cachedSchedule))
                {
                    return cachedSchedule;
                }
            }

            DaySchedule? loadedSchedule =
                await TryLoadArabicScheduleAsync(
                    schedule,
                    stoppingToken);

            if (loadedSchedule is not null)
            {
                lock (_arabicDashboardCacheLock)
                {
                    _arabicDashboardSchedulesByKey[key] =
                        loadedSchedule;
                }

                return loadedSchedule;
            }

            return schedule;
        }

        private string BuildBrowseCinemasText(
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            return arabic
                ? "🎬 <b>تصفح مواعيد VOX</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "اختر السينما، ثم اليوم، ثم الفيلم.\n" +
                  "سيتم عرض القاعات والمواعيد وروابط الحجز داخل نفس الرسالة."
                : "🎬 <b>Browse VOX Showtimes</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "Choose a cinema, then a date, then a movie.\n" +
                  "Halls, showtimes and booking links will appear in this same message.";
        }

        private object BuildBrowseCinemasKeyboard(
                string language)
        {
            var rows =
                new List<object>();

            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            foreach (CinemaOption cinema in
                     _lastDiscoveredCinemas)
            {
                string displayName =
                    arabic
                        ? GetArabicCinemaDisplayName(
                            cinema.Slug,
                            cinema.Name,
                            cinema.Name)
                        : cinema.Name;

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                "🏢 " +
                                displayName,

                            callback_data =
                                "browse:cin:" +
                                cinema.Slug
                        }
                    });
            }

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "🎯 الأيام والأفلام المتابعة"
                                : "🎯 Followed Dates & Movies",

                        callback_data =
                            "setup:scopes"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "🎬 الأفلام الحالية"
                                : "🎬 Current Movies",

                        callback_data =
                            "current:menu:0"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🔜 أفلام قريباً"
                                : "🔜 Coming Soon",

                        callback_data =
                            "coming:menu:0"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⚙️ تنبيهاتي"
                                : "⚙️ My Alerts",

                        callback_data =
                            "setup:edit"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🏠 القائمة الرئيسية"
                                : "🏠 Main Menu",

                        callback_data =
                            "home:main"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private string BuildCinemaDatesText(
                string cinemaSlug,
                string language)
        {
            CinemaOption? cinema =
                _lastDiscoveredCinemas.FirstOrDefault(
                    item =>
                        string.Equals(
                            item.Slug,
                            cinemaSlug,
                            StringComparison.OrdinalIgnoreCase));

            int dateCount =
                _knownScheduleByCinemaAndDate.Values.Count(
                    schedule =>
                        string.Equals(
                            schedule.CinemaSlug,
                            cinemaSlug,
                            StringComparison.OrdinalIgnoreCase) &&
                        schedule.Movies.Length > 0);

            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string cinemaName =
                arabic
                    ? GetArabicCinemaDisplayName(
                        cinemaSlug,
                        cinema?.Name ??
                        cinemaSlug,
                        cinema?.Name ??
                        cinemaSlug)
                    : cinema?.Name ??
                      cinemaSlug;

            return arabic
                ? "\u200F🏢 <b>" +
                  FormatArabicBidiText(
                      cinemaName) +
                  "</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "\u200Fاختر اليوم أو فعّل متابعة كل أيام السينما.\n" +
                  "\u200F📅 " +
                  dateCount +
                  " أيام متاحة"
                : "🏢 <b>" +
                  EscapeTelegramHtml(
                      cinemaName) +
                  "</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "Choose a date, or follow every date at this cinema.\n" +
                  "📅 " +
                  dateCount +
                  " available days";
        }

        private object BuildCinemaDatesKeyboard(
                string cinemaSlug,
                UserPreference preference)
        {
            var rows =
                new List<object>();

            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            bool broadSelected =
                preference.SelectedCinemaSlugs.Contains(
                    cinemaSlug);

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            (broadSelected
                                ? "✅ "
                                : "▫️ ") +
                            (arabic
                                ? "تابع كل أيام هذه السينما"
                                : "Follow all dates at this cinema"),

                        callback_data =
                            "scope:cin:" +
                            cinemaSlug
                    }
                });

            DaySchedule[] schedules =
                _knownScheduleByCinemaAndDate.Values
                    .Where(schedule =>
                        string.Equals(
                            schedule.CinemaSlug,
                            cinemaSlug,
                            StringComparison.OrdinalIgnoreCase) &&
                        schedule.Movies.Length > 0)
                    .OrderBy(schedule =>
                        schedule.Date)
                    .ToArray();

            foreach (DaySchedule schedule in
                     schedules)
            {
                string dateKey =
                    schedule.Date.ToString(
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture);

                bool daySelected =
                    IsDayScopeSelected(
                        preference,
                        cinemaSlug,
                        dateKey);

                string dateText =
                    arabic
                        ? FormatArabicDate(
                            schedule.Date)
                        : schedule.Date.ToString(
                            "ddd - d MMM",
                            CultureInfo.InvariantCulture);

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                "📅 " +
                                dateText +
                                " • 🎬 " +
                                schedule.Movies.Length,

                            callback_data =
                                "browse:date:" +
                                cinemaSlug +
                                ":" +
                                dateKey
                        },

                        new
                        {
                            text =
                                daySelected
                                    ? "✅ 🔔"
                                    : "▫️ 🔔",

                            callback_data =
                                "scope:day:" +
                                cinemaSlug +
                                ":" +
                                dateKey
                        }
                    });
            }

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⬅️ السينمات"
                                : "⬅️ Cinemas",

                        callback_data =
                            "browse:home"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🎯 متابعاتي"
                                : "🎯 My Scopes",

                        callback_data =
                            "setup:scopes"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private string BuildDateMoviesText(
                DaySchedule schedule,
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string cinemaName =
                arabic
                    ? GetArabicCinemaDisplayName(
                        schedule.CinemaSlug,
                        schedule.CinemaName,
                        schedule.CinemaName)
                    : schedule.CinemaName;

            return arabic
                ? "\u200F🏢 <b>" +
                  FormatArabicBidiText(
                      cinemaName) +
                  "</b>\n" +
                  "\u200F📅 <b>" +
                  EscapeTelegramHtml(
                      FormatArabicDate(
                          schedule.Date)) +
                  "</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "\u200F🎬 اختر الفيلم. زر الجرس يحدد متابعة اليوم أو الفيلم."
                : "🏢 <b>" +
                  EscapeTelegramHtml(
                      cinemaName) +
                  "</b>\n" +
                  "📅 <b>" +
                  EscapeTelegramHtml(
                      schedule.Date.ToString(
                          "dddd - d MMMM yyyy",
                          CultureInfo.InvariantCulture)) +
                  "</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "🎬 Choose a movie. Use the bell to follow the date or one movie.";
        }

        private object BuildDateMoviesKeyboard(
                DaySchedule schedule,
                UserPreference preference)
        {
            var rows =
                new List<object>();

            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string dateKey =
                schedule.Date.ToString(
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture);

            bool daySelected =
                IsDayScopeSelected(
                    preference,
                    schedule.CinemaSlug,
                    dateKey);

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            (daySelected
                                ? "✅ "
                                : "▫️ ") +
                            (arabic
                                ? "تابع كل تغييرات هذا اليوم"
                                : "Follow every change on this date"),

                        callback_data =
                            "scope:day:" +
                            schedule.CinemaSlug +
                            ":" +
                            dateKey
                    }
                });

            for (int index = 0;
                 index < schedule.Movies.Length;
                 index++)
            {
                MovieSchedule movie =
                    schedule.Movies[index];

                int showtimeCount =
                    movie.Halls.Sum(hall =>
                        hall.Showtimes.Length);

                string scopeMovieTitle =
                    GetEnglishMovieTitleForScope(
                        schedule.CinemaSlug,
                        dateKey,
                        index,
                        movie.Title);

                bool movieSelected =
                    IsMovieScopeSelected(
                        preference,
                        schedule.CinemaSlug,
                        dateKey,
                        scopeMovieTitle);

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                "🎬 " +
                                movie.Title +
                                " • 🕒 " +
                                showtimeCount,

                            callback_data =
                                "browse:movie:" +
                                schedule.CinemaSlug +
                                ":" +
                                dateKey +
                                ":" +
                                index.ToString(
                                    CultureInfo.InvariantCulture)
                        },

                        new
                        {
                            text =
                                movieSelected
                                    ? "✅ 🔔"
                                    : "▫️ 🔔",

                            callback_data =
                                "scope:movie:" +
                                schedule.CinemaSlug +
                                ":" +
                                dateKey +
                                ":" +
                                index.ToString(
                                    CultureInfo.InvariantCulture)
                        }
                    });
            }

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⬅️ الأيام"
                                : "⬅️ Dates",

                        callback_data =
                            "browse:cin:" +
                            schedule.CinemaSlug
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🏠 البداية"
                                : "🏠 Home",

                        callback_data =
                            "browse:home"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private string BuildMovieDetailsText(
                DaySchedule schedule,
                MovieSchedule movie,
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string cinemaName =
                arabic
                    ? GetArabicCinemaDisplayName(
                        schedule.CinemaSlug,
                        schedule.CinemaName,
                        schedule.CinemaName)
                    : schedule.CinemaName;

            var builder =
                new StringBuilder();

            builder.AppendLine(
                "🎬 <b>" +
                (arabic
                    ? FormatArabicBidiText(
                        movie.Title)
                    : EscapeTelegramHtml(
                        movie.Title)) +
                "</b>");

            builder.AppendLine(
                "🏢 " +
                (arabic
                    ? FormatArabicBidiText(
                        cinemaName)
                    : EscapeTelegramHtml(
                        cinemaName)));

            builder.AppendLine(
                "📅 " +
                EscapeTelegramHtml(
                    arabic
                        ? FormatArabicDate(
                            schedule.Date)
                        : schedule.Date.ToString(
                            "dddd - d MMMM yyyy",
                            CultureInfo.InvariantCulture)));

            builder.AppendLine(
                "━━━━━━━━━━━━━━━━━━━━");

            foreach (HallSchedule hall in
                     movie.Halls)
            {
                builder.AppendLine(
                    "🏛 <b>" +
                    (arabic
                        ? FormatArabicBidiText(
                            hall.Name)
                        : EscapeTelegramHtml(
                            hall.Name)) +
                    "</b>");

                foreach (ShowtimeSchedule showtime in
                         hall.Showtimes)
                {
                    if (showtime.IsAvailable)
                    {
                        builder.AppendLine(
                            "🟢 <b>" +
                            EscapeTelegramHtml(
                                showtime.Time) +
                            "</b> — " +
                            (arabic
                                ? "متاح"
                                : "Available"));
                    }
                    else
                    {
                        builder.AppendLine(
                            "🔴 <s>" +
                            EscapeTelegramHtml(
                                showtime.Time) +
                            "</s> — <b>" +
                            (arabic
                                ? "الحجز مكتمل"
                                : "SOLD OUT") +
                            "</b>");
                    }
                }

                builder.AppendLine();
            }

            builder.AppendLine(
                arabic
                    ? "🎟 استخدم أزرار الحجز المباشر بالأسفل."
                    : "🎟 Use the direct booking buttons below.");

            builder.Append(
                "🔗 <a href=\"" +
                EscapeTelegramHtml(
                    schedule.Url) +
                "\"><b>" +
                (arabic
                    ? "افتح الجدول الكامل"
                    : "Open full schedule") +
                "</b></a>");

            string result =
                builder.ToString();

            return result.Length <= 3900
                ? result
                : result.Substring(
                    0,
                    3850) +
                  "\n…";
        }

        private object BuildMovieDetailsKeyboard(
                string cinemaSlug,
                string dateKey,
                int movieIndex,
                MovieSchedule movie,
                UserPreference preference,
                bool linksAreLoading)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            var rows =
                new List<object>();

            var bookingButtons =
                new List<object>();
            foreach (HallSchedule hall in
                     movie.Halls)
            {
                foreach (ShowtimeSchedule showtime in
                         hall.Showtimes.Where(item =>
                             item.IsAvailable &&
                             IsSafeVoxBookingUrl(
                                 NormalizeGuestBookingUrl(
                                     item.BookingUrl))))
                {
                    bookingButtons.Add(
                        new
                        {
                            text =
                                "🎟 " +
                                showtime.Time +
                                " • " +
                                hall.Name,

                            url =
                                NormalizeGuestBookingUrl(
                                    showtime.BookingUrl)
                        });

                    if (bookingButtons.Count == 2)
                    {
                        rows.Add(
                            bookingButtons.ToArray());

                        bookingButtons.Clear();
                    }
                }
            }

            if (bookingButtons.Count > 0)
            {
                rows.Add(
                    bookingButtons.ToArray());
            }

            if (linksAreLoading &&
                rows.Count == 0)
            {
                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                arabic
                                    ? "⚡ جاري تحديث روابط الحجز..."
                                    : "⚡ Refreshing booking links...",

                            callback_data =
                                "noop:loading"
                        }
                    });
            }

            string movieTitle =
                GetEnglishMovieTitleForScope(
                    cinemaSlug,
                    dateKey,
                    movieIndex,
                    movie.Title);

            bool movieSelected =
                IsMovieScopeSelected(
                    preference,
                    cinemaSlug,
                    dateKey,
                    movieTitle);

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            (movieSelected
                                ? "✅ "
                                : "▫️ ") +
                            (arabic
                                ? "تابع هذا الفيلم في هذا اليوم"
                                : "Follow this movie on this date"),

                        callback_data =
                            "scope:movie:" +
                            cinemaSlug +
                            ":" +
                            dateKey +
                            ":" +
                            movieIndex.ToString(
                                CultureInfo.InvariantCulture)
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⬅️ الأفلام"
                                : "⬅️ Movies",

                        callback_data =
                            "browse:date:" +
                            cinemaSlug +
                            ":" +
                            dateKey
                    },

                    new
                    {
                        text =
                            arabic
                                ? "📅 الأيام"
                                : "📅 Dates",

                        callback_data =
                            "browse:cin:" +
                            cinemaSlug
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "🏠 البداية"
                                : "🏠 Home",

                        callback_data =
                            "browse:home"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "⚙️ تنبيهاتي"
                                : "⚙️ My Alerts",

                        callback_data =
                            "setup:edit"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private object BuildBackToCinemaKeyboard(
                string cinemaSlug,
                string language)
        {
            return new
            {
                inline_keyboard =
                    new object[]
                    {
                        new object[]
                        {
                            new
                            {
                                text =
                                    string.Equals(
                                        language,
                                        "ar",
                                        StringComparison.OrdinalIgnoreCase)
                                        ? "⬅️ العودة"
                                        : "⬅️ Back",

                                callback_data =
                                    "browse:cin:" +
                                    cinemaSlug
                            }
                        }
                    }
            };
        }

        private async Task DeleteIncomingPrivateMessageAsync(
                string botToken,
                long chatId,
                int messageId,
                CancellationToken stoppingToken)
        {
            bool deleted =
                await PostTelegramJsonAsync(
                    botToken,
                    "deleteMessage",
                    new
                    {
                        chat_id =
                            chatId,

                        message_id =
                            messageId
                    },
                    stoppingToken,
                    rateLimitChatKey:
                        chatId.ToString(
                            CultureInfo.InvariantCulture),
                    applyMessageRateLimit:
                        false);

            if (!deleted)
            {
                // The bot still ignores the content even if Telegram could not
                // delete it, for example if it is already unavailable.
                _logger.LogWarning(
                    "Buttons-only mode ignored an incoming private message, " +
                    "but Telegram could not delete message {MessageId} in chat {ChatId}.",
                    messageId,
                    chatId);
            }
        }

        private async Task<bool> SendTelegramHtmlWithMarkupAsync(
                string botToken,
                object chatId,
                string message,
                object replyMarkup,
                CancellationToken stoppingToken)
        {
            return await PostTelegramJsonAsync(
                botToken,
                "sendMessage",
                new
                {
                    chat_id =
                        chatId,

                    text =
                        message,

                    parse_mode =
                        "HTML",

                    disable_web_page_preview =
                        true,

                    reply_markup =
                        replyMarkup
                },
                stoppingToken,
                rateLimitChatKey:
                    Convert.ToString(
                        chatId,
                        CultureInfo.InvariantCulture),
                applyMessageRateLimit:
                    true);
        }

        private async Task<bool> EditTelegramHtmlWithMarkupAsync(
                string botToken,
                long chatId,
                int messageId,
                string message,
                object replyMarkup,
                CancellationToken stoppingToken)
        {
            return await PostTelegramJsonAsync(
                botToken,
                "editMessageText",
                new
                {
                    chat_id =
                        chatId,

                    message_id =
                        messageId,

                    text =
                        message,

                    parse_mode =
                        "HTML",

                    disable_web_page_preview =
                        true,

                    reply_markup =
                        replyMarkup
                },
                stoppingToken,
                rateLimitChatKey:
                    chatId.ToString(
                        CultureInfo.InvariantCulture),
                applyMessageRateLimit:
                    false);
        }

        private async Task AnswerCallbackQueryAsync(
                string botToken,
                string callbackQueryId,
                string message,
                bool showAlert,
                CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(
                    callbackQueryId))
            {
                return;
            }

            await PostTelegramJsonAsync(
                botToken,
                "answerCallbackQuery",
                new
                {
                    callback_query_id =
                        callbackQueryId,

                    text =
                        message,

                    show_alert =
                        showAlert
                },
                stoppingToken);
        }

        private async Task<bool> PostTelegramJsonAsync(
                string botToken,
                string method,
                object payload,
                CancellationToken stoppingToken,
                string? rateLimitChatKey = null,
                bool applyMessageRateLimit = false)
        {
            string apiUrl =
                $"https://api.telegram.org/bot{botToken}/{method}";

            int maximumNetworkAttempts =
                ReadIntegerSetting(
                    "Telegram:MaximumSendAttempts",
                    defaultValue: 5,
                    minimumValue: 1,
                    maximumValue: 20);

            int retryAfterBufferSeconds =
                ReadIntegerSetting(
                    "Telegram:RetryAfterBufferSeconds",
                    defaultValue: 3,
                    minimumValue: 1,
                    maximumValue: 15);

            bool gateAcquired =
                false;

            try
            {
                if (applyMessageRateLimit &&
                    !string.IsNullOrWhiteSpace(
                        rateLimitChatKey))
                {
                    await _telegramMessageGate.WaitAsync(
                        stoppingToken);

                    gateAcquired =
                        true;

                    await WaitForTelegramMessageWindowAsync(
                        rateLimitChatKey,
                        stoppingToken);
                }

                int networkAttempt =
                    0;

                while (!stoppingToken.IsCancellationRequested)
                {
                    string json =
                        JsonSerializer.Serialize(
                            payload);

                    using var content =
                        new StringContent(
                            json,
                            Encoding.UTF8,
                            "application/json");

                    try
                    {
                        using HttpResponseMessage response =
                            await _telegramHttpClient.PostAsync(
                                apiUrl,
                                content,
                                stoppingToken);

                        string responseBody =
                            await response.Content.ReadAsStringAsync(
                                stoppingToken);

                        if (response.IsSuccessStatusCode)
                        {
                            if (applyMessageRateLimit &&
                                !string.IsNullOrWhiteSpace(
                                    rateLimitChatKey))
                            {
                                RegisterTelegramMessageSent(
                                    rateLimitChatKey);
                            }

                            return true;
                        }

                        if ((int)response.StatusCode ==
                            400 &&
                            responseBody.Contains(
                                "message is not modified",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        if ((int)response.StatusCode ==
                            429)
                        {
                            int retryAfterSeconds =
                                TryReadTelegramRetryAfterSeconds(
                                    responseBody);

                            int waitSeconds =
                                Math.Max(
                                    1,
                                    retryAfterSeconds +
                                    retryAfterBufferSeconds);

                            // A 429 response is not treated as an application
                            // error. Telegram has accepted the request pattern
                            // but asked the bot to pause. Keep the same message
                            // at the front of the queue and retry until it succeeds.
                            _logger.LogInformation(
                                "Telegram requested a temporary sending pause. " +
                                "The queued message will retry automatically after " +
                                "{WaitSeconds} seconds.",
                                waitSeconds);

                            await Task.Delay(
                                TimeSpan.FromSeconds(
                                    waitSeconds),
                                stoppingToken);

                            continue;
                        }

                        _logger.LogError(
                            "Telegram {Method} failed. HTTP {StatusCode}: {Response}",
                            method,
                            (int)response.StatusCode,
                            LimitText(
                                responseBody,
                                1000));

                        return false;
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TaskCanceledException ex)
                        when (!stoppingToken.IsCancellationRequested)
                    {
                        networkAttempt++;

                        if (networkAttempt >=
                            maximumNetworkAttempts)
                        {
                            _logger.LogError(
                                ex,
                                "Telegram {Method} timed out after {MaximumAttempts} attempts.",
                                method,
                                maximumNetworkAttempts);

                            return false;
                        }

                        int retryDelaySeconds =
                            Math.Min(
                                networkAttempt * 2,
                                10);

                        _logger.LogWarning(
                            "Telegram {Method} timed out temporarily. " +
                            "Retrying after {RetryDelaySeconds} seconds.",
                            method,
                            retryDelaySeconds);

                        await Task.Delay(
                            TimeSpan.FromSeconds(
                                retryDelaySeconds),
                            stoppingToken);
                    }
                    catch (HttpRequestException ex)
                    {
                        networkAttempt++;

                        if (networkAttempt >=
                            maximumNetworkAttempts)
                        {
                            _logger.LogError(
                                ex,
                                "Telegram {Method} failed after {MaximumAttempts} " +
                                "network attempts.",
                                method,
                                maximumNetworkAttempts);

                            return false;
                        }

                        int retryDelaySeconds =
                            Math.Min(
                                networkAttempt * 2,
                                15);

                        _logger.LogWarning(
                            ex,
                            "Telegram {Method} had a temporary network failure. " +
                            "Retrying after {RetryDelaySeconds} seconds.",
                            method,
                            retryDelaySeconds);

                        await Task.Delay(
                            TimeSpan.FromSeconds(
                                retryDelaySeconds),
                            stoppingToken);
                    }
                }

                return false;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Telegram {Method} failed.",
                    method);

                return false;
            }
            finally
            {
                if (gateAcquired)
                {
                    _telegramMessageGate.Release();
                }
            }
        }

        private async Task WaitForTelegramMessageWindowAsync(
                string chatKey,
                CancellationToken stoppingToken)
        {
            int globalMinimumDelayMilliseconds =
                ReadIntegerSetting(
                    "Telegram:GlobalMinimumDelayMilliseconds",
                    defaultValue: 120,
                    minimumValue: 50,
                    maximumValue: 5000);

            int privateChatMinimumDelayMilliseconds =
                ReadIntegerSetting(
                    "Telegram:PrivateChatMinimumDelayMilliseconds",
                    defaultValue: 1100,
                    minimumValue: 1000,
                    maximumValue: 10000);

            int groupMinimumDelayMilliseconds =
                ReadIntegerSetting(
                    "Telegram:GroupMinimumDelayMilliseconds",
                    defaultValue: 3200,
                    minimumValue: 3000,
                    maximumValue: 15000);

            int chatDelayMilliseconds =
                chatKey.StartsWith(
                    "-",
                    StringComparison.Ordinal)
                    ? groupMinimumDelayMilliseconds
                    : privateChatMinimumDelayMilliseconds;

            DateTime now =
                DateTime.UtcNow;

            DateTime nextAllowedGlobalUtc =
                _lastTelegramGlobalMessageUtc.AddMilliseconds(
                    globalMinimumDelayMilliseconds);

            DateTime lastChatMessageUtc =
                _lastTelegramMessageUtcByChat.TryGetValue(
                    chatKey,
                    out DateTime storedLastChatMessageUtc)
                    ? storedLastChatMessageUtc
                    : DateTime.MinValue;

            DateTime nextAllowedChatUtc =
                lastChatMessageUtc.AddMilliseconds(
                    chatDelayMilliseconds);

            DateTime nextAllowedUtc =
                nextAllowedGlobalUtc >
                nextAllowedChatUtc
                    ? nextAllowedGlobalUtc
                    : nextAllowedChatUtc;

            TimeSpan delay =
                nextAllowedUtc -
                now;

            if (delay >
                TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Telegram message queued for {DelayMilliseconds} ms " +
                    "to stay within platform rate limits.",
                    (int)Math.Ceiling(
                        delay.TotalMilliseconds));

                await Task.Delay(
                    delay,
                    stoppingToken);
            }
        }

        private void RegisterTelegramMessageSent(
                string chatKey)
        {
            DateTime sentUtc =
                DateTime.UtcNow;

            _lastTelegramGlobalMessageUtc =
                sentUtc;

            _lastTelegramMessageUtcByChat[chatKey] =
                sentUtc;
        }

        private static int TryReadTelegramRetryAfterSeconds(
                string responseBody)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        responseBody);

                if (document.RootElement.TryGetProperty(
                        "parameters",
                        out JsonElement parameters) &&
                    parameters.TryGetProperty(
                        "retry_after",
                        out JsonElement retryAfterElement) &&
                    retryAfterElement.TryGetInt32(
                        out int retryAfterSeconds))
                {
                    return
                        Math.Max(
                            1,
                            retryAfterSeconds);
                }
            }
            catch
            {
                // Fall through to the safe default.
            }

            return 5;
        }



        private bool IsAdminUser(
                long userId)
        {
            return GetConfiguredAdminUserIds().Contains(userId);
        }

        private static string GetTelegramUsername(
                JsonElement message)
        {
            if (message.TryGetProperty(
                    "from",
                    out JsonElement from) &&
                from.TryGetProperty(
                    "username",
                    out JsonElement usernameElement))
            {
                return usernameElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private async Task SendTelegramMessageForMyIdAsync(
                string message,
                long chatId,
                string parseMode,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                _configuration["Telegram:BotToken"] ?? string.Empty,
                chatId,
                message,
                new
                {
                    inline_keyboard =
                        Array.Empty<object[]>()
                },
                stoppingToken);
        }

        private void LoadTelegramUserActivity()
        {
            lock (_telegramAdminStateLock)
            {
                if (_telegramActivityLoaded)
                {
                    return;
                }

                _telegramActivityLoaded = true;

                try
                {
                    if (!TryLoadPersistentJson(
                            Path.GetFileName(TelegramActivityFilePath),
                            TelegramActivityFilePath,
                            out string json))
                    {
                        return;
                    }

                    Dictionary<long, DateTime>? stored =
                        JsonSerializer.Deserialize<Dictionary<long, DateTime>>(
                            json);

                    if (stored is null)
                    {
                        return;
                    }

                    foreach (KeyValuePair<long, DateTime> item in stored)
                    {
                        _telegramUserLastSeenUtc[item.Key] =
                            item.Value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not load Telegram user activity statistics.");
                }
            }
        }

        private void RegisterTelegramUserActivity(
                long userId)
        {
            if (userId <= 0)
            {
                return;
            }

            LoadTelegramUserActivity();

            lock (_telegramAdminStateLock)
            {
                _telegramUserLastSeenUtc[userId] =
                    DateTime.UtcNow;

                // Persist immediately so /users always has the latest time.
                SaveTelegramUserActivityUnsafe();
            }
        }

        private void SaveTelegramUserActivityUnsafe()
        {
            try
            {
                string json =
                    JsonSerializer.Serialize(
                        _telegramUserLastSeenUtc,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                SavePersistentJson(
                    Path.GetFileName(TelegramActivityFilePath),
                    TelegramActivityFilePath,
                    json);

                _lastTelegramActivitySaveUtc =
                    DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not save Telegram user activity statistics.");
            }
        }

        private async Task SendChannelWelcomeOnceAsync(
                string botToken,
                long userId,
                long privateChatId,
                string language,
                CancellationToken stoppingToken)
        {
            bool shouldSend;

            lock (_telegramAdminStateLock)
            {
                shouldSend =
                    _channelWelcomeSentUsers.Add(
                        userId);
            }

            if (!shouldSend)
            {
                return;
            }

            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string botUsername =
                _botUsername.Trim().TrimStart('@');

            string botUrl =
                string.IsNullOrWhiteSpace(botUsername)
                    ? string.Empty
                    : "https://t.me/" + botUsername;

            string message =
                arabic
                    ? "🎬✨ <b>أهلاً بك في CinemaBot!</b>\n\n" +
                      "تم تفعيل حسابك بنجاح 🎉\n\n" +
                      "دلوقتي تقدر تتابع أفلام VOX والسينمات " +
                      "والمواعيد اللي تهمك وتحصل على التنبيهات فور حدوث أي تغيير.\n\n" +
                      "🍿 أفلام جديدة\n" +
                      "🎟️ مواعيد حجز جديدة\n" +
                      "🏛️ سينمات وقاعات جديدة\n" +
                      "🔔 فتح الحجز مرة أخرى\n" +
                      "🚨 تنبيهات Sold Out\n\n" +
                      "استمتع بالأفلام! 🎬❤️"
                    : "🎬✨ <b>Welcome to CinemaBot!</b>\n\n" +
                      "Your account is now activated! 🎉\n\n" +
                      "You can now follow VOX movies, cinemas, dates " +
                      "and showtimes and receive alerts when something changes.\n\n" +
                      "🍿 New movies\n" +
                      "🎟️ New booking times\n" +
                      "🏛️ New cinemas and halls\n" +
                      "🔔 Booking reopened\n" +
                      "🚨 Sold-out alerts\n\n" +
                      "Enjoy the movies! 🎬❤️";

            var rows =
                new List<object[]>();

            if (!string.IsNullOrWhiteSpace(
                    botUrl))
            {
                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                arabic
                                    ? "🚀 افتح CinemaBot"
                                    : "🚀 Open CinemaBot",
                            url =
                                botUrl
                        }
                    });
            }

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "🎯 تخصيص التنبيهات"
                                : "🎯 Customize My Alerts",
                        callback_data =
                            "home:main"
                    }
                });

            bool sent =
                await SendTelegramHtmlWithMarkupAsync(
                    botToken,
                    privateChatId,
                    message,
                    new
                    {
                        inline_keyboard =
                            rows.ToArray()
                    },
                    stoppingToken);

            if (!sent)
            {
                lock (_telegramAdminStateLock)
                {
                    _channelWelcomeSentUsers.Remove(
                        userId);
                }
            }
        }

        private async Task SendAdminDashboardAsync(
                string botToken,
                long chatId,
                string language,
                CancellationToken stoppingToken)
        {
            string text =
                await BuildAdminStatisticsTextAsync(
                    language,
                    botToken,
                    stoppingToken);

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                chatId,
                text,
                BuildAdminDashboardKeyboard(language),
                stoppingToken);
        }

        private async Task HandleAdminCallbackAsync(
                string botToken,
                string callbackId,
                string data,
                long userId,
                long chatId,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            if (!IsAdminUser(userId))
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    "Admin only.",
                    true,
                    stoppingToken);

                return;
            }

            if (data.StartsWith("admin:users:", StringComparison.OrdinalIgnoreCase))
            {
                string rawPage = data.Substring("admin:users:".Length);
                int.TryParse(rawPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out int page);
                page = Math.Max(0, page);

                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    preference.Language == "ar" ? "تحديث قائمة المستخدمين" : "Loading users...",
                    false,
                    stoppingToken);

                await EditTelegramHtmlWithMarkupAsync(
                    botToken,
                    chatId,
                    messageId,
                    await BuildKnownUsersPageTextAsync(preference.Language, page),
                    await BuildKnownUsersKeyboardAsync(preference.Language, page),
                    stoppingToken);
                return;
            }

            if (data.StartsWith(
                    "admin:limit:",
                    StringComparison.OrdinalIgnoreCase))
            {
                string rawLimit =
                    data.Substring("admin:limit:".Length);

                if (!int.TryParse(
                        rawLimit,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int requestedLimit) ||
                    requestedLimit < 0 ||
                    requestedLimit > 100000)
                {
                    await AnswerCallbackQueryAsync(
                        botToken,
                        callbackId,
                        "Invalid limit.",
                        true,
                        stoppingToken);

                    return;
                }

                SetMaxBotUsersLimit(requestedLimit);

                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    preference.Language == "ar"
                        ? requestedLimit == 0
                            ? "تم إلغاء حد المستخدمين."
                            : $"تم تحديد الحد الأقصى لمستخدمي البوت: {requestedLimit:N0}."
                        : requestedLimit == 0
                            ? "Bot user limit disabled."
                            : $"Bot user limit set to {requestedLimit:N0}.",
                    false,
                    stoppingToken);

                string limitText =
                    await BuildAdminStatisticsTextAsync(
                        preference.Language,
                        botToken,
                        stoppingToken);

                await EditTelegramHtmlWithMarkupAsync(
                    botToken,
                    chatId,
                    messageId,
                    limitText,
                    BuildAdminDashboardKeyboard(
                        preference.Language),
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    data,
                    "admin:close",
                    StringComparison.OrdinalIgnoreCase))
            {
                await AnswerCallbackQueryAsync(
                    botToken,
                    callbackId,
                    "Closed",
                    false,
                    stoppingToken);

                await EditTelegramHtmlWithMarkupAsync(
                    botToken,
                    chatId,
                    messageId,
                    preference.Language == "ar"
                        ? "🔐 تم إغلاق لوحة الإدارة."
                        : "🔐 Admin panel closed.",
                    new
                    {
                        inline_keyboard =
                            Array.Empty<object[]>()
                    },
                    stoppingToken);

                return;
            }

            await AnswerCallbackQueryAsync(
                botToken,
                callbackId,
                preference.Language == "ar"
                    ? "تم تحديث الإحصائيات."
                    : "Statistics refreshed.",
                false,
                stoppingToken);

            string text =
                await BuildAdminStatisticsTextAsync(
                    preference.Language,
                    botToken,
                    stoppingToken);

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                chatId,
                messageId,
                text,
                BuildAdminDashboardKeyboard(
                    preference.Language),
                stoppingToken);
        }

        private async Task SendKnownUsersPageAsync(
                string botToken,
                long chatId,
                string language,
                int page,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                chatId,
                await BuildKnownUsersPageTextAsync(language, page),
                await BuildKnownUsersKeyboardAsync(language, page),
                stoppingToken);
        }

        private async Task<string> BuildKnownUsersPageTextAsync(
                string language,
                int page)
        {
            LoadTelegramUserActivity();
            LoadAdminSettings();

            const int pageSize = 20;

            string botToken = NormalizeBotToken(
                _configuration["Telegram:BotToken"] ?? string.Empty);

            HashSet<long> currentIds =
                await GetCurrentSubscribedBotUserIdsAsync(
                    botToken,
                    CancellationToken.None);

            List<long> ids;
            lock (_telegramAdminStateLock)
            {
                ids = currentIds
                    .Where(id => id > 0)
                    .OrderByDescending(id => _telegramUserLastSeenUtc.TryGetValue(id, out DateTime seen) ? seen : DateTime.MinValue)
                    .ThenBy(id => id)
                    .ToList();
            }

            int totalPages = Math.Max(1, (ids.Count + pageSize - 1) / pageSize);
            page = Math.Min(page, totalPages - 1);

            StringBuilder sb = new StringBuilder();
            bool arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine(arabic
                ? $"👥 <b>المستخدمون الحاليون</b> — صفحة {page + 1}/{totalPages}"
                : $"👥 <b>Active Users</b> — Page {page + 1}/{totalPages}");
            sb.AppendLine(arabic
                ? $"المشتركون الحاليون: <b>{ids.Count:N0}</b>"
                : $"Current bot users & channel members: <b>{ids.Count:N0}</b>");
            sb.AppendLine();

            foreach (long id in ids.Skip(page * pageSize).Take(pageSize))
            {
                string username = string.Empty;
                string firstName = string.Empty;
                DateTime lastSeen = DateTime.MinValue;

                lock (_telegramAdminStateLock)
                {
                    _telegramUsernames.TryGetValue(id, out username);
                    _telegramUserLastSeenUtc.TryGetValue(id, out lastSeen);
                }

                lock (_preferencesLock)
                {
                    if (_userPreferences.TryGetValue(id, out UserPreference? userPreference))
                        firstName = userPreference.FirstName ?? string.Empty;
                }

                bool banned = IsBannedUser(id);
                bool admin = IsAdminUser(id);
                bool superAdmin = IsSuperAdminUser(id);

                string name = !string.IsNullOrWhiteSpace(username)
                    ? "@" + username
                    : (!string.IsNullOrWhiteSpace(firstName) ? firstName : "Telegram User");

                string role = superAdmin
                    ? "SUPER ADMIN"
                    : admin
                        ? "ADMIN"
                        : banned
                            ? "BANNED"
                            : "USER";

                string seen = lastSeen == DateTime.MinValue
                    ? "-"
                    : lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                sb.AppendLine($"<b>{EscapeTelegramHtml(name)}</b>");
                sb.AppendLine($"   🆔 <code>{id}</code>");
                sb.AppendLine($"   👤 {EscapeTelegramHtml(role)}");
                sb.AppendLine($"   🕐 Last use: <code>{EscapeTelegramHtml(seen)}</code>");
                sb.AppendLine("────────────────────");
            }

            if (ids.Count == 0)
            {
                sb.AppendLine(arabic
                    ? "لا يوجد مستخدمون مسجلون حتى الآن."
                    : "No known users yet.");
            }

            sb.AppendLine();
            sb.Append(arabic
                ? "استخدم <code>/ban TELEGRAM_ID</code> أو <code>/unban TELEGRAM_ID</code>."
                : "Use <code>/ban TELEGRAM_ID</code> or <code>/unban TELEGRAM_ID</code>.");

            return sb.ToString();
        }

        private async Task<object> BuildKnownUsersKeyboardAsync(string language, int page)
        {
            LoadTelegramUserActivity();
            LoadAdminSettings();

            string botToken = NormalizeBotToken(
                _configuration["Telegram:BotToken"] ?? string.Empty);

            HashSet<long> currentIds =
                await GetCurrentSubscribedBotUserIdsAsync(
                    botToken,
                    CancellationToken.None);

            int count = currentIds.Count;

            const int pageSize = 25;
            int totalPages = Math.Max(1, (count + pageSize - 1) / pageSize);
            page = Math.Min(Math.Max(0, page), totalPages - 1);
            bool arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
            var rows = new List<object[]>();

            if (page > 0 || page < totalPages - 1)
            {
                var nav = new List<object>();
                if (page > 0)
                    nav.Add(new { text = "◀️", callback_data = "admin:users:" + (page - 1).ToString(CultureInfo.InvariantCulture) });
                if (page < totalPages - 1)
                    nav.Add(new { text = "▶️", callback_data = "admin:users:" + (page + 1).ToString(CultureInfo.InvariantCulture) });
                rows.Add(nav.ToArray());
            }

            rows.Add(new object[]
            {
                new
                {
                    text = arabic ? "🔄 تحديث" : "🔄 Refresh",
                    callback_data = "admin:users:" + page.ToString(CultureInfo.InvariantCulture)
                },
                new
                {
                    text = arabic ? "🔙 لوحة الإدارة" : "🔙 Admin Panel",
                    callback_data = "admin:refresh"
                }
            });

            return new { inline_keyboard = rows.ToArray() };
        }

        private object BuildAdminDashboardKeyboard(
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            return new
            {
                inline_keyboard =
                    new object[][]
                    {
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🔄 تحديث الإحصائيات"
                                        : "🔄 Refresh Statistics",
                                callback_data =
                                    "admin:refresh"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    "100",
                                callback_data =
                                    "admin:limit:100"
                            },
                            new
                            {
                                text =
                                    "500",
                                callback_data =
                                    "admin:limit:500"
                            },
                            new
                            {
                                text =
                                    "1,000",
                                callback_data =
                                    "admin:limit:1000"
                            },
                            new
                            {
                                text =
                                    "2,000",
                                callback_data =
                                    "admin:limit:2000"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "♾️ بدون حد"
                                        : "♾️ Unlimited",
                                callback_data =
                                    "admin:limit:0"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "👥 المستخدمون"
                                        : "👥 Users",
                                callback_data =
                                    "admin:users:0"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "✖ إغلاق"
                                        : "✖ Close",
                                callback_data =
                                    "admin:close"
                            }
                        }
                    }
            };
        }

        private async Task<string> BuildAdminStatisticsTextAsync(
                string language,
                string botToken,
                CancellationToken stoppingToken)
        {
            LoadTelegramUserActivity();

            HashSet<long> subscribedBotUserIds =
                await GetCurrentSubscribedBotUserIdsAsync(
                    botToken,
                    stoppingToken);

            int botUsers =
                subscribedBotUserIds.Count;

            int usersWithAlerts;

            lock (_preferencesLock)
            {
                usersWithAlerts =
                    _userPreferences.Values.Count(
                        preference =>
                            subscribedBotUserIds.Contains(
                                preference.UserId) &&
                            preference.IsCompleted &&
                            (preference.SelectedCinemaSlugs.Count > 0 ||
                             preference.AlertScopes.Count > 0));
            }

            int activeWindowMinutes =
                ReadIntegerSetting(
                    "Telegram:ActiveUserWindowMinutes",
                    defaultValue: 30,
                    minimumValue: 1,
                    maximumValue: 1440);

            DateTime activeSinceUtc =
                DateTime.UtcNow.AddMinutes(
                    -activeWindowMinutes);

            int activeUsers;

            lock (_telegramAdminStateLock)
            {
                activeUsers =
                    _telegramUserLastSeenUtc
                        .Where(item =>
                            subscribedBotUserIds.Contains(
                                item.Key))
                        .Count(item =>
                            item.Value >= activeSinceUtc);
            }

            int channelSubscribers =
                await GetTelegramChannelSubscriberCountAsync(
                    botToken,
                    stoppingToken);

            string channelSubscribersText =
                channelSubscribers >= 0
                    ? channelSubscribers.ToString(
                        "N0",
                        CultureInfo.InvariantCulture)
                    : "N/A";

            int maxBotUsers =
                GetMaxBotUsersLimit();

            string limitText =
                maxBotUsers > 0
                    ? maxBotUsers.ToString(
                        "N0",
                        CultureInfo.InvariantCulture)
                    : "∞";

            string updated =
                DateTime.Now.ToString(
                    "dddd - dd MMM yyyy, hh:mm tt",
                    CultureInfo.InvariantCulture);

            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            if (arabic)
            {
                return
                    "📊 <b>إحصائيات CinemaBot</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n\n" +
                    "🤖 <b>مستخدمو البوت</b>\n" +
                    botUsers.ToString(
                        "N0",
                        CultureInfo.InvariantCulture) +
                    " / " + limitText +
                    "\n" +
                    "<i>المشتركين في القناة والمستخدمين فعليًا للبوت</i>\n\n" +
                    "📢 <b>مشتركو القناة</b>\n" +
                    channelSubscribersText +
                    "\n\n" +
                    "🟢 <b>المستخدمون النشطون حاليًا</b>\n" +
                    activeUsers.ToString(
                        "N0",
                        CultureInfo.InvariantCulture) +
                    "\n" +
                    $"<i>آخر {activeWindowMinutes} دقيقة</i>\n\n" +
                    "🔔 <b>المستخدمون أصحاب التنبيهات</b>\n" +
                    usersWithAlerts.ToString(
                        "N0",
                        CultureInfo.InvariantCulture) +
                    "\n\n" +
                    "👥 <b>حد مستخدمي البوت</b>\n" +
                    limitText +
                    "\n\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "🕐 <b>آخر تحديث</b>\n" +
                    EscapeTelegramHtml(updated);
            }

            return
                "📊 <b>CinemaBot Statistics</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                "🤖 <b>Bot Users</b>\n" +
                botUsers.ToString(
                    "N0",
                    CultureInfo.InvariantCulture) +
                " / " + limitText +
                "\n" +
                "<i>Current channel members who actively use the bot</i>\n\n" +
                "📢 <b>Channel Subscribers</b>\n" +
                channelSubscribersText +
                "\n\n" +
                "🟢 <b>Active Users</b>\n" +
                activeUsers.ToString(
                    "N0",
                    CultureInfo.InvariantCulture) +
                "\n" +
                $"<i>Last {activeWindowMinutes} minutes</i>\n\n" +
                "🔔 <b>Users with Alerts</b>\n" +
                usersWithAlerts.ToString(
                    "N0",
                    CultureInfo.InvariantCulture) +
                "\n\n" +
                "👥 <b>Bot User Limit</b>\n" +
                limitText +
                "\n\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "🕐 <b>Last Updated</b>\n" +
                EscapeTelegramHtml(updated);
        }

        private void SaveAdminSettingsUnsafe()
        {
            try
            {
                string? directory = Path.GetDirectoryName(AdminSettingsFilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var usernames = new Dictionary<string, string>();
                foreach (KeyValuePair<long, string> item in _telegramUsernames)
                    usernames[item.Key.ToString(CultureInfo.InvariantCulture)] = item.Value;

                string json = JsonSerializer.Serialize(new
                {
                    MaxBotUsers = _adminMaxBotUsersOverride ?? ReadIntegerSetting("Telegram:MaxBotUsers", 1000, 0, 100000),
                    AdminUserIds = _dynamicAdminUserIds.OrderBy(id => id).ToArray(),
                    BannedUserIds = _bannedUserIds.OrderBy(id => id).ToArray(),
                    Usernames = usernames,
                    EarlyBookingAlertsEnabled = _earlyBookingAlertsEnabled,
                    EarlyBookingAlertDelayMinutes = _earlyBookingAlertDelayMinutes,
                    EarlyBookingAlertUserOverrides = _earlyBookingAlertUserOverrides
                        .OrderBy(item => item.Key)
                        .ToDictionary(item => item.Key.ToString(CultureInfo.InvariantCulture), item => item.Value)
                }, new JsonSerializerOptions { WriteIndented = true });

                SavePersistentJson(
                    Path.GetFileName(AdminSettingsFilePath),
                    AdminSettingsFilePath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist Telegram admin settings.");
            }
        }

        private void SaveScanRange(int? daysAhead, string? mode, DateTime? specificDate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(ScanRangeSettingsFilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var payload = new
                {
                    DaysAhead = daysAhead,
                    Mode = mode,
                    SpecificDate = specificDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
                string json =
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                SavePersistentJson(
                    Path.GetFileName(ScanRangeSettingsFilePath),
                    ScanRangeSettingsFilePath,
                    json);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not save scan range settings."); }
        }

        private async Task<bool> SetTelegramChannelBanAsync(string botToken, long userId, bool ban, CancellationToken stoppingToken)
        {
            string channel = (_configuration["Telegram:ChannelUsername"] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(channel)) return false;
            string method = ban ? "banChatMember" : "unbanChatMember";
            string url = $"https://api.telegram.org/bot{botToken}/{method}";
            using var content = new StringContent(JsonSerializer.Serialize(new { chat_id = channel, user_id = userId, only_if_banned = !ban }), Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = await _telegramHttpClient.PostAsync(url, content, stoppingToken);
                string body = await response.Content.ReadAsStringAsync(stoppingToken);
                if (!response.IsSuccessStatusCode) return false;
                using JsonDocument document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Telegram {Method} failed for user {UserId}.", method, userId);
                return false;
            }
        }

        private int GetMaxBotUsersLimit()
        {
            LoadAdminSettings();

            lock (_telegramAdminStateLock)
            {
                if (_adminMaxBotUsersOverride.HasValue)
                {
                    return _adminMaxBotUsersOverride.Value;
                }
            }

            return ReadIntegerSetting(
                "Telegram:MaxBotUsers",
                defaultValue: 1000,
                minimumValue: 0,
                maximumValue: 100000);
        }

        private void LoadAdminSettings()
        {
            lock (_telegramAdminStateLock)
            {
                if (_adminLimitLoaded)
                {
                    return;
                }

                _adminLimitLoaded = true;

                try
                {
                    if (!TryLoadPersistentJson(
                            Path.GetFileName(AdminSettingsFilePath),
                            AdminSettingsFilePath,
                            out string json))
                    {
                        return;
                    }

                    using JsonDocument document =
                        JsonDocument.Parse(json);

                    if (document.RootElement.TryGetProperty("AdminUserIds", out JsonElement adminIds) &&
                        adminIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement idElement in adminIds.EnumerateArray())
                        {
                            if (idElement.TryGetInt64(out long id) && id > 0)
                                _dynamicAdminUserIds.Add(id);
                        }
                    }

                    if (document.RootElement.TryGetProperty("BannedUserIds", out JsonElement bannedIds) &&
                        bannedIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement idElement in bannedIds.EnumerateArray())
                        {
                            if (idElement.TryGetInt64(out long id) && id > 0)
                                _bannedUserIds.Add(id);
                        }
                    }

                    if (document.RootElement.TryGetProperty("Usernames", out JsonElement usernames) &&
                        usernames.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in usernames.EnumerateObject())
                        {
                            if (long.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) &&
                                property.Value.ValueKind == JsonValueKind.String)
                            {
                                string name = NormalizeTelegramUsername(property.Value.GetString() ?? string.Empty);
                                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                                {
                                    _telegramUsernames[id] = name;
                                    _telegramUsernameToUserId[name] = id;
                                }
                            }
                        }
                    }

                    if (document.RootElement.TryGetProperty(
                            "EarlyBookingAlertsEnabled",
                            out JsonElement earlyEnabled) &&
                        (earlyEnabled.ValueKind == JsonValueKind.True ||
                         earlyEnabled.ValueKind == JsonValueKind.False))
                    {
                        _earlyBookingAlertsEnabled = earlyEnabled.GetBoolean();
                    }

                    if (document.RootElement.TryGetProperty(
                            "EarlyBookingAlertDelayMinutes",
                            out JsonElement earlyDelay) &&
                        earlyDelay.TryGetInt32(out int parsedEarlyDelay))
                    {
                        _earlyBookingAlertDelayMinutes = Math.Max(0, Math.Min(parsedEarlyDelay, 1440));
                    }

                    if (document.RootElement.TryGetProperty(
                            "EarlyBookingAlertUserOverrides",
                            out JsonElement earlyOverrides) &&
                        earlyOverrides.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in earlyOverrides.EnumerateObject())
                        {
                            if (long.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) &&
                                id > 0 &&
                                property.Value.TryGetInt32(out int overrideMinutes) &&
                                (overrideMinutes == -1 || (overrideMinutes >= 0 && overrideMinutes <= 1440)))
                            {
                                _earlyBookingAlertUserOverrides[id] = overrideMinutes;
                            }
                        }
                    }

                    if (document.RootElement.TryGetProperty(
                            "MaxBotUsers",
                            out JsonElement value) &&
                        value.TryGetInt32(
                            out int parsed))
                    {
                        _adminMaxBotUsersOverride =
                            Math.Max(
                                0,
                                Math.Min(
                                    parsed,
                                    100000));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not load persisted Telegram admin settings.");
                }
            }
        }

        private int? GetEarlyBookingAlertDelayMinutes(long userId)
        {
            if (IsAdminUser(userId))
            {
                return 0;
            }

            LoadAdminSettings();

            lock (_earlyBookingAlertLock)
            {
                if (_earlyBookingAlertUserOverrides.TryGetValue(userId, out int userOverride))
                {
                    return userOverride < 0 ? null : userOverride;
                }

                return _earlyBookingAlertsEnabled
                    ? _earlyBookingAlertDelayMinutes
                    : null;
            }
        }

        private async Task HandleEarlyBookingAlertsCommandAsync(
                string botToken,
                long chatId,
                string argument,
                CancellationToken stoppingToken)
        {
            LoadAdminSettings();

            string[] parts = (argument ?? string.Empty)
                .Trim()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                await SendEarlyBookingAlertSettingsAsync(botToken, chatId, stoppingToken);
                return;
            }

            string action = parts[0].ToLowerInvariant();

            if (action == "on" || action == "enable")
            {
                lock (_earlyBookingAlertLock)
                {
                    _earlyBookingAlertsEnabled = true;
                    SaveAdminSettingsUnsafe();
                }

                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    "✅ تم تشغيل <b>Early Booking Alerts</b> لكل الـusers الذين ليس لديهم override خاص.\n" +
                    "الـAdmin والـSuper Admin دائمًا يستلموا التنبيه فورًا.", stoppingToken);
                return;
            }

            if (action == "off" || action == "disable")
            {
                lock (_earlyBookingAlertLock)
                {
                    _earlyBookingAlertsEnabled = false;
                    SaveAdminSettingsUnsafe();
                }

                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    "⛔ تم إيقاف <b>Early Booking Alerts</b> عالميًا.\n" +
                    "الـAdmin والـSuper Admin ما زالوا يستلموا التنبيه فورًا.", stoppingToken);
                return;
            }

            if (action == "reset")
            {
                lock (_earlyBookingAlertLock)
                {
                    _earlyBookingAlertsEnabled = true;
                    _earlyBookingAlertDelayMinutes = 5;
                    _earlyBookingAlertUserOverrides.Clear();
                    SaveAdminSettingsUnsafe();
                }

                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    "♻️ تم إرجاع إعدادات <b>Early Booking Alerts</b> للوضع الافتراضي: <b>5 دقائق</b>.", stoppingToken);
                return;
            }

            if (action == "user" || action == "users")
            {
                if (parts.Length < 3)
                {
                    await SendSimpleTelegramHtmlAsync(botToken, chatId,
                        "استخدم:\n" +
                        "<code>/earlyalerts user 123456789 10</code>\n" +
                        "<code>/earlyalerts user 123456789 off</code>\n" +
                        "<code>/earlyalerts user 123456789 default</code>\n\n" +
                        "ولأكثر من user: <code>/earlyalerts users 123,456,789 10</code>", stoppingToken);
                    return;
                }

                string[] userIds = parts[1]
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

                var parsedIds = new List<long>();
                foreach (string rawId in userIds)
                {
                    if (long.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long targetId) && targetId > 0)
                        parsedIds.Add(targetId);
                }

                if (parsedIds.Count == 0)
                {
                    await SendSimpleTelegramHtmlAsync(botToken, chatId,
                        "❌ اكتب Telegram ID صحيح، مثال: <code>/earlyalerts user 123456789 10</code>", stoppingToken);
                    return;
                }

                string value = parts[2].ToLowerInvariant();
                int overrideValue;

                if (value == "default" || value == "global" || value == "reset")
                {
                    lock (_earlyBookingAlertLock)
                    {
                        foreach (long targetId in parsedIds)
                            _earlyBookingAlertUserOverrides.Remove(targetId);
                        SaveAdminSettingsUnsafe();
                    }

                    await SendSimpleTelegramHtmlAsync(botToken, chatId,
                        $"♻️ تم إرجاع {parsedIds.Count} user إلى الإعداد العالمي.", stoppingToken);
                    return;
                }

                if (value == "off" || value == "disable")
                {
                    overrideValue = -1;
                }
                else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out overrideValue) ||
                         overrideValue < 0 || overrideValue > 1440)
                {
                    await SendSimpleTelegramHtmlAsync(botToken, chatId,
                        "❌ الوقت يجب أن يكون من <b>0</b> إلى <b>1440</b> دقيقة، أو <code>off</code>، أو <code>default</code>.", stoppingToken);
                    return;
                }

                lock (_earlyBookingAlertLock)
                {
                    foreach (long targetId in parsedIds)
                    {
                        if (IsSuperAdminUser(targetId))
                            continue;
                        _earlyBookingAlertUserOverrides[targetId] = overrideValue;
                    }
                    SaveAdminSettingsUnsafe();
                }

                string description = overrideValue < 0 ? "إيقاف" : $"{overrideValue} دقيقة";
                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    $"✅ تم ضبط {parsedIds.Count} user على: <b>{description}</b>.\n" +
                    "استخدم <code>default</code> لإرجاعه للإعداد العالمي.", stoppingToken);
                return;
            }

            if (int.TryParse(action, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) &&
                minutes >= 0 && minutes <= 1440)
            {
                lock (_earlyBookingAlertLock)
                {
                    _earlyBookingAlertsEnabled = true;
                    _earlyBookingAlertDelayMinutes = minutes;
                    SaveAdminSettingsUnsafe();
                }

                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    $"✅ تم ضبط التأخير العالمي للـusers العاديين على <b>{minutes} دقيقة</b>.\n" +
                    "الـAdmin والـSuper Admin دائمًا فورًا.", stoppingToken);
                return;
            }

            await SendSimpleTelegramHtmlAsync(botToken, chatId,
                "استخدم:\n" +
                "<code>/earlyalerts</code> — عرض الإعدادات\n" +
                "<code>/earlyalerts 5</code> — ضبط التأخير\n" +
                "<code>/earlyalerts 10</code> — مثال 10 دقائق\n" +
                "<code>/earlyalerts off</code> — إيقافه عالميًا\n" +
                "<code>/earlyalerts on</code> — تشغيله\n" +
                "<code>/earlyalerts user 123456789 15</code> — override لـuser\n" +
                "<code>/earlyalerts user 123456789 off</code> — إيقافه لهذا user فقط\n" +
                "<code>/earlyalerts user 123456789 default</code> — إرجاعه للإعداد العالمي\n" +
                "<code>/earlyalerts reset</code> — إرجاع كل شيء إلى 5 دقائق.", stoppingToken);
        }

        private async Task SendEarlyBookingAlertSettingsAsync(
                string botToken,
                long chatId,
                CancellationToken stoppingToken)
        {
            lock (_earlyBookingAlertLock)
            {
                // The actual message is sent below so the lock is never held over I/O.
            }

            bool enabled;
            int delay;
            int overrides;
            lock (_earlyBookingAlertLock)
            {
                enabled = _earlyBookingAlertsEnabled;
                delay = _earlyBookingAlertDelayMinutes;
                overrides = _earlyBookingAlertUserOverrides.Count;
            }

            await SendSimpleTelegramHtmlAsync(botToken, chatId,
                "⚙️ <b>Early Booking Alerts</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"Global: {(enabled ? "✅ ON" : "⛔ OFF")}\n" +
                $"Delay: <b>{delay} minutes</b>\n" +
                $"User overrides: <b>{overrides}</b>\n\n" +
                "👑 Admin/Super Admin: <b>0 minutes</b> دائمًا\n\n" +
                "<code>/earlyalerts 10</code>\n" +
                "<code>/earlyalerts off</code>\n" +
                "<code>/earlyalerts user 123456789 15</code>\n" +
                "<code>/earlyalerts user 123456789 off</code>\n" +
                "<code>/earlyalerts user 123456789 default</code>", stoppingToken);
        }

        private void SetMaxBotUsersLimit(
                int value)
        {
            value =
                Math.Max(
                    0,
                    Math.Min(
                        value,
                        100000));

            lock (_telegramAdminStateLock)
            {
                _adminMaxBotUsersOverride =
                    value;

                _adminLimitLoaded =
                    true;

                try
                {
                    string? directory =
                        Path.GetDirectoryName(
                            AdminSettingsFilePath);

                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(
                            directory);
                    }

                    SaveAdminSettingsUnsafe();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not persist the Telegram bot user limit.");
                }
            }
        }

        private bool IsSuperAdminUser(long userId)
        {
            string configured =
                (_configuration["Telegram:SuperAdminUserId"] ?? string.Empty).Trim();

            if (long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out long superAdminId) &&
                superAdminId > 0)
            {
                return userId == superAdminId;
            }

            // Backward-compatible fallback: the first configured admin is the super admin.
            long[] configuredAdmins = GetConfiguredAdminUserIds().ToArray();
            return configuredAdmins.Length > 0 && userId == configuredAdmins[0];
        }

        private IEnumerable<long> GetConfiguredAdminUserIds()
        {
            var ids = new HashSet<long>();

            foreach (string configuredId in
                     _configuration.GetSection("Telegram:AdminUserIds")
                         .GetChildren()
                         .Select(section => section.Value ?? string.Empty))
            {
                if (long.TryParse(configuredId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0)
                {
                    ids.Add(id);
                }
            }

            LoadAdminSettings();
            lock (_telegramAdminStateLock)
            {
                foreach (long id in _dynamicAdminUserIds)
                {
                    if (id > 0) ids.Add(id);
                }
            }

            string superAdmin = (_configuration["Telegram:SuperAdminUserId"] ?? string.Empty).Trim();
            if (long.TryParse(superAdmin, NumberStyles.Integer, CultureInfo.InvariantCulture, out long superAdminId) && superAdminId > 0)
            {
                ids.Add(superAdminId);
            }

            return ids;
        }

        private async Task SendSimpleTelegramHtmlAsync(
                string botToken,
                long chatId,
                string message,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                chatId,
                message,
                new { inline_keyboard = Array.Empty<object[]>() },
                stoppingToken);
        }

        private bool IsBannedUser(long userId)
        {
            if (userId <= 0) return false;
            LoadAdminSettings();
            lock (_telegramAdminStateLock)
            {
                return _bannedUserIds.Contains(userId);
            }
        }

        private static string GetTelegramCommandArgument(string messageText)
        {
            if (string.IsNullOrWhiteSpace(messageText)) return string.Empty;
            string[] parts = messageText.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1].Trim() : string.Empty;
        }

        private static string NormalizeTelegramUsername(string value)
        {
            string username = (value ?? string.Empty).Trim();
            if (username.StartsWith("@", StringComparison.Ordinal)) username = username.Substring(1);
            return username.Trim();
        }

        private void RegisterTelegramUsername(long userId, string username)
        {
            username = NormalizeTelegramUsername(username);
            if (userId <= 0 || string.IsNullOrWhiteSpace(username)) return;
            LoadAdminSettings();
            lock (_telegramAdminStateLock)
            {
                if (_telegramUsernames.TryGetValue(userId, out string old) && !string.Equals(old, username, StringComparison.OrdinalIgnoreCase))
                    _telegramUsernameToUserId.Remove(old);
                _telegramUsernames[userId] = username;
                SaveAdminSettingsUnsafe();
            }
        }

        private async Task HandleAdminUserManagementCommandAsync(string botToken, long chatId, long actorId, string command, string username, CancellationToken stoppingToken)
        {
            if (!IsSuperAdminUser(actorId)) return;
            LoadAdminSettings();
            long targetId = 0;
            lock (_telegramAdminStateLock)
            {
                _telegramUsernameToUserId.TryGetValue(username, out targetId);
            }

            if (targetId <= 0)
            {
                await SendSimpleTelegramHtmlAsync(botToken, chatId,
                    $"❌ مش لاقي <b>@{EscapeTelegramHtml(username)}</b> في بيانات المستخدمين.\\n\\nخليه يفتح البوت مرة واحدة على الأقل عشان أقدر أربط الـusername بالـTelegram ID.", stoppingToken);
                return;
            }

            if (targetId == actorId)
            {
                await SendSimpleTelegramHtmlAsync(botToken, chatId, "ℹ️ أنت بالفعل الـSuper Admin.", stoppingToken);
                return;
            }

            if (IsSuperAdminUser(targetId))
            {
                await SendSimpleTelegramHtmlAsync(botToken, chatId, "⛔ مينفعش إزالة الـSuper Admin.", stoppingToken);
                return;
            }

            bool adding = string.Equals(command, "/addadmin", StringComparison.OrdinalIgnoreCase);
            bool changed;
            lock (_telegramAdminStateLock)
            {
                changed = adding ? _dynamicAdminUserIds.Add(targetId) : _dynamicAdminUserIds.Remove(targetId);
                SaveAdminSettingsUnsafe();
            }

            if (changed)
            {
                await ConfigureTelegramAdminMenuForUserAsync(botToken, targetId, stoppingToken);
            }

            await SendSimpleTelegramHtmlAsync(botToken, chatId,
                adding
                    ? (changed ? $"✅ تم إضافة <b>@{EscapeTelegramHtml(username)}</b> كـ Admin." : "ℹ️ المستخدم ده Admin بالفعل.")
                    : (changed ? $"✅ تم إزالة <b>@{EscapeTelegramHtml(username)}</b> من الـAdmins." : "ℹ️ المستخدم ده مش Admin."),
                stoppingToken);
        }

        private async Task ConfigureTelegramAdminMenuForUserAsync(
                string botToken,
                long userId,
                CancellationToken stoppingToken)
        {
            bool isAdmin = IsAdminUser(userId);
            bool isSuperAdmin = IsSuperAdminUser(userId);

            var english = new List<object>
            {
                new { command = "start", description = "Open CinemaBot home" },
                new { command = "current", description = "Follow current movies" },
                new { command = "comingsoon", description = "Follow Coming Soon movies" },
                new { command = "cinemas", description = "Browse live cinema showtimes" },
                new { command = "alerts", description = "Manage my alerts" }
            };
            var arabic = new List<object>
            {
                new { command = "start", description = "فتح القائمة الرئيسية" },
                new { command = "current", description = "متابعة الأفلام الحالية" },
                new { command = "comingsoon", description = "متابعة أفلام قريباً" },
                new { command = "cinemas", description = "تصفح مواعيد السينمات المباشرة" },
                new { command = "alerts", description = "إدارة تنبيهاتي" }
            };

            if (isAdmin)
            {
                english.Add(new { command = "admin", description = "Open admin statistics" });
                english.Add(new { command = "myid", description = "Show my Telegram ID" });
                english.Add(new { command = "setlimit", description = "Set bot user limit" });
                arabic.Add(new { command = "admin", description = "فتح لوحة الإدارة" });
                arabic.Add(new { command = "myid", description = "عرض Telegram ID" });
                arabic.Add(new { command = "setlimit", description = "تحديد حد مستخدمي البوت" });
            }

            if (isSuperAdmin)
            {
                english.Add(new { command = "addadmin", description = "Add an admin by username" });
                english.Add(new { command = "removeadmin", description = "Remove an admin by username" });
                english.Add(new { command = "scan", description = "Set scan range" });
                english.Add(new { command = "ban", description = "Ban a user" });
                english.Add(new { command = "unban", description = "Unban a user" });
                english.Add(new { command = "earlyalerts", description = "Early booking alerts" });
                arabic.Add(new { command = "addadmin", description = "إضافة أدمن باليوزرنيم" });
                arabic.Add(new { command = "removeadmin", description = "حذف أدمن باليوزرنيم" });
                arabic.Add(new { command = "scan", description = "تحديد نطاق الفحص" });
                arabic.Add(new { command = "ban", description = "حظر مستخدم" });
                arabic.Add(new { command = "unban", description = "إلغاء حظر مستخدم" });
                arabic.Add(new { command = "earlyalerts", description = "تنبيهات الحجز المبكر" });
            }

            await PostTelegramJsonAsync(botToken, "setMyCommands", new { commands = english.ToArray(), scope = new { type = "chat", chat_id = userId }, language_code = "" }, stoppingToken);
            await PostTelegramJsonAsync(botToken, "setMyCommands", new { commands = arabic.ToArray(), scope = new { type = "chat", chat_id = userId }, language_code = "ar" }, stoppingToken);
        }

        private async Task<long?> GetTelegramBotUserIdAsync(string botToken, CancellationToken stoppingToken)
        {
            string apiUrl = $"https://api.telegram.org/bot{botToken}/getMe";
            try
            {
                using HttpResponseMessage response = await _telegramHttpClient.GetAsync(apiUrl, stoppingToken);
                string body = await response.Content.ReadAsStringAsync(stoppingToken);
                if (!response.IsSuccessStatusCode) return null;
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean() &&
                    document.RootElement.TryGetProperty("result", out JsonElement result) &&
                    result.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long botId))
                    return botId;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch { }
            return null;
        }

        private async Task<int> GetTelegramChannelSubscriberCountAsync(
                string botToken,
                CancellationToken stoppingToken)
        {
            string channel =
                (_configuration["Telegram:ChannelUsername"] ??
                 string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(
                    channel))
            {
                return -1;
            }

            string apiUrl =
                $"https://api.telegram.org/bot{botToken}/getChatMemberCount";

            try
            {
                using var content =
                    new StringContent(
                        JsonSerializer.Serialize(
                            new
                            {
                                chat_id =
                                    channel
                            }),
                        Encoding.UTF8,
                        "application/json");

                using HttpResponseMessage response =
                    await _telegramHttpClient.PostAsync(
                        apiUrl,
                        content,
                        stoppingToken);

                string body =
                    await response.Content.ReadAsStringAsync(
                        stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Could not read Telegram channel subscriber count. HTTP {StatusCode}.",
                        (int)response.StatusCode);

                    return -1;
                }

                using JsonDocument document =
                    JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty(
                        "ok",
                        out JsonElement okElement) &&
                    okElement.ValueKind ==
                    JsonValueKind.True &&
                    document.RootElement.TryGetProperty(
                        "result",
                        out JsonElement resultElement) &&
                    resultElement.TryGetInt32(
                        out int count))
                {
                    long? botUserId =
                        await GetTelegramBotUserIdAsync(
                            botToken,
                            stoppingToken);

                    if (botUserId.HasValue)
                    {
                        bool botIsMember =
                            await CheckChannelMembershipAsync(
                                botToken,
                                channel,
                                botUserId.Value,
                                stoppingToken);

                        if (botIsMember && count > 0)
                        {
                            count--;
                        }
                    }

                    return Math.Max(0, count);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not read Telegram channel subscriber count.");
            }

            return -1;
        }

        public static class TelegramMyIdCommand
        {
            public static async Task HandleAsync(
                Func<string, long, string, CancellationToken, Task> sendMessageAsync,
                ILogger logger,
                long userId,
                long chatId,
                string username,
                string firstName,
                bool isAdmin,
                CancellationToken stoppingToken)
            {
                logger.LogInformation(
                    "Telegram /myid requested. UserId={UserId}, Username={Username}, ChatId={ChatId}, IsAdmin={IsAdmin}",
                    userId,
                    string.IsNullOrWhiteSpace(username) ? "—" : username,
                    chatId,
                    isAdmin);

                string message =
                    "🔐 <b>Telegram Account Information</b>\n\n" +
                    "🆔 <b>User ID:</b> <code>" +
                    userId.ToString(CultureInfo.InvariantCulture) +
                    "</code>\n" +
                    "👤 <b>Name:</b> " +
                    EscapeHtml(firstName) +
                    "\n" +
                    "🔗 <b>Username:</b> " +
                    EscapeHtml(
                        string.IsNullOrWhiteSpace(username)
                            ? "—"
                            : "@" + username.TrimStart('@')) +
                    "\n\n" +
                    "👑 <b>Admin:</b> " +
                    (isAdmin ? "Yes ✅" : "No") +
                    "\n\n" +
                    "Copy this User ID into " +
                    "<code>Telegram:AdminUserIds</code> " +
                    "in appsettings.json.";

                await sendMessageAsync(
                    message,
                    chatId,
                    "HTML",
                    stoppingToken).ConfigureAwait(false);
            }

            private static string EscapeHtml(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return "—";
                }

                return value
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
            }
        }
    }
}
