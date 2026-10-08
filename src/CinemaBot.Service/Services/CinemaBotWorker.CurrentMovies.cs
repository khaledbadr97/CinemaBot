using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    public partial class CinemaBotWorker
    {
        private readonly object _currentMoviesLock =
            new object();

        private readonly HashSet<string> _pendingEarlyBookingAlertKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CurrentMovieCatalogItem[] _activeCurrentMovies =
            Array.Empty<CurrentMovieCatalogItem>();

        private Dictionary<long, CurrentMovieUserPreference> _currentMoviePreferences =
            new Dictionary<long, CurrentMovieUserPreference>();

        private string CurrentMoviePreferencesPath =>
            Path.Combine(
                _dataDirectory,
                "current-movie-user-preferences.json");

        private void RefreshCurrentMovieCatalog(
            Dictionary<string, DaySchedule> schedules)
        {
            DateTime nowLocal =
                DateTime.Now;

            CurrentMovieCatalogItem[] catalog =
                schedules.Values
                    .Where(schedule =>
                        schedule.Date.Date >=
                        nowLocal.Date)
                    .SelectMany(schedule =>
                        schedule.Movies.Where(movie =>
                            movie.Halls.Any(hall =>
                                hall.Showtimes.Any(showtime =>
                                    showtime.IsAvailable &&
                                    !string.IsNullOrWhiteSpace(
                                        showtime.BookingUrl) &&
                                    IsFutureShowtime(
                                        schedule.Date,
                                        showtime.Time,
                                        nowLocal)))))
                    .GroupBy(movie =>
                        NormalizeMovieTitleKey(
                            movie.Title),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group =>
                        !string.IsNullOrWhiteSpace(
                            group.Key))
                    .Select(group =>
                        new CurrentMovieCatalogItem
                        {
                            Key =
                                group.Key,

                            EnglishTitle =
                                group
                                    .Select(movie =>
                                        movie.Title)
                                    .OrderBy(title =>
                                        title.Length)
                                    .First(),

                            ArabicTitle =
                                FindCurrentMovieArabicTitle(
                                    group.Key)
                        })
                    .OrderBy(movie =>
                        movie.EnglishTitle,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            lock (_currentMoviesLock)
            {
                _activeCurrentMovies =
                    catalog;
            }
        }

        private string FindCurrentMovieArabicTitle(
            string movieKey)
        {
            DaySchedule[] schedules;

            lock (_arabicDashboardCacheLock)
            {
                schedules =
                    _arabicDashboardSchedulesByKey.Values
                        .ToArray();
            }

            foreach (DaySchedule schedule in
                     schedules)
            {
                MovieSchedule? movie =
                    schedule.Movies.FirstOrDefault(item =>
                        string.Equals(
                            NormalizeMovieTitleKey(
                                item.Title),
                            movieKey,
                            StringComparison.OrdinalIgnoreCase));

                if (movie is not null &&
                    ContainsArabicCharacters(
                        movie.Title))
                {
                    return movie.Title;
                }
            }

            return string.Empty;
        }

        private async Task EvaluateCurrentMovieSubscriptionsAsync(
            Dictionary<string, DaySchedule> currentSchedules,
            string botToken,
            bool telegramAvailable,
            CancellationToken stoppingToken)
        {
            RefreshCurrentMovieCatalog(
                currentSchedules);

            if (!telegramAvailable)
            {
                return;
            }

            CurrentMovieUserPreference[] settings;

            lock (_currentMoviesLock)
            {
                settings =
                    _currentMoviePreferences.Values
                        .Select(item =>
                            item.Clone())
                        .ToArray();
            }

            bool changed =
                false;

            foreach (CurrentMovieUserPreference setting in
                     settings)
            {
                UserPreference? userPreference =
                    GetUserPreferenceSnapshot(
                        setting.UserId);

                if (userPreference is null ||
                    setting.PrivateChatId == 0)
                {
                    continue;
                }

                foreach (CurrentMovieSubscription subscription in
                         setting.Subscriptions)
                {
                    DaySchedule[] matches =
                        GetCurrentMovieMatchingSchedules(
                            subscription,
                            currentSchedules.Values,
                            DateTime.Now);

                    foreach (DaySchedule schedule in
                             matches)
                    {
                        string notificationKey =
                            BuildCurrentMovieDateKey(
                                subscription.MovieKey,
                                schedule.CinemaSlug,
                                schedule.Date);

                        if (setting.KnownBookingDateKeys.Contains(
                                notificationKey))
                        {
                            continue;
                        }

                        int? earlyDelayMinutes =
                            GetEarlyBookingAlertDelayMinutes(setting.UserId);

                        if (!earlyDelayMinutes.HasValue)
                        {
                            setting.KnownBookingDateKeys.Add(notificationKey);
                            MarkCurrentMovieDateKnown(setting.UserId, notificationKey);
                            changed = true;
                            continue;
                        }

                        if (earlyDelayMinutes.Value <= 0)
                        {
                            bool sent = await SendCurrentMovieNewDateNotificationAsync(
                                botToken, userPreference, subscription, schedule, stoppingToken);

                            if (!sent) continue;

                            setting.KnownBookingDateKeys.Add(notificationKey);
                            MarkCurrentMovieDateKnown(setting.UserId, notificationKey);
                            changed = true;
                            continue;
                        }

                        string pendingKey = BuildEarlyBookingAlertPendingKey(
                            setting.UserId, notificationKey);

                        lock (_currentMoviesLock)
                        {
                            if (!_pendingEarlyBookingAlertKeys.Add(pendingKey))
                                continue;
                        }

                        _ = SendCurrentMovieNewDateNotificationAfterDelayAsync(
                            botToken,
                            setting.UserId,
                            subscription.Clone(),
                            schedule,
                            notificationKey,
                            pendingKey,
                            earlyDelayMinutes.Value,
                            stoppingToken);

                        changed = true;
                    }
                }
            }

            if (changed)
            {
                SaveCurrentMoviePreferences();
            }
        }

        private string BuildEarlyBookingAlertPendingKey(long userId, string notificationKey)
        {
            return userId.ToString(CultureInfo.InvariantCulture) + ":" + notificationKey;
        }

        private async Task SendCurrentMovieNewDateNotificationAfterDelayAsync(
                string botToken,
                long userId,
                CurrentMovieSubscription subscription,
                DaySchedule schedule,
                string notificationKey,
                string pendingKey,
                int delayMinutes,
                CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);

                UserPreference? latestPreference = GetUserPreferenceSnapshot(userId);
                if (latestPreference is null || latestPreference.PrivateChatId == 0) return;

                int? latestDelay = GetEarlyBookingAlertDelayMinutes(userId);
                if (!latestDelay.HasValue) return;

                bool sent = await SendCurrentMovieNewDateNotificationAsync(
                    botToken, latestPreference, subscription, schedule, stoppingToken);

                if (sent)
                {
                    lock (_currentMoviesLock)
                    {
                        if (_currentMoviePreferences.TryGetValue(userId, out CurrentMovieUserPreference? setting))
                            setting.KnownBookingDateKeys.Add(notificationKey);
                    }

                    MarkCurrentMovieDateKnown(userId, notificationKey);
                    SaveCurrentMoviePreferences();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Delayed early booking alert failed for Telegram user {UserId} and key {NotificationKey}.",
                    userId, notificationKey);
            }
            finally
            {
                lock (_currentMoviesLock)
                    _pendingEarlyBookingAlertKeys.Remove(pendingKey);
            }
        }

        private DaySchedule[] GetCurrentMovieMatchingSchedules(
            CurrentMovieSubscription subscription,
            IEnumerable<DaySchedule> schedules,
            DateTime nowLocal)
        {
            return schedules
                .Where(schedule =>
                    subscription.CinemaSlugs.Count == 0 ||
                    subscription.CinemaSlugs.Contains(
                        schedule.CinemaSlug))
                .Where(schedule =>
                    schedule.Date.Date >=
                    nowLocal.Date)
                .Where(schedule =>
                {
                    MovieSchedule? movie =
                        schedule.Movies.FirstOrDefault(item =>
                            string.Equals(
                                NormalizeMovieTitleKey(
                                    item.Title),
                                subscription.MovieKey,
                                StringComparison.OrdinalIgnoreCase));

                    return movie is not null &&
                           movie.Halls.Any(hall =>
                               hall.Showtimes.Any(showtime =>
                                   showtime.IsAvailable &&
                                   !string.IsNullOrWhiteSpace(
                                       showtime.BookingUrl) &&
                                   IsFutureShowtime(
                                       schedule.Date,
                                       showtime.Time,
                                       nowLocal)));
                })
                .OrderBy(schedule =>
                    schedule.Date)
                .ThenBy(schedule =>
                    schedule.CinemaName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<bool> SendCurrentMovieNewDateNotificationAsync(
            string botToken,
            UserPreference preference,
            CurrentMovieSubscription subscription,
            DaySchedule schedule,
            CancellationToken stoppingToken)
        {
            MovieSchedule? movie =
                schedule.Movies.FirstOrDefault(item =>
                    string.Equals(
                        NormalizeMovieTitleKey(
                            item.Title),
                        subscription.MovieKey,
                        StringComparison.OrdinalIgnoreCase));

            if (movie is null)
            {
                return false;
            }

            MovieSchedule resolvedMovie =
                await ResolveMovieBookingUrlsAsync(
                    movie,
                    schedule.Url,
                    stoppingToken,
                    forceRefresh:
                        true);

            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string cinemaName =
                arabic
                    ? GetArabicCinemaDisplayName(
                        schedule.CinemaSlug,
                        schedule.CinemaName,
                        schedule.CinemaName)
                    : schedule.CinemaName;

            string title =
                arabic &&
                !string.IsNullOrWhiteSpace(
                    FindCurrentMovieArabicTitle(
                        subscription.MovieKey))
                    ? FindCurrentMovieArabicTitle(
                        subscription.MovieKey)
                    : movie.Title;

            var builder =
                new StringBuilder();

            builder.AppendLine(
                arabic
                    ? "🆕 <b>تم فتح يوم حجز جديد</b>"
                    : "🆕 <b>NEW BOOKING DATE AVAILABLE</b>");

            builder.AppendLine(
                "━━━━━━━━━━━━━━━━━━━━");

            builder.AppendLine(
                "🎬 <b>" +
                (arabic
                    ? FormatArabicBidiText(
                        title)
                    : EscapeTelegramHtml(
                        title)) +
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

            foreach (HallSchedule hall in
                     resolvedMovie.Halls)
            {
                string[] times =
                    hall.Showtimes
                        .Where(showtime =>
                            showtime.IsAvailable &&
                            !string.IsNullOrWhiteSpace(
                                showtime.BookingUrl) &&
                            IsFutureShowtime(
                                schedule.Date,
                                showtime.Time,
                                DateTime.Now))
                        .Select(showtime =>
                            showtime.Time)
                        .ToArray();

                if (times.Length == 0)
                {
                    continue;
                }

                builder.AppendLine(
                    "🏛 " +
                    (arabic
                        ? FormatArabicBidiText(
                            hall.Name)
                        : EscapeTelegramHtml(
                            hall.Name)) +
                    ": " +
                    string.Join(
                        " • ",
                        times.Select(
                            EscapeTelegramHtml)));
            }

            return await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                LimitText(
                    builder.ToString(),
                    3900),
                BuildCurrentMovieBookingKeyboard(
                    preference.Language,
                    schedule,
                    resolvedMovie),
                stoppingToken);
        }

        private object BuildCurrentMovieBookingKeyboard(
            string language,
            DaySchedule schedule,
            MovieSchedule movie)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            int maximumButtons =
                ReadIntegerSetting(
                    "CurrentMovies:MaximumDirectBookingButtons",
                    defaultValue: 24,
                    minimumValue: 2,
                    maximumValue: 80);

            var rows =
                new List<object>();

            var row =
                new List<object>();

            int added =
                0;

            foreach (HallSchedule hall in
                     movie.Halls)
            {
                foreach (ShowtimeSchedule showtime in
                         hall.Showtimes.Where(item =>
                             item.IsAvailable &&
                             !string.IsNullOrWhiteSpace(
                                 item.BookingUrl) &&
                             IsFutureShowtime(
                                 schedule.Date,
                                 item.Time,
                                 DateTime.Now)))
                {
                    row.Add(
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

                    added++;

                    if (row.Count == 2)
                    {
                        rows.Add(
                            row.ToArray());

                        row.Clear();
                    }

                    if (added >=
                        maximumButtons)
                    {
                        break;
                    }
                }

                if (added >=
                    maximumButtons)
                {
                    break;
                }
            }

            if (row.Count > 0)
            {
                rows.Add(
                    row.ToArray());
            }

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

        private async Task SendCurrentMoviesMenuAsync(
            string botToken,
            UserPreference preference,
            int page,
            CancellationToken stoppingToken)
        {
            CurrentMovieUserPreference setting =
                GetOrCreateCurrentMoviePreference(
                    preference.UserId,
                    preference.PrivateChatId);

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildCurrentMoviesMenuText(
                    preference.Language),
                BuildCurrentMoviesMenuKeyboard(
                    preference.Language,
                    setting,
                    page),
                stoppingToken);
        }

        private async Task HandleCurrentMoviesCallbackAsync(
            string botToken,
            string callbackId,
            string data,
            UserPreference preference,
            int messageId,
            CancellationToken stoppingToken)
        {
            await AnswerCallbackQueryAsync(
                botToken,
                callbackId,
                string.Empty,
                false,
                stoppingToken);

            CurrentMovieUserPreference setting =
                GetOrCreateCurrentMoviePreference(
                    preference.UserId,
                    preference.PrivateChatId);

            string[] parts =
                data.Split(':');

            if (string.Equals(
                    data,
                    "current:refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                RequestImmediateScan(
                    force: true);

                await EditCurrentMoviesMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (data.StartsWith(
                    "current:menu",
                    StringComparison.OrdinalIgnoreCase))
            {
                int page =
                    parts.Length >= 3 &&
                    int.TryParse(
                        parts[2],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedPage)
                        ? parsedPage
                        : 0;

                await EditCurrentMoviesMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    page,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    data,
                    "current:selectall",
                    StringComparison.OrdinalIgnoreCase))
            {
                SelectAllCurrentMovies(
                    setting);

                SeedCurrentMovieBaseline(
                    setting,
                    _knownScheduleByCinemaAndDate.Values);

                SaveCurrentMoviePreferences();

                await EditCurrentMoviesMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    data,
                    "current:clearall",
                    StringComparison.OrdinalIgnoreCase))
            {
                setting.Subscriptions.Clear();
                setting.KnownBookingDateKeys.Clear();

                SaveCurrentMoviePreferences();

                await EditCurrentMoviesMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (parts.Length < 3 ||
                !int.TryParse(
                    parts[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int movieIndex))
            {
                return;
            }

            CurrentMovieCatalogItem? movie =
                GetCurrentMovieByIndex(
                    movieIndex);

            if (movie is null)
            {
                await EditCurrentMoviesMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    parts[1],
                    "movie",
                    StringComparison.OrdinalIgnoreCase))
            {
                await EditCurrentMovieAsync(
                    botToken,
                    preference,
                    setting,
                    movie,
                    movieIndex,
                    messageId,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    parts[1],
                    "all",
                    StringComparison.OrdinalIgnoreCase))
            {
                ToggleCurrentMovieAllCinemas(
                    setting,
                    movie);

                SeedCurrentMovieBaseline(
                    setting,
                    _knownScheduleByCinemaAndDate.Values,
                    movie.Key);

                SaveCurrentMoviePreferences();

                await EditCurrentMovieAsync(
                    botToken,
                    preference,
                    setting,
                    movie,
                    movieIndex,
                    messageId,
                    stoppingToken);

                return;
            }

            if (string.Equals(
                    parts[1],
                    "cin",
                    StringComparison.OrdinalIgnoreCase) &&
                parts.Length >= 4 &&
                int.TryParse(
                    parts[3],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int cinemaIndex) &&
                cinemaIndex >= 0 &&
                cinemaIndex <
                _lastDiscoveredCinemas.Length)
            {
                ToggleCurrentMovieCinema(
                    setting,
                    movie,
                    _lastDiscoveredCinemas[cinemaIndex].Slug);

                SeedCurrentMovieBaseline(
                    setting,
                    _knownScheduleByCinemaAndDate.Values,
                    movie.Key);

                SaveCurrentMoviePreferences();

                await EditCurrentMovieAsync(
                    botToken,
                    preference,
                    setting,
                    movie,
                    movieIndex,
                    messageId,
                    stoppingToken);
            }
        }

        private async Task EditCurrentMoviesMenuAsync(
            string botToken,
            UserPreference preference,
            CurrentMovieUserPreference setting,
            int messageId,
            int page,
            CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildCurrentMoviesMenuText(
                    preference.Language),
                BuildCurrentMoviesMenuKeyboard(
                    preference.Language,
                    setting,
                    page),
                stoppingToken);
        }

        private async Task EditCurrentMovieAsync(
            string botToken,
            UserPreference preference,
            CurrentMovieUserPreference setting,
            CurrentMovieCatalogItem movie,
            int movieIndex,
            int messageId,
            CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    preference.Language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string title =
                arabic &&
                !string.IsNullOrWhiteSpace(
                    movie.ArabicTitle)
                    ? movie.ArabicTitle
                    : movie.EnglishTitle;

            string message =
                arabic
                    ? "🎬 <b>متابعة فيلم معروض حالياً</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "🎬 <b>" +
                      FormatArabicBidiText(
                          title) +
                      "</b>\n\nاختر كل السينمات أو سينما واحدة أو أكثر. " +
                      "لن تتلقى الأيام الموجودة الآن؛ سيصلك تنبيه فقط عند فتح يوم حجز جديد للفيلم."
                    : "🎬 <b>FOLLOW CURRENT MOVIE</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "🎬 <b>" +
                      EscapeTelegramHtml(
                          title) +
                      "</b>\n\nChoose all cinemas or one or more cinemas. " +
                      "Existing dates are used as a baseline; you will be notified only when a new booking date opens.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                message,
                BuildCurrentMovieKeyboard(
                    preference.Language,
                    setting,
                    movie,
                    movieIndex),
                stoppingToken);
        }

        private string BuildCurrentMoviesMenuText(
            string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            return arabic
                ? "🎬 <b>متابعة الأفلام الحالية</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "🎞 " +
                  _activeCurrentMovies.Length +
                  " فيلم متاح حالياً.\n\n" +
                  "اختر Spider-Man أو The Odyssey أو أي فيلم أو أكثر، " +
                  "ثم اختر كل السينمات أو سينمات محددة. لن يصلك شيء عن الأيام الموجودة الآن؛ " +
                  "سيصلك تنبيه فقط عند فتح يوم حجز مستقبلي جديد لهذا الفيلم.\n\n" +
                  BuildLiveStatusLine(
                      language)
                : "🎬 <b>FOLLOW CURRENT MOVIES</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "🎞 " +
                  _activeCurrentMovies.Length +
                  " movies are currently bookable.\n\n" +
                  "Choose Spider-Man, The Odyssey, or any one or more movies, " +
                  "then choose all cinemas or selected cinemas. Existing dates are saved as the baseline; " +
                  "you are notified only when a new future booking date appears for your chosen movie.\n\n" +
                  BuildLiveStatusLine(
                      language);
        }

        private object BuildCurrentMoviesMenuKeyboard(
            string language,
            CurrentMovieUserPreference setting,
            int page)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            int pageSize =
                ReadIntegerSetting(
                    "CurrentMovies:PageSize",
                    defaultValue: 8,
                    minimumValue: 4,
                    maximumValue: 20);

            int pageCount =
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        _activeCurrentMovies.Length /
                        (double)pageSize));

            page =
                Math.Max(
                    0,
                    Math.Min(
                        page,
                        pageCount - 1));

            var rows =
                new List<object>();

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "✅ اختيار كل الأفلام"
                                : "✅ Select All Movies",

                        callback_data =
                            "current:selectall"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🧹 مسح الكل"
                                : "🧹 Clear All",

                        callback_data =
                            "current:clearall"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⚡ تحديث مباشر الآن"
                                : "⚡ Refresh Live Now",

                        callback_data =
                            "current:refresh"
                    }
                });

            int start =
                page *
                pageSize;

            for (int index = start;
                 index <
                 Math.Min(
                     start + pageSize,
                     _activeCurrentMovies.Length);
                 index++)
            {
                CurrentMovieCatalogItem movie =
                    _activeCurrentMovies[index];

                bool selected =
                    setting.Subscriptions.Any(subscription =>
                        string.Equals(
                            subscription.MovieKey,
                            movie.Key,
                            StringComparison.OrdinalIgnoreCase));

                string title =
                    arabic &&
                    !string.IsNullOrWhiteSpace(
                        movie.ArabicTitle)
                        ? movie.ArabicTitle
                        : movie.EnglishTitle;

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                (selected
                                    ? "✅ "
                                    : "🎬 ") +
                                title,

                            callback_data =
                                "current:movie:" +
                                index.ToString(
                                    CultureInfo.InvariantCulture)
                        }
                    });
            }

            if (pageCount > 1)
            {
                var navigation =
                    new List<object>();

                if (page > 0)
                {
                    navigation.Add(
                        new
                        {
                            text =
                                "⬅️",

                            callback_data =
                                "current:menu:" +
                                (page - 1).ToString(
                                    CultureInfo.InvariantCulture)
                        });
                }

                navigation.Add(
                    new
                    {
                        text =
                            (page + 1) +
                            "/" +
                            pageCount,

                        callback_data =
                            "current:menu:" +
                            page.ToString(
                                CultureInfo.InvariantCulture)
                    });

                if (page + 1 <
                    pageCount)
                {
                    navigation.Add(
                        new
                        {
                            text =
                                "➡️",

                            callback_data =
                                "current:menu:" +
                                (page + 1).ToString(
                                    CultureInfo.InvariantCulture)
                        });
                }

                rows.Add(
                    navigation.ToArray());
            }

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "🔜 أفلام قريباً"
                                : "🔜 Coming Soon",

                        callback_data =
                            "coming:menu:0"
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

        private object BuildCurrentMovieKeyboard(
            string language,
            CurrentMovieUserPreference setting,
            CurrentMovieCatalogItem movie,
            int movieIndex)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            CurrentMovieSubscription? subscription =
                setting.Subscriptions.FirstOrDefault(item =>
                    string.Equals(
                        item.MovieKey,
                        movie.Key,
                        StringComparison.OrdinalIgnoreCase));

            bool allCinemas =
                subscription is not null &&
                subscription.CinemaSlugs.Count == 0;

            var rows =
                new List<object>();

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            (allCinemas
                                ? "✅ "
                                : "▫️ ") +
                            (arabic
                                ? "كل السينمات"
                                : "All Cinemas"),

                        callback_data =
                            "current:all:" +
                            movieIndex.ToString(
                                CultureInfo.InvariantCulture)
                    }
                });

            for (int index = 0;
                 index <
                 _lastDiscoveredCinemas.Length;
                 index++)
            {
                CinemaOption cinema =
                    _lastDiscoveredCinemas[index];

                bool selected =
                    subscription is not null &&
                    subscription.CinemaSlugs.Contains(
                        cinema.Slug);

                string name =
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
                                (selected
                                    ? "✅ "
                                    : "▫️ ") +
                                name,

                            callback_data =
                                "current:cin:" +
                                movieIndex.ToString(
                                    CultureInfo.InvariantCulture) +
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
                                ? "⬅️ قائمة الأفلام الحالية"
                                : "⬅️ Current Movies List",

                        callback_data =
                            "current:menu:0"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private CurrentMovieCatalogItem? GetCurrentMovieByIndex(
            int index)
        {
            lock (_currentMoviesLock)
            {
                return index >= 0 &&
                       index <
                       _activeCurrentMovies.Length
                    ? _activeCurrentMovies[index]
                    : null;
            }
        }

        private CurrentMovieUserPreference GetOrCreateCurrentMoviePreference(
            long userId,
            long privateChatId)
        {
            lock (_currentMoviesLock)
            {
                if (!_currentMoviePreferences.TryGetValue(
                        userId,
                        out CurrentMovieUserPreference? setting))
                {
                    setting =
                        new CurrentMovieUserPreference
                        {
                            UserId =
                                userId,

                            PrivateChatId =
                                privateChatId
                        };

                    _currentMoviePreferences[userId] =
                        setting;

                    SaveCurrentMoviePreferences();
                }
                else
                {
                    setting.PrivateChatId =
                        privateChatId;
                }

                return setting;
            }
        }

        private static CurrentMovieSubscription GetOrCreateCurrentMovieSubscription(
            CurrentMovieUserPreference setting,
            CurrentMovieCatalogItem movie)
        {
            CurrentMovieSubscription? subscription =
                setting.Subscriptions.FirstOrDefault(item =>
                    string.Equals(
                        item.MovieKey,
                        movie.Key,
                        StringComparison.OrdinalIgnoreCase));

            if (subscription is null)
            {
                subscription =
                    new CurrentMovieSubscription
                    {
                        MovieKey =
                            movie.Key,

                        MovieTitle =
                            movie.EnglishTitle
                    };

                setting.Subscriptions.Add(
                    subscription);
            }

            return subscription;
        }

        private void SelectAllCurrentMovies(
            CurrentMovieUserPreference setting)
        {
            lock (_currentMoviesLock)
            {
                setting.Subscriptions =
                    _activeCurrentMovies
                        .Select(movie =>
                            new CurrentMovieSubscription
                            {
                                MovieKey =
                                    movie.Key,

                                MovieTitle =
                                    movie.EnglishTitle
                            })
                        .ToList();
            }
        }

        private static void ToggleCurrentMovieAllCinemas(
            CurrentMovieUserPreference setting,
            CurrentMovieCatalogItem movie)
        {
            CurrentMovieSubscription? existing =
                setting.Subscriptions.FirstOrDefault(item =>
                    string.Equals(
                        item.MovieKey,
                        movie.Key,
                        StringComparison.OrdinalIgnoreCase));

            if (existing is not null &&
                existing.CinemaSlugs.Count == 0)
            {
                setting.Subscriptions.Remove(
                    existing);

                return;
            }

            CurrentMovieSubscription subscription =
                GetOrCreateCurrentMovieSubscription(
                    setting,
                    movie);

            subscription.CinemaSlugs.Clear();
        }

        private static void ToggleCurrentMovieCinema(
            CurrentMovieUserPreference setting,
            CurrentMovieCatalogItem movie,
            string cinemaSlug)
        {
            CurrentMovieSubscription subscription =
                GetOrCreateCurrentMovieSubscription(
                    setting,
                    movie);

            if (subscription.CinemaSlugs.Count == 0)
            {
                subscription.CinemaSlugs.Add(
                    cinemaSlug);

                return;
            }

            if (!subscription.CinemaSlugs.Add(
                    cinemaSlug))
            {
                subscription.CinemaSlugs.Remove(
                    cinemaSlug);
            }

            if (subscription.CinemaSlugs.Count == 0)
            {
                setting.Subscriptions.Remove(
                    subscription);
            }
        }

        private void SeedCurrentMovieBaseline(
            CurrentMovieUserPreference setting,
            IEnumerable<DaySchedule> schedules,
            string? movieKey = null)
        {
            IEnumerable<CurrentMovieSubscription> subscriptions =
                setting.Subscriptions;

            if (!string.IsNullOrWhiteSpace(
                    movieKey))
            {
                subscriptions =
                    subscriptions.Where(subscription =>
                        string.Equals(
                            subscription.MovieKey,
                            movieKey,
                            StringComparison.OrdinalIgnoreCase));
            }

            foreach (CurrentMovieSubscription subscription in
                     subscriptions)
            {
                foreach (DaySchedule schedule in
                         GetCurrentMovieMatchingSchedules(
                             subscription,
                             schedules,
                             DateTime.Now))
                {
                    setting.KnownBookingDateKeys.Add(
                        BuildCurrentMovieDateKey(
                            subscription.MovieKey,
                            schedule.CinemaSlug,
                            schedule.Date));
                }
            }
        }

        private static string BuildCurrentMovieDateKey(
            string movieKey,
            string cinemaSlug,
            DateTime date)
        {
            return movieKey +
                   "|" +
                   cinemaSlug +
                   "|" +
                   date.ToString(
                       "yyyyMMdd",
                       CultureInfo.InvariantCulture);
        }

        private void MarkCurrentMovieDateKnown(
            long userId,
            string notificationKey)
        {
            lock (_currentMoviesLock)
            {
                if (_currentMoviePreferences.TryGetValue(
                        userId,
                        out CurrentMovieUserPreference? setting))
                {
                    setting.KnownBookingDateKeys.Add(
                        notificationKey);
                }
            }
        }

        private async Task EditCinemaNetworksMenuAsync(
                string botToken,
                UserPreference preference,
                int messageId,
                CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildCinemaNetworksText(preference.Language),
                BuildCinemaNetworksKeyboard(preference.Language),
                stoppingToken);
        }

        private static string BuildCinemaNetworksText(string language)
        {
            bool arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
            return arabic
                ? "🎬 <b>اختار شبكة السينمات</b>\n\nاختار الشبكة اللي عايز تتصفحها:"
                : "🎬 <b>Choose a cinema network</b>\n\nChoose the network you want to browse:";
        }

        private static object BuildCinemaNetworksKeyboard(string language)
        {
            bool arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
            return new
            {
                inline_keyboard = new object[][]
                {
                    new object[]
                    {
                        new { text = "🎥 VOX Cinemas", callback_data = "network:vox" }
                    },
                    new object[]
                    {
                        new
                        {
                            text = arabic ? "🎥 الأفلام الحالية" : "🎥 Current Movies",
                            callback_data = "current:menu:0"
                        }
                    },
                    new object[]
                    {
                        new
                        {
                            text = arabic ? "🔜 قريباً" : "🔜 Coming Soon",
                            callback_data = "coming:menu:0"
                        }
                    },
                    new object[]
                    {
                        new
                        {
                            text = arabic ? "⚙️ تنبيهاتي" : "⚙️ My Alerts",
                            callback_data = "setup:edit"
                        }
                    }
                }
            };
        }

        private async Task SendCinemaNetworksMenuAsync(
                string botToken,
                UserPreference preference,
                CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildCinemaNetworksText(preference.Language),
                BuildCinemaNetworksKeyboard(preference.Language),
                stoppingToken);
        }

        private async Task SendBotHomeAsync(
            string botToken,
            UserPreference preference,
            CancellationToken stoppingToken)
        {
            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildBotHomeText(
                    preference.Language),
                BuildBotHomeKeyboard(
                    preference.Language),
                stoppingToken);
        }

        private async Task EditBotHomeAsync(
            string botToken,
            UserPreference preference,
            int messageId,
            CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildBotHomeText(
                    preference.Language),
                BuildBotHomeKeyboard(
                    preference.Language),
                stoppingToken);
        }

        private string BuildBotHomeText(
            string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            return arabic
                ? "🎬 <b>CinemaBot</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "كل القوائم متاحة هنا داخل المحادثة الخاصة، بدون الرجوع إلى الجروب.\n\n" +
                  "🎬 الأفلام الحالية: " +
                  _activeCurrentMovies.Length +
                  "\n🔜 أفلام قريباً: " +
                  _activeComingSoonMovies.Length +
                  "\n\n" +
                  BuildLiveStatusLine(
                      language)
                : "🎬 <b>CinemaBot</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "All movie lists are available here in the private bot chat; the group is not required.\n\n" +
                  "🎬 Current movies: " +
                  _activeCurrentMovies.Length +
                  "\n🔜 Coming Soon: " +
                  _activeComingSoonMovies.Length +
                  "\n\n" +
                  BuildLiveStatusLine(
                      language);
        }

        private static object BuildBotHomeKeyboard(
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
                    new object[]
                    {
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🎬 شبكات السينما"
                                        : "🎬 Cinema Networks",
                                callback_data =
                                    "networks:home"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🎥 أفلام حالية"
                                        : "🎥 Current Movies",
                                callback_data =
                                    "current:menu:0"
                            },
                            new
                            {
                                text =
                                    arabic
                                        ? "🔜 قريباً"
                                        : "🔜 Coming Soon",
                                callback_data =
                                    "coming:menu:0"
                            }
                        },
                        new object[]
                        {
                            new
                            {
                                text =
                                    arabic
                                        ? "🏢 السينمات والمواعيد"
                                        : "🏢 Cinemas & Showtimes",
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
                                        ? "⚙️ تنبيهاتي"
                                        : "⚙️ My Alerts",
                                callback_data =
                                    "setup:edit"
                            }
                        }
                    }
            };
        }

        private void LoadCurrentMoviePreferences()
        {
            try
            {
                if (!TryLoadPersistentJson(
                        Path.GetFileName(CurrentMoviePreferencesPath),
                        CurrentMoviePreferencesPath,
                        out string json))
                {
                    return;
                }

                List<CurrentMovieUserPreference>? stored =
                    JsonSerializer.Deserialize<List<CurrentMovieUserPreference>>(json);

                _currentMoviePreferences =
                    (stored ??
                     new List<CurrentMovieUserPreference>())
                    .ToDictionary(
                        item =>
                            item.UserId,
                        item =>
                        {
                            item.Subscriptions =
                                item.Subscriptions ??
                                new List<CurrentMovieSubscription>();

                            foreach (CurrentMovieSubscription subscription in
                                     item.Subscriptions)
                            {
                                subscription.CinemaSlugs =
                                    new HashSet<string>(
                                        subscription.CinemaSlugs ??
                                        new HashSet<string>(),
                                        StringComparer.OrdinalIgnoreCase);
                            }

                            item.KnownBookingDateKeys =
                                new HashSet<string>(
                                    item.KnownBookingDateKeys ??
                                    new HashSet<string>(),
                                    StringComparer.OrdinalIgnoreCase);

                            return item;
                        });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load current-movie preferences.");
            }
        }

        private void SaveCurrentMoviePreferences()
        {
            try
            {
                List<CurrentMovieUserPreference> snapshot;

                lock (_currentMoviesLock)
                {
                    snapshot =
                        _currentMoviePreferences.Values
                            .Select(item =>
                                item.Clone())
                            .ToList();
                }

                string json =
                    JsonSerializer.Serialize(
                        snapshot,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                SavePersistentJson(
                    Path.GetFileName(CurrentMoviePreferencesPath),
                    CurrentMoviePreferencesPath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save current-movie preferences.");
            }
        }
    }
}
