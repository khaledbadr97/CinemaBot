using System;
using System.Collections.Generic;
using System.Linq;

namespace CinemaBot.Service
{
    internal sealed class ComingSoonMovie
    {
        public string Key { get; set; } = string.Empty;
        public string EnglishTitle { get; set; } = string.Empty;
        public string ArabicTitle { get; set; } = string.Empty;
        public DateTime? ReleaseDate { get; set; }
        public string DetailsUrl { get; set; } = string.Empty;
    }

    internal sealed class ComingSoonSubscription
    {
        public string MovieKey { get; set; } = string.Empty;

        // Empty means all cinemas.
        public HashSet<string> CinemaSlugs { get; set; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        public ComingSoonSubscription Clone()
        {
            return new ComingSoonSubscription
            {
                MovieKey =
                    MovieKey,

                CinemaSlugs =
                    new HashSet<string>(
                        CinemaSlugs,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    internal sealed class ComingSoonUserPreference
    {
        public long UserId { get; set; }
        public long PrivateChatId { get; set; }
        public bool NotifyNewMovies { get; set; } = true;
        public List<ComingSoonSubscription> Subscriptions { get; set; } =
            new List<ComingSoonSubscription>();

        // One entry per movie/cinema/date already announced to this user.
        // This allows a movie to notify a subscriber later when it reaches a
        // different selected cinema or a newly opened booking date.
        public HashSet<string> NotifiedReleaseKeys { get; set; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        public ComingSoonUserPreference Clone()
        {
            return new ComingSoonUserPreference
            {
                UserId =
                    UserId,

                PrivateChatId =
                    PrivateChatId,

                NotifyNewMovies =
                    NotifyNewMovies,

                Subscriptions =
                    Subscriptions
                        .Select(item =>
                            item.Clone())
                        .ToList(),

                NotifiedReleaseKeys =
                    new HashSet<string>(
                        NotifiedReleaseKeys,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    internal sealed class ComingSoonBookingOption
    {
        public string CinemaSlug { get; set; } = string.Empty;
        public string CinemaName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string HallName { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string BookingUrl { get; set; } = string.Empty;
    }

    internal sealed class ComingSoonStateStorage
    {
        public List<ComingSoonMovie>? Movies { get; set; }
        public List<string>? ReleasedKeys { get; set; }
    }

    internal sealed class ComingSoonReleaseMatch
    {
        public ComingSoonMovie Movie { get; }
        public CinemaBotWorker.DaySchedule[] Schedules { get; }

        public ComingSoonReleaseMatch(
            ComingSoonMovie movie,
            CinemaBotWorker.DaySchedule[] schedules)
        {
            Movie =
                movie;
            Schedules =
                schedules;
        }
    }

    internal sealed class CinemaBotHealthState
    {
        public string ServiceName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool GracefulShutdown { get; set; }
        public string Version { get; set; } = string.Empty;
    }
}
