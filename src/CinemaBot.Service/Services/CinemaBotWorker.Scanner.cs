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
        private async Task ScanAllCinemasCurrentMonthAsync(
                string baseVoxUrl,
                string botToken,
                string chatId,
                bool telegramAvailable,
                int maximumConcurrentRequests,
                CancellationToken stoppingToken)
        {
            CinemaOption[] cinemas =
                await DiscoverCinemasAsync(
                    baseVoxUrl,
                    stoppingToken);

            if (cinemas.Length == 0)
            {
                _logger.LogError(
                    "No VOX cinema branches could be discovered. " +
                    "The previous cinema list is also empty, so this cycle will be skipped.");

                return;
            }

            List<DateTime> dates =
                GetConfiguredScanDates();

            if (dates.Count == 0)
            {
                _logger.LogWarning("VOX scan range produced no dates. The cycle will be skipped.");
                return;
            }

            DateTime startDate = dates.Min().Date;
            DateTime endDate = dates.Max().Date;

            HashSet<string> activeStateKeys =
                new HashSet<string>(
                    cinemas.SelectMany(
                        cinema =>
                            dates.Select(
                                date =>
                                    FormatStateKey(
                                        cinema.Slug,
                                        date))),
                    StringComparer.OrdinalIgnoreCase);

            bool hasPreviousSnapshotForCurrentRange =
                _knownScheduleByCinemaAndDate.Keys.Any(
                    activeStateKeys.Contains);

            int totalPages =
                cinemas.Length *
                dates.Count;

            _logger.LogInformation(
                "Scanning {CinemaCount} cinema(s) across {DateCount} date(s): " +
                "{TotalPages} VOX page(s). Range: {StartDate} through {EndDate}. " +
                "Maximum concurrent requests: {MaximumConcurrentRequests}. " +
                "Cinemas: {CinemaNames}",
                cinemas.Length,
                dates.Count,
                totalPages,
                startDate.ToString(
                    "dd MMMM yyyy",
                    CultureInfo.InvariantCulture),
                endDate.ToString(
                    "dd MMMM yyyy",
                    CultureInfo.InvariantCulture),
                maximumConcurrentRequests,
                string.Join(
                    ", ",
                    cinemas.Select(
                        cinema =>
                            cinema.Name)));

            using var semaphore =
                new SemaphoreSlim(
                    maximumConcurrentRequests);

            Task<DateScanResult>[] tasks =
                cinemas
                    .SelectMany(
                        cinema =>
                            dates.Select(
                                date =>
                                    ScanDateWithLimitAsync(
                                        baseVoxUrl,
                                        cinema,
                                        date,
                                        semaphore,
                                        stoppingToken)))
                    .ToArray();

            DateScanResult[] results =
                await Task.WhenAll(
                    tasks);

            Dictionary<string, DaySchedule> nextState =
                new Dictionary<string, DaySchedule>(
                    _knownScheduleByCinemaAndDate,
                    StringComparer.OrdinalIgnoreCase);

            List<DayScheduleChange> notifications =
                new List<DayScheduleChange>();

            foreach (DateScanResult result in
                     results
                         .OrderBy(item =>
                             item.Schedule?.CinemaName ??
                             string.Empty,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item =>
                             item.Date))
            {
                if (!result.Success ||
                    result.Schedule is null)
                {
                    _logger.LogWarning(
                        "The scan failed for {Cinema} on {Date}. " +
                        "Its previous in-memory state will be preserved.",
                        result.CinemaName,
                        result.Date.ToString(
                            "dd MMMM yyyy",
                            CultureInfo.InvariantCulture));

                    continue;
                }

                DaySchedule currentSchedule =
                    result.Schedule;

                string stateKey =
                    FormatStateKey(
                        currentSchedule.CinemaSlug,
                        currentSchedule.Date);

                DaySchedule? previousSchedule =
                    null;

                _knownScheduleByCinemaAndDate.TryGetValue(
                    stateKey,
                    out previousSchedule);

                if (!hasPreviousSnapshotForCurrentRange)
                {
                    if (currentSchedule.Movies.Length > 0)
                    {
                        notifications.Add(
                            DayScheduleChange.Initial(
                                currentSchedule));
                    }
                }
                else
                {
                    DayScheduleChange? detectedChange =
                        DetectScheduleChanges(
                            previousSchedule,
                            currentSchedule);

                    if (detectedChange is not null)
                    {
                        notifications.Add(
                            detectedChange);
                    }
                }

                nextState[stateKey] =
                    currentSchedule;
            }

            string[] obsoleteKeys =
                nextState.Keys
                    .Where(key =>
                        !activeStateKeys.Contains(key))
                    .ToArray();

            foreach (string obsoleteKey in
                     obsoleteKeys)
            {
                nextState.Remove(
                    obsoleteKey);
            }

            bool isInitialSnapshot =
                !hasPreviousSnapshotForCurrentRange;

            await ScanComingSoonIfDueAsync(
                nextState,
                botToken,
                chatId,
                telegramAvailable,
                stoppingToken);

            await EvaluateCurrentMovieSubscriptionsAsync(
                nextState,
                botToken,
                telegramAvailable,
                stoppingToken);

            if (telegramAvailable)
            {
                await EnsureOrUpdateGroupDashboardAsync(
                    cinemas,
                    nextState,
                    notifications,
                    isInitialSnapshot,
                    botToken,
                    chatId,
                    stoppingToken);
            }

            if (notifications.Count > 0)
            {
                if (!telegramAvailable)
                {
                    _logger.LogWarning(
                        "VOX changes were detected in one or more cinemas, " +
                        "but Telegram is unavailable. The new state will not be committed, " +
                        "so the notifications will be retried in the next scan.");

                    return;
                }

                bool notificationSent =
                    await SendScheduleNotificationsAsync(
                        notifications,
                        isInitialSnapshot,
                        botToken,
                        chatId,
                        stoppingToken);

                if (!notificationSent)
                {
                    _logger.LogWarning(
                        "Telegram notification failed. " +
                        "The new state will not be committed, so it will be retried.");

                    return;
                }
            }
            else
            {
                _logger.LogInformation(
                    "No cinema, movie, hall, showtime, booking-link, " +
                    "or booking-status change was detected. " +
                    "No Telegram notification will be sent.");
            }

            _knownScheduleByCinemaAndDate =
                nextState;

            _lastSuccessfulScheduleScanUtc =
                DateTime.UtcNow;

            _logger.LogInformation(
                "In-memory state updated for {StateCount} cinema/date combination(s). " +
                "It will reset when the EXE stops or restarts.",
                _knownScheduleByCinemaAndDate.Count);
        }

        private async Task<CinemaOption[]> DiscoverCinemasAsync(
                string baseVoxUrl,
                CancellationToken stoppingToken)
        {
            string discoveryUrl =
                BuildCinemaDiscoveryUrl(
                    baseVoxUrl);

            _logger.LogInformation(
                "Discovering VOX cinema branches from {DiscoveryUrl}.",
                discoveryUrl);

            string? html =
                await DownloadVoxPageAsync(
                    discoveryUrl,
                    stoppingToken);

            if (!string.IsNullOrWhiteSpace(
                    html) &&
                TryExtractCinemaOptions(
                    html,
                    out CinemaOption[] cinemas) &&
                cinemas.Length > 0)
            {
                _lastDiscoveredCinemas =
                    cinemas;

                _logger.LogInformation(
                    "Discovered {CinemaCount} VOX cinema branch(es): {Cinemas}",
                    cinemas.Length,
                    string.Join(
                        ", ",
                        cinemas.Select(
                            cinema =>
                                $"{cinema.Name} ({cinema.Slug})")));

                return cinemas;
            }

            if (_lastDiscoveredCinemas.Length > 0)
            {
                _logger.LogWarning(
                    "Cinema discovery failed in this cycle. " +
                    "The last successfully discovered list of {CinemaCount} cinema(s) " +
                    "will be used.",
                    _lastDiscoveredCinemas.Length);

                return _lastDiscoveredCinemas;
            }

            return Array.Empty<CinemaOption>();
        }

        private bool TryExtractCinemaOptions(
                string html,
                out CinemaOption[] cinemas)
        {
            cinemas =
                Array.Empty<CinemaOption>();

            try
            {
                var document =
                    new HtmlDocument();

                document.LoadHtml(
                    html);

                HtmlNodeCollection? inputNodes =
                    document.DocumentNode.SelectNodes(
                        "//div[" +
                        "contains(" +
                        "concat(' ', normalize-space(@class), ' '), " +
                        "' pseudo-multi-select ')" +
                        " and " +
                        "contains(" +
                        "concat(' ', normalize-space(@class), ' '), " +
                        "' cinemas ')" +
                        "]//input[" +
                        "translate(@name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', " +
                        "'abcdefghijklmnopqrstuvwxyz')='c' and @value" +
                        "]");

                if (inputNodes is null ||
                    inputNodes.Count == 0)
                {
                    inputNodes =
                        document.DocumentNode.SelectNodes(
                            "//input[" +
                            "translate(@name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', " +
                            "'abcdefghijklmnopqrstuvwxyz')='c' and @value" +
                            "]");
                }

                if (inputNodes is null ||
                    inputNodes.Count == 0)
                {
                    _logger.LogWarning(
                        "The VOX page does not contain cinema inputs named c.");

                    return false;
                }

                var discovered =
                    new Dictionary<string, CinemaOption>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (HtmlNode inputNode in
                         inputNodes)
                {
                    string slug =
                        inputNode.GetAttributeValue(
                            "value",
                            string.Empty)
                            .Trim();

                    if (string.IsNullOrWhiteSpace(
                            slug))
                    {
                        continue;
                    }

                    HtmlNode? labelNode =
                        inputNode.Ancestors(
                            "label")
                            .FirstOrDefault();

                    HtmlNode? nameNode =
                        labelNode?.SelectSingleNode(
                            ".//span");

                    string name =
                        NormalizeDisplayText(
                            HtmlEntity.DeEntitize(
                                nameNode?.InnerText ??
                                string.Empty));

                    if (string.IsNullOrWhiteSpace(
                            name))
                    {
                        name =
                            CultureInfo.InvariantCulture
                                .TextInfo
                                .ToTitleCase(
                                    slug.Replace(
                                        "-",
                                        " "));
                    }

                    discovered[slug] =
                        new CinemaOption(
                            slug,
                            name);
                }

                cinemas =
                    discovered.Values
                        .OrderBy(
                            cinema =>
                                cinema.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                return cinemas.Length > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to parse the VOX cinema branch list.");

                return false;
            }
        }

        private DayScheduleChange? DetectScheduleChanges(
                DaySchedule? previousSchedule,
                DaySchedule currentSchedule)
        {
            var changes =
                new List<ScheduleChange>();

            if (previousSchedule is null)
            {
                foreach (MovieSchedule movie in
                         currentSchedule.Movies)
                {
                    changes.Add(
                        ScheduleChange.NewMovie(
                            movie.Title));
                }

                return changes.Count == 0
                    ? null
                    : DayScheduleChange.Changed(
                        currentSchedule,
                        changes);
            }

            foreach (MovieSchedule currentMovie in
                     currentSchedule.Movies)
            {
                MovieSchedule? previousMovie =
                    previousSchedule.Movies.FirstOrDefault(
                        movie =>
                            string.Equals(
                                movie.Title,
                                currentMovie.Title,
                                StringComparison.OrdinalIgnoreCase));

                if (previousMovie is null)
                {
                    changes.Add(
                        ScheduleChange.NewMovie(
                            currentMovie.Title));

                    continue;
                }

                foreach (HallSchedule currentHall in
                         currentMovie.Halls)
                {
                    HallSchedule? previousHall =
                        previousMovie.Halls.FirstOrDefault(
                            hall =>
                                string.Equals(
                                    hall.Name,
                                    currentHall.Name,
                                    StringComparison.OrdinalIgnoreCase));

                    if (previousHall is null)
                    {
                        changes.Add(
                            ScheduleChange.NewHall(
                                currentMovie.Title,
                                currentHall.Name));

                        continue;
                    }

                    foreach (ShowtimeSchedule currentShowtime in
                             currentHall.Showtimes)
                    {
                        ShowtimeSchedule? previousShowtime =
                            previousHall.Showtimes.FirstOrDefault(
                                showtime =>
                                    string.Equals(
                                        showtime.Time,
                                        currentShowtime.Time,
                                        StringComparison.OrdinalIgnoreCase));

                        if (previousShowtime is null)
                        {
                            if (currentShowtime.IsAvailable)
                            {
                                changes.Add(
                                    ScheduleChange.NewShowtime(
                                        currentMovie.Title,
                                        currentHall.Name,
                                        currentShowtime));
                            }

                            continue;
                        }

                        if (previousShowtime.IsAvailable &&
                            !currentShowtime.IsAvailable)
                        {
                            // A sold-out transition naturally removes the booking
                            // link, so report one clear Sold Out change only.
                            changes.Add(
                                ScheduleChange.SoldOut(
                                    currentMovie.Title,
                                    currentHall.Name,
                                    currentShowtime));
                        }
                        else if (!previousShowtime.IsAvailable &&
                                 currentShowtime.IsAvailable)
                        {
                            // Availability returning naturally adds a booking link,
                            // so report one clear Available Again change only.
                            changes.Add(
                                ScheduleChange.AvailableAgain(
                                    currentMovie.Title,
                                    currentHall.Name,
                                    currentShowtime));
                        }
                        else if (previousShowtime.IsAvailable &&
                                 currentShowtime.IsAvailable &&
                                 !string.Equals(
                                     previousShowtime.BookingUrl,
                                     currentShowtime.BookingUrl,
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(
                                    previousShowtime.BookingUrl) &&
                                !string.IsNullOrWhiteSpace(
                                    currentShowtime.BookingUrl))
                            {
                                changes.Add(
                                    ScheduleChange.BookingLinkAdded(
                                        currentMovie.Title,
                                        currentHall.Name,
                                        currentShowtime));
                            }
                            else if (!string.IsNullOrWhiteSpace(
                                         previousShowtime.BookingUrl) &&
                                     string.IsNullOrWhiteSpace(
                                         currentShowtime.BookingUrl))
                            {
                                changes.Add(
                                    ScheduleChange.BookingLinkRemoved(
                                        currentMovie.Title,
                                        currentHall.Name,
                                        currentShowtime));
                            }
                            else
                            {
                                changes.Add(
                                    ScheduleChange.BookingLinkChanged(
                                        currentMovie.Title,
                                        currentHall.Name,
                                        currentShowtime));
                            }
                        }
                    }

                    foreach (ShowtimeSchedule previousShowtime in
                             previousHall.Showtimes)
                    {
                        bool stillExists =
                            currentHall.Showtimes.Any(
                                showtime =>
                                    string.Equals(
                                        showtime.Time,
                                        previousShowtime.Time,
                                        StringComparison.OrdinalIgnoreCase));

                        if (!stillExists &&
                            previousShowtime.IsAvailable &&
                            IsFutureShowtime(
                                previousSchedule.Date,
                                previousShowtime.Time,
                                DateTime.Now))
                        {
                            changes.Add(
                                ScheduleChange.ShowtimeRemoved(
                                    currentMovie.Title,
                                    currentHall.Name,
                                    previousShowtime));
                        }
                    }
                }

                foreach (HallSchedule previousHall in
                         previousMovie.Halls)
                {
                    bool hallStillExists =
                        currentMovie.Halls.Any(
                            hall =>
                                string.Equals(
                                    hall.Name,
                                    previousHall.Name,
                                    StringComparison.OrdinalIgnoreCase));

                    bool hasFutureShowtime =
                        previousHall.Showtimes.Any(showtime =>
                            IsFutureShowtime(
                                previousSchedule.Date,
                                showtime.Time,
                                DateTime.Now));

                    if (!hallStillExists &&
                        hasFutureShowtime)
                    {
                        changes.Add(
                            ScheduleChange.HallRemoved(
                                currentMovie.Title,
                                previousHall.Name));
                    }
                }
            }

            foreach (MovieSchedule previousMovie in
                     previousSchedule.Movies)
            {
                bool movieStillExists =
                    currentSchedule.Movies.Any(
                        movie =>
                            string.Equals(
                                movie.Title,
                                previousMovie.Title,
                                StringComparison.OrdinalIgnoreCase));

                if (!movieStillExists)
                {
                    bool movieHasFutureShowtime =
                        previousMovie.Halls.Any(hall =>
                            hall.Showtimes.Any(showtime =>
                                IsPreviousShowtimeFuture(
                                    previousSchedule.Date,
                                    showtime.Time)));

                    if (movieHasFutureShowtime)
                    {
                        changes.Add(
                            ScheduleChange.MovieRemoved(
                                previousMovie.Title));
                    }
                }
            }

            return changes.Count == 0
                ? null
                : DayScheduleChange.Changed(
                    currentSchedule,
                    changes);
        }

        private bool IsPreviousShowtimeFuture(DateTime scheduleDate, string showtimeText)
        {
            return IsFutureShowtime(
                scheduleDate,
                showtimeText,
                DateTime.Now);
        }

        private List<DateTime> GetConfiguredScanDates()
        {
            DateTime today = DateTime.Today;
            string path = Path.Combine(_dataDirectory, "scan-range-settings.json");

            try
            {
                if (TryLoadPersistentJson(
                        Path.GetFileName(path),
                        path,
                        out string json))
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;

                    if (root.TryGetProperty("SpecificDate", out JsonElement specific) &&
                        specific.ValueKind == JsonValueKind.String &&
                        DateTime.TryParseExact(specific.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime specificDate))
                    {
                        return new List<DateTime> { specificDate.Date };
                    }

                    if (root.TryGetProperty("DaysAhead", out JsonElement daysElement) &&
                        daysElement.TryGetInt32(out int daysAhead) &&
                        daysAhead > 0)
                    {
                        daysAhead = Math.Min(daysAhead, 180);

                        // DaysAhead is a rolling window. It is intentionally
                        // calculated from DateTime.Today every time the scan
                        // cycle starts, so /scan 30 means today + the next
                        // 29 calendar days. Tomorrow the same setting becomes
                        // tomorrow + the next 29 calendar days automatically.
                        return Enumerable.Range(0, daysAhead)
                            .Select(i => today.AddDays(i).Date)
                            .ToList();
                    }

                    if (root.TryGetProperty("Mode", out JsonElement modeElement))
                    {
                        string mode = modeElement.GetString() ?? "current";
                        if (string.Equals(mode, "nextmonth", StringComparison.OrdinalIgnoreCase))
                        {
                            DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(1);
                            DateTime last = new DateTime(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
                            return Enumerable.Range(0, (last - first).Days + 1).Select(i => first.AddDays(i)).ToList();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read scan-range-settings.json. Falling back to current month.");
            }

            DateTime endOfMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
            return Enumerable.Range(0, (endOfMonth - today).Days + 1)
                .Select(i => today.AddDays(i))
                .ToList();
        }

        private async Task<DateScanResult> ScanDateWithLimitAsync(
                string baseVoxUrl,
                CinemaOption cinema,
                DateTime date,
                SemaphoreSlim semaphore,
                CancellationToken stoppingToken)
        {
            await semaphore.WaitAsync(
                stoppingToken);

            try
            {
                string dateUrl =
                    BuildVoxUrlForCinemaAndDate(
                        baseVoxUrl,
                        cinema.Slug,
                        date);

                _logger.LogInformation(
                    "Scanning {Cinema} on {Date}.",
                    cinema.Name,
                    date.ToString(
                        "dd MMMM yyyy",
                        CultureInfo.InvariantCulture));

                string? html =
                    await DownloadVoxPageAsync(
                        dateUrl,
                        stoppingToken);

                if (string.IsNullOrWhiteSpace(
                        html))
                {
                    return DateScanResult.Failed(
                        cinema,
                        date,
                        dateUrl);
                }

                if (!TryExtractDaySchedule(
                        html,
                        cinema,
                        date,
                        dateUrl,
                        out DaySchedule schedule))
                {
                    return DateScanResult.Failed(
                        cinema,
                        date,
                        dateUrl);
                }

                int hallCount =
                    schedule.Movies.Sum(
                        movie =>
                            movie.Halls.Length);

                int showtimeCount =
                    schedule.Movies.Sum(
                        movie =>
                            movie.Halls.Sum(
                                hall =>
                                    hall.Showtimes.Length));

                int soldOutCount =
                    schedule.Movies.Sum(
                        movie =>
                            movie.Halls.Sum(
                                hall =>
                                    hall.Showtimes.Count(
                                        showtime =>
                                            !showtime.IsAvailable)));

                _logger.LogInformation(
                    "{Cinema} — {Date}: {MovieCount} movie(s), {HallCount} hall(s), " +
                    "{ShowtimeCount} showtime(s), {SoldOutCount} sold out.",
                    cinema.Name,
                    date.ToString(
                        "dd MMMM yyyy",
                        CultureInfo.InvariantCulture),
                    schedule.Movies.Length,
                    hallCount,
                    showtimeCount,
                    soldOutCount);

                return DateScanResult.Succeeded(
                    schedule);
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
                    "Failed to scan {Cinema} on {Date}.",
                    cinema.Name,
                    date.ToString(
                        "dd MMMM yyyy",
                        CultureInfo.InvariantCulture));

                return DateScanResult.Failed(
                    cinema,
                    date,
                    BuildVoxUrlForCinemaAndDate(
                        baseVoxUrl,
                        cinema.Slug,
                        date));
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static string BuildCinemaDiscoveryUrl(
                string baseVoxUrl)
        {
            return BuildVoxUrl(
                baseVoxUrl,
                cinemaSlug: null,
                date: null);
        }

        private static string BuildVoxUrlForCinemaAndDate(
                string baseVoxUrl,
                string cinemaSlug,
                DateTime date)
        {
            return BuildVoxUrl(
                baseVoxUrl,
                cinemaSlug,
                date);
        }

        private static string BuildVoxUrl(
                string baseVoxUrl,
                string? cinemaSlug,
                DateTime? date)
        {
            var parsedUrl =
                new Uri(
                    baseVoxUrl);

            List<string> queryParts =
                parsedUrl.Query
                    .TrimStart('?')
                    .Split(
                        '&',
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(part =>
                        !part.StartsWith(
                            "c=",
                            StringComparison.OrdinalIgnoreCase) &&
                        !part.StartsWith(
                            "d=",
                            StringComparison.OrdinalIgnoreCase) &&
                        !part.StartsWith(
                            "monitorTs=",
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (!string.IsNullOrWhiteSpace(
                    cinemaSlug))
            {
                queryParts.Add(
                    "c=" +
                    Uri.EscapeDataString(
                        cinemaSlug));
            }

            if (date.HasValue)
            {
                queryParts.Add(
                    "d=" +
                    date.Value.ToString(
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture));
            }

            var builder =
                new UriBuilder(
                    parsedUrl)
                {
                    Query =
                        string.Join(
                            "&",
                            queryParts)
                };

            return builder.Uri.AbsoluteUri;
        }

        private static bool IsValidVoxBaseUrl(
                string value)
        {
            return Uri.TryCreate(
                       value,
                       UriKind.Absolute,
                       out Uri? parsedUrl)
                   && parsedUrl.Scheme ==
                      Uri.UriSchemeHttps;
        }

        private static string FormatStateKey(
                string cinemaSlug,
                DateTime date)
        {
            return cinemaSlug +
                   "|" +
                   date.ToString(
                       "yyyyMMdd",
                       CultureInfo.InvariantCulture);
        }

        private bool TryExtractDaySchedule(
                string html,
                CinemaOption cinema,
                DateTime date,
                string dateUrl,
                out DaySchedule schedule)
        {
            schedule =
                new DaySchedule(
                    cinema.Slug,
                    cinema.Name,
                    date,
                    dateUrl,
                    Array.Empty<MovieSchedule>());

            if (string.IsNullOrWhiteSpace(
                    html))
            {
                return false;
            }

            try
            {
                var document =
                    new HtmlDocument();

                document.LoadHtml(
                    html);

                HtmlNode? showtimesSection =
                    document.DocumentNode.SelectSingleNode(
                        "//section[" +
                        "contains(" +
                        "concat(' ', normalize-space(@class), ' '), " +
                        "' showtimes ')" +
                        "]");

                if (showtimesSection is null)
                {
                    _logger.LogWarning(
                        "The HTML does not contain " +
                        "<section class=\"showtimes\">.");

                    return false;
                }

                HtmlNodeCollection? movieNodes =
                    showtimesSection.SelectNodes(
                        ".//article[" +
                        "contains(" +
                        "concat(' ', normalize-space(@class), ' '), " +
                        "' movie-compare ')" +
                        "]");

                if (movieNodes is null ||
                    movieNodes.Count == 0)
                {
                    schedule =
                        new DaySchedule(
                            cinema.Slug,
                            cinema.Name,
                            date,
                            dateUrl,
                            Array.Empty<MovieSchedule>());

                    return true;
                }

                var moviesByTitle =
                    new Dictionary<string, MovieScheduleBuilder>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (HtmlNode movieNode in
                         movieNodes)
                {
                    HtmlNode? titleNode =
                        movieNode.SelectSingleNode(
                            ".//aside[" +
                            "contains(" +
                            "concat(' ', normalize-space(@class), ' '), " +
                            "' movie-hero ')" +
                            "]//h2") ??
                        movieNode.SelectSingleNode(
                            ".//h2[1]");

                    string movieTitle =
                        NormalizeDisplayText(
                            HtmlEntity.DeEntitize(
                                titleNode?.InnerText ??
                                string.Empty));

                    if (string.IsNullOrWhiteSpace(
                            movieTitle))
                    {
                        continue;
                    }

                    if (!moviesByTitle.TryGetValue(
                            movieTitle,
                            out MovieScheduleBuilder? movieBuilder))
                    {
                        movieBuilder =
                            new MovieScheduleBuilder(
                                movieTitle);

                        moviesByTitle[movieTitle] =
                            movieBuilder;
                    }

                    HtmlNodeCollection? hallNodes =
                        movieNode.SelectNodes(
                            ".//div[" +
                            "contains(" +
                            "concat(' ', normalize-space(@class), ' '), " +
                            "' dates ')" +
                            "]//ol[" +
                            "contains(" +
                            "concat(' ', normalize-space(@class), ' '), " +
                            "' showtimes ')" +
                            "]/li[./strong]");

                    if (hallNodes is null)
                    {
                        continue;
                    }

                    foreach (HtmlNode hallNode in
                             hallNodes)
                    {
                        string hallName =
                            NormalizeDisplayText(
                                HtmlEntity.DeEntitize(
                                    hallNode.SelectSingleNode(
                                        "./strong")?.InnerText ??
                                    string.Empty));

                        if (string.IsNullOrWhiteSpace(
                                hallName))
                        {
                            hallName =
                                "Standard";
                        }

                        HallScheduleBuilder hallBuilder =
                            movieBuilder.GetOrAddHall(
                                hallName);

                        HtmlNodeCollection? showtimeNodes =
                            hallNode.SelectNodes(
                                "./ol/li");

                        if (showtimeNodes is null)
                        {
                            continue;
                        }

                        foreach (HtmlNode showtimeNode in
                                 showtimeNodes)
                        {
                            HtmlNode? actionNode =
                                showtimeNode.SelectSingleNode(
                                    "./a[" +
                                    "contains(" +
                                    "concat(' ', normalize-space(@class), ' '), " +
                                    "' showtime ')" +
                                    "]") ??
                                showtimeNode.SelectSingleNode(
                                    "./span[" +
                                    "contains(" +
                                    "concat(' ', normalize-space(@class), ' '), " +
                                    "' showtime ')" +
                                    "]");

                            if (actionNode is null)
                            {
                                continue;
                            }

                            string time =
                                NormalizeDisplayText(
                                    HtmlEntity.DeEntitize(
                                        actionNode.InnerText));

                            if (string.IsNullOrWhiteSpace(
                                    time))
                            {
                                continue;
                            }

                            bool unavailable =
                                HasClassToken(
                                    actionNode,
                                    "unavailable");

                            string href =
                                actionNode.GetAttributeValue(
                                    "href",
                                    string.Empty)
                                    .Trim();

                            string bookingUrl =
                                unavailable
                                    ? string.Empty
                                    : MakeAbsoluteUrl(
                                        dateUrl,
                                        href);

                            string identifier =
                                showtimeNode.GetAttributeValue(
                                    "data-id",
                                    string.Empty)
                                    .Trim();

                            hallBuilder.AddOrUpdateShowtime(
                                new ShowtimeSchedule(
                                    time,
                                    !unavailable,
                                    bookingUrl,
                                    identifier));
                        }
                    }
                }

                MovieSchedule[] movies =
                    moviesByTitle.Values
                        .Select(builder =>
                            builder.Build())
                        .OrderBy(movie =>
                            movie.Title,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                schedule =
                    new DaySchedule(
                        cinema.Slug,
                        cinema.Name,
                        date,
                        dateUrl,
                        movies);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to parse the VOX movie, hall, showtime, " +
                    "booking-link, and availability details.");

                return false;
            }
        }

        private static bool HasClassToken(
                HtmlNode node,
                string className)
        {
            string classes =
                node.GetAttributeValue(
                    "class",
                    string.Empty);

            return classes.Split(
                    new[]
                    {
                        ' ',
                        '\t',
                        '\r',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(token =>
                    string.Equals(
                        token,
                        className,
                        StringComparison.OrdinalIgnoreCase));
        }
    }
}
