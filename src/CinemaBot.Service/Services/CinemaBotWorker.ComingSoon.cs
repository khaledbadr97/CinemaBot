using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    public partial class CinemaBotWorker
    {
        private readonly object _comingSoonLock =
            new object();

        private readonly HashSet<string> _pendingComingSoonEarlyBookingAlertKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ComingSoonMovie[] _activeComingSoonMovies =
            Array.Empty<ComingSoonMovie>();

        private Dictionary<string, ComingSoonMovie> _knownComingSoonMovies =
            new Dictionary<string, ComingSoonMovie>(
                StringComparer.OrdinalIgnoreCase);

        private HashSet<string> _releasedComingSoonKeys =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        private HashSet<string> _latestListedComingSoonKeys =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        private Dictionary<long, ComingSoonUserPreference> _comingSoonPreferences =
            new Dictionary<long, ComingSoonUserPreference>();

        private DateTime _lastComingSoonScanUtc =
            DateTime.MinValue;

        private bool _comingSoonStateFileExisted;

        private string ComingSoonStatePath =>
            Path.Combine(
                _dataDirectory,
                "coming-soon-state.json");

        private string ComingSoonPreferencesPath =>
            Path.Combine(
                _dataDirectory,
                "coming-soon-user-preferences.json");

        private async Task ScanComingSoonIfDueAsync(
            Dictionary<string, DaySchedule> currentSchedules,
            string botToken,
            string groupChatId,
            bool telegramAvailable,
            CancellationToken stoppingToken)
        {
            if (!ReadBooleanSetting(
                    "ComingSoon:Enabled",
                    defaultValue: true))
            {
                _activeComingSoonMovies =
                    Array.Empty<ComingSoonMovie>();
                return;
            }

            int intervalMinutes =
                ReadIntegerSetting(
                    "ComingSoon:ScanIntervalMinutes",
                    defaultValue: 30,
                    minimumValue: 5,
                    maximumValue: 720);

            bool pageRefreshDue =
                _lastComingSoonScanUtc ==
                DateTime.MinValue ||
                DateTime.UtcNow -
                _lastComingSoonScanUtc >=
                TimeSpan.FromMinutes(
                    intervalMinutes);

            if (!pageRefreshDue)
            {
                await RevalidateComingSoonAgainstShowtimesAsync(
                    currentSchedules,
                    botToken,
                    telegramAvailable,
                    stoppingToken);

                return;
            }

            string englishUrl =
                (_configuration["ComingSoon:EnglishUrl"] ??
                 "https://egy.voxcinemas.com/movies/comingsoon")
                .Trim();

            string arabicUrl =
                (_configuration["ComingSoon:ArabicUrl"] ??
                 "https://egy.voxcinemas.com/ar/movies/comingsoon")
                .Trim();

            string? englishHtml =
                await DownloadVoxPageAsync(
                    englishUrl,
                    stoppingToken);

            if (string.IsNullOrWhiteSpace(
                    englishHtml))
            {
                _logger.LogWarning(
                    "Coming Soon English page was unavailable. The previous list is preserved.");

                SetHealthStatus(
                    "Degraded",
                    "VOX Coming Soon page is unavailable.");

                return;
            }

            string? arabicHtml =
                await DownloadVoxPageAsync(
                    arabicUrl,
                    stoppingToken);

            ComingSoonMovie[] englishMovies =
                ExtractComingSoonMovies(
                    englishHtml,
                    englishUrl,
                    isArabic: false);

            ComingSoonMovie[] arabicMovies =
                string.IsNullOrWhiteSpace(
                    arabicHtml)
                    ? Array.Empty<ComingSoonMovie>()
                    : ExtractComingSoonMovies(
                        arabicHtml,
                        arabicUrl,
                        isArabic: true);

            Dictionary<string, ComingSoonMovie> arabicByKey =
                arabicMovies
                    .GroupBy(movie =>
                        movie.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group =>
                            group.Key,
                        group =>
                            group.First(),
                        StringComparer.OrdinalIgnoreCase);

            ComingSoonMovie[] combined =
                englishMovies
                    .GroupBy(movie =>
                        NormalizeMovieTitleKey(
                            movie.EnglishTitle) +
                        "|" +
                        (movie.ReleaseDate?.ToString(
                             "yyyyMMdd",
                             CultureInfo.InvariantCulture) ??
                         "unknown"),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .Select((movie, index) =>
                    {
                        ComingSoonMovie? arabicMovie =
                            arabicByKey.TryGetValue(
                                movie.Key,
                                out ComingSoonMovie? exact)
                                ? exact
                                : arabicMovies
                                    .Where(item =>
                                        item.ReleaseDate ==
                                        movie.ReleaseDate)
                                    .Skip(index)
                                    .FirstOrDefault() ??
                                  arabicMovies.ElementAtOrDefault(
                                      index);

                        if (arabicMovie is not null)
                        {
                            movie.ArabicTitle =
                                arabicMovie.EnglishTitle;
                        }

                        return movie;
                    })
                    .OrderBy(movie =>
                        movie.ReleaseDate ??
                        DateTime.MaxValue)
                    .ThenBy(movie =>
                        movie.EnglishTitle,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            HashSet<string> releasedSnapshot;

            lock (_comingSoonLock)
            {
                releasedSnapshot =
                    new HashSet<string>(
                        _releasedComingSoonKeys,
                        StringComparer.OrdinalIgnoreCase);
            }

            ComingSoonMovie[] active =
                combined
                    .Where(movie =>
                        !releasedSnapshot.Contains(
                            movie.Key) &&
                        !TryFindReleasedSchedules(
                            movie,
                            currentSchedules.Values,
                            out _))
                    .ToArray();

            ComingSoonMovie[] previousKnown;

            lock (_comingSoonLock)
            {
                previousKnown =
                    _knownComingSoonMovies.Values
                        .ToArray();
            }

            ComingSoonReleaseMatch[] releaseMatches =
                BuildComingSoonReleaseMatches(
                    previousKnown
                        .Concat(combined)
                        .GroupBy(movie =>
                            movie.Key,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group =>
                            group.First()),
                    currentSchedules.Values);

            ComingSoonMovie[] newlyAdded;

            lock (_comingSoonLock)
            {
                // A movie already present in actual Showtimes is not treated
                // as a new Coming Soon item, even if VOX still lists it there.
                newlyAdded =
                    active
                        .Where(movie =>
                            !_knownComingSoonMovies.ContainsKey(
                                movie.Key))
                        .ToArray();

                foreach (ComingSoonReleaseMatch release in
                         releaseMatches)
                {
                    _releasedComingSoonKeys.Add(
                        release.Movie.Key);
                }

                HashSet<string> subscribedMovieKeys =
                    new HashSet<string>(
                        _comingSoonPreferences.Values
                            .SelectMany(setting =>
                                setting.Subscriptions)
                            .Select(subscription =>
                                subscription.MovieKey),
                        StringComparer.OrdinalIgnoreCase);

                // Keep subscribed movies in the internal tracking state even
                // after VOX removes them from the Coming Soon page. This lets
                // the same subscription notify later when another selected
                // cinema or a new booking date becomes available.
                _knownComingSoonMovies =
                    combined
                        .Concat(previousKnown.Where(movie =>
                            subscribedMovieKeys.Contains(
                                movie.Key)))
                        .GroupBy(movie =>
                            movie.Key,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group =>
                                group.Key,
                            group =>
                                group.First(),
                            StringComparer.OrdinalIgnoreCase);

                _latestListedComingSoonKeys =
                    new HashSet<string>(
                        combined.Select(movie =>
                            movie.Key),
                        StringComparer.OrdinalIgnoreCase);

                _activeComingSoonMovies =
                    active;

                if (!_comingSoonStateFileExisted)
                {
                    foreach (ComingSoonReleaseMatch alreadyReleased in
                             releaseMatches)
                    {
                        _releasedComingSoonKeys.Add(
                            alreadyReleased.Movie.Key);
                    }
                }

                _lastComingSoonScanUtc =
                    DateTime.UtcNow;
            }

            // First installation creates a baseline. Later scans notify only
            // users who selected a movie, and only for cinema/date combinations
            // that have not already been announced to that user.
            if (_comingSoonStateFileExisted &&
                telegramAvailable)
            {
                await NotifyNewComingSoonMoviesAsync(
                    newlyAdded,
                    botToken,
                    stoppingToken);

                await NotifyReleasedComingSoonMoviesAsync(
                    releaseMatches.ToList(),
                    botToken,
                    stoppingToken);
            }

            _comingSoonStateFileExisted =
                true;

            SaveComingSoonState();

            _logger.LogInformation(
                "Coming Soon refreshed: {Total} listed, {Active} not released, {Released} already available in showtimes.",
                combined.Length,
                active.Length,
                combined.Length -
                active.Length);
        }

        private ComingSoonMovie[] ExtractComingSoonMovies(
            string html,
            string pageUrl,
            bool isArabic)
        {
            try
            {
                var document =
                    new HtmlDocument();

                document.LoadHtml(
                    html);

                HtmlNodeCollection? headings =
                    document.DocumentNode.SelectNodes(
                        "//h3");

                if (headings is null)
                {
                    return Array.Empty<ComingSoonMovie>();
                }

                var results =
                    new List<ComingSoonMovie>();

                foreach (HtmlNode heading in
                         headings)
                {
                    HtmlNode? anchor =
                        heading.Descendants("a")
                            .FirstOrDefault() ??
                        heading.ParentNode?
                            .Descendants("a")
                            .FirstOrDefault();

                    if (anchor is null)
                    {
                        continue;
                    }

                    string title =
                        CleanComingSoonMovieTitle(
                            NormalizeDisplayText(
                                HtmlEntity.DeEntitize(
                                    anchor.InnerText)));

                    if (string.IsNullOrWhiteSpace(
                            title))
                    {
                        continue;
                    }

                    HtmlNode card =
                        heading.Ancestors()
                            .FirstOrDefault(node =>
                                node.Name.Equals(
                                    "article",
                                    StringComparison.OrdinalIgnoreCase) ||
                                HasClassToken(
                                    node,
                                    "movie-card") ||
                                HasClassToken(
                                    node,
                                    "movie")) ??
                        heading.ParentNode?.ParentNode ??
                        heading.ParentNode ??
                        heading;

                    string cardText =
                        NormalizeDisplayText(
                            HtmlEntity.DeEntitize(
                                card.InnerText));

                    DateTime? releaseDate =
                        ParseComingSoonReleaseDate(
                            cardText,
                            isArabic);

                    string href =
                        anchor.GetAttributeValue(
                            "href",
                            string.Empty);

                    string absoluteUrl =
                        MakeAbsoluteUrl(
                            pageUrl,
                            href);

                    string key =
                        BuildComingSoonMovieKey(
                            absoluteUrl,
                            title,
                            releaseDate);

                    results.Add(
                        new ComingSoonMovie
                        {
                            Key =
                                key,

                            EnglishTitle =
                                title,

                            ArabicTitle =
                                isArabic
                                    ? title
                                    : string.Empty,

                            ReleaseDate =
                                releaseDate,

                            DetailsUrl =
                                absoluteUrl
                        });
                }

                return results
                    .GroupBy(movie =>
                        movie.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group.First())
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not parse the VOX Coming Soon page.");

                return Array.Empty<ComingSoonMovie>();
            }
        }

        private static DateTime? ParseComingSoonReleaseDate(
            string text,
            bool isArabic)
        {
            Match match =
                Regex.Match(
                    text,
                    isArabic
                        ? @"تاريخ\s*الاصدار\s*:\s*(?<date>[^\r\n]+?)(?:\s+النجوم|\s+اللغة|$)"
                        : @"Release\s+Date\s*:\s*(?<date>[^\r\n]+?)(?:\s+Starring|\s+Language|$)",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return null;
            }

            string value =
                NormalizeDisplayText(
                    match.Groups["date"].Value);

            string[] formats =
            {
                "dd MMMM yyyy",
                "d MMMM yyyy",
                "dd MMM yyyy",
                "d MMM yyyy"
            };

            if (!isArabic &&
                DateTime.TryParseExact(
                    value,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime englishDate))
            {
                return englishDate.Date;
            }

            var arabicMonths =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["يناير"] = 1,
                    ["فبراير"] = 2,
                    ["مارس"] = 3,
                    ["أبريل"] = 4,
                    ["ابريل"] = 4,
                    ["مايو"] = 5,
                    ["يونيو"] = 6,
                    ["يوليو"] = 7,
                    ["أغسطس"] = 8,
                    ["اغسطس"] = 8,
                    ["سبتمبر"] = 9,
                    ["أكتوبر"] = 10,
                    ["اكتوبر"] = 10,
                    ["نوفمبر"] = 11,
                    ["ديسمبر"] = 12
                };

            Match arabic =
                Regex.Match(
                    value,
                    @"(?<day>\d{1,2})\s+(?<month>\S+)\s+(?<year>\d{4})");

            if (arabic.Success &&
                int.TryParse(
                    arabic.Groups["day"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int day) &&
                int.TryParse(
                    arabic.Groups["year"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int year) &&
                arabicMonths.TryGetValue(
                    arabic.Groups["month"].Value,
                    out int month))
            {
                try
                {
                    return new DateTime(
                        year,
                        month,
                        day);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string BuildComingSoonMovieKey(
            string detailsUrl,
            string title,
            DateTime? releaseDate)
        {
            if (Uri.TryCreate(
                    detailsUrl,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                string path =
                    uri.AbsolutePath
                        .Replace(
                            "/ar/",
                            "/",
                            StringComparison.OrdinalIgnoreCase)
                        .TrimEnd('/')
                        .ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(
                        path) &&
                    path != "/movies/comingsoon")
                {
                    return path;
                }
            }

            return NormalizeMovieTitleKey(
                       title) +
                   "|" +
                   (releaseDate?.ToString(
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture) ??
                    "unknown");
        }

        private static bool TryFindReleasedSchedules(
            ComingSoonMovie movie,
            IEnumerable<DaySchedule> schedules,
            out DaySchedule[] matches)
        {
            matches =
                schedules
                    .Where(schedule =>
                        schedule.Movies.Any(current =>
                            IsComingSoonMovieMatch(
                                movie,
                                current.Title)))
                    .OrderBy(schedule =>
                        schedule.Date)
                    .ThenBy(schedule =>
                        schedule.CinemaName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            return matches.Length > 0;
        }

        private static bool IsComingSoonMovieMatch(
            ComingSoonMovie movie,
            string showtimeTitle)
        {
            string normalizedShowtime =
                NormalizeMovieTitleKey(
                    CleanComingSoonMovieTitle(
                        showtimeTitle));

            if (string.IsNullOrWhiteSpace(
                    normalizedShowtime))
            {
                return false;
            }

            string normalizedEnglish =
                NormalizeMovieTitleKey(
                    CleanComingSoonMovieTitle(
                        movie.EnglishTitle));

            string normalizedArabic =
                NormalizeMovieTitleKey(
                    CleanComingSoonMovieTitle(
                        movie.ArabicTitle));

            return string.Equals(
                       normalizedShowtime,
                       normalizedEnglish,
                       StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(
                        normalizedArabic) &&
                    string.Equals(
                        normalizedShowtime,
                        normalizedArabic,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static string CleanComingSoonMovieTitle(
            string value)
        {
            string result =
                NormalizeDisplayText(
                    value);

            if (string.IsNullOrWhiteSpace(
                    result))
            {
                return string.Empty;
            }

            // VOX currently includes age/rating labels inside the H3 text,
            // for example "G El Gawahergy", "16+ The Get Out" and
            // "18TC Paw Patrol". Language labels can also prefix filter names.
            string previous;

            do
            {
                previous =
                    result;

                result =
                    Regex.Replace(
                        result,
                        @"^\s*\((?:Arabic|English|Arabic\s*English|عربي|انجليزي|إنجليزي|عربي\s*انجليزي|عربي\s*إنجليزي)\)\s*",
                        string.Empty,
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);

                result =
                    Regex.Replace(
                        result,
                        @"^\s*(?:G|PG(?:-?13)?|R|TBC|NC-?15|\d{1,2}\+|\d{1,2}TC)\s+",
                        string.Empty,
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);
            }
            while (!string.Equals(
                       previous,
                       result,
                       StringComparison.Ordinal));

            return NormalizeDisplayText(
                result);
        }

        private static ComingSoonReleaseMatch[] BuildComingSoonReleaseMatches(
            IEnumerable<ComingSoonMovie> movies,
            IEnumerable<DaySchedule> schedules)
        {
            DaySchedule[] scheduleArray =
                schedules.ToArray();

            return movies
                .Select(movie =>
                {
                    return TryFindReleasedSchedules(
                               movie,
                               scheduleArray,
                               out DaySchedule[] matches)
                        ? new ComingSoonReleaseMatch(
                            movie,
                            matches)
                        : null;
                })
                .Where(item =>
                    item is not null)
                .Select(item =>
                    item!)
                .ToArray();
        }

        private async Task RevalidateComingSoonAgainstShowtimesAsync(
            Dictionary<string, DaySchedule> currentSchedules,
            string botToken,
            bool telegramAvailable,
            CancellationToken stoppingToken)
        {
            ComingSoonMovie[] known;
            HashSet<string> listedKeys;
            HashSet<string> releasedKeys;

            lock (_comingSoonLock)
            {
                known =
                    _knownComingSoonMovies.Values
                        .ToArray();

                listedKeys =
                    new HashSet<string>(
                        _latestListedComingSoonKeys,
                        StringComparer.OrdinalIgnoreCase);

                releasedKeys =
                    new HashSet<string>(
                        _releasedComingSoonKeys,
                        StringComparer.OrdinalIgnoreCase);
            }

            if (known.Length == 0)
            {
                return;
            }

            ComingSoonReleaseMatch[] releaseMatches =
                BuildComingSoonReleaseMatches(
                    known,
                    currentSchedules.Values);

            ComingSoonMovie[] active =
                known
                    .Where(movie =>
                        listedKeys.Contains(
                            movie.Key) &&
                        !releasedKeys.Contains(
                            movie.Key) &&
                        !releaseMatches.Any(release =>
                            string.Equals(
                                release.Movie.Key,
                                movie.Key,
                                StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(movie =>
                        movie.ReleaseDate ??
                        DateTime.MaxValue)
                    .ThenBy(movie =>
                        movie.EnglishTitle,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            lock (_comingSoonLock)
            {
                _activeComingSoonMovies =
                    active;

                foreach (ComingSoonReleaseMatch release in
                         releaseMatches)
                {
                    _releasedComingSoonKeys.Add(
                        release.Movie.Key);
                }
            }

            if (_comingSoonStateFileExisted &&
                telegramAvailable)
            {
                await NotifyReleasedComingSoonMoviesAsync(
                    releaseMatches.ToList(),
                    botToken,
                    stoppingToken);
            }

            SaveComingSoonState();
        }

        private async Task NotifyNewComingSoonMoviesAsync(
            ComingSoonMovie[] movies,
            string botToken,
            CancellationToken stoppingToken)
        {
            if (movies.Length == 0)
            {
                return;
            }

            ComingSoonUserPreference[] preferences;

            lock (_comingSoonLock)
            {
                preferences =
                    _comingSoonPreferences.Values
                        .Where(item =>
                            item.NotifyNewMovies)
                        .Select(item =>
                            item.Clone())
                        .ToArray();
            }

            foreach (ComingSoonUserPreference setting in
                     preferences)
            {
                UserPreference? user =
                    GetUserPreferenceSnapshot(
                        setting.UserId);

                if (user is null)
                {
                    continue;
                }

                if (ReadBooleanSetting(
                        "Telegram:RequireChannelSubscription",
                        defaultValue: false))
                {
                    bool subscribed =
                        await CheckChannelMembershipAsync(
                            botToken,
                            _configuration["Telegram:ChannelUsername"] ?? string.Empty,
                            setting.UserId,
                            stoppingToken);

                    if (!subscribed)
                    {
                        lock (_channelMembershipLock)
                        {
                            _channelMembershipCache.Remove(setting.UserId);
                        }

                        _logger.LogInformation(
                            "Skipping Coming Soon alert for Telegram user {UserId} because the user is not currently subscribed to the required channel.",
                            setting.UserId);

                        continue;
                    }
                }

                foreach (ComingSoonMovie movie in
                         movies)
                {
                    bool arabic =
                        string.Equals(
                            user.Language,
                            "ar",
                            StringComparison.OrdinalIgnoreCase);

                    string title =
                        arabic &&
                        !string.IsNullOrWhiteSpace(
                            movie.ArabicTitle)
                            ? movie.ArabicTitle
                            : movie.EnglishTitle;

                    string date =
                        movie.ReleaseDate.HasValue
                            ? arabic
                                ? FormatArabicDate(
                                    movie.ReleaseDate.Value)
                                : movie.ReleaseDate.Value.ToString(
                                    "dddd - d MMMM yyyy",
                                    CultureInfo.InvariantCulture)
                            : arabic
                                ? "لم يتم تحديد التاريخ"
                                : "Release date not announced";

                    string message =
                        arabic
                            ? "🔜 <b>فيلم جديد في قائمة قريباً</b>\n" +
                              "━━━━━━━━━━━━━━━━━━━━\n" +
                              "🎬 <b>" +
                              FormatArabicBidiText(
                                  title) +
                              "</b>\n" +
                              "📅 تاريخ العرض المتوقع: " +
                              EscapeTelegramHtml(
                                  date)
                            : "🔜 <b>NEW COMING SOON MOVIE</b>\n" +
                              "━━━━━━━━━━━━━━━━━━━━\n" +
                              "🎬 <b>" +
                              EscapeTelegramHtml(
                                  title) +
                              "</b>\n" +
                              "📅 Expected release: " +
                              EscapeTelegramHtml(
                                  date);

                    QueueTelegramAlert(
                        botToken,
                        user.PrivateChatId.ToString(
                            CultureInfo.InvariantCulture),
                        message,
                        BuildComingSoonOpenKeyboard(
                            user.Language));
                }
            }
        }

        private async Task NotifyReleasedComingSoonMoviesAsync(
            List<ComingSoonReleaseMatch> releases,
            string botToken,
            CancellationToken stoppingToken)
        {
            bool preferencesChanged =
                false;

            foreach (ComingSoonReleaseMatch release in
                     releases)
            {
                ComingSoonUserPreference[] subscribers;

                lock (_comingSoonLock)
                {
                    subscribers =
                        _comingSoonPreferences.Values
                            .Where(setting =>
                                setting.Subscriptions.Any(subscription =>
                                    string.Equals(
                                        subscription.MovieKey,
                                        release.Movie.Key,
                                        StringComparison.OrdinalIgnoreCase)))
                            .Select(setting =>
                                setting.Clone())
                            .ToArray();
                }

                foreach (ComingSoonUserPreference setting in
                         subscribers)
                {
                    UserPreference? user =
                        GetUserPreferenceSnapshot(
                            setting.UserId);

                    if (user is null)
                    {
                        continue;
                    }

                    if (ReadBooleanSetting(
                            "Telegram:RequireChannelSubscription",
                            defaultValue: false))
                    {
                        bool subscribed =
                            await CheckChannelMembershipAsync(
                                botToken,
                                _configuration["Telegram:ChannelUsername"] ?? string.Empty,
                                setting.UserId,
                                stoppingToken);

                        if (!subscribed)
                        {
                            lock (_channelMembershipLock)
                            {
                                _channelMembershipCache.Remove(setting.UserId);
                            }

                            _logger.LogInformation(
                                "Skipping released Coming Soon alert for Telegram user {UserId} because the user is not currently subscribed to the required channel.",
                                setting.UserId);

                            continue;
                        }
                    }

                    ComingSoonSubscription? subscription =
                        setting.Subscriptions.FirstOrDefault(item =>
                            string.Equals(
                                item.MovieKey,
                                release.Movie.Key,
                                StringComparison.OrdinalIgnoreCase));

                    if (subscription is null)
                    {
                        continue;
                    }

                    DaySchedule[] selectedSchedules =
                        release.Schedules
                            .Where(schedule =>
                                subscription.CinemaSlugs.Count == 0 ||
                                subscription.CinemaSlugs.Contains(
                                    schedule.CinemaSlug))
                            .Where(schedule =>
                                HasFutureBookableShowtime(
                                    release.Movie,
                                    schedule,
                                    DateTime.Now))
                            .OrderBy(schedule =>
                                schedule.Date)
                            .ThenBy(schedule =>
                                schedule.CinemaName,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                    DaySchedule[] notYetAnnounced =
                        selectedSchedules
                            .Where(schedule =>
                                !setting.NotifiedReleaseKeys.Contains(
                                    BuildComingSoonReleaseNotificationKey(
                                        release.Movie.Key,
                                        schedule)))
                            .ToArray();

                    if (notYetAnnounced.Length == 0)
                    {
                        continue;
                    }

                    string message =
                        await BuildComingSoonReleasedMessageAsync(
                            release.Movie,
                            notYetAnnounced,
                            user.Language,
                            stoppingToken);

                    object keyboard =
                        await BuildComingSoonReleaseKeyboardAsync(
                            release.Movie,
                            notYetAnnounced,
                            user.Language,
                            stoppingToken);

                    int? earlyDelayMinutes =
                        GetEarlyBookingAlertDelayMinutes(setting.UserId);

                    if (!earlyDelayMinutes.HasValue)
                    {
                        continue;
                    }

                    string[] notificationKeys =
                        notYetAnnounced
                            .Select(schedule =>
                                BuildComingSoonReleaseNotificationKey(
                                    release.Movie.Key,
                                    schedule))
                            .ToArray();

                    if (earlyDelayMinutes.Value <= 0)
                    {
                        QueueTelegramAlert(
                            botToken,
                            user.PrivateChatId.ToString(
                                CultureInfo.InvariantCulture),
                            message,
                            keyboard);

                        MarkComingSoonReleaseNotificationsSent(
                            setting.UserId,
                            notificationKeys);

                        preferencesChanged = true;
                        continue;
                    }

                    string pendingKey =
                        BuildComingSoonEarlyBookingPendingKey(
                            setting.UserId,
                            notificationKeys);

                    lock (_comingSoonLock)
                    {
                        if (!_pendingComingSoonEarlyBookingAlertKeys.Add(
                                pendingKey))
                        {
                            continue;
                        }
                    }

                    _ = SendComingSoonReleaseNotificationAfterDelayAsync(
                        botToken,
                        setting.UserId,
                        release.Movie,
                        message,
                        keyboard,
                        notificationKeys,
                        pendingKey,
                        earlyDelayMinutes.Value,
                        stoppingToken);
                }
            }

            if (preferencesChanged)
            {
                SaveComingSoonPreferences();
            }
        }

        private string BuildComingSoonEarlyBookingPendingKey(
            long userId,
            IEnumerable<string> notificationKeys)
        {
            return userId.ToString(CultureInfo.InvariantCulture) +
                   "|" +
                   string.Join("|", notificationKeys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        }

        private async Task SendComingSoonReleaseNotificationAfterDelayAsync(
            string botToken,
            long userId,
            ComingSoonMovie movie,
            string message,
            object keyboard,
            string[] notificationKeys,
            string pendingKey,
            int delayMinutes,
            CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(delayMinutes),
                    stoppingToken);

                UserPreference? latestPreference =
                    GetUserPreferenceSnapshot(userId);

                if (latestPreference is null ||
                    latestPreference.PrivateChatId == 0)
                {
                    return;
                }

                int? latestDelay =
                    GetEarlyBookingAlertDelayMinutes(userId);

                // null means early alerts are currently disabled for this user.
                // Admin/Super Admin users always resolve to 0 and therefore remain instant.
                if (!latestDelay.HasValue)
                {
                    return;
                }

                QueueTelegramAlert(
                    botToken,
                    latestPreference.PrivateChatId.ToString(
                        CultureInfo.InvariantCulture),
                    message,
                    keyboard);

                MarkComingSoonReleaseNotificationsSent(
                    userId,
                    notificationKeys);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Delayed Coming Soon booking alert failed for Telegram user {UserId} and movie {MovieKey}.",
                    userId,
                    movie.Key);
            }
            finally
            {
                lock (_comingSoonLock)
                {
                    _pendingComingSoonEarlyBookingAlertKeys.Remove(pendingKey);
                }
            }
        }

        private void MarkComingSoonReleaseNotificationsSent(
            long userId,
            IEnumerable<string> notificationKeys)
        {
            lock (_comingSoonLock)
            {
                if (!_comingSoonPreferences.TryGetValue(
                        userId,
                        out ComingSoonUserPreference? actual))
                {
                    return;
                }

                foreach (string key in notificationKeys)
                {
                    actual.NotifiedReleaseKeys.Add(key);
                }
            }

            SaveComingSoonPreferences();
        }

        private bool HasFutureBookableShowtime(
            ComingSoonMovie movie,
            DaySchedule schedule,
            DateTime nowLocal)
        {
            MovieSchedule? matchingMovie =
                schedule.Movies.FirstOrDefault(item =>
                    IsComingSoonMovieMatch(
                        movie,
                        item.Title));

            return matchingMovie is not null &&
                   matchingMovie.Halls.Any(hall =>
                       hall.Showtimes.Any(showtime =>
                           showtime.IsAvailable &&
                           IsFutureShowtime(
                               schedule.Date,
                               showtime.Time,
                               nowLocal)));
        }

        private static string BuildComingSoonReleaseNotificationKey(
            string movieKey,
            DaySchedule schedule)
        {
            return movieKey +
                   "|" +
                   schedule.CinemaSlug +
                   "|" +
                   schedule.Date.ToString(
                       "yyyyMMdd",
                       CultureInfo.InvariantCulture);
        }

        private async Task<object> BuildComingSoonReleaseKeyboardAsync(
            ComingSoonMovie movie,
            DaySchedule[] schedules,
            string language,
            CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            int maximumButtons =
                ReadIntegerSetting(
                    "ComingSoon:MaximumDirectBookingButtons",
                    defaultValue: 30,
                    minimumValue: 5,
                    maximumValue: 80);

            var options =
                new List<ComingSoonBookingOption>();

            foreach (DaySchedule schedule in
                     schedules)
            {
                MovieSchedule? matchingMovie =
                    schedule.Movies.FirstOrDefault(item =>
                        IsComingSoonMovieMatch(
                            movie,
                            item.Title));

                if (matchingMovie is null)
                {
                    continue;
                }

                MovieSchedule resolved =
                    await ResolveMovieBookingUrlsAsync(
                        matchingMovie,
                        schedule.Url,
                        stoppingToken,
                        forceRefresh:
                            true);

                foreach (HallSchedule hall in
                         resolved.Halls)
                {
                    foreach (ShowtimeSchedule showtime in
                             hall.Showtimes)
                    {
                        if (!showtime.IsAvailable ||
                            !IsFutureShowtime(
                                schedule.Date,
                                showtime.Time,
                                DateTime.Now))
                        {
                            continue;
                        }

                        string bookingUrl =
                            NormalizeGuestBookingUrl(
                                showtime.BookingUrl);

                        if (string.IsNullOrWhiteSpace(
                                bookingUrl) ||
                            !IsFinalGuestBookingUrl(
                                bookingUrl))
                        {
                            continue;
                        }

                        options.Add(
                            new ComingSoonBookingOption
                            {
                                CinemaSlug =
                                    schedule.CinemaSlug,

                                CinemaName =
                                    schedule.CinemaName,

                                Date =
                                    schedule.Date,

                                HallName =
                                    hall.Name,

                                Time =
                                    showtime.Time,

                                BookingUrl =
                                    bookingUrl
                            });
                    }
                }
            }

            var rows =
                new List<object>();

            foreach (ComingSoonBookingOption option in
                     options
                         .OrderBy(item =>
                             item.Date)
                         .ThenBy(item =>
                             item.CinemaName,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             TryBuildShowtimeDateTime(
                                 item.Date,
                                 item.Time) ??
                             DateTime.MaxValue)
                         .Take(maximumButtons))
            {
                string cinemaName =
                    arabic
                        ? GetArabicCinemaDisplayName(
                            option.CinemaSlug,
                            option.CinemaName,
                            option.CinemaName)
                        : option.CinemaName;

                string date =
                    arabic
                        ? option.Date.ToString(
                            "dd/MM",
                            CultureInfo.InvariantCulture)
                        : option.Date.ToString(
                            "dd MMM",
                            CultureInfo.InvariantCulture);

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            text =
                                "🎟 " +
                                cinemaName +
                                " • " +
                                date +
                                " • " +
                                option.Time,

                            url =
                                option.BookingUrl
                        }
                    });
            }

            foreach (DaySchedule schedule in
                     schedules
                         .GroupBy(item =>
                             item.CinemaSlug,
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
                                (arabic
                                    ? GetArabicCinemaDisplayName(
                                        schedule.CinemaSlug,
                                        schedule.CinemaName,
                                        schedule.CinemaName)
                                    : schedule.CinemaName) +
                                (arabic
                                    ? " — كل المواعيد"
                                    : " — All Showtimes"),

                            callback_data =
                                "browse:cin:" +
                                schedule.CinemaSlug
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
                                ? "🔜 قائمة قريباً"
                                : "🔜 Coming Soon",

                        callback_data =
                            "coming:menu:0"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🎬 الأفلام الحالية"
                                : "🎬 Current Movies",

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

        private async Task<string> BuildComingSoonReleasedMessageAsync(
            ComingSoonMovie movie,
            DaySchedule[] schedules,
            string language,
            CancellationToken stoppingToken)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            string title =
                arabic &&
                !string.IsNullOrWhiteSpace(
                    movie.ArabicTitle)
                    ? movie.ArabicTitle
                    : movie.EnglishTitle;

            var builder =
                new StringBuilder();

            builder.AppendLine(
                arabic
                    ? "🎉 <b>الفيلم أصبح متاحاً للحجز</b>"
                    : "🎉 <b>MOVIE IS NOW AVAILABLE TO BOOK</b>");

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

            foreach (DaySchedule schedule in
                     schedules.Take(6))
            {
                DaySchedule displaySchedule =
                    arabic
                        ? await TryLoadArabicScheduleAsync(
                              schedule,
                              stoppingToken) ??
                          schedule
                        : schedule;

                MovieSchedule? foundMovie =
                    displaySchedule.Movies.FirstOrDefault(item =>
                        IsComingSoonMovieMatch(
                            movie,
                            item.Title));

                if (foundMovie is null)
                {
                    continue;
                }

                MovieSchedule resolved =
                    await ResolveMovieBookingUrlsAsync(
                        foundMovie,
                        displaySchedule.Url,
                        stoppingToken,
                        forceRefresh:
                            true);

                builder.AppendLine();
                builder.AppendLine(
                    "🏢 <b>" +
                    (arabic
                        ? FormatArabicBidiText(
                            displaySchedule.CinemaName)
                        : EscapeTelegramHtml(
                            displaySchedule.CinemaName)) +
                    "</b>");
                builder.AppendLine(
                    "📅 " +
                    EscapeTelegramHtml(
                        arabic
                            ? FormatArabicDate(
                                displaySchedule.Date)
                            : displaySchedule.Date.ToString(
                                "dddd - d MMMM yyyy",
                                CultureInfo.InvariantCulture)));

                foreach (HallSchedule hall in
                         resolved.Halls)
                {
                    string[] availableTimes =
                        hall.Showtimes
                            .Where(showtime =>
                                showtime.IsAvailable &&
                                IsFutureShowtime(
                                    displaySchedule.Date,
                                    showtime.Time,
                                    DateTime.Now))
                            .Select(showtime =>
                                showtime.Time)
                            .ToArray();

                    if (availableTimes.Length > 0)
                    {
                        builder.AppendLine(
                            "🏛 " +
                            EscapeTelegramHtml(
                                hall.Name) +
                            ": " +
                            string.Join(
                                " • ",
                                availableTimes.Select(
                                    EscapeTelegramHtml)));
                    }
                }

                builder.AppendLine(
                    "🔗 <a href=\"" +
                    EscapeTelegramHtml(
                        displaySchedule.Url) +
                    "\"><b>" +
                    (arabic
                        ? "افتح الحجز"
                        : "Open booking") +
                    "</b></a>");
            }

            return LimitText(
                builder.ToString(),
                3900);
        }

        private UserPreference? GetUserPreferenceSnapshot(
            long userId)
        {
            lock (_preferencesLock)
            {
                return _userPreferences.TryGetValue(
                           userId,
                           out UserPreference? preference)
                    ? preference.Clone()
                    : null;
            }
        }

        private async Task SendComingSoonMenuAsync(
            string botToken,
            UserPreference preference,
            int page,
            CancellationToken stoppingToken)
        {
            ComingSoonUserPreference setting =
                GetOrCreateComingSoonPreference(
                    preference.UserId,
                    preference.PrivateChatId);

            await SendTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                BuildComingSoonMenuText(
                    preference.Language),
                BuildComingSoonMenuKeyboard(
                    preference.Language,
                    setting,
                    page),
                stoppingToken);
        }

        private async Task HandleComingSoonCallbackAsync(
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

            ComingSoonUserPreference setting =
                GetOrCreateComingSoonPreference(
                    preference.UserId,
                    preference.PrivateChatId);

            string[] parts =
                data.Split(':');

            if (string.Equals(
                    data,
                    "coming:refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                lock (_comingSoonLock)
                {
                    _lastComingSoonScanUtc =
                        DateTime.MinValue;
                }

                RequestImmediateScan(
                    force: true);

                await EditComingSoonMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (data.StartsWith(
                    "coming:menu",
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

                await EditComingSoonMenuAsync(
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
                    "coming:selectall",
                    StringComparison.OrdinalIgnoreCase))
            {
                SelectAllComingSoonMovies(
                    setting);

                SaveComingSoonPreferences();

                await EditComingSoonMenuAsync(
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
                    "coming:clearall",
                    StringComparison.OrdinalIgnoreCase))
            {
                setting.Subscriptions.Clear();
                setting.NotifiedReleaseKeys.Clear();

                SaveComingSoonPreferences();

                await EditComingSoonMenuAsync(
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
                    "coming:toggle:new",
                    StringComparison.OrdinalIgnoreCase))
            {
                setting.NotifyNewMovies =
                    !setting.NotifyNewMovies;

                SaveComingSoonPreferences();

                await EditComingSoonMenuAsync(
                    botToken,
                    preference,
                    setting,
                    messageId,
                    0,
                    stoppingToken);

                return;
            }

            if (parts.Length >= 3 &&
                int.TryParse(
                    parts[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int movieIndex))
            {
                ComingSoonMovie? movie =
                    GetComingSoonMovieByIndex(
                        movieIndex);

                if (movie is null)
                {
                    await EditComingSoonMenuAsync(
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
                    await EditComingSoonMovieAsync(
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
                    ToggleComingSoonAllCinemas(
                        setting,
                        movie.Key);
                    SaveComingSoonPreferences();

                    await EditComingSoonMovieAsync(
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
                    ToggleComingSoonCinema(
                        setting,
                        movie.Key,
                        _lastDiscoveredCinemas[cinemaIndex].Slug);
                    SaveComingSoonPreferences();

                    await EditComingSoonMovieAsync(
                        botToken,
                        preference,
                        setting,
                        movie,
                        movieIndex,
                        messageId,
                        stoppingToken);
                }
            }
        }

        private async Task EditComingSoonMenuAsync(
            string botToken,
            UserPreference preference,
            ComingSoonUserPreference setting,
            int messageId,
            int page,
            CancellationToken stoppingToken)
        {
            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                BuildComingSoonMenuText(
                    preference.Language),
                BuildComingSoonMenuKeyboard(
                    preference.Language,
                    setting,
                    page),
                stoppingToken);
        }

        private async Task EditComingSoonMovieAsync(
            string botToken,
            UserPreference preference,
            ComingSoonUserPreference setting,
            ComingSoonMovie movie,
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

            string date =
                movie.ReleaseDate.HasValue
                    ? arabic
                        ? FormatArabicDate(
                            movie.ReleaseDate.Value)
                        : movie.ReleaseDate.Value.ToString(
                            "dddd - d MMMM yyyy",
                            CultureInfo.InvariantCulture)
                    : arabic
                        ? "لم يتم تحديد التاريخ"
                        : "Release date not announced";

            string message =
                arabic
                    ? "🔜 <b>متابعة فيلم يعرض قريباً</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "🎬 <b>" +
                      FormatArabicBidiText(
                          title) +
                      "</b>\n" +
                      "📅 تاريخ العرض المتوقع: " +
                      EscapeTelegramHtml(
                          date) +
                      "\n\nاختر كل السينمات أو سينما واحدة أو أكثر. " +
                      "سيصلك تنبيه عند ظهور أول يوم ومواعيد حجز فعلية."
                    : "🔜 <b>FOLLOW COMING SOON MOVIE</b>\n" +
                      "━━━━━━━━━━━━━━━━━━━━\n" +
                      "🎬 <b>" +
                      EscapeTelegramHtml(
                          title) +
                      "</b>\n" +
                      "📅 Expected release: " +
                      EscapeTelegramHtml(
                          date) +
                      "\n\nChoose all cinemas or one or more cinemas. " +
                      "You will be notified when actual dates and showtimes become bookable.";

            await EditTelegramHtmlWithMarkupAsync(
                botToken,
                preference.PrivateChatId,
                messageId,
                message,
                BuildComingSoonMovieKeyboard(
                    preference.Language,
                    setting,
                    movie,
                    movieIndex),
                stoppingToken);
        }

        private string BuildComingSoonMenuText(
            string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            return arabic
                ? "🔜 <b>الأفلام التي تعرض قريباً</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "🎬 " +
                  _activeComingSoonMovies.Length +
                  " فيلم لم يبدأ حجزه فعلياً بعد.\n\n" +
                  "هذه القائمة موجودة داخل البوت الخاص نفسه. اختر فيلماً أو أكثر، " +
                  "ثم اختر كل السينمات أو سينمات محددة ليصلك الحجز فور ظهوره.\n\n" +
                  BuildLiveStatusLine(
                      language)
                : "🔜 <b>COMING SOON MOVIES</b>\n" +
                  "━━━━━━━━━━━━━━━━━━━━\n" +
                  "🎬 " +
                  _activeComingSoonMovies.Length +
                  " movies are not actually bookable yet.\n\n" +
                  "This list is available directly inside the private bot. Select one or more movies, " +
                  "then all cinemas or selected cinemas, and receive the booking as soon as it appears.\n\n" +
                  BuildLiveStatusLine(
                      language);
        }

        private object BuildComingSoonMenuKeyboard(
            string language,
            ComingSoonUserPreference setting,
            int page)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            const int pageSize = 8;
            int pageCount =
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        _activeComingSoonMovies.Length /
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
                            (setting.NotifyNewMovies
                                ? "✅ "
                                : "▫️ ") +
                            (arabic
                                ? "تنبيه أي فيلم جديد في قريباً"
                                : "Alert me about new Coming Soon movies"),

                        callback_data =
                            "coming:toggle:new"
                    }
                });

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
                            "coming:selectall"
                    },

                    new
                    {
                        text =
                            arabic
                                ? "🧹 مسح الكل"
                                : "🧹 Clear All",

                        callback_data =
                            "coming:clearall"
                    }
                });

            rows.Add(
                new object[]
                {
                    new
                    {
                        text =
                            arabic
                                ? "⚡ تحديث قائمة قريباً الآن"
                                : "⚡ Refresh Coming Soon Now",

                        callback_data =
                            "coming:refresh"
                    }
                });

            int start =
                page *
                pageSize;

            for (int index = start;
                 index <
                 Math.Min(
                     start + pageSize,
                     _activeComingSoonMovies.Length);
                 index++)
            {
                ComingSoonMovie movie =
                    _activeComingSoonMovies[index];

                string title =
                    arabic &&
                    !string.IsNullOrWhiteSpace(
                        movie.ArabicTitle)
                        ? movie.ArabicTitle
                        : movie.EnglishTitle;

                bool selected =
                    setting.Subscriptions.Any(subscription =>
                        string.Equals(
                            subscription.MovieKey,
                            movie.Key,
                            StringComparison.OrdinalIgnoreCase));

                rows.Add(
                    new object[]
                    {
                        new
                        {
                            // Keep the list clean: movie name only. Rating and
                            // language classifications are removed at parsing.
                            text =
                                (selected
                                    ? "✅ "
                                    : "🎬 ") +
                                title,

                            callback_data =
                                "coming:movie:" +
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
                                "coming:menu:" +
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
                            "coming:menu:" +
                            page.ToString(
                                CultureInfo.InvariantCulture)
                    });

                if (page + 1 < pageCount)
                {
                    navigation.Add(
                        new
                        {
                            text =
                                "➡️",

                            callback_data =
                                "coming:menu:" +
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

        private object BuildComingSoonMovieKeyboard(
            string language,
            ComingSoonUserPreference setting,
            ComingSoonMovie movie,
            int movieIndex)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            ComingSoonSubscription? subscription =
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
                            "coming:all:" +
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
                                "coming:cin:" +
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
                                ? "⬅️ قائمة قريباً"
                                : "⬅️ Coming Soon List",

                        callback_data =
                            "coming:menu:0"
                    }
                });

            return new
            {
                inline_keyboard =
                    rows.ToArray()
            };
        }

        private static object BuildComingSoonOpenKeyboard(
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
                                        ? "🔜 افتح قائمة قريباً"
                                        : "🔜 Open Coming Soon",

                                callback_data =
                                    "coming:menu:0"
                            }
                        }
                    }
            };
        }

        private ComingSoonMovie? GetComingSoonMovieByIndex(
            int index)
        {
            lock (_comingSoonLock)
            {
                return index >= 0 &&
                       index <
                       _activeComingSoonMovies.Length
                    ? _activeComingSoonMovies[index]
                    : null;
            }
        }

        private ComingSoonUserPreference GetOrCreateComingSoonPreference(
            long userId,
            long privateChatId)
        {
            lock (_comingSoonLock)
            {
                if (!_comingSoonPreferences.TryGetValue(
                        userId,
                        out ComingSoonUserPreference? setting))
                {
                    setting =
                        new ComingSoonUserPreference
                        {
                            UserId =
                                userId,

                            PrivateChatId =
                                privateChatId,

                            NotifyNewMovies =
                                true
                        };

                    _comingSoonPreferences[userId] =
                        setting;

                    SaveComingSoonPreferences();
                }
                else
                {
                    setting.PrivateChatId =
                        privateChatId;
                }

                return setting;
            }
        }

        private static ComingSoonSubscription GetOrCreateComingSoonSubscription(
            ComingSoonUserPreference setting,
            string movieKey)
        {
            ComingSoonSubscription? subscription =
                setting.Subscriptions.FirstOrDefault(item =>
                    string.Equals(
                        item.MovieKey,
                        movieKey,
                        StringComparison.OrdinalIgnoreCase));

            if (subscription is null)
            {
                subscription =
                    new ComingSoonSubscription
                    {
                        MovieKey =
                            movieKey
                    };

                setting.Subscriptions.Add(
                    subscription);
            }

            return subscription;
        }

        private void SelectAllComingSoonMovies(
            ComingSoonUserPreference setting)
        {
            lock (_comingSoonLock)
            {
                setting.Subscriptions =
                    _activeComingSoonMovies
                        .Select(movie =>
                            new ComingSoonSubscription
                            {
                                MovieKey =
                                    movie.Key
                            })
                        .ToList();
            }
        }

        private static void ToggleComingSoonAllCinemas(
            ComingSoonUserPreference setting,
            string movieKey)
        {
            ComingSoonSubscription? existing =
                setting.Subscriptions.FirstOrDefault(item =>
                    string.Equals(
                        item.MovieKey,
                        movieKey,
                        StringComparison.OrdinalIgnoreCase));

            if (existing is not null &&
                existing.CinemaSlugs.Count == 0)
            {
                setting.Subscriptions.Remove(
                    existing);

                setting.NotifiedReleaseKeys.RemoveWhere(key =>
                    key.StartsWith(
                        movieKey + "|",
                        StringComparison.OrdinalIgnoreCase));

                return;
            }

            ComingSoonSubscription subscription =
                GetOrCreateComingSoonSubscription(
                    setting,
                    movieKey);

            subscription.CinemaSlugs.Clear();
        }

        private static void ToggleComingSoonCinema(
            ComingSoonUserPreference setting,
            string movieKey,
            string cinemaSlug)
        {
            ComingSoonSubscription subscription =
                GetOrCreateComingSoonSubscription(
                    setting,
                    movieKey);

            // Empty means all cinemas. Selecting an individual cinema changes
            // the subscription from All to a specific set.
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

                setting.NotifiedReleaseKeys.RemoveWhere(key =>
                    key.StartsWith(
                        movieKey + "|",
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        private void LoadComingSoonState()
        {
            try
            {
                _comingSoonStateFileExisted =
                    TryLoadPersistentJson(
                        Path.GetFileName(ComingSoonStatePath),
                        ComingSoonStatePath,
                        out string stateJson);

                if (!_comingSoonStateFileExisted)
                {
                    return;
                }

                ComingSoonStateStorage? storage =
                    JsonSerializer.Deserialize<ComingSoonStateStorage>(stateJson);

                if (storage is null)
                {
                    return;
                }

                _knownComingSoonMovies =
                    (storage.Movies ??
                     new List<ComingSoonMovie>())
                    .Where(movie =>
                        !string.IsNullOrWhiteSpace(
                            movie.Key))
                    .ToDictionary(
                        movie =>
                            movie.Key,
                        movie =>
                            movie,
                        StringComparer.OrdinalIgnoreCase);

                _releasedComingSoonKeys =
                    new HashSet<string>(
                        storage.ReleasedKeys ??
                        new List<string>(),
                        StringComparer.OrdinalIgnoreCase);

                _activeComingSoonMovies =
                    _knownComingSoonMovies.Values
                        .Where(movie =>
                            !_releasedComingSoonKeys.Contains(
                                movie.Key))
                        .OrderBy(movie =>
                            movie.ReleaseDate ??
                            DateTime.MaxValue)
                        .ThenBy(movie =>
                            movie.EnglishTitle,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load Coming Soon state. A new baseline will be created.");
            }
        }

        private void SaveComingSoonState()
        {
            try
            {
                ComingSoonStateStorage storage;

                lock (_comingSoonLock)
                {
                    storage =
                        new ComingSoonStateStorage
                        {
                            Movies =
                                _knownComingSoonMovies.Values.ToList(),

                            ReleasedKeys =
                                _releasedComingSoonKeys.ToList()
                        };
                }

                string json =
                    JsonSerializer.Serialize(
                        storage,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                SavePersistentJson(
                    Path.GetFileName(ComingSoonStatePath),
                    ComingSoonStatePath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save Coming Soon state.");
            }
        }

        private void LoadComingSoonPreferences()
        {
            try
            {
                if (!TryLoadPersistentJson(
                        Path.GetFileName(ComingSoonPreferencesPath),
                        ComingSoonPreferencesPath,
                        out string json))
                {
                    return;
                }

                List<ComingSoonUserPreference>? stored =
                    JsonSerializer.Deserialize<List<ComingSoonUserPreference>>(json);

                _comingSoonPreferences =
                    (stored ??
                     new List<ComingSoonUserPreference>())
                    .ToDictionary(
                        item =>
                            item.UserId,
                        item =>
                        {
                            item.Subscriptions =
                                item.Subscriptions ??
                                new List<ComingSoonSubscription>();

                            foreach (ComingSoonSubscription subscription in
                                     item.Subscriptions)
                            {
                                subscription.CinemaSlugs =
                                    new HashSet<string>(
                                        subscription.CinemaSlugs ??
                                        new HashSet<string>(),
                                        StringComparer.OrdinalIgnoreCase);
                            }

                            item.NotifiedReleaseKeys =
                                new HashSet<string>(
                                    item.NotifiedReleaseKeys ??
                                    new HashSet<string>(),
                                    StringComparer.OrdinalIgnoreCase);

                            return item;
                        });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load Coming Soon preferences.");
            }
        }

        private void SaveComingSoonPreferences()
        {
            try
            {
                List<ComingSoonUserPreference> snapshot;

                lock (_comingSoonLock)
                {
                    snapshot =
                        _comingSoonPreferences.Values
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
                    Path.GetFileName(ComingSoonPreferencesPath),
                    ComingSoonPreferencesPath,
                    json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save Coming Soon preferences.");
            }
        }
    }
}
