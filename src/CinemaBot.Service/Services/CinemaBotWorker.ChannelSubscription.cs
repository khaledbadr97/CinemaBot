using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    public partial class CinemaBotWorker
    {
        private readonly object _channelMembershipLock = new object();

        private readonly Dictionary<long, ChannelMembershipCacheEntry>
            _channelMembershipCache =
                new Dictionary<long, ChannelMembershipCacheEntry>();

        private readonly object _botSubscriberLock = new object();

        private readonly HashSet<long> _knownBotSubscriberIds =
            new HashSet<long>();

        private string BotSubscribersFilePath =>
            Path.Combine(
                _dataDirectory,
                "telegram-bot-subscribers.json");

        private sealed class ChannelMembershipCacheEntry
        {
            public bool IsSubscribed { get; set; }
            public DateTime CheckedAtUtc { get; set; }
        }

        private sealed class TelegramMemberDisplayInfo
        {
            public bool IsCurrentMember { get; set; }
            public bool IsBot { get; set; }
            public string FirstName { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
        }

        private async Task<bool> EnsureUserCanUseBotAsync(
            string botToken,
            long userId,
            long privateChatId,
            UserPreference preference,
            CancellationToken stoppingToken,
            bool forceRefresh = false)
        {
            LoadAdminSettings();
            bool isBanned;
            lock (_telegramAdminStateLock)
            {
                isBanned = _bannedUserIds.Contains(userId);
            }

            if (isBanned)
            {
                await SendSimpleTelegramHtmlAsync(
                    botToken,
                    privateChatId,
                    preference.Language == "ar"
                        ? "🚫 <b>تم حظر حسابك من استخدام CinemaBot.</b>"
                        : "🚫 <b>Your access to CinemaBot has been banned.</b>",
                    stoppingToken);
                return false;
            }

            if (!ReadBooleanSetting(
                    "Telegram:RequireChannelSubscription",
                    defaultValue: false))
            {
                return true;
            }

            string channelUsername =
                (_configuration["Telegram:ChannelUsername"] ??
                 string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(channelUsername))
            {
                _logger.LogError(
                    "Channel subscription is enabled but Telegram:ChannelUsername is empty.");

                await SendSubscriptionRequiredMessageAsync(
                    botToken,
                    privateChatId,
                    preference,
                    stoppingToken,
                    configurationError: true);

                return false;
            }

            int cacheMinutes =
                ReadIntegerSetting(
                    "Telegram:SubscriptionCheckCacheMinutes",
                    defaultValue: 0,
                    minimumValue: 0,
                    maximumValue: 60);

            if (!forceRefresh &&
                cacheMinutes > 0)
            {
                lock (_channelMembershipLock)
                {
                    if (_channelMembershipCache.TryGetValue(
                            userId,
                            out ChannelMembershipCacheEntry cached) &&
                        DateTime.UtcNow - cached.CheckedAtUtc <
                        TimeSpan.FromMinutes(cacheMinutes))
                    {
                        return cached.IsSubscribed;
                    }
                }
            }

            bool subscribed =
                await CheckChannelMembershipAsync(
                    botToken,
                    channelUsername,
                    userId,
                    stoppingToken);

            lock (_channelMembershipLock)
            {
                _channelMembershipCache[userId] =
                    new ChannelMembershipCacheEntry
                    {
                        IsSubscribed = subscribed,
                        CheckedAtUtc = DateTime.UtcNow
                    };
            }

            if (!subscribed)
            {
                lock (_botSubscriberLock)
                {
                    _knownBotSubscriberIds.Remove(
                        userId);
                }

                SaveBotSubscriberIds();

                await SendSubscriptionRequiredMessageAsync(
                    botToken,
                    privateChatId,
                    preference,
                    stoppingToken);

                return false;
            }

            bool alreadyBotUser;

            lock (_botSubscriberLock)
            {
                alreadyBotUser =
                    _knownBotSubscriberIds.Contains(
                        userId);
            }

            if (!alreadyBotUser)
            {
                HashSet<long> currentBotUsers =
                    await GetCurrentSubscribedBotUserIdsAsync(
                        botToken,
                        stoppingToken,
                        includeUserId: userId);

                int maximumBotUsers =
                    GetMaxBotUsersLimit();

                alreadyBotUser =
                    currentBotUsers.Contains(
                        userId);

                if (!alreadyBotUser &&
                    maximumBotUsers > 0 &&
                    currentBotUsers.Count >= maximumBotUsers)
                {
                    await SendBotUserLimitReachedMessageAsync(
                        botToken,
                        privateChatId,
                        preference,
                        maximumBotUsers,
                        stoppingToken);

                    return false;
                }
            }

            RegisterBotSubscriber(userId);

            return true;
        }

        private async Task<HashSet<long>> GetCurrentSubscribedBotUserIdsAsync(
                string botToken,
                CancellationToken stoppingToken,
                long? includeUserId = null)
        {
            long[] candidateIds;

            lock (_preferencesLock)
            {
                candidateIds =
                    _userPreferences.Keys
                        .Where(id => id > 0)
                        .ToArray();
            }

            if (includeUserId.HasValue &&
                includeUserId.Value > 0 &&
                !candidateIds.Contains(includeUserId.Value))
            {
                candidateIds =
                    candidateIds
                        .Concat(new[] { includeUserId.Value })
                        .Distinct()
                        .ToArray();
            }

            var subscribedIds =
                new HashSet<long>();

            if (candidateIds.Length == 0)
            {
                return subscribedIds;
            }

            using var semaphore =
                new SemaphoreSlim(8, 8);

            Task[] tasks =
                candidateIds.Select(
                    async userId =>
                    {
                        await semaphore.WaitAsync(
                            stoppingToken);

                        try
                        {
                            bool subscribed =
                                await CheckChannelMembershipAsync(
                                    botToken,
                                    (_configuration["Telegram:ChannelUsername"] ??
                                     string.Empty).Trim(),
                                    userId,
                                    stoppingToken);

                            if (subscribed)
                            {
                                lock (subscribedIds)
                                {
                                    subscribedIds.Add(userId);
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }).ToArray();

            await Task.WhenAll(tasks);

            lock (_botSubscriberLock)
            {
                _knownBotSubscriberIds.Clear();

                foreach (long id in subscribedIds)
                {
                    _knownBotSubscriberIds.Add(id);
                }
            }

            SaveBotSubscriberIds();

            return subscribedIds;
        }

        private async Task SendBotUserLimitReachedMessageAsync(
                string botToken,
                long privateChatId,
                UserPreference preference,
                int maximumBotUsers,
                CancellationToken stoppingToken)
        {
            string message =
                preference.Language == "ar"
                    ? "⚠️ <b>البوت وصل للحد الأقصى من المستخدمين.</b>\n\n" +
                      "عدد مستخدمي CinemaBot المسموح به حاليًا هو " +
                      maximumBotUsers.ToString(
                          "N0",
                          CultureInfo.InvariantCulture) +
                      ".\n\n" +
                      "لو عندك مشكلة في الدخول، حاول لاحقًا."
                    : "⚠️ <b>CinemaBot has reached its current user limit.</b>\n\n" +
                      "The maximum number of bot users is " +
                      maximumBotUsers.ToString(
                          "N0",
                          CultureInfo.InvariantCulture) +
                      ".\n\n" +
                      "Please try again later.";

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                privateChatId,
                message,
                new
                {
                    inline_keyboard =
                        Array.Empty<object[]>()
                },
                stoppingToken);
        }

        private async Task<TelegramMemberDisplayInfo?> GetTelegramMemberDisplayInfoAsync(
            string botToken,
            string channelUsername,
            long userId,
            CancellationToken stoppingToken)
        {
            string normalizedChannel =
                channelUsername.StartsWith("@", StringComparison.Ordinal)
                    ? channelUsername
                    : "@" + channelUsername;

            if (string.IsNullOrWhiteSpace(channelUsername))
                return null;

            string url =
                "https://api.telegram.org/bot" +
                botToken +
                "/getChatMember?chat_id=" +
                Uri.EscapeDataString(normalizedChannel) +
                "&user_id=" +
                userId.ToString(CultureInfo.InvariantCulture);

            try
            {
                using HttpResponseMessage response =
                    await _telegramHttpClient.GetAsync(url, stoppingToken);

                string body =
                    await response.Content.ReadAsStringAsync(stoppingToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                using JsonDocument document = JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty("ok", out JsonElement ok) ||
                    !ok.GetBoolean() ||
                    !document.RootElement.TryGetProperty("result", out JsonElement result))
                    return null;

                string status = result.TryGetProperty("status", out JsonElement statusElement)
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

                bool currentMember =
                    string.Equals(status, "member", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "administrator", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "creator", StringComparison.OrdinalIgnoreCase);

                if (!currentMember)
                    return new TelegramMemberDisplayInfo { IsCurrentMember = false };

                if (!result.TryGetProperty("user", out JsonElement user))
                    return null;

                return new TelegramMemberDisplayInfo
                {
                    IsCurrentMember = true,
                    IsBot = user.TryGetProperty("is_bot", out JsonElement isBot) && isBot.GetBoolean(),
                    FirstName = user.TryGetProperty("first_name", out JsonElement firstName)
                        ? firstName.GetString() ?? string.Empty
                        : string.Empty,
                    Username = user.TryGetProperty("username", out JsonElement username)
                        ? username.GetString() ?? string.Empty
                        : string.Empty
                };
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Telegram member details for user {UserId}.", userId);
                return null;
            }
        }

        private async Task<bool> CheckChannelMembershipAsync(
            string botToken,
            string channelUsername,
            long userId,
            CancellationToken stoppingToken)
        {
            string normalizedChannel =
                channelUsername.StartsWith("@", StringComparison.Ordinal)
                    ? channelUsername
                    : "@" + channelUsername;

            string url =
                "https://api.telegram.org/bot" +
                botToken +
                "/getChatMember?chat_id=" +
                Uri.EscapeDataString(normalizedChannel) +
                "&user_id=" +
                userId.ToString(CultureInfo.InvariantCulture);

            try
            {
                using HttpResponseMessage response =
                    await _telegramHttpClient.GetAsync(
                        url,
                        stoppingToken);

                string body =
                    await response.Content.ReadAsStringAsync(
                        stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Telegram getChatMember failed. HTTP {StatusCode}: {Response}",
                        (int)response.StatusCode,
                        LimitText(body, 500));

                    return false;
                }

                using JsonDocument document =
                    JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty(
                        "ok",
                        out JsonElement okElement) ||
                    !okElement.GetBoolean())
                {
                    return false;
                }

                if (!document.RootElement.TryGetProperty(
                        "result",
                        out JsonElement result) ||
                    !result.TryGetProperty(
                        "status",
                        out JsonElement statusElement))
                {
                    return false;
                }

                string status =
                    statusElement.GetString() ?? string.Empty;

                return
                    string.Equals(status, "member",
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    string.Equals(status, "administrator",
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    string.Equals(status, "creator",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not verify Channel membership for user {UserId}.",
                    userId);

                return false;
            }
        }

        private async Task SendSubscriptionRequiredMessageAsync(
            string botToken,
            long privateChatId,
            UserPreference preference,
            CancellationToken stoppingToken,
            bool configurationError = false)
        {
            string channelUsername =
                (_configuration["Telegram:ChannelUsername"] ??
                 string.Empty).Trim();

            string subscribeUrl =
                "https://t.me/" +
                channelUsername.TrimStart('@');

            string message =
                configurationError
                    ? preference.Language == "ar"
                        ? "⚠️ إعداد قناة CinemaBot غير مكتمل.\n\n" +
                          "يرجى ضبط Telegram:ChannelUsername في appsettings.json."
                        : "⚠️ CinemaBot channel configuration is incomplete.\n\n" +
                          "Set Telegram:ChannelUsername in appsettings.json."
                    : preference.Language == "ar"
                        ? "🎬 <b>أهلاً بك في CinemaBot</b>\n\n" +
                          "لاستخدام البوت، يجب الاشتراك في القناة الرسمية أولاً.\n\n" +
                          "بعد الاشتراك اضغط <b>تم الاشتراك</b> وسيتم فتح البوت لك."
                        : "🎬 <b>Welcome to CinemaBot</b>\n\n" +
                          "You need to subscribe to our official channel first.\n\n" +
                          "After subscribing, press <b>I Joined</b> to continue.";

            object keyboard =
                new
                {
                    inline_keyboard =
                        new object[][]
                        {
                            new object[]
                            {
                                new
                                {
                                    text =
                                        preference.Language == "ar"
                                            ? "📢 اشترك في القناة"
                                            : "📢 Subscribe to Channel",
                                    url = subscribeUrl
                                }
                            },
                            new object[]
                            {
                                new
                                {
                                    text =
                                        preference.Language == "ar"
                                            ? "✅ تم الاشتراك"
                                            : "✅ I Joined — Check",
                                    callback_data = "subscription:check"
                                }
                            }
                        }
                };

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                privateChatId,
                message,
                keyboard,
                stoppingToken);
        }

        private void RegisterBotSubscriber(long userId)
        {
            bool added;

            lock (_botSubscriberLock)
            {
                added = _knownBotSubscriberIds.Add(userId);
            }

            if (added)
            {
                SaveBotSubscriberIds();
            }
        }

        private void LoadBotSubscriberIds()
        {
            try
            {
                if (!TryLoadPersistentJson(
                        Path.GetFileName(BotSubscribersFilePath),
                        BotSubscribersFilePath,
                        out string json))
                {
                    return;
                }

                long[] ids =
                    JsonSerializer.Deserialize<long[]>(json)
                    ?? Array.Empty<long>();

                lock (_botSubscriberLock)
                {
                    _knownBotSubscriberIds.Clear();

                    foreach (long id in ids)
                    {
                        if (id != 0)
                        {
                            _knownBotSubscriberIds.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load the CinemaBot subscriber registry.");
            }
        }

        private void SaveBotSubscriberIds()
        {
            try
            {
                long[] ids;

                lock (_botSubscriberLock)
                {
                    ids =
                        new List<long>(
                            _knownBotSubscriberIds).ToArray();
                }

                string json =
                    JsonSerializer.Serialize(
                        ids,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                SavePersistentJson(
                    Path.GetFileName(BotSubscribersFilePath),
                    BotSubscribersFilePath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save the CinemaBot subscriber registry.");
            }
        }

        private int GetBotSubscriberCount()
        {
            lock (_botSubscriberLock)
            {
                return _knownBotSubscriberIds.Count;
            }
        }
    }
}
