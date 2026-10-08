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
        private sealed class TelegramQueuedRequest
        {
            public string BotToken { get; }
            public string ChatId { get; }
            public string Message { get; }
            public object? ReplyMarkup { get; }

            public TelegramQueuedRequest(
                string botToken,
                string chatId,
                string message,
                object? replyMarkup)
            {
                BotToken =
                    botToken;

                ChatId =
                    chatId;

                Message =
                    message;

                ReplyMarkup =
                    replyMarkup;
            }

            public object CreatePayload()
            {
                if (ReplyMarkup is null)
                {
                    return new
                    {
                        chat_id =
                            ChatId,

                        text =
                            Message,

                        parse_mode =
                            "HTML",

                        disable_web_page_preview =
                            true
                    };
                }

                return new
                {
                    chat_id =
                        ChatId,

                    text =
                        Message,

                    parse_mode =
                        "HTML",

                    disable_web_page_preview =
                        true,

                    reply_markup =
                        ReplyMarkup
                };
            }
        }

        private sealed class BookingUrlCacheEntry
        {
            public string ResolvedUrl { get; }
            public DateTime ExpiresUtc { get; }

            public BookingUrlCacheEntry(
                string resolvedUrl,
                DateTime expiresUtc)
            {
                ResolvedUrl =
                    resolvedUrl;

                ExpiresUtc =
                    expiresUtc;
            }
        }

        private sealed class AlertScopeRule
        {
            public string CinemaSlug { get; set; } =
                string.Empty;

            public string DateKey { get; set; } =
                string.Empty;

            public string MovieTitle { get; set; } =
                string.Empty;

            public AlertScopeRule Clone()
            {
                return new AlertScopeRule
                {
                    CinemaSlug =
                        CinemaSlug,

                    DateKey =
                        DateKey,

                    MovieTitle =
                        MovieTitle
                };
            }
        }

        private sealed class AlertScopeRuleStorage
        {
            public string? CinemaSlug { get; set; }
            public string? DateKey { get; set; }
            public string? MovieTitle { get; set; }
        }

        private sealed class UserPreference
        {
            public long UserId { get; set; }
            public long PrivateChatId { get; set; }
            public string FirstName { get; set; } =
                string.Empty;

            public string Provider { get; set; } =
                "vox";

            public string Language { get; set; } =
                "en";

            public bool NotifySoldOut { get; set; } =
                true;

            public bool NotifyBookingOpenAgain { get; set; } =
                true;

            public bool NotifyNewShowtime { get; set; } =
                true;

            public bool NotifyShowtimeRemoved { get; set; } =
                true;

            public bool NotifyHallRemoved { get; set; } =
                false;

            public bool IsCompleted { get; set; }

            public HashSet<string> SelectedCinemaSlugs { get; set; } =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            public List<AlertScopeRule> AlertScopes { get; set; } =
                new List<AlertScopeRule>();

            public UserPreference Clone()
            {
                return new UserPreference
                {
                    UserId =
                        UserId,

                    PrivateChatId =
                        PrivateChatId,

                    FirstName =
                        FirstName,

                    Provider =
                        Provider,

                    Language =
                        Language,

                    NotifySoldOut =
                        NotifySoldOut,

                    NotifyBookingOpenAgain =
                        NotifyBookingOpenAgain,

                    NotifyNewShowtime =
                        NotifyNewShowtime,

                    NotifyShowtimeRemoved =
                        NotifyShowtimeRemoved,

                    NotifyHallRemoved =
                        NotifyHallRemoved,

                    IsCompleted =
                        IsCompleted,

                    SelectedCinemaSlugs =
                        new HashSet<string>(
                            SelectedCinemaSlugs,
                            StringComparer.OrdinalIgnoreCase),

                    AlertScopes =
                        AlertScopes
                            .Select(scope =>
                                scope.Clone())
                            .ToList()
                };
            }
        }

        private sealed class UserPreferenceStorage
        {
            public long UserId { get; set; }
            public long PrivateChatId { get; set; }
            public string? FirstName { get; set; }
            public string? Provider { get; set; }
            public string? Language { get; set; }
            public bool? NotifySoldOut { get; set; }
            public bool? NotifyBookingOpenAgain { get; set; }
            public bool? NotifyNewShowtime { get; set; }
            public bool? NotifyShowtimeRemoved { get; set; }
            public bool? NotifyHallRemoved { get; set; }
            public bool IsCompleted { get; set; }
            public List<string>? SelectedCinemaSlugs { get; set; }
            public List<AlertScopeRuleStorage>? AlertScopes { get; set; }
        }

        private sealed class ArabicDashboardLoadResult
        {
            public string Key { get; }
            public DaySchedule? Schedule { get; }

            public ArabicDashboardLoadResult(
                string key,
                DaySchedule? schedule)
            {
                Key =
                    key;

                Schedule =
                    schedule;
            }
        }

        private sealed class CinemaOption
        {
            public string Slug { get; }
            public string Name { get; }

            public CinemaOption(
                string slug,
                string name)
            {
                Slug =
                    slug;

                Name =
                    name;
            }
        }

        private sealed class DateScanResult
        {
            public string CinemaName { get; }
            public DateTime Date { get; }
            public string Url { get; }
            public bool Success { get; }
            public DaySchedule? Schedule { get; }

            private DateScanResult(
                string cinemaName,
                DateTime date,
                string url,
                bool success,
                DaySchedule? schedule)
            {
                CinemaName =
                    cinemaName;

                Date =
                    date;

                Url =
                    url;

                Success =
                    success;

                Schedule =
                    schedule;
            }

            public static DateScanResult Succeeded(
                DaySchedule schedule)
            {
                return new DateScanResult(
                    schedule.CinemaName,
                    schedule.Date,
                    schedule.Url,
                    true,
                    schedule);
            }

            public static DateScanResult Failed(
                CinemaOption cinema,
                DateTime date,
                string url)
            {
                return new DateScanResult(
                    cinema.Name,
                    date,
                    url,
                    false,
                    null);
            }
        }

        internal sealed class DaySchedule
        {
            public string CinemaSlug { get; }
            public string CinemaName { get; }
            public DateTime Date { get; }
            public string Url { get; }
            public MovieSchedule[] Movies { get; }

            public DaySchedule(
                string cinemaSlug,
                string cinemaName,
                DateTime date,
                string url,
                MovieSchedule[] movies)
            {
                CinemaSlug =
                    cinemaSlug;

                CinemaName =
                    cinemaName;

                Date =
                    date;

                Url =
                    url;

                Movies =
                    movies;
            }
        }

        internal sealed class MovieSchedule
        {
            public string Title { get; }
            public HallSchedule[] Halls { get; }

            public MovieSchedule(
                string title,
                HallSchedule[] halls)
            {
                Title =
                    title;

                Halls =
                    halls;
            }
        }

        internal sealed class HallSchedule
        {
            public string Name { get; }
            public ShowtimeSchedule[] Showtimes { get; }

            public HallSchedule(
                string name,
                ShowtimeSchedule[] showtimes)
            {
                Name =
                    name;

                Showtimes =
                    showtimes;
            }
        }

        internal sealed class ShowtimeSchedule
        {
            public string Time { get; }
            public bool IsAvailable { get; }
            public string BookingUrl { get; }
            public string Identifier { get; }

            public ShowtimeSchedule(
                string time,
                bool isAvailable,
                string bookingUrl,
                string identifier)
            {
                Time =
                    time;

                IsAvailable =
                    isAvailable;

                BookingUrl =
                    bookingUrl;

                Identifier =
                    identifier;
            }
        }

        internal sealed class MovieScheduleBuilder
        {
            private readonly Dictionary<string, HallScheduleBuilder> _halls =
                new Dictionary<string, HallScheduleBuilder>(
                    StringComparer.OrdinalIgnoreCase);

            public string Title { get; }

            public MovieScheduleBuilder(
                string title)
            {
                Title =
                    title;
            }

            public HallScheduleBuilder GetOrAddHall(
                string hallName)
            {
                if (!_halls.TryGetValue(
                        hallName,
                        out HallScheduleBuilder? hall))
                {
                    hall =
                        new HallScheduleBuilder(
                            hallName);

                    _halls[hallName] =
                        hall;
                }

                return hall;
            }

            public MovieSchedule Build()
            {
                return new MovieSchedule(
                    Title,
                    _halls.Values
                        .Select(hall =>
                            hall.Build())
                        .OrderBy(hall =>
                            hall.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }
        }

        internal sealed class HallScheduleBuilder
        {
            private readonly Dictionary<string, ShowtimeSchedule> _showtimes =
                new Dictionary<string, ShowtimeSchedule>(
                    StringComparer.OrdinalIgnoreCase);

            public string Name { get; }

            public HallScheduleBuilder(
                string name)
            {
                Name =
                    name;
            }

            public void AddOrUpdateShowtime(
                ShowtimeSchedule showtime)
            {
                _showtimes[showtime.Time] =
                    showtime;
            }

            public HallSchedule Build()
            {
                return new HallSchedule(
                    Name,
                    _showtimes.Values
                        .OrderBy(showtime =>
                            GetShowtimeSortValue(
                                showtime.Time))
                        .ThenBy(showtime =>
                            showtime.Time,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }

            private static int GetShowtimeSortValue(
                string time)
            {
                string compactTime =
                    time.Replace(
                        " ",
                        string.Empty);

                string[] formats =
                {
                    "h:mmtt",
                    "htt",
                    "hh:mmtt",
                    "h:mm tt",
                    "hh:mm tt"
                };

                if (DateTime.TryParseExact(
                        compactTime,
                        formats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out DateTime parsedTime))
                {
                    return
                        parsedTime.Hour * 60 +
                        parsedTime.Minute;
                }

                return
                    int.MaxValue;
            }
        }

        internal sealed class DayScheduleChange
        {
            public DaySchedule Schedule { get; }
            public bool IsInitial { get; }
            public ScheduleChange[] Changes { get; }

            private DayScheduleChange(
                DaySchedule schedule,
                bool isInitial,
                ScheduleChange[] changes)
            {
                Schedule =
                    schedule;

                IsInitial =
                    isInitial;

                Changes =
                    changes;
            }

            public static DayScheduleChange Initial(
                DaySchedule schedule)
            {
                return new DayScheduleChange(
                    schedule,
                    true,
                    Array.Empty<ScheduleChange>());
            }

            public static DayScheduleChange Changed(
                DaySchedule schedule,
                IEnumerable<ScheduleChange> changes)
            {
                return new DayScheduleChange(
                    schedule,
                    false,
                    changes.ToArray());
            }
        }

        internal enum ScheduleChangeType
        {
            NewMovie,
            MovieRemoved,
            NewHall,
            HallRemoved,
            NewShowtime,
            ShowtimeRemoved,
            SoldOut,
            AvailableAgain,
            BookingLinkAdded,
            BookingLinkRemoved,
            BookingLinkChanged
        }

        internal sealed class ScheduleChange
        {
            public ScheduleChangeType Type { get; }
            public string MovieTitle { get; }
            public string HallName { get; }
            public string Time { get; }
            public bool IsAvailable { get; }
            public string BookingUrl { get; }

            private ScheduleChange(
                ScheduleChangeType type,
                string movieTitle,
                string hallName,
                string time,
                bool isAvailable,
                string bookingUrl)
            {
                Type =
                    type;

                MovieTitle =
                    movieTitle;

                HallName =
                    hallName;

                Time =
                    time;

                IsAvailable =
                    isAvailable;

                BookingUrl =
                    bookingUrl;
            }

            public static ScheduleChange NewMovie(
                string movieTitle)
            {
                return CreateSimple(
                    ScheduleChangeType.NewMovie,
                    movieTitle,
                    string.Empty);
            }

            public static ScheduleChange MovieRemoved(
                string movieTitle)
            {
                return CreateSimple(
                    ScheduleChangeType.MovieRemoved,
                    movieTitle,
                    string.Empty);
            }

            public static ScheduleChange NewHall(
                string movieTitle,
                string hallName)
            {
                return CreateSimple(
                    ScheduleChangeType.NewHall,
                    movieTitle,
                    hallName);
            }

            public static ScheduleChange HallRemoved(
                string movieTitle,
                string hallName)
            {
                return CreateSimple(
                    ScheduleChangeType.HallRemoved,
                    movieTitle,
                    hallName);
            }

            public static ScheduleChange NewShowtime(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.NewShowtime,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange ShowtimeRemoved(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.ShowtimeRemoved,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange SoldOut(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.SoldOut,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange AvailableAgain(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.AvailableAgain,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange BookingLinkAdded(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.BookingLinkAdded,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange BookingLinkRemoved(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.BookingLinkRemoved,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public static ScheduleChange BookingLinkChanged(
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return FromShowtime(
                    ScheduleChangeType.BookingLinkChanged,
                    movieTitle,
                    hallName,
                    showtime);
            }

            public ScheduleChange WithBookingUrl(
                string bookingUrl)
            {
                return new ScheduleChange(
                    Type,
                    MovieTitle,
                    HallName,
                    Time,
                    IsAvailable,
                    bookingUrl);
            }

            private static ScheduleChange CreateSimple(
                ScheduleChangeType type,
                string movieTitle,
                string hallName)
            {
                return new ScheduleChange(
                    type,
                    movieTitle,
                    hallName,
                    string.Empty,
                    false,
                    string.Empty);
            }

            private static ScheduleChange FromShowtime(
                ScheduleChangeType type,
                string movieTitle,
                string hallName,
                ShowtimeSchedule showtime)
            {
                return new ScheduleChange(
                    type,
                    movieTitle,
                    hallName,
                    showtime.Time,
                    showtime.IsAvailable,
                    showtime.BookingUrl);
            }
        }
    }
}
