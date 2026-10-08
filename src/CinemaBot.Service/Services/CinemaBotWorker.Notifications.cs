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
        private void RemoveKnownBotSubscriber(
                long userId)
        {
            bool removed;

            lock (_botSubscriberLock)
            {
                removed =
                    _knownBotSubscriberIds.Remove(
                        userId);
            }

            if (removed)
            {
                SaveBotSubscriberIds();
            }
        }

        private async Task<bool> SendScheduleNotificationsAsync(
                List<DayScheduleChange> days,
                bool isInitialSnapshot,
                string botToken,
                string chatId,
                CancellationToken stoppingToken)
        {
            // The first scan is represented by the single live dashboard message
            // in the group. Do not send a large first-snapshot message.
            if (isInitialSnapshot)
            {
                return true;
            }

            int maximumLength =
                ReadIntegerSetting(
                    "Telegram:DigestMaximumLength",
                    defaultValue: 3500,
                    minimumValue: 2200,
                    maximumValue: 3900);

            return await SendPersonalizedChangeDigestsAsync(
                days,
                botToken,
                maximumLength,
                stoppingToken);
        }

        private async Task<bool> SendPersonalizedChangeDigestsAsync(
                List<DayScheduleChange> allChanges,
                string botToken,
                int maximumLength,
                CancellationToken stoppingToken)
        {
            UserPreference[] subscribers;

            lock (_preferencesLock)
            {
                subscribers =
                    _userPreferences.Values
                        .Where(preference =>
                            preference.IsCompleted &&
                            preference.SelectedCinemaSlugs.Count > 0)
                        .Select(preference =>
                            preference.Clone())
                        .ToArray();
            }

            if (subscribers.Length == 0)
            {
                _logger.LogInformation(
                    "VOX changes were detected, but no Telegram users currently " +
                    "have completed cinema subscriptions.");

                return true;
            }

            var arabicScheduleCache =
                new Dictionary<string, DaySchedule>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (UserPreference subscriber in
                     subscribers)
            {
                if (ReadBooleanSetting(
                        "Telegram:RequireChannelSubscription",
                        defaultValue: false))
                {
                    bool stillSubscribed =
                        await CheckChannelMembershipAsync(
                            botToken,
                            (_configuration["Telegram:ChannelUsername"] ??
                             string.Empty).Trim(),
                            subscriber.UserId,
                            stoppingToken);

                    if (!stillSubscribed)
                    {
                        RemoveKnownBotSubscriber(
                            subscriber.UserId);

                        _logger.LogInformation(
                            "Skipping Telegram alert for user {UserId}: user is no longer subscribed to the required channel.",
                            subscriber.UserId);

                        continue;
                    }
                }

                List<DayScheduleChange> selectedChanges =
                    FilterChangesForSubscriber(
                        allChanges,
                        subscriber);

                if (selectedChanges.Count == 0)
                {
                    continue;
                }

                selectedChanges =
                    await ResolveChangeBookingUrlsAsync(
                        selectedChanges,
                        stoppingToken);

                bool arabic =
                    string.Equals(
                        subscriber.Language,
                        "ar",
                        StringComparison.OrdinalIgnoreCase);

                Dictionary<string, DaySchedule>? localizedSchedules =
                    null;

                if (arabic)
                {
                    localizedSchedules =
                        new Dictionary<string, DaySchedule>(
                            StringComparer.OrdinalIgnoreCase);

                    foreach (DayScheduleChange day in
                             selectedChanges)
                    {
                        string cacheKey =
                            FormatStateKey(
                                day.Schedule.CinemaSlug,
                                day.Schedule.Date);

                        if (!arabicScheduleCache.TryGetValue(
                                cacheKey,
                                out DaySchedule? arabicSchedule))
                        {
                            arabicSchedule =
                                await TryLoadArabicScheduleAsync(
                                    day.Schedule,
                                    stoppingToken) ??
                                day.Schedule;

                            arabicScheduleCache[cacheKey] =
                                arabicSchedule;
                        }

                        localizedSchedules[cacheKey] =
                            arabicSchedule;
                    }
                }

                List<string> digestMessages =
                    BuildCompactChangeDigestMessages(
                        selectedChanges,
                        arabic,
                        localizedSchedules,
                        maximumLength);

                object keyboard =
                    BuildCompactAlertKeyboard(
                        selectedChanges,
                        subscriber.Language);

                _logger.LogInformation(
                    "Sending one consolidated VOX digest containing {ChangeCount} " +
                    "change(s) across {CinemaCount} cinema(s) to Telegram user {UserId}. " +
                    "Digest parts: {MessageCount}.",
                    selectedChanges.Sum(day =>
                        day.Changes.Length),
                    selectedChanges
                        .Select(day =>
                            day.Schedule.CinemaSlug)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    subscriber.UserId,
                    digestMessages.Count);

                foreach (string message in
                         digestMessages)
                {
                    QueueTelegramAlert(
                        botToken,
                        subscriber.PrivateChatId.ToString(
                            CultureInfo.InvariantCulture),
                        message,
                        keyboard);
                }
            }

            return true;
        }

        private static List<string> BuildInitialSnapshotDigestMessages(
                List<DayScheduleChange> days,
                int maximumLength)
        {
            List<DaySchedule> schedules =
                days
                    .Select(day =>
                        day.Schedule)
                    .Where(schedule =>
                        schedule.Movies.Length > 0)
                    .OrderBy(schedule =>
                        schedule.Date)
                    .ThenBy(schedule =>
                        schedule.CinemaName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var messages =
                new List<string>();

            foreach (IGrouping<DateTime, DaySchedule> dateGroup in
                     schedules.GroupBy(schedule =>
                         schedule.Date.Date))
            {
                DateTime date =
                    dateGroup.Key;

                int cinemaCount =
                    dateGroup
                        .Select(schedule =>
                            schedule.CinemaSlug)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .Count();

                int movieCount =
                    dateGroup.Sum(schedule =>
                        schedule.Movies.Length);

                string firstHeader =
                    "✨ <b>VOX CINEMA RADAR</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "📅 <b>" +
                    EscapeTelegramHtml(
                        date.ToString(
                            "dddd, dd MMMM yyyy",
                            CultureInfo.InvariantCulture)) +
                    "</b>\n" +
                    "🏢 " +
                    cinemaCount +
                    " cinemas  •  🎬 " +
                    movieCount +
                    " movies\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "Movie names are always visible. Tap an expandable section " +
                    "only when you need halls and showtimes.\n";

                string continuedHeader =
                    "✨ <b>VOX CINEMA RADAR</b>\n" +
                    "📅 <b>" +
                    EscapeTelegramHtml(
                        date.ToString(
                            "dd MMMM yyyy",
                            CultureInfo.InvariantCulture)) +
                    "</b>  •  <i>continued</i>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n";

                var current =
                    new StringBuilder(
                        firstHeader);

                foreach (DaySchedule schedule in
                         dateGroup.OrderBy(item =>
                             item.CinemaName,
                             StringComparer.OrdinalIgnoreCase))
                {
                    string cinemaHeader =
                        "\n🏢 <b>" +
                        EscapeTelegramHtml(
                            schedule.CinemaName) +
                        "</b>\n";

                    AppendDigestBlock(
                        messages,
                        current,
                        continuedHeader,
                        cinemaHeader,
                        maximumLength);

                    foreach (MovieSchedule movie in
                             schedule.Movies)
                    {
                        string movieCard =
                            BuildInitialExpandableMovieCard(
                                movie,
                                schedule.Url);

                        AppendDigestBlock(
                            messages,
                            current,
                            continuedHeader,
                            movieCard,
                            maximumLength);
                    }
                }

                if (current.Length > 0)
                {
                    messages.Add(
                        current.ToString());
                }
            }

            return messages;
        }

        private static string BuildInitialExpandableMovieCard(
                MovieSchedule movie,
                string scheduleUrl)
        {
            int showtimeCount =
                movie.Halls.Sum(hall =>
                    hall.Showtimes.Length);

            int availableCount =
                movie.Halls.Sum(hall =>
                    hall.Showtimes.Count(showtime =>
                        showtime.IsAvailable));

            int soldOutCount =
                showtimeCount -
                availableCount;

            var builder =
                new StringBuilder();

            builder.Append(
                "\n🎬 <b>" +
                EscapeTelegramHtml(
                    movie.Title) +
                "</b>\n");

            builder.Append(
                "   🏛 " +
                movie.Halls.Length +
                " halls  •  🕒 " +
                showtimeCount +
                " times");

            if (soldOutCount > 0)
            {
                builder.Append(
                    "  •  🔴 " +
                    soldOutCount +
                    " sold out");
            }

            builder.AppendLine();

            builder.Append(
                BuildExpandableMovieDetails(
                    movie,
                    scheduleUrl,
                    arabic:
                        false));

            return builder.ToString();
        }

        private static List<string> BuildChangeDigestMessages(
                List<DayScheduleChange> days,
                bool arabic,
                Dictionary<string, DaySchedule>? localizedSchedules,
                int maximumLength)
        {
            int changeCount =
                days.Sum(day =>
                    day.Changes.Length);

            int cinemaCount =
                days
                    .Select(day =>
                        day.Schedule.CinemaSlug)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Count();

            int dateCount =
                days
                    .Select(day =>
                        day.Schedule.Date.Date)
                    .Distinct()
                    .Count();

            string firstHeader =
                arabic
                    ? "🚨 <b>رادار ڤوكس — تغييرات جديدة</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "⚡ " +
                      changeCount +
                      " تغيير  •  🏢 " +
                      cinemaCount +
                      " سينما  •  📅 " +
                      dateCount +
                      " أيام\n" +
                      "🕒 " +
                      EscapeTelegramHtml(
                          DateTime.Now.ToString(
                              "dd/MM/yyyy hh:mm tt",
                              new CultureInfo(
                                  "ar-EG"))) +
                      "\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "اسم الفيلم والتغيير ظاهرين مباشرة. اضغط التفاصيل فقط " +
                      "لعرض القاعات والمواعيد.\n"
                    : "🚨 <b>VOX RADAR — NEW CHANGES</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "⚡ " +
                      changeCount +
                      " changes  •  🏢 " +
                      cinemaCount +
                      " cinemas  •  📅 " +
                      dateCount +
                      " days\n" +
                      "🕒 " +
                      EscapeTelegramHtml(
                          DateTime.Now.ToString(
                              "dd MMM yyyy, hh:mm tt",
                              CultureInfo.InvariantCulture)) +
                      "\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "Movie names and changes are shown immediately. Open details " +
                      "only for halls and showtimes.\n";

            string continuedHeader =
                arabic
                    ? "🚨 <b>رادار ڤوكس — تابع التغييرات</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n"
                    : "🚨 <b>VOX RADAR — CONTINUED</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n";

            var messages =
                new List<string>();

            var current =
                new StringBuilder(
                    firstHeader);

            foreach (DayScheduleChange day in
                     days
                         .OrderBy(item =>
                             item.Schedule.CinemaName,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.Schedule.Date))
            {
                DaySchedule originalSchedule =
                    day.Schedule;

                DaySchedule displaySchedule =
                    originalSchedule;

                if (localizedSchedules is not null)
                {
                    string localizedKey =
                        FormatStateKey(
                            originalSchedule.CinemaSlug,
                            originalSchedule.Date);

                    if (localizedSchedules.TryGetValue(
                            localizedKey,
                            out DaySchedule? localizedSchedule))
                    {
                        displaySchedule =
                            localizedSchedule;
                    }
                }

                string sectionHeader =
                    arabic
                        ? "\n━━━━━━━━━━━━━━━━━━━━\n" +
                          "🏢 <b>" +
                          EscapeTelegramHtml(
                              displaySchedule.CinemaName) +
                          "</b>\n" +
                          "📅 <b>" +
                          EscapeTelegramHtml(
                              displaySchedule.Date.ToString(
                                  "dddd، dd MMMM yyyy",
                                  new CultureInfo(
                                      "ar-EG"))) +
                          "</b>\n"
                        : "\n━━━━━━━━━━━━━━━━━━━━\n" +
                          "🏢 <b>" +
                          EscapeTelegramHtml(
                              displaySchedule.CinemaName) +
                          "</b>\n" +
                          "📅 <b>" +
                          EscapeTelegramHtml(
                              displaySchedule.Date.ToString(
                                  "dddd, dd MMMM yyyy",
                                  CultureInfo.InvariantCulture)) +
                          "</b>\n";

                AppendDigestBlock(
                    messages,
                    current,
                    continuedHeader,
                    sectionHeader,
                    maximumLength);

                foreach (IGrouping<string, ScheduleChange> movieChanges in
                         day.Changes.GroupBy(
                             change =>
                                 change.MovieTitle,
                             StringComparer.OrdinalIgnoreCase))
                {
                    string movieCard =
                        BuildExpandableMovieChangeCard(
                            movieChanges.ToArray(),
                            originalSchedule,
                            displaySchedule,
                            arabic);

                    AppendDigestBlock(
                        messages,
                        current,
                        continuedHeader,
                        movieCard,
                        maximumLength);
                }
            }

            string footer =
                arabic
                    ? "\n━━━━━━━━━━━━━━━━━━━━\n" +
                      "⏱ يتم جمع تغييرات كل فحص في رسالة منظمة واحدة.\n"
                    : "\n━━━━━━━━━━━━━━━━━━━━\n" +
                      "⏱ Changes from each scan are combined into one organized digest.\n";

            AppendDigestBlock(
                messages,
                current,
                continuedHeader,
                footer,
                maximumLength);

            if (current.Length > 0)
            {
                messages.Add(
                    current.ToString());
            }

            return messages;
        }

        private static string BuildExpandableMovieChangeCard(
                ScheduleChange[] changes,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule,
                bool arabic)
        {
            string originalMovieTitle =
                changes[0].MovieTitle;

            string displayMovieTitle =
                GetLocalizedMovieTitle(
                    originalMovieTitle,
                    originalSchedule,
                    displaySchedule);

            MovieSchedule? displayMovie =
                FindLocalizedMovieSchedule(
                    originalMovieTitle,
                    originalSchedule,
                    displaySchedule);

            string leadingIcon =
                GetMovieChangeIcon(
                    changes);

            var builder =
                new StringBuilder();

            builder.Append(
                "\n" +
                leadingIcon +
                " <b>" +
                EscapeTelegramHtml(
                    displayMovieTitle) +
                "</b>\n");

            foreach (ScheduleChange change in
                     changes)
            {
                builder.Append(
                    BuildCompactVisibleChangeLine(
                        change,
                        originalSchedule,
                        displaySchedule,
                        arabic));
            }

            if (displayMovie is not null)
            {
                int showtimeCount =
                    displayMovie.Halls.Sum(hall =>
                        hall.Showtimes.Length);

                int soldOutCount =
                    displayMovie.Halls.Sum(hall =>
                        hall.Showtimes.Count(showtime =>
                            !showtime.IsAvailable));

                builder.Append(
                    arabic
                        ? "   📊 " +
                          showtimeCount +
                          " مواعيد  •  🔴 " +
                          soldOutCount +
                          " مكتمل\n"
                        : "   📊 " +
                          showtimeCount +
                          " showtimes  •  🔴 " +
                          soldOutCount +
                          " sold out\n");

                builder.Append(
                    BuildExpandableMovieDetails(
                        displayMovie,
                        displaySchedule.Url,
                        arabic));
            }

            return builder.ToString();
        }

        private static string BuildCompactVisibleChangeLine(
                ScheduleChange change,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule,
                bool arabic)
        {
            string hall =
                EscapeTelegramHtml(
                    GetLocalizedHallName(
                        change.MovieTitle,
                        change.HallName,
                        originalSchedule,
                        displaySchedule));

            string time =
                EscapeTelegramHtml(
                    GetLocalizedShowtimeText(
                        change.MovieTitle,
                        change.HallName,
                        change.Time,
                        originalSchedule,
                        displaySchedule));

            string bookingLink =
                string.IsNullOrWhiteSpace(
                    change.BookingUrl)
                    ? string.Empty
                    : arabic
                        ? "  •  <a href=\"" +
                          EscapeTelegramHtml(
                              change.BookingUrl) +
                          "\"><b>احجز</b></a>"
                        : "  •  <a href=\"" +
                          EscapeTelegramHtml(
                              change.BookingUrl) +
                          "\"><b>BOOK</b></a>";

            if (arabic)
            {
                switch (change.Type)
                {
                    case ScheduleChangeType.NewMovie:
                        return
                            "   🆕 فيلم جديد ظهر في الجدول\n";

                    case ScheduleChangeType.MovieRemoved:
                        return
                            "   🗑 تم حذف الفيلم من الجدول\n";

                    case ScheduleChangeType.NewHall:
                        return
                            "   🏛 قاعة جديدة: <b>" +
                            hall +
                            "</b>\n";

                    case ScheduleChangeType.HallRemoved:
                        return
                            "   🚪 تم حذف القاعة: <b>" +
                            hall +
                            "</b>\n";

                    case ScheduleChangeType.NewShowtime:
                        return
                            "   🕒 موعد جديد: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            (change.IsAvailable
                                ? bookingLink
                                : "  •  🔴 مكتمل") +
                            "\n";

                    case ScheduleChangeType.ShowtimeRemoved:
                        return
                            "   ➖ تم حذف موعد: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.SoldOut:
                        return
                            "   🔴 الحجز اكتمل: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.AvailableAgain:
                        return
                            "   🟢 الحجز متاح مرة أخرى: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";

                    case ScheduleChangeType.BookingLinkAdded:
                        return
                            "   🎟 تمت إضافة رابط الحجز: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";

                    case ScheduleChangeType.BookingLinkRemoved:
                        return
                            "   ⚠️ تم حذف رابط الحجز: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.BookingLinkChanged:
                        return
                            "   🔄 تم تغيير رابط الحجز: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";
                }
            }
            else
            {
                switch (change.Type)
                {
                    case ScheduleChangeType.NewMovie:
                        return
                            "   🆕 Newly added to the schedule\n";

                    case ScheduleChangeType.MovieRemoved:
                        return
                            "   🗑 Removed from the schedule\n";

                    case ScheduleChangeType.NewHall:
                        return
                            "   🏛 New hall: <b>" +
                            hall +
                            "</b>\n";

                    case ScheduleChangeType.HallRemoved:
                        return
                            "   🚪 Hall removed: <b>" +
                            hall +
                            "</b>\n";

                    case ScheduleChangeType.NewShowtime:
                        return
                            "   🕒 New time: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            (change.IsAvailable
                                ? bookingLink
                                : "  •  🔴 SOLD OUT") +
                            "\n";

                    case ScheduleChangeType.ShowtimeRemoved:
                        return
                            "   ➖ Time removed: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.SoldOut:
                        return
                            "   🔴 Now sold out: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.AvailableAgain:
                        return
                            "   🟢 Booking open again: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";

                    case ScheduleChangeType.BookingLinkAdded:
                        return
                            "   🎟 Booking link added: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";

                    case ScheduleChangeType.BookingLinkRemoved:
                        return
                            "   ⚠️ Booking link removed: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>\n";

                    case ScheduleChangeType.BookingLinkChanged:
                        return
                            "   🔄 Booking link changed: " +
                            hall +
                            "  ›  <b>" +
                            time +
                            "</b>" +
                            bookingLink +
                            "\n";
                }
            }

            return string.Empty;
        }

        private static string BuildExpandableMovieDetails(
                MovieSchedule movie,
                string scheduleUrl,
                bool arabic)
        {
            const int maximumDetailsCharacters = 1800;

            var content =
                new StringBuilder();

            foreach (HallSchedule hall in
                     movie.Halls)
            {
                var hallBlock =
                    new StringBuilder();

                hallBlock.Append(
                    "🏛 <b>" +
                    EscapeTelegramHtml(
                        hall.Name) +
                    "</b>\n");

                if (hall.Showtimes.Length == 0)
                {
                    hallBlock.AppendLine(
                        arabic
                            ? "لا توجد مواعيد"
                            : "No showtimes");
                }
                else
                {
                    foreach (ShowtimeSchedule showtime in
                             hall.Showtimes)
                    {
                        if (showtime.IsAvailable)
                        {
                            hallBlock.Append(
                                "🟢 ");

                            if (!string.IsNullOrWhiteSpace(
                                    showtime.BookingUrl))
                            {
                                hallBlock.Append(
                                    "<a href=\"" +
                                    EscapeTelegramHtml(
                                        showtime.BookingUrl) +
                                    "\"><b>" +
                                    EscapeTelegramHtml(
                                        showtime.Time) +
                                    "</b></a>");
                            }
                            else
                            {
                                hallBlock.Append(
                                    "<b>" +
                                    EscapeTelegramHtml(
                                        showtime.Time) +
                                    "</b>");
                            }
                        }
                        else
                        {
                            hallBlock.Append(
                                "🔴 <s>" +
                                EscapeTelegramHtml(
                                    showtime.Time) +
                                "</s>");
                        }

                        hallBlock.AppendLine();
                    }
                }

                if (content.Length +
                    hallBlock.Length >
                    maximumDetailsCharacters)
                {
                    content.AppendLine(
                        arabic
                            ? "… توجد مواعيد أخرى على موقع VOX"
                            : "… More showtimes are available on the VOX website");

                    break;
                }

                content.Append(
                    hallBlock);
            }

            content.Append(
                arabic
                    ? "🔗 <a href=\"" +
                      EscapeTelegramHtml(
                          scheduleUrl) +
                      "\">افتح الجدول الكامل</a>"
                    : "🔗 <a href=\"" +
                      EscapeTelegramHtml(
                          scheduleUrl) +
                      "\">Open full schedule</a>");

            return
                "<blockquote expandable>" +
                content +
                "</blockquote>\n";
        }

        private static MovieSchedule? FindLocalizedMovieSchedule(
                string originalMovieTitle,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule)
        {
            int movieIndex =
                Array.FindIndex(
                    originalSchedule.Movies,
                    movie =>
                        string.Equals(
                            movie.Title,
                            originalMovieTitle,
                            StringComparison.OrdinalIgnoreCase));

            if (movieIndex >= 0 &&
                movieIndex <
                displaySchedule.Movies.Length)
            {
                return
                    displaySchedule.Movies[movieIndex];
            }

            return null;
        }

        private static string GetMovieChangeIcon(
                ScheduleChange[] changes)
        {
            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.NewMovie))
            {
                return "🆕";
            }

            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.SoldOut))
            {
                return "🔴";
            }

            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.AvailableAgain))
            {
                return "🟢";
            }

            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.NewShowtime))
            {
                return "🕒";
            }

            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.NewHall))
            {
                return "🏛";
            }

            if (changes.Any(change =>
                    change.Type ==
                    ScheduleChangeType.MovieRemoved))
            {
                return "🗑";
            }

            return "⚡";
        }

        private static string GetLocalizedMovieTitle(
                string originalMovieTitle,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule)
        {
            int movieIndex =
                Array.FindIndex(
                    originalSchedule.Movies,
                    movie =>
                        string.Equals(
                            movie.Title,
                            originalMovieTitle,
                            StringComparison.OrdinalIgnoreCase));

            if (movieIndex >= 0 &&
                movieIndex <
                displaySchedule.Movies.Length)
            {
                return
                    displaySchedule.Movies[movieIndex].Title;
            }

            return originalMovieTitle;
        }

        private static string GetLocalizedHallName(
                string originalMovieTitle,
                string originalHallName,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule)
        {
            int movieIndex =
                Array.FindIndex(
                    originalSchedule.Movies,
                    movie =>
                        string.Equals(
                            movie.Title,
                            originalMovieTitle,
                            StringComparison.OrdinalIgnoreCase));

            if (movieIndex < 0 ||
                movieIndex >=
                displaySchedule.Movies.Length)
            {
                return originalHallName;
            }

            MovieSchedule originalMovie =
                originalSchedule.Movies[movieIndex];

            MovieSchedule displayMovie =
                displaySchedule.Movies[movieIndex];

            int hallIndex =
                Array.FindIndex(
                    originalMovie.Halls,
                    hall =>
                        string.Equals(
                            hall.Name,
                            originalHallName,
                            StringComparison.OrdinalIgnoreCase));

            if (hallIndex >= 0 &&
                hallIndex <
                displayMovie.Halls.Length)
            {
                return
                    displayMovie.Halls[hallIndex].Name;
            }

            return originalHallName;
        }

        private static string GetLocalizedShowtimeText(
                string originalMovieTitle,
                string originalHallName,
                string originalTime,
                DaySchedule originalSchedule,
                DaySchedule displaySchedule)
        {
            int movieIndex =
                Array.FindIndex(
                    originalSchedule.Movies,
                    movie =>
                        string.Equals(
                            movie.Title,
                            originalMovieTitle,
                            StringComparison.OrdinalIgnoreCase));

            if (movieIndex < 0 ||
                movieIndex >=
                displaySchedule.Movies.Length)
            {
                return originalTime;
            }

            MovieSchedule originalMovie =
                originalSchedule.Movies[movieIndex];

            MovieSchedule displayMovie =
                displaySchedule.Movies[movieIndex];

            int hallIndex =
                Array.FindIndex(
                    originalMovie.Halls,
                    hall =>
                        string.Equals(
                            hall.Name,
                            originalHallName,
                            StringComparison.OrdinalIgnoreCase));

            if (hallIndex < 0 ||
                hallIndex >=
                displayMovie.Halls.Length)
            {
                return originalTime;
            }

            HallSchedule originalHall =
                originalMovie.Halls[hallIndex];

            HallSchedule displayHall =
                displayMovie.Halls[hallIndex];

            int showtimeIndex =
                Array.FindIndex(
                    originalHall.Showtimes,
                    showtime =>
                        string.Equals(
                            showtime.Time,
                            originalTime,
                            StringComparison.OrdinalIgnoreCase));

            if (showtimeIndex >= 0 &&
                showtimeIndex <
                displayHall.Showtimes.Length)
            {
                return
                    displayHall.Showtimes[showtimeIndex].Time;
            }

            return originalTime;
        }

        private static void AppendDigestBlock(
                List<string> messages,
                StringBuilder current,
                string continuedHeader,
                string block,
                int maximumLength)
        {
            if (string.IsNullOrEmpty(
                    block))
            {
                return;
            }

            // Digest blocks are normally small and are kept intact so Telegram
            // HTML tags and booking links are never cut in the middle.
            if (current.Length +
                block.Length <=
                maximumLength)
            {
                current.Append(
                    block);

                return;
            }

            if (current.Length > 0)
            {
                messages.Add(
                    current.ToString());

                current.Clear();
                current.Append(
                    continuedHeader);
            }

            if (current.Length +
                block.Length <=
                maximumLength)
            {
                current.Append(
                    block);

                return;
            }

            // Extremely large blocks are split only at newline boundaries.
            // This preserves complete HTML elements such as <a>...</a>.
            string[] lines =
                block.Split(
                    new[]
                    {
                        "\r\n",
                        "\n"
                    },
                    StringSplitOptions.None);

            foreach (string line in
                     lines)
            {
                string completeLine =
                    line +
                    "\n";

                if (current.Length +
                    completeLine.Length >
                    maximumLength &&
                    current.Length >
                    continuedHeader.Length)
                {
                    messages.Add(
                        current.ToString());

                    current.Clear();
                    current.Append(
                        continuedHeader);
                }

                if (completeLine.Length >
                    maximumLength -
                    current.Length)
                {
                    // A single plain-text line should not normally reach this
                    // size. Keep the digest valid rather than breaking HTML.
                    int allowedLength =
                        Math.Max(
                            1,
                            maximumLength -
                            current.Length -
                            2);

                    string safeLine =
                        StripTelegramHtml(
                            completeLine);

                    if (safeLine.Length >
                        allowedLength)
                    {
                        safeLine =
                            safeLine.Substring(
                                0,
                                allowedLength) +
                            "…";
                    }

                    current.AppendLine(
                        EscapeTelegramHtml(
                            safeLine));
                }
                else
                {
                    current.Append(
                        completeLine);
                }
            }
        }

        private static string StripTelegramHtml(
                string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            var builder =
                new StringBuilder();

            bool insideTag =
                false;

            foreach (char character in
                     value)
            {
                if (character == '<')
                {
                    insideTag =
                        true;

                    continue;
                }

                if (character == '>')
                {
                    insideTag =
                        false;

                    continue;
                }

                if (!insideTag)
                {
                    builder.Append(
                        character);
                }
            }

            return WebUtility.HtmlDecode(
                builder.ToString());
        }

        private async Task<bool> SendTelegramTextAsync(
                string message,
                string botToken,
                string chatId,
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
                        true
                },
                stoppingToken,
                rateLimitChatKey:
                    chatId,
                applyMessageRateLimit:
                    true);
        }

        private void QueueTelegramAlert(
                string botToken,
                string chatId,
                string message,
                object? replyMarkup)
        {
            var queuedRequest =
                new TelegramQueuedRequest(
                    botToken,
                    chatId,
                    message,
                    replyMarkup);

            if (!_telegramAlertQueue.Writer.TryWrite(
                    queuedRequest))
            {
                _logger.LogError(
                    "The Telegram alert queue could not accept a message for chat {ChatId}.",
                    chatId);
            }
            else
            {
                _logger.LogInformation(
                    "Telegram alert queued for chat {ChatId}. " +
                    "VOX scanning continues independently every 60 seconds.",
                    chatId);
            }
        }

        private async Task RunTelegramAlertSenderAsync(
                CancellationToken stoppingToken)
        {
            try
            {
                await foreach (TelegramQueuedRequest request in
                               _telegramAlertQueue.Reader.ReadAllAsync(
                                   stoppingToken))
                {
                    bool delivered =
                        false;

                    while (!delivered &&
                           !stoppingToken.IsCancellationRequested)
                    {
                        if (ReadBooleanSetting(
                                "Telegram:RequireChannelSubscription",
                                defaultValue: false) &&
                            long.TryParse(
                                request.ChatId,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out long alertUserId))
                        {
                            bool subscribed =
                                await CheckChannelMembershipAsync(
                                    request.BotToken,
                                    (_configuration["Telegram:ChannelUsername"] ?? string.Empty).Trim(),
                                    alertUserId,
                                    stoppingToken);

                            if (!subscribed)
                            {
                                RemoveKnownBotSubscriber(alertUserId);
                                _logger.LogInformation(
                                    "Dropping queued Telegram alert for user {UserId}: user is no longer subscribed to the required channel.",
                                    alertUserId);
                                delivered = true;
                                break;
                            }
                        }

                        delivered =
                            await PostTelegramJsonAsync(
                                request.BotToken,
                                "sendMessage",
                                request.CreatePayload(),
                                stoppingToken,
                                rateLimitChatKey:
                                    request.ChatId,
                                applyMessageRateLimit:
                                    true);

                        if (!delivered)
                        {
                            int retryDelaySeconds =
                                ReadIntegerSetting(
                                    "Telegram:QueueFailureRetrySeconds",
                                    defaultValue: 10,
                                    minimumValue: 3,
                                    maximumValue: 120);

                            _logger.LogWarning(
                                "A queued Telegram alert was not delivered. " +
                                "It remains at the front of the queue and will retry " +
                                "after {RetryDelaySeconds} seconds.",
                                retryDelaySeconds);

                            await Task.Delay(
                                TimeSpan.FromSeconds(
                                    retryDelaySeconds),
                                stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown.
            }
        }

        private List<string> BuildCompactChangeDigestMessages(
                List<DayScheduleChange> days,
                bool arabic,
                Dictionary<string, DaySchedule>? localizedSchedules,
                int maximumLength)
        {
            string firstHeader =
                arabic
                    ? "🚨 <b>تغييرات جديدة في VOX</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n"
                    : "🚨 <b>NEW VOX CHANGES</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n";

            string continuedHeader =
                arabic
                    ? "🚨 <b>تابع التغييرات</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n"
                    : "🚨 <b>VOX CHANGES — CONTINUED</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n";

            var messages =
                new List<string>();

            var current =
                new StringBuilder(
                    firstHeader);

            foreach (DayScheduleChange day in
                     days
                         .OrderBy(item =>
                             item.Schedule.CinemaName,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.Schedule.Date))
            {
                DaySchedule originalSchedule =
                    day.Schedule;

                DaySchedule displaySchedule =
                    originalSchedule;

                if (localizedSchedules is not null)
                {
                    string localizedKey =
                        FormatStateKey(
                            originalSchedule.CinemaSlug,
                            originalSchedule.Date);

                    if (localizedSchedules.TryGetValue(
                            localizedKey,
                            out DaySchedule? localizedSchedule))
                    {
                        displaySchedule =
                            localizedSchedule;
                    }
                }

                string section =
                    "\n🏢 <b>" +
                    EscapeTelegramHtml(
                        displaySchedule.CinemaName) +
                    "</b>\n" +
                    "📅 " +
                    EscapeTelegramHtml(
                        arabic
                            ? displaySchedule.Date.ToString(
                                "dd MMMM",
                                new CultureInfo(
                                    "ar-EG"))
                            : displaySchedule.Date.ToString(
                                "dd MMMM",
                                CultureInfo.InvariantCulture)) +
                    "\n";

                AppendDigestBlock(
                    messages,
                    current,
                    continuedHeader,
                    section,
                    maximumLength);

                foreach (IGrouping<string, ScheduleChange> movieChanges in
                         day.Changes.GroupBy(
                             change =>
                                 change.MovieTitle,
                             StringComparer.OrdinalIgnoreCase))
                {
                    string displayMovieTitle =
                        GetLocalizedMovieTitle(
                            movieChanges.Key,
                            originalSchedule,
                            displaySchedule);

                    string movieBlock =
                        "\n🎬 <b>" +
                        EscapeTelegramHtml(
                            displayMovieTitle) +
                        "</b>\n";

                    foreach (ScheduleChange change in
                             movieChanges.Take(4))
                    {
                        movieBlock +=
                            BuildCompactVisibleChangeLine(
                                change,
                                originalSchedule,
                                displaySchedule,
                                arabic);
                    }

                    if (movieChanges.Count() > 4)
                    {
                        movieBlock +=
                            arabic
                                ? "   • +" +
                                  (movieChanges.Count() - 4) +
                                  " تغييرات أخرى\n"
                                : "   • +" +
                                  (movieChanges.Count() - 4) +
                                  " more changes\n";
                    }

                    AppendDigestBlock(
                        messages,
                        current,
                        continuedHeader,
                        movieBlock,
                        maximumLength);
                }
            }

            AppendDigestBlock(
                messages,
                current,
                continuedHeader,
                arabic
                    ? "\n👇 استخدم الأزرار لفتح السينما ثم اليوم ثم الفيلم.\n"
                    : "\n👇 Use the buttons to open cinema → date → movie.\n",
                maximumLength);

            if (current.Length > 0)
            {
                messages.Add(
                    current.ToString());
            }

            return messages;
        }

        private object BuildCompactAlertKeyboard(
                List<DayScheduleChange> changes,
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            var rows =
                new List<object>();

            foreach (DayScheduleChange day in
                     changes
                         .GroupBy(change =>
                             change.Schedule.CinemaSlug,
                             StringComparer.OrdinalIgnoreCase)
                         .Select(group =>
                             group.First())
                         .Take(6))
            {
                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                "🏢 " +
                                day.Schedule.CinemaName,

                            callback_data =
                                "browse:cin:" +
                                day.Schedule.CinemaSlug
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
                                ? "🎬 تصفح الكل"
                                : "🎬 Browse All",

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
    }
}
