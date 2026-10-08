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
        private static List<DayScheduleChange> FilterChangesForSubscriber(
                List<DayScheduleChange> allChanges,
                UserPreference subscriber)
        {
            var result =
                new List<DayScheduleChange>();

            foreach (DayScheduleChange day in
                     allChanges
                         .OrderBy(item =>
                             item.Schedule.CinemaName,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.Schedule.Date))
            {
                ScheduleChange[] filtered =
                    day.Changes
                        .Where(change =>
                            MatchesSubscriberScope(
                                subscriber,
                                day.Schedule,
                                change))
                        .Where(change =>
                            change.Type !=
                            ScheduleChangeType.SoldOut ||
                            subscriber.NotifySoldOut)
                        .Where(change =>
                            change.Type !=
                            ScheduleChangeType.AvailableAgain ||
                            subscriber.NotifyBookingOpenAgain)
                        .Where(change =>
                            change.Type !=
                            ScheduleChangeType.NewShowtime ||
                            subscriber.NotifyNewShowtime)
                        .Where(change =>
                            change.Type !=
                            ScheduleChangeType.ShowtimeRemoved ||
                            subscriber.NotifyShowtimeRemoved)
                        .Where(change =>
                            change.Type !=
                            ScheduleChangeType.HallRemoved ||
                            subscriber.NotifyHallRemoved)
                        .ToArray();

                if (filtered.Length == 0)
                {
                    continue;
                }

                result.Add(
                    DayScheduleChange.Changed(
                        day.Schedule,
                        filtered));
            }

            return result;
        }

        private static bool MatchesSubscriberScope(
                UserPreference subscriber,
                DaySchedule schedule,
                ScheduleChange change)
        {
            if (subscriber.SelectedCinemaSlugs.Contains(
                    schedule.CinemaSlug))
            {
                return true;
            }

            string dateKey =
                schedule.Date.ToString(
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture);

            return subscriber.AlertScopes.Any(scope =>
                string.Equals(
                    scope.CinemaSlug,
                    schedule.CinemaSlug,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    scope.DateKey,
                    dateKey,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(
                     scope.MovieTitle) ||
                 AreMovieTitlesEquivalent(
                     scope.MovieTitle,
                     change.MovieTitle)));
        }

        private static bool AreMovieTitlesEquivalent(
                string first,
                string second)
        {
            return string.Equals(
                NormalizeMovieTitleKey(
                    first),
                NormalizeMovieTitleKey(
                    second),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMovieTitleKey(
                string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder();

            foreach (char character in
                     value.Normalize())
            {
                if (char.IsLetterOrDigit(
                        character))
                {
                    builder.Append(
                        char.ToLowerInvariant(
                            character));
                }
            }

            return builder.ToString();
        }

        private UserPreference GetOrCreatePreference(
                long userId,
                long privateChatId,
                string firstName)
        {
            lock (_preferencesLock)
            {
                if (!_userPreferences.TryGetValue(
                        userId,
                        out UserPreference? preference))
                {
                    preference =
                        new UserPreference
                        {
                            UserId =
                                userId,

                            PrivateChatId =
                                privateChatId,

                            FirstName =
                                firstName,

                            Provider =
                                "vox",

                            Language =
                                "en",

                            NotifySoldOut =
                                true,

                            NotifyBookingOpenAgain =
                                true,

                            NotifyNewShowtime =
                                true,

                            NotifyShowtimeRemoved =
                                true,

                            NotifyHallRemoved =
                                false,

                            SelectedCinemaSlugs =
                                new HashSet<string>(
                                    StringComparer.OrdinalIgnoreCase),

                            AlertScopes =
                                new List<AlertScopeRule>()
                        };

                    _userPreferences[userId] =
                        preference;
                }
                else
                {
                    preference.PrivateChatId =
                        privateChatId;

                    if (!string.IsNullOrWhiteSpace(
                            firstName))
                    {
                        preference.FirstName =
                            firstName;
                    }
                }

                return preference;
            }
        }

        private static void ToggleCinemaSelection(
                UserPreference preference,
                string slug)
        {
            if (preference.SelectedCinemaSlugs.Contains(
                    slug))
            {
                preference.SelectedCinemaSlugs.Remove(
                    slug);
            }
            else
            {
                preference.SelectedCinemaSlugs.Add(
                    slug);

                preference.AlertScopes.RemoveAll(scope =>
                    string.Equals(
                        scope.CinemaSlug,
                        slug,
                        StringComparison.OrdinalIgnoreCase));
            }

            preference.IsCompleted =
                preference.SelectedCinemaSlugs.Count > 0 ||
                preference.AlertScopes.Count > 0;
        }

        private static void ToggleBroadCinemaScope(
                UserPreference preference,
                string cinemaSlug)
        {
            if (preference.SelectedCinemaSlugs.Contains(
                    cinemaSlug))
            {
                preference.SelectedCinemaSlugs.Remove(
                    cinemaSlug);
            }
            else
            {
                preference.SelectedCinemaSlugs.Add(
                    cinemaSlug);

                preference.AlertScopes.RemoveAll(scope =>
                    string.Equals(
                        scope.CinemaSlug,
                        cinemaSlug,
                        StringComparison.OrdinalIgnoreCase));
            }

            preference.IsCompleted =
                preference.SelectedCinemaSlugs.Count > 0 ||
                preference.AlertScopes.Count > 0;
        }

        private static void ToggleDayScope(
                UserPreference preference,
                string cinemaSlug,
                string dateKey)
        {
            preference.SelectedCinemaSlugs.Remove(
                cinemaSlug);

            AlertScopeRule? existing =
                preference.AlertScopes.FirstOrDefault(scope =>
                    string.Equals(
                        scope.CinemaSlug,
                        cinemaSlug,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        scope.DateKey,
                        dateKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(
                        scope.MovieTitle));

            if (existing is not null)
            {
                preference.AlertScopes.Remove(
                    existing);
            }
            else
            {
                preference.AlertScopes.RemoveAll(scope =>
                    string.Equals(
                        scope.CinemaSlug,
                        cinemaSlug,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        scope.DateKey,
                        dateKey,
                        StringComparison.OrdinalIgnoreCase));

                preference.AlertScopes.Add(
                    new AlertScopeRule
                    {
                        CinemaSlug =
                            cinemaSlug,

                        DateKey =
                            dateKey,

                        MovieTitle =
                            string.Empty
                    });
            }

            preference.IsCompleted =
                preference.SelectedCinemaSlugs.Count > 0 ||
                preference.AlertScopes.Count > 0;
        }

        private static void ToggleMovieScope(
                UserPreference preference,
                string cinemaSlug,
                string dateKey,
                string movieTitle)
        {
            preference.SelectedCinemaSlugs.Remove(
                cinemaSlug);

            preference.AlertScopes.RemoveAll(scope =>
                string.Equals(
                    scope.CinemaSlug,
                    cinemaSlug,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    scope.DateKey,
                    dateKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(
                    scope.MovieTitle));

            AlertScopeRule? existing =
                preference.AlertScopes.FirstOrDefault(scope =>
                    string.Equals(
                        scope.CinemaSlug,
                        cinemaSlug,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        scope.DateKey,
                        dateKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    AreMovieTitlesEquivalent(
                        scope.MovieTitle,
                        movieTitle));

            if (existing is not null)
            {
                preference.AlertScopes.Remove(
                    existing);
            }
            else
            {
                preference.AlertScopes.Add(
                    new AlertScopeRule
                    {
                        CinemaSlug =
                            cinemaSlug,

                        DateKey =
                            dateKey,

                        MovieTitle =
                            movieTitle
                    });
            }

            preference.IsCompleted =
                preference.SelectedCinemaSlugs.Count > 0 ||
                preference.AlertScopes.Count > 0;
        }

        private static bool IsDayScopeSelected(
                UserPreference preference,
                string cinemaSlug,
                string dateKey)
        {
            return preference.AlertScopes.Any(scope =>
                string.Equals(
                    scope.CinemaSlug,
                    cinemaSlug,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    scope.DateKey,
                    dateKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(
                    scope.MovieTitle));
        }

        private static bool IsMovieScopeSelected(
                UserPreference preference,
                string cinemaSlug,
                string dateKey,
                string movieTitle)
        {
            return preference.AlertScopes.Any(scope =>
                string.Equals(
                    scope.CinemaSlug,
                    cinemaSlug,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    scope.DateKey,
                    dateKey,
                    StringComparison.OrdinalIgnoreCase) &&
                AreMovieTitlesEquivalent(
                    scope.MovieTitle,
                    movieTitle));
        }

        private string GetEnglishMovieTitleForScope(
                string cinemaSlug,
                string dateKey,
                int movieIndex,
                string fallback)
        {
            DaySchedule? englishSchedule =
                FindDaySchedule(
                    cinemaSlug,
                    dateKey);

            return englishSchedule is not null &&
                   movieIndex >= 0 &&
                   movieIndex <
                   englishSchedule.Movies.Length
                ? englishSchedule.Movies[movieIndex].Title
                : fallback;
        }

        private async Task SendProviderSelectionAsync(
                string botToken,
                UserPreference preference,
                CancellationToken stoppingToken)
        {
            string text =
                "🎬 <b>Choose your cinema network</b>\n" +
                "اختر شبكة السينما\n\n" +
                "Select a network to continue.\n" +
                "اختر الشبكة للمتابعة.";

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                text,
                BuildProviderKeyboard(),
                stoppingToken);
        }

        private async Task SendButtonsOnlyReminderAsync(
                string botToken,
                UserPreference preference,
                CancellationToken stoppingToken)
        {
            string text =
                preference.Language == "ar"
                    ? "🔘 لا تحتاج إلى كتابة أي رسالة. استخدم الأزرار لتحديد أو تعديل تنبيهاتك."
                    : "🔘 You do not need to type any message. Use the buttons to configure or edit your alerts.";

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                text,
                BuildPrivateAlertKeyboard(
                    preference),
                stoppingToken);
        }

        private async Task EditProviderSelectionAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            string text =
                "🎬 <b>Choose your cinema network</b>\n" +
                "اختر شبكة السينما\n\n" +
                "Select a network to continue.\n" +
                "اختر الشبكة للمتابعة.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                text,
                BuildProviderKeyboard(),
                stoppingToken);
        }

        private async Task EditLanguageSelectionAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            string text =
                "🌐 <b>Choose notification language</b>\n" +
                "اختر لغة أسماء الأفلام والقاعات والمواعيد";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                text,
                BuildLanguageKeyboard(),
                stoppingToken);
        }

        private async Task EditCinemaSelectionAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            string text =
                preference.Language == "ar"
                    ? "🏢 <b>اختر فرعًا أو أكثر</b>\n" +
                      "اضغط على السينما لتحديدها أو إلغاء تحديدها، ثم اضغط حفظ."
                    : "🏢 <b>Choose one or more cinemas</b>\n" +
                      "Tap a cinema to select or remove it, then press Save.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                text,
                BuildCinemaKeyboard(
                    preference),
                stoppingToken);
        }

        private async Task EditAlertTypesAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string message =
                arabic
                    ? "🔔 <b>أنواع التنبيهات</b>\n\n" +
                      "كل اختيار مستقل ويمكن تشغيل اختيار واحد أو أكثر:\n\n" +
                      "🔴 <b>الحجز اكتمل</b>\n" +
                      "🟢 <b>الحجز متاح مرة أخرى</b>\n" +
                      "🕒 <b>موعد جديد</b> — يُرسل فقط إذا كان الموعد متاحاً وليس Sold Out.\n" +
                      "➖ <b>تم حذف موعد</b> — لا يُرسل إذا كان الموعد المحذوف Sold Out.\n" +
                      "🚪 <b>تم حذف قاعة</b> — يُرسل فقط لو كان داخل القاعة موعد لم يبدأ بعد."
                    : "🔔 <b>Alert types</b>\n\n" +
                      "Every option is independent:\n\n" +
                      "🔴 <b>Sold Out</b>\n" +
                      "🟢 <b>Booking Open Again</b>\n" +
                      "🕒 <b>New Time</b> — sent only when the new time is available.\n" +
                      "➖ <b>Time Removed</b> — not sent when that time was already sold out.\n" +
                      "🚪 <b>Hall Removed</b> — sent only while that hall still had a future session.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                message,
                BuildAlertTypesKeyboard(
                    preference),
                stoppingToken);
        }

        private async Task EditAlertScopesAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string summary =
                BuildAlertScopeSummary(
                    preference,
                    arabic);

            string message =
                arabic
                    ? "🎯 <b>السينمات والأيام والأفلام المتابعة</b>\n\n" +
                      summary +
                      "\n\nاستخدم تصفح السينمات لإضافة سينما كاملة أو يوم معين أو فيلم معين."
                    : "🎯 <b>Followed cinemas, dates, and movies</b>\n\n" +
                      summary +
                      "\n\nUse Browse Cinemas to add an entire cinema, one date, or one movie.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                message,
                BuildAlertScopesKeyboard(
                    preference),
                stoppingToken);
        }

        private async Task EditSavedPreferenceAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string scopeSummary =
                BuildAlertScopeSummary(
                    preference,
                    arabic);

            string message =
                arabic
                    ? "✅ <b>تم تفعيل تنبيهاتك الشخصية</b>\n\n" +
                      "🎯 <b>نطاق التنبيه</b>\n" +
                      scopeSummary +
                      "\n\n🔔 <b>أنواع التنبيهات</b>\n" +
                      (preference.NotifySoldOut
                          ? "✅ الحجز اكتمل"
                          : "❌ الحجز اكتمل") +
                      "\n" +
                      (preference.NotifyBookingOpenAgain
                          ? "✅ الحجز متاح مرة أخرى"
                          : "❌ الحجز متاح مرة أخرى") +
                      "\n" +
                      (preference.NotifyNewShowtime
                          ? "✅ موعد جديد"
                          : "❌ موعد جديد") +
                      "\n" +
                      (preference.NotifyShowtimeRemoved
                          ? "✅ حذف موعد"
                          : "❌ حذف موعد") +
                      "\n" +
                      (preference.NotifyHallRemoved
                          ? "✅ حذف قاعة"
                          : "❌ حذف قاعة")
                    : "✅ <b>Your personal alerts are active</b>\n\n" +
                      "🎯 <b>Alert scope</b>\n" +
                      scopeSummary +
                      "\n\n🔔 <b>Alert types</b>\n" +
                      (preference.NotifySoldOut
                          ? "✅ Sold Out"
                          : "❌ Sold Out") +
                      "\n" +
                      (preference.NotifyBookingOpenAgain
                          ? "✅ Booking Open Again"
                          : "❌ Booking Open Again") +
                      "\n" +
                      (preference.NotifyNewShowtime
                          ? "✅ New Time"
                          : "❌ New Time") +
                      "\n" +
                      (preference.NotifyShowtimeRemoved
                          ? "✅ Time Removed"
                          : "❌ Time Removed") +
                      "\n" +
                      (preference.NotifyHallRemoved
                          ? "✅ Hall Removed"
                          : "❌ Hall Removed");

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                message,
                BuildPrivateAlertKeyboard(
                    preference),
                stoppingToken);
        }

        private string BuildAlertScopeSummary(
                UserPreference preference,
                bool arabic)
        {
            var lines =
                new List<string>();

            foreach (string cinemaSlug in
                     preference.SelectedCinemaSlugs
                         .OrderBy(slug =>
                             slug,
                             StringComparer.OrdinalIgnoreCase))
            {
                CinemaOption? cinema =
                    _lastDiscoveredCinemas.FirstOrDefault(item =>
                        string.Equals(
                            item.Slug,
                            cinemaSlug,
                            StringComparison.OrdinalIgnoreCase));

                string cinemaName =
                    cinema?.Name ??
                    cinemaSlug;

                if (arabic)
                {
                    cinemaName =
                        GetArabicCinemaDisplayName(
                            cinemaSlug,
                            cinemaName,
                            cinemaName);
                }

                lines.Add(
                    (arabic
                        ? "• كل الأيام — "
                        : "• All dates — ") +
                    (arabic
                        ? FormatArabicBidiText(
                            cinemaName)
                        : EscapeTelegramHtml(
                            cinemaName)));
            }

            foreach (AlertScopeRule scope in
                     preference.AlertScopes
                         .OrderBy(item =>
                             item.CinemaSlug,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.DateKey,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.MovieTitle,
                             StringComparer.OrdinalIgnoreCase))
            {
                CinemaOption? cinema =
                    _lastDiscoveredCinemas.FirstOrDefault(item =>
                        string.Equals(
                            item.Slug,
                            scope.CinemaSlug,
                            StringComparison.OrdinalIgnoreCase));

                string cinemaName =
                    cinema?.Name ??
                    scope.CinemaSlug;

                if (arabic)
                {
                    cinemaName =
                        GetArabicCinemaDisplayName(
                            scope.CinemaSlug,
                            cinemaName,
                            cinemaName);
                }

                string dateText =
                    DateTime.TryParseExact(
                        scope.DateKey,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime parsedDate)
                        ? arabic
                            ? FormatArabicDate(
                                parsedDate)
                            : parsedDate.ToString(
                                "dddd - d MMM yyyy",
                                CultureInfo.InvariantCulture)
                        : scope.DateKey;

                string line =
                    "• " +
                    (arabic
                        ? FormatArabicBidiText(
                            cinemaName)
                        : EscapeTelegramHtml(
                            cinemaName)) +
                    " — " +
                    EscapeTelegramHtml(
                        dateText);

                if (!string.IsNullOrWhiteSpace(
                        scope.MovieTitle))
                {
                    line +=
                        " — 🎬 " +
                        (arabic
                            ? FormatArabicBidiText(
                                scope.MovieTitle)
                            : EscapeTelegramHtml(
                                scope.MovieTitle));
                }

                lines.Add(
                    line);
            }

            return lines.Count == 0
                ? arabic
                    ? "لا توجد نطاقات محددة حتى الآن."
                    : "No alert scope is selected yet."
                : string.Join(
                    "\n",
                    lines);
        }

        private static object BuildProviderKeyboard()
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
                                    "🎬 VOX Cinemas",

                                callback_data =
                                    "setup:provider:vox"
                            }
                        }
                    }
            };
        }

        private static object BuildLanguageKeyboard()
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
                                    "🇬🇧 English",

                                callback_data =
                                    "setup:lang:en"
                            },

                            new
                            {
                                text =
                                    "🇪🇬 العربية",

                                callback_data =
                                    "setup:lang:ar"
                            }
                        }
                    }
            };
        }

        private object BuildCinemaKeyboard(
                UserPreference preference)
        {
            var rows =
                new List<object>();

            foreach (CinemaOption cinema in
                     _lastDiscoveredCinemas)
            {
                bool selected =
                    preference.SelectedCinemaSlugs.Contains(
                        cinema.Slug);

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                (selected
                                    ? "✅ "
                                    : "▫️ ") +
                                cinema.Name,

                            callback_data =
                                "setup:cinema:" +
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
                            preference.Language == "ar"
                                ? "✅ اختيار الكل"
                                : "✅ Select All",

                        callback_data =
                            "setup:all"
                    },

                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "🧹 مسح"
                                : "🧹 Clear",

                        callback_data =
                            "setup:clear"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "💾 حفظ التنبيهات"
                                : "💾 Save Alerts",

                        callback_data =
                            "setup:save"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "🔔 أنواع التنبيهات"
                                : "🔔 Alert Types",

                        callback_data =
                            "setup:alerts"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "🎯 اختيار يوم أو فيلم معين"
                                : "🎯 Choose Date or Movie",

                        callback_data =
                            "browse:home"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "🌐 تغيير اللغة"
                                : "🌐 Change Language",

                        callback_data =
                            "setup:language"
                    },

                    new
                    {
                        text =
                            preference.Language == "ar"
                                ? "🎬 شبكة السينما"
                                : "🎬 Cinema Network",

                        callback_data =
                            "setup:provider"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private static object BuildPrivateAlertKeyboard(
                UserPreference preference)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

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
                                    arabic
                                        ? "🎬 تصفح السينمات"
                                        : "🎬 Browse Cinemas",

                                callback_data =
                                    "browse:home"
                            },

                            new
                            {
                                text =
                                    arabic
                                        ? "🎯 نطاقات التنبيه"
                                        : "🎯 Alert Scopes",

                                callback_data =
                                    "setup:scopes"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "✏️ تنبيه كل أيام السينما"
                                        : "✏️ Full Cinema Alerts",

                                callback_data =
                                    "setup:edit"
                            },

                            new
                            {
                                text =
                                    arabic
                                        ? "🔔 أنواع التنبيهات"
                                        : "🔔 Alert Types",

                                callback_data =
                                    "setup:alerts"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🌐 تغيير اللغة"
                                        : "🌐 Language",

                                callback_data =
                                    "setup:language"
                            }
                        }
                    }
            };
        }

        private static object BuildAlertTypesKeyboard(
                UserPreference preference)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

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
                                    (preference.NotifySoldOut
                                        ? "✅ "
                                        : "▫️ ") +
                                    (arabic
                                        ? "الحجز اكتمل"
                                        : "Sold Out"),

                                callback_data =
                                    "setup:alert:soldout"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    (preference.NotifyBookingOpenAgain
                                        ? "✅ "
                                        : "▫️ ") +
                                    (arabic
                                        ? "الحجز متاح مرة أخرى"
                                        : "Booking Open Again"),

                                callback_data =
                                    "setup:alert:reopen"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    (preference.NotifyNewShowtime
                                        ? "✅ "
                                        : "▫️ ") +
                                    (arabic
                                        ? "موعد جديد"
                                        : "New Time"),

                                callback_data =
                                    "setup:alert:newtime"
                            },

                            new
                            {
                                text =
                                    (preference.NotifyShowtimeRemoved
                                        ? "✅ "
                                        : "▫️ ") +
                                    (arabic
                                        ? "حذف موعد"
                                        : "Time Removed"),

                                callback_data =
                                    "setup:alert:removedtime"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    (preference.NotifyHallRemoved
                                        ? "✅ "
                                        : "▫️ ") +
                                    (arabic
                                        ? "حذف قاعة"
                                        : "Hall Removed"),

                                callback_data =
                                    "setup:alert:hallremoved"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "⬅️ الرجوع"
                                        : "⬅️ Back",

                                callback_data =
                                    "setup:edit"
                            }
                        }
                    }
            };
        }

        private static object BuildAlertScopesKeyboard(
                UserPreference preference)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

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
                                    arabic
                                        ? "🎬 اختر سينما أو يوم أو فيلم"
                                        : "🎬 Add Cinema, Date, or Movie",

                                callback_data =
                                    "browse:home"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🧹 مسح كل النطاقات"
                                        : "🧹 Clear All Scopes",

                                callback_data =
                                    "scope:clear"
                            }
                        },

                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "⬅️ الإعدادات"
                                        : "⬅️ Settings",

                                callback_data =
                                    "setup:edit"
                            }
                        }
                    }
            };
        }

        private void LoadUserPreferences()
        {
            try
            {
                if (!TryLoadPersistentJson(
                        Path.GetFileName(_preferencesFilePath),
                        _preferencesFilePath,
                        out string json))
                {
                    return;
                }

                List<UserPreferenceStorage>? stored =
                    JsonSerializer.Deserialize<
                        List<UserPreferenceStorage>>(
                        json);

                if (stored is null)
                {
                    return;
                }

                lock (_preferencesLock)
                {
                    _userPreferences =
                        stored.ToDictionary(
                            item =>
                                item.UserId,

                            item =>
                                new UserPreference
                                {
                                    UserId =
                                        item.UserId,

                                    PrivateChatId =
                                        item.PrivateChatId,

                                    FirstName =
                                        item.FirstName ??
                                        string.Empty,

                                    Provider =
                                        item.Provider ??
                                        "vox",

                                    Language =
                                        item.Language ??
                                        "en",

                                    NotifySoldOut =
                                        item.NotifySoldOut ??
                                        true,

                                    NotifyBookingOpenAgain =
                                        item.NotifyBookingOpenAgain ??
                                        true,

                                    NotifyNewShowtime =
                                        item.NotifyNewShowtime ??
                                        true,

                                    NotifyShowtimeRemoved =
                                        item.NotifyShowtimeRemoved ??
                                        true,

                                    NotifyHallRemoved =
                                        item.NotifyHallRemoved ??
                                        false,

                                    IsCompleted =
                                        item.IsCompleted,

                                    SelectedCinemaSlugs =
                                        new HashSet<string>(
                                            item.SelectedCinemaSlugs ??
                                            new List<string>(),
                                            StringComparer.OrdinalIgnoreCase),

                                    AlertScopes =
                                        (item.AlertScopes ??
                                         new List<AlertScopeRuleStorage>())
                                            .Select(scope =>
                                                new AlertScopeRule
                                                {
                                                    CinemaSlug =
                                                        scope.CinemaSlug ??
                                                        string.Empty,

                                                    DateKey =
                                                        scope.DateKey ??
                                                        string.Empty,

                                                    MovieTitle =
                                                        scope.MovieTitle ??
                                                        string.Empty
                                                })
                                            .Where(scope =>
                                                !string.IsNullOrWhiteSpace(
                                                    scope.CinemaSlug))
                                            .ToList()
                                });
                }

                _logger.LogInformation(
                    "Loaded personalized Telegram preferences for {UserCount} user(s).",
                    _userPreferences.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load Telegram user preferences.");
            }
        }

        private void SaveUserPreferences()
        {
            try
            {
                List<UserPreferenceStorage> storage;

                lock (_preferencesLock)
                {
                    storage =
                        _userPreferences.Values
                            .Select(preference =>
                                new UserPreferenceStorage
                                {
                                    UserId =
                                        preference.UserId,

                                    PrivateChatId =
                                        preference.PrivateChatId,

                                    FirstName =
                                        preference.FirstName,

                                    Provider =
                                        preference.Provider,

                                    Language =
                                        preference.Language,

                                    NotifySoldOut =
                                        preference.NotifySoldOut,

                                    NotifyBookingOpenAgain =
                                        preference.NotifyBookingOpenAgain,

                                    NotifyNewShowtime =
                                        preference.NotifyNewShowtime,

                                    NotifyShowtimeRemoved =
                                        preference.NotifyShowtimeRemoved,

                                    NotifyHallRemoved =
                                        preference.NotifyHallRemoved,

                                    IsCompleted =
                                        preference.IsCompleted,

                                    SelectedCinemaSlugs =
                                        preference.SelectedCinemaSlugs
                                            .OrderBy(slug =>
                                                slug,
                                                StringComparer.OrdinalIgnoreCase)
                                            .ToList(),

                                    AlertScopes =
                                        preference.AlertScopes
                                            .Select(scope =>
                                                new AlertScopeRuleStorage
                                                {
                                                    CinemaSlug =
                                                        scope.CinemaSlug,

                                                    DateKey =
                                                        scope.DateKey,

                                                    MovieTitle =
                                                        scope.MovieTitle
                                                })
                                            .ToList()
                                })
                            .ToList();
                }

                string? directory =
                    Path.GetDirectoryName(
                        _preferencesFilePath);

                if (!string.IsNullOrWhiteSpace(
                        directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                string json =
                    JsonSerializer.Serialize(
                        storage,
                        new JsonSerializerOptions
                        {
                            WriteIndented =
                                true
                        });

                SavePersistentJson(
                    Path.GetFileName(_preferencesFilePath),
                    _preferencesFilePath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save Telegram user preferences.");
            }
        }
    }
}