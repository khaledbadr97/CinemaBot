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
    private async Task EnsureOrUpdateGroupDashboardAsync(
            CinemaOption[] cinemas,
            Dictionary<string, DaySchedule> schedulesByKey,
            List<DayScheduleChange> latestChanges,
            bool isInitialSnapshot,
            string botToken,
            string groupChatId,
            CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(
                    _botUsername))
            {
                return;
            }
    
            long parsedGroupChatId =
                ParseTelegramChatId(
                    groupChatId);
    
            // English live dashboard.
            string dashboardText =
                BuildGroupDashboardText(
                    cinemas,
                    schedulesByKey.Values,
                    latestChanges,
                    isInitialSnapshot);
    
            object dashboardKeyboard =
                BuildGroupDashboardKeyboard(
                    cinemas);
    
            if (_groupDashboardMessageId.HasValue)
            {
                bool edited =
                    await EditTelegramHtmlWithMarkupAsync(
                        botToken,
                        parsedGroupChatId,
                        _groupDashboardMessageId.Value,
                        dashboardText,
                        dashboardKeyboard,
                        stoppingToken);
    
                if (!edited)
                {
                    _groupDashboardMessageId =
                        null;
                }
            }
    
            if (!_groupDashboardMessageId.HasValue)
            {
                int? sentMessageId =
                    await SendTelegramHtmlWithMarkupAndGetMessageIdAsync(
                        botToken,
                        groupChatId,
                        dashboardText,
                        dashboardKeyboard,
                        stoppingToken);
    
                if (sentMessageId.HasValue)
                {
                    _groupDashboardMessageId =
                        sentMessageId.Value;
                }
            }
    
            // Arabic live dashboard. Arabic pages are cached and only missing or
            // changed cinema/date combinations are refreshed.
            Dictionary<string, DaySchedule> arabicSchedulesByKey =
                await GetArabicDashboardSchedulesAsync(
                    schedulesByKey,
                    latestChanges,
                    isInitialSnapshot,
                    stoppingToken);
    
            CinemaOption[] arabicCinemas =
                BuildArabicCinemaOptions(
                    cinemas,
                    arabicSchedulesByKey.Values);
    
            string arabicDashboardText =
                BuildArabicGroupDashboardText(
                    arabicCinemas,
                    schedulesByKey,
                    arabicSchedulesByKey,
                    latestChanges,
                    isInitialSnapshot);
    
            object arabicDashboardKeyboard =
                BuildGroupDashboardKeyboard(
                    arabicCinemas);
    
            if (_groupArabicDashboardMessageId.HasValue)
            {
                bool edited =
                    await EditTelegramHtmlWithMarkupAsync(
                        botToken,
                        parsedGroupChatId,
                        _groupArabicDashboardMessageId.Value,
                        arabicDashboardText,
                        arabicDashboardKeyboard,
                        stoppingToken);
    
                if (!edited)
                {
                    _groupArabicDashboardMessageId =
                        null;
                }
            }
    
            if (!_groupArabicDashboardMessageId.HasValue)
            {
                int? sentArabicMessageId =
                    await SendTelegramHtmlWithMarkupAndGetMessageIdAsync(
                        botToken,
                        groupChatId,
                        arabicDashboardText,
                        arabicDashboardKeyboard,
                        stoppingToken);
    
                if (sentArabicMessageId.HasValue)
                {
                    _groupArabicDashboardMessageId =
                        sentArabicMessageId.Value;
                }
            }
        }

    private async Task<Dictionary<string, DaySchedule>>
            GetArabicDashboardSchedulesAsync(
                Dictionary<string, DaySchedule> englishSchedulesByKey,
                List<DayScheduleChange> latestChanges,
                bool isInitialSnapshot,
                CancellationToken stoppingToken)
        {
            HashSet<string> activeKeys =
                new HashSet<string>(
                    englishSchedulesByKey
                        .Where(item =>
                            item.Value.Movies.Length > 0)
                        .Select(item =>
                            item.Key),
                    StringComparer.OrdinalIgnoreCase);
    
            string[] obsoleteKeys =
                _arabicDashboardSchedulesByKey.Keys
                    .Where(key =>
                        !activeKeys.Contains(
                            key))
                    .ToArray();
    
            foreach (string obsoleteKey in
                     obsoleteKeys)
            {
                _arabicDashboardSchedulesByKey.Remove(
                    obsoleteKey);
            }
    
            HashSet<string> changedKeys =
                new HashSet<string>(
                    latestChanges.Select(change =>
                        FormatStateKey(
                            change.Schedule.CinemaSlug,
                            change.Schedule.Date)),
                    StringComparer.OrdinalIgnoreCase);
    
            KeyValuePair<string, DaySchedule>[] schedulesToRefresh =
                englishSchedulesByKey
                    .Where(item =>
                        item.Value.Movies.Length > 0 &&
                        (isInitialSnapshot ||
                         changedKeys.Contains(
                             item.Key) ||
                         !_arabicDashboardSchedulesByKey.ContainsKey(
                             item.Key)))
                    .ToArray();
    
            int maximumConcurrentArabicRequests =
                ReadIntegerSetting(
                    "Scan:MaximumConcurrentArabicRequests",
                    defaultValue: 2,
                    minimumValue: 1,
                    maximumValue: 4);
    
            using var semaphore =
                new SemaphoreSlim(
                    maximumConcurrentArabicRequests);
    
            Task<ArabicDashboardLoadResult>[] tasks =
                schedulesToRefresh
                    .Select(item =>
                        LoadArabicDashboardScheduleAsync(
                            item.Key,
                            item.Value,
                            semaphore,
                            stoppingToken))
                    .ToArray();
    
            ArabicDashboardLoadResult[] results =
                await Task.WhenAll(
                    tasks);
    
            foreach (ArabicDashboardLoadResult result in
                     results)
            {
                if (result.Schedule is not null)
                {
                    _arabicDashboardSchedulesByKey[result.Key] =
                        result.Schedule;
                }
            }
    
            var activeArabicSchedules =
                new Dictionary<string, DaySchedule>(
                    StringComparer.OrdinalIgnoreCase);
    
            foreach (KeyValuePair<string, DaySchedule> item in
                     englishSchedulesByKey)
            {
                if (item.Value.Movies.Length == 0)
                {
                    continue;
                }
    
                activeArabicSchedules[item.Key] =
                    _arabicDashboardSchedulesByKey.TryGetValue(
                        item.Key,
                        out DaySchedule? arabicSchedule)
                        ? arabicSchedule
                        : item.Value;
            }
    
            return activeArabicSchedules;
        }

    private async Task<ArabicDashboardLoadResult>
            LoadArabicDashboardScheduleAsync(
                string key,
                DaySchedule englishSchedule,
                SemaphoreSlim semaphore,
                CancellationToken stoppingToken)
        {
            await semaphore.WaitAsync(
                stoppingToken);
    
            try
            {
                DaySchedule? arabicSchedule =
                    await TryLoadArabicScheduleAsync(
                        englishSchedule,
                        stoppingToken);
    
                return new ArabicDashboardLoadResult(
                    key,
                    arabicSchedule);
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
                    "Could not load the Arabic dashboard schedule for " +
                    "{Cinema} on {Date}. The English data will be used temporarily.",
                    englishSchedule.CinemaName,
                    englishSchedule.Date.ToString(
                        "dd MMMM yyyy",
                        CultureInfo.InvariantCulture));
    
                return new ArabicDashboardLoadResult(
                    key,
                    null);
            }
            finally
            {
                semaphore.Release();
            }
        }

    private static CinemaOption[] BuildArabicCinemaOptions(
            CinemaOption[] englishCinemas,
            IEnumerable<DaySchedule> arabicSchedules)
        {
            DaySchedule[] localizedSchedules =
                arabicSchedules.ToArray();
    
            return englishCinemas
                .Select(cinema =>
                {
                    string extractedName =
                        localizedSchedules
                            .Where(schedule =>
                                string.Equals(
                                    schedule.CinemaSlug,
                                    cinema.Slug,
                                    StringComparison.OrdinalIgnoreCase))
                            .Select(schedule =>
                                schedule.CinemaName)
                            .FirstOrDefault(name =>
                                !string.IsNullOrWhiteSpace(
                                    name)) ??
                        cinema.Name;
    
                    return new CinemaOption(
                        cinema.Slug,
                        GetArabicCinemaDisplayName(
                            cinema.Slug,
                            extractedName,
                            cinema.Name));
                })
                .OrderBy(cinema =>
                    cinema.Name,
                    StringComparer.CurrentCulture)
                .ToArray();
        }

    private static string GetArabicCinemaDisplayName(
            string cinemaSlug,
            string extractedName,
            string fallbackEnglishName)
        {
            if (ContainsArabicCharacters(
                    extractedName))
            {
                return extractedName;
            }
    
            var knownNames =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["mall-of-egypt"] =
                        "مول مصر",
    
                    ["city-centre-almaza"] =
                        "سيتي سنتر ألماظة",
    
                    ["city-centre-alexandria"] =
                        "سيتي سنتر الإسكندرية",
    
                    ["city-centre-madinaty"] =
                        "سيتي سنتر مدينتي",
    
                    ["city-centre-maadi"] =
                        "سيتي سنتر المعادي",
    
                    ["city-centre-hurghada"] =
                        "سيتي سنتر الغردقة"
                };
    
            if (knownNames.TryGetValue(
                    cinemaSlug,
                    out string? knownName))
            {
                return knownName;
            }
    
            return string.IsNullOrWhiteSpace(
                       extractedName)
                ? fallbackEnglishName
                : extractedName;
        }

    private static DaySchedule DeduplicateLocalizedSchedule(
            DaySchedule englishSchedule,
            DaySchedule localizedSchedule)
        {
            var remainingLocalized =
                localizedSchedule.Movies
                    .Select((movie, index) =>
                        new
                        {
                            Movie =
                                movie,
    
                            Index =
                                index,
    
                            Fingerprint =
                                BuildMovieIdentityFingerprint(
                                    movie)
                        })
                    .ToList();
    
            var result =
                new List<MovieSchedule>();
    
            for (int englishIndex = 0;
                 englishIndex < englishSchedule.Movies.Length;
                 englishIndex++)
            {
                MovieSchedule englishMovie =
                    englishSchedule.Movies[englishIndex];
    
                string englishFingerprint =
                    BuildMovieIdentityFingerprint(
                        englishMovie);
    
                var matching =
                    remainingLocalized
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(
                                englishFingerprint) &&
                            string.Equals(
                                item.Fingerprint,
                                englishFingerprint,
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item =>
                            ContainsArabicCharacters(
                                item.Movie.Title))
                        .ThenByDescending(item =>
                            item.Movie.Halls.Sum(hall =>
                                hall.Showtimes.Length))
                        .FirstOrDefault();
    
                if (matching is null &&
                    englishIndex <
                    remainingLocalized.Count)
                {
                    matching =
                        remainingLocalized[englishIndex];
                }
    
                MovieSchedule localizedMovie =
                    matching?.Movie ??
                    englishMovie;
    
                string displayTitle =
                    ContainsArabicCharacters(
                        localizedMovie.Title)
                        ? localizedMovie.Title
                        : englishMovie.Title;
    
                result.Add(
                    new MovieSchedule(
                        displayTitle,
                        localizedMovie.Halls));
    
                if (matching is not null)
                {
                    remainingLocalized.RemoveAll(item =>
                        (!string.IsNullOrWhiteSpace(
                             matching.Fingerprint) &&
                         string.Equals(
                             item.Fingerprint,
                             matching.Fingerprint,
                             StringComparison.OrdinalIgnoreCase)) ||
                        item.Index ==
                        matching.Index);
                }
            }
    
            foreach (var extraGroup in
                     remainingLocalized
                         .GroupBy(item =>
                             string.IsNullOrWhiteSpace(
                                 item.Fingerprint)
                                 ? NormalizeMovieTitleKey(
                                     item.Movie.Title)
                                 : item.Fingerprint,
                             StringComparer.OrdinalIgnoreCase))
            {
                MovieSchedule preferred =
                    extraGroup
                        .OrderByDescending(item =>
                            ContainsArabicCharacters(
                                item.Movie.Title))
                        .ThenByDescending(item =>
                            item.Movie.Halls.Sum(hall =>
                                hall.Showtimes.Length))
                        .Select(item =>
                            item.Movie)
                        .First();
    
                result.Add(
                    preferred);
            }
    
            return new DaySchedule(
                englishSchedule.CinemaSlug,
                GetArabicCinemaDisplayName(
                    englishSchedule.CinemaSlug,
                    localizedSchedule.CinemaName,
                    englishSchedule.CinemaName),
                englishSchedule.Date,
                localizedSchedule.Url,
                result
                    .GroupBy(movie =>
                        BuildMovieIdentityFingerprint(
                            movie),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group
                            .OrderByDescending(movie =>
                                ContainsArabicCharacters(
                                    movie.Title))
                            .First())
                    .ToArray());
        }

    private static MovieSchedule? FindMatchingLocalizedMovie(
            MovieSchedule englishMovie,
            MovieSchedule[] localizedMovies,
            int preferredIndex)
        {
            string fingerprint =
                BuildMovieIdentityFingerprint(
                    englishMovie);
    
            MovieSchedule? byFingerprint =
                localizedMovies
                    .Where(movie =>
                        !string.IsNullOrWhiteSpace(
                            fingerprint) &&
                        string.Equals(
                            BuildMovieIdentityFingerprint(
                                movie),
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(movie =>
                        ContainsArabicCharacters(
                            movie.Title))
                    .FirstOrDefault();
    
            if (byFingerprint is not null)
            {
                return byFingerprint;
            }
    
            return preferredIndex >= 0 &&
                   preferredIndex <
                   localizedMovies.Length
                ? localizedMovies[preferredIndex]
                : null;
        }

    private static string BuildMovieIdentityFingerprint(
            MovieSchedule movie)
        {
            string[] identifiers =
                movie.Halls
                    .SelectMany(hall =>
                        hall.Showtimes)
                    .Select(showtime =>
                        showtime.Identifier)
                    .Where(identifier =>
                        !string.IsNullOrWhiteSpace(
                            identifier))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(identifier =>
                        identifier,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    
            if (identifiers.Length > 0)
            {
                return "id:" +
                       string.Join(
                           "|",
                           identifiers);
            }
    
            string[] times =
                movie.Halls
                    .SelectMany(hall =>
                        hall.Showtimes)
                    .Select(showtime =>
                        NormalizeMovieTitleKey(
                            showtime.Time))
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(
                            value))
                    .OrderBy(value =>
                        value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    
            return times.Length == 0
                ? "title:" +
                  NormalizeMovieTitleKey(
                      movie.Title)
                : "times:" +
                  string.Join(
                      "|",
                      times);
        }

    private static long ParseTelegramChatId(
            string chatId)
        {
            if (long.TryParse(
                    chatId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long parsedChatId))
            {
                return parsedChatId;
            }
    
            throw new InvalidOperationException(
                "Telegram ChatId must be a numeric chat ID.");
        }

    private string BuildGroupDashboardText(
            CinemaOption[] cinemas,
            IEnumerable<DaySchedule> schedules,
            List<DayScheduleChange> latestChanges,
            bool isInitialSnapshot)
        {
            DaySchedule[] activeSchedules =
                schedules
                    .Where(schedule =>
                        schedule.Movies.Length > 0)
                    .OrderBy(schedule =>
                        schedule.Date)
                    .ThenBy(schedule =>
                        schedule.CinemaName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    
            CinemaOption[] activeCinemas =
                cinemas
                    .Where(cinema =>
                        activeSchedules.Any(schedule =>
                            string.Equals(
                                schedule.CinemaSlug,
                                cinema.Slug,
                                StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
    
            int activeDateCount =
                activeSchedules
                    .Select(schedule =>
                        schedule.Date.Date)
                    .Distinct()
                    .Count();
    
            string[] allMovieTitles =
                activeSchedules
                    .SelectMany(schedule =>
                        schedule.Movies)
                    .Select(movie =>
                        movie.Title)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(title =>
                        title,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    
            var builder =
                new StringBuilder();
    
            builder.AppendLine(
                "🎬 <b>CINEMABOT — VOX RADAR</b>");
            builder.AppendLine(
                "━━━━━━━━━━━━━━━━━━━━");
            builder.AppendLine(
                "🟢 <b>LIVE DASHBOARD — ENGLISH</b>");
            builder.AppendLine(
                "🕒 Updated: <b>" +
                EscapeTelegramHtml(
                    DateTime.Now.ToString(
                        "dddd - d MMM yyyy, hh:mm tt",
                        CultureInfo.InvariantCulture)) +
                "</b>");
            builder.AppendLine(
                "🏢 " +
                activeCinemas.Length +
                " cinemas  •  📅 " +
                activeDateCount +
                " active days  •  🎞 " +
                allMovieTitles.Length +
                " movies");
    
            builder.AppendLine();
            builder.AppendLine(
                "🏢 <b>Available Cinemas</b>");
    
            foreach (CinemaOption cinema in
                     activeCinemas)
            {
                builder.AppendLine(
                    "• " +
                    EscapeTelegramHtml(
                        cinema.Name));
            }
    
            builder.AppendLine();
            builder.AppendLine(
                "🎞 <b>All movies currently listed</b>");
    
            AppendAllMovieTitles(
                builder,
                allMovieTitles,
                "No movies are currently listed.");
    
            builder.AppendLine();
            builder.AppendLine(
                "🔜 <b>Coming Soon</b>");
            builder.AppendLine(
                _activeComingSoonMovies.Length +
                " Movies");
            builder.AppendLine(
                "👇 Open Menu from the button below.");
    
            AppendDashboardLatestUpdate(
                builder,
                latestChanges,
                isInitialSnapshot);
    
            builder.AppendLine();
            builder.AppendLine(
                "👇 Choose a cinema or open Coming Soon from the buttons.");
            builder.AppendLine(
                "This dashboard refreshes automatically every 60 seconds.");
    
            return builder.ToString();
        }

    private string BuildArabicGroupDashboardText(
            CinemaOption[] cinemas,
            Dictionary<string, DaySchedule> englishSchedulesByKey,
            Dictionary<string, DaySchedule> arabicSchedulesByKey,
            List<DayScheduleChange> latestChanges,
            bool isInitialSnapshot)
        {
            DaySchedule[] activeSchedules =
                arabicSchedulesByKey.Values
                    .Where(schedule =>
                        schedule.Movies.Length > 0)
                    .OrderBy(schedule =>
                        schedule.Date)
                    .ThenBy(schedule =>
                        schedule.CinemaName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    
            int activeDateCount =
                activeSchedules
                    .Select(schedule =>
                        schedule.Date.Date)
                    .Distinct()
                    .Count();
    
            string[] allMovieTitles =
                BuildArabicDashboardMovieTitles(
                    englishSchedulesByKey,
                    arabicSchedulesByKey);
    
            CinemaOption[] activeCinemas =
                cinemas
                    .Where(cinema =>
                        activeSchedules.Any(schedule =>
                            string.Equals(
                                schedule.CinemaSlug,
                                cinema.Slug,
                                StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
    
            var builder =
                new StringBuilder();
    
            builder.AppendLine(
                "\u200F🎬 <b>رادار ڤوكس سينما</b>");
    
            builder.AppendLine(
                "━━━━━━━━━━━━━━━━━━━━");
    
            builder.AppendLine(
                "\u200F🟢 <b>لوحة العرض المباشر — العربية</b>");
    
            builder.AppendLine(
                "\u200F🕒 آخر تحديث: <b>" +
                EscapeTelegramHtml(
                    FormatArabicDashboardDateTime(
                        DateTime.Now)) +
                "</b>");
    
            builder.AppendLine(
                "\u200F🏢 " +
                activeCinemas.Length +
                " سينمات  •  📅 " +
                activeDateCount +
                " أيام متاحة  •  🎞 " +
                allMovieTitles.Length +
                " أفلام");
    
            if (activeCinemas.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(
                    "\u200F🏢 <b>السينمات المتاحة</b>");
    
                foreach (CinemaOption cinema in
                         activeCinemas)
                {
                    builder.AppendLine(
                        "\u200F• " +
                        FormatArabicBidiText(
                            GetArabicCinemaDisplayName(
                                cinema.Slug,
                                cinema.Name,
                                cinema.Name)));
                }
            }
    
            builder.AppendLine();
            builder.AppendLine(
                "\u200F🎞 <b>كل الأفلام المعروضة حالياً</b>");
    
            AppendArabicMovieTitles(
                builder,
                allMovieTitles,
                emptyText:
                    "لا توجد أفلام معروضة حالياً.");
    
            builder.AppendLine();
            builder.AppendLine(
                "\u200F🔜 <b>يعرض قريباً</b>");
            builder.AppendLine(
                "\u200F" +
                _activeComingSoonMovies.Length +
                " فيلم");
            builder.AppendLine(
                "\u200F👇 افتح القائمة من الزر بالأسفل.");
    
            AppendArabicDashboardLatestUpdate(
                builder,
                englishSchedulesByKey,
                arabicSchedulesByKey,
                latestChanges,
                isInitialSnapshot);
    
            builder.AppendLine();
            builder.AppendLine(
                "\u200F👇 اختر السينما، ثم اليوم، ثم الفيلم من الأزرار.");
    
            builder.AppendLine(
                "\u200Fيتم تحديث نفس اللوحة تلقائياً كل 60 ثانية.");
    
            return builder.ToString();
        }

    private static void AppendAllMovieTitles(
            StringBuilder builder,
            string[] allMovieTitles,
            string emptyText)
        {
            if (allMovieTitles.Length == 0)
            {
                builder.AppendLine(
                    EscapeTelegramHtml(
                        emptyText));
    
                return;
            }
    
            for (int index = 0;
                 index < allMovieTitles.Length;
                 index++)
            {
                builder.AppendLine(
                    "🎞 <b>" +
                    (index + 1).ToString(
                        "00",
                        CultureInfo.InvariantCulture) +
                    ".</b>  " +
                    EscapeTelegramHtml(
                        allMovieTitles[index]));
            }
        }

    private static void AppendArabicMovieTitles(
            StringBuilder builder,
            string[] allMovieTitles,
            string emptyText)
        {
            if (allMovieTitles.Length == 0)
            {
                builder.AppendLine(
                    "\u200F" +
                    EscapeTelegramHtml(
                        emptyText));
    
                return;
            }
    
            for (int index = 0;
                 index < allMovieTitles.Length;
                 index++)
            {
                builder.AppendLine(
                    "\u200F🎞 <b>" +
                    (index + 1).ToString(
                        "00",
                        CultureInfo.InvariantCulture) +
                    ".</b>  " +
                    FormatArabicBidiText(
                        allMovieTitles[index]));
            }
        }

    private static string[] BuildArabicDashboardMovieTitles(
            Dictionary<string, DaySchedule> englishSchedulesByKey,
            Dictionary<string, DaySchedule> arabicSchedulesByKey)
        {
            var preferredTitles =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
    
            foreach (KeyValuePair<string, DaySchedule> englishItem in
                     englishSchedulesByKey)
            {
                if (englishItem.Value.Movies.Length == 0)
                {
                    continue;
                }
    
                DaySchedule localized =
                    arabicSchedulesByKey.TryGetValue(
                        englishItem.Key,
                        out DaySchedule? arabicSchedule)
                        ? arabicSchedule
                        : englishItem.Value;
    
                for (int index = 0;
                     index < englishItem.Value.Movies.Length;
                     index++)
                {
                    MovieSchedule englishMovie =
                        englishItem.Value.Movies[index];
    
                    MovieSchedule? localizedMovie =
                        FindMatchingLocalizedMovie(
                            englishMovie,
                            localized.Movies,
                            index);
    
                    string displayTitle =
                        localizedMovie is not null &&
                        ContainsArabicCharacters(
                            localizedMovie.Title)
                            ? localizedMovie.Title
                            : englishMovie.Title;
    
                    string key =
                        NormalizeMovieTitleKey(
                            englishMovie.Title);
    
                    if (!preferredTitles.TryGetValue(
                            key,
                            out string? existing) ||
                        (!ContainsArabicCharacters(
                             existing) &&
                         ContainsArabicCharacters(
                             displayTitle)))
                    {
                        preferredTitles[key] =
                            displayTitle;
                    }
                }
            }
    
            return preferredTitles.Values
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(title =>
                    title,
                    StringComparer.CurrentCulture)
                .ToArray();
        }

    private static string FormatArabicBidiText(
            string value)
        {
            string escaped =
                EscapeTelegramHtml(
                    value);
    
            if (ContainsArabicCharacters(
                    value))
            {
                return "\u200F" +
                       escaped;
            }
    
            return "\u200F\u2066" +
                   escaped +
                   "\u2069";
        }

    private static bool ContainsArabicCharacters(
            string value)
        {
            return value.Any(character =>
                (character >= '\u0600' &&
                 character <= '\u06FF') ||
                (character >= '\u0750' &&
                 character <= '\u077F') ||
                (character >= '\u08A0' &&
                 character <= '\u08FF'));
        }

    private static string FormatArabicDashboardDateTime(
            DateTime value)
        {
            string[] dayNames =
            {
                "الأحد",
                "الاثنين",
                "الثلاثاء",
                "الأربعاء",
                "الخميس",
                "الجمعة",
                "السبت"
            };
    
            string[] monthNames =
            {
                "يناير",
                "فبراير",
                "مارس",
                "أبريل",
                "مايو",
                "يونيو",
                "يوليو",
                "أغسطس",
                "سبتمبر",
                "أكتوبر",
                "نوفمبر",
                "ديسمبر"
            };
    
            int hour12 =
                value.Hour % 12;
    
            if (hour12 == 0)
            {
                hour12 =
                    12;
            }
    
            string period =
                value.Hour < 12
                    ? "صباحاً"
                    : "مساءً";
    
            return dayNames[(int)value.DayOfWeek] +
                   " - " +
                   value.Day.ToString(
                       CultureInfo.InvariantCulture) +
                   " " +
                   monthNames[value.Month - 1] +
                   " " +
                   value.Year.ToString(
                       CultureInfo.InvariantCulture) +
                   " - " +
                   hour12.ToString(
                       "00",
                       CultureInfo.InvariantCulture) +
                   ":" +
                   value.Minute.ToString(
                       "00",
                       CultureInfo.InvariantCulture) +
                   " " +
                   period;
        }

    private static string FormatArabicDate(
            DateTime value)
        {
            string full =
                FormatArabicDashboardDateTime(
                    value);
    
            int lastSeparator =
                full.LastIndexOf(
                    " - ",
                    StringComparison.Ordinal);
    
            return lastSeparator > 0
                ? full.Substring(
                    0,
                    lastSeparator)
                : full;
        }

    private static void AppendDashboardLatestUpdate(
            StringBuilder builder,
            List<DayScheduleChange> latestChanges,
            bool isInitialSnapshot)
        {
            builder.AppendLine();
            builder.AppendLine(
                "⚡ <b>Latest update</b>");
    
            if (isInitialSnapshot)
            {
                builder.AppendLine(
                    "✅ The live schedule is ready.");
                return;
            }
    
            if (latestChanges.Count == 0)
            {
                builder.AppendLine(
                    "✅ No new changes in the latest scan.");
                return;
            }
    
            var visibleChanges =
                latestChanges
                    .SelectMany(day =>
                        day.Changes.Select(change =>
                            new
                            {
                                Schedule =
                                    day.Schedule,
    
                                Change =
                                    change
                            }))
                    .Take(5)
                    .ToArray();
    
            foreach (var item in
                     visibleChanges)
            {
                builder.AppendLine(
                    BuildDashboardChangeLine(
                        item.Change,
                        item.Schedule));
            }
    
            int totalChanges =
                latestChanges.Sum(day =>
                    day.Changes.Length);
    
            if (totalChanges >
                visibleChanges.Length)
            {
                builder.AppendLine(
                    "• " +
                    (totalChanges -
                     visibleChanges.Length) +
                    " additional changes — open a cinema to review.");
            }
        }

    private static void AppendArabicDashboardLatestUpdate(
            StringBuilder builder,
            Dictionary<string, DaySchedule> englishSchedulesByKey,
            Dictionary<string, DaySchedule> arabicSchedulesByKey,
            List<DayScheduleChange> latestChanges,
            bool isInitialSnapshot)
        {
            builder.AppendLine();
            builder.AppendLine(
                "\u200F⚡ <b>آخر التغييرات</b>");
    
            if (isInitialSnapshot)
            {
                builder.AppendLine(
                    "\u200F✅ لوحة العرض جاهزة.");
                return;
            }
    
            if (latestChanges.Count == 0)
            {
                builder.AppendLine(
                    "\u200F✅ لا توجد تغييرات جديدة في آخر فحص.");
                return;
            }
    
            var visibleChanges =
                latestChanges
                    .SelectMany(day =>
                        day.Changes.Select(change =>
                            new
                            {
                                Schedule =
                                    day.Schedule,
    
                                Change =
                                    change
                            }))
                    .Take(5)
                    .ToArray();
    
            foreach (var item in
                     visibleChanges)
            {
                string key =
                    FormatStateKey(
                        item.Schedule.CinemaSlug,
                        item.Schedule.Date);
    
                DaySchedule displaySchedule =
                    arabicSchedulesByKey.TryGetValue(
                        key,
                        out DaySchedule? arabicSchedule)
                        ? arabicSchedule
                        : item.Schedule;
    
                string cinemaName =
                    GetArabicCinemaDisplayName(
                        item.Schedule.CinemaSlug,
                        displaySchedule.CinemaName,
                        item.Schedule.CinemaName);
    
                builder.AppendLine();
                builder.AppendLine(
                    "\u200F🏢 <b>" +
                    FormatArabicBidiText(
                        cinemaName) +
                    "</b>");
    
                builder.AppendLine(
                    "\u200F📅 " +
                    EscapeTelegramHtml(
                        FormatArabicDate(
                            item.Schedule.Date)));
    
                builder.AppendLine(
                    "\u200F" +
                    BuildArabicDashboardChangeLine(
                        item.Change,
                        item.Schedule,
                        displaySchedule));
            }
    
            int totalChanges =
                latestChanges.Sum(day =>
                    day.Changes.Length);
    
            if (totalChanges >
                visibleChanges.Length)
            {
                builder.AppendLine();
                builder.AppendLine(
                    "\u200F• توجد " +
                    (totalChanges -
                     visibleChanges.Length) +
                    " تغييرات إضافية — افتح السينما لمراجعتها.");
            }
        }

    private static string BuildArabicDashboardChangeLine(
            ScheduleChange change,
            DaySchedule originalSchedule,
            DaySchedule displaySchedule)
        {
            string icon =
                change.Type switch
                {
                    ScheduleChangeType.NewMovie =>
                        "🆕",
    
                    ScheduleChangeType.SoldOut =>
                        "🔴",
    
                    ScheduleChangeType.AvailableAgain =>
                        "🟢",
    
                    ScheduleChangeType.NewShowtime =>
                        "🕒",
    
                    ScheduleChangeType.ShowtimeRemoved =>
                        "➖",
    
                    ScheduleChangeType.NewHall =>
                        "🏛",
    
                    ScheduleChangeType.MovieRemoved =>
                        "🗑",
    
                    _ =>
                        "⚡"
                };
    
            string movieTitle =
                GetLocalizedMovieTitle(
                    change.MovieTitle,
                    originalSchedule,
                    displaySchedule);
    
            string changeText =
                change.Type switch
                {
                    ScheduleChangeType.NewMovie =>
                        "فيلم جديد",
    
                    ScheduleChangeType.MovieRemoved =>
                        "تم حذف الفيلم",
    
                    ScheduleChangeType.NewHall =>
                        "قاعة جديدة",
    
                    ScheduleChangeType.HallRemoved =>
                        "تم حذف قاعة",
    
                    ScheduleChangeType.NewShowtime =>
                        "موعد جديد",
    
                    ScheduleChangeType.ShowtimeRemoved =>
                        "تم حذف موعد",
    
                    ScheduleChangeType.SoldOut =>
                        "الحجز اكتمل",
    
                    ScheduleChangeType.AvailableAgain =>
                        "الحجز متاح مرة أخرى",
    
                    ScheduleChangeType.BookingLinkAdded =>
                        "تمت إضافة رابط الحجز",
    
                    ScheduleChangeType.BookingLinkRemoved =>
                        "تم حذف رابط الحجز",
    
                    ScheduleChangeType.BookingLinkChanged =>
                        "تم تغيير رابط الحجز",
    
                    _ =>
                        "تغيير جديد"
                };
    
            var details =
                new List<string>();
    
            if (!string.IsNullOrWhiteSpace(
                    change.HallName))
            {
                details.Add(
                    GetLocalizedHallName(
                        change.MovieTitle,
                        change.HallName,
                        originalSchedule,
                        displaySchedule));
            }
    
            if (!string.IsNullOrWhiteSpace(
                    change.Time))
            {
                details.Add(
                    GetLocalizedShowtimeText(
                        change.MovieTitle,
                        change.HallName,
                        change.Time,
                        originalSchedule,
                        displaySchedule));
            }
    
            string suffix =
                details.Count == 0
                    ? string.Empty
                    : " • " +
                      string.Join(
                          " • ",
                          details.Select(
                              FormatArabicBidiText));
    
            return icon +
                   " <b>" +
                   FormatArabicBidiText(
                       movieTitle) +
                   "</b>\n\u200F   " +
                   changeText +
                   suffix;
        }

    private static string BuildDashboardChangeLine(
            ScheduleChange change,
            DaySchedule schedule)
        {
            string icon =
                change.Type switch
                {
                    ScheduleChangeType.NewMovie =>
                        "🆕",
    
                    ScheduleChangeType.SoldOut =>
                        "🔴",
    
                    ScheduleChangeType.AvailableAgain =>
                        "🟢",
    
                    ScheduleChangeType.NewShowtime =>
                        "🕒",
    
                    ScheduleChangeType.NewHall =>
                        "🏛",
    
                    ScheduleChangeType.MovieRemoved =>
                        "🗑",
    
                    _ =>
                        "⚡"
                };
    
            string suffix =
                string.Empty;
    
            if (!string.IsNullOrWhiteSpace(
                    change.HallName))
            {
                suffix +=
                    " — " +
                    EscapeTelegramHtml(
                        change.HallName);
            }
    
            if (!string.IsNullOrWhiteSpace(
                    change.Time))
            {
                suffix +=
                    " — " +
                    EscapeTelegramHtml(
                        change.Time);
            }
    
            return
                icon +
                " " +
                EscapeTelegramHtml(
                    change.MovieTitle) +
                " — " +
                EscapeTelegramHtml(
                    schedule.CinemaName) +
                " — " +
                schedule.Date.ToString(
                    "dd MMM",
                    CultureInfo.InvariantCulture) +
                suffix;
        }

    private object BuildGroupDashboardKeyboard(
            CinemaOption[] cinemas)
        {
            var rows =
                new List<object>();
    
            for (int index = 0;
                 index < cinemas.Length;
                 index += 2)
            {
                var row =
                    new List<object>();
    
                CinemaOption first =
                    cinemas[index];
    
                row.Add(
                    new
                    {
                        text =
                            "🏢 " +
                            first.Name,
    
                        url =
                            BuildBotStartUrl(
                                "cin_" +
                                first.Slug)
                    });
    
                if (index + 1 <
                    cinemas.Length)
                {
                    CinemaOption second =
                        cinemas[index + 1];
    
                    row.Add(
                        new
                        {
                            text =
                                "🏢 " +
                                second.Name,
    
                            url =
                                BuildBotStartUrl(
                                    "cin_" +
                                    second.Slug)
                        });
                }
    
                rows.Add(
                    row.ToArray());
            }
    
            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            "🔜 Coming Soon (" +
                            _activeComingSoonMovies.Length +
                            ") | قريباً",
    
                        url =
                            BuildBotStartUrl(
                                "comingsoon")
                    }
                });
    
            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            "🎬 Browse All | تصفح الكل",
    
                        url =
                            BuildBotStartUrl(
                                "browse")
                    },
    
                    new
                    {
                        text =
                            "⚙️ My Alerts | تنبيهاتي",
    
                        url =
                            BuildBotStartUrl(
                                "setup")
                    }
                });
    
            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

    private string BuildBotStartUrl(
            string startParameter)
        {
            return
                "https://t.me/" +
                _botUsername +
                "?start=" +
                Uri.EscapeDataString(
                    startParameter);
        }

    private async Task<int?> SendTelegramHtmlWithMarkupAndGetMessageIdAsync(
            string botToken,
            string chatId,
            string message,
            object replyMarkup,
            CancellationToken stoppingToken)
        {
            string apiUrl =
                $"https://api.telegram.org/bot{botToken}/sendMessage";
    
            int retryAfterBufferSeconds =
                ReadIntegerSetting(
                    "Telegram:RetryAfterBufferSeconds",
                    defaultValue: 3,
                    minimumValue: 1,
                    maximumValue: 15);
    
            await _telegramMessageGate.WaitAsync(
                stoppingToken);
    
            try
            {
                await WaitForTelegramMessageWindowAsync(
                    chatId,
                    stoppingToken);
    
                while (!stoppingToken.IsCancellationRequested)
                {
                    string json =
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
                                    true,
    
                                reply_markup =
                                    replyMarkup
                            });
    
                    using var content =
                        new StringContent(
                            json,
                            Encoding.UTF8,
                            "application/json");
    
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
                        RegisterTelegramMessageSent(
                            chatId);
    
                        using JsonDocument document =
                            JsonDocument.Parse(
                                responseBody);
    
                        if (document.RootElement.TryGetProperty(
                                "result",
                                out JsonElement result) &&
                            result.TryGetProperty(
                                "message_id",
                                out JsonElement messageIdElement))
                        {
                            return
                                messageIdElement.GetInt32();
                        }
    
                        return null;
                    }
    
                    if ((int)response.StatusCode ==
                        429)
                    {
                        int waitSeconds =
                            TryReadTelegramRetryAfterSeconds(
                                responseBody) +
                            retryAfterBufferSeconds;
    
                        _logger.LogInformation(
                            "Telegram requested a temporary dashboard pause. " +
                            "Retrying automatically after {WaitSeconds} seconds.",
                            waitSeconds);
    
                        await Task.Delay(
                            TimeSpan.FromSeconds(
                                waitSeconds),
                            stoppingToken);
    
                        continue;
                    }
    
                    _logger.LogError(
                        "Telegram dashboard send failed. HTTP {StatusCode}: {Response}",
                        (int)response.StatusCode,
                        LimitText(
                            responseBody,
                            1000));
    
                    return null;
                }
    
                return null;
            }
            finally
            {
                _telegramMessageGate.Release();
            }
        }
    }
}
